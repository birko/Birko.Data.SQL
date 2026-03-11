using System.Data.Common;

namespace Birko.Data.SQL.Connectors.Strategies
{
    /// <summary>
    /// Strategy for handling LIKE conditions (Like, StartsWith, EndsWith)
    /// </summary>
    public class LikeConditionStrategy : IConditionStrategy
    {
        public bool CanHandle(Conditions.ConditionType type)
        {
            return type is Conditions.ConditionType.Like
                or Conditions.ConditionType.StartsWith
                or Conditions.ConditionType.EndsWith;
        }

        public string BuildSql(Conditions.Condition condition, DbCommand command, SqlBuilderContext context)
        {
            if (condition == null)
                throw new ArgumentNullException(nameof(condition));

            if (string.IsNullOrEmpty(condition.Name))
                throw new InvalidOperationException("Condition name cannot be null or empty");

            var op = condition.IsNot ? " NOT LIKE " : " LIKE ";
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
                var formattedValue = context.FormatValue(first, condition.Type);
                var paramName = context.GenerateParameterName(condition.Name, 0, command);
                var escapedValue = context.EscapeValue(formattedValue);
                context.AddParameter(command, paramName, escapedValue);
                return paramName;
            }
            else
            {
                return first?.ToString() ?? "NULL";
            }
        }
    }
}
