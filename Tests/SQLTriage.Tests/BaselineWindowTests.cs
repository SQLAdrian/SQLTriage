/* In the name of God, the Merciful, the Compassionate */

using System;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Settles dashboards-r2-03 at the code level. The dashboard's Baseline button reads a cache
    /// window ending at <c>TimeTo - 7d</c>, while <c>CachingQueryExecutor</c> trims (deletes) every
    /// cached point older than <c>TimeFrom</c> after each delta fetch. For every toolbar range the
    /// baseline window's END (<c>TimeTo - 7d</c>) sits at or below the trim cutoff (<c>TimeFrom</c>),
    /// so no retained point can ever populate it — the baseline read is structurally empty. This test
    /// pins that inequality for all six offered ranges; the reproduce pass could not fire the Blazor
    /// click, so this arithmetic is the deterministic settle, and the fix (an honest "no baseline
    /// available" notice) is what the user now sees instead of a silent active-but-empty toggle.
    ///
    /// Window arithmetic mirrors DynamicDashboard: TimeFrom = now - range, TimeTo = now,
    /// baselineTo = TimeTo - 7d; trim cutoff = TimeFrom.
    ///
    /// <para>STILL TRUE, AND NO LONGER THE WHOLE STORY (2026-08-26, DECISIONS 18:23 ruling 4). The
    /// inequality below is exactly why the CACHE can never supply a baseline, and it is unchanged:
    /// the delta fetch still trims. What changed is that a baseline no longer has to come from the
    /// cache. Panels with <c>retainHistory</c> write to <c>metric_history</c>, which none of the
    /// cache deleters touch, and the Baseline read consults that first. See MetricRetentionTests.
    /// The honest notice this test's fix introduced is not gone either — it now distinguishes "no
    /// panel here retains history" from "retention is running and is not seven days old yet".</para>
    /// </summary>
    public class BaselineWindowTests
    {
        private static readonly TimeSpan Shift = TimeSpan.FromDays(7);

        [Theory]
        [InlineData(5)]
        [InlineData(15)]
        [InlineData(60)]
        [InlineData(360)]
        [InlineData(1440)]
        public void Sub_seven_day_ranges_put_the_whole_baseline_window_below_the_trim_cutoff(int rangeMinutes)
        {
            var now = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
            var timeFrom = now.AddMinutes(-rangeMinutes);
            var timeTo = now;
            var baselineTo = timeTo - Shift;
            var trimCutoff = timeFrom;

            // The newest point the baseline could want is strictly older than the oldest point the
            // cache retains, so the baseline window is empty by construction.
            Assert.True(baselineTo < trimCutoff,
                $"range {rangeMinutes}m: baselineTo {baselineTo:o} should be < trim cutoff {trimCutoff:o}");
        }

        [Fact]
        public void At_exactly_seven_days_the_baseline_window_end_only_touches_the_cutoff()
        {
            var now = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
            var timeFrom = now.AddMinutes(-10080); // 7 days
            var baselineTo = now - Shift;

            // baselineTo == TimeFrom: the strict "time_value < cutoff" trim leaves at most a single
            // boundary instant, so even the 7-day range yields no usable baseline series.
            Assert.Equal(timeFrom, baselineTo);
        }
    }
}
