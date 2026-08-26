// In the name of God, the Merciful, the Compassionate

using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLTriage.Data
{
    /// <summary>
    /// Turns a series of readings of a CUMULATIVE counter into a per-second rate.
    ///
    /// Every source wired to this (sys.dm_os_performance_counters cntr_value, @@TOTAL_READ /
    /// @@TOTAL_WRITE, sys.dm_os_wait_stats wait_time_ms) counts UP from instance start. A single
    /// reading of such a counter is a total, never a rate — the counter names ending in "/sec" are
    /// SQL Server's naming, not its arithmetic.
    ///
    /// The method returns false rather than a number whenever no rate has actually been measured,
    /// because a printed 0/sec and an unmeasured rate look identical to the reader and mean opposite
    /// things: one says the server is idle, the other says nobody has looked twice yet.
    /// </summary>
    public static class DeltaRate
    {
        /// <summary>
        /// Computes the per-second rate across the oldest and newest sample in <paramref name="samples"/>.
        /// Returns false (and a rate of 0, which callers must not print) when:
        /// <list type="bullet">
        /// <item>fewer than two samples exist — nothing has been measured yet;</item>
        /// <item>the two samples carry the same timestamp — dividing by zero elapsed time;</item>
        /// <item>the counter went BACKWARDS — a cumulative counter only decreases across a reset
        /// (instance restart, DBCC SQLPERF clear), so the window spans a discontinuity and the
        /// difference across it is not a rate of anything.</item>
        /// </list>
        /// </summary>
        public static bool TryCompute(
            IReadOnlyCollection<(DateTime Time, double Value)> samples,
            out double ratePerSecond)
        {
            ratePerSecond = 0;

            if (samples == null || samples.Count < 2)
                return false;

            var oldest = samples.First();
            var newest = samples.Last();

            var seconds = (newest.Time - oldest.Time).TotalSeconds;
            if (seconds <= 0)
                return false;

            var delta = newest.Value - oldest.Value;
            if (delta < 0)
                return false;

            ratePerSecond = delta / seconds;
            return true;
        }
    }
}
