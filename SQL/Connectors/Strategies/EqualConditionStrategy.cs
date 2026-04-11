using System.Data.Common;

namespace Birko.Data.SQL.Connectors.Strategies
{
    /// <summary>
    /// Strategy for handling Equal (and Not Equal) conditions
    /// </summary>
    public class EqualConditionStrategy : IConditionStrategy
    {
        public bool CanHandle(Conditions.ConditionType type)
        {
            return type == Conditions.ConditionType.Equal;
        }

        public string BuildSql(Conditions.Condition condition, DbCommand command, SqlBuilderContext context)
        {
            if (condition == null)
                throw new ArgumentNullException(nameof(condition));

            if (string.IsNullOrEmpty(condition.Name))
                throw new InvalidOperationException("Condition name cannot be null or empty");

            var op = condition.IsNot ? " <> " : " = ";
            var valueExpression = BuildValueExpression(condition, command, context);

            return $"{condition.Name}{op}{valueExpression}";
        }

        private string BuildValueExpression(Conditions.Condition condition, DbCommand command, SqlBuilderContext context)
        {
            if (condition.Values == null)
                return "NULL";

            var enumerator = condition.Values.GetEnumerator();
            if (!enumerator.MoveNext())
                return "NULL";

            var first = enumerator.Current;

            if (!condition.IsField && first != null)
            {
                var paramName = context.GenerateParameterName(condition.Name!, 0, command);
                context.AddParameter(command, paramName, first);
                return paramName;
            }
            else
            {
                return first?.ToString() ?? "NULL";
            }
        }
    }
}
