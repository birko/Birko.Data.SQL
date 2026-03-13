using Birko.Data.SQL.Connectors;
using Birko.Data.SQL.Stores;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Birko.Data.Stores
{
    /// <summary>
    /// Basic database store for single-item CRUD operations.
    /// Provides core database functionality without bulk operations.
    /// For bulk operations, use <see cref="DataBaseBulkStore{DB, T}"/> instead.
    /// </summary>
    /// <typeparam name="DB">The type of database connector, must inherit from <see cref="AbstractConnector"/>.</typeparam>
    /// <typeparam name="T">The type of entity, must inherit from <see cref="Models.AbstractModel"/>.</typeparam>
    public class DataBaseStore<DB, T>
        : AbstractStore<T>
        , ISettingsStore<ISettings>
        , ISettingsStore<PasswordSettings>
        , ITransactionalStore<T, SqlTransactionContext>
        where T : Models.AbstractModel
        where DB : AbstractConnector
    {
        /// <summary>
        /// Gets the database connector for this store.
        /// </summary>
        public DB Connector { get; protected set; } = null!;

        /// <inheritdoc />
        public SqlTransactionContext? TransactionContext { get; private set; }

        /// <inheritdoc />
        public void SetTransactionContext(SqlTransactionContext? context)
        {
            TransactionContext = context;
            Connector?.SetExternalTransaction(context?.Connection, context?.Transaction);
        }

        /// <summary>
        /// Initializes a new instance of the DataBaseStore class.
        /// </summary>
        public DataBaseStore()
        {
        }

        /// <summary>
        /// Sets the connection settings using PasswordSettings.
        /// </summary>
        /// <param name="settings">The password settings containing connection information.</param>
        public virtual void SetSettings(PasswordSettings settings)
        {
            SetSettings((ISettings)settings);
        }

        /// <summary>
        /// Sets the connection settings.
        /// </summary>
        /// <param name="settings">The settings to use for database connection.</param>
        public virtual void SetSettings(ISettings settings)
        {
            if (settings is PasswordSettings sets)
            {
                Connector = (DB)SQL.DataBase.GetConnector<DB>(sets);
            }
        }

        /// <summary>
        /// Adds an initialization callback to the connector.
        /// </summary>
        /// <param name="onInit">The callback to invoke during initialization.</param>
        public void AddOnInit(InitConnector onInit)
        {
            if (onInit != null && Connector != null)
            {
                Connector.OnInit += onInit;
            }
        }

        /// <summary>
        /// Removes an initialization callback from the connector.
        /// </summary>
        /// <param name="onInit">The callback to remove.</param>
        public void RemoveOnInit(InitConnector onInit)
        {
            if (onInit != null && Connector != null)
            {
                Connector.OnInit -= onInit;
            }
        }

        #region Initialization and Lifecycle

        /// <inheritdoc />
        public override void Init()
        {
            Connector?.DoInit();
        }

        /// <inheritdoc />
        public override void Destroy()
        {
            Connector?.DropTable(new[] { typeof(T) });
        }

        #endregion

        #region Core CRUD Operations - Single Item

        /// <inheritdoc />
        public override Guid Create(T data, StoreDataDelegate<T>? storeDelegate = null)
        {
            data.Guid ??= Guid.NewGuid();
            storeDelegate?.Invoke(data);
            Connector.Insert(data);
            return data.Guid!.Value;
        }

        /// <inheritdoc />
        public override T? Read(Expression<Func<T, bool>>? filter = null)
        {
            return Connector?.Select(typeof(T), filter as LambdaExpression, null, 1, null)?.OfType<T>().FirstOrDefault();
        }

        /// <inheritdoc />
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

        /// <inheritdoc />
        public override void Delete(T data)
        {
            if (data == null) return;

            List<SQL.Conditions.Condition> conditions = new List<SQL.Conditions.Condition>();
            foreach (var field in SQL.DataBase.GetPrimaryFields(typeof(T)))
            {
                conditions.Add(SQL.DataBase.CreateCondition(field, data));
            }
            Connector.Delete(typeof(T), conditions);
        }

        #endregion

        #region Query and Count Operations

        /// <inheritdoc />
        public override long Count(Expression<Func<T, bool>>? filter = null)
        {
            return Connector?.SelectCount(typeof(T), filter) ?? 0;
        }

        #endregion
    }
}
