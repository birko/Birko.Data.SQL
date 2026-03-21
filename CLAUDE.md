# Birko.Data.SQL

## Overview
SQL-specific data access layer providing abstract base classes for SQL database operations.

## Project Location
`C:\Source\Birko.Data.SQL\`

## Purpose
- Provides abstract base classes for SQL database operations
- Defines connector pattern for database connections
- Implements common SQL functionality (tables, attributes, exceptions)

## Abstract Classes

### DataBaseStore\<DB,T\>
Base class for synchronous SQL stores.
- `DB Connector` - Database connection (protected set for derived classes)
- Abstract methods for CRUD operations
- Automatic connection management via Settings

### DataBaseBulkStore\<DB,T\>
Extends DataBaseStore with bulk operations.
- Optimized for bulk inserts/updates/deletes
- Uses database-specific bulk operations where available

### AsyncDataBaseStore\<DB,T\>
Base class for asynchronous SQL stores.
- `DB Connector` - Database connection (protected set)
- Async versions of all database operations

### AsyncDataBaseBulkStore\<DB,T\>
Extends AsyncDataBaseStore with async bulk operations.

## Key Components

### Stores
- `DataBaseStore<DB,T>` - Base sync SQL store
- `DataBaseBulkStore<DB,T>` - Base sync SQL bulk store
- `AsyncDataBaseStore<DB,T>` - Base async SQL store
- `AsyncDataBaseBulkStore<DB,T>` - Base async SQL bulk store

### Repositories
- `DataBaseRepository<T,S,DB>` - SQL repository base
- `DataBaseBulkRepository<T,S,DB>` - SQL bulk repository
- `AsyncDataBaseRepository<T,S,DB>` - Async SQL repository
- `AsyncDataBaseBulkRepository<T,S,DB>` - Async SQL bulk repository

### Query Caching
- `CachedAsyncDataBaseBulkStore<DB,T>` - Caching decorator for async bulk SQL stores
- `SqlCacheKeyBuilder` - Generates consistent cache keys from query parameters and filter expressions
- `SqlCacheOptions` - Configuration for cache TTL, key prefix, and invalidation strategy
- Automatic cache invalidation on write operations (Create, Update, Delete)
- Works with any `ICache` implementation (MemoryCache, RedisCache, HybridCache)

### SQL Components

#### Attributes (`Birko.Data.SQL.Attributes`)
- `Table(string name)` - Maps entity class to a database table
- `NamedField(string? name)` - Maps property to a column with a custom name
- `PrimaryField` - Marks primary key column
- `UniqueField` - Marks column as unique
- `IncrementField` - Marks column as auto-increment
- `RequiredField` - Forces NOT NULL even for nullable C# types
- `MaxLengthField(int maxLength)` - Sets VARCHAR length for string fields (takes priority over PrecisionField)
- `PrecisionField(int precision)` - Sets numeric precision
- `ScaleField(int scale)` - Sets numeric scale
- `IgnoreField` - Excludes a property from SQL field mapping (skipped during table creation and CRUD operations)

#### DataAnnotations Support (`System.ComponentModel.DataAnnotations`)
Standard DataAnnotations attributes are recognized alongside Birko attributes. Birko attributes take precedence when both are specified on the same property.

| DataAnnotation | Birko Equivalent | Notes |
|---|---|---|
| `[Table("name")]` | `[Table("name")]` | From `Schema` namespace |
| `[Column("name")]` | `[NamedField("name")]` | `[NamedField]` takes precedence if both present |
| `[Key]` | `[PrimaryField]` | |
| `[Required]` | `[RequiredField]` | |
| `[MaxLength(n)]` | `[MaxLengthField(n)]` | Birko value takes precedence |
| `[StringLength(n)]` | `[MaxLengthField(n)]` | Birko value takes precedence |
| `[DatabaseGenerated(Identity)]` | `[IncrementField]` | From `Schema` namespace |
| `[NotMapped]` | `[IgnoreField]` | From `Schema` namespace |

No DataAnnotations equivalent exists for `[UniqueField]`, `[PrecisionField]`, or `[ScaleField]`.

#### Models
- `ColumnModel` - Represents a table column
- `TableModel` - Represents a database table

#### Exceptions
- `SqlException` - SQL-specific exceptions
- `ConnectionException` - Connection failure exceptions
- `QueryException` - Query execution exceptions

#### Extensions
- SQL helper extensions for common operations

## Connector Pattern

SQL stores use a typed connector:

```csharp
public abstract class DataBaseStore<DB, T> : AbstractStore<T>
    where T : Models.Entity
    where DB : class, IDisposable
{
    protected DB Connector { get; protected set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Connector != null)
        {
            Connector.Dispose();
        }
        base.Dispose(disposing);
    }
}
```

## Implementation Example

```csharp
using Birko.Data.SQL.Stores;
using System.Data.SqlClient;

public class CustomerStore : DataBaseStore<SqlConnection, Customer>, IStore<Customer>
{
    public override Guid Create(Customer item)
    {
        var cmd = Connector.CreateCommand();
        cmd.CommandText = "INSERT INTO Customers (Id, Name) VALUES (@Id, @Name)";
        // Add parameters and execute
    }

    public override void Read(Customer item)
    {
        var cmd = Connector.CreateCommand();
        cmd.CommandText = "SELECT * FROM Customers WHERE Id = @Id";
        // Execute and populate item
    }
}
```

## Settings

SQL stores use `RemoteSettings` for connection:
- `Server` - Database server address
- `Port` - Database port (if applicable)
- `Database` - Database name
- `UserName` - Database user
- `Password` - Database password

Pass settings via `base.SetSettings()`:
```csharp
public override void SetSettings(Settings settings)
{
    base.SetSettings(settings);
    // Connector is created from settings
}
```

## Dependencies
- Birko.Data.Core, Birko.Data.Stores, Birko.Data.Repositories
- .NET 10.0

## Dependents
- Birko.Data.SQL.MSSql
- Birko.Data.SQL.PostgreSQL
- Birko.Data.SQL.MySQL
- Birko.Data.SQL.SqLite
- Birko.Data.TimescaleDB
- Birko.Data.SQL.View

## Important Notes

### Enum Support
Enum properties are automatically mapped to `INTEGER` fields. `IntegerField` handles read/write conversion via `Enum.ToObject()` and `(int)` cast. Both non-nullable and nullable enums are supported.

### IgnoreField Attribute
Properties decorated with `[IgnoreField]` are skipped by `AbstractField.CreateAbstractField()` — they won't be included in table creation or any CRUD operations. Unsupported property types also return `null` instead of throwing `FieldAttributeException`.

### Connector Property
Always use `protected set` for the Connector property:
```csharp
protected DB Connector { get; protected set; }
```

### Settings Handling
Don't create settings inline - pass through base class:
```csharp
// WRONG
var settings = new PasswordSettings { UserName = "...", Port = 123 };

// CORRECT
base.SetSettings(settings); // settings is passed from repository
```

### Parameterless Constructor
Provide a parameterless constructor in derived repositories:
```csharp
public class MyRepository : DataBaseRepository<Entity, MyStore, SqlConnection>
{
    public MyRepository() : base()
    {
        // Creates MyStore by default
    }
}
```

## Database Providers

### Microsoft SQL Server
- See: [Birko.Data.SQL.MSSql](../Birko.Data.SQL.MSSql/CLAUDE.md)
- Connector: `SqlConnection`
- Namespace: `System.Data.SqlClient`

### PostgreSQL
- See: [Birko.Data.SQL.PostgreSQL](../Birko.Data.SQL.PostgreSQL/CLAUDE.md)
- Connector: `NpgsqlConnection`
- Package: `Npgsql`

### MySQL
- See: [Birko.Data.SQL.MySQL](../Birko.Data.SQL.MySQL/CLAUDE.md)
- Connector: `MySqlConnection`
- Package: `MySql.Data`

### SQLite
- See: [Birko.Data.SQL.SqLite](../Birko.Data.SQL.SqLite/CLAUDE.md)
- Connector: `SqliteConnection`
- Package: `Microsoft.Data.Sqlite`

### TimescaleDB
- See: [Birko.Data.TimescaleDB](../Birko.Data.TimescaleDB/CLAUDE.md)
- Based on PostgreSQL
- Connector: `NpgsqlConnection`

## Maintenance

### README Updates
When making changes that affect the public API, features, or usage patterns of this project, update the README.md accordingly. This includes:
- New classes, interfaces, or methods
- Changed dependencies
- New or modified usage examples
- Breaking changes

### CLAUDE.md Updates
When making major changes to this project, update this CLAUDE.md to reflect:
- New or renamed files and components
- Changed architecture or patterns
- New dependencies or removed dependencies
- Updated interfaces or abstract class signatures
- New conventions or important notes

### Test Requirements
Every new public functionality must have corresponding unit tests. When adding new features:
- Create test classes in the corresponding test project
- Follow existing test patterns (xUnit + FluentAssertions)
- Test both success and failure cases
- Include edge cases and boundary conditions
