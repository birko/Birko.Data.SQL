using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractAsyncConnector
    {
        // TASK-110: property names in, resolution once in the Tables.Table funnel below — see the sync twin.
        public async IAsyncEnumerable<object> SelectAsync<T, P>(Type type, LambdaExpression? expr = null, IDictionary<Expression<Func<T, P>>, bool>? orderFields = null, int? limit = null, int? offset = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var o in SelectAsync(type, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields?.ToDictionary(x => DataBase.GetField(x.Key).Property.Name, x => x.Value), limit, offset, ct))
            {
                yield return o;
            }
        }

        public async IAsyncEnumerable<object> SelectAsync(Type type, LambdaExpression? expr = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var o in SelectAsync(type, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields, limit, offset, ct))
            {
                yield return o;
            }
        }

        public async IAsyncEnumerable<object> SelectAsync<T, P>(Type[] types, LambdaExpression? expr = null, IDictionary<Expression<Func<T, P>>, bool>? orderFields = null, int? limit = null, int? offset = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var o in SelectAsync(types, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields?.ToDictionary(x => DataBase.GetField(x.Key).Property.Name, x => x.Value), limit, offset, ct))
            {
                yield return o;
            }
        }

        public async IAsyncEnumerable<object> SelectAsync(Type[] types, LambdaExpression expr, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var item in SelectAsync(types, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields, limit, offset, ct))
            {
                yield return item;
            }
        }

        public async IAsyncEnumerable<object> SelectAsync(Type type, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var items in SelectAsync(new[] { type }, conditions, orderFields, limit, offset, ct))
            {
                yield return items.FirstOrDefault()!;
            }
        }

        public async IAsyncEnumerable<IEnumerable<object>> SelectAsync(IEnumerable<Type> types, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (types == null)
            {
                yield break;
            }

            // Materialize once; pre-load factories and fields outside the per-row loop.
            var typeArray = types.ToArray();
            var factories = typeArray.Select(DataBase.GetOrCreateInstanceFactory).ToArray();
            var fieldSets = typeArray.Select(DataBase.LoadFields).ToArray();

            await foreach (var set in SelectAsync(typeArray.Select(x => DataBase.LoadTable(x)), async (_, reader) =>
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
            }, conditions, orderFields, limit, offset, ct))
            {
                yield return set;
            }
        }

        public async IAsyncEnumerable<IEnumerable<object>> SelectAsync(IEnumerable<Tables.Table> tables, Func<IDictionary<int, string>, DbDataReader, Task<IEnumerable<object>>>? transformFunction = null, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null, [EnumeratorCancellation] CancellationToken ct = default)
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

            // TASK-110 (SH-H003 / SH-M022): resolve here, the last layer holding column metadata — see the
            // sync twin in AbstractConnector_Select.cs for why this is the single resolution point.
            orderFields = DataBase.ResolveOrderFields(tables, orderFields);

            await foreach (var item in SelectAsync(tables.Where(x => x != null).Select(x => x.Name), fields, transformFunction != null ? async (reader) => await transformFunction(fields, reader) : null, conditions, orderFields, limit, offset, ct))
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
            int? offset = null,
            [EnumeratorCancellation] CancellationToken ct = default
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
            }, transformFunction!, ct))
            {
                yield return item;
            }
        }
    }
}
