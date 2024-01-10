﻿using Birko.Data.Filters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Birko.Data.Repositories
{
    public abstract class DataBaseRepository<TConnector, TViewModel, TModel> 
        : AbstractStoreRepository<TViewModel, TModel>
        , IDataBaseRepository<TConnector, TViewModel, TModel>
        where TConnector : SQL.Connectors.AbstractConnector
        where TModel : Models.AbstractModel, Models.ILoadable<TViewModel>
        where TViewModel : Models.ILoadable<TModel>
    {

        public DataBaseRepository(): base ()
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
