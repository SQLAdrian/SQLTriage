/* In the name of God, the Merciful, the Compassionate */

// -- Retained metric history, 2026-08-26 ------------------------------------------------------
//
// WHY THIS FILE EXISTS. DECISIONS 2026-08-26 18:23 ruling 4. Two dashboard features were honest in
// their prose and empty in their substance: the AG send/redo queue panels drew one sample per
// refresh and called it a trend, and the Baseline compare had no 7-day-ago window to draw, so the
// button flipped active and rendered nothing. Both needed retained history, and the ruling set an
// explicit honesty bar with it: a range with insufficient retained history must still SAY so.
// Retention makes the features real over time. It does not backfill the past.
//
// WHAT IS ACTUALLY MEASURED HERE, tier by tier.
//   * Tier 1 is the canonical time format, in the production helper both the writer and the age
//     prune call. Nothing here re-implements it. The prune is a DELETE driven by a string compare,
//     so a format that is only accidentally sortable is a data-loss bug.
//   * Tier 2 opens a REAL SQLite store, writes retained history through the production append, and
//     then runs the three deleters that destroy the live cache: the per-fetch window trim, the
//     fetched_at eviction sweep, and the full invalidate. Retained history has to still be there.
//     That is the whole design claim in one test.
//   * Tier 3 is the pruning: by age, and by the hard row cap, with the survivors asserted to be the
//     NEWEST rows. A cap that kept an arbitrary subset would bound disk and lose the trend.
//   * Tier 3b is the READ against the chart point cap, added after the gate reproduced a silent
//     drop: the cap used to keep the OLDEST rows, so a range holding more buckets than the cap ended
//     its line hours before now, and the coverage probe, which counts without a cap, reported whole
//     coverage. A cap is a resolution. It is never a shorter window, and never silent.
//   * Tier 4 is the eviction-sweep trap, asserted structurally. EvictOlderThanCore discovers its
//     victims by joining pragma_table_info on a column named fetched_at and then passes each
//     discovered name through ValidateTableName. A metric_history that grew a fetched_at column
//     would be swept at 24 hours AND, if it were ever dropped from AllowedTables, would throw an
//     ArgumentException that CacheEvictionService swallows as a generic failure, killing ALL
//     eviction for the process. This runs the sweep's own discovery query.
//   * Tier 5 is the honesty text itself, from the production pure functions the dashboard renders,
//     across every branch. Including the one the gate found silent: a baseline that WAS drawn used to
//     return "" however thin it was, and the measurement behind it read the TOOLBAR window rather than
//     the 7-day-shifted window the baseline actually opens. At the shipped 8-day retention window a
//     "Last 7 days" baseline reads from 14 days back, so it cannot cover more than a seventh of itself
//     in the steady state, permanently — and it said nothing. The shifted window is measured now, the
//     partial coverage is stated in numbers, and the toolbar-window reading is pinned as a shortfall
//     the old measurement could not see.
//   * Tier 6 asserts what the shipped Config/dashboard-config.json says, so the two panels that
//     were given a trend cannot silently lose retention, and cannot go back to describing
//     themselves as a single reading.
//
// RED WITHOUT THE FEATURE. Every tier below 5 names metric_history, AppendMetricHistoryAsync,
// PruneMetricHistoryAsync or MetricHistoryTime, none of which exist at the base commit d2196aa —
// this file does not compile against it, which is the strongest form of red available for a
// storage feature.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using Xunit;

namespace SQLTriage.Tests
{
    public sealed class MetricRetentionTests : IDisposable
    {
        private readonly string _dir;
        private readonly List<IDisposable> _disposables = new();

        public MetricRetentionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sqlt-retention-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            foreach (var d in _disposables)
            {
                try { d.Dispose(); } catch { /* best effort */ }
            }
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        // ── Tier 1: the canonical time format ────────────────────────────────────────

        [Fact]
        public void Canonical_keys_are_fixed_width_and_sort_in_chronological_order()
        {
            var instants = new[]
            {
                new DateTime(2025, 12, 31, 23, 59, 59, DateTimeKind.Utc),
                new DateTime(2026, 01, 01, 00, 00, 00, DateTimeKind.Utc),
                new DateTime(2026, 01, 09, 09, 09, 09, DateTimeKind.Utc),
                new DateTime(2026, 01, 10, 10, 10, 10, DateTimeKind.Utc),
                new DateTime(2026, 08, 26, 19, 00, 00, DateTimeKind.Utc)
            };

            var keys = instants.Select(MetricHistoryTime.Format).ToArray();

            keys.Select(k => k.Length).Distinct().Should().ContainSingle(
                "the age prune is a string comparison; a variable-width key makes DELETE order lie");
            keys.Should().BeInAscendingOrder(StringComparer.Ordinal,
                "ordinal string order must equal chronological order or the row cap keeps the wrong rows");
        }

        [Fact]
        public void An_unspecified_time_is_read_as_local_wall_clock_and_round_trips()
        {
            // Panel rows arrive from GETDATE() with Kind=Unspecified. The dashboard already compares
            // them against DateTime.Now, so Unspecified means local here, and a round trip must not
            // move the wall clock.
            var wall = new DateTime(2026, 08, 26, 14, 30, 00, DateTimeKind.Unspecified);

            var stored = MetricHistoryTime.Format(wall);
            var back = MetricHistoryTime.ParseToLocal(stored);

            back.Should().Be(DateTime.SpecifyKind(wall, DateTimeKind.Local),
                "a retained sample must come back on the same clock the chart axis is drawn in");
        }

        [Fact]
        public void Samples_inside_one_bucket_collapse_to_one_key()
        {
            var bucket = TimeSpan.FromSeconds(60);
            var a = new DateTime(2026, 08, 26, 14, 30, 05, DateTimeKind.Utc);
            var b = new DateTime(2026, 08, 26, 14, 30, 55, DateTimeKind.Utc);
            var c = new DateTime(2026, 08, 26, 14, 31, 05, DateTimeKind.Utc);

            MetricHistoryTime.BucketKey(a, bucket).Should().Be(MetricHistoryTime.BucketKey(b, bucket),
                "downsampling is what bounds the row count independently of refresh rate");
            MetricHistoryTime.BucketKey(c, bucket).Should().NotBe(MetricHistoryTime.BucketKey(a, bucket));
            MetricHistoryTime.BucketKey(a, bucket).Should().Be("2026-08-26T14:30:00Z");
        }

        // ── Tier 2: retained history survives all three cache deleters ───────────────

        [Fact]
        public async Task Retained_history_survives_the_window_trim_the_eviction_sweep_and_a_full_invalidate()
        {
            var store = NewStore("survives");
            var at = DateTime.UtcNow.AddMinutes(-30);

            await store.UpsertTimeSeriesAsync("ag.send_queue", "SRV", Points(at, 5), DateTime.UtcNow);
            var written = await store.AppendMetricHistoryAsync(
                "ag.send_queue", "SRV", Points(at, 5), TimeSpan.FromSeconds(60));
            written.Should().Be(5);

            // Both writes above go through the batch write pump, which completes an operation's task
            // BEFORE the batch commits (see WaitForRows). Reading on the next line is how this cell
            // reddened CI run 33607957435 attempt 1 (branch lane/licence-card-hardening, sha 4a96ae3f)
            // with 0 rows — not a run on this lane, so name it in full or the next reader chases the
            // wrong history. Wait for each write the way the sibling cells do — the cache rows too,
            // because the three deleters below have to be deleting something real for their survival
            // claim to mean anything.
            await WaitForRows(store, written);
            await WaitForTimeSeriesRows(store, "ag.send_queue", "SRV", at.AddHours(-1), DateTime.UtcNow, 5);

            (await store.GetMetricHistoryAsync("ag.send_queue", "SRV", at.AddHours(-1), DateTime.UtcNow))
                .Should().HaveCount(5, "the samples were just retained");

            // Deleter 1 — the per-fetch window trim. CachingQueryExecutor calls this with the toolbar's
            // TimeFrom after EVERY delta fetch, which is why history could never outlive the window.
            await store.TrimTimeSeriesAsync("ag.send_queue", "SRV", DateTime.UtcNow);

            // Deleter 2 — the fetched_at sweep. Zero max-age evicts everything it can see.
            await store.EvictOlderThanAsync(TimeSpan.Zero);

            // Deleter 3 — the full invalidate, fired by any time-range, instance or timezone change.
            await store.InvalidateAllAsync();

            (await store.GetTimeSeriesAsync("ag.send_queue", "SRV", at.AddHours(-1), DateTime.UtcNow))
                .Should().BeEmpty("the cache is cache: all three deleters are supposed to empty it");

            (await store.GetMetricHistoryAsync("ag.send_queue", "SRV", at.AddHours(-1), DateTime.UtcNow))
                .Should().HaveCount(5,
                    "retained history is NOT a cache table. If any of the three reaches it, the AG trend "
                    + "and the 7-day baseline are back to being empty by design");
        }

        [Fact]
        public async Task Retained_history_is_readable_by_a_second_store_over_the_same_file()
        {
            // The restart claim, at the storage layer: values are plain REAL, not wrapped in the
            // per-process DataProtection key, so a new process reads what the old one wrote.
            var path = Path.Combine(_dir, "restart.db");
            var at = DateTime.UtcNow.AddMinutes(-10);

            var first = NewStoreAt(path);
            await first.AppendMetricHistoryAsync("ag.redo_queue", "SRV", Points(at, 3), TimeSpan.FromSeconds(60));
            first.Dispose();

            var second = NewStoreAt(path);
            var read = await second.GetMetricHistoryAsync("ag.redo_queue", "SRV", at.AddHours(-1), DateTime.UtcNow);

            read.Should().HaveCount(3, "retention that does not survive a restart is not retention");
            read.Select(p => p.Value).Should().BeEquivalentTo(new[] { 0.0, 1.0, 2.0 });
        }

        [Fact]
        public async Task A_later_sample_in_the_same_bucket_replaces_rather_than_accumulates()
        {
            var store = NewStore("bucket");
            var baseAt = new DateTime(2026, 08, 26, 12, 00, 00, DateTimeKind.Utc);

            await store.AppendMetricHistoryAsync("p", "SRV",
                new List<TimeSeriesPoint> { new() { Time = baseAt.AddSeconds(5), Series = "s", Value = 10 } },
                TimeSpan.FromSeconds(60));
            await store.AppendMetricHistoryAsync("p", "SRV",
                new List<TimeSeriesPoint> { new() { Time = baseAt.AddSeconds(50), Series = "s", Value = 20 } },
                TimeSpan.FromSeconds(60));

            // Same unwaited read-after-write as the survival cell above: both appends return before
            // the pump commits. This one cannot use WaitForRows, because both samples collapse into
            // ONE bucket and the row count never moves off 1. Wait on the replacing VALUE instead.
            (await WaitUntil(async () =>
                    (await store.GetMetricHistoryAsync("p", "SRV", baseAt.AddHours(-1), baseAt.AddHours(1)))
                        .Any(p => p.Value == 20)))
                .Should().BeTrue("the second append never committed, so the replace below is unmeasured");

            var read = await store.GetMetricHistoryAsync("p", "SRV", baseAt.AddHours(-1), baseAt.AddHours(1));
            read.Should().ContainSingle("one row per (panel, instance, series, bucket) is what bounds the table");
            read[0].Value.Should().Be(20, "the later sample in a bucket is the one that survives");
        }

        [Fact]
        public async Task An_unmeasured_instant_is_never_retained_as_a_number()
        {
            var store = NewStore("nan");
            var at = DateTime.UtcNow;

            var written = await store.AppendMetricHistoryAsync("p", "SRV", new List<TimeSeriesPoint>
            {
                new() { Time = at, Series = "a", Value = double.NaN },
                new() { Time = at, Series = "b", Value = 7 }
            }, TimeSpan.FromSeconds(60));

            written.Should().Be(1, "a NULL measurement is an unmeasured instant, never a fabricated zero");

            await WaitForRows(store, written);
            var read = await store.GetMetricHistoryAsync("p", "SRV", at.AddHours(-1), at.AddHours(1));
            read.Should().ContainSingle().Which.Series.Should().Be("b");
        }

        // ── Tier 3: the bounds ───────────────────────────────────────────────────────

        [Fact]
        public async Task The_age_prune_removes_only_what_falls_outside_the_window()
        {
            var store = NewStore("age");
            var now = DateTime.UtcNow;

            var written = await store.AppendMetricHistoryAsync("p", "SRV", new List<TimeSeriesPoint>
            {
                new() { Time = now.AddDays(-20), Series = "s", Value = 1 },
                new() { Time = now.AddDays(-9),  Series = "s", Value = 2 },
                new() { Time = now.AddDays(-3),  Series = "s", Value = 3 },
                new() { Time = now.AddMinutes(-1), Series = "s", Value = 4 }
            }, TimeSpan.FromSeconds(60));

            // NOT one of the unwaited reads, unlike the sibling cells: PruneMetricHistoryAsync is a
            // WRITER on its own connection, so under WAL its DELETE serialises behind the pump's open
            // transaction rather than reading past it. Measured with a 3 s commit hold parked behind
            // the append: the UNWAITED prune returned the correct byAge=2 after blocking 3134 ms,
            // while a reader taken at that same instant saw 0 rows. So this wait buys latency and an
            // explicit dependency, not a corrected answer — a cell that is right only by way of
            // SQLite's default command timeout is one pragma away from being wrong.
            await WaitForRows(store, written);

            var (byAge, byCap) = await store.PruneMetricHistoryAsync(now.AddDays(-8), maxRows: 100_000);

            byAge.Should().Be(2, "the 20-day and 9-day rows are outside an 8-day window");
            byCap.Should().Be(0, "four rows are nowhere near the cap");

            var left = await store.GetMetricHistoryAsync("p", "SRV", now.AddDays(-100), now.AddDays(1));
            left.Select(p => p.Value).Should().BeEquivalentTo(new[] { 3.0, 4.0 });
        }

        [Fact]
        public async Task The_row_cap_is_enforced_exactly_and_keeps_the_newest_rows()
        {
            var store = NewStore("cap");
            var start = new DateTime(2026, 08, 01, 00, 00, 00, DateTimeKind.Utc);

            // 50 distinct buckets, one minute apart, so nothing collapses.
            var points = Enumerable.Range(0, 50)
                .Select(i => new TimeSeriesPoint { Time = start.AddMinutes(i), Series = "s", Value = i })
                .ToList();
            var written = await store.AppendMetricHistoryAsync("p", "SRV", points, TimeSpan.FromSeconds(60));
            await WaitForRows(store, written);
            (await store.GetMetricHistoryRowCountAsync()).Should().Be(50);

            // A cutoff older than everything, so ONLY the cap can remove anything.
            var (byAge, byCap) = await store.PruneMetricHistoryAsync(start.AddDays(-1), maxRows: 20);

            byAge.Should().Be(0);
            byCap.Should().Be(30, "50 rows against a cap of 20 leaves exactly 20");
            (await store.GetMetricHistoryRowCountAsync()).Should().Be(20,
                "the cap is a hard bound on disk, so it cannot be approximate");

            var left = await store.GetMetricHistoryAsync("p", "SRV", start.AddDays(-1), start.AddDays(1));
            left.Select(p => p.Value).Min().Should().Be(30,
                "the cap must delete the OLDEST buckets. Keeping an arbitrary 20 would bound disk and "
                + "lose the trend, which is the thing retention exists to produce");
            left.Select(p => p.Value).Max().Should().Be(49);
        }

        [Fact]
        public async Task Coverage_reports_where_retained_history_actually_starts()
        {
            var store = NewStore("coverage");
            var now = DateTime.UtcNow;

            var empty = await store.GetMetricHistoryCoverageAsync("p", "SRV", now.AddHours(-24), now);
            empty.HasAnyHistory.Should().BeFalse();
            empty.IsShortOfWindow(now.AddHours(-24)).Should().BeTrue(
                "nothing retained is the most short a window can be, and the operator must be told");

            var written = await store.AppendMetricHistoryAsync("p", "SRV", new List<TimeSeriesPoint>
            {
                new() { Time = now.AddHours(-2), Series = "s", Value = 1 },
                new() { Time = now.AddHours(-1), Series = "s", Value = 2 }
            }, TimeSpan.FromSeconds(60));
            await WaitForRows(store, written);

            var partial = await store.GetMetricHistoryCoverageAsync("p", "SRV", now.AddHours(-24), now);
            partial.RowCount.Should().Be(2);
            partial.HasAnyHistory.Should().BeTrue();
            partial.IsShortOfWindow(now.AddHours(-24).ToLocalTime()).Should().BeTrue(
                "a 24-hour range backed by two hours of history is short, and the panel must say so");
            partial.IsShortOfWindow(now.AddMinutes(-30).ToLocalTime()).Should().BeFalse(
                "a 30-minute range IS covered; a notice on a whole trend would be noise");
        }

        // ── Tier 3b: the chart point cap is a resolution, not a shorter window ──────

        // The shipped cap is 2000 (UserSettingsService.GetChartDataPointCap, clamped 500-10000), and
        // the store falls back to the same 2000 with no settings service. These write past it on
        // purpose, against the default, because the default is what a client runs.
        private const int ShippedPointCap = 2000;

        [Fact]
        public async Task A_range_past_the_point_cap_keeps_its_newest_reading_and_its_whole_span()
        {
            // FOUND BY THE GATE, reproduced against the production read. Ascending order plus LIMIT
            // keeps the OLDEST rows: 3,000 sixty-second buckets rendered as a line that stopped 15h49m
            // before the newest retained sample, with the honesty notice silent. At the shipped bucket
            // that truncated every range past 33h20m on one series, which is both ranges the ruling
            // named, "Last 24 hours" at a fast refresh and "Last 7 days".
            var store = NewStore("cap-read");
            var start = new DateTime(2026, 08, 01, 00, 00, 00, DateTimeKind.Utc);
            const int written = ShippedPointCap + 500;

            var points = Enumerable.Range(0, written)
                .Select(i => new TimeSeriesPoint { Time = start.AddMinutes(i), Series = "s", Value = i })
                .ToList();
            await store.AppendMetricHistoryAsync("p", "SRV", points, TimeSpan.FromSeconds(60));
            await WaitForRows(store, written);

            var read = await store.GetMetricHistoryAsync(
                "p", "SRV", start.AddMinutes(-1), start.AddMinutes(written + 1));

            read.Count.Should().BeLessThanOrEqualTo(ShippedPointCap,
                "the cap is a hard bound on what a chart is asked to draw");
            read.Select(p => p.Time).Should().BeInAscendingOrder();

            read.Last().Time.Should().Be(start.AddMinutes(written - 1).ToLocalTime(),
                "the newest retained reading is the one an operator watching a queue is watching for. "
                + "Dropping it is the defect, and dropping it silently is the worse half of it");
            read.First().Time.Should().Be(start.ToLocalTime(),
                "the old end of the range is not shortened either. The window is what was asked for");

            var expected = points.ToDictionary(p => p.Time.ToLocalTime(), p => p.Value);
            foreach (var p in read)
            {
                expected.Should().ContainKey(p.Time,
                    "every plotted instant must be one that was actually retained");
                p.Value.Should().Be(expected[p.Time],
                    "the reduction is a subset of real measurements. An average or an interpolation "
                    + "would put a number on the chart that nothing ever measured");
            }
        }

        [Fact]
        public async Task Two_series_past_the_cap_each_keep_their_newest_and_oldest_point()
        {
            // Two series is the normal case on a real AG: the queue panels key per replica and
            // database. It is also where the old read broke first, at half the buckets.
            var store = NewStore("cap-read-2");
            var start = new DateTime(2026, 08, 01, 00, 00, 00, DateTimeKind.Utc);
            const int perSeries = 1400; // 2,800 rows against a 2,000 cap

            var points = new List<TimeSeriesPoint>();
            foreach (var series in new[] { "replica-a", "replica-b" })
                points.AddRange(Enumerable.Range(0, perSeries).Select(i => new TimeSeriesPoint
                {
                    Time = start.AddMinutes(i),
                    Series = series,
                    Value = i
                }));
            await store.AppendMetricHistoryAsync("p", "SRV", points, TimeSpan.FromSeconds(60));
            await WaitForRows(store, perSeries * 2);

            var read = await store.GetMetricHistoryAsync(
                "p", "SRV", start.AddMinutes(-1), start.AddMinutes(perSeries + 1));

            read.Count.Should().BeLessThanOrEqualTo(ShippedPointCap,
                "each series rounds its own count up, so the bound has to hold across all of them");

            foreach (var group in read.GroupBy(p => p.Series))
            {
                group.Should().HaveCountGreaterThan(1,
                    "{0} must still be a line, not a dot", group.Key);
                group.Max(p => p.Time).Should().Be(start.AddMinutes(perSeries - 1).ToLocalTime(),
                    "{0} keeps its newest reading", group.Key);
                group.Min(p => p.Time).Should().Be(start.ToLocalTime(),
                    "{0} still spans the requested window", group.Key);
            }

            read.Select(p => p.Series).Distinct().Should().HaveCount(2,
                "a cap that dropped a whole replica would hide the replica that is behind");
        }

        [Fact]
        public async Task A_range_the_chart_has_to_sample_says_so_and_a_whole_one_stays_silent()
        {
            var store = NewStore("cap-coverage");
            var start = new DateTime(2026, 08, 01, 00, 00, 00, DateTimeKind.Utc);

            await store.AppendMetricHistoryAsync("p", "SRV",
                Points(start, 10), TimeSpan.FromSeconds(60));
            await WaitForRows(store, 10);

            var whole = await store.GetMetricHistoryCoverageAsync(
                "p", "SRV", start.AddMinutes(-1), start.AddMinutes(11));
            whole.PointCap.Should().Be(ShippedPointCap,
                "the cap has to travel with the count or the caller cannot tell the two apart");
            whole.IsDownsampled.Should().BeFalse("ten points under a 2000-point cap are all drawn");

            await store.AppendMetricHistoryAsync("p", "SRV",
                Enumerable.Range(0, ShippedPointCap + 500)
                    .Select(i => new TimeSeriesPoint { Time = start.AddMinutes(i), Series = "s", Value = i })
                    .ToList(),
                TimeSpan.FromSeconds(60));
            await WaitForRows(store, ShippedPointCap + 500);

            var sampled = await store.GetMetricHistoryCoverageAsync(
                "p", "SRV", start.AddMinutes(-1), start.AddMinutes(ShippedPointCap + 501));
            sampled.RowCount.Should().Be(ShippedPointCap + 500);
            sampled.IsDownsampled.Should().BeTrue(
                "a line drawn from a subset of what was recorded is the second way a whole-looking "
                + "trend is not one, and the operator is owed both");
            sampled.IsShortOfWindow(start.ToLocalTime()).Should().BeFalse(
                "this range IS covered end to end. Reusing the short-of-window notice for it would "
                + "tell the operator the wrong thing about their own data");
        }

        // ── Tier 4: the eviction-sweep trap, shut structurally ──────────────────────

        [Fact]
        public async Task The_eviction_sweep_cannot_discover_retained_history()
        {
            var store = NewStore("sweep");
            await store.AppendMetricHistoryAsync("p", "SRV",
                Points(DateTime.UtcNow.AddMinutes(-5), 2), TimeSpan.FromSeconds(60));

            using var conn = store.CreateExternalConnection();
            using var cmd = conn.CreateCommand();
            // This is EvictOlderThanCore's own discovery query (SqliteCacheStore).
            cmd.CommandText = @"
                SELECT DISTINCT m.name
                FROM sqlite_master m
                JOIN pragma_table_info(m.name) c ON c.name = 'fetched_at'
                WHERE m.type = 'table'";

            var swept = new List<string>();
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) swept.Add(reader.GetString(0));
            }

            swept.Should().NotBeEmpty("the sweep is supposed to find the cache tables");
            swept.Should().NotContain("metric_history",
                "a fetched_at column on metric_history would delete retained history at 24 hours AND, if "
                + "the table were ever dropped from AllowedTables, would throw inside the sweep — a fault "
                + "CacheEvictionService swallows as 'Cache eviction failed', killing ALL eviction");
        }

        // ── Tier 5: the honesty text ────────────────────────────────────────────────

        [Fact]
        public void The_baseline_notice_names_the_reason_rather_than_apologising_generically()
        {
            var now = new DateTime(2026, 08, 26, 19, 00, 00, DateTimeKind.Local);
            var whole = WholeWindow(now);

            var drawn = SQLTriage.Components.Shared.DynamicDashboard.BuildBaselineNotice(
                baselineEmpty: false, tsPanelCount: 3, retainedPanelCount: 2,
                earliestRetained: now.AddDays(-1), now: now, coverage: whole);
            drawn.Should().BeEmpty("a baseline drawn from whole coverage needs no notice, and silence "
                + "means whole is the only reason the notice is worth reading");

            var noRetention = SQLTriage.Components.Shared.DynamicDashboard.BuildBaselineNotice(
                baselineEmpty: true, tsPanelCount: 3, retainedPanelCount: 0,
                earliestRetained: null, now: now, coverage: whole);
            noRetention.Should().Contain("no panel on this dashboard retains history",
                "there is no waiting this one out; the operator needs to know it will never appear");
            noRetention.Should().NotContain("becomes available",
                "promising a date that will never arrive is the dishonest version of this message");

            var tooYoung = SQLTriage.Components.Shared.DynamicDashboard.BuildBaselineNotice(
                baselineEmpty: true, tsPanelCount: 3, retainedPanelCount: 2,
                earliestRetained: now.AddDays(-2), now: now, coverage: whole);
            tooYoung.Should().Contain("24 Aug 2026 19:00",
                "the notice states the instant retained history actually starts, not a vague 'recently'");
            tooYoung.Should().Contain("31 Aug 2026 19:00",
                "and the date the baseline becomes real, so the operator can wait for it deliberately");
            tooYoung.Should().Contain("does not fill in the past",
                "the ruling's honesty bar, in the words the operator reads");

            var nothingYet = SQLTriage.Components.Shared.DynamicDashboard.BuildBaselineNotice(
                baselineEmpty: true, tsPanelCount: 3, retainedPanelCount: 2,
                earliestRetained: null, now: now, coverage: whole);
            nothingYet.Should().Contain("nothing has been recorded so far",
                "retention configured but not yet producing is a third, distinct fact");
        }

        // A baseline that WAS drawn used to return "" unconditionally, however thin it was. That is the
        // gate's blocking defect and the shape these three tests close: the notice measures the SHIFTED
        // window the baseline actually read, and speaks when the comparison covers only part of it.

        [Fact]
        public void A_partial_baseline_says_how_much_of_the_shifted_window_it_drew()
        {
            var now = new DateTime(2026, 08, 26, 19, 00, 00, DateTimeKind.Local);
            var windowFrom = now.AddDays(-14);   // "Last 7 days" reads 14 days back to 7 days back
            var windowTo = now.AddDays(-7);

            var partial = SQLTriage.Components.Shared.DynamicDashboard.BuildBaselineNotice(
                baselineEmpty: false, tsPanelCount: 3, retainedPanelCount: 2,
                earliestRetained: now.AddDays(-8), now: now,
                coverage: new SQLTriage.Components.Shared.DynamicDashboard.BaselineWindowCoverage(
                    windowFrom, windowTo,
                    ShortPanels: new[] { "Send Queue (23 hours 50 minutes of the 7 days asked for, from 19 Aug 2026 00:42)" },
                    SampledPanels: Array.Empty<string>(),
                    LiveCachePanels: Array.Empty<string>()));

            partial.Should().NotBeEmpty(
                "a comparison drawn from a fraction of its window is the exact case ruling 4 names: a "
                + "range with insufficient retained history must still say so");
            partial.Should().Contain("12 Aug 2026 19:00",
                "the operator is told which window the baseline read, not left to infer the 7-day shift");
            partial.Should().Contain("19 Aug 2026 19:00");
            partial.Should().Contain("23 hours 50 minutes of the 7 days asked for",
                "the measured span against the asked-for span, in numbers rather than an adjective");
            partial.Should().Contain("it was never recorded",
                "a thin line reads as never recorded, never as a quiet week on the server");
            partial.Should().Contain("does not fill in the past", "the ruling's bar, in the operator's words");

            var sampled = SQLTriage.Components.Shared.DynamicDashboard.BuildBaselineNotice(
                baselineEmpty: false, tsPanelCount: 3, retainedPanelCount: 2,
                earliestRetained: now.AddDays(-20), now: now,
                coverage: new SQLTriage.Components.Shared.DynamicDashboard.BaselineWindowCoverage(
                    windowFrom, windowTo,
                    ShortPanels: Array.Empty<string>(),
                    SampledPanels: new[] { "Send Queue (2500 retained points against a 2000-point chart limit)" },
                    LiveCachePanels: Array.Empty<string>()));
            sampled.Should().Contain("subset of the retained points",
                "a baseline strided down to the chart cap is the second way it is less than it looks, "
                + "and it is not the same fact as short coverage");
            sampled.Should().NotContain("never recorded",
                "history that reaches the whole window was recorded; saying otherwise would be false");

            var fromCache = SQLTriage.Components.Shared.DynamicDashboard.BuildBaselineNotice(
                baselineEmpty: false, tsPanelCount: 3, retainedPanelCount: 0,
                earliestRetained: null, now: now,
                coverage: new SQLTriage.Components.Shared.DynamicDashboard.BaselineWindowCoverage(
                    windowFrom, windowTo,
                    ShortPanels: Array.Empty<string>(),
                    SampledPanels: Array.Empty<string>(),
                    LiveCachePanels: new[] { "Batch Requests (drawn 19 Aug 2026 18:55 to 19 Aug 2026 19:00)" }));
            fromCache.Should().Contain("trimmed to the current window",
                "a comparison served from the volatile cache is not a record of 7 days ago, and the "
                + "operator is told which store answered");
        }

        [Fact]
        public void The_shortfall_predicate_reads_the_shifted_window_and_nothing_else()
        {
            var now = new DateTime(2026, 08, 26, 19, 00, 00, DateTimeKind.Local);
            var shiftedFrom = now.AddDays(-14);
            var shiftedTo = now.AddDays(-7);

            // The steady state: 8 days of retained history, a "Last 7 days" range. Whole against the
            // TOOLBAR window, six days short against the window the baseline actually reads.
            var eightDays = new RetentionCoverage(144, now.AddDays(-8), 2000);

            eightDays.IsShortOfWindow(now.AddDays(-7)).Should().BeFalse(
                "measured against the toolbar window this coverage is whole, which is exactly why "
                + "measuring the toolbar window kept a partial baseline silent");

            var shortfall = SQLTriage.Components.Shared.DynamicDashboard.DescribeBaselineShortfall(
                "Send Queue", eightDays, shiftedFrom, shiftedTo,
                drawnFrom: now.AddDays(-8), drawnTo: now.AddDays(-7));
            shortfall.Should().NotBeNull("against the shifted window the same coverage is six days short");
            shortfall.Should().Contain("1 day of the 7 days asked for",
                "the two measured spans, so the operator can see the size of the gap");

            var deepHistory = new RetentionCoverage(1000, now.AddDays(-20), 2000);
            SQLTriage.Components.Shared.DynamicDashboard.DescribeBaselineShortfall(
                    "Send Queue", deepHistory, shiftedFrom, shiftedTo,
                    drawnFrom: shiftedFrom, drawnTo: shiftedTo)
                .Should().BeNull("history that predates the shifted window covers it, and a notice on a "
                + "whole comparison would be noise that teaches the operator to ignore the notice");

            SQLTriage.Components.Shared.DynamicDashboard.DescribeBaselineShortfall(
                    "Send Queue", new RetentionCoverage(0, null, 2000), shiftedFrom, shiftedTo,
                    drawnFrom: null, drawnTo: null)
                .Should().Contain("nothing retained for that window",
                    "a retained panel that contributed no comparison at all is named too, rather than "
                    + "vanishing from a baseline other panels did draw");
        }

        [Fact]
        public async Task The_steady_state_partial_baseline_is_reproduced_through_the_production_store()
        {
            // The gate's scenario, deterministic: a mature install sampling every 10 minutes for 30
            // days, pruned at the shipped 8-day window, operator on "Last 7 days" with Baseline on.
            var store = NewStore("steadybaseline");
            var now = new DateTime(2026, 08, 26, 19, 00, 00, DateTimeKind.Local);

            var samples = Enumerable.Range(0, 30 * 24 * 6)
                .Select(i => new TimeSeriesPoint
                {
                    Time = now.AddDays(-30).AddMinutes(10 * i),
                    Series = "s",
                    Value = i
                })
                .ToList();
            await store.AppendMetricHistoryAsync("p", "SRV", samples, TimeSpan.FromSeconds(60));
            await WaitForRows(store, samples.Count);

            var (byAge, _) = await store.PruneMetricHistoryAsync(now.AddDays(-8), maxRows: 1_000_000);
            byAge.Should().BeGreaterThan(0, "the shipped 8-day window prunes 22 of those 30 days");

            var toolbarFrom = now.AddDays(-7);
            var shiftedFrom = now.AddDays(-14);
            var shiftedTo = now.AddDays(-7);

            var toolbar = await store.GetMetricHistoryCoverageAsync("p", "SRV", toolbarFrom, now);
            toolbar.IsShortOfWindow(toolbarFrom).Should().BeFalse(
                "the toolbar window IS covered here, so the old measurement had nothing to report");

            var shifted = await store.GetMetricHistoryCoverageAsync("p", "SRV", shiftedFrom, shiftedTo);
            shifted.RowCount.Should().BeGreaterThan(0, "the baseline did draw something");
            shifted.RowCount.Should().BeLessThan(toolbar.RowCount,
                "and it drew from a fraction of the window it was asked for");
            shifted.IsShortOfWindow(shiftedFrom).Should().BeTrue(
                "8 days of retention cannot reach 14 days back, so a 7-day baseline is short of its "
                + "window in the steady state, permanently, and must say so");

            var drawn = await store.GetMetricHistoryAsync("p", "SRV", shiftedFrom, shiftedTo);
            drawn.Should().NotBeEmpty();

            var notice = SQLTriage.Components.Shared.DynamicDashboard.BuildBaselineNotice(
                baselineEmpty: false, tsPanelCount: 2, retainedPanelCount: 1,
                earliestRetained: shifted.EarliestRetainedLocal, now: now,
                coverage: new SQLTriage.Components.Shared.DynamicDashboard.BaselineWindowCoverage(
                    shiftedFrom, shiftedTo,
                    ShortPanels: new[]
                    {
                        SQLTriage.Components.Shared.DynamicDashboard.DescribeBaselineShortfall(
                            "Send Queue", shifted, shiftedFrom, shiftedTo,
                            drawn.Min(p => p.Time), drawn.Max(p => p.Time))!
                    },
                    SampledPanels: Array.Empty<string>(),
                    LiveCachePanels: Array.Empty<string>()));

            notice.Should().NotBeEmpty(
                "this is the exact render the gate proved silent: a baseline drawn from a fraction of "
                + "its window with every honesty surface saying nothing");
            notice.Should().Contain("of the 7 days asked for");
            notice.Should().Contain(drawn.Min(p => p.Time).ToString("d MMM yyyy HH:mm"),
                "the notice carries the instant the comparison actually starts, read off the points "
                + "that will be drawn rather than assumed from the window");
        }

        // Measuring only the FRONT of the shifted window closed one arm of the gate's defect and left
        // two open. Both were reproduced live through the production store and these builders before
        // the code below existed: a panel that stopped recording drew 14 hours 10 minutes of a 24-hour
        // comparison in silence, and a panel whose window fell in a recording gap drew no line at all,
        // beside another panel's full line, also in silence.

        [Fact]
        public void A_panel_whose_recording_stopped_says_so_and_is_not_called_short_at_the_front()
        {
            var now = new DateTime(2026, 08, 26, 19, 00, 00, DateTimeKind.Local);
            var windowFrom = now.AddDays(-8);    // "Last 24 hours" reads 8 days back to 7 days back
            var windowTo = now.AddDays(-7);

            // Deep history that reaches well past the start of the window, and stops 7.4 days ago.
            var stopped = new RetentionCoverage(86, now.AddDays(-15), 2000, now.AddDays(-7.4));

            stopped.IsShortOfWindow(windowFrom).Should().BeFalse(
                "history from 15 days ago reaches the start of the window, which is exactly why the "
                + "front-of-window predicate reported nothing while the line stopped halfway");
            stopped.EndsBeforeWindowCloses(windowTo).Should().BeTrue(
                "and stops before the window closes, which is the fact the front predicate cannot see");

            var text = SQLTriage.Components.Shared.DynamicDashboard.DescribeBaselineStoppage(
                "Redo Queue", stopped, windowFrom, windowTo,
                drawnFrom: windowFrom, drawnTo: now.AddDays(-7.4));
            text.Should().NotBeNull();
            text.Should().Contain("of the 1 day asked for",
                "the measured span against the asked-for span, in numbers rather than an adjective");
            text.Should().Contain("recording stopped",
                "the operator is told the line stops because the recording did, not because the metric "
                + "went quiet");

            var notice = SQLTriage.Components.Shared.DynamicDashboard.BuildBaselineNotice(
                baselineEmpty: false, tsPanelCount: 2, retainedPanelCount: 2,
                earliestRetained: now.AddDays(-15), now: now,
                coverage: new SQLTriage.Components.Shared.DynamicDashboard.BaselineWindowCoverage(
                    windowFrom, windowTo,
                    ShortPanels: Array.Empty<string>(),
                    SampledPanels: Array.Empty<string>(),
                    LiveCachePanels: Array.Empty<string>(),
                    StoppedPanels: new[] { text! }));

            notice.Should().NotBeEmpty(
                "a comparison that stops halfway across the chart is insufficient retained history, "
                + "which ruling 4 says must still be said out loud");
            notice.Should().Contain("stops before the END of that window");
            notice.Should().NotContain("does not reach the start of that window",
                "the two shortfalls are opposite ends of the window and blurring them would tell the "
                + "operator to look in the wrong place");

            // A running collector cannot trip it. Its newest sample is always later than a window that
            // closed at least 7 days ago, so this is silent by arithmetic, not by a tuned tolerance.
            var running = new RetentionCoverage(144, now.AddDays(-15), 2000, now.AddMinutes(-1));
            running.EndsBeforeWindowCloses(windowTo).Should().BeFalse();
            SQLTriage.Components.Shared.DynamicDashboard.DescribeBaselineStoppage(
                    "Redo Queue", running, windowFrom, windowTo, windowFrom, windowTo)
                .Should().BeNull("silence has to keep meaning whole or the notice is wallpaper");

            // A panel with nothing at all is the front predicate's fact, and must be reported once.
            SQLTriage.Components.Shared.DynamicDashboard.DescribeBaselineStoppage(
                    "Redo Queue", new RetentionCoverage(0, null, 2000), windowFrom, windowTo, null, null)
                .Should().BeNull("nothing ever retained is one problem, not two");
        }

        [Fact]
        public void A_panel_whose_window_fell_in_a_recording_gap_is_named_rather_than_missing()
        {
            var now = new DateTime(2026, 08, 26, 19, 00, 00, DateTimeKind.Local);
            var windowFrom = now.AddDays(-8);
            var windowTo = now.AddDays(-7);

            // History on BOTH sides of the window and nothing inside it: neither boundary predicate can
            // see this, and the panel draws no comparison line at all.
            var gapped = new RetentionCoverage(0, now.AddDays(-15), 2000, now);

            gapped.IsShortOfWindow(windowFrom).Should().BeFalse();
            gapped.EndsBeforeWindowCloses(windowTo).Should().BeFalse();
            gapped.WindowFallsInAGap(windowFrom, windowTo).Should().BeTrue(
                "zero rows inside a window that history brackets on both sides is a hole, and zero "
                + "decides it with no tolerance to argue about");

            var text = SQLTriage.Components.Shared.DynamicDashboard.DescribeBaselineGap(
                "Redo Queue", gapped, windowFrom, windowTo);
            text.Should().NotBeNull();
            text.Should().Contain("no retained samples inside that window");

            var notice = SQLTriage.Components.Shared.DynamicDashboard.BuildBaselineNotice(
                baselineEmpty: false, tsPanelCount: 2, retainedPanelCount: 2,
                earliestRetained: now.AddDays(-15), now: now,
                coverage: new SQLTriage.Components.Shared.DynamicDashboard.BaselineWindowCoverage(
                    windowFrom, windowTo,
                    ShortPanels: Array.Empty<string>(),
                    SampledPanels: Array.Empty<string>(),
                    LiveCachePanels: Array.Empty<string>(),
                    GapPanels: new[] { text! }));

            notice.Should().NotBeEmpty(
                "a missing comparison line is the largest shortfall there is; a baseline that draws one "
                + "panel and silently drops another is the worst arm of the defect, not the smallest");
            notice.Should().Contain("no comparison line on this chart");
            notice.Should().Contain("returned no rows",
                "the collector skips a write both when it cannot reach the server and when the query "
                + "returns nothing, and after the fact the two are not distinguishable — saying which "
                + "one it was would be a fabrication");

            // Rows inside the window means there is no hole, whatever else is true of the line.
            new RetentionCoverage(5, now.AddDays(-15), 2000, now)
                .WindowFallsInAGap(windowFrom, windowTo).Should().BeFalse();
        }

        [Fact]
        public async Task The_stopped_and_gapped_baselines_are_reproduced_through_the_production_store()
        {
            // Both arms end to end on real pruned rows, at a retention window wide enough for the front
            // predicate to fall silent. MetricRetention:WindowDays binds and clamps 1 to 90, so 15 days
            // is shippable configuration and not a contrivance.
            var store = NewStore("stoppedbaseline");
            var now = new DateTime(2026, 08, 26, 19, 00, 00, DateTimeKind.Local);
            var window = TimeSpan.FromDays(15);

            // Healthy panel: 30 days to now. Stopped panel: 30 days ago until 7.4 days ago.
            // Gapped panel: 30 to 9 days ago, then 6 days ago to now. The window sits in the hole.
            var batches = new (string Panel, List<TimeSeriesPoint> Points)[]
            {
                ("healthy", Every10Minutes(now, 30, 0)),
                ("stopped", Every10Minutes(now, 30, 7.4)),
                ("gapped", Every10Minutes(now, 30, 9)),
                ("gapped", Every10Minutes(now, 6, 0)),
            };
            foreach (var (panel, points) in batches)
                await store.AppendMetricHistoryAsync(panel, "SRV", points, TimeSpan.FromSeconds(60));
            await WaitForRows(store, batches.Sum(b => b.Points.Count));

            await store.PruneMetricHistoryAsync(now - window, maxRows: 1_000_000);

            var windowFrom = now.AddDays(-8);   // "Last 24 hours", shifted 7 days
            var windowTo = now.AddDays(-7);

            var healthy = await store.GetMetricHistoryCoverageAsync("healthy", "SRV", windowFrom, windowTo);
            healthy.IsShortOfWindow(windowFrom).Should().BeFalse();
            healthy.EndsBeforeWindowCloses(windowTo).Should().BeFalse();
            healthy.WindowFallsInAGap(windowFrom, windowTo).Should().BeFalse(
                "the healthy panel is the control: if it speaks, the notice is wallpaper");

            var stopped = await store.GetMetricHistoryCoverageAsync("stopped", "SRV", windowFrom, windowTo);
            stopped.RowCount.Should().BeGreaterThan(0, "it drew a line");
            stopped.RowCount.Should().BeLessThan(healthy.RowCount, "and a shorter one than the whole window");
            stopped.IsShortOfWindow(windowFrom).Should().BeFalse(
                "which is why measuring only the front of the window kept this silent");
            stopped.EndsBeforeWindowCloses(windowTo).Should().BeTrue();

            var gapped = await store.GetMetricHistoryCoverageAsync("gapped", "SRV", windowFrom, windowTo);
            gapped.RowCount.Should().Be(0, "the window fell inside the recording gap");
            gapped.IsShortOfWindow(windowFrom).Should().BeFalse();
            gapped.EndsBeforeWindowCloses(windowTo).Should().BeFalse();
            gapped.WindowFallsInAGap(windowFrom, windowTo).Should().BeTrue();

            var drawn = await store.GetMetricHistoryAsync("stopped", "SRV", windowFrom, windowTo);
            var notice = SQLTriage.Components.Shared.DynamicDashboard.BuildBaselineNotice(
                baselineEmpty: false, tsPanelCount: 3, retainedPanelCount: 3,
                earliestRetained: healthy.EarliestRetainedLocal, now: now,
                coverage: new SQLTriage.Components.Shared.DynamicDashboard.BaselineWindowCoverage(
                    windowFrom, windowTo,
                    ShortPanels: Array.Empty<string>(),
                    SampledPanels: Array.Empty<string>(),
                    LiveCachePanels: Array.Empty<string>(),
                    StoppedPanels: new[]
                    {
                        SQLTriage.Components.Shared.DynamicDashboard.DescribeBaselineStoppage(
                            "Redo Queue", stopped, windowFrom, windowTo,
                            drawn.Min(p => p.Time), drawn.Max(p => p.Time))!
                    },
                    GapPanels: new[]
                    {
                        SQLTriage.Components.Shared.DynamicDashboard.DescribeBaselineGap(
                            "Send Queue", gapped, windowFrom, windowTo)!
                    }));

            notice.Should().NotBeEmpty("this is the exact render the probe proved silent");
            notice.Should().Contain("Redo Queue");
            notice.Should().Contain("Send Queue");
        }

        /// <summary>The production caller must measure the window the baseline READS, and must report
        /// all three shortfalls. Structural, because the defect was an argument and then a missing
        /// call, not a branch: the loop compiled and passed either way.</summary>
        [Fact]
        public void The_baseline_coverage_call_passes_the_shifted_window()
        {
            var razor = File.ReadAllText(Path.Combine(
                RawPassedScan.RepoRoot().FullName, "Components", "Shared", "DynamicDashboard.razor"));

            var load = razor.IndexOf("private async Task LoadBaselineDataAsync", StringComparison.Ordinal);
            load.Should().BeGreaterThan(0, "the baseline loader is what this pins; a rename must fail loudly");
            var end = razor.IndexOf("_baselineResults = baselineResults;", load, StringComparison.Ordinal);
            end.Should().BeGreaterThan(load, "the loader's end marker moved; this pin is measuring nothing");

            // Whitespace-collapsed, so reformatting the call cannot break the pin and cannot pass it either.
            var body = Regex.Replace(razor.Substring(load, end - load), @"\s+", " ");

            body.Should().Contain("GetRetentionCoverageAsync( panel.Id, currentFilter, baselineFrom, baselineTo)",
                "coverage measured over currentFilter's own window describes a window the baseline "
                + "never opened, which is how a 23-hour comparison rendered as a 7-day one in silence");
            body.Should().Contain("var baselineFrom = currentFilter.TimeFrom - shift;",
                "and those bounds are the shift the baseline read itself uses, not a second copy that "
                + "could drift away from it");

            body.Should().Contain("DescribeBaselineStoppage( panel.Title, coverage, baselineFrom, baselineTo, drawnFrom, drawnTo)",
                "a shortfall the loop measures but never calls is a shortfall the operator never sees, "
                + "and the first fix round measured only the front of the window");
            body.Should().Contain("DescribeBaselineGap(panel.Title, coverage, baselineFrom, baselineTo)",
                "a panel that draws no comparison line at all must be named rather than vanish from a "
                + "baseline the other panels did draw");
            // The handoff to the notice builder sits just past the loop's end marker, so it needs its
            // own slice. Measured and never handed over is the same silence by a longer route.
            var assign = razor.IndexOf("_baselineNotice = _showBaseline", end, StringComparison.Ordinal);
            assign.Should().BeGreaterThan(end, "the notice assignment moved; this pin is measuring nothing");
            var tail = razor.IndexOf("await SafeStateHasChanged();", assign, StringComparison.Ordinal);
            tail.Should().BeGreaterThan(assign);

            Regex.Replace(razor.Substring(assign, tail - assign), @"\s+", " ")
                .Should().Contain("stoppedPanels, gapPanels)",
                    "the two lists the loop fills must reach BuildBaselineNotice, or the operator is "
                    + "told nothing by a longer route");
        }

        [Fact]
        public void Merging_retained_history_with_the_live_cache_never_double_plots_an_instant()
        {
            var t0 = new DateTime(2026, 08, 26, 12, 00, 00, DateTimeKind.Local);
            var retained = new List<TimeSeriesPoint>
            {
                new() { Time = t0, Series = "s", Value = 1 },
                new() { Time = t0.AddMinutes(1), Series = "s", Value = 2 }
            };
            var live = new List<TimeSeriesPoint>
            {
                new() { Time = t0.AddMinutes(1), Series = "s", Value = 99 },  // same instant, same series
                new() { Time = t0.AddMinutes(2), Series = "s", Value = 3 }
            };

            var merged = CachingQueryExecutor.MergeRetainedWithLive(retained, live);

            merged.Should().HaveCount(3, "the overlapping instant is one point, not two");
            merged.Select(p => p.Time).Should().BeInAscendingOrder();
            merged.Single(p => p.Time == t0.AddMinutes(1)).Value.Should().Be(99,
                "the newest retained bucket is still forming, so the cache holds the fresher reading "
                + "for that instant. Every bucket BEFORE the cut is the retained record");
            merged.First().Value.Should().Be(1, "closed retained buckets are the record");
            merged.Last().Value.Should().Be(3, "the newest live sample still reaches the chart");
        }

        [Fact]
        public void Merging_holds_the_boundary_between_two_time_bases_and_plots_each_reading_once()
        {
            // FOUND BY THE GATE. The two sides are on different time bases: retained rows are floored
            // to a bucket, live cache rows carry the raw GETDATE() instant. De-duplicating on an exact
            // (series, instant) match therefore never fired in production, and every open bucket was
            // drawn twice, once at the bucket start and again at the raw instant of the same reading.
            // The old test passed only because its fixture handed both lists the same instant.
            var bucket0 = new DateTime(2026, 08, 26, 12, 00, 00, DateTimeKind.Local);
            var retained = new List<TimeSeriesPoint>
            {
                new() { Time = bucket0, Series = "s", Value = 10 },
                new() { Time = bucket0.AddMinutes(1), Series = "s", Value = 20 }
            };
            var live = Enumerable.Range(0, 12)
                .Select(i => new TimeSeriesPoint
                {
                    Time = bucket0.AddSeconds(7 + (i * 10)),   // 12:00:07 .. 12:01:57, a 10s refresh
                    Series = "s",
                    Value = 100 + i
                })
                .ToList();

            var merged = CachingQueryExecutor.MergeRetainedWithLive(retained, live);

            merged.Select(p => p.Time).Should().OnlyHaveUniqueItems(
                "one measurement is one point. Fourteen points from twelve readings is the defect");
            merged.Should().HaveCount(7,
                "the closed 12:00 bucket summarises its minute, and the cache owns everything from the "
                + "12:01 boundary on: one bucket plus six raw samples");
            merged.Should().NotContain(p => p.Time == bucket0.AddMinutes(1),
                "the open bucket holds a copy of a reading the cache is already plotting at its real "
                + "instant, so plotting it at the bucket start puts that reading on the chart 57 "
                + "seconds early");
            merged.First().Time.Should().Be(bucket0);
            merged.First().Value.Should().Be(10, "the closed bucket is the record for its minute");
            merged.Last().Time.Should().Be(bucket0.AddSeconds(117),
                "and the freshest live sample is still the last point on the chart");
            merged.Skip(1).Should().OnlyContain(p => p.Time >= bucket0.AddMinutes(1),
                "no live sample from inside a closed bucket is replotted beside it");
        }

        [Fact]
        public void A_series_the_cache_alone_has_seen_is_passed_through_whole()
        {
            // A replica that appears mid-window has no retained bucket yet. Filtering it against
            // another series' boundary would shorten a line nobody was told about.
            var t0 = new DateTime(2026, 08, 26, 12, 00, 00, DateTimeKind.Local);
            var retained = new List<TimeSeriesPoint>
            {
                new() { Time = t0.AddMinutes(5), Series = "replica-a", Value = 1 }
            };
            var live = new List<TimeSeriesPoint>
            {
                new() { Time = t0.AddMinutes(1), Series = "replica-b", Value = 2 },
                new() { Time = t0.AddMinutes(2), Series = "replica-b", Value = 3 }
            };

            var merged = CachingQueryExecutor.MergeRetainedWithLive(retained, live);

            merged.Where(p => p.Series == "replica-b").Should().HaveCount(2,
                "a series with no retained history of its own is the cache's alone");
            merged.Should().HaveCount(3,
                "and the retained series keeps its only bucket, which no live sample supersedes");
        }

        [Fact]
        public void The_retention_notice_separates_a_short_window_from_a_sampled_one()
        {
            var quiet = SQLTriage.Components.Shared.DynamicDashboard.BuildRetentionNotice(
                Array.Empty<string>(), Array.Empty<string>());
            quiet.Should().BeEmpty(
                "silence is the signal that a line is whole. A notice on every load is noise");

            var shortOnly = SQLTriage.Components.Shared.DynamicDashboard.BuildRetentionNotice(
                new[] { "Redo Queue (retained from 26 Aug 2026 21:02)" }, Array.Empty<string>());
            shortOnly.Should().Contain("was never recorded",
                "the early part of a short line is not missing data");
            shortOnly.Should().NotContain("chart draws",
                "a whole-resolution line must not be described as sampled");

            var sampledOnly = SQLTriage.Components.Shared.DynamicDashboard.BuildRetentionNotice(
                Array.Empty<string>(),
                new[] { "Log Send Queue (2500 retained points against a 2000-point chart limit)" });
            sampledOnly.Should().Contain("more retained points than the chart draws");
            sampledOnly.Should().Contain("2500 retained points against a 2000-point chart limit",
                "the operator gets the two measured numbers, not an adjective");
            sampledOnly.Should().Contain("real measurement",
                "nothing on the line is averaged or interpolated, and saying so is what stops a reader "
                + "distrusting the values as well as the resolution");
            sampledOnly.Should().NotContain("never recorded",
                "this range IS covered; borrowing the short-of-window wording would be false");

            var both = SQLTriage.Components.Shared.DynamicDashboard.BuildRetentionNotice(
                new[] { "Redo Queue (retained from 26 Aug 2026 21:02)" },
                new[] { "Log Send Queue (2500 retained points against a 2000-point chart limit)" });
            both.Should().Contain("was never recorded");
            both.Should().Contain("more retained points than the chart draws",
                "a dashboard can hold one panel of each, and dropping either fact hides a real one");
        }

        // ── Tier 5b: the collector files where the dashboard reads ──────────────────

        [Fact]
        public void The_collector_keys_history_where_the_dashboard_looks_for_it()
        {
            // Caught live on 2026-08-26: headless, nothing had ever set the instance selector, so the
            // collector keyed __all__ while the dashboard read "." — 16 real retained rows and a panel
            // notice reading "nothing retained yet". History filed where nothing looks is worse than no
            // history, because it costs disk and still reports a gap.
            static string CollectorKey(string? selected, string? factoryServer)
                => CachingQueryExecutor.BuildInstanceKey(new DashboardFilter
                {
                    Instances = SQLTriage.Data.Services.MetricHistoryCollectorService
                        .ResolveInstanceNames(selected, factoryServer)
                });

            static string DashboardKey(string selected)
                => CachingQueryExecutor.BuildInstanceKey(new DashboardFilter { Instances = new[] { selected } });

            CollectorKey("MSI", ".").Should().Be(DashboardKey("MSI"),
                "an operator selection is what the dashboard puts in its filter, so it wins");

            CollectorKey(null, ".").Should().Be(DashboardKey("."),
                "headless, the server the shared factory is about to query is the same one a "
                + "single-connection dashboard falls back to");

            CollectorKey("", "  ").Should().Be(
                CachingQueryExecutor.BuildInstanceKey(new DashboardFilter { Instances = Array.Empty<string>() }),
                "no instance at all keys __all__, matching an empty dashboard filter");

            CollectorKey("ALL", ".").Should().Be(DashboardKey("."),
                "'ALL' names no instance, and this collector samples exactly one server, so it files "
                + "under the server it actually queried. Keying rows as though they measured the whole "
                + "estate would be the fabrication; the dashboard says so on the page instead");

            CollectorKey(null, "Unknown").Should().Be(
                CachingQueryExecutor.BuildInstanceKey(new DashboardFilter { Instances = Array.Empty<string>() }),
                "'Unknown' is SqlServerConnectionFactory's placeholder for 'no idea', and filing rows "
                + "under a literal called Unknown would be a fabricated instance name");
        }

        // ── Tier 6: what the shipped config actually says ───────────────────────────

        [Fact]
        public void The_two_AG_queue_panels_retain_history_and_no_longer_call_themselves_one_reading()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ShippedConfigPath()));
            var panels = EnumeratePanels(doc.RootElement)
                .Where(p => p.TryGetProperty("id", out var id)
                            && (id.GetString() == "ag.send_queue" || id.GetString() == "ag.redo_queue"))
                .ToList();

            panels.Should().HaveCount(2, "both AG queue panels must still be in the shipped catalogue");

            foreach (var panel in panels)
            {
                var id = panel.GetProperty("id").GetString();
                panel.TryGetProperty("retainHistory", out var retain).Should().BeTrue(
                    "{0} was given a real trend by ruling 4; losing the flag silently returns it to one "
                    + "sample per refresh", id);
                retain.GetBoolean().Should().BeTrue();

                var description = panel.GetProperty("description").GetString() ?? "";
                description.Should().NotContain("not a retained historical trend",
                    "{0} now retains history, so the honest floor written when it did not is false", id);
                description.Should().Contain("retained",
                    "{0} must say what it does, and that a range longer than its history says so", id);
            }
        }

        [Fact]
        public void Retention_is_opt_in_and_the_shipped_catalogue_opts_in_deliberately_few_panels()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ShippedConfigPath()));
            var retained = EnumeratePanels(doc.RootElement)
                .Where(p => p.TryGetProperty("retainHistory", out var r) && r.GetBoolean())
                .Select(p => p.GetProperty("id").GetString())
                .ToList();

            retained.Should().BeEquivalentTo(new[] { "ag.send_queue", "ag.redo_queue" },
                "retention costs disk on a client's production server. Widening it past the panels a "
                + "ruling named is a product decision, not something a config edit does quietly");

            foreach (var p in EnumeratePanels(doc.RootElement)
                         .Where(p => p.TryGetProperty("retainHistory", out var r) && r.GetBoolean()))
            {
                p.GetProperty("panelType").GetString().Should().Be("TimeSeries",
                    "retention only means anything for a series of measurements over time");
            }
        }

        [Fact]
        public void Configured_bounds_are_clamped_rather_than_trusted()
        {
            var hostile = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MetricRetention:WindowDays"] = "100000",
                    ["MetricRetention:BucketSeconds"] = "0",
                    ["MetricRetention:MaxRows"] = "999999999",
                    ["MetricRetention:CollectSeconds"] = "1"
                })
                .Build();

            var options = MetricRetentionOptions.FromConfiguration(hostile);

            options.Window.Should().BeLessThanOrEqualTo(TimeSpan.FromDays(90),
                "a config typo must not turn a bounded feature into unbounded disk");
            options.Bucket.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(10),
                "a zero bucket would store every sample and defeat downsampling");
            options.MaxRows.Should().BeLessThanOrEqualTo(5_000_000);
            options.CollectInterval.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(10),
                "a one-second collector would hammer a client's production server");
        }

        // ── Harness ─────────────────────────────────────────────────────────────────

        private liveQueriesCacheStore NewStore(string name)
            => NewStoreAt(Path.Combine(_dir, name + ".db"));

        private liveQueriesCacheStore NewStoreAt(string path)
        {
            var store = new liveQueriesCacheStore();
            _disposables.Add(store);
            Repoint(store, "_connectionString", "Data Source=" + path + ";Mode=ReadWriteCreate;");
            store.EnsureSchema();
            return store;
        }

        /// <summary>
        /// Waits until a batch is actually on disk. The write pump completes an operation's task
        /// BEFORE it commits the transaction: SqliteCacheStore.BatchWriterLoopAsync completes each
        /// operation's TaskCompletionSource as it runs and calls tx.Commit() after the whole batch. So
        /// an append that has returned is not yet readable by a second connection. Production never notices: the
        /// collector appends on a timer and reads on a page load. A test that reads on the next line
        /// notices every time, and a Task.Delay would only move the flake.
        /// </summary>
        private static async Task WaitForRows(liveQueriesCacheStore store, long expected)
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (await store.GetMetricHistoryRowCountAsync() >= expected) return;
                await Task.Delay(25);
            }

            (await store.GetMetricHistoryRowCountAsync()).Should().BeGreaterThanOrEqualTo(expected,
                "the append never reached disk, so nothing below this line is measuring what it says");
        }

        /// <summary>
        /// The same wait as <see cref="WaitForRows"/>, for the CACHE side. UpsertTimeSeriesAsync goes
        /// through the same write pump and returns before its transaction commits. The three deleters
        /// are WRITERS on their own connections, so they do not read past that open transaction the
        /// way a reader does — they block on it. Measured with the seed deliberately unwaited under a
        /// 3 s commit hold: TrimTimeSeriesAsync blocked 3125 ms, all three together 3143 ms, and the
        /// row count once the hold released was 0, not 5. That last number is the discriminator: had
        /// the deleters run against an empty table and the seed committed behind them, five rows would
        /// have survived. The seed committed FIRST and was then really deleted, so the survival claim
        /// was never passing for the wrong reason. This wait is therefore latency, not correctness —
        /// it turns a ~3 s wait on SQLite's default command timeout into a fast, explicit one. The
        /// stale-read hazard <see cref="WaitForRows"/> exists for is a READER hazard; the age-prune
        /// cell carries the same measurement from the writer side.
        /// </summary>
        private static async Task WaitForTimeSeriesRows(
            liveQueriesCacheStore store, string queryId, string instanceKey,
            DateTime from, DateTime to, int expected)
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                if ((await store.GetTimeSeriesAsync(queryId, instanceKey, from, to)).Count >= expected) return;
                await Task.Delay(25);
            }

            (await store.GetTimeSeriesAsync(queryId, instanceKey, from, to)).Should().HaveCountGreaterThanOrEqualTo(
                expected, "the cache seed never reached disk, so the deleters below would delete nothing");
        }

        /// <summary>
        /// The general form of <see cref="WaitForRows"/>, for a cell whose writes cannot be counted:
        /// samples that collapse into one bucket leave the row count unmoved, so only the VALUE says
        /// the second write committed. Same budget, same poll, and no Task.Delay stands in for a commit.
        /// Returns whether the condition ever held, so the caller states its own failure message.
        /// </summary>
        private static async Task<bool> WaitUntil(Func<Task<bool>> committed)
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (await committed()) return true;
                await Task.Delay(25);
            }

            return await committed();
        }

        /// <summary>A baseline window with nothing to disclose, for the branches that are about the
        /// EMPTY baseline and must not accidentally assert anything about coverage.</summary>
        private static SQLTriage.Components.Shared.DynamicDashboard.BaselineWindowCoverage WholeWindow(DateTime now)
            => SQLTriage.Components.Shared.DynamicDashboard.BaselineWindowCoverage.Whole(
                now.AddDays(-14), now.AddDays(-7));

        /// <summary>Samples every 10 minutes from <paramref name="fromDaysAgo"/> until
        /// <paramref name="toDaysAgo"/>, which is what a real collector at a 60-second bucket leaves
        /// behind after the shipped downsample.</summary>
        private static List<TimeSeriesPoint> Every10Minutes(DateTime now, double fromDaysAgo, double toDaysAgo)
        {
            var points = new List<TimeSeriesPoint>();
            var end = now.AddDays(-toDaysAgo);
            for (var t = now.AddDays(-fromDaysAgo); t <= end; t = t.AddMinutes(10))
                points.Add(new TimeSeriesPoint { Time = t, Series = "s", Value = 1 });
            return points;
        }

        private static List<TimeSeriesPoint> Points(DateTime start, int count)
            => Enumerable.Range(0, count)
                .Select(i => new TimeSeriesPoint
                {
                    Time = start.AddMinutes(i),
                    Series = "s",
                    Value = i
                })
                .ToList();

        private static void Repoint(object target, string field, object? value)
        {
            var f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            f.Should().NotBeNull("the harness repoints {0}; a rename must fail loudly", field);
            f!.SetValue(target, value);
        }

        private static string ShippedConfigPath()
            => Path.Combine(RawPassedScan.RepoRoot().FullName, "Config", "dashboard-config.json");

        private static IEnumerable<JsonElement> EnumeratePanels(JsonElement root)
        {
            if (!root.TryGetProperty("dashboards", out var dashboards)) yield break;
            foreach (var dashboard in dashboards.EnumerateArray())
            {
                if (dashboard.TryGetProperty("panels", out var panels) && panels.ValueKind == JsonValueKind.Array)
                    foreach (var p in panels.EnumerateArray()) yield return p;

                if (dashboard.TryGetProperty("tabs", out var tabs) && tabs.ValueKind == JsonValueKind.Array)
                    foreach (var tab in tabs.EnumerateArray())
                        if (tab.TryGetProperty("panels", out var tabPanels) && tabPanels.ValueKind == JsonValueKind.Array)
                            foreach (var p in tabPanels.EnumerateArray()) yield return p;
            }
        }
    }
}
