using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractAsyncConnector
    {
        public Task CreateTableAsync(Type[] types, CancellationToken ct = default)
        {
            return CreateTableAsync(DataBase.LoadTables(types), ct);
        }

        public async Task CreateTableAsync(IEnumerable<Tables.Table> tables, CancellationToken ct = default)
        {
            if (tables != null && tables.Any() && tables.Any(x => x != null && x.Fields != null && x.Fields.Count > 0))
            {
                await CreateTableAsync(tables.ToDictionary(x => x.Name, x => x.Fields.Select(y => y.Value)), ct);
                foreach (var table in tables.Where(x => x.Indexes != null && x.Indexes.Count > 0))
                {
                    await CreateIndexesAsync(table.Name, table.Indexes!.Values, ct);
                }
            }
        }

        public Task CreateTableAsync(IDictionary<string, IEnumerable<Fields.AbstractField>> tables, CancellationToken ct = default)
        {
            if (tables != null && tables.Any() && tables.Any(x => x.Value != null && x.Value.Count() > 0))
            {
                var tasks = new List<Task>();
                foreach (var kvp in tables.Where(x => x.Value != null && x.Value.Any()))
                {
                    tasks.Add(CreateTableAsync(kvp.Key, kvp.Value.Select(x => FieldDefinition(x)), ct));
                }
                return Task.WhenAll(tasks);
            }
            return Task.CompletedTask;
        }

        public virtual async Task CreateTableAsync(string name, IEnumerable<string> fields, CancellationToken ct = default)
        {
            await DoCommandWithTransactionAsync(async (command) =>
            {
                command.CommandText = "CREATE TABLE IF NOT EXISTS "
                    + QuoteIdentifier(name)
                    + " ("
                    + string.Join(", ", fields.Where(x => !string.IsNullOrEmpty(x)))
                    + ")";
                await Task.CompletedTask;
            }, async (command) =>
            {
                await command.ExecuteNonQueryAsync(ct);
            }, true);
        }

        public virtual async Task CreateIndexesAsync(string tableName, IEnumerable<Tables.IndexDefinition> indexes, CancellationToken ct = default)
        {
            foreach (var index in indexes)
            {
                await DoCommandWithTransactionAsync(async (command) =>
                {
                    command.CommandText = CreateIndexSql(tableName, index);
                    await Task.CompletedTask;
                }, async (command) =>
                {
                    await command.ExecuteNonQueryAsync(ct);
                }, true);
            }
        }

        public virtual async Task DropIndexesAsync(string tableName, IEnumerable<Tables.IndexDefinition> indexes, CancellationToken ct = default)
        {
            foreach (var index in indexes)
            {
                await DoCommandWithTransactionAsync(async (command) =>
                {
                    command.CommandText = DropIndexSql(tableName, index);
                    await Task.CompletedTask;
                }, async (command) =>
                {
                    await command.ExecuteNonQueryAsync(ct);
                }, true);
            }
        }
    }
}
