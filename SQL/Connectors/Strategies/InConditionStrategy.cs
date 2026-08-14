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
            // supported providers. The two halves are NOT symmetric:
            //   · empty IN     → matches nothing    → always false → rendered as the `1 = 0` constant
            //   · empty NOT IN → matches everything → reduced away by the caller, never rendered
            //
            // TASK-137: the always-true half used to render `1 = 1`, and that was wrong twice over. `1 = 1` is
            // the signature of `' OR 1=1--`, so emitting it during normal operation trains operators to scroll
            // past the pattern they are supposed to react to — and, far worse, it is a non-empty WHERE that
            // constrains nothing, so it satisfied AddRequiredWhere's whole-table guard. Measured before the
            // fix: `Delete(x => !empty.Contains(x.Col))` left 0 of 3 rows and threw nothing.
            //
            // An always-true term has no rendering here BY DESIGN — `A AND TRUE` is `A`, so
            // AbstractConnectorBase.IsAlwaysTrueCondition reduces it away one layer up, where the surrounding
            // AND/OR context (and the negation of an enclosing group) is known. This throws rather than
            // returning a constant or an empty string: a tautology is the defect, and an empty string would be
            // silently swallowed by a chain that then joins two separators together. Nothing in the framework
            // reaches it — ConditionDefinition skips such terms before BuildSingleCondition is called.
            if (!HasAnyValue(condition.Values))
            {
                if (!condition.IsNot)
                    return Connectors.AbstractConnectorBase.AlwaysFalseSql;

                throw new InvalidOperationException(
                    $"An empty NOT IN on '{condition.Name}' matches every row and has no SQL rendering: "
                        + "it must be reduced away by the enclosing chain, which "
                        + $"{nameof(Connectors.AbstractConnectorBase)}."
                        + $"{nameof(Connectors.AbstractConnectorBase.IsAlwaysTrueCondition)} exists to detect. "
                        + "Render the condition tree through ConditionDefinition rather than calling this "
                        + "strategy directly. (TASK-137: it previously rendered `1 = 1`, which satisfied the "
                        + "whole-table write guard with a tautology.)");
            }

            var op = condition.IsNot ? " NOT IN " : " IN ";
            var inClause = BuildInClause(condition, command, context);

            return $"{condition.Name}{op}{inClause}";
        }

        /// <summary>
        /// True when the condition carries at least one value. `Values` is a non-generic IEnumerable, so this
        /// enumerates rather than reading a Count — it stops at the first element.
        /// </summary>
        private static bool HasAnyValue(System.Collections.IEnumerable? values)
        {
            if (values == null) return false;
            foreach (var _ in values) return true;
            return false;
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
