/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Computes governance scores using a weighted-ratio model.
    ///
    /// SCORING MODEL (read this before changing anything):
    ///   Each check has two tuning fields that flow from YAML → sql-checks.json → CheckResult:
    ///     ScoreWeight  (YAML: score_weight, default 1, range 1-25) — importance of this check
    ///     EffortHours  (YAML: effort_hours, default 0)             — remediation effort
    ///
    ///   check_value  = max(ScoreWeight, 1) × max(EffortHours, 1.0)
    ///   Score        = Σ(check_value for PASS or INFO) / Σ(check_value for non-SKIP) × 100
    ///
    /// RULES:
    ///   - INFO result  → counts as PASS  (informational, not a finding)
    ///   - SKIP result  → excluded from BOTH numerator and denominator
    ///                    (check not applicable to this server; neither penalises nor rewards)
    ///   - WARN/FAIL    → excluded from numerator only (pulls score down)
    ///   - IsBad        → costing only; does NOT affect pass/fail classification here
    ///   - EffortHours=0→ treated as 1 for score calc so zero-effort checks still count
    ///
    /// CATEGORY SCORE:
    ///   Same ratio computed per governance dimension (Security/Performance/Reliability/Cost/Compliance).
    ///   Overall = weighted average of category ratios using GovernanceWeights.Categories weights.
    ///
    /// TUNING:
    ///   Set score_weight in the YAML, regenerate sql-checks.json via
    ///   research_output/LLM1_deepseek/regenerate_checks_json.py.
    ///   Effort-hour overrides can be set per-check in /remediation-tuner (writes to
    ///   Config/sql-check-weights-override.json, read by RemediationWeightStore).
    ///   NOTE: RemediationWeightStore overrides are used for COSTING only; to affect
    ///   the governance score, update EffortHours in the YAML and regenerate.
    /// </summary>
    public interface IGovernanceService
    {
        /// <summary>
        /// Compute a quick indicative score from a subset of checks (≤60s run).
        /// </summary>
        Task<GovernanceScore> ComputeIndicativeAsync(
            IEnumerable<CheckResult> results,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Compute a full governance score from a complete vulnerability assessment.
        /// </summary>
        Task<GovernanceScore> ComputeFullAsync(
            IEnumerable<CheckResult> results,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Governance scoring implementation. See <see cref="IGovernanceService"/> for the
    /// full model description. GovernanceWeights (Config/ or Phase 3 license bundle) controls
    /// category weights and band thresholds — the per-check tuning lives in the YAML corpus.
    /// </summary>
    public sealed class GovernanceService : IGovernanceService
    {
        private readonly ILogger<GovernanceService> _logger;
        private readonly IGovernanceWeightsProvider _weightsProvider;

        public GovernanceService(
            ILogger<GovernanceService> logger,
            IGovernanceWeightsProvider weightsProvider)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _weightsProvider = weightsProvider ?? throw new ArgumentNullException(nameof(weightsProvider));
        }

        public Task<GovernanceScore> ComputeIndicativeAsync(
            IEnumerable<CheckResult> results,
            CancellationToken cancellationToken = default)
        {
            var score = Compute(results, isIndicative: true);
            return Task.FromResult(score);
        }

        public Task<GovernanceScore> ComputeFullAsync(
            IEnumerable<CheckResult> results,
            CancellationToken cancellationToken = default)
        {
            var score = Compute(results, isIndicative: false);
            return Task.FromResult(score);
        }

        private GovernanceScore Compute(IEnumerable<CheckResult> results, bool isIndicative)
        {
            var weights = _weightsProvider.Current;
            var list = results?.ToList() ?? new List<CheckResult>();

            if (list.Count == 0)
            {
                return new GovernanceScore
                {
                    IsIndicative = isIndicative,
                    Overall = 0,
                    Band = ScoreBand.Emerging,
                    Categories = new Dictionary<string, CategoryScore>(StringComparer.OrdinalIgnoreCase)
                };
            }

            // Weighted ratio model:
            //   check_value = score_weight × max(effort_hours, 1)
            //   Score = Σ(check_value for PASS/INFO) / Σ(check_value for non-SKIP) × 100
            //
            // INFO counts as pass. SKIP is excluded from both sides.
            // IsBad and EffortHours=0 do not affect fail/pass classification — only costing.

            static double CheckValue(CheckResult r) =>
                Math.Max(r.ScoreWeight, 1) * Math.Max(r.EffortHours, 1.0);

            static bool IsSkip(CheckResult r) =>
                r.Message.StartsWith("SKIP", StringComparison.OrdinalIgnoreCase) ||
                r.ErrorMessage != null;

            static bool CountsAsPass(CheckResult r) =>
                r.Passed || string.Equals(r.Severity, "INFO", StringComparison.OrdinalIgnoreCase);

            // 1. Per-finding values (exclude SKIPs)
            var scorable = list.Where(r => !IsSkip(r)).ToList();

            // 2. Per-category weighted ratio
            var categoryScores = new Dictionary<string, CategoryScore>(StringComparer.OrdinalIgnoreCase);
            foreach (var dim in weights.Categories.Keys)
            {
                var inDim = scorable.Where(r => MapCategory(r.Category, weights.CategoryMapping)
                                .Equals(dim, StringComparison.OrdinalIgnoreCase)).ToList();
                var denominator = inDim.Sum(CheckValue);
                var numerator = inDim.Where(CountsAsPass).Sum(CheckValue);
                var ratio = denominator > 0 ? (numerator / denominator) * 100.0 : 100.0;
                var catWeight = weights.Categories[dim];

                categoryScores[dim] = new CategoryScore
                {
                    Dimension = dim,
                    Weight = catWeight,
                    RawScore = ratio,
                    CappedScore = ratio * catWeight,
                    Ceiling = 100.0 * catWeight,
                    FindingCount = inDim.Count,
                    PassedCount = inDim.Count(CountsAsPass)
                };
            }

            // 3. Overall = weighted average of category ratios
            var totalWeight = categoryScores.Values.Sum(c => c.Weight);
            var overall = totalWeight > 0
                ? categoryScores.Values.Sum(c => c.RawScore * c.Weight) / totalWeight
                : 0.0;
            overall = Math.Round(Math.Clamp(overall, 0.0, 100.0), 1);

            var band = GetBand(overall, weights.Bands);

            _logger.LogInformation(
                "Governance score computed: Overall={Overall:F1}, Band={Band}, IsIndicative={IsIndicative}, Scorable={Scorable}/{Total}",
                overall, band, isIndicative, scorable.Count, list.Count);

            return new GovernanceScore
            {
                IsIndicative = isIndicative,
                Overall = overall,
                Band = band,
                Categories = categoryScores,
                TotalFindings = list.Count,
                PassedFindings = scorable.Count(CountsAsPass),
                FailedFindings = scorable.Count(r => !CountsAsPass(r))
            };
        }

        private static string MapCategory(string checkCategory, Dictionary<string, string> mapping)
        {
            if (string.IsNullOrWhiteSpace(checkCategory))
                return "Reliability";

            if (mapping.TryGetValue(checkCategory, out var mapped))
                return mapped;

            // Fallback: if the category itself is a governance dimension, use it directly
            var dims = new[] { "Security", "Performance", "Reliability", "Cost", "Compliance" };
            if (dims.Contains(checkCategory, StringComparer.OrdinalIgnoreCase))
                return checkCategory;

            return "Reliability";
        }

        private static ScoreBand GetBand(double overall, Dictionary<string, int[]> bands)
        {
            foreach (var kv in bands)
            {
                var range = kv.Value;
                if (range.Length >= 2 && overall >= range[0] && overall <= range[1])
                {
                    return Enum.TryParse<ScoreBand>(kv.Key, out var band) ? band : ScoreBand.Emerging;
                }
            }
            return ScoreBand.Emerging;
        }
    }

    /// <summary>
    /// Governance weights loaded from <c>Config/governance-weights.json</c> (Phase 2: via
    /// <see cref="GovernanceWeightsProvider"/>) or from the license bundle (Phase 3: via
    /// <c>IBundleAccessor</c>).  Defaults are provided as property initialisers so the
    /// app works without the file on disk.
    /// </summary>
    public class GovernanceWeights
    {
        public Dictionary<string, double> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Security"] = 0.25,
            ["Performance"] = 0.20,
            ["Reliability"] = 0.20,
            ["Cost"] = 0.15,
            ["Compliance"] = 0.20
        };

        public GovernanceCaps Caps { get; set; } = new();

        public Dictionary<string, int[]> Bands { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Emerging"] = new[] { 0, 30 },
            ["Bronze"] = new[] { 31, 50 },
            ["Silver"] = new[] { 51, 70 },
            ["Gold"] = new[] { 71, 85 },
            ["Platinum"] = new[] { 86, 100 }
        };

        public Dictionary<string, string> CategoryMapping { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Security"] = "Security",
            ["Authentication"] = "Security",
            ["Authorization"] = "Security",
            ["Encryption"] = "Security",
            ["Surface_Area"] = "Security",
            ["Auditing"] = "Compliance",
            ["Compliance"] = "Compliance",
            ["Data_Protection"] = "Compliance",
            ["Patching"] = "Compliance",
            ["Maintenance"] = "Compliance",
            ["Maintenance Monitoring checks"] = "Compliance",
            ["Monitoring"] = "Compliance",
            ["Configuration"] = "Reliability",
            ["DefaultRuleset"] = "Reliability",
            ["Backup"] = "Reliability",
            ["Availability"] = "Reliability",
            ["Reliability"] = "Reliability",
            ["Performance"] = "Performance",
            ["Memory"] = "Performance",
            ["Network"] = "Performance",
            ["Indexes"] = "Performance",
            ["Cost"] = "Cost",
            ["Custom"] = "Reliability"
        };
    }

    public class GovernanceCaps
    {
        public int PerFinding { get; set; } = 40;
        public int PerCategory { get; set; } = 100;
        public int Overall { get; set; } = 100;
    }

    /// <summary>
    /// Final governance score output.
    /// </summary>
    public class GovernanceScore
    {
        public bool IsIndicative { get; set; }
        public double Overall { get; set; }
        public ScoreBand Band { get; set; }
        public Dictionary<string, CategoryScore> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int TotalFindings { get; set; }
        public int PassedFindings { get; set; }
        public int FailedFindings { get; set; }
    }

    public class CategoryScore
    {
        public string Dimension { get; set; } = string.Empty;
        public double Weight { get; set; }
        public double RawScore { get; set; }
        public double CappedScore { get; set; }
        public double Ceiling { get; set; }
        public int FindingCount { get; set; }
        public int PassedCount { get; set; }
        public double PassRate => FindingCount == 0 ? 0 : (PassedCount * 100.0 / FindingCount);
    }

    public enum ScoreBand
    {
        Emerging = 0,
        Bronze = 1,
        Silver = 2,
        Gold = 3,
        Platinum = 4
    }
}
