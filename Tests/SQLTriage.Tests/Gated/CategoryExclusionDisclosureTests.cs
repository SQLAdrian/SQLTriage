/* In the name of God, the Merciful, the Compassionate */

// The honesty half of the Audit Assessment category filter (Adrian, 2026-08-05): a run that
// deliberately skipped part of the catalog must say so on every surface it produces, and a run that
// skipped nothing must render exactly as it always has.
//
// GATED because both builders are #if-fenced out of the community assembly:
// AssessmentPdf.BuildFindingsReport behind !SQLT_NO_REPORT_FINDINGS_PDF and
// AssessmentPdf.BuildExecutiveBriefing behind !SQLT_NO_REPORT_EXEC_BRIEFING (category 2 of
// Gated/README.md — the FILE still compiles, which is why a file-level audit of the gating never
// sees it). Community also drops the two export BUTTONS from QuickCheck.razor behind the same
// symbols, so nothing here is withheld from an edition that could reach it.
//
// Asserted against the BYTES a client receives, not the DTO: pinning the DTO pins the DTO, and the
// export it names can still drop the line. PdfTextExtractor walks the ToUnicode CMaps because Skia
// writes glyph ids, so a raw byte scan for these words silently finds nothing.

using System.Text.RegularExpressions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests.Gated
{
    public class CategoryExclusionDisclosureTests
    {
        // The one sentence, taken from the same place the app takes it, so this test cannot drift
        // from the string the page and the exports actually print.
        private static string Disclosure()
        {
            var catalog = new[]
            {
                new SQLTriage.Data.Models.SqlCheck { Id = "C1", Name = "c-one", Category = "Configuration" },
                new SQLTriage.Data.Models.SqlCheck { Id = "A1", Name = "a-one", Category = "Auditing" },
                new SQLTriage.Data.Models.SqlCheck { Id = "E1", Name = "e-one", Category = "Encryption" },
            };
            var sel = CategoryRunFilter.Apply(catalog, new[] { "Auditing", "Encryption" });
            Assert.NotNull(sel.Disclosure);
            return sel.Disclosure!;
        }

        /// <summary>Whitespace-normalised page text. QuestPDF emits one show-operation per LINE, and
        /// the extractor joins them with a single space — which is exactly the character a wrap
        /// replaced, so a sentence that wraps still reads verbatim after this.</summary>
        private static string Text(byte[] pdf) =>
            Regex.Replace(PdfTextExtractor.Extract(pdf), @"\s+", " ");

        private static FindingsReport Findings(string coverageNote) => new()
        {
            Meta = new AssessmentMeta
            {
                Title = "Audit Assessment", GeneratedUtc = "2026-08-05T00:00Z", RunId = "catfil01",
                CoverageNote = coverageNote,
            },
            Findings =
            {
                new FindingRow { State = FindingState.Pass, Name = "c-one", Category = "Configuration", Severity = "High", Server = "PROD-SQL-01", Detail = "ok" },
                new FindingRow { State = FindingState.Fail, Name = "c-two", Category = "Configuration", Severity = "Critical", Server = "PROD-SQL-01", Detail = "bad" },
            },
        };

        private static BriefingReport Briefing(string coverageNote) => new()
        {
            Meta = new AssessmentMeta
            {
                Title = "Audit Assessment", GeneratedUtc = "2026-08-05T00:00Z", RunId = "catfil01",
                CoverageNote = coverageNote,
            },
            Findings =
            {
                new FindingRow { State = FindingState.Pass, Name = "c-one", Category = "Configuration", Severity = "High", Server = "PROD-SQL-01", Detail = "ok" },
                new FindingRow { State = FindingState.Fail, Name = "c-two", Category = "Configuration", Severity = "Critical", Server = "PROD-SQL-01", Detail = "bad" },
            },
        };

        [Fact]
        public void FindingsPdf_OfAFilteredRun_PrintsTheCoverageSentence()
        {
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
            var note = Disclosure();

            var text = Text(AssessmentPdf.BuildFindingsReport(Findings(note)));

            // The extractor must actually be reading the document, or every assertion here passes
            // vacuously on an empty string — that is the failure mode this class exists to catch.
            Assert.Contains("Audit Assessment", text);

            Assert.Contains(note, text);
            Assert.Contains("Auditing, Encryption excluded from this run", text);
            Assert.Contains("2 of the 3 enabled checks in the catalog did not run", text);
        }

        [Fact]
        public void FindingsPdf_OfAnUnfilteredRun_MakesNoCoverageClaimAtAll()
        {
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

            var text = Text(AssessmentPdf.BuildFindingsReport(Findings("")));

            Assert.Contains("Audit Assessment", text);
            Assert.DoesNotContain("Category filter", text);
            Assert.DoesNotContain("did not run", text);
        }

        [Fact]
        public void Briefing_OfAFilteredRun_PrintsTheCoverageSentenceOnTheCover()
        {
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
            var note = Disclosure();

            var text = Text(AssessmentPdf.BuildExecutiveBriefing(Briefing(note)));

            Assert.Contains("Executive Briefing", text);
            Assert.Contains(note, text);
            Assert.Contains("Auditing, Encryption excluded from this run", text);
        }

        [Fact]
        public void Briefing_OfAnUnfilteredRun_MakesNoCoverageClaimAtAll()
        {
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

            var text = Text(AssessmentPdf.BuildExecutiveBriefing(Briefing("")));

            Assert.Contains("Executive Briefing", text);
            Assert.DoesNotContain("Category filter", text);
            Assert.DoesNotContain("did not run", text);
        }
    }
}
