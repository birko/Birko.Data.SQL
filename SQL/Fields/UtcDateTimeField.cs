using System;
using System.Data;
using System.Data.Common;

namespace Birko.Data.SQL.Fields
{
    /// <summary>
    /// A <see cref="DateTime"/> property marked <c>[UtcField]</c> — an <b>instant</b>, stored in the provider's
    /// timezone-aware column type and read back as <see cref="DateTimeKind.Utc"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sibling of <see cref="DateTimeField"/>, which is a wall clock (TASK-256). See the
    /// <c>Birko.Data.SQL.Attributes.UtcField</c> attribute for the two rules and why a caller's original offset
    /// is normalised away on every provider rather than preserved on the two that could.
    /// </para>
    /// <para>
    /// <b>Why <c>Write</c> returns a <see cref="DateTimeOffset"/> even though the property is a
    /// <see cref="DateTime"/>.</b> The bound value's CLR type is what every provider's parameter binding
    /// dispatches on, and it is the only lever available here — <c>AddParameter</c> takes
    /// <c>(command, name, value)</c> with no field context. Returning a <c>DateTimeOffset</c> buys three things
    /// at once: Npgsql binds it to <c>timestamptz</c> and SqlClient to <c>datetimeoffset</c> (the right type on
    /// both providers that have one), the instant is unambiguous rather than being re-interpreted through the
    /// session time zone, and — critically — <c>PostgreSQLConnector.NormalizeTimestampValue</c> matches
    /// <c>is DateTime</c> and therefore leaves it alone. That last point is not incidental: TASK-256 strips
    /// <c>Kind</c> from every bound <c>DateTime</c> on PostgreSQL, and a <c>Kind=Utc</c> value stripped to
    /// <c>Unspecified</c> is inferred as <c>timestamp</c>, which PostgreSQL then reads in the session's time
    /// zone when assigning it to a <c>timestamptz</c> column — a different instant, silently. Keeping this
    /// value out of that helper's type test is what makes the two features compose without either one growing
    /// per-column knowledge it has no way to obtain.
    /// </para>
    /// <para>
    /// <b>Why <c>Read</c> uses <c>GetFieldValue&lt;DateTimeOffset&gt;</c> and not <c>GetDateTime</c>.</b>
    /// Measured across all four providers, it is the only uniform path — and a field cannot branch per provider.
    /// <c>GetDateTime</c> is wrong or fatal on three of them: it throws <c>InvalidCastException</c> on MSSql's
    /// <c>datetimeoffset</c>, returns <c>Kind=Local</c> on SQLite, and returns <c>Unspecified</c> on MySQL.
    /// <c>GetFieldValue&lt;DateTimeOffset&gt;</c> returns the exact instant on every one, and
    /// <see cref="DateTimeOffset.UtcDateTime"/> hands back a <c>DateTime</c> already carrying
    /// <c>DateTimeKind.Utc</c>.
    /// </para>
    /// </remarks>
    public class UtcDateTimeField : AbstractField
    {
        public UtcDateTimeField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
            : base(property, name, DbType.DateTimeOffset, primary, true, unique)
        {
        }

        /// <summary>
        /// Normalises the property's value to a definite UTC instant and binds it as a
        /// <see cref="DateTimeOffset"/> at offset zero.
        /// </summary>
        /// <remarks>
        /// <c>Unspecified</c> is taken as UTC rather than local, because <c>[UtcField]</c> is a declaration that
        /// the property holds UTC — reading it as local would make the stored instant depend on the machine the
        /// write happened to run on. <c>Local</c> is converted.
        /// </remarks>
        public override object? Write(object value)
        {
            var raw = base.Write(value);
            if (raw is not DateTime dateTime)
            {
                return raw;
            }
            var utc = dateTime.Kind switch
            {
                DateTimeKind.Utc => dateTime,
                DateTimeKind.Local => dateTime.ToUniversalTime(),
                _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            };
            return new DateTimeOffset(utc);
        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            Property.SetValue(value, reader.GetFieldValue<DateTimeOffset>(index).UtcDateTime, null);
        }
    }

    public class NullableUtcDateTimeField : UtcDateTimeField
    {
        public NullableUtcDateTimeField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
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
