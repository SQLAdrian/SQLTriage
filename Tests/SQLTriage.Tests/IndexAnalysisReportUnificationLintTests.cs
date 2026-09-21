/* In the name of God, the Merciful, the Compassionate */

// ── Index Analysis report-unification lint, 2026-08-14 ───────────────────────────────────────
//
// THIS FILE REPLACES IndexAnalysisSqlDriftLintTests, AND THE REPLACEMENT IS THE POINT.
//
// That lint existed because the three index DMV queries lived TWICE in this tree: in
// IndexAnalysisService (the live /index-analysis page) and byte-for-byte again in
// PerformanceReportComposer (the full-private client report). It held the copies in step by
// comparing source text, and it was written on an explicit disclosure that unifying them was "not
// this lane's change".
//
// It has been. Adrian ruled on 2026-08-13 that the client report follows the page to top-20-by-IO
// scope, and the report now CALLS IndexAnalysisService instead of holding copies. So there is no
// second copy left to hold in step, and a lint that compares two copies would pass by finding
// nothing — the failure mode its own plumbing warned about. The tests are therefore INVERTED: they
// no longer assert the copies match, they assert THE COPIES ARE GONE and the single call site is
// still in place.
//
// WHY A LINT AT ALL, NOW THAT THE CODE IS UNIFIED. Because the pressure that created the copies is
// still there. PerformanceReportComposer is Compile-Removed from both shipped profiles
// (buildprofile.targets), so nothing in the default or community suite can BIND it, and a future
// edit that pastes a convenient copy of the SQL back into it would compile privately, ship to a
// client, and be exercised by no CI run anywhere. A source-text lint is the only instrument that
// reaches that file from a suite that actually runs. It is the same reason the previous lint read
// source text rather than binding the type, kept for the same reason.
//
// ⚠ THIS IS A LINT, NOT A BOUNDARY. It reads source TEXT. It cannot see a semantic change, it does
// not execute the composer, and it says nothing about whether the rendered PDF is correct — the
// composer is provable only by a live private-build run. Same framing as
// ExecutiveHealthScoreReaderLintTests, said out loud for the same reason: a lint sold as a boundary
// is worse than no lint.

using System;
using System.IO;
using FluentAssertions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public class IndexAnalysisReportUnificationLintTests
{
    private const string ComposerPath = "Data/Services/PerformanceReport/PerformanceReportComposer.cs";
    private const string RendererPath = "Data/Services/PerformanceReport/AssessmentPdf.PerformanceReport.cs";
    private const string ModelPath = "Data/Services/PerformanceReport/PerformanceReportModel.cs";

    /// <summary>
    /// The DMV names, not the constant names. A copy pasted back under a different constant name
    /// would slip a name check; it cannot slip the DMVs it has to read.
    /// </summary>
    public static TheoryData<string> IndexDmvs => new()
    {
        "dm_db_missing_index_details",
        "dm_db_index_usage_stats",
        "dm_db_index_physical_stats",
    };

    [Theory]
    [MemberData(nameof(IndexDmvs))]
    public void The_report_holds_no_copy_of_the_index_sql(string dmv)
        => Source(ComposerPath).Should().NotContain(dmv,
            "the report's index sections come from IndexAnalysisService now; a query re-pasted here "
            + "would compile only under -p:SQLTriagePrivate=true, ship to a client, and be run by no "
            + "CI job in either profile");

    /// <summary>
    /// ⚠ PINNED TO A CALL, NOT TO TWO NAMES, 2026-08-14 (gate finding, proved by mutation). This
    /// asserted <c>Contain("IndexAnalysisService")</c> and <c>Contain("GetAnalysisAsync")</c>. Both
    /// names appear in the composer's own comment block, so deleting the call entirely — replacing
    /// the analysis with an empty result, the report reading no index data at all — left the lint
    /// 7/7 green. A bare type name in a file that documents itself is not an assertion about
    /// routing; the receiver and the parens are.
    /// </summary>
    [Fact]
    public void The_report_reads_its_index_sections_through_the_service()
    {
        var composer = Source(ComposerPath);
        composer.Should().Contain("_index.GetAnalysisAsync(",
            "the composer must CALL the page's service rather than merely mention it: the type "
            + "name alone survives in prose after the call is gone, which is how a report that "
            + "reads no index data at all can still satisfy a name check");
        composer.Should().Contain("IndexAnalysisService _index",
            "and hold it as the injected dependency, so the call above cannot be to a local stand-in");
    }

    /// <summary>
    /// The scope claim the report makes rests on the ranking pass existing and on the sample size
    /// it advertises. Both are asserted against the BOUND type, so this half is a real check rather
    /// than a text match.
    /// </summary>
    [Fact]
    public void The_service_still_ranks_by_the_house_io_metric()
    {
        IndexAnalysisService.DatabaseRankingSql.Should().Contain("sys.dm_io_virtual_file_stats",
            "the top-20 scope both surfaces claim comes from a ranking pass over the house IO metric");
        IndexAnalysisService.TopDatabasesByIo.Should().Be(20);
    }

    /// <summary>
    /// One sentence source for the screen and the client PDF. The renderer must compose its scope
    /// wording from IndexAnalysisScopeNarrative — the type the page renders — because two sets of
    /// words about one scan is exactly how the previous divergence survived unnoticed for months.
    ///
    /// <para>⚠ PINNED TO THE CALLS, 2026-08-14. Three of the six occurrences of the type name in
    /// the renderer are comment lines, so removing every call would have satisfied a bare-name
    /// check — the same hole proved by mutation on the composer assertion above.</para>
    /// </summary>
    [Fact]
    public void The_report_states_its_scope_in_the_page_s_own_words()
    {
        var renderer = Source(RendererPath);
        renderer.Should().Contain("IndexAnalysisScopeNarrative.For(",
            "the PDF's scope, metric and skip sentences must be BUILT from the page's narrative, "
            + "not merely credited to it in a comment");
        renderer.Should().Contain("IndexAnalysisScopeNarrative.MissingSentence",
            "the missing-index read has a different scope from the two per-database tables, and "
            + "the sentence saying so is the page's too");
    }

    /// <summary>
    /// A size nobody measured must not reach a client as 0.00 MB. The service made SizeMB nullable
    /// for exactly this reason; the snapshot model has to carry the null through rather than
    /// flattening it on the way in.
    /// </summary>
    [Fact]
    public void The_snapshot_carries_an_unmeasured_index_size_as_null()
    {
        var model = Source(ModelPath);
        model.Should().Contain("public double? SizeMB",
            "the unused-index size comes through a LEFT JOIN and is genuinely absent on a miss; a "
            + "non-nullable double turns that absence into a measurement claim in a deliverable");
        // ⚠ THE REASON WAS WRONG, 2026-08-14 (gate finding). It read "or one table renders 0.00 for
        // unmeasured while the other renders the truth", describing a rendering that does not
        // exist: the fragmented table's columns are Database, Table, Index, Frag % and Action, so
        // PerfSizeLabel is only ever applied to the unused table. FragIndexRow.SizeMB is
        // SNAPSHOT-ONLY — and that is the real reason it must stay nullable, because the snapshot
        // is the diffable artifact a later engagement compares against.
        model.Should().NotContain("public double SizeMB",
            "no row type may keep the non-nullable form: FragIndexRow.SizeMB reaches no column in "
            + "the PDF but it does reach the JSON snapshot, where a 0.00 standing in for an absent "
            + "LEFT JOIN row becomes a measurement a future before/after diff would trust");
    }

    /// <summary>
    /// THE SAME RULE FOR THE IMPACT COLUMNS, 2026-08-14. Both are declared nullable by the server
    /// (sys.dm_exec_describe_first_result_set over MissingIndexSql, measured on 14.0.2120.1 and
    /// 16.0.4262.2). Until this lane the service mapped them through a helper that substituted 0m,
    /// so an unmeasured suggestion reached this report as an "Impact" cell reading 0 — which a
    /// client reads as "the optimizer expects nothing from this index", the opposite of an absent
    /// reading — and it reached the JSON snapshot as a zero a before/after diff would trust.
    /// </summary>
    [Fact]
    public void The_snapshot_carries_an_unmeasured_impact_figure_as_null()
    {
        var model = Source(ModelPath);

        model.Should().Contain("public double? AvgImpact",
            "the optimizer's impact figures are nullable at the source and must stay nullable here");
        model.Should().Contain("public double? ImpactScore");
        model.Should().NotContain("public double AvgImpact",
            "a non-nullable double turns an absent reading into 0.00 in a client deliverable");
        model.Should().NotContain("public double ImpactScore");

        // ⚠ THE COMPARISON, not just the type. `null >= threshold` is false in C#, so the material
        // filter is right by accident; what is NOT automatic is counting what could not be judged.
        // A verdict that silently drops the unjudged rows is the house defect class.
        model.Should().Contain("m.ImpactScore.HasValue",
            "the material filter must decide the null case explicitly, because the count of the "
            + "ones it could not judge has to leave the method");
        model.Should().Contain("UnmeasuredMissingImpactCount",
            "an unmeasured suggestion is neither material nor cleared, and the verdict carries "
            + "that number so both branches can say so");
    }

    /// <summary>
    /// ⚠ THE CLEAR BRANCH IS THE ONE THAT MATTERS, and it is in the RENDERER, not the model. A
    /// floor sentence that only appears inside a fired clause leaves "No material index imbalance
    /// …" unguarded — and that sentence is on page 1, the page a client forwards.
    ///
    /// <para>Both the executive line and the section headline read
    /// <c>IndexVerdict.UnmeasuredImpactCaveat</c>, so this pins TWO occurrences. The composed
    /// property is asserted for its wording by PerformanceReportScopeLiveHarness, which is the only
    /// instrument that can bind the type at all.</para>
    /// </summary>
    [Fact]
    public void The_report_s_clear_index_verdict_names_what_it_could_not_judge()
    {
        var renderer = Source(RendererPath);

        System.Text.RegularExpressions.Regex
            .Matches(renderer, @"UnmeasuredImpactCaveat")
            .Count.Should().BeGreaterThanOrEqualTo(2,
                "the executive summary line AND the section's synthesis line both print a CLEAR "
                + "index verdict, so both have to name the suggestions that carried no score");

        renderer.Should().NotContain("r.ImpactScore.ToString(",
            "a nullable impact score printed with a raw ToString either throws or renders a zero "
            + "the run never measured");
        renderer.Should().Contain("IndexAnalysisRendering.Number(r.ImpactScore",
            "the absent case goes through the shared label, so the PDF and the screen say the "
            + "same word for the same absence");
    }

    /// <summary>
    /// A READ THAT FAILED IS NOT AN EMPTY SECTION, 2026-08-14. The ranking read used to throw, and
    /// the throw landed in ComposeAsync's live-collection catch, which marks the WHOLE instance
    /// Succeeded=false — so one fifteen-second timeout also cost the client the disk section, the
    /// hotspots, the maintenance advice and the capacity page. The service degrades now, and the
    /// report has to render which read it lost or the section reads as a clean one.
    /// </summary>
    [Fact]
    public void The_report_renders_the_read_it_lost_rather_than_an_empty_section()
    {
        Source(ComposerPath).Should().Contain("RankingFailure = analysis.RankingFailure",
            "the composer must carry the sentence onto the snapshot, which is the diffable "
            + "artifact a later engagement compares against");
        Source(ComposerPath).Should().Contain("MissingReadFailure = analysis.MissingReadFailure");

        Source(ModelPath).Should().Contain("public string? RankingFailure",
            "and the snapshot must hold it, so a diff of two runs can see that one read nothing");
        Source(ModelPath).Should().Contain("public string? MissingReadFailure");

        var renderer = Source(RendererPath);
        renderer.Should().Contain("PerfIndexReadFailures(m)",
            "the section must render the failure. An empty index section that does not say why is "
            + "the worst outcome available here: it reads as a clean one");

        // The empty missing-index line must stop asserting a finding when the read never returned.
        renderer.Should().Contain("ix.Scope.MissingReadFailure",
            "'No missing-index suggestion was recorded at export' is a claim about the optimizer, "
            + "and it rendered identically over a read that threw");

        // ⚠⚠ THE SECTION WAS HONEST AND PAGE 1 WAS NOT, 2026-08-14 (gate). The assertion above is
        // satisfied by ONE call site, and the exec summary was not it: PerfIndexReadFailures was
        // consulted on one of the exec line's three branches, so a lost missing-index read rendered
        // an executive page byte-identical to a healthy run, and a lost ranking read was shadowed
        // outright whenever the verdict fired. Both were read off a rendered PDF.
        //
        // The disclosure is now joined ONCE, outside the branches, in a single named function that
        // the exec summary delegates to whole — a branch cannot forget to carry what it never
        // chose. This pins that structure, because the wording is pinned where the type can be
        // bound (PerformanceReportScopeLiveHarness) and this suite is the one CI actually runs.
        renderer.Should().Contain("PerfExecIndexLine(m, cb)",
            "the executive summary must not compose the Indexing line inline again; the branch "
            + "that overclaimed was invisible to every test because nothing could name it");
        renderer.Should().Contain("failures.Append(body)",
            "the lost reads are prepended to whichever verdict the run earned, outside the "
            + "branch selection, so no future branch can be added that silently drops them");
    }

    /// <summary>
    /// THE SCHEMA REACHES THE SNAPSHOT, 2026-08-14. The index queries projected OBJECT_NAME alone,
    /// which returns a BARE table name — so this report named tables ambiguously and the page's
    /// copy scripts hardcoded <c>[dbo]</c>. Two tables of the same name in different schemas are
    /// two objects, and the snapshot is the diffable artifact a later engagement compares against:
    /// without the schema, a before/after diff cannot tell them apart.
    /// </summary>
    [Fact]
    public void The_snapshot_and_the_report_name_the_schema_each_row_came_from()
    {
        var model = Source(ModelPath);
        System.Text.RegularExpressions.Regex
            .Matches(model, @"public string Schema \{ get; set; \}")
            .Count.Should().Be(3,
                "all three index row types carry it: missing, unused and fragmented");

        Source(ComposerPath).Should().Contain("Schema = r.Schema,",
            "the composer copies it explicitly, so a new column on either side is a compile error "
            + "rather than a silently absent one");

        Source(RendererPath).Should().Contain("IndexAnalysisRendering.QualifiedTable(r.Schema, r.Table)",
            "and the rendered tables name schema.table through the same helper the screen uses");
    }

    /// <summary>
    /// ONE WORD FOR ONE MEANING. "not measured" used to be a literal in the renderer AND a literal
    /// on Pages/IndexAnalysis.razor; they agreed only because one author wrote both. The word is a
    /// product decision, so it lives on the bound shared type and both surfaces read it.
    /// </summary>
    [Fact]
    public void Both_surfaces_take_the_absent_value_wording_from_one_place()
    {
        IndexAnalysisRendering.Unmeasured.Should().Be("not measured");

        var renderer = Source(RendererPath);
        renderer.Should().Contain("IndexAnalysisRendering.Number(sizeMb",
            "PerfSizeLabel must delegate the word, keeping only the per-surface number format");
        renderer.Should().NotContain("\"not measured\"",
            "the renderer must not carry its own copy of the word: a second literal is how the "
            + "PDF and the screen came to be able to disagree about one absence");
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────
    //
    // Reads the source tree on purpose: these types are absent from every assembly either suite
    // builds, so binding them would make this test impossible to run in exactly the two
    // configurations that are run.

    private static string Source(string repoRelativePath)
        => File.ReadAllText(Path.Combine(FindRepoRoot(), repoRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Data", "Services", "IndexAnalysisService.cs"))
                && Directory.Exists(Path.Combine(dir.FullName, "Pages")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repo root above " + AppContext.BaseDirectory
            + ". This lint reads the source tree on purpose (the report tree is Compile-Removed from "
            + "both built profiles). If the suite is being run somewhere without sources, this test "
            + "must be seen to FAIL rather than silently pass.");
    }
}
