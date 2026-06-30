/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Peer-benchmark distribution: where a client estate's check-failure burden sits
    /// relative to a real, anonymized baseline of production SQL Servers
    /// (SQLDBA.ORG; see <c>tools/extract_health_benchmark.py</c>). The artifact is
    /// just integers + summary stats — no customer/server identifiers.
    ///
    /// Ranking metric is FailedItems (count of failing checks per server, lower = healthier) —
    /// the same metric the source's GlobalHealthRank is built on, so the comparison is
    /// genuinely like-for-like on the shared baseline check set.
    /// </summary>
    public sealed class HealthBenchmark
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
        [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
        [JsonPropertyName("asOf")] public string AsOf { get; set; } = string.Empty;
        [JsonPropertyName("metric")] public string Metric { get; set; } = "FailedItems";
        [JsonPropertyName("lowerIsBetter")] public bool LowerIsBetter { get; set; } = true;
        [JsonPropertyName("n")] public int N { get; set; }
        [JsonPropertyName("min")] public int Min { get; set; }
        [JsonPropertyName("max")] public int Max { get; set; }
        [JsonPropertyName("avg")] public double Avg { get; set; }

        /// <summary>Ascending-sorted per-server metric values. Length == N.</summary>
        [JsonPropertyName("sortedValues")] public List<int> SortedValues { get; set; } = new();

        [JsonPropertyName("secondary")] public HealthBenchmarkSecondary? Secondary { get; set; }
    }

    public sealed class HealthBenchmarkSecondary
    {
        [JsonPropertyName("metric")] public string Metric { get; set; } = string.Empty;
        [JsonPropertyName("breakpoints")] public Dictionary<string, double> Breakpoints { get; set; } = new();
    }

    /// <summary>
    /// Result of ranking one estate value against the baseline.
    /// <paramref name="HealthierThanPct"/> is the share of peers the estate beats (higher = better);
    /// <paramref name="TopPercent"/> is its complement ("top X% of production servers").
    /// </summary>
    public sealed record BenchmarkPercentile(
        int Value,
        int BaselineN,
        double HealthierThanPct,
        double TopPercent,
        int Median,
        double Avg,
        bool IsAboveMedian);

    public interface IHealthBenchmarkProvider
    {
        /// <summary>The active benchmark, or null when no artifact is present (e.g. free-state boot).</summary>
        HealthBenchmark? Current { get; }

        /// <summary>Rank an estate's FailedItems against the baseline; null when no benchmark is loaded.</summary>
        BenchmarkPercentile? Rank(int failedItems);
    }

    /// <inheritdoc cref="IHealthBenchmarkProvider"/>
    public sealed class HealthBenchmarkProvider : IHealthBenchmarkProvider
    {
        private readonly ILogger<HealthBenchmarkProvider> _logger;
        private readonly IBundleAccessor _bundle;
        private readonly object _lock = new();
        private HealthBenchmark? _cached;
        private bool _loaded;

        public HealthBenchmarkProvider(ILogger<HealthBenchmarkProvider> logger, IBundleAccessor bundle)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
            _bundle.BundleStateChanged += (_, _) => { lock (_lock) { _loaded = false; _cached = null; } };
        }

        public HealthBenchmark? Current
        {
            get
            {
                if (_loaded) return _cached;
                lock (_lock)
                {
                    if (_loaded) return _cached;
                    _cached = LoadFromBundle();
                    _loaded = true;
                }
                return _cached;
            }
        }

        public BenchmarkPercentile? Rank(int failedItems)
        {
            var b = Current;
            if (b is null || b.SortedValues.Count == 0) return null;
            return ComputePercentile(b, failedItems);
        }

        /// <summary>
        /// Pure percentile computation (no I/O) — exposed for testing. Uses the full sorted
        /// array for an exact rank. Ties get half-credit (standard percentile-rank convention).
        /// </summary>
        public static BenchmarkPercentile ComputePercentile(HealthBenchmark b, int value)
        {
            var sorted = b.SortedValues;
            int n = sorted.Count;

            int less = LowerBound(sorted, value);   // count of peers strictly less than value
            int lessOrEqual = UpperBound(sorted, value);
            int equal = lessOrEqual - less;
            int greater = n - lessOrEqual;          // count of peers strictly greater than value

            // lowerIsBetter: the estate is "healthier than" peers with MORE fails, plus half the ties.
            double healthierThanPct = b.LowerIsBetter
                ? (greater + 0.5 * equal) / n * 100.0
                : (less + 0.5 * equal) / n * 100.0;

            healthierThanPct = Math.Round(Math.Clamp(healthierThanPct, 0.0, 100.0), 1);
            double topPercent = Math.Round(100.0 - healthierThanPct, 1);

            int median = sorted[n / 2];
            bool isAboveMedian = b.LowerIsBetter ? value < median : value > median;

            return new BenchmarkPercentile(value, n, healthierThanPct, topPercent, median, b.Avg, isAboveMedian);
        }

        // First index whose value >= target (count of strictly-less elements).
        private static int LowerBound(List<int> a, int target)
        {
            int lo = 0, hi = a.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (a[mid] < target) lo = mid + 1; else hi = mid; }
            return lo;
        }

        // First index whose value > target (count of <= elements).
        private static int UpperBound(List<int> a, int target)
        {
            int lo = 0, hi = a.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (a[mid] <= target) lo = mid + 1; else hi = mid; }
            return lo;
        }

        private HealthBenchmark? LoadFromBundle()
        {
            var text = _bundle.GetText("Config/health-benchmark.json");
            if (text is null)
            {
                _logger.LogInformation(
                    "[HealthBenchmark] health-benchmark.json not in current bundle (tier={Tier}); peer index unavailable.",
                    _bundle.Tier);
                return null;
            }
            try
            {
                var b = JsonSerializer.Deserialize<HealthBenchmark>(
                    text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (b is null || b.SortedValues.Count == 0)
                {
                    _logger.LogWarning("[HealthBenchmark] artifact parsed empty; peer index unavailable.");
                    return null;
                }
                // Defend against an unsorted artifact (binary search assumes ascending).
                b.SortedValues.Sort();
                _logger.LogInformation(
                    "[HealthBenchmark] Loaded peer baseline n={N} (metric={Metric}, asOf={AsOf}).",
                    b.N, b.Metric, b.AsOf);
                return b;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HealthBenchmark] Failed to parse health-benchmark.json; peer index unavailable.");
                return null;
            }
        }
    }
}
