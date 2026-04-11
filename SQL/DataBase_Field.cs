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

        internal static IEnumerable<AbstractField> LoadFields(Type type)
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
            // Direct cast works when attribute type identity matches (same compilation unit).
            var directAttrs = field.GetCustomAttributes(typeof(Birko.Data.SQL.Attributes.Field), true);
            Birko.Data.SQL.Attributes.Field[] fieldAttrs;

            if (directAttrs.Length > 0)
            {
                fieldAttrs = (Birko.Data.SQL.Attributes.Field[])directAttrs;
            }
            else
            {
                // Cross-assembly shared-project fallback: attribute compiled into a different assembly
                // has a different CLR type identity. Reconstruct equivalent local attributes via reflection.
                var crossAttrs = field.GetCustomAttributes(true)
                    .Where(a => a.GetType().FullName?.StartsWith("Birko.Data.SQL.Attributes.") == true
                             && !(a is Birko.Data.SQL.Attributes.Field))
                    .ToList();

                if (crossAttrs.Count == 0)
                {
                    fieldAttrs = Array.Empty<Birko.Data.SQL.Attributes.Field>();
                }
                else
                {
                    var rebuilt = new List<Birko.Data.SQL.Attributes.Field>();
                    foreach (var attr in crossAttrs)
                    {
                        var typeName = attr.GetType().Name;
                        switch (typeName)
                        {
                            case "PrimaryField":
                                rebuilt.Add(new Birko.Data.SQL.Attributes.PrimaryField());
                                break;
                            case "UniqueField":
                                rebuilt.Add(new Birko.Data.SQL.Attributes.UniqueField());
                                break;
                            case "RequiredField":
                                rebuilt.Add(new Birko.Data.SQL.Attributes.RequiredField());
                                break;
                            case "IncrementField":
                                rebuilt.Add(new Birko.Data.SQL.Attributes.IncrementField());
                                break;
                            case "IgnoreField":
                                rebuilt.Add(new Birko.Data.SQL.Attributes.IgnoreField());
                                break;
                            case "NamedField":
                                var name = attr.GetType().GetProperty("Name")?.GetValue(attr) as string;
                                if (!string.IsNullOrEmpty(name))
                                    rebuilt.Add(new Birko.Data.SQL.Attributes.NamedField(name));
                                break;
                            case "PrecisionField":
                                var prec = attr.GetType().GetProperty("Precision")?.GetValue(attr);
                                rebuilt.Add(new Birko.Data.SQL.Attributes.PrecisionField(prec is int p ? p : 0));
                                break;
                            case "ScaleField":
                                var sc = attr.GetType().GetProperty("Scale")?.GetValue(attr);
                                rebuilt.Add(new Birko.Data.SQL.Attributes.ScaleField(sc is int s ? s : 0));
                                break;
                            case "MaxLengthField":
                                var ml = attr.GetType().GetProperty("MaxLength")?.GetValue(attr);
                                rebuilt.Add(new Birko.Data.SQL.Attributes.MaxLengthField(ml is int m ? m : 0));
                                break;
                            case "IndexedField":
                                var idxName = attr.GetType().GetProperty("Name")?.GetValue(attr) as string;
                                var idxOrder = attr.GetType().GetProperty("Order")?.GetValue(attr) is int io ? io : 0;
                                var idxDesc = attr.GetType().GetProperty("IsDescending")?.GetValue(attr) is bool id && id;
                                if (!string.IsNullOrEmpty(idxName))
                                    rebuilt.Add(new Birko.Data.SQL.Attributes.IndexedField(idxName!, idxOrder, idxDesc));
                                break;
                            default:
                                // Unknown attribute subtype — skip (property still gets mapped without attributes)
                                break;
                        }
                    }
                    fieldAttrs = rebuilt.ToArray();
                }
            }

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

        /// <summary>
        /// Resolves an AbstractField from a non-generic LambdaExpression.
        /// Used by bulk store PropertyUpdate where generic type args are erased.
        /// </summary>
        public static AbstractField GetFieldFromLambda(LambdaExpression expr)
        {
            PropertyInfo? propInfo = null;
            if (expr.Body is UnaryExpression expression)
            {
                propInfo = (expression.Operand as MemberExpression)?.Member as PropertyInfo;
            }
            else if (expr.Body is MemberExpression memberExpression)
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
            var primaries = table?.GetPrimaryFields()?.ToList();

            // Fallback: if no [PrimaryField] attribute is declared, use the Guid property
            // (all AbstractModel descendants have Guid). This prevents UPDATE/DELETE
            // without a WHERE clause for platform-independent models.
            if ((primaries == null || primaries.Count == 0) && table != null)
            {
                var guidField = table.GetFieldByPropertyName("Guid");
                if (guidField != null)
                    return new[] { guidField };
            }

            return (IEnumerable<AbstractField>?)primaries ?? Array.Empty<AbstractField>();
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
