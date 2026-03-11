using System.Data;
using System.Data.Common;

namespace Birko.Data.SQL.Connectors
{
    /// <summary>
    /// Strategy interface for building SQL condition clauses
    /// </summary>
    public interface IConditionStrategy
    {
        /// <summary>
        /// Determines if this strategy can handle the given condition type
        /// </summary>
        bool CanHandle(Conditions.ConditionType type);

        /// <summary>
        /// Builds the SQL condition clause for the given condition
        /// </summary>
        string BuildSql(Conditions.Condition condition, DbCommand command, SqlBuilderContext context);
    }
}
