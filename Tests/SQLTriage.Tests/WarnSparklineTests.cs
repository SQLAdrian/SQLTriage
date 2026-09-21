/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Components.Shared;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// A1 / A1b (2026-07-20) — the per-check trend page's two chart defects.
    ///
    /// <para><b>A1.</b> <c>Pages/CheckTrend.razor</c> built its dot chart as
    /// <c>List&lt;bool&gt;</c> off <c>CheckHistoryPoint.Passed</c> and
    /// <c>Components/Shared/Sparkline.razor</c> drew it two-state (green/red). WARN rides
    /// Passed=true, so a run that could not assess the target drew a GREEN dot — on the one page a
    /// DBA opens to say "it has been green for thirty runs". Nothing in Tests/ referenced Flags,
    /// Sparkline or CheckTrend, so nothing caught it. This is that missing coverage.</para>
    ///
    /// <para><b>A1b.</b> A day whose every run was unassessable had no pass rate, and charted 0 —
    /// a cliff to the floor that reads as a total-failure day. The old comment defended it as
    /// "charts as 0 rather than inventing 100"; but inventing a cliff is not more honest than
    /// inventing a peak. The honest shape for "no value" is a GAP.</para>
    /// </summary>
    public class WarnSparklineTests : IDisposable
    {
        private readonly string _tempDir;

        public WarnSparklineTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "warn-sparkline-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(_tempDir, recursive: true);
            }
            catch { /* test cleanup */ }
        }

        private GovernanceHistoryService NewService()
            => new(NullLogger<GovernanceHistoryService>.Instance, retentionDays: 365, dbDir: _tempDir);

        // ── A1: the gate's probe, through the shipped write/read path ─────────────────────────

        [Fact]
        public void Sparkline_DrawsAWarnRunAsNeitherPassNorFail()
        {
            // The reproduction, end to end through the SHIPPED RecordCheckResult / GetCheckHistory
            // rather than over hand-built points — the defect lived in what the persisted verdict
            // column means once rehydrated, so a test that skips persistence would skip the defect.
            //
            // The gate's probe on the old code:
            //   SparklineFills = [GREEN, GREEN, GREEN, GREEN, RED]   <- four green dots
            //   warnAwareTruth = [GREEN, GREEN, WARN,  WARN,  RED]   <- two genuinely passing runs
            using var svc = NewService();

            svc.RecordCheckResult("SRV1", Result(passed: true,  verdict: "PASS"));
            svc.RecordCheckResult("SRV1", Result(passed: true,  verdict: "PASS"));
            svc.RecordCheckResult("SRV1", Result(passed: true,  verdict: "WARN"));
            svc.RecordCheckResult("SRV1", Result(passed: true,  verdict: "WARN"));
            svc.RecordCheckResult("SRV1", Result(passed: false, verdict: "FAIL"));

            var history = svc.GetCheckHistory("SQLT-TREND-1", days: 90);
            history.Should().HaveCount(5, "all five runs must rehydrate, or this proves nothing");

            // The shipped mapping the page now uses.
            var states = history.Select(SparkStates.For).ToList();

            // Asserted as a multiset: five rows written in the same second can tie on recorded_at,
            // so the ORDER BY is not a guarantee worth resting an assertion on. The defect is a
            // COUNT defect anyway — how many dots claim "this check passed".
            states.Count(s => s == SparkState.Pass).Should().Be(2,
                "only the two genuine PASS runs may draw a passing dot. Before A1 this was 4: the " +
                "two WARN runs rode Passed=true straight into the green arm");
            states.Count(s => s == SparkState.Unassessed).Should().Be(2,
                "the two runs that could not fully assess must draw the third state");
            states.Count(s => s == SparkState.Fail).Should().Be(1);

            // And the three states must actually be drawn differently, or the enum is decoration.
            var fills = states.Distinct().Select(Sparkline.FillFor).ToList();
            fills.Should().OnlyHaveUniqueItems("three states that render the same colour are one state");
        }

        [Fact]
        public void SparkStates_PutsTheWarnArmAheadOfThePassedArm()
        {
            // A WARN carries Passed=true. If the Passed arm were tested first the third state
            // would be unreachable — the exact ordering bug this whole slice keeps re-finding.
            SparkStates.For(Point(passed: true, verdict: "WARN")).Should().Be(SparkState.Unassessed);
            SparkStates.For(Point(passed: true, verdict: "warn")).Should().Be(SparkState.Unassessed,
                "the verdict comparison is case-insensitive");

            SparkStates.For(Point(passed: true, verdict: "PASS")).Should().Be(SparkState.Pass);
            SparkStates.For(Point(passed: false, verdict: "FAIL")).Should().Be(SparkState.Fail);

            // Null verdict = a row written before check_results.verdict existed. NOT a WARN — it
            // keeps its pre-existing pass/fail reading rather than being retro-labelled.
            SparkStates.For(Point(passed: true, verdict: null)).Should().Be(SparkState.Pass);
            SparkStates.For(Point(passed: false, verdict: null)).Should().Be(SparkState.Fail);
        }

        [Fact]
        public void TheThirdDotColour_IsNeitherThePassNorTheFailColour()
        {
            // "Legible against green/red at 2.5px radius" was a requirement, so it is asserted.
            // --orange was rejected on evidence: wwwroot/css/Accessibility.css remaps --red to
            // #d97706 and --orange to #f59e0b in colour-blind mode, two ambers ~7deg apart.
            var unassessed = Sparkline.FillFor(SparkState.Unassessed);

            unassessed.Should().NotBe(Sparkline.FillFor(SparkState.Pass));
            unassessed.Should().NotBe(Sparkline.FillFor(SparkState.Fail));
            unassessed.Should().NotContain("--orange",
                "--orange and --red both remap to an amber in colour-blind mode (Accessibility.css:24-25), " +
                "so at a 5px dot the third state would read as a failure to the readers who can least afford it");
            unassessed.Should().Be("var(--text-muted)",
                "--text-muted is not remapped by Accessibility.css, so it separates from both by " +
                "saturation rather than hue and holds in either palette");
        }

        // ── A1b: an unassessable day is a GAP, not a zero ──────────────────────────────────────

        [Fact]
        public void AnUnassessableDay_BreaksTheLine_InsteadOfPlottingZero()
        {
            // Exercises the real shipped segment builder. Day 3 has no rate at all.
            var segments = Sparkline.BuildSegments(
                new List<double?> { 90, 95, null, 100, 92 }, width: 100, height: 20);

            segments.Should().HaveCount(2, "the null must SPLIT the line, not be drawn through");
            segments.All(s => !s.IsSinglePoint).Should().BeTrue("both runs here have two or more points");

            // FOUR points are plotted, not five. The unassessable day contributes no coordinate at
            // all — that is what makes it a gap rather than a value.
            //
            // (An earlier version of this test asserted "no y may sit on the floor", reasoning that
            // a charted 0 would land there. That was wrong and this test caught it: the series is
            // normalised to its own min/max, so the floor is the SMALLEST PLOTTED VALUE (90), not
            // 0%. Absence of a coordinate is the property that actually distinguishes a gap.)
            var xs = segments
                .SelectMany(s => s.Points.Split(' '))
                .Select(p => double.Parse(p.Split(',')[0]))
                .ToList();

            xs.Should().HaveCount(4, "five samples, one of them unmeasurable");

            // X(i) = i*(100-4)/4 + 2  ->  2, 26, 50, 74, 98. Index 2 (x=50) is the missing day.
            xs.Should().BeEquivalentTo(new[] { 2.0, 26.0, 74.0, 98.0 });
            xs.Should().NotContain(50.0,
                "x=50 is the unassessable day's slot; a coordinate there would mean a rate was " +
                "plotted for a day on which nothing was measured");

            // And the slot is HELD: the surrounding days must not slide together, or the reader
            // cannot see that a day is missing at all.
            var firstRunLastX = double.Parse(segments[0].Points.Split(' ').Last().Split(',')[0]);
            var secondRunFirstX = double.Parse(segments[1].Points.Split(' ').First().Split(',')[0]);
            (secondRunFirstX - firstRunLastX).Should().BeApproximately(48.0, 0.5,
                "two index steps of 24 — the missing day keeps its horizontal space");
        }

        [Fact]
        public void ALoneSurvivingDayBetweenTwoGaps_IsStillDrawn()
        {
            // A one-point polyline renders nothing at all, so an isolated value would vanish —
            // replacing the false cliff with a silent disappearance.
            var segments = Sparkline.BuildSegments(
                new List<double?> { null, 75, null }, width: 100, height: 20);
            segments.Should().ContainSingle();
            segments[0].IsSinglePoint.Should().BeTrue("an isolated value must be drawn as a dot");
        }

        [Fact]
        public void ADayWithNoValuesAtAll_DrawsNothingRatherThanAFlatFloor()
        {
            Sparkline.BuildSegments(new List<double?> { null, null }, width: 100, height: 20)
                .Should().BeEmpty();
        }

        // ── the page must actually be wired to them ───────────────────────────────────────────

        [Fact]
        public void CheckTrendMarkup_FeedsTheSparklineStates_NotARawPassedBool()
        {
            // The helpers above are exercised directly, but WHICH helper the page calls exists only
            // in the .razor. Asserted against the real shipped markup (copied to the test output by
            // SQLTriage.Tests.csproj), because the previous round fixed three sites in this file,
            // left :211 raw, and then declared the file "already guarded".
            //
            // NOT claimed by this test: that the chart LOOKS right. No pixels were rendered.
            //
            // Comments are stripped first, using the raw-.Passed guard's own stripper. The fix's
            // comment quotes the old code verbatim ("was `.Select(p => p.Passed)`") so that the
            // next reader knows what was wrong — and a naive text search would have matched that
            // comment and reported the defect as still present. Assert on the CODE.
            var markup = RawPassedScan.Normalize(
                string.Join("\n", RawPassedScan.StripComments(File.ReadAllLines(MarkupPath("CheckTrend.razor")), isRazor: true)));

            markup.Should().Contain("Select(SparkStates.For)",
                "the dot series must be built through the classified mapping");
            markup.Should().NotContain("Select(p => p.Passed)",
                "the raw two-state projection is the defect and must not survive anywhere in the file");
            markup.Should().NotContain("PassFlags",
                "the bool-based Sparkline parameter was removed outright so it cannot be reintroduced");
            markup.Should().Contain("States=\"grp.States\"");

            markup.Should().Contain("List<double?>",
                "A1b: the daily series must be able to REPRESENT a day with no rate");
            markup.Should().Contain("(double?)null",
                "an all-unassessable day must yield null, not a number");
            markup.Should().NotContain("scorable.Count == 0 ? 0 :",
                "charting 0 for an unmeasured day is the A1b defect");
        }

        [Fact]
        public void NoPassRateOnThePage_FallsBackToZeroWhenNothingCouldBeAssessed()
        {
            // FOUND BY THE A1b TEST ABOVE, not by the sweep. Two more sites had the same shape as
            // the daily line and neither was in the brief:
            //   OverallPassRate  -> `scorable.Count == 0 ? 0`  — the page's biggest tile
            //   per-server rate  -> `grp.Total > 0 ? ... : 0`  — one row per server
            // Both rendered a red 0% for a check nothing could be measured on, which reads as
            // "failed every run" rather than "never assessed". Same lie, louder surface.
            var markup = RawPassedScan.Normalize(
                string.Join("\n", RawPassedScan.StripComments(File.ReadAllLines(MarkupPath("CheckTrend.razor")), isRazor: true)));

            markup.Should().NotContain("scorable.Count == 0 ? 0",
                "the overall pass rate must be null when nothing was assessable, not 0");
            markup.Should().NotContain("grp.Total > 0 ? (grp.Passed * 100.0 / grp.Total) : 0",
                "the per-server rate must be null when no run on that server was assessable");

            markup.Should().Contain("private double? OverallPassRate",
                "the type itself must admit that there may be no rate");
            markup.Should().Contain("RateColour",
                "banding must run through a helper with an explicit null case, or a missing rate " +
                "falls through the >= comparisons into the red arm");

            // And the null case must be muted, not red — red is a verdict.
            RateColourIsMutedForNull(markup);
        }

        private static void RateColourIsMutedForNull(string markup)
        {
            markup.Should().MatchRegex(
                @"rate is null \? ""var\(--text-muted\)""",
                "a missing rate must take the muted token; painting it red states a failure that " +
                "was never observed");
        }

        [Fact]
        public void CheckTrendMarkup_ExplainsTheThirdDotAndTheGap()
        {
            // A third dot state nobody labels is a question, and an unexplained break in a line
            // reads as a rendering glitch. Both are their own kind of dishonesty.
            var markup = ReadMarkup("CheckTrend.razor");

            markup.Should().Contain("check-trend-spark-dot unassessed",
                "the legend must carry a swatch for the third state");
            markup.Should().Contain("could not fully assess",
                "and must say in words what that dot means");
            // Asserted against the RENDER SITE, not the identifier. A bare Contain("_unratedDays")
            // was satisfied by the field declaration and the assignment, so mutation M9 — which
            // replaced the rendered value with a literal 0 — SURVIVED it. That is precisely this
            // repo's dominant defect class (an assertion nobody exercised), caught here by
            // mutation rather than by review.
            markup.Should().Contain("@_unratedDays of these days have",
                "the count must be RENDERED into the caption, not merely computed — the reader " +
                "needs to know how many days have no rate rather than guessing at the break");
            markup.Should().Contain("@if (_unratedDays > 0)",
                "and the caption must be driven by the real count, so it appears exactly when " +
                "there IS a gap and stays silent when there is not");
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────

        private static CheckResult Result(bool passed, string verdict) => new()
        {
            CheckId = "SQLT-TREND-1",
            CheckName = "trend probe",
            Category = "Security",
            Severity = "High",
            InstanceName = "SRV1",
            Passed = passed,
            Verdict = verdict,
            Message = verdict == "WARN" ? "Could not read 3 of 14 databases (insufficient permissions)." : "",
        };

        private static CheckHistoryPoint Point(bool passed, string? verdict) => new()
        {
            Server = "SRV1",
            RecordedAt = "2026-07-20 09:00:00",
            Passed = passed,
            Verdict = verdict,
        };

        private static string MarkupPath(string name)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Markup", name);
            File.Exists(path).Should().BeTrue(
                $"{name} is copied to the test output by SQLTriage.Tests.csproj; without it every " +
                "assertion below would vacuously pass");
            return path;
        }

        private static string ReadMarkup(string name) => File.ReadAllText(MarkupPath(name));
    }
}
