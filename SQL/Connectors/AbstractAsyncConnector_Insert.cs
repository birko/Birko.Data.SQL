using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractAsyncConnector
    {
        public Task InsertAsync(object model, CancellationToken ct = default)
        {
            if (model != null)
            {
                return InsertAsync(model.GetType(), model, ct);
            }
            return Task.CompletedTask;
        }

        public Task InsertAsync(Type type, object model, CancellationToken ct = default)
        {
            return InsertAsync(DataBase.LoadTable(type), model, ct);
        }

        public Task InsertAsync(Tables.Table table, object model, CancellationToken ct = default)
        {
            if (model != null)
            {
                return InsertAsync(table, new[] { DataBase.Write(table.Fields.Select(f => f.Value), model) }, ct);
            }
            return Task.CompletedTask;
        }

        public Task InsertAsync(Tables.Table table, IDictionary<string, object> values, CancellationToken ct = default)
        {
            var tableName = table.Name;
            return InsertAsync(tableName, values, ct);
        }

        public Task InsertAsync(Tables.Table table, IEnumerable<object> models, CancellationToken ct = default)
        {
            if (models != null && models.Any() && models.Any(x => x != null))
            {
                var tableName = table.Name;
                return InsertAsync(tableName, models.Where(x => x != null).Select(x => DataBase.Write(table.Fields.Select(f => f.Value), x)), ct);
            }
            return Task.CompletedTask;
        }

        private Task InsertAsync(string tableName, IDictionary<string, object> values, CancellationToken ct)
        {
            return InsertAsync(tableName, new[] { values }, ct);
        }

        public Task InsertAsync(Tables.Table table, IEnumerable<IDictionary<string, object>> values, CancellationToken ct = default)
        {
            var tableName = table.Name;
            return InsertAsync(tableName, values, ct);
        }

        public virtual async Task InsertAsync(string tableName, IEnumerable<IDictionary<string, object>> values, CancellationToken ct = default)
        {
            if (values != null && values.Any() && values.All(x => x.Any()))
            {
                var first = values.First();
                await DoCommandWithTransactionAsync(async (command) =>
                {
                    command.CommandText = "INSERT INTO " + QuoteIdentifier(tableName)
                                + " (" + string.Join(", ", first.Keys) + ")"
                                + " VALUES"
                                + " (" + string.Join(", ", first.Keys.Select(x => "@" + x.Replace(".", string.Empty))) + ")";
                    foreach (var kvp in first)
                    {
                        AddParameter(command, "@" + kvp.Key.Replace(".", string.Empty), kvp.Value);
                    }
                    await Task.CompletedTask;
                }, async (command) =>
                {
                    foreach (var item in values)
                    {
                        foreach (var kvp in item)
                        {
                            AddParameter(command, "@" + kvp.Key.Replace(".", string.Empty), kvp.Value);
                        }
                        await command.ExecuteNonQueryAsync(ct);
                    }
                }, true);
            }
        }
    }
}
