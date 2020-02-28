using Birko.Data.SQL.Fields;
using System;
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
        private static Dictionary<Type, Tables.Table> _tableCache = null;

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
                throw new Exceptions.TableAttributeException("Types enumerable is empty ot null");
            }
        }

        public static Tables.Table LoadTable(Type type)
        {
            if (_tableCache == null)
            {
                _tableCache = new Dictionary<Type, Tables.Table>();
            }
            if (!_tableCache.ContainsKey(type))
            {
                object[] attrs = type.GetCustomAttributes(typeof(Attributes.Table), true);
                if (attrs != null)
                {
                    foreach (Attributes.Table attr in attrs)
                    {
                        Tables.Table table = new Tables.Table()
                        {
                            Name = attr.Name,
                            Type = type,
                            Fields = LoadFields(type).ToDictionary(x => x.Name),
                        };
                        if (table.Fields != null && table.Fields.Any())
                        {
                            foreach (var field in table.Fields)
                            {
                                field.Value.Table = table;
                            }
                            _tableCache.Add(type, table);
                            return table;
                        }
                    }
                    return null;
                }
                else
                {
                    throw new Exceptions.TableAttributeException("No table attributes in type");
                }
            }
            return _tableCache[type];
        }



        public static int Read(DbDataReader reader, object data, int index = 0)
        {
            var type = data.GetType();
            List<Fields.AbstractField> tableFields = new List<Fields.AbstractField>();
            GetProperties(type, (field) =>
            {
                Attributes.Field[] fieldAttrs = (Attributes.Field[])field.GetCustomAttributes(typeof(Attributes.Field), true);
                if (fieldAttrs != null)
                {
                    // from cache
                    if (_fieldsCache.ContainsKey(type) && _fieldsCache[type].Any(x => x.Property.Name == field.Name))
                    {
                        tableFields.Add(_fieldsCache[type].FirstOrDefault(x => x.Property.Name == field.Name));
                    }
                    else
                    {
                        tableFields.Add(Fields.AbstractField.CreateAbstractField(field, fieldAttrs));
                    }
                }
            });
            return Read(tableFields, reader, data, index);
        }

        public static Dictionary<string, object> Write(object data)
        {
            var type = data.GetType();
            List<Fields.AbstractField> tableFields = new List<Fields.AbstractField>();
            GetProperties(type, (field) =>
            {
                Attributes.Field[] fieldAttrs = (Attributes.Field[])field.GetCustomAttributes(typeof(Attributes.Field), true);
                tableFields.Add(Fields.AbstractField.CreateAbstractField(field, fieldAttrs));
            });
            return Write(tableFields, data);
        }
    }
}
