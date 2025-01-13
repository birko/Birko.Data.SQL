using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using Birko.Data.Attributes;
using Birko.Data.SQL.Conditions;
using Birko.Data.SQL.Connectors;

namespace Birko.Data.Stores
{

    public class DataBaseBulkStore<DB, T> 
        : DataBaseStore<DB,T>
        , IBulkStore<T>
        , ISettingsStore<PasswordSettings>
        where T : Models.AbstractModel
        where DB : AbstractConnector
    {
        public DataBaseBulkStore()
        {

        }

        public IEnumerable<T> Read(Expression<Func<T, bool>>? filter = null, int? limit = null, int? offset = null)
        {
            if (Connector == null)
            {
                yield break;
            }
            foreach (var item in Connector.Select(typeof(T), filter, null, limit, offset))
            {
                yield return (T)item;
            }
        }

        public void Create(IEnumerable<T> data, StoreDataDelegate<T>? storeDelegate = null)
        {
            if (Connector == null)
            {
                return;
            }

            foreach (var item in data.Where(x => x != null))
            {
                item.Guid = Guid.NewGuid();
                storeDelegate?.Invoke(item);
                Connector.Insert(data);
            }
        }

        public void Update(IEnumerable<T> data, StoreDataDelegate<T>? storeDelegate = null)
        {
            if (Connector == null)
            {
                return;
            }
            var primaryFields = SQL.DataBase.GetPrimaryFields(typeof(T));

            List<Condition> conditions = new();
            foreach (var item in data.Where(x => x != null))
            {
                foreach (var field in primaryFields)
                {
                    conditions.Add(SQL.DataBase.CreateCondition(field, item));
                }
                storeDelegate?.Invoke(item);
                Connector.Update(data, conditions);
            }
        }

        public void Delete(IEnumerable<T> data)
        {
            if (Connector == null)
            {
                return;
            }
            var primaryFields = SQL.DataBase.GetPrimaryFields(typeof(T));
            foreach (var item in data.Where(x => x != null))
            {
                List<Condition> conditions = new ();
                foreach (var field in primaryFields)
                {
                    conditions.Add(SQL.DataBase.CreateCondition(field, item));
                }
                Connector?.Delete(typeof(T), conditions);
            }
        }
    }
}
