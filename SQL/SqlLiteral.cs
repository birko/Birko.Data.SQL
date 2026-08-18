using System;

namespace Birko.Data.SQL
{
    /// <summary>
    /// The one producer for embedding text inside a SQL string literal.
    /// </summary>
    /// <remarks>
    /// <b>Values belong in a <c>DbParameter</c>; this is for the text that cannot be parameterised.</b>
    /// A handful of statements take a name where the grammar allows only a literal — PostgreSQL's
    /// <c>create_hypertable('"T"', 'ts')</c>, TimescaleDB's policy functions, MSSql's
    /// <c>OBJECT_ID('T')</c>, every <c>sys.indexes WHERE name = '…'</c> probe — and those have no
    /// parameter to bind to. This is the escaping for exactly that case, and it is not a licence to
    /// interpolate a value.
    /// <para>
    /// It existed already, twenty times over: <c>Replace("'", "''")</c> is hand-written in
    /// <c>SqlIndexManager</c>, <c>MSSqlConnector</c>, all four index managers, the Cosmos data migrator
    /// and elsewhere — the same expression, re-derived per sink, which is how one of them ends up
    /// forgetting. TASK-253 named it so a sink can reuse the rule rather than rewrite it.
    /// </para>
    /// <para>
    /// <b>Premise, stated once because everything here depends on it:</b> doubling <c>'</c> is
    /// <i>complete</i> containment — text inside a literal cannot leave it — but only while
    /// <c>standard_conforming_strings</c> is <c>on</c>. That is PostgreSQL's default since 9.1 and the
    /// ANSI behaviour on the other three providers. With it off, backslash escapes revive and
    /// <c>\'</c> breaks out. Every literal-interpolating sink in this framework has always rested on
    /// this; it is written down here rather than assumed.
    /// </para>
    /// </remarks>
    public static class SqlLiteral
    {
        /// <summary>
        /// Escapes <paramref name="value"/> for placement inside a single-quoted SQL literal, by doubling
        /// every embedded single quote. Does <b>not</b> add the surrounding quotes — the caller composes
        /// those, because several sinks nest a quoted identifier inside the literal and need to control
        /// the order of the two escapes.
        /// </summary>
        /// <param name="value">The text to escape. A null is treated as empty.</param>
        public static string EscapeLiteral(string? value)
            => value == null ? string.Empty : value.Replace("'", "''");
    }
}
