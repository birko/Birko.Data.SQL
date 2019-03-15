using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Birko.Data.SQL.Field;

namespace Birko.Data.SQL.Table
{
    public class View
    {
        public string Name { get; private set; }
        public IEnumerable<Table> Tables { get; private set; }
        public IEnumerable<Condition.Join> Join { get; private set; }

        public View(IEnumerable<Table> tables = null, IEnumerable<Condition.Join> join = null, string name = null)
        {
            Tables = tables;
            Join = join;
            Name = name;
            if (!string.IsNullOrEmpty(name) && Tables != null && Tables.Any())
            {
                Name = string.Join(string.Empty, Tables.Select(x => x.Name).Where(x => !string.IsNullOrEmpty(x)).Distinct());
            }
        }

        public View AddTable(Table table)
        {
            return AddTable(table.Name, table.Type, table.Fields.Values);
        }

        public View AddTable(string tableName, Type tableType, IEnumerable<Field.AbstractField> fields)
        {
            if (fields != null && fields.Any())
            {
                foreach (var field in fields)
                {
                    AddField(tableName, tableType, field);
                }
            }
            return this;
        }

        public View AddField(string tableName, Type tableType, AbstractField field, string name = null)
        {
            if (!string.IsNullOrEmpty(tableName) && field != null)
            {
                Table table = null;
                if (Tables != null && Tables.Any() && Tables.Any(x => x.Name == tableName))
                {
                    table = Tables.FirstOrDefault(x => x.Name == tableName);
                }
                else
                {
                    table = new Table() {
                        Name = tableName,
                        Type = tableType
                    };
                    Tables = (Tables == null) ? new[] { table } : Tables.Concat(new[] { table });
                }
                if (table.Fields == null)
                {
                    table.Fields = new Dictionary<string, AbstractField>();
                }
                var fieldName = (!string.IsNullOrEmpty(name)) ? name : field.Name;
                if (!table.Fields.ContainsKey(fieldName))
                {
                    field.Table = table;
                    table.Fields.Add(fieldName, field);
                }
            }
            return this;
        }

        public View AddJoin(IEnumerable<Condition.Join> conditions)
        {
            if (conditions != null && conditions.Any())
            {
                foreach (var condition in conditions)
                {
                    AddJoin(condition);
                }
            }
            return this;
        }

        public View AddJoin(Condition.Join condition)
        {
            if (condition != null)
            {
                if (Join == null)
                {
                    Join = new[] { condition };
                }
                else if (Join.Any(x => x.Left == condition.Left && x.Right == condition.Right && x.JoinType == condition.JoinType))
                {
                    var join = Join.FirstOrDefault(x => x.Left == condition.Left && x.Right == condition.Right && x.JoinType == condition.JoinType);
                    if (join != null)
                    {
                        join.AddConditions(condition.Conditions);
                    }
                }
                else
                {
                    Join = Join.Concat(new[] { condition });
                }
            }
            return this;
        }

        public IDictionary<int, string> GetSelectFields(bool notAggregate = false)
        {
            var result = new Dictionary<int, string>();
            int i = 0;
            foreach (var table in Tables)
            {
                var fields = table?.GetSelectFields(true, notAggregate);
                if (fields != null && fields.Any())
                {
                    foreach (var field in fields)
                    {
                        result.Add(i, field.Value);
                        i++;
                    }
                }
            }
            return result;
        }

        internal IEnumerable<AbstractField> GetTableFields(bool notAggregate = false)
        {
            List<AbstractField> tableFields = new List<Field.AbstractField>();
            foreach(var table in Tables.Where(x=> x!= null))
            {
                tableFields.AddRange(table.GetTableFields(notAggregate));
            }
            return tableFields;
        }

        public bool HasAggregateFields()
        {
            return Tables?.Any(x => x?.HasAggregateFields() ?? false) ?? false;
        }
    }
}
