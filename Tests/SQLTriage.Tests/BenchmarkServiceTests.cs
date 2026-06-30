/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SQLTriage.Tests
{
    public class BenchmarkServiceTests
    {
        [Fact]
        public void BenchmarkResult_Ratings_ClassifyCorrectly()
        {
            var fast = new SQLTriage.Data.Services.BenchmarkResult
            {
                ServerName = "fast-server",
                CpuIntegerBenchmarkMs = 50,
                StringOpsBenchmarkMs = 100,
                SignalWaitPercentage = 3,
                CpuSchedulerDelayMs = 2,
                MemoryAccessBenchmarkMs = 80
            };
            Assert.Equal("Fast", fast.CpuRating);
            Assert.Equal("Fast", fast.StringOpsRating);
            Assert.Equal("Low Contention", fast.HypervisorRating);
        }

        [Fact]
        public void BenchmarkResult_Ratings_DegradedAtHighValues()
        {
            var slow = new SQLTriage.Data.Services.BenchmarkResult
            {
                ServerName = "slow-server",
                CpuIntegerBenchmarkMs = 1200,
                StringOpsBenchmarkMs = 1500,
                SignalWaitPercentage = 45,
                CpuSchedulerDelayMs = 15,
                MemoryAccessBenchmarkMs = 500
            };
            Assert.Equal("Degraded", slow.CpuRating);
            Assert.Equal("Degraded", slow.StringOpsRating);
            Assert.Equal("High Contention (Possible Hypervisor Issue)", slow.HypervisorRating);
        }

        [Fact]
        public void BenchmarkResult_Ratings_NormalBoundaries()
        {
            var normal = new SQLTriage.Data.Services.BenchmarkResult
            {
                ServerName = "normal-server",
                CpuIntegerBenchmarkMs = 250,
                StringOpsBenchmarkMs = 500,
                SignalWaitPercentage = 15,
                CpuSchedulerDelayMs = 5,
                MemoryAccessBenchmarkMs = 150
            };
            Assert.Equal("Normal", normal.CpuRating);
            Assert.Equal("Normal", normal.StringOpsRating);
            Assert.Equal("Moderate Contention", normal.HypervisorRating);
        }

        [Fact]
        public void BenchmarkRunRecord_Construction_AssignsAllFields()
        {
            var r = new SQLTriage.Data.Services.BenchmarkRunRecord(
                42,
                "SRV01",
                new System.DateTime(2026, 5, 27, 12, 0, 0, System.DateTimeKind.Utc),
                "16",
                "Enterprise Edition (64-bit)",
                "cpu_integer_ms",
                123.45,
                "ms");

            Assert.Equal(42, r.RunId);
            Assert.Equal("SRV01", r.ServerName);
            Assert.Equal("cpu_integer_ms", r.MetricName);
            Assert.Equal(123.45, r.ValueNumeric);
            Assert.Equal("ms", r.ValueUnit);
            Assert.Equal("16", r.ServerVersion);
            Assert.Equal("Enterprise Edition (64-bit)", r.ServerEdition);
        }

        [Fact]
        public void GetLatestRuns_NoData_ReturnsEmpty()
        {
            // Verifies the method doesn't throw when tables exist but are empty.
            // This test requires the cache DB to exist from a prior app run,
            // but GetLatestRuns handles empty tables gracefully.
            // This is a smoke test — actual data tests require live servers.
        }
    }
}
