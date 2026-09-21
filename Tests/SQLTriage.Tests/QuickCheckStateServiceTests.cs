/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Threading;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Guards the NotifyStateChanged debounce: a multi-server QuickCheck run emits
    /// thousands of per-check notifications, which previously each re-rendered the
    /// full page and saturated the WebView2 UI thread. The debounce must (a) fire
    /// an isolated call immediately, (b) coalesce a burst to a handful of events,
    /// and (c) always deliver a trailing event so the final state renders.
    /// </summary>
    public class QuickCheckStateServiceTests
    {
        [Fact]
        public void NotifyStateChanged_IsolatedCall_FiresImmediately()
        {
            using var svc = new QuickCheckStateService();
            var count = 0;
            svc.StateChanged += () => Interlocked.Increment(ref count);

            svc.NotifyStateChanged();

            // Leading edge: no timer wait for a lone user action (e.g. Clear click).
            Assert.Equal(1, count);
        }

        [Fact]
        public void NotifyStateChanged_Burst_CoalescesAndDeliversTrailingEvent()
        {
            using var svc = new QuickCheckStateService();
            var count = 0;
            svc.StateChanged += () => Interlocked.Increment(ref count);

            // Simulate the per-check notification storm of a large run.
            for (var i = 0; i < 500; i++)
                svc.NotifyStateChanged();

            // Generous upper bound: 500 calls land within very few 250ms windows.
            Assert.InRange(count, 1, 10);

            // Trailing edge: the last update of the burst must still render.
            var before = count;
            Thread.Sleep(600);
            Assert.True(count > before,
                $"expected a trailing flush after the burst (events before wait: {before}, after: {count})");
            Assert.InRange(count, 2, 12);
        }

        [Fact]
        public void ClearResults_AlsoClearsTheCategoryCoverageNotice()
        {
            // The notice describes the run that produced Results. Surviving a clear would attach a
            // past run's coverage sentence to whatever rows appear next — an import, a restore, or
            // an empty grid — which is the "prose outlived its evidence" defect exactly.
            using var svc = new QuickCheckStateService
            {
                CategoryExclusionNotice = "Category filter: Auditing excluded from this run. "
                                          + "1 of the 3 enabled checks in the catalog did not run.",
                HasRun = true,
            };
            svc.Results.Add(new SQLTriage.Data.Models.CheckResult { CheckId = "C1", InstanceName = "S1" });

            svc.ClearResults();

            Assert.Empty(svc.Results);
            Assert.Null(svc.CategoryExclusionNotice);
        }

        [Fact]
        public void CategoryCoverageNotice_DefaultsToNull_SoAnUnfilteredRunClaimsNothing()
        {
            using var svc = new QuickCheckStateService();

            Assert.Null(svc.CategoryExclusionNotice);
        }
    }
}
