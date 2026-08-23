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

### Transaction boundaries (`AmbientSqlTransaction`, `SqlUnitOfWork`) — TASK-240

A boundary is an **ambient `AsyncLocal` scope**, not state on the connector and not state on the store.
Open one with `SqlUnitOfWork`; every store operation in that async flow joins it automatically.

```csharp
await using var uow = SqlUnitOfWork.FromStore(store);   // construct it in the flow that will use it
await uow.BeginAsync();
await orders.CreateAsync(order);        // both stores join the boundary with no per-store wiring
await payments.CreateAsync(payment);
await uow.CommitAsync();                 // or RollbackAsync() — nothing is committed until then
```

**What was measured (2026-08-17).** `AbstractAsyncConnector` inherited `ExternalConnection` /
`ExternalTransaction` from its sync base and **never read them**: both async entry points opened their own
connection, and the transactional one its own `BeginTransactionAsync`, per statement batch.
`AsyncDataBaseStore.SetTransactionContext` called `Connector.SetExternalTransaction` and the async write
path ignored it — so a caller could set a transaction, get no error, and have every write commit outside
it. A hook that reads as available is worse than an absent feature.

**Why ambient, and not the shape the NoSQL stores use.** Mongo, Raven and Cosmos keep the context as
instance state on the store, which is safe only while the store is per-scope. Connectors here are cached
process-wide per `(type, settings id)` in `DataBase.GetConnector`, and consumers register SQL stores as
singletons over them — so connector state *and* store state are both process-wide in practice. Copying
either would make one request's transaction capture every concurrent request's writes: a correctness
disaster strictly worse than having no transactions. An `AsyncLocal` scope is visible only to the
continuation chain that entered it, so it is correct under concurrent request threads regardless of store
lifetime, and it composes across stores without the caller remembering to wire each one.

- **Keyed by settings id.** A boundary on database A cannot capture a write to B; boundaries on different
  databases nest and compose.
- **Reads join it too.** `RunReaderCommandAsync` runs on the boundary's connection, so read-then-write
  logic sees its own uncommitted writes instead of the pre-transaction snapshot.
- **Checked before the `_asyncLock` gate.** A command on the caller's own connection needs no mutual
  exclusion against commands on other connections, and taking the gate there is how a boundary holder and
  the gate holder would wait on each other. (`isLock: true` currently has **zero** call sites
  framework-wide, so the gate is unreachable today — but the ordering is deliberate, not incidental.)
- **A participating command opens nothing, commits nothing and disposes nothing but the command.** The
  caller's connection must outlive the operation; disposing it mid-transaction is the failure this pattern
  invites. `RunCommandOnAsync` is also deliberately **not** retried — a retry inside a transaction whose
  earlier statements succeeded can only fail differently.
- **Nesting joins.** A second `SqlUnitOfWork` on the same database inside an open boundary becomes a
  *participant* (`IsParticipant`): no second connection, no second transaction. Its commit is a no-op —
  the owner commits — and its **rollback marks the boundary rollback-only**, so the owner's commit throws
  `TransactionRollbackOnlyException` rather than silently discarding the participant's decision.
- **⚠ Construct the unit of work synchronously, in the flow that will use it.** An `async` method cannot
  publish an `AsyncLocal` to its caller — `AsyncMethodBuilder.Start` saves the ambient `ExecutionContext`
  and restores it when the state machine returns — so `BeginAsync()` cannot install the scope. The
  constructor installs a mutable cell; `BeginAsync` publishes the boundary by mutating it. This was got
  wrong first and is pinned by
  `AmbientSqlTransactionTests.An_async_method_cannot_publish_an_ambient_boundary_to_its_caller`.
- **A boundary stops resolving the instant its owner leaves** (`Entry.IsEnded`), even for a flow still
  holding a stale cell — because `DisposeAsync` cannot restore the cell either. Without it, a later read
  ran on a disposed connection.
- **`SetTransactionContext` still exists and now works**, as store *instance* state routed through the
  same ambient mechanism, matching Mongo/Raven/Cosmos — with their caveat: safe only while the store is
  per-scope. It no longer touches the connector. Prefer `SqlUnitOfWork` for anything spanning stores.
- **⚠ The legacy `SetExternalTransaction` pair is GONE (TASK-259).** This section used to say it was
  "untouched and remains sync-only", with `SqlSchemaBuilder` as its sole caller. That caller published its
  connection *and* transaction onto a process-wide cached connector and never cleared them, so the runner's
  `using` disposed both and the next store's lazy schema-ensure ran on a dead connection. The builder now
  enters an `AmbientSqlTransaction` boundary like everything else and the pair was deleted with zero
  production callers. Do not reintroduce it: per-caller, per-operation state on a cached connector is the
  defect, not the spelling.

**Capabilities.** `IUnitOfWork.Capabilities` (`Birko.Data.Patterns.UnitOfWork.ITransactionCapabilities`)
states per backend what a boundary actually promises — modelled on `IJobLockProvider.IsLeaseBased`. SQL
declares `Atomic` / `Database` / reads-see-own-writes. The backends genuinely differ (Cosmos is
single-partition, Mongo needs a replica set, ElasticSearch has no transactions), and a contract that hid
that would be worse than none — see each provider's own `CLAUDE.md`.

#### Schema-ensure inside a boundary (TASK-244)

A store's lazy schema-ensure **participates** in the caller's boundary, and a participating schema-ensure is
**not remembered**.

- Both doors agree: `InitCore` / `InitCoreAsync` enter the transaction scope, so `SetTransactionContext`
  behaves like `SqlUnitOfWork`. Before this, `EnsureInitialized()` ran in the public wrapper while
  `EnterTransactionScope()` lived only in `*Core`, so the per-store door ran its DDL on a connection of its
  own — and on SQLite could not even begin one (`SQLite Error 5: 'database is locked'`, measured).
- `_initialized` is set from `CanRememberInitialization`, which the SQL stores answer from
  `AbstractConnector.DdlSurvivesRollback` (`AmbientTransaction == null || !SupportsTransactionalDdl`). So a
  rollback that removes the table also leaves the store willing to re-create it. **MySQL is the provider
  where the answer is "remember"**, because its DDL is issued off the boundary anyway.
- Cost: one idempotent `CREATE TABLE IF NOT EXISTS` on the boundary's own connection, on the next operation
  after a boundary-scoped init. The alternative — invalidate on rollback — has no steady-state cost and was
  rejected as needing every not-committed path to be caught.
- **Pinned by:** `Birko.Data.SQL.SqLite.Tests.SchemaEnsureRollbackResidueTests` (3, including one that
  asserts the [[TASK-277]] swallow as a defect) and `SchemaEnsureRollbackResidueLiveTests` in the
  PostgreSQL, MySQL and MSSql suites (3 each; MySQL's assert the opposite outcomes on purpose).

**Pinned by:** `Birko.Data.SQL.Tests.Connectors.AmbientSqlTransactionTests` (the primitive, 14),
`Birko.Data.SQL.SqLite.Tests.TransactionBoundaryEndToEndTests` (real SQLite, 13) and
`Birko.Data.SQL.PostgreSQL.Tests.TransactionBoundaryLiveTests` (live PostgreSQL, 10 — gated on
`BIRKO_PG_HOST`). **Both engines, because SQLite cannot express the concurrency half**: it serialises at
the file level, so a second writer — measured, even a second *reader* — blocks for the whole busy timeout
while a write transaction is open. The genuinely simultaneous inside/outside proofs are the PostgreSQL
ones. Mutation-tested: unwiring the ambient from the async connector fails 7 of 10 on PostgreSQL and 9 of
207 on SQLite; making the ambient process-wide instead of flow-local fails **exactly the 3 concurrency
tests** and no others — which is what a naive fix looks like from a single-threaded suite.

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

#### The emitted statement (TASK-245)
`AbstractConnectorBase.CreateIndexSql(tableName, index, conditional = true)` is the **single producer** of
index DDL for every dialect — the index managers' parallel `CreateUniqueIndexSql` family is gone (see
below).

- **Column identifiers are emitted BARE, the table identifier quoted.** Not cosmetic: `CreateTable` emits
  column definitions bare, so on PostgreSQL every column is stored case-folded, and a quoted `"Status"`
  cannot resolve it — measured on PostgreSQL 16 as `ERROR 42703: column "Status" does not exist`, meaning
  **no declared PascalCase index could be created on PostgreSQL at all**, silently, since TASK-204. Seventh
  instance of the identifier family; § Conventions in the aggregator CLAUDE.md is the standing rule. Do not
  "restore" the quoting from symmetry with the table name.
  MSSql's override keeps its columns bracket-quoted **deliberately** — its identifiers are case-insensitive
  under the default collation, so there is no defect there and no live measurement backing a change.
- **`conditional` controls the ensure-vs-create semantics, uniformly.** Default true emits `IF NOT EXISTS`
  (SQLite/PostgreSQL) or a synthesised `sys.indexes` guard (MSSql); MySQL has **no conditional form at all**
  (`CREATE INDEX IF NOT EXISTS` is `ERROR 1064`) and instead relies on the predicate below. Passing
  `CreateIndexes(..., throwIfExists: true)` drops the conditional form on **every** provider, so the flag
  means the same thing everywhere rather than being honoured on one and silently ignored on three.
- **`IsIndexAlreadyExistsException` is how a provider without a conditional form fakes one.** Base returns
  `false` — that is the whole no-behaviour-change-off-MySQL claim, and it is asserted, not argued. MySQL
  matches error **1061** (`Duplicate key name`) on the **code**, walking `InnerException` because
  `InitException` re-wraps every command failure.
- **"Already there" is not "unbuildable".** On MySQL those are different codes — 1061 vs **1062**
  (`Duplicate entry`) — so tolerating the former cannot swallow the latter and TASK-204 is untouched.
  Widening the predicate to any `MySqlException` fails 4 of the MySQL live suite's tests.
- **The public `CreateIndexes` is therefore idempotent on MySQL**, which is a deliberate change to the
  bullet above: it already was on the other three (native or synthesised conditional), and its one external
  caller — `SqlSchemaBuilder` — wants a re-applied migration to succeed. The "still throws" contract holds
  where it means something: an *unbuildable* index (1062) still throws from the explicit path. Both halves
  are pinned by tests.
- A same-name index over **different columns** is silently accepted on every provider. Faithful, not a hole:
  measured on PostgreSQL 16, `CREATE INDEX IF NOT EXISTS` reports *"relation already exists, skipping"* and
  keeps the old definition; MSSql's guard compares the name alone.
- **MySQL `DROP INDEX` takes no `IF EXISTS` and requires `ON <table>`** — the base emitted neither
  correctly, so no declared index could be dropped there either. Dropping an absent index on MySQL now
  **throws** (1091) where the base's `IF EXISTS` tolerated it: deliberate, because a `DropIndexes` caller
  named a specific index and the migrations drop step should fail loudly.
- **An indexed unbounded `string` is bounded on MySQL, and only there** (TASK-248). MySQL maps a plain
  `string` to `LONGTEXT` and cannot index a BLOB/TEXT column without a key length (**1170**), so
  `MySQLConnector.ConvertType` emits `VARCHAR(255)` when `AbstractField.IsIndexed` is set. `IsIndexed` is
  populated by `DataBase.LoadIndexes` at **both** of its column-resolution points — the per-property
  `[IndexedField]` branch and the class-level `[CompositeIndex]` branch — and missing either leaves half the
  declarations looking unindexed (reverting only the per-property one failed 0 tests until a test for that
  attribute form existed).
  SQLite, PostgreSQL and MSSql index TEXT natively and **ignore the flag deliberately**: seven live consumer
  entities declare UNIQUE composites over unbounded strings and work correctly there, so bounding the column
  everywhere would impose a 255-character ceiling on data that has none today. A prefix index was rejected in
  turn — every real case is UNIQUE, and a prefix makes the constraint *weaker than declared*. Declaring
  `[MaxLengthField(n)]` is still preferable to relying on the default: portable, visible at the model, and it
  applies on every provider.
- **`CreateIndexesAsync` has no production caller through stores.** `AsyncDataBaseStore.InitCoreAsync` calls
  the **sync** `Connector.CreateTable` inside a `Task.Run`, so an async store's schema-ensure runs the sync
  index loop. The async loop is reachable only via an explicit `CreateTableAsync`. Both are wired and tested
  — but a revert of only the async site fails **0** tests, so measure against the sync site when checking
  this path.

#### One producer for unique index DDL
`SqlIndexManager.ToSqlIndexDefinition` used to drop the `Unique` flag on the way in, which is the *only*
reason a parallel `CreateUniqueIndexSql` existed on the base, `PostgreSqlIndexManager` and
`MSSqlIndexManager`. TASK-245 carries the flag across and deletes all three; `CreateAsync` calls the
connector emitter unconditionally. The PostgreSQL copy quoted its columns, so it could never build a unique
index on a PascalCase entity — a second, independent instance of the identifier defect. If a dialect ever
needs a genuinely different unique statement, override `CreateIndexSql` on that **connector** so index DDL
keeps one producer.

`IIndexManager.CreateAsync` executes through `SqlIndexManager`'s own `ExecuteNonQueryAsync`, **not** the
connector's `CreateIndexes` funnel, so it does not get the 1061 tolerance. Correct for an explicit
bare-metal call whose failure is already wrapped in `IndexManagementException` and whose callers have
`ExistsAsync`.

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
