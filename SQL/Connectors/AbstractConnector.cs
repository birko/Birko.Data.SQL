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

    /// <summary>
    /// An index declared on an entity that could not be created during schema-ensure, together with the
    /// error that prevented it. Almost always a UNIQUE index over data that already violates it.
    /// </summary>
    public sealed class IndexCreationFailure
    {
        public IndexCreationFailure(string tableName, string? indexName, Exception error)
        {
            TableName = tableName;
            IndexName = indexName;
            Error = error;
        }

        public string TableName { get; }
        public string? IndexName { get; }
        public Exception Error { get; }

        public override string ToString()
            => $"index '{IndexName ?? "(unnamed)"}' on table '{TableName}': {Error.Message}";
    }

    public abstract partial class AbstractConnector : AbstractConnectorBase
    {
        public event InitConnector OnInit = null!;
        public event OnException? OnException;
        public event OnExecute? OnExecute;

        // Keyed by (table, index), NOT a list, and it is CURRENT STATE rather than a log of attempts.
        //
        // Connectors are cached process-wide per (connector type, settings id) in DataBase.GetConnector,
        // while the `_initialized` flag that gates schema-ensure lives on the STORE. A web app resolving a
        // scoped store per request therefore re-runs schema-ensure per request, against one shared
        // connector: an append-only list grew by one entry per request forever, on a process-lifetime
        // object, for as long as the index stayed unbuildable.
        //
        // The re-attempt itself is deliberately KEPT — it is what lets the index appear on its own once an
        // operator repairs the offending rows, with no restart. Only the bookkeeping is deduplicated.
        private readonly Dictionary<string, IndexCreationFailure> _indexCreationFailures =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _indexFailureLock = new();

        private static string IndexFailureKey(string tableName, string? indexName)
            => tableName + "\u0000" + (indexName ?? string.Empty);

        /// <summary>
        /// Indexes that could not be created on their most recent schema-ensure attempt. Empty in the
        /// normal case.
        /// </summary>
        /// <remarks>
        /// Current state, not history: an index that later builds successfully (because the data blocking
        /// it was repaired) drops out of this collection, and a given index appears at most once no matter
        /// how many times schema-ensure has run. An empty collection is NOT proof that every declared index
        /// exists — a store is initialised lazily on first access, so an entity that has not been touched
        /// yet has not attempted its indexes.
        /// </remarks>
        public IReadOnlyList<IndexCreationFailure> IndexCreationFailures
        {
            get
            {
                lock (_indexFailureLock)
                {
                    // Ordered so a host's startup report is stable rather than dictionary-dependent.
                    return _indexCreationFailures.Values
                        .OrderBy(x => x.TableName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.IndexName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
            }
        }

        /// <summary>
        /// Raised when a declared index could not be created during schema-ensure. Subscribe to log or
        /// escalate; the store initialises regardless.
        /// </summary>
        /// <remarks>
        /// Fires on the TRANSITION into failure, not on every attempt — otherwise a per-request store over
        /// an unbuildable index would raise this on every HTTP request. If the index later builds and then
        /// fails again, that is a new transition and raises again.
        /// </remarks>
        public event Action<IndexCreationFailure>? OnIndexCreationFailed;

        /// <summary>
        /// Records an index that schema-ensure could not build, and notifies any subscriber the first time
        /// that index enters the failed state.
        /// </summary>
        /// <remarks>
        /// Deliberately does NOT rethrow — see <c>CreateTable(IEnumerable&lt;Tables.Table&gt;)</c> for why
        /// an unbuildable index must not take the table's whole read surface with it.
        /// </remarks>
        protected void RecordIndexCreationFailure(string tableName, string? indexName, Exception error)
        {
            var failure = new IndexCreationFailure(tableName, indexName, error);
            bool isNew;
            lock (_indexFailureLock)
            {
                var key = IndexFailureKey(tableName, indexName);
                isNew = !_indexCreationFailures.ContainsKey(key);
                // Always overwrite: the latest error is the one that describes the current state.
                _indexCreationFailures[key] = failure;
            }
            if (isNew)
            {
                OnIndexCreationFailed?.Invoke(failure);
            }
        }

        /// <summary>
        /// Clears any recorded failure for an index that has now been created successfully, so
        /// <see cref="IndexCreationFailures"/> cannot report a condition an operator has already repaired.
        /// </summary>
        protected void ClearIndexCreationFailure(string tableName, string? indexName)
        {
            lock (_indexFailureLock)
            {
                _indexCreationFailures.Remove(IndexFailureKey(tableName, indexName));
            }
        }

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

        /// <summary>
        /// The ambient transaction boundary covering THIS connector's database, or null.
        /// </summary>
        /// <remarks>
        /// Checked before <see cref="ExternalConnection"/> because it is the more specific answer, and
        /// before the serialization gate because a command joining a caller's own connection needs no
        /// mutual exclusion against commands on other connections — taking the gate there is how a
        /// boundary holder and the gate holder would deadlock on each other.
        /// </remarks>
        protected AmbientSqlTransaction.Entry? AmbientTransaction
            => AmbientSqlTransaction.Find(_settings?.GetId());

        public virtual void DoCommand(Action<DbCommand> createCommand, Action<DbCommand> executeCommand, bool isLock = false)
        {
            var ambient = AmbientTransaction;
            if (ambient != null)
            {
                RunCommandOn(ambient.Connection, ambient.Transaction, createCommand, executeCommand);
                return;
            }
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
            // Inside a boundary this must NOT open a nested transaction and must NOT commit or roll
            // back — the owner commits. A committed inner transaction inside an outer one that later
            // rolls back is partial application reporting green.
            var ambient = AmbientTransaction;
            if (ambient != null)
            {
                RunCommandOn(ambient.Connection, ambient.Transaction, createCommand, executeCommand);
                return;
            }
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

        /// <summary>
        /// Runs a multi-statement bulk write on the ambient boundary when this flow is inside one, and on its
        /// own connection and transaction when it is not. Synchronous twin of
        /// <c>AbstractAsyncConnector.RunBulkAsync</c>.
        /// </summary>
        /// <param name="label">Command text used for retry logging and for <see cref="InitException"/>.</param>
        /// <param name="body">
        /// Receives the connection, the transaction, and whether it <b>owns</b> them. When it does not own
        /// them it must not commit, roll back, or dispose either one — the boundary owner does that.
        /// </param>
        /// <param name="retryWhenOwned">
        /// Whether the own-connection path is wrapped in <see cref="AbstractConnectorBase.ExecuteWithRetry"/>.
        /// Each provider's shipped bulk path is preserved as it was: SQLite retries (CR-M144), PostgreSQL,
        /// MySQL and MSSql never did. The participating path never retries whatever this says.
        /// </param>
        /// <remarks>
        /// <b>Why the sync half matters just as much.</b> <c>DataBaseStore.EnterTransactionScope</c> publishes
        /// a sync store's transaction context into <see cref="AmbientSqlTransaction"/> exactly as the async
        /// store does, so sync single-row writes already honoured a boundary while sync bulk writes opened a
        /// second connection and escaped it — the same asymmetry, on the same store.
        /// <para>
        /// <b>The participating path is deliberately NOT wrapped in <c>ExecuteWithRetry</c>.</b> A retry would
        /// re-run statements inside a transaction whose earlier statements already succeeded, and on most
        /// providers the first failure has already aborted it, so the retry can only fail differently.
        /// Retrying is the boundary owner's decision — the same reasoning <see cref="RunCommandOn"/> already
        /// applies to single commands.
        /// </para>
        /// <para>
        /// The legacy <see cref="ExternalConnection"/>/<see cref="ExternalTransaction"/> pair is honoured
        /// second, exactly as <see cref="DoCommand"/> does, so the two doors into "participate in somebody
        /// else's transaction" cannot disagree for bulk writes when they already agree for single ones.
        /// </para>
        /// </remarks>
        protected void RunBulk(
            string label,
            Action<DbConnection, DbTransaction, bool> body,
            bool retryWhenOwned = true)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            // `transaction!` is provably non-null here: ownTransaction: true means the owned path begins one,
            // and every participating path hands over an existing (never-null) transaction.
            RunBulkCore(label, (connection, transaction, owned) => body(connection, transaction!, owned),
                        ownTransaction: true, retryWhenOwned);
        }

        /// <summary>
        /// The same decision as <see cref="RunBulk"/> for a bulk write that carries its <b>own</b> atomicity
        /// and therefore wants a connection but no transaction of its own — PostgreSQL's binary <c>COPY</c>
        /// and <c>SqlBulkCopy</c>. The body receives a null transaction when it owns the connection.
        /// </summary>
        protected void RunBulkOnConnection(
            string label,
            Action<DbConnection, DbTransaction?, bool> body,
            bool retryWhenOwned = true)
            => RunBulkCore(label, body, ownTransaction: false, retryWhenOwned);

        private void RunBulkCore(
            string label,
            Action<DbConnection, DbTransaction?, bool> body,
            bool ownTransaction,
            bool retryWhenOwned)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            var ambient = AmbientTransaction;
            if (ambient != null)
            {
                body(ambient.Connection, ambient.Transaction, false);
                return;
            }
            if (ExternalConnection != null && ExternalTransaction != null)
            {
                body(ExternalConnection, ExternalTransaction, false);
                return;
            }

            void Owned()
            {
                using var connection = CreateConnection(_settings);
                connection.Open();
                if (!ownTransaction)
                {
                    body(connection, null, true);
                    return;
                }
                using var transaction = connection.BeginTransaction();
                body(connection, transaction, true);
            }

            if (retryWhenOwned)
            {
                ExecuteWithRetry(Owned, label);
                return;
            }
            Owned();
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
                catch (Exception ex) when (IsMissingTableException(ex)) { yield break; }
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
            => RunCommandOn(ExternalConnection!, ExternalTransaction!, createCommand, executeCommand);

        /// <summary>
        /// Runs one command on a connection and transaction owned by somebody else.
        /// </summary>
        /// <remarks>
        /// Opens nothing, commits nothing, and disposes nothing but the command: the caller's connection
        /// must outlive the operation, and disposing it mid-transaction is the failure this pattern
        /// invites. Shared by the ambient boundary and the legacy external-transaction pair so the two
        /// doors cannot disagree about what participating in a transaction means.
        /// </remarks>
        private void RunCommandOn(DbConnection connection, DbTransaction transaction, Action<DbCommand> createCommand, Action<DbCommand> executeCommand)
        {
            string? commandText = null;
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
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

            // A read inside a boundary must run on the boundary's connection, or it cannot see the
            // boundary's own uncommitted writes — read-then-write service logic would get a stale
            // snapshot, which is a wrong answer rather than a missing feature.
            var ambient = AmbientTransaction;
            if (ambient != null)
            {
                foreach (var item in RunReaderCommandWithExternalTransaction(ambient.Connection, ambient.Transaction, createCommand, transformFunction))
                    yield return item;
                yield break;
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
                catch (Exception ex) when (IsMissingTableException(ex)) { yield break; }
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
