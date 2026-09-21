/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Narration;
using Xunit;

namespace SQLTriage.Tests;

// ── FIX 3 + FIX 4 + FIX 5, persona board 2026-07-21 ──────────────────────────
// A buyer's objection was not that any single number was wrong — it was that the
// screen carried several numbers that disagreed with each other and nothing
// reconciled them. Each test below pins one of those reconciliations.
public class ContradictoryCopyTests
{
    // ── FIX 3: the narrative headline follows the gauge beside it ────────────

    [Fact]
    public void The_narration_headline_follows_the_headline_score_it_is_rendered_above()
    {
        // The shipped defect: governance 78 → "Healthy" bucket → "This estate looks
        // reasonably healthy", rendered in a green banner directly above an Executive
        // Health gauge reading 38 / Critical with six CRITICAL findings beneath it.
        var governance = ScoreWith(overall: 78.0);

        var withoutHeadline = NarrationService.Narrate(governance);
        var withHeadline    = NarrationService.Narrate(governance, headlineScore: 38.0);

        // Compared against the library's own bucket vocabulary rather than one hand-picked
        // phrase — each bucket holds several phrasings and the composer picks by seed, so
        // asserting a single sentence would pass or fail for the wrong reason.
        StartsWithAnyOf(withoutHeadline, NarrationLibrary.Headline[ScoreBucket.Healthy])
            .Should().BeTrue("sanity: with no headline number the governance score (78) still keys "
                           + "the sentence, exactly as before — this fix adds a caller option, it "
                           + "does not change existing callers");

        StartsWithAnyOf(withHeadline, NarrationLibrary.Headline[ScoreBucket.Healthy])
            .Should().BeFalse("a page whose headline gauge reads 38 / Critical must not open by "
                            + "calling the estate healthy — that green banner sat directly above "
                            + "the red gauge in the shipped build");
        StartsWithAnyOf(withHeadline, NarrationLibrary.Headline[ScoreBucket.Poor])
            .Should().BeTrue("38 lands in the Poor bucket, which is what the reader's eye is on");
    }

    [Fact]
    public void The_weakest_dimension_is_the_one_with_the_lowest_rendered_ring()
    {
        // Ranking used to be by unweighted pass ratio while the rings render the WEIGHTED
        // RawScore, so the sentence could name a dimension that was not the lowest ring
        // on screen. Compliance here has the better raw ratio but the worse ring.
        var governance = new GovernanceScore
        {
            Overall = 60,
            TotalFindings = 20,
            PassedFindings = 12,
            FailedFindings = 8,
            Categories = new Dictionary<string, CategoryScore>(StringComparer.OrdinalIgnoreCase)
            {
                ["Compliance"] = new() { Dimension = "Compliance", FindingCount = 38, PassedCount = 26, RawScore = 45, Assessed = true },
                ["Security"]   = new() { Dimension = "Security",   FindingCount = 10, PassedCount = 5,  RawScore = 89, Assessed = true },
            }
        };

        var text = NarrationService.Narrate(governance);

        text.Should().Contain("Compliance",
            "Compliance owns the lowest ring (45) and must be named weakest even though "
          + "Security has the worse raw pass ratio (5/10 vs 26/38)");
        text.Should().NotContain("weakest area is Security");
    }

    [Fact]
    public void The_weakest_sentence_reconciles_the_ring_with_the_counts_it_quotes()
    {
        // "Compliance — 26 of 38 checks passing" (68%) sat beside a ring reading 45%. Both
        // true, neither explaining the other. The sentence must now carry both and say why.
        var governance = new GovernanceScore
        {
            Overall = 45,
            TotalFindings = 38,
            PassedFindings = 26,
            FailedFindings = 12,
            Categories = new Dictionary<string, CategoryScore>(StringComparer.OrdinalIgnoreCase)
            {
                ["Compliance"] = new() { Dimension = "Compliance", FindingCount = 38, PassedCount = 26, RawScore = 45, Assessed = true },
            }
        };

        var text = NarrationService.Narrate(governance);

        text.Should().Contain("45", "the ring's own figure must appear, not only the raw counts");
        text.Should().Contain("26").And.Contain("38", "the counts behind it stay visible");
        text.Should().MatchRegex("weight",
            "the sentence must say WHY 26 of 38 is not 45% — the score weights by risk and effort");
    }

    [Fact]
    public void Every_weakest_phrasing_carries_all_four_tokens()
    {
        // A phrasing missing {score} would silently reintroduce the count-only sentence.
        foreach (var phrasing in NarrationLibrary.Weakest)
        {
            phrasing.Should().Contain("{dim}");
            phrasing.Should().Contain("{score}");
            phrasing.Should().Contain("{passed}");
            phrasing.Should().Contain("{total}");
        }
    }

    // ── FIX 3: the CRITICAL badge and the CRITICAL column share a unit ──────

    [Fact]
    public void The_estate_critical_badge_is_the_heatmap_columns_total()
    {
        // The badge counted SERVERS in critical health (2); the column beside it counted
        // critical FINDINGS (6). One word, two units, one screen.
        var markup = ReadMarkup("CioDashboard.razor");

        markup.Should().Contain("_estateCriticalCount = _serverCritical.Values.Sum()",
            "the badge must be the sum of the very dictionary the Critical column renders");
        markup.Should().NotContain("_estateCriticalCount = assessedScores.Count(s => s.Severity == HealthSeverity.Critical)",
            "counting servers under a findings label is the defect");
        markup.Should().Contain("critical finding@(_estateCriticalCount == 1 ? \"\" : \"s\")",
            "and the badge must name the unit it counts");
    }

    // ── FIX 3: one source for the framework count ───────────────────────────

    [Theory]
    [InlineData("Guide.razor")]
    [InlineData("Index.razor")]
    public void The_landing_framework_count_comes_from_the_same_source_as_the_map_tabs(string page)
    {
        // "22 frameworks mapped" (check-derived) vs 30 framework tabs (catalogue-derived).
        var markup = ReadMarkup(page);

        markup.Should().Contain("MappingSvc.GetFrameworks().Count",
            "the landing stat must read the mapping catalogue — the same call /compliance-map "
          + "builds its tabs from — so the two agree by construction");
        markup.Should().NotContain("CheckRepo.FrameworkMappings.Values",
            "deriving the same claim a second way is how the two figures drifted apart");
    }

    // ── FIX 4: Actual/Expected only where it means something ────────────────

    [Fact]
    public void A_verdict_contract_finding_does_not_print_a_contradictory_Actual_Expected_pair()
    {
        // The shipped line: "No enabled Server Audit Specifications found ...
        // (Actual: 1, Expected: 0)" — prose asserting none, evidence apparently saying one.
        var r = new CheckResult
        {
            CheckId = "SQLT-GAPFIL-00250",
            CheckName = "At Least One Server Audit Specification Is Enabled",
            Message = "No enabled Server Audit Specifications found",
            Verdict = "FAIL",
            ActualValue = 1,
            ExpectedValue = 0,
            Passed = false,
        };

        var suffix = FindingTranslator.MeasurementSuffix(r);

        suffix.Should().NotContain("Actual:",
            "on the verdict contract ActualValue is the check's own count column, not a measurement "
          + "of the noun in the message — labelling it 'Actual' manufactures a contradiction from "
          + "two individually-correct values");
        suffix.Should().Contain("FAIL", "the verdict IS the assertion the check made — show that instead");
        suffix.Should().Contain("1", "the count the check returned is still disclosed, honestly framed");
    }

    [Fact]
    public void A_numeric_contract_finding_keeps_its_Actual_Expected_pair()
    {
        // Here the pair is real: CheckExecutionService sets Passed = ActualValue == ExpectedValue.
        var r = new CheckResult
        {
            CheckName = "TempDB files on the system drive",
            Message = "9 tempdb file(s) are on the C: (system) drive.",
            Verdict = "",
            ActualValue = 9,
            ExpectedValue = 0,
            Passed = false,
        };

        FindingTranslator.MeasurementSuffix(r).Should().Be(" (Actual: 9, Expected: 0)");
    }

    [Fact]
    public void A_partial_finding_reports_the_products_word_for_it_not_the_wire_token()
    {
        var r = new CheckResult { Message = "Could not read every database", Verdict = "WARN", ActualValue = 0 };

        var suffix = FindingTranslator.MeasurementSuffix(r);

        suffix.Should().Contain("PARTIAL");
        suffix.Should().NotContain("WARN", "WARN is the corpus verdict token, not a word shown to a client");
    }

    // ── FIX 5: the adversarial review moved, and its text did not ───────────

    [Fact]
    public void The_adversarial_review_is_behind_a_link_at_a_stable_anchor()
    {
        var markup = ReadMarkup("About.razor");

        markup.Should().Contain(@"id=""adversarial-review""",
            "the review needs a stable anchor so it can be linked and cited");

        // The BUTTON's own label, not merely the phrase appearing somewhere on the page —
        // mutation testing caught the looser version passing while the button was relabelled
        // "Read: about the author", because the phrase also lives in the do-not-edit banner
        // comment and the modal's aria-label.
        markup.Should().Contain("Read: an adversarial review of this project",
            "the link must be signposted for what it is — the candour is what converted the "
          + "hardest persona, and a neutral 'about the author' label buries exactly that");

        // The review body must be INSIDE the conditional, not merely guarded by a flag that
        // exists — mutation testing caught the looser version passing with @if (true).
        var guardAt = markup.IndexOf("@if (_showAdversarialReview)", StringComparison.Ordinal);
        guardAt.Should().BeGreaterThan(-1, "the modal must be rendered conditionally");
        var bodyAt = markup.IndexOf(@"<pre class=""author-perspective-text"">", StringComparison.Ordinal);
        bodyAt.Should().BeGreaterThan(guardAt,
            "the review text must render only inside the modal — unrolling it on the page body "
          + "is the placement Adrian ruled against");
        markup.IndexOf(@"<pre class=""author-perspective-text"">", StringComparison.Ordinal)
              .Should().Be(markup.LastIndexOf(@"<pre class=""author-perspective-text"">", StringComparison.Ordinal),
            "the review must render in exactly ONE place");

        // Opening it is a deliberate act (a click, or the deep link) — never an accident.
        markup.Should().Contain("private bool _showAdversarialReview;",
            "the modal must default to closed, so a client landing on /about never falls into it");
        markup.Should().Contain("AdversarialReviewAnchor",
            "and the deep link must resolve through the same named anchor the section carries");
    }

    [Fact]
    public void The_review_text_is_still_the_sealed_text_byte_for_byte()
    {
        // The relocation must not have touched one word. The page recomputes this hash at
        // runtime and renders "TEXT MODIFIED" on a mismatch; this pins the same invariant in
        // CI, where a well-meaning tidy-up would otherwise only be caught by a human opening
        // the page. The two stale "547 curated checks" figures are INSIDE this seal and stay.
        var source = ReadMarkup("About.razor");

        const string sealedSha = "c0d6e3b6adcafeb0f22364c9915972619582bff8fb2a6ba02b84da77552c54e8";
        source.Should().Contain(sealedSha,
            "the authored checksum must remain the one the page checks against");

        var start = source.IndexOf("private const string AuthorAssessment = @\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the assessment literal must be locatable, or this test is vacuous");
        start = source.IndexOf('"', start + "private const string AuthorAssessment = @".Length) + 1;
        var end = source.IndexOf("\";", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);

        // Verbatim string literal: "" is an escaped quote in source, one quote in the value.
        var text = source.Substring(start, end - start).Replace("\"\"", "\"").Replace("\r\n", "\n");
        var actual = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

        actual.Should().Be(sealedSha,
            "the adversarial review is authored, commissioned, checksum-sealed content. Moving it is a "
          + "placement decision; editing it is not ours to make. If this fails, someone edited the text "
          + "— revert it rather than re-recording the hash.");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="text"/> opens with any phrasing from <paramref name="phrasings"/>.
    /// Compared on the literal prefix before the first token placeholder, since the composer fills
    /// {failed} etc. at render time.
    /// </summary>
    private static bool StartsWithAnyOf(string text, IEnumerable<string> phrasings) =>
        phrasings.Any(p =>
        {
            var brace = p.IndexOf('{');
            var prefix = (brace > 0 ? p.Substring(0, brace) : p).Trim();
            return prefix.Length > 0 && text.StartsWith(prefix, StringComparison.Ordinal);
        });

    private static GovernanceScore ScoreWith(double overall) => new()
    {
        Overall = overall,
        TotalFindings = 100,
        PassedFindings = 60,
        FailedFindings = 40,
        Categories = new Dictionary<string, CategoryScore>(StringComparer.OrdinalIgnoreCase)
        {
            ["Compliance"] = new() { Dimension = "Compliance", FindingCount = 38, PassedCount = 26, RawScore = 45, Assessed = true },
        }
    };

    private static string ReadMarkup(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Markup", fileName);
        File.Exists(path).Should().BeTrue(
            $"{fileName} is copied to the test output by SQLTriage.Tests.csproj; "
          + "if this fails every assertion below would vacuously pass");
        return File.ReadAllText(path);
    }
}
