using System.Data;
using System.Data.Common;
using System.Text;

namespace Birko.Data.SQL.Connectors
{
    /// <summary>
    /// Context class for SQL building operations
    /// </summary>
    public class SqlBuilderContext
    {
        private static readonly System.Text.RegularExpressions.Regex _sanitizeRegex =
            new(@"[^a-zA-Z0-9_]", System.Text.RegularExpressions.RegexOptions.Compiled);

        private readonly StringBuilder _stringBuilder;
        private readonly AbstractConnectorBase _connector;

        public SqlBuilderContext(AbstractConnectorBase connector)
        {
            _connector = connector ?? throw new ArgumentNullException(nameof(connector));
            _stringBuilder = new StringBuilder();
        }

        /// <summary>
        /// Generates a unique parameter name for the given field
        /// </summary>
        public string GenerateParameterName(string fieldName, int index, DbCommand command)
        {
            if (string.IsNullOrEmpty(fieldName))
            {
                throw new ArgumentException("Field name cannot be null or empty", nameof(fieldName));
            }

            var count = command.Parameters?.Count ?? 0;
            var sanitizedName = _sanitizeRegex.Replace(fieldName, string.Empty);
            return $"@WHERE{sanitizedName}{index}_{count}";
        }

        /// <summary>
        /// Formats a value based on the condition type (e.g., adds wildcards for LIKE operations)
        /// </summary>
        public object? FormatValue(object? value, Conditions.ConditionType type)
        {
            if (value is string str)
            {
                return type switch
                {
                    Conditions.ConditionType.StartsWith => $"{str}%",
                    Conditions.ConditionType.Like => $"%{str}%",
                    Conditions.ConditionType.EndsWith => $"%{str}",
                    _ => str,
                };
            }
            return value;
        }

        /// <summary>
        /// Escapes a value to prevent SQL injection (fallback when parameters can't be used)
        /// </summary>
        public object? EscapeValue(object? item)
        {
            if (item is string str)
            {
                // TASK-253: one producer for the quote-doubling rule. This sink legitimately escapes
                // rather than parameterises — see SqlLiteral for the two cases where that is true.
                return SqlLiteral.EscapeLiteral(str);
            }
            return item;
        }

        /// <summary>
        /// Adds a parameter to the command
        /// </summary>
        public void AddParameter(DbCommand command, string name, object? value)
        {
            _connector.AddParameter(command, name, value);
        }
    }
}
