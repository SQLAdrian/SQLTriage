/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;

namespace SQLTriage.Data
{
    /// <summary>
    /// Executes enabled SQL checks from the check repository against configured
    /// SQL Server instances.  Results are tracked per-instance and per-check
    /// with a configurable history depth.
    ///
    /// Key design choices (inspired by PerformanceMonitor):
    ///   - Per-instance query throttling via SemaphoreSlim to avoid overwhelming
    ///     any single server.
    ///   - Thread-safe result storage using ConcurrentDictionary.
    ///   - Fire-and-forget timer or on-demand execution.
    /// </summary>
    public class CheckExecutionService : IDisposable
    {
        private readonly ILogger<CheckExecutionService> _logger;
        private readonly CheckRepositoryService _checkRepo;
        private readonly ServerConnectionManager _connectionManager;
        private readonly IConfiguration _configuration;
        private readonly GovernanceHistoryService? _historyService;
        private readonly QuickCheckResultStore? _resultStore;

        /// <summary>#27 Phase A — checkId→SQL resolution seam (optional; null ⇒ inline fallback).</summary>
        private readonly CheckSqlStore? _sqlStore;

        /// <summary>
        /// Instance-seat licence gate (optional; null ⇒ no filtering, prior behaviour exactly).
        ///
        /// SCOPE — audits and reports ONLY. This service is the CHECK/AUDIT lane: everything it
        /// holds is corpus/check results feeding /audit, /governance, the CIO dashboard, the report
        /// bundles and the estate roll-ups. LIVE MONITORING is explicitly out of scope per Adrian
        /// and is unaffected because it does not come through here — the perf/SQLWATCH surfaces read
        /// PerformanceMonitor and the dynamic dashboard, not CheckExecutionService. If a future
        /// change routes a live-monitoring surface through GetResults, this filter would silently
        /// widen past its ruling; keep them separate.
        /// </summary>
        private readonly SQLTriage.Data.Services.Licensing.ISeatRegister? _seats;

        /// <summary>
        /// Per-instance query throttles, keyed by "serverName|limit" — NOT by serverName alone.
        ///
        /// Why the limit is part of the key: this is a <c>GetOrAdd</c> cache on a DI SINGLETON and
        /// nothing ever evicts an entry (Dispose at the bottom of the file is the only other
        /// reader). Keyed by serverName alone, the FIRST limit used against a server would be
        /// pinned for the whole process lifetime, so changing the setting and re-running — the
        /// exact thing a user does to test it — would have had no effect. Including the limit means
        /// a changed limit lands on a fresh key with a correctly-sized semaphore.
        ///
        /// Consequence worth knowing: two runs against the SAME server with DIFFERENT limits hold
        /// two different semaphores, so their concurrency adds rather than sharing one budget. The
        /// desktop UI starts one audit at a time, so this is reachable only by a caller that starts
        /// overlapping runs on one instance. I have NOT exercised that case.
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceThrottles = new();

        /// <summary>
        /// Concurrent queries per instance when nothing else says otherwise. 7 is the value that
        /// was a hard-coded const before this became configurable, so a host with no
        /// UserSettingsService (tests, and any composition root that does not register it) and an
        /// install with no stored setting both keep the previous behaviour exactly.
        /// </summary>
        private const int DefaultConcurrentQueriesPerInstance = UserSettingsService.AuditConcurrencyDefault;

        // Optional user settings (DI). CONCRETE type deliberately: Data/Services/WindowsServiceHost.cs
        // registers only the concrete UserSettingsService, so injecting the interface would resolve
        // null in the CLI/headless host and silently ignore the setting there.
        private readonly UserSettingsService? _userSettings;

        /// <summary>
        /// #28 mitigation (2026-07-13, evidence-backed hypothesis — see handoff
        /// HANDOFF-2026-07-13-slow-burn.md §2): check-ids whose underlying SQL calls
        /// xp_readerrorlog against the instance's error log. Two of these running
        /// CONCURRENTLY against the SAME instance were observed to start at the same
        /// instant and one session died mid-query with "A severe error occurred on the
        /// current command" on SQL 2025 RTM. Matched by check-id (string) rather than a
        /// corpus metadata flag — see the #28 mitigation report's SPEC NOTE for the
        /// ideal long-term shape (a `runsExclusivelyPerInstance` / exclusion-group flag
        /// on SqlCheck, populated from the corpus). Extend this set if more
        /// xp_readerrorlog-based checks are added to the family.
        /// </summary>
        // Internal (not private) for test visibility (InternalsVisibleTo SQLTriage.Tests) —
        // see CheckExecutionServiceErrorLogMitigationTests.
        internal static readonly HashSet<string> ErrorLogFamilyCheckIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "SQLT-VA-ERRORLOG-KNOWN-ERROR-WATCHLIST",
            "SQLT-CUSTOM-ERRORLOG-CRITICAL-EVENTS",
        };

        /// <summary>
        /// Per-instance mutex (capacity 1) that serializes ONLY the checks in
        /// <see cref="ErrorLogFamilyCheckIds"/> — NOT a whole-audit lock. Keyed by plain
        /// serverName, so the other ~570 checks in a run are completely unaffected by this gate.
        /// (2026-07-19: <see cref="_instanceThrottles"/> now keys by serverName+limit; this lock
        /// deliberately stays on serverName alone, because the ERRORLOG family must stay
        /// serialized per instance whatever concurrency the run was given.)
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _errorLogFamilyLocks = new();

        /// <summary>Per-instance, per-check result history (most recent first).</summary>
        private readonly ConcurrentDictionary<string, List<CheckResult>> _resultsByInstance = new();

        /// <summary>Per-instance execution summaries.</summary>
        private readonly ConcurrentDictionary<string, CheckExecutionSummary> _lastSummary = new();

        /// <summary>
        /// The rows the most recent run IN THIS PROCESS produced for an instance — exactly those,
        /// never topped up from anywhere. Deliberately separate from <see cref="_resultsByInstance"/>:
        /// that hot cache is capped at <see cref="_maxResultsPerInstance"/> and
        /// <see cref="GetResults"/> fills the shortfall from the persisted latest-run store and
        /// SQLite, so a caller asking it for "the run that just finished" gets a UNION with the
        /// previous, possibly wider run. A caller that must show ONE run and nothing else reads
        /// <see cref="GetLastRunResults"/>. Replaced wholesale per run (never mutated in place),
        /// so a reader needs no lock.
        /// </summary>
        private readonly ConcurrentDictionary<string, List<CheckResult>> _lastRunResults = new();

        private readonly int _maxResultsPerInstance;
        private const int CheckCommandTimeoutSeconds = 30;

        // S-1 (pre-mortem FM-5, 2026-06-01): make every check session production-safe.
        //   • READ UNCOMMITTED → reads take no shared locks, so a check cannot block a
        //     busy production server (the documented blast-radius risk: 809/812 checks
        //     carry no NOLOCK and the default READ COMMITTED takes shared locks).
        //   • LOCK_TIMEOUT → bounds the rare schema-stability (Sch-S) lock wait behind
        //     DDL so a check aborts (Msg 1222) instead of hanging the whole run.
        // Both are prepended to the T-SQL check command text (SqlSessionSafety.BuildPrefix), so
        // they apply across the scalar / reader / rowcount paths and to both pooled and
        // directly-opened connections, regardless of each check's own SQL. They do NOT reach the
        // host-probe lane (no SQL session) or any other service's connections — the other read
        // lanes apply the same options themselves via SqlSessionSafety.
        // Configurable so they can be tuned/disabled without a rebuild.
        // NOTE: ApplicationIntent=ReadOnly is deliberately NOT set — it is an Always-On
        // read-routing hint, not a safety control, and would change which replica a check
        // observes (and fail on servers/strings that don't support it). Isolation level +
        // lock timeout are the real, universally-safe controls.
        private readonly bool _readUncommitted;
        private readonly int _lockTimeoutMs;
        private readonly string _sessionPrefix;

        // Optional shared pool (DI). When set, per-check connections are rented from it
        // so parallel multi-server Assessment runs (servers × 7 per-instance queries)
        // stay under the global cap. Null in tests / non-DI → direct open.
        private readonly SqlConnectionPoolService? _pool;

        // S1: tracks check IDs already logged as "missing integrity baseline" so the
        // info line fires once per id, not once per server in a multi-server run.
        private readonly ConcurrentDictionary<string, byte> _integrityMissingLogged = new();

        private bool _disposed;

        /// <summary>Raised after a full execution run completes for an instance.</summary>
        public event Action<CheckExecutionSummary>? OnExecutionCompleted;

        /// <summary>Raised after each individual check completes (for diagnostic logging).</summary>
        public event Action<CheckResult>? OnCheckCompleted;

        // Optional host-probe lane (DI). When set + capability-granted, checks declaring
        // method:host-probe run an OS/AD probe instead of T-SQL. Null in tests / when absent.
        private readonly SQLTriage.Data.Services.HostProbe.HostProbeService? _hostProbe;

        // F6: optional accepted-findings layer (DI). When set, GetResults annotates each
        // not-passed result with any client acceptance so it downgrades in score math.
        // Null in tests / when absent → no annotation (behaviour-identical).
        private readonly SQLTriage.Data.Services.AcceptedFindingsService? _acceptedFindings;

        public CheckExecutionService(
            ILogger<CheckExecutionService> logger,
            CheckRepositoryService checkRepo,
            ServerConnectionManager connectionManager,
            IConfiguration configuration,
            GovernanceHistoryService? historyService = null,
            QuickCheckResultStore? resultStore = null,
            CheckSqlStore? sqlStore = null,
            SqlConnectionPoolService? pool = null,
            SQLTriage.Data.Services.HostProbe.HostProbeService? hostProbe = null,
            SQLTriage.Data.Services.AcceptedFindingsService? acceptedFindings = null,
            SQLTriage.Data.Services.Licensing.ISeatRegister? seats = null,
            UserSettingsService? userSettings = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _checkRepo = checkRepo ?? throw new ArgumentNullException(nameof(checkRepo));
            _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _historyService = historyService;
            _resultStore = resultStore;
            _sqlStore = sqlStore;
            _pool = pool;
            _hostProbe = hostProbe;
            _acceptedFindings = acceptedFindings;
            _seats = seats;
            _userSettings = userSettings;
            // 2026-05-12: lowered hot cache from 500→50 per instance to cut RAM pressure
            // on 25-server runs (~3 GB resident observed). SQLite history retains the full
            // record via GovernanceHistoryService; consumers re-hydrate from disk on demand.
            _maxResultsPerInstance = _configuration.GetValue("CheckExecution:MaxResultsPerInstance", 50);
            // S-1: session-safety options (see field comments). Defaults are safe-on.
            _readUncommitted = _configuration.GetValue("CheckExecution:ReadUncommitted", true);
            _lockTimeoutMs = _configuration.GetValue("CheckExecution:LockTimeoutMs", 5000);
            _sessionPrefix = SQLTriage.Data.SqlSessionSafety.BuildPrefix(_readUncommitted, _lockTimeoutMs);
            AuditDiagnosticSink.Enabled = _configuration.GetValue("CheckExecution:DiagnosticJsonl", true);
        }

        /// <summary>
        /// True when a result carries the "SKIP — ..." Message convention set by the permission-skip
        /// and host-probe SKIP paths in ExecuteSingleCheckAsync. Used only to keep a SKIP that also
        /// carries a diagnostic ErrorMessage out of the run's error tally; the pass/fail/info split
        /// stays with <see cref="CheckClassification"/>.
        /// </summary>
        private static bool IsSkipMessage(string? message) =>
            message != null && message.StartsWith("SKIP", StringComparison.OrdinalIgnoreCase);

        // ────────────────────── Execution ──────────────────────

        /// <summary>
        /// Why the last catalogue load produced nothing, or null when it produced checks.
        /// Read by callers that must report an empty run honestly rather than as a clean pass.
        /// </summary>
        public string? CatalogueLoadError => _checkRepo.LoadError;

        /// <summary>
        /// Loads the check catalogue if a run reaches this point with nothing loaded (2026-08-05).
        ///
        /// The catalogue is populated by whichever PAGE the operator happens to open, so an
        /// unattended run on a headless host reached the executor with an empty repository, came
        /// back with TotalChecks 0, and was recorded "Success" — a clean pass over zero checks.
        /// Warming at the execution seam rather than at host start covers every entry path
        /// (scheduler, CLI, page) instead of only the one host, and touches no licensing code.
        ///
        /// Guarded on both counts: only when nothing is loaded AND no prior load has already
        /// recorded why it failed, so this never retries a known-bad bundle on every run.
        /// </summary>
        private async Task<List<SqlCheck>> EnsureCatalogueLoadedAsync()
        {
            var checks = _checkRepo.GetEnabledChecks();
            if (checks.Count > 0 || _checkRepo.LoadError != null) return checks;

            try
            {
                _logger.LogInformation(
                    "Check catalogue is empty at run start; loading it before the run rather than "
                    + "reporting an assessment of nothing.");
                await _checkRepo.LoadChecksAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // LoadChecksAsync records its own LoadError for the honest-empty path below; a
                // throw here must not take the run down, it must leave the run empty and SAYING so.
                _logger.LogWarning(ex, "Loading the check catalogue at run start failed");
            }

            return _checkRepo.GetEnabledChecks();
        }

        /// <summary>
        /// Decides how many queries may run at once against one instance for THIS run, in
        /// precedence order: per-run override → persisted user setting → the shipped default (7).
        /// Every branch is clamped to the supported range, so neither a caller nor an edited
        /// settings file can push the throttle outside it.
        /// </summary>
        private int ResolveMaxConcurrentPerInstance(int? perRunOverride)
        {
            var value = perRunOverride
                        ?? _userSettings?.GetAuditMaxConcurrentPerInstance()
                        ?? DefaultConcurrentQueriesPerInstance;
            return Math.Clamp(value, UserSettingsService.AuditConcurrencyMin, UserSettingsService.AuditConcurrencyMax);
        }

        /// <summary>
        /// Fetches (or creates) the per-instance throttle for a given limit. See the
        /// <see cref="_instanceThrottles"/> remarks for why the limit is part of the cache key.
        /// </summary>
        private SemaphoreSlim GetInstanceThrottle(string serverName, int limit)
            => _instanceThrottles.GetOrAdd($"{serverName}|{limit}", _ => new SemaphoreSlim(limit));

        /// <summary>
        /// Runs all enabled checks against a single SQL Server instance.
        /// Queries against the instance are throttled — see <paramref name="maxConcurrentPerInstance"/>.
        /// </summary>
        /// <param name="maxConcurrentPerInstance">
        /// Optional per-run override for concurrent queries per instance. Null (the default) uses
        /// the persisted Settings value, which itself falls back to 7. The value is read ONCE here,
        /// at run start; a run already under way is not re-read if the setting changes.
        /// </param>
        public async Task<CheckExecutionSummary> ExecuteChecksAsync(
            ServerConnection connection, string serverName, CancellationToken ct = default,
            int? maxConcurrentPerInstance = null)
        {
            var summary = new CheckExecutionSummary
            {
                InstanceName = serverName,
                StartedAt = DateTime.UtcNow
            };

            var enabledChecks = await EnsureCatalogueLoadedAsync();
            summary.TotalChecks = enabledChecks.Count;

            // #27 Phase A: SQL resolved through the in-memory store seam.
            if (_sqlStore != null)
            {
                _sqlStore.PopulateFromCatalogue(enabledChecks);
                _logger.LogInformation("SQL store: {Count} checks via {Source}",
                    _sqlStore.Count, _sqlStore.Source);
            }

            if (enabledChecks.Count == 0)
            {
                summary.CompletedAt = DateTime.UtcNow;
                // A run that executed nothing produced nothing. Recording the empty set is what
                // stops GetLastRunResults handing the PREVIOUS run's rows to a caller asking what
                // this one produced.
                _lastRunResults[serverName] = new List<CheckResult>();
                return summary;
            }

            var concurrency = ResolveMaxConcurrentPerInstance(maxConcurrentPerInstance);
            var throttle = GetInstanceThrottle(serverName, concurrency);
            _logger.LogInformation("{Server}: running {Checks} checks at {Concurrency} concurrent queries/instance",
                LogAnon.S(serverName), enabledChecks.Count, concurrency);

            // Use master database for check execution to allow running checks
            // even when SQLWATCH is not installed
            var connectionString = connection.GetConnectionString(serverName, "master");

            // X-1 (FM-3): probe the server's EngineEdition once per run so edition-gated
            // checks can SKIP instead of hard-erroring (e.g. on Azure SQL DB). 0 = unknown.
            var engineEdition = await DetectEngineEditionAsync(connectionString, ct);

            // Instance-seat capture — one read-only round trip per run, before results are stored so
            // this run's results are already joinable to a seat by the time anything reads them.
            await TryCaptureFingerprintAsync(connectionString, serverName, ct);

            var tasks = enabledChecks.Select(check =>
                ExecuteSingleCheckAsync(check, connectionString, serverName, throttle, engineEdition, ct));

            var results = await Task.WhenAll(tasks);

            foreach (var result in results)
            {
                // ErrorMessage alone does not make a result an error: the host-probe lane parks its
                // diagnostic Detail there on a SKIP outcome (NeedsElevation / CapabilityDenied /
                // CouldNotProbe), which is a check that could not be assessed, not one that failed.
                // Counting those as errors made an unprivileged audit report errors — and the CLI
                // exit code follows this tally. The "SKIP" Message prefix is the convention both
                // SKIP paths in ExecuteSingleCheckAsync set, and a genuine error sets Message to
                // "Error: ...", so the two are distinguishable here.
                if (result.ErrorMessage != null && !IsSkipMessage(result.Message))
                    summary.Errors++;
                // 2026-07-16: SKIP/INFO checked BEFORE Passed — CheckExecutionService sets
                // Passed=true for both, so without this branch they fell straight into
                // summary.Passed++ below and inflated it past what /governance's scored
                // PassedFindings counts for the identical run (CheckClassification.IsScorable
                // is the shared definition both sides now use).
                else if (!CheckClassification.IsScorable(result))
                {
                    summary.Informational++;
                    // 2026-07-21: Partial (Verdict=WARN) is a NAMED SUBSET of Informational, not a
                    // sixth bucket - the footing invariant above is untouched. Counted here so the
                    // roll-up surfaces can decompose "informational/skipped" instead of burying
                    // WARN inside it. See CheckExecutionSummary.Partial.
                    if (CheckClassification.IsWarn(result)) summary.Partial++;
                }
                else if (result.Passed)
                    summary.Passed++;
                else if (_acceptedFindings?.IsAccepted(serverName, result.CheckId) == true)
                    summary.Accepted++;   // F6: client-accepted — not an open finding (grid agrees)
                else
                    summary.Failed++;

                StoreResult(serverName, result);
            }

            summary.CompletedAt = DateTime.UtcNow;
            _lastSummary[serverName] = summary;
            // This run's rows, kept whole and uncapped, for callers that must show this run and
            // not a union with the last one. See the _lastRunResults field comment.
            _lastRunResults[serverName] = results.ToList();

            // Persist the full run to disk so consumers can re-hydrate without
            // keeping the entire result set in RAM (worklist item 2026-05-12 #4).
            _resultStore?.WriteRun(serverName, results);

            // P3: freeze the first-assessment baseline ("gospel") the very first time we
            // see a full run for this server. Later runs leave it untouched; re-baselining
            // is an explicit user action (ReBaseline).
            MaybeFreezeFirstBaseline(serverName, results, enabledChecks.Count);

            _logger.LogInformation("{Server}: {Passed} passed, {Failed} failed, {Errors} errors in {Duration:F1}s",
                LogAnon.S(serverName), summary.Passed, summary.Failed, summary.Errors, summary.Duration.TotalSeconds);

            OnExecutionCompleted?.Invoke(summary);
            return summary;
        }

        /// <summary>
        /// Runs a filtered subset of enabled checks against a single SQL Server instance.
        /// Use this for Quick Check mode (e.g., Critical + Warning severity only).
        /// </summary>
        /// <param name="maxConcurrentPerInstance">
        /// Optional per-run override for concurrent queries per instance — same semantics as the
        /// unfiltered overload above.
        /// </param>
        /// <param name="persistRun">
        /// Whether this run becomes the server's persisted "latest run" in the result store.
        /// Defaults to true, which is what every historic caller of this overload got and what
        /// QuickCheckRunner's quick-check runs still get. The Audit Assessment page passes false
        /// when its CATEGORY exclusion narrowed the run (Adrian's ruling, 2026-08-05): the
        /// latest-run record is read by Checks, RemediationTuner, ComplianceMap/Tree and /audit's
        /// cold start, none of which has any notion of coverage, so a 13-check Encryption-only run
        /// must not silently become the record of a full assessment. The caller decides and says
        /// so — this method never infers it from <paramref name="filter"/>, which QuickCheckRunner
        /// also narrows.
        /// <para>The run is unaffected in every other respect: it executes, its rows are returned,
        /// cached and available to GetLastRunResults, and the page renders and exports them.</para>
        /// </param>
        public async Task<CheckExecutionSummary> ExecuteChecksAsync(
            ServerConnection connection, string serverName,
            Func<SqlCheck, bool> filter, CancellationToken ct = default,
            int? maxConcurrentPerInstance = null, bool persistRun = true)
        {
            var summary = new CheckExecutionSummary
            {
                InstanceName = serverName,
                StartedAt = DateTime.UtcNow
            };

            var filteredChecks = (await EnsureCatalogueLoadedAsync()).Where(filter).ToList();
            summary.TotalChecks = filteredChecks.Count;

            // #27 Phase A: SQL resolved through the in-memory store seam.
            if (_sqlStore != null)
            {
                _sqlStore.PopulateFromCatalogue(filteredChecks);
                _logger.LogInformation("SQL store: {Count} checks via {Source}",
                    _sqlStore.Count, _sqlStore.Source);
            }

            if (filteredChecks.Count == 0)
            {
                summary.CompletedAt = DateTime.UtcNow;
                // Every category unticked is a real, reachable state on the audit page. It ran
                // nothing, so it produced nothing — recorded, so the caller cannot be handed the
                // previous run's rows and show them under this run's coverage sentence.
                _lastRunResults[serverName] = new List<CheckResult>();
                return summary;
            }

            var concurrency = ResolveMaxConcurrentPerInstance(maxConcurrentPerInstance);
            var throttle = GetInstanceThrottle(serverName, concurrency);
            _logger.LogInformation("{Server}: running {Checks} filtered checks at {Concurrency} concurrent queries/instance",
                LogAnon.S(serverName), filteredChecks.Count, concurrency);

            // Use master database for check execution to allow running checks
            // even when SQLWATCH is not installed
            var connectionString = connection.GetConnectionString(serverName, "master");

            // X-1 (FM-3): probe EngineEdition once per run (see ExecuteChecksAsync above).
            var engineEdition = await DetectEngineEditionAsync(connectionString, ct);

            // Instance-seat capture (see ExecuteChecksAsync above) — a filtered run seats the
            // instance too, so a Critical-only Quick Check is enough to establish coverage.
            await TryCaptureFingerprintAsync(connectionString, serverName, ct);

            var tasks = filteredChecks.Select(check =>
                ExecuteSingleCheckAsync(check, connectionString, serverName, throttle, engineEdition, ct));

            var results = await Task.WhenAll(tasks);

            foreach (var result in results)
            {
                // ErrorMessage alone does not make a result an error: the host-probe lane parks its
                // diagnostic Detail there on a SKIP outcome (NeedsElevation / CapabilityDenied /
                // CouldNotProbe), which is a check that could not be assessed, not one that failed.
                // Counting those as errors made an unprivileged audit report errors — and the CLI
                // exit code follows this tally. The "SKIP" Message prefix is the convention both
                // SKIP paths in ExecuteSingleCheckAsync set, and a genuine error sets Message to
                // "Error: ...", so the two are distinguishable here.
                if (result.ErrorMessage != null && !IsSkipMessage(result.Message))
                    summary.Errors++;
                // 2026-07-16: SKIP/INFO checked BEFORE Passed — CheckExecutionService sets
                // Passed=true for both, so without this branch they fell straight into
                // summary.Passed++ below and inflated it past what /governance's scored
                // PassedFindings counts for the identical run (CheckClassification.IsScorable
                // is the shared definition both sides now use).
                else if (!CheckClassification.IsScorable(result))
                {
                    summary.Informational++;
                    // 2026-07-21: Partial (Verdict=WARN) is a NAMED SUBSET of Informational, not a
                    // sixth bucket - the footing invariant above is untouched. Counted here so the
                    // roll-up surfaces can decompose "informational/skipped" instead of burying
                    // WARN inside it. See CheckExecutionSummary.Partial.
                    if (CheckClassification.IsWarn(result)) summary.Partial++;
                }
                else if (result.Passed)
                    summary.Passed++;
                else if (_acceptedFindings?.IsAccepted(serverName, result.CheckId) == true)
                    summary.Accepted++;   // F6: client-accepted — not an open finding (grid agrees)
                else
                    summary.Failed++;

                StoreResult(serverName, result);
            }

            summary.CompletedAt = DateTime.UtcNow;
            _lastSummary[serverName] = summary;
            // The filtered run's OWN rows. This is the overload the audit page calls, and the one
            // where a union with the previous run is a lie: a 13-check Encryption-only run must not
            // hand the page the wider earlier run's rows under a notice saying those categories did
            // not run (reproduced through the store in LastRunResultsIsolationTests).
            _lastRunResults[serverName] = results.ToList();

            // Persist the run to disk; consumers re-hydrate via the store. A caller that narrowed
            // the run by category opts out (see persistRun): the record stays the last FULL
            // assessment, because every consumer of it reads it as one. Announced in the log rather
            // than skipped quietly — a run that does not persist is a surprise worth finding later.
            if (persistRun)
            {
                _resultStore?.WriteRun(serverName, results);
            }
            else
            {
                _logger.LogInformation(
                    "{Server}: filtered run of {Checks} checks NOT persisted as the latest run (caller opted out); "
                    + "the previous persisted run stands", LogAnon.S(serverName), filteredChecks.Count);
            }

            // P3: freeze the gospel baseline only if this filtered run actually covered
            // every enabled check (e.g. the "All checks" audit, whose filter is c => true).
            // A Critical-only Quick Check has fewer results than enabled and won't qualify.
            MaybeFreezeFirstBaseline(serverName, results, _checkRepo.GetEnabledChecks().Count);

            _logger.LogInformation("{Server}: {Passed} passed, {Failed} failed, {Errors} errors in {Duration:F1}s",
                LogAnon.S(serverName), summary.Passed, summary.Failed, summary.Errors, summary.Duration.TotalSeconds);

            OnExecutionCompleted?.Invoke(summary);
            return summary;
        }

        /// <summary>
        /// Runs all enabled checks against every enabled server connection.
        /// </summary>
        public async Task<List<CheckExecutionSummary>> ExecuteChecksAllInstancesAsync(
            CancellationToken ct = default)
        {
            var summaries = new List<CheckExecutionSummary>();
            var connections = _connectionManager.GetEnabledConnections();

            foreach (var conn in connections)
            {
                foreach (var server in conn.GetServerList())
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        var summary = await ExecuteChecksAsync(conn, server, ct);
                        summaries.Add(summary);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error executing checks on {Server}", LogAnon.S(server));

                        summaries.Add(new CheckExecutionSummary
                        {
                            InstanceName = server,
                            StartedAt = DateTime.UtcNow,
                            CompletedAt = DateTime.UtcNow,
                            Errors = _checkRepo.GetEnabledChecks().Count
                        });
                    }
                }
            }

            return summaries;
        }

        // ────────────────────── Single Check ──────────────────────

        private async Task<CheckResult> ExecuteSingleCheckAsync(
            SqlCheck check, string connectionString, string serverName,
            SemaphoreSlim throttle, int engineEdition, CancellationToken ct)
        {
            var result = new CheckResult
            {
                CheckId = check.Id,
                CheckName = check.Name,
                Category = check.Category,
                Severity = check.Severity,
                ExpectedValue = check.ExpectedValue,
                EffortHours = check.EffortHours,
                IsBad = check.IsBad,
                ScoreWeight = check.ScoreWeight > 0 ? check.ScoreWeight : 1,
                InstanceName = serverName,
                RecommendedAction = check.RecommendedAction,
                Description = check.Description
            };

            // Host-probe checks run an OS/AD probe (fail-closed, bundle-gated) instead of T-SQL —
            // they have no SQL connection / query, so branch BEFORE the SQL resolution below.
            if (string.Equals(check.Method, "host-probe", StringComparison.OrdinalIgnoreCase))
                return await ExecuteHostProbeAsync(check, serverName, result, ct).ConfigureAwait(false);

            // X-1 (pre-mortem FM-3): EngineEdition gate. If the check declares the editions
            // it supports and the live server isn't one of them, SKIP rather than run it —
            // closes the Azure version-trap where an on-prem-only check (master/msdb/OS)
            // hard-errors on Azure SQL DB after a version gate wrongly passed. engineEdition<=0
            // means detection failed → don't skip anything (preserves prior behaviour), and
            // an empty SupportedEngineEditions means "all editions" (the default).
            if (engineEdition > 0 && check.SupportedEngineEditions.Count > 0
                && !check.SupportedEngineEditions.Contains(engineEdition))
            {
                result.Message = $"SKIP — not applicable on EngineEdition {engineEdition} " +
                                 $"(supported: {string.Join(",", check.SupportedEngineEditions)})";
                result.Passed = true;        // SKIP rides the "passed for scoring" tier
                result.ActualValue = 0;
                result.ExecutedAt = DateTime.UtcNow;
                return result;
            }

            // #27 Phase A: resolve SQL through the store seam; fall back to the
            // inline SqlQuery if the store is absent or the id is missing
            // (fault-tolerant — behaviour-identical until Phase B swaps loaders).
            var sqlText = (_sqlStore != null && _sqlStore.TryGet(check.Id, out var storedSql))
                ? storedSql
                : check.SqlQuery;

            if (string.IsNullOrWhiteSpace(sqlText))
            {
                result.ErrorMessage = "Check has no SQL query defined";
                return result;
            }

            // S1: per-query integrity gate. The SQL arrives inside the GCM-authenticated
            // bundle; this catches in-memory / post-decrypt tampering between load and
            // execution. Mismatch → block + flag Corrupted (do NOT execute). Missing
            // baseline (e.g. inline-fallback SQL) → allow + log once (back-compat).
            if (_sqlStore != null)
            {
                var integrity = _sqlStore.Verify(check.Id, sqlText);
                if (integrity == SqlIntegrity.Mismatch)
                {
                    result.IsCorrupted = true;
                    result.Passed = false;
                    result.ErrorMessage = "INTEGRITY: SQL checksum mismatch — query blocked (not executed).";
                    result.Message = "Corrupted — SQL altered after load; blocked for safety.";
                    _logger.LogError(
                        "[S1] Integrity BLOCK: check {CheckId} SQL checksum mismatch on {Server} — not executed.",
                        check.Id, LogAnon.S(serverName));
                    return result;
                }
                if (integrity == SqlIntegrity.Missing && _integrityMissingLogged.TryAdd(check.Id, 0))
                {
                    _logger.LogInformation(
                        "[S1] No integrity baseline for check {CheckId} (inline-fallback SQL) — allowed.",
                        check.Id);
                }
            }

            var sw = Stopwatch.StartNew();

            // #28 mitigation: acquire the narrow ERRORLOG-family per-instance lock BEFORE
            // the throttle/connection/execute sequence below, so the two family checks
            // never overlap on this instance. Every other check skips this block entirely
            // (isErrorLogFamilyCheck is false → errorLogFamilyLock stays null → no wait).
            var isErrorLogFamilyCheck = ErrorLogFamilyCheckIds.Contains(check.Id);
            SemaphoreSlim? errorLogFamilyLock = null;
            if (isErrorLogFamilyCheck)
            {
                errorLogFamilyLock = _errorLogFamilyLocks.GetOrAdd(serverName, _ => new SemaphoreSlim(1, 1));
                _logger.LogInformation(
                    "[#28] {CheckId} waiting on ERRORLOG-family per-instance lock for {Server} at {AtUtc:o}",
                    check.Id, LogAnon.S(serverName), DateTime.UtcNow);
                await errorLogFamilyLock.WaitAsync(ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "[#28] {CheckId} ACQUIRED ERRORLOG-family per-instance lock for {Server} at {AtUtc:o}",
                    check.Id, LogAnon.S(serverName), DateTime.UtcNow);
            }
            try
            {

            await throttle.WaitAsync(ct);
            // Rent from the shared pool when available so parallel multi-server runs
            // stay under the global cap; else open directly. Released in `finally`.
            // H7 (2026-07-07): acquisition sits in its OWN try that releases the throttle
            // permit before re-throwing — a failed rent/open (server unreachable, pool
            // saturation) previously leaked the permit acquired above, and after enough
            // failures the semaphore drained and no check would run until app restart.
            // Re-throw preserves the existing propagation/fast-fail on a dead server.
            SqlConnection conn;
            bool connFromPool = _pool != null;
            try
            {
                if (connFromPool)
                    conn = (SqlConnection)await _pool!.GetConnectionAsync(connectionString, ct);
                else
                {
                    conn = new SqlConnection(connectionString);
                    await conn.OpenAsync(ct);
                }
            }
            catch
            {
                throttle.Release();
                throw;
            }
            try
            {
                using var cmd = conn.CreateCommand();
                // S-1: prepend session-safety options (READ UNCOMMITTED + LOCK_TIMEOUT).
                // _sessionPrefix is built from config; integrity was verified on sqlText
                // (unprefixed) above, so this does not affect the checksum gate.
                cmd.CommandText = _sessionPrefix + sqlText;
                cmd.CommandTimeout = CheckCommandTimeoutSeconds;

                // Determine execution type:
                //   "Binary" / "scalar" / null → ExecuteScalar, compare to ExpectedValue
                //   "RowCount"                 → count rows, evaluate via RowCountCondition
                var execType = (check.ExecutionType ?? "scalar").ToLowerInvariant();

                if (execType == "rowcount")
                {
                    // Row-count based check — count returned rows and evaluate
                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    int rowCount = 0;
                    while (await reader.ReadAsync(ct)) rowCount++;
                    result.ActualValue = rowCount;
                    result.Passed = EvaluateRowCount(rowCount, check);
                }
                else
                {
                    // Check if this check has a text-based ResultInterpretation or the v2 'verdict' contract
                    var ri = check.ResultInterpretation;
                    if (!string.IsNullOrEmpty(ri) && (ri.Contains("Pass", StringComparison.OrdinalIgnoreCase) || ri.Equals("verdict", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Text-based result: e.g., PassFail, PassInfo, PassWarnFail, PassFailSkip, or verdict
                        // SQL should return a 'result' column with PASS/FAIL/INFO/WARN/SKIP
                        // G3 contract also returns 'message' and 'count' columns
                        using var reader = await cmd.ExecuteReaderAsync(ct);
                        string resultText = "";
                        string? messageText = null;
                        int? countValue = null;

                        if (await reader.ReadAsync(ct))
                        {
                            try { resultText = reader["result"]?.ToString() ?? ""; }
                            catch { resultText = reader[0]?.ToString() ?? ""; }

                            try { messageText = reader["message"]?.ToString(); } catch { }
                            try 
                            { 
                                var cObj = reader["count"];
                                if (cObj != null && cObj != DBNull.Value) countValue = Convert.ToInt32(cObj);
                            } 
                            catch { }
                        }

                        // Route by the RETURNED VALUE's type, not the Pass* label.
                        // ~65% of checks declare a Pass* ResultInterpretation but
                        // return a numeric 0/1 (SELECT CASE WHEN <bad> THEN 1 ELSE 0
                        // END). Comparing those to the literal "PASS" force-failed
                        // every one. If the scalar is integral, evaluate it as the
                        // numeric path would; only genuine non-numeric verdicts use
                        // the string PASS check (behaviour unchanged for those).
                        if (int.TryParse(resultText.Trim(), out var textNumeric))
                        {
                            result.ActualValue = countValue ?? textNumeric;
                            result.Passed = check.Severity.Equals("Info", StringComparison.OrdinalIgnoreCase)
                                || textNumeric == check.ExpectedValue;
                            result.Message = !string.IsNullOrEmpty(messageText) ? messageText : (result.Passed
                                ? $"Check passed (value={textNumeric})"
                                : $"Check failed: got {textNumeric}, expected {check.ExpectedValue}");
                        }
                        else
                        {
                            result.ActualValue = countValue ?? 0; // text results map to count or 0
                            
                            // Declared verdict tiers (ResultInterpretation:
                            // PassFail / PassWarnFail / PassInfo / PassFailSkip / verdict).
                            // SKIP = check not applicable on this server;
                            // INFO = informational note;
                            // WARN = the check ran but could not fully assess the
                            //        target (e.g. an under-privileged caller could
                            //        not read some databases) — none of the three is
                            //        a failure (previously every non-"PASS" verdict
                            //        was force-failed, polluting the scorecard
                            //        exactly like the numeric bug).
                            //
                            // Ruling #4 (2026-07-20) added WARN to this tier. It was
                            // held as not-passed until now, which meant a corpus WARN
                            // rendered to a client as a FAILED server check — a
                            // permissions problem presented as a configuration defect.
                            // Passed=true here is "not a failure", NOT "the control is
                            // correctly set": CheckClassification.IsWarn keeps WARN out
                            // of IsScorable, so it lands in neither the numerator nor
                            // the denominator of any score, and the raw Verdict below
                            // is what every surface renders its distinct badge from.
                            var verdict = resultText.Trim().ToUpperInvariant();
                            result.Passed = IsNotFailureVerdict(verdict);
                            result.Message = !string.IsNullOrEmpty(messageText) ? messageText : resultText;
                            // #49 (2026-07-15): preserve the raw verdict tier so the UI can tell
                            // a genuine PASS from an INFO riding the same Passed=true tier —
                            // both count as "not a failure" for scoring (unchanged above), but
                            // only PASS is actually an assertion the control is correctly set.
                            result.Verdict = verdict;
                        }
                    }
                    else
                    {
                        // Numeric check (default): SELECT CASE WHEN EXISTS (...) THEN 1 ELSE 0 END
                        var scalar = await cmd.ExecuteScalarAsync(ct);
                        result.ActualValue = scalar != null && scalar != DBNull.Value
                            ? Convert.ToInt32(scalar)
                            : 0;

                        if (check.Severity.Equals("Info", StringComparison.OrdinalIgnoreCase))
                        {
                            result.Passed = true;
                        }
                        else
                        {
                            result.Passed = result.ActualValue == check.ExpectedValue;
                        }
                        result.Message = result.Passed
                            ? $"Check passed (value={result.ActualValue})"
                            : $"Check failed: got {result.ActualValue}, expected {check.ExpectedValue}";
                    }
                }
            }
            catch (SqlException sqlEx) when (TryMapPermissionSkip(sqlEx, out var skipReason))
            {
                // Permission / cross-DB access failure — treat as SKIP (check is not
                // applicable because this account can't see what it needs to). SKIP is
                // distinct from ERROR in the verdict model: it neither pulls the score
                // down nor counts toward fail/error tallies. See ResultInterpretation
                // SKIP semantics above.
                result.Message = $"SKIP — {skipReason} (SQL error {sqlEx.Number})";
                result.Passed = true;   // SKIP rides the verdict tier "passed for scoring" path
                result.ActualValue = 0;
                result.ErrorMessage = null;   // not an error — honest skip with reason
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                result.Message = $"Error: {ex.Message}";
                result.Passed = false;
                // #28 diagnostic: capture the real SQL error Number/Class/State/LineNumber/Server
                // (walking InnerException in case ex WRAPS a SqlException) so the next occurrence
                // of a check error — ERRORLOG-family or otherwise — self-diagnoses instead of only
                // showing the generic ex.Message text.
                ApplySqlExceptionDetails(result, ex);
                // The detail above only reaches the result object. Without a log line a headless
                // --audit run reports an error count with nothing anywhere saying which check or
                // which server produced it.
                _logger.LogWarning(ex, "Check {CheckId} failed on {Server}", result.CheckId, LogAnon.S(serverName));
            }
            finally
            {
                if (connFromPool)
                    _pool!.ReturnConnection(conn, connectionString);
                else
                    conn.Dispose();
                throttle.Release();
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
                result.ExecutedAt = DateTime.UtcNow;
            }
            }
            finally
            {
                // #28 mitigation: release the ERRORLOG-family per-instance lock (see acquire above).
                if (isErrorLogFamilyCheck)
                {
                    _logger.LogInformation(
                        "[#28] {CheckId} RELEASING ERRORLOG-family per-instance lock for {Server} at {AtUtc:o} (check ran {Ms}ms)",
                        check.Id, LogAnon.S(serverName), DateTime.UtcNow, sw.ElapsedMilliseconds);
                    errorLogFamilyLock?.Release();
                }
            }

            // Fire per-check diagnostic event
            try { OnCheckCompleted?.Invoke(result); }
            catch (Exception ex) { _logger.LogWarning(ex, "OnCheckCompleted subscriber threw for check {CheckId}", result.CheckId); }

            // Verbose triage sink — untruncated raw outcome + execution contract.
            AuditDiagnosticSink.Record(serverName, check, result);

            return result;
        }

        // ────────────────────── Host-probe (OS/AD) checks ──────────────────────

        /// <summary>
        /// Runs a method:host-probe check via the fail-closed HostProbeService and maps its
        /// distinct terminal state to a CheckResult. Compliant→Pass, NotCompliant→Fail; every
        /// did-NOT-run outcome (NeedsElevation / CapabilityDenied / CouldNotProbe) becomes a
        /// SKIP-with-reason — NEVER a silent pass. No SQL connection is opened.
        /// </summary>
        private async Task<CheckResult> ExecuteHostProbeAsync(
            SqlCheck check, string serverName, CheckResult result, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (_hostProbe == null)
                {
                    result.Passed = true; result.ActualValue = 0; // SKIP — not assessed, not a pass
                    result.Message = "SKIP — host-probe lane is not available in this build.";
                    return result;
                }

                var host = ResolveHostName(serverName);
                var probe = await RunProbeAsync(check.ProbeKey, host, serverName, ct).ConfigureAwait(false);
                switch (probe.Outcome)
                {
                    case SQLTriage.Data.Services.HostProbe.HostProbeOutcome.Compliant:
                        result.Passed = true; result.ActualValue = 0; result.Message = probe.Message; break;
                    case SQLTriage.Data.Services.HostProbe.HostProbeOutcome.NotCompliant:
                        result.Passed = false; result.ActualValue = 1; result.Message = probe.Message; break;
                    default: // NeedsElevation / CapabilityDenied / CouldNotProbe — SKIP-with-reason, NOT a pass
                        result.Passed = true; result.ActualValue = 0;
                        result.Message = "SKIP — " + probe.Message;
                        result.ErrorMessage = probe.Detail; // diagnostic only; SKIP is not an error
                        break;
                }
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.ErrorMessage = ex.Message;
                result.Message = $"Error: host probe failed — {ex.Message}";
            }
            finally
            {
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
                result.ExecutedAt = DateTime.UtcNow;
            }

            try { OnCheckCompleted?.Invoke(result); }
            catch (Exception ex) { _logger.LogWarning(ex, "OnCheckCompleted subscriber threw for check {CheckId}", result.CheckId); }
            AuditDiagnosticSink.Record(serverName, check, result);
            return result;
        }

        // Maps a probe key to its HostProbeService method. host.adlogin targets the SQL instance
        // (it validates the instance's Windows logins vs AD); the others target the Windows host.
        private Task<SQLTriage.Data.Services.HostProbe.HostProbeResult> RunProbeAsync(
            string? probeKey, string host, string sqlInstance, CancellationToken ct) =>
            (probeKey?.Trim().ToLowerInvariant()) switch
            {
                "host.powerplan" => _hostProbe!.ProbePowerPlanAsync(host, ct),
                "host.diskalloc" => _hostProbe!.ProbeDiskAllocationAsync(host, ct),
                "host.spn"       => _hostProbe!.ProbeSpnAsync(host, ct),
                "host.adlogin"   => _hostProbe!.ProbeWindowsLoginsAsync(sqlInstance, ct),
                _ => Task.FromResult(new SQLTriage.Data.Services.HostProbe.HostProbeResult(
                        probeKey ?? "(none)", host,
                        SQLTriage.Data.Services.HostProbe.HostProbeOutcome.CouldNotProbe,
                        $"Unknown or missing host-probe key '{probeKey}'.")),
            };

        /// <summary>
        /// The declared verdict tiers that are NOT a check failure. Extracted from
        /// ExecuteSingleCheckAsync (ruling #4, 2026-07-20) so this set is unit-pinnable instead of
        /// being an inline literal nothing could exercise — the whole tier changed under it once
        /// already. Input must already be Trim()'d + upper-cased, exactly as the caller does.
        ///
        /// PASS — the control is correctly set.
        /// SKIP — not applicable on this server.
        /// INFO — an informational note.
        /// WARN — the check ran and found nothing wrong, but could not fully assess the target
        ///        (under-privileged caller). Added by ruling #4; before that it was force-failed,
        ///        which reported a permissions gap to the client as a failed server check.
        ///
        /// "Not a failure" is NOT "a pass": only PASS is an assertion the control is correct.
        /// SKIP/INFO/WARN are all excluded from scoring by CheckClassification.IsScorable, and
        /// each renders as its own distinct state. Anything else (including an empty/unknown
        /// token) stays a failure — this set fails CLOSED by construction.
        /// </summary>
        internal static bool IsNotFailureVerdict(string upperTrimmedVerdict) =>
            upperTrimmedVerdict is "PASS" or "SKIP" or "INFO" or "WARN";

        // Derive the Windows host from a SQL server name: strip the named instance (\) and port (,),
        // and map local aliases to this machine. Test-Dba* -ComputerName wants the host, not the instance.
        // Internal for test visibility (InternalsVisibleTo SQLTriage.Tests).
        //
        // 2026-07-21 adjudication: dropping the port HERE IS CORRECT and stays. This is the one
        // comma-handling site in the sweep that legitimately wants a host — the value is passed to
        // dbatools -ComputerName, an OS-level machine argument, and a machine has neither an
        // instance nor a port. Routed through ServerAddress.HostOnly so it shares the primitive
        // rather than re-deriving it (and so it also strips a "host:port" form, which the old
        // inline Split pair did not).
        internal static string ResolveHostName(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return Environment.MachineName;
            var host = SQLTriage.Data.Services.ServerAddress.HostOnly(serverName);
            if (host.Length == 0 || host == "."
                || host.Equals("(local)", StringComparison.OrdinalIgnoreCase)
                || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host.StartsWith("127.", StringComparison.Ordinal))
                return Environment.MachineName;
            return host;
        }

        /// <summary>
        /// X-1 (FM-3): one cheap probe per run to learn the server's EngineEdition so
        /// edition-gated checks can SKIP rather than hard-error (the Azure version-trap).
        /// Returns 0 on any failure ("unknown" → no check is skipped, preserving prior
        /// behaviour). Uses a short timeout so a slow/blocked server can't stall the run.
        /// </summary>
        /// <summary>
        /// Instance-seat capture: fingerprints the instance and claims a seat on the FIRST successful
        /// probe. Runs once per server per run, right beside DetectEngineEditionAsync (the same
        /// one-cheap-probe-per-run precedent), so it adds a single round trip and no per-object chatter.
        ///
        /// A seat is claimed at PROBE time, never at add time — so configuring a server that turns out
        /// to be unreachable costs the operator nothing. If the probe fails we do NOT seat and do NOT
        /// invent a fingerprint: the instance simply will not be reported, which the excluded-banner
        /// states honestly.
        ///
        /// Fully isolated: a seat-register fault must never fail or alter an assessment run.
        /// </summary>
        private async Task TryCaptureFingerprintAsync(string connectionString, string serverName, CancellationToken ct)
        {
            if (_seats == null) return;
            try
            {
                var fp = await SQLTriage.Data.Services.Licensing.InstanceFingerprintProbe
                    .TryProbeAsync(connectionString, ct);
                if (fp == null)
                {
                    _logger.LogInformation(
                        "[SEATS] Could not fingerprint {Server} — it will not be seated and its results will not be reported.",
                        LogAnon.S(serverName));
                    return;
                }

                var decision = _seats.ClaimOnProbe(fp, serverName);
                if (!decision.Allowed)
                    _logger.LogWarning("[SEATS] {Server} is not covered by your licence: {Reason}",
                        LogAnon.S(serverName), decision.Reason);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SEATS] Seat capture failed for {Server} (assessment unaffected)",
                    LogAnon.S(serverName));
            }
        }

        private static async Task<int> DetectEngineEditionAsync(string connectionString, CancellationToken ct)
        {
            try
            {
                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(ct);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int);";
                cmd.CommandTimeout = 10;
                var scalar = await cmd.ExecuteScalarAsync(ct);
                return scalar != null && scalar != DBNull.Value ? Convert.ToInt32(scalar) : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool EvaluateRowCount(int rowCount, SqlCheck check)
        {
            var condition = (check.RowCountCondition ?? "equals").ToLowerInvariant();
            return condition switch
            {
                // SQLTriage format (snake_case)
                "equals" => rowCount == check.ExpectedValue,
                "greater_than" => rowCount > check.ExpectedValue,
                "less_than" => rowCount < check.ExpectedValue,
                "not_equals" => rowCount != check.ExpectedValue,

                // SQLMonitoring format (PascalCase with embedded value)
                "equals0" => rowCount == 0,
                "greaterthan0" => rowCount > 0,
                "lessthan" => rowCount < check.ExpectedValue,
                "notequals0" => rowCount != 0,

                _ => rowCount == check.ExpectedValue
            };
        }

        // ────────────────────── Result Storage ──────────────────────

        private void StoreResult(string instanceName, CheckResult result)
        {
            var list = _resultsByInstance.GetOrAdd(instanceName, _ => new List<CheckResult>());

            lock (list)
            {
                list.Insert(0, result);
                while (list.Count > _maxResultsPerInstance)
                    list.RemoveAt(list.Count - 1);
            }

            // Persist for restart survival
            try { _historyService?.RecordCheckResult(instanceName, result); } catch { /* best-effort */ }
        }

        /// <summary>
        /// P3: freezes the first-assessment baseline for a server the first time a FULL
        /// run completes (all enabled checks). A partial/filtered run (e.g. a Critical-only
        /// Quick Check) must never become the frozen "gospel", so we require the run to cover
        /// every enabled check. No-op if no history service is wired or a baseline already
        /// exists. Best-effort — a baseline failure must never fail the scan. Composite score
        /// is the latest persisted health snapshot, falling back to the run's pass rate
        /// (transparent, no estimation) when the dashboard hasn't computed a health score yet.
        /// </summary>
        private void MaybeFreezeFirstBaseline(string serverName, IReadOnlyList<CheckResult> results, int enabledCount)
        {
            if (_historyService == null || results == null || results.Count == 0) return;
            // Only a complete run is gospel-worthy. enabledCount<=0 is a degenerate guard.
            if (enabledCount <= 0 || results.Count < enabledCount) return;
            try
            {
                if (_historyService.HasBaseline(serverName)) return;

                var composite = ResolveComposite(serverName, results);
                _historyService.RecordBaseline(serverName, composite, results, reason: null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to freeze first baseline for {Server}", LogAnon.S(serverName));
            }
        }

        /// <summary>Transparent pass-rate composite (0-100): passed / total over the SCORABLE
        /// subset, rounded. A fallback only — not the canonical health score.
        ///
        /// 2026-07-20 (ruling #4): moved off raw <c>r.Passed</c> onto
        /// <see cref="CheckClassification.IsScorable"/> + <see cref="CheckClassification.CountsAsPass"/>,
        /// the same basis <see cref="GovernanceService"/> and <see cref="GovernanceHistoryService.RecordBaseline"/>
        /// already use. On the raw basis every not-a-failure verdict rode the numerator: a
        /// {PASS, FAIL} run scored 50, and adding a single WARN to it scored 67 — a run that got
        /// LESS visibility scored BETTER, which is the false-clean this whole lane exists to kill.
        /// SKIP and INFO inflated it the same way and had done since they joined the Passed tier.
        ///
        /// Both the numerator AND the denominator are the scorable subset, so an unassessable
        /// check neither flatters the score nor drags it down — the rate answers "of what this
        /// run could actually assess, how much passed", which is the only honest question a
        /// partially-blind run can answer. A run with NOTHING scorable returns 0, matching the
        /// empty-results guard: no assessment made, no score claimed.
        /// </summary>
        private static int PassRateScore(IReadOnlyList<CheckResult> results)
        {
            if (results.Count == 0) return 0;
            var scorable = results.Where(CheckClassification.IsScorable).ToList();
            if (scorable.Count == 0) return 0;
            var passed = scorable.Count(CheckClassification.CountsAsPass);
            return (int)Math.Round(100.0 * passed / scorable.Count);
        }

        /// <summary>
        /// The composite score to stamp on a baseline / transition: the latest persisted health
        /// snapshot when it's a real value, else the transparent pass-rate fallback. A persisted
        /// 0 is treated as "not computed" (no health snapshot yet), NOT a real score of zero —
        /// otherwise a server the dashboard never scored would baseline at 0 and every later run
        /// would read as a huge phantom improvement. Best-effort; any failure → pass-rate.
        /// </summary>
        private int ResolveComposite(string serverName, IReadOnlyList<CheckResult> results)
        {
            if (_historyService == null) return PassRateScore(results);
            try
            {
                var latest = _historyService
                    .GetLatestHealthScoreAsync(serverName, DateTime.UtcNow.Date.AddDays(-1))
                    .GetAwaiter().GetResult();
                return latest is > 0 ? latest.Value : PassRateScore(results);
            }
            catch { return PassRateScore(results); }
        }

        /// <summary>
        /// P3: explicit re-baseline action (a milestone — e.g. "post-remediation").
        /// Supersedes the server's current baseline with the most recent full result set.
        /// <paramref name="reason"/> is required so the timeline records WHY the gospel moved.
        /// Returns the new baseline id, or 0 if there's no history service or no results.
        /// </summary>
        public long ReBaseline(string serverName, string reason)
        {
            if (_historyService == null || string.IsNullOrWhiteSpace(serverName)) return 0;
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("A re-baseline reason is required.", nameof(reason));

            var results = GetResults(serverName, maxCount: int.MaxValue);
            if (results.Count == 0)
            {
                _logger.LogWarning("ReBaseline for {Server} skipped — no results to freeze", LogAnon.S(serverName));
                return 0;
            }

            var composite = ResolveComposite(serverName, results);
            return _historyService.RecordBaseline(serverName, composite, results, reason);
        }

        /// <summary>P3: baseline-vs-current transitions for a server (Fail→Pass / Pass→Fail
        /// + health delta). Null if no history service or no active baseline. Uses the
        /// current persisted result set as "current".</summary>
        public CheckTransitionResult? GetBaselineTransitions(string serverName)
        {
            if (_historyService == null) return null;
            var results = GetResults(serverName, maxCount: int.MaxValue);
            var composite = ResolveComposite(serverName, results);
            return _historyService.ComputeTransitions(serverName, results, composite);
        }

        // ────────────────────── Queries ──────────────────────

        /// <summary>
        /// Gets the most recent results for an instance, optionally filtered.
        /// Hot cache holds the most recent run; older data is fetched from SQLite
        /// (GovernanceHistoryService) on demand to keep RAM bounded.
        ///
        /// SEAT-GATED: returns an EMPTY list for an instance not covered by the licence (audits and
        /// reports may only include seated instances). Legacy/unlimited licences are unaffected.
        /// Callers that list servers must surface <see cref="GetExcludedServers"/> so the omission
        /// is stated, never silent.
        /// </summary>
        public List<CheckResult> GetResults(string instanceName, int maxCount = 50,
            string? category = null, bool? passedOnly = null)
        {
            // ── Instance-seat gate ────────────────────────────────────────────────────
            // Only SEATED ("coded") instances may contribute to audits and reports. An unseated
            // instance yields NO results rather than partial ones — a half-reported server is worse
            // than an absent one, because it reads as a clean bill.
            //
            // This is NOT a silent drop: every surface that lists servers gets the excluded set from
            // GetServerSeatFilter()/GetExcludedServers() and renders the honest banner naming what
            // was left out and why. If you add a caller, wire the banner too.
            //
            // Unlimited/legacy licences short-circuit inside IsSeated, so this costs one dictionary
            // lookup on the hot path and nothing at all for the grandfathered case.
            if (_seats != null && !_seats.IsSeated(instanceName))
                return new List<CheckResult>();

            List<CheckResult> hot;
            if (_resultsByInstance.TryGetValue(instanceName, out var list))
            {
                lock (list)
                {
                    hot = new List<CheckResult>(list);
                }
            }
            else
            {
                hot = new List<CheckResult>();
            }

            // Re-hydrate from disk if the hot cache is short of what was asked.
            // Most consumers ask for 500–1000 to render a dashboard; the hot cache
            // is capped at 50 to keep RAM down, so the dashboard depends on persisted
            // data for the rest. Order of preference: JSON file (richest), SQLite (fallback).
            if (hot.Count < maxCount)
            {
                var seen = new HashSet<string>(hot.Select(r => r.CheckId), StringComparer.OrdinalIgnoreCase);

                // Primary: JSON per-server store
                if (_resultStore != null)
                {
                    try
                    {
                        var fromJson = _resultStore.ReadLatestRun(instanceName);
                        if (fromJson != null)
                        {
                            foreach (var r in fromJson)
                                if (seen.Add(r.CheckId)) hot.Add(r);
                        }
                    }
                    catch { /* best-effort */ }
                }

                // Fallback: SQLite history (used when JSON file is missing for this server)
                if (hot.Count < maxCount && _historyService != null)
                {
                    try
                    {
                        var fromHistory = _historyService.LoadLatestCheckResults(instanceName);
                        foreach (var r in fromHistory)
                            if (seen.Add(r.CheckId)) hot.Add(r);
                    }
                    catch { /* best-effort */ }
                }
            }

            IEnumerable<CheckResult> query = hot;
            if (category != null)
                query = query.Where(r => r.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
            if (passedOnly.HasValue)
            {
                // 2026-07-20 sweep: was `r.Passed == passedOnly.Value`. WARN/SKIP/INFO ride
                // Passed=true, so ?passedOnly=true handed an RMM/PSA integration every
                // "could not fully assess" result as a PASSING check. Scoped to scorable results:
                // a non-assertion is neither a pass nor a failure, so it belongs in NEITHER
                // filtered view — an unfiltered call still returns it.
                query = query.Where(r => CheckClassification.IsScorable(r) && r.Passed == passedOnly.Value);
            }
            var results = query.Take(maxCount).ToList();

            // F6: annotate acceptances ON READ — the stored/persisted result stays truthful
            // (Passed unchanged), but an accepted not-passed finding is flagged so it rides the
            // Passed tier in score math and renders an "Accepted" badge. Re-evaluated every call
            // (acceptances can be added/revoked/expired between reads); idempotent by design.
            AnnotateAcceptances(results);
            return results;
        }

        /// <summary>
        /// The rows THIS PROCESS's most recent run produced for an instance, and nothing else.
        /// Empty when no run has executed for that instance since start-up (a cold page restoring
        /// from history is not a run) and empty for an instance the licence does not seat, on the
        /// same terms as <see cref="GetResults"/>.
        ///
        /// <para>Use this, not <see cref="GetResults"/>, when the answer has to be "what did THIS
        /// run cover". GetResults answers a different question — "the best current picture of this
        /// instance" — and fills its capped hot cache from the persisted latest-run store, so after
        /// a wide run a narrow one reads back the wide run's rows as well. The audit page renders a
        /// coverage sentence beside those rows, and a sentence beside a verdict has to be
        /// conditioned on the same measurement that produced it.</para>
        ///
        /// <para>Acceptances are annotated on read, exactly as GetResults does, so the two agree
        /// about which findings are accepted.</para>
        /// </summary>
        public List<CheckResult> GetLastRunResults(string instanceName)
        {
            if (string.IsNullOrWhiteSpace(instanceName)) return new List<CheckResult>();

            // Seat gate: same rule and same reason as GetResults — an unseated instance yields no
            // rows rather than partial ones. Callers listing servers surface GetExcludedServers().
            if (_seats != null && !_seats.IsSeated(instanceName)) return new List<CheckResult>();

            var rows = _lastRunResults.TryGetValue(instanceName, out var list)
                ? new List<CheckResult>(list)
                : new List<CheckResult>();

            AnnotateAcceptances(rows);
            return rows;
        }

        /// <summary>
        /// F6: stamp <see cref="CheckResult.IsAccepted"/> (+ reason/who/expiry) on each
        /// not-passed result that has a live client acceptance for this instance. Resets the
        /// flag first so a revoked/expired acceptance clears on the next read. No-op when the
        /// accepted-findings layer isn't wired (tests / absent DI).
        ///
        /// <para>PUBLIC because a caller re-applying the overlay after an accept/revoke must apply
        /// THIS rule to the rows already on screen, rather than re-reading them from a store that
        /// would bring other rows with them. One rule, one place: a second in-page copy is how the
        /// grid's badge and the executor's own reads come to disagree about one finding.</para>
        /// </summary>
        public void AnnotateAcceptances(IList<CheckResult>? results)
        {
            if (_acceptedFindings == null || results == null) return;
            foreach (var r in results)
            {
                // Passing checks are never "accepted" — acceptance only applies to a finding.
                //
                // Ruling #4 (2026-07-20): WARN is exempt from that clearing. WARN rides Passed=true
                // ("not a failure"), so the raw r.Passed test below would strip the acceptance
                // metadata off any accepted finding that later degrades to WARN — the client's
                // sign-off would silently vanish from the badge/tooltip the moment their audit
                // account lost visibility, and reappear when it came back. An acceptance is a
                // statement about the FINDING, not about this run's visibility, so it survives a
                // run that could not fully assess the check. IsScorable already keeps WARN out of
                // every score, so preserving the flag here cannot move any number.
                if (r.Passed && !CheckClassification.IsWarn(r))
                {
                    r.IsAccepted = false;
                    r.AcceptanceReason = null;
                    r.AcceptedBy = null;
                    r.AcceptanceExpiresAt = null;
                    continue;
                }

                var acceptance = _acceptedFindings.GetAcceptance(r.InstanceName, r.CheckId);
                if (acceptance != null)
                {
                    r.IsAccepted = true;
                    r.AcceptanceReason = acceptance.Reason;
                    r.AcceptedBy = acceptance.AcceptedBy;
                    r.AcceptanceExpiresAt = acceptance.ExpiresAt;
                }
                else
                {
                    r.IsAccepted = false;
                    r.AcceptanceReason = null;
                    r.AcceptedBy = null;
                    r.AcceptanceExpiresAt = null;
                }
            }
        }

        /// <summary>
        /// Gets the most recent result for a specific check on a specific instance.
        /// </summary>
        public CheckResult? GetLatestResult(string instanceName, string checkId)
        {
            if (!_resultsByInstance.TryGetValue(instanceName, out var list))
                return null;

            lock (list)
            {
                return list.FirstOrDefault(r => r.CheckId == checkId);
            }
        }

        /// <summary>
        /// Gets the last execution summary for an instance.
        /// </summary>
        public CheckExecutionSummary? GetLastSummary(string instanceName)
        {
            _lastSummary.TryGetValue(instanceName, out var summary);
            return summary;
        }

        /// <summary>
        /// Gets summaries for all instances that have been checked.
        /// </summary>
        public Dictionary<string, CheckExecutionSummary> GetAllSummaries()
        {
            return new Dictionary<string, CheckExecutionSummary>(_lastSummary);
        }

        /// <summary>
        /// Gets the SEATED instance names that have results (in-memory hot set only).
        /// Instances not covered by the licence are excluded — pair with
        /// <see cref="GetExcludedServers"/> for the honest banner. Unfiltered on a legacy/unlimited
        /// licence.
        /// </summary>
        public List<string> GetMonitoredInstances()
        {
            // Seat-gated (audits/reports lane — see the _seats field comment for why live monitoring
            // is unaffected). Pair with GetExcludedServers() to render the honest banner.
            var all = _resultsByInstance.Keys.ToList();
            return _seats == null ? all : _seats.Filter(all).Seated.ToList();
        }

        /// <summary>
        /// The EXCLUDED signal that pairs with every seat-filtered read on this service.
        /// Instances that HAVE results but are not covered by the licence — so a surface can say
        /// "3 instances excluded: not covered by your licence" instead of quietly showing fewer
        /// servers than the operator configured. Empty on an unlimited/legacy licence.
        /// </summary>
        public List<string> GetExcludedServers() => GetServerSeatFilter().Excluded.ToList();

        /// <summary>
        /// Both halves at once — seated + excluded, over every server with results (hot cache UNION
        /// the persisted store). Prefer this over calling GetServersWithResults() and
        /// GetExcludedServers() separately: it is one projection, so the two lists cannot disagree
        /// if the register changes between calls.
        /// </summary>
        public SQLTriage.Data.Services.Licensing.SeatFilter GetServerSeatFilter()
        {
            var all = AllServersWithResultsUnfiltered();
            return _seats == null
                ? new SQLTriage.Data.Services.Licensing.SeatFilter(all, Array.Empty<string>())
                : _seats.Filter(all);
        }

        /// <summary>Every server with results, BEFORE the seat gate. Internal: the licensed
        /// surfaces must go through the filtered reads so the banner stays honest.</summary>
        private List<string> AllServersWithResultsUnfiltered()
        {
            var set = new HashSet<string>(_resultsByInstance.Keys, StringComparer.OrdinalIgnoreCase);
            if (_resultStore != null)
            {
                foreach (var s in _resultStore.GetServersWithRuns())
                    set.Add(s);
            }
            return set.ToList();
        }

        /// <summary>
        /// The SEATED instance names that have corpus results available — the in-memory hot set
        /// UNION any server with a persisted run on disk, then filtered to instances covered by the
        /// licence. Estate report roll-ups use this so corpus-only servers (checked via the suite
        /// but never Microsoft-VA-scanned, or surviving a restart via the JSON store) are still
        /// discovered. The in-memory-only <see cref="GetMonitoredInstances"/> would miss
        /// persisted-but-not-yet-rehydrated servers.
        ///
        /// The filter is NOT a silent drop: callers pair this with <see cref="GetExcludedServers"/>
        /// (or take both at once from <see cref="GetServerSeatFilter"/>) and render the honest
        /// banner. On an unlimited/legacy licence nothing is filtered and the excluded set is empty.
        /// </summary>
        public List<string> GetServersWithResults() => GetServerSeatFilter().Seated.ToList();

        /// <summary>
        /// Clears all stored results for a specific instance.
        /// </summary>
        public void ClearResults(string instanceName)
        {
            _lastRunResults.TryRemove(instanceName, out _);
            if (_resultsByInstance.TryRemove(instanceName, out _))
            {
                _logger.LogDebug("Cleared results for {Instance}", instanceName);
            }
        }

        /// <summary>
        /// Clears all stored results for all instances.
        /// </summary>
        public void ClearAllResults()
        {
            _resultsByInstance.Clear();
            _lastSummary.Clear();
            _lastRunResults.Clear();
            _logger.LogDebug("Cleared all check results");
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                foreach (var throttle in _instanceThrottles.Values)
                    throttle.Dispose();
                foreach (var errorLogLock in _errorLogFamilyLocks.Values)
                    errorLogLock.Dispose();
                _disposed = true;
            }
        }

        // ── Permission-denied → SKIP mapping ────────────────────────────────────
        //
        // When a check's SQL fails because the connecting login lacks server- or
        // database-level permission to read what the query needs (e.g. low-priv
        // accounts on a vanilla local SQL instance), we'd rather report SKIP
        // with a human reason than ERROR. SKIP semantics: not applicable on this
        // server; neither passes nor pulls the score down. Reduces "Error" noise
        // on Audit Assessment runs where the operator simply doesn't have all
        // the perms a comprehensive audit assumes.
        //
        // Mapping is conservative: only canonical permission-denied SQL error
        // numbers map to SKIP. Anything else falls through to the catch-all
        // Exception handler and surfaces as ERROR.
        private static bool TryMapPermissionSkip(SqlException sqlEx, out string reason)
        {
            // sqlEx.Errors enumerates all errors in the batch; iterate for safety.
            foreach (SqlError err in sqlEx.Errors)
            {
                switch (err.Number)
                {
                    case 229:   // SELECT permission was denied on object X
                    case 230:   // SELECT permission was denied on column X
                        reason = "missing SELECT permission on a required object";
                        return true;
                    case 262:   // Permission to CREATE/ALTER/... was denied
                    case 297:   // The user does not have permission to perform this action
                        reason = "the connecting login lacks a required permission";
                        return true;
                    case 300:   // VIEW SERVER STATE permission was denied
                        reason = "VIEW SERVER STATE permission required";
                        return true;
                    case 916:   // Cannot open database 'X' requested by the login
                        reason = "cannot open a database required by this check";
                        return true;
                    case 18456: // Login failed (rare for in-session checks; defensive)
                        reason = "login failed during check execution";
                        return true;
                }
            }
            reason = string.Empty;
            return false;
        }

        // ── #28 diagnostic: full SqlException capture ───────────────────────────
        //
        // CheckExecutionService.cs:590-593 previously stored only ex.Message on a check
        // error — the #28 investigation (SQL 2025 RTM "A severe error occurred on the
        // current command", handoff HANDOFF-2026-07-13-slow-burn.md §2) hit exactly this
        // gap: no SQL error Number/Class/State was captured, so the root cause had to be
        // reconstructed from the ERRORLOG / SQLDump files server-side. This walks the
        // exception chain (a caught exception may WRAP the real SqlException) and stamps
        // Number/Class/State/LineNumber/Server onto the result whenever one is found, so
        // the next occurrence — this bug or any other check error — self-diagnoses.
        // Internal for test visibility (InternalsVisibleTo SQLTriage.Tests).
        internal static void ApplySqlExceptionDetails(CheckResult result, Exception ex)
        {
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is SqlException sqlEx)
                {
                    result.SqlErrorNumber = sqlEx.Number;
                    result.SqlErrorClass = sqlEx.Class;
                    result.SqlErrorState = sqlEx.State;
                    result.SqlErrorLineNumber = sqlEx.LineNumber;
                    result.SqlErrorServer = sqlEx.Server;
                    return;
                }
            }
        }
    }
}
