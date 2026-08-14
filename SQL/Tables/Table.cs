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

        /// <param name="aggregateAlias">
        /// Whether an aggregate field's projection carries its <c>as &lt;alias&gt;</c> suffix. True for
        /// every read path (the alias names the column in the result set). <b>False for the view-DDL
        /// path</b>, which appends its own *quoted* alias — see <c>ViewSelectSqlBuilder</c> and TASK-129:
        /// both emitting produced <c>COUNT(VOrders.PersonId) as COUNT AS "OrderCount"</c>, two aliases on
        /// one column and a syntax error on every provider.
        /// </param>
        public IDictionary<int, string> GetSelectFields(bool withName  = false, bool notAggregate = false, bool aggregateAlias = true)
        {
            Dictionary<int, string> fields = new Dictionary<int, string>();
            var keys = Fields.Keys.ToArray();
            for (int i = 0; i < keys.Length; i++)
            {
                var field = Fields[keys[i]];
                if (!notAggregate || !field.IsAggregate)
                {
                    fields.Add(i, field.GetSelectName(withName)
                        + (field.IsAggregate && aggregateAlias ? " as " + AggregateAlias(field, keys[i]) : ""));
                }
            }
            return fields;
        }

        /// <summary>
        /// The single name an aggregate column is exposed under. TASK-129: an aggregate has exactly one
        /// public identity — its view property — and three places have to agree on it: this alias, the
        /// persistent read (<c>View.GetPersistentViewSelectFields</c>) and the sort key
        /// (<c>DataBase.ViewOrderFieldName</c>). All three read it off
        /// <see cref="Birko.Data.SQL.Fields.AbstractField.Property"/>, so they agree by construction rather
        /// than by each view builder happening to key the field the same way — which is what went wrong:
        /// the field-dictionary key held the SQL function name (<c>COUNT</c>), so this alias said
        /// <c>as COUNT</c> while a second producer emitted <c>AS "OrderCount"</c> for the same column.
        /// <para>
        /// Falls back to the dictionary key when <c>Property</c> is unset. It is declared non-nullable but
        /// assigned by the view builders, and an alias is not worth a NullReferenceException.
        /// </para>
        /// </summary>
        private static string AggregateAlias(Fields.AbstractField field, string fieldsKey)
            => field.Property != null ? field.Property.Name : fieldsKey;

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
