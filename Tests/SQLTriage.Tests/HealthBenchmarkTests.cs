/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.Text.Json;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class HealthBenchmarkTests
    {
        private static HealthBenchmark Bench(params int[] values)
        {
            var list = new List<int>(values);
            list.Sort();
            return new HealthBenchmark
            {
                Metric = "FailedItems",
                LowerIsBetter = true,
                N = list.Count,
                Min = list.Count > 0 ? list[0] : 0,
                Max = list.Count > 0 ? list[^1] : 0,
                Avg = 0,
                SortedValues = list,
            };
        }

        [Fact]
        public void Value_betterThanAll_ranksTop()
        {
            var p = HealthBenchmarkProvider.ComputePercentile(Bench(10, 20, 30, 40), value: 0);
            Assert.Equal(100.0, p.HealthierThanPct);
            Assert.Equal(0.0, p.TopPercent);
            Assert.True(p.IsAboveMedian);
        }

        [Fact]
        public void Value_worseThanAll_ranksBottom()
        {
            var p = HealthBenchmarkProvider.ComputePercentile(Bench(10, 20, 30, 40), value: 100);
            Assert.Equal(0.0, p.HealthierThanPct);
            Assert.Equal(100.0, p.TopPercent);
            Assert.False(p.IsAboveMedian);
        }

        [Fact]
        public void Value_inGap_countsStrictlyWorsePeers()
        {
            // [10,20,30,40], value 15 → 3 peers worse (20,30,40), 1 better (10).
            var p = HealthBenchmarkProvider.ComputePercentile(Bench(10, 20, 30, 40), value: 15);
            Assert.Equal(75.0, p.HealthierThanPct);
            Assert.Equal(25.0, p.TopPercent);
        }

        [Fact]
        public void Ties_getHalfCredit()
        {
            // [10,20,20,30], value 20 → 1 worse(30), 1 better(10), 2 equal → (1 + 0.5*2)/4 = 50%.
            var p = HealthBenchmarkProvider.ComputePercentile(Bench(10, 20, 20, 30), value: 20);
            Assert.Equal(50.0, p.HealthierThanPct);
            Assert.Equal(50.0, p.TopPercent);
        }

        [Fact]
        public void HigherIsBetter_direction_inverts()
        {
            var b = Bench(10, 20, 30, 40);
            b.LowerIsBetter = false; // e.g. a "pass rate" metric
            var p = HealthBenchmarkProvider.ComputePercentile(b, value: 15);
            // 1 peer below (10) → beats 25%.
            Assert.Equal(25.0, p.HealthierThanPct);
        }

        [Fact]
        public void Median_isReported()
        {
            var p = HealthBenchmarkProvider.ComputePercentile(Bench(10, 20, 30, 40), value: 25);
            Assert.Equal(30, p.Median); // sorted[n/2] = sorted[2] = 30
            Assert.Equal(4, p.BaselineN);
        }

        [Fact]
        public void Deserializes_artifact_json_with_camelCase_keys()
        {
            const string json = """
            {
              "schemaVersion": 1,
              "source": "test",
              "asOf": "2026-06-04",
              "metric": "FailedItems",
              "lowerIsBetter": true,
              "n": 3,
              "min": 0, "max": 4, "avg": 2.0,
              "sortedValues": [0, 2, 4],
              "secondary": { "metric": "AllFailedItems", "breakpoints": { "p50": 131 } }
            }
            """;
            var b = JsonSerializer.Deserialize<HealthBenchmark>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            Assert.NotNull(b);
            Assert.Equal(3, b!.N);
            Assert.Equal(new[] { 0, 2, 4 }, b.SortedValues.ToArray());
            Assert.True(b.LowerIsBetter);
            Assert.NotNull(b.Secondary);
            Assert.Equal(131.0, b.Secondary!.Breakpoints["p50"]);
        }
    }
}
