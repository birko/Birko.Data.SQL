using Birko.Data.SQL.Connectors;
using Birko.Data.Stores;
using Birko.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Birko.Data.SQL.Stores
{
    /// <summary>
    /// Bulk database store that provides optimized bulk operations.
    /// Extends <see cref="DataBaseStore{DB, T}"/> with bulk CRUD capabilities.
    /// </summary>
    /// <typeparam name="DB">The type of database connector, must inherit from <see cref="AbstractConnector"/>.</typeparam>
    /// <typeparam name="T">The type of entity, must inherit from <see cref="Models.AbstractModel"/>.</typeparam>
    public class DataBaseBulkStore<DB, T> : DataBaseStore<DB, T>, IBulkStore<T>
        where T : Models.AbstractModel
        where DB : AbstractConnector
    {
        /// <summary>
        /// Initializes a new instance of the DataBaseBulkStore class.
        /// </summary>
        public DataBaseBulkStore()
            : base()
        {
        }

        #region Bulk Read Operations

        /// <inheritdoc />
        public virtual IEnumerable<T> Read(Expression<Func<T, bool>>? filter = null, OrderBy<T>? orderBy = null, int? limit = null, int? offset = null)
        {
            return Connector?.Select(typeof(T), filter as LambdaExpression, orderBy?.ToDictionary(), limit, offset)?.OfType<T>() ?? Enumerable.Empty<T>();
        }

        /// <inheritdoc />
        public virtual IEnumerable<T> Read()
        {
            return Read(null, null, null, null);
        }

        #endregion

        #region Bulk Write Operations

        /// <inheritdoc />
        public virtual void Create(IEnumerable<T> data, StoreDataDelegate<T>? storeDelegate = null)
        {
            foreach (var item in data)
            {
                Create(item, storeDelegate);
            }
        }

        /// <inheritdoc />
        public virtual void Update(IEnumerable<T> data, StoreDataDelegate<T>? storeDelegate = null)
        {
            foreach (var item in data)
            {
                Update(item, storeDelegate);
            }
        }

        /// <inheritdoc />
        public virtual void Update(Expression<Func<T, bool>> filter, Action<T> updateAction)
        {
            var items = Read(filter, null, null, null).ToList();
            foreach (var item in items)
            {
                updateAction(item);
                Update(item);
            }
        }

        /// <inheritdoc />
        public virtual void Update(Expression<Func<T, bool>> filter, PropertyUpdate<T> updates)
        {
            if (Connector == null || updates.Assignments.Count == 0) return;

            var table = SQL.DataBase.LoadTable(typeof(T));
            var fields = new Dictionary<int, string>();
            var values = new Dictionary<string, object>();
            int i = 0;
            foreach (var (property, value) in updates.Assignments)
            {
                var field = SQL.DataBase.GetFieldFromLambda(property);
                fields.Add(i, field.Name);
                values.Add(field.Name, value ?? DBNull.Value);
                i++;
            }
            var conditions = SQL.DataBase.ParseConditionExpression(filter as LambdaExpression);
            Connector.Update(table.Name, fields, values, conditions);
        }

        /// <inheritdoc />
        public virtual void Delete(IEnumerable<T> data)
        {
            foreach (var item in data)
            {
                Delete(item);
            }
        }

        /// <inheritdoc />
        public virtual void Delete(Expression<Func<T, bool>> filter)
        {
            if (Connector == null) return;
            Connector.Delete(typeof(T), filter as LambdaExpression);
        }

        #endregion
    }
}
