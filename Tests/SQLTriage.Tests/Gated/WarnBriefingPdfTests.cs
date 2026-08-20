/* In the name of God, the Merciful, the Compassionate */

// Extracted from WarnVerdictTests.cs on 2026-08-04. Same runtime-refusal gate as
// Gated/ScheduledReportRenderTests.cs: this test renders the Executive Briefing PDF, and
// ScheduledReportService.Render throws NotSupportedException in an edition where
// reports."executive-briefing" is off (the shipped community state). It compiled fine in the
// community build and failed at run time — one of the seven failures the community suite produced
// the first time CI ever got past BUILD to run it.
//
// The other 27 tests in WarnVerdictTests are profile-independent and stay there; Warn() is now
// internal so this file reuses the same fixture rather than copying it. See Gated/README.md.

using System;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests.Gated
{
    public class WarnBriefingPdfTests
    {
        [Fact]
        public void ScheduledReport_ExportedBriefingPdf_TellsTheReaderTheRunWasNotFullyAssessed()
        {
            // THE test the old name promised. Renders the real client-facing artifact and reads the
            // visible text back out of the PDF (PdfTextExtractor walks the ToUnicode CMaps — Skia
            // writes glyph ids, so a raw byte scan for the words silently finds nothing).
            //
            // Reproduced before the fix: three checks ran, the document said "2 checks evaluated",
            // and neither the warn check's id nor any "could not" wording appeared anywhere. WARN is
            // correctly out of every rate (IsRated) and out of every open-finding list (IsOpen) —
            // and those individually-correct exclusions summed to a document that read exactly like
            // a clean two-check run. Adrian ruled: surface it.
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

            var pass = new CheckResult { CheckId = "P1", CheckName = "p-one", Category = "Security", Severity = "High", Passed = true,  Verdict = "PASS", InstanceName = "PROD-SQL-01" };
            var fail = new CheckResult { CheckId = "F1", CheckName = "f-one", Category = "Security", Severity = "High", Passed = false, Verdict = "FAIL", InstanceName = "PROD-SQL-01" };
            var warn = WarnVerdictTests.Warn(); warn.InstanceName = "PROD-SQL-01";

            var briefing = ScheduledReportService.BuildBriefing(
                new AssessmentMeta { Title = "Audit", GeneratedUtc = "2026-07-20T00:00Z", RunId = "warn0001" },
                new[] { pass, fail, warn });

            var text = PdfTextExtractor.Extract(ScheduledReportService.Render(ReportKind.ExecutiveBriefing, briefing));

            // The extractor must actually be reading the document — otherwise every Contains below
            // passes vacuously on an empty string, which is the failure mode that let the original
            // defect through. Pin something only a rendered briefing contains.
            Assert.Contains("Executive Briefing", text);

            // The ruling, verbatim in the artifact: a degraded run says so.
            Assert.Contains("could not be fully assessed", text);
            Assert.Contains("1 check could not be fully assessed", text);

            // And it is not smuggled in as a finding or a pass.
            Assert.DoesNotContain("3 checks evaluated", text);   // the WARN is out of the rate...
            Assert.Contains("2 checks evaluated", text);         // ...so the rated denominator is 2
        }
    }
}
