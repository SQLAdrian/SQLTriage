/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    ///   Each check has two tuning fields that flow from YAML (corpus-v2) → the packed check bundle → CheckResult:
    ///     ScoreWeight  (YAML: score_weight, default 1, range 1-25) — importance of this check
    ///     EffortHours  (YAML: effort_hours, default 0)             — remediation effort
    ///
    ///   check_value  = max(ScoreWeight, 1) × max(EffortHours, 1.0)
    ///   Score        = Σ(check_value for PASS or INFO) / Σ(check_value for non-SKIP) × 100
    ///
    /// RULES:
    ///   - INFO result  → EXCLUDED from BOTH numerator and denominator
    ///                    (informational, not a clean pass; neither rewards nor penalises —
    ///                     changed 2026-06-04: previously counted as PASS, which padded the score)
    ///   - SKIP result  → excluded from BOTH numerator and denominator
    ///                    (check not applicable to this server; neither penalises nor rewards)
    ///   - WARN/FAIL    → excluded from numerator only (pulls score down)
    ///   - IsBad        → costing only; does NOT affect pass/fail classification here
    ///   - EffortHours=0→ treated as 1 for score calc so zero-effort checks still count
    ///
    /// CATEGORY SCORE:
    ///   Same ratio computed per governance dimension (Security/Performance/Reliability/Cost/Compliance).
    ///   Overall = weighted average of category ratios using GovernanceWeights.Categories weights,
    ///   over ASSESSED categories only. A dimension with no scorable checks this run is EXCLUDED
    ///   (weights renormalised) — it does NOT score a free 100%. (Fix 2026-06-04.)
    ///
    /// TUNING:
    ///   Set score_weight in the corpus-v2 YAML (sqltriage-corpus repo), then re-pack
    ///   the v2 corpus bundle so the change reaches CheckRepositoryService.LoadViaBundle.
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

        // 2026-07-16: the actual classification logic moved to CheckClassification (see that
        // file's doc comment for why — IsInfo now also honors Verdict=="INFO", a gap this
        // duplicate would otherwise have to be fixed in twice). These four stay as public thin
        // forwards — several surfaces already reference them, some via
        // "@using static SQLTriage.Data.Services.GovernanceService" — so nothing downstream needs
        // to change its call site, only the DailySummaryBuilder/etc. copies that genuinely
        // duplicated the logic get deleted in favour of calling CheckClassification directly.

        /// <summary>SKIP result: not applicable to this server, excluded from scoring.</summary>
        public static bool IsSkip(CheckResult r) => CheckClassification.IsSkip(r);

        /// <summary>INFO result: informational, not a pass/fail verdict, excluded from scoring.</summary>
        public static bool IsInfo(CheckResult r) => CheckClassification.IsInfo(r);

        /// <summary>WARN result: the check ran but could not fully assess the target (e.g. an
        /// under-privileged caller). Not a failure, not a pass — excluded from scoring.</summary>
        public static bool IsWarn(CheckResult r) => CheckClassification.IsWarn(r);

        /// <summary>
        /// True for a result that participates in the governance Passed/Failed math (i.e. NOT
        /// SKIP or INFO). Public so any UI surface that shows per-category or per-framework
        /// Passed/Failed counts (e.g. <see cref="SQLTriage.Components.Shared.GovernanceTimeline"/>)
        /// filters to the exact same subset the headline score/PassedFindings/FailedFindings use —
        /// otherwise the page shows two different, irreconcilable "Passed" totals (Wave 2,
        /// 2026-07-12 fix: framework cards summed to 1478/247 over all 1725 results while the
        /// header read 1112/240 over the 1352 scorable subset).
        /// </summary>
        public static bool IsScorable(CheckResult r) => CheckClassification.IsScorable(r);

        /// <summary>
        /// F6: a client-accepted finding (accepted-by-design on this instance) rides the
        /// Passed tier so it stops dragging the score — but it stays a distinct, counted
        /// category (see AcceptedFindings below), never a silent pass. Public for the same
        /// reason as <see cref="IsScorable"/> — any surface counting Passed must agree with
        /// this definition, not raw <c>CheckResult.Passed</c>.
        /// </summary>
        public static bool CountsAsPass(CheckResult r) => CheckClassification.CountsAsPass(r);

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

            // 1. Per-finding values — IsScorable/CountsAsPass are the SAME public helpers
            // consumed by GovernanceTimeline (Wave 2, 2026-07-12 fix): the framework cards
            // on /governance must count Passed/Failed over the identical scorable subset
            // used here, or the page shows two irreconcilable "Passed" totals (ruled defect).
            var scorable = list.Where(IsScorable).ToList();

            // 2. Per-category weighted ratio
            var categoryScores = new Dictionary<string, CategoryScore>(StringComparer.OrdinalIgnoreCase);
            foreach (var dim in weights.Categories.Keys)
            {
                var inDim = scorable.Where(r => MapCategory(r.Category, weights.CategoryMapping)
                                .Equals(dim, StringComparison.OrdinalIgnoreCase)).ToList();
                var denominator = inDim.Sum(CheckValue);
                var numerator = inDim.Where(CountsAsPass).Sum(CheckValue);
                // Unassessed category (no scorable checks) → NOT 100%. It contributes
                // nothing and is excluded from the overall average below. A free 100%
                // here used to inflate the headline for dimensions never tested.
                var assessed = denominator > 0;
                var ratio = assessed ? (numerator / denominator) * 100.0 : 0.0;
                var catWeight = weights.Categories[dim];

                categoryScores[dim] = new CategoryScore
                {
                    Dimension = dim,
                    Weight = catWeight,
                    RawScore = ratio,
                    CappedScore = ratio * catWeight,
                    Ceiling = 100.0 * catWeight,
                    FindingCount = inDim.Count,
                    PassedCount = inDim.Count(CountsAsPass),
                    Assessed = assessed
                };
            }

            // 3. Overall = weighted average of category ratios, over ASSESSED categories
            //    only. Weights are renormalised across what was actually measured, so an
            //    un-run dimension neither helps nor hurts (it just isn't part of the score).
            var assessedCats = categoryScores.Values.Where(c => c.Assessed).ToList();
            var totalWeight = assessedCats.Sum(c => c.Weight);
            var overall = totalWeight > 0
                ? assessedCats.Sum(c => c.RawScore * c.Weight) / totalWeight
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
                FailedFindings = scorable.Count(r => !CountsAsPass(r)),
                // F6: accepted findings are a subset of the passed tier — surfaced separately so
                // the headline can read "N open · M accepted" instead of a flat pass count.
                AcceptedFindings = scorable.Count(r => !r.Passed && r.IsAccepted),
                // A2 (2026-07-13): errored checks, a subset of IsSkip — surfaced so the governance
                // strip can name them instead of silently lumping them into "informational/skipped".
                ErroredFindings = list.Count(r => r.ErrorMessage != null),
                // 2026-07-21: WARN/"Partial" results, a subset of the excluded (non-scorable)
                // tier, surfaced so /cio and /governance can NAME the state instead of folding it
                // into "informational/skipped". Errored results are excluded from this count so
                // the three excluded sub-counts the UI prints stay disjoint and foot exactly to
                // TotalFindings - (Passed + Failed).
                PartialFindings = list.Count(r => r.ErrorMessage == null && IsWarn(r))
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
            // Scores are fractional but the bands are integer buckets (…Gold 71-85,
            // Platinum 86-100). Round to the displayed integer first — otherwise a
            // score like 85.6 (shown as "86") satisfies neither Gold (<=85) nor
            // Platinum (>=86), falls through every band, and drops to the Emerging
            // fallback. Same 1-unit gaps exist at 30/31, 50/51, 70/71.
            var v = (int)Math.Round(overall, MidpointRounding.AwayFromZero);
            return Enum.TryParse<ScoreBand>(GovernanceWeights.BandName(v, bands), out var band)
                ? band : ScoreBand.Emerging;
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

        // governance-weights.json carries intentional "_comment" documentation
        // keys inside the bands object. A plain Dictionary<string,int[]> bind
        // throws on those (string value → int[]) and the whole load falls back
        // to defaults. The converter skips non-array entries (e.g. _comment) so
        // the documented JSON round-trips cleanly. (Fix 2026-05-29.)
        [JsonConverter(typeof(CommentTolerantBandsConverter))]
        public Dictionary<string, int[]> Bands { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Emerging"] = new[] { 0, 30 },
            ["Bronze"] = new[] { 31, 50 },
            ["Silver"] = new[] { 51, 70 },
            ["Gold"] = new[] { 71, 85 },
            ["Platinum"] = new[] { 86, 100 }
        };

        /// <summary>
        /// Medal/band name for a 0-100 score using the configured <see cref="Bands"/>.
        /// Single source of the tier thresholds — used by both the governance score and
        /// the CIO health-score medal so a governance-weights.json edit applies to both.
        /// </summary>
        public string BandFor(int score) => BandName(score, Bands);

        /// <summary>Single band-matching implementation shared by <see cref="BandFor"/>
        /// (CIO/health medal) and GovernanceService.GetBand (governance score) so the tier
        /// thresholds can never diverge between surfaces.</summary>
        public static string BandName(int score, Dictionary<string, int[]> bands)
        {
            score = Math.Clamp(score, 0, 100); // an out-of-range score must still land in a band
            foreach (var kv in bands)
                if (kv.Value is { Length: >= 2 } r && score >= r[0] && score <= r[1])
                    return kv.Key;
            return "Emerging";
        }

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

    /// <summary>
    /// Deserialises the governance "bands" object into Dictionary&lt;string,int[]&gt;
    /// while tolerating documentation keys whose value is NOT an int array
    /// (e.g. "_comment": "..."). Such keys are skipped rather than throwing,
    /// so the intentionally-commented governance-weights.json loads cleanly.
    /// </summary>
    public sealed class CommentTolerantBandsConverter : JsonConverter<Dictionary<string, int[]>>
    {
        public override Dictionary<string, int[]> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var result = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected object for governance bands.");

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) return result;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("Expected property name in governance bands.");

                var key = reader.GetString() ?? "";
                reader.Read();

                // Only [int, int, ...] values are bands; skip anything else
                // (string comments, objects, etc.) so docs don't break the load.
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    var nums = new List<int>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType == JsonTokenType.Number) nums.Add(reader.GetInt32());
                    }
                    result[key] = nums.ToArray();
                }
                else
                {
                    reader.Skip(); // non-array value (e.g. _comment) — ignore
                }
            }
            throw new JsonException("Unexpected end of JSON in governance bands.");
        }

        public override void Write(
            Utf8JsonWriter writer, Dictionary<string, int[]> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kvp in value)
            {
                writer.WritePropertyName(kvp.Key);
                writer.WriteStartArray();
                foreach (var n in kvp.Value) writer.WriteNumberValue(n);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
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

        /// <summary>
        /// F6: findings that FAIL/WARN but are client-accepted-by-design on this instance.
        /// Counted in <see cref="PassedFindings"/> for score math; surfaced separately so the
        /// UI can read "N open · M accepted" and an auditor sees what was signed off.
        /// </summary>
        public int AcceptedFindings { get; set; }

        /// <summary>
        /// A2 (2026-07-13): checks that errored during execution (CheckResult.ErrorMessage != null).
        /// These are a subset of IsSkip and stay EXCLUDED from score math — an error is absence of
        /// evidence, scored as neither pass nor fail — but they are surfaced separately so the
        /// governance strip's "N informational/skipped" label doesn't quietly bury execution errors
        /// inside a benign-sounding count.
        /// </summary>
        public int ErroredFindings { get; set; }

        /// <summary>
        /// 2026-07-21: checks whose verdict is WARN — the state every user-facing surface labels
        /// <b>Partial</b>. Like <see cref="ErroredFindings"/> these stay EXCLUDED from score math
        /// (a WARN asserts nothing about the control, so it belongs in neither the numerator nor
        /// the denominator) but they are counted here so the roll-up surfaces can decompose the
        /// excluded tier instead of presenting it as one benign "informational/skipped" number.
        ///
        /// Disjoint from <see cref="ErroredFindings"/> by construction (errored rows are filtered
        /// out), so Passed + Failed + Partial + Errored + (plain informational/skipped) ==
        /// <see cref="TotalFindings"/> — no surface has to invent a figure to make the page foot.
        /// </summary>
        public int PartialFindings { get; set; }
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

        /// <summary>
        /// True when the category had at least one scorable check-result this run.
        /// Unassessed categories (FindingCount == 0) are EXCLUDED from the overall
        /// weighted average — they neither reward nor penalise (fix 2026-06-04;
        /// previously they scored a free 100% that inflated the headline).
        /// </summary>
        public bool Assessed { get; set; }
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
