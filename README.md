# Birko.Data.SQL

SQL-specific data access layer providing abstract base classes for SQL database operations in the Birko Framework.

## Features

- Abstract SQL store classes (sync and async, with bulk support)
- Typed connector pattern for database connections
- SQL attributes for table/column mapping
- Repository base classes for SQL databases
- Query caching layer with automatic invalidation
- Automatic connection management via Settings

## Installation

```bash
dotnet add package Birko.Data.SQL
```

## Dependencies

- Birko.Data.Core (AbstractModel, models)
- Birko.Data.Stores (store interfaces, Settings, RemoteSettings)
- .NET 10.0

## Usage

```csharp
using Birko.Data.SQL.Stores;

public class CustomerStore : DataBaseStore<SqlConnection, Customer>, IStore<Customer>
{
    public override Guid Create(Customer item)
    {
        var cmd = Connector.CreateCommand();
        cmd.CommandText = "INSERT INTO Customers (Id, Name) VALUES (@Id, @Name)";
        cmd.Parameters.AddWithValue("@Id", item.Id);
        cmd.Parameters.AddWithValue("@Name", item.Name);
        cmd.ExecuteNonQuery();
        return item.Id;
    }
}
```

## API Reference

### Stores

- **DataBaseStore\<DB,T\>** - Base sync SQL store with `Connector` property
- **DataBaseBulkStore\<DB,T\>** - Extends with bulk operations
- **AsyncDataBaseStore\<DB,T\>** - Base async SQL store
- **AsyncDataBaseBulkStore\<DB,T\>** - Async with bulk operations

### Repositories

- **DataBaseRepository\<T,S,DB\>** - SQL repository base
- **DataBaseBulkRepository\<T,S,DB\>** - Bulk repository
- **AsyncDataBaseRepository\<T,S,DB\>** - Async repository
- **AsyncDataBaseBulkRepository\<T,S,DB\>** - Async bulk repository

### Attributes (`Birko.Data.SQL.Attributes`)

- **Table(string name)** - Maps entity class to a database table
- **NamedField(string? name)** - Maps property to a column with a custom name
- **PrimaryField** - Marks primary key column
- **UniqueField** - Marks column as unique
- **IncrementField** - Marks column as auto-increment
- **RequiredField** - Forces NOT NULL even for nullable C# types
- **MaxLengthField(int maxLength)** - Sets VARCHAR length for string fields
- **PrecisionField(int precision)** - Sets numeric precision
- **ScaleField(int scale)** - Sets numeric scale
- **IgnoreField** - Excludes a property from SQL field mapping

### DataAnnotations Support

Standard `System.ComponentModel.DataAnnotations` attributes are recognized alongside Birko attributes (Birko takes precedence when both are specified):

- `[Table]` / `[Column]` - Table and column name mapping
- `[Key]` - Primary key (same as `[PrimaryField]`)
- `[Required]` - NOT NULL (same as `[RequiredField]`)
- `[MaxLength]` / `[StringLength]` - VARCHAR length (same as `[MaxLengthField]`)
- `[DatabaseGenerated(Identity)]` - Auto-increment (same as `[IncrementField]`)
- `[NotMapped]` - Exclude from mapping (same as `[IgnoreField]`)

### Settings

Uses `SqlSettings` (extends `RemoteSettings`) with `CommandTimeout`, `ConnectionTimeout`, and abstract `GetConnectionString()` overridden by each provider:

- **MSSqlSettings** — `MultipleActiveResultSets`, `TrustServerCertificate`
- **MySqlSettings** — `BulkInsertBatchSize`
- **PostgreSqlSettings** — `UseBinaryImport`
- **SqLiteSettings** (extends `PasswordSettings`) — `CommandTimeout`

```csharp
var settings = new MSSqlSettings
{
    Location = "localhost",
    Name = "MyDatabase",
    UserName = "sa",
    Password = "password",
    TrustServerCertificate = true,
    CommandTimeout = 60
};
var store = new MSSqlStore<Customer>();
store.SetSettings(settings);
```

### Index Management

```csharp
using Birko.Data.SQL.IndexManagement;
using Birko.Data.SQL.PostgreSQL.IndexManagement;
using Birko.Data.Patterns.IndexManagement;

// Dialect-specific managers
var indexManager = new PostgreSqlIndexManager(connector);  // or MSSqlIndexManager, SqLiteIndexManager, MySqlIndexManager

// Same IIndexManager interface as NoSQL providers
await indexManager.CreateAsync(new IndexDefinition
{
    Name = "idx_order_date",
    Fields = new[] { IndexField.Ascending("CustomerId"), IndexField.Descending("CreatedAt") },
    Unique = false
}, scope: "Orders");

// List, check, drop
var indexes = await indexManager.ListAsync(scope: "Orders");
bool exists = await indexManager.ExistsAsync("idx_order_date", scope: "Orders");
await indexManager.DropAsync("idx_order_date", scope: "Orders");
```

**Dialect-specific SQL generation:**
| Provider | Catalog View | Quoting |
|----------|-------------|---------|
| PostgreSQL | `pg_indexes` + `pg_class` | `"identifier"` |
| MSSQL | `sys.indexes` + `sys.index_columns` | `[identifier]` |
| MySQL | `information_schema.statistics` | `` `identifier` `` |
| SQLite | `sqlite_master` + `PRAGMA index_info` | `"identifier"` |

### Query Caching

- **CachedAsyncDataBaseBulkStore\<DB,T\>** - Caching decorator for async bulk stores with automatic invalidation

```csharp
using Birko.Data.SQL.Stores;
using Birko.Data.SQL.Caching;

var cacheOptions = new SqlCacheOptions
{
    DefaultExpiration = TimeSpan.FromMinutes(10),
    KeyPrefix = "customers"
};

var innerStore = new AsyncPostgreSQLBulkStore<Customer>();
var cachedStore = new CachedAsyncDataBaseBulkStore<NpgsqlConnection, Customer>(
    innerStore, cache, cacheOptions);

// Reads are served from cache when available
var customer = await cachedStore.ReadAsync(customerId);

// Writes automatically invalidate affected cache entries
await cachedStore.UpdateAsync(customer);
```

Key components:
- **SqlCacheKeyBuilder** - Generates consistent cache keys from query parameters and filters
- **SqlCacheOptions** - Configuration for cache TTL, key prefix, and invalidation strategy

## Database Providers

- [Birko.Data.SQL.MSSql](../Birko.Data.SQL.MSSql/) - SQL Server
- [Birko.Data.SQL.PostgreSQL](../Birko.Data.SQL.PostgreSQL/) - PostgreSQL
- [Birko.Data.SQL.MySQL](../Birko.Data.SQL.MySQL/) - MySQL
- [Birko.Data.SQL.SqLite](../Birko.Data.SQL.SqLite/) - SQLite
- [Birko.Data.TimescaleDB](../Birko.Data.TimescaleDB/) - TimescaleDB

## Filter-Based Bulk Operations

SQL stores support native filter-based update and delete — single SQL commands instead of per-row operations:

```csharp
// Native UPDATE ... SET active=0 WHERE category='old' (single SQL command)
await store.UpdateAsync(
    x => x.Category == "old",
    new PropertyUpdate<Product>().Set(x => x.Active, false).Set(x => x.Category, "archived"));

// Native DELETE FROM ... WHERE is_expired=1 (single SQL command)
await store.DeleteAsync(x => x.IsExpired);

// Complex mutations use read-modify-save fallback
await store.UpdateAsync(x => x.Price > 100, item => { item.Price *= 0.9m; });
```

`PropertyUpdate<T>` assignments are translated to SQL SET clauses via the connector's field resolution. The filter expression is converted to a WHERE clause via `DataBase.ParseConditionExpression()`.

### Filter translation notes

- **Empty membership sets are safe.** `ids.Contains(x.Id)` with an empty collection renders `1 = 0` (matches
  nothing) and its negation renders `1 = 1` (matches everything) — never `IN ()`, which PostgreSQL and MSSQL
  reject as a syntax error.
- **Enum collections work.** `statuses.Contains(x.Status)` translates to a real `IN` on all providers; enum
  parameters bind as their underlying integer, matching how enums are persisted (`INTEGER`).
- **Culture/comparer arguments are ignored** as operands — `Title.Contains(q, StringComparison.OrdinalIgnoreCase)`
  filters on `q` alone, with case-sensitivity delegated to the column collation.
- Predicates also accept ternaries, `??`, and column arithmetic — see
  [CLAUDE.md § Filter translation](CLAUDE.md) for the full supported surface.

## License

Part of the Birko Framework.
