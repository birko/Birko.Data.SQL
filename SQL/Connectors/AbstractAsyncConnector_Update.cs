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
        public Task UpdateAsync(object model, LambdaExpression expr, CancellationToken ct = default)
        {
            return UpdateAsync(model, DataBase.ParseConditionExpression(expr), ct);
        }

        public Task UpdateAsync(object model, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            if (model != null)
            {
                return UpdateAsync(model.GetType(), model, conditions, ct);
            }
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Type type, object model, LambdaExpression expr, CancellationToken ct = default)
        {
            return UpdateAsync(DataBase.LoadTable(type), model, DataBase.ParseConditionExpression(expr), ct);
        }

        public Task UpdateAsync(Type type, object model, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            return UpdateAsync(DataBase.LoadTable(type), model, conditions, ct);
        }

        public Task UpdateAsync(Tables.Table table, object model, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            if (model != null)
            {
                return UpdateAsync(table.Name, table.GetSelectFields(), DataBase.Write(table.Fields.Select(f => f.Value), model), conditions, false, ct);
            }
            return Task.CompletedTask;
        }

        public Task UpdateAsync<T, P>(Type type, IDictionary<Expression<Func<T, P>>, object> expresions, LambdaExpression expr, CancellationToken ct = default)
        {
            return UpdateAsync(type, expresions, DataBase.ParseConditionExpression(expr), ct);
        }

        public Task UpdateAsync<T, P>(Type type, IDictionary<Expression<Func<T, P>>, Expression<Func<T, P>>> expresions, LambdaExpression expr, CancellationToken ct = default)
        {
            return UpdateAsync(type, expresions, DataBase.ParseConditionExpression(expr), ct);
        }

        public Task UpdateAsync<T, P>(Type type, IDictionary<Expression<Func<T, P>>, object> expresions, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            var table = DataBase.LoadTable(type);
            return UpdateAsync(table, expresions, conditions, ct);
        }

        public Task UpdateAsync<T, P>(Type type, IDictionary<Expression<Func<T, P>>, Expression<Func<T, P>>> expresions, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            var table = DataBase.LoadTable(type);
            return UpdateAsync(table, expresions, conditions, ct);
        }

        public Task UpdateAsync<T, P>(Tables.Table table, IDictionary<Expression<Func<T, P>>, object> expresions, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            if (table != null)
            {
                return UpdateAsync(table.Name, expresions, conditions, ct);
            }
            return Task.CompletedTask;
        }

        public Task UpdateAsync<T, P>(Tables.Table table, IDictionary<Expression<Func<T, P>>, Expression<Func<T, P>>> expresions, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            if (table != null)
            {
                return UpdateAsync(table.Name, expresions, conditions, ct);
            }
            return Task.CompletedTask;
        }

        public Task UpdateAsync<T, P>(string tableName, IDictionary<Expression<Func<T, P>>, Expression<Func<T, P>>> expresions, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            var fields = new Dictionary<int, string>();
            var values = new Dictionary<string, object>();
            int i = 0;
            foreach (var kvp in expresions)
            {
                var field = DataBase.GetField(kvp.Key);
                var fieldExpr = DataBase.ParseExpression(kvp.Value, values);
                fields.Add(i, field.Name + " = " + fieldExpr);
                i++;
            }
            return UpdateAsync(tableName, fields, values, conditions, true, ct);
        }

        public Task UpdateAsync<T, P>(string tableName, IDictionary<Expression<Func<T, P>>, object> expresions, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            var fields = new Dictionary<int, string>();
            var values = new Dictionary<string, object>();
            int i = 0;
            foreach (var kvp in expresions)
            {
                var field = DataBase.GetField(kvp.Key);
                fields.Add(i, field.Name);
                values.Add(field.Name, kvp.Value);
                i++;
            }
            return UpdateAsync(tableName, fields, values, conditions, false, ct);
        }

        public Task UpdateAsync(Tables.Table table, IDictionary<string, object> values, IEnumerable<Conditions.Condition>? conditions = null, CancellationToken ct = default)
        {
            var tableName = table.Name;
            IDictionary<int, string> fields = table.GetSelectFields();
            return UpdateAsync(tableName, fields, values, conditions, false, ct);
        }

        public virtual async Task UpdateAsync(string tableName, IDictionary<int, string> fields, IDictionary<string, object> values, IEnumerable<Conditions.Condition>? conditions = null, bool isExpressionValues = false, CancellationToken ct = default)
        {
            if (values != null && values.Any())
            {
                await DoCommandWithTransactionAsync(async (command) =>
                {
                    command.CommandText = "UPDATE " + QuoteIdentifier(tableName) + " SET ";
                    if (!isExpressionValues)
                    {
                        command.CommandText += string.Join(", ", fields.Values.Select(x => x + "= @SET" + x.Replace(".", string.Empty)));
                    }
                    else
                    {
                        command.CommandText += string.Join(", ", fields.Values.Select(x => x));
                    }

                    AddWhere(conditions, command);
                    foreach (var kvp in values)
                    {
                        if (!isExpressionValues)
                        {
                            AddParameter(command, "@SET" + kvp.Key.Replace(".", string.Empty), kvp.Value);
                        }
                        else
                        {
                            AddParameter(command, kvp.Key, kvp.Value);
                        }
                    }
                    await Task.CompletedTask;
                }, async (command) =>
                {
                    await command.ExecuteNonQueryAsync(ct);
                }, true);
            }
        }
    }
}
