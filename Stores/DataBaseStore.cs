using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using Birko.Data.SQL.Connectors;

namespace Birko.Data.Stores
{

    public class DataBaseStore<DB, T> : AbstractStore<T>
        where T : Models.AbstractModel
        where DB : AbstractConnector
    {
        private Dictionary<Guid, T> _insertList = null;
        private Dictionary<Guid, T> _updateList = null;
        private Dictionary<Guid, T> _deleteList = null;

        public AbstractConnector Connector { get; private set; }

        public DataBaseStore()
        {

        }

        public override void SetSettings(ISettings settings)
        {
            if (settings is PasswordSettings sets)
            {
                Connector = SQL.DataBase.GetConnector<DB>(sets);
                _insertList = new Dictionary<Guid, T>();
                _updateList = new Dictionary<Guid, T>();
                _deleteList = new Dictionary<Guid, T>();
            }
        }

        public void AddOnInit(InitConnector onInit)
        {
            if (onInit != null && Connector != null)
            {
                Connector.OnInit += onInit;
            }
        }

        public void RemoveOnInit(InitConnector onInit)
        {
            if (onInit != null && Connector != null)
            {
                Connector.OnInit -= onInit;
            }
        }

        public override void Init()
        {
            Connector?.DoInit();
        }

        public override void Destroy()
        {
            Connector?.DropTable(typeof(T));
        }

        public override void Delete(T data)
        {
            if (data != null && data.Guid != null && !_deleteList.ContainsKey(data.Guid.Value))
            {
                _deleteList.Add(data.Guid.Value, data);
            }
        }

        public override void List(Expression<Func<T, bool>> filter, Action<T> action, int? limit = null, int? offset = null)
        {
            if (Connector != null && action != null)
            {
                Connector.Select(typeof(T), (data) =>
                {
                    if (data != null)
                    {
                        action?.Invoke((T)data);
                    }
                }, filter, null, limit, offset);
            }
        }

        public override long Count(Expression<Func<T, bool>> filter)
        {
            if (Connector != null)
            {
                return Connector.SelectCount(typeof(T), filter);
            }
            return 0;
        }

        public override void Save(T data, StoreDataDelegate<T> storeDelegate = null)
        {
            if (data != null)
            {
                bool newItem = data.Guid == null;
                if (newItem) // new
                {
                    data.Guid = Guid.NewGuid();

                }
                data = storeDelegate?.Invoke(data) ?? data;
                if (data != null)
                {
                    if (newItem)
                    {
                        if (!_insertList.ContainsKey(data.Guid.Value))
                        {
                            _insertList.Add(data.Guid.Value, data);
                        }
                        else
                        {
                            _insertList[data.Guid.Value] = data;
                        }
                    }
                    else
                    {
                        if (data is Models.AbstractLogModel)
                        {
                            (data as Models.AbstractLogModel).PrevUpdatedAt = (data as Models.AbstractLogModel).UpdatedAt;
                            (data as Models.AbstractLogModel).UpdatedAt = DateTime.UtcNow;
                        }
                        if (!_updateList.ContainsKey(data.Guid.Value))
                        {
                            _updateList.Add(data.Guid.Value, data);
                        }
                        else
                        {
                            _updateList[data.Guid.Value] = data;
                        }
                    }
                }
            }
        }

        public override void StoreChanges()
        {
            if (Connector != null)
            {
                IEnumerable<SQL.Fields.AbstractField> primaryFields = new SQL.Fields.AbstractField[0];
                if (_deleteList.Count > 0 || _updateList.Count > 0)
                {
                    primaryFields = SQL.DataBase.GetPrimaryFields(typeof(T));
                    if (primaryFields == null || !primaryFields.Any())
                    {
                        throw new Exceptions.StoreException("No primary fields in stored model");
                    }
                }
                //delete
                while (_deleteList.Count > 0)
                {
                    var kvp = _deleteList.First();
                    List<SQL.Conditions.Condition> conditions = new List<SQL.Conditions.Condition>();
                    foreach (var field in primaryFields)
                    {
                        conditions.Add(SQL.DataBase.CreateCondition(field, kvp.Value));
                    }
                    Connector.Delete(typeof(T), conditions);
                    _deleteList.Remove(kvp.Key);
                }
                //update
                while (_updateList.Count > 0)
                {
                    var kvp = _updateList.First();

                    List<SQL.Conditions.Condition> conditions = new List<SQL.Conditions.Condition>();
                    foreach (var field in primaryFields)
                    {
                        conditions.Add(SQL.DataBase.CreateCondition(field, kvp.Value));
                    }
                    Connector.Update(kvp.Value, conditions);
                    _updateList.Remove(kvp.Key);
                }
                //insert
                while (_insertList.Count > 0)
                {
                    var kvp = _insertList.First();
                    Connector.Insert(kvp.Value);
                    _insertList.Remove(kvp.Key);
                }
            }
            else
            {
                throw new Exceptions.StoreException("No database connector provided");
            }
        }
    }
}
