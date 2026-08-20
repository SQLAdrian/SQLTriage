/* In the name of God, the Merciful, the Compassionate */

// The per-run category filter's wiring lives in Pages/QuickCheck.razor and cannot be reached from
// C#: which delegate the page hands the executor, whether the results summary renders the measured
// sentence or a literal beside it, and whether the chip state leaks into a settings file are all
// facts about the MARKUP. They are asserted against the real shipped .razor (copied to the test
// output by SQLTriage.Tests.csproj) rather than left to a comment claiming the wiring is right.
//
// These are LINTS over fixed strings, not a boundary — a rewrite that renames the members will
// fail them loudly, which is the point; a rewrite that keeps the names and changes the meaning
// will not. The behaviour itself is pinned in CategoryRunFilterTests and, for the exported
// documents, in Gated/CategoryExclusionDisclosureTests.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace SQLTriage.Tests
{
    public class CategoryFilterMarkupTests
    {
        private static string ReadQuickCheckMarkup()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Markup", "QuickCheck.razor");
            File.Exists(path).Should().BeTrue(
                "QuickCheck.razor is copied to the test output by SQLTriage.Tests.csproj; " +
                "if this fails every assertion below would vacuously pass");
            return File.ReadAllText(path);
        }

        [Fact]
        public void TheChipRow_IsRenderedFromTheCatalog_AndTogglesTheRunScope()
        {
            var markup = ReadQuickCheckMarkup();

            markup.Should().Contain("foreach (var opt in CategoryOptions)",
                "the chips are populated from the loaded catalog, never from a hard-coded list");
            markup.Should().Contain("ToggleCategory(opt.Category)");
            markup.Should().Contain("var enabled = CheckRepo.GetEnabledChecks();");
            markup.Should().Contain("CategoryRunFilter.Options(enabled, _excludedCategories)");

            // The chip row is memoised (582 items, re-read on every render, and a run re-renders
            // per progress tick). A cache is only correct while it is dropped wherever either input
            // changes — the exclusion set and the catalog.
            markup.Should().Contain("private void InvalidateCategoryChips() => _categoryChipCache = null;");
            Regex.Matches(markup, @"InvalidateCategoryChips\(\);").Count.Should().BeGreaterThanOrEqualTo(3,
                "invalidated on toggle, and after each CheckRepo.LoadChecksAsync (mount + Force fresh)");
        }

        [Fact]
        public void TheRun_IsAssembledOnce_ThroughTheOnePredicate()
        {
            // One rule, one place: the same delegate that produces the diagnostic check list is the
            // delegate the executor receives. Two separately-derived rules is how a run and its own
            // log come to describe different things.
            var markup = ReadQuickCheckMarkup();

            markup.Should().Contain(
                "var selection = CategoryRunFilter.Apply(CheckRepo.GetEnabledChecks(), _excludedCategories);");
            markup.Should().Contain("Func<SqlCheck, bool> filter = selection.Predicate;");
            markup.Should().Contain("var checksToRun = selection.ChecksToRun;");
            markup.Should().Contain(
                "ExecuteChecksAsync(conn, server, filter, ct, _auditConcurrencyOverride, persistRun: persistThisRun)",
                "the filtered overload is the one the interactive page calls, and it is told in the "
                + "same breath whether this run may become the server's latest run");
        }

        [Fact]
        public void TheChipState_IsNeverPersisted()
        {
            // Ruled by this project the hard way: a dashboard UI choice that reaches the operator's
            // settings file silently narrows every LATER run, including one nobody configured. The
            // exclusion set is a page-local field and touches no store.
            var markup = ReadQuickCheckMarkup();

            markup.Should().Contain("private readonly HashSet<string> _excludedCategories",
                "the exclusion set is a page-local field, re-created on every page load");

            var leaks = markup.Split('\n')
                .Where(l => l.Contains("_excludedCategories", StringComparison.Ordinal))
                .Where(l => l.Contains("UserSettings", StringComparison.Ordinal)
                         || l.Contains("View.", StringComparison.Ordinal)
                         || l.Contains("State.", StringComparison.Ordinal))
                .ToList();

            leaks.Should().BeEmpty(
                "the chip state must not reach UserSettings, the circuit view state, or the "
                + "process-wide run state — only its MEASURED EFFECT does, as a sentence");
        }

        [Fact]
        public void TheOnPageNotice_IsConditionedOnTheRunsOwnMeasurement()
        {
            var markup = ReadQuickCheckMarkup();

            markup.Should().Contain("@if (!string.IsNullOrWhiteSpace(State.CoverageNoteForReports))",
                "the notice renders only when the run that produced these counts actually measured "
                + "something to disclose; a complete unfiltered run must render exactly as it always "
                + "has, and CoverageNoteForReports is empty for exactly that run");
            markup.Should().Contain("<span>@State.CoverageNoteForReports</span>");
        }

        [Fact]
        public void TheExportedDocuments_CarryTheSameSentenceThePageShows()
        {
            // Both exports read the one string the run wrote. A second sentence composed at the
            // export site is how a PDF and the page beside it come to disagree about one run.
            var markup = ReadQuickCheckMarkup();

            // 2026-08-10: the wiring moved from `State.CategoryExclusionNotice ?? ""` to the state
            // service's CoverageNoteForReports, which joins that sentence with the completeness one.
            // The property is still ONE string written by the run, so the argument is unchanged and
            // the count is still what holds it: neither export composes its own.
            var wirings = Regex.Matches(markup, @"CoverageNote\s*=\s*State\.CoverageNoteForReports,").Count;
            wirings.Should().Be(2,
                "the findings PDF and the executive briefing each carry the sentence, and neither "
                + "composes its own");
            markup.Should().NotContain(@"CoverageNote = State.CategoryExclusionNotice",
                "an export reading only the exclusion sentence would drop the completeness one, which "
                + "is the whole defect: an UNFILTERED run that stopped early has no exclusion sentence");

            // AND THE PAGE READS THE SAME PROPERTY. This test is named for an IDENTITY, and for half
            // a day it asserted only the export half of it: the exports moved to
            // CoverageNoteForReports while the banner stayed on CategoryExclusionNotice, so a
            // cancelled unfiltered run put a degraded-run sentence on the client's PDF that the
            // operator's own screen never showed. Both halves are pinned here now, in the one test
            // whose name claims they agree.
            markup.Should().Contain("<span>@State.CoverageNoteForReports</span>",
                "the on-screen banner must render the SAME string the two exports carry");
        }

        [Fact]
        public void ThePage_NeverWritesTheDisclosureSentenceItself()
        {
            // The sentence exists in exactly one place (CategoryRunFilter). If a literal copy ever
            // appears in the markup, the two can drift and one surface starts describing a run the
            // other measured differently.
            var markup = ReadQuickCheckMarkup();

            markup.Should().NotContain("Category filter:",
                "the disclosure text is built by CategoryRunFilter and rendered, never re-typed");
        }

        [Fact]
        public void TheGrid_IsFilledFromTheRun_NotFromTheResultCache()
        {
            // D1 (gate, 2026-08-05): RunOneTarget filled State.Results from
            // CheckExecutor.GetResults(server, 1000), which tops its capped hot cache up from the
            // persisted latest-run store, so a narrow run rendered rows no part of it executed —
            // reproduced in LastRunResultsIsolationTests, where a 58-row previous run on disk gives
            // GetResults 58 rows for a run that covered 13 Encryption checks, 45 of them from the
            // categories the notice underneath calls excluded. GetLastRunResults returns that
            // call's own rows and nothing else.
            var markup = ReadQuickCheckMarkup();

            markup.Should().Contain("var serverResults = CheckExecutor.GetLastRunResults(server);");
            markup.Should().NotContain("CheckExecutor.GetResults(srv, 1000)",
                "the accept/revoke refresh must not re-import the grid — that is the same union");
            markup.Should().Contain("CheckExecutor.AnnotateAcceptances(State.Results);",
                "the overlay is re-applied to the rows already shown, adding none");
        }

        [Fact]
        public void EveryPathThatAddsForeignRows_ClearsTheCoverageNotice()
        {
            // The notice is only true while the grid holds exactly the run it describes. The
            // imported-run top-up appends another run's rows, and nothing records which categories
            // THAT run covered, so the sentence goes rather than being stretched over them.
            var markup = ReadQuickCheckMarkup();

            var topUp = markup[markup.IndexOf("private void TopUpImportedServers()", StringComparison.Ordinal)..];
            topUp[..2000].Should().Contain("State.CategoryExclusionNotice = null;",
                "TopUpImportedServers adds rows from a run this notice never measured");
        }

        [Fact]
        public void TheNotice_IsDerivedAfterTheRun_FromWhatTheRunProduced()
        {
            // D2 (gate, 2026-08-05): the sentence was assigned from the PLAN before execution and
            // never re-derived, so a cancelled run printed "27 of 582 did not run" over a grid
            // where far more than 27 had not run. It is now written once, after execution, from the
            // rows produced — and "completed" is measured, not assumed.
            var markup = ReadQuickCheckMarkup();

            markup.Should().NotContain("CategoryExclusionNotice = selection.Disclosure",
                "the plan's arithmetic must never reach a client surface unverified");
            markup.Should().Contain(
                "State.CategoryExclusionNotice = selection.MeasuredDisclosure(allResults, runCompleted, plannedTargets);");
            markup.Should().Contain(
                "var runCompleted = !ct.IsCancellationRequested && (planIsEmpty || targetsWithRows == totalTargets);",
                "a target that threw reported no rows; that shortfall is not the filter's and the "
                + "sentence must not describe it as such — while a run with nothing to execute asked "
                + "nothing of any target and completed by producing nothing");
            markup.Should().Contain("var plannedTargets = planIsEmpty ? 0 : totalTargets;",
                "a silent target leaves no row to count as zero, so the PLANNED count is what stops "
                + "the healthy target's coverage speaking for it");
        }

        [Fact]
        public void ACategoryFilteredRun_TellsTheExecutorNotToPersistIt()
        {
            // Adrian's ruling, 2026-08-05: a category-filtered run never becomes the server's
            // persisted latest run. The decision is made here, from the page's own selection, and
            // travels as an explicit option — QuickCheckRunner narrows the SAME executor overload
            // with a quick-check predicate and must keep persisting, so nothing downstream may
            // infer this from the delegate.
            var markup = ReadQuickCheckMarkup();

            markup.Should().Contain("var persistThisRun = !selection.ExcludesAnyCategory;");
            markup.Should().Contain("persistRun: persistThisRun");
            markup.Should().NotContain("persistRun: false",
                "the flag follows the measured exclusion, never a literal at the call site");
        }

        [Fact]
        public void TheEnabledChecksBadge_StillDescribesTheCatalog_NotTheRun()
        {
            // The header pill reads "N enabled checks". It is a property of the CATALOG and stays
            // literally true for a filtered run — provided it keeps counting the catalog. If it is
            // ever rewired to the filtered set it becomes a per-run claim in a per-catalog sentence.
            var markup = ReadQuickCheckMarkup();

            markup.Should().Contain("@EnabledChecksCount enabled checks");
            markup.Should().Contain("private int EnabledChecksCount => CheckRepo.GetEnabledChecks().Count;");
            markup.Should().NotContain("EnabledChecksCount => selection",
                "the badge must not be re-pointed at a run-scoped count");
        }
    }
}
