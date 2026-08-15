using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Birko.Data.SQL.Connectors.Strategies;
using PasswordSettings = Birko.Configuration.PasswordSettings;

namespace Birko.Data.SQL.Connectors
{
    /// <summary>
    /// Base class for SQL database connectors containing shared functionality.
    /// </summary>
    public abstract partial class AbstractConnectorBase
    {
        protected readonly PasswordSettings _settings = null!;
        protected readonly object _lock = new();
        public bool IsInitializing { get; protected set; } = false;

        /// <summary>
        /// Gets the connection settings for this connector.
        /// </summary>
        public PasswordSettings Settings => _settings;

        /// <summary>
        /// Retry policy for transient failures (deadlocks, timeouts, connection drops).
        /// Set to <see cref="RetryPolicy.None"/> to disable retries. Default is no retries.
        /// </summary>
        public RetryPolicy RetryPolicy { get; set; } = RetryPolicy.None;

        /// <summary>
        /// Determines whether an exception is transient and the operation should be retried.
        /// Override in provider-specific connectors to detect provider-specific transient errors
        /// (e.g., SQL Server error 1205 for deadlocks, PostgreSQL 40P01, MySQL 1213).
        /// </summary>
        public virtual bool IsTransientException(Exception ex)
        {
            if (ex is TimeoutException) return true;
            if (ex is DbException dbEx && dbEx.IsTransient) return true;
            return false;
        }

        /// <summary>
        /// Determines whether an exception indicates the queried table/relation does not exist, so a
        /// reader can yield an empty result instead of faulting. The base match is SQLite's wording
        /// ("no such table"); provider-specific connectors override this to add their own phrasing
        /// (PostgreSQL: 'relation "x" does not exist', MySQL: "doesn't exist", MSSQL: "Invalid object name").
        /// Mirrors the <see cref="IsTransientException"/> override pattern.
        /// </summary>
        public virtual bool IsMissingTableException(Exception ex)
        {
            return ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Executes an action with retry logic for transient failures.
        /// </summary>
        protected void ExecuteWithRetry(Action action, string? commandText = null)
        {
            var policy = RetryPolicy;
            if (policy.MaxRetries <= 0)
            {
                action();
                return;
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception ex) when (attempt < policy.MaxRetries && IsTransientException(ex))
                {
                    var delay = policy.GetDelay(attempt + 1);
                    Thread.Sleep(delay);
                }
            }
        }

        /// <summary>
        /// Executes an async action with retry logic for transient failures.
        /// </summary>
        protected async Task ExecuteWithRetryAsync(Func<Task> action, CancellationToken ct = default, string? commandText = null)
        {
            var policy = RetryPolicy;
            if (policy.MaxRetries <= 0)
            {
                await action().ConfigureAwait(false);
                return;
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await action().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (attempt < policy.MaxRetries && IsTransientException(ex))
                {
                    var delay = policy.GetDelay(attempt + 1);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }

        // Strategy pattern for condition building — keyed by ConditionType for O(1) dispatch
        private readonly Dictionary<Conditions.ConditionType, IConditionStrategy> _conditionStrategyMap = new();

        protected AbstractConnectorBase(PasswordSettings settings)
        {
            _settings = settings;
            InitializeConditionStrategies();
        }

        /// <summary>
        /// Initializes the condition strategy builders
        /// </summary>
        private void InitializeConditionStrategies()
        {
            IConditionStrategy[] strategies =
            [
                new Strategies.EqualConditionStrategy(),
                new Strategies.ComparisonConditionStrategy(),
                new Strategies.LikeConditionStrategy(),
                new Strategies.InConditionStrategy(),
                new Strategies.NullConditionStrategy(),
            ];
            foreach (var strategy in strategies)
                foreach (Conditions.ConditionType ct in Enum.GetValues<Conditions.ConditionType>())
                    if (strategy.CanHandle(ct))
                        _conditionStrategyMap[ct] = strategy;
        }

        /// <summary>
        /// Creates a database connection.
        /// </summary>
        public abstract DbConnection CreateConnection(PasswordSettings settings);

        /// <summary>
        /// Converts a DbType to database-specific type string.
        /// </summary>
        public abstract string ConvertType(DbType type, Fields.AbstractField field);

        /// <summary>
        /// Gets the field definition string for a specific field.
        /// </summary>
        public abstract string FieldDefinition(Fields.AbstractField field);

        /// <summary>
        /// Converts a DbType to its corresponding CLR type.
        /// Used for DataTable construction in bulk operations.
        /// Override in provider-specific connectors if the platform requires different mappings.
        /// </summary>
        public virtual Type DbTypeToClrType(DbType dbType)
        {
            return dbType switch
            {
                DbType.Boolean => typeof(bool),
                DbType.Byte or DbType.SByte => typeof(byte),
                DbType.Single => typeof(float),
                DbType.Int16 or DbType.UInt16 => typeof(short),
                DbType.Int32 or DbType.UInt32 => typeof(int),
                DbType.Int64 or DbType.UInt64 => typeof(long),
                DbType.Decimal or DbType.VarNumeric or DbType.Currency => typeof(decimal),
                DbType.Double => typeof(double),
                DbType.Guid => typeof(Guid),
                DbType.Date or DbType.DateTime or DbType.DateTime2 or DbType.Time => typeof(DateTime),
                DbType.DateTimeOffset => typeof(DateTimeOffset),
                DbType.Binary or DbType.Object => typeof(byte[]),
                _ => typeof(string),
            };
        }

        /// <summary>
        /// Quotes a SQL identifier (table or column name) to prevent reserved word conflicts and injection.
        /// Default uses ANSI SQL double quotes. Override for provider-specific quoting.
        /// </summary>
        public virtual string QuoteIdentifier(string identifier)
        {
            return "\"" + identifier.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// Converts a CLR value into the form the storage layer actually persists, before it is bound
        /// to a <see cref="DbParameter"/>. Today that means one thing: unwrapping an enum to its
        /// underlying integral value.
        /// <para>
        /// <see cref="Fields.AbstractField.CreateField"/> maps every enum property to an
        /// <see cref="Fields.IntegerField"/>, so an enum column holds INTEGER. Enum EQUALITY never needed
        /// help — the C# compiler lifts <c>x.Status == Foo</c> to the underlying integral type inside the
        /// expression tree — but the members of a collection in <c>set.Contains(x.Status)</c>, and an enum
        /// in an <c>UPDATE … SET</c>, reach the parameter still boxed as the enum, leaving each provider
        /// to guess. Microsoft.Data.Sqlite happens to convert them; Npgsql rejects an unmapped CLR enum
        /// outright. Binding the integral value the column actually stores removes the guess.
        /// </para>
        /// <para>
        /// This is hardening, NOT the cause of the zero-rows defect that prompted it — that was
        /// <c>DataBase.IsNonOperandArgument</c> (see Symbio TASK-249/TASK-254 and
        /// <c>SqlEnumInPredicateTests</c>).
        /// </para>
        /// <para>
        /// Every <c>AddParameter</c> override must funnel its value through here — the provider overrides
        /// deliberately do not chain to this base implementation, so the conversion cannot live in the
        /// body below.
        /// </para>
        /// </summary>
        public static object? NormalizeParameterValue(object? value)
        {
            if (value == null) return null;
            var type = value.GetType();
            // Boxed nullable enums arrive already unwrapped to the underlying enum type.
            if (!type.IsEnum) return value;
            return System.Convert.ChangeType(
                value,
                Enum.GetUnderlyingType(type),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Adds a parameter to a DbCommand.
        /// </summary>
        public virtual DbCommand AddParameter(DbCommand command, string name, object? value)
        {
            value = NormalizeParameterValue(value);
            if (command.Parameters.Contains(name))
            {
                command.Parameters[name].Value = value ?? DBNull.Value;
            }
            else
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
            return command;
        }

        /// <summary>
        /// Builds a SQL condition clause. Allocates one StringBuilder for the entire
        /// condition tree (shared across all nested levels).
        /// </summary>
        public virtual string ConditionDefinition(Conditions.Condition condition, DbCommand command)
        {
            if (condition == null) return string.Empty;
            // TASK-137: a tree that constrains nothing renders no WHERE, exactly like `x => true`. On a read
            // that is read-everything; on a destructive statement it is what AddRequiredWhere refuses.
            if (IsAlwaysTrueCondition(condition)) return string.Empty;
            var sb = new StringBuilder();
            AppendConditionTo(sb, condition, command);
            return sb.ToString();
        }

        /// <summary>
        /// The always-false constant. Emitted for a predicate that legitimately matches no row: an empty
        /// <c>IN</c> (<see cref="Strategies.InConditionStrategy"/>), <c>_ =&gt; false</c>
        /// (<c>DataBase.MakeFalseCondition</c>), and a negated group that reduces to always-true
        /// (<c>NOT (A OR TRUE)</c>).
        /// </summary>
        /// <remarks>
        /// There is deliberately <b>no always-TRUE counterpart</b> (TASK-137). An always-false term cannot be
        /// dropped — <c>A AND FALSE</c> is <c>FALSE</c>, not <c>A</c> — so it has to be rendered, and
        /// <c>1 = 0</c> carries no injection connotation. An always-true term is the opposite on both counts:
        /// it can always be reduced away, and the constant that would express it (<c>1 = 1</c>) is the
        /// signature of <c>' OR 1=1--</c>. It is therefore reduced by
        /// <see cref="IsAlwaysTrueCondition"/> rather than emitted.
        /// </remarks>
        public const string AlwaysFalseSql = "1 = 0";

        /// <summary>
        /// True when <paramref name="condition"/> constrains nothing, i.e. it matches every row (TASK-137).
        /// <b>The single producer of that verdict</b>: the renderer below reduces such terms away, and
        /// <see cref="WouldTargetEveryRow"/> refuses a destructive statement built from them. Two
        /// implementations of "means everything" is how a scope guard ends up agreeing with itself and
        /// disagreeing with the emitted SQL.
        /// </summary>
        /// <remarks>
        /// <para>Today exactly one leaf reduces: an <c>IN</c> with <see cref="Conditions.Condition.IsNot"/>
        /// and no values. "Not in the empty set" is true of every row, and it used to render <c>1 = 1</c> —
        /// which satisfied <see cref="AddRequiredWhere"/>'s "something was rendered" test with a tautology,
        /// so <c>Delete(x =&gt; !empty.Contains(x.Col))</c> emptied the table and reported success. Measured:
        /// 0 of 3 rows left, no exception.</para>
        /// <para><b>Group algebra.</b> A group's children all share the group's separator (the parser expresses
        /// precedence by nesting, never by a mixed unparenthesized chain), so: an AND group means everything
        /// only if <i>every</i> child does; an OR group if <i>any</i> child does. A negated group inverts —
        /// <c>NOT (A OR TRUE)</c> is always <b>false</b>, so it is not always-true and renders
        /// <see cref="AlwaysFalseSql"/>.</para>
        /// <para>This is not <c>DataBase.IsExplicitAllRows</c> and must not be confused with it. That one asks
        /// "did the caller explicitly say every row", and answers yes only for a single normalized constant
        /// node — the deliberate <c>DeleteAll()</c> synonym. This one asks "does this tree happen to reduce to
        /// every row", which is the case TASK-109 refuses.</para>
        /// </remarks>
        public static bool IsAlwaysTrueCondition(Conditions.Condition? condition)
        {
            if (condition == null) return false;

            var subConditions = condition.SubConditions;
            if (subConditions?.Any() == true)
            {
                // A negated group that reduces to always-true is always-FALSE, never always-true.
                if (condition.IsNot) return false;
                return IsAlwaysTrueChain(subConditions.Select(sub => (condition.IsOr, IsAlwaysTrueCondition(sub))));
            }

            return condition.Type == Conditions.ConditionType.In
                && condition.IsNot
                && !HasAnyValue(condition.Values);
        }

        /// <summary>
        /// True when a negated group reduces to always-<b>false</b> — the <c>NOT (A OR TRUE)</c> case, which
        /// must render <see cref="AlwaysFalseSql"/> rather than have its inner terms dropped (dropping them
        /// would turn "matches nothing" into "matches everything").
        /// </summary>
        private static bool IsNegatedAlwaysTrueGroup(Conditions.Condition condition)
        {
            var subConditions = condition.SubConditions;
            if (subConditions?.Any() != true || !condition.IsNot) return false;
            return IsAlwaysTrueChain(subConditions.Select(sub => (condition.IsOr, IsAlwaysTrueCondition(sub))));
        }

        /// <summary>
        /// Reduces a chain of <c>(joinsWithOr, isAlwaysTrue)</c> terms to a single "means everything" verdict.
        /// <para>Shared by groups (where every term carries the group's separator) and by the flat
        /// <see cref="ConditionDefinition(IEnumerable{Conditions.Condition}, DbCommand)"/> list, where each
        /// term brings its own. AND binds tighter than OR, so the chain is a series of AND-runs OR'd together:
        /// a run means everything when all of its terms do, and the chain when any run does. Written this way
        /// so the flat mixed case — <c>A OR TRUE AND B</c>, which is <c>A OR (TRUE AND B)</c> and NOT
        /// always-true — cannot be over-reduced.</para>
        /// </summary>
        private static bool IsAlwaysTrueChain(IEnumerable<(bool JoinsWithOr, bool IsAlwaysTrue)> terms)
        {
            bool runIsAlwaysTrue = true;
            bool anyRun = false;
            bool first = true;

            foreach (var (joinsWithOr, isAlwaysTrue) in terms)
            {
                if (!first && joinsWithOr)
                {
                    if (runIsAlwaysTrue) return true;   // a completed run means everything → so does the chain
                    runIsAlwaysTrue = true;             // start the next AND-run
                }
                runIsAlwaysTrue &= isAlwaysTrue;
                anyRun = true;
                first = false;
            }

            return anyRun && runIsAlwaysTrue;
        }

        /// <summary>True when <paramref name="values"/> holds at least one element. Enumerates; stops at the first.</summary>
        private static bool HasAnyValue(System.Collections.IEnumerable? values)
        {
            if (values == null) return false;
            foreach (var _ in values) return true;
            return false;
        }

        /// <summary>
        /// Appends the SQL for <paramref name="condition"/> to a shared <paramref name="sb"/>,
        /// avoiding per-level StringBuilder allocations in nested AND/OR trees.
        /// </summary>
        /// <remarks>
        /// Callers must skip a condition for which <see cref="IsAlwaysTrueCondition"/> holds — it has no
        /// rendering, which is the point (TASK-137). This method renders the <c>NOT (A OR TRUE)</c> case,
        /// where the reduction is to always-<i>false</i> and so does have one.
        /// </remarks>
        private void AppendConditionTo(StringBuilder sb, Conditions.Condition condition, DbCommand command)
        {
            if (IsNegatedAlwaysTrueGroup(condition))
            {
                sb.Append(AlwaysFalseSql);
                return;
            }

            if (condition.SubConditions?.Any() == true)
                AppendSubConditionsTo(sb, condition, command);
            else
                sb.Append(BuildSingleCondition(condition, command));
        }

        /// <summary>
        /// Appends nested sub-conditions to <paramref name="sb"/> using the parent's IsOr flag
        /// as the join operator — no in-place mutation of sub.IsOr.
        /// Wraps in parentheses when there are two or more children, or when the group is negated.
        /// A negated group (parent IsNot, produced by <c>!(a &amp;&amp; b)</c> / <c>!(a || b)</c> or a
        /// negated comparison that became a single-child group) is prefixed with <c>NOT</c> so the
        /// negation binds the whole group — otherwise the flag would be silently dropped and the filter
        /// would match the OPPOSITE rows.
        /// </summary>
        private void AppendSubConditionsTo(StringBuilder sb, Conditions.Condition condition, DbCommand command)
        {
            var separator = condition.IsOr ? " OR " : " AND ";
            int startIndex = sb.Length;
            int count = 0;
            foreach (var sub in condition.SubConditions!)
            {
                // TASK-137: an always-true child is reduced away rather than rendered — `A AND TRUE` is `A`,
                // and there is no constant for TRUE that is not an injection lookalike. The caller has already
                // established the group as a whole is not always-true (IsAlwaysTrueCondition), so in an OR
                // group no child can be always-true here and this only ever drops AND terms.
                if (IsAlwaysTrueCondition(sub)) continue;

                if (count > 0) sb.Append(separator);
                AppendConditionTo(sb, sub, command);
                count++;
            }
            if (count >= 1 && (count > 1 || condition.IsNot))
            {
                sb.Insert(startIndex, '(');
                sb.Append(')');
            }
            if (count >= 1 && condition.IsNot)
            {
                sb.Insert(startIndex, "NOT ");
            }
        }

        /// <summary>
        /// Builds SQL for a single condition using the appropriate strategy.
        /// </summary>
        private string BuildSingleCondition(Conditions.Condition condition, DbCommand command)
        {
            if (string.IsNullOrEmpty(condition.Name))
                throw new InvalidOperationException("Condition name cannot be null or empty for non-subconditions");

            if (!_conditionStrategyMap.TryGetValue(condition.Type, out var strategy))
                throw new NotSupportedException($"Condition type {condition.Type} is not supported");

            var context = new SqlBuilderContext(this);
            return strategy.BuildSql(condition, command, context);
        }

        /// <summary>
        /// Builds SQL for multiple conditions using a single shared StringBuilder.
        /// </summary>
        public virtual string ConditionDefinition(IEnumerable<Conditions.Condition>? conditions, DbCommand command)
        {
            if (conditions == null) return string.Empty;
            var terms = conditions as IList<Conditions.Condition> ?? conditions.ToList();
            if (terms.Count == 0) return string.Empty;

            // TASK-137: same reduction as the single-condition overload. Each term brings its own separator
            // here, so the AND-run algebra in IsAlwaysTrueChain is what decides whether the whole flat chain
            // means everything — `A OR TRUE AND B` is `A OR (TRUE AND B)`, which does not.
            if (IsAlwaysTrueChain(terms.Select((term, i) => (i > 0 && term.IsOr, IsAlwaysTrueCondition(term)))))
                return string.Empty;

            var sb = new StringBuilder();
            int count = 0;
            bool inheritedOr = false;
            foreach (var term in terms)
            {
                // An always-true term is dropped from its AND-run. Sound whatever the surrounding precedence:
                // `X AND TRUE` is `X`, and the chain-level reduction above has already handled the case where
                // dropping it would leave a run that is itself always-true.
                if (IsAlwaysTrueCondition(term))
                {
                    // A dropped term that OPENED a run hands its OR to whichever term takes over that run —
                    // otherwise `A OR TRUE AND B` would render `A AND B`, silently narrowing the result to the
                    // intersection. (`A OR TRUE AND B` is `A OR (TRUE AND B)`, i.e. `A OR B`.)
                    if (count > 0 && term.IsOr) inheritedOr = true;
                    continue;
                }

                if (count > 0) sb.Append(term.IsOr || inheritedOr ? " OR " : " AND ");
                inheritedOr = false;
                AppendConditionTo(sb, term, command);
                count++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Generates a CREATE INDEX SQL statement for the given table and index definition.
        /// Override in provider-specific connectors if the DDL syntax differs.
        /// </summary>
        public virtual string CreateIndexSql(string tableName, Tables.IndexDefinition index)
        {
            var columns = string.Join(", ", index.Columns.Select(c =>
                QuoteIdentifier(c.ColumnName) + (c.IsDescending ? " DESC" : "")));

            var unique = index.Unique ? "UNIQUE " : "";
            return $"CREATE {unique}INDEX IF NOT EXISTS {QuoteIdentifier(index.Name)} ON {QuoteIdentifier(tableName)} ({columns})";
        }

        /// <summary>
        /// Generates a DROP INDEX SQL statement.
        /// Override in provider-specific connectors if the DDL syntax differs (e.g. MSSQL).
        /// </summary>
        public virtual string DropIndexSql(string tableName, Tables.IndexDefinition index)
        {
            return $"DROP INDEX IF EXISTS {QuoteIdentifier(index.Name)}";
        }

        /// <summary>
        /// Builds LIMIT and OFFSET clause
        /// </summary>
        public virtual string? LimitOffsetDefinition(DbCommand command, int? limit = null, int? offset = null)
        {
            if (limit == null)
            {
                return null;
            }
            var result = new StringBuilder();
            result.Append(" LIMIT @LIMIT");
            AddParameter(command, "@LIMIT", limit.Value);
            if (offset != null)
            {
                result.Append(" OFFSET @OFFSET");
                AddParameter(command, "@OFFSET", offset.Value);
            }
            return result.ToString();
        }

        /// <summary>
        /// Adds WHERE clause to command
        /// </summary>
        public virtual DbCommand? AddWhere(IEnumerable<Conditions.Condition>? conditions, DbCommand? command)
        {
            if (command == null || conditions == null) return command;
            var sql = ConditionDefinition(conditions, command);
            if (!string.IsNullOrEmpty(sql))
                command.CommandText += " WHERE " + sql;
            return command;
        }

        /// <summary>
        /// True when <paramref name="conditions"/> carries nothing, so a destructive statement built from it
        /// would target every row. Checked by the four destructive funnels **before** they enter
        /// <c>DoCommandWithTransaction</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a pre-check as well as <see cref="AddRequiredWhere"/>.</b> The transaction wrapper
        /// funnels every exception from its command-building callback through <c>InitException</c>, which
        /// re-wraps it in a bare <see cref="Exception"/> — so a refusal thrown from inside would reach the
        /// caller as a generic exception that no <c>catch (WholeTableWriteException)</c> or
        /// <c>catch (InvalidOperationException)</c> can select, i.e. an unhandled 500 for a request-shaped
        /// problem. Refusing before the wrapper keeps the type intact, and avoids opening a connection and
        /// beginning a transaction for a statement that will never run.</para>
        /// <para>An empty collection is the common cause — a null filter, an untranslatable predicate and a
        /// predicate reducing to <c>true</c> all produce one. <b>TASK-137: a NON-empty collection can mean
        /// everything too</b>, when every term reduces away. That case used to render <c>1 = 1</c>, which
        /// satisfied <see cref="AddRequiredWhere"/>'s "something was rendered" test, so
        /// <c>Delete(x =&gt; !empty.Contains(x.Col))</c> reached a whole-table DELETE with the guard's
        /// blessing — measured at 0 of 3 rows left, no exception. It is checked here, and not only at render
        /// time, because a refusal thrown from inside the transaction callback is re-wrapped by
        /// <c>InitException</c> into a bare <see cref="Exception"/> that no
        /// <c>catch (WholeTableWriteException)</c> can select.</para>
        /// <para><see cref="AddRequiredWhere"/> stays as the backstop for a non-empty collection that renders
        /// to nothing for some other reason (e.g. a malformed condition).</para>
        /// </remarks>
        protected static bool WouldTargetEveryRow(IEnumerable<Conditions.Condition>? conditions)
        {
            if (conditions == null) return true;
            var terms = conditions as IList<Conditions.Condition> ?? conditions.ToList();
            // Enumerates rather than reading a Count — an empty collection constrains nothing.
            if (terms.Count == 0) return true;
            // Shares IsAlwaysTrueChain with the renderer, so the guard and the emitted SQL cannot disagree
            // about what "everything" means.
            return IsAlwaysTrueChain(terms.Select((term, i) => (i > 0 && term.IsOr, IsAlwaysTrueCondition(term))));
        }

        /// <summary>
        /// Appends the <c>WHERE</c> clause for a <b>destructive</b> statement, throwing
        /// <see cref="Data.Exceptions.WholeTableWriteException"/> when nothing would be appended.
        /// </summary>
        /// <remarks>
        /// <para><b>SH-H002 — the guard is on the RENDERED clause, not on the condition collection.</b>
        /// <see cref="ConditionDefinition(IEnumerable{Conditions.Condition}, DbCommand)"/> returns
        /// <see cref="string.Empty"/> for a null <i>or</i> empty enumerable, and builds each term through
        /// <c>BuildSingleCondition</c>, which can yield an empty string for a malformed condition — so a
        /// non-empty collection can still produce no <c>WHERE</c>.</para>
        /// <para>A separate method rather than a flag on <see cref="AddWhere"/> because reads share
        /// <c>AddWhere</c> and a null filter on a read legitimately means read-everything.</para>
        /// <para><paramref name="allowAllRows"/> is the explicit opt-in used by <c>DeleteAll</c> /
        /// <c>UpdateAll</c> and by a caller-supplied <c>x =&gt; true</c>. It renders the conditionless
        /// statement — clean SQL, no <c>1 = 1</c> marker, since that pattern is indistinguishable from
        /// <c>' OR 1=1--</c> in a query log and would train operators to ignore a real attack signature.</para>
        /// </remarks>
        public virtual DbCommand? AddRequiredWhere(
            IEnumerable<Conditions.Condition>? conditions,
            DbCommand? command,
            string operation,
            string tableName,
            bool allowAllRows = false)
        {
            if (command == null) return command;

            var sql = ConditionDefinition(conditions, command);
            if (!string.IsNullOrEmpty(sql))
            {
                command.CommandText += " WHERE " + sql;
                return command;
            }

            if (!allowAllRows)
            {
                throw new Data.Exceptions.WholeTableWriteException(operation, tableName);
            }

            return command;
        }

        /// <summary>
        /// Creates a SELECT command with conditions, order, limit and offset.
        /// </summary>
        public virtual DbCommand CreateSelectCommand(DbCommand command, IEnumerable<string> tableNames, IDictionary<int, string> fields, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            return CreateSelectCommand(command, tableNames, fields, null, conditions, null, orderFields, limit, offset);
        }

        /// <summary>
        /// Returns the SQL aggregate function name for the given aggregate function.
        /// Shared by store-level and view-level aggregation.
        /// </summary>
        public static string GetSqlFunctionName(Birko.Data.Stores.AggregateFunction function)
        {
            return function switch
            {
                Birko.Data.Stores.AggregateFunction.Count => "COUNT",
                Birko.Data.Stores.AggregateFunction.Sum => "SUM",
                Birko.Data.Stores.AggregateFunction.Avg => "AVG",
                Birko.Data.Stores.AggregateFunction.Min => "MIN",
                Birko.Data.Stores.AggregateFunction.Max => "MAX",
                _ => throw new NotSupportedException($"Aggregate function {function} is not supported")
            };
        }

        /// <summary>
        /// Resolves a C# property name to its SQL column name using loaded field metadata.
        /// </summary>
        protected static string ResolveSqlName(IEnumerable<Birko.Data.SQL.Fields.AbstractField> fields, string propertyName)
        {
            var field = fields.FirstOrDefault(f =>
                f.Property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            if (field != null)
                return field.Name;

            // Fallback: use property name as-is
            return propertyName;
        }

        /// <summary>
        /// Builds SELECT and GROUP BY field dictionaries for an aggregation query.
        /// Shared by sync and async aggregate connectors to avoid duplicating query-building logic.
        /// </summary>
        protected (Dictionary<int, string> fields, Dictionary<int, string> groupFields, IEnumerable<Conditions.Condition>? conditions) BuildAggregateQueryParts<T>(
            Type type,
            Birko.Data.Stores.AggregateQuery<T> query)
            where T : Models.AbstractModel
        {
            var fields = new Dictionary<int, string>();
            var groupFields = new Dictionary<int, string>();
            int idx = 0;

            var allFields = DataBase.LoadFields(type);
            foreach (var groupBy in query.GroupByFields)
            {
                var sqlName = ResolveSqlName(allFields, groupBy);
                fields[idx] = sqlName;
                groupFields[idx] = sqlName;
                idx++;
            }

            if (!string.IsNullOrEmpty(query.TimeBucketInterval) && !string.IsNullOrEmpty(query.TimeColumn))
            {
                var timeCol = ResolveSqlName(allFields, query.TimeColumn);
                var interval = Birko.Data.Stores.TimeIntervalParser.ToSqlInterval(query.TimeBucketInterval);
                fields[idx] = $"time_bucket('{interval}', {timeCol}) AS bucket_time";
                groupFields[idx] = "bucket_time";
                idx++;
            }

            foreach (var agg in query.Aggregates)
            {
                var alias = agg.ResolvedAlias;
                var funcName = GetSqlFunctionName(agg.Function);
                var sqlFunc = agg.Function == Birko.Data.Stores.AggregateFunction.Count
                    ? "COUNT(*)"
                    : $"{funcName}({ResolveSqlName(allFields, agg.SourcePropertyName)})";
                fields[idx] = $"{sqlFunc} AS {QuoteIdentifier(alias)}";
                idx++;
            }

            var conditions = query.Filter != null
                ? DataBase.ParseConditionExpression(query.Filter as System.Linq.Expressions.LambdaExpression)
                : null;

            return (fields, groupFields, conditions);
        }

        /// <summary>
        /// Maps a <see cref="DbDataReader"/> row to an <see cref="Birko.Data.Stores.AggregateResult"/>.
        /// Shared by sync and async aggregate connectors.
        /// </summary>
        protected static Birko.Data.Stores.AggregateResult ReadAggregateResult(DbDataReader reader)
        {
            var dict = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                dict[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            return new Birko.Data.Stores.AggregateResult(dict);
        }

        /// <summary>
        /// A table name that can carry a bare SQL alias: a plain identifier, nothing needing quotes.
        /// Anchored <c>\A…\z</c> rather than <c>^…$</c> because .NET's <c>$</c> also matches before a
        /// trailing newline (the same anchoring rule as <c>ValidateRuleFieldIdentifier</c>).
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex PlainTableIdentifier =
            new(@"\A[A-Za-z_][A-Za-z0-9_]*\z", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// The token a SELECT's <c>FROM</c> / <c>JOIN</c> emits for a table: the <b>quoted</b> table name
        /// followed by a <b>bare</b> alias equal to that name.
        /// <para>
        /// TASK-211. Every read this connector builds qualifies its columns — <c>Table.Column</c> from
        /// <see cref="Birko.Data.SQL.Tables.Table.GetSelectFields"/> for the projection, from
        /// <c>DataBase.ResolveColumnName(…, withTableName: true)</c> for the <c>WHERE</c>, and the same
        /// again for <c>GROUP BY</c>, <c>ORDER BY</c> and a join's <c>ON</c>. Those qualifiers are emitted
        /// bare, while the <c>FROM</c> quoted its table — and on PostgreSQL, the one supported provider that
        /// case-folds an unquoted identifier, a bare <c>OfPersons</c> folds to <c>ofpersons</c> and does not
        /// match the quoted relation. Measured on 16.4: <c>SELECT OfPersons.Name FROM "OfPersons"</c> →
        /// <c>ERROR: missing FROM-clause entry for table "ofpersons"</c>, which this layer then swallowed
        /// into an empty result. So <b>every read of every PascalCase-named entity returned zero rows</b>,
        /// silently — reads, not just the views the defect was filed against.
        /// </para>
        /// <para>
        /// The alias is what makes the fix total. Quoting each qualifier instead would mean teaching every
        /// producer of a qualified name — including the ones that wrap it, <c>LOWER(T.Col)</c>,
        /// <c>COALESCE</c>, the <c>.Date</c> rewrite — and a producer missed is the identical silent empty
        /// result, which is precisely how this survived. Aliasing is one site and correct by construction:
        /// the alias folds exactly as the qualifiers do. It also keeps
        /// <c>DataBase.ParseConditionExpression</c> provider-independent, which the alternative would not.
        /// </para>
        /// <para>
        /// Quoting the alias would defeat it — a quoted alias is case-sensitive again and the bare
        /// qualifiers would stop matching. That is consistent with, not a departure from, § Conventions'
        /// <i>quote tables, never quote columns</i>: the relation is still addressed quoted, and the alias
        /// is the bare name every column reference already resolves against.
        /// </para>
        /// <para>
        /// A name that cannot take a bare alias (quoted-only: spaces, punctuation, a reserved word) is
        /// emitted unaliased, exactly as before. That is not a gap — such a table already cannot be read
        /// through a qualified SELECT on any provider (measured on PostgreSQL: <c>SELECT Order.Guid FROM
        /// "Order"</c> is <c>syntax error at or near "."</c> with or without an alias) — and it keeps the
        /// change away from the one shape it is not about, an unqualified <c>SELECT COUNT(*)</c>, which
        /// works today for such a table and must keep working.
        /// </para>
        /// </summary>
        protected virtual string SelectTableReference(string table)
        {
            // The alias is emitted BARE and unescaped, so it is gated on a strict identifier pattern —
            // anything else gets the quoted name only. That is what keeps § Conventions' rule that an
            // identifier reaching interpolated SQL is never caller text validated loosely: a name that is
            // not a plain identifier cannot reach the statement unquoted.
            var quoted = QuoteIdentifier(table);
            return PlainTableIdentifier.IsMatch(table)
                ? quoted + " AS " + table
                : quoted;
        }

        /// <summary>
        /// Creates a SELECT command with joins, conditions, grouping, order, limit and offset.
        /// </summary>
        public virtual DbCommand CreateSelectCommand(DbCommand command, IEnumerable<string> tableNames, IDictionary<int, string> fields, IEnumerable<Conditions.Join>? joinconditions = null, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<int, string>? groupFields = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            command.CommandText = "SELECT " + string.Join(", ", fields.Values) + " FROM ";

            Dictionary<string, List<Conditions.Join>> joins = new();
            if (joinconditions != null && joinconditions.Any())
            {
                string? prevleft = null;
                string? prevright = null;
                foreach (var join in joinconditions)
                {
                    if (!string.IsNullOrEmpty(prevleft) && !string.IsNullOrEmpty(prevright) && !joins.ContainsKey(join.Left) && prevright == join.Left && joins.ContainsKey(prevleft))
                    {
                        joins[prevleft].Add(join);
                    }
                    else
                    {
                        if (!joins.ContainsKey(join.Left))
                        {
                            joins.Add(join.Left, new List<Conditions.Join>());
                        }
                        joins[join.Left].Add(join);
                        prevleft = join.Left;
                    }
                    prevright = join.Right;
                }
            }

            int i = 0;
            foreach (var table in tableNames.Distinct())
            {
                if (i > 0)
                {
                    command.CommandText += ", ";
                }
                command.CommandText += SelectTableReference(table);
                if (joins != null && joins.ContainsKey(table))
                {
                    var joingroups = joins[table].GroupBy(x => new { x.Right, x.JoinType }).ToDictionary(x => x.Key, x => x.SelectMany(y => y.Conditions ?? Enumerable.Empty<Conditions.Condition>()).Where(z => z != null));
                    foreach (var joingroup in joingroups.Where(x => x.Value.Any()))
                    {
                        command.CommandText +=
                            joingroup.Key.JoinType switch
                            {
                                Conditions.JoinType.Inner => " INNER JOIN ",
                                Conditions.JoinType.LeftOuter => " LEFT OUTER JOIN ",
                                _ => " CROSS JOIN ",
                            };
                        command.CommandText += SelectTableReference(joingroup.Key.Right);
                        if (joingroup.Key.JoinType != Conditions.JoinType.Cross && joingroup.Value != null && joingroup.Value.Any())
                        {
                            command.CommandText += " ON (";
                            command.CommandText += ConditionDefinition(joingroup.Value, command);
                            command.CommandText += ")";
                        }
                    }
                }
                i++;
            }
            AddWhere(conditions, command);
            if (groupFields != null && groupFields.Any())
            {
                command.CommandText += " GROUP BY " + string.Join(", ", groupFields.Values);
            }
            if (orderFields != null && orderFields.Any())
            {
                command.CommandText += " ORDER BY " + string.Join(", ", orderFields.Select(kvp => string.Format("{0} {1}", kvp.Key, kvp.Value ? "DESC" : "ASC")));
            }
            if (limit != null)
            {
                command.CommandText += LimitOffsetDefinition(command, limit, offset) ?? string.Empty;
            }
            return command;
        }
    }
}
