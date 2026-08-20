/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Cli;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Ruling #4 (2026-07-20) — the WARN verdict tier.
    ///
    /// The corpus (master 7e365cc0) emits WARN from inside its <c>## Query</c> blocks by
    /// downgrading a would-be PASS when the check could not fully assess the target:
    /// <c>CASE WHEN (@errs > 0 OR @tf_unknown = 1 OR @worklist_ok = 0) AND @__result = 'PASS'
    /// THEN 'WARN' ELSE @__result END</c>. That expression was run against a live .\old2017
    /// instance on 2026-07-20 and returns the exact token <c>WARN</c>, unpadded — which is why
    /// the fixtures below use that literal.
    ///
    /// The property under test, stated once: a WARN is a PERMISSIONS/VISIBILITY observation, not
    /// a server-configuration defect. It must therefore be
    ///   (a) not a failure  — never Passed=false, never in an open-finding list, never in issues[];
    ///   (b) not a pass     — never counted as a passed check, never a green badge;
    ///   (c) not scored     — in neither the numerator nor the denominator of any rate or grade;
    ///   (d) visible        — its own distinct state everywhere a human or machine reads a result.
    /// Before this ruling it was (a)-violating: force-failed at CheckExecutionService.cs:697.
    /// </summary>
    public class WarnVerdictTests
    {
        /// <summary>A corpus WARN as CheckExecutionService now produces one: Passed=true
        /// ("not a failure"), Verdict="WARN", no ErrorMessage (it ran fine — it just could not
        /// see everything), and a non-INFO declared Severity.</summary>
        internal static CheckResult Warn(string id = "W1", string category = "Security", string severity = "High") =>
            new()
            {
                CheckId = id,
                CheckName = "w-" + id,
                Category = category,
                Severity = severity,
                Passed = true,
                Verdict = "WARN",
                Message = "Could not read 3 of 14 databases (insufficient permissions).",
            };

        // ── (a)+(b) the executor's own mapping ────────────────────────────────────────────────

        [Fact]
        public void Executor_TreatsWarnAsNotAFailure()
        {
            // THE regression this ruling exists to prevent. Until 2026-07-20 this returned false,
            // so a corpus WARN reached the client as a FAILED server check.
            Assert.True(CheckExecutionService.IsNotFailureVerdict("WARN"));
        }

        [Theory]
        [InlineData("PASS")]
        [InlineData("SKIP")]
        [InlineData("INFO")]
        [InlineData("WARN")]
        public void Executor_NotFailureTier_IsExactlyTheFourDeclaredTiers(string verdict) =>
            Assert.True(CheckExecutionService.IsNotFailureVerdict(verdict));

        [Theory]
        [InlineData("FAIL")]
        [InlineData("")]
        [InlineData("WARNING")]   // near-miss: must NOT be absorbed into the WARN tier
        [InlineData("UNKNOWN")]
        public void Executor_EverythingElseStaysAFailure_FailsClosed(string verdict) =>
            Assert.False(CheckExecutionService.IsNotFailureVerdict(verdict));

        // ── (c) scoring: neither numerator nor denominator ────────────────────────────────────

        [Fact]
        public void IsWarn_True_ForVerdictWarn_CaseInsensitive()
        {
            var r = Warn();
            r.Verdict = "warn";
            Assert.True(CheckClassification.IsWarn(r));
        }

        [Fact]
        public void IsWarn_False_ForEveryOtherVerdict()
        {
            foreach (var v in new[] { "PASS", "FAIL", "SKIP", "INFO", "WARNING", "" })
            {
                var r = Warn();
                r.Verdict = v;
                Assert.False(CheckClassification.IsWarn(r), $"verdict '{v}' must not read as WARN");
            }
        }

        [Fact]
        public void Warn_IsNotScorable_SoItCannotDragOrInflateAScore()
        {
            var r = Warn();
            Assert.False(CheckClassification.IsScorable(r));
            // And it is genuinely its OWN bucket, not smuggled in as a skip or an info.
            Assert.False(CheckClassification.IsSkip(r));
            Assert.False(CheckClassification.IsInfo(r));
        }

        [Fact]
        public async Task Governance_WarnIsNeitherPassedNorFailed_AndDoesNotMoveTheScore()
        {
            var svc = new GovernanceService(
                NullLogger<GovernanceService>.Instance,
                new WarnFixedWeightsProvider(new GovernanceWeights()));

            var baseline = new List<CheckResult>
            {
                new() { CheckId = "P1", CheckName = "p1", Category = "Security", Severity = "High", Passed = true },
                new() { CheckId = "F1", CheckName = "f1", Category = "Security", Severity = "High", Passed = false },
            };
            var withWarn = new List<CheckResult>(baseline) { Warn() };

            var before = await svc.ComputeFullAsync(baseline);
            var after  = await svc.ComputeFullAsync(withWarn);

            // Not counted as a defect...
            Assert.Equal(before.FailedFindings, after.FailedFindings);
            // ...and not counted as a pass either.
            Assert.Equal(before.PassedFindings, after.PassedFindings);
            // Out of the denominator too: adding an unassessable check must not move the grade.
            Assert.Equal(before.Overall, after.Overall);
        }

        // ── (d) visibility: every human/machine surface renders it distinctly ─────────────────

        [Fact]
        public void Cli_MapsWarnToItsOwnState_NotPassNotFail()
        {
            var state = CliResultState.Of(Warn());
            Assert.Equal(FindingState.Warn, state);
            Assert.NotEqual(FindingState.Pass, state);
            Assert.NotEqual(FindingState.Fail, state);
        }

        [Fact]
        public void Cli_CsvStatusColumn_RoundTripsTheCorpusToken()
        {
            // Machine-readable column: must be the corpus token. NOT_ASSESSED is already taken
            // (a server that produced no results at all) and means something else entirely.
            Assert.Equal("WARN", CliResultState.Label(FindingState.Warn));
        }

        [Fact]
        public void Cli_WarnBeatsInfo_WhenACheckDeclaresInfoSeverity()
        {
            // A WARN on a check whose declared Severity is "Info" must still read as Warn. If the
            // Info arm won, the "could not assess" signal would silently vanish — the exact
            // failure mode this state exists to stop.
            var r = Warn(severity: "Info");
            Assert.True(CheckClassification.IsInfo(r));           // both predicates match...
            Assert.Equal(FindingState.Warn, CliResultState.Of(r)); // ...and Warn wins the cascade.
        }

        [Fact]
        public void ScheduledReport_MapsWarnToItsOwnRowState_NotPass()
        {
            // RENAMED 2026-07-20. This was called ScheduledReport_ExportsWarnDistinctly_NotAsAGreenPass
            // and asserted only the DTO — the EXPORT it named was never exercised, and the cold gate
            // found the export did NOT show the row. A test name asserting a property nobody
            // exercised is this repo's dominant defect class, so the name now claims exactly what
            // it checks (the DTO mapping) and the export claim moved to the test below, which
            // renders the real PDF and reads the text back out.
            var briefing = ScheduledReportService.BuildBriefing(
                new AssessmentMeta { Title = "t", GeneratedUtc = "2026-07-20T00:00Z" },
                new[] { Warn() });

            var row = Assert.Single(briefing.Findings);
            Assert.Equal(FindingState.Warn, row.State);
        }

        // ScheduledReport_ExportedBriefingPdf_TellsTheReaderTheRunWasNotFullyAssessed moved
        // 2026-08-04 to Gated/WarnBriefingPdfTests.cs. It renders the Executive Briefing, which the
        // community edition refuses at runtime (reports."executive-briefing" = "off"), so it failed
        // in the community lane while compiling perfectly. Warn() is now internal so it can be
        // reused there rather than duplicated.

        [Theory]
        [InlineData(0, null)]
        [InlineData(1, "1 check could not be fully assessed — it is excluded from the pass rate above.")]
        [InlineData(3, "3 checks could not be fully assessed — they are excluded from the pass rate above.")]
        public void UnassessedNote_IsSilentWhenCleanAndExplicitWhenNot(int warnCount, string? expected)
        {
            // Copy is load-bearing here: "could not be fully assessed", never "warnings". A client
            // reading "3 warnings" hears three mild findings — the exact misreading the state exists
            // to prevent. Zero returns null so a clean run carries no noise.
            var rows = Enumerable.Range(0, warnCount)
                .Select(i => new FindingRow { State = FindingState.Warn, Name = "w" + i })
                .Concat(new[] { new FindingRow { State = FindingState.Pass, Name = "p" } })
                .ToList();

            Assert.Equal(expected, AssessmentPdf.UnassessedNote(rows));
        }

        // Pdf_WarnRendersAsAValidDocument_AndIsOrderedAboveThePasses moved 2026-08-04 to
        // Gated/WarnFindingsPdfRenderTests.cs. It is the only test here that binds a
        // community-gated symbol (AssessmentPdf.BuildFindingsReport, behind
        // !SQLT_NO_REPORT_FINDINGS_PDF), and it was breaking the community test build. The
        // other 28 tests in this fixture are profile-independent and stay.

        [Fact]
        public void Pdf_WarnIsNotAnOpenFinding_ByTheEnumContract()
        {
            // AssessmentPdf.IsOpen/IsRated are private; what is publicly checkable is that Warn is
            // a member distinct from every state that already existed, so no pre-existing "is this
            // an open finding" comparison can accidentally match it.
            var all = System.Enum.GetValues<FindingState>();
            Assert.Contains(FindingState.Warn, all);
            Assert.Equal(7, all.Length);
            foreach (var s in all.Where(s => s != FindingState.Warn))
                Assert.NotEqual(s, FindingState.Warn);
        }

        // ── (c) scoring, surface by surface ───────────────────────────────────────────────────

        private static int PassRate(params CheckResult[] results) =>
            (int)typeof(CheckExecutionService)
                .GetMethod("PassRateScore", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { (IReadOnlyList<CheckResult>)results.ToList() })!;

        private static CheckResult P(string id = "P1") =>
            new() { CheckId = id, CheckName = "p-" + id, Category = "Security", Severity = "High", Passed = true, Verdict = "PASS" };

        private static CheckResult F(string id = "F1") =>
            new() { CheckId = id, CheckName = "f-" + id, Category = "Security", Severity = "High", Passed = false, Verdict = "FAIL" };

        [Fact]
        public void PassRateScore_WarnMovesTheScoreNowhere()
        {
            // THE defect this round exists to kill. PassRateScore counted raw r.Passed and never
            // consulted IsScorable, so WARN rode the numerator: measured 50 for {PASS, FAIL} and
            // 67 once a WARN joined them — a run that could see LESS of the server scored BETTER.
            // Adding an unassessable check must move the rate by exactly zero.
            Assert.Equal(50, PassRate(P(), F()));
            Assert.Equal(50, PassRate(P(), F(), Warn()));
            Assert.Equal(50, PassRate(P(), F(), Warn("W2"), Warn("W3")));
        }

        [Fact]
        public void PassRateScore_NothingAssessable_ClaimsNoScore()
        {
            // On the raw basis an all-WARN run scored 100 — a totally blind run reported as
            // flawless. Out of the denominator too, so there is no rate to state: 0, matching the
            // empty-results guard. No assessment made, no score claimed.
            Assert.Equal(0, PassRate(Warn()));
            Assert.Equal(0, PassRate(Warn("W1"), Warn("W2")));
        }

        [Fact]
        public void ExecutiveNarrative_DoesNotTellTheClientAWarnCheckPassed()
        {
            // The gate's reproduction, verbatim: "The chk-w1 check on PROD-SQL-01 passed. No action
            // required." — a partially-blind check reported as verified-clean in the single most
            // quotable sentence in the deliverable. FindingTranslator keyed on raw r.Passed and had
            // no Warn arm at all.
            var translator = NewTranslator();
            var w = Warn(); w.InstanceName = "PROD-SQL-01"; w.CheckName = "chk-w1";

            var t = translator.TranslateAsync(w).GetAwaiter().GetResult();

            Assert.DoesNotContain("passed", t.Executive.PlainLanguageSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("could not be fully assessed", t.Executive.PlainLanguageSummary);
            Assert.NotEqual("No action required.", t.Executive.RecommendedAction);
            // The check's own reason is carried through rather than invented.
            Assert.Contains("Could not read 3 of 14 databases", t.Executive.PlainLanguageSummary);
        }

        [Theory]
        [InlineData("VA-001")]        // RenderBackupValidation
        [InlineData("TRIAGE_002")]    // RenderLogSpace
        public void ExecutiveNarrative_SpecificRenderersDoNotReinstateThePassText(string checkId)
        {
            // The three specific renderers overwrite Executive.PlainLanguageSummary AFTER
            // RenderGeneric, each with its own r.Passed ternary. Fixing only the generic path would
            // leave the false-clean live for exactly the checks important enough to have been given
            // a bespoke renderer.
            var w = Warn(); w.CheckId = checkId; w.InstanceName = "PROD-SQL-01";

            var t = NewTranslator().TranslateAsync(w).GetAwaiter().GetResult();

            Assert.Contains("could not be fully assessed", t.Executive.PlainLanguageSummary);
            Assert.DoesNotContain("validation passed", t.Executive.PlainLanguageSummary);
            Assert.DoesNotContain("within normal parameters", t.Executive.PlainLanguageSummary);
        }

        [Fact]
        public void ComplianceScorecard_WarnIsOutOfNumeratorAndDenominator()
        {
            // Measured before the fix: a two-check control scored 50.00%, and the SAME control with
            // one WARN added scored 66.67% — less visibility, better compliance score, in the
            // document a client hands to an auditor.
            var bundle = new SQLTriage.Tests.Licensing.FakeBundleAccessor()
                .PutFile("Config/control_mappings.json", ScorecardFwJson);
            var mapping = new ComplianceMappingService(NullLogger<ComplianceMappingService>.Instance, bundle);
            var svc = new ComplianceScoreService(NullLogger<ComplianceScoreService>.Instance, mapping);

            // 2026-07-20 sweep: calls the SHIPPED mapping. This local helper used to carry its own
            // copy of the ternary, which meant the test could stay green while the three real
            // adapters inflated — a test asserting a property of code it does not run.
            AssessmentResult Map(CheckResult c) => new()
            {
                CheckId = c.CheckId, DisplayName = c.CheckName ?? "", Category = c.Category ?? "",
                Severity = c.Severity ?? "",
                Status = ComplianceScoreService.StatusFor(c),
            };

            var before = svc.ComputeScorecard(new[] { P(), F() }.Select(Map).ToList(), "TESTFW");
            var after  = svc.ComputeScorecard(new[] { P(), F(), Warn() }.Select(Map).ToList(), "TESTFW");

            Assert.Equal(before.OverallPercent, after.OverallPercent);
            // Out of the denominator, and counted where a consumer can say so out loud.
            Assert.Equal(2, after.FamilyScores.Sum(f => f.TotalChecks));
            Assert.Equal(1, after.FamilyScores.Sum(f => f.UnassessedChecks));
            // And never presented as audit evidence of a control breach.
            Assert.DoesNotContain(after.FamilyScores.SelectMany(f => f.FailingResults),
                r => string.Equals(r.CheckId, "W1", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A verdict-contract SKIP: not applicable to this server. Passed=true.</summary>
        private static CheckResult Skip(string id = "S1") =>
            new() { CheckId = id, CheckName = "s-" + id, Category = "Security", Severity = "High", Passed = true, Verdict = "SKIP" };

        /// <summary>An INFO result: informational, asserts nothing. Passed=true.</summary>
        private static CheckResult Inf(string id = "I1") =>
            new() { CheckId = id, CheckName = "i-" + id, Category = "Security", Severity = "INFO", Passed = true };

        [Fact]
        public void ComplianceScorecard_SkipAndInfoAreAlsoOutOfTheNumeratorAndDenominator()
        {
            // COMPLIANCE INFLATION (2026-07-20 sweep) — PREDATES the WARN slice. The adapters'
            // ternary tail was `r.Passed ? "Passed" : "Failed"`, and SKIP and INFO both ride
            // Passed=true, so a check that did not APPLY to this server and a check that returned
            // an informational NOTE were both tagged Passed in the compliance percentage a client
            // hands to an auditor. Ruling #4 fixed the WARN arm only; these two rode on.
            //
            // The worked example below is the number, measured on this fixture:
            //   {PASS, FAIL}                    → 50.00%   (the truth: 1 of 2 controls met)
            //   {PASS, FAIL, SKIP, INFO} before → 75.00%   (3 of 4 "passed" — inflated by 25 points)
            //   {PASS, FAIL, SKIP, INFO} after  → 50.00%   (unchanged; both dropped from both sides)
            var bundle = new SQLTriage.Tests.Licensing.FakeBundleAccessor()
                .PutFile("Config/control_mappings.json", ScorecardFwJson);
            var mapping = new ComplianceMappingService(NullLogger<ComplianceMappingService>.Instance, bundle);
            var svc = new ComplianceScoreService(NullLogger<ComplianceScoreService>.Instance, mapping);

            AssessmentResult Map(CheckResult c) => new()
            {
                CheckId = c.CheckId, DisplayName = c.CheckName ?? "", Category = c.Category ?? "",
                Severity = c.Severity ?? "", Status = ComplianceScoreService.StatusFor(c),
            };

            // The old, inflating mapping — kept explicit so the delta is measured, not asserted.
            AssessmentResult MapOld(CheckResult c) => new()
            {
                CheckId = c.CheckId, DisplayName = c.CheckName ?? "", Category = c.Category ?? "",
                Severity = c.Severity ?? "", Status = c.Passed ? "Passed" : "Failed",
            };

            var truth    = svc.ComputeScorecard(new[] { P(), F() }.Select(Map).ToList(), "TESTFW");
            var inflated = svc.ComputeScorecard(new[] { P(), F(), Skip(), Inf() }.Select(MapOld).ToList(), "TESTFW");
            var fixedNow = svc.ComputeScorecard(new[] { P(), F(), Skip(), Inf() }.Select(Map).ToList(), "TESTFW");

            Assert.Equal(50.00, Math.Round(truth.OverallPercent, 2));
            Assert.Equal(75.00, Math.Round(inflated.OverallPercent, 2));   // the defect, measured
            Assert.Equal(50.00, Math.Round(fixedNow.OverallPercent, 2));   // adding blind spots moves it nowhere

            // Out of the denominator, and counted where a consumer can say so out loud.
            Assert.Equal(2, fixedNow.FamilyScores.Sum(f => f.TotalChecks));
            Assert.Equal(2, fixedNow.FamilyScores.Sum(f => f.UnassessedChecks));
            // And neither is presented as audit evidence of a control breach.
            Assert.DoesNotContain(fixedNow.FamilyScores.SelectMany(f => f.FailingResults),
                r => r.CheckId is "S1" or "I1");
        }

        [Theory]
        [InlineData("SQLTriage.Pages.ComplianceMap")]
        [InlineData("SQLTriage.Pages.ComplianceBoard")]
        public void ComplianceAdapters_MapSkipAndInfoToUnassessed_NotPassed(string componentType)
        {
            // Same three adapters, the two tiers ruling #4 did not reach.
            var t = typeof(CheckExecutionService).Assembly.GetType(componentType)!;
            var mi = t.GetMethod("ToAssessmentResult", BindingFlags.NonPublic | BindingFlags.Static)!;

            Assert.Equal(ComplianceScoreService.UnassessedStatus, ((AssessmentResult)mi.Invoke(null, new object[] { Skip() })!).Status);
            Assert.Equal(ComplianceScoreService.UnassessedStatus, ((AssessmentResult)mi.Invoke(null, new object[] { Inf() })!).Status);
        }

        [Fact]
        public void ReportBundle_MapsSkipAndInfoToUnassessed_NotPassed()
        {
            var mi = typeof(ReportBundleService).GetMethod("CorpusToAssessment", BindingFlags.NonPublic | BindingFlags.Static)!;

            Assert.Equal(ComplianceScoreService.UnassessedStatus, ((AssessmentResult)mi.Invoke(null, new object[] { Skip() })!).Status);
            Assert.Equal(ComplianceScoreService.UnassessedStatus, ((AssessmentResult)mi.Invoke(null, new object[] { Inf() })!).Status);
        }

        [Theory]
        [InlineData("SQLTriage.Pages.ComplianceMap")]
        [InlineData("SQLTriage.Pages.ComplianceBoard")]
        public void ComplianceAdapters_MapWarnToUnassessed_NotPassed(string componentType)
        {
            // Both razor adapters carried `Status = c.Passed ? "Passed" : "Failed"`, and their own
            // comments claim they are one conversion with no forked mapping — so both are pinned,
            // or the fork comes back the next time only one is touched.
            var t = typeof(CheckExecutionService).Assembly.GetType(componentType)!;
            var mi = t.GetMethod("ToAssessmentResult", BindingFlags.NonPublic | BindingFlags.Static)!;

            Assert.Equal(ComplianceScoreService.UnassessedStatus, ((AssessmentResult)mi.Invoke(null, new object[] { Warn() })!).Status);
            Assert.Equal("Passed", ((AssessmentResult)mi.Invoke(null, new object[] { P() })!).Status);
            Assert.Equal("Failed", ((AssessmentResult)mi.Invoke(null, new object[] { F() })!).Status);
        }

        [Fact]
        public void ReportBundle_MapsWarnToUnassessed_NotPassed()
        {
            var mi = typeof(ReportBundleService).GetMethod("CorpusToAssessment", BindingFlags.NonPublic | BindingFlags.Static)!;

            Assert.Equal(ComplianceScoreService.UnassessedStatus, ((AssessmentResult)mi.Invoke(null, new object[] { Warn() })!).Status);
            Assert.Equal("Passed", ((AssessmentResult)mi.Invoke(null, new object[] { P() })!).Status);
        }

        // ── (d) the distinction has to survive a restart ──────────────────────────────────────

        [Fact]
        public void Persistence_WarnSurvivesTheRoundTrip_AndDoesNotRehydrateAsAPass()
        {
            // check_results.passed cannot represent a tier: PASS/SKIP/INFO/WARN all write 1. Before
            // the verdict column, a WARN came back Passed=true/Verdict=null — IsWarn FALSE and
            // IsScorable TRUE — so the audit grid painted it green the morning after a restart and
            // the per-check trend counted it as a clean pass. Measured on both read paths.
            var dir = Path.Combine(Path.GetTempPath(), "warn-persist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using var svc = new GovernanceHistoryService(NullLogger<GovernanceHistoryService>.Instance, 365, dir);
                var w = Warn(); w.InstanceName = "SRV1";
                svc.RecordCheckResult("SRV1", w);
                svc.RecordCheckResult("SRV1", F());

                var rehydrated = svc.LoadLastKnownResults("SRV1", out _);
                var backWarn = Assert.Single(rehydrated, r => r.CheckId == "W1");
                Assert.Equal("WARN", backWarn.Verdict);
                Assert.True(CheckClassification.IsWarn(backWarn));
                Assert.False(CheckClassification.IsScorable(backWarn));

                // The FAIL beside it must be unaffected — this is a new column, not a reclassification.
                var backFail = Assert.Single(rehydrated, r => r.CheckId == "F1");
                Assert.False(CheckClassification.IsWarn(backFail));
                Assert.True(CheckClassification.IsScorable(backFail));

                // Second read path: the per-check trend's own query.
                var point = Assert.Single(svc.GetCheckHistory("W1"));
                Assert.True(CheckClassification.IsWarnVerdict(point.Verdict));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void Persistence_VerdictColumnMigrationIsIdempotent()
        {
            // CREATE TABLE IF NOT EXISTS is a no-op on an existing database, so upgraded installs
            // depend entirely on the ALTER. Opening the same directory twice must not throw on the
            // duplicate-column error, or the second launch loses its history service.
            var dir = Path.Combine(Path.GetTempPath(), "warn-migrate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using (var first = new GovernanceHistoryService(NullLogger<GovernanceHistoryService>.Instance, 365, dir))
                    first.RecordCheckResult("SRV1", Warn());

                using var second = new GovernanceHistoryService(NullLogger<GovernanceHistoryService>.Instance, 365, dir);
                second.RecordCheckResult("SRV1", Warn("W2"));

                Assert.Equal(2, second.LoadLastKnownResults("SRV1", out _).Count(CheckClassification.IsWarn));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ── Adrian's ruling: an acceptance outlives a loss of visibility ──────────────────────

        [Fact]
        public void Acceptance_SurvivesAFindingDegradingToWarn()
        {
            // AnnotateAcceptances cleared acceptance metadata for any Passed=true, which now
            // includes WARN — so a finding the client had signed off lost its "Accepted" badge the
            // moment their audit account lost visibility, and got it back when it returned. An
            // acceptance is a statement about the FINDING, not about this run's visibility.
            //
            // Exercised against the REAL AcceptedFindingsService on a temp DB (the brief named an
            // IAcceptedFindings interface; no such interface exists — the service is a sealed class
            // with a dbPath test seam, which is what every other test in this repo uses).
            var dir = Path.Combine(Path.GetTempPath(), "warn-accept-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using var accepted = new AcceptedFindingsService(
                    NullLogger<AcceptedFindingsService>.Instance, audit: null,
                    dbPath: Path.Combine(dir, "check-baselines.db"));
                accepted.Accept("SRV1", "W1", "known permissions gap on the reporting replica", "adrian")
                        .GetAwaiter().GetResult();

                var svc = (CheckExecutionService)System.Runtime.CompilerServices
                    .RuntimeHelpers.GetUninitializedObject(typeof(CheckExecutionService));
                typeof(CheckExecutionService)
                    .GetField("_acceptedFindings", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(svc, accepted);

                var warn = Warn(); warn.InstanceName = "SRV1";
                var rows = new List<CheckResult> { warn };
                // Called directly: AnnotateAcceptances went public on 2026-08-05 so the audit page's
                // accept/revoke refresh can re-apply THIS rule to the rows already on the grid,
                // instead of re-importing them through GetResults (which unions the persisted
                // previous run in — the D1 defect). One rule, and this test now exercises the same
                // entry point the page does.
                svc.AnnotateAcceptances(rows);

                Assert.True(rows[0].IsAccepted);
                Assert.Equal("known permissions gap on the reporting replica", rows[0].AcceptanceReason);
                Assert.Equal("adrian", rows[0].AcceptedBy);

                // A genuine PASS still clears — the exemption is for WARN alone, not for the tier.
                var pass = P(); pass.InstanceName = "SRV1"; pass.CheckId = "W1"; pass.IsAccepted = true;
                var passRows = new List<CheckResult> { pass };
                svc.AnnotateAcceptances(passRows);
                Assert.False(passRows[0].IsAccepted);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void CheckTrend_PassRateExcludesUnassessedRuns_AndCountsThemSeparately()
        {
            // The per-check trend computes its pass-rate from rehydrated history points, where a
            // WARN carries Passed=true. Counting it as a pass meant a check that lost visibility
            // across the estate TRENDED UP. Out of numerator and denominator, same as every other
            // rate; the count it was excluded from is surfaced beside it rather than dropped.
            var component = System.Activator.CreateInstance(
                typeof(CheckExecutionService).Assembly.GetType("SQLTriage.Pages.CheckTrend")!)!;
            var type = component.GetType();

            static CheckHistoryPoint Pt(bool passed, string? verdict) => new()
            { Server = "SRV1", RecordedAt = "2026-07-20 10:00:00", Passed = passed, Verdict = verdict };

            void SetPoints(params CheckHistoryPoint[] pts) =>
                type.GetField("_points", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(component, pts.ToList());

            // A1b (2026-07-20): OverallPassRate is now double? — see the last block of this test.
            double? Rate() => (double?)type.GetProperty("OverallPassRate", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(component);
            int Unassessed() => (int)type.GetProperty("UnassessedCount", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(component)!;

            SetPoints(Pt(true, "PASS"), Pt(false, "FAIL"));
            Assert.Equal(50.0, Rate());
            Assert.Equal(0, Unassessed());

            // Adding an unassessable run moves the rate by zero and is counted where it went.
            SetPoints(Pt(true, "PASS"), Pt(false, "FAIL"), Pt(true, "WARN"));
            Assert.Equal(50.0, Rate());
            Assert.Equal(1, Unassessed());

            // A history row predating the verdict column carries null — "unknown tier", which stays
            // in the pre-existing Passed-based bucket rather than being retro-labelled either way.
            SetPoints(Pt(true, null), Pt(false, null));
            Assert.Equal(50.0, Rate());
            Assert.Equal(0, Unassessed());

            // Nothing assessable: no rate to state.
            //
            // CORRECTED 2026-07-20 (A1b). This block asserted 0.0 while its own comment said "no
            // rate to state" — the comment was right and the assertion pinned the defect. 0.0 is a
            // rate, and the page rendered it as a RED 0% on its largest tile, so a check nobody
            // could assess was displayed as a check that failed every run. The property is now
            // double? and this is null.
            SetPoints(Pt(true, "WARN"), Pt(true, "WARN"));
            Assert.Null(Rate());
            Assert.Equal(2, Unassessed());
        }

        private const string ScorecardFwJson = @"{
  ""regions"": { ""Test"": { ""frameworks"": [ { ""name"": ""Test Framework"", ""acronym"": ""TESTFW"",
    ""categories"": [ { ""id"": ""T-SCORED"", ""name"": ""Scored Control"", ""sqlCheckHints"": [""access_control""] } ] } ] } } }";

        private static FindingTranslator NewTranslator() =>
            new(new Microsoft.Extensions.Caching.Memory.MemoryCache(
                    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                new WarnStubQueryRepository(),
                new WarnFixedWeightsProvider(new GovernanceWeights()),
                NullLogger<FindingTranslator>.Instance);

        private sealed class WarnStubQueryRepository : ISqlQueryRepository
        {
            private readonly Dictionary<string, SqlQueryDefinition> _d = new(System.StringComparer.OrdinalIgnoreCase);
            public SqlQueryDefinition? Get(string id) => _d.TryGetValue(id, out var v) ? v : null;
            public IReadOnlyDictionary<string, SqlQueryDefinition> GetAll() => _d;
            public IReadOnlyList<SqlQueryDefinition> GetByTag(string tag) => new List<SqlQueryDefinition>();
            public IReadOnlyList<SqlQueryDefinition> GetQuickChecks() => new List<SqlQueryDefinition>();
            public Task ReloadAsync() => Task.CompletedTask;

            // Fixed in-memory stub: nothing is loaded, so there is no load to wait for.
            public Task InitializationComplete => Task.CompletedTask;
        }

        /// <summary>Test-only fixed weights provider (mirrors GovernanceServiceTests).</summary>
        private sealed class WarnFixedWeightsProvider : IGovernanceWeightsProvider
        {
            private readonly GovernanceWeights _weights;
            public WarnFixedWeightsProvider(GovernanceWeights weights) => _weights = weights;
            public GovernanceWeights Current => _weights;
            public event System.EventHandler? WeightsChanged;
        }
    }
}
