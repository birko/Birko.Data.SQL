using System;
using Birko.Data.Repositories;
using Birko.Data.SQL.Connectors;
using Birko.Data.SQL.Stores;
using Birko.Data.Stores;
using Birko.Configuration;

namespace Birko.Data.SQL.Repositories
{
    /// <summary>
    /// Async database repository for direct model access with SQL-based storage.
    /// </summary>
    /// <typeparam name="T">The type of data model.</typeparam>
    public class AsyncDataBaseModelRepository<T> : Data.Repositories.AbstractAsyncBulkRepository<T>
        where T : Data.Models.AbstractModel
    {
        /// <summary>
        /// Gets the database store.
        /// </summary>
        public AsyncDataBaseBulkStore<SQL.Connectors.AbstractConnector, T>? DataBaseStore =>
            Store?.GetUnwrappedStore<T, Stores.AsyncDataBaseBulkStore<SQL.Connectors.AbstractConnector, T>>();

        /// <summary>
        /// Initializes a new instance with dependency injection support.
        /// </summary>
        /// <param name="store">The async database bulk store to use.</param>
        public AsyncDataBaseModelRepository(Data.Stores.IAsyncBulkStore<T>? store)
            : base(null)
        {
            if (store != null && !store.IsStoreOfType<T, Stores.AsyncDataBaseBulkStore<SQL.Connectors.AbstractConnector, T>>())
            {
                throw new ArgumentException(
                    "Store must be of type AsyncDataBaseBulkStore<T> or a wrapper around it.",
                    nameof(store));
            }
            if (store != null)
            {
                Store = store;
            }
        }
    }
}
