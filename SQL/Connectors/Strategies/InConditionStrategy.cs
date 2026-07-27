using System.Data.Common;
using System.Text;

namespace Birko.Data.SQL.Connectors.Strategies
{
    /// <summary>
    /// Strategy for handling IN clause conditions
    /// </summary>
    public class InConditionStrategy : IConditionStrategy
    {
        public bool CanHandle(Conditions.ConditionType type)
        {
            return type == Conditions.ConditionType.In;
        }

        public string BuildSql(Conditions.Condition condition, DbCommand command, SqlBuilderContext context)
        {
            if (condition == null)
                throw new ArgumentNullException(nameof(condition));

            if (string.IsNullOrEmpty(condition.Name))
                throw new InvalidOperationException("Condition name cannot be null or empty");

            // An EMPTY value set must not render as `Col IN ()`. That is a syntax error on PostgreSQL and
            // MSSQL; SQLite's grammar happens to permit it and treats it as always-false, so the defect is
            // invisible to a SQLite-only test environment while being a hard failure on two of the four
            // supported providers. Emit a constant with the same SET SEMANTICS instead:
            //   · empty IN     → matches nothing    → always false
            //   · empty NOT IN → matches everything → always true (it must NOT silently invert)
            // `1 = 0` / `1 = 1` are valid on every supported dialect and need no parameters, so the clause
            // still composes inside AND/OR chains exactly as a real IN would.
            if (IsEmpty(condition.Values))
                return condition.IsNot ? "1 = 1" : "1 = 0";

            var op = condition.IsNot ? " NOT IN " : " IN ";
            var inClause = BuildInClause(condition, command, context);

            return $"{condition.Name}{op}{inClause}";
        }

        /// <summary>
        /// True when the condition carries no values at all. `Values` is a non-generic IEnumerable, so this
        /// enumerates rather than reading a Count — it stops at the first element.
        /// </summary>
        private static bool IsEmpty(System.Collections.IEnumerable? values)
        {
            if (values == null) return true;
            foreach (var _ in values) return false;
            return true;
        }

        private string BuildInClause(Conditions.Condition condition, DbCommand command, SqlBuilderContext context)
        {
            var sb = new StringBuilder("(");

            if (condition.Values != null)
            {
                int i = 0;
                foreach (var item in condition.Values)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    if (!condition.IsField)
                    {
                        var paramName = context.GenerateParameterName(condition.Name!, i, command);
                        context.AddParameter(command, paramName, item);
                        sb.Append(paramName);
                    }
                    else
                    {
                        sb.Append(item?.ToString() ?? "NULL");
                    }

                    i++;
                }
            }

            sb.Append(")");
            return sb.ToString();
        }
    }
}
