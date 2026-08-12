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

### SqlSettings (Birko.Data.SQL.Stores)
Base settings class for all SQL providers, extending `RemoteSettings`:
- `CommandTimeout` (default: 30 seconds) — SQL command execution timeout
- `ConnectionTimeout` (default: 15 seconds) — connection attempt timeout
- Abstract `GetConnectionString()` — overridden by each SQL provider

Provider-specific settings extend `SqlSettings`:
- `MSSqlSettings` — `MultipleActiveResultSets`, `TrustServerCertificate`
- `MySqlSettings` — `BulkInsertBatchSize`
- `PostgreSqlSettings` — `UseBinaryImport`

SQLite uses `SqLiteSettings` (extends `PasswordSettings`, not `SqlSettings`).

### Legacy Settings (still supported)
SQL stores still accept `RemoteSettings` / `PasswordSettings` via `SetSettings(ISettings)`. The connector's `CreateConnection` checks for typed settings first and falls back to the legacy format.

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

### Index creation during schema-ensure (`CreateTable(IEnumerable<Tables.Table>)`)
Schema-ensure creates declared indexes **one statement per index**, and an index it cannot build is
**recorded, not thrown** — on `AbstractConnector.IndexCreationFailures`, with the `OnIndexCreationFailed`
event for hosts that want to surface it at startup.

Why: stores run schema-ensure **lazily** on first data access and set `_initialized` only *after* it
returns, so an exception there left the store permanently uninitialised and re-threw on every later
operation — including reads that never touched the indexed column. One duplicate pair under a UNIQUE index
took down six entities' entire read surfaces in a consumer, with no way to even read the rows to repair
them (TASK-204).

- The public **`CreateIndexes` / `CreateIndexesAsync` still throw.** An explicit call — e.g.
  `Birko.Data.Migrations.SQL`'s `SqlSchemaBuilder` — is a caller asking for that index *now*. Only
  schema-ensure degrades.
- `IndexCreationFailures` is **current state keyed by (table, index)**, not a log: one entry per index
  however many times schema-ensure ran, the event fires on the transition into failure, and a successful
  build clears the entry. This matters because connectors are cached process-wide per (type, settings id)
  in `DataBase.GetConnector` while `_initialized` lives on the store — a scoped store per HTTP request
  re-runs schema-ensure per request against one shared connector.
- The **re-attempt on later runs is deliberate**: it is how the index appears once an operator repairs the
  offending rows, with no restart. Don't "optimise" it into skipping known-failed indexes.
- An empty `IndexCreationFailures` is **not** proof every declared index exists — an entity nothing has
  touched yet has not attempted its indexes.

### Enum Support
Enum properties are automatically mapped to `INTEGER` fields. `IntegerField` handles read/write conversion via `Enum.ToObject()` and `(int)` cast. Both non-nullable and nullable enums are supported.

### IgnoreField Attribute
Properties decorated with `[IgnoreField]` are skipped by `AbstractField.CreateAbstractField()` — they won't be included in table creation or any CRUD operations. Unsupported property types also return `null` instead of throwing `FieldAttributeException`.

### Filter translation (`DataBase.ParseConditionExpression` / `ParseExpression`)
The hand-rolled filter parsers run every predicate (Where / Delete / Update) and every value-position
Update-SET expression through the shared `Birko.Data.Expressions.ExpressionNormalizer` (in
`Birko.Data.Core`) at the lambda boundary first. The normalizer:
- **funcletizes** any parameter-free subtree to a constant (so parameter-free ternary / `??` /
  arithmetic and all closures collapse before parsing), and
- **desugars** a boolean-typed ternary `c ? t : f` → `(c && t) || (!c && f)` and a boolean-typed
  `a ?? b` → `(a == true) || (a == null && b)`, so `ParseConditionExpression` only ever sees
  AND/OR/NOT/comparisons.

On top of that, the SQL parsers themselves handle:
- **Value-expression operand in a predicate** — column arithmetic (`x.A + x.B > 5`, `x.Price * 2 >= 10`,
  `x.Total == x.A + x.B`, `x.Bonus % 2 == 0`), null-coalescing (`(x.Score ?? 0) > 5`) and a value-position
  ternary compared to something (`(x.Vip ? x.Premium : x.Score) > 100` — i.e. **CASE in WHERE**). The
  value side is rendered to a raw SQL fragment (`(A + B)`, `COALESCE(..)`, `CASE WHEN … END`, nullable
  `.Value` unwrap) placed in `Condition.Name`; the operator flips when the value is on the left;
  both-column comparisons use the `IsField` verbatim value. Because the WHERE builder binds parameters
  only later (via the condition strategies), constants **inside a fragment** cannot be parameterised and
  are inlined as portable SQL literals — numeric (invariant), `bool`→1/0, `enum`→integer, `string`
  single-quoted with `'` escaped (`RenderValueFragment` / `RenderBoolFragment` / `InlineConstant`).
  Non-portable literal types (DateTime, Guid, byte[]) and anything else it cannot faithfully translate
  throw `NotSupportedException` rather than silently dropping the filter.
- **Value-position** (`ParseExpression`, Update SET RHS) — `a ?? b` → `COALESCE(a, b)`,
  `c ? t : f` → `CASE WHEN c THEN t ELSE f END`, and `x == null` / `x != null` → `IS [NOT] NULL`.

Tests: `Birko.Data.Core.Tests.ExpressionNormalizerTests` (shared transform, semantic parity),
`Birko.Data.SQL.SqLite.Tests.SqlPredicateNormalizationTests` (end-to-end Where/Delete/Update + value
position vs a compiled-delegate oracle), alongside the existing `SqlExpressionParityTests`.

#### Empty `IN` renders a constant, not `IN ()`

`InConditionStrategy` had no empty-set case and emitted `Col IN ()`. SQLite's grammar permits that (and
evaluates it as always-false), which is why the SQLite-backed suites never saw it — PostgreSQL and MSSQL both
reject it as a **syntax error**. An empty set now renders a constant with the same set semantics, valid on
every dialect, needing no parameters and composing inside AND/OR chains exactly as a real `IN` would:

| Predicate | Renders | Why |
|-----------|---------|-----|
| empty `IN` | `1 = 0` | nothing is a member of the empty set |
| empty `NOT IN` | `1 = 1` | *everything* is "not in" the empty set — always-false here would silently invert the predicate |

All four providers share the one strategy (registered in `AbstractConnectorBase.InitializeConditionStrategies`)
and none override `IN` rendering, so this covers PostgreSQL/MSSQL/MySQL/SQLite. `ParseConditionExpression`
matches: an empty materialized collection stays an `In` with no values (it used to degrade to
`ConditionType.IsNull`, i.e. "rows whose column is NULL" — a different wrong answer); only a genuine `= null`
maps to `IsNull`. An all-null list also collapses to "matches nothing", faithfully — `Col IN (NULL)` is never
true. Tests: `Strategies/InConditionStrategyTests` (incl. single-value and all-null boundary cases guarding
against an over-eager emptiness check).

#### Overload-disambiguating arguments are not operands (`IsNonOperandArgument`)

`enumSet.Contains(x.EnumColumn)` silently matched **zero rows**. On .NET 9+ an *array* `set.Contains(x.Col)`
binds to `MemoryExtensions.Contains(ReadOnlySpan<T>, T, IEqualityComparer<T>?)` whenever `T` is not
`IEquatable<T>` — true for every enum and nullable enum. The parser iterated *every* argument, so the trailing
`null` comparer hit the constant-null branch and flipped the whole condition to `ConditionType.IsNull`.
`Guid`/`int`/`string` **are** `IEquatable`, bind the 2-argument overload, and were never affected — which is
why the canonical batch-query pattern never exposed it. Same defect family as the earlier
`Title.Contains(query, StringComparison…)` bug.

`DataBase.IsNonOperandArgument` now skips comparer / `StringComparison` / `CultureInfo` arguments. A **non-null**
comparer is skipped too: its semantics delegate to the column collation, exactly as the `StringComparison`
overloads already do.

#### Enum parameter values bind as their underlying integer

Enums persist as `INTEGER` (`IntegerField`), but a boxed enum reaching a provider parameter was left to that
provider's own inference — `Microsoft.Data.Sqlite` converts it, **Npgsql rejects an unmapped CLR enum**.
`AbstractConnectorBase.NormalizeParameterValue` unwraps enums to their underlying integral type; because the
provider `AddParameter` overrides deliberately do **not** chain to the base implementation, each one
(SQLite/PostgreSQL/MySQL/MSSQL) calls it directly. Covers enum values in `UPDATE … SET` as well as predicates.
Tests: `Birko.Data.SQL.Tests/Connectors/EnumParameterBindingTests` (condition tree + bound parameter values),
`Birko.Data.SQL.SqLite.Tests/SqlEnumInPredicateTests` (11 cases on a real SQLite file vs a compiled-delegate
oracle).

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
