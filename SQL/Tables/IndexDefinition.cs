using System.Collections.Generic;

namespace Birko.Data.SQL.Tables
{
    public class IndexDefinition
    {
        public string Name { get; set; } = null!;
        /// <summary>When true, a UNIQUE index is emitted (a composite unique constraint over <see cref="Columns"/>).</summary>
        public bool Unique { get; set; }
        public List<IndexColumn> Columns { get; } = new();

        /// <summary>
        /// Null-tests restricting which rows enter the index — a partial (PostgreSQL/SQLite) or filtered
        /// (MSSql) index. Empty for an ordinary index, which is the overwhelming majority.
        /// </summary>
        /// <remarks>
        /// TASK-273. This sits on the <b>index</b> rather than on <see cref="IndexColumn"/> deliberately: a
        /// predicate column need not be a key column at all. The measured motivating case is a key of
        /// <c>(TenantGuid, Number)</c> filtered by <c>DeletedAt IS NULL</c> — unique among rows that are not
        /// soft-deleted — which a per-column flag structurally cannot express.
        /// <para>
        /// Order is significant only in that it must be <b>deterministic</b>: the emitted statement is
        /// compared byte-for-byte by tests that prove an ordinary index's DDL is unchanged, and a re-run must
        /// produce the same text. <c>DataBase.LoadIndexes</c> is the only producer and appends
        /// <c>WhereNotNull</c> terms before <c>WhereNull</c> ones, each in declaration order, de-duplicated.
        /// </para>
        /// </remarks>
        public List<IndexPredicate> Predicates { get; } = new();
    }

    /// <summary>
    /// One null-test in an index's partial/filtered predicate: <c>ColumnName IS NULL</c> when
    /// <see cref="RequireNull"/>, otherwise <c>ColumnName IS NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// TASK-273. Only these two shapes exist, and that is the point: the column name is resolved from table
    /// metadata and the operator is one of two constants, so nothing a caller typed can reach the emitted
    /// DDL. A general predicate string (<c>WHERE IsActive = 1</c>) would be caller text interpolated into
    /// <c>CREATE INDEX</c> — unparameterisable, and unvalidatable by
    /// <c>DataBase.ValidateIndexFieldIdentifier</c>, which checks a single bare identifier. That is the
    /// SH-H023 sink family, and it is why the supported surface is two column lists rather than a predicate.
    /// </remarks>
    public class IndexPredicate
    {
        public string ColumnName { get; set; } = null!;

        /// <summary>True emits <c>IS NULL</c>; false emits <c>IS NOT NULL</c>.</summary>
        public bool RequireNull { get; set; }
    }

    public class IndexColumn
    {
        public string ColumnName { get; set; } = null!;
        public int Order { get; set; }
        public bool IsDescending { get; set; }
    }
}
