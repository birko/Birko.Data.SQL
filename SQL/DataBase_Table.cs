using Birko.Data.SQL.Fields;
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
                IEnumerable<object> attrs = t.GetCustomAttributes(typeof(Birko.Data.Attributes.Table), true)
                    .Concat(t.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.Schema.TableAttribute), true));
                if (attrs != null)
                {
                    foreach (Attribute attr in attrs)
                    {
                        string tableName = null;
                        if (attr is Birko.Data.Attributes.Table birkoTable)
                        {
                            tableName = birkoTable.Name;
                        }
                        else if (attr is System.ComponentModel.DataAnnotations.Schema.TableAttribute dataTable)
                        {
                            tableName = dataTable.Name;
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
                                return table;
                            }
                        }
                    }
                    return null;
                }
                else
                {
                    throw new Exceptions.TableAttributeException("No table attributes in type");
                }
            });
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
