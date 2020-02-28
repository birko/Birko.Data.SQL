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
        private static Dictionary<Type, IEnumerable<Fields.AbstractField>> _fieldsCache = null;

        private static IEnumerable<AbstractField> LoadFields(Type type)
        {
            if (_fieldsCache == null)
            {
                _fieldsCache = new Dictionary<Type, IEnumerable<Fields.AbstractField>>();
            }
            if (!_fieldsCache.ContainsKey(type))
            {
                List<AbstractField> list = new List<AbstractField>();
                GetProperties(type, (field) =>
                {
                    list.AddRange(LoadField(field));
                });
                _fieldsCache.Add(type, list.ToArray());
            }
            return _fieldsCache[type];
        }

        public static IEnumerable<AbstractField> LoadField(PropertyInfo field)
        {
            List<AbstractField> list = new List<AbstractField>();
            Attributes.Field[] fieldAttrs = (Attributes.Field[])field.GetCustomAttributes(typeof(Attributes.Field), true);
            var tableField = Fields.AbstractField.CreateAbstractField(field, fieldAttrs);
            if (tableField != null)
            {
                list.Add(tableField);
            }
            return list.ToArray();
        }

        public static AbstractField GetField<T, P>(Expression<Func<T, P>> expr)
        {
            PropertyInfo propInfo = null;
            if (expr.Body is UnaryExpression expression)
            {
                propInfo = (expression.Operand as MemberExpression).Member as PropertyInfo;
            }
            else if(expr.Body is  MemberExpression memberExpression)
            {
                propInfo = memberExpression.Member as PropertyInfo;
            }
            if (propInfo.ReflectedType == typeof(Models.AbstractLogModel))
            {
                propInfo = typeof(Models.AbstractDatabaseLogModel).GetProperty(propInfo.Name);
            }
            else if (propInfo.ReflectedType == typeof(Models.AbstractModel))
            {
                propInfo = typeof(Models.AbstractDatabaseModel).GetProperty(propInfo.Name);
            }
            var fields = LoadField(propInfo);
            return fields.First();
        }

        public static IEnumerable<AbstractField> GetPrimaryFields(Type type)
        {
            var table = LoadTable(type);
            return table?.GetPrimaryFields() ?? new AbstractField[0];
        }

        public static int Read(IEnumerable<Fields.AbstractField> fields, DbDataReader reader, object data, int index = 0)
        {
            if (fields != null)
            {
                foreach (var tableField in fields)
                {
                    tableField.Read(data, reader, index);
                    index++;
                }
            }
            return index;
        }

        public static Dictionary<string, object> Write(IEnumerable<Fields.AbstractField> fields, object data)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            if (fields != null)
            {
                foreach (var tableField in fields)
                {
                    result.Add(tableField.Name, tableField.Write(data));
                }
            }
            return result;
        }
    }
}
