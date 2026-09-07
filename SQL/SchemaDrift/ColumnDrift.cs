using System.Collections.Generic;

namespace Birko.Data.SQL.SchemaDrift
{
    /// <summary>
    /// How a stored column differs from what the model declares.
    /// </summary>
    public enum ColumnDriftKind
    {
        /// <summary>
        /// The model declares the column and the table does not have it. `CREATE TABLE IF NOT EXISTS`
        /// never adds a column to an existing table, so this is what a newly-added property looks like
        /// against a database created before it.
        /// </summary>
        Missing = 0,

        /// <summary>
        /// Both have the column and the stored type is not the one <c>ConvertType</c> would emit today.
        /// The case TASK-269 exists for.
        /// </summary>
        TypeMismatch = 1,

        /// <summary>
        /// The table has a column the model does not declare. Reported, never actioned — a removed
        /// property and a column another system owns look identical from here.
        /// </summary>
        Unexpected = 2,
    }

    /// <summary>
    /// One column's disagreement between a model and the table behind it.
    /// </summary>
    /// <remarks>
    /// TASK-269. <see cref="Declared"/> comes from <c>AbstractConnectorBase.ConvertType</c> — the same
    /// method <c>CREATE TABLE</c> uses — so this record cannot disagree with the DDL about what the
    /// column should be, and every column-typing rule the framework has (TASK-257, TASK-264, TASK-265,
    /// TASK-266, TASK-275) is covered without being restated here.
    /// </remarks>
    public sealed class ColumnDrift
    {
        public ColumnDrift(string table, string column, ColumnDriftKind kind, string? declared, string? stored)
        {
            Table = table;
            Column = column;
            Kind = kind;
            Declared = declared;
            Stored = stored;
        }

        /// <summary>The table as the framework names it.</summary>
        public string Table { get; }

        /// <summary>The column name.</summary>
        public string Column { get; }

        /// <summary>What kind of disagreement this is.</summary>
        public ColumnDriftKind Kind { get; }

        /// <summary>
        /// The type <c>ConvertType</c> would emit for this field today, or null for
        /// <see cref="ColumnDriftKind.Unexpected"/>, where the model has no field to ask about.
        /// </summary>
        public string? Declared { get; }

        /// <summary>
        /// The type the table actually carries, rendered into <c>ConvertType</c>'s vocabulary by the
        /// provider, or null for <see cref="ColumnDriftKind.Missing"/>.
        /// </summary>
        public string? Stored { get; }

        public override string ToString() => Kind switch
        {
            ColumnDriftKind.Missing => $"{Table}.{Column}: declared {Declared}, not present",
            ColumnDriftKind.Unexpected => $"{Table}.{Column}: present as {Stored}, not declared",
            _ => $"{Table}.{Column}: declared {Declared}, stored {Stored}",
        };
    }

    /// <summary>
    /// The outcome of comparing one entity's declared columns against the table behind it.
    /// </summary>
    /// <remarks>
    /// ⚠ <b><see cref="Supported"/> is not the same as "no drift", and conflating them is the whole
    /// point of having it.</b> A provider that cannot be asked, or a table that does not exist yet,
    /// must not read as a clean bill of health — that is the silence TASK-204's channel already
    /// demonstrates, where "nobody reported anything" and "nothing is wrong" were indistinguishable
    /// for the life of the framework.
    /// </remarks>
    public sealed class SchemaDriftReport
    {
        public SchemaDriftReport(string table, bool supported, bool tableExists, IReadOnlyList<ColumnDrift> drifts, string? reason = null)
        {
            Table = table;
            Supported = supported;
            TableExists = tableExists;
            Drifts = drifts;
            Reason = reason;
        }

        public string Table { get; }

        /// <summary>
        /// False when this provider has no column catalogue this framework knows how to read. The
        /// report then carries no drifts and asserts nothing.
        /// </summary>
        public bool Supported { get; }

        /// <summary>
        /// False when the table is not there at all. Not drift: a store creates its table lazily on
        /// first use, so an untouched entity legitimately has none.
        /// </summary>
        public bool TableExists { get; }

        public IReadOnlyList<ColumnDrift> Drifts { get; }

        /// <summary>Why the report is unsupported or empty, when that needs saying.</summary>
        public string? Reason { get; }

        /// <summary>
        /// True only when the question was actually answered and the answer was "no disagreement".
        /// </summary>
        public bool IsClean => Supported && TableExists && Drifts.Count == 0;
    }
}
