using System;
using System.Data;
using System.Data.Common;

namespace Birko.Data.SQL.Fields
{
    /// <summary>
    /// A 16-bit integer column (<c>SMALLINT</c> / SQLite <c>INTEGER</c>). See
    /// <see cref="LongField"/> for the SH-H037 background — a <c>short</c> property produced no column at all.
    /// </summary>
    public class ShortField : AbstractField
    {
        public ShortField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false, bool autoincrement = false)
            : base(property, name, DbType.Int16, primary, true, unique, autoincrement)
        {
        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            Property.SetValue(value, reader.GetInt16(index), null);
        }
    }

    public class NullableShortField : ShortField
    {
        public NullableShortField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false, bool autoincrement = false)
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
