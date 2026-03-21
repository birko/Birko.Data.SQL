using Birko.Data.Patterns.IndexManagement;
using Birko.Data.SQL.Connectors;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Birko.Data.SQL.IndexManagement
{
    /// <summary>
    /// SQL implementation of <see cref="IIndexManager"/>.
    /// Scope = table name (required).
    /// Uses information_schema for MySQL; override <see cref="ListIndexesSql"/> and
    /// <see cref="IndexExistsSql"/> for other SQL dialects.
    /// </summary>
    public class SqlIndexManager : IIndexManager
    {
        private readonly AbstractConnectorBase _connector;

        /// <summary>
        /// Gets the underlying connector.
        /// </summary>
        protected AbstractConnectorBase Connector => _connector;

        public SqlIndexManager(AbstractConnectorBase connector)
        {
            _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string indexName, string? scope = null, CancellationToken ct = default)
        {
            ValidateScope(scope);
            if (string.IsNullOrWhiteSpace(indexName)) throw new ArgumentException("Index name is required.", nameof(indexName));

            var sql = IndexExistsSql(scope!, indexName);
            var result = false;

            await ExecuteReaderAsync(sql, reader =>
            {
                if (reader.Read())
                {
                    result = reader.GetInt32(0) > 0;
                }
            }, ct).ConfigureAwait(false);

            return result;
        }

        /// <inheritdoc />
        public async Task CreateAsync(IndexDefinition definition, string? scope = null, CancellationToken ct = default)
        {
            ValidateScope(scope);
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (string.IsNullOrWhiteSpace(definition.Name)) throw new ArgumentException("Index name is required.", nameof(definition));
            if (definition.Fields == null || definition.Fields.Count == 0) throw new ArgumentException("At least one field is required.", nameof(definition));

            var sqlIndex = ToSqlIndexDefinition(definition);
            var sql = definition.Unique
                ? CreateUniqueIndexSql(scope!, sqlIndex)
                : _connector.CreateIndexSql(scope!, sqlIndex);

            try
            {
                await ExecuteNonQueryAsync(sql, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new IndexManagementException(
                    $"Failed to create index '{definition.Name}' on table '{scope}'.",
                    definition.Name, scope, ex);
            }
        }

        /// <inheritdoc />
        public async Task DropAsync(string indexName, string? scope = null, CancellationToken ct = default)
        {
            ValidateScope(scope);
            if (string.IsNullOrWhiteSpace(indexName)) throw new ArgumentException("Index name is required.", nameof(indexName));

            var sqlIndex = new Tables.IndexDefinition { Name = indexName };
            var sql = _connector.DropIndexSql(scope!, sqlIndex);

            try
            {
                await ExecuteNonQueryAsync(sql, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new IndexManagementException(
                    $"Failed to drop index '{indexName}' on table '{scope}'.",
                    indexName, scope, ex);
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<Patterns.IndexManagement.IndexInfo>> ListAsync(string? scope = null, CancellationToken ct = default)
        {
            ValidateScope(scope);

            var sql = ListIndexesSql(scope!);
            var rows = new List<(string IndexName, string ColumnName, bool IsDescending, bool IsUnique, int Ordinal)>();

            await ExecuteReaderAsync(sql, reader =>
            {
                while (reader.Read())
                {
                    rows.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt32(2) != 0,
                        reader.GetInt32(3) != 0,
                        reader.GetInt32(4)
                    ));
                }
            }, ct).ConfigureAwait(false);

            // Group rows by index name → multi-column indexes
            return rows
                .GroupBy(r => r.IndexName)
                .Select(g =>
                {
                    var first = g.First();
                    return new Patterns.IndexManagement.IndexInfo
                    {
                        Name = g.Key,
                        Unique = first.IsUnique,
                        Fields = g.OrderBy(r => r.Ordinal).Select(r => new IndexField
                        {
                            Name = r.ColumnName,
                            IsDescending = r.IsDescending
                        }).ToList()
                    };
                })
                .ToList();
        }

        /// <inheritdoc />
        public async Task<Patterns.IndexManagement.IndexInfo?> GetInfoAsync(string indexName, string? scope = null, CancellationToken ct = default)
        {
            var all = await ListAsync(scope, ct).ConfigureAwait(false);
            return all.FirstOrDefault(i => string.Equals(i.Name, indexName, StringComparison.OrdinalIgnoreCase));
        }

        #region SQL generation (virtual — overridable per dialect)

        /// <summary>
        /// SQL to check if an index exists. Returns a scalar count.
        /// Default uses information_schema.statistics (MySQL).
        /// </summary>
        protected virtual string IndexExistsSql(string tableName, string indexName)
        {
            var safeIndex = indexName.Replace("'", "''");
            var safeTable = tableName.Replace("'", "''");
            return $"SELECT COUNT(*) FROM information_schema.statistics WHERE table_name = '{safeTable}' AND index_name = '{safeIndex}'";
        }

        /// <summary>
        /// SQL to list all indexes on a table.
        /// Must return: index_name, column_name, is_descending (0/1), is_unique (0/1), ordinal_position.
        /// </summary>
        protected virtual string ListIndexesSql(string tableName)
        {
            var safeTable = tableName.Replace("'", "''");
            return $@"SELECT index_name, column_name, 0 AS is_descending, CASE WHEN non_unique = 0 THEN 1 ELSE 0 END AS is_unique, seq_in_index AS ordinal_position
FROM information_schema.statistics
WHERE table_name = '{safeTable}'
ORDER BY index_name, seq_in_index";
        }

        /// <summary>
        /// SQL for CREATE UNIQUE INDEX.
        /// </summary>
        protected virtual string CreateUniqueIndexSql(string tableName, Tables.IndexDefinition index)
        {
            var columns = string.Join(", ", index.Columns.Select(c =>
                _connector.QuoteIdentifier(c.ColumnName) + (c.IsDescending ? " DESC" : "")));

            return $"CREATE UNIQUE INDEX IF NOT EXISTS {_connector.QuoteIdentifier(index.Name)} ON {_connector.QuoteIdentifier(tableName)} ({columns})";
        }

        #endregion

        #region Helpers

        private static void ValidateScope(string? scope)
        {
            if (string.IsNullOrWhiteSpace(scope))
                throw new ArgumentException("Table name (scope) is required for SQL index management.", nameof(scope));
        }

        private static Tables.IndexDefinition ToSqlIndexDefinition(IndexDefinition definition)
        {
            var sqlDef = new Tables.IndexDefinition { Name = definition.Name };
            int order = 0;
            foreach (var field in definition.Fields)
            {
                sqlDef.Columns.Add(new Tables.IndexColumn
                {
                    ColumnName = field.Name,
                    IsDescending = field.IsDescending,
                    Order = order++
                });
            }
            return sqlDef;
        }

        /// <summary>
        /// Opens a new connection using the connector's settings.
        /// </summary>
        protected DbConnection CreateConnection()
        {
            return _connector.CreateConnection(_connector.Settings);
        }

        protected async Task ExecuteNonQueryAsync(string sql, CancellationToken ct)
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        protected async Task ExecuteReaderAsync(string sql, Action<DbDataReader> readAction, CancellationToken ct)
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            readAction(reader);
        }

        #endregion
    }
}
