using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Birko.Data.SQL
{
    public static partial class DataBase
    {
        /// <summary>
        /// A plain SQL column identifier, optionally qualified by one table name: <c>Rank</c>,
        /// <c>label_col</c>, <c>PRows.Rank</c>. Nothing else — no whitespace, no operators, no quotes, no
        /// parentheses, no statement separator, no comment marker.
        /// <para>
        /// Anchored with <c>\z</c>, not <c>$</c>: in .NET <c>$</c> also matches immediately before a
        /// trailing newline, so <c>"Rank\n"</c> would satisfy a <c>$</c>-anchored pattern. That particular
        /// string is harmless (a trailing newline is SQL whitespace) and a payload after the newline is
        /// still refused, but an anchor that admits a character the pattern never listed is the wrong
        /// anchor for a guard whose entire job is "these characters and no others".
        /// </para>
        /// </summary>
        private static readonly Regex _bareIdentifier = new(
            @"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?\z",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// <see cref="_bareIdentifier"/> without the optional <c>Table.</c> qualifier — for a sink where a
        /// qualifier is not merely unnecessary but invalid (an index column list). Same <c>\A…\z</c>
        /// anchoring, for the same reason: .NET's <c>$</c> also matches before a trailing newline.
        /// </summary>
        private static readonly Regex _unqualifiedIdentifier = new(
            @"\A[A-Za-z_][A-Za-z0-9_]*\z",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Resolves a <see cref="Birko.Rules.Rule.Field"/> into the SQL select name of the column that
        /// <paramref name="entityType"/> actually has.
        /// <para>
        /// SH-H023 (TASK-111). Every condition strategy interpolates <c>Condition.Name</c> straight into
        /// <c>CommandText</c> — <c>EqualConditionStrategy</c> is literally
        /// <c>$"{condition.Name}{op}{value}"</c> — and <c>RuleConditionConverter</c> used to hand it
        /// <c>rule.Field</c> verbatim. A rule tree is configuration data and <c>docs/rules.md</c> advertises
        /// this path as producing "a direct WHERE clause", so caller text reached the statement. Measured
        /// against a real SQLite file, on a 3-row table, with a rule whose value matched nothing:
        /// <list type="bullet">
        /// <item><c>Field = "Rank OR 1=1 --"</c> returned <b>3 rows of 3</b>, silently.</item>
        /// <item><c>Field = "Rank; CREATE TABLE Pwned (x INTEGER); --"</c> <b>created the table</b>.</item>
        /// <item><c>Field = "(SELECT count(*) FROM sqlite_master)"</c> evaluated the subquery as the
        /// left operand — a blind-boolean oracle.</item>
        /// </list>
        /// The trailing <c> = @param</c> the strategy appends is not a mitigation: <c>--</c> comments it
        /// out. The parameter <i>name</i> is sanitised (<c>SqlBuilderContext.GenerateParameterName</c>),
        /// which is what made this look safe on a skim — the sanitisation was on the wrong string.
        /// </para>
        /// <para>
        /// A field that survives this method is a name read out of table metadata, never caller text:
        /// <b>the resolution IS the whitelist</b>. The same lookup fixes the ordinary-consumer half — a
        /// <c>[NamedField("label_col")]</c> property was emitted under its CLR name and the database
        /// answered <i>no such column: Label</i>, so a remapped property could not be filtered at all.
        /// </para>
        /// <para>
        /// Deliberately does NOT quote the resolved **column** identifier, for the reason
        /// <see cref="ResolveOrderFields"/> records for the ORDER BY sink: this codebase emits column
        /// identifiers bare everywhere and quotes only table names, so quoting here would break a working
        /// filter on PostgreSQL, where an unquoted DDL identifier is folded to lower case.
        /// </para>
        /// <para>
        /// It resolves **table-qualified**, matching what the expression path already emits for WHERE
        /// (<c>ResolveColumnName(exprType, name, withTableName: true)</c>) and closing the finding's
        /// "nothing qualifies the name, making it ambiguous in a join".
        /// <b>Note the qualifier is a separate question from the column, and the PostgreSQL argument above
        /// does not extend to it.</b> The emitted <c>Table.Column</c> leaves the table part unquoted while
        /// <c>CreateSelectCommand</c> emits <c>FROM "Table"</c> quoted, so on PostgreSQL a table whose name
        /// is not already lower case folds to lower case here and the statement fails with
        /// <c>42P01 missing FROM-clause entry</c>. That is **pre-existing and framework-wide**, not
        /// introduced here — the SELECT list qualifies identically via <c>GetSelectFields(true)</c>, so any
        /// mixed-case table is already affected on every multi-column read. Tracked separately; this method
        /// deliberately matches the surrounding convention rather than diverging from it in one sink.
        /// </para>
        /// </summary>
        /// <param name="entityType">The entity the rule filters; supplies the field metadata.</param>
        /// <param name="field">The rule's field — a CLR property name, or the mapped column name.</param>
        /// <returns>The resolved, table-qualified select name.</returns>
        /// <exception cref="ArgumentException">
        /// The field is blank, or matches no property and no column of <paramref name="entityType"/>.
        /// Filtering on something the entity does not have is a configuration error, and failing here names
        /// the field and the type instead of letting the database answer with a column name it never heard
        /// of — or, far worse, answering with rows.
        /// </exception>
        public static string ResolveRuleField(Type entityType, string? field)
        {
            if (entityType == null)
            {
                throw new ArgumentNullException(nameof(entityType));
            }
            if (string.IsNullOrWhiteSpace(field))
            {
                throw new ArgumentException(
                    $"A rule filtering {entityType.Name} has a blank Field. Every rule must name a mapped "
                    + "property or its column.",
                    nameof(field));
            }

            var table = LoadTable(entityType);
            var resolved = table == null ? null : ResolveFieldNameIn(new[] { table }, field!, withTableName: true);
            resolved ??= ResolveFieldSelectName?.Invoke(entityType, field!, true);

            if (string.IsNullOrEmpty(resolved))
            {
                throw new ArgumentException(
                    $"Rule field '{field}' does not resolve to a column of {entityType.Name}. "
                    + "Filter on a mapped property name or its column name.",
                    nameof(field));
            }

            return resolved!;
        }

        /// <summary>
        /// Guards a rule field on the type-less conversion path, where there is no entity to resolve
        /// against and the field is therefore emitted as given.
        /// <para>
        /// SH-H023 (TASK-111). This is strictly weaker than <see cref="ResolveRuleField"/> — it cannot fix
        /// a <c>[NamedField]</c>-remapped property, because it has no metadata to remap through. What it
        /// does guarantee is that whatever reaches <c>CommandText</c> is a single bare identifier, so none
        /// of the measured payloads survive: all four carry a space, an operator, a parenthesis or a
        /// statement separator. A bare identifier that names no column is at worst a database error, which
        /// is a wrong answer that reports itself; the payloads were wrong answers that did not.
        /// </para>
        /// <para>
        /// The type-less overloads are kept working rather than removed because a caller whose rule fields
        /// are already correct column names is doing nothing wrong, and every such rule keeps its exact
        /// pre-fix behaviour. Callers who want the remapping — and the stronger check — pass the entity
        /// type.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentException">The field is not a bare, optionally table-qualified identifier.</exception>
        public static string ValidateRuleFieldIdentifier(string? field)
        {
            if (string.IsNullOrWhiteSpace(field))
            {
                throw new ArgumentException(
                    "A rule has a blank Field. Every rule must name a column.", nameof(field));
            }

            if (!_bareIdentifier.IsMatch(field!))
            {
                throw new ArgumentException(
                    $"Rule field '{field}' is not a plain column identifier, and rule fields are "
                    + "interpolated into the WHERE clause. Pass the entity type to RuleConditionConverter "
                    + "so the field can be resolved against table metadata, or supply a bare column name.",
                    nameof(field));
            }

            return field!;
        }

        /// <summary>
        /// The same bare-identifier check applied to an <b>index</b> column name (TASK-245).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Third sink in the interpolated-identifier family, and it arrived as a consequence of a fix:
        /// TASK-245 made index column identifiers be emitted <b>bare</b>, because a quoted one cannot resolve
        /// the case-folded column that bare-column <c>CREATE TABLE</c> actually creates on PostgreSQL. That
        /// is safe wherever the name comes from table metadata — schema-ensure resolves
        /// <c>[IndexedField]</c> / <c>[CompositeIndex]</c> columns against mapped properties — but
        /// <c>IIndexManager.CreateAsync</c> takes its field names from the <b>caller</b> as free text and
        /// they land in <c>CommandText</c>. Before that change <c>QuoteIdentifier</c> was incidentally
        /// containing them; bare, the payload breaks out, exactly as SH-H023's rule field did.
        /// </para>
        /// <para>
        /// <c>SqlIndexManager</c> holds a table name and no entity type, so metadata resolution is
        /// unavailable and this is the sanctioned weaker fallback — it cannot fix a <c>[NamedField]</c>
        /// remapping, but it refuses every payload. It shares <c>_bareIdentifier</c> with
        /// <see cref="ValidateRuleFieldIdentifier"/> deliberately: one regex, so the two sinks cannot drift
        /// apart about what an acceptable identifier is, and anchored <c>\A…\z</c> because .NET's <c>$</c>
        /// also matches before a trailing newline.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentException">The name is blank or not a bare identifier.</exception>
        public static string ValidateIndexFieldIdentifier(string? field)
        {
            if (string.IsNullOrWhiteSpace(field))
            {
                throw new ArgumentException(
                    "An index field has a blank Name. Every indexed field must name a column.", nameof(field));
            }

            // NOT _bareIdentifier: that pattern allows an optional `Table.` qualifier, which is correct for
            // the WHERE-clause sink it was written for and WRONG here. A CREATE INDEX column list takes no
            // qualifier on any supported provider — `(Docs.Status)` is a syntax error, not a resolvable
            // column — and the framework-wide invariant is that a qualifier is only ever emitted where a bare
            // alias introduces it (TASK-211), which index DDL has none of. Accepting one would turn a clear
            // ArgumentException into a provider syntax error, i.e. the guard would pass the payload's
            // harmless cousin through to break the statement anyway.
            if (!_unqualifiedIdentifier.IsMatch(field!))
            {
                throw new ArgumentException(
                    $"Index field '{field}' is not a plain, unqualified column identifier, and index columns "
                    + "are interpolated bare into the CREATE INDEX statement. Supply a bare column name "
                    + "(a 'Table.Column' qualifier is not valid in an index column list).",
                    nameof(field));
            }

            return field!;
        }

        /// <summary>
        /// The same bare-identifier check applied to a <b>column reference</b> that is interpolated into a
        /// statement the caller does not otherwise control (TASK-255).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Fourth sink in the interpolated-identifier family, and it arrived the same way the third did:
        /// a column reference must be emitted <b>bare</b> to resolve the case-folded column that
        /// bare-column <c>CREATE TABLE</c> actually creates on PostgreSQL, and bare removes the accidental
        /// containment that identifier quoting was providing. The first caller is
        /// <c>TimescaleDBMigration.BuildContinuousAggregateSql</c>, whose bucketing column reaches
        /// <c>time_bucket(…, &lt;col&gt;)</c> inside a <c>CREATE MATERIALIZED VIEW</c> body — a real
        /// identifier position, so neither literal escaping nor <c>CatalogueNameLiteral</c> applies.
        /// </para>
        /// <para>
        /// <b>Why this exists rather than a call to <see cref="ValidateIndexFieldIdentifier"/>.</b> The
        /// check is identical and deliberately shares <c>_unqualifiedIdentifier</c> with it, so the sinks
        /// cannot drift about what an acceptable identifier is. What differs is the <i>message</i>: that
        /// one names <c>CREATE INDEX</c> and an index column list, which would tell a migration author
        /// about indexes they never mentioned. A refusal has to name the door the caller actually has
        /// (§ Conventions, TASK-215), so the shared thing is the regex and the separate thing is the
        /// wording.
        /// </para>
        /// <para>
        /// Same tier and same honest limit as its siblings: this is the <b>weaker</b> fallback, used where
        /// there is no entity type to resolve against, so it cannot fix a <c>[NamedField]</c> remapping.
        /// What it guarantees is that whatever reaches <c>CommandText</c> is a single bare identifier — every
        /// measured payload carries a space, an operator, a parenthesis or a statement separator — and that
        /// a bare identifier naming no column is at worst a database error, which is a wrong answer that
        /// reports itself.
        /// </para>
        /// <para>
        /// A <c>Table.</c> qualifier is refused, as in the index sink and for a comparable reason: this
        /// framework only ever emits a qualifier where a bare alias introduces it (TASK-211), and the
        /// statements this guards introduce none.
        /// </para>
        /// </remarks>
        /// <param name="column">The column reference to validate.</param>
        /// <param name="paramName">
        /// The name of the <i>caller's</i> parameter, for <see cref="ArgumentException.ParamName"/>. Defaults
        /// to <c>"column"</c>, but a caller whose parameter is called something else should pass its own name:
        /// the same "a refusal names the door THIS caller has" rule that gives this method a message separate
        /// from <see cref="ValidateIndexFieldIdentifier"/>'s applies to the <c>ParamName</c> too, and a
        /// <c>ParamName</c> naming a parameter the caller does not have is the quiet version of the defect
        /// (§ Conventions, TASK-215).
        /// </param>
        /// <exception cref="ArgumentException">The name is blank or not a bare, unqualified identifier.</exception>
        public static string ValidateColumnIdentifier(string? column, string paramName = "column")
        {
            if (string.IsNullOrWhiteSpace(column))
            {
                throw new ArgumentException(
                    "A column reference is blank. Every interpolated column reference must name a column.",
                    paramName);
            }

            if (!_unqualifiedIdentifier.IsMatch(column!))
            {
                throw new ArgumentException(
                    $"Column '{column}' is not a plain, unqualified column identifier, and column references "
                    + "are interpolated bare into the statement. Supply a bare column name "
                    + "(a 'Table.Column' qualifier is not accepted here).",
                    paramName);
            }

            return column!;
        }

        /// <summary>
        /// The one lookup order shared by both identifier sinks — the ORDER BY keys
        /// (<see cref="ResolveOrderFields"/>) and the rule fields (<see cref="ResolveRuleField"/>).
        /// Property name first (what callers normally write), then the mapped column name (which worked
        /// before either guard existed and has to keep working, and is drawn from the same metadata so it
        /// is equally safe). Shared so the two sinks cannot drift apart: they closed the same class of
        /// defect and a consumer should not have to learn two rules.
        /// <para>
        /// <c>internal</c>, not <c>private</c>: a third interpolated-identifier sink (a GROUP BY or HAVING
        /// builder under <c>SQL/Connectors</c>, say) has to be able to *call* this, or it will rediscover
        /// the lookup — which is precisely the outcome the § Conventions rule was written to prevent. It
        /// stays internal rather than public because the two public entry points above are the supported
        /// surface; a new sink lives in this assembly.
        /// </para>
        /// </summary>
        internal static string? ResolveFieldNameIn(IReadOnlyList<Tables.Table> tables, string key, bool withTableName)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            foreach (var table in tables)
            {
                var field = table.GetFieldByPropertyName(key);
                if (field != null)
                {
                    return field.GetSelectName(withTableName);
                }
            }

            foreach (var table in tables)
            {
                var field = table.Fields?.Values
                    .FirstOrDefault(f => f.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (field != null)
                {
                    return field.GetSelectName(withTableName);
                }
            }

            return null;
        }
    }
}
