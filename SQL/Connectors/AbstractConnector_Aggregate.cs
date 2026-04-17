using System;
using System.Collections.Generic;
using Birko.Data.Stores;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractConnector
    {
        /// <summary>
        /// Executes a synchronous aggregation query using SQL GROUP BY.
        /// Builds SELECT with aggregate functions, passes groupFields to CreateSelectCommand.
        /// </summary>
        public IEnumerable<AggregateResult> SelectAggregate<T>(
            Type type,
            AggregateQuery<T> query)
            where T : Models.AbstractModel
        {
            var table = DataBase.LoadTable(type);
            if (table == null) yield break;

            var (fields, groupFields, conditions) = BuildAggregateQueryParts(type, query);

            foreach (var row in RunReaderCommand(
                command =>
                {
                    CreateSelectCommand(
                        command,
                        new[] { table.Name },
                        fields,
                        null,
                        conditions,
                        groupFields.Count > 0 ? groupFields : null,
                        query.OrderBy,
                        query.Limit,
                        query.Offset);
                },
                reader => new object[] { ReadAggregateResult(reader) }))
            {
                foreach (var item in row) yield return (AggregateResult)item;
            }
        }

    }
}
