using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace Birko.Data.SQL.Attributes
{
    [System.AttributeUsage(System.AttributeTargets.Property)]
    public abstract class Field : System.Attribute
    {
    }

    /// <summary>
    /// Marks a property to be excluded from SQL field mapping.
    /// Properties with this attribute are skipped during table creation and CRUD operations.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class IgnoreField : Field
    {
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class NamedField : Field
    {
        public string? Name { get; internal set; } = null;
        public NamedField(string? name = null)
        {
            Name = name;
        }
    }


    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class UniqueField : Field
    {
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class PrimaryField : Field
    {
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class IncrementField : Field
    {

    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class RequiredField : Field
    {
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class MaxLengthField : Field
    {
        public int MaxLength = 0;
        public MaxLengthField(int maxLength = 0) : base()
        {
            MaxLength = maxLength;
        }
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class PrecisionField : Field
    {
        public int Precision = 0;
        public PrecisionField( int precision = 0) : base()
        {
            Precision = precision;
        }
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class ScaleField : Field
    {
        public int Scale = 0;
        public ScaleField(int scale = 0) : base()
        {
            Scale = scale;
        }
    }

    /// <summary>
    /// Marks a property as part of a named database index.
    /// Multiple properties sharing the same index name form a composite index.
    /// Use Order to control column position within composite indexes.
    /// AllowMultiple — a single property can participate in multiple indexes.
    ///
    /// Set <see cref="IsUnique"/> on any contributing property to make the whole index UNIQUE
    /// (a composite unique constraint, e.g. per-tenant uniqueness over (TenantGuid, Number)).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Partial (filtered) indexes ARE supported</b> via <see cref="WhereNotNull"/> and
    /// <see cref="WhereNull"/> — see <see cref="CompositeIndex"/> for the full rationale, the measured
    /// per-provider behaviour and the MySQL policy. This doc comment previously stated the opposite
    /// ("only a full (non-partial) unique index is emitted … NOT supported … not portable"), which was the
    /// record of a decision TASK-273 reversed on measurement, so it is corrected rather than left standing.
    /// </para>
    /// <para>
    /// <b>Both lists are merged across every attribute contributing to the same index name</b>, exactly as
    /// <see cref="IsUnique"/> is: the union is taken, duplicates collapse, and the result is validated once
    /// per index. Two properties naming the same column in opposite lists is a contradiction that indexes no
    /// rows, and it is only visible after the merge — so that is where it throws.
    /// </para>
    /// </remarks>
    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true, AllowMultiple = true)]
    public class IndexedField : Field
    {
        public string Name { get; }
        public int Order { get; }
        public bool IsDescending { get; }
        public bool IsUnique { get; }

        /// <inheritdoc cref="CompositeIndex.WhereNotNull"/>
        public string[] WhereNotNull
        {
            get => _whereNotNull;
            set => _whereNotNull = value ?? System.Array.Empty<string>();
        }

        /// <inheritdoc cref="CompositeIndex.WhereNull"/>
        public string[] WhereNull
        {
            get => _whereNull;
            set => _whereNull = value ?? System.Array.Empty<string>();
        }

        private string[] _whereNotNull = System.Array.Empty<string>();
        private string[] _whereNull = System.Array.Empty<string>();

        public IndexedField(string name, int order = 0, bool isDescending = false, bool IsUnique = false)
        {
            Name = name;
            Order = order;
            IsDescending = isDescending;
            this.IsUnique = IsUnique;
        }
    }

    /// <summary>
    /// Declares a named composite index at the CLASS level, listing the participating properties in column
    /// order. Unlike per-property <see cref="IndexedField"/>, this can reference properties declared on a
    /// base class (e.g. a shared tenant entity's TenantGuid) together with a property on the derived entity —
    /// the only safe way to form a composite such as (TenantGuid, Number) when the discriminator lives on a
    /// base type.
    ///
    /// <para>Inherited = false: the index is declared only on the annotated class, NOT propagated to every
    /// subclass — that would collide on the database-global index name and mis-apply the constraint.
    /// AllowMultiple = true: a class may declare several composite indexes.</para>
    ///
    /// <para>Set <see cref="IsUnique"/> for a composite UNIQUE constraint. For uniqueness over a column that
    /// is allowed to be empty, add that column to <see cref="WhereNotNull"/> — a full unique index over a
    /// nullable column does not work on SQL Server (see below).</para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Partial (filtered) indexes, and why the previous "not portable" note was wrong (TASK-273).</b>
    /// <see cref="WhereNotNull"/> and <see cref="WhereNull"/> restrict which rows enter the index, emitted as
    /// <c>… WHERE &lt;col&gt; IS [NOT] NULL</c>. Measured 2026-08-22 on SQL Server 2022 (16.0.4265.3),
    /// PostgreSQL 16.15, TimescaleDB 2/PG16, MySQL 8.4.11 and SQLite 3.53.3:
    /// </para>
    /// <list type="bullet">
    /// <item><b>A full unique index over a nullable column is BROKEN on SQL Server, and only there.</b> It
    /// treats NULLs as equal, so <c>UNIQUE (TenantGuid, ExternalId)</c> admits one NULL row per tenant and
    /// rejects the second ordinary row with <c>Msg 2601</c>. PostgreSQL, SQLite and MySQL treat NULLs as
    /// distinct and admit any number. So this is not a missing optimisation — it breaks inserts, on the one
    /// provider consumers do not test on.</item>
    /// <item><b>A predicate column need not be part of the key.</b> <c>WHERE DeletedAt IS NULL</c> over a key
    /// of <c>(TenantGuid, Number)</c> works on all three providers that support partial indexes — that is
    /// "unique among rows that are not soft-deleted", and <c>ISoftDeletable.DeletedAt</c> is null-means-active
    /// on every entity that implements it.</item>
    /// <item><b>MySQL supports no partial index at all</b> (<c>ERROR 1064</c>), and the two polarities are
    /// therefore treated differently — see the policy below. </item>
    /// </list>
    /// <para>
    /// <b>MySQL policy: a tail is dropped only where dropping it means the same thing.</b> MySQL supports no
    /// partial index, so each declaration is classified rather than waved through — the test is
    /// "does the unfiltered index enforce what was declared?":
    /// </para>
    /// <list type="bullet">
    /// <item><b>Non-unique index → dropped.</b> It constrains nothing, so a wider index is semantically
    /// identical.</item>
    /// <item><b>Unique, <see cref="WhereNotNull"/> over one of the index's own KEY columns → dropped.</b>
    /// MySQL treats NULLs as distinct, so a row with NULL there already has a distinct key and is exempt.</item>
    /// <item><b>Anything else → refused.</b> A <see cref="WhereNull"/> tail, and a <see cref="WhereNotNull"/>
    /// tail over a <i>non-key</i> column: in both cases the unfiltered index applies the constraint to rows
    /// the declaration excludes, i.e. it is <b>stricter</b> than declared and rejects legitimate rows.
    /// <c>UNIQUE (TenantGuid, Number) WHERE ApprovedAt IS NOT NULL</c> dropped to
    /// <c>UNIQUE (TenantGuid, Number)</c> starts refusing two unapproved drafts sharing a number.</item>
    /// </list>
    /// <para>
    /// A refusal is recorded by schema-ensure (<c>IndexCreationFailures</c>, TASK-204) and thrown by an
    /// explicit <c>CreateIndexes</c> call. MySQL 8 <i>could</i> emulate the filtered index with a functional
    /// key part (measured working); that is deliberately not done — see TASK-273 § Out of scope.
    /// </para>
    /// <para>
    /// <b>Names are property names, resolved against the entity's mapped columns</b> exactly like
    /// <see cref="Properties"/>, so <c>[NamedField]</c> / <c>ModelMap</c> remaps are honoured and no caller
    /// text ever reaches the DDL. A name that is not a mapped property, a column this framework declares
    /// <c>NOT NULL</c> (or a primary key), or the same column in both lists <b>throws at table load</b>.
    /// </para>
    /// <para>
    /// <b>What the nullability check can and cannot see.</b> C# nullable-reference annotations are not read,
    /// so <c>string</c> and <c>string?</c> are the same thing here — both nullable unless
    /// <c>[RequiredField]</c> / <c>[Required]</c> is present. A <see cref="WhereNotNull"/> naming an
    /// always-populated <c>string</c> is therefore accepted and merely vacuous rather than refused; only a
    /// column this framework actually declares <c>NOT NULL</c> is rejected.
    /// </para>
    /// <para>
    /// <b>Limit: a CHANGED predicate is not applied to an existing database.</b> Schema-ensure matches an
    /// index by name and never alters one, so editing these lists on an entity whose index already exists is
    /// silently ignored on every provider (measured: SQL Server's guard skips and
    /// <c>sys.indexes.filter_definition</c> keeps its original value). Drop the index by hand to re-create
    /// it. Same position as TASK-257's columns and TASK-245's same-name-different-columns case.
    /// </para>
    /// </remarks>
    [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
    public class CompositeIndex : System.Attribute
    {
        public string Name { get; }
        public string[] Properties { get; }
        public bool IsUnique { get; set; }

        /// <summary>
        /// Property names that must be NOT NULL for a row to enter the index, emitted as
        /// <c>WHERE &lt;col&gt; IS NOT NULL</c> (one term per name, joined with <c>AND</c>). The
        /// "unique when set" case: a nullable business key such as an external id.
        /// </summary>
        public string[] WhereNotNull
        {
            get => _whereNotNull;
            set => _whereNotNull = value ?? System.Array.Empty<string>();
        }

        /// <summary>
        /// Property names that must be NULL for a row to enter the index, emitted as
        /// <c>WHERE &lt;col&gt; IS NULL</c>. The soft-delete case: unique among live rows only, where
        /// "live" is <c>DeletedAt IS NULL</c>. Refused on MySQL — see the class remarks.
        /// </summary>
        public string[] WhereNull
        {
            get => _whereNull;
            set => _whereNull = value ?? System.Array.Empty<string>();
        }

        private string[] _whereNotNull = System.Array.Empty<string>();
        private string[] _whereNull = System.Array.Empty<string>();

        public CompositeIndex(string name, params string[] properties)
        {
            Name = name;
            Properties = properties ?? System.Array.Empty<string>();
        }
    }

    /// <summary>
    /// Declares that a <see cref="System.DateTime"/> property holds an <b>instant</b>, not a wall clock, and
    /// must be stored in the provider's timezone-aware column type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two rules, and why both exist (TASK-256, TASK-263).</b> A plain Birko <c>DateTime</c> column is a
    /// <i>wall clock</i>: it stores the value's date and time components as supplied, <c>DateTimeKind</c> is not
    /// persisted, and every read returns <c>Unspecified</c>. That is correct for a local calendar date — a
    /// notice date, an opening time — but it cannot say <i>which instant</i> a timestamp names. Marking the
    /// property <c>[UtcField]</c> switches it to the other meaning: the value is normalised to UTC on write,
    /// stored in <c>TIMESTAMPTZ</c> / <c>DATETIMEOFFSET</c> where the provider has one, and read back as
    /// <c>DateTimeKind.Utc</c>. The two coexist per property on the same entity.
    /// </para>
    /// <para>
    /// <b>What it does NOT promise: your original offset.</b> A caller's offset is normalised away on every
    /// provider, deliberately. MySQL's <c>DATETIME</c> and SQLite's numeric affinity cannot carry one, and a
    /// field cannot behave differently per provider — <c>Tables.Table</c> holds no connector and
    /// <c>AbstractField.Read</c> is reached through the provider-blind <c>DataBase.Read</c>. So the promise is
    /// uniform across all four providers and deliberately narrower than the column type suggests: the
    /// <b>instant is exact</b>, and it reads back as UTC. This is why the opt-in is an attribute on a
    /// <c>DateTime</c> rather than a <c>DateTimeOffset</c> property — a <c>DateTimeOffset</c> would advertise
    /// an offset that cannot survive on half the supported providers.
    /// </para>
    /// <para>
    /// <b>A value with no <c>Kind</c> is read as UTC</b>, not as local: the attribute is a declaration that the
    /// property holds UTC, so <c>Unspecified</c> is taken at its word. <c>Local</c> is converted.
    /// </para>
    /// <para>
    /// Applying this to a non-<c>DateTime</c> property throws <c>FieldAttributeException</c> at table load. An
    /// attribute that silently did nothing would leave the model claiming an instant while the column stored a
    /// wall clock — the § SH-H037 shape.
    /// </para>
    /// </remarks>
    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public class UtcField : Field
    {
        public UtcField() : base() { }
    }

}
