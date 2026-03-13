using Birko.Data.Repositories;
using Birko.Data.SQL.Stores;
using Birko.Data.Stores;
using System;

namespace Birko.Data.SQL.Repositories
{
    /// <summary>
    /// Synchronous SQL database repository for direct model access.
    /// </summary>
    /// <typeparam name="TConnector">The SQL connector type.</typeparam>
    /// <typeparam name="T">The type of data model.</typeparam>
    public abstract class DataBaseModelRepository<TConnector, T>
        : AbstractBulkRepository<T>
        , IDataBaseRepository<TConnector, T>
        where TConnector : SQL.Connectors.AbstractConnector
        where T : Models.AbstractModel
    {
        /// <summary>
        /// Gets the database connector from the (potentially wrapped) store.
        /// </summary>
        public TConnector? Connector => Store?.GetUnwrappedStore<T, DataBaseBulkStore<TConnector, T>>()?.Connector;

        public DataBaseModelRepository()
            : this(new DataBaseBulkStore<TConnector, T>())
        {
        }

        public DataBaseModelRepository(IStore<T>? store) : base(null)
        {
            if (store != null && !store.IsStoreOfType<T, DataBaseBulkStore<TConnector, T>>())
            {
                throw new ArgumentException(
                    "Store must be of type DataBaseBulkStore<TConnector, T> or a wrapper around it.",
                    nameof(store));
            }
            if (store != null)
            {
                Store = store;
            }
        }

        public virtual void AddOnInit(SQL.Connectors.InitConnector onInit)
        {
            if (Store != null && onInit != null)
            {
                var innerStore = Store.GetUnwrappedStore<T, DataBaseBulkStore<TConnector, T>>();
                innerStore?.AddOnInit(onInit);
            }
        }

        public virtual void RemoveOnInit(SQL.Connectors.InitConnector onInit)
        {
            if (Store != null && onInit != null)
            {
                var innerStore = Store.GetUnwrappedStore<T, DataBaseBulkStore<TConnector, T>>();
                innerStore?.RemoveOnInit(onInit);
            }
        }
    }
}
