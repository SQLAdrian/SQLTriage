/* In the name of God, the Merciful, the Compassionate */

// Honesty pins for the attestation and coverage surfaces (lane reports, 2026-08-27):
// reports-r2-06 (an empty state naming a scan that cannot fill it), reports-r2-07 (an attestation
// promising an integrity file that is written best-effort) and reports-r1-07 (partial-coverage
// disclosure dropped between the web page and the client PDF).
//
// This file lives in Gated/ because it binds three category-2 and category-1 symbols:
// AssessmentPdf.BuildExecutiveSummaryBundle and .BuildDbaHandoffBundle sit behind
// #if !SQLT_NO_REPORT_EXEC_SUMMARY / _DBA_HANDOFF, AssessmentPdf.BuildComplianceReport behind
// #if !SQLT_NO_REPORT_COMPLIANCE_REPORT, and RoadmapPdfBuilder's whole file is Compile-Removed when
// reports."diagnostics-roadmap" is off — which buildprofile.json says it is. See Gated/README.md.

using System.Collections.Generic;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests.Gated
{
    public class ReportAttestationHonestyTests
    {
        static ReportAttestationHonestyTests()
        {
            // App.xaml.cs sets this at startup; tests bypass startup, so set it here.
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }

        private static AssessmentMeta Meta(string title) =>
            new() { Title = title, GeneratedUtc = "2026-08-27T00:00Z", RunId = "att00001" };

        // ── reports-r2-06: the empty state named a scan that cannot fill the table ────────────
        //
        // The 2026-07-16 honesty ruling made the Executive Summary, DBA Handoff and Risk Register
        // corpus-only (ReportBundleService.GetMergedFindings). All three PDFs kept telling the
        // reader to "Run a Vulnerability Assessment first" — a scan that leaves every one of them
        // exactly as empty. Their HTML twins, built by the same service in the same run, already
        // said "Run the check suite (Audit Assessment) first". Two deliverables from one dataset
        // giving a client contradictory instructions.

        [Fact]
        public void ExecutiveSummaryPdf_SendsAnEmptyReaderToTheCheckSuite_NotToAVulnerabilityAssessment()
        {
            var text = ReportBundleServiceTests.PdfText(AssessmentPdf.BuildExecutiveSummaryBundle(
                new ExecutiveSummaryBundle
                {
                    Meta = Meta("Executive Summary"),
                    ScoreAssessed = true,
                    Score = 80,
                    TopRisks = new List<BundleFinding>(),
                }));

            Assert.Contains("Top5Risks", text);                       // the extractor is reading it
            Assert.Contains("Runthechecksuite(AuditAssessment)first", text);
            Assert.DoesNotContain("VulnerabilityAssessmentfirst", text);
        }

        [Fact]
        public void DbaHandoffPdf_SendsAnEmptyReaderToTheCheckSuite_NotToAVulnerabilityAssessment()
        {
            var text = ReportBundleServiceTests.PdfText(AssessmentPdf.BuildDbaHandoffBundle(
                new DbaHandoffBundle
                {
                    Meta = Meta("DBA Handoff"),
                    AllFindings = new List<BundleFinding>(),
                }));

            Assert.Contains("AllDiagnosticFindings", text);
            Assert.Contains("Runthechecksuite(AuditAssessment)first", text);
            Assert.DoesNotContain("VulnerabilityAssessmentfirst", text);
        }

        // ── reports-r1-07: the partial-coverage count never reached the client PDF ────────────
        //
        // ComplianceScoreService carries UnassessedChecks precisely so a consumer can say the rate
        // was computed over less than the whole control. Pages/ComplianceMap.razor renders it as a
        // "Not assessed" column. ComplianceFamilyRow had no field for it, so the exported PDF — the
        // copy that leaves the building as audit evidence — presented every partial rate as a full
        // one with nothing in the bytes saying otherwise. Measured before the fix, on a
        // PASS+FAIL+WARN fixture: "scorecard gives TotalChecks=2, Percent=50, UnassessedChecks=1 …
        // space-stripped scan of the whole PDF for 'unassessed'/'notassessed'/'couldnot': FALSE".

        [Fact]
        public void ComplianceReportPdf_StatesThePartialCoverage_InsteadOfPresentingAPartialRateAsAFullOne()
        {
            var text = ReportBundleServiceTests.PdfText(AssessmentPdf.BuildComplianceReport(
                new ComplianceReport
                {
                    Meta = Meta("ISO 27001"),
                    OverallPercent = 50,
                    Compliant = 0, Partial = 1, NonCompliant = 0, OutsideScope = 0, NotTested = 0,
                    Families = new List<ComplianceFamilyRow>
                    {
                        new() { Id = "A.8", Name = "Asset management", Percent = 50,
                                Status = "PartiallyCompliant", UnassessedChecks = 1 },
                        new() { Id = "A.9", Name = "Access control", Percent = 100,
                                Status = "Compliant", UnassessedChecks = 0 },
                    },
                }));

            Assert.Contains("Assetmanagement", text);            // the extractor is reading it
            Assert.Contains("Notassessed", text);                // the column exists
            Assert.Contains("Partialcoverage:1check", text);     // and the headline carries the fact
            Assert.Contains("computedoverlessthanthewholecontrol", text);
        }

        [Fact]
        public void TheComplianceCoverageAssertionDiscriminates_ProvedByRenderingAFullyAssessedScorecard()
        {
            // Mutation control. The assertions above are only worth something if the SAME builder
            // stays quiet when nothing is unassessed — otherwise a builder that printed the
            // partial-coverage sentence unconditionally would pass them, and would be a new
            // over-claim in place of the old omission.
            var text = ReportBundleServiceTests.PdfText(AssessmentPdf.BuildComplianceReport(
                new ComplianceReport
                {
                    Meta = Meta("ISO 27001"),
                    OverallPercent = 100,
                    Compliant = 1,
                    Families = new List<ComplianceFamilyRow>
                    {
                        new() { Id = "A.9", Name = "Access control", Percent = 100,
                                Status = "Compliant", UnassessedChecks = 0 },
                    },
                }));

            Assert.Contains("Accesscontrol", text);
            Assert.DoesNotContain("Partialcoverage:", text);
        }

        // ── reports-r1-07 at the call site: the projection that carries the count ─────────────────
        //
        // The two tests above hand BuildComplianceReport a ComplianceFamilyRow this file filled in
        // itself, so they prove the RENDERER. They do not prove that anything ever sets
        // UnassessedChecks on the way to it. The fix that does was one line inside
        // Pages/ComplianceMap.razor's export handler, and nothing in this project can construct a
        // razor component: the gate deleted that line, rebuilt, ran the whole Debug suite and got
        // 5466 passed / 0 failed — byte-identical to the unmutated run — while the client PDF
        // silently lost its "Not assessed" column and its partial-coverage sentence.
        //
        // The projection now lives in ComplianceScoreService.ToExportModel, which the page calls and
        // this test drives. Real scoring service, real scorecard, real projection, real PDF bytes:
        // the count is read off the delivered artefact. Delete the copy and this goes red.
        [Fact]
        public void ComplianceExport_CarriesThePartialCoverageCount_FromTheRealScorecardToThePdfBytes()
        {
            var svc = ComplianceScoreServiceTests.ScoreWithJson(ComplianceScoreServiceTests.TestFwJson);

            // One control, three results: one PASS, one FAIL, one that asserted nothing. The
            // scorecard scores 1 of 2 and records the third as unassessed — a rate computed over
            // less than the whole control, which is the fact the exported PDF used to drop.
            var scorecard = svc.ComputeScorecard(new List<AssessmentResult>
            {
                ComplianceScoreServiceTests.MakeResult("Security", "Passed"),
                ComplianceScoreServiceTests.MakeResult("Security", "Failed"),
                ComplianceScoreServiceTests.MakeResult("Security", ComplianceScoreService.UnassessedStatus),
            }, "TESTFW");

            var scored = scorecard.FamilyScores.Single(f => f.FamilyId == "T-SCORED");
            Assert.Equal(2, scored.TotalChecks);        // the fixture really produced a partial control
            Assert.Equal(1, scored.UnassessedChecks);

            var report = ComplianceScoreService.ToExportModel(scorecard, Meta("Test Framework"));
            Assert.Equal(1, report.Families.Single(f => f.Id == "T-SCORED").UnassessedChecks);

            var text = ReportBundleServiceTests.PdfText(AssessmentPdf.BuildComplianceReport(report));

            Assert.Contains("ScoredControl", text);              // the extractor is reading it
            Assert.Contains("Notassessed", text);                // the column reached the PDF…
            Assert.Contains("Partialcoverage:1check", text);     // …and so did the headline fact
            Assert.Contains("computedoverlessthanthewholecontrol", text);
        }

        // ── reports-r2-07: the attestation asserted an integrity file written best-effort ─────
        //
        // The Operator Attestation told the recipient a sibling .manifest.json carries the PDF's
        // SHA-256 "so a recipient can verify the PDF is byte-for-byte identical". The hash is taken
        // FROM the PDF, so the PDF is on disk before the manifest is attempted; the whole step sits
        // in one try/catch that only LogWarnings, and the export toast said "PDF exported" either
        // way. A recipient holding a manifest-less PDF was reading a promise about a file nobody
        // wrote. The claim is now conditional on what the recipient can actually see.

        [Fact]
        public void RoadmapAttestation_MakesTheHashClaimConditionalOnTheManifestBeingThere()
        {
            var text = ReportBundleServiceTests.PdfText(
                RoadmapPdfBuilder.Build(RoadmapPdfBuilderTests.Report(55)));

            Assert.Contains("OperatorAttestation", text);        // the extractor is reading it
            Assert.Contains("SQLTriageattemptstowriteasibling", text);
            Assert.Contains("Ifitisnotthere,themanifestwritefailed", text);
            Assert.Contains("nosuchverificationispossible", text);
        }

        // ── reports-r1-08: closed by the audit lane, and this is the render that says so ──────
        //
        // The lane brief closes r1-08 as already-fixed on main, and marks the closure "believe only
        // — nobody in this pass rendered the PDF to confirm the disclaimer text". This is that
        // render. The attestation used to print "Compliance frameworks: ISO 27001:2022 · SOC 2 ·
        // NIST CSF 2.0" from a one-line return, inside a section headed Operator Attestation, where
        // it reads as an attested compliance claim. It is not one, and the row now says so.

        [Fact]
        public void RoadmapAttestation_LabelsTheControlMappingsAsAToolProperty_NotAsAConformanceClaim()
        {
            var text = ReportBundleServiceTests.PdfText(
                RoadmapPdfBuilder.Build(RoadmapPdfBuilderTests.Report(55)));

            Assert.Contains("Controlmappings", text);
            Assert.Contains("Itisapropertyofthetool", text);
            Assert.Contains("Itisnotanassessmentagainstthoseframeworks", text);
            Assert.Contains("nofindinginthisreportisevidenceofconformancewithone", text);
            Assert.DoesNotContain("Complianceframeworks", text);
        }
    }
}
