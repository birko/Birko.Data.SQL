using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using Birko.Data.SQL.Connectors.Strategies;
using PasswordSettings = Birko.Configuration.PasswordSettings;

namespace Birko.Data.SQL.Connectors
{
    public delegate void InitConnector(AbstractConnector connector);
    public delegate void OnException(Exception ex, string? commandText);
    public delegate void OnExecute(string commandText);

    /// <summary>
    /// An index declared on an entity that could not be created during schema-ensure, together with the
    /// error that prevented it. Almost always a UNIQUE index over data that already violates it.
    /// </summary>
    public sealed class IndexCreationFailure
    {
        public IndexCreationFailure(string tableName, string? indexName, Exception error)
        {
            TableName = tableName;
            IndexName = indexName;
            Error = error;
        }

        public string TableName { get; }
        public string? IndexName { get; }
        public Exception Error { get; }

        public override string ToString()
            => $"index '{IndexName ?? "(unnamed)"}' on table '{TableName}': {Error.Message}";
    }

    /// <summary>
    /// A statement that failed because its table was missing <b>although this connector had already
    /// created that table</b> — TASK-286's anomaly — on a path that answers the failure instead of
    /// reporting it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TASK-287. TASK-285 made a <c>COUNT</c> of a missing table return <b>0</b>, which is the right
    /// answer and is the answer a <c>SELECT</c> of the same table already gave. TASK-286 then made
    /// <c>EnsureSchemaAndReport</c> annotate its exception when the table being reported missing is one
    /// this connector created. The two do not compose: the annotation travels <b>on the thrown
    /// exception</b>, and on the count path there is no longer a thrown exception to travel on, so it was
    /// produced and immediately discarded.
    /// </para>
    /// <para>
    /// ⚠ <b>That blind spot was over the only shape ever seen in the wild.</b> Both occurrences consumer
    /// Symbio's TASK-602 recorded were counts. Measured 2026-08-31 against a live API, both halves in the
    /// same deliberately-forced condition minutes apart: a <b>COUNT</b> answered <c>200</c> with
    /// <c>totalCount: 0</c> and logged <b>zero</b> lines, while a <b>write</b> answered <c>500</c> and
    /// logged the annotation. Nineteen instrumented bring-ups had logged <b>0</b> escapes against
    /// <b>4,397</b> benign first-touch errors — on the write path that silence is real, on the count path
    /// <c>0</c> is what a blind instrument reports whether the condition happened 0 times or 19.
    /// </para>
    /// <para>
    /// ⚠ <b>Anomalous only.</b> An ordinary lazy first-touch failure is <i>also</i> a missing table and is
    /// roughly <b>245× more common</b> per bring-up, so this channel discriminates on TASK-286's
    /// annotation — "this connector already created it" — and never on "the table was missing". A channel
    /// that recorded every missing table would be no signal at all.
    /// </para>
    /// </remarks>
    public sealed class SchemaEscape
    {
        public SchemaEscape(IReadOnlyList<string> tableNames, string? annotation, Exception error)
        {
            TableNames = tableNames;
            Annotation = annotation;
            Error = error;
            DetectedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The tables the answered statement named, distinct and ordered.</summary>
        /// <remarks>
        /// The set, not the one that was absent: a joined count names several and the provider's error
        /// does not always say which. Recording the set is honest; guessing would not be.
        /// </remarks>
        public IReadOnlyList<string> TableNames { get; }

        /// <summary>TASK-286's annotated message, carrying the statement and the create's timestamp.</summary>
        public string? Annotation { get; }

        /// <summary>The failure as caught, with the provider's own error in its chain.</summary>
        public Exception Error { get; }

        /// <summary>When this escape was answered rather than reported.</summary>
        public DateTimeOffset DetectedAt { get; }

        public override string ToString()
            => $"schema escape on '{string.Join(", ", TableNames)}' at {DetectedAt:O}: {Annotation ?? Error.Message}";
    }


    /// <summary>
    /// A diagnostic subscriber that threw. Recorded so a handler's own defect is reported rather than
    /// discarded — <b>and never raised as an event</b>, because an event announcing an event's failure has
    /// the identical hole.
    /// </summary>
    /// <remarks>
    /// TASK-289. Same "current state, keyed" contract as <see cref="IndexCreationFailure"/>: one entry per
    /// (channel, exception type) however many times it has fired, latest occurrence wins, empty in the
    /// normal case. Read it when a diagnostic channel appears to be silent — an entry here means the
    /// framework raised the event and the host's handler failed, which looks exactly like nothing having
    /// happened.
    /// </remarks>
    public sealed class SubscriberFailure
    {
        public SubscriberFailure(string channel, Exception error)
        {
            Channel = channel;
            Error = error;
            DetectedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The event whose subscriber threw, e.g. <c>OnSchemaEscapeDetected</c>.</summary>
        public string Channel { get; }

        /// <summary>The handler's own exception, exactly as thrown.</summary>
        public Exception Error { get; }

        public DateTimeOffset DetectedAt { get; }

        public override string ToString()
            => $"subscriber to '{Channel}' threw at {DetectedAt:O}: {Error.GetType().Name}: {Error.Message}";
    }
    public abstract partial class AbstractConnector : AbstractConnectorBase
    {
        public event InitConnector OnInit = null!;
        public event OnException? OnException;
        public event OnExecute? OnExecute;

        // Keyed by (table, index), NOT a list, and it is CURRENT STATE rather than a log of attempts.
        //
        // Connectors are cached process-wide per (connector type, settings id) in DataBase.GetConnector,
        // while the `_initialized` flag that gates schema-ensure lives on the STORE. A web app resolving a
        // scoped store per request therefore re-runs schema-ensure per request, against one shared
        // connector: an append-only list grew by one entry per request forever, on a process-lifetime
        // object, for as long as the index stayed unbuildable.
        //
        // The re-attempt itself is deliberately KEPT — it is what lets the index appear on its own once an
        // operator repairs the offending rows, with no restart. Only the bookkeeping is deduplicated.
        // TASK-254 extracted the keyed / transition-fired / clearable / locked / ordered bookkeeping into
        // SchemaEnsureFailureLog so there is ONE implementation of it, not two: the hypertable channel on
        // TimescaleDBConnector needs the identical behaviour. This surface is unchanged -- IndexCreationFailure,
        // IndexCreationFailures, OnIndexCreationFailed, Record*, Clear* all keep their exact signatures and
        // semantics, because a consumer depends on them by name (measured: Symbio's production code, tests,
        // specs and CLAUDE.md).
        private readonly SchemaEnsureFailureLog<IndexCreationFailure> _indexCreationFailures =
            new(f => f.TableName + "\u0000" + (f.IndexName ?? string.Empty));

        private static string IndexFailureKey(string tableName, string? indexName)
            => tableName + "\u0000" + (indexName ?? string.Empty);

        /// <summary>
        /// Indexes that could not be created on their most recent schema-ensure attempt. Empty in the
        /// normal case.
        /// </summary>
        /// <remarks>
        /// Current state, not history: an index that later builds successfully (because the data blocking
        /// it was repaired) drops out of this collection, and a given index appears at most once no matter
        /// how many times schema-ensure has run. An empty collection is NOT proof that every declared index
        /// exists — a store is initialised lazily on first access, so an entity that has not been touched
        /// yet has not attempted its indexes.
        /// </remarks>
        public IReadOnlyList<IndexCreationFailure> IndexCreationFailures
        {
            // Ordered so a host's startup report is stable rather than dictionary-dependent. The sort key
            // combines table and index exactly as the pre-TASK-254 OrderBy/ThenBy pair did.
            get => _indexCreationFailures.Snapshot;
        }

        /// <summary>
        /// Raised when a declared index could not be created during schema-ensure. Subscribe to log or
        /// escalate; the store initialises regardless.
        /// </summary>
        /// <remarks>
        /// Fires on the TRANSITION into failure, not on every attempt — otherwise a per-request store over
        /// an unbuildable index would raise this on every HTTP request. If the index later builds and then
        /// fails again, that is a new transition and raises again.
        /// </remarks>
        public event Action<IndexCreationFailure>? OnIndexCreationFailed;

        /// <summary>
        /// Records an index that schema-ensure could not build, and notifies any subscriber the first time
        /// that index enters the failed state.
        /// </summary>
        /// <remarks>
        /// Deliberately does NOT rethrow — see <c>CreateTable(IEnumerable&lt;Tables.Table&gt;)</c> for why
        /// an unbuildable index must not take the table's whole read surface with it.
        /// </remarks>
        protected void RecordIndexCreationFailure(string tableName, string? indexName, Exception error)
        {
            var failure = new IndexCreationFailure(tableName, indexName, error);
            // Record returns true only on the TRANSITION into failure -- an event per attempt would fire on
            // every HTTP request for a per-request store over an unbuildable index.
            if (_indexCreationFailures.Record(IndexFailureKey(tableName, indexName), failure))
            {
                // ⚠ A BARE Invoke, and deliberately still so — TASK-283 owns this one. A throwing
                // subscriber here propagates out of schema-ensure and bricks the entity, which is the
                // same hole TASK-289 closed on OnSchemaEscapeDetected with RaiseDiagnostic. It is NOT
                // changed here because this channel is consumed in Symbio production code, its host, two
                // test files and its specs, so changing whether a handler exception propagates is a
                // behaviour change on consumed surface and needs TASK-283 own measurement first.
                // When that measurement is done, adopt RaiseDiagnostic rather than writing a second
                // policy beside it.
                OnIndexCreationFailed?.Invoke(failure);
            }
        }

        /// <summary>
        /// Clears any recorded failure for an index that has now been created successfully, so
        /// <see cref="IndexCreationFailures"/> cannot report a condition an operator has already repaired.
        /// </summary>
        protected void ClearIndexCreationFailure(string tableName, string? indexName)
        {
            _indexCreationFailures.Clear(IndexFailureKey(tableName, indexName));
        }

        // TASK-287 — the same keyed / transition-fired / locked / ordered bookkeeping the index channel
        // above uses, for schema escapes that a path ANSWERS instead of reporting.
        //
        // Keyed rather than listed for the reason recorded on SchemaEnsureFailureLog: connectors are cached
        // process-wide per (connector type, settings id) while a web app resolves a store per request, so a
        // list on this object grows for as long as the condition lasts.
        //
        // ⚠ There is deliberately NO Clear counterpart, and that is the one place this channel departs from
        // the index one. An unbuildable index is a CURRENT condition an operator repairs, so a stale record
        // must be able to drop out. An escape is a PAST event at a timestamp: nothing an operator does makes
        // it not have happened, and the condition heals on its own within milliseconds (the very next count
        // usually succeeds), so clearing on success would delete the record before any reader could see it —
        // leaving the channel observable only through an event, which is precisely the "recorded nowhere"
        // outcome TASK-286 was written to avoid.
        private readonly SchemaEnsureFailureLog<SchemaEscape> _schemaEscapes =
            new(f => string.Join("\u0000", f.TableNames));

        /// <summary>
        /// Schema escapes this connector answered rather than reported — a statement that failed because
        /// its table was missing on a table this connector had already created. Empty in the normal case,
        /// and empty is the normal case.
        /// </summary>
        /// <remarks>
        /// ⚠ <b>Ordinary lazy first-touch is NOT recorded here.</b> That is also a missing table and is far
        /// more common (measured at roughly 245 per bring-up in consumer Symbio against 0 escapes in
        /// nineteen); recording it would drown the one entry that means something.
        /// <para>
        /// One entry per statement's table set, however many times it has occurred — the latest occurrence
        /// overwrites, so <see cref="SchemaEscape.DetectedAt"/> is the most recent one. A repeat therefore
        /// refreshes the record without raising <see cref="OnSchemaEscapeDetected"/> again.
        /// </para>
        /// </remarks>
        public IReadOnlyList<SchemaEscape> SchemaEscapes => _schemaEscapes.Snapshot;

        /// <summary>
        /// Raised the first time a given statement's table set produces a schema escape that was answered
        /// rather than reported. Subscribe to log or escalate; the caller still receives its answer.
        /// </summary>
        /// <remarks>
        /// Fires on the TRANSITION into the condition, not on every occurrence — the count path runs per
        /// request, so an event per occurrence would storm exactly as the index channel's did before
        /// TASK-204 keyed it.
        /// <para>
        /// ⚠ <b>A host has to subscribe for this to reach a log.</b> Consumer Symbio surfaces connector
        /// diagnostics through boot-time checks, which by construction cannot see a runtime escape — so
        /// TASK-286 rode on the exception message instead. On this path there is no exception to ride on,
        /// which is why the channel exists and why wiring it is the host's remaining half.
        /// </para>
        /// </remarks>
        public event Action<SchemaEscape>? OnSchemaEscapeDetected;

        /// <summary>
        /// The marker TASK-286's annotation carries in the anomalous case, and the <b>only</b>
        /// discriminator this channel uses.
        /// </summary>
        /// <remarks>
        /// One producer: <see cref="DescribeSchemaEscape"/> writes it and
        /// <see cref="IsAnomalousSchemaEscapeChain"/> reads it, so the two cannot drift into disagreeing
        /// about which case is the anomaly.
        /// </remarks>
        private const string AnomalousEscapeMarker = "but this connector already created it";

        /// <summary>
        /// Whether anywhere in <paramref name="ex"/>'s chain sits TASK-286's anomalous annotation.
        /// </summary>
        /// <remarks>
        /// ⚠ <b>The chain, not the outermost message.</b> <see cref="EnsureSchemaAndReport"/> rethrows as
        /// <c>new Exception(annotatedText, ex)</c> and callers may wrap that again, so the annotation sits
        /// at an arbitrary depth. A check on <c>ex.Message</c> alone compiles, runs, and silently never
        /// matches — the identical inert guard already shipped once here, which is why
        /// <see cref="AbstractConnectorBase.IsMissingTableExceptionChain"/> exists.
        /// <para>
        /// ⚠ <b>Defensive, not witnessed — say which, or the next reader deletes it as dead weight.</b>
        /// Measured on SQLite: on both live count paths the annotation is at depth <b>0</b>, because
        /// nothing between <c>EnsureSchemaAndReport</c> and the catch wraps again. So collapsing this loop
        /// to a single-message check fails exactly <b>one</b> test — the synthetic one that hands it a
        /// twice-wrapped exception — and no end-to-end one. It is kept because the depth is a property of
        /// the call stack rather than a promise: <c>InitException</c> is reached from eleven sites, a
        /// provider may wrap, and the failure mode of guessing wrong is a guard that runs and never
        /// matches.
        /// </para>
        /// </remarks>
        public bool IsAnomalousSchemaEscapeChain(Exception? ex)
            => FindAnomalousAnnotation(ex) != null;

        /// <summary>
        /// Records — and, on the transition, raises — a schema escape that a caller is about to answer
        /// instead of reporting. Does nothing for ordinary lazy first-touch.
        /// </summary>
        /// <remarks>
        /// Called from both <c>SelectCount</c> overloads. It is one shared producer rather than a copy in
        /// each because the sync and async count paths are separate code, and shipping one of the two is
        /// how half a fix looks green here.
        /// <para>
        /// ⚠ <b>It deliberately does not rethrow.</b> Doing so is tempting — the table is not genuinely
        /// missing, so <c>0</c> is a wrong answer — but it silently reopens what TASK-285 closed, and at
        /// roughly one bring-up in five that turns a real defect back into something read as infrastructure
        /// flakiness. This task adds observation, not behaviour. If the throw is ever wanted it is a
        /// decision with a consequence, taken deliberately.
        /// </para>
        /// </remarks>
        // TASK-289 — the last-resort sink for a diagnostic handler that threw. Keyed by (channel, exception
        // type) so a logging bug and a metrics bug stay distinguishable, and DELIBERATELY WITHOUT AN EVENT:
        // announcing a subscriber failure through a subscriber is the same hole one level up.
        private readonly SchemaEnsureFailureLog<SubscriberFailure> _subscriberFailures =
            new(f => f.Channel + " " + f.Error.GetType().FullName);

        /// <summary>
        /// Diagnostic subscribers that threw. Empty in the normal case; an entry means the framework
        /// raised an event and the <b>host's</b> handler failed.
        /// </summary>
        /// <remarks>
        /// Check this when a diagnostic channel looks silent — a broken handler and an event that never
        /// fired are indistinguishable from the outside, and this is what tells them apart.
        /// </remarks>
        public IReadOnlyList<SubscriberFailure> SubscriberFailures => _subscriberFailures.Snapshot;

        /// <summary>
        /// Raises a diagnostic event so that a handler's exception <b>cannot</b> reach the caller, and so
        /// that one bad handler cannot suppress the others.
        /// </summary>
        /// <remarks>
        /// <para>
        /// TASK-289, measured before it was written. <c>OnSchemaEscapeDetected</c> is raised from inside
        /// <see cref="EnsureSchemaAndReport"/>, between building the annotated exception and throwing it,
        /// so a subscriber that threw <b>replaced</b> that exception. Two consequences, and the second is
        /// the one that is easy to miss:
        /// </para>
        /// <list type="number">
        /// <item>the write lost TASK-286's annotation — the instrument was destroyed by the handler written
        /// to read it;</item>
        /// <item>the replacement no longer satisfied <c>SelectCount</c>'s
        /// <c>catch … when (IsMissingTableExceptionChain(ex))</c>, so the <b>exception filter stopped
        /// matching</b>, the catch never ran, and the count threw instead of returning <c>0</c> — TASK-285
        /// reopened from outside the framework, by a host doing nothing worse than escalating.</item>
        /// </list>
        /// <para>
        /// ⚠ <b>Per subscriber, not one <c>try</c> around the multicast.</b> A plain
        /// <c>handler?.Invoke(x)</c> stops at the first delegate that throws, so a host with a logger and a
        /// metric loses the metric to a bug in the logger — a silent drop of exactly the kind this channel
        /// exists to prevent. <see cref="Delegate.GetInvocationList"/> isolates them.
        /// </para>
        /// <para>
        /// ⚠ <b>Swallowed here means RECORDED, not discarded</b> — see <see cref="SubscriberFailures"/>.
        /// A channel that hid a handler's own defect would be the same failure one level up.
        /// </para>
        /// <para>
        /// ⚠ <b>Callers must complete their bookkeeping BEFORE calling this.</b> That ordering is what kept
        /// TASK-288's healing alive when a subscriber threw, and it was luck rather than design until this
        /// method existed. It has a test.
        /// </para>
        /// <para>
        /// <b>Not yet used by <c>OnIndexCreationFailed</c></b>, deliberately. That channel is consumed in
        /// production by Symbio, so changing whether a handler's exception propagates is a behaviour change
        /// on consumed surface and needs its own measurement first — [[TASK-283]] owns it, and this method
        /// is the candidate answer for it to adopt rather than a second policy to live beside it.
        /// </para>
        /// </remarks>
        /// <param name="handler">The event's backing delegate, possibly null.</param>
        /// <param name="payload">What each subscriber receives.</param>
        /// <param name="channel">The event's name, recorded against any failure.</param>
        protected void RaiseDiagnostic<T>(Action<T>? handler, T payload, string channel)
        {
            if (handler == null)
            {
                return;
            }

            foreach (var subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((Action<T>)subscriber)(payload);
                }
                catch (Exception ex)
                {
                    _subscriberFailures.Record(channel + " " + ex.GetType().FullName,
                        new SubscriberFailure(channel, ex));
                }
            }
        }

        protected void RecordSchemaEscape(Exception ex, IEnumerable<string>? tableNames)
        {
            var annotation = FindAnomalousAnnotation(ex);
            if (annotation == null)
            {
                // Ordinary lazy first-touch. Silent on purpose — see SchemaEscapes.
                return;
            }

            // TASK-288 — recording the escape and invalidating the stores' remembered initialization are
            // the SAME event, so they are one statement apart and cannot be wired up separately. A heal
            // that nobody could see would have cost Symbio TASK-602 the discriminator it was reasoning
            // from ("a real absence never heals") and handed back nothing.
            System.Threading.Interlocked.Increment(ref _schemaGeneration);

            var names = (tableNames ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var escape = new SchemaEscape(names, annotation, ex);
            if (_schemaEscapes.Record(string.Join("\u0000", names), escape))
            {
                // TASK-289 — via RaiseDiagnostic, never a bare Invoke: this runs inside
                // EnsureSchemaAndReport between building the annotated exception and throwing it, so a
                // handler that threw used to REPLACE that exception -- destroying TASK-286's annotation
                // and, because the replacement no longer matched SelectCount's
                // `when (IsMissingTableExceptionChain(ex))` filter, making the count throw instead of
                // returning 0. Everything above this line is bookkeeping that must already be done.
                RaiseDiagnostic(OnSchemaEscapeDetected, escape, nameof(OnSchemaEscapeDetected));
            }
        }

        /// <summary>
        /// The annotated message from the chain, so a subscriber need not walk it again, and so the
        /// predicate and the record cannot disagree about what they matched.
        /// </summary>
        private static string? FindAnomalousAnnotation(Exception? ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current.Message != null
                    && current.Message.Contains(AnomalousEscapeMarker, StringComparison.Ordinal))
                {
                    return current.Message;
                }
            }
            return null;
        }

        public AbstractConnector(PasswordSettings settings) : base(settings)
        {
        }

        /// <summary>
        /// Invokes the OnExecute event. Can be called from derived classes.
        /// </summary>
        protected void InvokeOnExecute(string commandText) => OnExecute?.Invoke(commandText);

        public virtual void InitException(Exception ex, string? commandText)
        {
            if (OnException != null)
            {
                OnException.Invoke(ex, commandText);
            }
            else
            {
                throw ex;
            }
        }

        // TASK-286 — when this connector last completed a CREATE TABLE for a given table name.
        //
        // ⚠ TASK-286 said "diagnostic only: nothing branches on it". That stopped being true at TASK-288 —
        // an entry here is now what tells EnsureSchemaAndReport it is looking at the anomaly rather than an
        // ordinary first touch, which drives both the SchemaEscapes record and the SchemaGeneration bump
        // that makes a store forget its remembered initialization. It is load-bearing; treat a change to
        // what gets recorded here as a behaviour change.
        //
        // It is still deliberately NOT a claim that the table exists now. It answers one question that
        // could not otherwise be answered after the fact — "was this table created earlier in this process,
        // and then reported missing?" — which is exactly the open question in consumer Symbio's TASK-602.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _tablesCreated
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Tables this connector has completed a <c>CREATE TABLE</c> for, and when.
        /// </summary>
        /// <remarks>
        /// ⚠ <b>Not an inventory and not proof of existence.</b> Schema-ensure is lazy, so a table nobody
        /// has touched is absent from this map while existing perfectly well in an older database; and a
        /// create that was later rolled back with a caller's transaction stays recorded. It exists to date
        /// a create, not to assert a current state — the same caveat <see cref="IndexCreationFailures"/>
        /// carries, for the same lazy-initialisation reason.
        /// </remarks>
        public IReadOnlyDictionary<string, DateTimeOffset> TablesCreated => _tablesCreated;

        /// <summary>Records that a <c>CREATE TABLE</c> for <paramref name="name"/> completed.</summary>
        protected void RecordTableCreated(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                _tablesCreated[name] = DateTimeOffset.UtcNow;
            }
        }

        // TASK-288 — bumped whenever an anomalous schema escape is seen, and read by the SQL stores'
        // CanTrustRememberedInitialization so a store whose table has vanished forgets that it is
        // initialised and schema-ensures again on its next operation.
        //
        // ⚠ A PULL, not an event, and that is the whole reason it is a counter. The obvious wiring is for
        // a store to subscribe to something on the connector — but connectors are cached process-wide per
        // (connector type, settings id) while a web app resolves a store per request, so a per-request
        // subscriber list on a process-lifetime object grows without bound and keeps dead stores alive.
        // That is TASK-204's defect exactly. A counter the store reads costs one volatile read per
        // operation and cannot leak.
        private long _schemaGeneration;

        /// <summary>
        /// Increments each time this connector observes a table it created being reported missing.
        /// </summary>
        /// <remarks>
        /// A store that recorded this value when it initialised, and later reads a different one, knows
        /// its schema-ensure may no longer hold. Meaningless in absolute terms — only changes matter.
        /// </remarks>
        public long SchemaGeneration => System.Threading.Interlocked.Read(ref _schemaGeneration);

        /// <summary>
        /// The shared body of every provider's <c>OnException</c> handler: ensure the schema if the failure
        /// looks like a missing table, and then <b>always report the failure</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// TASK-277. All four handlers previously answered a missing table with <c>DoInit()</c> and a
        /// <b>return</b> — so the statement was discarded and the caller was told it had succeeded. For a
        /// write that means the row is silently gone: measured on SQLite as <c>CreateAsync</c> returning a
        /// non-empty <c>Guid</c> against a table that does not exist and is not created.
        /// </para>
        /// <para>
        /// ⚠ <b><c>DoInit()</c> is not what makes the next attempt succeed, and TASK-288 stopped this
        /// comment claiming it was.</b> <c>DoInit()</c> raises the <c>OnInit</c> event, which nothing in
        /// the framework subscribes to — only a consumer can, through <c>IDataBaseRepository.AddOnInit</c>
        /// — and it issues no per-entity DDL of its own. Measured on SQLite (Symbio TASK-627, reproduced
        /// in <c>VanishedTableHealingTests</c>): with the table dropped beneath an initialised store,
        /// <b>five consecutive writes threw and <c>sqlite_master</c> held 0 rows throughout</b>. Only a new
        /// store instance recovered it, which is what proved the broken state was the store's remembered
        /// <c>_initialized</c> flag rather than anything on disk. <c>DoInit()</c> is still called, because
        /// a consumer that registered a handler should still get it.
        /// </para>
        /// <para>
        /// <b>What does make the next attempt succeed is <see cref="SchemaGeneration"/>.</b> An anomalous
        /// escape — a table this connector itself created being reported missing — bumps it, and the SQL
        /// stores stop trusting their remembered initialization, so the next operation re-runs
        /// schema-ensure. The framework's own asymmetry argues for this: per
        /// <c>AbstractStore.CanRememberInitialization</c>, answering "do not remember" costs one idempotent
        /// <c>CREATE TABLE IF NOT EXISTS</c> while answering it wrongly leaves a store broken for the life
        /// of the process.
        /// </para>
        /// <para>
        /// <b>And every such invalidation is recorded</b> on <see cref="SchemaEscapes"/> /
        /// <see cref="OnSchemaEscapeDetected"/>, deliberately. Healing removes the discriminator Symbio
        /// TASK-602 was using — "a real absence never heals, and both observed occurrences healed, so the
        /// anomaly is not an absent table" — so the behaviour change had to hand back a stronger signal
        /// than it took away: an absent table now announces itself in a channel instead of being inferred
        /// from a symptom.
        /// </para>
        /// <para>
        /// <b>This does not touch the read contract.</b> A missing table on a read is handled in
        /// <c>RunReaderCommandOn</c> — <c>catch (Exception ex) when (IsMissingTableException(ex))</c> →
        /// <c>yield break</c> — which never reaches here. TASK-211 narrowed *which* errors count as a
        /// missing table; whether an empty result is the right answer for a read is its decision, and it
        /// keeps its stated callers (lazy create-on-first-use, view-existence probing, CR-M149).
        /// </para>
        /// </remarks>
        protected void EnsureSchemaAndReport(Exception ex, string? commandText)
        {
            if (!IsInitializing && IsMissingTableException(ex))
            {
                DoInit();
            }

            // One detection point for the whole framework. Both the record and the generation bump hang off
            // it, and so does the annotation, so the three cannot disagree about which case is the anomaly.
            var reported = new Exception(DescribeSchemaEscape(ex, commandText), ex);
            RecordSchemaEscape(reported, CreatedTablesNamedIn(commandText).Select(kvp => kvp.Key));
            throw reported;
        }

        /// <summary>
        /// The message <see cref="EnsureSchemaAndReport"/> throws: the command text, plus — when this is a
        /// missing-table failure on a table this connector already created — the time of that create.
        /// </summary>
        /// <remarks>
        /// <para>
        /// TASK-286, and it exists to make one specific anomaly self-reporting rather than hunted.
        /// A store gates every public CRUD call on <c>EnsureInitializedAsync</c>, which creates the table
        /// before the statement runs — so a missing-table failure arriving <i>here</i> means the store
        /// believed it was already initialised. If this connector also has a create recorded for that
        /// table, the table was created and later found missing, and the two timestamps bound the window.
        /// </para>
        /// <para>
        /// ⚠ <b>It rides on the exception message on purpose.</b> Consumer Symbio surfaces connector
        /// diagnostics through boot-time checks (<c>UniqueIndexDataCheck</c>, <c>SchemaDriftCheck</c>),
        /// which by construction cannot see a runtime escape, and it subscribes to no connector event. An
        /// event would therefore have been recorded nowhere. This lands in the log the host already writes,
        /// with no wiring to forget.
        /// </para>
        /// <para>
        /// ⚠ <b>Diagnostic only — nothing branches on it.</b> Measured in Symbio across eleven hypotheses:
        /// every mechanism reachable by reading the code was eliminated, so the remaining candidates are
        /// timing or visibility effects that only an observation can separate. Guessing further was the
        /// wrong instrument.
        /// </para>
        /// </remarks>
        /// <summary>
        /// The tables this connector created that <paramref name="commandText"/> mentions — empty for an
        /// ordinary first-touch failure, non-empty for the anomaly.
        /// </summary>
        /// <remarks>
        /// Substring rather than SQL parsing: a false positive costs one extra line in an exception nobody
        /// sees unless something already went wrong.
        /// <para>
        /// Extracted at TASK-288 because three things now hang off this one answer — the annotation, the
        /// <see cref="SchemaEscapes"/> record, and the <see cref="SchemaGeneration"/> bump that lets a
        /// store heal. Computing it in two places is how those three would come to disagree about which
        /// case is the anomaly.
        /// </para>
        /// </remarks>
        private List<KeyValuePair<string, DateTimeOffset>> CreatedTablesNamedIn(string? commandText)
        {
            if (string.IsNullOrEmpty(commandText))
            {
                return new List<KeyValuePair<string, DateTimeOffset>>();
            }
            return _tablesCreated
                .Where(kvp => commandText!.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private string? DescribeSchemaEscape(Exception ex, string? commandText)
        {
            if (string.IsNullOrEmpty(commandText) || !IsMissingTableException(ex))
            {
                return commandText;
            }

            var created = CreatedTablesNamedIn(commandText);

            if (created.Count == 0)
            {
                return commandText
                    + " [schema-ensure escape: this connector has NO recorded CREATE TABLE for any table"
                    + " named in the statement]";
            }

            var when = string.Join(", ", created.Select(kvp =>
                $"{kvp.Key} created {kvp.Value:O}"));
            // The marker is interpolated rather than spelled out, so DescribeSchemaEscape (which writes it)
            // and IsAnomalousSchemaEscapeChain (which reads it) have ONE producer: TASK-287 has to
            // recognise this exact case on the count path, where the exception is answered rather than
            // thrown, and two spellings of the discriminator is how that guard would go quietly inert.
            return commandText
                + $" [schema-ensure escape: reported missing at {DateTimeOffset.UtcNow:O}, "
                + AnomalousEscapeMarker
                + $" — {when}. The store's init gate had therefore passed,"
                + " so the table was created and later found absent; these two timestamps bound the"
                + " window (TASK-286 / Symbio TASK-602)]";
        }

        public void DoInit()
        {
            if (!IsInitializing)
            {
                IsInitializing = true;
                OnInit?.Invoke(this);
                IsInitializing = false;
            }
        }

        /// <summary>
        /// The ambient transaction boundary covering THIS connector's database, or null.
        /// </summary>
        /// <remarks>
        /// Checked before the serialization gate because a command joining a caller's own connection needs no
        /// mutual exclusion against commands on other connections — taking the gate there is how a
        /// boundary holder and the gate holder would deadlock on each other.
        /// </remarks>
        protected AmbientSqlTransaction.Entry? AmbientTransaction
            => AmbientSqlTransaction.Find(_settings?.GetId());

        /// <summary>
        /// Whether DDL issued right now would survive a rollback of the ambient boundary — i.e. whether a
        /// schema-ensure performed in this flow is durable.
        /// </summary>
        /// <remarks>
        /// TASK-244, and it is deliberately expressed from the same two facts <see cref="DoDdlCommand"/>
        /// consults, so the two cannot disagree:
        /// <list type="bullet">
        /// <item>no ambient boundary — nothing can roll the DDL back, so it is durable;</item>
        /// <item>an ambient boundary on a provider whose DDL is <b>not</b> transactional (MySQL alone) —
        /// <see cref="DoDdlCommand"/> suppresses the ambient and the statement commits on its own, so it is
        /// durable, and TASK-243 has a test pinning exactly that;</item>
        /// <item>an ambient boundary on PostgreSQL / SQL Server / SQLite — the DDL is part of the boundary
        /// and a rollback removes it, so it is <b>not</b> durable.</item>
        /// </list>
        /// The store consumes this through <c>CanRememberInitialization</c> so that a schema-ensure which
        /// can still be rolled back does not leave the store believing it is initialised.
        /// </remarks>
        public bool DdlSurvivesRollback => AmbientTransaction == null || !SupportsTransactionalDdl;

        public virtual void DoCommand(Action<DbCommand> createCommand, Action<DbCommand> executeCommand, bool isLock = false)
        {
            var ambient = AmbientTransaction;
            if (ambient != null)
            {
                RunCommandOn(ambient.Connection, ambient.Transaction, createCommand, executeCommand);
                return;
            }
            if (!isLock)
            {
                RunCommand(createCommand, executeCommand);
            }
            else
            {
                lock (_lock)
                {
                    RunCommand(createCommand, executeCommand);
                }
            }
        }

        // TASK-259 deleted the ExternalConnection/ExternalTransaction pair and its SetExternalTransaction
        // setter that used to live here. They were this framework's first answer to "run the connector's own
        // SQL inside a transaction the caller owns", and the answer was stored in the wrong place: connectors
        // are cached process-wide per (type, settings id) by DataBase.GetConnector, so a per-caller,
        // per-operation fact became shared, long-lived state. Concurrent callers saw each other's
        // transaction, and a caller that finished without clearing it left a disposed connection behind for
        // everyone — measured on SQLite as a store whose lazy schema-ensure threw and which then stayed
        // permanently uninitialised.
        //
        // AmbientSqlTransaction (TASK-240) is the replacement and the only mechanism now: it lives in an
        // AsyncLocal cell, is keyed by settings id, nests as a stack and restores on dispose, so a boundary
        // cannot outlive the flow that entered it. Both stores moved to it then; SqlSchemaBuilder was the
        // last holdout and moved in TASK-259, which left this pair with zero callers.
        //
        // Deleted rather than left in place (TASK-247's rule: a mechanism nobody can reach is not a safety
        // net, it is a second implementation that drifts) after measuring 0 uses across all 16 consumer
        // repos. Do not reintroduce it: putting per-operation state on a cached connector is the defect, not
        // the spelling.

        public virtual void DoCommandWithTransaction(Action<DbCommand> createCommand, Action<DbCommand> executeCommand, bool isLock = false)
        {
            // Inside a boundary this must NOT open a nested transaction and must NOT commit or roll
            // back — the owner commits. A committed inner transaction inside an outer one that later
            // rolls back is partial application reporting green.
            var ambient = AmbientTransaction;
            if (ambient != null)
            {
                RunCommandOn(ambient.Connection, ambient.Transaction, createCommand, executeCommand);
                return;
            }
            if (!isLock)
            {
                RunCommandTransaction(createCommand, executeCommand);
            }
            else
            {
                lock (_lock)
                {
                    RunCommandTransaction(createCommand, executeCommand);
                }
            }
        }

        /// <summary>
        /// Runs one DDL statement — the funnel every schema emitter goes through, and the only place that
        /// consults <see cref="AbstractConnectorBase.SupportsTransactionalDdl"/>.
        /// </summary>
        /// <remarks>
        /// Identical to <see cref="DoCommandWithTransaction"/> on a provider with transactional DDL. Where
        /// DDL is <b>not</b> transactional the statement is issued with the ambient boundary suppressed, so
        /// it runs on a connection of its own and the caller's transaction is left intact.
        /// <para>
        /// <b>Why this is a funnel and not a fix at the store.</b> The defect (TASK-243) was found through
        /// lazy schema-ensure, but nothing about it is specific to schema-ensure: on MySQL <i>any</i> DDL
        /// reaching the boundary's connection implicitly commits it, so <c>CreateTable</c>, the index
        /// emitters, <c>DropTable</c>, the two <c>ALTER</c>s and the view DDL are all the same defect
        /// wearing different statements. Guarding the emitters instead of the caller means a new schema
        /// emitter is correct without being told.
        /// </para>
        /// <para>
        /// There is nothing else to suppress. The legacy ExternalConnection/ExternalTransaction pair that
        /// this paragraph used to carve out was deleted in TASK-259 once <c>SqlSchemaBuilder</c> — its last
        /// caller — moved onto <see cref="AmbientSqlTransaction"/>, so a migration's DDL is now an ambient
        /// boundary like any other and is suppressed here on exactly the same terms.
        /// </para>
        /// </remarks>
        /// <param name="createCommand">Builds the statement.</param>
        /// <param name="executeCommand">Executes it.</param>
        /// <param name="isLock">Passed through to the serialization gate, unchanged.</param>
        /// <param name="inOwnTransaction">
        /// Whether the statement runs in a transaction of its own (<see cref="DoCommandWithTransaction"/>)
        /// or autocommitted (<see cref="DoCommand"/>). Each emitter passes what it already did — the base
        /// <c>CreateTable</c> wraps, the provider overrides of it do not — because this change is about
        /// <i>which connection</i> DDL runs on, not about giving it atomicity it never had. On a provider
        /// with non-transactional DDL the wrapper transaction is a fiction anyway: the statement commits it
        /// on the way in.
        /// </param>
        protected void DoDdlCommand(Action<DbCommand> createCommand, Action<DbCommand> executeCommand, bool isLock = false, bool inOwnTransaction = true)
        {
            if (SupportsTransactionalDdl)
            {
                RunDdl(createCommand, executeCommand, isLock, inOwnTransaction);
                return;
            }
            using var _suppressed = AmbientSqlTransaction.Suppress();
            RunDdl(createCommand, executeCommand, isLock, inOwnTransaction);
        }

        private void RunDdl(Action<DbCommand> createCommand, Action<DbCommand> executeCommand, bool isLock, bool inOwnTransaction)
        {
            if (inOwnTransaction)
            {
                DoCommandWithTransaction(createCommand, executeCommand, isLock);
                return;
            }
            DoCommand(createCommand, executeCommand, isLock);
        }

        /// <summary>
        /// Runs a multi-statement bulk write on the ambient boundary when this flow is inside one, and on its
        /// own connection and transaction when it is not. Synchronous twin of
        /// <c>AbstractAsyncConnector.RunBulkAsync</c>.
        /// </summary>
        /// <param name="label">Command text used for retry logging and for <see cref="InitException"/>.</param>
        /// <param name="body">
        /// Receives the connection, the transaction, and whether it <b>owns</b> them. When it does not own
        /// them it must not commit, roll back, or dispose either one — the boundary owner does that.
        /// </param>
        /// <param name="retryWhenOwned">
        /// Whether the own-connection path is wrapped in <see cref="AbstractConnectorBase.ExecuteWithRetry"/>.
        /// Each provider's shipped bulk path is preserved as it was: SQLite retries (CR-M144), PostgreSQL,
        /// MySQL and MSSql never did. The participating path never retries whatever this says.
        /// </param>
        /// <remarks>
        /// <b>Why the sync half matters just as much.</b> <c>DataBaseStore.EnterTransactionScope</c> publishes
        /// a sync store's transaction context into <see cref="AmbientSqlTransaction"/> exactly as the async
        /// store does, so sync single-row writes already honoured a boundary while sync bulk writes opened a
        /// second connection and escaped it — the same asymmetry, on the same store.
        /// <para>
        /// <b>The participating path is deliberately NOT wrapped in <c>ExecuteWithRetry</c>.</b> A retry would
        /// re-run statements inside a transaction whose earlier statements already succeeded, and on most
        /// providers the first failure has already aborted it, so the retry can only fail differently.
        /// Retrying is the boundary owner's decision — the same reasoning <see cref="RunCommandOn"/> already
        /// applies to single commands.
        /// </para>
        /// <para>
        /// There is now exactly <b>one</b> door into "participate in somebody else's transaction", so the
        /// two-doors-must-agree paragraph that stood here is moot: TASK-259 deleted the legacy
        /// ExternalConnection/ExternalTransaction pair after its last caller moved to the ambient.
        /// </para>
        /// </remarks>
        protected void RunBulk(
            string label,
            Action<DbConnection, DbTransaction, bool> body,
            bool retryWhenOwned = true)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            // `transaction!` is provably non-null here: ownTransaction: true means the owned path begins one,
            // and every participating path hands over an existing (never-null) transaction.
            RunBulkCore(label, (connection, transaction, owned) => body(connection, transaction!, owned),
                        ownTransaction: true, retryWhenOwned);
        }

        /// <summary>
        /// The same decision as <see cref="RunBulk"/> for a bulk write that carries its <b>own</b> atomicity
        /// and therefore wants a connection but no transaction of its own — PostgreSQL's binary <c>COPY</c>
        /// and <c>SqlBulkCopy</c>. The body receives a null transaction when it owns the connection.
        /// </summary>
        protected void RunBulkOnConnection(
            string label,
            Action<DbConnection, DbTransaction?, bool> body,
            bool retryWhenOwned = true)
            => RunBulkCore(label, body, ownTransaction: false, retryWhenOwned);

        private void RunBulkCore(
            string label,
            Action<DbConnection, DbTransaction?, bool> body,
            bool ownTransaction,
            bool retryWhenOwned)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            var ambient = AmbientTransaction;
            if (ambient != null)
            {
                body(ambient.Connection, ambient.Transaction, false);
                return;
            }

            void Owned()
            {
                using var connection = CreateConnection(_settings);
                connection.Open();
                if (!ownTransaction)
                {
                    body(connection, null, true);
                    return;
                }
                using var transaction = connection.BeginTransaction();
                body(connection, transaction, true);
            }

            if (retryWhenOwned)
            {
                ExecuteWithRetry(Owned, label);
                return;
            }
            Owned();
        }

        private IEnumerable<IEnumerable<object>> RunReaderCommandOn(DbConnection connection, DbTransaction transaction, Action<DbCommand> createCommand, Func<DbDataReader, IEnumerable<object>> transformFunction)
        {
            string? commandText = null;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                bool faulted = false;
                try
                {
                    createCommand?.Invoke(command);
                    commandText = DataBase.GetGeneratedQuery(command);
                    OnExecute?.Invoke(command.CommandText);
                }
                catch (Exception ex) { InitException(ex, commandText); faulted = true; } // CR-M134
                if (faulted) yield break;
                DbDataReader reader;
                try { reader = command.ExecuteReader(); }
                catch (Exception ex) when (IsMissingTableException(ex)) { yield break; }
                using var _r = reader;
                if (!(reader?.HasRows ?? false)) yield break;
                bool isNext = false;
                try { isNext = reader.Read(); }
                catch (Exception ex) { InitException(ex, commandText); yield break; }
                while (isNext)
                {
                    IEnumerable<object>? row = null;
                    try { row = transformFunction.Invoke(reader); }
                    catch (Exception ex) { InitException(ex, commandText); }
                    if (row == null) yield break;
                    yield return row;
                    try { isNext = reader.Read(); }
                    catch (Exception ex) { InitException(ex, commandText); isNext = false; }
                }
            }
        }

        /// <summary>
        /// Runs one command on a connection and transaction owned by somebody else.
        /// </summary>
        /// <remarks>
        /// Opens nothing, commits nothing, and disposes nothing but the command: the caller's connection
        /// must outlive the operation, and disposing it mid-transaction is the failure this pattern
        /// invites. Shared by the ambient boundary and the legacy external-transaction pair so the two
        /// doors cannot disagree about what participating in a transaction means.
        /// </remarks>
        private void RunCommandOn(DbConnection connection, DbTransaction transaction, Action<DbCommand> createCommand, Action<DbCommand> executeCommand)
        {
            string? commandText = null;
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    createCommand?.Invoke(command);
                    commandText = DataBase.GetGeneratedQuery(command);
                    OnExecute?.Invoke(commandText);
                    executeCommand?.Invoke(command);
                }
            }
            catch (Exception ex)
            {
                InitException(ex, commandText);
            }
        }

        private void RunCommandTransaction(Action<DbCommand> createCommand, Action<DbCommand> executeCommand)
        {
            ExecuteWithRetry(() =>
            {
                using var db = CreateConnection(_settings);
                db.Open();
                using var transaction = db.BeginTransaction();
                string? commandText = null;
                try
                {
                    using (var command = db.CreateCommand())
                    {
                        command.Transaction = transaction;
                        createCommand?.Invoke(command);
                        commandText = DataBase.GetGeneratedQuery(command);
                        OnExecute?.Invoke(commandText);
                        executeCommand?.Invoke(command);
                    }
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    InitException(ex, commandText);
                }
                finally
                {
                    db.Close();
                }
            });
        }

        private void RunCommand(Action<DbCommand> createCommand, Action<DbCommand> executeCommand)
        {
            ExecuteWithRetry(() =>
            {
                using var db = CreateConnection(_settings);
                db.Open();
                string? commandText = null;
                try
                {
                    using (var command = db.CreateCommand())
                    {
                        createCommand?.Invoke(command);
                        commandText = DataBase.GetGeneratedQuery(command);
                        OnExecute?.Invoke(commandText);
                        executeCommand?.Invoke(command);
                    }
                }
                catch (Exception ex)
                {
                    InitException(ex, commandText);
                }
                finally
                {
                    db.Close();
                }
            });
        }

        private IEnumerable<IEnumerable<object>> RunReaderCommand(Action<DbCommand> createCommand, Func<DbDataReader, IEnumerable<object>> transformFunction)
        {
            if (transformFunction == null)
            {
                throw new ArgumentNullException(nameof(transformFunction));
            }

            // A read inside a boundary must run on the boundary's connection, or it cannot see the
            // boundary's own uncommitted writes — read-then-write service logic would get a stale
            // snapshot, which is a wrong answer rather than a missing feature.
            var ambient = AmbientTransaction;
            if (ambient != null)
            {
                foreach (var item in RunReaderCommandOn(ambient.Connection, ambient.Transaction, createCommand, transformFunction))
                    yield return item;
                yield break;
            }

            using var db = CreateConnection(_settings);
            db.Open();
            string? commandText = null;
            using (var command = db.CreateCommand())
            {
                bool faulted = false;
                try
                {
                    createCommand?.Invoke(command);
                    commandText = DataBase.GetGeneratedQuery(command);
                    OnExecute?.Invoke(command.CommandText);
                }
                catch (Exception ex)
                {
                    // CR-M134: when an OnException handler is registered InitException returns instead of
                    // rethrowing — do NOT fall through to ExecuteReader on a command that failed to build,
                    // which would raise a second, more confusing error (or run a malformed command).
                    InitException(ex, commandText);
                    faulted = true;
                }
                if (faulted)
                {
                    yield break;
                }
                DbDataReader reader2;
                try { reader2 = command.ExecuteReader(); }
                catch (Exception ex) when (IsMissingTableException(ex)) { yield break; }
                using var _r2 = reader2;
                if (!(reader2?.HasRows ?? false))
                {
                    yield break;
                }
                bool isNext = false;
                try
                {
                    isNext = reader2.Read();
                }
                catch (Exception ex)
                {
                    InitException(ex, commandText);
                    yield break;
                }
                while (isNext)
                {
                    IEnumerable<object>? row = null;
                    try
                    {
                        row = transformFunction.Invoke(reader2);
                    }
                    catch (Exception ex)
                    {
                        InitException(ex, commandText);
                    }
                    if (row == null)
                    {
                        yield break;
                    }
                    yield return row;
                    try
                    {
                        isNext = reader2.Read();
                    }
                    catch (Exception ex)
                    {
                        InitException(ex, commandText);
                        isNext = false;
                    }
                }
            }
        }
    }
}
