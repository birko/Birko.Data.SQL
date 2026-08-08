using System;
using System.Data;
using System.Data.Common;

namespace Birko.Data.SQL.Fields
{
    /// <summary>
    /// An 8-byte IEEE floating-point column (<c>DOUBLE PRECISION</c> / <c>DOUBLE</c> / <c>FLOAT</c> /
    /// SQLite <c>REAL</c>). See <see cref="LongField"/> for the SH-H037 background.
    /// <para>
    /// Deliberately NOT routed through <see cref="DecimalField"/>: <c>decimal</c> is exact base-10 and
    /// <c>double</c> is binary floating point, so mapping one onto the other would round-trip a value the
    /// caller never stored. They are different columns on every provider.
    /// </para>
    /// </summary>
    public class DoubleField : AbstractField
    {
        public DoubleField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
            : base(property, name, DbType.Double, primary, true, unique)
        {
        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            Property.SetValue(value, reader.GetDouble(index), null);
        }
    }

    public class NullableDoubleField : DoubleField
    {
        public NullableDoubleField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
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
