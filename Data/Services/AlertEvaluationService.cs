/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// How a keyed collection inside <see cref="AlertEvaluationService"/> is SCOPED - what tuple
    /// its keys identify. Declared ON THE FIELD so the invariant sits where the next person to add
    /// a collection is already typing, not in a document they will never open.
    /// </summary>
    internal enum AlertKeyScopeKind
    {
        /// <summary>Keyed by alert id alone. Correct ONLY for state that genuinely has no server
        /// dimension - the due-check clock, a once-per-process log flag. NEVER correct for anything
        /// that guards, times or judges a per-(alert, server) state.</summary>
        PerAlert,

        /// <summary>Keyed by <see cref="AlertEvaluationService.StateKey"/>, i.e. by
        /// (alert, server). The scope of <c>_activeStates</c>, and therefore the required scope of
        /// everything that guards it.</summary>
        PerAlertServer,
    }

    /// <summary>
    /// Declares one collection's key scope. EVERY <c>ConcurrentDictionary&lt;string, ...&gt;</c>
    /// field on <see cref="AlertEvaluationService"/> must carry this attribute:
    /// <c>AlertStampKeyCensusTests</c> enumerates those fields by REFLECTION and fails naming any
    /// that arrives unclassified, so a sixth collection cannot be added silently.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class AlertKeyScopeAttribute : Attribute
    {
        public AlertKeyScopeAttribute(AlertKeyScopeKind kind) => Kind = kind;
        public AlertKeyScopeKind Kind { get; }
    }

    /// <summary>
    /// Declares that a method deliberately touches BOTH a per-(alert, server) collection and a
    /// per-alert one, and why. The census treats an UNDECLARED mix as a finding, because that
    /// combination is the exact shape of the defect this lane fixed: a method holding a per-server
    /// state in one hand and a per-alert clock in the other, and judging the first by the second.
    /// Declaring a mix does not make it safe - it makes it REVIEWED.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class AlertKeyScopeMixAttribute : Attribute
    {
        public AlertKeyScopeMixAttribute(string reason) => Reason = reason;
        public string Reason { get; }
    }

    /// <summary>
    /// Timer-based engine that periodically evaluates alert definitions against all configured servers.
    /// Runs each alert's T-SQL query, compares the result to thresholds, manages the state machine
    /// (Active → Acknowledged → Resolved), and dispatches notifications via AlertingService.
    /// </summary>
    // BM:AlertEvaluationService.Class — timer-based alert engine
    public class AlertEvaluationService : IDisposable
    {
        private readonly ILogger<AlertEvaluationService> _logger;
        private readonly AlertDefinitionService _definitions;
        private readonly AlertHistoryService _history;
        private readonly AlertingService _alerting;
        private readonly ServerConnectionManager _connections;
        private readonly ToastService _toast;
        private readonly NotificationChannelService _channels;
        private readonly liveQueriesCacheStore _cache;
        private readonly AlertBaselineService? _baseline;
        private readonly ConnectionHealthService? _health;
        private readonly IQueryOrchestrator _orchestrator;
        private readonly SqlConnectionPoolService? _pool;
        private readonly ServerCircuitBreakerService? _breaker;

        // eval-failure-visible (ruling 2026-09-06): an evaluation failure is a durable, audited fact,
        // not a fire-and-forget in-memory flag. This optional audit sink records ONE entry when a
        // failure episode opens and ONE when it recovers (never per tick); null on a build/host that
        // registers no AuditLogService. See RecordEvaluationFailure / ClearEvaluationFailure.
        private readonly AuditLogService? _audit;

        // =======================================================================================
        // THE KEYING INVARIANT (alert-stamp-key-per-server, 2026-09-15)
        //
        // EVERY READ AND EVERY WRITE OF A FRESHNESS STAMP MUST BE KEYED BY THE SAME TUPLE AS THE
        // STATE IT GUARDS. _activeStates is per (alert, server), so anything that guards, times or
        // judges one of its entries is per (alert, server) too.
        //
        // This is not a style note - it shipped broken. One dictionary, _lastEvaluation, was keyed
        // by alert id ALONE, and ResolveCleared read it to decide whether a PER-SERVER state had
        // recently been measured. A clean run of the same alert on one server therefore refreshed
        // the freshness used to judge every OTHER server, and silently reaped alerts still breaching
        // on a server nothing had looked at. Of at least 16 silent reaps seen live, 14 were on the
        // single server whose circuit breaker had opened.
        //
        // Every ConcurrentDictionary<string, ...> field below MUST carry [AlertKeyScope].
        // AlertStampKeyCensusTests enumerates these fields BY REFLECTION - never from a hand-typed
        // list, because a hand-typed list is what failed here twice - and goes red on any field that
        // arrives unclassified, and on any method that holds a per-server state and a per-alert
        // clock at once without declaring the mix.
        //
        // AND EVERY PerAlertServer COLLECTION MUST BE PRUNED WHEN ITS KEY LEAVES THE ENABLED SET
        // (fix round 2, owner's ruling 2026-09-17). PruneUnmonitoredAlertKeys removes each key that is
        // no longer StateKey(enabled alert, enabled server). It names the collections one by one, so a
        // NEW PerAlertServer collection is not pruned until someone adds it there. That is guarded by
        // behaviour, not by this comment:
        // AlertStampKeyPerServerTests.I2_aServerRemovedFromMonitoringLeavesNoKeyInAnyPerServerCollection
        // enumerates the collections through AlertStampKeyCensusTests.KeyedCollections(), requires its
        // drive to have populated every one of them, and goes red on any that still holds a key for
        // the removed server.
        // =======================================================================================

        // In-memory state: key = StateKey(alertId, serverName).
        [AlertKeyScope(AlertKeyScopeKind.PerAlertServer)]
        private readonly ConcurrentDictionary<string, AlertState> _activeStates = new(StringComparer.OrdinalIgnoreCase);

        // THE SCHEDULER CLOCK - per ALERT, and deliberately so: "when was this alert last
        // DUE-CHECKED". The due decision is taken once per alert, in catalogue order, BEFORE the
        // alert fans out across its servers; serverName is not in scope there and must not be, or a
        // 60 s alert would become due once PER SERVER per cycle.
        //
        // Renamed from _lastEvaluation on 2026-09-15. The old name read as "when was this alert
        // last evaluated", which is precisely what invited a reader to use it as a per-server
        // measurement guard. It is not one and never was. It answers only "is it time to ask
        // again?". "Did we actually get an answer from THIS server" is _lastServerEvaluation's
        // question, below, and the two must never be merged again.
        [AlertKeyScope(AlertKeyScopeKind.PerAlert)]
        private readonly ConcurrentDictionary<string, DateTime> _lastDueCheck = new(StringComparer.OrdinalIgnoreCase);

        // THE MEASUREMENT CLOCK - per (alert, server): "when did this alert last get an ANSWER from
        // this server". Written ONLY by RecordServerEvaluation, and only on a route on which the
        // server answered: a standard query that COMPLETED without throwing, whether it produced a
        // value or a NULL (owner's ruling 2026-09-17, "a completed query counts"), or a special
        // handler that produced a value. Never from a query that threw, timed out or was cancelled,
        // a refused socket on the standard path, an open breaker, the RequiresOnPrem skip, or an
        // attempt that never ran. This is the only stamp ResolveCleared is entitled to read, because
        // it is the only one keyed like the state ResolveCleared removes.
        [AlertKeyScope(AlertKeyScopeKind.PerAlertServer)]
        private readonly ConcurrentDictionary<string, DateTime> _lastServerEvaluation = new(StringComparer.OrdinalIgnoreCase);

        // Alert ids whose log-scan frequency clamp has already been warned about in this process.
        // Its own collection since 2026-09-15: this sentinel used to live INSIDE _lastEvaluation
        // under a "__logwarn__{id}" key, sharing one dictionary and one KEYSPACE with the freshness
        // stamps. Two key grammars in one dictionary is how the defect above was born, and it makes
        // any re-key of the stamps either orphan the sentinel - so the once-per-run warning fires
        // every cycle forever - or leave the two grammars side by side. A flag is not a clock: it
        // gets its own collection and its own value type, so the two cannot be confused again.
        [AlertKeyScope(AlertKeyScopeKind.PerAlert)]
        private readonly ConcurrentDictionary<string, byte> _frequencyClampWarned = new(StringComparer.OrdinalIgnoreCase);

        // Tracks last notification time per alert+server for cooldown
        [AlertKeyScope(AlertKeyScopeKind.PerAlertServer)]
        private readonly ConcurrentDictionary<string, DateTime> _lastNotified = new(StringComparer.OrdinalIgnoreCase);

        // Tracks timestamps of each hit within the escalation window, keyed by "alertId:serverName"
        [AlertKeyScope(AlertKeyScopeKind.PerAlertServer)]
        private readonly ConcurrentDictionary<string, Queue<DateTime>> _hitTimes = new(StringComparer.OrdinalIgnoreCase);

        // special-alerts-escalation-parity (2026-09-11): the escalations this process has already
        // sent, keyed by "alertId:serverName", with the instant each was sent. This OUTLIVES the
        // AlertState, and that is the entire point - see ApplyEscalationForFiringCycle. An entry is
        // removed by EndEscalationEpisode when the alert was MEASURED and found not to be breaching,
        // and - since fix round 2 of alert-stamp-key-per-server, 2026-09-17 - by
        // PruneUnmonitoredAlertKeys when its alert or server has left the enabled set, because
        // nothing will ever measure that key again. It is deliberately NOT removed by ResolveCleared,
        // which reaps an alert that stopped being measured while still breaching. Bounded by alerts x
        // servers, and only ever written when an alert actually escalates.
        //
        // KNOWN, BOUNDED LEAK (recorded by the fix round, 2026-09-11, NARROWED 2026-09-17). One
        // entry survives for the process lifetime when an alert escalates, is then reaped by
        // ResolveCleared for want of measurement, and is NEVER MEASURED AGAIN while its alert and
        // server both stay enabled - a handler that stops returning a value forever. No measured-clear
        // cycle ever runs, so nothing calls EndEscalationEpisode. The two other routes this note used
        // to name - the definition deleted, the server decommissioned - are no longer leaks: both take
        // the key out of the enabled set, and the prune removes it. The ceiling is one DateTime per
        // (alert x server) that has escalated at least once - the same ceiling _activeStates and
        // _lastNotified already carry - so it is a bounded retention, not an unbounded growth. It is
        // left alone on purpose: the only safe pruner for a key that is still monitored is one driven
        // by a MEASUREMENT, and by definition this route has none. A time-based sweep would be exactly
        // the bug this fix round removed, wearing a clock.
        [AlertKeyScope(AlertKeyScopeKind.PerAlertServer)]
        private readonly ConcurrentDictionary<string, DateTime> _escalatedEpisodes = new(StringComparer.OrdinalIgnoreCase);

        // lane Q14 fix round 2 (2026-09-18): the unbroken run of BREACHING measurements behind
        // AlertDefinition.HoldSeconds, keyed StateKey(alert, server). Written by ObserveBreachForHold on
        // every breaching measurement of every alert, a hold of 0 included, so the run is always current;
        // only an alert with a hold consults it. Removed by EndBreachRun when a measurement does NOT
        // breach, marked broken (never removed) by BreakBreachRun when an attempt produces no value, and
        // removed by PruneUnmonitoredAlertKeys when the key leaves the enabled set.
        [AlertKeyScope(AlertKeyScopeKind.PerAlertServer)]
        private readonly ConcurrentDictionary<string, BreachRun> _breachRuns = new(StringComparer.OrdinalIgnoreCase);

        // #68 LEG 2: an alert whose query THREW on its last attempt, keyed by "alertId:serverName".
        // Cleared the instant that same query succeeds again. Drives AlertsNoc's Unknown/degraded
        // card state so a server whose alert evaluation is silently erroring every cycle reads as
        // "we don't know", never as a clean, healthy Ok.
        [AlertKeyScope(AlertKeyScopeKind.PerAlertServer)]
        private readonly ConcurrentDictionary<string, AlertEvalFailure> _evalFailures = new(StringComparer.OrdinalIgnoreCase);

        // eval-failure-visible: _evalFailures is durable now. The dictionary above is the live copy;
        // this JSON file (default Config/eval-failures.json beside the exe) is its on-disk mirror, so
        // the "N alerts cannot be evaluated" panel and the "failing since" timestamp survive a
        // restart instead of resetting to a false all-clear on every process start. The path is
        // injectable so a test can point it at a throwaway directory; every read/write holds _storeLock.
        private readonly string _evalFailureStorePath;
        private readonly object _storeLock = new();

        // eval-failures-store-replace-fails (2026-09-10). Three pieces of persist state, all read and
        // written under _storeLock.
        //
        // _storeDirty  — a CHANGE THAT IS NOT YET ON DISK. Set by the deferred path (a repeat failure
        //                on an episode the store already carries) AND by a write that SURRENDERED, so
        //                the next flush retries it instead of dropping it; cleared only by a physical
        //                write that actually landed.
        // _persistEpisodeOpen — whether the store is currently UNWRITABLE, so the log can obey the same
        //                cardinality the failure log already obeys: one Warning naming the cause when the
        //                episode opens, Debug for every repeat, one Information line when it recovers.
        //                Without it a store that cannot be written logs a stack trace on every attempt —
        //                119 of them on 2026-09-09 — which is the same unreadability defect as a silent
        //                failure, in the opposite direction.
        // _storeWriteCount — successful PHYSICAL writes. Internal so a test can MEASURE the coalescing
        //                claim ("seven failing pairs in one cycle write the store once, not seven times")
        //                instead of asserting it by reading the code.
        private bool _storeDirty;
        private bool _persistEpisodeOpen;
        private long _storeWriteCount;

        /// <summary>Successful physical writes of the evaluation-failure store since construction.
        /// Internal for EvalFailureStorePersistTests; nothing in the product reads it.</summary>
        internal long StoreWriteCount { get { lock (_storeLock) return _storeWriteCount; } }

        /// <summary>Test seam: invoked with the 1-based attempt number immediately BEFORE each
        /// write+replace attempt, inside <c>_storeLock</c>. It exists so a test can make attempt 1 fail
        /// against a REAL held file handle and attempt 2 succeed, deterministically — a retry pin that
        /// slept and hoped would be exactly the flaky test this codebase keeps paying for. Null in
        /// production; the product never sets it.</summary>
        internal Action<int>? StoreWriteAttemptHookForTests;

        /// <summary>
        /// How many times a single persist tries the write+replace before it surrenders, and how long it
        /// waits between tries.
        ///
        /// <para><b>Why a retry at all (2026-09-10).</b> The live service logged 119 Win32 1175
        /// (ERROR_UNABLE_TO_REMOVE_REPLACED) failures on 2026-09-09, in ADJACENT PAIRS 17–107 ms apart
        /// with ~300 s between clusters: a short-lived contention on the destination, not a broken path.
        /// MEASURED on this box: no static holder of any share mode produces 1175 (a plain reader
        /// produces ERROR_SHARING_VIOLATION 32, a full-sharing scanner produces nothing at all), while
        /// CONCURRENT destination-mutating operations do. Either way the contention clears in
        /// milliseconds, so the budget below — 20 + 60 + 180 ms across four attempts — covers the whole
        /// observed cluster width with room to spare while staying far inside one 30 s tick.</para>
        /// </summary>
        internal const int StoreWriteAttempts = 4;
        private static readonly int[] StoreWriteBackoffMs = { 20, 60, 180 };

        private bool _isRunning;
        private bool _dryRun;
        private readonly int _baseTickSeconds;

        /// <summary>
        /// How many alerts may be in flight at once inside one evaluation cycle. Each still fans out
        /// across every enabled server, so the real ceiling on concurrent queries is this times the
        /// server count, and QueryOrchestrator's global limit (45) sits underneath it.
        ///
        /// <para><b>Why this exists (2026-08-28).</b> The cycle was serial ACROSS alerts:
        /// <c>foreach (var alert in alerts) { ...; await Task.WhenAll(tasks); }</c>, parallel only
        /// across the servers of ONE alert. Three re-based alerts - io_stall_time, disk_latency_read,
        /// disk_latency_write - each embed <c>WAITFOR DELAY '00:00:05'</c> to sample
        /// sys.dm_io_virtual_file_stats twice, and each ships frequencySeconds 60. MEASURED on
        /// .\NEW2022: 5181 ms, 5194 ms and 5218 ms individually, 15,593 ms run back to back. Against
        /// the default 30-second tick that is HALF the budget spent waiting before any other alert
        /// runs, and if the remainder takes another 15 s the loop logs "Evaluation tick overran
        /// interval; next tick dropped" and every 30-second alert - deadlock, cluster_failover -
        /// misses its cadence. Four is chosen so the three windowed alerts overlap with room to
        /// spare, and so the added concurrency stays well inside the orchestrator's own limit.</para>
        /// </summary>
        internal const int DefaultMaxConcurrentAlerts = 4;

        private readonly int _maxConcurrentAlerts;
        private readonly SemaphoreSlim _evaluationLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private Task? _loopTask;

        public event Action? OnAlertsChanged;

        /// <summary>
        /// When true, alerts fire and appear in-memory but no notifications are dispatched and nothing is persisted.
        /// Useful for testing alert rules without spamming notifications.
        /// </summary>
        public bool DryRun
        {
            get => _dryRun;
            set
            {
                _dryRun = value;
                _logger.LogInformation("Alert dry-run mode: {Mode}", value ? "ON" : "OFF");
            }
        }

        /// <summary>
        /// All currently active/acknowledged alert states (for UI binding).
        /// </summary>
        public IReadOnlyCollection<AlertState> ActiveAlerts =>
            _activeStates.Values.Where(s => s.Status != AlertStatus.Resolved).OrderByDescending(s => s.LastTriggered).ToList();

        public int ActiveCount => _activeStates.Values.Count(s => s.Status == AlertStatus.Active);

        /// <summary>True if the given server has at least one alert whose query threw on its most
        /// recent evaluation attempt and hasn't since succeeded — i.e. we genuinely don't know that
        /// server's alert state. #68 LEG 2.</summary>
        public bool HasEvaluationFailure(string serverName) =>
            _evalFailures.Values.Any(f => f.ServerName.Equals(serverName, StringComparison.OrdinalIgnoreCase));

        /// <summary>Snapshot of every alert currently failing to evaluate. Drives the AlertsNoc Unknown
        /// tile and the eval-failure-visible "N alerts cannot be evaluated" panel on the Alerts and
        /// landing pages. Durable: reloaded from disk on startup, so it does not reset on a restart.</summary>
        public IReadOnlyCollection<AlertEvalFailure> EvaluationFailures => _evalFailures.Values.ToList();

        public bool IsRunning => _isRunning;

        /// <summary>The Details["Category"] value on every evaluation-failure audit entry, on both the
        /// episode-open and the recovery record, so an auditor (or a test) can select exactly these out
        /// of the shared SecurityEvent stream. eval-failure-visible.</summary>
        internal const string EvalFailureAuditCategory = "AlertEvaluationFailure";

        /// <summary>
        /// Records — or refreshes — an evaluation-failure EPISODE for one alert+server, and is the ONLY
        /// place an evaluation failure is written. Episode semantics (ruling 2026-09-06):
        /// <list type="bullet">
        /// <item>the FIRST failing tick for a key OPENS the episode — first-seen is stamped, the record
        /// is persisted, and exactly ONE audit entry (Warning, Phase=Opened) is written;</item>
        /// <item>every later failing tick only refreshes last-seen/reason and increments the count — it
        /// re-persists but does NOT audit, so a server unreachable for an hour produces one audit entry,
        /// not one per 30-second tick.</item>
        /// </list>
        /// This method NEVER calls <see cref="DispatchNotification"/> or <see cref="DispatchEscalation"/>:
        /// a monitoring gap is made visible and audited, never paged. That guarantee is pinned by
        /// <c>EvalFailureVisibleTests</c> — adding a dispatch call to this body (or to
        /// <see cref="ClearEvaluationFailure"/>) turns that test red.
        ///
        /// <para><b>Returns TRUE exactly when this call OPENED the episode</b> (logon-failure-unmeasurable,
        /// 2026-09-09). The episode notion already lived here, in the <c>TryAdd</c> above the audit — it is
        /// now surrendered to the caller so the failure LOG can obey the same cardinality as the audit
        /// (one Warning naming the cause per episode, then Debug per cycle) instead of inventing a second,
        /// drifting notion of "first time". Note the value is computed before the <c>_dryRun</c> test, so a
        /// dry run still logs once per episode even though it persists and audits nothing.</para>
        /// </summary>
        internal bool RecordEvaluationFailure(string stateKey, string alertId, string serverName, string reason)
        {
            // An attempt that produced no value: whatever was breaching is no longer known to be.
            BreakBreachRun(stateKey);

            var now = DateTime.UtcNow;
            var opened = _evalFailures.TryAdd(
                stateKey,
                new AlertEvalFailure(alertId, serverName, now, reason)
                {
                    FirstFailureUtc = now,
                    FailureCount = 1
                });

            if (!opened)
            {
                // Key already present = the episode is ongoing. Refresh last-seen/reason and bump the
                // count; leave FirstFailureUtc untouched so "failing since" and the single audit stand.
                _evalFailures.AddOrUpdate(
                    stateKey,
                    // Add branch is unreachable here (TryAdd already proved the key present) but the API
                    // demands it; build the same open-shape defensively rather than throw.
                    _ => new AlertEvalFailure(alertId, serverName, now, reason) { FirstFailureUtc = now, FailureCount = 1 },
                    (_, existing) => existing with
                    {
                        LastFailureUtc = now,
                        ErrorSummary = reason,
                        FailureCount = existing.FailureCount + 1
                    });
            }

            // DryRun's contract is "in-memory only — nothing persisted, nothing dispatched", so the
            // dictionary above still drives the live panel during a dry run, but the durable store and
            // the audit entry (both persisted side effects) are held back until the engine runs for real.
            if (!_dryRun)
            {
                // eval-failures-store-replace-fails (2026-09-10): write ONLY when the set of failures
                // changed. An opening episode is a new row the panel is about to show, so it goes to
                // disk now — the durability contract is unchanged. A repeat tick only refreshes count,
                // last-seen and reason on a row the store already carries, so it defers to the one
                // flush at the end of the cycle. With seven failing pairs that is one write per cycle
                // instead of seven, and seven times less exposure to whatever briefly holds the file.
                if (opened) PersistFailures();
                else MarkStoreDirty();

                if (opened)
                {
                    _audit?.Enqueue(
                        AuditEventType.SecurityEvent,
                        AuditSeverity.Warning,
                        $"Alert '{alertId}' can no longer be evaluated on server '{serverName}': {reason}",
                        new Dictionary<string, string>
                        {
                            ["Category"] = EvalFailureAuditCategory,
                            ["Phase"] = "Opened",
                            ["AlertId"] = alertId,
                            ["ServerName"] = serverName,
                            ["Reason"] = reason,
                            ["FirstFailureUtc"] = now.ToString("o"),
                        });
                }
            }

            return opened;
        }

        /// <summary>
        /// The one place an evaluation-failure reason is worded, so the durable record
        /// (<c>Config/eval-failures.json</c> → <see cref="AlertEvalFailure.ErrorSummary"/>), the audit
        /// entry, the operator panel and the Warning log line all carry the SAME text.
        ///
        /// <para><b>logon-failure-unmeasurable (2026-09-09).</b> The reason used to be a bare
        /// <c>ex.Message</c>, which drops the exception TYPE — and the type is often the whole diagnosis:
        /// a <c>SqlException</c> ("could not open a connection"), an <c>InvalidCastException</c>
        /// ("Specified cast is not valid") and a <c>TaskCanceledException</c> (a command timeout) are three
        /// different faults whose messages read alike, and one of them is not about the server at all.
        /// Prefixing the type costs nothing and is what tells an operator which of the three they have.</para>
        /// </summary>
        internal static string DescribeEvaluationFailure(Exception ex) =>
            $"{ex.GetType().Name}: {ex.Message}";

        /// <summary>
        /// What ONE special-alert handler produced this cycle: the measurement it made (or null for
        /// "could not measure"), and — new — the exception it caught getting there.
        ///
        /// <para><b>Why a pair and not a rethrow (logon-failure-unmeasurable, 2026-09-09).</b> Every
        /// special handler caught its own exception, logged it at Debug and returned null, so the
        /// exception died inside the handler and <c>EvaluateSpecialAlertAsync</c>'s <c>handlerError</c>
        /// was ALWAYS null — which is why the live service recorded 76 consecutive
        /// <c>logon_failure</c> failures whose entire stated reason was the caller's fallback string,
        /// "queryMode 'error_log_scan' produced no measurement this cycle". Rethrowing would have
        /// surrendered the exception but CHANGED A MEASUREMENT:
        /// <see cref="CheckConnectivityAsync"/> deliberately turns a refused connection into the value
        /// <c>1</c> ("unreachable"), the reading that makes <c>instance_unreachable</c> fire, so a
        /// rethrow there would convert a DOWN instance from a fired Critical into an unmeasurable
        /// Unknown. Returning the pair lets a handler hand up its exception while its
        /// <see cref="Value"/> stays byte-for-byte what it was: null stays null, 1 stays 1, 0 stays 0.</para>
        /// </summary>
        internal readonly record struct SpecialHandlerOutcome(double? Value, Exception? Error)
        {
            /// <summary>A measurement, with nothing caught on the way.</summary>
            internal static SpecialHandlerOutcome Measured(double value) => new(value, null);

            /// <summary>No measurement and no exception — a non-throwing failure (SQL NULL, or an id
            /// this mode does not map). There is nothing to name beyond the mode itself.</summary>
            internal static SpecialHandlerOutcome NotMeasured() => new(null, null);

            /// <summary>No measurement, and here is the exception that stopped it.</summary>
            internal static SpecialHandlerOutcome Failed(Exception ex) => new(null, ex);
        }

        /// <summary>
        /// Closes the evaluation-failure episode for one alert+server (the recovery point: its query
        /// measured cleanly again). Removes the record, re-persists, and — only if a record was actually
        /// present — writes exactly ONE audit entry (Info, Phase=Recovered). A clear on a key with no open
        /// episode is a no-op with no audit, so calling it on every clean tick is safe. Like
        /// <see cref="RecordEvaluationFailure"/> it NEVER dispatches; see EvalFailureVisibleTests.
        /// </summary>
        internal void ClearEvaluationFailure(string stateKey)
        {
            if (_evalFailures.TryRemove(stateKey, out var cleared) && !_dryRun)
            {
                // Same DryRun contract as RecordEvaluationFailure: the in-memory removal above always
                // happens (so the panel clears live), but the persisted store and audit are held back
                // while dry-running.
                PersistFailures();
                _audit?.Enqueue(
                    AuditEventType.SecurityEvent,
                    AuditSeverity.Info,
                    $"Alert '{cleared.AlertId}' on server '{cleared.ServerName}' is no longer failing to "
                        + $"evaluate (it measured cleanly, or is no longer monitored) after "
                        + $"{cleared.FailureCount} failing check(s)",
                    new Dictionary<string, string>
                    {
                        ["Category"] = EvalFailureAuditCategory,
                        ["Phase"] = "Recovered",
                        ["AlertId"] = cleared.AlertId,
                        ["ServerName"] = cleared.ServerName,
                        ["FirstFailureUtc"] = cleared.FirstFailureUtc.ToString("o"),
                        ["FailureCount"] = cleared.FailureCount.ToString(),
                    });
            }
        }

        /// <summary>
        /// Clears any durable evaluation failure whose alert or server has left the enabled set — the
        /// operator disabled the alert, or removed the server from monitoring. Nothing evaluates such a
        /// key any more, so its natural recovery (a clean measurement) can never arrive; this is the
        /// honest close. Each prune goes through <see cref="ClearEvaluationFailure"/>, so it audits
        /// exactly one recovery and re-persists. Callers must invoke this only with a genuinely-known
        /// enabled set (past the no-server / no-alert guards) so a transient empty read cannot wipe the
        /// whole set.
        ///
        /// <para>Its sibling for every OTHER per-(alert, server) collection is
        /// <see cref="PruneUnmonitoredAlertKeys"/>, called immediately after this one (2026-09-17).</para>
        /// </summary>
        private void ReconcileEvaluationFailures(List<AlertDefinition> enabledAlerts, List<ServerConnection> enabledServers)
        {
            if (_evalFailures.IsEmpty) return;
            var liveAlerts = new HashSet<string>(enabledAlerts.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            var liveServers = new HashSet<string>(enabledServers.SelectMany(c => c.GetServerList()), StringComparer.OrdinalIgnoreCase);
            foreach (var f in _evalFailures.Values.ToList())
            {
                if (!liveAlerts.Contains(f.AlertId) || !liveServers.Contains(f.ServerName))
                    ClearEvaluationFailure(StateKey(f.AlertId, f.ServerName));
            }
        }

        /// <summary>
        /// Drops every per-(alert, server) key the enabled set no longer evaluates - the operator
        /// removed or disabled the server, or disabled or deleted the alert - from every collection
        /// declared <see cref="AlertKeyScopeKind.PerAlertServer"/>. Invariant I2 of
        /// alert-stamp-key-per-server, fix round 2 (owner's ruling 2026-09-17, "prune in fix round 2").
        ///
        /// <para><b>Why it exists.</b> Once ResolveCleared judged a state by a PER-SERVER clock, an
        /// alert state on a server nobody evaluates any more could never be reaped: nothing stamps its
        /// clock, so ShouldAutoResolveAsCleared declines forever. Before the lane it WAS reaped, after
        /// three intervals, through the alert-wide clock the alert's other servers refreshed - the very
        /// defect the lane removed, which happened to give the right answer here. A cold gate proved
        /// the regression live. Nothing will measure such a key again, so it is closed here, the same
        /// honest close for "no longer monitored" that <see cref="ReconcileEvaluationFailures"/>
        /// already gives a failure record.</para>
        ///
        /// <para><b>What is LIVE, and must never be pruned.</b> The live set is
        /// <see cref="StateKey"/>(enabled alert id, enabled server name) over the two enabled sets. It
        /// is COMPOSED, never parsed back out of a key, because a server name carries backslashes and
        /// commas. A key in that set survives here UNCONDITIONALLY: an alert whose server is
        /// unreachable this cycle, skipped by an open breaker, not yet due or suppressed by a window is
        /// still monitored. Whether such an alert still breaches is ResolveCleared's question, and its
        /// answer rests on a measurement; this method never asks it. Pinned by
        /// AlertStampKeyPerServerTests.I2_anEnabledServerThatWasNotMeasuredThisCycleIsNeverPruned.</para>
        ///
        /// <para><b>A disabled or deleted ALERT is closed too, on every server, and that is accepted
        /// rather than incidental.</b> The live set is a cross product, so an alert that left the
        /// enabled set has no live key anywhere - exactly as ReconcileEvaluationFailures already closes
        /// that alert's failure records. An operator who disables an alert has stopped monitoring it,
        /// and a state nothing will measure should not stay on the wall. The consequence: an alert
        /// re-enabled while still breaching fires again as a new state, and may escalate again, because
        /// its escalation episode was dropped with it.</para>
        ///
        /// <para><b>One caller:</b> <see cref="EvaluateAllAsync"/>, immediately after
        /// ReconcileEvaluationFailures and behind the same guards - past the engine-disabled,
        /// no-server and no-alert returns - so a transient empty or damaged configuration read, which
        /// returns early, can never prune the whole set. That matters because ServerConnectionManager
        /// turns a damaged server-connections store into an EMPTY enabled list: called above the
        /// guards, this method would close every alert state and resolve every open history row across
        /// the estate. Pinned by
        /// AlertStampKeyPerServerTests.I2_anEmptyEnabledSetIsNotEvidenceThatAnythingWasRemoved.</para>
        ///
        /// <para><b>THE LIMIT THAT GUARD BUYS, stated plainly.</b> The guard cannot tell "nothing is
        /// configured" from "the store is damaged", so it treats both alike and returns. So removing
        /// the LAST enabled server, or disabling EVERY alert, closes nothing: the cycle returns before
        /// this method and before ResolveCleared, and the states on the wall stay there, with their
        /// open history rows and every other per-(alert, server) key, until a cycle with a non-empty
        /// enabled server set and a non-empty enabled alert set runs. That is the price of never
        /// wiping the estate on a damaged read, and it is accepted.</para>
        ///
        /// <para><b>The collections are named one by one below.</b> That is a hand list, so it is
        /// guarded by
        /// AlertStampKeyPerServerTests.I2_aServerRemovedFromMonitoringLeavesNoKeyInAnyPerServerCollection,
        /// which enumerates every PerAlertServer collection by reflection. <c>_evalFailures</c> is the
        /// one collection NOT removed from here: ReconcileEvaluationFailures owns it, because that
        /// removal must audit one recovery and re-persist the durable store, and it has already run.</para>
        ///
        /// <para>No [AlertKeyScopeMix]: this touches PerAlertServer collections only and no PerAlert
        /// one, and AlertStampKeyCensusTests.Axis2_noMethodHoldsAPerServerStateAndAPerAlertClockWithoutSayingWhy
        /// flags a method only when it reaches both scopes.</para>
        /// </summary>
        private void PruneUnmonitoredAlertKeys(List<AlertDefinition> enabledAlerts, List<ServerConnection> enabledServers)
        {
            var serverNames = enabledServers.SelectMany(c => c.GetServerList()).ToList();
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var enabledAlert in enabledAlerts)
                foreach (var serverName in serverNames)
                    live.Add(StateKey(enabledAlert.Id, serverName));

            // The alert states first, Active and Acknowledged alike. Each is closed the way
            // ResolveCleared closes one - status, instant, history row - and said once at
            // Information, because this close has no measurement behind it and nothing else will
            // explain why the alert left the wall.
            foreach (var entry in _activeStates)
            {
                if (live.Contains(entry.Key)) continue;
                if (!_activeStates.TryRemove(entry.Key, out var closed)) continue;   // closed concurrently

                closed.Status = AlertStatus.Resolved;
                closed.ResolvedAt = DateTime.UtcNow;
                _history.ResolveAlert(closed.AlertId, closed.ServerName);
                _logger.LogInformation(
                    "Alert {AlertId} on {Server} closed without a measurement: the alert or the server is no longer monitored, so nothing will evaluate it again",
                    closed.AlertId, LogAnon.S(closed.ServerName));
            }

            // Every other PerAlertServer collection, except _evalFailures (see the summary).
            foreach (var key in _lastServerEvaluation.Keys)
                if (!live.Contains(key)) _lastServerEvaluation.TryRemove(key, out _);
            foreach (var key in _lastNotified.Keys)
                if (!live.Contains(key)) _lastNotified.TryRemove(key, out _);
            foreach (var key in _hitTimes.Keys)
                if (!live.Contains(key)) _hitTimes.TryRemove(key, out _);
            foreach (var key in _escalatedEpisodes.Keys)
                if (!live.Contains(key)) _escalatedEpisodes.TryRemove(key, out _);
            foreach (var key in _breachRuns.Keys)
                if (!live.Contains(key)) _breachRuns.TryRemove(key, out _);
        }

        /// <summary>Writes the current failure set to <see cref="_evalFailureStorePath"/> as JSON,
        /// atomically (temp file + replace) so a crash mid-write cannot truncate the store. Best effort:
        /// a store that cannot be written must never take the evaluation loop down, so a failure here is
        /// logged and swallowed — the in-memory set is still correct for this run.</summary>
        private void PersistFailures()
        {
            lock (_storeLock) WriteStoreLocked();
        }

        /// <summary>
        /// Records that the store no longer matches memory, WITHOUT writing it — the deferred half of
        /// the coalescing described on <see cref="FlushFailureStore"/>. Used only where the change
        /// cannot alter which failures exist (a repeat tick on an episode the store already carries).
        /// </summary>
        private void MarkStoreDirty()
        {
            lock (_storeLock) _storeDirty = true;
        }

        /// <summary>
        /// Writes the store if, and only if, something deferred is waiting to go out. Called at the end
        /// of every evaluation cycle and again on <see cref="Dispose"/>.
        ///
        /// <para><b>Why coalesce (eval-failures-store-replace-fails, 2026-09-10).</b>
        /// <see cref="PersistFailures"/> is called once per FAILING ALERT+SERVER PAIR, not once per
        /// cycle. The live service had seven failing pairs, so one cycle rewrote the whole store seven
        /// times in a few milliseconds to publish one logical snapshot — and it was those rewrites that
        /// collided with whatever briefly holds the destination. Cutting seven writes to one cuts the
        /// exposure with them, which is a better answer than forgiving a collision we invited.</para>
        ///
        /// <para><b>The durability contract this must not break.</b> A crash mid-cycle must not lose a
        /// failure the panel is already showing. So the split is by WHAT CHANGED, not by when:
        /// <list type="bullet">
        /// <item>a change to WHICH failures exist — an episode opening in
        /// <see cref="RecordEvaluationFailure"/>, or one closing in
        /// <see cref="ClearEvaluationFailure"/> — is written SYNCHRONOUSLY, exactly as before;</item>
        /// <item>only a metadata refresh on an episode the store ALREADY carries (its
        /// <c>FailureCount</c>, <c>LastFailureUtc</c> and reason text) is deferred to this flush.</item>
        /// </list>
        /// So a crash between cycles can cost at most one cycle of failure-count and last-seen
        /// precision on failures that are already durable. It can never lose a failure the panel shows,
        /// and never resurrect one it does not, which is what
        /// <see cref="LoadPersistedFailures"/> reads back.</para>
        /// </summary>
        internal void FlushFailureStore()
        {
            lock (_storeLock)
            {
                if (!_storeDirty) return;
                WriteStoreLocked();
            }
        }

        /// <summary>
        /// The one physical write. Caller must hold <see cref="_storeLock"/>.
        ///
        /// <para><b>Retry (2026-09-10).</b> The write is attempted up to
        /// <see cref="StoreWriteAttempts"/> times with the backoff in <see cref="StoreWriteBackoffMs"/>.
        /// Only <see cref="IOException"/> is retried: that is the shape the measured fault takes (Win32
        /// 1175, "Unable to remove the file to be replaced"), and it is the shape that clears on its own
        /// within milliseconds. Anything else — a permissions fault, a path that cannot exist — is
        /// permanent, so retrying it would only delay the log by a quarter of a second and hide nothing.
        /// A permanent IOException still surrenders after the last attempt: a retry loop that hid a real
        /// failure forever would be worse than the noise it replaced.</para>
        ///
        /// <para>The snapshot is taken INSIDE the lock, per attempt. Outside it (where it used to be) a
        /// concurrent record could change the set between the snapshot and the write, and the write
        /// would then clear <c>_storeDirty</c> for a change it had not actually persisted.</para>
        /// </summary>
        private void WriteStoreLocked()
        {
            // I1 (fix round 1, 2026-09-10). DryRun's contract, stated on the DryRun property, is
            // "in-memory only - nothing is persisted". The test belongs HERE, at the one physical
            // write, and not at each caller: guarding only the callers that existed when this lane
            // landed is exactly what let a dry run write to disk. RecordEvaluationFailure and
            // ClearEvaluationFailure were guarded; FlushFailureStore - reached from the finally of
            // EvaluateAllAsync and from Dispose - was not, so a dirty flag carried out of a REAL
            // cycle was flushed to disk after DryRun was switched on. Guarding the chokepoint makes
            // every present and future write path honour it by construction.
            //
            // The dirty flag is deliberately left as it was. A change made before the dry run began
            // is still owed to disk; the flush that runs once DryRun goes off is the one that pays
            // it. What must not happen - and now cannot - is a write while DryRun is ON.
            if (_dryRun) return;

            Exception? last = null;

            for (var attempt = 1; attempt <= StoreWriteAttempts; attempt++)
            {
                try
                {
                    StoreWriteAttemptHookForTests?.Invoke(attempt);

                    var json = JsonSerializer.Serialize(_evalFailures.Values.ToList());
                    var dir = Path.GetDirectoryName(_evalFailureStorePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var tmp = _evalFailureStorePath + ".tmp";
                    File.WriteAllText(tmp, json);
                    if (File.Exists(_evalFailureStorePath))
                        File.Replace(tmp, _evalFailureStorePath, null);
                    else
                        File.Move(tmp, _evalFailureStorePath);

                    _storeWriteCount++;
                    _storeDirty = false;

                    // A retry that WORKED is not a failure and must not warn — but it is the only
                    // trace that the contention is still there, so it is stated at Debug.
                    if (attempt > 1)
                        _logger.LogDebug(
                            "Persisted the evaluation-failure store to {Path} on attempt {Attempt} of {Attempts}",
                            _evalFailureStorePath, attempt, StoreWriteAttempts);

                    if (_persistEpisodeOpen)
                    {
                        _persistEpisodeOpen = false;
                        _logger.LogInformation(
                            "The evaluation-failure store at {Path} is writable again", _evalFailureStorePath);
                    }
                    return;
                }
                catch (IOException ex)
                {
                    last = ex;
                    if (attempt < StoreWriteAttempts) Thread.Sleep(StoreWriteBackoffMs[attempt - 1]);
                }
                catch (Exception ex)
                {
                    // Not contention. Surrender now rather than sleeping through a fault that cannot clear.
                    last = ex;
                    break;
                }
            }

            // I2 (fix round 1, 2026-09-10). SURRENDERED, so memory and disk now disagree: say so, or
            // the change is dropped on the floor. Without this the end-of-cycle flush saw a clean
            // flag and did nothing, and the failure never reached disk until some unrelated set
            // change happened to write the store. Two measured consequences: a failure the panel was
            // showing stayed off disk across a Dispose, and a surrendered RECOVERY let a restart
            // reload a failure that no longer existed - the resurrection
            // ARecovery_isOnDiskImmediately_soARestartCannotResurrectIt exists to forbid.
            //
            // This cannot spin. The retry budget is bounded per call (StoreWriteAttempts, 260 ms of
            // backoff at worst), the flag causes at most ONE further attempt-set per flush, and the
            // Warning cardinality is owned by _persistEpisodeOpen and not by this flag - a store
            // that is permanently unwritable still logs one Warning for the episode and Debug
            // thereafter, however many flushes retry it. It is also unreachable under DryRun: the
            // guard at the top of this method returns before the attempt loop.
            _storeDirty = true;

            // Same cardinality the failure log itself obeys: name the cause once per episode at
            // Warning, repeat at Debug, and say so once when it recovers.
            if (!_persistEpisodeOpen)
            {
                _persistEpisodeOpen = true;
                _logger.LogWarning(last,
                    "Could not persist evaluation-failure store to {Path} after {Attempts} attempts; the "
                        + "in-memory set is still correct for this run, but a restart would not see it",
                    _evalFailureStorePath, StoreWriteAttempts);
            }
            else
            {
                _logger.LogDebug(last,
                    "Still could not persist evaluation-failure store to {Path} after {Attempts} attempts",
                    _evalFailureStorePath, StoreWriteAttempts);
            }
        }

        /// <summary>Reloads the failure set the previous process persisted, so the panel and the
        /// "failing since" timestamp are correct from the first render and a mid-episode restart does not
        /// re-audit. Fail-safe: a missing or unreadable store starts empty (the honest "we do not yet
        /// know" state), never a crash.</summary>
        private void LoadPersistedFailures()
        {
            try
            {
                if (!File.Exists(_evalFailureStorePath)) return;
                string json;
                lock (_storeLock) json = File.ReadAllText(_evalFailureStorePath);
                if (string.IsNullOrWhiteSpace(json)) return;
                var loaded = JsonSerializer.Deserialize<List<AlertEvalFailure>>(json);
                if (loaded == null) return;
                foreach (var f in loaded)
                {
                    if (string.IsNullOrEmpty(f.AlertId) || string.IsNullOrEmpty(f.ServerName)) continue;
                    var stateKey = StateKey(f.AlertId, f.ServerName);
                    _evalFailures[stateKey] = f;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not reload evaluation-failure store from {Path}; starting with none", _evalFailureStorePath);
            }
        }

        public AlertEvaluationService(
            ILogger<AlertEvaluationService> logger,
            AlertDefinitionService definitions,
            AlertHistoryService history,
            AlertingService alerting,
            ServerConnectionManager connections,
            ToastService toast,
            NotificationChannelService channels,
            liveQueriesCacheStore cache,
            IQueryOrchestrator orchestrator,
            AlertBaselineService? baseline = null,
            ConnectionHealthService? health = null,
            SqlConnectionPoolService? pool = null,
            IConfiguration? configuration = null,
            ServerCircuitBreakerService? breaker = null,
            AuditLogService? audit = null,
            string? evalFailureStorePath = null)
        {
            _logger = logger;
            _definitions = definitions;
            _history = history;
            _alerting = alerting;
            _connections = connections;
            _toast = toast;
            _channels = channels;
            _cache = cache;
            _orchestrator = orchestrator;
            _baseline = baseline;
            _breaker = breaker;
            _health = health;
            _pool = pool;

            var baseTickSeconds = configuration?.GetValue<int>("AlertEvaluation:BaseTickSeconds", 30) ?? 30;
            if (baseTickSeconds <= 0) baseTickSeconds = 30;
            _baseTickSeconds = baseTickSeconds;

            var maxConcurrentAlerts = configuration?.GetValue<int>("AlertEvaluation:MaxConcurrentAlerts", DefaultMaxConcurrentAlerts)
                                      ?? DefaultMaxConcurrentAlerts;
            if (maxConcurrentAlerts <= 0) maxConcurrentAlerts = DefaultMaxConcurrentAlerts;
            _maxConcurrentAlerts = maxConcurrentAlerts;

            _audit = audit;
            _evalFailureStorePath = evalFailureStorePath
                ?? Path.Combine(AppContext.BaseDirectory, "Config", "eval-failures.json");
            // Reload any failures the last process left behind, BEFORE the loop starts, so the panel
            // and "failing since" are correct from the first render and a mid-episode restart does not
            // re-open (and so re-audit) an episode that was already open — TryAdd below sees the
            // reloaded key and stays quiet until the alert actually recovers.
            LoadPersistedFailures();
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _loopTask = Task.Run(() => LoopAsync(_cts.Token));
            _logger.LogInformation("Alert evaluation engine started ({IntervalS}s tick)", _baseTickSeconds);
        }

        public void Stop()
        {
            _isRunning = false;
            _cts.Cancel();
            _logger.LogInformation("Alert evaluation engine stopped");
        }

        /// <summary>
        /// Marker for the one-shot repair below. Dated, so a later repair of the same table gets
        /// its own marker instead of being swallowed by this one.
        /// </summary>
        internal const string StrandedCumulativeMarker = "resolve-stranded-cumulative-alerts-2026-08-24";

        /// <summary>
        /// Resolves the alert-history rows a cumulative alert left behind when it was firing on a
        /// running total, exactly once per database.
        ///
        /// <para>WHY IT IS NEEDED. Purging the poisoned baselines cleans what the defect taught
        /// the software; these rows are what the defect showed the CLIENT, and nothing else can
        /// clear them. An alert leaves the active list only through the resolve branch of an
        /// evaluation cycle, and that branch is gated on removing the alert from
        /// <c>_activeStates</c> - an in-memory dictionary a restart empties. After this fix the
        /// six alerts correctly fire nothing, so they never re-enter that dictionary, so they are
        /// never removed, so ResolveAlert is never called. The Alerts page reads
        /// AlertHistoryService.GetActiveAlerts and would go on showing the old fabricated
        /// Criticals - os_paging at 98,250,865 against a threshold of 50, read out of a real
        /// install's alert-history.db on 2026-08-24 - with the numbers now known to be wrong and
        /// no path in the product that removes them.</para>
        ///
        /// <para>WHY RESOLVING ALL OF THEM IS SAFE. It runs once, before this process evaluates
        /// anything, and every row it can see was written by a build that compared a running
        /// total to a rate threshold - because that is what every build before this one did. A
        /// genuinely firing alert cannot be among them: the first honest measurement of these six
        /// happens after this returns.</para>
        ///
        /// <para>The loop re-reads rather than paging: resolving removes rows from the active
        /// set, so each pass makes progress, and the row cap on the read cannot leave a tail
        /// behind. The pass ceiling is a guard against a resolve that silently does nothing, not
        /// an expected count.</para>
        /// </summary>
        internal async Task<int> ResolveStrandedCumulativeAlertsAsync()
        {
            try
            {
                if (_dryRun) return 0;

                var cumulative = new HashSet<string>(
                    _definitions.GetAllAlerts().Where(a => a.IsCumulativeCounter).Select(a => a.Id),
                    StringComparer.OrdinalIgnoreCase);
                if (cumulative.Count == 0) return 0;

                if (!await _cache.TryClaimSchemaMarkerAsync(StrandedCumulativeMarker)) return 0;

                // alerts-r1-14: count DISTINCT pairs actually cleared, not once-per-row-per-pass.
                // The old loop incremented `resolved` on every row of every pass, so if ResolveAlert
                // silently failed to clear a row (it swallows its own exception and returns void) the
                // same rows were recounted up to 20 times — six stranded rows logged as resolved=120
                // with the table unchanged. Track every pair seen, stop the moment a pass makes no
                // progress, and report only the pairs that are genuinely no longer stranded.
                static string StrandedKey(string alertId, string serverName) => StateKey(alertId, serverName);
                var everStranded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (var pass = 0; pass < 20; pass++)
                {
                    var stranded = _history.GetActiveAlerts()
                        .Where(r => cumulative.Contains(r.AlertId))
                        .Select(r => (r.AlertId, r.ServerName))
                        .Distinct()
                        .ToList();
                    if (stranded.Count == 0) break;

                    foreach (var (alertId, serverName) in stranded)
                    {
                        everStranded.Add(StrandedKey(alertId, serverName));
                        _history.ResolveAlert(alertId, serverName);
                    }

                    // If a full pass of resolves did not shrink the stranded set, ResolveAlert is not
                    // clearing these rows — stop rather than spin 20 times recounting the same ones.
                    var remainingAfterPass = _history.GetActiveAlerts()
                        .Count(r => cumulative.Contains(r.AlertId)
                                    && stranded.Any(s => s.AlertId == r.AlertId && s.ServerName == r.ServerName));
                    if (remainingAfterPass >= stranded.Count) break;
                }

                var stillStranded = _history.GetActiveAlerts()
                    .Where(r => cumulative.Contains(r.AlertId))
                    .Select(r => StrandedKey(r.AlertId, r.ServerName))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var resolved = everStranded.Count(k => !stillStranded.Contains(k));

                if (resolved > 0)
                    _logger.LogInformation(
                        "Resolved {N} unresolved alert(s) that were firing on a raw counter total rather than a rate. Their values were never the quantity the alert is named for, so they are cleared once here; from now on these alerts measure the difference between two samples",
                        resolved);

                return resolved;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve the stranded cumulative alerts");
                return 0;
            }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            // Before the first tick, never after: a cycle that runs first could fire one of these
            // alerts honestly, and the repair would then clear a real alert.
            await ResolveStrandedCumulativeAlertsAsync();

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_baseTickSeconds));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var tickStart = DateTime.UtcNow;
                try
                {
                    await EvaluateAllAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Alert evaluation cycle failed");
                }
                var elapsed = DateTime.UtcNow - tickStart;
                if (elapsed.TotalSeconds > _baseTickSeconds)
                    _logger.LogWarning("[ALERT] Evaluation tick overran interval ({ElapsedMs}ms > {IntervalS}s); next tick dropped",
                        (int)elapsed.TotalMilliseconds, _baseTickSeconds);
            }
        }

        /// <summary>
        /// Run a single evaluation cycle across all enabled alerts and all enabled servers.
        /// </summary>
        [AlertKeyScopeMix("The cycle legitimately holds both clocks: it reads the PER-ALERT due "
            + "clock to decide which alerts to run, and separately walks PER-SERVER _activeStates "
            + "to auto-acknowledge stale ones. The two never judge each other - no _activeStates "
            + "entry's fate is decided here from _lastDueCheck. That judgement lives in "
            + "ResolveCleared, which reads the per-server measurement clock only.")]
        public async Task EvaluateAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _evaluationLock.WaitAsync(0, cancellationToken)) return; // skip if already running

            try
            {
                var globalDefaults = _definitions.GetGlobalDefaults();
                if (!globalDefaults.Enabled) return;

                var alerts = _definitions.GetEnabledAlerts();
                var serverConnections = _connections.GetEnabledConnections();
                // no-server-idle ruling: zero configured servers is an idle posture, not a fault. The
                // cycle is already a no-op here; announce the idle line once (host-agnostic safety net —
                // the shared latch keeps it to a single line) and produce no per-tick noise.
                if (serverConnections.Count == 0)
                {
                    SQLTriage.Data.NoServerIdleNotice.AnnounceOnce(_logger);
                    return;
                }
                if (alerts.Count == 0) return;

                // eval-failure-visible: prune durable failures whose alert or server has left the
                // enabled set. Such a record can never see a fresh success to clear it (nothing
                // evaluates it any more), so without this it would linger on the panel forever; clearing
                // it here audits one recovery, the honest close for "no longer monitored". Runs only past
                // the two guards above, so a transient empty/damaged config (which returns early) can
                // never false-prune the whole set.
                ReconcileEvaluationFailures(alerts, serverConnections);

                // alert-stamp-key-per-server fix round 2 (owner's ruling 2026-09-17): the same close
                // for every OTHER per-(alert, server) collection - the alert states themselves and the
                // clocks that judge them. Immediately after the line above, behind the same guards,
                // for the same reason. See PruneUnmonitoredAlertKeys.
                PruneUnmonitoredAlertKeys(alerts, serverConnections);

                var now = DateTime.UtcNow;

                // Enforce retention policy from global defaults
                _history.EnforceRetention(globalDefaults.RetentionDays);

                // Auto-acknowledge stale alerts
                var autoAcked = _history.AutoAcknowledge(globalDefaults.AutoAcknowledgeHours);
                if (autoAcked > 0)
                {
                    _logger.LogInformation("Auto-acknowledged {Count} stale alerts", autoAcked);
                    // Also update in-memory states
                    foreach (var state in _activeStates.Values.Where(s => s.Status == AlertStatus.Active
                        && (now - s.FirstTriggered).TotalHours >= globalDefaults.AutoAcknowledgeHours))
                    {
                        state.Status = AlertStatus.Acknowledged;
                        state.AcknowledgedAt = now;
                    }
                }

                // Check operational/maintenance windows once per cycle
                var windows = _channels.GetAlertWindows();

                // Evaluate each alert that is due. The per-alert work is COLLECTED here and run
                // below with a bounded degree of parallelism: the due checks, the window checks and
                // the _lastDueCheck stamp stay strictly sequential and in catalogue order, and
                // only the querying overlaps. See DefaultMaxConcurrentAlerts for the measurement
                // that made this necessary.
                var dueAlerts = new List<Func<Task>>();

                foreach (var alert in alerts)
                {
                    // Enforce minimum 300s for log-scan alert types (xp_readerrorlog is expensive)
                    var effectiveFrequency = EffectiveFrequencySeconds(alert);
                    // The once-per-process clamp warning. TryAdd both tests and sets the flag in
                    // one atomic step, in a collection of its own - it used to be a "__logwarn__"
                    // key inside the freshness dictionary, which put two key grammars in one
                    // keyspace and made any re-key of the stamps orphan this sentinel.
                    if (effectiveFrequency != alert.FrequencySeconds
                        && _frequencyClampWarned.TryAdd(alert.Id, 0))
                    {
                        _logger.LogWarning("Alert '{AlertId}' FrequencySeconds ({Stored}s) is below log-scan minimum; effective frequency clamped to {Min}s",
                            alert.Id, alert.FrequencySeconds, LogScanMinFrequencySeconds);
                    }

                    // Skip if not due yet based on frequency. PER ALERT on purpose: the due
                    // decision is taken once, here, before the alert fans out across its servers.
                    // This clock says "is it time to ask again"; it says NOTHING about whether any
                    // particular server answered, and nothing may read it as if it did.
                    var dueKey = alert.Id;
                    if (_lastDueCheck.TryGetValue(dueKey, out var lastDue)
                        && (now - lastDue).TotalSeconds < effectiveFrequency)
                    {
                        continue;
                    }

                    // Suppress based on operational / maintenance windows
                    if (!windows.ShouldFire(alert.AlwaysAlert))
                    {
                        _logger.LogDebug("Alert '{AlertId}' suppressed by window config (maintenance={M}, operational={O}, alwaysAlert={A})",
                            alert.Id, windows.IsMaintenanceActive, windows.OperationalWindow.IsActive(), alert.AlwaysAlert);
                        continue;
                    }

                    // Route special query modes to dedicated handlers
                    var isSpecial = IsRoutedToBuiltInHandler(alert);

                    RecordEvaluationAttempt(dueKey, now);

                    var due = alert;    // captured per iteration, not by reference to the loop
                    dueAlerts.Add(async () =>
                    {
                        // Run against each server. This half is unchanged: one alert still fans out
                        // across its servers and waits for all of them.
                        var tasks = new List<Task>();
                        foreach (var conn in serverConnections)
                        {
                            foreach (var serverName in conn.GetServerList())
                            {
                                if (_breaker != null && !_breaker.ShouldAttempt(serverName))
                                    continue;
                                tasks.Add(isSpecial
                                    ? ThrottledSpecialEvaluateAsync(due, conn, serverName, globalDefaults, cancellationToken)
                                    : ThrottledEvaluateAsync(due, conn, serverName, globalDefaults, cancellationToken));
                            }
                        }

                        await Task.WhenAll(tasks);
                    });
                }

                await RunBoundedAsync(dueAlerts, _maxConcurrentAlerts, cancellationToken);

                // Resolve alerts that are no longer triggering
                ResolveCleared();

                OnAlertsChanged?.Invoke();
            }
            catch (OperationCanceledException)
            {
                // Shutdown, not a defect. LoopAsync catches this to break out of its timer loop;
                // folding it into the LogError below would announce a false failure on every stop
                // and keep the loop spinning through it.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Alert evaluation cycle failed");
            }
            finally
            {
                // The coalescing point (eval-failures-store-replace-fails, 2026-09-10). Every deferred
                // metadata refresh from this cycle goes out here as ONE write. In the finally, so a
                // cycle that threw still persists what it learned before it threw.
                FlushFailureStore();
                _evaluationLock.Release();
            }
        }

        /// <summary>
        /// Runs every unit of work, at most <paramref name="maxConcurrent"/> of them at a time, and
        /// completes when all have. The scheduler the alert cycle uses across alerts; internal so a
        /// test can MEASURE it (InternalsVisibleTo SQLTriage.Tests) rather than assert it by reading.
        ///
        /// <para>The bound is real and not decorative: the work here opens SQL connections, so
        /// "run them all at once" would multiply a client's concurrent query count by the number of
        /// due alerts. Peak concurrency is asserted, not just wall-clock time, by
        /// <c>AlertCycleConcurrencyTests</c>.</para>
        /// </summary>
        internal static async Task RunBoundedAsync(
            IReadOnlyList<Func<Task>> work, int maxConcurrent, CancellationToken cancellationToken = default)
        {
            if (work is null || work.Count == 0) return;
            if (maxConcurrent <= 0) maxConcurrent = 1;

            using var slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
            var running = new List<Task>(work.Count);

            foreach (var unit in work)
            {
                running.Add(RunOneAsync(unit));
            }

            await Task.WhenAll(running);

            async Task RunOneAsync(Func<Task> unit)
            {
                await slots.WaitAsync(cancellationToken);
                try
                {
                    await unit();
                }
                finally
                {
                    slots.Release();
                }
            }
        }

        /// <summary>Runs one standard alert evaluation through the orchestrator and applies what it
        /// learned about the server to the circuit breaker. Internal rather than private so a test can
        /// drive the whole path — wrapper, evaluation, exception handling, breaker — against a real
        /// unreachable endpoint (InternalsVisibleTo SQLTriage.Tests).</summary>
        internal async Task ThrottledEvaluateAsync(
            AlertDefinition alert,
            ServerConnection connection,
            string serverName,
            AlertGlobalDefaults globalDefaults,
            CancellationToken cancellationToken)
        {
            // Per-attempt sink. EvaluateAlertOnServerAsync writes into it what it actually observed
            // about the SERVER; see ServerReachabilityProbe for why result.Success cannot tell us.
            var reach = new ServerReachabilityProbe();

            var result = await _orchestrator.EnqueueAsync(new QueryRequest
            {
                QueryId = $"alert:{alert.Id}:{serverName}",
                Work = async _ => await EvaluateAlertOnServerAsync(alert, connection, serverName, globalDefaults, reach),
                CancellationToken = cancellationToken
            }, QueryPriority.P1_Alert, cancellationToken);

            if (!result.Success)
            {
                ApplyOrchestratorFailure(_breaker, serverName, result);
                BreakBreachRun(StateKey(alert.Id, serverName));
                _logger.LogError(result.Exception, "Alert evaluation failed for {AlertId} on {Server}", alert.Id, serverName);
                return;
            }

            ApplyBreakerOutcome(_breaker, serverName, reach);
        }

        /// <summary>
        /// What one evaluation attempt learned about the SERVER, as opposed to what it learned about
        /// the orchestrator. Written by the evaluation, read by the throttling wrapper.
        ///
        /// <para><b>The defect this exists to fix (2026-08-01).</b> The wrappers used to call
        /// <c>_breaker.RecordSuccess(serverName)</c> whenever <c>result.Success</c> was true. That flag
        /// answers "did the work delegate throw", not "did we reach SQL Server" — and the delegate never
        /// throws, because every evaluation path catches its own <see cref="SqlException"/>, logs it and
        /// returns (deliberately: the recorded failure is what makes AlertsNoc render Unknown instead of
        /// a clean Ok). So polling a DEAD server reported success, ConsecutiveFailures reset to 0, and
        /// the circuit closed on the very next tick. The live log shows 143 "circuit OPENED" and 143
        /// "circuit CLOSED", perfectly paired: ConnectionHealthService — which sees real failures —
        /// opened the breaker, and the alert loop immediately closed it again. Two subsystems sharing
        /// one breaker with contradictory definitions of success. The breaker consequently never
        /// suppressed the expensive path, so a dead server burned a full login timeout on every tick.</para>
        ///
        /// <para><b><see cref="ServerReachability.Undetermined"/> is a first-class state, not a
        /// synonym for success.</b> An attempt that was skipped (alert not supported on Azure SQL) or
        /// that failed for a reason which is not the server's fault says nothing about reachability,
        /// and must leave the breaker exactly as it found it. Recording success there would be the same
        /// false-signal bug in a smaller costume; recording failure there would suppress polling of a
        /// perfectly healthy server because of a defect in our own alert logic.</para>
        /// </summary>
        internal enum ServerReachability
        {
            /// <summary>Nothing was learned about the server this attempt. Leave the breaker alone.</summary>
            Undetermined = 0,
            /// <summary>A query completed against the server — it is up and answering.</summary>
            Reached,
            /// <summary>The connection or query failed at the SQL layer — treat as a real failure.</summary>
            Unreachable
        }

        /// <summary>
        /// The per-attempt sink. <b>It has no public setter, deliberately (invariant B,
        /// 2026-09-19).</b> The only ways to write it are <see cref="MarkReached"/> and
        /// <see cref="MarkFailed"/>, so the compiler enumerates every writer, and a failure cannot
        /// be recorded without the exception that caused it. The defect this closes: the SQL catch
        /// in EvaluateAlertOnServerAsync wrote <c>Unreachable</c> for EVERY SqlException, so a
        /// permission refusal from a healthy server fed the breaker like a dead one, and
        /// <see cref="ApplyBreakerOutcome"/> saw only the enum, with no exception in scope to ask
        /// about. The cause now travels with the verdict, and the verdict is
        /// <see cref="ServerAnswerClassifier"/>'s.
        /// </summary>
        internal sealed class ServerReachabilityProbe
        {
            public ServerReachability Reachability { get; private set; } = ServerReachability.Undetermined;

            /// <summary>The exception behind the last <see cref="MarkFailed"/>; null otherwise.</summary>
            public Exception? Cause { get; private set; }

            /// <summary>A query completed against the server.</summary>
            public void MarkReached()
            {
                Reachability = ServerReachability.Reached;
                Cause = null;
            }

            /// <summary>The attempt against the server ended in <paramref name="cause"/>. The shared
            /// classifier decides what that means: a server that ANSWERED with a refusal or a
            /// user-defined error is Reached (it is up), anything else is Unreachable.</summary>
            public void MarkFailed(Exception cause)
            {
                Cause = cause;
                Reachability = ServerAnswerClassifier.ServerAnswered(cause)
                    ? ServerReachability.Reached
                    : ServerReachability.Unreachable;
            }
        }

        /// <summary>Applies one attempt's observed reachability to the shared circuit breaker, WITH the
        /// exception that produced it (invariant B, 2026-09-19): the enum alone cannot tell a refusal
        /// from a dead server, so the probe carries its cause and the breaker's own classifier reads it.
        /// Internal test seam (InternalsVisibleTo SQLTriage.Tests).</summary>
        internal static void ApplyBreakerOutcome(
            ServerCircuitBreakerService? breaker, string serverName, ServerReachabilityProbe reach)
        {
            if (breaker == null) return;
            switch (reach.Reachability)
            {
                case ServerReachability.Reached:
                    breaker.RecordSuccess(serverName);
                    break;
                case ServerReachability.Unreachable:
                    // MarkFailed is the only way to reach Unreachable, and it always sets the cause.
                    breaker.RecordFailure(serverName, reach.Cause
                        ?? new InvalidOperationException("Reachability was Unreachable with no cause recorded."));
                    break;
                default:
                    break; // Undetermined — say nothing rather than say something false.
            }
        }

        /// <summary>
        /// What the circuit breaker learns when the query orchestrator reports a failure instead of
        /// running the evaluation to completion. <b>Invariant B-prime (2026-09-19): our own fault is
        /// not evidence about the server.</b> A concurrency slot the orchestrator could not grant
        /// (<see cref="QueryResult.FailedBeforeWorkStarted"/>, set at its origin in QueryOrchestrator)
        /// means OUR resources were exhausted and the server was never asked, so the breaker is left
        /// alone. Anything else keeps its old meaning and goes to the breaker with its cause, where
        /// the shared classifier decides. A COMMAND timeout never arrives here: it is a SqlException
        /// inside the work, caught by the evaluation and carried by the reachability probe. The two
        /// wrappers, standard and special, both call this; neither decides for itself.
        /// </summary>
        internal static void ApplyOrchestratorFailure(
            ServerCircuitBreakerService? breaker, string serverName, QueryResult result)
        {
            if (breaker == null || result.FailedBeforeWorkStarted) return;
            breaker.RecordFailure(serverName, result.Exception
                ?? new InvalidOperationException("The query orchestrator reported a failure and carried no exception."));
        }

        private async Task ThrottledSpecialEvaluateAsync(
            AlertDefinition alert,
            ServerConnection connection,
            string serverName,
            AlertGlobalDefaults globalDefaults,
            CancellationToken cancellationToken)
        {
            var result = await _orchestrator.EnqueueAsync(new QueryRequest
            {
                QueryId = $"alert:special:{alert.Id}:{serverName}",
                Work = async _ => await EvaluateSpecialAlertAsync(alert, connection, serverName, globalDefaults),
                CancellationToken = cancellationToken
            }, QueryPriority.P1_Alert, cancellationToken);

            if (!result.Success)
            {
                ApplyOrchestratorFailure(_breaker, serverName, result);
                BreakBreachRun(StateKey(alert.Id, serverName));
                _logger.LogError(result.Exception, "Special alert evaluation failed for {AlertId} on {Server}", alert.Id, serverName);
                return;
            }

            // No RecordSuccess here — deliberately. The special handlers convert unreachability into a
            // VALUE rather than an error: CheckConnectivityAsync catches everything and returns 1 =
            // unreachable, because that IS the result of a connectivity alert. So this path cannot
            // distinguish "the server answered" from "the server did not" without restructuring those
            // handlers — and it must not report success on the strength of a delegate that did not
            // throw. Reachability here is Undetermined by construction: the breaker is left alone,
            // which is also what keeps the alert that exists to detect a down server from being the
            // thing that suppresses its own polling.
        }

        /// <summary>
        /// Evaluates one non-<c>standard</c> <c>queryMode</c> alert against one server.
        /// Internal rather than private so a test can drive the fail-closed contract below without
        /// standing up the whole loop (InternalsVisibleTo SQLTriage.Tests).
        ///
        /// <para><b>r2-09 / r2-01 (2026-08-26): a handler that cannot measure now fails CLOSED and
        /// LOUD.</b> Every special handler catches its own SQL error and returns <c>null</c>, and an
        /// unrecognised <c>queryMode</c> falls to <c>_ =&gt; null</c>. The old body then did
        /// <c>if (value == null) return;</c> — no state, no history row, no <c>_evalFailures</c>
        /// entry, no log line at any level. A non-sysadmin monitoring login that <c>xp_readerrorlog</c>
        /// and <c>dm_io_virtual_file_stats</c> refuse made five alerts fail every cycle forever while
        /// <c>AlertsNoc</c> read "All Clear"; a shipped alert whose <c>queryMode</c> had no case
        /// (<c>windows_power_plan</c>'s <c>registry_check</c>) read as a correct power plan. A null is
        /// "we could not measure", so it now records an <c>_evalFailures</c> entry — the same signal
        /// the standard path writes — which is what drives the NOC's Unknown state instead of a false
        /// Ok. A measured value clears any prior failure, exactly as the standard path does.</para>
        /// </summary>
        internal async Task EvaluateSpecialAlertAsync(
            AlertDefinition alert,
            ServerConnection connection,
            string serverName,
            AlertGlobalDefaults globalDefaults)
        {
            var stateKey = StateKey(alert.Id, serverName);

            double? value = null;
            Exception? handlerError = null;
            try
            {
                // Each handler hands back its measurement AND anything it caught reaching for it, so a
                // swallowed exception no longer dies inside the handler (SpecialHandlerOutcome).
                var outcome = alert.QueryMode switch
                {
                    "connectivity_check" => await CheckConnectivityAsync(connection, serverName),
                    "host_connectivity_check" => await CheckConnectivityAsync(connection, serverName),
                    "error_log_scan" => await ScanErrorLogAsync(alert, connection, serverName),
                    "io_error_check" => await CheckIoErrorsAsync(connection, serverName),
                    "deadlock_count" => await CountDeadlocksAsync(connection, serverName),
                    "registry_check" => await CheckPowerPlanAsync(connection, serverName),
                    "response_time_probe" => await MeasureResponseTimeAsync(connection, serverName),
                    _ => SpecialHandlerOutcome.NotMeasured()
                };
                value = outcome.Value;
                handlerError = outcome.Error;
            }
            catch (Exception ex)
            {
                // The handlers catch their own SQL errors (and now surrender them above), so a throw
                // that escapes to here is unusual — but it is still "we could not measure", not
                // evidence the server is healthy.
                handlerError = ex;
            }

            if (value == null)
            {
                // Fail closed and loud: record the failure so AlertsNoc renders Unknown, never a
                // clean Ok, for a special alert that could not evaluate this cycle (#68 LEG 2 parity).
                // Durable + audited once per episode via the shared sink (eval-failure-visible).
                //
                // logon-failure-unmeasurable (2026-09-09): the reason now NAMES THE CAUSE — exception
                // type and message — and the same text goes to the durable record, the audit entry, the
                // operator panel and, ONCE PER EPISODE, a Warning line. Production emits no Debug, so
                // the old pair of LogDebug calls meant an alert could fail 76 times running and state
                // nothing anywhere an operator reads but the caller's own fallback sentence. The
                // per-cycle repeat stays at Debug so a week-long outage is one Warning, not 20,000.
                var reason = handlerError != null
                    ? DescribeEvaluationFailure(handlerError)
                    : $"queryMode '{alert.QueryMode}' produced no measurement this cycle";

                var opened = RecordEvaluationFailure(stateKey, alert.Id, serverName, reason);

                if (opened)
                    _logger.LogWarning(handlerError,
                        "Alert {AlertId} (queryMode '{Mode}') can no longer be evaluated on {Server}: {Reason}. Its state reads Unknown, not Ok, until it measures again",
                        alert.Id, alert.QueryMode, LogAnon.S(serverName), reason);
                else
                    _logger.LogDebug(handlerError,
                        "Alert {AlertId} (queryMode '{Mode}') still cannot be evaluated on {Server}: {Reason}",
                        alert.Id, alert.QueryMode, LogAnon.S(serverName), reason);
                return;
            }

            // A value was measured — clear any prior failure record, exactly as the standard path does.
            ClearEvaluationFailure(stateKey);

            // ...and stamp the measurement clock for THIS server. Below this line a value is in
            // hand; above it every route is "we could not measure" and stamps nothing, which is
            // what stops a failed cycle from looking like a clean one to ResolveCleared.
            RecordServerEvaluation(alert.Id, serverName, DateTime.UtcNow);

            try
            {
                var isTriggered = IsThresholdBreached(value.Value, alert.Thresholds.Warning, alert.Operator)
                    || (alert.Thresholds.Critical.HasValue && IsThresholdBreached(value.Value, alert.Thresholds.Critical, alert.Operator));

                if (isTriggered)
                {
                    // The hold (AlertDefinition.HoldSeconds), on this path exactly as on the standard one:
                    // a breach that has not lasted long enough opens no episode. An active alert still
                    // updates below.
                    if (!ObserveBreachForHold(alert, stateKey) && !_activeStates.ContainsKey(stateKey))
                    {
                        LogBreachHeld(alert, serverName, value.Value, stateKey);
                        return;
                    }

                    var isCritical = alert.Thresholds.Critical.HasValue
                        && IsThresholdBreached(value.Value, alert.Thresholds.Critical, alert.Operator);
                    // Which numeric threshold fired, kept separate from the alert's severity so the
                    // message names the real threshold even when the declared-severity floor (r1-07)
                    // routes it at Critical.
                    var thresholdTier = isCritical ? "Critical" : "Warning";
                    var severity = RuntimeSeverity(alert, isCritical);

                    // 2026-08-05, the same class as the main path with a different shape: this
                    // defaulted to `?? 1` when Warning was null, so the message named a threshold
                    // of 1 that no definition carried. These special modes fire on a fixed
                    // threshold or not at all, so a null Warning with no Critical breach means
                    // nothing measured a threshold, and none is printed.
                    var firingBasis = isCritical
                        ? FiringBasis.Fixed(alert.Thresholds.Critical!.Value)
                        : alert.Thresholds.Warning.HasValue
                            ? FiringBasis.Fixed(alert.Thresholds.Warning.Value)
                            : FiringBasis.Unknown();

                    if (!_activeStates.TryGetValue(stateKey, out var existing))
                    {
                        existing = new AlertState
                        {
                            AlertId = alert.Id,
                            AlertName = alert.Name,
                            ServerName = serverName,
                            Severity = severity,
                            Status = AlertStatus.Active,
                            LastValue = value.Value,
                            ThresholdValue = firingBasis.Threshold,
                            BasisKind = firingBasis.Kind.ToString(),
                            HitCount = 1,
                            FirstTriggered = DateTime.UtcNow,
                            LastTriggered = DateTime.UtcNow,
                            Message = FormatMessage(alert, serverName, value.Value, firingBasis, thresholdTier)
                        };
                        _activeStates[stateKey] = existing;
                        if (!_dryRun) _history.UpsertAlert(existing);
                        if (!_dryRun) DispatchNotification(alert, existing);
                        _lastNotified[stateKey] = DateTime.UtcNow;
                        // special-alerts-fire-silently (2026-09-10): this branch built the state,
                        // wrote the history row and dispatched the notification while logging
                        // NOTHING at any level, so seven alerts - instance_unreachable and
                        // machine_unreachable among them - fired invisibly in the service log. Same
                        // helper, same template, same level, same cardinality as the standard path.
                        LogAlertFired(alert, serverName, severity, value.Value);
                    }
                    else
                    {
                        existing.HitCount++;
                        existing.LastValue = value.Value;
                        existing.LastTriggered = DateTime.UtcNow;
                        // C3 (2026-08-05): this branch re-DISPATCHES, and it used to carry the
                        // FIRST fire's basis, threshold and sentence into the new notification.
                        // A re-fire that crossed Critical was paged out naming the Warning
                        // threshold from an hour earlier. The main evaluation path already
                        // refreshed these on re-fire; this one is now the same shape.
                        existing.Severity = severity;
                        existing.ThresholdValue = firingBasis.Threshold;
                        existing.BasisKind = firingBasis.Kind.ToString();
                        existing.Message = FormatMessage(alert, serverName, value.Value, firingBasis, thresholdTier);
                        var cooldown = alert.NextAlertDelayMinutes.HasValue
                            ? TimeSpan.FromMinutes(alert.NextAlertDelayMinutes.Value)
                            : _definitions.GetCooldown(alert);
                        if (!_lastNotified.TryGetValue(stateKey, out var lastNotify)
                            || (DateTime.UtcNow - lastNotify) >= cooldown)
                        {
                            if (!_dryRun) DispatchNotification(alert, existing);
                            _lastNotified[stateKey] = DateTime.UtcNow;
                        }
                        if (!_dryRun) _history.UpsertAlert(existing);
                    }

                    // special-alerts-escalation-parity (2026-09-11): THE fix. Placement is the fix
                    // as much as the call is - this sits OUTSIDE the new-vs-re-fire if/else and
                    // INSIDE the "it is breaching" arm, exactly where the standard path puts it, so
                    // it runs on EVERY firing cycle. A time-based escalation is false on the first
                    // cycle by construction and only becomes true on a later one; inside the
                    // first-fire arm this call would be dead for every alert on earth.
                    ApplyEscalationForFiringCycle(alert, stateKey, serverName);
                }
                else
                {
                    // MEASURED, and not breaching. THE EPISODE ENDS HERE - outside and BEFORE the
                    // "was there a state left to resolve" question (fix round, 2026-09-11).
                    //
                    // It used to sit inside the TryRemove arm, which is reached ONLY when a state
                    // SURVIVED to be resolved. After ResolveCleared reaps a still-breaching alert
                    // for want of measurement - which deliberately does NOT end the episode - there
                    // is no state left. A later, genuinely-clear measurement then removed nothing,
                    // ended nothing, and _escalatedEpisodes[stateKey] outlived the process: the
                    // next real outbreak had the OLD EscalatedAt restored by the guard, was forced
                    // to Critical, and NEVER PAGED AGAIN.
                    //
                    // The distinction Adrian ruled on is untouched: an episode ends on a
                    // MEASUREMENT that found the alert clear, and never on a reap. ResolveCleared
                    // still does not call this - reaching this line means a value was measured on
                    // this cycle and did not breach.
                    EndEscalationEpisode(stateKey);
                    EndBreachRun(stateKey);

                    if (_activeStates.TryRemove(stateKey, out var cleared))
                    {
                        cleared.Status = AlertStatus.Resolved;
                        cleared.ResolvedAt = DateTime.UtcNow;
                        if (!_dryRun) _history.ResolveAlert(alert.Id, serverName);
                        _lastNotified.TryRemove(stateKey, out _);
                        // The SAME defect wearing the other hat, and fixing only the fire would have
                        // made it worse: a log that says "Alert fired: Instance unreachable" and never
                        // says it cleared reads as a server still down. The standard path has said
                        // "Alert resolved" since it was written; this one never did.
                        LogAlertResolved(alert, serverName);
                    }
                }
            }
            catch (Exception ex)
            {
                // A fault APPLYING a measured value (history write, dispatch, threshold maths) is
                // still "we do not know this alert's state" — record it rather than swallow it.
                // Same episode discipline as the no-measurement arm: the type and message are named
                // at Warning once, then the repeat drops to Debug.
                var reason = DescribeEvaluationFailure(ex);
                if (RecordEvaluationFailure(stateKey, alert.Id, serverName, reason))
                    _logger.LogWarning(ex, "Special alert {AlertId} ({Mode}) failed applying its result on {Server}: {Reason}",
                        alert.Id, alert.QueryMode, LogAnon.S(serverName), reason);
                else
                    _logger.LogDebug(ex, "Special alert {AlertId} ({Mode}) is still failing to apply its result on {Server}: {Reason}",
                        alert.Id, alert.QueryMode, LogAnon.S(serverName), reason);
            }
        }

        /// <summary>
        /// The T-SQL <see cref="CheckPowerPlanAsync"/> runs, extracted as an internal static so a test
        /// can pin the fail-closed shape without a live server.
        ///
        /// <para><b>r2-01 fail-closed arm (2026-08-26).</b> <c>xp_instance_regread</c> does not throw
        /// when the registry key or value is absent — it prints an informational message
        /// (severity &lt;= 10, so no <c>SqlException</c>) and leaves <c>@scheme</c> NULL, and the batch
        /// runs on. Without the explicit <c>WHEN @scheme IS NULL THEN NULL</c> arm the CASE fell
        /// straight to <c>ELSE 2.0</c> and returned a fire, asserting "not High Performance" about a
        /// host whose plan was never read (and clearing any prior evaluation-failure record while doing
        /// it). The NULL arm makes that non-throwing failure surface as SQL NULL, which
        /// <see cref="CheckPowerPlanAsync"/>'s DBNull guard turns into a null return = evaluation
        /// failure = Unknown, never a fabricated Critical. Proved on <c>.\new2022</c>: an absent
        /// key/value returned scalar 2.0 without this arm and SQL NULL with it.</para>
        /// </summary>
        internal static string BuildPowerPlanCheckSql() => @"
                    DECLARE @scheme NVARCHAR(255);
                    EXEC master.dbo.xp_instance_regread
                        N'HKEY_LOCAL_MACHINE',
                        N'SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes',
                        N'ActivePowerScheme',
                        @scheme OUTPUT;
                    SELECT CASE WHEN @scheme IS NULL THEN NULL
                                WHEN LOWER(LTRIM(RTRIM(@scheme))) = N'8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'
                                     THEN 0.0 ELSE 2.0 END;";

        /// <summary>
        /// Reads the SQL Server host's active Windows power scheme through the registry and reports
        /// whether it is High Performance.
        ///
        /// <para><b>r2-01 (ruling 2026-08-25 06:53 #3): this alert now has a real handler.</b>
        /// <c>windows_power_plan</c> ships enabled with <c>queryMode: registry_check</c>, but no case
        /// existed for it — it fell to <c>_ =&gt; null</c> and read as a correct power plan on every
        /// server, forever. Its own shipped <c>query</c> could not have rescued it either: it named
        /// <c>...\Control\PowerUser\PowerSchemes</c>, and the real key is
        /// <c>...\Control\Power\User\PowerSchemes</c>. This reads the correct key with
        /// <c>xp_instance_regread</c> over the existing connection — so it measures the monitored SQL
        /// server's own power plan, which is the machine whose plan matters — and compares the active
        /// scheme GUID to High Performance (<c>8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c</c>).</para>
        ///
        /// <para>Returns 0 when High Performance is active (healthy) and 2 when it is not — above the
        /// shipped <c>warning: 1</c> fire threshold. A read that fails returns null, which the caller
        /// records as an evaluation failure rather than a clean Ok: an unreadable power plan is
        /// unknown, not high-performance. Two failure shapes reach that null: a thrown exception (the
        /// proc is refused, the connection drops) is caught below; a non-throwing failure (the key or
        /// value is absent — <c>xp_instance_regread</c> only prints an informational message and
        /// leaves <c>@scheme</c> NULL) is turned into SQL NULL by the query's own
        /// <c>WHEN @scheme IS NULL THEN NULL</c> arm. Without that arm the CASE fell to <c>ELSE 2.0</c>
        /// and fired a false Critical about a host whose plan was never read — see
        /// <see cref="BuildPowerPlanCheckSql"/>.</para>
        /// </summary>
        private async Task<SpecialHandlerOutcome> CheckPowerPlanAsync(ServerConnection connection, string serverName)
        {
            try
            {
                var connStr = connection.GetConnectionString(serverName, "master");
                var sql = BuildPowerPlanCheckSql();
                var (sqlConn, pooled) = await RentConnectionAsync(connStr);
                try
                {
                    using var cmd = new SqlCommand(SqlSessionSafety.DefaultPrefix + sql, (SqlConnection)sqlConn) { CommandTimeout = 10 };
                    var result = await cmd.ExecuteScalarAsync();
                    // An unreadable plan surfaces as SQL NULL (the query's WHEN @scheme IS NULL arm),
                    // which ExecuteScalar returns as DBNull — "we could not read the plan", not "High
                    // Performance". Fail closed to unknown so the NOC does not read a healthy Ok.
                    if (result == null || result == DBNull.Value) return SpecialHandlerOutcome.NotMeasured();
                    return SpecialHandlerOutcome.Measured(Convert.ToDouble(result));
                }
                finally { ReturnOrDispose(sqlConn, connStr, pooled); }
            }
            catch (Exception ex)
            {
                // Debug here is the PER-CYCLE repeat. The caller names this exception at Warning once
                // per episode (logon-failure-unmeasurable), so the cause is readable without Debug.
                _logger.LogDebug(ex, "Power plan registry check failed on {Server}", LogAnon.S(serverName));
                return SpecialHandlerOutcome.Failed(ex);
            }
        }

        // Helper: get a connection — from pool if available, else direct.
        // Caller must call ReturnOrDispose when done.
        private async Task<(System.Data.IDbConnection Conn, bool Pooled)> RentConnectionAsync(string connStr, CancellationToken ct = default)
        {
            if (_pool != null)
                return (await _pool.GetConnectionAsync(connStr, ct), true);
            var c = new SqlConnection(connStr);
            await c.OpenAsync(ct);
            return (c, false);
        }

        private void ReturnOrDispose(System.Data.IDbConnection conn, string connStr, bool pooled)
        {
            if (pooled && _pool != null)
                _pool.ReturnConnection(conn, connStr);
            else
                try { conn.Dispose(); } catch { /* best effort */ }
        }

        private async Task<SpecialHandlerOutcome> CheckConnectivityAsync(ServerConnection connection, string serverName)
        {
            try
            {
                var connStr = connection.GetConnectionString(serverName, "master");
                var (sqlConn, pooled) = await RentConnectionAsync(connStr);
                try
                {
                    using var cmd = new SqlCommand(SqlSessionSafety.DefaultPrefix + "SELECT 1", (SqlConnection)sqlConn) { CommandTimeout = 5 };
                    await cmd.ExecuteScalarAsync();
                    return SpecialHandlerOutcome.Measured(0); // 0 = reachable (below threshold of 1 = unreachable)
                }
                finally { ReturnOrDispose(sqlConn, connStr, pooled); }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Connectivity check failed for {Server} — treating as unreachable", LogAnon.S(serverName));
                // THE VALUE IS UNCHANGED AND MUST STAY UNCHANGED: for this handler a refused connection
                // IS the measurement (1 = unreachable), and that reading is what makes
                // instance_unreachable / machine_unreachable fire. The exception rides beside it purely
                // so a caller could name it; because the value is non-null the caller records no
                // failure at all, so nothing about this alert's behaviour moves.
                return new SpecialHandlerOutcome(1, ex);
            }
        }

        /// <summary>
        /// THE ONE ROUTING PREDICATE. True when the evaluator hands this alert to a built-in handler
        /// (<see cref="EvaluateSpecialAlertAsync"/>) and its <c>query</c> field is text nobody
        /// executes. Every site that must agree with the evaluator about that calls this, rather than
        /// restating the test: the dispatch decision in the evaluation loop,
        /// <see cref="QueryValueShape"/> (and through it <see cref="SmallestAlertableValue"/> and the
        /// alert editor's threshold hint), and <c>AlertBaselineService.SeedableAlerts</c>, the
        /// baseline seeder's filter, which used to run the inert text of any alert with
        /// <c>canBaseline</c> set and would have learned fences over the constant 1 once
        /// <c>sql_response_time</c> moved to a handler (lane Q14, 2026-09-18). Guarded by
        /// SpecialAlertPathNeverFiresTests.The_baseline_seeder_never_runs_a_handler_routed_alerts_inert_query.
        ///
        /// <para>The semantics are the evaluator's own, unchanged: a null or empty mode, or exactly
        /// <c>standard</c>, runs the query field; anything else is routed.</para>
        /// </summary>
        internal static bool IsRoutedToBuiltInHandler(AlertDefinition alert) =>
            !string.IsNullOrEmpty(alert.QueryMode) && alert.QueryMode != "standard";

        /// <summary>
        /// <c>response_time_probe</c>: how long this app waits for a <c>SELECT 1</c> round trip,
        /// in milliseconds, TIMED IN THE APP (owner's ruling R1, DECISIONS 2026-09-18 01:40).
        ///
        /// <para><b>What was wrong.</b> <c>sql_response_time</c> shipped the standard query
        /// <c>DECLARE @t DATETIME = GETDATE(); SELECT 1; SELECT DATEDIFF(MILLISECOND, @t, GETDATE()) AS value</c>.
        /// <see cref="ExecuteAlertQueryAsync"/> reads the FIRST result set, which is the constant 1,
        /// against thresholds of 1000 and 2000 ms, so the alert could never fire. PROVED live on
        /// <c>.\NEW2022</c> and <c>.\OLD2017</c> (scout wf_ee2a97c4-5b3, 2026-09-17). A server-side
        /// timing in one result set was rejected by the ruling: it reads 0-1 ms, because the server
        /// never waits on itself.</para>
        ///
        /// <para><b>What it measures.</b> The connection comes over the SAME path a standard alert query
        /// takes: the same connection string, <see cref="RentConnectionAsync"/> (the pool when there is
        /// one), the same <see cref="SqlSessionSafety.DefaultPrefix"/> and the same 15 second command
        /// timeout. The clock starts only once that connection is IN HAND, immediately before
        /// <c>ExecuteScalarAsync</c>, and stops when it completes. So the number is the round trip:
        /// the network both ways and the server finding a worker to run the batch.</para>
        ///
        /// <para><b>INVARIANT: NO WAIT FOR A SQLTRIAGE CONNECTION SLOT, AND NO LOGIN, IS IN THE
        /// NUMBER.</b> The first version started the clock before the rent.
        /// <see cref="SqlConnectionPoolService.GetConnectionAsync"/> blocks for up to 30 s when the
        /// per-server cap is reached, and that pool is shared with check execution, diagnostics and wait
        /// stats, so a busy SQLTriage made a healthy server read slow. PROVED live by the Q14 gate
        /// (2026-09-18) on <c>.\NEW2022</c>: 13 held pooled connections gave a 2,604 ms reading and a
        /// Critical at the shipped thresholds, against 2.8 ms after release. Login time is outside the
        /// clock for the same reason: whether a rent logs in at all depends on the pool's state, not the
        /// server's. A rent that never gets a connection throws, which is the failure outcome below,
        /// never a reading. Guarded by
        /// SpecialAlertPathNeverFiresTests.Sql_response_time_does_not_count_a_wait_for_the_apps_own_pool_on_a_live_instance.</para>
        ///
        /// <para><b>WHAT IS STILL IN THE NUMBER: SQLTriage's own scheduling.</b> The clock runs inside
        /// this process, so a starved thread pool delays the continuation after
        /// <c>ExecuteScalarAsync</c> and that delay reads as latency. No timing taken in the app can
        /// exclude it. PROVED by Q14 gate 2 (2026-09-18, probe X1b): with the app's thread pool flooded
        /// and no load on a healthy <c>.\NEW2022</c>, one cycle read 1,418.6 ms and went Warning, and on
        /// a second build 2,117.99 ms and went Critical. So the shipped alert carries
        /// <c>holdSeconds: 120</c> (<see cref="AlertDefinition.HoldSeconds"/>, ruling 2 of DECISIONS
        /// 2026-09-18 04:21): one stalled cycle opens no episode, and its description says a stalled
        /// SQLTriage process reads slow too.</para>
        ///
        /// <para><b>A probe that fails is NOT a latency.</b> Any exception, a timeout included,
        /// returns <see cref="SpecialHandlerOutcome.Failed"/>: no value, so the caller records an
        /// evaluation failure and the alert reads Unknown. It never fabricates a large reading and
        /// never fires a latency alert from an exception; reachability is the connectivity alerts'
        /// job. Guarded by
        /// SpecialAlertPathNeverFiresTests.Sql_response_time_against_a_dead_endpoint_is_unknown_and_never_a_latency.</para>
        /// </summary>
        private async Task<SpecialHandlerOutcome> MeasureResponseTimeAsync(ServerConnection connection, string serverName)
        {
            try
            {
                var connStr = connection.GetConnectionString(serverName, "master");
                var (sqlConn, pooled) = await RentConnectionAsync(connStr);
                try
                {
                    using var cmd = new SqlCommand(SqlSessionSafety.DefaultPrefix + "SELECT 1", (SqlConnection)sqlConn) { CommandTimeout = 15 };
                    // After the rent, never before it: see the invariant in the summary above.
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    await cmd.ExecuteScalarAsync();
                    clock.Stop();
                    return SpecialHandlerOutcome.Measured(clock.Elapsed.TotalMilliseconds);
                }
                finally { ReturnOrDispose(sqlConn, connStr, pooled); }
            }
            catch (Exception ex)
            {
                // Debug is the per-cycle repeat; the caller names this exception at Warning once per
                // episode, exactly as it does for the other measuring handlers.
                _logger.LogDebug(ex, "Response time probe failed on {Server}", LogAnon.S(serverName));
                return SpecialHandlerOutcome.Failed(ex);
            }
        }

        /// <summary>
        /// The T-SQL each <c>error_log_scan</c> mode actually runs, extracted as an internal static
        /// so a test can assert the executed text against a live server or without one.
        ///
        /// <para><b>r1-01 (2026-08-26).</b> The executed <c>error_log_severity</c> query applied no
        /// severity filter at all — a bare <c>NOT LIKE '%Backup%'</c> that counted every
        /// non-backup line in the window, while the catalogue showed, and the Definitions dialog
        /// let an admin edit, a <c>Severity &gt;= 17</c> filter that nothing ran. Proved on
        /// <c>.\new2022</c>: over a 9-day capture the executed query returned 61,217 rows where the
        /// sev&gt;=17 filter returns 1,139, and 32,727 of those counted rows carry no
        /// <c>Severity:</c> string at all. The executed text now matches the shipped catalogue
        /// query: pre-filter <c>xp_readerrorlog</c> to <c>Severity:</c> lines and count only
        /// <c>Severity: 17..29</c> (SQL Server tops out at 25). <c>error_log_fatal</c> and
        /// <c>logon_failure</c> are left exactly as they were — a separate finding owns them.</para>
        ///
        /// <para><b>logon-failure-root-cause (2026-09-09). Every string argument here must be
        /// nvarchar.</b> <c>logon_failure</c> passed the bare literal <c>'Login failed'</c> and was
        /// unmeasurable on all three monitored instances, every cycle, from the 2026-09-08 deploy.
        /// <c>xp_readerrorlog</c> is an ODS extended procedure that validates its search-string
        /// parameter's TDS type and rejects varchar with <c>Msg 22004, Level 12: "Error executing
        /// extended stored procedure: Invalid Parameter Type"</c> — PROVED on <c>.\OLD2017</c> and
        /// <c>.\NEW2022</c> for a bare literal, a VARCHAR variable and a SqlDbType.VarChar parameter
        /// alike. That message is raised onto the TDS stream, not through the engine: an Extended
        /// Events capture of <c>error_reported</c> at severity &gt;= 11 saw NOTHING and the ERRORLOG
        /// is silent. Wrapped in <c>INSERT INTO @r EXEC</c> the error token is swallowed by the
        /// INSERT and only the DONE server-error bit escapes, which SqlClient reports as
        /// <c>SqlException</c> number 0 / class 11 — "A severe error occurred on the current
        /// command" — naming nothing. sqlcmd (ODBC) ignores that bit entirely and returns
        /// <c>Matches = 0</c>, a silent FALSE ZERO, which is why six sqlcmd runs could not reproduce
        /// it. The 2x2 isolates the cause: bare <c>'Severity:'</c> FAILS, <c>N'Login failed'</c>
        /// SUCCEEDS — the literal's type, not which lines match. The two siblings survived only
        /// because they pass <c>N'Severity:'</c> and <c>NULL</c>.</para>
        /// </summary>
        internal static string? BuildErrorLogScanSql(string alertId) => alertId switch
        {
            // Pre-filter to lines containing "Severity:", then keep 17-19 and 2x (SQL Server
            // severity never exceeds 25). xp_readerrorlog params 5 & 6 must be datetime variables,
            // not inline expressions.
            "error_log_severity" => "DECLARE @f DATETIME = DATEADD(MINUTE,-5,GETDATE()), @t DATETIME = GETDATE(); DECLARE @r TABLE(LogDate DATETIME, ProcessInfo NVARCHAR(50), [Text] NVARCHAR(MAX)); INSERT INTO @r EXEC xp_readerrorlog 0, 1, N'Severity:', NULL, @f, @t; SELECT COUNT(*) FROM @r WHERE [Text] LIKE '%Severity: 1[7-9]%' OR [Text] LIKE '%Severity: 2%'",
            "error_log_fatal" => "DECLARE @f DATETIME = DATEADD(MINUTE,-5,GETDATE()), @t DATETIME = GETDATE(); DECLARE @r TABLE(LogDate DATETIME, ProcessInfo NVARCHAR(50), [Text] NVARCHAR(MAX)); INSERT INTO @r EXEC xp_readerrorlog 0, 1, NULL, NULL, @f, @t; SELECT COUNT(*) FROM @r WHERE [Text] LIKE '%Fatal%' OR [Text] LIKE '%severity 2[0-5]%'",
            "logon_failure" => "DECLARE @f DATETIME = DATEADD(MINUTE,-5,GETDATE()), @t DATETIME = GETDATE(); DECLARE @r TABLE(LogDate DATETIME, ProcessInfo NVARCHAR(50), [Text] NVARCHAR(MAX)); INSERT INTO @r EXEC xp_readerrorlog 0, 1, N'Login failed', NULL, @f, @t; SELECT COUNT(*) FROM @r",
            _ => null
        };

        private async Task<SpecialHandlerOutcome> ScanErrorLogAsync(AlertDefinition alert, ServerConnection connection, string serverName)
        {
            try
            {
                var connStr = connection.GetConnectionString(serverName, "master");

                // Count matching errors in xp_readerrorlog output from the last 5 minutes.
                var sql = BuildErrorLogScanSql(alert.Id);

                // An id this mode does not map is a non-throwing failure: there is no exception to
                // name, and the caller's fallback sentence about the queryMode is the whole truth.
                if (sql == null) return SpecialHandlerOutcome.NotMeasured();

                var (sqlConn, pooled) = await RentConnectionAsync(connStr);
                try
                {
                    using var cmd = new SqlCommand(SqlSessionSafety.DefaultPrefix + sql, (SqlConnection)sqlConn) { CommandTimeout = 15 };
                    var result = await cmd.ExecuteScalarAsync();
                    return SpecialHandlerOutcome.Measured(
                        result == null || result == DBNull.Value ? 0 : Convert.ToDouble(result));
                }
                finally { ReturnOrDispose(sqlConn, connStr, pooled); }
            }
            catch (Exception ex)
            {
                // THIS is the catch that hid the live logon_failure defect: it logged at Debug — which
                // production never emits — and returned a bare null, so the caller had no exception to
                // report and fell back to "queryMode 'error_log_scan' produced no measurement this
                // cycle" for 76 consecutive cycles on three instances. The exception now goes up.
                _logger.LogDebug(ex, "Error log scan failed for alert {AlertId} on {Server}: {Msg}", alert.Id, LogAnon.S(serverName), ex.Message);
                return SpecialHandlerOutcome.Failed(ex);
            }
        }

        /// <summary>
        /// The T-SQL <see cref="CheckIoErrorsAsync"/> runs, extracted as an internal static so a test
        /// can pin the measurement without a live server.
        ///
        /// <para><b>strings-r2-04b (2026-08-28).</b> This handler used to run
        /// <c>SELECT COUNT(*) FROM sys.dm_io_virtual_file_stats(NULL, NULL) WHERE io_stall_read_ms &gt;
        /// 5000 OR io_stall_write_ms &gt; 5000</c>, a count of files whose LIFETIME cumulative stall
        /// exceeds five seconds, which every server accumulates simply by running. Against the
        /// <c>io_error</c> definition's unit "event", operator greater_than and warning threshold 1,
        /// that made a Critical alert named "SQL Server has encountered an I/O error on a database
        /// file" fire continuously on healthy instances. PROVED on <c>.\new2022</c> 2026-08-28: the old
        /// query returned 14 of 33 files on an instance that had been up 96 minutes, while
        /// <c>msdb.dbo.suspect_pages</c>, the record SQL Server actually writes when it cannot read a
        /// page, held zero rows. A permanent Critical on a healthy server trains operators to ignore
        /// the alert, so it is worse than no alert.</para>
        ///
        /// <para>What it measures now: unrepaired suspect pages. SQL Server inserts a row here on
        /// error 823 or 824, and event types 1, 2 and 3 are the unresolved ones (I/O error, bad
        /// checksum, torn page); 4, 5 and 7 mean restored, repaired by DBCC, or deallocated and are
        /// deliberately excluded, so a page that has been fixed stops firing. PROVED on
        /// <c>.\new2022</c> in a rolled-back transaction: zero on the healthy instance, 1 with one
        /// event-type-2 row seeded, still 1 after adding an event-type-5 row (the repaired row does not
        /// count), and back to zero after rollback.</para>
        ///
        /// <para>BOUNDARY, stated rather than hidden: this does not see error 825, the transient
        /// read-retry that succeeded, because SQL Server records that in the error log and never in
        /// this table. The alert's description says so in the same words.</para>
        ///
        /// <para>This handler is reached only by an INSTALLED definition that still carries
        /// <c>queryMode: "io_error_check"</c>. The shipped definition no longer does: its honest SQL
        /// now lives in the config's own <c>query</c> field, where an operator editing it changes what
        /// runs. The handler is fixed all the same, because an install whose <c>io_error</c> was edited
        /// matches no superseded signature and is therefore never re-based.</para>
        /// </summary>
        internal static string BuildIoErrorCheckSql() =>
            "SELECT COUNT(*) FROM msdb.dbo.suspect_pages WHERE event_type IN (1, 2, 3)";

        private async Task<SpecialHandlerOutcome> CheckIoErrorsAsync(ServerConnection connection, string serverName)
        {
            try
            {
                var connStr = connection.GetConnectionString(serverName, "master");
                var (sqlConn, pooled) = await RentConnectionAsync(connStr);
                try
                {
                    using var cmd = new SqlCommand(SqlSessionSafety.DefaultPrefix + BuildIoErrorCheckSql(), (SqlConnection)sqlConn)
                    { CommandTimeout = 10 };
                    var result = await cmd.ExecuteScalarAsync();
                    return SpecialHandlerOutcome.Measured(
                        result == null || result == DBNull.Value ? 0 : Convert.ToDouble(result));
                }
                finally { ReturnOrDispose(sqlConn, connStr, pooled); }
            }
            catch (Exception ex)
            {
                // Reading msdb can fail on permissions alone. That is "we could not measure", which
                // the caller turns into Unknown - never a fabricated zero that would read as healthy.
                //
                // Demoted Warning -> Debug deliberately (logon-failure-unmeasurable): this ran on EVERY
                // cycle, so a permissions problem wrote one Warning every 30 seconds for as long as it
                // lasted. The caller now writes one Warning per EPISODE carrying this same exception's
                // type and message, so the operator gains the cause and loses the flood.
                _logger.LogDebug(ex, "I/O error check failed on {Server}", LogAnon.S(serverName));
                return SpecialHandlerOutcome.Failed(ex);
            }
        }

        private async Task<SpecialHandlerOutcome> CountDeadlocksAsync(ServerConnection connection, string serverName)
        {
            try
            {
                var connStr = connection.GetConnectionString(serverName, "master");
                var (sqlConn, pooled) = await RentConnectionAsync(connStr);

                // Count xml_deadlock_report events in the last 5 minutes from system_health ring buffer.
                // Pre-check: if target_data contains no 'xml_deadlock_report' string at all,
                // return 0 immediately without any XML parse — this is the fast path when there
                // are no deadlocks (the common case). Only cast to XML when deadlocks exist.
                const string sql = @"
                    IF NOT EXISTS (
                        SELECT 1 FROM sys.dm_xe_session_targets st WITH (NOLOCK)
                        JOIN sys.dm_xe_sessions s WITH (NOLOCK)
                          ON s.address = st.event_session_address
                        WHERE s.name = N'system_health'
                          AND st.target_name = N'ring_buffer'
                          AND CAST(target_data AS NVARCHAR(MAX)) LIKE N'%xml_deadlock_report%'
                    )
                    BEGIN SELECT 0; RETURN; END;

                    SELECT COUNT(*) FROM (
                        SELECT TOP 200 xed.value('(@timestamp)[1]', 'varchar(50)') AS ts
                        FROM (
                            SELECT CAST(target_data AS XML) AS td
                            FROM sys.dm_xe_session_targets st WITH (NOLOCK)
                            JOIN sys.dm_xe_sessions s WITH (NOLOCK)
                              ON s.address = st.event_session_address
                            WHERE s.name = N'system_health'
                              AND st.target_name = N'ring_buffer'
                        ) rb
                        CROSS APPLY rb.td.nodes(
                            'RingBufferTarget/event[@name=''xml_deadlock_report'']') xe(xed)
                    ) ev
                    WHERE TRY_CAST(ts AS datetimeoffset)
                          >= DATEADD(MINUTE, -5, SYSUTCDATETIME())";

                try
                {
                    using var cmd = new SqlCommand(SqlSessionSafety.DefaultPrefix + sql, (SqlConnection)sqlConn) { CommandTimeout = 30 };
                    var result = await cmd.ExecuteScalarAsync();
                    return SpecialHandlerOutcome.Measured(
                        result == null || result == DBNull.Value ? 0 : Convert.ToDouble(result));
                }
                finally { ReturnOrDispose(sqlConn, connStr, pooled); }
            }
            catch (Exception ex)
            {
                // Debug, not Warning, for the same reason as CheckIoErrorsAsync above: this is the
                // per-cycle repeat. The caller names this exception once per episode at Warning.
                _logger.LogDebug(ex, "Deadlock count check failed on {Server}", LogAnon.S(serverName));
                return SpecialHandlerOutcome.Failed(ex);
            }
        }

        private async Task EvaluateAlertOnServerAsync(
            AlertDefinition alert,
            ServerConnection connection,
            string serverName,
            AlertGlobalDefaults globalDefaults,
            ServerReachabilityProbe? reach = null)
        {
            var stateKey = StateKey(alert.Id, serverName);

            try
            {
                // Skip alerts tagged as unsupported on Azure SQL DB / Managed Instance
                if (alert.RequiresOnPrem && _health != null && _health.IsAzureSql(serverName))
                {
                    // Reachability stays Undetermined: nothing was attempted against the server.
                    _logger.LogDebug("Skipping alert {AlertId} on {Server} — not supported on Azure SQL", alert.Id, LogAnon.S(serverName));
                    return;
                }

                var raw = await ExecuteAlertQueryAsync(alert, connection, serverName);

                // A query completed — this is the one place in this method that proves the server is
                // up and answering, and it is the ONLY thing entitled to close the circuit.
                reach?.MarkReached();

                // THE MEASUREMENT CLOCK, stamped HERE: the first point at which the standard query has
                // COMPLETED without throwing, and ABOVE the value/no-value split below, so both of
                // those routes carry it (owner's ruling 2026-09-17, "a completed query counts"). Every
                // route that does not reach this line stamps nothing: the RequiresOnPrem skip above,
                // and a throw, timeout or cancellation inside ExecuteAlertQueryAsync, which lands in
                // the catches below. An open breaker never enqueues this method at all.
                //
                // An ACCEPTED consequence of that ruling: a result emptied by PERMISSIONS also
                // completes. A login without VIEW SERVER STATE sees only its own requests, so
                // blocking_process returns NULL whether or not blocking exists, and that NULL refreshes
                // this clock like any other.
                //
                // The value path stamps AGAIN, in ApplyObservedValueAsync. That double write is
                // intended and harmless (the same instant, give or take the apply): that method is a
                // firing path in its own right, also reached directly through ObserveAndApplyAsync, and
                // AlertStampKeyCensusTests.Axis4_everyFiringPathWritesTheMeasurementClock requires
                // every firing path to reach RecordServerEvaluation itself.
                RecordServerEvaluation(alert.Id, serverName, DateTime.UtcNow);

                ClearEvaluationFailure(stateKey); // query succeeded — clear any prior failure record

                if (raw == null)
                {
                    // The query COMPLETED and returned no value: no rows, or a NULL scalar. That is
                    // NOT zero and NOT a reading within threshold, so nothing is compared and no alert
                    // state changes - not its value, not its hit count, not the fire or clear decision
                    // (strings-r2-05: the windowed latency alerts reach this path legitimately whenever
                    // no I/O completed in the window). The reason is said out loud, because an absence
                    // that states no reason is indistinguishable from an alert that measured a clean
                    // result.
                    //
                    // But the server ANSWERED, and the auto-clear clock was stamped above, by the
                    // owner's ruling of 2026-09-17: "a completed query counts". Shipped alerts return
                    // NULL precisely when the server is healthy - blocking_process and
                    // agent_job_long_running take a MAX over an empty set - so an alert that fired and
                    // then went quiet arrives here on every healthy cycle. While only a value stamped,
                    // such an alert could never auto-resolve; a cold gate proved that live. Debug, as
                    // before, because this is the normal cycle of every such alert.
                    _logger.LogDebug(
                        "Alert {AlertId} on {Server}: its query completed and returned no value (no rows, or a NULL scalar). No comparison was made and the alert's state is unchanged, and this is not evidence the server is healthy against this alert's thresholds. The server answered, so the auto-clear clock was refreshed (ruling 2026-09-17: a completed query counts)",
                        alert.Id, LogAnon.S(serverName));
                    BreakBreachRun(stateKey);   // no value: a breach in progress is no longer known to last
                    return;
                }

                await ObserveAndApplyAsync(alert, serverName, raw.Value, DateTime.UtcNow);

            }
            catch (Microsoft.Data.SqlClient.SqlException sqlEx)
            {
                // Connection failures and SQL errors are expected (server offline, AG not configured, etc.)
                // Log at Debug to avoid spamming the log on every evaluation cycle — but still record the
                // failure so AlertsNoc can render this server as Unknown instead of silently Ok (#68 LEG 2).
                // This handler deliberately does NOT rethrow; the breaker is told via `reach` instead.
                // logon-failure-unmeasurable (2026-09-09): the STANDARD path had the same readability
                // hole as the special path — Debug per cycle, and a durable reason with no exception
                // type. It now names the cause at Warning once per episode and repeats at Debug.
                //
                // Invariant B (2026-09-19): this catch used to write Unreachable for EVERY SqlException,
                // so a healthy server that REFUSED a login without VIEW SERVER STATE (42 of 65 standard
                // alerts on NEW2022) counted three refusals as three breaker failures and was backed
                // off. The probe now carries the exception and the shared classifier decides: refused
                // or answered-with-a-THROW is Reached; transport, login and command-timeout errors stay
                // Unreachable. The failure record below is unconditional either way, so a refused alert
                // still reads Unknown, never Ok.
                reach?.MarkFailed(sqlEx);
                var sqlReason = DescribeEvaluationFailure(sqlEx);
                if (RecordEvaluationFailure(stateKey, alert.Id, serverName, sqlReason))
                    _logger.LogWarning(sqlEx, "Alert {AlertId} can no longer be evaluated on {Server}: {Reason}. Its state reads Unknown, not Ok, until it measures again",
                        alert.Id, LogAnon.S(serverName), sqlReason);
                else
                    _logger.LogDebug(sqlEx, "Alert query failed (SQL) {AlertId} on {Server}: {Msg}", alert.Id, LogAnon.S(serverName), sqlEx.Message);
            }
            catch (Exception ex)
            {
                // Reachability stays whatever it already was. A non-SQL exception here is a fault in OUR
                // logic (threshold maths, notification dispatch, history write), not evidence about the
                // server — opening the circuit on it would suppress polling of a healthy instance.
                var reason = DescribeEvaluationFailure(ex);
                if (RecordEvaluationFailure(stateKey, alert.Id, serverName, reason))
                    _logger.LogWarning(ex, "Failed to evaluate alert {AlertId} on {Server}: {Reason}", alert.Id, LogAnon.S(serverName), reason);
                else
                    _logger.LogDebug(ex, "Alert {AlertId} is still failing to evaluate on {Server}: {Reason}", alert.Id, LogAnon.S(serverName), reason);
            }
        }

        /// <summary>
        /// Returns the average value of a cached time-series metric over the last 7 days,
        /// excluding the most recent hour (so today's spike doesn't inflate the baseline).
        /// Returns null if insufficient data.
        /// </summary>
        private async Task<double?> GetBaselineAverageAsync(string queryId, string serverName, string? seriesFilter)
        {
            try
            {
                var to = DateTime.UtcNow.AddHours(-1);
                var from = to.AddDays(-7);
                var points = await _cache.GetTimeSeriesAsync(queryId, serverName, from, to);
                if (points.Count < 10) return null; // not enough data for a meaningful baseline

                var filtered = string.IsNullOrEmpty(seriesFilter)
                    ? points
                    : points.Where(p => p.Series.Equals(seriesFilter, StringComparison.OrdinalIgnoreCase)).ToList();

                if (filtered.Count < 10) return null;
                return filtered.Average(p => p.Value);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Baseline lookup failed for queryId={QueryId} server={Server}", queryId, LogAnon.S(serverName));
                return null;
            }
        }

        /// <summary>
        /// The one place a number read off a server becomes an alert decision.
        ///
        /// <para>A cumulative counter is a RUNNING TOTAL, not the rate its thresholds describe,
        /// so it is differenced HERE - before the fixed thresholds, the learned fences, the trend
        /// signal, the baseline sampler, the history row and the notification can see anything.
        /// The raw total never becomes the alert's value. Four shipped alerts fired a permanent
        /// Critical after every service restart because it did (2026-08-22 18:17 live burst:
        /// 44,239,320,670 ms of since-startup wait compared to a 15,000 ms threshold).</para>
        ///
        /// <para>When no rate can be computed the cycle produces NO measurement: nothing is
        /// compared, no history row is written, no sample is fed to the baseline, and the reason
        /// is logged. The raw sample is still stored, because it is what the NEXT cycle differences
        /// against. (The auto-clear clock is a different matter: on the standard path the caller
        /// already stamped it when the query completed, because the server answered - owner's
        /// ruling 2026-09-17, "a completed query counts".)</para>
        ///
        /// <para>Internal, and the substitution lives at this call site rather than inside the
        /// decision method, so a test can drive the real path from a raw reading: removing the
        /// substitution here hands the running total straight to the comparison and the test that
        /// asserts a first sample does not fire goes red.</para>
        /// </summary>
        internal async Task ObserveAndApplyAsync(
            AlertDefinition alert, string serverName, double raw, DateTime nowUtc)
        {
            AlertObservation observed;
            if (alert.IsCumulativeCounter)
            {
                var previous = await _cache.GetLastRawSampleAsync(alert.Id, serverName);
                observed = ObserveValue(alert, raw, previous?.Raw, previous?.SampledAtUtc, nowUtc);

                // A store that cannot take this sample never gives the next cycle a previous one,
                // so the alert reports not-yet-measured forever: no history row, no active alert,
                // a UI that reads as healthy. Honest, and invisible unless it is said out loud.
                if (!await _cache.SaveLastRawSampleAsync(alert.Id, serverName, raw, nowUtc))
                    _logger.LogWarning(
                        "Alert {AlertId} on {Server} could not store its raw counter sample. Until the cache store is writable this alert can produce NO reading at all, so it cannot fire. It can still clear: its query completes, which refreshes the auto-clear clock, so an Active alert it raised earlier auto-resolves once it has gone three intervals without firing. Neither its silence nor that clear is evidence that the server is healthy",
                        alert.Id, LogAnon.S(serverName));
            }
            else
            {
                observed = new AlertObservation(raw, null);
            }

            if (observed.Value == null)
            {
                _logger.LogInformation(
                    "Alert {AlertId} on {Server} not measured this cycle: {Reason}. Its query returns a cumulative counter, so nothing was compared and no alert state changed",
                    alert.Id, LogAnon.S(serverName), observed.NotMeasuredReason);
                BreakBreachRun(StateKey(alert.Id, serverName));
                return;
            }

            await ApplyObservedValueAsync(alert, serverName, observed.Value.Value);
        }

        /// <summary>
        /// Everything an evaluation cycle does once it HAS a measurement: record the baseline
        /// sample, work out which of the four bases fired, and update state, history and
        /// notifications. <paramref name="value"/> is the quantity the alert's thresholds
        /// describe - for a cumulative alert that is the computed rate, never the raw counter.
        /// </summary>
        private async Task ApplyObservedValueAsync(AlertDefinition alert, string serverName, double value)
        {
            var stateKey = StateKey(alert.Id, serverName);

            // The firing step's own measurement stamp. On the shipped standard cycle this is the
            // SECOND write of the same clock: EvaluateAlertOnServerAsync already stamped it the moment
            // ExecuteAlertQueryAsync returned, a NULL included (owner's ruling 2026-09-17, "a
            // completed query counts"). It stays because this method is a firing path in its own
            // right - ObserveAndApplyAsync reaches it without that caller - and
            // AlertStampKeyCensusTests.Axis4_everyFiringPathWritesTheMeasurementClock requires every
            // firing path to reach RecordServerEvaluation. The double write is intended.
            RecordServerEvaluation(alert.Id, serverName, DateTime.UtcNow);

            // Record sample for IQR baseline (fire-and-forget, no latency impact)
            _baseline?.RecordSample(alert.Id, serverName, value);

            // -- Which of the four bases fired ---------------------------------------
            // 2026-08-05: the message printed alert.Thresholds regardless of what actually
            // fired, so a TREND-only fire on an alert with no fixed thresholds configured
            // rendered "above 0.0" -- a threshold that exists nowhere. The basis is now
            // carried out of the decision block and NAMED in the message.
            FiringBasis? basis = null;

            // 1. Fixed thresholds from the alert definition.
            var isWarning = IsThresholdBreached(value, alert.Thresholds.Warning, alert.Operator);
            var isCritical = alert.Thresholds.Critical.HasValue
                && IsThresholdBreached(value, alert.Thresholds.Critical, alert.Operator);
            if (isWarning || isCritical)
                basis = FiringBasis.Fixed(isCritical
                    ? alert.Thresholds.Critical!.Value
                    : alert.Thresholds.Warning!.Value);

            // 2. IQR dynamic baseline thresholds (override fixed thresholds when baseline is ready)
            if (!isWarning && !isCritical && _baseline != null && alert.CanBaseline)
            {
                var (bWarn, bCrit) = _baseline.GetThresholds(alert.Id, serverName);
                if (bWarn.HasValue)
                    isWarning = IsThresholdBreached(value, bWarn, alert.Operator);
                if (bCrit.HasValue)
                    isCritical = IsThresholdBreached(value, bCrit, alert.Operator);
                if (isWarning || isCritical)
                    basis = FiringBasis.Learned(isCritical ? bCrit!.Value : bWarn!.Value);
            }

            // 3. Trend anomaly: rising/falling slope over a rolling 72-hour window. This
            //    fires on a SHAPE, not on a level, so there is no threshold to print.
            if (!isWarning && !isCritical && _baseline != null && alert.CanBaseline)
            {
                var (trendWarn, trendCrit) = _baseline.GetTrendSignal(alert.Id, serverName);
                if (trendCrit) isCritical = true;
                else if (trendWarn) isWarning = true;
                if (isWarning || isCritical) basis = FiringBasis.Trend();
            }

            // Legacy baseline deviation (kept for backwards compat with existing alert configs)
            if (!isWarning && !isCritical
                && alert.BaselineDeviationPercent > 0
                && !string.IsNullOrEmpty(alert.BaselineQueryId))
            {
                var baseline = await GetBaselineAverageAsync(alert.BaselineQueryId, serverName, alert.BaselineSeries);
                if (baseline.HasValue && baseline.Value > 0)
                {
                    var deviationPct = ((value - baseline.Value) / baseline.Value) * 100.0;
                    if (deviationPct >= alert.BaselineDeviationPercent)
                    {
                        isWarning = true;
                        // 4. Legacy deviation: the breach is a PERCENTAGE off a measured
                        //    baseline average, not a level, so both numbers are carried.
                        basis = FiringBasis.Deviation(baseline.Value, deviationPct, alert.BaselineDeviationPercent);
                        _logger.LogDebug("Baseline deviation: {AlertId} on {Server} - current={V:N1}, baseline={B:N1}, deviation={D:N1}%",
                            alert.Id, LogAnon.S(serverName), value, baseline.Value, deviationPct);
                    }
                }
            }

            // Track hit times for escalation window
            var hits = _hitTimes.GetOrAdd(stateKey, _ => new Queue<DateTime>());

            if (isWarning || isCritical)
            {
                // The hold (AlertDefinition.HoldSeconds): a breach that has not lasted long enough opens
                // no episode, whichever basis breached. An alert that is already active updates below.
                if (!ObserveBreachForHold(alert, stateKey) && !_activeStates.ContainsKey(stateKey))
                {
                    LogBreachHeld(alert, serverName, value, stateKey);
                    return;
                }

                // Which numeric threshold actually fired, kept separate from the routed severity so
                // the message names the real threshold even when the declared-severity floor (r1-07)
                // lifts an alert to Critical on its warning threshold.
                var thresholdTier = isCritical ? "Critical" : "Warning";
                var severity = RuntimeSeverity(alert, isCritical);
                // Should be unreachable: every branch above that sets a flag also sets the
                // basis. Unknown() prints no threshold at all rather than inventing one.
                var firingBasis = basis ?? FiringBasis.Unknown();

                // The hit that feeds the escalation window is recorded by
                // ApplyEscalationForFiringCycle at the end of this block, alongside the decision
                // that reads it (special-alerts-escalation-parity, 2026-09-11). Enqueue still
                // happens before the decision; nothing between here and there reads _hitTimes.

                if (_activeStates.TryGetValue(stateKey, out var existing))
                {
                    // Already active — increment hit count, update value
                    existing.HitCount++;
                    existing.LastValue = value;
                    existing.LastTriggered = DateTime.UtcNow;
                    existing.Severity = severity;
                    existing.ThresholdValue = firingBasis.Threshold;
                    existing.BasisKind = firingBasis.Kind.ToString();
                    existing.Message = FormatMessage(alert, serverName, value, firingBasis, thresholdTier);

                    // Per-alert next-alert-delay override
                    var cooldown = alert.NextAlertDelayMinutes.HasValue
                        ? TimeSpan.FromMinutes(alert.NextAlertDelayMinutes.Value)
                        : _definitions.GetCooldown(alert);
                    if (!_lastNotified.TryGetValue(stateKey, out var lastNotify)
                        || (DateTime.UtcNow - lastNotify) >= cooldown)
                    {
                        if (!_dryRun) DispatchNotification(alert, existing);
                        _lastNotified[stateKey] = DateTime.UtcNow;
                    }

                    if (!_dryRun) _history.UpsertAlert(existing);
                }
                else
                {
                    // New alert
                    var state = new AlertState
                    {
                        AlertId = alert.Id,
                        AlertName = alert.Name,
                        ServerName = serverName,
                        Severity = severity,
                        Status = AlertStatus.Active,
                        LastValue = value,
                        ThresholdValue = firingBasis.Threshold,
                        BasisKind = firingBasis.Kind.ToString(),
                        HitCount = 1,
                        FirstTriggered = DateTime.UtcNow,
                        LastTriggered = DateTime.UtcNow,
                        Message = FormatMessage(alert, serverName, value, firingBasis, thresholdTier)
                    };
                    _activeStates[stateKey] = state;
                    if (!_dryRun) _history.UpsertAlert(state);

                    if (!_dryRun) DispatchNotification(alert, state);
                    _lastNotified[stateKey] = DateTime.UtcNow;

                    LogAlertFired(alert, serverName, severity, value);
                }

                // ── Escalation check ───────────────────────────────────
                // Was inline here until special-alerts-escalation-parity (2026-09-11). It is now the
                // shared callee both firing paths call, in the same position it occupied inline.
                ApplyEscalationForFiringCycle(alert, stateKey, serverName);
            }
            else
            {
                // Condition cleared — mark as resolved if was active.
                //
                // THE EPISODE ENDS HERE, outside and BEFORE the TryRemove (fix round, 2026-09-11),
                // for the reason spelt out on the special path: reaching this branch means a value
                // WAS measured this cycle and did not breach, and that ends the episode whether or
                // not a state survived ResolveCleared to be resolved. Inside the arm, a measurement
                // gap followed by a genuine clear left _escalatedEpisodes[stateKey] set forever and
                // the next outbreak could never page. ResolveCleared still does not call this, so
                // the reap-vs-measured distinction Adrian ruled on is unchanged.
                EndEscalationEpisode(stateKey);
                EndBreachRun(stateKey);

                if (_activeStates.TryRemove(stateKey, out var cleared))
                {
                    cleared.Status = AlertStatus.Resolved;
                    cleared.ResolvedAt = DateTime.UtcNow;
                    _history.ResolveAlert(alert.Id, serverName);
                    _lastNotified.TryRemove(stateKey, out _);
                    lock (hits) { hits.Clear(); }

                    LogAlertResolved(alert, serverName);
                }
            }
        }

        /// <summary>
        /// TEST SEAM (InternalsVisibleTo SQLTriage.Tests), the house pattern
        /// <c>ServerConfigScriptService.ConnectionFactoryOverride</c> uses. FOR TESTS ONLY: production
        /// never sets it, so it is null on every shipped path and <see cref="ExecuteAlertQueryAsync"/>
        /// builds its connection string, opens its real connection and runs its real command.
        ///
        /// <para><b>What it replaces: the raw scalar, and nothing else.</b> When set,
        /// <see cref="ExecuteAlertQueryAsync"/> consults it at the one point where the real path takes
        /// the object <c>ExecuteScalarAsync</c> returns, and uses what it yields as that object: a boxed
        /// number, <c>DBNull.Value</c> (a query that returned a NULL scalar), or null (a query that
        /// returned no rows). Whatever it throws escapes from that same point, as the real call's
        /// exception would. No connection string is built and no connection is opened. Everything
        /// after that point in <see cref="ExecuteAlertQueryAsync"/> runs for real: the null and DBNull
        /// mapping, <c>Convert.ToDouble</c>, and the finally, which then has no connection to return.
        /// So does everything around the method: the reachability verdict and the breaker in
        /// <see cref="ThrottledEvaluateAsync"/>, the measurement stamp,
        /// <see cref="ClearEvaluationFailure"/>, both catches and the whole value path.</para>
        ///
        /// <para><b>Why it yields the RAW object (fix round 2 review, 2026-09-17).</b> Its first shape
        /// returned an already-mapped reading from the top of the method, so no test that used it ran
        /// the mapping. That mapping is load-bearing: a reviewer measured the shipped blocking_process and
        /// agent_job_long_running queries returning DBNull from ExecuteScalar on a healthy server, on
        /// both local instances, and Convert.ToDouble of DBNull throws InvalidCastException. If the
        /// mapping tested for null alone, every healthy cycle of those alerts would land in the general
        /// catch as an evaluation failure and stamp nothing, and a seam above the mapping would keep
        /// every test green. The R1 test therefore answers DBNull.Value, and null as a second case.</para>
        ///
        /// <para><b>Why it exists (alert-stamp-key-per-server fix round 2, 2026-09-17).</b> Invariant
        /// I1 is about which ROUTES out of a standard query stamp the measurement clock - completed
        /// with a value, completed with NULL, threw - and a unit test cannot make a real server return
        /// NULL, or throw, on demand. Used by these tests in AlertStampKeyPerServerTests:
        /// R1_aStandardQueryThatCompletesWithNullStampsTheClock_soAStaleStateIsCleared,
        /// I1_aStandardQueryThatThrowsStampsNothing_soAStaleStateSurvives,
        /// I2_aServerRemovedFromMonitoringLeavesNoKeyInAnyPerServerCollection,
        /// I2_anEnabledServerThatWasNotMeasuredThisCycleIsNeverPruned and
        /// I2_anEmptyEnabledSetIsNotEvidenceThatAnythingWasRemoved.</para>
        /// </summary>
        internal Func<AlertDefinition, string, Task<object?>>? StandardQueryOverrideForTests { get; set; }

        /// <summary>
        /// Runs a standard alert's query and returns the value its thresholds are compared against.
        ///
        /// <para><b>INVARIANT: THE VALUE IS THE FIRST COLUMN OF THE FIRST ROW OF THE FIRST RESULT SET,
        /// AND NOTHING ELSE.</b> <c>ExecuteScalarAsync</c> never reads past the first result set, and
        /// nothing in this file calls <c>NextResult</c>. A query that returns an earlier result set -
        /// a stray <c>SELECT 1</c>, a <c>SELECT</c> of a variable, a bare <c>EXEC</c> - hands that
        /// earlier value to the thresholds, whatever its last statement computes.
        /// <c>sql_response_time</c> shipped exactly that until lane Q14 (2026-09-18) and could never
        /// fire. So every ENABLED shipped standard query must return at most one client-visible result
        /// set on any path, and at least one on some path, whose first column is the value:
        /// AlertQueryResultSetCensusTests walks each
        /// one with ScriptDom and goes red otherwise, and AlertQueryPropertyNameCensusTests refuses a
        /// <c>SERVERPROPERTY</c>-family name that nobody proved returns non-NULL (the
        /// <c>connection_count</c> half of the same lane). A new site that executes alert query text
        /// must read the same value this method reads, or say why it does not.</para>
        /// </summary>
        private async Task<double?> ExecuteAlertQueryAsync(
            AlertDefinition alert,
            ServerConnection connection,
            string serverName)
        {
            // The test seam, read once. It is null in production, so the else branch below is the
            // only one ever taken there. When a test sets it, it stands in for the SQL round trip
            // ALONE - no connection string, no connection, no command - and the object it yields
            // goes through the same mapping below as a real scalar. See StandardQueryOverrideForTests.
            var scalarOverrideForTests = StandardQueryOverrideForTests;
            string? connString = null;
            System.Data.IDbConnection? sqlConn = null;
            var pooled = false;
            try
            {
                object? result;
                if (scalarOverrideForTests != null)
                {
                    result = await scalarOverrideForTests(alert, serverName);
                }
                else
                {
                    connString = connection.GetConnectionString(serverName, "master");
                    (sqlConn, pooled) = await RentConnectionAsync(connString);
                    using var cmd = new SqlCommand(SqlSessionSafety.DefaultPrefix + alert.Query, (SqlConnection)sqlConn)
                    {
                        CommandTimeout = 15
                    };
                    result = await cmd.ExecuteScalarAsync();
                }

                if (result == null || result == DBNull.Value) return null;
                return Convert.ToDouble(result);
            }
            finally
            {
                if (sqlConn != null) ReturnOrDispose(sqlConn, connString!, pooled);
            }
        }

        /// <summary>
        /// What a single evaluation cycle actually observed. <see cref="Value"/> null means the
        /// cycle produced NO measurement - not zero, and never the raw counter total. Nothing
        /// downstream (fixed thresholds, learned fences, trend, history, notifications, the
        /// baseline sampler) may run on a null observation.
        /// </summary>
        internal readonly record struct AlertObservation(double? Value, string? NotMeasuredReason);

        /// <summary>
        /// How many seconds of counter movement one unit of this alert's threshold represents.
        /// Taken from the definition's own declared unit, so the number the operator set keeps
        /// the meaning the definition already gave it: "per_minute" thresholds are compared to a
        /// per-minute rate, everything else to a per-second rate. This reads the shipped unit; it
        /// does not retune a shipped threshold.
        /// </summary>
        internal static double RateWindowSeconds(AlertDefinition alert) =>
            string.Equals(alert.Unit, "per_minute", StringComparison.OrdinalIgnoreCase) ? 60.0 : 1.0;

        /// <summary>
        /// Turns a raw reading into the quantity this alert's thresholds describe.
        ///
        /// <para>For an ordinary alert the reading already IS that quantity and comes straight
        /// back. For a cumulative counter (see <see cref="AlertDefinition.IsCumulativeCounter"/>)
        /// the reading is a running total since the SQL Server service started, so the rate is
        /// the difference against the previous sample over the elapsed wall-clock time.</para>
        ///
        /// <para>Three cases produce no measurement rather than a number:</para>
        /// <list type="bullet">
        /// <item>no previous sample - the first cycle after a start, an upgrade, or a purge;</item>
        /// <item>the reading is BELOW the previous one, so the counter reset and the difference
        /// is meaningless. The reason line says that and no more. A SQL Server restart is the
        /// usual explanation but it is not the only one, and it is not what was observed: a
        /// multi-row scalar whose first row changes identity between samples produces the same
        /// signal, and claiming a restart there would be a specific false statement about the
        /// server in a client-facing log;</item>
        /// <item>no wall-clock time elapsed between the samples - dividing by it would be a
        /// fabricated rate (or an infinity).</item>
        /// </list>
        ///
        /// <para>Caveat, stated rather than hidden: these counters move in whole units, so over a
        /// very short interval the computed rate carries the counter's own quantisation error.
        /// The evaluator samples at the alert's frequency (60-300 s in the shipped definitions)
        /// and the baseline seeder at 15 s, so the shortest real interval is ~15 s.</para>
        /// </summary>
        internal static AlertObservation ObserveValue(
            AlertDefinition alert, double raw, double? prevRaw, DateTime? prevUtc, DateTime nowUtc)
        {
            if (!alert.IsCumulativeCounter) return new AlertObservation(raw, null);

            if (!prevRaw.HasValue || !prevUtc.HasValue)
                return new AlertObservation(null, "first sample since start - a rate needs two");

            if (raw < prevRaw.Value)
                return new AlertObservation(null, "counter reset - the counter is lower than the previous sample, which a SQL Server restart usually explains");

            var seconds = (nowUtc - prevUtc.Value).TotalSeconds;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
                return new AlertObservation(null, "no wall-clock time elapsed since the previous sample");

            return new AlertObservation((raw - prevRaw.Value) / seconds * RateWindowSeconds(alert), null);
        }

        internal static bool IsThresholdBreached(double value, double? threshold, string op)
        {
            if (!threshold.HasValue) return false;
            return op == "less_than"
                ? value < threshold.Value
                : value > threshold.Value;
        }

        /// <summary>
        /// The range of numbers an alert's own SQL can hand back, read off the query text. Used by the
        /// alert editor (to stop presenting a threshold band where there is no band) and by
        /// AlertNeverFiresTests (to refuse a catalogue whose thresholds no value can reach).
        /// </summary>
        internal enum AlertValueShape
        {
            /// <summary>Anything else. Nothing here can be said about the reachable range.</summary>
            Unknown,

            /// <summary>A yes/no: <c>CASE WHEN ... THEN 1 ELSE 0 END</c>. Only 0 and 1 are reachable.</summary>
            BooleanFlag,

            /// <summary>A cardinality: a <c>COUNT(*)</c>. Zero and one are both reachable.</summary>
            OccurrenceCount,

            /// <summary>A literal. Exactly one value is reachable, whatever the thresholds say.</summary>
            Constant,

            /// <summary>An elapsed time in the alert's unit: continuous, zero upwards, with no "one
            /// occurrence". Its thresholds are a band an author chose, so there is no smallest alertable
            /// value to test against (lane Q14, <c>response_time_probe</c>).</summary>
            Duration
        }

        /// <summary>
        /// WHY THIS EXISTS (strings-r2-06, 2026-08-28). <see cref="IsThresholdBreached"/> is STRICT:
        /// <c>value &gt; threshold</c>, never <c>&gt;=</c>. Combine that with a query that can only
        /// return 0 or 1 and a shipped <c>warning: 1</c>, and the alert is silent for ever - 1 is not
        /// greater than 1, and no number an operator can type helps except one below 1. Sixteen shipped
        /// alerts were in exactly that state, four of the enabled ones announcing conditions a DBA
        /// would page on. PROVED live on .\new2022: high_risk_linked_servers returned 1 - the condition
        /// present - and could not fire; implicit_column_conversions likewise; low_compression_success_
        /// rates shipped the literal <c>SELECT 0</c>.
        ///
        /// <para>A further fifteen counted real occurrences with the same <c>warning: 1</c>, so they
        /// needed TWO before saying anything while their own description named one ("A user database
        /// is not in ONLINE state", Critical). Same arithmetic, milder shape, same remedy.</para>
        ///
        /// <para>THIS IS A TEXT READING AND IT IS DELIBERATELY CONSERVATIVE. It answers Unknown for
        /// anything it does not recognise, so the lint that consumes it can only ever fail on a shape
        /// it positively identified. It is not a SQL parser and must not be used as a security or
        /// correctness boundary - it exists to make one specific, mechanical mistake impossible to
        /// re-introduce silently.</para>
        /// </summary>
        internal static AlertValueShape QueryValueShape(AlertDefinition alert)
        {
            if (alert is null) return AlertValueShape.Unknown;

            // INVARIANT: THIS ANSWERS FOR WHAT THE EVALUATOR RUNS (lane Q14 fix round, 2026-09-18). An
            // alert routed to a built-in handler never executes its query field, so reading that text
            // describes nothing. It did harm: the alert editor shows this reader's answer under the
            // threshold box, and the inert "SELECT 1 AS value" read as Constant, so instance_unreachable
            // and machine_unreachable (both Critical) told the operator no threshold could make them
            // fire and to turn them off. The handler's own range is the answer instead. Guarded by
            // AlertNeverFiresTests.The_shape_reader_answers_for_the_handler_on_every_routed_alert.
            if (IsRoutedToBuiltInHandler(alert)) return SpecialHandlerValueShape(alert.QueryMode);

            var sql = alert.Query;
            if (string.IsNullOrWhiteSpace(sql)) return AlertValueShape.Unknown;

            // Collapse runs of whitespace so the patterns below do not depend on formatting.
            var flat = System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ").Trim();

            // A literal: "SELECT 0", "SELECT 1 AS value", with or without a trailing comment.
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    flat, @"^SELECT\s+-?\d+(\.\d+)?\s*(AS\s+\[?\w+\]?\s*)?(;|--.*)?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return AlertValueShape.Constant;

            // A yes/no in EITHER direction. The first version of this reader matched the literal
            // substring "THEN 1 ELSE 0 END" and nothing else, which made it blind to the inverted
            // form - and one shipped alert uses it: failed_login_xevent_session answers
            // CASE WHEN EXISTS (...) THEN 0 ELSE 1 END, a boolean capped at 1, against warning 1.
            // The reader returned Unknown, SmallestAlertableValue returned null, and all four lints
            // skipped it, so the guard written to make this class impossible reported green with a
            // member of the class still in the file. Matching both directions is the whole fix.
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    flat, @"\bTHEN\s+(0\s+ELSE\s+1|1\s+ELSE\s+0)\s+END\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return AlertValueShape.BooleanFlag;

            if (System.Text.RegularExpressions.Regex.IsMatch(
                    flat, @"\bCOUNT\s*\(\s*\*\s*\)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return AlertValueShape.OccurrenceCount;

            return AlertValueShape.Unknown;
        }

        /// <summary>
        /// The smallest value that should make this alert speak, or null when nothing useful can be
        /// said about it.
        ///
        /// <para>A yes/no answers 1 when its condition holds, and that is the ONLY value it can ever
        /// hand back, so 1 is definitionally the smallest alertable one.</para>
        ///
        /// <para>A COUNT is only "one occurrence" when the number really is a cardinality, so the
        /// alert's declared unit is consulted as well as its query. <c>connection_count</c> is why:
        /// its SQL reads <c>CAST(COUNT(*) AS FLOAT) * 100.0 /</c> the configured user-connection limit,
        /// which the text reader sees as a count and which is in fact a PERCENTAGE with a deliberate
        /// band at 80. Treating 1 as its smallest alertable value would have called a correct alert
        /// broken. (Until lane Q14, 2026-09-18, that divisor was <c>SERVERPROPERTY('MaxConnections')</c>,
        /// which is not a property, so the value was NULL on every server and the alert could never
        /// fire; AlertQueryPropertyNameCensusTests now refuses a property name nobody proved live.)</para>
        /// </summary>
        /// <summary>
        /// The range a BUILT-IN handler can hand back, per <c>queryMode</c>. Read off the handlers in
        /// this file, each named beside its mode, so the lint can see the alerts whose query field is
        /// dead text.
        ///
        /// <para>WHY THIS EXISTS (2026-08-28, the fix round on this lane's own cluster 1). The first
        /// version of <see cref="SmallestAlertableValue"/> returned null for every queryMode alert,
        /// on the true observation that the query field is text nobody executes. But "we cannot read
        /// the shape off the query" is not "the alert has no shape" - the shape is in the HANDLER.
        /// Exempting the whole class by construction hid three members of the very defect the lint
        /// was written for: <c>instance_unreachable</c> and <c>machine_unreachable</c> (both Critical,
        /// both enabled) shipped warning 1 while <see cref="CheckConnectivityAsync"/> returns exactly
        /// 1 for an unreachable server, so 1 &gt; 1 was false and a down instance produced silence;
        /// and <c>deadlock</c> shipped warning 1 while <c>CountDeadlocksAsync</c> returns 1 for a
        /// single deadlock, under a description reading "One or more deadlocks detected". PROVED by
        /// execution, not read: <c>SpecialAlertPathNeverFiresTests</c> drives the shipped definitions
        /// through <see cref="EvaluateSpecialAlertAsync"/> against a dead endpoint.</para>
        ///
        /// <para><c>registry_check</c> is Unknown DELIBERATELY and is the reason this returns a shape
        /// rather than a bool. <see cref="CheckPowerPlanAsync"/> answers 0.0 healthy, 2.0 unhealthy and
        /// SQL NULL when the scheme cannot be read - a two-state code with a gap, not a 0/1 flag and
        /// not a cardinality. <c>windows_power_plan</c>'s warning of 1 sits in that gap on purpose, so
        /// calling it a BooleanFlag would make the "no threshold of exactly 1" lint cry wolf on a
        /// correct alert.</para>
        /// </summary>
        internal static AlertValueShape SpecialHandlerValueShape(string? queryMode) => queryMode switch
        {
            // CheckConnectivityAsync: return 0 on a successful SELECT 1, return 1 from the catch.
            "connectivity_check" => AlertValueShape.BooleanFlag,
            "host_connectivity_check" => AlertValueShape.BooleanFlag,

            // CountDeadlocksAsync / ScanErrorLogAsync / CheckIoErrorsAsync: a COUNT(*), zero upwards.
            "deadlock_count" => AlertValueShape.OccurrenceCount,
            "error_log_scan" => AlertValueShape.OccurrenceCount,
            "io_error_check" => AlertValueShape.OccurrenceCount,

            // MeasureResponseTimeAsync: elapsed milliseconds for a SELECT 1 round trip, timed in the app
            // on a connection already in hand.
            // Continuous and zero upwards; a failed probe is null, never a reading. A band, not a flag.
            "response_time_probe" => AlertValueShape.Duration,

            // CheckPowerPlanAsync: 0.0 / 2.0 / null. See the paragraph above.
            "registry_check" => AlertValueShape.Unknown,

            _ => AlertValueShape.Unknown
        };

        internal static double? SmallestAlertableValue(AlertDefinition alert)
        {
            if (alert is null) return null;

            // QueryValueShape answers for what the evaluator runs: for an alert routed by queryMode
            // (IsRoutedToBuiltInHandler) that is the handler's range, SpecialHandlerValueShape, never the
            // inert query field. One reader, so this lint and the alert editor's hint cannot disagree.
            return QueryValueShape(alert) switch
            {
                AlertValueShape.BooleanFlag => 1.0,
                AlertValueShape.OccurrenceCount when IsCardinalityUnit(alert.Unit) => 1.0,
                // A duration is a band: there is no single occurrence whose smallest value to test.
                AlertValueShape.Duration => null,
                _ => null
            };
        }

        private static bool IsCardinalityUnit(string? unit) =>
            string.Equals(unit, "count", StringComparison.OrdinalIgnoreCase)
            || string.Equals(unit, "event", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The severity a fired alert is routed and displayed at. The critical threshold escalates
        /// it to Critical, and — the r1-07 fix — the alert's own declared catalogue severity is a
        /// FLOOR beneath it: an alert the catalogue calls Critical never fires below Critical.
        ///
        /// <para><b>Why (2026-08-26).</b> Runtime severity was <c>isCritical ? "Critical" :
        /// "Warning"</c> from thresholds alone, and the per-alert Severity control persisted to
        /// <c>alert-definitions.json</c> was read by nothing. 34 of 74 enabled alerts carry only a
        /// warning threshold, so a shipped-Critical alert — <c>instance_unreachable</c>,
        /// <c>database_unavailable</c>, <c>machine_unreachable</c>, <c>ag_failover</c> — could never
        /// produce runtime Critical, and every severity-gated channel (PagerDuty, ServiceNow,
        /// WhatsApp default <c>MinimumSeverity: "critical"</c>) dropped it silently. Honouring the
        /// declared severity as a floor makes the control real and stops the silent drop; it fails
        /// safe (a severity is only ever raised, never lowered — see the third-state-downgrade
        /// lesson), so it cannot suppress delivery. Only an explicit "critical" declaration lifts the
        /// floor; reconciling the catalogue vocabulary with the 2-level runtime tier
        /// (Warning/Critical) beyond that is left to the owner.</para>
        ///
        /// <para><b>Correction (special-alerts-escalation-parity, 2026-09-11).</b> This paragraph
        /// used to name the catalogue vocabulary as "Info/Low/Medium/High/Critical". Measured against
        /// the shipped <c>Config/alert-definitions.json</c> that is wrong twice over: the 80 seeded
        /// alerts declare exactly two severities, <c>Warning</c> (66) and <c>Critical</c> (14), so the
        /// level 66 of them actually use was the one level the sentence omitted, and the four it did
        /// name are used by none. A reader sizing "5-level catalogue vs 2-level runtime" as a real gap
        /// was reading a gap that does not exist in the shipped data.</para>
        /// </summary>
        internal static string RuntimeSeverity(AlertDefinition alert, bool criticalThresholdCrossed)
        {
            var declaredCritical = string.Equals(alert.Severity, "critical", StringComparison.OrdinalIgnoreCase);
            return criticalThresholdCrossed || declaredCritical ? "Critical" : "Warning";
        }

        /// <summary>
        /// THE ONE PLACE the "Alert fired" line is written, for every path that fires an alert.
        ///
        /// <para><b>special-alerts-fire-silently (2026-09-10).</b> Until this lane there was a single
        /// inline emit, in <c>EvaluateAlertAsync</c>&#39;s new-alert branch, and the special/queryMode
        /// path in <c>EvaluateSpecialAlertAsync</c> had none - it upserted history and dispatched a
        /// notification in total log silence. Seven shipped alerts route through that path, four of them
        /// Critical (instance_unreachable, machine_unreachable, error_log_fatal, windows_power_plan), so
        /// an unreachable instance paged the operator and left NOTHING in the log a support engineer
        /// could read afterwards. It also cost this project a working conclusion: the lane that found it
        /// first read the log silence as "the alert does not fire", and was wrong.</para>
        ///
        /// <para><b>Why a helper rather than a second copy of the line.</b> A duplicated format string
        /// is how two paths drift, and a log PARSER must see one format for ever - the shape
        /// <c>Alert fired: {AlertName} on {Server} ({Severity}) - value: {Value}{DryRun}</c> is quoted
        /// in this repo&#39;s own test prose (AlertBaselineFenceDirectionTests) off real service output.
        /// Routing both paths through here also makes <see cref="LogAnon"/> non-optional: a future path
        /// cannot leak a real server name into the log by forgetting to anonymise. The parameters are
        /// the caller&#39;s own locals rather than the AlertState so this body is a VERBATIM move of the
        /// emit it replaced - template and argument list byte-compare against the pre-lane source.</para>
        ///
        /// <para><b>Cardinality is deliberate: FIRST FIRE ONLY.</b> Neither re-fire branch logs, on
        /// either path. That is the standard path&#39;s behaviour as written and this lane matches it
        /// rather than inventing: an alert re-evaluating every 60 s would otherwise write a Warning per
        /// cycle for the life of the incident. The line is written under dry run too, tagged [DRY RUN],
        /// because dry run suppresses the SIDE EFFECTS and not the account of them.</para>
        /// </summary>
        private void LogAlertFired(AlertDefinition alert, string serverName, string severity, double value)
        {
            _logger.LogWarning("Alert fired: {AlertName} on {Server} ({Severity}) - value: {Value}{DryRun}",
                alert.Name, LogAnon.S(serverName), severity, value,
                _dryRun ? " [DRY RUN]" : "");
        }

        /// <summary>
        /// The counterpart, and the same lane: the one place "Alert resolved" is written by an
        /// evaluation path. The special path cleared its state, resolved the history row and removed the
        /// notify stamp without a word, so an operator reading the log saw a fire that never ended.
        /// <para>NOT covered by this helper, and left deliberately: <see cref="ResolveCleared"/>, the
        /// stale-state sweeper, also resolves silently. It is silent for BOTH paths, so it is symmetric
        /// and outside this lane - recorded rather than fixed here.</para>
        /// </summary>
        private void LogAlertResolved(AlertDefinition alert, string serverName) =>
            _logger.LogInformation("Alert resolved: {AlertName} on {Server}", alert.Name, LogAnon.S(serverName));

        private void DispatchNotification(AlertDefinition alert, AlertState state)
        {
            // Toast notification — uses alert channel so flood muting applies
            _toast.ShowAlert($"{alert.Name} — {state.ServerName}",
                state.Message, critical: state.Severity == "Critical", duration: 6000);

            // Feed into existing AlertingService notification pipeline for email/Teams
            _ = DispatchAndSurfaceAsync(BuildFiringNotification(alert, state));
        }

        /// <summary>
        /// The notification a FIRST FIRE sends. Extracted 2026-08-23 so the plumb below is
        /// reachable from a test: deleting <c>HitCount = state.HitCount</c> was invisible to the
        /// whole suite while it lived inside a private void.
        /// </summary>
        internal static AlertNotification BuildFiringNotification(AlertDefinition alert, AlertState state)
            => new()
            {
                AlertName = alert.Name,
                Metric = alert.Id,
                CurrentValue = state.LastValue,
                ThresholdValue = state.ThresholdValue,
                BasisKind = state.BasisKind,
                // N-1 (2026-08-22): the real count, which this state has always held and never
                // passed on. The rendered email printed a hard-coded "1" before this line existed.
                HitCount = state.HitCount,
                Severity = state.Severity.ToLowerInvariant(),
                InstanceName = state.ServerName,
                Message = state.Message,
                TriggeredAt = state.LastTriggered,
                SendEmail = alert.SendEmail
            };

        /// <summary>
        /// THE ONE PLACE escalation is decided, for every path that fires an alert - and the one
        /// place the escalation-window hit is recorded, so the count and the decision that reads it
        /// cannot drift apart.
        ///
        /// <para><b>THE INVARIANT, and it is the whole lane:</b> <i>every evaluation path that can
        /// fire an alert must call this method, on every firing cycle.</i> Not on first fire - on
        /// EVERY firing cycle, because a time-based escalation is false by construction on the cycle
        /// that creates the state and can only become true on a later one. So the call belongs
        /// OUTSIDE the new-vs-re-fire branch and INSIDE the "it is breaching" arm, which is where
        /// both call sites put it (EvaluateSpecialAlertAsync, ApplyObservedValueAsync).</para>
        ///
        /// <para><b>The census that enforces it is real and is named here on purpose:</b>
        /// <c>EscalationParityCensusTests.EveryFiringPathEvaluatesEscalation</c> in
        /// <c>Tests/SQLTriage.Tests/EscalationParityCensusTests.cs</c>. It enumerates the firing
        /// paths FROM THE SOURCE - every method under Data/ that calls <c>LogAlertFired</c> or
        /// creates an <c>_activeStates</c> entry - and fails naming any that does not call this.
        /// Add a third firing path without a call to this method and that test goes red.</para>
        ///
        /// <para><b>special-alerts-escalation-parity (2026-09-11).</b> Until this lane the block
        /// below lived inline in <c>ApplyObservedValueAsync</c> and nowhere else, so the seven
        /// queryMode alerts - <c>instance_unreachable</c> and <c>machine_unreachable</c> among them -
        /// could be ticked "escalate" in the UI and never escalate. Two paths carrying parallel
        /// copies of one behaviour is how that defect was born, so this is a shared callee and not a
        /// second copy.</para>
        ///
        /// <para><b>Episode continuity (Adrian's ruling, 2026-09-10 21:15).</b> An alert escalates
        /// ONCE PER EPISODE. An episode ends when the alert is MEASURED and found not to be
        /// breaching (<see cref="EndEscalationEpisode"/>), never when <see cref="ResolveCleared"/>
        /// reaps it for want of measurement. Without that distinction a Critical alert on a host
        /// whose measurement fails - <c>windows_power_plan</c> reads a registry key every 3600 s -
        /// is reaped after 3x its frequency, rebuilt by the next successful read with
        /// <c>EscalatedAt = null</c>, and pages again. That route is PROVED, not assumed, by
        /// <c>SpecialAlertEscalationTests.EscalationSurvivesAMeasurementGapReap_oneEpisodeOnePage</c>.</para>
        ///
        /// <para><b>THE OTHER HALF OF THAT RULING, and where the fix round put it (2026-09-11).</b>
        /// The invariant is two-sided and BOTH sides must hold: an episode must NOT end on a reap,
        /// and it MUST end on a measured not-breaching cycle <b>whether or not an AlertState
        /// survived to be resolved</b>. The <c>EndEscalationEpisode</c> calls used to sit inside the
        /// <c>_activeStates.TryRemove</c> arm on both paths, which meant reap-then-clear ended
        /// nothing and this guard then suppressed EVERY future escalation for that alert, forever,
        /// while reporting the new outbreak as already-escalated and Critical. They now sit OUTSIDE
        /// that arm, at the top of the not-breaching branch on both paths. Pinned by
        /// <c>SpecialAlertEscalationTests.AReapThenAMeasuredClear_endsTheEpisode_standardPath</c>,
        /// <c>...specialPath</c>, and
        /// <c>...AfterAReapAndAMeasuredClear_theNewOutbreakDoesNotInheritTheOldEpisode</c>. Move a
        /// call back inside a TryRemove arm and those three go red.</para>
        /// </summary>
        private void ApplyEscalationForFiringCycle(AlertDefinition alert, string stateKey, string serverName)
        {
            // Record this hit for escalation window tracking. Trim to the longest escalation window
            // we care about so the queue cannot grow forever while an alert stays firing. Default
            // 60min cap is plenty. Unconditional, exactly as it was inline: dry run and a cleared
            // Escalate flag suppress the ESCALATION, never the accounting of the hits.
            var hits = _hitTimes.GetOrAdd(stateKey, _ => new Queue<DateTime>());
            var trimWindow = alert.EscalationWindowMinutes > 0
                ? alert.EscalationWindowMinutes
                : 60;
            var trimCutoff = DateTime.UtcNow.AddMinutes(-trimWindow);
            lock (hits)
            {
                hits.Enqueue(DateTime.UtcNow);
                while (hits.Count > 0 && hits.Peek() < trimCutoff) hits.Dequeue();
            }

            if (!alert.Escalate || _dryRun) return;
            if (!_activeStates.TryGetValue(stateKey, out var activeState)) return;

            // THE GUARD. This state may be a REBUILD of one ResolveCleared reaped while it was still
            // breaching, in which case EscalatedAt is null again and the gate below would happily
            // page a second time for the same unbroken episode. Restore what we already sent. The
            // severity is restored with it because that is what the escalation itself did (below);
            // leaving it at Warning would report an escalated alert as un-escalated to every
            // severity-gated channel.
            if (!activeState.IsEscalated
                && _escalatedEpisodes.TryGetValue(stateKey, out var alreadyEscalatedAt))
            {
                activeState.EscalatedAt = alreadyEscalatedAt;
                activeState.Severity = "Critical";
            }

            if (activeState.IsEscalated || activeState.Status == AlertStatus.Acknowledged) return;

            bool shouldEscalate;
            if (alert.EscalationThresholdEvents > 0 && alert.EscalationWindowMinutes > 0)
            {
                // Event-count based: N events within M minutes
                var windowStart = DateTime.UtcNow.AddMinutes(-alert.EscalationWindowMinutes);
                int recentHits;
                lock (hits) { recentHits = hits.Count(t => t >= windowStart); }
                shouldEscalate = recentHits >= alert.EscalationThresholdEvents;
            }
            else
            {
                // Time-based: unacknowledged for X minutes
                shouldEscalate = (DateTime.UtcNow - activeState.FirstTriggered).TotalMinutes >= alert.EscalationAfterMinutes;
            }

            if (shouldEscalate)
            {
                activeState.EscalatedAt = DateTime.UtcNow;
                activeState.Severity = "Critical"; // escalate severity
                _escalatedEpisodes[stateKey] = activeState.EscalatedAt.Value;
                DispatchEscalation(alert, activeState);
                _logger.LogWarning("Alert escalated: {AlertName} on {Server}", alert.Name, LogAnon.S(serverName));
            }
        }

        /// <summary>
        /// Ends an escalation episode: the next outbreak of this alert on this server may escalate
        /// again. The hit queue goes with it so a stale hit cannot count towards the next episode's
        /// event-count window.
        ///
        /// <para><b>THE INVARIANT, both halves (Adrian's ruling 2026-09-10 21:15; placement fixed
        /// 2026-09-11).</b> Call this from a MEASURED not-breaching cycle, from nowhere else, and
        /// from EVERY such cycle. <see cref="ResolveCleared"/> is the caller that must not exist -
        /// it reaps an alert that stopped being measured while still breaching. And every call site
        /// must sit OUTSIDE any <c>_activeStates.TryRemove</c> arm: a measured clear ends the
        /// episode even when the reaper already took the state, or the episode never ends and
        /// <see cref="ApplyEscalationForFiringCycle"/>'s guard silences the alert for the life of
        /// the process. Both call sites are the top of the not-breaching branch, one per firing
        /// path. Pinned by SpecialAlertEscalationTests' three reap-then-clear tests, named in full
        /// on <see cref="ApplyEscalationForFiringCycle"/>.</para>
        ///
        /// <para><b>The one other remover of these two entries (2026-09-17)</b> is
        /// <see cref="PruneUnmonitoredAlertKeys"/>, and it deliberately does not call this method. It
        /// runs only for a key whose alert or server has LEFT the enabled set: that is not an episode
        /// ending on a measurement, it is the end of monitoring, and nothing will ever measure the
        /// key again. A key that is still monitored is never touched by it.</para>
        /// </summary>
        private void EndEscalationEpisode(string stateKey)
        {
            _escalatedEpisodes.TryRemove(stateKey, out _);
            _hitTimes.TryRemove(stateKey, out _);
        }

        // ── The breach hold (AlertDefinition.HoldSeconds), lane Q14 fix round 2, 2026-09-18 ──────────

        /// <summary>One unbroken run of breaching measurements for one (alert, server).</summary>
        internal readonly record struct BreachRun(DateTime FirstUtc, DateTime LastUtc, bool Broken);

        /// <summary>The minimum frequency the log-scan alerts are clamped to (xp_readerrorlog is expensive).</summary>
        internal const int LogScanMinFrequencySeconds = 300;

        /// <summary>
        /// The frequency an alert is actually due at. ONE definition, read by the scheduler in
        /// <see cref="EvaluateAllAsync"/> and by the hold's gap rule, so the two cannot disagree about
        /// how often a measurement arrives.
        /// </summary>
        internal static int EffectiveFrequencySeconds(AlertDefinition alert) =>
            alert.Id is "error_log_severity" or "error_log_fatal" or "logon_failure"
                ? Math.Max(alert.FrequencySeconds, LogScanMinFrequencySeconds)
                : alert.FrequencySeconds;

        /// <summary>
        /// TEST SEAM (InternalsVisibleTo SQLTriage.Tests), read ONLY by the hold. Null in production,
        /// where the hold reads <see cref="DateTime.UtcNow"/>. A test sets it to step time across a hold
        /// without waiting for it; the live proof of the hold runs in real time.
        /// </summary>
        internal Func<DateTime>? BreachHoldClockForTests { get; set; }

        private DateTime BreachHoldNow() => BreachHoldClockForTests?.Invoke() ?? DateTime.UtcNow;

        /// <summary>
        /// The longest gap between two breaching measurements that still counts as ONE run: two due
        /// intervals plus one tick. The scheduler measures an alert no more often than once per
        /// max(effective frequency, tick) and can slip by a tick, so consecutive measurements of a
        /// running evaluator are at most frequency + 2 ticks apart, which is inside this bound: the
        /// rule does not stop a sustained breach that is measured every cycle from firing. A longer
        /// gap is a stretch with no measurement at all (a maintenance window, an open breaker, a cycle
        /// that overran badly), and nothing seen then can prove the value stayed high.
        /// </summary>
        internal double BreachRunMaxGapSeconds(AlertDefinition alert) =>
            2.0 * Math.Max(EffectiveFrequencySeconds(alert), _baseTickSeconds) + _baseTickSeconds;

        /// <summary>
        /// Called on EVERY breaching measurement, on the standard path and the built-in-handler path
        /// alike, before the fire decision. Extends this key's run, or starts a new one when there is
        /// none, it was broken, or the gap since its last breach exceeds
        /// <see cref="BreachRunMaxGapSeconds"/>. Returns true when the alert may open a new episode now:
        /// it has no hold, or the run has lasted at least <see cref="AlertDefinition.HoldSeconds"/>.
        ///
        /// <para>INVARIANT (see <see cref="AlertDefinition.HoldSeconds"/>): an alert with a hold opens an
        /// episode only on a breaching measurement taken at least HoldSeconds after the first breaching
        /// measurement of an unbroken run. The routes that end or break a run are
        /// <see cref="EndBreachRun"/> (a measured value that did not breach, both paths),
        /// <see cref="BreakBreachRun"/> (every attempt that produced no value: the shared
        /// <see cref="RecordEvaluationFailure"/>, a completed standard query that returned NULL, a
        /// cumulative first sample, and an orchestrator failure on either path) and the gap rule. Each
        /// route is driven by AlertBreachHoldTests.</para>
        /// </summary>
        private bool ObserveBreachForHold(AlertDefinition alert, string stateKey)
        {
            var now = BreachHoldNow();
            var maxGap = BreachRunMaxGapSeconds(alert);
            var run = _breachRuns.AddOrUpdate(
                stateKey,
                _ => new BreachRun(now, now, false),
                (_, previous) => previous.Broken
                                 || now < previous.LastUtc
                                 || (now - previous.LastUtc).TotalSeconds > maxGap
                    ? new BreachRun(now, now, false)
                    : previous with { LastUtc = now });

            return alert.HoldSeconds <= 0 || (now - run.FirstUtc).TotalSeconds >= alert.HoldSeconds;
        }

        /// <summary>A measured value that did not breach: the run is over.</summary>
        private void EndBreachRun(string stateKey) => _breachRuns.TryRemove(stateKey, out _);

        /// <summary>
        /// An attempt that produced no value: the run is BROKEN, so the next breach starts a new one.
        /// The key is kept, not removed, because a failed attempt keeps every per-server key it had
        /// (AlertStampKeyPerServerTests); only a measurement or the prune removes one.
        /// </summary>
        private void BreakBreachRun(string stateKey)
        {
            if (_breachRuns.TryGetValue(stateKey, out var run) && !run.Broken)
                _breachRuns.TryUpdate(stateKey, run with { Broken = true }, run);
        }

        private void LogBreachHeld(AlertDefinition alert, string serverName, double value, string stateKey)
        {
            var heldFor = _breachRuns.TryGetValue(stateKey, out var run) ? (BreachHoldNow() - run.FirstUtc).TotalSeconds : 0;
            _logger.LogDebug(
                "Alert {AlertId} on {Server} is over its threshold ({Value}) and has been for {HeldSeconds:N0} of the {HoldSeconds} seconds it must last, so it has not fired",
                alert.Id, LogAnon.S(serverName), value, heldFor, alert.HoldSeconds);
        }

        /// <summary>
        /// The ONE key shape for anything scoped to (alert, server). Every per-(alert, server)
        /// collection on this class is keyed through here, so the tuple, the separator and the
        /// casing cannot drift between a collection and the guard that reads it - which is exactly
        /// how alert-stamp-key-per-server was born. The dictionaries are additionally
        /// OrdinalIgnoreCase, so the ToLowerInvariant is belt and braces, not the guarantee.
        /// </summary>
        internal static string StateKey(string alertId, string serverName) =>
            $"{alertId}:{serverName}".ToLowerInvariant();

        /// <summary>
        /// Stamps THE SCHEDULER CLOCK: "this alert was due-checked at this instant". Per ALERT.
        ///
        /// <para><b>This is not a measurement and nothing may read it as one</b>
        /// (alert-stamp-key-per-server, 2026-09-15). It is written BEFORE the query, unconditionally,
        /// once per alert for all of that alert's servers at once. A cycle whose query timed out,
        /// was refused, or was skipped by an open breaker stamps this clock exactly like a cycle
        /// that succeeded. It answers one question - "is it time to ask again?" - and it is the
        /// input to nothing else.</para>
        ///
        /// <para>Before this lane this method wrote the clock <see cref="ResolveCleared"/> read, so
        /// an INTENTION to evaluate counted as a MEASUREMENT, and an alert-wide stamp counted as a
        /// per-server one. Both halves are fixed: the reaper now reads
        /// <see cref="RecordServerEvaluation"/>'s clock instead. Kept as a named method, and with
        /// its original signature, because it is a seam the shipped cycle uses and existing tests
        /// drive.</para>
        /// </summary>
        internal void RecordEvaluationAttempt(string alertId, DateTime whenUtc) =>
            _lastDueCheck[alertId] = whenUtc;

        /// <summary>
        /// Stamps THE MEASUREMENT CLOCK: "this alert got an ANSWER from this server at this instant".
        /// Per (alert, server) - the same tuple as the <c>_activeStates</c> entry it exists to guard,
        /// which is the invariant this lane restored.
        ///
        /// <para><b>THE RULE FOR CALLERS (invariant I1; owner's ruling 2026-09-17, "a completed query
        /// counts"):</b> stamp on EVERY route on which a query against this server COMPLETED without
        /// throwing - on the standard path whether it produced a value or not, a NULL scalar or no
        /// rows included - and on NO route on which it threw, timed out, was cancelled, was skipped
        /// (the RequiresOnPrem skip on Azure SQL, an open breaker) or never ran. Those are "the server
        /// did not answer", and the honest consequence of not stamping is that
        /// <see cref="ShouldAutoResolveAsCleared"/> declines to auto-resolve - the alert stays visible
        /// instead of silently clearing. That is the safe direction: a stale alert on the wall is a
        /// nuisance, a false all-clear is a missed outage.</para>
        ///
        /// <para><b>Why a NULL counts.</b> Shipped standard alerts return NULL precisely when the
        /// server is healthy - <c>blocking_process</c> and <c>agent_job_long_running</c> take a MAX
        /// over an empty set. While this clock was stamped only with a value in hand, such an alert,
        /// once fired, could never auto-resolve: every healthy cycle completed, stamped nothing, and
        /// the reaper declined for ever. A cold gate proved that regression live against the pre-lane
        /// engine, which reaped it after three intervals. A NULL is still no comparison and changes no
        /// state; it records only that the server answered.</para>
        ///
        /// <para><b>THE CALLERS - three sites, two paths.</b>
        /// <see cref="EvaluateAlertOnServerAsync"/>, the standard path, at the point
        /// <c>ExecuteAlertQueryAsync</c> has returned and above its value/no-value split.
        /// <see cref="ApplyObservedValueAsync"/>, the standard path's firing step, which stamps a
        /// second time on the value route - intended, because it is a firing path in its own right and
        /// axis 4 below requires it. <see cref="EvaluateSpecialAlertAsync"/>, the queryMode path, once
        /// its handler produced a value - for connectivity_check that includes a refused connection,
        /// because there the refusal IS the measurement (1 = unreachable). A special handler's null is
        /// NOT stamped, and is not a twin of the standard NULL: on that path a null means the handler
        /// could not measure, and it is recorded as an evaluation failure (fail closed). The two
        /// firing paths are the same two
        /// <see cref="ApplyEscalationForFiringCycle"/> names.</para>
        ///
        /// <para><b>A THIRD FIRING PATH MUST CALL THIS TOO, and that is GUARDED, not merely asked
        /// for.</b> <c>AlertStampKeyCensusTests.Axis4_everyFiringPathWritesTheMeasurementClock</c>
        /// decodes this class's IL, recognises a firing path by its call to <c>LogAlertFired</c> or
        /// <c>ApplyEscalationForFiringCycle</c>, and goes RED on any such method that never reaches
        /// this one. It reads CALL TOKENS, not source characters, so a wrapped line or a comment
        /// quoting the name cannot fool it either way. What it CANNOT see is which branch the call
        /// sits on, or whether the query had completed when it ran; those are behavioural and belong
        /// to <c>AlertStampKeyPerServerTests.H1_aCleanRunOnOneServerDoesNotRefreshTheFreshnessUsedToJudgeAnother</c>,
        /// <c>H2_anAttemptThatMeasuredNothingDoesNotCountAsAMeasurement</c>,
        /// <c>R1_aStandardQueryThatCompletesWithNullStampsTheClock_soAStaleStateIsCleared</c> and
        /// <c>I1_aStandardQueryThatThrowsStampsNothing_soAStaleStateSurvives</c>, which drive the real
        /// engine.</para>
        ///
        /// <para>The other axes of that census do NOT guard this sentence and never did. Axis 1
        /// notices a COLLECTION added without a scope; axis 2 a METHOD holding both scopes without
        /// saying why; axis 3 a scope DECLARED wrongly. A PATH that fires an alert and forgets to
        /// stamp is invisible to all three, which is why axis 4 exists.</para>
        /// </summary>
        internal void RecordServerEvaluation(string alertId, string serverName, DateTime whenUtc) =>
            _lastServerEvaluation[StateKey(alertId, serverName)] = whenUtc;

        /// <summary>
        /// The last escalation dispatch, kept so a test can AWAIT and READ what production
        /// deliberately discards. Production behaviour is unchanged - nothing on the evaluation
        /// thread awaits this - but "the engine passed the operator's channel pick to the
        /// dispatcher" is otherwise unobservable WITHOUT A RACE, because the FIRING notification on
        /// the same cycle is fire-and-forget too and its requests interleave with the escalation's.
        /// Counting sockets cannot separate them; this can. That wiring is the entire risk of the
        /// EscalationChannel change, so it gets an exact instrument rather than an approximate one.
        /// Only ever overwritten by the next escalation.
        /// </summary>
        internal Task<IReadOnlyList<ChannelDispatchResult>> LastEscalationDispatch { get; private set; }
            = Task.FromResult<IReadOnlyList<ChannelDispatchResult>>(Array.Empty<ChannelDispatchResult>());

        /// <summary>
        /// Sends the escalation. <b>The routing decision is one argument and it is load-bearing:</b>
        /// <c>alert.EscalationChannel</c> goes to
        /// <see cref="NotificationChannelService.DispatchAsync(AlertNotification, string?)"/>, where
        /// null/empty means EVERY admitted channel and a non-empty value means that channel only.
        ///
        /// <para><b>special-alerts-escalation-parity (2026-09-11).</b> This line is the whole of the
        /// EscalationChannel wiring. Before it, <c>AlertDefinition.EscalationChannel</c> had exactly
        /// two references in the entire tree - its own declaration and the &lt;select&gt; that binds
        /// it at <c>Pages/Alerts.razor:877</c> - so the Alerts page offered every one of the 80
        /// alerts a channel picker the dispatcher could not honour. Remove the argument and
        /// <c>EscalationChannelRoutingTests.TheEngineHandsTheAlertsEscalationChannelToTheDispatcher</c>
        /// goes red.</para>
        /// </summary>
        private void DispatchEscalation(AlertDefinition alert, AlertState state)
        {
            var msg = $"ESCALATED — {alert.Name} on {state.ServerName} has been active for {(DateTime.UtcNow - state.FirstTriggered).TotalMinutes:N0} min without acknowledgement. {state.Message}";
            _toast.ShowError($"ESCALATED: {alert.Name} — {state.ServerName}", msg, 10000);

            LastEscalationDispatch =
                DispatchAndSurfaceAsync(BuildEscalationNotification(alert, state, msg), alert.EscalationChannel);
        }

        /// <summary>
        /// The notification an ESCALATION sends. The message is composed by the caller and passed
        /// in; everything else about the shape is decided here.
        ///
        /// <para>Note what this shape does NOT set: <c>SendEmail</c>. An escalation therefore
        /// never reaches the SMTP channel, which is the only surface that renders a hit count at
        /// all. The count is carried honestly all the same, so the day an escalation does go by
        /// email it will be the measured one.</para>
        /// </summary>
        internal static AlertNotification BuildEscalationNotification(
            AlertDefinition alert, AlertState state, string message)
            => new()
            {
                AlertName = $"[ESCALATED] {alert.Name}",
                Metric = alert.Id,
                CurrentValue = state.LastValue,
                ThresholdValue = state.ThresholdValue,
                BasisKind = state.BasisKind,
                HitCount = state.HitCount,   // N-1 (2026-08-22), same fix on the escalation path
                Severity = "critical",
                InstanceName = state.ServerName,
                Message = message,
                TriggeredAt = DateTime.UtcNow
            };

        /// <summary>Awaits the channel dispatch and toasts if any channel failed. #68 LEG 1: the old
        /// call site was `_ = _channels.DispatchAsync(notification);` — fire-and-forget with no way
        /// to know a channel silently failed. DispatchAsync itself now records per-channel delivery
        /// health (visible on the Alerting Config page); this adds an immediate, loud signal at the
        /// point an alert actually fires.</summary>
        private async Task<IReadOnlyList<ChannelDispatchResult>> DispatchAndSurfaceAsync(
            AlertNotification notification, string? restrictToChannelId = null)
        {
            try
            {
                var results = await _channels.DispatchAsync(notification, restrictToChannelId);
                var failures = results.Where(r => !r.Success).ToList();
                if (failures.Count > 0)
                {
                    _toast.ShowError(
                        $"Alert delivery failed — {notification.AlertName}",
                        string.Join("; ", failures.Select(f => $"{f.Channel}: {f.Detail}")),
                        8000);
                }

                return results;
            }
            catch (Exception ex)
            {
                // DispatchAsync itself should never throw (each channel catches its own), but this is
                // the fire-and-forget boundary — never let an unexpected fault vanish silently.
                _logger.LogError(ex, "Unexpected error dispatching alert notification for {AlertName}", notification.AlertName);
                return Array.Empty<ChannelDispatchResult>();
            }
        }

        /// <summary>
        /// Resolve any in-memory states that haven't been refreshed in 3x their frequency
        /// (the alert condition has likely cleared but was never re-evaluated to confirm).
        /// </summary>
        /// <summary>
        /// alerts-r1-05: auto-resolving a stale Active alert means "we re-checked and it stopped
        /// firing", NOT "we stopped looking". An alert skipped by a maintenance window, an
        /// operational window, an open circuit breaker or a paused engine has a stale LastTriggered
        /// because nothing measured it — resolving it there turns a suppressed alert into a clean Ok
        /// on the NOC wall that no measurement supports (a 60 s alert would auto-resolve three
        /// minutes into a maintenance window). Only resolve when the alert was actually EVALUATED
        /// within the same stale window and simply did not re-fire.
        /// </summary>
        internal static bool ShouldAutoResolveAsCleared(
            AlertStatus status, DateTime lastTriggeredUtc, DateTime? lastEvaluatedUtc,
            int frequencySeconds, DateTime nowUtc)
        {
            if (status != AlertStatus.Active) return false;
            var staleCutoff = TimeSpan.FromSeconds(frequencySeconds * 3);
            if ((nowUtc - lastTriggeredUtc) <= staleCutoff) return false; // not stale enough to clear
            // Never evaluated, or not evaluated inside the stale window ⇒ we do not know it cleared.
            if (!lastEvaluatedUtc.HasValue || (nowUtc - lastEvaluatedUtc.Value) > staleCutoff) return false;
            return true;
        }

        /// <summary>
        /// <para><b>special-alerts-escalation-parity (2026-09-11).</b> This reaper deliberately does
        /// NOT call <see cref="EndEscalationEpisode"/>. It removes an alert that is very possibly
        /// STILL BREACHING and has merely stopped being measured, so treating it as the end of an
        /// escalation episode is what let one unbroken outage page an operator once per measurement
        /// gap. Pinned by
        /// SpecialAlertEscalationTests.EscalationSurvivesAMeasurementGapReap_oneEpisodeOnePage.</para>
        /// <para>internal, not private, so that pin can drive the REAL reaper rather than a
        /// re-implementation of it.</para>
        /// </summary>
        internal void ResolveCleared()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _activeStates)
            {
                var state = kvp.Value;
                var alert = _definitions.GetAlert(state.AlertId);
                if (alert == null) continue;

                // Keyed by the SAME tuple as the state being judged - kvp.Key IS that state's key.
                // This read used to be _lastEvaluation[state.AlertId], an ALERT-wide clock used to
                // decide the fate of a PER-SERVER state, so a clean run of this alert on any other
                // server presented as fresh evidence about THIS one. It is now the per-server
                // measurement clock, which is stamped only when THIS server answered: a standard query
                // that completed, NULL included (ruling 2026-09-17), or a special handler that produced
                // a value - never an attempt that threw, timed out, was cancelled or was skipped. A
                // state whose server left the enabled set never reaches this loop; it is closed first,
                // by PruneUnmonitoredAlertKeys.
                DateTime? lastEval = _lastServerEvaluation.TryGetValue(kvp.Key, out var le)
                    ? le : (DateTime?)null;

                if (ShouldAutoResolveAsCleared(state.Status, state.LastTriggered, lastEval,
                        alert.FrequencySeconds, now))
                {
                    state.Status = AlertStatus.Resolved;
                    state.ResolvedAt = now;
                    _activeStates.TryRemove(kvp.Key, out _);
                    _history.ResolveAlert(state.AlertId, state.ServerName);
                    _lastNotified.TryRemove(kvp.Key, out _);
                }
            }
        }

        public void AcknowledgeAlert(string alertId, string serverName)
        {
            var key = StateKey(alertId, serverName);
            if (_activeStates.TryGetValue(key, out var state))
            {
                state.Status = AlertStatus.Acknowledged;
                state.AcknowledgedAt = DateTime.UtcNow;
            }
            _history.AcknowledgeAlert(alertId, serverName);
            OnAlertsChanged?.Invoke();
        }

        public void AcknowledgeAll()
        {
            foreach (var state in _activeStates.Values.Where(s => s.Status == AlertStatus.Active))
            {
                state.Status = AlertStatus.Acknowledged;
                state.AcknowledgedAt = DateTime.UtcNow;
            }
            _history.AcknowledgeAll();
            OnAlertsChanged?.Invoke();
        }

        /// <summary>
        /// Builds the sentence beside a fired alert. Conditioned on the basis that ACTUALLY fired
        /// (2026-08-05): this used to take a bare double read from <c>alert.Thresholds</c> whatever
        /// the basis was, so a trend-only fire on an alert with no fixed thresholds rendered
        /// "above 0.0", naming a threshold that exists in no definition. Each branch prints only
        /// numbers its own basis measured, and the trend branch prints no threshold at all,
        /// because a slope has none.
        /// </summary>
        internal static string FormatMessage(AlertDefinition alert, string server, double value,
            FiringBasis basis, string severity)
            => FormatMessage(alert, server, value, basis, severity, DateTime.UtcNow);

        /// <summary>The same, against a stated clock, so the never-checked sentinel can be tested
        /// at any instant. See <see cref="IsNeverCheckedReading"/>.</summary>
        internal static string FormatMessage(AlertDefinition alert, string server, double value,
            FiringBasis basis, string severity, DateTime nowUtc)
        {
            // Invariant D (2026-09-19): a reading an operator cannot interpret is not a reading.
            // Ruled by Adrian 2026-09-19, "keep the number, fix the message": the value, the firing
            // decision and the severity are untouched; only this sentence changes.
            if (IsNeverCheckedReading(alert, value, nowUtc))
                return NeverCheckedMessage(value, severity);

            var direction = alert.Operator == "less_than" ? "below" : "above";
            var unit = alert.Unit switch
            {
                "percent" => "%",
                "seconds" => "s",
                "milliseconds" => "ms",
                "megabytes" => " MB",
                "hours" => " hrs",
                "minutes" => " min",
                "count" => "",
                "per_second" => "/s",
                "per_minute" => "/min",
                // strings-r2-07: these two shipped units fell to the empty default, so an alert
                // declaring them was paged out as a bare number with no quantity named at all
                // ("245.0 (above the 200.0 warning threshold)" - of what?). job_duration_unusual
                // still ships percent_difference; percent_of_average is kept because the editor's
                // Unit control can now produce it, and a unit the operator can select must render.
                "percent_of_average" => "% of average",
                "percent_difference" => "% difference",
                _ => ""
            };

            // A cumulative alert's value is a RATE, so the rendered number must carry a rate
            // unit. Definitions whose declared unit is already a rate ("/s", "/min") say so;
            // one that declares a plain quantity (wait_time_anomaly declares milliseconds) would
            // otherwise print "44,239,320,670.0 ms" for something measured per second. The
            // suffix is appended, never substituted, so the quantity stays named.
            if (alert.IsCumulativeCounter && !unit.EndsWith("/s", StringComparison.Ordinal)
                                          && !unit.EndsWith("/min", StringComparison.Ordinal))
                unit += "/s";

            var sev = severity.ToLowerInvariant();

            return basis.Kind switch
            {
                AlertBasisKind.FixedThreshold =>
                    $"{value:N1}{unit} ({direction} the {basis.Threshold!.Value:N1}{unit} {sev} threshold)",

                AlertBasisKind.LearnedBaseline =>
                    $"{value:N1}{unit} ({direction} the learned {sev} threshold of "
                    + $"{basis.Threshold!.Value:N1}{unit}, from this alert's own recent samples)",

                AlertBasisKind.TrendAnomaly =>
                    $"{value:N1}{unit} (a {sev} trend over the last 72 h of samples; "
                    + $"no fixed threshold was crossed)",

                AlertBasisKind.BaselineDeviation =>
                    $"{value:N1}{unit} ({basis.DeviationPercent!.Value:N1}% above the "
                    + $"{basis.BaselineAverage!.Value:N1}{unit} baseline average, over the "
                    + $"{basis.DeviationLimitPercent!.Value:N1}% deviation limit)",

                _ =>
                    $"{value:N1}{unit} ({sev}; the condition that fired was not recorded, so no "
                    + $"threshold is named here)",
            };
        }

        /// <summary>The shipped alert whose "never" reading this recognises.</summary>
        internal const string IntegrityCheckOverdueId = "integrity_check_overdue";

        /// <summary>SQL Server's placeholder for "no date": what <c>DATABASEPROPERTYEX(...,
        /// 'LastGoodCheckDbTime')</c> and <c>dbi_dbccLastKnownGood</c> report for a database that has
        /// never had a clean DBCC CHECKDB.</summary>
        internal static readonly DateTime SqlServerNoDate = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        /// <summary>How far past 1900-01-01 an implied date may land and still be the placeholder.
        /// Two days, because the reading is <c>DATEDIFF(HOUR, last_good, GETDATE())</c> on the SERVER's
        /// local clock and this check runs on ours: the server may sit anywhere from 12 hours behind
        /// UTC to 14 hours ahead, DATEDIFF counts whole hour boundaries (up to 1 hour off), and the
        /// message may be built a little after the query ran. That puts the placeholder within about
        /// 15 hours of 1900-01-01 as seen from here. Two days covers that with room for a drifting
        /// clock, and no real CHECKDB date can be anywhere near 1900.</summary>
        internal static readonly TimeSpan NeverCheckedMargin = TimeSpan.FromDays(2);

        /// <summary>
        /// True when an <c>integrity_check_overdue</c> reading is SQL Server's "never checked"
        /// placeholder rather than an age.
        ///
        /// <para><b>INVARIANT D (lane alert-correctness, 2026-09-19): a reading an operator cannot
        /// interpret is not a reading.</b> For a database that has never had a clean CHECKDB the
        /// query counts hours from 1900-01-01, and the message used to say "1,110,757.0 hrs". The
        /// placeholder is recognised by the date it IMPLIES (now minus the hours, at or before
        /// 1900-01-01 plus <see cref="NeverCheckedMargin"/>), never by a magic number: the reading grows
        /// by one every hour (1,110,750 on 2026-09-18, 1,110,757 seven hours later), so any fixed
        /// number is wrong within the hour. A reading too large to subtract from now at all implies
        /// a date before year 1 and is the placeholder too.</para>
        ///
        /// <para>Scope, stated: only this alert. The three backup-overdue alerts mark "never backed
        /// up" with a fixed 9999 or 99999 inside their queries; recognising that from here would
        /// need the magic number this rule refuses, so it waits for a definition change. Exports and
        /// history still carry the raw number (the ruled cost). Census:
        /// IntegrityNeverCheckedMessageTests.</para>
        /// </summary>
        internal static bool IsNeverCheckedReading(AlertDefinition alert, double hours, DateTime nowUtc)
        {
            if (!string.Equals(alert.Id, IntegrityCheckOverdueId, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(alert.Unit, "hours", StringComparison.OrdinalIgnoreCase)) return false;
            if (double.IsNaN(hours) || hours <= 0) return false;

            var sinceFloor = nowUtc - (SqlServerNoDate + NeverCheckedMargin);
            return hours >= sinceFloor.TotalHours;
        }

        /// <summary>The operator-facing sentence for a never-checked reading. Three sentences at most,
        /// no em-dash, straight quotes (dev/VOICE_GUIDE.md).</summary>
        internal static string NeverCheckedMessage(double hours, string severity) =>
            $"Never checked ({severity.ToLowerInvariant()}): at least one user database has no clean DBCC CHECKDB on record. "
            + $"SQL Server stores that as the date 1900-01-01, so the raw reading of {hours:N0} hours is not an age. "
            + "Schedule DBCC CHECKDB for every user database.";

        public void Dispose()
        {
            Stop();
            try { _loopTask?.Wait(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
            // An orderly shutdown is the other end-of-cycle: flush anything the last cycle deferred so a
            // stop between cycles costs nothing at all. Best effort, like every other write here.
            try { FlushFailureStore(); } catch { /* a store that cannot be written never blocks shutdown */ }
            _cts?.Dispose();
            _evaluationLock?.Dispose();
        }
    }

    /// <summary>An alert query that could not be evaluated on its most recent attempt. #68 LEG 2.
    /// eval-failure-visible (2026-09-06): the record is durable and episode-aware now —
    /// <see cref="FirstFailureUtc"/> stamps when the current failing run began (carried unchanged across
    /// failing ticks so the panel shows "failing since" and the audit fires once) and
    /// <see cref="FailureCount"/> is how many consecutive failing checks this episode has seen. The four
    /// positional members keep their shape so every existing reader and the on-disk JSON round-trip
    /// unchanged.</summary>
    public sealed record AlertEvalFailure(string AlertId, string ServerName, DateTime LastFailureUtc, string ErrorSummary)
    {
        /// <summary>When the current failing episode first opened. default(DateTime) for a record built
        /// by the positional constructor alone; every record this service creates stamps it.</summary>
        public DateTime FirstFailureUtc { get; init; }

        /// <summary>Consecutive failing checks in the current episode (&gt;= 1).</summary>
        public int FailureCount { get; init; } = 1;
    }

    /// <summary>
    /// Which of the four conditions actually fired an alert. Four bases can trip one alert, and
    /// only two of them involve a threshold the definition carries, so the basis has to travel
    /// with the decision or the message ends up naming whatever number was nearest to hand.
    /// </summary>
    public enum AlertBasisKind
    {
        /// <summary>A fixed threshold from the alert definition was crossed.</summary>
        FixedThreshold,
        /// <summary>An IQR threshold learned from this alert's own recent samples was crossed.</summary>
        LearnedBaseline,
        /// <summary>A slope over the rolling 72 h window. Fires on shape, so there is NO threshold.</summary>
        TrendAnomaly,
        /// <summary>The legacy percentage-off-a-baseline-average rule. The numbers are a percentage and an average.</summary>
        BaselineDeviation,
        /// <summary>The firing condition was not recorded. Prints no number rather than a plausible one.</summary>
        Unrecorded,
    }

    /// <summary>
    /// The basis that fired, carried out of the decision block so the message can name it
    /// (2026-08-05). Every numeric field is nullable and null unless THIS basis measured it:
    /// <see cref="Threshold"/> is null for a trend fire because a slope has no threshold, and
    /// that null is the whole point — the old code substituted 0 and printed "above 0.0".
    /// </summary>
    public sealed record FiringBasis(
        AlertBasisKind Kind,
        double? Threshold = null,
        double? BaselineAverage = null,
        double? DeviationPercent = null,
        double? DeviationLimitPercent = null)
    {
        public static FiringBasis Fixed(double threshold)
            => new(AlertBasisKind.FixedThreshold, Threshold: threshold);

        public static FiringBasis Learned(double threshold)
            => new(AlertBasisKind.LearnedBaseline, Threshold: threshold);

        public static FiringBasis Trend()
            => new(AlertBasisKind.TrendAnomaly);

        public static FiringBasis Deviation(double baselineAverage, double deviationPercent, double limitPercent)
            => new(AlertBasisKind.BaselineDeviation,
                   BaselineAverage: baselineAverage,
                   DeviationPercent: deviationPercent,
                   DeviationLimitPercent: limitPercent);

        public static FiringBasis Unknown()
            => new(AlertBasisKind.Unrecorded);
    }
}
