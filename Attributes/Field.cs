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
}
