/* In the name of God, the Merciful, the Compassionate */

// Extracted from ReportBodyMarkdownAndLayoutTests.cs on 2026-08-04. These are the two tests in
// that fixture that bind a community-gated symbol: AssessmentPdf.BuildDbaHandoffBundle sits
// behind #if !SQLT_NO_REPORT_DBA_HANDOFF, and buildprofile.json has reports."dba-handoff" = "off",
// so the method is absent from the community assembly (CS0117 — the FILE still compiles).
//
// The four MarkdownToPlain tests are profile-independent and stay in the origin fixture, which
// also still owns the shared CorpusStyleBody generator (made internal so this file can reuse it
// rather than duplicate the corpus-shaped fixture). See Gated/README.md.

using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests.Gated
{
    public class DbaHandoffLayoutTests
    {
        static DbaHandoffLayoutTests()
        {
            // App.xaml.cs sets this at startup; tests bypass startup, so set it here.
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }

        private static byte[] RenderHandoff(int rowCount, string message)
        {
            var findings = Enumerable.Range(0, rowCount).Select(i => new BundleFinding
            {
                Id = $"SQLT-SYNTHETIC-{i:D4}",
                Severity = i % 2 == 0 ? "Critical" : "High",
                Name = $"Synthetic check {i} with a deliberately long display name to stress the column",
                Category = "Configuration",
                Message = message,
                Source = "Corpus",
                Status = "Fail",
            }).ToList();

            return AssessmentPdf.BuildDbaHandoffBundle(new DbaHandoffBundle
            {
                Meta = new AssessmentMeta { Title = "Synthetic DBA Handoff" },
                Inventory = new List<(string, string)> { ("Server", "synthetic.local") },
                AllFindings = findings,
                KnownIssues = findings,
            });
        }

        [Theory]
        [InlineData(1)]
        [InlineData(60)]
        public void DbaHandoff_renders_at_the_raised_ceiling(int rowCount)
        {
            var pdf = RenderHandoff(rowCount,
                AssessmentPdf.MarkdownToPlain(ReportBodyMarkdownAndLayoutTests.CorpusStyleBody(6100)));

            Assert.NotNull(pdf);
            Assert.True(pdf.Length > 1000, $"PDF suspiciously small: {pdf.Length} bytes");
            Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46 }, pdf.Take(4).ToArray()); // %PDF
        }

        [Fact]
        public void DbaHandoff_actually_renders_the_longer_body_rather_than_dropping_it()
        {
            // Deliberately NOT an assertion that the renderer throws on overflow. That was tried and
            // measured: QuestPDF swallowed a 4000-char unbreakable token without raising, so a
            // does-not-throw test here would pass regardless of what the layout did and would be
            // worth nothing. The failure mode that IS detectable is content silently disappearing,
            // so this asserts the long-body document is materially heavier than the short-body one.
            var shortPdf = RenderHandoff(40, "A short finding message.");
            var longPdf  = RenderHandoff(40,
                AssessmentPdf.MarkdownToPlain(ReportBodyMarkdownAndLayoutTests.CorpusStyleBody(1500)));

            // Threshold calibrated against a measurement, not guessed: 40 rows at ~1500 chars renders
            // 323,622 bytes against 203,603 for the same 40 rows at ~24 chars — 1.59x. It is not
            // proportional to the 60x character increase because fonts, chrome and PDF text
            // compression dominate the byte count, so 1.25x is the floor below which the body has
            // plainly stopped reaching the page. (An earlier 2x threshold failed on correct output.)
            Assert.True(longPdf.Length > shortPdf.Length * 1.25,
                $"long-body PDF ({longPdf.Length} bytes) is not materially larger than the short-body one " +
                $"({shortPdf.Length} bytes) — the raised ceiling may be clipping instead of rendering.");
        }
    }
}
