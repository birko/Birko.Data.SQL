using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using Birko.Data.SQL.Connectors.Strategies;
using PasswordSettings = Birko.Configuration.PasswordSettings;

namespace Birko.Data.SQL.Connectors
{
    public delegate void InitConnector(AbstractConnector connector);
    public delegate void OnException(Exception ex, string? commandText);
    public delegate void OnExecute(string commandText);

    public abstract partial class AbstractConnector : AbstractConnectorBase
    {
        public event InitConnector OnInit = null!;
        public event OnException? OnException;
        public event OnExecute? OnExecute;

        public AbstractConnector(PasswordSettings settings) : base(settings)
        {
        }

        /// <summary>
        /// Invokes the OnExecute event. Can be called from derived classes.
        /// </summary>
        protected void InvokeOnExecute(string commandText) => OnExecute?.Invoke(commandText);

        public virtual void InitException(Exception ex, string? commandText)
        {
            if (OnException != null)
            {
                OnException.Invoke(ex, commandText);
            }
            else
            {
                throw ex;
            }
        }

        public void DoInit()
        {
            if (!IsInitializing)
            {
                IsInitializing = true;
                OnInit?.Invoke(this);
                IsInitializing = false;
            }
        }

        public virtual void DoCommand(Action<DbCommand> createCommand, Action<DbCommand> executeCommand, bool isLock = false)
        {
            if (ExternalConnection != null && ExternalTransaction != null)
            {
                RunCommandWithExternalTransaction(createCommand, executeCommand);
                return;
            }
            if (!isLock)
            {
                RunCommand(createCommand, executeCommand);
            }
            else
            {
                lock (_lock)
                {
                    RunCommand(createCommand, executeCommand);
                }
            }
        }

        /// <summary>
        /// External transaction context. When set, DoCommandWithTransaction and DoCommand
        /// use this connection/transaction instead of creating their own.
        /// </summary>
        public DbConnection? ExternalConnection { get; private set; }
        public DbTransaction? ExternalTransaction { get; private set; }

        /// <summary>
        /// Sets or clears the external transaction context.
        /// When set, all operations participate in this transaction.
        /// </summary>
        public void SetExternalTransaction(DbConnection? connection, DbTransaction? transaction)
        {
            ExternalConnection = connection;
            ExternalTransaction = transaction;
        }

        public virtual void DoCommandWithTransaction(Action<DbCommand> createCommand, Action<DbCommand> executeCommand, bool isLock = false)
        {
            if (ExternalConnection != null && ExternalTransaction != null)
            {
                RunCommandWithExternalTransaction(createCommand, executeCommand);
                return;
            }
            if (!isLock)
            {
                RunCommandTransaction(createCommand, executeCommand);
            }
            else
            {
                lock (_lock)
                {
                    RunCommandTransaction(createCommand, executeCommand);
                }
            }
        }

        private IEnumerable<IEnumerable<object>> RunReaderCommandWithExternalTransaction(DbConnection connection, DbTransaction transaction, Action<DbCommand> createCommand, Func<DbDataReader, IEnumerable<object>> transformFunction)
        {
            string? commandText = null;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                bool faulted = false;
                try
                {
                    createCommand?.Invoke(command);
                    commandText = DataBase.GetGeneratedQuery(command);
                    OnExecute?.Invoke(command.CommandText);
                }
                catch (Exception ex) { InitException(ex, commandText); faulted = true; } // CR-M134
                if (faulted) yield break;
                DbDataReader reader;
                try { reader = command.ExecuteReader(); }
                catch (Exception ex) when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase)) { yield break; }
                using var _r = reader;
                if (!(reader?.HasRows ?? false)) yield break;
                bool isNext = false;
                try { isNext = reader.Read(); }
                catch (Exception ex) { InitException(ex, commandText); yield break; }
                while (isNext)
                {
                    IEnumerable<object>? row = null;
                    try { row = transformFunction.Invoke(reader); }
                    catch (Exception ex) { InitException(ex, commandText); }
                    if (row == null) yield break;
                    yield return row;
                    try { isNext = reader.Read(); }
                    catch (Exception ex) { InitException(ex, commandText); isNext = false; }
                }
            }
        }

        private void RunCommandWithExternalTransaction(Action<DbCommand> createCommand, Action<DbCommand> executeCommand)
        {
            string? commandText = null;
            try
            {
                using (var command = ExternalConnection!.CreateCommand())
                {
                    command.Transaction = ExternalTransaction;
                    createCommand?.Invoke(command);
                    commandText = DataBase.GetGeneratedQuery(command);
                    OnExecute?.Invoke(commandText);
                    executeCommand?.Invoke(command);
                }
            }
            catch (Exception ex)
            {
                InitException(ex, commandText);
            }
        }

        private void RunCommandTransaction(Action<DbCommand> createCommand, Action<DbCommand> executeCommand)
        {
            ExecuteWithRetry(() =>
            {
                using var db = CreateConnection(_settings);
                db.Open();
                using var transaction = db.BeginTransaction();
                string? commandText = null;
                try
                {
                    using (var command = db.CreateCommand())
                    {
                        command.Transaction = transaction;
                        createCommand?.Invoke(command);
                        commandText = DataBase.GetGeneratedQuery(command);
                        OnExecute?.Invoke(commandText);
                        executeCommand?.Invoke(command);
                    }
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    InitException(ex, commandText);
                }
                finally
                {
                    db.Close();
                }
            });
        }

        private void RunCommand(Action<DbCommand> createCommand, Action<DbCommand> executeCommand)
        {
            ExecuteWithRetry(() =>
            {
                using var db = CreateConnection(_settings);
                db.Open();
                string? commandText = null;
                try
                {
                    using (var command = db.CreateCommand())
                    {
                        createCommand?.Invoke(command);
                        commandText = DataBase.GetGeneratedQuery(command);
                        OnExecute?.Invoke(commandText);
                        executeCommand?.Invoke(command);
                    }
                }
                catch (Exception ex)
                {
                    InitException(ex, commandText);
                }
                finally
                {
                    db.Close();
                }
            });
        }

        private IEnumerable<IEnumerable<object>> RunReaderCommand(Action<DbCommand> createCommand, Func<DbDataReader, IEnumerable<object>> transformFunction)
        {
            if (transformFunction == null)
            {
                throw new ArgumentNullException(nameof(transformFunction));
            }

            // Use external transaction's connection if available
            if (ExternalConnection != null && ExternalTransaction != null)
            {
                foreach (var item in RunReaderCommandWithExternalTransaction(ExternalConnection, ExternalTransaction, createCommand, transformFunction))
                    yield return item;
                yield break;
            }

            using var db = CreateConnection(_settings);
            db.Open();
            string? commandText = null;
            using (var command = db.CreateCommand())
            {
                bool faulted = false;
                try
                {
                    createCommand?.Invoke(command);
                    commandText = DataBase.GetGeneratedQuery(command);
                    OnExecute?.Invoke(command.CommandText);
                }
                catch (Exception ex)
                {
                    // CR-M134: when an OnException handler is registered InitException returns instead of
                    // rethrowing — do NOT fall through to ExecuteReader on a command that failed to build,
                    // which would raise a second, more confusing error (or run a malformed command).
                    InitException(ex, commandText);
                    faulted = true;
                }
                if (faulted)
                {
                    yield break;
                }
                DbDataReader reader2;
                try { reader2 = command.ExecuteReader(); }
                catch (Exception ex) when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase)) { yield break; }
                using var _r2 = reader2;
                if (!(reader2?.HasRows ?? false))
                {
                    yield break;
                }
                bool isNext = false;
                try
                {
                    isNext = reader2.Read();
                }
                catch (Exception ex)
                {
                    InitException(ex, commandText);
                    yield break;
                }
                while (isNext)
                {
                    IEnumerable<object>? row = null;
                    try
                    {
                        row = transformFunction.Invoke(reader2);
                    }
                    catch (Exception ex)
                    {
                        InitException(ex, commandText);
                    }
                    if (row == null)
                    {
                        yield break;
                    }
                    yield return row;
                    try
                    {
                        isNext = reader2.Read();
                    }
                    catch (Exception ex)
                    {
                        InitException(ex, commandText);
                        isNext = false;
                    }
                }
            }
        }
    }
}
