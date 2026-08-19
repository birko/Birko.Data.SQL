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
    /// Note: only a full (non-partial) unique index is emitted. Partial/filtered unique indexes
    /// (e.g. <c>WHERE Number &lt;&gt; ''</c> to allow multiple empty-string drafts) are NOT supported —
    /// they are not portable across SQLite/PostgreSQL (partial), MSSQL (filtered), and MySQL (neither).
    /// A composite unique index therefore fits columns that are always populated; columns left empty on
    /// drafts must rely on an application-level guarded allocator instead.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true, AllowMultiple = true)]
    public class IndexedField : Field
    {
        public string Name { get; }
        public int Order { get; }
        public bool IsDescending { get; }
        public bool IsUnique { get; }

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
    /// <para>Set <see cref="IsUnique"/> for a composite UNIQUE constraint. As with <see cref="IndexedField"/>,
    /// only a full (non-partial) unique index is emitted — partial/filtered unique indexes are not supported
    /// (not portable across providers), so this fits always-populated columns.</para>
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
    public class CompositeIndex : System.Attribute
    {
        public string Name { get; }
        public string[] Properties { get; }
        public bool IsUnique { get; set; }

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
