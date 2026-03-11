using System;
using Birko.Data.Stores;
using Birko.Data.SQL.Connectors;

namespace Birko.Data.Repositories
{
    /// <summary>
    /// Async database repository for SQL-based storage.
    /// Works with wrapped stores (e.g., tenant wrappers).
    /// </summary>
    /// <typeparam name="TViewModel">The type of view model.</typeparam>
    /// <typeparam name="TModel">The type of data model.</typeparam>
    public class AsyncDataBaseRepository<TViewModel, TModel> : Data.Repositories.AbstractAsyncBulkViewModelRepository<TViewModel, TModel>
        where TModel : Data.Models.AbstractModel, Data.Models.ILoadable<TViewModel>
        where TViewModel : Data.Models.ILoadable<TModel>
    {
        /// <summary>
        /// Gets the database store.
        /// This works with wrapped stores (e.g., tenant wrappers).
        /// </summary>
        public Data.Stores.AsyncDataBaseBulkStore<SQL.Connectors.AbstractConnector, TModel>? DataBaseStore =>
            Store?.GetUnwrappedStore<TModel, Stores.AsyncDataBaseBulkStore<SQL.Connectors.AbstractConnector, TModel>>();

        //public TConnector Connector => DataBaseStore?.Connector;

        /// <summary>
        /// Initializes a new instance with dependency injection support.
        /// </summary>
        /// <param name="store">The async database bulk store to use (optional). Can be wrapped (e.g., by tenant wrappers).</param>
        public AsyncDataBaseRepository(Data.Stores.IAsyncBulkStore<TModel>? store)
            : base(null)
        {
            if (store != null && !store.IsStoreOfType<TModel, Stores.AsyncDataBaseBulkStore<SQL.Connectors.AbstractConnector, TModel>>())
            {
                throw new ArgumentException(
                    "Store must be of type AsyncDataBaseBulkStore<TModel> or a wrapper around it (e.g., AsyncTenantBulkStoreWrapper).",
                    nameof(store));
            }
            // Set the store after validation - base constructor handles null by creating default
            if (store != null)
            {
                Store = store;
            }
        }
    }
}
