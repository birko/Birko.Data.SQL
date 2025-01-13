using System;
using System.Collections.Generic;
using System.Text;

namespace Birko.Data.Repositories
{
    public interface IDataBaseRepository<TConnector, TViewModel, TModel> : IRepository<TViewModel, TModel>
        where TConnector : SQL.Connectors.AbstractConnector
        where TModel : Models.AbstractModel, Models.ILoadable<TViewModel>
        where TViewModel : Models.ILoadable<TModel>
    {
        TConnector Connector { get; }
        void AddOnInit(SQL.Connectors.InitConnector onInit);
        void RemoveOnInit(SQL.Connectors.InitConnector onInit);
    }
}