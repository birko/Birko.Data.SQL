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

            var op = condition.IsNot ? " NOT IN " : " IN ";
            var inClause = BuildInClause(condition, command, context);

            return $"{condition.Name}{op}{inClause}";
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
                        var paramName = context.GenerateParameterName(condition.Name, i, command);
                        var escapedValue = context.EscapeValue(item);
                        context.AddParameter(command, paramName, escapedValue);
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
