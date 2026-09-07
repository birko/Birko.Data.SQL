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
        /// <summary>
        /// The declared column width in bytes, or null for the provider's unbounded blob type.
        /// </summary>
        /// <remarks>
        /// <para>
        /// TASK-266. Before this a <c>byte[]</c> column could not be given a width at all —
        /// <c>[MaxLengthField(32)]</c> on one was <b>silently dropped</b> — which mattered because neither
        /// SQL Server nor MySQL can use an unbounded blob as an index key: measured on 16.0.4265.3 an
        /// inline <c>UNIQUE</c> over <c>VARBINARY(MAX)</c> is <b>Msg 1919 + Msg 1750</b> and is not
        /// catchable by <c>TRY/CATCH</c>, so the whole <c>CREATE TABLE</c> aborts; on MySQL 8.4.11 it is
        /// <b>ERROR 1170</b>. A hash or fingerprint — the plausible real binary unique key — has a natural
        /// fixed width, so being able to say it is the point.
        /// </para>
        /// <para>
        /// Spelled <c>MaxLength</c> and deliberately <b>not</b> <c>Lenght</c> like
        /// <see cref="CharField.Lenght"/>. That misspelling is shipped public API and renaming it would be
        /// a breaking change, but a new member should not inherit it, and <c>MaxLength</c> matches
        /// <c>MaxLengthField</c> and <c>FieldDescriptor.MaxLength</c>.
        /// </para>
        /// </remarks>
        public int? MaxLength = null;

        public BinaryField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false, int? maxLength = null)
            : base(property, name, DbType.Binary, primary, false, unique)
        {
            MaxLength = maxLength;
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
