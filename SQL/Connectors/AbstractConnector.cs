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
        // TASK-254 extracted the keyed / transition-fired / clearable / locked / ordered bookkeeping into
        // SchemaEnsureFailureLog so there is ONE implementation of it, not two: the hypertable channel on
        // TimescaleDBConnector needs the identical behaviour. This surface is unchanged -- IndexCreationFailure,
        // IndexCreationFailures, OnIndexCreationFailed, Record*, Clear* all keep their exact signatures and
        // semantics, because a consumer depends on them by name (measured: Symbio's production code, tests,
        // specs and CLAUDE.md).
        private readonly SchemaEnsureFailureLog<IndexCreationFailure> _indexCreationFailures =
            new(f => f.TableName + "\u0000" + (f.IndexName ?? string.Empty));

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
            // Ordered so a host's startup report is stable rather than dictionary-dependent. The sort key
            // combines table and index exactly as the pre-TASK-254 OrderBy/ThenBy pair did.
            get => _indexCreationFailures.Snapshot;
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
            // Record returns true only on the TRANSITION into failure -- an event per attempt would fire on
            // every HTTP request for a per-request store over an unbuildable index.
            if (_indexCreationFailures.Record(IndexFailureKey(tableName, indexName), failure))
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
            _indexCreationFailures.Clear(IndexFailureKey(tableName, indexName));
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

        /// <summary>
        /// The shared body of every provider's <c>OnException</c> handler: ensure the schema if the failure
        /// looks like a missing table, and then <b>always report the failure</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// TASK-277. All four handlers previously answered a missing table with <c>DoInit()</c> and a
        /// <b>return</b> — so the statement was discarded and the caller was told it had succeeded. For a
        /// write that means the row is silently gone: measured on SQLite as <c>CreateAsync</c> returning a
        /// non-empty <c>Guid</c> against a table that does not exist and is not created.
        /// </para>
        /// <para>
        /// <b>Why the recovery it looked like never existed.</b> <c>DoInit()</c> raises the
        /// <c>OnInit</c> event and nothing in the framework subscribes to it — only a consumer can, through
        /// <c>IDataBaseRepository.AddOnInit</c> — and the failed statement is never retried either way. So
        /// the branch could not repair anything even in principle; it could only hide the failure.
        /// </para>
        /// <para>
        /// <c>DoInit()</c> is still called, deliberately: a consumer that registered a handler gets its
        /// schema ensured, so the caller's <i>next</i> attempt can succeed. What changed is that this
        /// attempt is now reported instead of being dropped.
        /// </para>
        /// <para>
        /// <b>This does not touch the read contract.</b> A missing table on a read is handled in
        /// <c>RunReaderCommandOn</c> — <c>catch (Exception ex) when (IsMissingTableException(ex))</c> →
        /// <c>yield break</c> — which never reaches here. TASK-211 narrowed *which* errors count as a
        /// missing table; whether an empty result is the right answer for a read is its decision, and it
        /// keeps its stated callers (lazy create-on-first-use, view-existence probing, CR-M149).
        /// </para>
        /// </remarks>
        protected void EnsureSchemaAndReport(Exception ex, string? commandText)
        {
            if (!IsInitializing && IsMissingTableException(ex))
            {
                DoInit();
            }
            throw new Exception(commandText, ex);
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
        /// Checked before the serialization gate because a command joining a caller's own connection needs no
        /// mutual exclusion against commands on other connections — taking the gate there is how a
        /// boundary holder and the gate holder would deadlock on each other.
        /// </remarks>
        protected AmbientSqlTransaction.Entry? AmbientTransaction
            => AmbientSqlTransaction.Find(_settings?.GetId());

        /// <summary>
        /// Whether DDL issued right now would survive a rollback of the ambient boundary — i.e. whether a
        /// schema-ensure performed in this flow is durable.
        /// </summary>
        /// <remarks>
        /// TASK-244, and it is deliberately expressed from the same two facts <see cref="DoDdlCommand"/>
        /// consults, so the two cannot disagree:
        /// <list type="bullet">
        /// <item>no ambient boundary — nothing can roll the DDL back, so it is durable;</item>
        /// <item>an ambient boundary on a provider whose DDL is <b>not</b> transactional (MySQL alone) —
        /// <see cref="DoDdlCommand"/> suppresses the ambient and the statement commits on its own, so it is
        /// durable, and TASK-243 has a test pinning exactly that;</item>
        /// <item>an ambient boundary on PostgreSQL / SQL Server / SQLite — the DDL is part of the boundary
        /// and a rollback removes it, so it is <b>not</b> durable.</item>
        /// </list>
        /// The store consumes this through <c>CanRememberInitialization</c> so that a schema-ensure which
        /// can still be rolled back does not leave the store believing it is initialised.
        /// </remarks>
        public bool DdlSurvivesRollback => AmbientTransaction == null || !SupportsTransactionalDdl;

        public virtual void DoCommand(Action<DbCommand> createCommand, Action<DbCommand> executeCommand, bool isLock = false)
        {
            var ambient = AmbientTransaction;
            if (ambient != null)
            {
                RunCommandOn(ambient.Connection, ambient.Transaction, createCommand, executeCommand);
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

        // TASK-259 deleted the ExternalConnection/ExternalTransaction pair and its SetExternalTransaction
        // setter that used to live here. They were this framework's first answer to "run the connector's own
        // SQL inside a transaction the caller owns", and the answer was stored in the wrong place: connectors
        // are cached process-wide per (type, settings id) by DataBase.GetConnector, so a per-caller,
        // per-operation fact became shared, long-lived state. Concurrent callers saw each other's
        // transaction, and a caller that finished without clearing it left a disposed connection behind for
        // everyone — measured on SQLite as a store whose lazy schema-ensure threw and which then stayed
        // permanently uninitialised.
        //
        // AmbientSqlTransaction (TASK-240) is the replacement and the only mechanism now: it lives in an
        // AsyncLocal cell, is keyed by settings id, nests as a stack and restores on dispose, so a boundary
        // cannot outlive the flow that entered it. Both stores moved to it then; SqlSchemaBuilder was the
        // last holdout and moved in TASK-259, which left this pair with zero callers.
        //
        // Deleted rather than left in place (TASK-247's rule: a mechanism nobody can reach is not a safety
        // net, it is a second implementation that drifts) after measuring 0 uses across all 16 consumer
        // repos. Do not reintroduce it: putting per-operation state on a cached connector is the defect, not
        // the spelling.

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
        /// Runs one DDL statement — the funnel every schema emitter goes through, and the only place that
        /// consults <see cref="AbstractConnectorBase.SupportsTransactionalDdl"/>.
        /// </summary>
        /// <remarks>
        /// Identical to <see cref="DoCommandWithTransaction"/> on a provider with transactional DDL. Where
        /// DDL is <b>not</b> transactional the statement is issued with the ambient boundary suppressed, so
        /// it runs on a connection of its own and the caller's transaction is left intact.
        /// <para>
        /// <b>Why this is a funnel and not a fix at the store.</b> The defect (TASK-243) was found through
        /// lazy schema-ensure, but nothing about it is specific to schema-ensure: on MySQL <i>any</i> DDL
        /// reaching the boundary's connection implicitly commits it, so <c>CreateTable</c>, the index
        /// emitters, <c>DropTable</c>, the two <c>ALTER</c>s and the view DDL are all the same defect
        /// wearing different statements. Guarding the emitters instead of the caller means a new schema
        /// emitter is correct without being told.
        /// </para>
        /// <para>
        /// There is nothing else to suppress. The legacy ExternalConnection/ExternalTransaction pair that
        /// this paragraph used to carve out was deleted in TASK-259 once <c>SqlSchemaBuilder</c> — its last
        /// caller — moved onto <see cref="AmbientSqlTransaction"/>, so a migration's DDL is now an ambient
        /// boundary like any other and is suppressed here on exactly the same terms.
        /// </para>
        /// </remarks>
        /// <param name="createCommand">Builds the statement.</param>
        /// <param name="executeCommand">Executes it.</param>
        /// <param name="isLock">Passed through to the serialization gate, unchanged.</param>
        /// <param name="inOwnTransaction">
        /// Whether the statement runs in a transaction of its own (<see cref="DoCommandWithTransaction"/>)
        /// or autocommitted (<see cref="DoCommand"/>). Each emitter passes what it already did — the base
        /// <c>CreateTable</c> wraps, the provider overrides of it do not — because this change is about
        /// <i>which connection</i> DDL runs on, not about giving it atomicity it never had. On a provider
        /// with non-transactional DDL the wrapper transaction is a fiction anyway: the statement commits it
        /// on the way in.
        /// </param>
        protected void DoDdlCommand(Action<DbCommand> createCommand, Action<DbCommand> executeCommand, bool isLock = false, bool inOwnTransaction = true)
        {
            if (SupportsTransactionalDdl)
            {
                RunDdl(createCommand, executeCommand, isLock, inOwnTransaction);
                return;
            }
            using var _suppressed = AmbientSqlTransaction.Suppress();
            RunDdl(createCommand, executeCommand, isLock, inOwnTransaction);
        }

        private void RunDdl(Action<DbCommand> createCommand, Action<DbCommand> executeCommand, bool isLock, bool inOwnTransaction)
        {
            if (inOwnTransaction)
            {
                DoCommandWithTransaction(createCommand, executeCommand, isLock);
                return;
            }
            DoCommand(createCommand, executeCommand, isLock);
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
        /// There is now exactly <b>one</b> door into "participate in somebody else's transaction", so the
        /// two-doors-must-agree paragraph that stood here is moot: TASK-259 deleted the legacy
        /// ExternalConnection/ExternalTransaction pair after its last caller moved to the ambient.
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

        private IEnumerable<IEnumerable<object>> RunReaderCommandOn(DbConnection connection, DbTransaction transaction, Action<DbCommand> createCommand, Func<DbDataReader, IEnumerable<object>> transformFunction)
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
                foreach (var item in RunReaderCommandOn(ambient.Connection, ambient.Transaction, createCommand, transformFunction))
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
