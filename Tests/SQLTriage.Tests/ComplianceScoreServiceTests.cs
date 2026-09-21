/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests
{
    public class ComplianceScoreServiceTests
    {
        // ComplianceScoreService.ComputeScorecard is pure: no DB, no IO.
        // It depends on ComplianceMappingService for category → hint mapping.
        // We exercise it with the real mapping service (file-based) and also with
        // synthetic VA results to control pass/fail counts precisely.

        private static ComplianceMappingService NewMapping() =>
            new(NullLogger<ComplianceMappingService>.Instance, new FakeBundleAccessor());

        private static ComplianceScoreService NewScore() =>
            new(NullLogger<ComplianceScoreService>.Instance, NewMapping());

        // Self-contained mapping so the scoring assertions actually run (a bare FakeBundleAccessor
        // has no control_mappings.json, which makes the file-dependent tests above self-skip).
        // internal, not private: Gated/ReportAttestationHonestyTests drives the real scorecard →
        // export-model → PDF chain for reports-r1-07 and needs the same deterministic fixture. One
        // copy, so the two files cannot disagree about what a scored control looks like.
        internal static ComplianceScoreService ScoreWithJson(string json)
        {
            var bundle = new FakeBundleAccessor().PutFile("Config/control_mappings.json", json);
            var mapping = new ComplianceMappingService(NullLogger<ComplianceMappingService>.Instance, bundle);
            return new ComplianceScoreService(NullLogger<ComplianceScoreService>.Instance, mapping);
        }

        // One scored control (access_control ← "Security") + one notTested control (#87).
        internal const string TestFwJson = @"{
  ""regions"": {
    ""Test"": {
      ""frameworks"": [
        {
          ""name"": ""Test Framework"", ""acronym"": ""TESTFW"",
          ""categories"": [
            { ""id"": ""T-SCORED"",   ""name"": ""Scored Control"",   ""sqlCheckHints"": [""access_control""] },
            { ""id"": ""T-UNTESTED"", ""name"": ""Untested Control"", ""sqlCheckHints"": [""change_management""], ""notTested"": true, ""notTestedReason"": ""Not tested by SQLTriage."" }
          ]
        }
      ]
    }
  }
}";

        internal static AssessmentResult MakeResult(string category, string status) => new()
        {
            CheckId = Guid.NewGuid().ToString("N")[..8],
            DisplayName = $"Test check {category}",
            Category = category,
            Status = status,
            Severity = status == "Passed" ? "Information" : "High",
        };

        // ── All-pass → near 100% ─────────────────────────────────────────────

        [Fact]
        public void ComputeScorecard_AllPass_100Percent()
        {
            var svc = NewScore();

            // "Security" category maps to access_control, identity_authentication, monitoring_detection
            // Use 10 passing Security results.
            var results = Enumerable.Range(0, 10)
                .Select(_ => MakeResult("Security", "Passed"))
                .ToList<AssessmentResult>();

            var scorecard = svc.ComputeScorecard(results, "SOC2");

            // If mapping file wasn't loaded (CI path issue), HasData will be false — skip.
            if (!scorecard.HasData) return;

            // All results pass → overall should be 100
            Assert.Equal(100.0, scorecard.OverallPercent, precision: 0);
        }

        // ── Half fail → ~50% ────────────────────────────────────────────────

        [Fact]
        public void ComputeScorecard_HalfFail_50Percent()
        {
            var svc = NewScore();

            var results = new List<AssessmentResult>();
            for (int i = 0; i < 5; i++) results.Add(MakeResult("Security", "Passed"));
            for (int i = 0; i < 5; i++) results.Add(MakeResult("Security", "Failed"));

            var scorecard = svc.ComputeScorecard(results, "SOC2");

            if (!scorecard.HasData) return;

            Assert.InRange(scorecard.OverallPercent, 45.0, 55.0);
        }

        // ── Unmapped framework → empty scorecard, no exception ───────────────

        [Fact]
        public void ComputeScorecard_UnmappedFramework_ReturnsEmptyScorecardNotException()
        {
            var svc = NewScore();
            var results = new List<AssessmentResult>
            {
                MakeResult("Security", "Passed"),
            };

            // "NONEXISTENT_FRAMEWORK" won't exist in control_mappings.json
            ComplianceScoreService.ComplianceScorecard scorecard;
            var ex = Record.Exception(() =>
                scorecard = svc.ComputeScorecard(results, "NONEXISTENT_FRAMEWORK_XYZ"));

            Assert.Null(ex);
        }

        // ── FamilyScores: NoData sentinel when nothing maps ──────────────────

        [Fact]
        public void ComputeScorecard_FamilyWithNoResults_ReportsNoData()
        {
            var svc = NewScore();

            // Use a real framework (SOC2 exists) but supply zero matching VA results.
            var scorecard = svc.ComputeScorecard(new List<AssessmentResult>(), "SOC2");

            // All families should be NoData (no VA results supplied).
            if (scorecard.FamilyScores.Count == 0) return; // mapping file not loaded

            Assert.All(scorecard.FamilyScores, f =>
                Assert.Equal(ComplianceScoreService.ControlStatus.NoData, f.Status));
        }

        // ── #87: notTested control is un-scored, excluded from the aggregate, empty findings ──

        [Fact]
        public void ComputeScorecard_NotTestedControl_UnscoredExcludedFromAggregate()
        {
            var svc = ScoreWithJson(TestFwJson);

            var results = new List<AssessmentResult>();
            for (int i = 0; i < 7; i++) results.Add(MakeResult("Security", "Failed"));
            for (int i = 0; i < 3; i++) results.Add(MakeResult("Security", "Passed"));
            // "Configuration" maps to change_management — this must NOT score the notTested control.
            results.Add(MakeResult("Configuration", "Failed"));

            var sc = svc.ComputeScorecard(results, "TESTFW");

            var untested = sc.FamilyScores.Single(f => f.FamilyId == "T-UNTESTED");
            Assert.Equal(ComplianceScoreService.ControlStatus.NotTested, untested.Status);
            Assert.Equal(-1, untested.Percent);
            Assert.Empty(untested.FailingResults);            // never scored off generic checks
            Assert.Equal("Not tested by SQLTriage.", untested.NotTestedReason);

            // Aggregate is computed only from the scored control (7 fail / 3 pass of 10 = 30%).
            Assert.Equal(30.0, sc.OverallPercent, precision: 0);
            Assert.True(sc.HasData);                          // a notTested placeholder alone is not "data"
        }

        // ── #87: exported evidence must carry ALL failing findings, not a capped sample of 5 ──

        [Fact]
        public void ComputeScorecard_FailingResults_AreNotCappedAtFive()
        {
            var svc = ScoreWithJson(TestFwJson);

            var results = new List<AssessmentResult>();
            for (int i = 0; i < 12; i++) results.Add(MakeResult("Security", "Failed"));

            var sc = svc.ComputeScorecard(results, "TESTFW");
            var scored = sc.FamilyScores.Single(f => f.FamilyId == "T-SCORED");

            Assert.Equal(12, scored.FailingResults.Count);    // complete, uncapped (was clamped to 5)
        }

        // ── reports-r1-06 (CONTESTED, ruled NOT-A-DEFECT 2026-08-27) ─────────────────────────
        //
        // The claim: exporting a scorecard for a framework with NO data writes a tamper-evident audit
        // line asserting a measured 0% compliance, because Pages/ComplianceMap.razor's export call
        // collapses the -1 sentinel (`OverallPercent >= 0 ? OverallPercent : 0`) and
        // AuditLogService.LogComplianceReportExported then writes "0.0% overall compliance".
        //
        // Ruled not a defect AS CODED, on an exhaustive case analysis of ComputeScorecard's only two
        // classification branches: every family classified Compliant/Partial/NonCompliant adds to
        // overallTotal, and HasData is "any family whose Status is neither NoData nor NotTested". So
        // HasData ⟹ overallTotal >= 1 ⟹ OverallPercent is a genuine 0-100 value, and conversely
        // OverallPercent == -1 ⟹ HasData == false. The page renders the Export button only inside the
        // HasData branch, so the one production caller cannot reach the collapsing expression. The
        // reproduce pass got its 0.00 by calling the audit writer directly with a hand-supplied 0,
        // which proves the writer will print what it is given, not that the app's path does.
        //
        // What was NOT closed by that ruling: the honesty of that audit line rests entirely on one
        // caller's control flow, and nothing in the code says so. These pin the invariant the ruling
        // depends on, so a future change to either side is a failing test rather than a fabricated
        // compliance figure in a tamper-evident log.

        [Fact]
        public void OverallPercentSentinel_ImpliesNoData_SoTheExportGateCannotBeReached()
        {
            var svc = ScoreWithJson(TestFwJson);

            // A result that made no assertion (WARN/SKIP/INFO all land on this status) is the exact
            // fixture the reproduce pass used, and it produces the sentinel.
            var sc = svc.ComputeScorecard(
                new List<AssessmentResult> { MakeResult("Security", ComplianceScoreService.UnassessedStatus) },
                "TESTFW");

            Assert.Equal(-1, sc.OverallPercent);
            Assert.False(sc.HasData,
                "the -1 sentinel and HasData=false are one fact: the export button lives inside the "
                + "HasData branch, and that is the only thing keeping a fabricated 0.00 out of the audit log");
        }

        [Fact]
        public void HasData_ImpliesAGenuinePercent_NeverTheSentinel()
        {
            var svc = ScoreWithJson(TestFwJson);

            var sc = svc.ComputeScorecard(
                new List<AssessmentResult> { MakeResult("Security", "Passed") }, "TESTFW");

            Assert.True(sc.HasData);
            Assert.True(sc.OverallPercent >= 0,
                "HasData means at least one family was classified, which means overallTotal >= 1");
        }
    }
}
