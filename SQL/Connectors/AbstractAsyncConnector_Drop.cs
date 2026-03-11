using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractAsyncConnector
    {
        public Task DropTableAsync(Type[] types, CancellationToken ct = default)
        {
            return DropTableAsync(DataBase.LoadTables(types), ct);
        }

        public Task DropTableAsync(IEnumerable<Tables.Table> tables, CancellationToken ct = default)
        {
            if (tables != null && tables.Any() && tables.Any(x => x != null))
            {
                return DropTableAsync(tables.Where(x => x != null).Select(x => x.Name), ct);
            }
            return Task.CompletedTask;
        }

        public virtual async Task DropTableAsync(IEnumerable<string> tables, CancellationToken ct = default)
        {
            if (tables != null && tables.Any() && tables.Any(x => !string.IsNullOrEmpty(x)))
            {
                foreach (var tableName in tables.Where(x => !string.IsNullOrEmpty(x)))
                {
                    await DoCommandWithTransactionAsync(async (command) =>
                    {
                        command.CommandText = "DROP TABLE IF EXISTS " + QuoteIdentifier(tableName);
                        await Task.CompletedTask;
                    }, async (command) =>
                    {
                        await command.ExecuteNonQueryAsync(ct);
                    }, true);
                }
            }
        }
    }
}
