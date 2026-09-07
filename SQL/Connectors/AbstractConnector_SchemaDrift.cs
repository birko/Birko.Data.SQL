using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Birko.Data.SQL.SchemaDrift;

namespace Birko.Data.SQL.Connectors
{
    /// <summary>
    /// Reports a column whose stored type no longer matches what the model declares (TASK-269).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this needs a dialect branch, when the neighbouring diagnostic deliberately avoided one.</b>
    /// Consumer Symbio's <c>SchemaDriftCheck</c> reads <c>SELECT * FROM T WHERE 1 = 0</c> and takes the
    /// reader's column <i>names</i>, on the stated grounds that a per-provider catalogue query would mean
    /// "a diagnostic that only runs on the dialect the developer happens to use". That reasoning is right
    /// for names and does not survive the type half: measured on Microsoft.Data.Sqlite,
    /// <c>GetDataTypeName()</c> returns <c>VARCHAR</c> for a <c>VARCHAR(255)</c> column and
    /// <c>DECIMAL</c> for a <c>DECIMAL(18,2)</c> one, and <c>GetColumnSchema()</c> answers
    /// <c>ColumnSize = -1</c> with null precision and scale. So the width is <b>not obtainable</b> from
    /// the provider-independent surface, and a check built on it would report TASK-264's money case —
    /// <c>DECIMAL(18,0)</c> against <c>DECIMAL(18,2)</c>, the same keyword — as perfectly healthy.
    /// </para>
    /// <para>
    /// The framework's answer to "don't let a dialect branch rot" is not to avoid the branch but to state
    /// it once per provider on the connector and assert every side of it, exactly as
    /// <see cref="AbstractConnectorBase.SupportsTransactionalDdl"/>,
    /// <see cref="AbstractConnectorBase.FoldsUnquotedIdentifiers"/>, <c>SupportsPartialIndexes</c> and
    /// <c>RequiresOrderByForPaging</c> already are. Symbio had no connector to hang one on; this does.
    /// </para>
    /// <para>
    /// ⚠ <b>Never use <c>DbDataReader.GetFieldType()</c> for this.</b> Measured on the same probe: it
    /// reports the stored <i>value's</i> affinity rather than the declaration, so a SQLite <c>REAL</c>
    /// column holding the text <c>'not-a-number'</c> reads back as <c>String</c>, and one column answered
    /// <c>String</c> empty and <c>Double</c> populated. A drift check built on it would report drift, or
    /// not, according to which rows happen to be in the table. <c>GetDataTypeName()</c> is stable across
    /// all three cases — it is only insufficient, not unstable.
    /// </para>
    /// </remarks>
    public abstract partial class AbstractConnector
    {
        /// <summary>
        /// A column as the database actually holds it, before rendering.
        /// </summary>
        protected sealed class StoredColumn
        {
            public StoredColumn(string name, string typeName, long? size = null, int? precision = null, int? scale = null)
            {
                Name = name;
                TypeName = typeName;
                Size = size;
                Precision = precision;
                Scale = scale;
            }

            public string Name { get; }

            /// <summary>The bare type keyword as the catalogue reports it, e.g. <c>nvarchar</c>.</summary>
            public string TypeName { get; }

            /// <summary>Character/byte width where the catalogue separates it, else null.</summary>
            public long? Size { get; }

            public int? Precision { get; }

            public int? Scale { get; }
        }

        /// <summary>
        /// The statement that lists <paramref name="tableName"/>'s columns, or null on a provider whose
        /// catalogue this framework does not know how to read.
        /// </summary>
        /// <remarks>
        /// Returning null is a first-class answer, not a failure: it produces an <b>unsupported</b>
        /// report rather than an empty one, so a provider nobody has taught cannot masquerade as a clean
        /// bill of health. The name is interpolated, so it is escaped as a literal by the override —
        /// every current override passes it through <see cref="SqlLiteral.EscapeLiteral"/>
        /// or a parameter.
        /// </remarks>
        protected virtual string? StoredColumnsSql(string tableName) => null;

        /// <summary>
        /// Reads one row of <see cref="StoredColumnsSql"/>'s result. Overridden together with it.
        /// </summary>
        protected virtual StoredColumn ReadStoredColumn(DbDataReader reader) =>
            new StoredColumn(reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1));

        /// <summary>
        /// Renders a stored column into the vocabulary <see cref="AbstractConnectorBase.ConvertType"/>
        /// emits, so the comparison itself stays provider-independent.
        /// </summary>
        protected virtual string RenderStoredType(StoredColumn column) => column.TypeName;

        /// <summary>
        /// Compares what <paramref name="type"/> declares against the table behind it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The declared side is <see cref="AbstractConnectorBase.ConvertType"/> — the same producer
        /// <c>CREATE TABLE</c> uses — so this cannot drift away from the DDL, and a future column-typing
        /// rule is covered without being restated here (§ Conventions' one-producer rule, applied to a
        /// comparison rather than to a name).
        /// </para>
        /// <para>
        /// ⚠ <b>On demand only.</b> This is never called from schema-ensure: it costs a catalogue
        /// round-trip on first use of every store, and a diagnostic that can stop a store starting is the
        /// defect TASK-204 and TASK-254 removed.
        /// </para>
        /// </remarks>
        public SchemaDriftReport DetectDrift(Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            // DataBase.LoadTable returns null for a type carrying no [Table] attribute and no
            // ModelMap registration. A DIAGNOSTIC must not answer that with a NullReferenceException --
            // it is the most likely thing a host passes by mistake (a DTO, a view model), and § TASK-290
            // records this same shape reaching a consumer as an NRE out of schema-ensure. Report it.
            var table = DataBase.LoadTable(type);
            if (table == null || string.IsNullOrEmpty(table.Name))
            {
                return new SchemaDriftReport(type.Name, supported: false, tableExists: false,
                    Array.Empty<ColumnDrift>(),
                    $"{type.FullName} is not a mapped entity: it has no [Table] attribute and no ModelMap registration.");
            }

            var sql = StoredColumnsSql(table.Name);
            if (string.IsNullOrEmpty(sql))
            {
                return new SchemaDriftReport(table.Name, supported: false, tableExists: false,
                    Array.Empty<ColumnDrift>(),
                    $"{GetType().Name} does not expose a column catalogue this framework can read.");
            }

            var stored = ReadStoredColumns(sql!);
            if (stored.Count == 0)
            {
                // Not drift. A store creates its table on first use, so an entity nobody has touched
                // legitimately has no table -- and TASK-211's reader answers a missing table with an
                // empty result rather than throwing, so "absent" and "empty" arrive here identically.
                return new SchemaDriftReport(table.Name, supported: true, tableExists: false,
                    Array.Empty<ColumnDrift>(),
                    $"Table \"{table.Name}\" does not exist yet, or has no columns.");
            }

            var drifts = new List<ColumnDrift>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var field in table.Fields.Values)
            {
                seen.Add(field.Name);

                var declared = ConvertType(field.Type, field);
                if (!stored.TryGetValue(field.Name, out var actual))
                {
                    drifts.Add(new ColumnDrift(table.Name, field.Name, ColumnDriftKind.Missing, declared, null));
                    continue;
                }

                var rendered = RenderStoredType(actual);
                if (!SameColumnType(declared, rendered))
                {
                    drifts.Add(new ColumnDrift(table.Name, field.Name, ColumnDriftKind.TypeMismatch, declared, rendered));
                }
            }

            foreach (var kvp in stored)
            {
                if (!seen.Contains(kvp.Key))
                {
                    drifts.Add(new ColumnDrift(table.Name, kvp.Key, ColumnDriftKind.Unexpected, null, RenderStoredType(kvp.Value)));
                }
            }

            return new SchemaDriftReport(table.Name, supported: true, tableExists: true, drifts);
        }

        private Dictionary<string, StoredColumn> ReadStoredColumns(string sql)
        {
            var result = new Dictionary<string, StoredColumn>(StringComparer.OrdinalIgnoreCase);

            // ⚠ The transform is invoked ONCE PER ROW with the reader already positioned --
            // RunReaderCommandOn drives reader.Read() itself. A transform that loops internally skips
            // the current row and consumes the rest, so exactly the FIRST column goes missing and every
            // table reports a spurious Missing drift. Measured: it cost the first column of every table
            // until the tests caught it.
            foreach (var row in RunReaderCommand(
                command => { command.CommandText = sql; },
                reader => new List<object> { ReadStoredColumn(reader) }))
            {
                foreach (var item in row)
                {
                    if (item is StoredColumn column && !string.IsNullOrEmpty(column.Name))
                    {
                        result[column.Name] = column;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Whether two rendered column types mean the same thing.
        /// </summary>
        /// <remarks>
        /// Case- and space-insensitive, because a catalogue reports its own casing (<c>nvarchar</c>)
        /// while <c>ConvertType</c> emits the framework's (<c>NVARCHAR</c>), and neither is more correct.
        /// Nothing else is normalised away: a difference in width or scale is exactly what this exists to
        /// find, so <c>DECIMAL(18,0)</c> and <c>DECIMAL(18,2)</c> must not compare equal.
        /// </remarks>
        internal static bool SameColumnType(string? declared, string? stored)
        {
            static string Normalize(string? value) =>
                new string((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

            return Normalize(declared) == Normalize(stored);
        }
    }
}
