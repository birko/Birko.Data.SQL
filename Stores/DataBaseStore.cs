using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using Birko.Data.Attributes;
using Birko.Data.SQL.Connectors;

namespace Birko.Data.Stores
{

    public class DataBaseStore<DB, T> 
        : AbstractStore<T>
        , ISettingsStore<PasswordSettings>
        where T : Models.AbstractModel
        where DB : AbstractConnector
    {
        public DB Connector { get; private set; }

        public DataBaseStore()
        {

        }

        public virtual void SetSettings(PasswordSettings settings)
        {
            if (settings is PasswordSettings sets)
            {
                Connector = (DB)SQL.DataBase.GetConnector<DB>(sets);
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

        public override long Count(Expression<Func<T, bool>>? filter = null)
        {
            return Connector?.SelectCount(typeof(T), filter) ?? 0;
        }

        public override T? ReadOne(Expression<Func<T, bool>>? filter = null)
        {
            return (T?)Connector?.Select(typeof(T), filter)?.FirstOrDefault();
        }

        public override void Create(T data, StoreDataDelegate<T>? storeDelegate = null)
        {
            data.Guid = Guid.NewGuid();
            storeDelegate?.Invoke(data);
            Connector.Insert(data);
        }

        public override void Update(T data, StoreDataDelegate<T>? storeDelegate = null)
        {
            List<SQL.Conditions.Condition> conditions = new List<SQL.Conditions.Condition>();

            foreach (var field in SQL.DataBase.GetPrimaryFields(typeof(T)))
            {
                conditions.Add(SQL.DataBase.CreateCondition(field, data));
            }

            storeDelegate?.Invoke(data);
            Connector.Update(data, conditions);
        }

        public override void Delete(T data)
        {
            if (data != null)
            {
                List<SQL.Conditions.Condition> conditions = new List<SQL.Conditions.Condition>();
                foreach (var field in SQL.DataBase.GetPrimaryFields(typeof(T)))
                {
                    conditions.Add(SQL.DataBase.CreateCondition(field, data));
                }
                Connector.Delete(typeof(T), conditions);
            }
        }
    }
}
