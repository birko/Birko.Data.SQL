using System;
using System.Data;
using System.Data.Common;

namespace Birko.Data.SQL.Fields
{
    /// <summary>
    /// A binary blob column (<c>BLOB</c> / <c>BYTEA</c> / <c>LONGBLOB</c> / <c>VARBINARY(MAX)</c>) backing a
    /// <c>byte[]</c> property. See <see cref="LongField"/> for the SH-H037 background.
    /// <para>
    /// <c>byte[]</c> is a reference type, so it follows <see cref="StringField"/>'s nullability convention
    /// rather than the value-type <c>Nullable*</c> pairing: nullable by default, and NOT NULL only when the
    /// model asks for it via <c>[RequiredField]</c> / <c>[Required]</c>. An empty array and a null are
    /// therefore distinct stored values, not two spellings of the same one.
    /// </para>
    /// </summary>
    public class BinaryField : AbstractField
    {
        public BinaryField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
            : base(property, name, DbType.Binary, primary, false, unique)
        {
        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            if (reader.IsDBNull(index))
            {
                Property.SetValue(value, null, null);
            }
            else
            {
                Property.SetValue(value, reader.GetFieldValue<byte[]>(index), null);
            }
        }
    }
}
