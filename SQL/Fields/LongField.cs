using System;
using System.Data;
using System.Data.Common;

namespace Birko.Data.SQL.Fields
{
    /// <summary>
    /// A 64-bit integer column (<c>BIGINT</c> / SQLite <c>INTEGER</c>).
    /// <para>
    /// SH-H037: before this existed, a <c>long</c> property matched no arm of
    /// <see cref="AbstractField.CreateAbstractField"/> and produced a null field, so the column was never
    /// created, never written and never read — silent data loss. The provider connectors already mapped
    /// <see cref="DbType.Int64"/>; only the field class was missing.
    /// </para>
    /// </summary>
    public class LongField : AbstractField
    {
        public LongField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false, bool autoincrement = false)
            : base(property, name, DbType.Int64, primary, true, unique, autoincrement)
        {
        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            Property.SetValue(value, reader.GetInt64(index), null);
        }
    }

    public class NullableLongField : LongField
    {
        public NullableLongField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false, bool autoincrement = false)
            : base(property, name, primary, unique, autoincrement)
        {
            IsNotNull = false;
        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            if (reader.IsDBNull(index))
            {
                Property.SetValue(value, null, null);
            }
            else
            {
                base.Read(value, reader, index);
            }
        }
    }
}
