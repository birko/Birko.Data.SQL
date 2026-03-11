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
        public Task AlterTableAddAsync(Type type, IEnumerable<Fields.AbstractField> fields, CancellationToken ct = default)
        {
            return AlterTableAddAsync(DataBase.LoadTable(type), fields, ct);
        }

        public Task AlterTableAddAsync(Tables.Table table, IEnumerable<Fields.AbstractField> fields, CancellationToken ct = default)
        {
            if (table != null && fields != null && fields.Any())
            {
                return AlterTableAddAsync(table.Name, fields, ct);
            }
            return Task.CompletedTask;
        }

        public virtual async Task AlterTableAddAsync(string tableName, IEnumerable<Fields.AbstractField> fields, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(tableName) && fields != null && fields.Any())
            {
                foreach (var field in fields.Where(x => x != null))
                {
                    await DoCommandWithTransactionAsync(async (command) =>
                    {
                        command.CommandText = "ALTER TABLE "
                            + QuoteIdentifier(tableName)
                            + " ADD COLUMN "
                            + FieldDefinition(field);
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
