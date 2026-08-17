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
    /// Uses Template Method pattern — concrete stores override *Core methods.
    /// </summary>
    /// <typeparam name="DB">The type of database connector, must inherit from <see cref="AbstractConnector"/>.</typeparam>
    /// <typeparam name="T">The type of entity, must inherit from <see cref="Models.AbstractModel"/>.</typeparam>
    public class DataBaseBulkStore<DB, T> : DataBaseStore<DB, T>, IBulkStore<T>, IAggregatableStore<T>
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
        public IEnumerable<T> Read(Expression<Func<T, bool>>? filter = null, OrderBy<T>? orderBy = null, int? limit = null, int? offset = null)
        {
            EnsureInitialized();
            return ReadCore(filter, orderBy, limit, offset);
        }

        /// <summary>
        /// Core bulk read implementation. Override in concrete stores for provider-specific behavior.
        /// </summary>
        protected virtual IEnumerable<T> ReadCore(Expression<Func<T, bool>>? filter = null, OrderBy<T>? orderBy = null, int? limit = null, int? offset = null)
        {
            using var _tx = EnterTransactionScope();
            return Connector?.Select(typeof(T), filter as LambdaExpression, orderBy?.ToDictionary(), limit, offset)?.OfType<T>() ?? Enumerable.Empty<T>();
        }

        /// <inheritdoc />
        public virtual IEnumerable<T> Read()
        {
            return Read(null, null, null, null);
        }

        /// <inheritdoc />
        public virtual T? ReadFirst(Expression<Func<T, bool>>? filter = null)
        {
            // base = DataBaseStore<DB, T> — resolves to the single-result Read(filter) (LIMIT 1 via ReadCore)
            // the bulk overload hides here.
            return base.Read(filter);
        }

        #endregion

        #region Bulk Write Operations

        /// <inheritdoc />
        public void Create(IEnumerable<T> data, StoreDataDelegate<T>? storeDelegate = null)
        {
            EnsureInitialized();
            CreateCore(data, storeDelegate);
        }

        /// <summary>
        /// Core bulk create implementation. Override in concrete stores for provider-specific behavior.
        /// </summary>
        protected virtual void CreateCore(IEnumerable<T> data, StoreDataDelegate<T>? storeDelegate = null)
        {
            foreach (var item in data)
            {
                Create(item, storeDelegate);
            }
        }

        /// <inheritdoc />
        public void Update(IEnumerable<T> data, StoreDataDelegate<T>? storeDelegate = null)
        {
            EnsureInitialized();
            UpdateCore(data, storeDelegate);
        }

        /// <summary>
        /// Core bulk update implementation. Override in concrete stores for provider-specific behavior.
        /// </summary>
        protected virtual void UpdateCore(IEnumerable<T> data, StoreDataDelegate<T>? storeDelegate = null)
        {
            foreach (var item in data)
            {
                Update(item, storeDelegate);
            }
        }

        /// <inheritdoc />
        public virtual void Update(Expression<Func<T, bool>> filter, Action<T> updateAction)
        {
            // SH-M023: read-then-loop, so no conditionless SQL is emitted and the connector guard cannot see
            // this path — but a null filter still means Read(null) = every row, then mutate every one.
            RequireFilter(filter, "update");
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
            RequireFilter(filter, "update");
            UpdateInternal(filter, updates, SQL.DataBase.IsExplicitAllRows(filter as LambdaExpression));
        }

        /// <summary>
        /// Updates EVERY row with <paramref name="updates"/> — the explicit all-rows door (SH-H002).
        /// Equivalent to <c>Update(x =&gt; true, updates)</c>, and the recommended spelling.
        /// </summary>
        public virtual void UpdateAll(PropertyUpdate<T> updates)
        {
            UpdateInternal(null, updates, allowAllRows: true);
        }

        /// <summary>
        /// Deletes EVERY row — the explicit all-rows door (SH-H002). Equivalent to
        /// <c>Delete(x =&gt; true)</c>. Use <c>Destroy()</c> to drop the table instead of emptying it.
        /// </summary>
        public virtual void DeleteAll()
        {
            EnsureInitialized();
            using var _tx = EnterTransactionScope();
            Connector?.DeleteAll(typeof(T));
        }

        /// <summary>
        /// Guards the filter-based destructive overloads. A null filter is refused rather than treated as
        /// "every row" — the parameter is already declared non-nullable, so this closes the gap between the
        /// declaration and the behaviour (SH-M023).
        /// </summary>
        private static void RequireFilter(Expression<Func<T, bool>> filter, string operation)
        {
            if (filter == null)
            {
                throw new ArgumentNullException(
                    nameof(filter),
                    $"A filter is required to {operation} {typeof(T).Name}: a missing filter would affect every "
                        + $"row. To target every row deliberately use {(operation == "delete" ? "DeleteAll()" : "UpdateAll(updates)")} "
                        + "or an explicit `x => true` filter.");
            }
        }

        /// <summary>
        /// Shared body of the filter overload and <see cref="UpdateAll"/>. A null filter is only legal with
        /// <paramref name="allowAllRows"/>; the public overload guards it first.
        /// </summary>
        private void UpdateInternal(Expression<Func<T, bool>>? filter, PropertyUpdate<T> updates, bool allowAllRows)
        {
            EnsureInitialized();
            if (Connector == null || updates.Assignments.Count == 0) return;
            using var _tx = EnterTransactionScope();


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
            Connector.Update(table.Name, fields, values, conditions, false, allowAllRows);
        }

        /// <inheritdoc />
        public void Delete(IEnumerable<T> data)
        {
            EnsureInitialized();
            DeleteCore(data);
        }

        /// <summary>
        /// Core bulk delete implementation. Override in concrete stores for provider-specific behavior.
        /// </summary>
        protected virtual void DeleteCore(IEnumerable<T> data)
        {
            foreach (var item in data)
            {
                Delete(item);
            }
        }

        /// <inheritdoc />
        public virtual void Delete(Expression<Func<T, bool>> filter)
        {
            RequireFilter(filter, "delete");
            EnsureInitialized();
            if (Connector == null) return;
            using var _tx = EnterTransactionScope();

            if (SQL.DataBase.IsExplicitAllRows(filter as LambdaExpression))
            {
                Connector.DeleteAll(typeof(T));
                return;
            }
            Connector.Delete(typeof(T), filter as LambdaExpression);
        }

        #endregion

        #region Aggregation

        /// <summary>
        /// Executes a synchronous aggregation query using SQL GROUP BY.
        /// </summary>
        public IReadOnlyList<AggregateResult> Aggregate(AggregateQuery<T> query)
        {
            EnsureInitialized();
            if (Connector == null) return Array.Empty<AggregateResult>();
            using var _tx = EnterTransactionScope();


            var results = new List<AggregateResult>();
            foreach (var row in Connector.SelectAggregate(typeof(T), query))
            {
                results.Add(row);
            }
            return results.AsReadOnly();
        }

        #endregion
    }
}
