using System;
using System.Collections.Generic;
using System.Linq;

namespace Birko.Data.SQL.Connectors
{
    /// <summary>
    /// The bookkeeping behind schema-ensure's degrade-and-report channel: <b>current state, keyed by
    /// identity</b>, with the event raised on the transition into failure and the record cleared when the
    /// condition is repaired.
    /// </summary>
    /// <remarks>
    /// <b>Extracted at TASK-254 so there is one implementation of the parts that drift, not two.</b>
    /// TASK-204 introduced this behaviour for unbuildable indexes and got it wrong the first time — an
    /// append-only list, on a process-lifetime object, growing by one entry per HTTP request for as long as
    /// the index stayed unbuildable. Connectors are cached process-wide per (connector type, settings id)
    /// in <c>DataBase.GetConnector</c> while the <c>_initialized</c> flag that gates schema-ensure lives on
    /// the STORE, so a web app resolving a scoped store per request re-runs schema-ensure per request
    /// against one shared connector.
    /// <para>
    /// Every subtle property here exists because of that: <b>keyed rather than listed</b> (one entry per
    /// identity no matter how many attempts), <b>transition-fired</b> (an event per attempt would fire on
    /// every request), <b>clearable</b> (a report that cannot un-report is one an operator learns to
    /// ignore), <b>locked</b> (the connector is shared across request threads) and <b>stably ordered</b> (a
    /// host's startup report should not be dictionary-dependent).
    /// </para>
    /// <para>
    /// <b>Why a helper rather than generalising <c>IndexCreationFailure</c> itself.</b> That type is public
    /// surface a consumer depends on — measured at TASK-254: Symbio names it in production code, in two test
    /// files, and as a documented contract in its own CLAUDE.md and specs, including the "not an inventory"
    /// property. Reshaping it is consumer-visible; extracting the mechanism underneath it is not.
    /// </para>
    /// <para>
    /// <b>Deliberately used by exactly two callers and no more are expected</b> — the index channel on
    /// <c>AbstractConnector</c> and the hypertable channel on <c>TimescaleDBConnector</c>. Compression and
    /// retention policies are <i>not</i> schema-ensure steps (they are migration-path only, and an explicit
    /// call should throw), so they are not future callers. This is not built for reuse; it is built so the
    /// logic above has one home. <b>If it acquires configuration or a type hierarchy, that is the signal it
    /// should have stayed two copies.</b>
    /// </para>
    /// <para>
    /// The re-attempt itself is NOT suppressed — schema-ensure retries on the next run, which is what lets a
    /// condition repair itself once an operator fixes the offending rows, with no restart. Only the
    /// bookkeeping is deduplicated.
    /// </para>
    /// </remarks>
    /// <typeparam name="TFailure">The public record type this log holds.</typeparam>
    internal sealed class SchemaEnsureFailureLog<TFailure>
        where TFailure : class
    {
        private readonly Dictionary<string, TFailure> _failures = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private readonly Func<TFailure, string> _orderBy;

        /// <param name="orderBy">
        /// Sort key for <see cref="Snapshot"/>, so a host's report is stable rather than
        /// dictionary-dependent.
        /// </param>
        public SchemaEnsureFailureLog(Func<TFailure, string> orderBy)
            => _orderBy = orderBy ?? throw new ArgumentNullException(nameof(orderBy));

        /// <summary>
        /// Current state, ordered. An entry that is later cleared drops out; a given identity appears at
        /// most once however many attempts have run.
        /// </summary>
        public IReadOnlyList<TFailure> Snapshot
        {
            get
            {
                lock (_lock)
                {
                    return _failures.Values
                        .OrderBy(_orderBy, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
            }
        }

        /// <summary>
        /// Records <paramref name="failure"/> under <paramref name="key"/> and returns <see langword="true"/>
        /// only if this is the TRANSITION into the failed state — the caller raises its event on that.
        /// </summary>
        /// <remarks>
        /// Always overwrites: the latest error is the one describing the current state. Returning the
        /// transition rather than raising the event here keeps the event's identity (and its type) with the
        /// caller, which is what lets the public surfaces stay byte-identical.
        /// </remarks>
        public bool Record(string key, TFailure failure)
        {
            lock (_lock)
            {
                var isNew = !_failures.ContainsKey(key);
                _failures[key] = failure;
                return isNew;
            }
        }

        /// <summary>
        /// Drops any record for <paramref name="key"/>, so the channel cannot report a condition an operator
        /// has already repaired.
        /// </summary>
        public void Clear(string key)
        {
            lock (_lock)
            {
                _failures.Remove(key);
            }
        }
    }
}
