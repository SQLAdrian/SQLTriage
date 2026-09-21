/* In the name of God, the Merciful, the Compassionate */

// ── Gate fix C4 (2026-08-05): the enumeration, kept honest by failing ────────────────────────
//
// ExecutiveHealthScore.Score holds 0 in EVERY state where nothing was measured. ComputeComposite
// needs an int to return, so the placeholder has to exist; printing it is the defect this wave
// has now removed from eight surfaces across three rounds. Each round enumerated the readers by
// hand and each round missed one, so the enumeration is written down here and the build breaks
// when it changes.
//
// ⚠ THIS IS A LINT, NOT A BOUNDARY. It matches identifiers in source text. It cannot resolve
// types, so it cannot tell ExecutiveHealthScore.Score from any other .Score, and a rewrite that
// keeps the names and changes the meaning will pass it. The runtime chokepoint is
// EstateHealthPolicy plus ExecutiveHealthScore.MeasuredScore, whose null is what actually stops
// the number being printed; this only stops a NEW raw reader arriving unnoticed. Same framing as
// ConditioningSweepMarkupTests and CategoryFilterMarkupTests, said out loud for the same reason:
// a lint sold as a boundary is worse than no lint.
//
// It reads the repo source tree rather than a copied file list, deliberately: a copy list cannot
// see a file that does not exist yet, and the whole failure mode here is the NEXT surface.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace SQLTriage.Tests;

public class ExecutiveHealthScoreReaderLintTests
{
    /// <summary>Directories the app's own health-reading code lives in.</summary>
    private static readonly string[] ScanRoots = { "Pages", "Components", "Data" };

    /// <summary>
    /// A file is a candidate when it names the type or one of the two service calls that hand one
    /// out. A new surface that reads a score has to do one of those, so it lands in the scan.
    /// </summary>
    private static readonly string[] CandidateMarkers =
    {
        "ExecutiveHealthScore", "GetAllHealthScoresAsync", "GetHealthScoreAsync",
    };

    /// <summary>
    /// Files that CARRY a health score without ever naming the type, because it reached them
    /// through a DTO. AssessmentPdf receives ExecutiveSummaryBundle.Score, which ReportBundleService
    /// fills from ExecutiveHealthScore.MeasuredScore, so the marker scan cannot see it and the
    /// 0/100 donut on a client deliverable was drawn from there. Listed by hand, and the positive
    /// control below asserts the list is actually reached.
    /// </summary>
    private static readonly string[] AlwaysScan =
    {
        "Data/Services/AssessmentPdf.cs",
        // Same case, found while extending this lint to .Trend (2026-08-08): DailySummaryBuilder
        // receives HealthScoreInput — score, severity and trend already extracted by the publisher —
        // and projects it into the emitted portal block. It names no marker, so the scan walked past
        // the file that writes the client-visible copy of every field this lint is about.
        "Data/Services/Portal/DailySummaryBuilder.cs",
    };

    // ── THE ENUMERATION ─────────────────────────────────────────────────────────────────────
    // Every sanctioned raw ".Score" read in a candidate file, as "relative/path|trimmed source
    // line". Each entry is a place where reading the raw int is CORRECT, and the note says why.
    // Anything not on this list fails; anything on it that has vanished fails too, so the list
    // cannot quietly rot into a rubber stamp.
    private static readonly string[] Sanctioned =
    {
        // ── The policy itself, and the model. These ARE the chokepoint. ──────────────────────
        // Guarded by Classify(...) == Assessed one line above: this is the mean's only input.
        @"Data/Services/EstateHealthPolicy.cs|assessed.Add(s!.Score);",
        // DimensionScore.Score (a different type), guarded by State == Measured in the same expression.
        @"Data/Services/EstateHealthPolicy.cs|=> dim != null && dim.State == DimensionState.Measured ? $""{dim.Score}/100"" : ""n/a"";",
        // ComputeComposite. The one place the raw dimension score is arithmetic, not a claim.
        @"Data/Services/ExecutiveHealthService.cs|total += d.Weight * d.Score;",

        // ── Portal ──────────────────────────────────────────────────────────────────────────
        // Guarded by EstateHealthPolicy.MayPublish(score) three lines above, which returns false
        // in every unmeasured state, so the block is omitted rather than filled.
        @"Data/Services/Portal/DailySummaryPublisher.cs|Score = score.Score,",
        // The projection of a value the publisher already decided may be emitted: h is
        // HealthScoreInput, built only on the MayPublish path, and MapHealth is null-in/null-out.
        @"Data/Services/Portal/DailySummaryBuilder.cs|Score = h.Score,",
        // The estate mean, over blocks that EXIST — and a block exists only for a server that
        // passed MayPublish, so no placeholder can enter the average.
        @"Data/Services/Portal/DailySummaryBuilder.cs|var scores = inputs.Where(i => i.HealthScore != null).Select(i => i.HealthScore!.Score).ToList();",

        // ── Deliverables ────────────────────────────────────────────────────────────────────
        // ExecutiveSummaryBundle.Score, all four inside `else` on scoreAssessed (b.ScoreAssessed).
        @"Data/Services/AssessmentPdf.cs|var scoreCol = ScoreColor(b.Score, cb);",
        @"Data/Services/AssessmentPdf.cs|new() { Value = b.Score,                 Color = scoreCol },",
        @"Data/Services/AssessmentPdf.cs|new() { Value = Math.Max(0, 100 - b.Score), Color = ""#eeeeee"" },",
        @"Data/Services/AssessmentPdf.cs|row.ConstantItem(150).AlignMiddle().Element(d => Donut(d, segs, $""{b.Score}"", ""/ 100"", scoreCol, 140));",

        // ── /cio ────────────────────────────────────────────────────────────────────────────
        // _estateHealth is CONSTRUCTED only inside `if (_estateRollup.MeanScore.HasValue)`, so
        // every read of it is already conditioned on a measured mean existing.
        @"Pages/CioDashboard.razor|<p class=""cio-narration-text"">@NarrationService.Narrate(_governanceScore, _estateHealth?.Score, _estateRollup)</p>",
        @"Pages/CioDashboard.razor|<path class=""cio-gauge-fill"" style=""stroke:@SeverityColor(_estateHealth.Severity); stroke-dashoffset:@GaugeDashOffset(_estateHealth.Score);"" d=""M14,100 A86,86 0 0 1 186,100""></path>",
        @"Pages/CioDashboard.razor|@_estateHealth.Score",
        @"Pages/CioDashboard.razor|var estateMean = _estateHealth?.Score;",
        // Paired with EhAssessed = _estateHealth != null; AssessmentPdf branches on that flag.
        @"Pages/CioDashboard.razor|EhScore      = _estateHealth?.Score ?? 0,",
        // Heatmap cell and PDF row: both inside `hasHealth`, which is `... && health.IsAssessed`.
        @"Pages/CioDashboard.razor|title=""@($""Executive Health {health.Score} — {health.Severity}"")"">@health.Score · @health.Severity</span>",
        @"Pages/CioDashboard.razor|EhScore    = hasHealth ? health!.Score : 0,",
        // Weighted-deficit ranking over dimensions already filtered by d.HasData.
        @"Pages/CioDashboard.razor|var avgScore = withData.Average(d => d.Score);",
        @"Pages/CioDashboard.razor|var lowCount = withData.Count(d => d.Score < 71); // below the Healthy threshold",

        // ── /health ─────────────────────────────────────────────────────────────────────────
        // DimensionScore reads, all three inside `measured` (dim.HasData).
        @"Pages/Health.razor|var barPct   = measured ? Math.Max(2, dim.Score) : 0;",
        @"Pages/Health.razor|var dimColor = measured ? DimColor(dim.Score) : ""var(--text-muted)"";",
        @"Pages/Health.razor|@dim.Score<span class=""health-dim-pts"">/100</span>",
    };

    [Fact]
    public void Every_raw_score_read_is_one_this_enumeration_sanctions()
    {
        var root = FindRepoRoot();
        var found = ScanForRawScoreReads(root);

        found.Should().NotBeEmpty(
            "if the scan finds nothing the whole test passes vacuously, which is how the previous "
            + "rounds of this work shipped");

        var unsanctioned = Subtract(found, Sanctioned);
        unsanctioned.Should().BeEmpty(
            "a new raw read of a health score must go through EstateHealthPolicy or "
            + "ExecutiveHealthScore.MeasuredScore. If this read really is conditioned already, add "
            + "it to Sanctioned with a note saying what conditions it. Unsanctioned:\n  "
            + string.Join("\n  ", Subtract(found, Sanctioned)));

        var vanished = Subtract(Sanctioned, found);
        vanished.Should().BeEmpty(
            "an entry in the enumeration that no longer exists means the list has drifted from the "
            + "code and is no longer evidence of anything. Remove it deliberately. Missing:\n  "
            + string.Join("\n  ", Subtract(Sanctioned, found)));
    }

    // ── THE SAME ENUMERATION FOR .Trend (SR-11, 2026-08-08) ─────────────────────────────────
    //
    // ExecutiveHealthScore.Trend is the second placeholder-carrying field on the same object, and it
    // is worse than Score in one way: HealthTrend has no value meaning "nothing was compared", so
    // GetTrendAsync returned Stable for an absent snapshot AND for a failed history read, and every
    // reader printed a stability verdict. MeasuredTrend is the conditioned reader; these hold the
    // raw ones to a written list, exactly as above.

    /// <summary>
    /// Raw <c>.Trend</c> reads that are CONDITIONED where they stand. Same contract as
    /// <see cref="Sanctioned"/>: the note says what conditions it.
    /// </summary>
    private static readonly string[] SanctionedTrendReads =
    {
        // The projection of a value ALREADY decided upstream: h.Trend here is a string on
        // HealthScoreInput, not the enum, and MapHealth is null-in/null-out.
        @"Data/Services/Portal/DailySummaryBuilder.cs|Trend = h.Trend,",
    };

    /// <summary>
    /// ⚠ NOT conditioned, and named as such rather than filed under "sanctioned" — an allow-list
    /// entry with a false justification is the failure mode this whole family of lints exists for.
    ///
    /// <para>EMPTY since 2026-08-09. Its one entry was
    /// <c>DailySummaryPublisher.cs|Trend = score.Trend.ToString()</c>: the portal daily blob
    /// published the string "Stable" for a server assessed for the first time today, because the
    /// schema field was a non-nullable three-value vocabulary and narrowing it was a cross-repo
    /// change. That change was made — the publisher now reads <c>MeasuredTrend</c> and the property
    /// is OMITTED when nothing was compared — so the read no longer exists to sanction. The array
    /// stays as the named home for any future residual; leaving one here is a decision to publish
    /// an unconditioned claim, and it should read like one.</para>
    /// </summary>
    private static readonly string[] UnconditionedByContract = Array.Empty<string>();

    /// <summary>
    /// The positive half of the closure above: the publisher does not merely avoid the raw field,
    /// it reads the conditioned one. A lint that only checks for absence passes just as well if the
    /// line is deleted altogether.
    /// </summary>
    [Fact]
    public void The_portal_daily_blob_supplies_its_trend_from_the_conditioned_reader()
    {
        var root = FindRepoRoot();
        var publisher = File.ReadAllText(
            Path.Combine(root, "Data", "Services", "Portal", "DailySummaryPublisher.cs"));

        publisher.Should().Contain("Trend = score.MeasuredTrend?.ToString(),",
            "the daily blob's trend must come from MeasuredTrend, which is null unless a real "
            + "yesterday was compared; the raw Trend field holds Stable when nothing was");
        publisher.Should().NotContain("score.Trend.ToString()",
            "the raw direction reaching the blob is the SR-11 over-claim in client-facing form");
    }

    [Fact]
    public void Every_raw_trend_read_is_either_conditioned_or_a_named_residual()
    {
        var root = FindRepoRoot();
        var found = ScanForRawReads(root, "Trend");

        found.Should().NotBeEmpty(
            "if the scan finds nothing it passes vacuously — the same way the score rounds shipped");

        var known = SanctionedTrendReads.Concat(UnconditionedByContract).ToList();

        Subtract(found, known).Should().BeEmpty(
            "a new raw read of ExecutiveHealthScore.Trend must go through MeasuredTrend or "
            + "EstateHealthPolicy.TrendValue/TrendBasis. The raw field holds Stable when nothing was "
            + "compared. Unsanctioned:\n  " + string.Join("\n  ", Subtract(found, known)));

        Subtract(known, found).Should().BeEmpty(
            "an entry that no longer exists means the list has drifted from the code. Missing:\n  "
            + string.Join("\n  ", Subtract(known, found)));
    }

    [Fact]
    public void The_three_screens_this_round_conditioned_hold_no_raw_trend_read_at_all()
    {
        var root = FindRepoRoot();
        var found = ScanForRawReads(root, "Trend");

        foreach (var file in new[]
        {
            "Pages/Health.razor",
            "Pages/ServerComparison.razor",
            "Components/Shared/HealthBadge.razor",
        })
            found.Where(f => f.StartsWith(file + "|", StringComparison.Ordinal)).Should().BeEmpty(
                $"{file} reads MeasuredTrend or the policy and nothing else");
    }

    [Fact]
    public void The_scan_actually_reaches_the_files_it_claims_to()
    {
        // A positive control. If the candidate marker or the roots ever stop matching, the lint
        // goes quiet rather than failing, and a quiet lint is indistinguishable from a clean one.
        var root = FindRepoRoot();
        var candidates = CandidateFiles(root).Select(f => Rel(root, f)).ToList();

        candidates.Should().Contain("Data/Services/EstateHealthPolicy.cs");
        candidates.Should().Contain("Pages/CioDashboard.razor");
        candidates.Should().Contain("Components/Shared/HealthBadge.razor");
        candidates.Should().Contain("Pages/Servers.razor");
        candidates.Should().Contain("Pages/ServerComparison.razor");
        // The DTO carrier, which names none of the markers and still draws the donut.
        candidates.Should().Contain("Data/Services/AssessmentPdf.cs");
    }

    [Fact]
    public void The_surfaces_this_wave_conditioned_hold_no_raw_read_at_all()
    {
        var root = FindRepoRoot();
        var found = ScanForRawScoreReads(root);

        foreach (var file in new[]
        {
            "Components/Shared/HealthBadge.razor",
            "Pages/Servers.razor",
            "Pages/ServerComparison.razor",
            "Pages/DbaDashboard.razor",
        })
            found.Where(f => f.StartsWith(file + "|", StringComparison.Ordinal)).Should().BeEmpty(
                $"{file} reads MeasuredScore or the policy and nothing else");
    }

    // ── Mechanics ───────────────────────────────────────────────────────────────────────────

    private static List<string> Subtract(IEnumerable<string> a, IEnumerable<string> b)
    {
        var remaining = new List<string>(b);
        var result = new List<string>();
        foreach (var item in a)
        {
            if (remaining.Remove(item)) continue;
            result.Add(item);
        }
        return result;
    }

    private static IEnumerable<string> CandidateFiles(string root)
        => ScanRoots
            .Select(r => Path.Combine(root, r))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => AlwaysScan.Contains(Rel(root, f), StringComparer.Ordinal)
                     || CandidateMarkers.Any(m => File.ReadAllText(f).Contains(m, StringComparison.Ordinal)))
            .OrderBy(f => f, StringComparer.Ordinal);

    private static List<string> ScanForRawScoreReads(string root) => ScanForRawReads(root, "Score");

    /// <summary>
    /// Every raw ".&lt;member&gt;" read in a candidate file, minus comment lines. Comments are skipped
    /// because a lint that trips on a code comment gets silenced badly later; that already happened
    /// once on this branch (174466b, the IndexAnalysis note). The cost is that a raw read hidden
    /// inside a commented-out block is not seen, which is fine: it does not render.
    ///
    /// <para><paramref name="member"/> is "Score" or "Trend". Both are fields on
    /// ExecutiveHealthScore that hold a placeholder in the states where nothing was measured, and
    /// both have a conditioned reader prefixed <c>Measured</c> — which is what the lookbehind
    /// excludes, and why the same mechanism serves both.</para>
    /// </summary>
    private static List<string> ScanForRawReads(string root, string member)
    {
        var hits = new List<string>();
        foreach (var file in CandidateFiles(root))
        {
            var rel = Rel(root, file);
            var inRazorComment = false;
            var inBlockComment = false;

            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();

                if (inRazorComment) { if (line.Contains("*@")) inRazorComment = false; continue; }
                if (inBlockComment) { if (line.Contains("*/")) inBlockComment = false; continue; }
                if (line.StartsWith("@*", StringComparison.Ordinal))
                {
                    if (!line.Contains("*@")) inRazorComment = true;
                    continue;
                }
                if (line.StartsWith("/*", StringComparison.Ordinal))
                {
                    if (!line.Contains("*/")) inBlockComment = true;
                    continue;
                }
                if (line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("*", StringComparison.Ordinal)
                    || line.StartsWith("<!--", StringComparison.Ordinal)) continue;

                if (!line.Contains("." + member, StringComparison.Ordinal)) continue;
                // "MeasuredScore"/"MeasuredTrend" and "ScoreToSeverity"/"ScoreColor" style
                // identifiers are not reads of the raw member; ".Scores" is a different member
                // entirely, and ".TrendState" is the discriminator rather than the claim (the \b
                // rejects it, since the next character is a word character).
                if (!System.Text.RegularExpressions.Regex.IsMatch(
                        line, $@"(?<!Measured)\.{member}\b(?!s)"))
                    continue;

                hits.Add($"{rel}|{line}");
            }
        }
        return hits;
    }

    private static string Rel(string root, string file)
        => Path.GetRelativePath(root, file).Replace('\\', '/');

    /// <summary>
    /// Walks up from the test binary to the repo root. Fails LOUDLY when it cannot find one: a
    /// lint that skips when it cannot read the tree is a lint that reports success for the wrong
    /// reason, which is the exact failure this file exists to prevent.
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Data", "Services", "EstateHealthPolicy.cs"))
                && Directory.Exists(Path.Combine(dir.FullName, "Pages")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repo root above " + AppContext.BaseDirectory
            + ". This lint reads the source tree on purpose (a copied file list cannot see a file "
            + "that does not exist yet). If the suite is being run somewhere without sources, this "
            + "test must be seen to FAIL rather than silently pass.");
    }
}
