using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractConnector
    {

        public IEnumerable<object> Select<T, P>(Type type, LambdaExpression? expr = null, IDictionary<Expression<Func<T, P>>, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            foreach (var o in Select(type, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields?.ToDictionary(x => DataBase.GetField(x.Key).GetSelectName(false), x => x.Value), limit, offset))
            {
                yield return o;
            }
        }

        public IEnumerable<object> Select(Type type, LambdaExpression? expr = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            foreach (var o in Select(type, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields, limit, offset))
            {
                yield return o;
            }
        }

        public IEnumerable<object> Select<T, P>(Type[] types, LambdaExpression? expr = null, IDictionary<Expression<Func<T, P>>, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            foreach (var o in Select(types, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields?.ToDictionary(x => DataBase.GetField(x.Key).GetSelectName(true), x => x.Value), limit, offset))
            {
                yield return o;
            }
        }

        public IEnumerable<object> Select(Type[] types, LambdaExpression expr, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            foreach (var item in Select(types, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields, limit, offset))
            {
                yield return item;
            }
        }

        public IEnumerable<object> Select(Type type, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            foreach (var items in Select(new[] { type }, conditions, orderFields, limit, offset))
            {
                yield return items.FirstOrDefault()!;
            }
        }

        public IEnumerable<IEnumerable<object>> Select(IEnumerable<Type> types, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            if (types == null)
            {
                yield break;
            }

            // Materialize once; pre-load factories and fields outside the per-row loop.
            var typeArray = types.ToArray();
            var factories = typeArray.Select(DataBase.GetOrCreateInstanceFactory).ToArray();
            var fieldSets = typeArray.Select(DataBase.LoadFields).ToArray();

            foreach (var set in Select(typeArray.Select(x => DataBase.LoadTable(x)), (_, reader) =>
            {
                var index = 0;
                List<object> objects = new();
                for (int ti = 0; ti < typeArray.Length; ti++)
                {
                    var data = factories[ti]();
                    index = DataBase.Read(fieldSets[ti], reader, data, index);
                    objects.Add(data);
                }
                return objects.AsEnumerable();
            }, conditions, orderFields, limit, offset))
            {
                yield return set;
            }
        }

        public IEnumerable<IEnumerable<object>> Select(IEnumerable<Tables.Table> tables, Func<IDictionary<int, string>, DbDataReader, IEnumerable<object>>? transformFunction = null, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            if (tables == null)
            {
                yield break;
            }
            Dictionary<int, string> fields = new Dictionary<int, string>();
            int i = 0;
            foreach (var table in tables.Where(x => x != null))
            {
                var tablefields = table.GetSelectFields(true);
                foreach (var kvp in tablefields)
                {
                    fields.Add(i, kvp.Value);
                    i++;
                }
            }

            foreach (var item in Select(tables.Where(x => x != null).Select(x => x.Name), fields, transformFunction != null ? (reader) => transformFunction(fields, reader) : null, conditions, orderFields, limit, offset))
            {
                yield return item;
            }

        }

        public IEnumerable<IEnumerable<object>> Select(
            IEnumerable<string> tableNames,
            IDictionary<int, string> fields,
            Func<DbDataReader, IEnumerable<object>>? transformFunction = null,
            IEnumerable<Conditions.Condition>? conditions = null, 
            IDictionary<string, bool>? orderFields = null, 
            int? limit = null, 
            int? offset = null
        )
        {
            if (!(tableNames?.Any(x => !string.IsNullOrEmpty(x)) ?? false))
            {
                yield break;
            }
            foreach(var item in RunReaderCommand((command) => {
                command = CreateSelectCommand(command, tableNames.Where(x => !string.IsNullOrEmpty(x)).Distinct(), fields, conditions, orderFields, limit, offset);
            }, transformFunction!))
            { 
                yield return item; 
            }
        }
    }
}
