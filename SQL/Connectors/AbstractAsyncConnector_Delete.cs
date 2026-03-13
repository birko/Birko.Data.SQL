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

        private async Task DeleteAsyncAsync(string tableName, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            await DoCommandWithTransactionAsync(async (command) =>
            {
                command.CommandText = "DELETE FROM " + QuoteIdentifier(tableName);
                AddWhere(conditions, command);
                await Task.CompletedTask;
            }, async (command) =>
            {
                await command.ExecuteNonQueryAsync(ct);
            }, true);
        }
    }
}
