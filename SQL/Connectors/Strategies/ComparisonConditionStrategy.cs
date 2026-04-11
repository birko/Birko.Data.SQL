using System.Data.Common;

namespace Birko.Data.SQL.Connectors.Strategies
{
    /// <summary>
    /// Strategy for handling comparison conditions (Less, Greater, LessAndEqual, GreaterAndEqual)
    /// </summary>
    public class ComparisonConditionStrategy : IConditionStrategy
    {
        public bool CanHandle(Conditions.ConditionType type)
        {
            return type is Conditions.ConditionType.Less
                or Conditions.ConditionType.Greather
                or Conditions.ConditionType.LessAndEqual
                or Conditions.ConditionType.GreatherAndEqual;
        }

        public string BuildSql(Conditions.Condition condition, DbCommand command, SqlBuilderContext context)
        {
            if (condition == null)
                throw new ArgumentNullException(nameof(condition));

            if (string.IsNullOrEmpty(condition.Name))
                throw new InvalidOperationException("Condition name cannot be null or empty");

            var op = GetOperator(condition);
            var valueExpression = BuildValueExpression(condition, command, context);

            return $"{condition.Name}{op}{valueExpression}";
        }

        private string GetOperator(Conditions.Condition condition)
        {
            return condition.Type switch
            {
                Conditions.ConditionType.Less => condition.IsNot ? " >= " : " < ",
                Conditions.ConditionType.Greather => condition.IsNot ? " <= " : " > ",
                Conditions.ConditionType.LessAndEqual => condition.IsNot ? " > " : " <= ",
                Conditions.ConditionType.GreatherAndEqual => condition.IsNot ? " < " : " >= ",
                _ => throw new NotSupportedException($"Condition type {condition.Type} is not supported by {nameof(ComparisonConditionStrategy)}"),
            };
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
