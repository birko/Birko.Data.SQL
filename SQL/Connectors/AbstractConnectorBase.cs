using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
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

        // Strategy pattern for condition building
        private readonly List<IConditionStrategy> _conditionStrategies = new();

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
            _conditionStrategies.Add(new Strategies.EqualConditionStrategy());
            _conditionStrategies.Add(new Strategies.ComparisonConditionStrategy());
            _conditionStrategies.Add(new Strategies.LikeConditionStrategy());
            _conditionStrategies.Add(new Strategies.InConditionStrategy());
            _conditionStrategies.Add(new Strategies.NullConditionStrategy());
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
        /// Builds a SQL condition clause using the strategy pattern
        /// </summary>
        public virtual string ConditionDefinition(Conditions.Condition condition, DbCommand command)
        {
            if (condition == null) return string.Empty;

            // Handle subconditions (nested conditions with AND/OR logic)
            if (condition.SubConditions?.Any() == true)
            {
                return BuildSubConditions(condition, command);
            }

            // Handle single condition
            return BuildSingleCondition(condition, command);
        }

        /// <summary>
        /// Builds SQL for nested/sub conditions
        /// </summary>
        private string BuildSubConditions(Conditions.Condition condition, DbCommand command)
        {
            // Pass the parent's IsOr flag to subconditions so they're joined correctly.
            // The parser sets IsOr on the parent, but subconditions default to IsOr=false.
            if (condition.IsOr && condition.SubConditions != null)
            {
                foreach (var sub in condition.SubConditions)
                {
                    sub.IsOr = true;
                }
            }
            var subConditionsSql = ConditionDefinition(condition.SubConditions!, command);
            var needsParens = condition.SubConditions!.Count() > 1;
            return needsParens ? $"({subConditionsSql})" : subConditionsSql;
        }

        /// <summary>
        /// Builds SQL for a single condition using the appropriate strategy
        /// </summary>
        private string BuildSingleCondition(Conditions.Condition condition, DbCommand command)
        {
            if (string.IsNullOrEmpty(condition.Name))
            {
                throw new InvalidOperationException("Condition name cannot be null or empty for non-subconditions");
            }

            var strategy = _conditionStrategies.FirstOrDefault(s => s.CanHandle(condition.Type));
            if (strategy == null)
            {
                throw new NotSupportedException($"Condition type {condition.Type} is not supported");
            }

            var context = new SqlBuilderContext(this);
            return strategy.BuildSql(condition, command, context);
        }

        /// <summary>
        /// Builds SQL for multiple conditions
        /// </summary>
        public virtual string ConditionDefinition(IEnumerable<Conditions.Condition>? conditions, DbCommand command)
        {
            var result = new StringBuilder();
            if (conditions != null && conditions.Any())
            {
                int i = 0;
                foreach (var condition in conditions)
                {
                    if (i > 0)
                    {
                        if (condition.IsOr)
                        {
                            result.Append(" OR ");
                        }
                        else
                        {
                            result.Append(" AND ");
                        }
                    }
                    result.Append(ConditionDefinition(condition, command));
                    i++;
                }
            }
            return result.ToString();
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
            if (command != null && conditions != null && conditions.Any())
            {
                command.CommandText += " WHERE ";
                command.CommandText += ConditionDefinition(conditions, command);
            }
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
