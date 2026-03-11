using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractAsyncConnector
    {
        public Task<long> SelectCountAsync(Type type, LambdaExpression? expr, CancellationToken ct = default)
        {
            return SelectCountAsync(new[] { type }, expr, ct);
        }

        public Task<long> SelectCountAsync(IEnumerable<Type> types, LambdaExpression? expr = null, CancellationToken ct = default)
        {
            return SelectCountAsync(types, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, ct);
        }

        public Task<long> SelectCountAsync(Type type, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            return SelectCountAsync(new[] { type }, conditions, ct);
        }

        public Task<long> SelectCountAsync(IEnumerable<Type> types, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            return (types != null) ? SelectCountAsync(types.Select(x => DataBase.LoadTable(x)), conditions, ct) : Task.FromResult(0L);
        }

        public Task<long> SelectCountAsync(IEnumerable<Tables.Table> tables, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            return (tables != null) ? SelectCountAsync(tables.Select(x => x.Name), conditions, ct) : Task.FromResult(0L);
        }

        public Task<long> SelectCountAsync(IEnumerable<string> tableNames, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            return SelectCountAsync(tableNames, null, conditions, ct);
        }

        public virtual async Task<long> SelectCountAsync(IEnumerable<string> tableNames, IEnumerable<Conditions.Join>? joinconditions = null, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            long count = 0;
            if (tableNames != null && tableNames.Any() && tableNames.Any(x => !string.IsNullOrEmpty(x)))
            {
                await DoCommandAsync(async (command) =>
                {
                    command = CreateSelectCommand(
                        command,
                        tableNames.Where(x => !string.IsNullOrEmpty(x)).Distinct(),
                        new Dictionary<int, string>()
                        {
                            { 0, "count(*) as count"}
                        },
                        joinconditions, conditions);
                    await Task.CompletedTask;
                }, async (command) =>
                {
                    var data = await command.ExecuteScalarAsync(ct);
                    count = data != null ? Convert.ToInt64(data) : 0;
                });
            }
            return count;
        }
    }
}
