using Birko.Data.SQL.Connectors;
using Birko.Data.Stores;
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
    /// </summary>
    /// <typeparam name="DB">The type of database connector, must inherit from <see cref="AbstractConnector"/>.</typeparam>
    /// <typeparam name="T">The type of entity, must inherit from <see cref="Models.AbstractModel"/>.</typeparam>
    public class AsyncDataBaseBulkStore<DB, T> : AsyncDataBaseStore<DB, T>, IAsyncBulkStore<T>
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
        public virtual async Task<IEnumerable<T>> ReadAsync(
            Expression<Func<T, bool>>? filter = null,
            OrderBy<T>? orderBy = null,
            int? limit = null,
            int? offset = null,
            CancellationToken ct = default)
        {
            if (Connector == null) return Enumerable.Empty<T>();

            var results = await Task.Run(() => Connector.Select(typeof(T), filter as LambdaExpression, orderBy?.ToDictionary(), limit, offset), ct);
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
        public virtual async Task CreateAsync(
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
        public virtual async Task UpdateAsync(
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
        public virtual async Task DeleteAsync(
            IEnumerable<T> data,
            CancellationToken ct = default)
        {
            foreach (var item in data)
            {
                await DeleteAsync(item, ct);
            }
        }

        #endregion
    }
}
