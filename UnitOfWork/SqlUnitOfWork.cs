using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Birko.Data.Patterns.UnitOfWork;
using Birko.Data.SQL.Connectors;
using Birko.Data.Models;
using Birko.Data.SQL.Stores;
using Birko.Data.Stores;
using Birko.Configuration;
using PasswordSettings = Birko.Configuration.PasswordSettings;

/// <summary>
/// SQL Unit of Work implementation using ADO.NET DbTransaction.
/// Creates a connection from the connector and manages BEGIN/COMMIT/ROLLBACK.
/// </summary>
public sealed class SqlUnitOfWork : IUnitOfWork<SqlTransactionContext>
{
    private readonly AbstractConnectorBase _connector;
    private readonly PasswordSettings _settings;
    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private bool _disposed;

    public bool IsActive => _transaction is not null;
    public SqlTransactionContext? Context { get; private set; }

    /// <summary>
    /// Creates a new SqlUnitOfWork.
    /// </summary>
    /// <param name="connector">Any SQL connector (PostgreSQL, MSSql, MySQL, SQLite) — used as connection factory.</param>
    /// <param name="settings">Connection settings for creating the database connection.</param>
    public SqlUnitOfWork(AbstractConnectorBase connector, PasswordSettings settings)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Creates a new SqlUnitOfWork from a configured store.
    /// </summary>
    public static SqlUnitOfWork FromStore<DB, T>(AsyncDataBaseStore<DB, T> store)
        where DB : AbstractConnector
        where T : AbstractModel
    {
        var connector = store.Connector
            ?? throw new InvalidOperationException("Store connector is not initialized. Call SetSettings() first.");
        // The connector already exposes its settings via the public Settings property — pass them into
        // the normal ctor rather than routing through a dummy ctor + reflection over the private field.
        return new SqlUnitOfWork(connector, connector.Settings);
    }

    public async Task BeginAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsActive)
            throw new TransactionAlreadyActiveException();

        _connection = _connector.CreateConnection(_settings);

        await _connection.OpenAsync(ct);
        _transaction = await _connection.BeginTransactionAsync(ct);
        Context = new SqlTransactionContext(_connection, _transaction);
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsActive)
            throw new NoActiveTransactionException();

        await _transaction!.CommitAsync(ct);
        await CleanupAsync();
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsActive)
            throw new NoActiveTransactionException();

        await _transaction!.RollbackAsync(ct);
        await CleanupAsync();
    }

    private async Task CleanupAsync()
    {
        Context = null;
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }
        if (_connection is not null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await CleanupAsync();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _transaction?.Dispose();
            _transaction = null;
            _connection?.Close();
            _connection?.Dispose();
            _connection = null;
            Context = null;
        }
    }
}
