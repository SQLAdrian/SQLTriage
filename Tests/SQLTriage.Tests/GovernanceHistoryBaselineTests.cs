/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// P3 — baseline ("gospel") freeze + Fail→Pass / Pass→Fail transitions.
    /// Each test points the service at a throwaway dbDir (test seam) so the
    /// production governance-history.db is never touched.
    /// </summary>
    public class GovernanceHistoryBaselineTests : IDisposable
    {
        private readonly string _tempDir;

        public GovernanceHistoryBaselineTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "gov-baseline-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(_tempDir, recursive: true);
            }
            catch { /* test cleanup; ignore */ }
        }

        private GovernanceHistoryService NewService()
            => new(NullLogger<GovernanceHistoryService>.Instance, retentionDays: 365, dbDir: _tempDir);

        private static CheckResult Chk(string id, bool passed, double effort = 1.0, string sev = "High")
            => new()
            {
                CheckId = id,
                CheckName = id + " name",
                Category = "Security",
                Severity = sev,
                Passed = passed,
                EffortHours = effort,
            };

        [Fact]
        public void RecordBaseline_FirstCapture_SetsActiveAndCounts()
        {
            using var svc = NewService();
            Assert.False(svc.HasBaseline("SRV1"));

            var id = svc.RecordBaseline("SRV1", compositeScore: 70, new[]
            {
                Chk("SQLT-A", passed: true),
                Chk("SQLT-B", passed: false),
                Chk("SQLT-C", passed: false),
            });

            Assert.True(id > 0);
            Assert.True(svc.HasBaseline("SRV1"));

            var b = svc.GetActiveBaseline("SRV1");
            Assert.NotNull(b);
            Assert.Equal(70, b!.CompositeScore);
            Assert.Equal(3, b.TotalChecks);
            Assert.Equal(1, b.PassedChecks);
            Assert.Equal(2, b.FailedChecks);
            Assert.Null(b.Reason); // first/gospel has no reason
        }

        [Fact]
        public void RecordBaseline_ReBaseline_SupersedesPreviousActive()
        {
            using var svc = NewService();
            var first = svc.RecordBaseline("SRV1", 50, new[] { Chk("SQLT-A", false) });
            var second = svc.RecordBaseline("SRV1", 80, new[] { Chk("SQLT-A", true) }, reason: "post-remediation milestone");

            Assert.NotEqual(first, second);

            // Only the second baseline is active, and it carries the re-baseline reason.
            var active = svc.GetActiveBaseline("SRV1");
            Assert.NotNull(active);
            Assert.Equal(second, active!.BaselineId);
            Assert.Equal(80, active.CompositeScore);
            Assert.Equal("post-remediation milestone", active.Reason);
        }

        [Fact]
        public void ComputeTransitions_NoBaseline_ReturnsNull()
        {
            using var svc = NewService();
            var result = svc.ComputeTransitions("SRV-UNSEEN", new[] { Chk("SQLT-A", true) }, 90);
            Assert.Null(result);
        }

        [Fact]
        public void ComputeTransitions_ClassifiesAllFourBuckets()
        {
            using var svc = NewService();
            // Baseline: A fail, B pass, C fail, D fail.
            svc.RecordBaseline("SRV1", 60, new[]
            {
                Chk("SQLT-A", passed: false),
                Chk("SQLT-B", passed: true),
                Chk("SQLT-C", passed: false),
                Chk("SQLT-D", passed: false),
            });

            // Current: A now passes (resolved), B now fails (regressed),
            // C still fails (no transition), D absent (disappeared-failing),
            // E new and failing (newly-failing).
            var current = new[]
            {
                Chk("SQLT-A", passed: true),
                Chk("SQLT-B", passed: false),
                Chk("SQLT-C", passed: false),
                Chk("SQLT-E", passed: false),
            };

            var r = svc.ComputeTransitions("SRV1", current, currentCompositeScore: 75);
            Assert.NotNull(r);

            Assert.Single(r!.Resolved);
            Assert.Equal("SQLT-A", r.Resolved[0].CheckId);

            Assert.Single(r.Regressed);
            Assert.Equal("SQLT-B", r.Regressed[0].CheckId);

            Assert.Single(r.NewlyFailing);
            Assert.Equal("SQLT-E", r.NewlyFailing[0].CheckId);

            Assert.Single(r.DisappearedFailing);
            Assert.Equal("SQLT-D", r.DisappearedFailing[0].CheckId);

            Assert.Equal(15, r.HealthDelta); // 75 - 60
        }

        [Fact]
        public void ComputeTransitions_TransitionCarriesEffortHours()
        {
            using var svc = NewService();
            svc.RecordBaseline("SRV1", 40, new[] { Chk("SQLT-A", passed: false, effort: 8.0) });

            var r = svc.ComputeTransitions("SRV1",
                new[] { Chk("SQLT-A", passed: true, effort: 8.0) }, 55);

            Assert.NotNull(r);
            Assert.Single(r!.Resolved);
            Assert.Equal(8.0, r.Resolved[0].EffortHours);
        }

        [Fact]
        public void ComputeTransitions_AgainstActiveBaselineOnly_AfterReBaseline()
        {
            using var svc = NewService();
            // Gospel: A fail.
            svc.RecordBaseline("SRV1", 30, new[] { Chk("SQLT-A", passed: false) });
            // Re-baseline after fixing A: now A pass is the new gospel.
            svc.RecordBaseline("SRV1", 90, new[] { Chk("SQLT-A", passed: true) }, reason: "milestone");

            // Current still passes A — measured against the NEW baseline this is no transition.
            var r = svc.ComputeTransitions("SRV1", new[] { Chk("SQLT-A", passed: true) }, 90);
            Assert.NotNull(r);
            Assert.Empty(r!.Resolved);
            Assert.Empty(r.Regressed);
            Assert.Equal(0, r.HealthDelta);
        }
    }
}
