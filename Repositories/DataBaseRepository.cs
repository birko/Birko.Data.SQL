using Birko.Data.Stores;
using System;

namespace Birko.Data.Repositories
{
    public abstract class DataBaseRepository<TConnector, TViewModel, TModel>
        : AbstractBulkViewModelRepository<TViewModel, TModel>
        , IDataBaseRepository<TConnector, TViewModel, TModel>
        where TConnector : SQL.Connectors.AbstractConnector
        where TModel : Models.AbstractModel, Models.ILoadable<TViewModel>
        where TViewModel : Models.ILoadable<TModel>
    {
        /// <summary>
        /// Gets the database connector from the (potentially wrapped) store.
        /// This works with tenant wrappers and other store wrappers.
        /// </summary>
        public TConnector Connector => Store?.GetUnwrappedStore<TModel, DataBaseBulkStore<TConnector, TModel>>()?.Connector;

        public DataBaseRepository()
            : this(new DataBaseBulkStore<TConnector, TModel>())
        {
        }

        public DataBaseRepository(IStore<TModel>? store) : base(null)
        {
            if (store != null && !store.IsStoreOfType<TModel, DataBaseBulkStore<TConnector, TModel>>())
            {
                throw new ArgumentException(
                    "Store must be of type DataBaseBulkStore<TConnector, TModel> or a wrapper around it (e.g., TenantStoreWrapper).",
                    nameof(store));
            }
            // Set the store after validation - base constructor handles null by creating default
            if (store != null)
            {
                Store = store;
            }
        }

        public virtual void AddOnInit(SQL.Connectors.InitConnector onInit)
        {
            if (Store != null && onInit != null)
            {
                var innerStore = Store.GetUnwrappedStore<TModel, DataBaseBulkStore<TConnector, TModel>>();
                innerStore?.AddOnInit(onInit);
            }
        }

        public virtual void RemoveOnInit(SQL.Connectors.InitConnector onInit)
        {
            if (Store != null && onInit != null)
            {
                var innerStore = Store.GetUnwrappedStore<TModel, DataBaseBulkStore<TConnector, TModel>>();
                innerStore?.RemoveOnInit(onInit);
            }
        }

        /*

        public virtual void ReadView<TView>(Action<TView> readAction, IDictionary<Expression<Func<TModel, object>>, bool> orderByExpr = null)
        {
            ReadView(null, readAction, orderByExpr);
        }

        public virtual void ReadView<TView>(Expression<Func<TView, bool>> expr, Action<TView> readAction, IDictionary<Expression<Func<TModel, object>>, bool> orderByExpr = null)
        {
            var _store = Store;
            if (_store != null && readAction != null)
            {
                var connector = GetConnector();
                connector?.SelectView(typeof(TView), (data) =>
                {
                    readAction((TView)data);
                }, expr, orderByExpr);
            }
        }
        */
    }
}
