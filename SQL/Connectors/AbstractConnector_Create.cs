using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractConnector
    {
        public void CreateTable(Type[] types)
        {
            CreateTable(DataBase.LoadTables(types));
        }

        public void CreateTable(IEnumerable<Tables.Table> tables)
        {
            if (tables != null && tables.Any() && tables.Any(x => x != null && x.Fields != null && x.Fields.Count > 0))
            {
                CreateTable(tables.ToDictionary(x => x.Name, x => x.Fields.Select(y => y.Value)));
                foreach (var table in tables.Where(x => x.Indexes != null && x.Indexes.Count > 0))
                {
                    // An index that cannot be built is RECORDED, not thrown — one index per attempt so a
                    // failure cannot hide the indexes behind it.
                    //
                    // This path is schema-ensure, which stores run LAZILY on first data access
                    // (AbstractAsyncStore.EnsureInitializedAsync -> InitCoreAsync -> CreateTable). Letting
                    // the exception escape therefore meant the store never initialised, and EVERY
                    // subsequent operation on that entity re-attempted and re-threw: a single unbuildable
                    // index took down the entity's whole surface, including reads that never touched the
                    // indexed column, and it could not self-heal.
                    //
                    // Measured in consumer Symbio (TASK-354): one duplicate (TenantGuid, OrderNumber) pair
                    // left behind by pre-allocator numbering made a later-declared UNIQUE index unbuildable,
                    // and GET /api/manufacturing/orders, the same route with a status filter, and the detail
                    // route all returned 500 while the sibling entity in the same module was fine. The same
                    // annotation is on five further entities there, so the blast radius was six entities'
                    // read surfaces — permanently, with no way to even read the rows to repair them.
                    //
                    // An index is a constraint/optimisation, so degrading it to "absent and reported" is
                    // strictly better than "table unusable": the data stays reachable, and the host can
                    // surface the failure (IndexCreationFailures / OnIndexCreationFailed) at startup. It is
                    // NOT silent — that is the whole point of recording it rather than swallowing it.
                    //
                    // Note the public CreateIndexes(...) below is UNCHANGED and still throws: an explicit
                    // call (e.g. the migrations SqlSchemaBuilder) is a caller asking for this index now, and
                    // must fail loudly. Only schema-ensure degrades.
                    foreach (var index in table.Indexes!.Values)
                    {
                        try
                        {
                            CreateIndexes(table.Name, new[] { index });
                            // Schema-ensure re-runs on every store instance, so this is also the path by
                            // which a previously unbuildable index recovers once its data is repaired.
                            ClearIndexCreationFailure(table.Name, index?.Name);
                        }
                        catch (Exception ex)
                        {
                            RecordIndexCreationFailure(table.Name, index?.Name, ex);
                        }
                    }
                }
            }
        }

        public void CreateTable(IDictionary<string, IEnumerable<Fields.AbstractField>> tables)
        {
            if (tables != null && tables.Any() && tables.Any(x => x.Value != null && x.Value.Count() > 0))
            {
                foreach (var kvp in tables.Where(x => x.Value != null && x.Value.Any()))
                {
                    CreateTable(kvp.Key, kvp.Value.Select(x => FieldDefinition(x)));
                }
            }
        }

        public virtual void CreateTable(string name, IEnumerable<string> fields)
        {
            DoCommandWithTransaction((command) =>
            {
                command.CommandText = "CREATE TABLE IF NOT EXISTS "
                    + QuoteIdentifier(name)
                    + " ("
                    + string.Join(", ", fields.Where(x => !string.IsNullOrEmpty(x)))
                    + ")";
            }, (command) =>
            {
                command.ExecuteNonQuery();
            }, true);
        }

        public virtual void CreateIndexes(string tableName, IEnumerable<Tables.IndexDefinition> indexes)
        {
            foreach (var index in indexes)
            {
                DoCommandWithTransaction((command) =>
                {
                    command.CommandText = CreateIndexSql(tableName, index);
                }, (command) =>
                {
                    command.ExecuteNonQuery();
                }, true);
            }
        }

        public virtual void DropIndexes(string tableName, IEnumerable<Tables.IndexDefinition> indexes)
        {
            foreach (var index in indexes)
            {
                DoCommandWithTransaction((command) =>
                {
                    command.CommandText = DropIndexSql(tableName, index);
                }, (command) =>
                {
                    command.ExecuteNonQuery();
                }, true);
            }
        }
    }
}
