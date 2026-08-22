using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractAsyncConnector
    {
        public Task CreateTableAsync(Type[] types, CancellationToken ct = default)
        {
            return CreateTableAsync(DataBase.LoadTables(types), ct);
        }

        public async Task CreateTableAsync(IEnumerable<Tables.Table> tables, CancellationToken ct = default)
        {
            if (tables != null && tables.Any() && tables.Any(x => x != null && x.Fields != null && x.Fields.Count > 0))
            {
                await CreateTableAsync(tables.ToDictionary(x => x.Name, x => x.Fields.Select(y => y.Value)), ct);
                foreach (var table in tables.Where(x => x.Indexes != null && x.Indexes.Count > 0))
                {
                    // Async mirror of the sync path — see AbstractConnector_Create.cs for the full
                    // reasoning. Short version: schema-ensure runs lazily on first data access, so an
                    // exception here left the store permanently uninitialised and killed the entity's whole
                    // surface over one unbuildable index. Recorded, not thrown; CreateIndexesAsync itself
                    // still throws for direct callers.
                    foreach (var index in table.Indexes!.Values)
                    {
                        try
                        {
                            await CreateIndexesAsync(table.Name, new[] { index }, ct);
                            ClearIndexCreationFailure(table.Name, index?.Name);
                        }
                        // Cancellation is NOT an index failure — recording it would both mislabel the
                        // failure and swallow the caller's cancellation.
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            RecordIndexCreationFailure(table.Name, index?.Name, ex);
                        }
                    }
                }
            }
        }

        public Task CreateTableAsync(IDictionary<string, IEnumerable<Fields.AbstractField>> tables, CancellationToken ct = default)
        {
            if (tables != null && tables.Any() && tables.Any(x => x.Value != null && x.Value.Count() > 0))
            {
                var tasks = new List<Task>();
                foreach (var kvp in tables.Where(x => x.Value != null && x.Value.Any()))
                {
                    tasks.Add(CreateTableAsync(kvp.Key, kvp.Value.Select(x => FieldDefinition(x)), ct));
                }
                return Task.WhenAll(tasks);
            }
            return Task.CompletedTask;
        }

        public virtual async Task CreateTableAsync(string name, IEnumerable<string> fields, CancellationToken ct = default)
        {
            await DoDdlCommandAsync(async (command) =>
            {
                command.CommandText = "CREATE TABLE IF NOT EXISTS "
                    + QuoteIdentifier(name)
                    + " ("
                    + string.Join(", ", fields.Where(x => !string.IsNullOrEmpty(x)))
                    + ")";
                await Task.CompletedTask;
            }, async (command) =>
            {
                await command.ExecuteNonQueryAsync(ct);
            }, true);
        }

        /// <param name="throwIfExists">See the sync twin — false (default) is an ensure, true is a plain create.</param>
        /// <remarks>
        /// <c>throwIfExists</c> sits <b>after</b> <paramref name="ct"/>, against the usual
        /// cancellation-token-last convention, and deliberately: the token is already the third positional
        /// parameter and at least one caller passes it that way, so moving it would be a source-breaking
        /// change for every consumer that does the same. Pass the new flag by name.
        /// </remarks>
        public virtual async Task CreateIndexesAsync(string tableName, IEnumerable<Tables.IndexDefinition> indexes, CancellationToken ct = default, bool throwIfExists = false)
        {
            foreach (var index in indexes)
            {
                // TASK-273 — refuse BEFORE DoDdlCommand, not inside it. A callback exception is re-wrapped
                // by InitException as `new Exception(commandText, ex)`, so a caller could not select this
                // refusal by type; thrown here it arrives intact. Schema-ensure's per-index catch still
                // records it (TASK-204), and an explicit call still fails loudly, which is what criterion 4
                // asks for: a provider that cannot honour the predicate must not quietly emit the index
                // without it, because for an IS NULL term that is a STRICTER constraint than declared.
                RequireExpressiblePredicates(index);

                try
                {
                    await DoDdlCommandAsync(async (command) =>
                    {
                        command.CommandText = CreateIndexSql(tableName, index, conditional: !throwIfExists);
                        await Task.CompletedTask;
                    }, async (command) =>
                    {
                        await command.ExecuteNonQueryAsync(ct);
                    }, true);
                }
                catch (Exception ex) when (!throwIfExists && IsIndexAlreadyExistsException(ex))
                {
                    // TASK-245 — see the sync twin in AbstractConnector_Create.cs for the full reasoning.
                    // In short: MySQL has no IF NOT EXISTS for CREATE INDEX, so "already there" (1061) is
                    // classified here; 1062 (unbuildable) is a different code and still reaches the recorder,
                    // leaving TASK-204 intact. The exception is wrapped by InitException, hence the chain walk.
                }
            }
        }

        public virtual async Task DropIndexesAsync(string tableName, IEnumerable<Tables.IndexDefinition> indexes, CancellationToken ct = default)
        {
            foreach (var index in indexes)
            {
                await DoDdlCommandAsync(async (command) =>
                {
                    command.CommandText = DropIndexSql(tableName, index);
                    await Task.CompletedTask;
                }, async (command) =>
                {
                    await command.ExecuteNonQueryAsync(ct);
                }, true);
            }
        }
    }
}
