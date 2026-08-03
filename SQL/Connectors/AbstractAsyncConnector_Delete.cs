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
        public Task DeleteAsync(Type type, LambdaExpression expr, CancellationToken ct = default)
        {
            return DeleteAsync(type, DataBase.ParseConditionExpression(expr), ct);
        }

        public Task DeleteAsync(Type type, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            return DeleteAsync(DataBase.LoadTable(type), conditions, ct);
        }

        public Task DeleteAsync(Tables.Table table, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            var tableName = table.Name;
            return DeleteAsyncAsync(tableName, conditions, ct);
        }

        /// <inheritdoc cref="AbstractConnector.DeleteAll(Type)"/>
        public Task DeleteAllAsync(Type type, CancellationToken ct = default)
        {
            return DeleteAsyncAsync(DataBase.LoadTable(type).Name, conditions: null, ct, allowAllRows: true);
        }

        private async Task DeleteAsyncAsync(string tableName, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default, bool allowAllRows = false)
        {
            // SH-H002 — refuse before the transaction wrapper; see AbstractConnector_Delete.
            if (!allowAllRows && WouldTargetEveryRow(conditions))
            {
                throw new Data.Exceptions.WholeTableWriteException("delete", tableName);
            }

            await DoCommandWithTransactionAsync(async (command) =>
            {
                command.CommandText = "DELETE FROM " + QuoteIdentifier(tableName);
                AddRequiredWhere(conditions, command, "delete", tableName, allowAllRows);
                await Task.CompletedTask;
            }, async (command) =>
            {
                await command.ExecuteNonQueryAsync(ct);
            }, true);
        }
    }
}
