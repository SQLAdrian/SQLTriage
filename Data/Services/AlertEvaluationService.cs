/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Concurrent;
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

        // In-memory state: key = "alertId:serverName"
        private readonly ConcurrentDictionary<string, AlertState> _activeStates = new(StringComparer.OrdinalIgnoreCase);

        // Tracks last evaluation time per alert so we respect individual frequencies
        private readonly ConcurrentDictionary<string, DateTime> _lastEvaluation = new(StringComparer.OrdinalIgnoreCase);

        // Tracks last notification time per alert+server for cooldown
        private readonly ConcurrentDictionary<string, DateTime> _lastNotified = new(StringComparer.OrdinalIgnoreCase);

        // Tracks timestamps of each hit within the escalation window, keyed by "alertId:serverName"
        private readonly ConcurrentDictionary<string, Queue<DateTime>> _hitTimes = new(StringComparer.OrdinalIgnoreCase);

        // #68 LEG 2: an alert whose query THREW on its last attempt, keyed by "alertId:serverName".
        // Cleared the instant that same query succeeds again. Drives AlertsNoc's Unknown/degraded
        // card state so a server whose alert evaluation is silently erroring every cycle reads as
        // "we don't know", never as a clean, healthy Ok.
        private readonly ConcurrentDictionary<string, AlertEvalFailure> _evalFailures = new(StringComparer.OrdinalIgnoreCase);

        private bool _isRunning;
        private bool _dryRun;
        private readonly int _baseTickSeconds;
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

        /// <summary>Snapshot of every alert currently failing to evaluate (diagnostics / future UI).</summary>
        public IReadOnlyCollection<AlertEvalFailure> EvaluationFailures => _evalFailures.Values.ToList();

        public bool IsRunning => _isRunning;

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
            ServerCircuitBreakerService? breaker = null)
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
                static string StrandedKey(string alertId, string serverName) => $"{alertId}:{serverName}";
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
                if (alerts.Count == 0 || serverConnections.Count == 0) return;

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

                // Evaluate each alert that is due
                foreach (var alert in alerts)
                {
                    // Enforce minimum 300s for log-scan alert types (xp_readerrorlog is expensive)
                    const int LogScanMinFrequencySeconds = 300;
                    var effectiveFrequency = alert.Id is "error_log_severity" or "error_log_fatal" or "logon_failure"
                        ? Math.Max(alert.FrequencySeconds, LogScanMinFrequencySeconds)
                        : alert.FrequencySeconds;
                    if (effectiveFrequency != alert.FrequencySeconds
                        && !_lastEvaluation.ContainsKey($"__logwarn__{alert.Id}"))
                    {
                        _lastEvaluation[$"__logwarn__{alert.Id}"] = now; // sentinel — log once per run
                        _logger.LogWarning("Alert '{AlertId}' FrequencySeconds ({Stored}s) is below log-scan minimum; effective frequency clamped to {Min}s",
                            alert.Id, alert.FrequencySeconds, LogScanMinFrequencySeconds);
                    }

                    // Skip if not due yet based on frequency
                    var evalKey = alert.Id;
                    if (_lastEvaluation.TryGetValue(evalKey, out var lastEval)
                        && (now - lastEval).TotalSeconds < effectiveFrequency)
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
                    if (!string.IsNullOrEmpty(alert.QueryMode) && alert.QueryMode != "standard")
                    {
                        _lastEvaluation[evalKey] = now;
                        var specialTasks = new List<Task>();
                        foreach (var conn in serverConnections)
                        {
                            foreach (var serverName in conn.GetServerList())
                            {
                                if (_breaker != null && !_breaker.ShouldAttempt(serverName))
                                    continue;
                                specialTasks.Add(ThrottledSpecialEvaluateAsync(alert, conn, serverName, globalDefaults, cancellationToken));
                            }
                        }
                        await Task.WhenAll(specialTasks);
                        continue;
                    }

                    _lastEvaluation[evalKey] = now;

                    // Run against each server with bounded concurrency
                    var tasks = new List<Task>();
                    foreach (var conn in serverConnections)
                    {
                        foreach (var serverName in conn.GetServerList())
                        {
                            if (_breaker != null && !_breaker.ShouldAttempt(serverName))
                                continue;
                            tasks.Add(ThrottledEvaluateAsync(alert, conn, serverName, globalDefaults, cancellationToken));
                        }
                    }

                    await Task.WhenAll(tasks);
                }

                // Resolve alerts that are no longer triggering
                ResolveCleared();

                OnAlertsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Alert evaluation cycle failed");
            }
            finally
            {
                _evaluationLock.Release();
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
                _breaker?.RecordFailure(serverName);
                _logger.LogError(result.Exception, "Alert evaluation failed for {AlertId} on {Server}", alert.Id, serverName);
                return;
            }

            ApplyBreakerOutcome(_breaker, serverName, reach.Reachability);
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

        internal sealed class ServerReachabilityProbe
        {
            public ServerReachability Reachability { get; set; } = ServerReachability.Undetermined;
        }

        /// <summary>Applies one attempt's observed reachability to the shared circuit breaker.
        /// Internal test seam (InternalsVisibleTo SQLTriage.Tests).</summary>
        internal static void ApplyBreakerOutcome(
            ServerCircuitBreakerService? breaker, string serverName, ServerReachability reachability)
        {
            if (breaker == null) return;
            switch (reachability)
            {
                case ServerReachability.Reached:
                    breaker.RecordSuccess(serverName);
                    break;
                case ServerReachability.Unreachable:
                    breaker.RecordFailure(serverName);
                    break;
                default:
                    break; // Undetermined — say nothing rather than say something false.
            }
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
                _breaker?.RecordFailure(serverName);
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
            var stateKey = $"{alert.Id}:{serverName}".ToLowerInvariant();

            double? value = null;
            Exception? handlerError = null;
            try
            {
                value = alert.QueryMode switch
                {
                    "connectivity_check" => await CheckConnectivityAsync(connection, serverName),
                    "host_connectivity_check" => await CheckConnectivityAsync(connection, serverName),
                    "error_log_scan" => await ScanErrorLogAsync(alert, connection, serverName),
                    "io_error_check" => await CheckIoErrorsAsync(connection, serverName),
                    "deadlock_count" => await CountDeadlocksAsync(connection, serverName),
                    "registry_check" => await CheckPowerPlanAsync(connection, serverName),
                    _ => null
                };
            }
            catch (Exception ex)
            {
                // The handlers swallow their own SQL errors, so a throw here is unusual — but if one
                // does, it is still "we could not measure", not evidence the server is healthy.
                handlerError = ex;
            }

            if (value == null)
            {
                // Fail closed and loud: record the failure so AlertsNoc renders Unknown, never a
                // clean Ok, for a special alert that could not evaluate this cycle (#68 LEG 2 parity).
                _evalFailures[stateKey] = new AlertEvalFailure(
                    alert.Id, serverName, DateTime.UtcNow,
                    handlerError?.Message ?? $"queryMode '{alert.QueryMode}' produced no measurement this cycle");
                if (handlerError != null)
                    _logger.LogDebug(handlerError, "Special alert {AlertId} ({Mode}) failed on {Server}",
                        alert.Id, alert.QueryMode, LogAnon.S(serverName));
                else
                    _logger.LogDebug("Special alert {AlertId} ({Mode}) produced no measurement on {Server}; recorded so the NOC reads Unknown, not clean",
                        alert.Id, alert.QueryMode, LogAnon.S(serverName));
                return;
            }

            // A value was measured — clear any prior failure record, exactly as the standard path does.
            _evalFailures.TryRemove(stateKey, out _);

            try
            {
                var isTriggered = IsThresholdBreached(value.Value, alert.Thresholds.Warning, alert.Operator)
                    || (alert.Thresholds.Critical.HasValue && IsThresholdBreached(value.Value, alert.Thresholds.Critical, alert.Operator));

                if (isTriggered)
                {
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
                }
                else if (_activeStates.TryRemove(stateKey, out var cleared))
                {
                    cleared.Status = AlertStatus.Resolved;
                    cleared.ResolvedAt = DateTime.UtcNow;
                    if (!_dryRun) _history.ResolveAlert(alert.Id, serverName);
                    _lastNotified.TryRemove(stateKey, out _);
                }
            }
            catch (Exception ex)
            {
                // A fault APPLYING a measured value (history write, dispatch, threshold maths) is
                // still "we do not know this alert's state" — record it rather than swallow it.
                _evalFailures[stateKey] = new AlertEvalFailure(alert.Id, serverName, DateTime.UtcNow, ex.Message);
                _logger.LogWarning(ex, "Special alert {AlertId} ({Mode}) failed applying its result on {Server}",
                    alert.Id, alert.QueryMode, LogAnon.S(serverName));
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
        private async Task<double?> CheckPowerPlanAsync(ServerConnection connection, string serverName)
        {
            try
            {
                var connStr = connection.GetConnectionString(serverName, "master");
                var sql = BuildPowerPlanCheckSql();
                var (sqlConn, pooled) = await RentConnectionAsync(connStr);
                try
                {
                    using var cmd = new SqlCommand(sql, (SqlConnection)sqlConn) { CommandTimeout = 10 };
                    var result = await cmd.ExecuteScalarAsync();
                    // An unreadable plan surfaces as SQL NULL (the query's WHEN @scheme IS NULL arm),
                    // which ExecuteScalar returns as DBNull — "we could not read the plan", not "High
                    // Performance". Fail closed to unknown so the NOC does not read a healthy Ok.
                    if (result == null || result == DBNull.Value) return null;
                    return Convert.ToDouble(result);
                }
                finally { ReturnOrDispose(sqlConn, connStr, pooled); }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Power plan registry check failed on {Server}", LogAnon.S(serverName));
                return null;
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

        private async Task<double?> CheckConnectivityAsync(ServerConnection connection, string serverName)
        {
            try
            {
                var connStr = connection.GetConnectionString(serverName, "master");
                var (sqlConn, pooled) = await RentConnectionAsync(connStr);
                try
                {
                    using var cmd = new SqlCommand("SELECT 1", (SqlConnection)sqlConn) { CommandTimeout = 5 };
                    await cmd.ExecuteScalarAsync();
                    return 0; // 0 = reachable (below threshold of 1 = unreachable)
                }
                finally { ReturnOrDispose(sqlConn, connStr, pooled); }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Connectivity check failed for {Server} — treating as unreachable", LogAnon.S(serverName));
                return 1; // 1 = unreachable
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
        /// </summary>
        internal static string? BuildErrorLogScanSql(string alertId) => alertId switch
        {
            // Pre-filter to lines containing "Severity:", then keep 17-19 and 2x (SQL Server
            // severity never exceeds 25). xp_readerrorlog params 5 & 6 must be datetime variables,
            // not inline expressions.
            "error_log_severity" => "DECLARE @f DATETIME = DATEADD(MINUTE,-5,GETDATE()), @t DATETIME = GETDATE(); DECLARE @r TABLE(LogDate DATETIME, ProcessInfo NVARCHAR(50), [Text] NVARCHAR(MAX)); INSERT INTO @r EXEC xp_readerrorlog 0, 1, N'Severity:', NULL, @f, @t; SELECT COUNT(*) FROM @r WHERE [Text] LIKE '%Severity: 1[7-9]%' OR [Text] LIKE '%Severity: 2%'",
            "error_log_fatal" => "DECLARE @f DATETIME = DATEADD(MINUTE,-5,GETDATE()), @t DATETIME = GETDATE(); DECLARE @r TABLE(LogDate DATETIME, ProcessInfo NVARCHAR(50), [Text] NVARCHAR(MAX)); INSERT INTO @r EXEC xp_readerrorlog 0, 1, NULL, NULL, @f, @t; SELECT COUNT(*) FROM @r WHERE [Text] LIKE '%Fatal%' OR [Text] LIKE '%severity 2[0-5]%'",
            "logon_failure" => "DECLARE @f DATETIME = DATEADD(MINUTE,-5,GETDATE()), @t DATETIME = GETDATE(); DECLARE @r TABLE(LogDate DATETIME, ProcessInfo NVARCHAR(50), [Text] NVARCHAR(MAX)); INSERT INTO @r EXEC xp_readerrorlog 0, 1, 'Login failed', NULL, @f, @t; SELECT COUNT(*) FROM @r",
            _ => null
        };

        private async Task<double?> ScanErrorLogAsync(AlertDefinition alert, ServerConnection connection, string serverName)
        {
            try
            {
                var connStr = connection.GetConnectionString(serverName, "master");

                // Count matching errors in xp_readerrorlog output from the last 5 minutes.
                var sql = BuildErrorLogScanSql(alert.Id);

                if (sql == null) return null;

                var (sqlConn, pooled) = await RentConnectionAsync(connStr);
                try
                {
                    using var cmd = new SqlCommand(sql, (SqlConnection)sqlConn) { CommandTimeout = 15 };
                    var result = await cmd.ExecuteScalarAsync();
                    return result == null || result == DBNull.Value ? 0 : Convert.ToDouble(result);
                }
                finally { ReturnOrDispose(sqlConn, connStr, pooled); }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error log scan failed for alert {AlertId} on {Server}: {Msg}", alert.Id, LogAnon.S(serverName), ex.Message);
                return null;
            }
        }

        private async Task<double?> CheckIoErrorsAsync(ServerConnection connection, string serverName)
        {
            try
            {
                var connStr = connection.GetConnectionString(serverName, "master");
                var (sqlConn, pooled) = await RentConnectionAsync(connStr);
                try
                {
                    using var cmd = new SqlCommand(
                        "SELECT COUNT(*) FROM sys.dm_io_virtual_file_stats(NULL, NULL) WHERE io_stall_read_ms > 5000 OR io_stall_write_ms > 5000",
                        (SqlConnection)sqlConn)
                    { CommandTimeout = 10 };
                    var result = await cmd.ExecuteScalarAsync();
                    return result == null || result == DBNull.Value ? 0 : Convert.ToDouble(result);
                }
                finally { ReturnOrDispose(sqlConn, connStr, pooled); }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "I/O error check failed on {Server}", LogAnon.S(serverName));
                return null;
            }
        }

        private async Task<double?> CountDeadlocksAsync(ServerConnection connection, string serverName)
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
                    using var cmd = new SqlCommand(sql, (SqlConnection)sqlConn) { CommandTimeout = 30 };
                    var result = await cmd.ExecuteScalarAsync();
                    return result == null || result == DBNull.Value ? 0 : Convert.ToDouble(result);
                }
                finally { ReturnOrDispose(sqlConn, connStr, pooled); }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deadlock count check failed on {Server}", LogAnon.S(serverName));
                return null;
            }
        }

        private async Task EvaluateAlertOnServerAsync(
            AlertDefinition alert,
            ServerConnection connection,
            string serverName,
            AlertGlobalDefaults globalDefaults,
            ServerReachabilityProbe? reach = null)
        {
            var stateKey = $"{alert.Id}:{serverName}".ToLowerInvariant();

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
                if (reach != null) reach.Reachability = ServerReachability.Reached;

                _evalFailures.TryRemove(stateKey, out _); // query succeeded — clear any prior failure record
                if (raw == null) return; // query returned no data

                await ObserveAndApplyAsync(alert, serverName, raw.Value, DateTime.UtcNow);

            }
            catch (Microsoft.Data.SqlClient.SqlException sqlEx)
            {
                // Connection failures and SQL errors are expected (server offline, AG not configured, etc.)
                // Log at Debug to avoid spamming the log on every evaluation cycle — but still record the
                // failure so AlertsNoc can render this server as Unknown instead of silently Ok (#68 LEG 2).
                // This handler deliberately does NOT rethrow; the breaker is told via `reach` instead.
                if (reach != null) reach.Reachability = ServerReachability.Unreachable;
                _logger.LogDebug(sqlEx, "Alert query failed (SQL) {AlertId} on {Server}: {Msg}", alert.Id, LogAnon.S(serverName), sqlEx.Message);
                _evalFailures[stateKey] = new AlertEvalFailure(alert.Id, serverName, DateTime.UtcNow, sqlEx.Message);
            }
            catch (Exception ex)
            {
                // Reachability stays whatever it already was. A non-SQL exception here is a fault in OUR
                // logic (threshold maths, notification dispatch, history write), not evidence about the
                // server — opening the circuit on it would suppress polling of a healthy instance.
                _logger.LogWarning(ex, "Failed to evaluate alert {AlertId} on {Server}", alert.Id, LogAnon.S(serverName));
                _evalFailures[stateKey] = new AlertEvalFailure(alert.Id, serverName, DateTime.UtcNow, ex.Message);
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
        /// against.</para>
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
                        "Alert {AlertId} on {Server} could not store its raw counter sample. Until the cache store is writable this alert can produce NO measurement at all - it will neither fire nor clear, and its silence is not evidence that the server is healthy",
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
            var stateKey = $"{alert.Id}:{serverName}".ToLowerInvariant();

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
                // Which numeric threshold actually fired, kept separate from the routed severity so
                // the message names the real threshold even when the declared-severity floor (r1-07)
                // lifts an alert to Critical on its warning threshold.
                var thresholdTier = isCritical ? "Critical" : "Warning";
                var severity = RuntimeSeverity(alert, isCritical);
                // Should be unreachable: every branch above that sets a flag also sets the
                // basis. Unknown() prints no threshold at all rather than inventing one.
                var firingBasis = basis ?? FiringBasis.Unknown();

                // Record this hit for escalation window tracking. Trim to the longest
                // escalation window we care about so the queue can't grow forever
                // while an alert stays firing. Default 60min cap is plenty.
                var trimWindow = alert.EscalationWindowMinutes > 0
                    ? alert.EscalationWindowMinutes
                    : 60;
                var trimCutoff = DateTime.UtcNow.AddMinutes(-trimWindow);
                lock (hits)
                {
                    hits.Enqueue(DateTime.UtcNow);
                    while (hits.Count > 0 && hits.Peek() < trimCutoff) hits.Dequeue();
                }

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

                    _logger.LogWarning("Alert fired: {AlertName} on {Server} ({Severity}) - value: {Value}{DryRun}",
                        alert.Name, LogAnon.S(serverName), severity, value,
                        _dryRun ? " [DRY RUN]" : "");
                }

                // ── Escalation check ───────────────────────────────────
                if (alert.Escalate && !_dryRun
                    && _activeStates.TryGetValue(stateKey, out var activeState)
                    && !activeState.IsEscalated
                    && activeState.Status != AlertStatus.Acknowledged)
                {
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
                        DispatchEscalation(alert, activeState);
                        _logger.LogWarning("Alert escalated: {AlertName} on {Server}", alert.Name, LogAnon.S(serverName));
                    }
                }
            }
            else
            {
                // Condition cleared — mark as resolved if was active
                if (_activeStates.TryRemove(stateKey, out var cleared))
                {
                    cleared.Status = AlertStatus.Resolved;
                    cleared.ResolvedAt = DateTime.UtcNow;
                    _history.ResolveAlert(alert.Id, serverName);
                    _lastNotified.TryRemove(stateKey, out _);
                    lock (hits) { hits.Clear(); }

                    _logger.LogInformation("Alert resolved: {AlertName} on {Server}", alert.Name, LogAnon.S(serverName));
                }
            }
        }

        private async Task<double?> ExecuteAlertQueryAsync(
            AlertDefinition alert,
            ServerConnection connection,
            string serverName)
        {
            var connString = connection.GetConnectionString(serverName, "master");
            var (sqlConn, pooled) = await RentConnectionAsync(connString);
            try
            {
                using var cmd = new SqlCommand(alert.Query, (SqlConnection)sqlConn)
                {
                    CommandTimeout = 15
                };
                var result = await cmd.ExecuteScalarAsync();
                if (result == null || result == DBNull.Value) return null;
                return Convert.ToDouble(result);
            }
            finally { ReturnOrDispose(sqlConn, connString, pooled); }
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
        /// floor; the 5-level catalogue vocabulary (Info/Low/Medium/High/Critical) vs the 2-level
        /// runtime tier (Warning/Critical) reconciliation beyond that is left to the owner.</para>
        /// </summary>
        internal static string RuntimeSeverity(AlertDefinition alert, bool criticalThresholdCrossed)
        {
            var declaredCritical = string.Equals(alert.Severity, "critical", StringComparison.OrdinalIgnoreCase);
            return criticalThresholdCrossed || declaredCritical ? "Critical" : "Warning";
        }

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

        private void DispatchEscalation(AlertDefinition alert, AlertState state)
        {
            var msg = $"ESCALATED — {alert.Name} on {state.ServerName} has been active for {(DateTime.UtcNow - state.FirstTriggered).TotalMinutes:N0} min without acknowledgement. {state.Message}";
            _toast.ShowError($"ESCALATED: {alert.Name} — {state.ServerName}", msg, 10000);

            _ = DispatchAndSurfaceAsync(BuildEscalationNotification(alert, state, msg));
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
        private async Task DispatchAndSurfaceAsync(AlertNotification notification)
        {
            try
            {
                var results = await _channels.DispatchAsync(notification);
                var failures = results.Where(r => !r.Success).ToList();
                if (failures.Count > 0)
                {
                    _toast.ShowError(
                        $"Alert delivery failed — {notification.AlertName}",
                        string.Join("; ", failures.Select(f => $"{f.Channel}: {f.Detail}")),
                        8000);
                }
            }
            catch (Exception ex)
            {
                // DispatchAsync itself should never throw (each channel catches its own), but this is
                // the fire-and-forget boundary — never let an unexpected fault vanish silently.
                _logger.LogError(ex, "Unexpected error dispatching alert notification for {AlertName}", notification.AlertName);
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

        private void ResolveCleared()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _activeStates)
            {
                var state = kvp.Value;
                var alert = _definitions.GetAlert(state.AlertId);
                if (alert == null) continue;

                DateTime? lastEval = _lastEvaluation.TryGetValue(state.AlertId, out var le)
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
            var key = $"{alertId}:{serverName}".ToLowerInvariant();
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
        {
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

        public void Dispose()
        {
            Stop();
            try { _loopTask?.Wait(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
            _cts?.Dispose();
            _evaluationLock?.Dispose();
        }
    }

    /// <summary>An alert query that threw on its most recent evaluation attempt. #68 LEG 2.</summary>
    public sealed record AlertEvalFailure(string AlertId, string ServerName, DateTime LastFailureUtc, string ErrorSummary);

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
