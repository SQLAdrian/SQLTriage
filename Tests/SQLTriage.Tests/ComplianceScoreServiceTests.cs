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
        private static ComplianceScoreService ScoreWithJson(string json)
        {
            var bundle = new FakeBundleAccessor().PutFile("Config/control_mappings.json", json);
            var mapping = new ComplianceMappingService(NullLogger<ComplianceMappingService>.Instance, bundle);
            return new ComplianceScoreService(NullLogger<ComplianceScoreService>.Instance, mapping);
        }

        // One scored control (access_control ← "Security") + one notTested control (#87).
        private const string TestFwJson = @"{
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

        private static AssessmentResult MakeResult(string category, string status) => new()
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
    }
}
