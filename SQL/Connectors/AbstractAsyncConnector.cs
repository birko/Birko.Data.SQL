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
    public delegate void InitAsyncConnector(AbstractAsyncConnector connector);
    public delegate void OnAsyncException(Exception ex, string? commandText);
    public delegate void OnAsyncExecute(string commandText);

    public abstract partial class AbstractAsyncConnector : AbstractConnectorBase
    {
        public event InitAsyncConnector? OnInit;
        public event OnAsyncException? OnException;
        public event OnAsyncExecute? OnExecute;

        public AbstractAsyncConnector(PasswordSettings settings) : base(settings)
        {
        }

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

        public virtual async Task DoInitAsync(CancellationToken ct = default)
        {
            if (!IsInitializing)
            {
                IsInitializing = true;
                OnInit?.Invoke(this);
                IsInitializing = false;
                await Task.CompletedTask;
            }
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
                        OnExecute?.Invoke(commandText);
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
                        OnExecute?.Invoke(commandText);
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
                    OnExecute?.Invoke(command.CommandText);
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
