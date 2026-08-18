using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PasswordSettings = Birko.Configuration.PasswordSettings;

namespace Birko.Data.SQL.Connectors
{
    /// <summary>
    /// Abstract async connector that extends <see cref="AbstractConnector"/> with native async
    /// command execution methods. Inherits all sync methods, events, and external transaction support.
    /// </summary>
    public abstract partial class AbstractAsyncConnector : AbstractConnector
    {
        // Async serialization gate for isLock=true commands. A monitor `lock` cannot span an await,
        // so the old `Task.Run(() => { lock (_lock) { return RunCommandAsync(...); } })` released the
        // lock at the first await — before the DB work ran — providing no mutual exclusion (CR-H082).
        private readonly SemaphoreSlim _asyncLock = new(1, 1);

        public AbstractAsyncConnector(PasswordSettings settings) : base(settings)
        {
        }

        /// <summary>
        /// Runs a multi-statement bulk write on the ambient boundary when this flow is inside one, and on its
        /// own connection and transaction when it is not.
        /// </summary>
        /// <param name="label">Command text used for retry logging and for <c>InitException</c>.</param>
        /// <param name="body">
        /// Receives the connection, the transaction, and whether it <b>owns</b> them. When it does not own
        /// them it must not commit, roll back, or dispose either one — the boundary owner does that.
        /// </param>
        /// <param name="ct">Cancellation token.</param>
        /// <param name="retryWhenOwned">
        /// Whether the <b>own-connection</b> path is wrapped in <see cref="AbstractConnectorBase.ExecuteWithRetryAsync"/>.
        /// Each provider's shipped bulk path is preserved as it was: SQLite retries (CR-M144 — SQLITE_BUSY /
        /// SQLITE_LOCKED are transient and the whole unit rolls back before the next attempt), PostgreSQL,
        /// MySQL and MSSql never did, and this fix is not the place to start. The participating path never
        /// retries whatever this says.
        /// </param>
        /// <remarks>
        /// <b>Why this exists.</b> The bulk paths used to open their own connection and their own transaction
        /// unconditionally, so a bulk write issued inside an <see cref="AmbientSqlTransaction"/> boundary
        /// happened <i>outside</i> it. The symptom differed by provider, and the quiet one was the dangerous
        /// one: on SQLite the second connection cannot take the write lock the boundary already holds, so it
        /// blocked for the full command timeout and then failed; on PostgreSQL and MySQL two connections are
        /// perfectly legal, so the bulk write committed independently and survived the owner's rollback with
        /// no error anywhere. Every collection-shaped repository write routes here, so that was
        /// create-many, update-many, delete-many, delete-where and delete-all silently escaping every
        /// transaction boundary a caller drew.
        /// <para>
        /// <b>The participating path is deliberately NOT wrapped in <c>ExecuteWithRetryAsync</c>.</b> A retry
        /// would re-run statements inside a transaction whose earlier statements already succeeded, and on
        /// most providers the first failure has already aborted the transaction, so the retry can only fail
        /// differently. Retrying is the boundary owner's decision, not this method's — the same reasoning
        /// <c>RunCommandOnAsync</c> already applies to single commands.
        /// </para>
        /// </remarks>
        protected Task RunBulkAsync(
            string label,
            Func<DbConnection, DbTransaction, bool, Task> body,
            CancellationToken ct = default,
            bool retryWhenOwned = true)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            // `transaction!` is provably non-null here: ownTransaction: true means the owned path begins one,
            // and the participating path always hands over the boundary's own (never-null) transaction.
            return RunBulkCoreAsync(label, (connection, transaction, owned) => body(connection, transaction!, owned),
                                    ownTransaction: true, retryWhenOwned, ct);
        }

        /// <summary>
        /// The same decision as <see cref="RunBulkAsync"/> for a bulk write that carries its <b>own</b>
        /// atomicity and therefore wants a connection but no transaction of its own — PostgreSQL's binary
        /// <c>COPY</c> and <c>SqlBulkCopy</c>.
        /// </summary>
        /// <remarks>
        /// The body receives a <b>null</b> transaction when it owns the connection, and the boundary's
        /// transaction when it does not. Both are still one producer for "am I inside a boundary": the two
        /// public shapes differ only in what the owned path acquires, so a COPY-shaped path and a
        /// statement-shaped path cannot disagree about what participating means.
        /// <para>
        /// Wrapping these in an owned transaction instead would have been the smaller diff and a real
        /// behaviour change — <c>COPY</c> and <c>SqlBulkCopy</c> run unwrapped today, and this fix is about
        /// the boundary, not about their standalone atomicity.
        /// </para>
        /// </remarks>
        protected Task RunBulkOnConnectionAsync(
            string label,
            Func<DbConnection, DbTransaction?, bool, Task> body,
            CancellationToken ct = default,
            bool retryWhenOwned = true)
            => RunBulkCoreAsync(label, body, ownTransaction: false, retryWhenOwned, ct);

        private async Task RunBulkCoreAsync(
            string label,
            Func<DbConnection, DbTransaction?, bool, Task> body,
            bool ownTransaction,
            bool retryWhenOwned,
            CancellationToken ct)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            var ambient = AmbientTransaction;
            if (ambient != null)
            {
                await body(ambient.Connection, ambient.Transaction, false).ConfigureAwait(false);
                return;
            }

            async Task Owned()
            {
                using var connection = CreateConnection(_settings);
                await connection.OpenAsync(ct).ConfigureAwait(false);
                if (!ownTransaction)
                {
                    await body(connection, null, true).ConfigureAwait(false);
                    return;
                }
                using var transaction = connection.BeginTransaction();
                await body(connection, transaction, true).ConfigureAwait(false);
            }

            if (retryWhenOwned)
            {
                await ExecuteWithRetryAsync(Owned, ct, label).ConfigureAwait(false);
                return;
            }
            await Owned().ConfigureAwait(false);
        }

        /// <summary>
        /// Async twin of <c>AbstractConnector.DoDdlCommand</c> — see its remarks for why DDL is funnelled
        /// rather than fixed at the caller, and why the legacy external-transaction pair is left alone.
        /// </summary>
        /// <remarks>
        /// The suppression scope wraps the <c>await</c>, which is what makes it visible to the statement:
        /// an <see cref="AsyncLocal{T}"/> write flows to callees. It cannot leak back to this method's
        /// caller even if the scope were not disposed.
        /// </remarks>
        protected async Task DoDdlCommandAsync(Func<DbCommand, Task> createCommand, Func<DbCommand, Task> executeCommand, bool isLock = false, CancellationToken ct = default, bool inOwnTransaction = true)
        {
            if (SupportsTransactionalDdl)
            {
                await RunDdlAsync(createCommand, executeCommand, isLock, inOwnTransaction, ct).ConfigureAwait(false);
                return;
            }
            using var _suppressed = AmbientSqlTransaction.Suppress();
            await RunDdlAsync(createCommand, executeCommand, isLock, inOwnTransaction, ct).ConfigureAwait(false);
        }

        private Task RunDdlAsync(Func<DbCommand, Task> createCommand, Func<DbCommand, Task> executeCommand, bool isLock, bool inOwnTransaction, CancellationToken ct)
            => inOwnTransaction
                ? DoCommandWithTransactionAsync(createCommand, executeCommand, isLock, ct)
                : DoCommandAsync(createCommand, executeCommand, isLock, ct);

        public virtual Task DoInitAsync(CancellationToken ct = default)
        {
            DoInit();
            return Task.CompletedTask;
        }

        public virtual async Task DoCommandAsync(Func<DbCommand, Task> createCommand, Func<DbCommand, Task> executeCommand, bool isLock = false, CancellationToken ct = default)
        {
            // Checked BEFORE the gate: a command joining the caller's own connection needs no mutual
            // exclusion against commands on other connections, and taking the gate here is exactly how
            // a boundary holder and the gate holder would wait on each other.
            var ambient = AmbientTransaction;
            if (ambient != null)
            {
                await RunCommandOnAsync(ambient.Connection, ambient.Transaction, createCommand, executeCommand);
                return;
            }
            if (!isLock)
            {
                await RunCommandAsync(createCommand, executeCommand, ct);
                return;
            }

            // Hold the async gate across the entire awaited DB operation so concurrent callers are
            // actually serialized (CR-H082).
            await _asyncLock.WaitAsync(ct);
            try
            {
                await RunCommandAsync(createCommand, executeCommand, ct);
            }
            finally
            {
                _asyncLock.Release();
            }
        }

        public virtual async Task DoCommandWithTransactionAsync(Func<DbCommand, Task> createCommand, Func<DbCommand, Task> executeCommand, bool isLock = false, CancellationToken ct = default)
        {
            // Inside a boundary: no nested BeginTransactionAsync, no commit, no rollback, no disposal
            // of the caller's connection. The owner commits.
            var ambient = AmbientTransaction;
            if (ambient != null)
            {
                await RunCommandOnAsync(ambient.Connection, ambient.Transaction, createCommand, executeCommand);
                return;
            }
            if (!isLock)
            {
                await RunCommandTransactionAsync(createCommand, executeCommand, ct);
                return;
            }

            await _asyncLock.WaitAsync(ct);
            try
            {
                await RunCommandTransactionAsync(createCommand, executeCommand, ct);
            }
            finally
            {
                _asyncLock.Release();
            }
        }

        /// <summary>
        /// Runs one command asynchronously on a connection and transaction owned by somebody else.
        /// </summary>
        /// <remarks>
        /// The async twin of <c>RunCommandOn</c>. Deliberately NOT wrapped in
        /// <c>ExecuteWithRetryAsync</c>: a retry would re-run a statement inside a transaction whose
        /// earlier statements already succeeded, and on most providers the first failure has already
        /// aborted the transaction, so the retry can only fail differently. Retrying is the boundary
        /// owner's decision, not this method's.
        /// </remarks>
        private async Task RunCommandOnAsync(DbConnection connection, DbTransaction transaction, Func<DbCommand, Task> createCommand, Func<DbCommand, Task> executeCommand)
        {
            string? commandText = null;
            try
            {
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    if (createCommand != null) await createCommand.Invoke(command);
                    commandText = DataBase.GetGeneratedQuery(command);
                    InvokeOnExecute(commandText);
                    if (executeCommand != null) await executeCommand.Invoke(command);
                }
            }
            catch (Exception ex)
            {
                InitException(ex, commandText);
            }
        }

        private Task RunCommandTransactionAsync(Func<DbCommand, Task> createCommand, Func<DbCommand, Task> executeCommand, CancellationToken ct)
        {
            return ExecuteWithRetryAsync(async () =>
            {
                await using var db = CreateConnection(_settings);
                await db.OpenAsync(ct);
                await using var transaction = await db.BeginTransactionAsync(ct);
                string? commandText = null;
                try
                {
                    await using (var command = db.CreateCommand())
                    {
                        command.Transaction = transaction;
                        if (createCommand != null) await createCommand.Invoke(command);
                        commandText = DataBase.GetGeneratedQuery(command);
                        InvokeOnExecute(commandText);
                        if (executeCommand != null) await executeCommand.Invoke(command);
                    }
                    await transaction.CommitAsync(ct);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(ct);
                    InitException(ex, commandText);
                }
                finally
                {
                    await db.CloseAsync();
                }
            }, ct);
        }

        private Task RunCommandAsync(Func<DbCommand, Task> createCommand, Func<DbCommand, Task> executeCommand, CancellationToken ct)
        {
            return ExecuteWithRetryAsync(async () =>
            {
                await using var db = CreateConnection(_settings);
                await db.OpenAsync(ct);
                string? commandText = null;
                try
                {
                    await using (var command = db.CreateCommand())
                    {
                        if (createCommand != null) await createCommand.Invoke(command);
                        commandText = DataBase.GetGeneratedQuery(command);
                        InvokeOnExecute(commandText);
                        if (executeCommand != null) await executeCommand.Invoke(command);
                    }
                }
                catch (Exception ex)
                {
                    InitException(ex, commandText);
                }
                finally
                {
                    await db.CloseAsync();
                }
            }, ct);
        }

        private async IAsyncEnumerable<IEnumerable<object>> RunReaderCommandAsync(Func<DbCommand, Task> createCommand, Func<DbDataReader, Task<IEnumerable<object>>> transformFunction, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (transformFunction == null)
            {
                throw new ArgumentNullException(nameof(transformFunction));
            }

            // A read inside a boundary runs on the boundary's connection, so it sees the boundary's own
            // uncommitted writes. Without this, read-then-write logic inside a transaction gets a stale
            // snapshot — a wrong answer, not a missing feature.
            var ambient = AmbientTransaction;
            if (ambient != null)
            {
                await foreach (var row in ReadOnAsync(ambient.Connection, ambient.Transaction, createCommand, transformFunction, ct))
                {
                    yield return row;
                }
                yield break;
            }

            await using var db = CreateConnection(_settings);
            await db.OpenAsync(ct);
            await foreach (var row in ReadOnAsync(db, null, createCommand, transformFunction, ct))
            {
                yield return row;
            }
        }

        /// <summary>
        /// Streams a reader over an already-open connection, optionally enlisted in a transaction.
        /// </summary>
        /// <remarks>
        /// Disposes the command and the reader but never the connection — when the connection belongs to
        /// an ambient boundary, closing it here would end the caller's transaction mid-operation.
        /// </remarks>
        private async IAsyncEnumerable<IEnumerable<object>> ReadOnAsync(DbConnection db, DbTransaction? transaction, Func<DbCommand, Task> createCommand, Func<DbDataReader, Task<IEnumerable<object>>> transformFunction, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            string? commandText = null;
            await using (var command = db.CreateCommand())
            {
                command.Transaction = transaction;
                bool faulted = false;
                try
                {
                    if (createCommand != null) await createCommand.Invoke(command);
                    commandText = DataBase.GetGeneratedQuery(command);
                    InvokeOnExecute(command.CommandText);
                }
                catch (Exception ex)
                {
                    // CR-M134: with an OnException handler registered InitException returns instead of
                    // rethrowing — short-circuit rather than executing a command that failed to build.
                    InitException(ex, commandText);
                    faulted = true;
                }
                if (faulted)
                {
                    yield break;
                }
                DbDataReader reader;
                try
                {
                    reader = await command.ExecuteReaderAsync(ct);
                }
                catch (Exception ex) when (IsMissingTableException(ex))
                {
                    yield break;
                }
                await using var _ = reader;
                if (!(reader?.HasRows ?? false))
                {
                    yield break;
                }
                bool isNext = false;
                try
                {
                    isNext = await reader.ReadAsync(ct);
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
                        row = await transformFunction.Invoke(reader);
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
                        isNext = await reader.ReadAsync(ct);
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
