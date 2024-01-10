using Birko.Data.Filters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.Repositories
{
    public abstract class DataBaseBulkRepository<TConnector, TViewModel, TModel> 
        : AbstractBulkStoreRepository<TViewModel, TModel>
        , IDataBaseRepository<TConnector, TViewModel, TModel>
        where TConnector : SQL.Connectors.AbstractConnector
        where TModel : Models.AbstractModel, Models.ILoadable<TViewModel>
        where TViewModel : Models.ILoadable<TModel>
    {
        public DataBaseBulkRepository(): base ()
        {
        }

        public override void SetSettings(Stores.ISettings settings)
        {
            if (settings is Stores.PasswordSettings setts)
            {
                base.SetSettings(setts);
                Store = Stores.StoreLocator.GetStore<Stores.DataBaseStore<TConnector, TModel>, Stores.ISettings>(setts);
                AddOnInit((connector) =>
                {
                    connector.CreateTable(new[] { typeof(TModel) });
                });
            }
        }

        public virtual void AddOnInit(SQL.Connectors.InitConnector onInit)
        {
            if (Store != null && onInit != null)
            {
                (Store as Stores.DataBaseStore<TConnector, TModel>)?.AddOnInit(onInit);
            }
        }

        public virtual void RemoveOnInit(SQL.Connectors.InitConnector onInit)
        {
            if (Store != null && onInit != null)
            {
                (Store as Stores.DataBaseStore<TConnector, TModel>)?.RemoveOnInit(onInit);
            }
        }

        public TConnector Connector => (Store as Stores.DataBaseStore<TConnector, TModel>)?.Connector;

        public virtual IEnumerable<TViewModel> Read(IRepositoryFilter<TModel> filter = null, IDictionary<Expression<Func<TModel, object>>, bool> orderByExpr = null, int? limit = null, int? offset = null)
        {
            if (Connector != null)
            {
                foreach (TModel item in Connector?.Select(typeof(TModel), filter?.Filter(), orderByExpr, limit, offset))
                {
                    if (item != null)
                    {
                        yield return LoadInstance(item);
                    }
                }
            }
        }

        public virtual void Update(IDictionary<Expression<Func<TModel, object>>, Expression<Func<TModel, object>>> expresions, IRepositoryFilter<TModel> filter = null)
        {
            if (Connector != null)
            {
                if (typeof(TModel).IsSubclassOf(typeof(Models.AbstractLogModel)))
                {
                    //copy UpdateAt date to prevUpdateAt date
                    Expression<Func<TModel, object>> prevUpdateAtFunc = m => (m as Models.AbstractLogModel).PrevUpdatedAt;
                    Expression<Func<TModel, object>> prevUpdateAtExpr = m => (m as Models.AbstractLogModel).UpdatedAt;
                    if (!expresions.ContainsKey(prevUpdateAtFunc))
                    {
                        expresions.Add(prevUpdateAtFunc, prevUpdateAtExpr);
                    }
                    //update UpdateAt date
                    Expression<Func<TModel, object>> updateAtFunc = m => (m as Models.AbstractLogModel).UpdatedAt;
                    if (!expresions.ContainsKey(updateAtFunc))
                    {
                        expresions.Add(updateAtFunc, m => DateTime.UtcNow);
                    }
                }

                Connector?.Update(typeof(TModel), expresions, filter?.Filter());
            }
        }


        public virtual void Delete(IRepositoryFilter<TModel> filter = null)
        {
            if (Connector != null)
            {
                Connector.Delete(typeof(TModel), filter?.Filter());
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
