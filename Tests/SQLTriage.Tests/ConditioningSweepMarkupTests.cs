/* In the name of God, the Merciful, the Compassionate */

// ── The conditioning sweep, 2026-08-05 ───────────────────────────────────────────────────────
// EstateHealthPolicyTests pins the SENTENCES character-for-character. What C# cannot reach is
// whether the .razor files actually call the policy, or quietly compose their own sentence beside
// it — which is precisely how each round of this work removed one over-claim and shipped another.
//
// These are LINTS over fixed strings in the real shipped markup (copied to the test output by
// SQLTriage.Tests.csproj), not a boundary — same framing as CategoryFilterMarkupTests. A rewrite
// that renames the members fails them loudly, which is the point; a rewrite that keeps the names
// and changes the meaning will not. The rendered matrix itself is driven live against a probe
// instance; these keep the wiring from rotting between drives.

using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace SQLTriage.Tests;

public class ConditioningSweepMarkupTests
{
    private static string ReadMarkup(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Markup", fileName);
        File.Exists(path).Should().BeTrue(
            $"{fileName} is copied to the test output by SQLTriage.Tests.csproj; if this fails "
            + "every assertion below would vacuously pass");
        return File.ReadAllText(path);
    }

    // ── /cio ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_cio_caption_comes_from_the_policy_and_not_from_its_own_count()
    {
        var markup = ReadMarkup("CioDashboard.razor");

        markup.Should().Contain("EstateHealthPolicy.EstateBasis(_estateRollup)",
            "the caption is the policy sentence, not a locally composed one");
        markup.Should().Contain("_estateRollup = EstateHealthPolicy.Summarise(_serverScores.Values)",
            "the mean and the counts come from ONE pass over ONE dictionary");
        markup.Should().NotContain("assessed server@(assessedCount == 1",
            "the hand-rolled caption is gone; it counted assessed servers independently of the "
            + "mean, and the two disagreed whenever a server was offline");
        markup.Should().NotContain("_serverScores.Values.Count(s => s.IsAssessed)",
            "a second, independent count of 'assessed' on the same card is the drift that produced "
            + "'mean across 2 assessed servers' beside 'nothing has been assessed yet'");
    }

    [Fact]
    public void The_cio_heatmap_cell_says_which_kind_of_unassessed_it_is()
    {
        var markup = ReadMarkup("CioDashboard.razor");

        markup.Should().Contain("title=\"@EstateHealthPolicy.ServerBasis(health)\"");
        markup.Should().NotContain("title=\"Not yet assessed\"",
            "that tooltip was printed for a server that did not answer and for one whose "
            + "collection threw, neither of which is 'not yet assessed'");
    }

    [Fact]
    public void The_cio_pdf_carries_the_same_basis_sentence_the_screen_does()
        => ReadMarkup("CioDashboard.razor").Should().Contain(
            "EhBasisLine  = EstateHealthPolicy.EstateBasis(_estateRollup)",
            "the deliverable and the screen must not disagree about the same estate");

    /// <summary>
    /// THE THIRTEENTH (2026-08-06). GovernanceScore.Overall holds a placeholder 0 when no check
    /// returned a pass or a fail, and Band reads that 0 as "Emerging", so /cio printed
    /// "0 · Emerging" as a maturity verdict for an estate where nothing had been scored. Three
    /// surfaces carried it off the one field: the strip, the governance-derived headline gauge
    /// above it, and the GovAssessed flag that puts a medal-coloured governance row in the client
    /// PDF. All three now ask the same question first.
    /// </summary>
    [Fact]
    public void The_cio_governance_score_and_band_are_withheld_when_nothing_was_scored()
    {
        var markup = ReadMarkup("CioDashboard.razor");

        markup.Should().Contain("private int GovernanceScoredChecks =>",
            "one definition of 'scored' for the page — a second, independent count is the drift "
            + "that makes two numbers on one card disagree");
        markup.Should().Contain("@if (stripEvaluated > 0)",
            "the strip's score and band are gated on a check having been scored");
        markup.Should().Contain("cio-gov-strip-score--unscored",
            "the unmeasured state renders a word, the way the mini-rings do, not a 0");
        markup.Should().Contain("@if (GovernanceScoredChecks > 0)",
            "the governance-derived headline gauge is the biggest number on the page and reads "
            + "the same placeholder");
        markup.Should().Contain("GovAssessed = GovernanceScoredChecks > 0",
            "the PDF prints its governance row on this flag");
        markup.Should().NotContain("GovAssessed = _governanceScore != null",
            "that read 'a governance object exists', not 'anything was scored', so a client PDF "
            + "carried '0 / Emerging' beside a medal swatch");
    }

    [Fact]
    public void The_cio_catch_records_the_fault_it_watched()
        => ReadMarkup("CioDashboard.razor").Should().Contain("CollectionFailed = true",
            "without it the estate caption counts a faulting server among the never-assessed");

    // ── /dba ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_dba_card_renders_every_dimension_and_reads_both_value_and_reason_from_the_policy()
    {
        var markup = ReadMarkup("DbaDashboard.razor");

        markup.Should().Contain("@foreach (var d in eh.Breakdown.Dimensions)",
            "all five render; filtering out the unmeasured ones made the card silently shrink "
            + "with nothing saying which dimension went missing or why");
        markup.Should().NotContain("eh.Breakdown.Dimensions.Where(d => d.HasData)");
        markup.Should().Contain("EstateHealthPolicy.DimensionValue(d)");
        markup.Should().Contain("EstateHealthPolicy.DimensionBasis(d)");
        markup.Should().NotContain("<span class=\"dba-dim-value\">@d.Score/100</span>",
            "printing Score unconditionally is what rendered 'Resource 0/100' for a silent server");
    }

    [Fact]
    public void The_dba_empty_card_says_which_kind_of_empty_it_is()
    {
        var markup = ReadMarkup("DbaDashboard.razor");

        markup.Should().Contain("EstateHealthPolicy.ServerBasis(eh)");
        markup.Should().NotContain(">No dimension data yet.</p>",
            "one sentence for four different states is the defect this wave exists to remove");
    }

    // ── /health ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_health_hero_withholds_the_number_when_nothing_measured_it()
    {
        var markup = ReadMarkup("Health.razor");

        markup.Should().Contain("hs.MeasuredScore?.ToString() ?? \"n/a\"",
            "the hero printed a large 0 beside the words 'Not yet assessed'");
        markup.Should().Contain("@if (hs.MeasuredScore.HasValue)",
            "the band is a verdict too, and it was rendered from that same 0");
        markup.Should().NotContain("<div class=\"health-hero-score\">@hs.Score</div>");
    }

    [Fact]
    public void The_health_sublines_come_from_the_policy()
    {
        var markup = ReadMarkup("Health.razor");

        markup.Should().Contain("EstateHealthPolicy.ServerCoverage(hs)");
        markup.Should().Contain("EstateHealthPolicy.DimensionBasis(dim)");
        markup.Should().NotContain("no data collected yet — excluded from the score (not assumed healthy)",
            "that told a reader 'no data collected yet' about a dimension whose server had gone "
            + "silent, which is a different fact and a different fix");
    }

    /// <summary>
    /// SR-11 (2026-08-08). The hero's trend chip was the card's last unhedged claim: its default
    /// branch printed "Stable" under an equals icon inside <c>title="Trend vs yesterday"</c>, and
    /// GetTrendAsync returned Stable for "no earlier snapshot" and for "the history read threw"
    /// alike. The sentences are pinned character-for-character in EstateHealthPolicyTests; this is
    /// the half C# cannot reach — whether the markup asks before it asserts.
    /// </summary>
    [Fact]
    public void The_health_trend_chip_is_conditioned_on_there_being_something_to_compare()
    {
        var markup = ReadMarkup("Health.razor");

        markup.Should().Contain("@if (hs.MeasuredTrend is null)",
            "a trend needs two measurements; MeasuredTrend is null in every state where fewer "
            + "than two exist");
        markup.Should().Contain("@switch (hs.MeasuredTrend.Value)",
            "the direction is read from the conditioned property, not the raw field");
        markup.Should().Contain("EstateHealthPolicy.TrendValue(hs)");
        markup.Should().Contain("title=\"@EstateHealthPolicy.TrendBasis(hs)\"");

        markup.Should().NotContain("@switch (hs.Trend)",
            "the raw field holds Stable in every unmeasured state — switching on it IS the defect");
        markup.Should().NotContain("title=\"Trend vs yesterday\"",
            "that tooltip asserted a yesterday to a reader whose server had none");
    }

    /// <summary>
    /// The same claim on the second surface that prints a direction off the same field. /cio's
    /// carrier objects and /server-comparison's cards both read it, and this one had already been
    /// half-fixed: it conditioned on the SCORE existing (2026-08-05) and still printed a confident
    /// "Stable" for a scored server with no history at all.
    /// </summary>
    [Fact]
    public void The_comparison_card_conditions_the_trend_and_not_only_the_score()
    {
        var markup = ReadMarkup("ServerComparison.razor");

        markup.Should().Contain("else if (hs.MeasuredTrend is null)");
        markup.Should().Contain("EstateHealthPolicy.TrendBasis(hs)");
        markup.Should().NotContain("hs.Trend == HealthTrend.Improving",
            "the raw field cannot distinguish a compared Stable from an uncompared one");
    }

    // ── The nav badge ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_badge_averages_the_policys_mean_and_not_every_score_in_the_dictionary()
    {
        var markup = ReadMarkup("HealthBadge.razor");

        markup.Should().Contain("EstateHealthPolicy.Summarise(all.Values)");
        markup.Should().Contain("_score = rollup.MeanScore;");
        markup.Should().NotContain("all.Values.Average(v => v.Score)",
            "that averaged the placeholder 0 an unreachable server carries into the estate figure "
            + "printed in the nav bar of every page");
    }

    [Fact]
    public void The_badge_never_lets_a_silent_server_lose_to_a_healthy_one()
    {
        var markup = ReadMarkup("HealthBadge.razor");

        markup.Should().NotContain("all.Values.Max(v => v.Severity)",
            "HealthSeverity.Unknown is 0 and Healthy is 1, so Max() ranked a silent server BELOW "
            + "a healthy one and flipped a baseline-red badge green for the same estate");
        markup.Should().Contain("private static int BadgeRank(HealthSeverity s)",
            "the ordering is explicit and named, not inherited from the enum's declaration order");
        markup.Should().Contain("candidates.Add(HealthSeverity.Unknown)",
            "an estate with an unmeasured server puts Unknown into the ranking rather than "
            + "aggregating only over the servers that answered");
    }

    /// <summary>
    /// The badge's glyph is an assertion with no words. It rendered the equals icon for an estate in
    /// which not one server had a second measurement to compare against — on the nav bar of every
    /// page, from the day of install (SR-11, 2026-08-08).
    /// </summary>
    [Fact]
    public void The_badge_paints_no_trend_glyph_when_nothing_has_been_compared()
    {
        var markup = ReadMarkup("HealthBadge.razor");

        markup.Should().Contain("private HealthTrend? _trend;",
            "null is 'no direction established', which is not the same fact as Stable");
        markup.Should().Contain("@if (_trend is not null)",
            "no comparison anywhere ⇒ no glyph");
        markup.Should().Contain("v.MeasuredTrend.HasValue",
            "the contributing set is the servers that HAVE a trend, not the ones with a score");
        markup.Should().NotContain("assessed.Any(v => v.Trend == HealthTrend.Degrading)",
            "reading the raw field collapsed 'no history' and 'history unreadable' into Stable, "
            + "which then fell through to the badge's steady glyph");
    }

    [Fact]
    public void The_badge_tooltip_names_the_servers_the_number_does_not_cover()
    {
        var markup = ReadMarkup("HealthBadge.razor");

        markup.Should().Contain("Name(\"Did not answer\"");
        markup.Should().Contain("Name(\"Health collection failed\"");
        markup.Should().Contain("Name(\"Not assessed yet\"");
        markup.Should().Contain("of {rollup.ServerCount} server(s)",
            "the mean says how many of how many it covers; 'across N server(s)' read as all of them");
    }

    // ── The status-bar power chip ────────────────────────────────────────────────────────────

    /// <summary>
    /// The chip sits in the shell, so its label is on EVERY page. Its relative branch interpolated
    /// the headroom band straight out of the DTO, and that band is 0/0 for a server whose checks
    /// have not run — so "tuning headroom ~0–0% (this server)" rode along beside every screen as a
    /// measured-looking zero (2026-08-08). The four state sentences are pinned in
    /// PowerEstimateServiceTuningHeadroomTests; this is the wiring.
    /// </summary>
    [Fact]
    public void The_power_chip_label_comes_from_the_state_and_not_from_the_band()
    {
        var markup = ReadMarkup("PowerChip.razor");

        markup.Should().Contain("_est.RelativeChipLabel",
            "one property, conditioned on HeadroomState, so the chip and its tooltip cannot "
            + "describe the same estimate differently");
        markup.Should().NotContain("tuning headroom ~{_est.TuneCutLowPct}",
            "interpolating the band unconditionally is the defect: 0–0% reads as a measurement of "
            + "no headroom, and it renders before any check has run");
    }

    // ── The empty-state lane ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_blocking_view_asks_before_it_asserts()
    {
        var markup = ReadMarkup("BlockingForensics.razor");

        markup.Should().Contain("@if (!_hasQueried)",
            "an empty list before the first query means 'we have not asked', not 'there is none'");
        markup.Should().Contain("_hasQueried = true;");
        markup.Should().Contain("_hasQueried = false; _hasQueriedHistory = false;",
            "clearing the data must clear the claim that we measured it");
        markup.Should().Contain("private void OnServerChanged(ChangeEventArgs e)",
            "the rows on screen were read from the OLD server; the claim must not survive the switch");
        markup.Should().NotContain("No active blocking on @_selectedServer right now.",
            "the unconditioned sentence is gone");
    }

    [Fact]
    public void The_shared_viewers_refuse_to_assert_an_absence_they_were_not_told_about()
    {
        foreach (var file in new[] { "BlockingTreeViewer.razor", "DeadlockViewer.razor" })
        {
            var markup = ReadMarkup(file);

            markup.Should().Contain("[Parameter] public bool? Queried { get; set; }",
                $"{file}: three-state on purpose, so a caller that says nothing gets the neutral "
                + "sentence rather than the reassuring one");
            markup.Should().Contain("@if (Queried != true)",
                $"{file}: null and false both mean no measurement was reported");
        }

        ReadMarkup("BlockingTreeViewer.razor").Should().NotContain(">No active blocking detected<");
        ReadMarkup("DeadlockViewer.razor").Should()
            .NotContain(">No deadlocks detected in the selected time range<");
    }

    [Fact]
    public void The_index_page_stops_claiming_every_index_is_used()
    {
        var markup = ReadMarkup("IndexAnalysis.razor");

        markup.Should().NotContain("All indexes are being utilized",
            "an outright universal claim, from a query that reads one database and whose DMV is "
            + "wiped by an instance restart");

        // The measurement itself moved out of the page on 2026-08-13 (the @code block's SQL and
        // mappers now live in IndexAnalysisService, so the InvalidCastException in one of them
        // could finally be tested). The CLAIM being defended is unchanged — the qualifier is
        // MEASURED in the same pass as the index reads, not asserted — so it is now asserted
        // against the type rather than against a string in the markup, which is stronger: this
        // binds the shipped constant instead of matching source text.
        SQLTriage.Data.Services.IndexAnalysisService.UsageWindowSql
            .Should().Contain("sqlserver_start_time").And.Contain("DB_NAME()",
                "both qualifiers come from one read against the connection the index reads use");
        markup.Should().Contain("IndexAnalysisSvc.GetAnalysisAsync",
            "the page takes the qualifiers from the same single-pass call that returns the rows, "
            + "so they cannot describe a different moment than the numbers beside them");
        markup.Should().Contain("analysis.ServerStartTime");
        markup.Should().Contain("UsageWindowSentence()");

        // ── The scope half, re-pointed 2026-08-13 ────────────────────────────────────────────
        // This used to assert `analysis.ScopeDatabase` and `ScopeDatabaseLabel()`, because the
        // page read exactly ONE database — whichever its connection landed in — and the qualifier
        // that kept the empty states honest was that database's name. The page now reads the 20
        // busiest user databases by IO, so that field no longer describes what the tabs show and
        // the page deliberately stopped holding it: a qualifier naming one database beside rows
        // from twenty is the same defect this test exists to prevent, pointing the other way.
        //
        // The CLAIM under test is unchanged — the page's scope sentences must come from the same
        // pass that returned the rows, never from a constant — so it is asserted against the
        // members that now carry it.
        markup.Should().NotContain("ScopeDatabaseLabel()",
            "the single-database qualifier must be GONE, not merely unused: leaving it in the page "
            + "is leaving a sentence that names one database next to rows from twenty");

        markup.Should().Contain("analysis.Scanned");
        markup.Should().Contain("analysis.Skipped");
        markup.Should().Contain("analysis.Census",
            "the scanned/skipped counts and the sample size are MEASURED by the ranking pass in "
            + "the same call that returned the rows, not assumed by the page");

        // ── Re-pointed again 2026-08-13 (the fix round) ─────────────────────────────────────
        // The sentences moved OUT of the page into IndexAnalysisScopeNarrative, because three of
        // them stated numbers the run never measured — most plainly "This run did not read
        // {list length} databases", where the list is capped. The claim under test is unchanged
        // and now has a stronger home: the words are asserted against a census in
        // IndexAnalysisMapperTests, and this only checks the page renders THOSE and composes none
        // of its own. `_census.TopN` is gone from the markup for the same reason the sentences
        // are: the narrative reads it, from the same snapshot every other sentence uses.
        markup.Should().Contain("IndexAnalysisScopeNarrative.For(_census, _scanned.Count, _skipped.Count)",
            "every scope sentence must come from ONE narrative built from THIS run's census and "
            + "THIS run's list lengths, or two sentences on the same screen can describe different "
            + "moments");
        markup.Should().Contain("Scope().Headline");
        markup.Should().Contain("Scope().MetricSentence");
        markup.Should().Contain("UnusedCoverage()").And.Contain("FragmentedCoverage()");

        markup.Should().NotContain("_scanned.Count} scanned",
            "the scope headline used to read 'N scanned, M skipped', which invites an operator to "
            + "add two list lengths into an instance total the run never measured");
        markup.Should().NotContain("ranked below the cut",
            "that sentence is the narrative's, conditioned on Census.RankedBelowCut; composing it "
            + "here is how it came to count databases whose IO was never read");
    }

    /// <summary>
    /// A database the run did not read must be NAMED on the page. Absence is the claim being
    /// defended against: an operator reading a per-database table cannot tell a database with no
    /// findings from a database nobody looked at, so the page has to say which it is.
    ///
    /// <para>⚠ A LINT over markup, same framing as the size test below. The runtime chokepoint is
    /// <c>IndexAnalysisResult.Skipped</c> being populated for every non-scanned database — proved
    /// against a real offline database by IndexAnalysisLiveSmokeTests, and over the captured
    /// ranking shapes by IndexAnalysisMapperTests. This only checks the page renders it.</para>
    /// </summary>
    [Fact]
    public void The_index_page_names_every_database_it_did_not_read()
    {
        var markup = ReadMarkup("IndexAnalysis.razor");

        markup.Should().Contain("_skipped.Count > 0",
            "the skipped list renders whenever it is non-empty");
        markup.Should().Contain("@skip.Database").And.Contain("@skip.Reason",
            "the name AND the reason — a count alone tells an operator nothing actionable");
        markup.Should().Contain("Scope().UnlistedCount > 0").And.Contain("Scope().UnlistedSentence",
            "the passed-over list is CAPPED so a large estate cannot flood the page, and a cap that "
            + "is not counted out loud is silent truncation");
        markup.Should().Contain("Scope().SkippedHeadline",
            "the headline over that list must be the census's not-read count, never the capped "
            + "list's own length — the length is what made 'This run did not read 20 databases' "
            + "false on an instance that went unread in 27");

        // The reasons themselves are composed in the service (DescribeStateSkip /
        // DescribeUnrankedSkip / the budget and failure sentences) and asserted verbatim there.
        // The page must not compose its own.
        markup.Should().NotContain("Not scanned.",
            "skip reasons come from IndexAnalysisService so the words an operator reads are the "
            + "words the tests pin; a sentence built in the markup is one nothing checks");

        // The per-database row caps. TOP 50 / TOP 30 are applied ONCE PER DATABASE now, so a
        // populated table is up to twenty independently truncated lists laid end to end.
        markup.Should().Contain("ia-table-scope",
            "the cap is disclosed on the POPULATED table, not only on the empty state — a full "
            + "table is exactly where an undisclosed truncation misleads");
    }

    /// <summary>
    /// Size is LEFT JOINed and can be genuinely absent; rendering 0.00 for it would assert an
    /// index occupies no space, which is a measurement nobody took. This is the same defect class
    /// as the sentences above — a rendered number conditioned on nothing — and it is asserted
    /// against the real shipped markup because whether the page prints the honest label or a raw
    /// ToString beside it cannot be seen from C#.
    ///
    /// <para>⚠ A LINT over fixed literals, not a boundary: a rewrite that keeps these names and
    /// changes the meaning passes it. The runtime chokepoint is
    /// <c>IndexAnalysisUnusedRow.SizeMB</c> / <c>IndexAnalysisFragRow.SizeMB</c> being
    /// <c>decimal?</c>, whose null is what actually stops a number being printed.</para>
    /// </summary>
    [Fact]
    public void The_index_page_never_prints_an_unmeasured_index_size_as_a_number()
    {
        var markup = ReadMarkup("IndexAnalysis.razor");

        markup.Should().NotContain("@idx.SizeMB.ToString(\"F2\")",
            "SizeMB is nullable; a raw ToString either throws or invents 0.00");
        markup.Should().NotContain("{idx.SizeMB:F2} MB wasted",
            "the DROP script comment is evidence an operator carries into a change request");
        markup.Should().Contain("SizeLabel(idx.SizeMB)");

        // ⚠ THE WORD MOVED OUT OF THE MARKUP, 2026-08-14, and this assertion moved with it. It read
        // Contain("not measured") over the page text; the page's SizeLabel now delegates to
        // IndexAnalysisRendering, where the client PDF reads the SAME word from the SAME constant.
        // Asserting the BOUND constant is strictly stronger than the text match it replaces — a
        // text match would have stayed green over a page that spelled the word its own way.
        markup.Should().Contain("IndexAnalysisRendering.Number(sizeMb, \"F2\")",
            "the page must render the absent case through the shared label, not its own literal");
        SQLTriage.Data.Services.IndexAnalysisRendering.Unmeasured.Should().Be("not measured",
            "the absent case is said out loud rather than rendered as a zero, in one word both "
            + "surfaces share");
    }

    /// <summary>
    /// THE SAME RULE FOR THE IMPACT COLUMNS, 2026-08-14. Both are declared nullable by the server
    /// (sys.dm_exec_describe_first_result_set, measured on 14.0.2120.1 and 16.0.4262.2), and the
    /// page routed both through a helper that substituted 0m — so an unmeasured suggestion rendered
    /// "0" and "0.00%", which reads as "the optimizer expects nothing from this index". That is a
    /// verdict, not an absence.
    ///
    /// <para>⚠ THE COLOUR IS PART OF IT. <c>ia-impact-low</c> is a verdict rendered in CSS, and
    /// applying it to a row nobody measured says in colour exactly what the text no longer says in
    /// words. The class helper takes <c>decimal?</c> so the null case is a branch the compiler
    /// forces someone to answer.</para>
    /// </summary>
    [Fact]
    public void The_index_page_never_prints_an_unmeasured_impact_figure_as_a_number()
    {
        var markup = ReadMarkup("IndexAnalysis.razor");

        markup.Should().NotContain("@idx.ImpactScore.ToString(\"N0\")",
            "ImpactScore is nullable; a raw ToString either throws or invents a zero");
        markup.Should().NotContain("@idx.AvgImpact.ToString(\"F2\")%",
            "the percent sign used to sit in the markup beside the value, so an absent reading "
            + "would have rendered as \"not measured%\"");

        markup.Should().Contain("IndexAnalysisRendering.Number(idx.ImpactScore, \"N0\")");
        markup.Should().Contain("IndexAnalysisRendering.Percent(idx.AvgImpact, \"F2\")");
        markup.Should().Contain("GetImpactClass(decimal? score)",
            "the severity colour must take the nullable type, so the unmeasured case is a branch "
            + "the compiler forces an author to answer rather than one a cast hides");
    }

    /// <summary>
    /// An EMPTY suggested script is not a script that is not needed. The statement is one SQL
    /// concatenation and every piece of it propagates NULL, so one object the login cannot resolve
    /// empties the whole thing — and the column is declared nullable (measured, 16.0.4262.2). The
    /// page used to render an empty &lt;pre&gt; and a Copy button that copied nothing.
    /// </summary>
    /// <summary>
    /// A READ THAT FAILED IS NOT AN EMPTY RESULT, 2026-08-14. The ranking read used to be the only
    /// uncaught command on this path, so a fifteen-second timeout replaced the whole page. It now
    /// degrades — and the degradation is only honest if the page RENDERS the reason: every table
    /// below is empty either way, so this block is the sole discriminator between "we read twenty
    /// databases and found nothing" and "we read nothing".
    ///
    /// <para>⚠ The runtime chokepoint is <c>IndexAnalysisResult.RankingFailure</c> being non-null
    /// and the selection being empty, both asserted in IndexAnalysisMapperTests. This is the lint
    /// that the page does not drop the sentence on the floor, which C# cannot see.</para>
    /// </summary>
    [Fact]
    public void The_index_page_says_which_read_failed_rather_than_rendering_an_empty_result()
    {
        var markup = ReadMarkup("IndexAnalysis.razor");

        markup.Should().Contain("_rankingFailure = analysis.RankingFailure",
            "the page must take the sentence off the result rather than composing its own");
        markup.Should().Contain("_missingReadFailure = analysis.MissingReadFailure");
        markup.Should().Contain("if (!string.IsNullOrEmpty(_rankingFailure))",
            "and render it: an unrendered failure field is the silent absence this replaces");
        markup.Should().Contain("if (!string.IsNullOrEmpty(_missingReadFailure))");

        // The empty Missing table must stop asserting a finding when the read never returned.
        markup.Should().NotContain(
            "<p>No missing-index suggestion was returned for the user databases on this instance.</p>",
            "that literal is a FINDING about the instance and rendered identically over a read "
            + "that never returned; it is now a branch");
        markup.Should().Contain("MissingEmptyHeadline()");
    }

    /// <summary>
    /// THE COPY SCRIPTS NAME THE REAL SCHEMA, 2026-08-14. Both builders wrote <c>[dbo]</c> as a
    /// LITERAL, because the queries projected OBJECT_NAME and OBJECT_NAME returns a bare table
    /// name — so every DROP INDEX and every ALTER INDEX this page emitted for a table outside dbo
    /// named an object that does not exist. An operator pastes these into a change request.
    ///
    /// <para>⚠ A LINT over the markup, not a boundary. The runtime chokepoints are the three
    /// queries projecting OBJECT_SCHEMA_NAME and <c>IndexAnalysisRendering.ScriptTarget</c>
    /// bracketing every part; both are asserted against bound code in IndexAnalysisMapperTests, and
    /// the emitted scripts were run against a real server. This checks the page reaches them.</para>
    /// </summary>
    [Fact]
    public void The_index_page_scripts_name_the_real_schema_rather_than_guessing_dbo()
    {
        var markup = ReadMarkup("IndexAnalysis.razor");

        markup.Should().NotContain(".[dbo].",
            "a hardcoded schema in an emitted script is a wrong object name for every table "
            + "outside dbo");
        markup.Should().Contain("IndexAnalysisRendering.ScriptTarget(idx.Database, idx.Schema, idx.Table)",
            "both the DROP script and the maintenance script must build their target from the "
            + "schema the run actually read, through the one shared builder");

        markup.Should().NotContain("private static string QuoteName(",
            "the escaping rule moved to IndexAnalysisRendering so the client report shares it: an "
            + "escaping rule that exists on one surface is missing from the other");
        markup.Should().Contain("IndexAnalysisRendering.QuoteName(idx.IndexName)");

        // The tables name the object a reader can find, not a bare name that is ambiguous the
        // moment an estate has two schemas.
        markup.Should().Contain("IndexAnalysisRendering.QualifiedTable(idx.Schema, idx.Table)");
        markup.Should().NotContain("<td>@idx.Table</td>");
    }

    [Fact]
    public void The_index_page_says_why_a_suggested_script_is_empty_rather_than_showing_an_empty_box()
    {
        var markup = ReadMarkup("IndexAnalysis.razor");

        markup.Should().Contain("string.IsNullOrWhiteSpace(idx.SuggestedScript)",
            "the empty case has to be a branch before it can be explained");
        markup.Should().Contain("No script was returned for this suggestion.",
            "an empty script box beside a populated row reads as 'no script needed'");
    }

    [Fact]
    public void The_xevents_page_distinguishes_never_read_from_read_and_empty_from_read_and_failed()
    {
        var markup = ReadMarkup("XEvents.razor");

        markup.Should().Contain("else if (!HasLoadedSessions)");
        markup.Should().Contain("else if (!string.IsNullOrEmpty(SessionLoadError))",
            "a read that threw used to render as an absence");
        markup.Should().NotContain("No XEvent sessions found. Create a new session or use a template.");
        markup.Should().NotContain("SessionLoadError = ex.Message",
            "a SQL or transport fault can embed the connection string; the type only");
    }
}
