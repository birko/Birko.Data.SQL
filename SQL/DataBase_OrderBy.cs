using Birko.Data.SQL.Fields;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Birko.Data.SQL
{
    public static partial class DataBase
    {
        /// <summary>
        /// Resolves ORDER BY keys — CLR property names as produced by <see cref="Birko.Data.Stores.OrderBy{T}"/>,
        /// including the arbitrary strings <c>OrderBy&lt;T&gt;.ByName</c> accepts — into the SQL select names
        /// of the columns those tables actually have.
        /// <para>
        /// SH-H003 / SH-M022 (TASK-110). The ORDER BY clause interpolates its keys into
        /// <c>CommandText</c> verbatim, so before this existed <c>ByName(request.Sort)</c> put caller text
        /// straight into the statement: <c>ByName("Rank; CREATE TABLE Pwned (x INTEGER); --")</c> created the
        /// table, and <c>ByName("Rank LIMIT 1 --")</c> commented out and overrode the caller's own LIMIT.
        /// A key that survives this method is a name read out of table metadata, never caller text —
        /// <b>the resolution IS the whitelist</b>, which is what closes the injection. The same lookup also
        /// fixes the ordinary-consumer half: a <c>[NamedField("col")]</c>-remapped property was emitted under
        /// its CLR name and the database rejected the statement with <i>no such column</i>, so a remapped
        /// column could not be sorted at all.
        /// </para>
        /// <para>
        /// Deliberately does NOT quote the resolved identifier. This codebase emits column identifiers bare
        /// everywhere else — the DDL (<c>CREATE TABLE "T" (label_col TEXT, Rank INTEGER)</c>), every WHERE
        /// condition strategy, and the SELECT list — and quotes only table names. Quoting solely here would
        /// break a working sort on PostgreSQL, where the unquoted DDL identifier is folded to lower case, so
        /// <c>ORDER BY "Rank"</c> would not match the column that DDL actually created.
        /// </para>
        /// </summary>
        /// <param name="tables">The tables being selected from; supplies the field metadata.</param>
        /// <param name="orderFields">Keys to resolve, mapped to true for descending. May be null or empty.</param>
        /// <returns>
        /// A dictionary with the same values and iteration order, keyed by resolved select names; the
        /// original reference when there is nothing to resolve.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// A key matches neither a property name nor a column name of any of the tables. Ordering by
        /// something the entity does not have is a programming error, and failing here names the key and the
        /// type instead of letting the database answer with the column name it never heard of.
        /// </exception>
        public static IDictionary<string, bool>? ResolveOrderFields(IEnumerable<Tables.Table>? tables, IDictionary<string, bool>? orderFields)
        {
            if (orderFields == null || orderFields.Count == 0)
            {
                return orderFields;
            }

            var tableList = tables?.Where(x => x != null).ToArray() ?? Array.Empty<Tables.Table>();
            if (tableList.Length == 0)
            {
                throw new ArgumentException(
                    $"Cannot resolve ORDER BY key(s) '{string.Join("', '", orderFields.Keys)}' — no table metadata was supplied.",
                    nameof(tables));
            }

            // More than one table means the statement joins, so the select name has to carry its table
            // prefix to stay unambiguous — the same rule the expression-keyed overloads applied.
            var withTableName = tableList.Length > 1;

            var resolved = new Dictionary<string, bool>(orderFields.Count);
            foreach (var kvp in orderFields)
            {
                var name = ResolveOrderFieldName(tableList, kvp.Key, withTableName);
                if (name == null)
                {
                    throw new ArgumentException(
                        $"ORDER BY key '{kvp.Key}' does not resolve to a column of "
                        + $"{string.Join(", ", tableList.Select(t => t.Type?.Name ?? t.Name))}. "
                        + "Order by a mapped property name or its column name.",
                        nameof(orderFields));
                }

                // Indexer, not Add: two keys can legitimately resolve to the same column (a property and
                // its own column name), and a duplicate sort key is not worth throwing over.
                resolved[name] = kvp.Value;
            }
            return resolved;
        }

        /// <summary>
        /// Resolves one ORDER BY key against the given tables, or null when it matches nothing.
        /// <para>
        /// The property-then-column lookup lives in <see cref="ResolveFieldNameIn"/>, shared with the rule
        /// field sink (TASK-111) so the two identifier guards cannot drift apart. The view fallback below
        /// stays here: it is keyed on the ORDER BY resolver's table list, and the rule path reaches
        /// <see cref="ResolveFieldSelectName"/> through its own single entity type.
        /// </para>
        /// </summary>
        private static string? ResolveOrderFieldName(IReadOnlyList<Tables.Table> tables, string key, bool withTableName)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var resolved = ResolveFieldNameIn(tables, key, withTableName);
            if (resolved != null)
            {
                return resolved;
            }

            // Views register a resolver here, so a view column that no Table knows about still resolves.
            foreach (var table in tables.Where(t => t.Type != null))
            {
                var viewName = ResolveFieldSelectName?.Invoke(table.Type, key, withTableName);
                if (!string.IsNullOrEmpty(viewName))
                {
                    return viewName;
                }
            }

            return null;
        }
    }
}
