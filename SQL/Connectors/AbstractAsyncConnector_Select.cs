using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractAsyncConnector
    {
        public async IAsyncEnumerable<object> SelectAsync<T, P>(Type type, LambdaExpression? expr = null, IDictionary<Expression<Func<T, P>>, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            await foreach (var o in SelectAsync(type, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields?.ToDictionary(x => DataBase.GetField(x.Key).GetSelectName(false), x => x.Value), limit, offset))
            {
                yield return o;
            }
        }

        public async IAsyncEnumerable<object> SelectAsync(Type type, LambdaExpression? expr = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            await foreach (var o in SelectAsync(type, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields, limit, offset))
            {
                yield return o;
            }
        }

        public async IAsyncEnumerable<object> SelectAsync<T, P>(Type[] types, LambdaExpression? expr = null, IDictionary<Expression<Func<T, P>>, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            await foreach (var o in SelectAsync(types, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields?.ToDictionary(x => DataBase.GetField(x.Key).GetSelectName(true), x => x.Value), limit, offset))
            {
                yield return o;
            }
        }

        public async IAsyncEnumerable<object> SelectAsync(Type[] types, LambdaExpression expr, IDictionary<string, bool> orderFields = null, int? limit = null, int? offset = null)
        {
            await foreach (var item in SelectAsync(types, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields, limit, offset))
            {
                yield return item;
            }
        }

        public async IAsyncEnumerable<object> SelectAsync(Type type, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<string, bool> orderFields = null, int? limit = null, int? offset = null)
        {
            await foreach (var items in SelectAsync(new[] { type }, conditions, orderFields, limit, offset))
            {
                yield return items.FirstOrDefault();
            }
        }

        public async IAsyncEnumerable<IEnumerable<object>> SelectAsync(IEnumerable<Type> types, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            if (types == null)
            {
                yield break;
            }

            await foreach (var set in SelectAsync(types.Select(x => DataBase.LoadTable(x)), async (fields, reader) =>
            {
                var index = 0;
                List<object> objects = new();
                foreach (var type in types)
                {
                    var data = Activator.CreateInstance(type, Array.Empty<object>());
                    index = DataBase.Read(reader, data, index);
                    objects.Add(data);
                }
                return objects.AsEnumerable();
            }, conditions, orderFields, limit, offset))
            {
                yield return set;
            }
        }

        public async IAsyncEnumerable<IEnumerable<object>> SelectAsync(IEnumerable<Tables.Table> tables, Func<IDictionary<int, string>, DbDataReader, Task<IEnumerable<object>>>? transformFunction = null, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
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

            await foreach (var item in SelectAsync(tables.Where(x => x != null).Select(x => x.Name), fields, transformFunction != null ? async (reader) => await transformFunction(fields, reader) : null, conditions, orderFields, limit, offset))
            {
                yield return item;
            }
        }

        public async IAsyncEnumerable<IEnumerable<object>> SelectAsync(
            IEnumerable<string> tableNames,
            IDictionary<int, string> fields,
            Func<DbDataReader, Task<IEnumerable<object>>>? transformFunction = null,
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
            await foreach (var item in RunReaderCommandAsync(async (command) =>
            {
                command = CreateSelectCommand(command, tableNames.Where(x => !string.IsNullOrEmpty(x)).Distinct(), fields, conditions, orderFields, limit, offset);
                await Task.CompletedTask;
            }, transformFunction))
            {
                yield return item;
            }
        }
    }
}
