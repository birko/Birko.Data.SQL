using System.Data.Common;

namespace Birko.Data.SQL.Connectors.Strategies
{
    /// <summary>
    /// Strategy for handling IS NULL and IS NOT NULL conditions
    /// </summary>
    public class NullConditionStrategy : IConditionStrategy
    {
        public bool CanHandle(Conditions.ConditionType type)
        {
            return type == Conditions.ConditionType.IsNull;
        }

        public string BuildSql(Conditions.Condition condition, DbCommand command, SqlBuilderContext context)
        {
            if (condition == null)
                throw new ArgumentNullException(nameof(condition));

            if (string.IsNullOrEmpty(condition.Name))
                throw new InvalidOperationException("Condition name cannot be null or empty");

            var op = condition.IsNot ? " IS NOT NULL" : " IS NULL";
            return $"{condition.Name}{op}";
        }
    }
}
