/* In the name of God, the Merciful, the Compassionate */

// ── The evaluation cycle was serial across alerts (strings lane fix round, 2026-08-28) ─────────
//
// WHAT WAS WRONG. EvaluateAllAsync ran
//
//     foreach (var alert in alerts) { ...; await Task.WhenAll(tasks); }
//
// so it was parallel across the SERVERS of one alert and strictly serial across ALERTS. Three
// alerts this lane re-based in cluster 2 - io_stall_time, disk_latency_read, disk_latency_write -
// each take two readings of sys.dm_io_virtual_file_stats five seconds apart, with
// WAITFOR DELAY '00:00:05' inside the query, and each ships frequencySeconds 60.
//
// MEASURED on .\NEW2022 from a master connection, 2026-08-28:
//
//     io_stall_time        5181 ms
//     disk_latency_read    5194 ms
//     disk_latency_write   5218 ms
//     three back to back  15593 ms
//
// AlertEvaluation:BaseTickSeconds defaults to 30. So on any tick where all three were due, more
// than HALF the tick budget went on waiting before any other alert was evaluated, and if the
// remaining ~70 alerts took another 15 s the loop logged "Evaluation tick overran interval; next
// tick dropped" and every 30-second alert - deadlock, cluster_failover - missed its cadence.
//
// WHAT CHANGED. The per-alert work is collected and run through
// AlertEvaluationService.RunBoundedAsync with AlertEvaluation:MaxConcurrentAlerts (default 4), so
// three five-second windows cost five seconds and not fifteen. The due checks, the window
// suppression and the _lastDueCheck stamp all stay sequential and in catalogue order; only the
// querying overlaps.
//
// WHY BOUNDED AND NOT UNBOUNDED. The work opens SQL connections against a client's servers. Running
// every due alert at once would multiply their concurrent query count by the number of due alerts,
// on the strength of a tick-time argument. The bound is asserted below by PEAK CONCURRENCY, not
// only by wall clock, because a wall-clock-only test passes just as happily on an unbounded
// implementation.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class AlertCycleConcurrencyTests
    {
        /// <summary>
        /// THE DEFECT AND THE FIX IN ONE MEASUREMENT. Three units that each sleep 400 ms - standing
        /// in for the three WAITFOR windows - complete in about one unit's time under the real
        /// scheduler, and in about three units' time under the serial shape that shipped.
        /// </summary>
        [Fact]
        public async Task Three_windowed_units_cost_one_window_and_not_three()
        {
            const int unitMs = 400;

            var parallel = Stopwatch.StartNew();
            await AlertEvaluationService.RunBoundedAsync(Units(3, unitMs), maxConcurrent: 4);
            parallel.Stop();

            var serial = Stopwatch.StartNew();
            foreach (var unit in Units(3, unitMs)) await unit();
            serial.Stop();

            // Generous margins: this is a scheduler test on a shared build box, not a benchmark.
            // What it must not tolerate is the two shapes being the same.
            Assert.True(parallel.ElapsedMilliseconds < unitMs * 2,
                $"three concurrent {unitMs} ms units took {parallel.ElapsedMilliseconds} ms - that is "
                + "the serial shape the cycle used to have");
            Assert.True(serial.ElapsedMilliseconds > parallel.ElapsedMilliseconds,
                $"non-vacuity: the serial run ({serial.ElapsedMilliseconds} ms) must be the slower of "
                + $"the two, or this test is measuring nothing (parallel {parallel.ElapsedMilliseconds} ms)");
        }

        /// <summary>
        /// The bound is real. Twenty units, four slots: peak concurrency must be four, never twenty.
        /// This is the half that stops "make the tick faster" turning into "open twenty connections
        /// to a client's SQL Server at once".
        /// </summary>
        [Fact]
        public async Task No_more_than_the_configured_number_of_alerts_run_at_once()
        {
            var inFlight = 0;
            var peak = 0;
            var gate = new object();

            var work = new List<Func<Task>>();
            for (var i = 0; i < 20; i++)
            {
                work.Add(async () =>
                {
                    lock (gate)
                    {
                        inFlight++;
                        if (inFlight > peak) peak = inFlight;
                    }

                    await Task.Delay(40);

                    lock (gate) { inFlight--; }
                });
            }

            await AlertEvaluationService.RunBoundedAsync(work, maxConcurrent: 4);

            Assert.Equal(0, inFlight);
            Assert.True(peak <= 4, $"peak concurrency was {peak}, above the bound of 4");
            Assert.True(peak > 1, $"peak concurrency was {peak}: the scheduler ran serially");
        }

        /// <summary>Every unit runs, and the call does not complete before all of them have. A
        /// scheduler that dropped work would make the cycle quietly stop evaluating alerts.</summary>
        [Fact]
        public async Task Every_unit_of_work_runs_exactly_once_and_all_are_awaited()
        {
            var ran = new int[50];
            var work = new List<Func<Task>>();
            for (var i = 0; i < ran.Length; i++)
            {
                var index = i;
                work.Add(async () => { await Task.Yield(); Interlocked.Increment(ref ran[index]); });
            }

            await AlertEvaluationService.RunBoundedAsync(work, maxConcurrent: 4);

            Assert.All(ran, r => Assert.Equal(1, r));
        }

        [Fact]
        public async Task An_empty_or_degenerate_cycle_is_not_an_error()
        {
            await AlertEvaluationService.RunBoundedAsync(Array.Empty<Func<Task>>(), 4);
            await AlertEvaluationService.RunBoundedAsync(null!, 4);

            // A misconfigured MaxConcurrentAlerts of 0 must still run the work, serially, rather
            // than deadlocking on a semaphore with no slots.
            var ran = 0;
            await AlertEvaluationService.RunBoundedAsync(
                new List<Func<Task>> { () => { Interlocked.Increment(ref ran); return Task.CompletedTask; } }, 0);
            Assert.Equal(1, ran);
        }

        /// <summary>Cancellation stops the cycle rather than being swallowed into a false
        /// "evaluation failed" line at shutdown.</summary>
        [Fact]
        public async Task A_cancelled_cycle_surfaces_cancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => AlertEvaluationService.RunBoundedAsync(Units(3, 10), 4, cts.Token));
        }

        /// <summary>The shipped default, pinned: three five-second windows must fit inside it, or
        /// the fix does not reach the case that produced it.</summary>
        [Fact]
        public void The_shipped_bound_is_wide_enough_for_the_three_windowed_alerts()
        {
            Assert.True(AlertEvaluationService.DefaultMaxConcurrentAlerts >= 3,
                "io_stall_time, disk_latency_read and disk_latency_write all ship frequencySeconds "
                + "60 and all embed a five-second WAITFOR, so they land on the same tick");
        }

        private static List<Func<Task>> Units(int count, int delayMs)
        {
            var work = new List<Func<Task>>();
            for (var i = 0; i < count; i++) work.Add(() => Task.Delay(delayMs));
            return work;
        }
    }
}
