/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Computes a weighted 0-100 Health Score and Risk Rating from five dimensions:
    ///   1. Performance   — saturation wait-stats trend (PAGEIOLATCH/CXPACKET/SOS/LCK_M_*) (weight: governance-weights.json Performance)
    ///   2. Compliance    — governance pass-rate across Compliance dimension (weight: Compliance)
    ///   3. Security      — VA security findings pass-rate (weight: Security)
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
            IConfiguration? configuration = null)
        {
            _healthCheckService = healthCheckService ?? throw new ArgumentNullException(nameof(healthCheckService));
            _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
            _blockingHistory = blockingHistory ?? throw new ArgumentNullException(nameof(blockingHistory));
            _perfHistory = perfHistory ?? throw new ArgumentNullException(nameof(perfHistory));
            _vaState = vaState ?? throw new ArgumentNullException(nameof(vaState));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
                var severity = ScoreToSeverity(score);
                var trend = await GetTrendAsync(serverName, score);

                // Persist snapshot once per UTC day
                MaybeWriteSnapshot(serverName, score, breakdown);

                return new ExecutiveHealthScore
                {
                    Score = score,
                    Severity = severity,
                    Trend = trend,
                    Message = HealthMessage(severity),
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

            // ── 1. Performance: wait-stats trend (HistoricalPerformanceService) ──────
            breakdown.Performance = ScorePerformance(serverName);

            // ── 2. Compliance: governance compliance pass-rate ───────────────────────
            breakdown.Compliance = ScoreCompliance(serverName);

            // ── 3. Security: VA security findings ───────────────────────────────────
            breakdown.Security = ScoreSecurity(serverName);

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
                        hasData: false);

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
                return new DimensionScore(dim, weight, 100, "No performance data — full points by default.", "",
                    hasData: false);
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
        /// </summary>
        private List<AssessmentResult> VaResultsForServer(string serverName) =>
            _vaState.Results.Where(r =>
                string.Equals(r.ThisServer, serverName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.TargetName, serverName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Compliance (0-100): governance compliance pass-rate from VA results (Compliance/Auditing/Patching categories).
        private DimensionScore ScoreCompliance(string serverName)
        {
            const string dim = "Compliance";
            double weight = GetWeight(dim);

            try
            {
                var results = VaResultsForServer(serverName);
                if (results.Count == 0)
                    return new DimensionScore(dim, weight, 100, "No VA results for this server — full points by default.",
                        "Run Vulnerability Assessment against this server to populate compliance score.",
                        hasData: false);

                // Compliance-mapped categories from governance-weights.json categoryMapping
                var complianceCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "Auditing", "Compliance", "Data_Protection", "Patching", "Maintenance", "Monitoring" };

                var complianceResults = results.Where(r =>
                    complianceCategories.Contains(r.Category ?? "")).ToList();

                if (complianceResults.Count == 0)
                    return new DimensionScore(dim, weight, 100, "No compliance-category findings in VA results.",
                        "Categories: Auditing, Compliance, Data_Protection, Patching, Maintenance, Monitoring.",
                        hasData: false);

                int passing = complianceResults.Count(r =>
                    string.Equals(r.Status, "Passed", StringComparison.OrdinalIgnoreCase));
                int total = complianceResults.Count;
                int score = (int)Math.Round(passing * 100.0 / total);

                return new DimensionScore(dim, weight, score,
                    $"{passing}/{total} compliance checks passing ({score}%).",
                    $"This dimension contributes {weight * score:F0} of {weight * 100:F0} possible points " +
                    $"(weight {weight * 100:F0}%). " +
                    $"Categories: Auditing, Compliance, Data_Protection, Patching, Maintenance.");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Compliance score failed");
                return new DimensionScore(dim, weight, 100, "No compliance data.", "", hasData: false);
            }
        }

        // Security (0-100): VA security findings pass-rate.
        private DimensionScore ScoreSecurity(string serverName)
        {
            const string dim = "Security";
            double weight = GetWeight(dim);

            try
            {
                var results = VaResultsForServer(serverName);
                if (results.Count == 0)
                    return new DimensionScore(dim, weight, 100, "No VA results for this server — full points by default.",
                        "Run Vulnerability Assessment against this server to populate security score.",
                        hasData: false);

                var securityCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "Security", "Authentication", "Authorization", "Encryption", "Surface_Area" };

                var secResults = results.Where(r =>
                    securityCategories.Contains(r.Category ?? "")).ToList();

                if (secResults.Count == 0)
                    return new DimensionScore(dim, weight, 100, "No security-category findings in VA results.",
                        "Categories: Security, Authentication, Authorization, Encryption, Surface_Area.",
                        hasData: false);

                int passing = secResults.Count(r =>
                    string.Equals(r.Status, "Passed", StringComparison.OrdinalIgnoreCase));
                int total = secResults.Count;
                int critical = secResults.Count(r =>
                    !string.Equals(r.Status, "Passed", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.Severity, "Critical", StringComparison.OrdinalIgnoreCase));

                // Critical failures penalise harder: raw pass-rate minus 3 pts per critical
                double rawPct = passing * 100.0 / total;
                int score = (int)Math.Round(Math.Clamp(rawPct - critical * 3, 0, 100));

                return new DimensionScore(dim, weight, score,
                    $"{passing}/{total} security checks passing ({score}%); {critical} critical failures.",
                    $"This dimension contributes {weight * score:F0} of {weight * 100:F0} possible points " +
                    $"(weight {weight * 100:F0}%). " +
                    $"Critical failures carry extra penalty (−3 pts each). " +
                    $"Source: VA results from VulnerabilityAssessmentStateService.");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Security score failed");
                return new DimensionScore(dim, weight, 100, "No security data.", "", hasData: false);
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
                    // absence of data must not drag the composite down. Offline (a real
                    // negative signal) is handled below and still scores 0.
                    return new DimensionScore(dim, weight, 100,
                        "No live health data yet — full points awarded by default.",
                        "Score will reflect real resource saturation after the first health poll.",
                        hasData: false);

                if (status.IsOnline != true)
                    return new DimensionScore(dim, weight, 0,
                        "Server is offline — resource score is 0.",
                        "Server must be online to compute resource saturation.");

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
                return new DimensionScore(dim, weight, 100, "No resource data.", "", hasData: false);
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
                    hasData: hasData);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Blocking score failed for {Server}", serverName);
                return new DimensionScore(dim, weight, 100, "No blocking history.", "", hasData: false);
            }
        }

        // ── Composite calculation ─────────────────────────────────────────────

        private static int ComputeComposite(HealthScoreBreakdown b)
        {
            double total = 0;
            double wsum = 0;
            foreach (var d in b.Dimensions)
            {
                // No-data dimensions return a stub Score of 100 (hasData:false).
                // Including them averaged every unpolled server up to 100%.
                // Only real data contributes; IsAssessed (any HasData) drives the
                // "Not yet assessed" UI when nothing qualifies here.
                if (!d.HasData) continue;
                total += d.Weight * d.Score;
                wsum += d.Weight;
            }
            if (wsum <= 0) return 0;
            return (int)Math.Round(Math.Clamp(total / wsum, 0, 100));
        }

        // ── Trend (yesterday's snapshot vs today) ────────────────────────────

        private async Task<HealthTrend> GetTrendAsync(string serverName, int currentScore)
        {
            try
            {
                var yesterday = await _historyService.GetLatestHealthScoreAsync(
                    serverName, DateTime.UtcNow.Date.AddDays(-1));
                if (yesterday == null) return HealthTrend.Stable;

                int diff = currentScore - yesterday.Value;
                if (diff > 5) return HealthTrend.Improving;
                if (diff < -5) return HealthTrend.Degrading;
                return HealthTrend.Stable;
            }
            catch
            {
                return HealthTrend.Stable;
            }
        }

        // ── Snapshot persistence ──────────────────────────────────────────────

        private void MaybeWriteSnapshot(string serverName, int score, HealthScoreBreakdown breakdown)
        {
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
        /// False when the dimension returned its no-data default (100 / full-points-by-default).
        /// True for every normally computed result, including offline (a real negative signal).
        /// </summary>
        public bool HasData { get; }

        public DimensionScore(string name, double weight, int score, string summary, string tooltip,
            bool hasData = true)
        {
            Name = name;
            Weight = weight;
            Score = score;
            Summary = summary;
            Tooltip = tooltip;
            HasData = hasData;
        }
    }

    /// <summary>Full breakdown of the 5-dimension health index.</summary>
    public sealed class HealthScoreBreakdown
    {
        public DimensionScore Performance { get; set; } = new("Performance", 0.15, 100, "", "", hasData: false);
        public DimensionScore Compliance { get; set; } = new("Compliance", 0.20, 100, "", "", hasData: false);
        public DimensionScore Security { get; set; } = new("Security", 0.25, 100, "", "", hasData: false);
        public DimensionScore Resource { get; set; } = new("Resource", 0.20, 100, "", "", hasData: false);
        public DimensionScore Blocking { get; set; } = new("Blocking", 0.10, 100, "", "", hasData: false);

        public IEnumerable<DimensionScore> Dimensions =>
            new[] { Security, Compliance, Performance, Resource, Blocking };
    }

    /// <summary>Executive health score result including full breakdown.</summary>
    public class ExecutiveHealthScore
    {
        public int Score { get; set; }
        public HealthSeverity Severity { get; set; }
        public HealthTrend Trend { get; set; }
        public string Message { get; set; } = "";
        public DateTime LastUpdated { get; set; }
        public HealthScoreBreakdown Breakdown { get; set; } = new();
        /// <summary>
        /// True when at least one dimension has real poll data (HasData == true).
        /// False means every dimension returned a no-data default — the composite
        /// score of 100 is an artifact, not a real assessment.
        /// </summary>
        public bool IsAssessed => Breakdown.Dimensions.Any(d => d.HasData);
    }

    /// <summary>Health trend direction.</summary>
    public enum HealthTrend { Improving, Stable, Degrading }

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
