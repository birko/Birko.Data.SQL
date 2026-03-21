using Birko.Caching;
using Birko.Data.SQL.Caching;
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
    /// Async bulk database store with transparent query caching.
    /// Caches ReadAsync results and automatically invalidates on writes.
    /// </summary>
    /// <typeparam name="DB">The type of database connector, must inherit from <see cref="AbstractConnector"/>.</typeparam>
    /// <typeparam name="T">The type of entity, must inherit from <see cref="Models.AbstractModel"/>.</typeparam>
    public class CachedAsyncDataBaseBulkStore<DB, T> : AsyncDataBaseBulkStore<DB, T>
        where T : Models.AbstractModel
        where DB : AbstractConnector
    {
        private readonly ICache _cache;
        private readonly SqlCacheOptions _options;
        private readonly string _tableName;

        /// <summary>
        /// Initializes a new instance of the CachedAsyncDataBaseBulkStore class.
        /// </summary>
        /// <param name="cache">The cache implementation to use.</param>
        /// <param name="options">Optional cache configuration. Uses defaults if null.</param>
        public CachedAsyncDataBaseBulkStore(ICache cache, SqlCacheOptions? options = null)
            : base()
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _options = options ?? new SqlCacheOptions();
            _tableName = ResolveTableName();
        }

        #region Cached Read Operations

        /// <inheritdoc />
        public override async Task<T?> ReadAsync(Expression<Func<T, bool>>? filter = null, CancellationToken ct = default)
        {
            if (!_options.Enabled)
            {
                return await base.ReadAsync(filter, ct);
            }

            var filterString = filter?.ToString();
            var key = SqlCacheKeyBuilder.BuildKey(_tableName, filterString, null, 1, null);

            var cached = await _cache.GetAsync<T>(key, ct);
            if (cached.HasValue)
            {
                return cached.Value;
            }

            var result = await base.ReadAsync(filter, ct);

            await _cache.SetAsync(key, result, CreateEntryOptions(), ct);

            return result;
        }

        /// <inheritdoc />
        public override async Task<IEnumerable<T>> ReadAsync(
            Expression<Func<T, bool>>? filter = null,
            OrderBy<T>? orderBy = null,
            int? limit = null,
            int? offset = null,
            CancellationToken ct = default)
        {
            if (!_options.Enabled)
            {
                return await base.ReadAsync(filter, orderBy, limit, offset, ct);
            }

            var filterString = filter?.ToString();
            var orderString = orderBy?.ToDictionary() is { } dict
                ? string.Join(",", dict.Select(kvp => $"{kvp.Key}:{kvp.Value}"))
                : null;
            var key = SqlCacheKeyBuilder.BuildKey(_tableName, filterString, orderString, limit, offset);

            var cached = await _cache.GetAsync<List<T>>(key, ct);
            if (cached.HasValue)
            {
                return cached.Value ?? Enumerable.Empty<T>();
            }

            var result = (await base.ReadAsync(filter, orderBy, limit, offset, ct)).ToList();

            await _cache.SetAsync(key, result, CreateEntryOptions(), ct);

            return result;
        }

        /// <inheritdoc />
        public override async Task<IEnumerable<T>> ReadAsync(CancellationToken ct = default)
        {
            return await ReadAsync(null, null, null, null, ct);
        }

        #endregion

        #region Write Operations with Cache Invalidation

        /// <inheritdoc />
        public override async Task<Guid> CreateAsync(T data, StoreDataDelegate<T>? processDelegate = null, CancellationToken ct = default)
        {
            var result = await base.CreateAsync(data, processDelegate, ct);
            await InvalidateCacheAsync(ct);
            return result;
        }

        /// <inheritdoc />
        public override async Task UpdateAsync(T data, StoreDataDelegate<T>? processDelegate = null, CancellationToken ct = default)
        {
            await base.UpdateAsync(data, processDelegate, ct);
            await InvalidateCacheAsync(ct);
        }

        /// <inheritdoc />
        public override async Task DeleteAsync(T data, CancellationToken ct = default)
        {
            await base.DeleteAsync(data, ct);
            await InvalidateCacheAsync(ct);
        }

        /// <inheritdoc />
        public override async Task CreateAsync(IEnumerable<T> data, StoreDataDelegate<T>? storeDelegate = null, CancellationToken ct = default)
        {
            await base.CreateAsync(data, storeDelegate, ct);
            await InvalidateCacheAsync(ct);
        }

        /// <inheritdoc />
        public override async Task UpdateAsync(IEnumerable<T> data, StoreDataDelegate<T>? storeDelegate = null, CancellationToken ct = default)
        {
            await base.UpdateAsync(data, storeDelegate, ct);
            await InvalidateCacheAsync(ct);
        }

        /// <inheritdoc />
        public override async Task DeleteAsync(IEnumerable<T> data, CancellationToken ct = default)
        {
            await base.DeleteAsync(data, ct);
            await InvalidateCacheAsync(ct);
        }

        #endregion

        #region Private Helpers

        private async Task InvalidateCacheAsync(CancellationToken ct)
        {
            if (!_options.Enabled) return;

            var prefix = SqlCacheKeyBuilder.GetTablePrefix(_tableName);
            await _cache.RemoveByPrefixAsync(prefix, ct);
        }

        private CacheEntryOptions CreateEntryOptions()
        {
            return CacheEntryOptions.Absolute(_options.DefaultExpiration);
        }

        private static string ResolveTableName()
        {
            var table = SQL.DataBase.LoadTable(typeof(T));
            if (table != null && !string.IsNullOrEmpty(table.Name))
            {
                return table.Name;
            }

            // Fallback to type name if table attribute is not found
            return typeof(T).Name;
        }

        #endregion
    }
}
