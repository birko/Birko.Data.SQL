using System;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace Birko.Data.SQL.Fields
{
    /// <summary>
    /// Maps <see cref="TimeOnly"/> / <see cref="Nullable{TimeOnly}"/> to a fixed-width
    /// <c>HH:mm:ss</c> text column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Added for SH-H037 (TASK-197) / Symbio TASK-361. <c>TimeOnly</c> was one more BCL value type
    /// <c>CreateAbstractField</c> had no arm for, which was harmless while an unmapped type was silently
    /// skipped, and became a hard failure the moment the mapper started throwing instead: the exception is
    /// raised at TABLE LOAD, so a single unmapped property makes <b>every</b> route on that entity return
    /// 500, not just the ones that touch the column.
    /// </para>
    /// <para>
    /// <b>Why text and not <see cref="DbType.Time"/>.</b> <c>AbstractConnectorBase</c> maps
    /// <c>DbType.Time</c> to <c>typeof(DateTime)</c>, so the value would round-trip through a type that
    /// carries a date component this one does not have — and the dialects disagree about what a bare TIME
    /// column even is (SQLite has no time type at all). <c>DbType.String</c> renders as TEXT/VARCHAR
    /// everywhere and needs no per-dialect special case.
    /// </para>
    /// <para>
    /// <b>Why the width is fixed.</b> Text ordering is lexical, so <c>HH:mm:ss</c> compares correctly with
    /// <c>&lt;</c>, <c>&gt;</c> and <c>BETWEEN</c> only while every value is the same length — <c>9:05</c>
    /// would sort after <c>10:00</c>. This is the same class of defect as TASK-355, where a 10-character
    /// date string was compared against a full timestamp and the shorter prefix sorted first. Zero-padding
    /// every component is what keeps range queries on a time column honest.
    /// </para>
    /// <para>
    /// The colons are escaped in the format string so they stay literal: in a custom date/time format
    /// <c>:</c> means "culture's time separator", which is not <c>:</c> everywhere. Combined with
    /// <see cref="CultureInfo.InvariantCulture"/> the stored shape is identical on every machine — a
    /// server-locale-dependent column would be unreadable by a differently configured replica.
    /// </para>
    /// <para>
    /// Sub-second precision is deliberately dropped: <c>TimeOnly</c> is used for wall-clock schedule
    /// boundaries ("active from 08:00"), and storing ticks would make equality comparisons against a
    /// caller-supplied <c>HH:mm</c> fail for reasons no caller could see.
    /// </para>
    /// </remarks>
    public class TimeOnlyField : AbstractField
    {
        /// <summary>Fixed-width, culture-independent wire shape. See the remarks on ordering.</summary>
        internal const string Format = @"HH\:mm\:ss";

        public TimeOnlyField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
            : base(property, name, DbType.String, primary, true, unique)
        {
        }

        public override object? Write(object value)
        {
            var raw = Property.GetValue(value, null);
            return raw is TimeOnly time
                ? time.ToString(Format, CultureInfo.InvariantCulture)
                : null;
        }

        /// <summary>
        /// The non-nullable arm: the property cannot hold <c>null</c>, so an unreadable or NULL column
        /// falls back to <c>default</c> (midnight) rather than throwing. A row that cannot be materialised
        /// takes down every read of the table, which is precisely the failure this field exists to remove.
        /// </summary>
        public override void Read(object value, DbDataReader reader, int index)
        {
            Property.SetValue(value, Parse(reader.GetValue(index)) ?? default(TimeOnly), null);
        }

        /// <summary>
        /// Accepts the canonical shape first, then falls back to a lenient invariant parse so a column
        /// written before this mapping existed (or by another tool) still loads rather than throwing at
        /// read time — the failure mode being avoided is the same one this field was added to fix.
        /// </summary>
        protected static TimeOnly? Parse(object? raw)
        {
            if (raw is null || raw is DBNull) return null;
            if (raw is TimeOnly already) return already;
            if (raw is DateTime dt) return TimeOnly.FromDateTime(dt);
            if (raw is TimeSpan ts) return TimeOnly.FromTimeSpan(ts);

            var text = raw as string ?? raw.ToString();
            if (string.IsNullOrWhiteSpace(text)) return null;

            return TimeOnly.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)
                ? exact
                : TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose)
                    ? loose
                    : null;
        }
    }

    public class NullableTimeOnlyField : TimeOnlyField
    {
        public NullableTimeOnlyField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
            : base(property, name, primary, unique)
        {
            IsNotNull = false;
        }

        /// <summary>
        /// Sets a <see cref="Nullable{TimeOnly}"/> directly rather than delegating to the base arm, whose
        /// <c>?? default</c> fallback would turn an unparseable value into midnight — a real time — instead
        /// of the <c>null</c> that honestly says "no boundary set".
        /// </summary>
        public override void Read(object value, DbDataReader reader, int index)
        {
            Property.SetValue(value, reader.IsDBNull(index) ? null : Parse(reader.GetValue(index)), null);
        }
    }
}
