using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractConnector
    {
        public void Insert(object model)
        {
            if (model != null)
            {
                Insert(model.GetType(), model);
            }
        }

        public void Insert(Type type, object model)
        {
            Insert(DataBase.LoadTable(type), model);
        }

        public void Insert(Tables.Table table, object model)
        {
            if (model != null)
            {
                Insert(table, new[] { DataBase.Write(table.Fields.Select(f => f.Value), model) });
            }
        }

        public void Insert(Tables.Table table, IDictionary<string, object> values)
        {
            var tableName = table.Name;
            Insert(tableName, values);
        }

        public void Insert(Tables.Table table, IEnumerable<object> models)
        {
            if (models != null && models.Any() && models.Any(x => x != null))
            {
                var tableName = table.Name;
                Insert(tableName, models.Where(x => x != null).Select(x => DataBase.Write(table.Fields.Select(f => f.Value), x)));
            }
        }

        private void Insert(string tableName, IDictionary<string, object> values)
        {
            Insert(tableName, new[] { values });
        }

        public void Insert(Tables.Table table, IEnumerable<IDictionary<string, object>> values)
        {
            var tableName = table.Name;
            Insert(tableName, values);
        }

        public void Insert(string tableName, IEnumerable<IDictionary<string, object>> values)
        {
            if (values != null && values.Any() && values.All(x => x.Any()))
            {
                var first = values.First();
                DoCommand((command) =>
                {
                    command.CommandText = "INSERT INTO " + tableName
                                + " (" + string.Join(", ", first.Keys) + ")"
                                + " VALUES"
                                + " (" + string.Join(", ", first.Keys.Select(x => "@" + x.Replace(".", string.Empty))) + ")";
                    foreach (var kvp in first)
                    {
                        AddParameter(command, "@" + kvp.Key.Replace(".", string.Empty), kvp.Value);
                    }
                }, (command) =>
                {
                    foreach (var item in values)
                    {
                        foreach (var kvp in item)
                        {
                            AddParameter(command, "@" + kvp.Key.Replace(".", string.Empty), kvp.Value);
                        }
                        command.ExecuteNonQuery();
                    }
                }, true);
            }
        }
    }
}
