using System;
using System.Collections.Generic;
using System.Text;

namespace Birko.Data.Repositories
{
    /// <summary>
    /// Model-direct SQL database repository interface.
    /// </summary>
    /// <typeparam name="TConnector">The SQL connector type.</typeparam>
    /// <typeparam name="T">The type of data model.</typeparam>
    public interface IDataBaseRepository<TConnector, T> : IRepository<T>
        where TConnector : SQL.Connectors.AbstractConnector
        where T : Models.AbstractModel
    {
        TConnector Connector { get; }
        void AddOnInit(SQL.Connectors.InitConnector onInit);
        void RemoveOnInit(SQL.Connectors.InitConnector onInit);
    }
}
