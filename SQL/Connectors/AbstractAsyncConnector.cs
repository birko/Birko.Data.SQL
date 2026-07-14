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

        public virtual Task DoInitAsync(CancellationToken ct = default)
        {
            DoInit();
            return Task.CompletedTask;
        }

        public virtual async Task DoCommandAsync(Func<DbCommand, Task> createCommand, Func<DbCommand, Task> executeCommand, bool isLock = false, CancellationToken ct = default)
        {
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

            await using var db = CreateConnection(_settings);
            await db.OpenAsync(ct);
            string? commandText = null;
            await using (var command = db.CreateCommand())
            {
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
