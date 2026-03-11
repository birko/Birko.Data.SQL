using System.Data.Common;

namespace Birko.Data.SQL.Stores;

/// <summary>
/// SQL transaction context holding a shared connection and transaction.
/// Used by ITransactionalStore and IUnitOfWork to coordinate operations
/// across multiple stores within a single database transaction.
/// </summary>
public sealed class SqlTransactionContext
{
    public DbConnection Connection { get; }
    public DbTransaction Transaction { get; }

    public SqlTransactionContext(DbConnection connection, DbTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }
}
