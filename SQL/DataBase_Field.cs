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
        internal static readonly ConcurrentDictionary<Type, IEnumerable<Fields.AbstractField>> _fieldsCache = new();

        private static IEnumerable<AbstractField> LoadFields(Type type)
        {
            return _fieldsCache.GetOrAdd(type, t =>
            {
                List<AbstractField> list = new List<AbstractField>();
                GetProperties(t, (field) =>
                {
                    list.AddRange(LoadField(field));
                });
                return list.ToArray();
            });
        }

        public static IEnumerable<AbstractField> LoadField(PropertyInfo field)
        {
            Birko.Data.SQL.Attributes.Field[] fieldAttrs = (Birko.Data.SQL.Attributes.Field[])field.GetCustomAttributes(typeof(Birko.Data.SQL.Attributes.Field), true);
            var tableField = Fields.AbstractField.CreateAbstractField(field, fieldAttrs);
            return tableField != null ? new[] { tableField } : Array.Empty<AbstractField>();
        }

        public static AbstractField GetField<T, P>(Expression<Func<T, P>> expr)
        {
            PropertyInfo? propInfo = null;
            if (expr.Body is UnaryExpression expression)
            {
                propInfo = (expression.Operand as MemberExpression)?.Member as PropertyInfo;
            }
            else if(expr.Body is MemberExpression memberExpression)
            {
                propInfo = memberExpression.Member as PropertyInfo;
            }
            if (propInfo == null)
            {
                throw new ArgumentException($"Unable to resolve property from expression: {expr}", nameof(expr));
            }
            if (propInfo.ReflectedType == typeof(Models.AbstractLogModel))
            {
                propInfo = typeof(Models.AbstractDatabaseLogModel).GetProperty(propInfo.Name);
            }
            else if (propInfo.ReflectedType == typeof(Models.AbstractModel))
            {
                propInfo = typeof(Models.AbstractDatabaseModel).GetProperty(propInfo.Name);
            }
            var fields = LoadField(propInfo!);
            return fields.First();
        }

        public static IEnumerable<AbstractField> GetPrimaryFields(Type type)
        {
            var table = LoadTable(type);
            return table?.GetPrimaryFields() ?? Array.Empty<AbstractField>();
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
                    result.Add(tableField.Name, tableField.Write(data)!);
                }
            }
            return result;
        }
    }
}
