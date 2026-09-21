/* In the name of God, the Merciful, the Compassionate */

/*
 * CategoryRunFilter — the per-run category exclusion for the Audit Assessment page, and the ONE
 * place its disclosure sentence is written.
 *
 * WHY IT IS A SERVICE AND NOT A LAMBDA IN THE PAGE: the exclusion is applied once, at the run's
 * assembly seam (Pages/QuickCheck.razor RunChecks), and three client-facing surfaces then have to
 * say what it removed — the on-page results summary, the findings PDF and the executive briefing.
 * A sentence beside a verdict must be conditioned on the same measurement that produced the
 * verdict, so all three read one string built here from the catalog the run was actually assembled
 * from. The operator's INTENT (which chips are unticked) never reaches a report: only the measured
 * effect does, which is why Apply() returns the excluded category names it found in the catalog
 * rather than echoing back the set it was handed.
 *
 * SCOPE: this is used by the interactive page only. Scheduled runs (ScheduledTaskEngine) and the
 * CLI (--audit) call the UNFILTERED CheckExecutionService.ExecuteChecksAsync overload and are
 * deliberately untouched — a chip on a dashboard must never silently narrow an unattended run.
 * (The filtered overload itself has a second, unrelated caller — QuickCheckRunner, which passes a
 * quick-check id set. It never goes through this file, so nothing here describes its runs.)
 *
 * PLAN vs OUTCOME: Apply() measures the PLAN — what the filter will remove from the catalog the
 * run is being assembled from. The sentence a client reads is MeasuredDisclosure(), taken after
 * execution from the rows the run actually produced, because a cancelled or partly-failed run
 * skipped more than the filter did and the plan's arithmetic would understate the loss beside the
 * very rows that prove it. The outcome side needs ONE fact the rows cannot carry — how many targets
 * the run planned to hear from — because a target that reported nothing leaves no row to be counted
 * as zero, so both MeasuredDisclosure() and CoveredCheckCount() take that count as a parameter.
 *
 * PERSISTENCE: a run narrowed by this filter never becomes the server's persisted "latest run"
 * (Adrian's ruling, 2026-08-05). ExcludesAnyCategory is the flag that decides it, and the page
 * passes it to the executor explicitly — nothing downstream sniffs the predicate.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>One category chip: the category, how many enabled checks carry it, and whether it
    /// is currently included in the next run.</summary>
    public sealed record CategoryRunOption(string Category, int CheckCount, bool Included);

    /// <summary>What a category filter actually removed from a run, measured against the catalog
    /// the run was assembled from. Every number here is a count taken at assembly time.</summary>
    public sealed class CategoryRunSelection
    {
        /// <summary>The predicate handed to the executor, and the same delegate that produced
        /// <see cref="ChecksToRun"/>. One RULE, not one list: the executor re-applies this predicate
        /// to its own <c>GetEnabledChecks()</c> snapshot, so the two agree exactly as long as the
        /// catalog does not change between assembly and execution. What the run actually covered is
        /// therefore read back off its results, not assumed from this — see
        /// <see cref="MeasuredDisclosure"/>.</summary>
        public Func<SqlCheck, bool> Predicate { get; init; } = _ => true;

        /// <summary>The enabled checks that survive <see cref="Predicate"/>.</summary>
        public IReadOnlyList<SqlCheck> ChecksToRun { get; init; } = Array.Empty<SqlCheck>();

        /// <summary>Enabled checks in the catalog at assembly time (the denominator).</summary>
        public int EnabledCheckCount { get; init; }

        /// <summary>Enabled checks the filter removed. Zero whenever nothing was excluded.</summary>
        public int ExcludedCheckCount => EnabledCheckCount - ChecksToRun.Count;

        /// <summary>Categories PRESENT IN THE CATALOG that the filter excluded, ordered. An
        /// unticked category the catalog no longer carries (after a Force-fresh reload, say) is
        /// absent here, because it removed nothing and naming it would claim a loss that did not
        /// happen.</summary>
        public IReadOnlyList<string> ExcludedCategories { get; init; } = Array.Empty<string>();

        /// <summary>Whether this run is narrowed by the CATEGORY exclusion, measured the same way
        /// every other number here is: against the catalog the run was assembled from. Unticking a
        /// category the catalog does not carry removes nothing, so such a run is not narrowed and
        /// this is false.
        /// <para>This is the flag the persistence decision keys on (Adrian's ruling, 2026-08-05: a
        /// category-filtered run never becomes the server's persisted "latest run"). It must stay
        /// keyed on the category exclusion and never on the presence of a filter delegate —
        /// QuickCheckRunner passes its own quick-check predicate through the same executor overload
        /// and its runs must keep persisting.</para></summary>
        public bool ExcludesAnyCategory => ExcludedCategories.Count > 0;

        /// <summary>The sentence for a run that COMPLETED and whose results matched this plan, or
        /// <c>null</c> when nothing was excluded — in which case each surface renders exactly as it
        /// always has, with no clause claiming "0 categories excluded".
        /// <para>Do not assign this to a client-facing surface directly: on its own it is an
        /// assertion about a run that has not happened yet. <see cref="MeasuredDisclosure"/> decides
        /// whether this sentence is the true one for the run that did happen, and returns it
        /// verbatim when it is, so there is still exactly one string.</para></summary>
        public string? Disclosure => ExcludedCategories.Count == 0
            ? null
            : $"Category filter: {string.Join(", ", ExcludedCategories)} excluded from this run. "
              + $"{ExcludedCheckCount} of the {EnabledCheckCount} enabled checks in the catalog did not run.";

        /// <summary>
        /// The sentence a client actually reads, derived from what the run PRODUCED rather than
        /// from what it planned. <c>null</c> when the filter excluded nothing, on the same terms as
        /// <see cref="Disclosure"/> — an unfiltered run makes no coverage claim in either direction.
        /// </summary>
        /// <param name="producedRows">The rows the run put on the grid. They are the same rows the
        /// counts and the verdicts beside this sentence are computed from, which is the whole
        /// point: one measurement under everything printed about it.</param>
        /// <param name="runCompleted">Whether the run reached its end. Measured by the caller (not
        /// cancelled, and — when the plan contained checks — every planned target reported), never
        /// assumed. A run whose PLAN was empty completed by producing nothing: that is the
        /// legitimate outcome of unticking every category, and it must not be described as a run
        /// that failed to finish.</param>
        /// <param name="plannedTargetCount">How many targets this run intended to collect results
        /// from. Required, because coverage is a floor across the PLANNED targets and a target that
        /// reported nothing forms no group in <paramref name="producedRows"/> to be the floor: pass
        /// the planned set's size and a silent target counts as the zero it was. Pass 0 when the
        /// plan contained no checks — then no target was asked to report and none is silent.</param>
        /// <remarks>
        /// A cancelled or partly-failed run skipped more than the filter did. Printing
        /// "27 of the 582 enabled checks did not run" over it would name a smaller loss than the
        /// grid beneath it demonstrates — so that arithmetic is emitted only when the run completed,
        /// every planned target reported, AND the rows agree with the plan. Otherwise the sentence
        /// states the measured coverage and says plainly that the filter does not account for all of
        /// the shortfall.
        /// </remarks>
        public string? MeasuredDisclosure(
            IEnumerable<CheckResult>? producedRows, bool runCompleted, int plannedTargetCount)
        {
            if (ExcludedCategories.Count == 0) return null;

            var reporting = CategoryRunFilter.ReportingTargetCount(producedRows);
            var silent    = Math.Max(0, plannedTargetCount - reporting);
            var covered   = CategoryRunFilter.CoveredCheckCount(producedRows, plannedTargetCount);
            var notRun    = Math.Max(0, EnabledCheckCount - covered);

            // The run did what it said it would: the one sentence, unchanged. A silent target is
            // never "what it said it would do", whatever the caller measured completion as.
            if (runCompleted && silent == 0 && notRun == ExcludedCheckCount) return Disclosure;

            var comparison =
                notRun > ExcludedCheckCount ? $"more than the {ExcludedCheckCount} the filter excluded"
              : notRun < ExcludedCheckCount ? $"fewer than the {ExcludedCheckCount} the filter excluded"
              : $"the {ExcludedCheckCount} the filter excluded, though the run did not complete";

            // A target that produced nothing is named, because otherwise "0 of the 576" sits beside
            // a grid holding the healthy target's rows and reads as a contradiction rather than as
            // the floor it is.
            var silentClause = silent == 0
                ? string.Empty
                : $" {silent} of the {plannedTargetCount} targets in this run produced no results at all, "
                  + "so this run's measured coverage is the coverage of a target that assessed nothing.";

            return $"Category filter: {string.Join(", ", ExcludedCategories)} excluded from this run."
                 + silentClause
                 + $" This run produced results for {covered} of the {EnabledCheckCount} enabled checks "
                 + $"in the catalog, so {notRun} did not run: {comparison}.";
        }

        /// <summary>Pre-run preview for the chip row. Future tense on purpose: it describes the run
        /// the operator is about to start, never one that has happened, so it can never be read as
        /// a result. <c>null</c> when every category is included.</summary>
        public string? PlannedSummary => ExcludedCategories.Count == 0
            ? null
            : $"{ExcludedCheckCount} of the {EnabledCheckCount} enabled checks will not run: "
              + string.Join(", ", ExcludedCategories) + ".";
    }

    public static class CategoryRunFilter
    {
        /// <summary>Label used for a check whose Category is blank. Chips and the predicate both go
        /// through here, so a blank-category check is togglable rather than permanently unfilterable.</summary>
        public const string Uncategorised = "Uncategorised";

        public static string CategoryKey(string? category) =>
            string.IsNullOrWhiteSpace(category) ? Uncategorised : category.Trim();

        public static string CategoryKey(SqlCheck check) => CategoryKey(check?.Category);

        /// <summary>The chip row: every category the enabled catalog carries, with its check count,
        /// marked included unless it is in <paramref name="excludedCategories"/>. An empty exclusion
        /// set — the state of a freshly loaded page — returns every option included.</summary>
        public static IReadOnlyList<CategoryRunOption> Options(
            IEnumerable<SqlCheck>? enabledChecks,
            IReadOnlyCollection<string>? excludedCategories)
        {
            var excluded = ToSet(excludedCategories);
            return (enabledChecks ?? Enumerable.Empty<SqlCheck>())
                .GroupBy(CategoryKey)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CategoryRunOption(g.Key, g.Count(), !excluded.Contains(g.Key)))
                .ToList();
        }

        /// <summary>Apply the exclusion to a catalog and measure what it removed.</summary>
        public static CategoryRunSelection Apply(
            IEnumerable<SqlCheck>? enabledChecks,
            IReadOnlyCollection<string>? excludedCategories)
        {
            var all      = (enabledChecks ?? Enumerable.Empty<SqlCheck>()).ToList();
            var excluded = ToSet(excludedCategories);

            // No exclusion at all keeps the historic predicate verbatim, so an all-on run is
            // byte-for-byte the run this page has always done.
            Func<SqlCheck, bool> predicate = excluded.Count == 0
                ? (_ => true)
                : (c => !excluded.Contains(CategoryKey(c)));

            var toRun = all.Where(predicate).ToList();

            // Measured, not echoed: only categories the catalog actually carried and the filter
            // actually dropped.
            var names = all.Select(CategoryKey)
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .Where(excluded.Contains)
                           .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                           .ToList();

            return new CategoryRunSelection
            {
                Predicate          = predicate,
                ChecksToRun        = toRun,
                EnabledCheckCount  = all.Count,
                ExcludedCategories = names,
            };
        }

        /// <summary>
        /// How many distinct checks a run actually got a result for, measured on its WORST-covered
        /// PLANNED target. Deliberately not a union across servers: on a two-server run where one
        /// server answered everything and the other answered nothing, a union would let the healthy
        /// server's coverage speak for both and the sentence would claim a completeness the second
        /// server never had. Zero for no rows at all, which is the truth about a run that produced
        /// none.
        /// <para>The planned count is a parameter and not an inference, because the failure this
        /// method exists to prevent leaves NO TRACE in the rows: a target that reported nothing
        /// forms no group here, so a floor taken over the groups alone silently drops it and the
        /// healthy server speaks for both anyway. Live on 2026-08-05 that printed "13 of the 576"
        /// for a two-target run whose second target ran zero checks. Fewer reporting targets than
        /// planned therefore means zero coverage.</para>
        /// </summary>
        /// <param name="plannedTargetCount">The size of the planned target set. Pass 0 when the run
        /// planned no checks at all, since then no target was asked to report.</param>
        public static int CoveredCheckCount(IEnumerable<CheckResult>? producedRows, int plannedTargetCount)
        {
            var perServer = PerTargetCoverage(producedRows);

            // A planned target that produced nothing covered nothing, and the floor is its number.
            if (plannedTargetCount > perServer.Count) return 0;

            return perServer.Count == 0 ? 0 : perServer.Min();
        }

        /// <summary>How many distinct targets actually put rows on the grid. Pairs with the planned
        /// count so a caller can say how many went silent instead of inferring it.</summary>
        public static int ReportingTargetCount(IEnumerable<CheckResult>? producedRows) =>
            PerTargetCoverage(producedRows).Count;

        /// <summary>Distinct checks covered, per reporting target. A silent target is absent by
        /// construction — that is exactly why callers must supply the planned count.</summary>
        private static List<int> PerTargetCoverage(IEnumerable<CheckResult>? producedRows)
        {
            if (producedRows == null) return new List<int>();

            return producedRows
                .Where(r => r != null)
                .GroupBy(r => r.InstanceName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Select(r => r.CheckId ?? string.Empty)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .Count())
                .ToList();
        }

        private static HashSet<string> ToSet(IReadOnlyCollection<string>? categories)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (categories == null) return set;
            foreach (var c in categories)
                if (!string.IsNullOrWhiteSpace(c)) set.Add(c.Trim());
            return set;
        }
    }
}
