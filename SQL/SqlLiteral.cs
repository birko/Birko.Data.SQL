using System;

namespace Birko.Data.SQL
{
    /// <summary>
    /// The one producer for embedding text inside a SQL string literal.
    /// </summary>
    /// <remarks>
    /// <b>Parameterise when you can; this is for the text that cannot be.</b> Two kinds of text end up
    /// embedded in a single-quoted literal in this framework, and the escaping rule is the same for both —
    /// which is why they share one producer rather than two:
    /// <list type="bullet">
    /// <item><description>
    /// <b>A name the grammar will only accept as a literal.</b> PostgreSQL's
    /// <c>create_hypertable('"T"', 'ts')</c> and TimescaleDB's policy functions, MSSql's
    /// <c>OBJECT_ID('T')</c>, every <c>sys.indexes WHERE name = '…'</c> probe. See
    /// <see cref="Connectors.AbstractConnectorBase.RegclassLiteral"/> and
    /// <see cref="Connectors.AbstractConnectorBase.CatalogueNameLiteral"/>, which build on this and add the
    /// quoting or folding each position needs.
    /// </description></item>
    /// <item><description>
    /// <b>A constant in a statement that takes no parameters at all.</b> <c>CREATE VIEW</c> is the one that
    /// matters — a view definition's constants are baked into DDL, so <c>DataBase.InlineConstant</c> and
    /// <c>ViewSelectSqlBuilder.FormatJoinConditionValue</c> have nothing to bind to. Such constants must come
    /// from the model, never from user input; that is a caller obligation this escaping does not remove.
    /// </description></item>
    /// </list>
    /// <b>It is still not a licence to interpolate a value that COULD be parameterised</b> — a value with a
    /// <see cref="System.Data.Common.DbParameter"/> available belongs in one, and
    /// <c>SqlBuilderContext.AddParameter</c> is the road for that.
    /// <para>
    /// It existed already, twenty-one times over: <c>Replace("'", "''")</c> was hand-written in
    /// <c>SqlIndexManager</c>, <c>MSSqlConnector</c>, all four index managers, <c>DataBase.InlineConstant</c>,
    /// <c>SqlBuilderContext.EscapeValue</c> and <c>ViewSelectSqlBuilder</c> — the same expression, re-derived
    /// per sink, which is how one of them ends up forgetting. TASK-253 named it and converged all of them, so
    /// a sink reuses the rule rather than rewriting it and an audit has one place to look.
    /// <para>
    /// <b>One copy is deliberately left behind:</b> <c>CosmosDBDataMigrator.FormatSqlValue</c>.
    /// <c>Birko.Data.Migrations.CosmosDB</c> does not import <c>Birko.Data.SQL</c>, and Cosmos SQL is not this
    /// dialect — converging it would buy one line at the cost of a project dependency on a SQL layer it has
    /// nothing else to do with.
    /// </para>
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
        /// <param name="value">The text to escape. Must not be null — see the remarks.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="value"/> is null.
        /// </exception>
        /// <remarks>
        /// <b>Refuses null rather than treating it as empty, and that is a deliberate reversal.</b> The first
        /// version of this helper returned <see cref="string.Empty"/> for a null, which looked accommodating
        /// and was wrong: converging the framework's 18 hand-rolled copies onto it (TASK-253 step 7) meant
        /// every one of those sinks would have turned a null identifier into an <i>empty</i> one — a silently
        /// malformed statement, where the hand-written <c>Replace</c> had thrown. § SH-H037's rule, arriving
        /// through a helper's null-handling: an unmappable input must fail loudly, because the quiet half is
        /// what guarantees the next sink repeats it.
        /// <para>
        /// Nothing legitimately passes null: <c>SqlBuilderContext.EscapeValue</c> and
        /// <c>DataBase.InlineConstant</c> both handle null before reaching here, and the identifier sinks have
        /// no meaningful empty case. Note <c>Tables.IndexDefinition.Name</c> is declared
        /// <c>string Name { get; set; } = null!;</c>, so a null really can arrive there at runtime — it merely
        /// used to fail on the neighbouring <c>QuoteIdentifier</c> call instead, one line later.
        /// </para>
        /// </remarks>
        public static string EscapeLiteral(string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value),
                    "Cannot escape a null for a SQL literal. Escaping it to an empty string would emit a "
                  + "silently malformed statement — an empty identifier or an empty constant — so the null is "
                  + "refused here instead. Handle it at the call site: the value sinks answer NULL for it, and "
                  + "an identifier sink has no empty case.");
            }
            return value.Replace("'", "''");
        }
    }
}
