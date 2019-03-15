using Birko.Data.SQL.Field;
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
        private static Dictionary<Type, Table.Table> _tableCache = null;

        public static IEnumerable<Table.Table> LoadTables(IEnumerable<Type> types)
        {
            if (types != null && types.Any())
            {
                List<Table.Table> tables = new List<Table.Table>();
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

        public static Table.Table LoadTable(Type type)
        {
            if (_tableCache == null)
            {
                _tableCache = new Dictionary<Type, Table.Table>();
            }
            if (!_tableCache.ContainsKey(type))
            {
                object[] attrs = type.GetCustomAttributes(typeof(Attribute.Table), true);
                if (attrs != null)
                {
                    foreach (Attribute.Table attr in attrs)
                    {
                        Table.Table table = new Table.Table()
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
            List<Field.AbstractField> tableFields = new List<Field.AbstractField>();
            foreach (var field in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                object[] fieldAttrs = field.GetCustomAttributes(typeof(Attribute.Field), true);
                if (fieldAttrs != null)
                {
                    foreach (Attribute.Field fieldAttr in fieldAttrs)
                    {
                        // from cache
                        if (_fieldsCache.ContainsKey(type) && _fieldsCache[type].Any(x => x.Property.Name == field.Name))
                        {
                            tableFields.Add(_fieldsCache[type].FirstOrDefault(x => x.Property.Name == field.Name));
                        }
                        else
                        {
                            tableFields.Add(Field.AbstractField.CreateAbstractField(field, fieldAttr));
                        }
                    }
                }
            }
            return Read(tableFields, reader, data, index);
        }

        public static Dictionary<string, object> Write(object data)
        {
            var type = data.GetType();
            List<Field.AbstractField> tableFields = new List<Field.AbstractField>();
            foreach (var field in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                object[] fieldAttrs = field.GetCustomAttributes(typeof(Attribute.Field), true);
                if (fieldAttrs != null)
                {
                    foreach (Attribute.Field fieldAttr in fieldAttrs)
                    {
                        tableFields.Add(Field.AbstractField.CreateAbstractField(field, fieldAttr));
                    }
                }
            }
            return Write(tableFields, data);
        }
    }
}
