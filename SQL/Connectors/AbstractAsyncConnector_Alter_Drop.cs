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
        public Task AlterTableDropAsync(Type type, IEnumerable<Fields.AbstractField> fields, CancellationToken ct = default)
        {
            return AlterTableDropAsync(DataBase.LoadTable(type), fields, ct);
        }

        public Task AlterTableDropAsync(Tables.Table table, IEnumerable<Fields.AbstractField> fields, CancellationToken ct = default)
        {
            if (table != null && fields != null && fields.Any())
            {
                return AlterTableDropAsync(table.Name, fields, ct);
            }
            return Task.CompletedTask;
        }

        public virtual async Task AlterTableDropAsync(string tableName, IEnumerable<Fields.AbstractField> fields, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(tableName) && fields != null && fields.Any())
            {
                foreach (var field in fields.Where(x => x != null))
                {
                    await DoCommandWithTransactionAsync(async (command) =>
                    {
                        command.CommandText = "ALTER TABLE "
                            + QuoteIdentifier(tableName)
                            + " DROP COLUMN "
                            + field.Name;
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
