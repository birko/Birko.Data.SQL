using Birko.Data.SQL.Connectors;
using Birko.Data.Stores;
using Birko.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Birko.Data.SQL.Stores
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

        /// <summary>
        /// Gets the connector as an async connector for native async operations.
        /// Returns null if the connector does not support native async.
        /// </summary>
        protected AbstractAsyncConnector? AsyncConnector => Connector as AbstractAsyncConnector;

        /// <inheritdoc />
        public SqlTransactionContext? TransactionContext { get; private set; }

        /// <summary>
        /// Sets the transaction this store's operations participate in, or null to clear.
        /// </summary>
        /// <remarks>
        /// This is store INSTANCE state — matching <c>AsyncMongoDBStore</c>, <c>AsyncRavenDBStore</c> and
        /// <c>AsyncCosmosDBStore</c> — and is therefore safe only while the store itself is per-scope.
        /// It deliberately no longer calls <c>Connector.SetExternalTransaction</c>: connectors are cached
        /// process-wide per (type, settings id), so that call published one caller's transaction to every
        /// concurrent caller against the same database.
        /// <para>
        /// For a singleton store, or for a boundary spanning several stores, prefer
        /// <see cref="SqlUnitOfWork"/>, which publishes an <see cref="AmbientSqlTransaction"/> scope that
        /// travels with the async flow and needs no per-store call.
        /// </para>
        /// </remarks>
        public void SetTransactionContext(SqlTransactionContext? context)
        {
            TransactionContext = context;
        }

        /// <summary>
        /// Publishes <see cref="TransactionContext"/> for the duration of one operation, so the connector
        /// runs it on that transaction. Returns null (and costs nothing) when no context is set.
        /// </summary>
        /// <remarks>
        /// Routing the per-store door through the same ambient mechanism the unit of work uses is what
        /// stops the two disagreeing about what "inside a transaction" means.
        /// </remarks>
        protected IDisposable? EnterTransactionScope()
        {
            var context = TransactionContext;
            if (context == null || Connector == null)
            {
                return null;
            }
            return AmbientSqlTransaction.Enter(Connector.Settings.GetId(), context.Connection, context.Transaction);
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

        protected override async Task InitCoreAsync(CancellationToken ct = default)
        {
            if (Connector == null) return;
            await Task.Run(() => Connector.CreateTable(new[] { typeof(T) }), ct).ConfigureAwait(false);
            if (AsyncConnector != null)
                await AsyncConnector.DoInitAsync(ct);
            else
                await Task.Run(() => Connector.DoInit(), ct);
        }

        /// <inheritdoc />
        public override async Task DestroyAsync(CancellationToken ct = default)
        {
            if (Connector == null) return;
            if (AsyncConnector != null)
                await AsyncConnector.DropTableAsync(new[] { typeof(T) }, ct);
            else
                await Task.Run(() => Connector.DropTable(new[] { typeof(T) }), ct);
        }

        #endregion

        #region Core CRUD Operations - Single Item

        protected override async Task<Guid> CreateCoreAsync(T data, StoreDataDelegate<T>? processDelegate = null, CancellationToken ct = default)
        {
            if (Connector == null || data == null) return Guid.Empty;
            using var _tx = EnterTransactionScope();

            data.Guid ??= Guid.NewGuid();
            processDelegate?.Invoke(data);

            if (AsyncConnector != null)
                await AsyncConnector.InsertAsync(data, ct);
            else
                await Task.Run(() => Connector!.Insert(data), ct);
            return data.Guid!.Value;
        }

        protected override async Task<T?> ReadCoreAsync(Expression<Func<T, bool>>? filter = null, CancellationToken ct = default)
        {
            if (Connector == null) return default;
            using var _tx = EnterTransactionScope();

            if (AsyncConnector != null)
            {
                await foreach (var item in AsyncConnector.SelectAsync(typeof(T), filter as LambdaExpression, null, 1, null, ct))
                {
                    if (item is T typed) return typed;
                }
                return default;
            }

            var results = await Task.Run(() => Connector.Select(typeof(T), filter as LambdaExpression), ct);
            if (results == null) return default;

            foreach (var item in results)
            {
                if (item is T typedItem)
                {
                    return typedItem;
                }
            }
            return default;
        }

        protected override async Task UpdateCoreAsync(T data, StoreDataDelegate<T>? processDelegate = null, CancellationToken ct = default)
        {
            if (Connector == null || data == null) return;
            using var _tx = EnterTransactionScope();

            var conditions = new List<SQL.Conditions.Condition>();

            foreach (var field in SQL.DataBase.GetPrimaryFields(typeof(T)))
            {
                conditions.Add(SQL.DataBase.CreateCondition(field, data));
            }

            processDelegate?.Invoke(data);

            if (AsyncConnector != null)
                await AsyncConnector.UpdateAsync(data, conditions, ct);
            else
                await Task.Run(() => Connector!.Update(data, conditions), ct);
        }

        protected override async Task DeleteCoreAsync(T data, CancellationToken ct = default)
        {
            if (Connector == null || data == null) return;
            using var _tx = EnterTransactionScope();

            var conditions = new List<SQL.Conditions.Condition>();
            foreach (var field in SQL.DataBase.GetPrimaryFields(typeof(T)))
            {
                conditions.Add(SQL.DataBase.CreateCondition(field, data));
            }

            if (AsyncConnector != null)
                await AsyncConnector.DeleteAsync(typeof(T), conditions, ct);
            else
                await Task.Run(() => Connector!.Delete(typeof(T), conditions), ct);
        }

        #endregion

        #region Query and Count Operations

        protected override async Task<long> CountCoreAsync(Expression<Func<T, bool>>? filter = null, CancellationToken ct = default)
        {
            if (Connector == null) return 0;
            using var _tx = EnterTransactionScope();

            if (AsyncConnector != null)
                return await AsyncConnector.SelectCountAsync(typeof(T), filter, ct);
            return await Task.Run(() => Connector!.SelectCount(typeof(T), filter), ct);
        }

        #endregion
    }
}
