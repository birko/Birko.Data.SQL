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

    // The ambient registration this unit of work owns, released on commit/rollback/dispose.
    private IDisposable? _ambientScope;
    // The AsyncLocal cell installed at construction — see the constructor for why it cannot be installed
    // in BeginAsync. Released on dispose.
    private readonly IDisposable _cellScope;
    // Set when BeginAsync found an enclosing boundary against the same database and joined it instead
    // of opening a second connection. A participant never commits and never rolls back the transaction.
    private AmbientSqlTransaction.Entry? _joined;

    public bool IsActive => _transaction is not null || _joined is not null;

    /// <summary>
    /// True when this unit of work joined an enclosing boundary rather than opening its own.
    /// </summary>
    /// <remarks>
    /// A participant's <see cref="CommitAsync"/> is a no-op — the owner commits. Its
    /// <see cref="RollbackAsync"/> marks the enclosing boundary rollback-only so the owner's commit
    /// throws, because a nested rollback that the owner then committed over would be a decision silently
    /// discarded.
    /// </remarks>
    public bool IsParticipant => _joined is not null;

    /// <inheritdoc />
    public ITransactionCapabilities Capabilities { get; } = new TransactionCapabilities(
        TransactionAtomicity.Atomic,
        TransactionBoundaryScope.Database,
        readsSeeUncommittedWrites: true);

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

        // Installed HERE, in a synchronous constructor, and not in BeginAsync — an async method cannot
        // publish an AsyncLocal to its caller, because AsyncMethodBuilder.Start saves the ambient
        // ExecutionContext and restores it when the state machine returns. A boundary opened by an
        // awaited BeginAsync() would be invisible to the code that awaited it, which is precisely the
        // "set a transaction, get no error, write outside it" failure this class exists to remove.
        // Construct the unit of work in the flow that will use it.
        _cellScope = AmbientSqlTransaction.InstallCell();
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

        var settingsId = _settings.GetId();

        // Nesting JOINS rather than opening a second transaction. Opening one here would give a
        // committed inner transaction inside an outer one that later rolls back — partial application
        // reporting green, which is the exact failure this whole boundary exists to remove.
        var enclosing = AmbientSqlTransaction.Find(settingsId);
        if (enclosing is not null)
        {
            _joined = enclosing;
            Context = new SqlTransactionContext(enclosing.Connection, enclosing.Transaction);
            return;
        }

        _connection = _connector.CreateConnection(_settings);

        await _connection.OpenAsync(ct);
        _transaction = await _connection.BeginTransactionAsync(ct);
        Context = new SqlTransactionContext(_connection, _transaction);

        // Publishing to the ambient scope is what makes every store in this flow participate without
        // the caller having to hand the context to each one individually.
        _ambientScope = AmbientSqlTransaction.Enter(settingsId, _connection, _transaction);
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsActive)
            throw new NoActiveTransactionException();

        if (_joined is not null)
        {
            // A participant does not commit — the owner does. Refuse only if this participant is being
            // asked to commit a boundary it has itself poisoned.
            if (_joined.IsRollbackOnly)
                throw new TransactionRollbackOnlyException();
            _joined = null;
            Context = null;
            return;
        }

        if (_ambientScope is not null && AmbientSqlTransaction.Find(_settings.GetId()) is { IsRollbackOnly: true })
        {
            throw new TransactionRollbackOnlyException();
        }

        // Leave the ambient scope BEFORE committing, so a store used during commit cannot enlist in a
        // transaction that is on its way out.
        ExitAmbient();
        await _transaction!.CommitAsync(ct);
        await CleanupAsync();
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsActive)
            throw new NoActiveTransactionException();

        if (_joined is not null)
        {
            // Nothing to undo here — the owner holds the transaction. Poison it so the owner's commit
            // fails rather than silently discarding this decision.
            _joined.MarkRollbackOnly();
            _joined = null;
            Context = null;
            return;
        }

        ExitAmbient();
        await _transaction!.RollbackAsync(ct);
        await CleanupAsync();
    }

    private void ExitAmbient()
    {
        _ambientScope?.Dispose();
        _ambientScope = null;
    }

    private async Task CleanupAsync()
    {
        ExitAmbient();
        _joined = null;
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
            _cellScope.Dispose();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            ExitAmbient();
            _cellScope.Dispose();
            _joined = null;
            _transaction?.Dispose();
            _transaction = null;
            _connection?.Close();
            _connection?.Dispose();
            _connection = null;
            Context = null;
        }
    }
}
