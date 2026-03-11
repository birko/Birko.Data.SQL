using Birko.Data.SQL.Connectors;
using Birko.Data.SQL.Stores;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Birko.Data.Stores
{
    /// <summary>
    /// Async version of DataBaseStore for basic database operations.
    /// Provides core async CRUD functionality without bulk operations.
    /// For bulk operations, use <see cref="AsyncDataBaseBulkStore{DB, T}"/> instead.
    /// </summary>
    /// <typeparam name="DB">The type of database connector, must inherit from <see cref="AbstractConnector"/>.</typeparam>
    /// <typeparam name="T">The type of entity, must inherit from <see cref="Models.AbstractModel"/>.</typeparam>
    public class AsyncDataBaseStore<DB, T>
        : AbstractAsyncStore<T>
        , ISettingsStore<ISettings>
        , ISettingsStore<PasswordSettings>
        , IAsyncTransactionalStore<T, SqlTransactionContext>
        where T : Models.AbstractModel
        where DB : AbstractConnector
    {
        /// <summary>
        /// Gets the database connector.
        /// </summary>
        public DB? Connector { get; protected set; }

        /// <inheritdoc />
        public SqlTransactionContext? TransactionContext { get; private set; }

        /// <inheritdoc />
        public void SetTransactionContext(SqlTransactionContext? context)
        {
            TransactionContext = context;
            Connector?.SetExternalTransaction(context?.Connection, context?.Transaction);
        }

        /// <summary>
        /// Initializes a new instance of the AsyncDataBaseStore class.
        /// </summary>
        public AsyncDataBaseStore()
        {
        }

        #region Settings and Initialization

        /// <summary>
        /// Sets the connection settings using PasswordSettings.
        /// </summary>
        /// <param name="settings">The password settings.</param>
        public virtual void SetSettings(PasswordSettings settings)
        {
            SetSettings((ISettings)settings);
        }

        /// <summary>
        /// Sets the connection settings.
        /// </summary>
        /// <param name="settings">The settings to use.</param>
        public virtual void SetSettings(ISettings settings)
        {
            if (settings is PasswordSettings sets)
            {
                Connector = (DB)SQL.DataBase.GetConnector<DB>(sets);
            }
        }

        /// <summary>
        /// Adds an initialization callback.
        /// </summary>
        /// <param name="onInit">The callback to add.</param>
        public void AddOnInit(InitConnector onInit)
        {
            if (onInit != null && Connector != null)
            {
                Connector.OnInit += onInit;
            }
        }

        /// <summary>
        /// Removes an initialization callback.
        /// </summary>
        /// <param name="onInit">The callback to remove.</param>
        public void RemoveOnInit(InitConnector onInit)
        {
            if (onInit != null && Connector != null)
            {
                Connector.OnInit -= onInit;
            }
        }

        /// <inheritdoc />
        public override async Task InitAsync(CancellationToken ct = default)
        {
            if (Connector != null)
            {
                await Task.Run(() => Connector.DoInit(), ct);
            }
        }

        /// <inheritdoc />
        public override async Task DestroyAsync(CancellationToken ct = default)
        {
            if (Connector != null)
            {
                await Task.Run(() => Connector.DropTable(new[] { typeof(T) }), ct);
            }
        }

        #endregion

        #region Core CRUD Operations - Single Item

        /// <inheritdoc />
        public override async Task<Guid> CreateAsync(T data, StoreDataDelegate<T>? processDelegate = null, CancellationToken ct = default)
        {
            if (Connector == null || data == null)
            {
                return Guid.Empty;
            }

            data.Guid ??= Guid.NewGuid();
            processDelegate?.Invoke(data);

            await Task.Run(() => Connector.Insert(data), ct);
            return data.Guid!.Value;
        }

        /// <inheritdoc />
        public override async Task<T?> ReadAsync(Expression<Func<T, bool>>? filter = null, CancellationToken ct = default)
        {
            if (Connector == null)
            {
                return default;
            }

            var results = await Task.Run(() => Connector.Select(typeof(T), filter as LambdaExpression), ct);
            if (results == null)
            {
                return default;
            }

            foreach (var item in results)
            {
                if (item is T typedItem)
                {
                    return typedItem;
                }
            }
            return default;
        }

        /// <inheritdoc />
        public override async Task UpdateAsync(T data, StoreDataDelegate<T>? processDelegate = null, CancellationToken ct = default)
        {
            if (Connector == null || data == null)
            {
                return;
            }

            var conditions = new List<SQL.Conditions.Condition>();

            foreach (var field in SQL.DataBase.GetPrimaryFields(typeof(T)))
            {
                conditions.Add(SQL.DataBase.CreateCondition(field, data));
            }

            processDelegate?.Invoke(data);

            await Task.Run(() => Connector.Update(data, conditions), ct);
        }

        /// <inheritdoc />
        public override async Task DeleteAsync(T data, CancellationToken ct = default)
        {
            if (Connector == null || data == null)
            {
                return;
            }

            var conditions = new List<SQL.Conditions.Condition>();
            foreach (var field in SQL.DataBase.GetPrimaryFields(typeof(T)))
            {
                conditions.Add(SQL.DataBase.CreateCondition(field, data));
            }

            await Task.Run(() => Connector.Delete(typeof(T), conditions), ct);
        }

        #endregion

        #region Query and Count Operations

        /// <inheritdoc />
        public override async Task<long> CountAsync(Expression<Func<T, bool>>? filter = null, CancellationToken ct = default)
        {
            if (Connector == null)
            {
                return 0;
            }

            return await Task.Run(() => Connector.SelectCount(typeof(T), filter), ct);
        }

        #endregion
    }
}
