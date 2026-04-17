using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Birko.Data.Stores;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractAsyncConnector
    {
        /// <summary>
        /// Executes an aggregation query using SQL GROUP BY.
        /// Builds SELECT with aggregate functions, passes groupFields to CreateSelectCommand.
        /// </summary>
        public async IAsyncEnumerable<AggregateResult> SelectAggregateAsync<T>(
            Type type,
            AggregateQuery<T> query,
            [EnumeratorCancellation] CancellationToken ct = default)
            where T : Models.AbstractModel
        {
            var table = DataBase.LoadTable(type);
            if (table == null) yield break;

            var (fields, groupFields, conditions) = BuildAggregateQueryParts(type, query);

            await foreach (var row in RunReaderCommandAsync(
                async command =>
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
                    await Task.CompletedTask;
                },
                async reader => new[] { ReadAggregateResult(reader) }))
            {
                foreach (var item in row) yield return (AggregateResult)item;
            }
        }

    }
}
