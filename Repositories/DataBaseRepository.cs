using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.Repositories
{
    public abstract class DataBaseRepository<TConnector, TViewModel, TModel> : AbstractRepository<TViewModel, TModel>
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
            var _store = Store;
            if (_store != null && onInit != null)
            {
                (_store as Stores.DataBaseStore<TConnector, TModel>).AddOnInit(onInit);
            }
        }

        public virtual void RemoveOnInit(SQL.Connectors.InitConnector onInit)
        {
            var _store = Store;
            if (_store != null && onInit != null)
            {
                (_store as Stores.DataBaseStore<TConnector, TModel>).RemoveOnInit(onInit);
            }
        }
        protected SQL.Connectors.AbstractConnector GetConnector()
        {
            var _store = Store;
            return (_store != null) ? (_store as Stores.DataBaseStore<TConnector, TModel>).Connector : null;
        }

        public override void Read(Action<TViewModel> readAction)
        {
            Read(null, readAction);
        }

        public override void Read(Expression<Func<TModel, bool>> expr, Action<TViewModel> readAction)
        {
            Read(expr, readAction, null);
        }

        public virtual void Read(Action<TViewModel> readAction, IDictionary<Expression<Func<TModel, object>>, bool> orderByExpr = null)
        {
            Read(null, readAction, orderByExpr);
        }

        public override TViewModel Read(Guid Id)
        {
            return ReadOne(x => x.Guid == Id);
        }

        public virtual void Read(Expression<Func<TModel, bool>> expr, Action<TViewModel> readAction, IDictionary<Expression<Func<TModel, object>>, bool> orderByExpr = null)
        {
            var _store = Store;
            if (_store != null && readAction != null)
            {
                var connector = GetConnector();
                connector?.Select(typeof(TModel), (data) =>
                {
                    if (data != null)
                    {
                        TViewModel result = (TViewModel)Activator.CreateInstance(typeof(TViewModel), new object[] { });
                        result.LoadFrom((data as TModel));
                        StoreHash((data as TModel));
                        readAction?.Invoke(result);
                    }
                }, expr, orderByExpr);
            }
        }

        public virtual TViewModel ReadOne(Expression<Func<TModel, bool>> expr, IDictionary<Expression<Func<TModel, object>>, bool> orderByExpr = null)
        {
            var _store = Store;
            if (_store != null)
            {
                TViewModel result = default(TViewModel);
                Read(expr, (item) =>
                {
                    result = item;
                }, orderByExpr);
                return result;
            }
            return default(TViewModel);
        }

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

        //bulk update command
        public virtual void Update(IDictionary<Expression<Func<TModel, object>>, Expression<Func<TModel, object>>> expresions, Expression<Func<TModel, bool>> expr, Action<TViewModel> readAction, Expression<Func<TModel, bool>> readExpr)
        {
            var _store = Store;
            if (_store != null)
            {
                var connector = GetConnector();
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
                    Expression<Func<TModel, object>> updateAtExpr = m => DateTime.UtcNow;
                    if (!expresions.ContainsKey(updateAtFunc))
                    {
                        expresions.Add(updateAtFunc, updateAtExpr);
                    }
                }

                connector?.Update(typeof(TModel), expresions, expr);
                if (readAction != null)
                {
                    Read(readExpr, (data) =>
                    {
                        readAction?.Invoke(data);
                    });
                }
            }
        }

        // TODO test
        //bulk update command
        public virtual void Update(IDictionary<Expression<Func<TModel, object>>, object> expresions, Expression<Func<TModel, bool>> expr, Action<TViewModel> readAction, Expression<Func<TModel, bool>> readExpr)
        {
            var _store = Store;
            if (_store != null)
            {
                var connector = GetConnector();
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
                    Expression<Func<TModel, object>> updateAtFunc =  m => (m as Models.AbstractLogModel).UpdatedAt;
                    if (!expresions.ContainsKey(updateAtFunc))
                    {
                        expresions.Add(updateAtFunc, DateTime.UtcNow);
                    }
                }

                connector?.Update(typeof(TModel), expresions, expr);
                Read(readExpr, (data) => {
                    readAction?.Invoke(data);
                });
            }
        }

        public virtual void Delete(Expression<Func<TModel, bool>> expr)
        {
            var _store = Store;
            if (_store != null)
            {
                GetConnector()?.Delete(typeof(TModel), expr);
            }
        }
    }
}
