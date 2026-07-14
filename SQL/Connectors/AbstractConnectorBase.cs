using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Birko.Data.SQL.Connectors.Strategies;
using PasswordSettings = Birko.Configuration.PasswordSettings;

namespace Birko.Data.SQL.Connectors
{
    /// <summary>
    /// Base class for SQL database connectors containing shared functionality.
    /// </summary>
    public abstract partial class AbstractConnectorBase
    {
        protected readonly PasswordSettings _settings = null!;
        protected readonly object _lock = new();
        public bool IsInitializing { get; protected set; } = false;

        /// <summary>
        /// Gets the connection settings for this connector.
        /// </summary>
        public PasswordSettings Settings => _settings;

        /// <summary>
        /// Retry policy for transient failures (deadlocks, timeouts, connection drops).
        /// Set to <see cref="RetryPolicy.None"/> to disable retries. Default is no retries.
        /// </summary>
        public RetryPolicy RetryPolicy { get; set; } = RetryPolicy.None;

        /// <summary>
        /// Determines whether an exception is transient and the operation should be retried.
        /// Override in provider-specific connectors to detect provider-specific transient errors
        /// (e.g., SQL Server error 1205 for deadlocks, PostgreSQL 40P01, MySQL 1213).
        /// </summary>
        public virtual bool IsTransientException(Exception ex)
        {
            if (ex is TimeoutException) return true;
            if (ex is DbException dbEx && dbEx.IsTransient) return true;
            return false;
        }

        /// <summary>
        /// Determines whether an exception indicates the queried table/relation does not exist, so a
        /// reader can yield an empty result instead of faulting. The base match is SQLite's wording
        /// ("no such table"); provider-specific connectors override this to add their own phrasing
        /// (PostgreSQL: 'relation "x" does not exist', MySQL: "doesn't exist", MSSQL: "Invalid object name").
        /// Mirrors the <see cref="IsTransientException"/> override pattern.
        /// </summary>
        public virtual bool IsMissingTableException(Exception ex)
        {
            return ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Executes an action with retry logic for transient failures.
        /// </summary>
        protected void ExecuteWithRetry(Action action, string? commandText = null)
        {
            var policy = RetryPolicy;
            if (policy.MaxRetries <= 0)
            {
                action();
                return;
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception ex) when (attempt < policy.MaxRetries && IsTransientException(ex))
                {
                    var delay = policy.GetDelay(attempt + 1);
                    Thread.Sleep(delay);
                }
            }
        }

        /// <summary>
        /// Executes an async action with retry logic for transient failures.
        /// </summary>
        protected async Task ExecuteWithRetryAsync(Func<Task> action, CancellationToken ct = default, string? commandText = null)
        {
            var policy = RetryPolicy;
            if (policy.MaxRetries <= 0)
            {
                await action().ConfigureAwait(false);
                return;
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await action().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (attempt < policy.MaxRetries && IsTransientException(ex))
                {
                    var delay = policy.GetDelay(attempt + 1);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }

        // Strategy pattern for condition building — keyed by ConditionType for O(1) dispatch
        private readonly Dictionary<Conditions.ConditionType, IConditionStrategy> _conditionStrategyMap = new();

        protected AbstractConnectorBase(PasswordSettings settings)
        {
            _settings = settings;
            InitializeConditionStrategies();
        }

        /// <summary>
        /// Initializes the condition strategy builders
        /// </summary>
        private void InitializeConditionStrategies()
        {
            IConditionStrategy[] strategies =
            [
                new Strategies.EqualConditionStrategy(),
                new Strategies.ComparisonConditionStrategy(),
                new Strategies.LikeConditionStrategy(),
                new Strategies.InConditionStrategy(),
                new Strategies.NullConditionStrategy(),
            ];
            foreach (var strategy in strategies)
                foreach (Conditions.ConditionType ct in Enum.GetValues<Conditions.ConditionType>())
                    if (strategy.CanHandle(ct))
                        _conditionStrategyMap[ct] = strategy;
        }

        /// <summary>
        /// Creates a database connection.
        /// </summary>
        public abstract DbConnection CreateConnection(PasswordSettings settings);

        /// <summary>
        /// Converts a DbType to database-specific type string.
        /// </summary>
        public abstract string ConvertType(DbType type, Fields.AbstractField field);

        /// <summary>
        /// Gets the field definition string for a specific field.
        /// </summary>
        public abstract string FieldDefinition(Fields.AbstractField field);

        /// <summary>
        /// Converts a DbType to its corresponding CLR type.
        /// Used for DataTable construction in bulk operations.
        /// Override in provider-specific connectors if the platform requires different mappings.
        /// </summary>
        public virtual Type DbTypeToClrType(DbType dbType)
        {
            return dbType switch
            {
                DbType.Boolean => typeof(bool),
                DbType.Byte or DbType.SByte => typeof(byte),
                DbType.Single => typeof(float),
                DbType.Int16 or DbType.UInt16 => typeof(short),
                DbType.Int32 or DbType.UInt32 => typeof(int),
                DbType.Int64 or DbType.UInt64 => typeof(long),
                DbType.Decimal or DbType.VarNumeric or DbType.Currency => typeof(decimal),
                DbType.Double => typeof(double),
                DbType.Guid => typeof(Guid),
                DbType.Date or DbType.DateTime or DbType.DateTime2 or DbType.Time => typeof(DateTime),
                DbType.DateTimeOffset => typeof(DateTimeOffset),
                DbType.Binary or DbType.Object => typeof(byte[]),
                _ => typeof(string),
            };
        }

        /// <summary>
        /// Quotes a SQL identifier (table or column name) to prevent reserved word conflicts and injection.
        /// Default uses ANSI SQL double quotes. Override for provider-specific quoting.
        /// </summary>
        public virtual string QuoteIdentifier(string identifier)
        {
            return "\"" + identifier.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// Adds a parameter to a DbCommand.
        /// </summary>
        public virtual DbCommand AddParameter(DbCommand command, string name, object? value)
        {
            if (command.Parameters.Contains(name))
            {
                command.Parameters[name].Value = value ?? DBNull.Value;
            }
            else
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
            return command;
        }

        /// <summary>
        /// Builds a SQL condition clause. Allocates one StringBuilder for the entire
        /// condition tree (shared across all nested levels).
        /// </summary>
        public virtual string ConditionDefinition(Conditions.Condition condition, DbCommand command)
        {
            if (condition == null) return string.Empty;
            var sb = new StringBuilder();
            AppendConditionTo(sb, condition, command);
            return sb.ToString();
        }

        /// <summary>
        /// Appends the SQL for <paramref name="condition"/> to a shared <paramref name="sb"/>,
        /// avoiding per-level StringBuilder allocations in nested AND/OR trees.
        /// </summary>
        private void AppendConditionTo(StringBuilder sb, Conditions.Condition condition, DbCommand command)
        {
            if (condition.SubConditions?.Any() == true)
                AppendSubConditionsTo(sb, condition, command);
            else
                sb.Append(BuildSingleCondition(condition, command));
        }

        /// <summary>
        /// Appends nested sub-conditions to <paramref name="sb"/> using the parent's IsOr flag
        /// as the join operator — no in-place mutation of sub.IsOr.
        /// Wraps in parentheses when there are two or more children.
        /// </summary>
        private void AppendSubConditionsTo(StringBuilder sb, Conditions.Condition condition, DbCommand command)
        {
            var separator = condition.IsOr ? " OR " : " AND ";
            int startIndex = sb.Length;
            int count = 0;
            foreach (var sub in condition.SubConditions!)
            {
                if (count > 0) sb.Append(separator);
                AppendConditionTo(sb, sub, command);
                count++;
            }
            if (count > 1)
            {
                sb.Insert(startIndex, '(');
                sb.Append(')');
            }
        }

        /// <summary>
        /// Builds SQL for a single condition using the appropriate strategy.
        /// </summary>
        private string BuildSingleCondition(Conditions.Condition condition, DbCommand command)
        {
            if (string.IsNullOrEmpty(condition.Name))
                throw new InvalidOperationException("Condition name cannot be null or empty for non-subconditions");

            if (!_conditionStrategyMap.TryGetValue(condition.Type, out var strategy))
                throw new NotSupportedException($"Condition type {condition.Type} is not supported");

            var context = new SqlBuilderContext(this);
            return strategy.BuildSql(condition, command, context);
        }

        /// <summary>
        /// Builds SQL for multiple conditions using a single shared StringBuilder.
        /// </summary>
        public virtual string ConditionDefinition(IEnumerable<Conditions.Condition>? conditions, DbCommand command)
        {
            if (conditions == null) return string.Empty;
            using var en = conditions.GetEnumerator();
            if (!en.MoveNext()) return string.Empty;
            var sb = new StringBuilder();
            AppendConditionTo(sb, en.Current, command);
            while (en.MoveNext())
            {
                sb.Append(en.Current.IsOr ? " OR " : " AND ");
                AppendConditionTo(sb, en.Current, command);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Generates a CREATE INDEX SQL statement for the given table and index definition.
        /// Override in provider-specific connectors if the DDL syntax differs.
        /// </summary>
        public virtual string CreateIndexSql(string tableName, Tables.IndexDefinition index)
        {
            var columns = string.Join(", ", index.Columns.Select(c =>
                QuoteIdentifier(c.ColumnName) + (c.IsDescending ? " DESC" : "")));

            return $"CREATE INDEX IF NOT EXISTS {QuoteIdentifier(index.Name)} ON {QuoteIdentifier(tableName)} ({columns})";
        }

        /// <summary>
        /// Generates a DROP INDEX SQL statement.
        /// Override in provider-specific connectors if the DDL syntax differs (e.g. MSSQL).
        /// </summary>
        public virtual string DropIndexSql(string tableName, Tables.IndexDefinition index)
        {
            return $"DROP INDEX IF EXISTS {QuoteIdentifier(index.Name)}";
        }

        /// <summary>
        /// Builds LIMIT and OFFSET clause
        /// </summary>
        public virtual string? LimitOffsetDefinition(DbCommand command, int? limit = null, int? offset = null)
        {
            if (limit == null)
            {
                return null;
            }
            var result = new StringBuilder();
            result.Append(" LIMIT @LIMIT");
            AddParameter(command, "@LIMIT", limit.Value);
            if (offset != null)
            {
                result.Append(" OFFSET @OFFSET");
                AddParameter(command, "@OFFSET", offset.Value);
            }
            return result.ToString();
        }

        /// <summary>
        /// Adds WHERE clause to command
        /// </summary>
        public virtual DbCommand? AddWhere(IEnumerable<Conditions.Condition>? conditions, DbCommand? command)
        {
            if (command == null || conditions == null) return command;
            var sql = ConditionDefinition(conditions, command);
            if (!string.IsNullOrEmpty(sql))
                command.CommandText += " WHERE " + sql;
            return command;
        }

        /// <summary>
        /// Creates a SELECT command with conditions, order, limit and offset.
        /// </summary>
        public virtual DbCommand CreateSelectCommand(DbCommand command, IEnumerable<string> tableNames, IDictionary<int, string> fields, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            return CreateSelectCommand(command, tableNames, fields, null, conditions, null, orderFields, limit, offset);
        }

        /// <summary>
        /// Returns the SQL aggregate function name for the given aggregate function.
        /// Shared by store-level and view-level aggregation.
        /// </summary>
        public static string GetSqlFunctionName(Birko.Data.Stores.AggregateFunction function)
        {
            return function switch
            {
                Birko.Data.Stores.AggregateFunction.Count => "COUNT",
                Birko.Data.Stores.AggregateFunction.Sum => "SUM",
                Birko.Data.Stores.AggregateFunction.Avg => "AVG",
                Birko.Data.Stores.AggregateFunction.Min => "MIN",
                Birko.Data.Stores.AggregateFunction.Max => "MAX",
                _ => throw new NotSupportedException($"Aggregate function {function} is not supported")
            };
        }

        /// <summary>
        /// Resolves a C# property name to its SQL column name using loaded field metadata.
        /// </summary>
        protected static string ResolveSqlName(IEnumerable<Birko.Data.SQL.Fields.AbstractField> fields, string propertyName)
        {
            var field = fields.FirstOrDefault(f =>
                f.Property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            if (field != null)
                return field.Name;

            // Fallback: use property name as-is
            return propertyName;
        }

        /// <summary>
        /// Builds SELECT and GROUP BY field dictionaries for an aggregation query.
        /// Shared by sync and async aggregate connectors to avoid duplicating query-building logic.
        /// </summary>
        protected (Dictionary<int, string> fields, Dictionary<int, string> groupFields, IEnumerable<Conditions.Condition>? conditions) BuildAggregateQueryParts<T>(
            Type type,
            Birko.Data.Stores.AggregateQuery<T> query)
            where T : Models.AbstractModel
        {
            var fields = new Dictionary<int, string>();
            var groupFields = new Dictionary<int, string>();
            int idx = 0;

            var allFields = DataBase.LoadFields(type);
            foreach (var groupBy in query.GroupByFields)
            {
                var sqlName = ResolveSqlName(allFields, groupBy);
                fields[idx] = sqlName;
                groupFields[idx] = sqlName;
                idx++;
            }

            if (!string.IsNullOrEmpty(query.TimeBucketInterval) && !string.IsNullOrEmpty(query.TimeColumn))
            {
                var timeCol = ResolveSqlName(allFields, query.TimeColumn);
                var interval = Birko.Data.Stores.TimeIntervalParser.ToSqlInterval(query.TimeBucketInterval);
                fields[idx] = $"time_bucket('{interval}', {timeCol}) AS bucket_time";
                groupFields[idx] = "bucket_time";
                idx++;
            }

            foreach (var agg in query.Aggregates)
            {
                var alias = agg.ResolvedAlias;
                var funcName = GetSqlFunctionName(agg.Function);
                var sqlFunc = agg.Function == Birko.Data.Stores.AggregateFunction.Count
                    ? "COUNT(*)"
                    : $"{funcName}({ResolveSqlName(allFields, agg.SourcePropertyName)})";
                fields[idx] = $"{sqlFunc} AS {QuoteIdentifier(alias)}";
                idx++;
            }

            var conditions = query.Filter != null
                ? DataBase.ParseConditionExpression(query.Filter as System.Linq.Expressions.LambdaExpression)
                : null;

            return (fields, groupFields, conditions);
        }

        /// <summary>
        /// Maps a <see cref="DbDataReader"/> row to an <see cref="Birko.Data.Stores.AggregateResult"/>.
        /// Shared by sync and async aggregate connectors.
        /// </summary>
        protected static Birko.Data.Stores.AggregateResult ReadAggregateResult(DbDataReader reader)
        {
            var dict = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                dict[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            return new Birko.Data.Stores.AggregateResult(dict);
        }

        /// <summary>
        /// Creates a SELECT command with joins, conditions, grouping, order, limit and offset.
        /// </summary>
        public virtual DbCommand CreateSelectCommand(DbCommand command, IEnumerable<string> tableNames, IDictionary<int, string> fields, IEnumerable<Conditions.Join>? joinconditions = null, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<int, string>? groupFields = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            command.CommandText = "SELECT " + string.Join(", ", fields.Values) + " FROM ";

            Dictionary<string, List<Conditions.Join>> joins = new();
            if (joinconditions != null && joinconditions.Any())
            {
                string? prevleft = null;
                string? prevright = null;
                foreach (var join in joinconditions)
                {
                    if (!string.IsNullOrEmpty(prevleft) && !string.IsNullOrEmpty(prevright) && !joins.ContainsKey(join.Left) && prevright == join.Left && joins.ContainsKey(prevleft))
                    {
                        joins[prevleft].Add(join);
                    }
                    else
                    {
                        if (!joins.ContainsKey(join.Left))
                        {
                            joins.Add(join.Left, new List<Conditions.Join>());
                        }
                        joins[join.Left].Add(join);
                        prevleft = join.Left;
                    }
                    prevright = join.Right;
                }
            }

            int i = 0;
            foreach (var table in tableNames.Distinct())
            {
                if (i > 0)
                {
                    command.CommandText += ", ";
                }
                command.CommandText += QuoteIdentifier(table);
                if (joins != null && joins.ContainsKey(table))
                {
                    var joingroups = joins[table].GroupBy(x => new { x.Right, x.JoinType }).ToDictionary(x => x.Key, x => x.SelectMany(y => y.Conditions ?? Enumerable.Empty<Conditions.Condition>()).Where(z => z != null));
                    foreach (var joingroup in joingroups.Where(x => x.Value.Any()))
                    {
                        command.CommandText +=
                            joingroup.Key.JoinType switch
                            {
                                Conditions.JoinType.Inner => " INNER JOIN ",
                                Conditions.JoinType.LeftOuter => " LEFT OUTER JOIN ",
                                _ => " CROSS JOIN ",
                            };
                        command.CommandText += QuoteIdentifier(joingroup.Key.Right);
                        if (joingroup.Key.JoinType != Conditions.JoinType.Cross && joingroup.Value != null && joingroup.Value.Any())
                        {
                            command.CommandText += " ON (";
                            command.CommandText += ConditionDefinition(joingroup.Value, command);
                            command.CommandText += ")";
                        }
                    }
                }
                i++;
            }
            AddWhere(conditions, command);
            if (groupFields != null && groupFields.Any())
            {
                command.CommandText += " GROUP BY " + string.Join(", ", groupFields.Values);
            }
            if (orderFields != null && orderFields.Any())
            {
                command.CommandText += " ORDER BY " + string.Join(", ", orderFields.Select(kvp => string.Format("{0} {1}", kvp.Key, kvp.Value ? "DESC" : "ASC")));
            }
            if (limit != null)
            {
                command.CommandText += LimitOffsetDefinition(command, limit, offset) ?? string.Empty;
            }
            return command;
        }
    }
}
