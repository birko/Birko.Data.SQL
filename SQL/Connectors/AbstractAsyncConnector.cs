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
        public AbstractAsyncConnector(PasswordSettings settings) : base(settings)
        {
        }

        public virtual Task DoInitAsync(CancellationToken ct = default)
        {
            DoInit();
            return Task.CompletedTask;
        }

        public virtual Task DoCommandAsync(Func<DbCommand, Task> createCommand, Func<DbCommand, Task> executeCommand, bool isLock = false, CancellationToken ct = default)
        {
            if (!isLock)
            {
                return RunCommandAsync(createCommand, executeCommand, ct);
            }
            else
            {
                return Task.Run(() =>
                {
                    lock (_lock)
                    {
                        return RunCommandAsync(createCommand, executeCommand, ct);
                    }
                }, ct);
            }
        }

        public virtual Task DoCommandWithTransactionAsync(Func<DbCommand, Task> createCommand, Func<DbCommand, Task> executeCommand, bool isLock = false, CancellationToken ct = default)
        {
            if (!isLock)
            {
                return RunCommandTransactionAsync(createCommand, executeCommand, ct);
            }
            else
            {
                return Task.Run(() =>
                {
                    lock (_lock)
                    {
                        return RunCommandTransactionAsync(createCommand, executeCommand, ct);
                    }
                }, ct);
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

        private async IAsyncEnumerable<IEnumerable<object>> RunReaderCommandAsync(Func<DbCommand, Task> createCommand, Func<DbDataReader, Task<IEnumerable<object>>> transformFunction)
        {
            if (transformFunction == null)
            {
                throw new ArgumentNullException(nameof(transformFunction));
            }

            await using var db = CreateConnection(_settings);
            await db.OpenAsync();
            string? commandText = null;
            await using (var command = db.CreateCommand())
            {
                try
                {
                    if (createCommand != null) await createCommand.Invoke(command);
                    commandText = DataBase.GetGeneratedQuery(command);
                    InvokeOnExecute(command.CommandText);
                }
                catch (Exception ex)
                {
                    InitException(ex, commandText);
                }
                await using var reader = await command.ExecuteReaderAsync();
                if (!(reader?.HasRows ?? false))
                {
                    yield break;
                }
                bool isNext = false;
                try
                {
                    isNext = await reader.ReadAsync();
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
                        isNext = await reader.ReadAsync();
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
