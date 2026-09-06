/* In the name of God, the Merciful, the Compassionate */

// Synthetic-data tests for the 2026-07-22 report-body fix. NO SQL, NO client data —
// fabricated strings only, sized from the real corpus profile (576 Intents: median 670
// chars, max ~6050, 189 carrying a "### " sub-heading; 215 heading occurrences in all).
//
// Two things are guarded here:
//   1. Corpus bodies are Markdown and QuestPDF has no Markdown support, so the raw string
//      used to reach client deliverables as literal "**823, 824, and 825**", backticked
//      identifiers and a stray "### Why this matters".
//   2. The cell clamp was raised 400 -> 1500 so bodies stop truncating mid-sentence. That
//      trades against a real, documented failure mode in this codebase: a QuestPDF table
//      cell cannot split across a page break, and an over-tall non-splittable block throws
//      DocumentLayoutException (see RoadmapPdfBuilder.cs:178 and :363). A raised ceiling is
//      only safe if the document still renders, so it is rendered here rather than reasoned about.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class ReportBodyMarkdownAndLayoutTests
    {
        static ReportBodyMarkdownAndLayoutTests()
        {
            // App.xaml.cs sets this at startup; tests bypass startup, so set it here.
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }

        /// <summary>A body shaped like a real corpus Intent: bold, code spans, a sub-heading, hard wraps.</summary>
        internal static string CorpusStyleBody(int approxChars)
        {
            var sb = new StringBuilder();
            sb.Append("SQL Server raises errors **823, 824, and 825** on I/O integrity failures\n");
            sb.Append("(823 = OS-level read/write failure), read from `sys.master_files`.\n");
            sb.Append("Oracle: **sp_Blitz CheckID 96**.\n\n");
            sb.Append("### Why this matters\n");
            while (sb.Length < approxChars)
            {
                sb.Append("A tiny fixed autogrowth means a file that needs space grows in many tiny\n");
                sb.Append("steps, each a separate allocation event with `is_percent_growth = 0`.\n");
            }
            return sb.ToString();
        }

        [Fact]
        public void MarkdownToPlain_strips_every_markdown_artefact()
        {
            var plain = AssessmentPdf.MarkdownToPlain(CorpusStyleBody(900));

            Assert.DoesNotContain("**", plain);
            Assert.DoesNotContain("`", plain);
            Assert.DoesNotContain("### ", plain);
        }

        [Fact]
        public void MarkdownToPlain_promotes_a_subheading_instead_of_gluing_it_to_the_next_sentence()
        {
            // Markdig flattens a heading inline, which produced "...CheckID 96. Why this matters A
            // tiny fixed autogrowth...". 186 of 215 sub-headings are signposting, but "Cost",
            // "Caveat" and "Heuristic limits (read before acting)" are load-bearing, so headings are
            // promoted with a colon rather than dropped.
            var plain = AssessmentPdf.MarkdownToPlain(CorpusStyleBody(900));

            Assert.Contains("Why this matters:", plain);
            Assert.DoesNotContain("Why this matters A", plain);
        }

        [Fact]
        public void MarkdownToPlain_collapses_the_corpus_hard_wrap()
        {
            // Corpus source wraps at ~100 columns. Left alone, the cell renders the source's wrap
            // instead of reflowing to its own width.
            var plain = AssessmentPdf.MarkdownToPlain("one two\nthree four\n\nsecond para\nhere");

            Assert.Contains("one two three four", plain);
            Assert.Contains("second para here", plain);
        }

        [Fact]
        public void MarkdownToPlain_is_null_and_whitespace_safe()
        {
            Assert.Equal("", AssessmentPdf.MarkdownToPlain(null));
            Assert.Equal("", AssessmentPdf.MarkdownToPlain("   \r\n  "));
        }

        // RenderHandoff + DbaHandoff_renders_at_the_raised_ceiling +
        // DbaHandoff_actually_renders_the_longer_body_rather_than_dropping_it moved 2026-08-04 to
        // Gated/DbaHandoffLayoutTests.cs. They bind AssessmentPdf.BuildDbaHandoffBundle, which is
        // #if-fenced out of the community assembly (!SQLT_NO_REPORT_DBA_HANDOFF) and was breaking
        // the community test build. The MarkdownToPlain tests above are profile-independent and
        // stay; CorpusStyleBody is now internal so the moved tests reuse it instead of copying it.
    }
}
