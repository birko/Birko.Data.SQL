using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractConnector
    {
        public void CreateTable(Type type)
        {
            CreateTable(new[] { type });
        }

        public void CreateTable(Type[] types)
        {
            CreateTable(DataBase.LoadTables(types));
        }

        public void CreateTable(Tables.Table table)
        {
            CreateTable(new[] { table });
        }

        public void CreateTable(IEnumerable<Tables.Table> tables)
        {
            if (tables != null && tables.Any() && tables.Any(x => x != null && x.Fields != null && x.Fields.Count > 0))
            {
                CreateTable(tables.ToDictionary(x => x.Name, x => x.Fields.Select(y => y.Value)));
            }
        }

        public void CreateTable(string tableName, IEnumerable<Fields.AbstractField> fields)
        {
            CreateTable(new Dictionary<string, IEnumerable<Fields.AbstractField>>() {
                { tableName, fields }
            });
        }

        public void CreateTable(IDictionary<string, IEnumerable<Fields.AbstractField>> tables)
        {
            if (tables != null && tables.Any() && tables.Any(x => x.Value != null && x.Value.Count() > 0))
            {
                foreach (var kvp in tables.Where(x => x.Value != null && x.Value.Any()))
                {
                    CreateTable(kvp.Key, kvp.Value.Select(x => FieldDefinition(x)));
                }
            }
        }

        public virtual void CreateTable(string name, IEnumerable<string> fields)
        {
            DoCommandWithTransaction((command) =>
            {
                command.CommandText = "CREATE TABLE IF NOT EXISTS "
                    + name
                    + " ("
                    + string.Join(", ", fields.Where(x => !string.IsNullOrEmpty(x)))
                    + ")";
            }, (command) =>
            {
                command.ExecuteNonQuery();
            }, true);
        }
    }
}
