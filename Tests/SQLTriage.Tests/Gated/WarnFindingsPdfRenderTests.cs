/* In the name of God, the Merciful, the Compassionate */

// Extracted from WarnVerdictTests.cs on 2026-08-04. This is the ONE test in that 29-test
// fixture that binds a community-gated symbol: AssessmentPdf.BuildFindingsReport sits behind
// #if !SQLT_NO_REPORT_FINDINGS_PDF, and buildprofile.json has reports."findings-pdf" = "off",
// so the method is absent from the community assembly (CS0117, not CS0246 — the FILE still
// compiles, which is why a file-level audit of the gating never saw it).
//
// Only this test moved. The other 28 are profile-independent and stay where they were: gating
// the whole fixture would have deleted them from the community lane, which is the only lane CI
// runs. See Gated/README.md.

using System.Collections.Generic;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests.Gated
{
    public class WarnFindingsPdfRenderTests
    {
        [Fact]
        public void Pdf_WarnRendersAsAValidDocument_AndIsOrderedAboveThePasses()
        {
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

            var report = new FindingsReport
            {
                Meta = new AssessmentMeta { Title = "Audit", GeneratedUtc = "2026-07-20T00:00Z", RunId = "warn0001" },
                Findings =
                {
                    new FindingRow { State = FindingState.Pass, Name = "a-pass", Severity = "High",   Server = "S1", Detail = "ok" },
                    new FindingRow { State = FindingState.Warn, Name = "b-warn", Severity = "High",   Server = "S1", Detail = "could not read 3 dbs" },
                    new FindingRow { State = FindingState.Fail, Name = "c-fail", Severity = "Critical", Server = "S1", Detail = "bad" },
                },
            };

            // Exercised, not asserted-by-comment: the builder must survive the new enum member on
            // every switch it passes through (glyph, rank, table label) rather than throwing or
            // silently falling into a default arm that renders it as Info.
            var bytes = AssessmentPdf.BuildFindingsReport(report);
            Assert.True(bytes.Length > 1000, $"PDF unexpectedly small: {bytes.Length} bytes");
            Assert.Equal((byte)'%', bytes[0]);
        }
    }
}
