/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Computes a weighted 0-100 Health Score and Risk Rating from five dimensions:
    ///   1. Performance   — saturation wait-stats trend (PAGEIOLATCH/CXPACKET/SOS/LCK_M_*) (weight: governance-weights.json Performance)
    ///   2. Compliance    — corpus-fed governance pass-rate across Compliance dimension; no VA fallback (weight: Compliance)
    ///   3. Security      — corpus-fed governance pass-rate across Security dimension; no VA fallback (weight: Security)
    ///   4. Resource      — CPU / memory saturation from live HealthCheckService (weight: Reliability)
    ///   5. Blocking      — blocking event frequency from BlockingHistoryService (weight: governance-weights.json Blocking)
    ///
    /// Weights are read from Config/governance-weights.json via the same IOptionsMonitor used by
    /// GovernanceService.  Breakdown snapshots are written once per UTC day to governance-history.db
    /// (health_score_history table) for trend rendering.
    /// Performance signal wait types are configurable via Health:PerformanceSignalWaitTypes (appsettings).
    /// </summary>
    public class ExecutiveHealthService
    {
        private readonly HealthCheckService _healthCheckService;
        private readonly GovernanceHistoryService _historyService;
        private readonly BlockingHistoryService _blockingHistory;
        private readonly HistoricalPerformanceService _perfHistory;
        private readonly VulnerabilityAssessmentStateService _vaState;
        private readonly ILogger<ExecutiveHealthService> _logger;

        // P4-ii (2026-07-12): corpus-fed Security/Compliance. Optional (nullable) so unit
        // tests that construct this service directly keep working unchanged; the real DI
        // container always supplies both (ServiceCollectionExtensions registers both as
        // singletons regardless of registration order). Null in a test double ⇒ Security/
        // Compliance fall through to the pre-existing VA-only path.
        private readonly IGovernanceService? _governanceService;
        private readonly CheckExecutionService? _checkExecutionService;

        // B1 (2026-07-13, ruling R-B1): canonical server identity for VA result matching
        // and the "not available on SQL Server 2017" version gate (B3). Optional/nullable
        // so the existing unit tests that construct this service directly keep working
        // unchanged — a null value just falls back to exact-alias matching (prior
        // behaviour) and skips the SQL2017 gate (prior behaviour), never a regression.
        private readonly ConnectionHealthService? _connectionHealth;

        // Governance weights — loaded once from governance-weights.json at startup.
        // Reload is not needed at runtime for v1; the file changes rarely.
        private readonly GovernanceWeightsConfig _weights;

        // Saturation/contention wait-type prefixes for the Performance dimension.
        // Configurable via Health:PerformanceSignalWaitTypes in appsettings.json.
        // Default covers I/O, parallel-query contention, scheduler starvation, and lock waits.
        private static readonly string[] DefaultPerfSignalPrefixes =
        {
            "PAGEIOLATCH_",
            "CXPACKET",
            "CXCONSUMER",
            "SOS_SCHEDULER_YIELD",
            "LCK_M_",
        };

        private readonly string[] _perfSignalPrefixes;

        // One snapshot per server per UTC day — guards against duplicate writes.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>
            _lastSnapshotDate = new(StringComparer.OrdinalIgnoreCase);

        public ExecutiveHealthService(
            HealthCheckService healthCheckService,
            GovernanceHistoryService historyService,
            BlockingHistoryService blockingHistory,
            HistoricalPerformanceService perfHistory,
            VulnerabilityAssessmentStateService vaState,
            ILogger<ExecutiveHealthService> logger,
            IConfiguration? configuration = null,
            IGovernanceService? governanceService = null,
            CheckExecutionService? checkExecutionService = null,
            ConnectionHealthService? connectionHealth = null)
        {
            _healthCheckService = healthCheckService ?? throw new ArgumentNullException(nameof(healthCheckService));
            _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
            _blockingHistory = blockingHistory ?? throw new ArgumentNullException(nameof(blockingHistory));
            _perfHistory = perfHistory ?? throw new ArgumentNullException(nameof(perfHistory));
            _vaState = vaState ?? throw new ArgumentNullException(nameof(vaState));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _governanceService = governanceService;
            _checkExecutionService = checkExecutionService;
            _connectionHealth = connectionHealth;

            _weights = LoadWeights();

            // Load performance signal wait-type prefixes from config, falling back to defaults.
            var configuredTypes = configuration?.GetSection("Health:PerformanceSignalWaitTypes")
                                               .Get<string[]>();
            _perfSignalPrefixes = configuredTypes is { Length: > 0 }
                ? configuredTypes
                : DefaultPerfSignalPrefixes;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Computes a weighted 0-100 health score for the specified server,
        /// including a full per-dimension breakdown with tooltips.
        /// Writes a daily snapshot to governance-history.db for trend data.
        /// </summary>
        public async Task<ExecutiveHealthScore> GetHealthScoreAsync(string serverName)
        {
            try
            {
                var breakdown = await BuildBreakdownAsync(serverName);
                var score = ComputeComposite(breakdown);
                var (trend, trendState) = await GetTrendAsync(serverName, score);

                // Severity is a VERDICT, and a verdict must be conditioned on the measurement
                // that produced it (2026-08-05). With no measured dimension the composite is a
                // forced 0, and ScoreToSeverity(0) used to stamp that server "Critical — requires
                // immediate action" on the strength of nothing. Unknown is the honest read, and
                // EstateHealthPolicy supplies the sentence that says which kind of unknown.
                var measured = breakdown.Dimensions.Any(d => d.State == DimensionState.Measured);
                var severity = measured ? ScoreToSeverity(score) : HealthSeverity.Unknown;

                // Persist snapshot once per UTC day — only when a measurement produced the score.
                MaybeWriteSnapshot(serverName, score, breakdown, measured);

                return new ExecutiveHealthScore
                {
                    Score = score,
                    Severity = severity,
                    Trend = trend,
                    TrendState = trendState,
                    Message = measured ? HealthMessage(severity) : "",
                    LastUpdated = DateTime.Now,
                    Breakdown = breakdown,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetHealthScoreAsync failed for {Server}", serverName);
                return new ExecutiveHealthScore
                {
                    Score = 0,
                    Severity = HealthSeverity.Unknown,
                    Message = "Health data unavailable",
                    Breakdown = new HealthScoreBreakdown(),
                    // The one place entitled to assert this: it watched the collection throw.
                    CollectionFailed = true,
                };
            }
        }

        /// <summary>
        /// Gets health scores for all servers registered with the connection manager.
        /// </summary>
        public async Task<Dictionary<string, ExecutiveHealthScore>> GetAllHealthScoresAsync()
        {
            var allHealth = _healthCheckService.GetAllHealth();
            var results = new Dictionary<string, ExecutiveHealthScore>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in allHealth)
                results[kvp.Key] = await GetHealthScoreAsync(kvp.Key).ConfigureAwait(false);
            return results;
        }

        // ── Dimension builders ────────────────────────────────────────────────

        private async Task<HealthScoreBreakdown> BuildBreakdownAsync(string serverName)
        {
            var breakdown = new HealthScoreBreakdown();

            // Corpus precedence (ruled 2026-07-12): compute the governance score ONCE per
            // server — shared by both Security and Compliance below — when corpus check
            // results exist for this server. Reuses GovernanceService's own CategoryMapping/
            // scoring (Compute path) rather than forking a second mapping; never injects
            // synthetic results into VulnerabilityAssessmentStateService. Null corpusGov
            // (no corpus results yet, or the deps aren't wired) falls through to the
            // pre-existing VA-only path unchanged.
            GovernanceScore? corpusGov = null;
            List<CheckResult>? corpusResults = null;
            if (_checkExecutionService != null && _governanceService != null)
            {
                try
                {
                    var results = _checkExecutionService.GetResults(serverName, maxCount: int.MaxValue);
                    if (results.Count > 0)
                    {
                        corpusResults = results;
                        corpusGov = await _governanceService.ComputeFullAsync(results);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Corpus lookup failed for {Server}; falling back to VA", serverName);
                }
            }

            // ── 1. Performance: wait-stats trend (HistoricalPerformanceService) ──────
            breakdown.Performance = ScorePerformance(serverName);

            // ── 2. Compliance: corpus-fed when available, else governance/VA pass-rate ──
            breakdown.Compliance = ScoreCompliance(serverName, corpusGov, corpusResults);

            // ── 3. Security: corpus-fed when available, else VA security findings ────
            breakdown.Security = ScoreSecurity(serverName, corpusGov, corpusResults);

            // ── 4. Resource: CPU / memory saturation (live HealthCheckService) ───────
            breakdown.Resource = ScoreResource(serverName);

            // ── 5. Blocking: event frequency last 24 h ───────────────────────────────
            breakdown.Blocking = await ScoreBlockingAsync(serverName);

            return breakdown;
        }

        // Performance (0-100): deduct points when avg wait_ms is elevated vs prior day.
        // Only saturation/contention wait types (prefixes in _perfSignalPrefixes) are included.
        // Configurable via Health:PerformanceSignalWaitTypes; defaults: PAGEIOLATCH_*, CXPACKET,
        // CXCONSUMER, SOS_SCHEDULER_YIELD, LCK_M_*.
        private DimensionScore ScorePerformance(string serverName)
        {
            const string dim = "Performance";
            double weight = GetWeight(dim);

            try
            {
                var now = DateTime.UtcNow;
                var allRows = _perfHistory.GetHourlyWaitStats(serverName, now.AddHours(-24), now);

                // Filter to saturation/contention signal wait types only.
                var todayRows = allRows
                    .Where(r => IsPerfSignalWait(r.WaitType))
                    .ToList();

                if (todayRows.Count == 0)
                    return new DimensionScore(dim, weight, 100,
                        "No saturation wait-stat history yet — full points awarded by default.",
                        $"Collects once HistoricalPerformanceService has 24 h of data for signal waits " +
                        $"({string.Join(", ", _perfSignalPrefixes)}).",
                        state: DimensionState.NotAssessed);

                double avgWaitMs = todayRows.Average(r => r.AvgWaitMs);

                // Compare to prior day for trend signal (same wait-type filter)
                var priorAll = _perfHistory.GetHourlyWaitStats(serverName, now.AddHours(-48), now.AddHours(-24));
                var priorRows = priorAll.Where(r => IsPerfSignalWait(r.WaitType)).ToList();
                double priorAvg = priorRows.Count > 0 ? priorRows.Average(r => r.AvgWaitMs) : avgWaitMs;

                // Score: 100 at ≤50ms avg wait, linear decay to 0 at ≥1500ms
                double rawScore = Math.Max(0, 100 - (avgWaitMs - 50) / 14.5);
                int score = (int)Math.Round(Math.Clamp(rawScore, 0, 100));

                double delta = priorRows.Count > 0 ? avgWaitMs - priorAvg : 0;
                string direction = delta > 10 ? "degrading" : delta < -10 ? "improving" : "stable";

                return new DimensionScore(dim, weight, score,
                    $"Avg saturation wait {avgWaitMs:F0} ms over last 24 h ({direction} vs prior day). " +
                    $"Score = {score}/100.",
                    $"This dimension contributes {weight * score:F0} of {weight * 100:F0} possible points " +
                    $"(weight {weight * 100:F0}%). " +
                    $"Signal wait types: {string.Join(", ", _perfSignalPrefixes)}. " +
                    $"Source: {todayRows.Count} matching hourly rows from HistoricalPerformanceService.");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Performance score failed for {Server}", serverName);
                return new DimensionScore(dim, weight, 100,
                    "Performance collection failed — nothing was measured.",
                    "Reading wait-stat history for this server threw. Nothing is claimed about "
                    + "performance; this dimension is excluded from the score, not assumed healthy.",
                    state: DimensionState.CollectionFailed);
            }
        }

        /// <summary>Returns true if the wait type matches any configured performance signal prefix.</summary>
        private bool IsPerfSignalWait(string? waitType)
        {
            if (string.IsNullOrEmpty(waitType)) return false;
            foreach (var prefix in _perfSignalPrefixes)
            {
                if (waitType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// VA results scoped to one server. Results carry server identity in
        /// ThisServer (per-server runs; TargetName as fallback for server-level
        /// checks). Without this filter every server card scored the whole
        /// estate's findings.
        /// B1 (2026-07-13): match on CANONICAL identity, not the raw alias — a VA run
        /// stamps ThisServer with whatever alias was used at run time, which can differ
        /// from the alias GetHealthScoreAsync is called with for the same physical
        /// instance. ResolveCanonical falls back to the input string when unresolved,
        /// so this is a strict superset of the old exact-match (no regression).
        /// </summary>
        private List<AssessmentResult> VaResultsForServer(string serverName)
        {
            var canonicalTarget = _connectionHealth?.ResolveCanonical(serverName) ?? serverName;
            return _vaState.Results.Where(r =>
                string.Equals(string.IsNullOrEmpty(r.ThisServer) ? r.ThisServer : (_connectionHealth?.ResolveCanonical(r.ThisServer) ?? r.ThisServer), canonicalTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(string.IsNullOrEmpty(r.TargetName) ? r.TargetName : (_connectionHealth?.ResolveCanonical(r.TargetName) ?? r.TargetName), canonicalTarget, StringComparison.OrdinalIgnoreCase))
            .ToList();
        }

        // Compliance (0-100): corpus-fed ONLY via GovernanceService's shared CategoryMapping
        // (ruled 2026-07-16, Builder A: VA-fallback removed — Executive Health never scores
        // from Vulnerability Assessment; VA appears only on its own page). When corpus
        // results exist for this server (PRECEDENCE ruled 2026-07-12 — corpus wins even when
        // VA results also exist; VA demotes to a corroboration note only). No corpus results
        // (or the corpus run didn't assess Compliance this time) → honest not-assessed.
        private DimensionScore ScoreCompliance(string serverName, GovernanceScore? corpusGov, List<CheckResult>? corpusResults)
        {
            const string dim = "Compliance";
            double weight = GetWeight(dim);

            try
            {
                if (corpusGov != null && corpusGov.Categories.TryGetValue(dim, out var cat) && cat.Assessed)
                {
                    int corpusScore = (int)Math.Round(Math.Clamp(cat.RawScore, 0, 100));
                    var asOf = corpusResults!.Max(r => r.ExecutedAt);
                    var vaNote = VaResultsForServer(serverName).Count > 0
                        ? " A Vulnerability Assessment also ran for this server; corpus results take precedence (VA is corroboration only)."
                        : "";

                    return new DimensionScore(dim, weight, corpusScore,
                        // Fixed 2026-07-12: do not pair a raw pass-count fraction with the
                        // weighted RawScore percentage — they answer different questions and
                        // rarely match when check weights are non-uniform (e.g. 28/44 = 64% of
                        // checks pass, but the effort-weighted score can land at 46/100).
                        $"{cat.PassedCount}/{cat.FindingCount} compliance checks passing; weighted score " +
                        $"{corpusScore}/100 from {cat.FindingCount} corpus checks.{vaNote}",
                        $"This dimension contributes {weight * corpusScore:F0} of {weight * 100:F0} possible points " +
                        $"(weight {weight * 100:F0}%). Source: corpus check results (CheckExecutionService), " +
                        $"category 'Compliance' per GovernanceWeights.CategoryMapping — the same mapping the " +
                        $"Governance Maturity sub-lens uses (no forked scoring).",
                        source: DimensionSource.Corpus, checkCount: cat.FindingCount, asOf: asOf);
                }

                // Honesty ruling (2026-07-16, Builder A): Executive Health is corpus-fed
                // ONLY. No corpus results for this server (or Compliance unassessed by the
                // corpus run this time) → honest not-assessed, modelled on the former
                // SQL2017-gate pattern (hasData:false excludes it from the composite —
                // not a pass, not a fail, absence of evidence). NEVER fall back to
                // Vulnerability Assessment here — VA now appears only on its own page
                // (Pages/VulnerabilityAssessment.razor) and in the gated Audit Evidence
                // export, never as a silent stand-in for a missing corpus run.
                return new DimensionScore(dim, weight, 100,
                    "Not yet assessed — no corpus check results for this server.",
                    "Run checks against this server to populate the Compliance score. " +
                    "Vulnerability Assessment results are shown separately on the Vulnerability " +
                    "Assessment page and no longer feed Executive Health.",
                    state: DimensionState.NotAssessed);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Compliance score failed");
                return new DimensionScore(dim, weight, 100,
                    "Compliance collection failed — nothing was measured.",
                    "Reading corpus check results for this server threw. Nothing is claimed about "
                    + "compliance; this dimension is excluded from the score, not assumed healthy.",
                    state: DimensionState.CollectionFailed);
            }
        }

        // Security (0-100): corpus-fed ONLY via GovernanceService's shared CategoryMapping
        // (ruled 2026-07-16, Builder A: VA-fallback removed — Executive Health never scores
        // from Vulnerability Assessment; VA appears only on its own page). When corpus
        // results exist for this server (PRECEDENCE ruled 2026-07-12 — corpus wins even when
        // VA results also exist; VA demotes to a corroboration note only). No corpus results
        // (or the corpus run didn't assess Security this time) → honest not-assessed.
        private DimensionScore ScoreSecurity(string serverName, GovernanceScore? corpusGov, List<CheckResult>? corpusResults)
        {
            const string dim = "Security";
            double weight = GetWeight(dim);

            try
            {
                if (corpusGov != null && corpusGov.Categories.TryGetValue(dim, out var cat) && cat.Assessed)
                {
                    int corpusScore = (int)Math.Round(Math.Clamp(cat.RawScore, 0, 100));
                    var asOf = corpusResults!.Max(r => r.ExecutedAt);
                    var vaNote = VaResultsForServer(serverName).Count > 0
                        ? " A Vulnerability Assessment also ran for this server; corpus results take precedence (VA is corroboration only)."
                        : "";

                    return new DimensionScore(dim, weight, corpusScore,
                        // Fixed 2026-07-12: see the matching Compliance note above — the raw pass
                        // fraction and the effort-weighted score are different numbers and must
                        // not be printed as if one equals the other.
                        $"{cat.PassedCount}/{cat.FindingCount} security checks passing; weighted score " +
                        $"{corpusScore}/100 from {cat.FindingCount} corpus checks.{vaNote}",
                        $"This dimension contributes {weight * corpusScore:F0} of {weight * 100:F0} possible points " +
                        $"(weight {weight * 100:F0}%). Source: corpus check results (CheckExecutionService), " +
                        $"category 'Security' per GovernanceWeights.CategoryMapping — the same mapping the " +
                        $"Governance Maturity sub-lens uses (no forked scoring).",
                        source: DimensionSource.Corpus, checkCount: cat.FindingCount, asOf: asOf);
                }

                // Honesty ruling (2026-07-16, Builder A): see the matching Compliance note
                // above — no corpus results for this server (or Security unassessed by the
                // corpus run this time) → honest not-assessed. NEVER fall back to
                // Vulnerability Assessment here — VA now appears only on its own page and
                // in the gated Audit Evidence export, never as a silent stand-in for a
                // missing corpus run.
                return new DimensionScore(dim, weight, 100,
                    "Not yet assessed — no corpus check results for this server.",
                    "Run checks against this server to populate the Security score. " +
                    "Vulnerability Assessment results are shown separately on the Vulnerability " +
                    "Assessment page and no longer feed Executive Health.",
                    state: DimensionState.NotAssessed);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Security score failed");
                return new DimensionScore(dim, weight, 100,
                    "Security collection failed — nothing was measured.",
                    "Reading corpus check results for this server threw. Nothing is claimed about "
                    + "security; this dimension is excluded from the score, not assumed healthy.",
                    state: DimensionState.CollectionFailed);
            }
        }

        // Resource (0-100): CPU / memory / blocking saturation from live ServerHealthStatus.
        // Uses Reliability weight since resource saturation is a reliability concern.
        private DimensionScore ScoreResource(string serverName)
        {
            const string dim = "Resource";
            double weight = GetWeight("Reliability");

            try
            {
                var status = _healthCheckService.GetCachedHealth(serverName);
                if (status == null)
                    // No poll yet ≠ unhealthy. Consistent with every other dimension
                    // (and this service's documented "no data → full points" contract):
                    // absence of data must not drag the composite down.
                    return new DimensionScore(dim, weight, 100,
                        "No live health data yet — full points awarded by default.",
                        "Score will reflect real resource saturation after the first health poll.",
                        state: DimensionState.NotAssessed);

                // Gate fix R11 (2026-08-05). IsOnline is bool?, and the branch below caught BOTH
                // false and null, printing "the server did not answer" for each. One of those is
                // the FIRST-POLL WINDOW: HealthCheckService creates the cache row and sets
                // IsLoading before it opens a connection, so between those two moments the row
                // exists, IsOnline is still null, and the server is mid-question. "Did not answer"
                // is a measurement, and in that window nobody has taken it yet.
                //
                // Discriminated on IsLoading, and deliberately narrow. A repoll of a server that
                // has answered before leaves IsOnline true while IsLoading is set, so this fires
                // on the first poll only. A poll that has FINISHED with IsOnline still null threw
                // on the way out, which is a real negative observation, and it keeps the branch
                // below.
                if (status.IsOnline == null && status.IsLoading)
                    return new DimensionScore(dim, weight, 100,
                        "The first health poll for this server has not come back yet.",
                        "This server is still being asked. Nothing is claimed about its resource "
                        + "saturation either way; the dimension is excluded from the score until "
                        + "the poll answers, not assumed healthy and not reported unreachable.",
                        state: DimensionState.NotAssessed);

                if (status.IsOnline != true)
                    // ROOT CAUSE, fixed 2026-08-05. This branch used to omit the state
                    // argument, so the ctor default made "the server did not answer" read
                    // as a MEASURED 0. One missing argument printed four claims nothing had
                    // measured: the CIO estate mean and its "N assessed servers" caption, the
                    // /dba Resource card's "0/100", and the portal daily summary's health
                    // block, whose own guard comment promises it never emits a fabricated
                    // value. What was measured here is that the server did not answer. That
                    // is not a resource-saturation score, so no score is claimed.
                    return new DimensionScore(dim, weight, 0,
                        "Server did not answer — resource saturation was not measured.",
                        "Nothing is claimed about this server's resource saturation. The 0 is a "
                        + "placeholder excluded from the composite, not a finding; read MeasuredScore, "
                        + "which is null in this state.",
                        state: DimensionState.Unreachable);

                int score = 100;

                // CPU: up to −30
                if (status.TotalCpuPercent.HasValue)
                {
                    if (status.TotalCpuPercent >= 95) score -= 30;
                    else if (status.TotalCpuPercent >= 80) score -= 18;
                    else if (status.TotalCpuPercent >= 60) score -= 8;
                }

                // Memory waits: up to −25
                if (status.RequestsWaitingForMemory > 0) score -= 25;
                else if (status.MemorySeverity == HealthSeverity.Warning) score -= 12;

                // Thread starvation: up to −15
                if (status.ThreadsSeverity == HealthSeverity.Critical) score -= 15;
                else if (status.ThreadsSeverity == HealthSeverity.Warning) score -= 8;

                // Waits: up to −10
                if (status.WaitSeverity == HealthSeverity.Critical) score -= 10;
                else if (status.WaitSeverity == HealthSeverity.Warning) score -= 5;

                score = Math.Clamp(score, 0, 100);

                string cpuText = status.TotalCpuPercent.HasValue
                    ? $"CPU {status.TotalCpuPercent:F0}%"
                    : "CPU unknown";

                string memText = status.RequestsWaitingForMemory > 0
                    ? $"{status.RequestsWaitingForMemory} requests waiting for memory"
                    : $"memory {status.MemorySeverity}";

                return new DimensionScore(dim, weight, score,
                    $"{cpuText}, {memText}, threads {status.ThreadsSeverity}. Score = {score}/100.",
                    $"This dimension contributes {weight * score:F0} of {weight * 100:F0} possible points " +
                    $"(weight {weight * 100:F0}%, uses Reliability weight). " +
                    $"Source: live HealthCheckService poll.");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Resource score failed for {Server}", serverName);
                return new DimensionScore(dim, weight, 100,
                    "Resource collection failed — nothing was measured.",
                    "Reading the live health poll for this server threw. Nothing is claimed about "
                    + "resource saturation; this dimension is excluded from the score, not assumed healthy.",
                    state: DimensionState.CollectionFailed);
            }
        }

        // Blocking (0-100): event count over last 24 h from BlockingHistoryService.
        // Weight read directly from governance-weights.json "Blocking" key (first-class dimension).
        private async Task<DimensionScore> ScoreBlockingAsync(string serverName)
        {
            const string dim = "Blocking";
            double weight = GetWeight("Blocking");

            try
            {
                var offenders = await _blockingHistory.GetTopOffendersAsync(
                    serverName, DateTime.UtcNow.AddHours(-24), topN: 20);

                int totalEvents = offenders.Sum(o => o.EventCount);
                int totalDuration = offenders.Sum(o => o.TotalDurationSeconds);

                // Score: 100 at 0 events, decays to 0 at ≥50 events
                int score = totalEvents == 0
                    ? 100
                    : (int)Math.Round(Math.Clamp(100 - totalEvents * 2.0, 0, 100));

                string detail = totalEvents == 0
                    ? "No blocking events in last 24 h."
                    : $"{totalEvents} blocking event(s) in last 24 h (total {totalDuration}s blocked).";

                // Zero offenders is indistinguishable between "monitored, truly no
                // blocking" and "blocking history was never collected for this
                // server". Treating empty as a perfect 100/hasData:true made
                // unmonitored servers read as 100% healthy (the reported bug).
                // Absence is not positive evidence — only count blocking as
                // assessed data when there is actual blocking activity.
                bool hasData = totalEvents > 0;

                return new DimensionScore(dim, weight, score,
                    $"{detail} Score = {score}/100.",
                    $"This dimension contributes {weight * score:F0} of {weight * 100:F0} possible points " +
                    $"(weight {weight * 100:F0}%). " +
                    $"Source: BlockingHistoryService last 24 h.",
                    state: hasData ? DimensionState.Measured : DimensionState.NotAssessed);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Blocking score failed for {Server}", serverName);
                return new DimensionScore(dim, weight, 100,
                    "Blocking collection failed — nothing was measured.",
                    "Reading blocking history for this server threw. Nothing is claimed about "
                    + "blocking; this dimension is excluded from the score, not assumed healthy.",
                    state: DimensionState.CollectionFailed);
            }
        }

        // ── Composite calculation ─────────────────────────────────────────────

        private static int ComputeComposite(HealthScoreBreakdown b)
        {
            double total = 0;
            double wsum = 0;
            foreach (var d in b.Dimensions)
            {
                // Unmeasured dimensions carry a placeholder Score (100 for not-assessed and
                // collection-failed, 0 for unreachable). Including them averaged every unpolled
                // server up to 100%, and the unreachable placeholder dragged a real estate mean
                // down to a fabricated 0. Only DimensionState.Measured contributes; State on the
                // parent score says which kind of nothing the rest was.
                if (d.State != DimensionState.Measured) continue;
                total += d.Weight * d.Score;
                wsum += d.Weight;
            }
            if (wsum <= 0) return 0;
            return (int)Math.Round(Math.Clamp(total / wsum, 0, 100));
        }

        // ── Trend (yesterday's snapshot vs today) ────────────────────────────

        /// <summary>
        /// Today's score against the most recent snapshot at or after yesterday's date, as a
        /// direction PLUS the state that says whether a comparison happened at all.
        ///
        /// <para>SR-11 (2026-08-07). This returned a bare <see cref="HealthTrend"/> and answered
        /// <c>Stable</c> for all three of "the score rose less than six points", "there is no earlier
        /// snapshot to compare against" and "reading the history threw". /health rendered the third
        /// and second of those as the literal word "Stable" under an equals icon, in a tooltip reading
        /// "Trend vs yesterday" — a stability verdict against a yesterday that did not exist, and the
        /// last unhedged claim on a page whose score beside it was honestly marked indicative.</para>
        ///
        /// <para>The direction is still returned in every case so nothing downstream has to handle a
        /// null; what makes it printable is <see cref="ExecutiveHealthScore.MeasuredTrend"/>, which is
        /// null unless the state is <see cref="HealthTrendState.Compared"/> AND today itself was
        /// measured. A placeholder 0 composite against a real snapshot would otherwise have produced a
        /// confident "Degrading" for a server nobody assessed.</para>
        /// </summary>
        private async Task<(HealthTrend Trend, HealthTrendState State)> GetTrendAsync(
            string serverName, int currentScore)
        {
            try
            {
                // The window is EXACTLY yesterday: on or after yesterday, strictly before today.
                // Without the upper bound this query returned today's own snapshot — MaybeWriteSnapshot
                // writes one on the first assessment of each UTC day — so from the second call of any
                // day the "trend" compared the score with itself and read Stable by arithmetic. That
                // would have survived the SR-11 fix and rendered a self-comparison as a real one.
                var yesterday = await _historyService.GetLatestHealthScoreAsync(
                    serverName,
                    DateTime.UtcNow.Date.AddDays(-1),
                    beforeDate: DateTime.UtcNow.Date);
                if (yesterday == null)
                    return (HealthTrend.Stable, HealthTrendState.NoPriorSnapshot);

                int diff = currentScore - yesterday.Value;
                if (diff > 5) return (HealthTrend.Improving, HealthTrendState.Compared);
                if (diff < -5) return (HealthTrend.Degrading, HealthTrendState.Compared);
                return (HealthTrend.Stable, HealthTrendState.Compared);
            }
            catch
            {
                // GetLatestHealthScoreAsync swallows its own read failures and returns null, so this
                // catch is for a fault above it (an unopenable store). Either way nothing was
                // compared, and a fault is not the same fact as an absent snapshot.
                return (HealthTrend.Stable, HealthTrendState.LookupFailed);
            }
        }

        // ── Snapshot persistence ──────────────────────────────────────────────

        /// <summary>
        /// Writes one health-score snapshot per server per UTC day.
        ///
        /// <para>reports-r1-05 (2026-08-27): this fired unconditionally, immediately after the
        /// caller computed <c>measured</c> and refused to publish the composite without it. So a day
        /// on which NO dimension was collected still wrote <c>composite_score = 0</c> to
        /// governance-history.db. Measured before the fix: GetHealthScoreAsync over an all-empty
        /// estate returned MeasuredScore=null and the store then held exactly one row, composite=0 —
        /// and the next real measurement compared itself against that fabricated 0, producing a
        /// green "Improving" arrow against a baseline nobody measured (private GetTrendAsync
        /// returned (Improving, Compared) over a seeded 0). The gate is the parameter, not a check
        /// at the call site, so a second caller cannot reintroduce the write.</para>
        ///
        /// <para>Same guard shape as CheckExecutionService.ResolveComposite, which already refuses
        /// to publish a composite nothing measured.</para>
        /// </summary>
        private void MaybeWriteSnapshot(string serverName, int score, HealthScoreBreakdown breakdown, bool measured)
        {
            // An unmeasured day leaves a GAP in the trend. A gap is the honest record: the sparkline
            // and the trend chip both read "no snapshot" rather than a scored zero.
            if (!measured) return;

            var today = DateTime.UtcNow.Date;
            if (_lastSnapshotDate.TryGetValue(serverName, out var last) && last >= today)
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    _historyService.RecordHealthScore(serverName, score, breakdown);
                    _lastSnapshotDate[serverName] = today;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist health score snapshot for {Server}", serverName);
                }
            });
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private double GetWeight(string dimension) =>
            _weights.Categories.TryGetValue(dimension, out var w) ? w : 0.20;

        // Aligned with the CIO band scale (Gold ≥71, Silver ≥51) so a Gold score
        // never renders in Warning-yellow.
        private static HealthSeverity ScoreToSeverity(int score)
        {
            if (score >= 71) return HealthSeverity.Healthy;
            if (score >= 51) return HealthSeverity.Warning;
            return HealthSeverity.Critical;
        }

        private static string HealthMessage(HealthSeverity sev) => sev switch
        {
            HealthSeverity.Healthy => "Server is healthy",
            HealthSeverity.Warning => "Server needs attention",
            HealthSeverity.Critical => "Server requires immediate action",
            _ => "Health status unknown"
        };

        private static GovernanceWeightsConfig LoadWeights()
        {
            try
            {
                var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "Config", "governance-weights.json");
                if (!System.IO.File.Exists(path)) return GovernanceWeightsConfig.Defaults;
                var json = System.IO.File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<GovernanceWeightsConfig>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return cfg ?? GovernanceWeightsConfig.Defaults;
            }
            catch
            {
                return GovernanceWeightsConfig.Defaults;
            }
        }
    }

    // ── Data models ──────────────────────────────────────────────────────────

    /// <summary>Per-dimension score: 0-100 normalised, with weight and tooltip text.</summary>
    public sealed class DimensionScore
    {
        public string Name { get; }
        public double Weight { get; }
        public int Score { get; }
        /// <summary>Short label shown in the bar chart (e.g. "CPU 72%, memory OK — 84/100").</summary>
        public string Summary { get; }
        /// <summary>Full tooltip explaining "X of Y points because Z".</summary>
        public string Tooltip { get; }
        /// <summary>Weighted contribution to composite score (0-100 scale).</summary>
        public double Contribution => Weight * Score;

        /// <summary>
        /// THE THIRD STATE (2026-08-05). What was actually measured for this dimension.
        /// Two states could not carry it: "no data yet" and "the server did not answer"
        /// are different observations, and collapsing them meant the offline branch of
        /// <c>ScoreResource</c> had to pick one. It picked the ctor default (Measured),
        /// which put a fabricated 0 into four surfaces at once. See <see cref="DimensionState"/>.
        /// </summary>
        public DimensionState State { get; }

        /// <summary>
        /// True only when a real measurement produced <see cref="Score"/>. Every existing
        /// reader that filtered on this keeps its meaning and now correctly excludes an
        /// unreachable dimension as well as an uncollected one.
        /// </summary>
        public bool HasData => State == DimensionState.Measured;

        /// <summary>
        /// The score, or null when nothing measured it. <see cref="Score"/> carries a
        /// placeholder in the two unmeasured states (100 for not-assessed, 0 for
        /// unreachable) purely so the composite arithmetic has an int to skip; neither
        /// number is a finding and neither may be printed. Read this instead.
        /// </summary>
        public int? MeasuredScore => State == DimensionState.Measured ? Score : null;

        /// <summary>
        /// P4-ii (2026-07-12): where this dimension's data came from. Drives the CIO estate
        /// card's honesty sub-line — a bare score must never again hide whether Security/
        /// Compliance were corpus-fed, VA-fed, or not assessed at all (the live re-review's
        /// "no-such-subline" security-blind defect). Defaults to None for dimensions that
        /// don't distinguish a source (Performance/Resource/Blocking — live signals only).
        /// </summary>
        public DimensionSource Source { get; }

        /// <summary>Count of underlying corpus check results feeding this dimension (only
        /// meaningful when <see cref="Source"/> is <see cref="DimensionSource.Corpus"/>).</summary>
        public int CheckCount { get; }

        /// <summary>Timestamp the underlying data was collected as-of (only meaningful when
        /// <see cref="Source"/> is <see cref="DimensionSource.Corpus"/>).</summary>
        public DateTime? AsOf { get; }

        public DimensionScore(string name, double weight, int score, string summary, string tooltip,
            DimensionState state = DimensionState.Measured, DimensionSource source = DimensionSource.None,
            int checkCount = 0, DateTime? asOf = null)
        {
            Name = name;
            Weight = weight;
            Score = score;
            Summary = summary;
            Tooltip = tooltip;
            State = state;
            Source = source;
            CheckCount = checkCount;
            AsOf = asOf;
        }
    }

    /// <summary>
    /// What produced (or failed to produce) a <see cref="DimensionScore"/>. The law this
    /// enum exists to serve: a printed claim must be conditioned on the same measurement
    /// that produced the verdict beside it.
    /// </summary>
    public enum DimensionState
    {
        /// <summary>A real measurement produced the score. It may be printed.</summary>
        Measured,
        /// <summary>Nothing has been collected for this dimension yet. No score exists to print.</summary>
        NotAssessed,
        /// <summary>The server did not answer, so this dimension could not be measured. No score exists to print.</summary>
        Unreachable,
        /// <summary>
        /// Collecting this dimension threw. Distinct from <see cref="NotAssessed"/> on purpose:
        /// "run checks to populate this" is the wrong remedy for a collector that faulted, and
        /// printing it would be a claim conditioned on a measurement nobody took.
        /// </summary>
        CollectionFailed,
    }

    /// <summary>Where a <see cref="DimensionScore"/>'s data came from. See <see cref="DimensionScore.Source"/>.</summary>
    public enum DimensionSource { None, Corpus, VulnerabilityAssessment, Live }

    /// <summary>Full breakdown of the 5-dimension health index.</summary>
    public sealed class HealthScoreBreakdown
    {
        public DimensionScore Performance { get; set; } = new("Performance", 0.15, 100, "", "", state: DimensionState.NotAssessed);
        public DimensionScore Compliance { get; set; } = new("Compliance", 0.20, 100, "", "", state: DimensionState.NotAssessed);
        public DimensionScore Security { get; set; } = new("Security", 0.25, 100, "", "", state: DimensionState.NotAssessed);
        public DimensionScore Resource { get; set; } = new("Resource", 0.20, 100, "", "", state: DimensionState.NotAssessed);
        public DimensionScore Blocking { get; set; } = new("Blocking", 0.10, 100, "", "", state: DimensionState.NotAssessed);

        public IEnumerable<DimensionScore> Dimensions =>
            new[] { Security, Compliance, Performance, Resource, Blocking };
    }

    /// <summary>
    /// What is known about one server's health, as a whole. Derived from the dimension
    /// states except for <see cref="HealthAssessmentState.Unknown"/>, which only a caller
    /// that watched the collection itself fail can assert.
    /// </summary>
    public enum HealthAssessmentState
    {
        /// <summary>At least one dimension was measured. <see cref="ExecutiveHealthScore.Score"/> may be printed.</summary>
        Assessed,
        /// <summary>Nothing has been collected for this server yet. No score exists to print.</summary>
        NotAssessed,
        /// <summary>The server did not answer on every dimension that tried to reach it. No score exists to print.</summary>
        Unreachable,
        /// <summary>Health collection itself failed, so not even the state is known. No score exists to print.</summary>
        Unknown,
    }

    /// <summary>Executive health score result including full breakdown.</summary>
    public class ExecutiveHealthScore
    {
        public int Score { get; set; }
        public HealthSeverity Severity { get; set; }
        public HealthTrend Trend { get; set; }

        /// <summary>
        /// Whether <see cref="Trend"/> is the result of an actual comparison. Defaults to
        /// <see cref="HealthTrendState.NoPriorSnapshot"/>, so a score object built by hand — the /cio
        /// estate carrier and the collection-failed carrier both are — makes no trend claim unless
        /// something deliberately sets this. Read <see cref="MeasuredTrend"/>, never
        /// <see cref="Trend"/>, to decide what to print.
        /// </summary>
        public HealthTrendState TrendState { get; set; } = HealthTrendState.NoPriorSnapshot;

        public string Message { get; set; } = "";
        public DateTime LastUpdated { get; set; }
        public HealthScoreBreakdown Breakdown { get; set; } = new();

        /// <summary>
        /// Set true ONLY by the code that watched health collection throw. A faulting
        /// service is otherwise indistinguishable from a server nobody has assessed,
        /// and those two deserve different sentences.
        /// </summary>
        public bool CollectionFailed { get; set; }

        /// <summary>
        /// The four-state read every surface conditions its sentence on. Ranked measured >
        /// unreachable > collection-failed > never-collected: a partially reachable server with
        /// one real dimension has genuinely been assessed on that dimension, and a real negative
        /// observation ("did not answer") outranks a fault, which outranks plain absence.
        /// </summary>
        public HealthAssessmentState State =>
            CollectionFailed ? HealthAssessmentState.Unknown
            : Breakdown.Dimensions.Any(d => d.State == DimensionState.Measured) ? HealthAssessmentState.Assessed
            : Breakdown.Dimensions.Any(d => d.State == DimensionState.Unreachable) ? HealthAssessmentState.Unreachable
            : Breakdown.Dimensions.Any(d => d.State == DimensionState.CollectionFailed) ? HealthAssessmentState.Unknown
            : HealthAssessmentState.NotAssessed;

        /// <summary>
        /// True when at least one dimension was really measured. False means the composite
        /// is an artifact, not an assessment. Kept as the name every existing caller uses;
        /// <see cref="State"/> says which kind of false it is.
        /// </summary>
        public bool IsAssessed => State == HealthAssessmentState.Assessed;

        /// <summary>
        /// The composite score, or null when nothing measured it. <see cref="Score"/> holds
        /// 0 in that case only because <c>ComputeComposite</c> must return an int; that 0 is
        /// not a finding and must not be printed.
        /// </summary>
        public int? MeasuredScore => IsAssessed ? Score : null;

        /// <summary>
        /// The trend, or null when no trend has been established. A trend is a comparison between two
        /// measurements, so it needs BOTH ends: a measured score today (<see cref="IsAssessed"/>) and
        /// a prior snapshot to compare it against (<see cref="HealthTrendState.Compared"/>). Null
        /// means print no direction — <c>EstateHealthPolicy.TrendValue</c> and
        /// <c>EstateHealthPolicy.TrendBasis</c> supply the words for that state.
        ///
        /// <para>Same shape and the same reason as <see cref="MeasuredScore"/>:
        /// <see cref="Trend"/> must hold one of three enum values whatever happened, so the
        /// placeholder has to exist; printing it is the defect (SR-11).</para>
        /// </summary>
        public HealthTrend? MeasuredTrend =>
            IsAssessed && TrendState == HealthTrendState.Compared ? Trend : null;
    }

    /// <summary>Health trend direction.</summary>
    public enum HealthTrend { Improving, Stable, Degrading }

    /// <summary>
    /// Whether a <see cref="ExecutiveHealthScore.Trend"/> was arrived at by comparing anything.
    /// Declaration order matters only in that the default (0) must be a state that claims NOTHING.
    /// </summary>
    public enum HealthTrendState
    {
        /// <summary>Nothing earlier exists for this server. A trend needs a second measurement.</summary>
        NoPriorSnapshot,
        /// <summary>Reading the score history faulted, so no comparison was made.</summary>
        LookupFailed,
        /// <summary>Today's score was compared against a real earlier snapshot.</summary>
        Compared,
    }

    /// <summary>Typed read of governance-weights.json categories block.</summary>
    internal sealed class GovernanceWeightsConfig
    {
        public Dictionary<string, double> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static readonly GovernanceWeightsConfig Defaults = new()
        {
            Categories = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Security"] = 0.25,
                ["Performance"] = 0.15,
                ["Reliability"] = 0.20,
                ["Compliance"] = 0.20,
                ["Cost"] = 0.10,
                ["Blocking"] = 0.10,
            }
        };
    }
}
