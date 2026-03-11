using Birko.Data.SQL.Connectors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Birko.Data.Stores
{
    /// <summary>
    /// Bulk database store that provides optimized bulk operations.
    /// Extends <see cref="DataBaseStore{DB, T}"/> with bulk CRUD capabilities.
    /// </summary>
    /// <typeparam name="DB">The type of database connector, must inherit from <see cref="AbstractConnector"/>.</typeparam>
    /// <typeparam name="T">The type of entity, must inherit from <see cref="Models.AbstractModel"/>.</typeparam>
    public class DataBaseBulkStore<DB, T> : DataBaseStore<DB, T>, IBulkStore<T>
        where T : Models.AbstractModel
        where DB : AbstractConnector
    {
        /// <summary>
        /// Initializes a new instance of the DataBaseBulkStore class.
        /// </summary>
        public DataBaseBulkStore()
            : base()
        {
        }

        #region Bulk Read Operations

        /// <inheritdoc />
        public virtual IEnumerable<T> Read(Expression<Func<T, bool>>? filter = null, OrderBy<T>? orderBy = null, int? limit = null, int? offset = null)
        {
            return Connector?.Select(typeof(T), filter as LambdaExpression, orderBy?.ToDictionary(), limit, offset)?.OfType<T>() ?? Enumerable.Empty<T>();
        }

        /// <inheritdoc />
        public virtual IEnumerable<T> Read()
        {
            return Read(null, null, null, null);
        }

        #endregion

        #region Bulk Write Operations

        /// <inheritdoc />
        public virtual void Create(IEnumerable<T> data, StoreDataDelegate<T>? storeDelegate = null)
        {
            foreach (var item in data)
            {
                Create(item, storeDelegate);
            }
        }

        /// <inheritdoc />
        public virtual void Update(IEnumerable<T> data, StoreDataDelegate<T>? storeDelegate = null)
        {
            foreach (var item in data)
            {
                Update(item, storeDelegate);
            }
        }

        /// <inheritdoc />
        public virtual void Delete(IEnumerable<T> data)
        {
            foreach (var item in data)
            {
                Delete(item);
            }
        }

        #endregion
    }
}
