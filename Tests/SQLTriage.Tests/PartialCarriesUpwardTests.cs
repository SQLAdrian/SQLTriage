/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

// ── FIX 2, persona board 2026-07-21 ───────────────────────────────────────────
// The row grid named PARTIAL. Everything above it did not: /cio and /dba folded all
// 45 into "Informational/skipped (210)", "363 of 575 scored" = 317 + 46 put PARTIAL
// outside the scored denominator, and the run-completion line reported 317/46/2 for a
// 575-check run — 45 results of the run the user had just executed, missing from its
// own summary. The exclusion moved the score in the FLATTERING direction, which is the
// same failure class as the PassRateScore 33→67 incident this slice was built to kill.
//
// The reconciliation these tests pin: PARTIAL travels upward as a DECOMPOSITION of the
// excluded count already on screen, never as an additional headline figure — so a page
// already criticised for carrying too many numbers that disagree gains no new number.
public class PartialCarriesUpwardTests
{
    private static CheckResult Row(string id, bool passed, string verdict = "", string severity = "High",
                                   string? error = null, string message = "") =>
        new()
        {
            CheckId = id,
            CheckName = id,
            InstanceName = "SRV",
            Category = "Security",
            Severity = severity,
            Passed = passed,
            Verdict = verdict,
            ErrorMessage = error,
            Message = message,
            ScoreWeight = 1,
            EffortHours = 1,
        };

    /// <summary>Real GovernanceService over default weights — no stub scoring path.</summary>
    private static GovernanceScore Score(IReadOnlyList<CheckResult> rows) =>
        new GovernanceService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GovernanceService>.Instance,
                new InMemoryWeights())
            .ComputeIndicativeAsync(rows).GetAwaiter().GetResult();

    /// <summary>Default weights, no disk I/O.</summary>
    private sealed class InMemoryWeights : IGovernanceWeightsProvider
    {
        public GovernanceWeights Current { get; } = new();

        // Required by the interface; these weights never change, so nothing ever raises it.
        // Explicit add/remove accessors rather than a field-like event, so the "never used"
        // warning is answered by design instead of adding a 140th warning to a 139 baseline.
        public event EventHandler? WeightsChanged { add { } remove { } }
    }

    /// <summary>2 pass, 1 fail, 3 partial, 1 info, 1 skip = 8 results.</summary>
    private static List<CheckResult> Mixed() => new()
    {
        Row("P1", true),
        Row("P2", true),
        Row("F1", false),
        Row("W1", true, verdict: "WARN"),
        Row("W2", true, verdict: "WARN"),
        Row("W3", true, verdict: "WARN"),
        Row("I1", true, severity: "INFO"),
        Row("S1", true, message: "SKIP — not applicable"),
    };

    // ── The score object the roll-ups read ───────────────────────────────────

    [Fact]
    public void GovernanceScore_counts_Partial_separately_from_the_rest_of_the_excluded_tier()
    {
        var score = Score(Mixed());

        score.PartialFindings.Should().Be(3,
            "the three WARN results must be counted, not absorbed into an anonymous excluded total");
        score.PassedFindings.Should().Be(2);
        score.FailedFindings.Should().Be(1);
    }

    [Fact]
    public void Naming_Partial_does_not_change_the_score_or_any_total()
    {
        // The whole point: this is a LABELLING fix. If counting Partial had moved the score,
        // the fix would itself be the flattering-direction defect it exists to expose.
        var score = Score(Mixed());

        score.TotalFindings.Should().Be(8);
        (score.PassedFindings + score.FailedFindings).Should().Be(3,
            "WARN stays out of the scored numerator AND denominator — naming it changes nothing");
    }

    [Fact]
    public void The_three_excluded_sub_counts_are_disjoint_and_foot_to_the_excluded_total()
    {
        // This is the arithmetic every surface relies on to itemise without inventing:
        // plain informational/skipped + partial + errored == total − (passed + failed).
        var rows = Mixed();
        rows.Add(Row("E1", false, error: "boom"));

        var score = Score(rows);

        var excluded = score.TotalFindings - (score.PassedFindings + score.FailedFindings);
        var plain = excluded - score.ErroredFindings - score.PartialFindings;

        plain.Should().BeGreaterThanOrEqualTo(0, "the sub-counts must never overlap into a negative remainder");
        (plain + score.PartialFindings + score.ErroredFindings).Should().Be(excluded,
            "the itemised parts must sum to exactly the number they decompose — no new total");
    }

    [Fact]
    public void An_errored_WARN_is_counted_once_as_errored_not_twice()
    {
        var rows = new List<CheckResult> { Row("W1", true, verdict: "WARN", error: "permission denied") };

        var score = Score(rows);

        score.ErroredFindings.Should().Be(1);
        score.PartialFindings.Should().Be(0,
            "counting it in both buckets would make the itemised parts sum to more than the whole");
    }

    // ── The per-run summary /dba reads ───────────────────────────────────────

    [Fact]
    public void CheckExecutionSummary_carries_Partial_as_a_subset_of_Informational()
    {
        var rows = Mixed();

        var summary = new CheckExecutionSummary
        {
            TotalChecks   = rows.Count,
            Informational = rows.Count(r => !CheckClassification.IsScorable(r)),
            Partial       = rows.Count(CheckClassification.IsWarn),
            Passed        = rows.Count(r => CheckClassification.IsScorable(r) && r.Passed),
            Failed        = rows.Count(r => CheckClassification.IsScorable(r) && !r.Passed && !r.IsAccepted),
        };

        summary.Partial.Should().Be(3);
        summary.Partial.Should().BeLessThanOrEqualTo(summary.Informational,
            "Partial is a NAMED SUBSET of Informational, not a sixth additive bucket");
        (summary.Passed + summary.Failed + summary.Accepted + summary.Errors + summary.Informational)
            .Should().Be(summary.TotalChecks,
                "the pre-existing footing invariant must be untouched by the new field");
        (summary.Informational - summary.Partial).Should().Be(2,
            "the surfaces render Informational MINUS Partial, so the displayed buckets stay disjoint");
    }

    [Fact]
    public void The_live_run_tally_increments_Partial_inside_the_non_scorable_branch()
    {
        // DECLARED LIMIT: CheckExecutionService's own tally loop runs only against a live SQL
        // Server, so this asserts its SHAPE against the shipped source rather than executing it.
        // That is weaker than the behavioural tests above and is said so plainly here rather than
        // left to a comment claiming coverage that does not exist.
        //
        // What it does catch, and what actually matters: Partial must be counted INSIDE the
        // !IsScorable branch. Counted outside it, Partial would stop being a subset of
        // Informational and the strip's "Informational − Partial" would go negative-adjacent and
        // stop footing to Checks run — which is the whole invariant this fix rests on.
        var source = ReadRepoSource(@"Data\CheckExecutionService.cs");

        var branch = "else if (!CheckClassification.IsScorable(result))";
        var occurrences = CountOccurrences(source, branch);
        occurrences.Should().Be(2,
            "both tally loops (ExecuteChecksAsync and its filtered sibling) must be found, "
          + "or this assertion is scanning the wrong thing");

        var increments = CountOccurrences(source, "if (CheckClassification.IsWarn(result)) summary.Partial++;");
        increments.Should().Be(2, "both tally loops must count Partial, or one run path silently drops it");

        // Each increment must sit AFTER a branch opener and BEFORE the next tally arm.
        var index = 0;
        for (var i = 0; i < 2; i++)
        {
            var branchAt = source.IndexOf(branch, index, StringComparison.Ordinal);
            branchAt.Should().BeGreaterThan(-1);
            var nextArm = source.IndexOf("else if (result.Passed)", branchAt, StringComparison.Ordinal);
            nextArm.Should().BeGreaterThan(branchAt);

            var body = source.Substring(branchAt, nextArm - branchAt);
            body.Should().Contain("summary.Informational++;");
            body.Should().Contain("if (CheckClassification.IsWarn(result)) summary.Partial++;",
                "Partial is counted within the non-scorable arm, keeping it a strict subset of Informational");

            index = nextArm;
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    private static string ReadRepoSource(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the repo root must be locatable, or this assertion is vacuous");
        var path = Path.Combine(dir!.FullName, relativePath);
        File.Exists(path).Should().BeTrue($"{relativePath} must exist to be asserted against");
        return File.ReadAllText(path);
    }

    [Fact]
    public void An_older_summary_without_the_field_still_foots()
    {
        // Persisted summaries written before Partial existed carry 0 and must stay valid.
        var legacy = new CheckExecutionSummary
        {
            TotalChecks = 10, Passed = 6, Failed = 2, Informational = 2,
        };

        legacy.Partial.Should().Be(0);
        (legacy.Passed + legacy.Failed + legacy.Accepted + legacy.Errors + legacy.Informational)
            .Should().Be(legacy.TotalChecks);
    }

    // ── The surfaces, asserted against the real shipped markup ───────────────

    [Fact]
    public void The_cio_dashboard_names_Partial_and_carves_it_out_of_the_excluded_block()
    {
        var markup = ReadMarkup("CioDashboard.razor");

        markup.Should().Contain("PartialFindings",
            "/cio must read the Partial count, not leave it buried in informational/skipped");
        // The exclusion label is built TWICE from an identical expression — once for the on-screen
        // strip and once for the PDF DTO. A bare Contain("partial\"") is satisfied by EITHER site,
        // and also by the adjacent CSS class `cio-completeness-tick--partial"`, which is how the
        // earlier version of this assertion stayed green while a cold gate deleted BOTH sites.
        // Count the occurrences so removing either one goes red.
        System.Text.RegularExpressions.Regex
            .Matches(markup, @"\$""\{stripPartial\} partial""").Count
            .Should().Be(2,
                "the exclusion label must itemise Partial in BOTH the on-screen strip and the PDF DTO — "
                + "a single occurrence means one of the two surfaces has silently stopped naming it");

        markup.Should().Contain("checks scored{stripExcludedLabel}",
            "the PDF DTO's check-count label must carry the itemisation, not just the bare scored count");

        markup.Should().Contain("checks scored@(stripExcludedLabel)",
            "the on-screen strip must carry the itemisation, not just the bare scored count");
        markup.Should().Contain("cio-completeness-tick--partial",
            "the completeness bar must render Partial as its own segment");
        markup.Should().Contain("Partial (@stripPartial)",
            "the legend must name Partial with its count");
        // The ARITHMETIC, not just the identifier: mutation testing caught the looser version
        // passing while stripExcludedPlain was reassigned to the un-netted value, which would have
        // rendered Partial twice — once in its own segment and once inside informational/skipped —
        // and made the completeness bar sum past the total it claims to decompose.
        markup.Should().Contain("var stripExcludedPlain = stripExcludedNonError - stripPartial;",
            "the informational/skipped figure must be reduced by Partial, or the bar double-counts");
        markup.Should().Contain("var stripPartial = _governanceScore.PartialFindings;",
            "and it must come from the score object, not be re-derived on the page");
    }

    [Fact]
    public void The_dba_dashboard_names_Partial_in_both_the_strip_and_the_per_server_table()
    {
        var markup = ReadMarkup("DbaDashboard.razor");

        markup.Should().Contain("_totalPartial", "the strip must carry a Partial tile");
        markup.Should().Contain("@(_totalInformational - _totalPartial)",
            "Informational/Skipped must be shown NET of Partial so the strip still foots to Checks run");
        // Asserted as its own CELL, not merely as an appearance of the token anywhere: mutation
        // testing caught the looser version passing while the column was gutted, because the
        // net-of-Partial Info/Skip cell on the line above also mentions kvp.Value.Partial.
        markup.Should().Contain("<td>@kvp.Value.Partial</td>",
            "the per-server table must have a Partial column of its own — it had none at all");
        markup.Should().Contain(@"<th title=""Ran but could not fully assess the target; excluded from scoring"">Partial</th>",
            "and a header naming it, or the column is an unlabelled number");
        markup.Should().Contain("@(kvp.Value.Informational - kvp.Value.Partial)",
            "the per-server Info/Skip cell must be net of Partial for the same reason");
    }

    [Fact]
    public void The_run_completion_line_reports_Partial_when_the_run_produced_any()
    {
        var markup = ReadMarkup("QuickCheck.razor");

        markup.Should().Contain("partialClause",
            "the completion sentence omitted 45 results of the run the user had just executed");
        markup.Should().Contain("{WarnCount} partial",
            "and it must state them in the same sentence as passed/failed/errors");
    }

    [Fact]
    public void The_UI_uses_exactly_one_spelling_for_the_Partial_state()
    {
        // Chip "PARTIAL", label "Partial", filter value "warn" was three names for one state.
        var markup = ReadMarkup("QuickCheck.razor");

        markup.Should().Contain(@"<option value=""partial"">Partial (@WarnCount)</option>");
        markup.Should().NotContain(@"<option value=""warn""",
            "the dropdown must not offer a second spelling of the state");
        markup.Should().NotContain(@"SetFilterFromCard(""warn"")",
            "the summary card must set the same token the dropdown offers");
        markup.Should().NotContain(@"SelectedFilter == ""warn""",
            "the active-filter highlight must test the same token, or the card never lights up");
    }

    private static string ReadMarkup(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Markup", fileName);
        File.Exists(path).Should().BeTrue(
            $"{fileName} is copied to the test output by SQLTriage.Tests.csproj; "
          + "if this fails every assertion below would vacuously pass");
        return File.ReadAllText(path);
    }
}
