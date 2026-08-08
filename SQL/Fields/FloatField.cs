using System;
using System.Data;
using System.Data.Common;

namespace Birko.Data.SQL.Fields
{
    /// <summary>
    /// A 4-byte IEEE floating-point column (<c>REAL</c> / MySQL <c>FLOAT</c>). See
    /// <see cref="LongField"/> for the SH-H037 background.
    /// <para>
    /// <see cref="DbType.Single"/> is the <see cref="DbType"/> most often grouped with the integral types
    /// by mistake — PostgreSQL and MSSql both shipped that bug and fixed it under CR-H087, and SQLite still
    /// carried it when this field class was added. A <c>float</c> column declared as an integer truncates
    /// every fraction it is given, silently.
    /// </para>
    /// </summary>
    public class FloatField : AbstractField
    {
        public FloatField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
            : base(property, name, DbType.Single, primary, true, unique)
        {
        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            Property.SetValue(value, reader.GetFloat(index), null);
        }
    }

    public class NullableFloatField : FloatField
    {
        public NullableFloatField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
            : base(property, name, primary, unique)
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
