/* In the name of God, the Merciful, the Compassionate */

// The Audit Assessment page's per-run category filter (Adrian, 2026-08-05). The page's RunChecks
// method is a private member of a Blazor component and cannot be called from a test, which is
// exactly why the rule lives in CategoryRunFilter instead of in a lambda at the seam: the thing
// that decides what executes, and the sentence every client-facing surface prints about it, are
// both exercised here against real catalogs.
//
// Profile-independent: CategoryRunFilter compiles in every edition, as does the page it serves.

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class CategoryRunFilterTests
    {
        private static SqlCheck Check(string id, string category) =>
            new() { Id = id, Name = id, Category = category };

        /// <summary>A stand-in catalog with the shape of the real one: a few categories of very
        /// different sizes, so an off-by-one in the counting shows up as a wrong number rather
        /// than a coincidence.</summary>
        private static List<SqlCheck> Catalog() => new()
        {
            Check("C1", "Configuration"), Check("C2", "Configuration"), Check("C3", "Configuration"),
            Check("P1", "Performance"),   Check("P2", "Performance"),
            Check("A1", "Auditing"),
            Check("E1", "Encryption"),
        };

        // ── The chips ────────────────────────────────────────────────────────

        [Fact]
        public void Options_OnAFreshPage_HasEveryCategoryIncluded()
        {
            // The default the feature was specified with: all on. A fresh page load hands an empty
            // exclusion set, and nothing may be off.
            var options = CategoryRunFilter.Options(Catalog(), new HashSet<string>());

            options.Should().HaveCount(4);
            options.Should().OnlyContain(o => o.Included,
                "an unconfigured page runs the whole catalog");
            options.Select(o => o.Category).Should()
                .Equal("Auditing", "Configuration", "Encryption", "Performance");
        }

        [Fact]
        public void Options_CountEachCategorysChecks_AndMarkOnlyTheUntickedOnesExcluded()
        {
            var options = CategoryRunFilter.Options(Catalog(), new[] { "Auditing" });

            options.Single(o => o.Category == "Configuration").CheckCount.Should().Be(3);
            options.Single(o => o.Category == "Performance").CheckCount.Should().Be(2);
            options.Single(o => o.Category == "Auditing").CheckCount.Should().Be(1);

            options.Single(o => o.Category == "Auditing").Included.Should().BeFalse();
            options.Where(o => o.Category != "Auditing").Should().OnlyContain(o => o.Included);
        }

        [Fact]
        public void Options_CategoryMatching_IsCaseInsensitive()
        {
            // The chip label comes from the catalog; the excluded set comes from a click on that
            // label. They can only disagree through casing, and a filter that silently fails to
            // exclude would run checks the operator believes are off.
            var options = CategoryRunFilter.Options(Catalog(), new[] { "aUdItInG" });

            options.Single(o => o.Category == "Auditing").Included.Should().BeFalse();
        }

        // ── What runs ────────────────────────────────────────────────────────

        [Fact]
        public void Apply_WithNothingExcluded_RunsEveryEnabledCheck()
        {
            var catalog = Catalog();

            var selection = CategoryRunFilter.Apply(catalog, new HashSet<string>());

            selection.ChecksToRun.Should().HaveCount(catalog.Count);
            selection.ExcludedCheckCount.Should().Be(0);
            selection.ExcludedCategories.Should().BeEmpty();
        }

        [Fact]
        public void Apply_ExcludesExactlyThatCategorysChecks_AndNothingElse()
        {
            var catalog = Catalog();

            var selection = CategoryRunFilter.Apply(catalog, new[] { "Auditing", "Encryption" });

            selection.ChecksToRun.Select(c => c.Id).Should()
                .BeEquivalentTo(new[] { "C1", "C2", "C3", "P1", "P2" });
            selection.ChecksToRun.Should().NotContain(c => c.Category == "Auditing");
            selection.ChecksToRun.Should().NotContain(c => c.Category == "Encryption");
            selection.ExcludedCheckCount.Should().Be(2);
        }

        [Fact]
        public void Apply_ThePredicateAndTheCheckList_AreTheSameRule()
        {
            // The page hands Predicate to the executor and reports ChecksToRun in the diagnostics.
            // If those two were derived separately the log could describe a run that never happened
            // — this pins them to one another over the whole catalog, in both directions.
            var catalog = Catalog();

            var selection = CategoryRunFilter.Apply(catalog, new[] { "Performance" });

            catalog.Where(selection.Predicate).Select(c => c.Id).Should()
                .Equal(selection.ChecksToRun.Select(c => c.Id));
            catalog.Where(c => !selection.Predicate(c)).Should()
                .OnlyContain(c => c.Category == "Performance");
        }

        [Fact]
        public void Apply_ABlankCategory_IsTogglableAsUncategorised()
        {
            // A check with no category must not be permanently unfilterable (nor silently dropped):
            // it gets one chip, under one label, and the predicate honours the same label.
            var catalog = new List<SqlCheck> { Check("B1", ""), Check("C1", "Configuration") };

            CategoryRunFilter.Options(catalog, Array.Empty<string>())
                .Select(o => o.Category).Should().Contain(CategoryRunFilter.Uncategorised);

            var selection = CategoryRunFilter.Apply(catalog, new[] { CategoryRunFilter.Uncategorised });

            selection.ChecksToRun.Select(c => c.Id).Should().Equal("C1");
            selection.ExcludedCategories.Should().Equal(CategoryRunFilter.Uncategorised);
        }

        // ── What it says ─────────────────────────────────────────────────────

        [Fact]
        public void Disclosure_IsNull_WhenNothingWasExcluded()
        {
            // The whole point of the null: a run with every category on renders exactly as this
            // page always has. No surface prints a clause claiming "0 categories excluded".
            var selection = CategoryRunFilter.Apply(Catalog(), new HashSet<string>());

            selection.Disclosure.Should().BeNull();
            selection.PlannedSummary.Should().BeNull();
        }

        [Fact]
        public void Disclosure_NamesEveryExcludedCategory_AndBothCounts()
        {
            var selection = CategoryRunFilter.Apply(Catalog(), new[] { "Encryption", "Auditing" });

            selection.Disclosure.Should().Be(
                "Category filter: Auditing, Encryption excluded from this run. "
                + "2 of the 7 enabled checks in the catalog did not run.");
        }

        [Fact]
        public void Disclosure_NamesOnlyCategoriesTheCatalogActuallyCarried()
        {
            // An unticked category that the catalog no longer holds (a Force-fresh reload between
            // the click and the run) removed nothing. Naming it would claim a loss that did not
            // happen; the sentence states only what was measured.
            var selection = CategoryRunFilter.Apply(Catalog(), new[] { "Auditing", "Telepathy" });

            selection.ExcludedCategories.Should().Equal("Auditing");
            selection.Disclosure.Should().Contain("Auditing");
            selection.Disclosure.Should().NotContain("Telepathy");
            selection.ExcludedCheckCount.Should().Be(1);
        }

        [Fact]
        public void Disclosure_IsNull_WhenTheUntickedCategoriesRemovedNothing()
        {
            // Same rule taken to its limit: an exclusion set that matched no check at all is not a
            // filtered run, and must not print a coverage caveat over a complete one.
            var selection = CategoryRunFilter.Apply(Catalog(), new[] { "Telepathy" });

            selection.ChecksToRun.Should().HaveCount(7);
            selection.Disclosure.Should().BeNull();
        }

        [Fact]
        public void PlannedSummary_IsFutureTense_SoItCannotBeReadAsAResult()
        {
            // It sits under the chips BEFORE a run. "will not run" is the whole distinction from
            // the post-run disclosure's "did not run".
            var selection = CategoryRunFilter.Apply(Catalog(), new[] { "Auditing" });

            selection.PlannedSummary.Should().Be(
                "1 of the 7 enabled checks will not run: Auditing.");
            selection.PlannedSummary.Should().NotContain("did not run");
        }

        // ── What it says about the run that actually happened ────────────────
        //
        // D2 (gate ruling, 2026-08-05): the page assigned the PLAN's sentence before anything
        // executed and never re-derived it. A cancelled or partly-failed run skips more than the
        // filter did, so "2 of the 7 enabled checks did not run" sat over a grid proving otherwise.
        // Every number below is taken from the rows the run produced.

        private static CheckResult Row(string server, string checkId, string category) =>
            new() { InstanceName = server, CheckId = checkId, Category = category, CheckName = checkId };

        /// <summary>The rows a COMPLETE Auditing+Encryption-excluded run over Catalog() produces on
        /// one server: the five checks that survived the filter, one row each.</summary>
        private static List<CheckResult> CompleteRunRows(string server) => new()
        {
            Row(server, "C1", "Configuration"), Row(server, "C2", "Configuration"),
            Row(server, "C3", "Configuration"), Row(server, "P1", "Performance"),
            Row(server, "P2", "Performance"),
        };

        [Fact]
        public void MeasuredDisclosure_OfARunThatDidWhatItPlanned_IsTheOneSentenceVerbatim()
        {
            // Agreement between plan and outcome is the common case, and it must read exactly as
            // Disclosure does — one string, not a second phrasing of the same fact.
            var selection = CategoryRunFilter.Apply(Catalog(), new[] { "Encryption", "Auditing" });

            var sentence = selection.MeasuredDisclosure(
                CompleteRunRows("SQL01"), runCompleted: true, plannedTargetCount: 1);

            sentence.Should().Be(selection.Disclosure);
            sentence.Should().Be(
                "Category filter: Auditing, Encryption excluded from this run. "
                + "2 of the 7 enabled checks in the catalog did not run.");
        }

        [Fact]
        public void MeasuredDisclosure_OfARunThatWasCancelled_NeverPrintsThePlansArithmetic()
        {
            // The defect verbatim: a cancelled run that got through 3 of its 5 planned checks. The
            // filter excluded 2; four did not run. Printing "2 of the 7 did not run" here names a
            // smaller loss than the grid beside it shows.
            var selection = CategoryRunFilter.Apply(Catalog(), new[] { "Encryption", "Auditing" });
            var partial = CompleteRunRows("SQL01").Take(3).ToList();

            var sentence = selection.MeasuredDisclosure(
                partial, runCompleted: false, plannedTargetCount: 1);

            sentence.Should().NotBe(selection.Disclosure);
            sentence.Should().NotContain("2 of the 7 enabled checks in the catalog did not run");
            sentence.Should().Be(
                "Category filter: Auditing, Encryption excluded from this run. "
                + "This run produced results for 3 of the 7 enabled checks in the catalog, "
                + "so 4 did not run: more than the 2 the filter excluded.");
        }

        [Fact]
        public void MeasuredDisclosure_OfAMultiServerRunWhereOneServerNeverReported_CountsTheSilentServerAsZero()
        {
            // Two targets, one unreachable. The run "completed" only in the sense that it stopped.
            // A silent target forms NO GROUP in the produced rows, so a floor taken over the rows
            // alone dropped it and the healthy server's five checks spoke for the server that
            // answered nothing — live on 2026-08-05 that printed "13 of the 576" for a run whose
            // second target ran zero checks. The planned target count is what makes the zero
            // visible, and the sentence names the silent target rather than implying the grid's
            // rows are the whole story.
            var selection = CategoryRunFilter.Apply(Catalog(), new[] { "Encryption", "Auditing" });
            var rows = CompleteRunRows("SQL01");   // SQL02 threw and contributed no rows

            CategoryRunFilter.CoveredCheckCount(rows, plannedTargetCount: 2).Should().Be(0);

            var sentence = selection.MeasuredDisclosure(rows, runCompleted: false, plannedTargetCount: 2);

            sentence.Should().Contain("1 of the 2 targets in this run produced no results at all");
            sentence.Should().Contain("This run produced results for 0 of the 7 enabled checks");
            sentence.Should().Contain("so 7 did not run: more than the 2 the filter excluded");
            sentence.Should().NotContain("results for 5 of the 7",
                "the healthy server's coverage may never speak for a target that assessed nothing");
        }

        [Fact]
        public void MeasuredDisclosure_OfASilentTarget_StaysHonestEvenWhenTheCallerCallsTheRunComplete()
        {
            // The caller measures completion; this class must not be defeated by a caller that
            // measures it wrongly. A silent planned target is never "the run did what it planned",
            // so the plan's clean arithmetic stays unprinted whatever runCompleted says.
            var selection = CategoryRunFilter.Apply(Catalog(), new[] { "Encryption", "Auditing" });

            var sentence = selection.MeasuredDisclosure(
                CompleteRunRows("SQL01"), runCompleted: true, plannedTargetCount: 2);

            sentence.Should().NotBe(selection.Disclosure);
            sentence.Should().Contain("1 of the 2 targets in this run produced no results at all");
        }

        [Fact]
        public void MeasuredDisclosure_OfAMultiServerRunWhereOneServerReportedLess_TakesTheFloorNotTheUnion()
        {
            // Both servers reported, so a union would read 5 of 7 and print the clean sentence. The
            // second server assessed 2 checks; the honest claim about THIS run's coverage is the
            // one that holds for every server in it.
            var selection = CategoryRunFilter.Apply(Catalog(), new[] { "Encryption", "Auditing" });
            var rows = CompleteRunRows("SQL01");
            rows.AddRange(CompleteRunRows("SQL02").Take(2));

            CategoryRunFilter.CoveredCheckCount(rows, plannedTargetCount: 2).Should().Be(2);

            var sentence = selection.MeasuredDisclosure(rows, runCompleted: true, plannedTargetCount: 2);

            sentence.Should().NotBe(selection.Disclosure);
            sentence.Should().Contain("This run produced results for 2 of the 7 enabled checks");
            sentence.Should().Contain("so 5 did not run: more than the 2 the filter excluded");
        }

        [Fact]
        public void MeasuredDisclosure_OfARunWhoseOnlyTargetProducedNothing_SaysZero()
        {
            // One planned target, unreachable. Zero is the truth, and the sentence has to be able
            // to say zero — including when it is handed no collection at all.
            var selection = CategoryRunFilter.Apply(Catalog(), new[] { "Encryption", "Auditing" });

            selection.MeasuredDisclosure(new List<CheckResult>(), runCompleted: false, plannedTargetCount: 1)
                .Should().Contain("This run produced results for 0 of the 7 enabled checks");
            selection.MeasuredDisclosure(null, runCompleted: false, plannedTargetCount: 1)
                .Should().Contain("This run produced results for 0 of the 7 enabled checks");
        }

        [Fact]
        public void MeasuredDisclosure_OfACompletedRunWithNothingLeftToExecute_NeverSaysItDidNotComplete()
        {
            // D-B (gate, 2026-08-05): unticking every category is a legitimate run whose correct
            // outcome is zero rows. Judging it by "did every target report rows" printed "though
            // the run did not complete" beside the page's own "No checks were executed" status.
            // An empty plan asks nothing of any target, so no target is silent and the run that
            // reaches its end completed: the plain arithmetic is the true sentence for it.
            var selection = CategoryRunFilter.Apply(
                Catalog(), new[] { "Encryption", "Auditing", "Configuration", "Performance" });

            var sentence = selection.MeasuredDisclosure(
                new List<CheckResult>(), runCompleted: true, plannedTargetCount: 0);

            sentence.Should().Be(selection.Disclosure);
            sentence.Should().Be(
                "Category filter: Auditing, Configuration, Encryption, Performance excluded from this run. "
                + "7 of the 7 enabled checks in the catalog did not run.");
            sentence.Should().NotContain("did not complete");
            sentence.Should().NotContain("targets in this run produced no results",
                "no target was asked to report, so none went silent");
        }

        [Fact]
        public void MeasuredDisclosure_OfAnUnfilteredRun_IsNull_WhateverHappenedToIt()
        {
            // An unfiltered run makes no coverage claim in either direction, cancelled or not. The
            // run's own status line reports a cancellation; a CATEGORY notice over a run with no
            // category filter would be a caveat about a thing that did not happen.
            var selection = CategoryRunFilter.Apply(Catalog(), Array.Empty<string>());

            selection.MeasuredDisclosure(CompleteRunRows("SQL01"), runCompleted: true, plannedTargetCount: 1)
                .Should().BeNull();
            selection.MeasuredDisclosure(new List<CheckResult>(), runCompleted: false, plannedTargetCount: 2)
                .Should().BeNull();
        }

        [Fact]
        public void CoveredCheckCount_CountsDistinctChecks_NotRows()
        {
            // A check can appear more than once for an instance across a session's stored rows.
            // Coverage is about which checks were assessed, not how many rows exist.
            var rows = new List<CheckResult>
            {
                Row("SQL01", "C1", "Configuration"),
                Row("SQL01", "C1", "Configuration"),
                Row("SQL01", "C2", "Configuration"),
            };

            CategoryRunFilter.CoveredCheckCount(rows, plannedTargetCount: 1).Should().Be(2);
        }

        [Fact]
        public void CoveredCheckCount_TakesTheFloorAcrossPLANNEDTargets_NotAcrossTheOnesThatAnswered()
        {
            // The defect this parameter exists for, at unit scale. The same rows are full coverage
            // for a one-target run and zero coverage for a two-target run, because in the second
            // case a target that was asked for results produced none and no row records that.
            var rows = CompleteRunRows("SQL01");

            CategoryRunFilter.CoveredCheckCount(rows, plannedTargetCount: 1).Should().Be(5);
            CategoryRunFilter.CoveredCheckCount(rows, plannedTargetCount: 2).Should().Be(0);
            CategoryRunFilter.CoveredCheckCount(rows, plannedTargetCount: 3).Should().Be(0);
        }

        [Fact]
        public void ReportingTargetCount_CountsTheTargetsThatActuallyPutRowsOnTheGrid()
        {
            var rows = CompleteRunRows("SQL01");
            rows.AddRange(CompleteRunRows("SQL02").Take(2));

            CategoryRunFilter.ReportingTargetCount(rows).Should().Be(2);
            CategoryRunFilter.ReportingTargetCount(new List<CheckResult>()).Should().Be(0);
            CategoryRunFilter.ReportingTargetCount(null).Should().Be(0);
        }

        // ── The persistence flag (Adrian's ruling, 2026-08-05) ───────────────

        [Fact]
        public void ExcludesAnyCategory_IsTrue_OnlyWhenTheFilterActuallyNarrowedTheCatalog()
        {
            // The flag the page hands the executor to withhold the latest-run write. It follows the
            // MEASURED exclusion, so a chip for a category the catalog no longer carries — which
            // removes nothing, and leaves a run identical to the full one — still persists.
            CategoryRunFilter.Apply(Catalog(), new[] { "Encryption" })
                .ExcludesAnyCategory.Should().BeTrue();
            CategoryRunFilter.Apply(Catalog(), Array.Empty<string>())
                .ExcludesAnyCategory.Should().BeFalse();
            CategoryRunFilter.Apply(Catalog(), new[] { "Replication" })
                .ExcludesAnyCategory.Should().BeFalse(
                    "an unticked category the catalog does not carry removed nothing from this run");
        }
    }
}
