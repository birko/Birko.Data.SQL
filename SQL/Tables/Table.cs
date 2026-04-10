using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Birko.Data.SQL.Fields;

namespace Birko.Data.SQL.Tables
{
    public class Table
    {
        public string Name { get; set; } = null!;
        public Dictionary<string, Fields.AbstractField> Fields { get; set; } = null!;
        public Type Type { get; set; } = null!;
        public Dictionary<string, IndexDefinition>? Indexes { get; set; }

        /// <summary>
        /// Reverse lookup: property name → field. Built lazily on first GetFieldByPropertyName call.
        /// </summary>
        private Dictionary<string, Fields.AbstractField>? _propertyNameIndex;

        public IDictionary<int, string> GetSelectFields(bool withName  = false, bool notAggregate = false)
        {
            Dictionary<int, string> fields = new Dictionary<int, string>();
            var keys = Fields.Keys.ToArray();
            for (int i = 0; i < keys.Length; i++)
            {
                var field = Fields[keys[i]];
                if (!notAggregate || !field.IsAggregate)
                {
                    fields.Add(i, field.GetSelectName(withName) + (field.IsAggregate? " as " + keys[i] : "") );
                }
            }
            return fields;
        }

        internal IEnumerable<AbstractField> GetTableFields(bool notAggregate)
        {
            List<AbstractField> tableFields = new List<Fields.AbstractField>();
            foreach (var field in Fields.Where(x => x.Value != null))
            {
                if (!notAggregate || !field.Value.IsAggregate)
                {
                    tableFields.Add(field.Value);
                }
            }
            return tableFields;
        }

        public bool HasAggregateFields()
        {
            return Fields?.Any(x => x.Value?.IsAggregate ?? false) ?? false;
        }

        internal IEnumerable<Fields.AbstractField>? GetPrimaryFields()
        {
            return Fields?.Values.Where(x => x.IsPrimary);
        }

        internal Fields.AbstractField? GetField(string name)
        {
            if (Fields == null) return null;
            return Fields.TryGetValue(name, out var field) ? field : null;
        }

        internal Fields.AbstractField? GetFieldByPropertyName(string name)
        {
            if (Fields == null || Fields.Count == 0) return null;

            _propertyNameIndex ??= Fields.Values
                .Where(f => f.Property != null)
                .ToDictionary(f => f.Property.Name, f => f);

            return _propertyNameIndex.TryGetValue(name, out var field) ? field : null;
        }
    }
}
