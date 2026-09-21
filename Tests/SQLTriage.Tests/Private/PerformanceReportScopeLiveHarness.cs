/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.PerformanceReport;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests.Private;

/// <summary>
/// EXERCISE VEHICLE for the client Performance Analysis Report's index scope — gate-owned.
///
/// <para>⚠⚠ THIS FILE IS NOT IN EITHER SHIPPED SUITE, and nothing here may be counted inside a
/// "suite green" claim. <c>Data\Services\PerformanceReport\**</c> is Compile-Removed from the
/// default AND the community build (buildprofile.targets), so the composer, the snapshot model and
/// the report's PDF builder exist only under <c>-p:SQLTriagePrivate=true</c>. The test project
/// mirrors that condition on this whole folder. CI compiles neither the code under test nor this
/// file. That is exactly why the report needs a live harness: it is the ONLY instrument that ever
/// runs against it.</para>
///
/// <para>WHAT IT PROVES, 2026-08-14. Adrian ruled that the client report follows the live
/// /index-analysis page to top-20-by-IO scope. The composer now calls IndexAnalysisService instead
/// of holding byte-copies of its SQL, so what has to be shown is not that the SQL matches — there
/// is one copy — but that the RENDERED DELIVERABLE says what the run actually did: which databases
/// were read, which were not and why, and that no sentence claims instance-wide coverage the scan
/// did not perform.</para>
///
/// <para>INVOCATION (gate, on a box with the local test instances):</para>
/// <code>
///   $env:PERFREPORT_LIVE_TARGET = ".\old2017"
///   $env:PERFREPORT_LIVE_OUT    = "C:\temp\prscope"
///   dotnet test Tests/SQLTriage.Tests -c Release -p:SQLTriagePrivate=true `
///       --filter "FullyQualifiedName~PerformanceReportScopeLiveHarness"
/// </code>
///
/// <para>⚠ THE FIXTURE CONTRACT IS A PRECONDITION. This file creates nothing and drops nothing.
/// The 2026-08-14 lane built these on <c>.\old2017</c> as <c>_sqlt_prscope_*</c> and dropped them
/// afterwards, so this list IS the contract:</para>
/// <list type="bullet">
/// <item><c>_sqlt_prscope_hot</c> — the busiest user database by IO, holding a nonclustered index
/// that is written and never read. ⚠ Drive its writes through SINGLETON UPDATES BY PRIMARY KEY: a
/// predicate on any column the narrow index covers makes the optimizer SCAN that index, and a
/// scanned index stops qualifying as unused. Two fixture indexes were lost that way first.</item>
/// <item><c>_sqlt_prscope_frag</c> — an index over 5% fragmented across more than 1,000 pages, in a
/// database that is NOT rank 1, so finding its rows proves the sweep reached past the first
/// database. ⚠ A set-based <c>INSERT … SELECT</c> into a clustered index is sorted before insert
/// and fragments nothing; rebuild at FILLFACTOR 100 and then insert singletons at random keys.</item>
/// <item><c>_sqlt_prscope_bulk</c> — over 1 GB of zero-read index with more than 1,000 writes, so
/// the synthesis verdict's zero-read clause actually FIRES and its wording is rendered from live
/// numbers rather than believed. ⚠ A set-based insert counts as ONE user_update however many rows
/// it writes; the writes have to be singletons.</item>
/// <item><c>_sqlt_prscope_quiet</c> — a third database with a written-never-read index, so a
/// per-database label cannot be a constant.</item>
/// <item><c>_sqlt_prscope_ro</c> — READ_ONLY, so the run has a database it must NAME as skipped
/// with the state that caused it.</item>
/// <item>a missing-index suggestion somewhere on the instance, so the instance-wide table is not
/// empty. It needs a plan the optimizer costed as expensive.</item>
/// </list>
/// </summary>
public sealed class PerformanceReportScopeLiveHarness
{
    private static readonly string? Target = Environment.GetEnvironmentVariable("PERFREPORT_LIVE_TARGET");
    private static readonly string? OutDir = Environment.GetEnvironmentVariable("PERFREPORT_LIVE_OUT");

    private readonly ITestOutputHelper _out;

    public PerformanceReportScopeLiveHarness(ITestOutputHelper output) => _out = output;

    /// <summary>Same shape as IndexAnalysisLiveSmokeTests.LiveFactAttribute: SKIPPED, never a
    /// vacuous pass, when the harness is not armed.</summary>
    public sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute(params string[] requiredEnvironmentVariables)
        {
            var unset = requiredEnvironmentVariables.FirstOrDefault(
                name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));

            if (unset is not null)
                Skip = $"Live harness not armed. Set {unset} to run it. An armed run needs a "
                       + "reachable SQL instance carrying the _sqlt_prscope_* fixtures; see the "
                       + "INVOCATION block on PerformanceReportScopeLiveHarness.";
        }
    }

    private static string RequireTarget()
    {
        Assert.False(string.IsNullOrWhiteSpace(Target),
            "PERFREPORT_LIVE_TARGET is not set, so this test has no instance to probe and nothing "
            + "to assert. It should have been SKIPPED by LiveFactAttribute; if it ran, that "
            + "attribute is no longer doing its job.");
        return Target!;
    }

    // ── 1. The full run: the report describes the sweep it actually performed ────────────────

    [LiveFact("PERFREPORT_LIVE_TARGET")]
    public async Task The_report_names_the_databases_it_read_and_the_ones_it_did_not()
    {
        var alias = RequireTarget();
        using var scope = new HarnessScope();

        var model = await scope.ComposeAsync(alias, IndexAnalysisService.OverallBudgetDefault);

        model.Succeeded.Should().BeTrue(model.FailureReason ?? "the live collection failed");

        var ixScope = model.Index.Scope;
        ixScope.Ranked.Should().BeTrue("the ranking pass must have returned a census");

        // THE PARTITION. Every count the report renders is built off this identity, so if it does
        // not hold the sentences are arithmetic about nothing.
        var c = ixScope.Census;
        (c.Sampled + c.PassedOver + c.RankedBelowCut).Should().Be(c.UserDatabases,
            "the census partitions the instance three ways by construction");
        c.TopN.Should().Be(IndexAnalysisService.TopDatabasesByIo);

        // The sweep reached the fixtures, and it reached PAST the busiest database.
        ixScope.ScannedDatabases.Should().Contain("_sqlt_prscope_hot");
        ixScope.ScannedDatabases.Should().Contain("_sqlt_prscope_frag");
        ixScope.ScannedDatabases[0].Should().NotBe("_sqlt_prscope_frag",
            "the fragmentation fixture is deliberately not rank 1, so a fragmented row from it "
            + "proves the sweep did not stop at the first database");

        // Every row names a database, and that database is one the run actually read. A row
        // labelled with a database the report never scanned is the defect this lane removes.
        foreach (var r in model.Index.Unused)
        {
            r.Database.Should().NotBeNullOrWhiteSpace();
            ixScope.ScannedDatabases.Should().Contain(r.Database);
        }
        foreach (var r in model.Index.Fragmented)
        {
            r.Database.Should().NotBeNullOrWhiteSpace();
            ixScope.ScannedDatabases.Should().Contain(r.Database);
        }

        model.Index.Unused.Select(r => r.Database).Distinct().Should().HaveCountGreaterThan(1,
            "with unused indexes in three fixtures, a constant database label would mean the "
            + "per-database sweep collapsed back to one database");

        model.Index.Fragmented.Should().Contain(r => r.Database == "_sqlt_prscope_frag",
            "the fragmented fixture must appear, or the sweep never reached it");

        // The read-only fixture is NAMED, with the state that caused it.
        var readOnly = ixScope.Skipped.FirstOrDefault(s => s.Database == "_sqlt_prscope_ro");
        readOnly.Should().NotBeNull("a database the run could not read must be named, never omitted");
        readOnly!.Reason.Should().Contain("read-only");

        // The synthesis clause is built from the databases READ, and says so.
        model.Index.Verdict.DatabasesRead.Should().Be(ixScope.ScannedDatabases.Count);
        model.Index.Verdict.Fired.Should().BeTrue(
            "the bulk fixture carries over 1 GB of zero-read index with more than 1,000 writes");
        var clause = model.Index.Verdict.Clauses.Single(x => x.Contains("zero-read indexes"));
        clause.Should().Contain($"the {ixScope.ScannedDatabases.Count} databases this run read",
            "the gigabyte figure covers the sample and no more, so the sentence names the sample");

        await CaptureAsync(model, "full");
    }

    // ── 2. The budget run: a database the run gave up on is named, not dropped ───────────────

    [LiveFact("PERFREPORT_LIVE_TARGET")]
    public async Task A_forced_tiny_budget_names_every_database_it_gave_up_on()
    {
        var alias = RequireTarget();
        using var scope = new HarnessScope();

        // A fake clock that advances 10 seconds per reading against a 15-second budget: the first
        // database gets its turn, and everything after it meets an exhausted budget. Wall clock is
        // never involved, so the branch is reachable in milliseconds and deterministically.
        var ticks = 0;
        var origin = new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc);
        Func<DateTime> clock = () => origin.AddSeconds(10 * ticks++);

        var model = await scope.ComposeAsync(alias, TimeSpan.FromSeconds(15), clock);

        model.Succeeded.Should().BeTrue(model.FailureReason ?? "the live collection failed");

        var ixScope = model.Index.Scope;
        ixScope.BudgetExhausted.Should().BeTrue("the budget was forced to run out mid-sweep");
        ixScope.ScannedDatabases.Should().HaveCount(1, "only the first database got a turn");

        var budgetSkips = ixScope.Skipped.Where(s => s.Reason.Contains("budget")).ToList();
        budgetSkips.Should().NotBeEmpty("a database the budget stopped is named, never dropped");
        budgetSkips.Should().OnlyContain(s => s.Reason.Contains("15-second"),
            "the sentence quotes the budget this run was actually given");

        // The headline counts the INSTANCE, not the list. The whole point of the census.
        var narrative = IndexAnalysisScopeNarrative.For(
            ixScope.Census, ixScope.ScannedDatabases.Count, ixScope.Skipped.Count);
        narrative.NotRead.Should().Be(ixScope.Census.UserDatabases - 1);
        narrative.SkippedHeadline.Should().Contain($"did not read {narrative.NotRead} of the "
                                                   + $"{ixScope.Census.UserDatabases} user database");

        await CaptureAsync(model, "budget");
    }

    // ── 3. Pure: the verdict's wording, without a server ─────────────────────────────────────
    //
    // These need no live target, and they still only run in a private build. They are here because
    // the clause text is client-facing and two of its branches cannot be reached from a fixture:
    // a size that was never measured, and a single-database run.

    [Fact]
    public void The_zero_read_clause_names_the_sample_it_was_measured_over()
    {
        var unused = new List<UnusedIndexRow>
        {
            new() { Database = "A", IndexName = "IX_1", UserReads = 0, UserWrites = 5000, SizeMB = 2048 },
        };

        var v = IndexSynthesis.Synthesize(Array.Empty<MissingIndexRow>(), unused, "SRV01", databasesRead: 7);

        v.Clauses.Should().ContainSingle();
        v.Clauses[0].Should().Contain("the 7 databases this run read");
        v.Clauses[0].Should().NotContain("floor", "every size in this set was measured");
        v.DatabasesRead.Should().Be(7);
    }

    [Fact]
    public void An_unmeasured_index_size_makes_the_gigabyte_figure_a_declared_floor()
    {
        var unused = new List<UnusedIndexRow>
        {
            new() { Database = "A", IndexName = "IX_1", UserReads = 0, UserWrites = 5000, SizeMB = 2048 },
            new() { Database = "A", IndexName = "IX_2", UserReads = 0, UserWrites = 5000, SizeMB = null },
        };

        var v = IndexSynthesis.Synthesize(Array.Empty<MissingIndexRow>(), unused, "SRV01", databasesRead: 1);

        v.ZeroReadTaxUnmeasuredIndexCount.Should().Be(1);
        v.ZeroReadTaxGb.Should().Be(2.0, "an absent size is not a zero-byte index and is not summed");
        v.Clauses[0].Should().Contain("1 of them returned no size, so the gigabyte figure is a floor.");
        v.Clauses[0].Should().Contain("the 1 database this run read");
    }

    // ── 3b. Pure: an impact score nobody measured, 2026-08-14 ────────────────────────────────
    //
    // Both impact columns are declared nullable by the server (sys.dm_exec_describe_first_result_set
    // over MissingIndexSql, measured on 14.0.2120.1 and 16.0.4262.2), and until this lane the
    // service mapped them through a helper that substituted 0m. In this report that rendered as an
    // "Impact" cell reading 0 — a client reads that as "the optimizer expects nothing from this
    // index" — and it silently excluded the suggestion from the material count with no disclosure.
    //
    // ⚠ THE CLEAR BRANCH IS THE ONE THAT MATTERS. A floor sentence that only appears inside a FIRED
    // clause leaves "No material index imbalance …" unguarded, and that is the sentence on the page
    // a client forwards. Both branches read IndexVerdict.UnmeasuredImpactCaveat.

    private static MissingIndexRow Missing(string db, double? score) => new()
    {
        Database = db, Table = "T", KeyColumns = "[c]", UserHits = 10,
        AvgImpact = score.HasValue ? 90d : null, ImpactScore = score,
    };

    [Fact]
    public void A_suggestion_with_no_impact_score_is_neither_counted_as_material_nor_cleared()
    {
        var missing = new List<MissingIndexRow>
        {
            Missing("A", 5_000_000), Missing("A", 4_000_000), Missing("A", 3_000_000),
            Missing("A", 2_000_000), Missing("A", 1_500_000),
            Missing("A", null), Missing("A", null),
        };

        var v = IndexSynthesis.Synthesize(missing, Array.Empty<UnusedIndexRow>(), "SRV01", databasesRead: 3);

        v.MaterialMissingCount.Should().Be(5,
            "a null never satisfies the threshold comparison, so it is not material");
        v.UnmeasuredMissingImpactCount.Should().Be(2, "...and it is not silently dropped either");
        v.Clauses.Should().ContainSingle();
        v.Clauses[0].Should().Contain("Under-indexed on A: 5 high-impact missing-index suggestion(s)");
        v.Clauses[0].Should().Contain(
            "2 further suggestion(s) returned no impact score and could not be judged either way, "
            + "so this count is a floor.");
    }

    /// <summary>
    /// THE CLEAR BRANCH. Nothing crosses the threshold, so the report prints "No material index
    /// imbalance …" — over suggestions it could not judge. The caveat is what stops that being a
    /// verdict conditioned on a measurement nobody took.
    /// </summary>
    [Fact]
    public void A_clear_index_verdict_names_the_suggestions_it_could_not_judge()
    {
        var v = IndexSynthesis.Synthesize(
            new List<MissingIndexRow> { Missing("A", 10), Missing("A", null), Missing("B", null) },
            Array.Empty<UnusedIndexRow>(), "SRV01", databasesRead: 4);

        v.Fired.Should().BeFalse("nothing crossed a threshold, so the report takes the clear branch");
        v.UnmeasuredMissingImpactCount.Should().Be(2);
        v.UnmeasuredImpactCaveat.Should().Be(
            "2 missing-index suggestion(s) returned no impact score, so they were neither counted "
            + "as material nor cleared.");
    }

    [Fact]
    public void The_caveat_is_silent_when_every_suggestion_carried_a_score()
    {
        var v = IndexSynthesis.Synthesize(
            new List<MissingIndexRow> { Missing("A", 10), Missing("B", 20) },
            Array.Empty<UnusedIndexRow>(), "SRV01", databasesRead: 4);

        v.UnmeasuredMissingImpactCount.Should().Be(0);
        v.UnmeasuredImpactCaveat.Should().BeEmpty("a caveat about nothing is noise");
    }

    /// <summary>
    /// The snapshot is the diffable artifact a later engagement compares against, so an absent
    /// impact has to survive serialisation as an absence. A 0 here would be trusted by a
    /// before/after diff as a real reading that later "improved".
    /// </summary>
    [Fact]
    public void An_absent_impact_survives_the_snapshot_as_null_rather_than_as_a_zero()
    {
        var row = Missing("A", null);

        var json = JsonSerializer.Serialize(row);

        json.Should().Contain("\"ImpactScore\":null");
        json.Should().Contain("\"AvgImpact\":null");
        json.Should().NotContain("\"ImpactScore\":0");

        JsonSerializer.Deserialize<MissingIndexRow>(json)!.ImpactScore.Should().BeNull();
    }

    /// <summary>
    /// THE PIXELS. Renders the REAL client PDF over a model holding one measured and one unmeasured
    /// suggestion, and writes it to <c>PERFREPORT_LIVE_OUT</c> so the gate can read the page a
    /// client would.
    ///
    /// <para>⚠ WHY A RENDER AND NOT ONLY THE MODEL ASSERTIONS ABOVE. Making the column nullable
    /// moves the failure: <c>r.ImpactScore.ToString("N0")</c> on a <c>double?</c> renders the EMPTY
    /// STRING rather than throwing, so a report with a blank Impact cell and no explanation would
    /// have passed every type-level check in this file. Only rendering shows what is in the cell.
    /// This needs no live instance — the renderer is a pure function of the model — so it runs
    /// wherever the private build does.</para>
    /// </summary>
    [Fact]
    public async Task The_rendered_report_prints_an_absent_impact_as_words_and_never_as_a_blank()
    {
        var model = new PerformanceReportModel
        {
            ConnectionAlias = "SRV01",
            CanonicalServerName = "SRV01",
            AppVersion = "0.0.0-harness",
            GeneratedUtc = "2026-08-14 00:00:00Z",
            TimezoneId = "UTC",
            RunId = "idx-smalls-a",
            CompanyName = "Harness",
        };
        model.Index.Missing.Add(Missing("A", 2_500_000));
        model.Index.Missing.Add(Missing("A", null));
        model.Index.Scope.Ranked = true;
        model.Index.Scope.Census = new IndexAnalysisRankingCensus
        {
            TopN = 20, UserDatabases = 3, Eligible = 3, Ineligible = 0,
            Sampled = 3, PassedOver = 0, RankedBelowCut = 0, SkipReportCap = 20,
        };
        model.Index.Scope.ScannedDatabases.AddRange(new[] { "A", "B", "C" });
        model.Index.Verdict = IndexSynthesis.Synthesize(
            model.Index.Missing, model.Index.Unused, model.CanonicalServerName,
            model.Index.Scope.ScannedDatabases.Count);

        model.Index.Verdict.UnmeasuredMissingImpactCount.Should().Be(1,
            "the fixture must actually carry an unmeasured suggestion, or the render proves nothing");

        await CaptureAsync(model, "null-impact");
    }

    // ── 3d. The ranking read that failed, rendered, 2026-08-14 ───────────────────────────────
    //
    // ⚠⚠ THIS STATE COULD NOT REACH THIS REPORT BEFORE. ReadDatabaseRankingAsync threw, the throw
    // landed in ComposeAsync's live-collection catch, and that marks the WHOLE instance
    // Succeeded=false and returns -- so a fifteen-second timeout on ONE read also cost the client
    // the disk section, the hotspots, the maintenance advice and the capacity page, none of which
    // had anything to do with ranking databases by IO. The service degrades now, and what has to be
    // shown is that the rendered deliverable SAYS which read it lost. Every table in the section is
    // empty either way; without the sentence the section reads as a clean one.
    //
    // No live instance is involved: the renderer is a pure function of the model, and forcing a
    // real 15-second ranking timeout on demand needs a server nobody has.

    private static PerformanceReportModel DegradedModel(string? rankingFailure, string? missingFailure)
    {
        var model = new PerformanceReportModel
        {
            ConnectionAlias = "SRV01",
            CanonicalServerName = "SRV01",
            AppVersion = "0.0.0-harness",
            GeneratedUtc = "2026-08-14 00:00:00Z",
            TimezoneId = "UTC",
            RunId = "idx-smalls-b",
            CompanyName = "Harness",
        };
        model.Index.Scope.RankingFailure = rankingFailure;
        model.Index.Scope.MissingReadFailure = missingFailure;
        model.Index.Verdict = IndexSynthesis.Synthesize(
            model.Index.Missing, model.Index.Unused, model.CanonicalServerName, databasesRead: 0);
        return model;
    }

    [Fact]
    public void The_report_carries_both_read_failures_in_the_services_own_words()
    {
        var ranking = IndexAnalysisService.DescribeRankingFailure(new TimeoutException("x"));
        var missing = IndexAnalysisService.DescribeMissingReadFailure(new TimeoutException("x"));

        AssessmentPdf.PerfIndexReadFailures(DegradedModel(ranking, missing))
            .Should().BeEquivalentTo(new[] { ranking, missing }, o => o.WithStrictOrdering(),
                "in the order the reads were attempted, and verbatim: a report that paraphrases "
                + "the service's sentence is a second wording of one event");

        AssessmentPdf.PerfIndexReadFailures(DegradedModel(null, null))
            .Should().BeEmpty("a healthy run carries no failure box at all");

        AssessmentPdf.PerfIndexReadFailures(DegradedModel(null, missing))
            .Should().BeEquivalentTo(new[] { missing },
                "the two reads are independent; losing the missing-index list must not make the "
                + "report claim it scanned no database");
    }

    [Fact]
    public async Task The_rendered_report_names_the_ranking_read_it_lost()
    {
        var model = DegradedModel(
            IndexAnalysisService.DescribeRankingFailure(new TimeoutException("Execution Timeout Expired.")),
            IndexAnalysisService.DescribeMissingReadFailure(new TimeoutException("Execution Timeout Expired.")));

        model.Index.Scope.ScannedDatabases.Should().BeEmpty(
            "the fixture must be the degraded shape, or the render proves nothing");
        model.Index.Verdict.Fired.Should().BeFalse();

        await CaptureAsync(model, "ranking-failed");
    }

    // ── 3e. The EXEC PAGE, held to the same lost reads, 2026-08-14 (gate) ─────────────────────
    //
    // ⚠⚠ THE TESTS ABOVE ASSERTED THE HELPER AND NEVER THE BRANCH THAT CALLED IT, and two states
    // walked straight through that gap into a rendered client PDF. Both were read off the PDF, not
    // off the code:
    //
    //   (1) The 30-second missing-index read timed out, ranking healthy, 3 databases scanned. The
    //       exec page printed "No material index imbalance in the 3 databases this run read at
    //       export (point-in-time)." — and page 1 came out BYTE-IDENTICAL, modulo the run id, to a
    //       healthy control that read the same 3 databases and genuinely found nothing.
    //   (2) The 15-second ranking read timed out while the INDEPENDENT instance-wide missing read
    //       still returned 5 material suggestions. IndexVerdict.Fired was true, so the exec page
    //       took the Fired branch — which consulted no failure at all — and said nothing about the
    //       ranking read or the zero databases scanned.
    //
    // In both cases page 2 carried the red failure box, so the SECTION was honest and the page a
    // client forwards was not. That is the house defect class in its highest-visibility position.
    //
    // These bind AssessmentPdf.PerfExecIndexLine, which is the branch itself rather than the
    // helper it feeds on. No live instance is involved: the renderer is a pure function of the
    // model, and forcing a real 15-second ranking timeout on demand needs a server nobody has —
    // the SENTENCE is separately proved to be what the real guard produces over a real provider
    // timeout, and the guard is proved not to throw, by the gate's own live probe.

    private static readonly string RankingTimeout =
        IndexAnalysisService.DescribeRankingFailure(new TimeoutException("Execution Timeout Expired."));

    private static readonly string MissingTimeout =
        IndexAnalysisService.DescribeMissingReadFailure(new TimeoutException("Execution Timeout Expired."));

    /// <summary>
    /// A run that read <paramref name="scanned"/> of <paramref name="userDatabases"/> databases,
    /// optionally having lost one of the two independent index reads.
    /// </summary>
    private static PerformanceReportModel ScannedModel(
        int userDatabases, int scanned, string? ranking = null, string? missing = null,
        IEnumerable<MissingIndexRow>? missingRows = null)
    {
        var m = new PerformanceReportModel
        {
            ConnectionAlias = "SRV01", CanonicalServerName = "SRV01", AppVersion = "0.0.0-harness",
            GeneratedUtc = "2026-08-14 00:00:00Z", TimezoneId = "UTC", RunId = "idx-smalls-b",
            CompanyName = "Harness",
        };
        m.Index.Scope.Ranked = userDatabases > 0;
        m.Index.Scope.Census = new IndexAnalysisRankingCensus
        {
            TopN = 20, UserDatabases = userDatabases, Eligible = userDatabases, Sampled = scanned,
            SkipReportCap = 20,
        };
        m.Index.Scope.ScannedDatabases.AddRange(Enumerable.Range(1, scanned).Select(i => $"db{i}"));
        m.Index.Scope.RankingFailure = ranking;
        m.Index.Scope.MissingReadFailure = missing;
        if (missingRows is not null) m.Index.Missing.AddRange(missingRows);
        m.Index.Verdict = IndexSynthesis.Synthesize(
            m.Index.Missing, m.Index.Unused, m.CanonicalServerName, databasesRead: scanned);
        return m;
    }

    /// <summary>
    /// GAP 1. The clear branch. A run whose missing-index read was lost may not print the same
    /// executive page as a run that read everything and found nothing.
    /// </summary>
    [Fact]
    public void A_lost_missing_read_is_disclosed_on_the_exec_page_not_only_in_the_section()
    {
        var degraded = ScannedModel(userDatabases: 3, scanned: 3, missing: MissingTimeout);
        var healthy = ScannedModel(userDatabases: 3, scanned: 3);

        degraded.Index.Verdict.Fired.Should().BeFalse(
            "the fixture must take the CLEAR branch, or it proves nothing about it");

        var (text, accent) = AssessmentPdf.PerfExecIndexLine(degraded, cb: false);

        text.Should().StartWith(MissingTimeout,
            "the read that was lost is this run's news, and it comes before the verdict it "
            + "undermines — not after it, and not only on page 2");
        text.Should().NotBe(AssessmentPdf.PerfExecIndexLine(healthy, cb: false).Text,
            "a run that lost a read rendered an executive page byte-identical to a healthy one; "
            + "that identity IS the defect");
        accent.Should().NotBe(AssessmentPdf.Pass(false),
            "a green all-clear accent beside a sentence saying a read was lost is the same "
            + "overclaim in a second channel");
        accent.Should().Be(AssessmentPdf.Warn(false));

        // The other half of page 1: the empty Notable Findings line carries the same disclosure.
        AssessmentPdf.PerfIndexCoverageCaveat(degraded).Should().EndWith(MissingTimeout,
            "\"read 3 user databases, which is all of them\" under \"no findings crossed the "
            + "thresholds\" reads as an all-clear when the lost read is the one that finds "
            + "missing indexes");
    }

    /// <summary>
    /// GAP 2. The FIRED branch, evaluated first, shadowed the only branch that disclosed a lost
    /// ranking read — permanently, because a ranking failure implies zero scanned databases.
    /// </summary>
    [Fact]
    public void A_lost_ranking_read_is_disclosed_even_when_the_index_verdict_fires()
    {
        var m = ScannedModel(
            userDatabases: 0, scanned: 0, ranking: RankingTimeout,
            missingRows: Enumerable.Range(0, 5).Select(i => Missing("A", 9_000_000 - i)));

        m.Index.Verdict.Fired.Should().BeTrue(
            "the instance-wide missing read is INDEPENDENT of the ranking read — different query, "
            + "different timeout — so material suggestions with zero databases scanned is an "
            + "ordinary estate shape, not a contrived one");
        m.Index.Scope.ScannedDatabases.Should().BeEmpty();

        var (text, accent) = AssessmentPdf.PerfExecIndexLine(m, cb: false);

        text.Should().StartWith(RankingTimeout,
            "the exec page must say the sweep read nothing, whatever else it found");
        text.Should().Contain("Under-indexed on A",
            "and it must still carry the finding — disclosure replaces neither the verdict nor "
            + "the other way round");
        accent.Should().Be(AssessmentPdf.Fail(false));
    }

    /// <summary>
    /// The zero-database branch that already disclosed correctly, pinned so the restructure that
    /// hoisted the sentences out of it did not quietly drop the case it was written for.
    /// </summary>
    [Fact]
    public void The_zero_database_branch_still_leads_with_its_cause()
    {
        var (text, accent) = AssessmentPdf.PerfExecIndexLine(
            DegradedModel(RankingTimeout, MissingTimeout), cb: false);

        text.Should().StartWith(RankingTimeout, "in the order the reads were attempted");
        text.Should().Contain(MissingTimeout, "both lost reads are named, not just the first");
        text.Should().Contain("This run read no database for index detail");
        accent.Should().Be(AssessmentPdf.Fail(false));
    }

    /// <summary>
    /// A healthy run carries no failure prose at all. Without this, "disclose on every branch"
    /// could be satisfied by a renderer that shouts on every run, which is the same as silence.
    /// </summary>
    [Fact]
    public void A_healthy_run_carries_no_failure_sentence_and_keeps_its_green_accent()
    {
        var (text, accent) = AssessmentPdf.PerfExecIndexLine(
            ScannedModel(userDatabases: 3, scanned: 3), cb: false);

        text.Should().Be(
            "No material index imbalance in the 3 databases this run read at export (point-in-time).");
        accent.Should().Be(AssessmentPdf.Pass(false));

        AssessmentPdf.PerfIndexCoverageCaveat(ScannedModel(3, 3)).Should().Be(
            "The index sections read 3 user databases, which is all of them.");
    }

    // ── 4. Pure: the two exec-page sentences the 2026-08-14 gate found overclaiming ───────────
    //
    // Both are client-facing prose on the page a client forwards, and neither can be swept by a
    // live run: which branch fires depends on an estate shape (how many databases lost the sample,
    // how the rows spread across databases) that no fixture can dial in on demand. The gate proved
    // ONE branch of each against .\old2017 and .\new2022; these fix the rest in place.

    /// <summary>
    /// ⚠⚠ THE EXEC PAGE PROMISED NAMES THE SECTION REFUSES TO GIVE. Page 1 read "Index Analysis
    /// names the ones they did not" on every run. Proved live on a 30-database instance: the run
    /// read 20, the section named 1 (a RESTRICTED_USER database) and then said "9 more user
    /// databases were not read and are not named above". The section names only Scope.Skipped,
    /// which excludes every database counted in RankedBelowCut and every passed-over database
    /// beyond the service's skip cap — its own header documents that choice. So the exec sentence
    /// asserted a property of a section that the section denies two pages later.
    /// </summary>
    [Theory]
    // userDbs, scanned, skippedRows  → the clause the exec page is allowed to print
    [InlineData(30, 20, 1, "Index Analysis names 1 of the 10 databases it did not read and counts the rest.")]
    [InlineData(30, 0, 21, "Index Analysis names 21 of the 30 databases it did not read and counts the rest.")]
    [InlineData(23, 20, 3, "Index Analysis names all 3 databases it did not read.")]
    [InlineData(21, 20, 1, "Index Analysis names the one database it did not read.")]
    [InlineData(30, 20, 0, "Index Analysis counts the 10 databases it did not read rather than naming them.")]
    public void The_exec_page_promises_only_the_names_the_index_section_actually_prints(
        int userDatabases, int scanned, int skippedRows, string expected)
    {
        var m = ModelWithScope(userDatabases, scanned, skippedRows);

        AssessmentPdf.PerfIndexNamingClause(m).Should().Be(expected);

        // The clause and the section's own unlisted count must decompose the same number, or the
        // two pages are describing different runs again.
        var narrative = IndexAnalysisScopeNarrative.For(
            m.Index.Scope.Census, scanned, skippedRows);
        (Math.Min(skippedRows, narrative.NotRead) + narrative.UnlistedCount)
            .Should().Be(narrative.NotRead);
    }

    /// <summary>
    /// Without a census there is no instance-wide total, so the clause may not imply the list is
    /// complete OR incomplete. Same split as PerfIndexCoverageCaveat's, for the same reason.
    /// </summary>
    [Fact]
    public void With_no_census_the_naming_clause_claims_nothing_about_completeness()
    {
        var m = ModelWithScope(userDatabases: 0, scanned: 2, skippedRows: 0);
        AssessmentPdf.PerfIndexNamingClause(m).Should().Be(
            "Index Analysis names no database as unread, and this run took no instance-wide count "
            + "to measure that against.");

        var listed = ModelWithScope(userDatabases: 0, scanned: 2, skippedRows: 3);
        AssessmentPdf.PerfIndexNamingClause(listed).Should().Be(
            "Index Analysis names 3 unread databases, and this run took no instance-wide count to "
            + "measure that list against.");
    }

    /// <summary>
    /// ⚠⚠ THE ROW-CAP DISCLOSURE NAMED THE WRONG CASUALTY. It read "the databases lower down the
    /// sample are the ones cut" — an inference true only when the rows span several databases.
    /// Proved live on .\new2022: 19 fragmented rows, all from SQLWATCH, capped at 15. No database
    /// was cut; the four least-fragmented indexes of the LOUDEST database were. A DBA reads the old
    /// sentence and places the omission at the quiet end of the estate.
    /// </summary>
    [Fact]
    public void The_row_cap_disclosure_names_what_the_cut_actually_removed()
    {
        // The live .\new2022 shape: one database, 19 rows, 15 shown.
        AssessmentPdf.PerfRowCapSentence(Rows(("SQLWATCH", 19))).Should().Be(
            "Showing 15 of 19 rows. Every row here comes from SQLWATCH, so the 4 rows cut are the "
            + "rest of that one database's list and not another database.");

        // Several databases, and the cut lands inside the second while a third loses everything.
        AssessmentPdf.PerfRowCapSentence(Rows(("Hot", 10), ("Warm", 8), ("Quiet", 3))).Should().Be(
            "Showing 15 of 21 rows. The rows run in database order, busiest first, so the cut takes "
            + "the tail: 1 of the 3 databases in this table contributes no row above, and 3 further "
            + "rows of Warm are cut.");

        // Every database keeps a row; only the last one's tail is cut.
        AssessmentPdf.PerfRowCapSentence(Rows(("Hot", 10), ("Warm", 9))).Should().Be(
            "Showing 15 of 19 rows. The rows run in database order, busiest first, so the cut takes "
            + "the tail: every database in this table keeps at least one row above, and the 4 rows "
            + "cut are the rest of Warm's list.");

        // A cut that removes whole databases and nothing else.
        AssessmentPdf.PerfRowCapSentence(Rows(("Hot", 15), ("Quiet", 2), ("Quieter", 1))).Should().Be(
            "Showing 15 of 18 rows. The rows run in database order, busiest first, so the cut takes "
            + "the tail: 2 of the 3 databases in this table contribute no row above.");

        foreach (var rows in new[]
                 {
                     Rows(("SQLWATCH", 19)),
                     Rows(("Hot", 10), ("Warm", 8), ("Quiet", 3)),
                     Rows(("Hot", 10), ("Warm", 9)),
                     Rows(("Hot", 15), ("Quiet", 2), ("Quieter", 1)),
                 })
            AssessmentPdf.PerfRowCapSentence(rows).Should().NotContain(
                "the databases lower down the sample are the ones cut",
                "that clause is an inference about WHICH databases lost coverage, and it is false "
                + "whenever one database fills the table");
    }

    private static List<string> Rows(params (string Database, int Count)[] blocks)
        => blocks.SelectMany(b => Enumerable.Repeat(b.Database, b.Count)).ToList();

    /// <summary>
    /// A model carrying only the index scope the two sentences above read. UserDatabases = 0 is the
    /// no-census state (the ranking pass returned no census row), which is why it is spelled out
    /// rather than defaulted.
    /// </summary>
    private static PerformanceReportModel ModelWithScope(int userDatabases, int scanned, int skippedRows)
        => new()
        {
            Index = new IndexSection
            {
                Scope = new IndexScope
                {
                    Ranked = userDatabases > 0,
                    Census = new IndexAnalysisRankingCensus
                    {
                        TopN = 20, UserDatabases = userDatabases, SkipReportCap = 20,
                    },
                    ScannedDatabases = Enumerable.Range(1, scanned).Select(i => $"db{i}").ToList(),
                    Skipped = Enumerable.Range(1, skippedRows)
                        .Select(i => new SkippedDatabaseRow { Database = $"skip{i}", Reason = "not scanned." })
                        .ToList(),
                },
            },
        };

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The composer's five other dependencies, pointed at a throwaway directory so the harness
    /// never touches the operator's real history stores. Every section they feed is guarded inside
    /// ComposeAsync, so an empty store renders an honest empty state rather than failing the run —
    /// which is the same thing the app does on a first run.
    /// </summary>
    private sealed class HarnessScope : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "prscope-harness-" + Guid.NewGuid().ToString("N")[..8]);

        public HarnessScope() => Directory.CreateDirectory(_dir);

        public async Task<PerformanceReportModel> ComposeAsync(
            string alias, TimeSpan overallBudget, Func<DateTime>? clock = null)
        {
            var hist = new HistoricalPerformanceService(
                NullLogger<HistoricalPerformanceService>.Instance,
                dbPath: Path.Combine(_dir, "perf-history.db"));
            var blocking = new BlockingHistoryService(
                NullLogger<BlockingHistoryService>.Instance,
                dbPath: Path.Combine(_dir, "blocking-history.db"));
            var connections = new ServerConnectionManager(
                NullLogger<ServerConnectionManager>.Instance,
                connectionsFilePath: Path.Combine(_dir, "server-connections.json"));
            var maint = new MaintenanceScriptService(
                NullLogger<MaintenanceScriptService>.Instance, connections);

            var index = new IndexAnalysisService(
                log: null,
                utcNow: clock,
                overallBudget: overallBudget,
                perDatabaseSlice: IndexAnalysisService.PerDatabaseSliceDefault);

            var composer = new PerformanceReportComposer(
                hist, blocking, new DiskIoService(), new CodeHotspotsService(), maint, index);

            var conn = new ServerConnection
            {
                ServerNames = alias,
                Database = "master",
                UseWindowsAuthentication = true,
                TrustServerCertificate = true,
            };

            var stamp = new ProvenanceStamp
            {
                AppVersion = "harness",
                GeneratedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mmZ"),
                TimezoneId = TimeZoneInfo.Local.StandardName,
                RunId = "prscope",
                CompanyName = "SQLTriage live harness",
            };

            return await composer.ComposeAsync(conn, alias, null, stamp, CancellationToken.None);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Writes the REAL artefacts the operator would receive — the PDF built by the shipped builder
    /// and the JSON snapshot written beside it — so the gate reads the sentences off the deliverable
    /// rather than off an assertion about it.
    /// </summary>
    private async Task CaptureAsync(PerformanceReportModel model, string label)
    {
        // The three hosts that render PDFs set this at startup (App.xaml.cs, CliAuditHost,
        // WindowsServiceHost). A test host is none of them, so the builder throws on license
        // validation before it renders a byte. Same line the shipped hosts use.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        var pdf = AssessmentPdf.BuildPerformanceAnalysisReport(model);
        pdf.Length.Should().BeGreaterThan(1000, "the report must actually render");

        _out.WriteLine($"[{label}] scanned={string.Join(", ", model.Index.Scope.ScannedDatabases)}");
        foreach (var s in model.Index.Scope.Skipped)
            _out.WriteLine($"[{label}] skipped: {s.Database}: {s.Reason}");
        foreach (var clause in model.Index.Verdict.Clauses)
            _out.WriteLine($"[{label}] verdict: {clause}");

        if (string.IsNullOrWhiteSpace(OutDir)) return;

        Directory.CreateDirectory(OutDir);
        await File.WriteAllBytesAsync(Path.Combine(OutDir, $"report-{label}.pdf"), pdf);
        await File.WriteAllTextAsync(
            Path.Combine(OutDir, $"snapshot-{label}.json"),
            JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
    }
}
