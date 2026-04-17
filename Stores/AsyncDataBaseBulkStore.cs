using Birko.Data.SQL.Connectors;
using Birko.Data.Stores;
using Birko.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Birko.Data.SQL.Stores
{
    /// <summary>
    /// Async bulk database store that provides optimized bulk operations.
    /// Extends <see cref="AsyncDataBaseStore{DB, T}"/> with async bulk CRUD capabilities.
    /// Uses Template Method pattern — concrete stores override *CoreAsync methods.
    /// </summary>
    /// <typeparam name="DB">The type of database connector, must inherit from <see cref="AbstractConnector"/>.</typeparam>
    /// <typeparam name="T">The type of entity, must inherit from <see cref="Models.AbstractModel"/>.</typeparam>
    public class AsyncDataBaseBulkStore<DB, T> : AsyncDataBaseStore<DB, T>, IAsyncBulkStore<T>, IAsyncAggregatableStore<T>
        where T : Models.AbstractModel
        where DB : AbstractConnector
    {
        /// <summary>
        /// Initializes a new instance of the AsyncDataBaseBulkStore class.
        /// </summary>
        public AsyncDataBaseBulkStore()
            : base()
        {
        }

        #region Bulk Read Operations

        /// <inheritdoc />
        public async Task<IEnumerable<T>> ReadAsync(
            Expression<Func<T, bool>>? filter = null,
            OrderBy<T>? orderBy = null,
            int? limit = null,
            int? offset = null,
            CancellationToken ct = default)
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            return await ReadCoreAsync(filter, orderBy, limit, offset, ct);
        }

        /// <summary>
        /// Core bulk read implementation. Override in concrete stores for provider-specific behavior.
        /// </summary>
        protected virtual async Task<IEnumerable<T>> ReadCoreAsync(
            Expression<Func<T, bool>>? filter = null,
            OrderBy<T>? orderBy = null,
            int? limit = null,
            int? offset = null,
            CancellationToken ct = default)
        {
            if (Connector == null) return Enumerable.Empty<T>();

            if (AsyncConnector != null)
            {
                var items = new List<T>();
                await foreach (var item in AsyncConnector.SelectAsync(typeof(T), filter as LambdaExpression, orderBy?.ToDictionary(), limit, offset))
                {
                    if (item is T typed) items.Add(typed);
                }
                return items;
            }

            var results = await Task.Run(() => Connector!.Select(typeof(T), filter as LambdaExpression, orderBy?.ToDictionary(), limit, offset), ct);
            if (results == null) return Enumerable.Empty<T>();

            return results.OfType<T>();
        }

        /// <inheritdoc />
        public virtual async Task<IEnumerable<T>> ReadAsync(CancellationToken ct = default)
        {
            return await ReadAsync(null, null, null, null, ct);
        }

        #endregion

        #region Bulk Write Operations

        /// <inheritdoc />
        public async Task CreateAsync(
            IEnumerable<T> data,
            StoreDataDelegate<T>? storeDelegate = null,
            CancellationToken ct = default)
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            await CreateCoreAsync(data, storeDelegate, ct);
        }

        /// <summary>
        /// Core bulk create implementation. Override in concrete stores for provider-specific behavior.
        /// </summary>
        protected virtual async Task CreateCoreAsync(
            IEnumerable<T> data,
            StoreDataDelegate<T>? storeDelegate = null,
            CancellationToken ct = default)
        {
            foreach (var item in data)
            {
                await CreateAsync(item, storeDelegate, ct);
            }
        }

        /// <inheritdoc />
        public async Task UpdateAsync(
            IEnumerable<T> data,
            StoreDataDelegate<T>? storeDelegate = null,
            CancellationToken ct = default)
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            await UpdateCoreAsync(data, storeDelegate, ct);
        }

        /// <summary>
        /// Core bulk update implementation. Override in concrete stores for provider-specific behavior.
        /// </summary>
        protected virtual async Task UpdateCoreAsync(
            IEnumerable<T> data,
            StoreDataDelegate<T>? storeDelegate = null,
            CancellationToken ct = default)
        {
            foreach (var item in data)
            {
                await UpdateAsync(item, storeDelegate, ct);
            }
        }

        /// <inheritdoc />
        public virtual async Task UpdateAsync(
            Expression<Func<T, bool>> filter,
            Action<T> updateAction,
            CancellationToken ct = default)
        {
            var items = (await ReadAsync(filter, null, null, null, ct)).ToList();
            foreach (var item in items)
            {
                updateAction(item);
                await UpdateAsync(item, ct: ct);
            }
        }

        /// <inheritdoc />
        public virtual async Task UpdateAsync(
            Expression<Func<T, bool>> filter,
            PropertyUpdate<T> updates,
            CancellationToken ct = default)
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            if (Connector == null || updates.Assignments.Count == 0) return;

            var table = SQL.DataBase.LoadTable(typeof(T));
            var fields = new Dictionary<int, string>();
            var values = new Dictionary<string, object>();
            int i = 0;
            foreach (var (property, value) in updates.Assignments)
            {
                var field = SQL.DataBase.GetFieldFromLambda(property);
                fields.Add(i, field.Name);
                values.Add(field.Name, value ?? DBNull.Value);
                i++;
            }
            var conditions = SQL.DataBase.ParseConditionExpression(filter as LambdaExpression);
            if (AsyncConnector != null)
                await AsyncConnector.UpdateAsync(table.Name, fields, values, conditions, false, ct);
            else
                await Task.Run(() => Connector!.Update(table.Name, fields, values, conditions), ct);
        }

        /// <inheritdoc />
        public async Task DeleteAsync(
            IEnumerable<T> data,
            CancellationToken ct = default)
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            await DeleteCoreAsync(data, ct);
        }

        /// <summary>
        /// Core bulk delete implementation. Override in concrete stores for provider-specific behavior.
        /// </summary>
        protected virtual async Task DeleteCoreAsync(
            IEnumerable<T> data,
            CancellationToken ct = default)
        {
            foreach (var item in data)
            {
                await DeleteAsync(item, ct);
            }
        }

        /// <inheritdoc />
        public virtual async Task DeleteAsync(
            Expression<Func<T, bool>> filter,
            CancellationToken ct = default)
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            if (Connector == null) return;
            if (AsyncConnector != null)
                await AsyncConnector.DeleteAsync(typeof(T), filter as LambdaExpression, ct);
            else
                await Task.Run(() => Connector!.Delete(typeof(T), filter as LambdaExpression), ct);
        }

        #endregion

        #region Aggregation

        /// <summary>
        /// Executes an aggregation query using SQL GROUP BY.
        /// </summary>
        public async Task<IReadOnlyList<AggregateResult>> AggregateAsync(
            AggregateQuery<T> query,
            CancellationToken ct = default)
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            if (AsyncConnector == null || Connector == null)
                return Array.Empty<AggregateResult>();

            var results = new List<AggregateResult>();
            await foreach (var row in AsyncConnector.SelectAggregateAsync(typeof(T), query, ct))
            {
                results.Add(row);
            }
            return results.AsReadOnly();
        }

        #endregion
    }
}
