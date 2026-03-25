using Birko.Data.SQL.Fields;
using Birko.Data.SQL.Tables;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Birko.Data.SQL
{
    public static partial class DataBase
    {
        private static readonly ConcurrentDictionary<Type, Tables.Table> _tableCache = new();
        private static readonly ConcurrentDictionary<Type, string> _tableNameOverrides = new();

        /// <summary>
        /// Register a table name for a model type (used by fluent mapping systems).
        /// This takes precedence over attribute-based discovery when no [Table] attribute is present.
        /// </summary>
        public static void RegisterTableName(Type modelType, string tableName)
        {
            _tableNameOverrides[modelType] = tableName;
            // Invalidate cache so LoadTable picks up the override
            _tableCache.TryRemove(modelType, out _);
        }

        /// <summary>
        /// Register table names for all model mappings in the given registry.
        /// </summary>
        public static void RegisterTableNames(IEnumerable<KeyValuePair<Type, string>> mappings)
        {
            foreach (var (type, name) in mappings)
                RegisterTableName(type, name);
        }

        public static IEnumerable<Tables.Table> LoadTables(IEnumerable<Type> types)
        {
            if (types != null && types.Any())
            {
                List<Tables.Table> tables = new List<Tables.Table>();
                foreach (Type type in types)
                {
                    var table = LoadTable(type);
                    if (table != null && table.Fields != null && table.Fields.Any())
                    {
                        tables.Add(table);
                    }
                }
                return tables.ToArray();
            }
            else
            {
                throw new Exceptions.TableAttributeException("Types enumerable is empty or null");
            }
        }

        public static Tables.Table LoadTable(Type type)
        {
            return _tableCache.GetOrAdd(type, t =>
            {
                // Check both Birko and DataAnnotations table attributes.
                // Use name-based matching for Birko.Data.SQL.Attributes.Table to handle
                // shared-project type identity issues (same attribute compiled into multiple assemblies).
                IEnumerable<object> attrs = t.GetCustomAttributes(true)
                    .Where(a => a is Birko.Data.SQL.Attributes.Table
                             || a is System.ComponentModel.DataAnnotations.Schema.TableAttribute
                             || a.GetType().FullName == "Birko.Data.SQL.Attributes.Table");
                if (attrs != null)
                {
                    foreach (Attribute attr in attrs)
                    {
                        string? tableName = null;
                        if (attr is Birko.Data.SQL.Attributes.Table birkoTable)
                        {
                            tableName = birkoTable.Name;
                        }
                        else if (attr is System.ComponentModel.DataAnnotations.Schema.TableAttribute dataTable)
                        {
                            tableName = dataTable.Name;
                        }
                        else if (attr.GetType().FullName == "Birko.Data.SQL.Attributes.Table")
                        {
                            // Cross-assembly shared-project attribute — read Name via reflection
                            tableName = attr.GetType().GetProperty("Name")?.GetValue(attr) as string;
                        }
                        if (!string.IsNullOrEmpty(tableName))
                        {
                            Tables.Table table = new Tables.Table()
                            {
                                Name = tableName,
                                Type = t,
                                Fields = LoadFields(t).ToDictionary(x => x.Name),
                            };
                            if (table.Fields != null && table.Fields.Any())
                            {
                                foreach (var field in table.Fields)
                                {
                                    field.Value.Table = table;
                                }
                                table.Indexes = LoadIndexes(t, table.Fields);
                                return table;
                            }
                        }
                    }
                }

                // Fallback: check fluent mapping overrides (registered via RegisterTableName)
                if (_tableNameOverrides.TryGetValue(t, out var overrideName))
                {
                    var table = new Tables.Table()
                    {
                        Name = overrideName,
                        Type = t,
                        Fields = LoadFields(t).ToDictionary(x => x.Name),
                    };
                    if (table.Fields != null && table.Fields.Any())
                    {
                        foreach (var field in table.Fields)
                            field.Value.Table = table;
                        table.Indexes = LoadIndexes(t, table.Fields);
                        return table;
                    }
                }

                return null!;
            });
        }

        public static Dictionary<string, IndexDefinition> LoadIndexes(Type type, Dictionary<string, AbstractField> fields)
        {
            var indexes = new Dictionary<string, IndexDefinition>();

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                // Direct cast
                var directAttrs = prop.GetCustomAttributes(typeof(Attributes.IndexedField), true)
                    .OfType<Attributes.IndexedField>();

                // Cross-assembly shared-project fallback
                var crossAttrs = prop.GetCustomAttributes(true)
                    .Where(a => a.GetType().FullName == "Birko.Data.SQL.Attributes.IndexedField"
                             && !(a is Attributes.IndexedField));

                var allAttrs = new List<(string Name, int Order, bool IsDescending)>();

                foreach (var attr in directAttrs)
                {
                    allAttrs.Add((attr.Name, attr.Order, attr.IsDescending));
                }

                foreach (var attr in crossAttrs)
                {
                    var attrType = attr.GetType();
                    var name = attrType.GetProperty("Name")?.GetValue(attr) as string;
                    var order = attrType.GetProperty("Order")?.GetValue(attr) is int o ? o : 0;
                    var isDesc = attrType.GetProperty("IsDescending")?.GetValue(attr) is bool d && d;
                    if (!string.IsNullOrEmpty(name))
                    {
                        allAttrs.Add((name!, order, isDesc));
                    }
                }

                foreach (var (name, order, isDescending) in allAttrs)
                {
                    if (!indexes.TryGetValue(name, out var idx))
                    {
                        idx = new IndexDefinition { Name = name };
                        indexes[name] = idx;
                    }

                    // Resolve actual column name from field metadata
                    var field = fields.Values.FirstOrDefault(f => f.Property?.Name == prop.Name);
                    var columnName = field?.Name ?? prop.Name;

                    idx.Columns.Add(new IndexColumn
                    {
                        ColumnName = columnName,
                        Order = order,
                        IsDescending = isDescending,
                    });
                }
            }

            // Sort columns by Order within each index
            foreach (var idx in indexes.Values)
            {
                idx.Columns.Sort((a, b) => a.Order.CompareTo(b.Order));
            }

            return indexes;
        }

        public static int Read(DbDataReader reader, object data, int index = 0)
        {
            return Read(LoadFields(data.GetType()), reader, data, index);
        }

        public static Dictionary<string, object> Write(object data)
        {
            return Write(LoadFields(data.GetType()), data);
        }
    }
}
