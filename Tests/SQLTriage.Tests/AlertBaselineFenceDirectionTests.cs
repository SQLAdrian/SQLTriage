/* In the name of God, the Merciful, the Compassionate */

// ── B: the learned IQR fence was computed on one side only, 2026-08-22 ───────────────────────
//
// WHY THIS FILE EXISTS. C:\SQLTriage-Service\logs\service-20260822.log, line 175:
//
//     2026-08-22 18:17:39.942 +12:00 [WRN] Alert fired: Buffer Cache Hit Ratio on .\NEW2022
//                                          (Critical) - value: 100
//
// The shipped definition for buffer_cache_hit is operator less_than, warning 95, critical 90,
// severity Low. A 100 % hit ratio cannot breach it, and the three installed copies of
// alert-definitions.json are byte-identical, so no override explains it.
//
// AlertBaselineService.ComputeStats computed UPPER fences only -- P75 + k x IQR -- and
// GetThresholds handed them back unconditioned. AlertEvaluationService then applied them through
// the definition's own operator, so for a less_than alert the test became
// "value < (a fence above almost every sample)", which is true almost always. And because the
// critical fence sat FURTHER above the data than the warning fence, under less_than the critical
// band was a strict SUPERSET of the warning band: the computed level was Critical every time.
// Two sibling less_than alerts fired in the same second on the same theory -- Page Life
// Expectancy 410 (critical 120) and Disk Space Low 549762 (critical 2048).
//
// WHAT THE FIX SILENCES (measured). The direction fix silences the three less_than alerts in the
// 18:17:39 burst whose samples have spread: Buffer Cache Hit Ratio on .\NEW2022 (lower critical
// 99.894 against a reading of 100), Page Life Expectancy (lower critical -664.75 against a 597
// maximum) and Disk Space Low (lower warning 542863 against 549762). The other four alerts in the
// burst are the separate raw-counter class and are untouched by this lane.
//
// THE ZERO-SPREAD RULING, 2026-08-23 (tier 6, rewritten). The direction fix did NOT reach the
// 18:38:39 line on server "." at 99.99096983926314: that metric is a flat 100, so the iqr is 0,
// both lower fences collapsed onto 100, and a reading a ten-thousandth below the constant was
// still a breach, Critical, because critical equalled warning. That was pinned rather than fixed
// because nobody had ruled on it. Adrian ruled on 2026-08-23 (DECISIONS 05:00 #2): a zero-spread
// window produces NO learned fence in either direction, and only the fixed thresholds apply.
// AlertBaselineService.SelectFences now returns (null, null) when Iqr <= 0, tier 6 is the ruled
// behaviour instead of its opposite, and tier 7 drives it through the real service both from
// memory and off a persisted database row.
//
// WHAT IS REAL HERE. These drive the REAL production decision functions:
// AlertBaselineService.ComputeStats, AlertBaselineService.SelectFences,
// AlertEvaluationService.IsThresholdBreached, and liveQueriesCacheStore.MigrateAlertBaselineStats
// against a database created with the OLD schema. Nothing is re-implemented in the test.
//
// THE WIRING IS NOW EXERCISED TOO (tier 4, added 2026-08-23). The first version of this file
// tested the decision functions and read the two call sites that use them, and said so. That was
// not enough: bypassing BOTH call sites -- GetThresholds returning the upper pair again and
// GetTrendSignal returning the stored booleans again -- passed all 4672 tests. Tier 5 builds a
// real AlertBaselineService over a copy of the SHIPPED alert definitions and calls its public
// GetThresholds and GetTrendSignal, so the line that fixes the live log entry is run.
//
// WHAT IS STILL READ AND NOT RUN. AlertEvaluationService.cs:716-725, where the evaluator asks for
// those fences and applies them, is unchanged by this lane and is not driven by any test: a live
// evaluation needs a reachable SQL Server. That call site is BELIEVE. Everything below it is
// proved.
//
// MUTATION THAT MUST FAIL (direction): revert ComputeStats to the single upper pair
//     var warn = Math.Max(p75 + WarnMultiplier * iqr, p95);
//     var crit = p75 + CritMultiplier * iqr;
// and make SelectFences return (s.ThresholdWarn, s.ThresholdCrit) unconditionally. Tiers 2 and 3
// go red. Tier 1 alone would NOT catch it -- IsThresholdBreached was never the defect.
//
// MUTATION THAT MUST FAIL (zero spread): delete the "if (s.Iqr <= 0) return (null, null);" guard
// from SelectFences. Six tests across tiers 6 and 7 go red (measured 2026-08-23). NARROWING it
// rather than deleting it must also go red: swapping the condition for "s.P05 == s.P95" turns
// A_zero_iqr_window_yields_no_fence_even_when_p05_and_p95_differ red on its own (measured). The
// third candidate, min == max, has no field on BaselineStats to mutate against, so it is pinned
// instead by The_ruled_window_is_not_perfectly_flat_so_min_equals_max_would_have_missed_it, which
// asserts the ruled window's own min and max differ.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public sealed class AlertBaselineFenceDirectionTests : IDisposable
{
    private readonly string _dir;
    private liveQueriesCacheStore? _cache;

    public AlertBaselineFenceDirectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fence-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _cache?.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly List<(string AlertId, string ServerName, double Value, DateTime SampledAt)> NoTrend = new();

    /// <summary>
    /// Twenty buffer-cache-hit-ratio readings off a healthy instance: nothing below 99.90, most of
    /// them a flat 100. This is the distribution that produced the live 18:17:39 line.
    /// </summary>
    private static List<double> HealthyBufferCacheSamples()
    {
        var v = new List<double>();
        for (var i = 0; i < 4; i++) v.Add(99.90);
        for (var i = 0; i < 4; i++) v.Add(99.95);
        for (var i = 0; i < 12; i++) v.Add(100.00);
        v.Sort();
        return v;
    }

    // ── Tier 1: the predicate itself was never the defect ───────────────────────

    // 2026-08-28, strings-r2-06: the four rows here had no EQUALITY case in either direction, and
    // equality is where the predicate's strictness lives. Sixteen shipped alerts were silent for
    // ever on exactly that arithmetic - a query capped at 1 against a warning threshold of 1 - and
    // this tier could not have caught it. The two equality rows are added here, at the gap the hunt
    // named; the catalogue-wide consequence is AlertNeverFiresTests.
    [Theory]
    [InlineData(100.0, 90.0, "less_than", false)]
    [InlineData(85.0, 90.0, "less_than", true)]
    [InlineData(97.0, 95.0, "greater_than", true)]
    [InlineData(80.0, 95.0, "greater_than", false)]
    [InlineData(1.0, 1.0, "greater_than", false)]   // value == threshold does NOT fire
    [InlineData(90.0, 90.0, "less_than", false)]    // nor in the other direction
    public void IsThresholdBreached_reads_the_operator_correctly(
        double value, double threshold, string op, bool expected)
    {
        AlertEvaluationService.IsThresholdBreached(value, threshold, op).Should().Be(expected);
    }

    // ── Tier 2: the fence lands on the side the alert calls bad ─────────────────

    [Fact]
    public void A_less_than_alert_gets_a_fence_BELOW_its_samples_so_a_perfect_reading_is_not_critical()
    {
        var stats = AlertBaselineService.ComputeStats(
            "buffer_cache_hit", @".\NEW2022", HealthyBufferCacheSamples(), NoTrend);

        var (warn, crit) = AlertBaselineService.SelectFences(stats, "less_than");

        warn.Should().NotBeNull();
        crit.Should().NotBeNull();

        // The fence must sit below every sample the alert has ever produced, not above them.
        crit!.Value.Should().BeLessThan(99.90);
        warn!.Value.Should().BeLessThan(99.90);

        // The live line, reproduced against the fixed fence: 100 is not a breach of either band.
        AlertEvaluationService.IsThresholdBreached(100.0, crit, "less_than").Should().BeFalse(
            "a 100 % buffer cache hit ratio is the best possible reading and cannot be Critical");
        AlertEvaluationService.IsThresholdBreached(100.0, warn, "less_than").Should().BeFalse();

        // A genuinely bad reading still fires.
        AlertEvaluationService.IsThresholdBreached(50.0, crit, "less_than").Should().BeTrue();
    }

    [Fact]
    public void The_upper_fence_is_what_used_to_be_handed_to_a_less_than_alert()
    {
        // Pins the defect itself rather than only its absence: the UPPER pair, which the old
        // GetThresholds returned for every alert whatever its operator, IS above the samples,
        // so "value < fence" was true for a perfect 100 and the level computed as Critical.
        var stats = AlertBaselineService.ComputeStats(
            "buffer_cache_hit", @".\NEW2022", HealthyBufferCacheSamples(), NoTrend);

        var (upperWarn, upperCrit) = AlertBaselineService.SelectFences(stats, "greater_than");

        upperCrit!.Value.Should().BeGreaterThan(100.0);
        AlertEvaluationService.IsThresholdBreached(100.0, upperCrit, "less_than").Should().BeTrue(
            "this is the 2026-08-22 18:17:39 defect, kept here so a regression is recognisable");
        upperWarn.Should().NotBeNull();
    }

    // ── Tier 3: the critical band is never the wider of the two ─────────────────

    [Fact]
    public void For_a_less_than_alert_the_critical_band_is_inside_the_warning_band()
    {
        var stats = AlertBaselineService.ComputeStats(
            "buffer_cache_hit", @".\NEW2022", HealthyBufferCacheSamples(), NoTrend);

        var (warn, crit) = AlertBaselineService.SelectFences(stats, "less_than");

        crit!.Value.Should().BeLessThanOrEqualTo(warn!.Value,
            "on a less_than alert a lower critical fence means a NARROWER critical band; when crit "
            + "sat above warn every warning was also a critical, which is why the live line said Critical");

        // A value between the two fences is a Warning and not a Critical.
        var between = (warn.Value + crit.Value) / 2.0;
        AlertEvaluationService.IsThresholdBreached(between, warn, "less_than").Should().BeTrue();
        AlertEvaluationService.IsThresholdBreached(between, crit, "less_than").Should().BeFalse();
    }

    [Fact]
    public void For_a_greater_than_alert_a_skewed_distribution_no_longer_inverts_the_bands()
    {
        // warn carries a P95 floor and crit did not, so wherever P95 > P75 + 3 x IQR the critical
        // fence landed INSIDE the warning fence. A body of samples around 10 with a five-sample
        // tail at 1000 is exactly that shape. Not observed live; asserted anyway.
        //
        // THE BODY CARRIES SPREAD DELIBERATELY (ruling 2026-08-23 #2). The first version of this
        // fixture put all 95 body samples on a flat 10.0, which makes the iqr 0 -- and under the
        // ruling a zero-iqr window now yields no fence at all, so the fixture could no longer
        // reach the clamp it exists to test. The 95/5 zero-iqr shape is not lost: it moved to
        // A_zero_iqr_window_yields_no_fence_even_when_p05_and_p95_differ, which pins that the
        // clamp still produces warn == crit there and that the pair is therefore suppressed.
        var samples = new List<double>();
        for (var i = 0; i < 95; i++) samples.Add(9.0 + (i % 3));
        for (var i = 0; i < 5; i++) samples.Add(1000.0);
        samples.Sort();

        var stats = AlertBaselineService.ComputeStats("skewed_metric", "SRV1", samples, NoTrend);
        var (warn, crit) = AlertBaselineService.SelectFences(stats, "greater_than");

        stats.Iqr.Should().BeGreaterThan(0,
            "with no spread the ruling suppresses the pair and this test would measure nothing");
        stats.P95.Should().BeGreaterThan(stats.P75 + 3.0 * stats.Iqr,
            "the fixture must actually be the skewed shape this test is about");
        crit!.Value.Should().BeGreaterThanOrEqualTo(warn!.Value);
    }

    // ── Tier 4: an installed database upgrades in place ─────────────────────────

    [Fact]
    public void An_existing_database_on_the_old_schema_gains_the_lower_fence_columns_and_keeps_its_rows()
    {
        var dbPath = Path.Combine(_dir, "old-schema.db");
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;");
        conn.Open();

        // The ORIGINAL alert_baseline_stats shape: no trend columns, no lower fences.
        using (var create = conn.CreateCommand())
        {
            create.CommandText = @"
                CREATE TABLE alert_baseline_stats (
                    alert_id        TEXT NOT NULL,
                    server_name     TEXT NOT NULL,
                    sample_count    INTEGER NOT NULL,
                    p25             REAL NOT NULL,
                    p50             REAL NOT NULL,
                    p75             REAL NOT NULL,
                    p95             REAL NOT NULL,
                    iqr             REAL NOT NULL,
                    threshold_warn  REAL NOT NULL,
                    threshold_crit  REAL NOT NULL,
                    last_computed   TEXT NOT NULL,
                    baseline_locked INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (alert_id, server_name)
                );";
            create.ExecuteNonQuery();
        }

        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = @"
                INSERT INTO alert_baseline_stats
                    (alert_id, server_name, sample_count, p25, p50, p75, p95, iqr,
                     threshold_warn, threshold_crit, last_computed, baseline_locked)
                VALUES ('buffer_cache_hit', '.\NEW2022', 42, 99.95, 100, 100, 100, 0.05,
                        100.075, 100.15, '2026-08-22T06:00:00.0000000Z', 1)";
            insert.ExecuteNonQuery();
        }

        liveQueriesCacheStore.MigrateAlertBaselineStats(conn);

        var columns = new List<string>();
        using (var info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(alert_baseline_stats)";
            using var reader = info.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }

        columns.Should().Contain(new[]
        {
            "p05", "threshold_warn_lower", "threshold_crit_lower",
            "trend_slope", "trend_r_squared", "trend_sample_count",
            "is_trend_warning", "is_trend_critical"
        });

        // The pre-existing row is still there, its old values intact, and the new columns carry
        // the backfilled default rather than NULL.
        using (var read = conn.CreateCommand())
        {
            read.CommandText = @"
                SELECT sample_count, threshold_crit, baseline_locked, threshold_crit_lower, p05
                FROM alert_baseline_stats WHERE alert_id = 'buffer_cache_hit'";
            using var reader = read.ExecuteReader();
            reader.Read().Should().BeTrue("the migration must not drop the install's existing stats");
            reader.GetInt32(0).Should().Be(42);
            reader.GetDouble(1).Should().Be(100.15);
            reader.GetInt32(2).Should().Be(1);
            reader.IsDBNull(3).Should().BeFalse();
            reader.GetDouble(3).Should().Be(0.0);
            reader.GetDouble(4).Should().Be(0.0);
        }

        // Running it a second time is a no-op, not a duplicate-column failure.
        liveQueriesCacheStore.MigrateAlertBaselineStats(conn);

        var second = new List<string>();
        using (var info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(alert_baseline_stats)";
            using var reader = info.ExecuteReader();
            while (reader.Read()) second.Add(reader.GetString(1));
        }
        second.Count.Should().Be(columns.Count);
        second.Distinct().Count().Should().Be(second.Count);
    }
    // -- Tier 5: the WIRING, on a real AlertBaselineService ---------------------

    /// <summary>
    /// Copies the SHIPPED alert definitions into this test's own directory and builds a real
    /// AlertBaselineService over them. The definitions are the installed bytes, not a fixture:
    /// buffer_cache_hit really is less_than and cpu_sql_usage really is greater_than, and the
    /// first assertion in each test below says so before relying on it.
    /// </summary>
    private AlertBaselineService NewBaselineService(out AlertDefinitionService definitions)
    {
        var shipped = ShippedDefinitionsPath();
        var local = Path.Combine(_dir, "alert-definitions.json");
        File.Copy(shipped, local);

        definitions = new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance, local);

        var settings = new UserSettingsService(Path.Combine(_dir, "user-settings.json"));
        settings.SetAlertBaselineEnabled(true);
        settings.SetAlertBaselinePerServer(true);

        _cache = new liveQueriesCacheStore();

        return new AlertBaselineService(
            NullLogger<AlertBaselineService>.Instance,
            definitions,
            new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
            _cache,
            settings);
    }

    private static string ShippedDefinitionsPath()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var folder in new[] { "config", "Config" })
        {
            var candidate = Path.Combine(baseDir, folder, "alert-definitions.json");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            "The shipped alert definitions must be beside the test assembly: " + baseDir);
    }

    /// <summary>Writes a stats row straight into the service's in-memory cache, using the
    /// service's OWN key function rather than a copy of its format.</summary>
    private static void SeedStats(AlertBaselineService svc, string alertId, string server, BaselineStats stats)
    {
        var key = (string)typeof(AlertBaselineService)
            .GetMethod("Key", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { alertId, server })!;

        var dict = (ConcurrentDictionary<string, BaselineStats>)typeof(AlertBaselineService)
            .GetField("_stats", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(svc)!;

        dict[key] = stats;
    }

    [Fact]
    public void GetThresholds_hands_a_less_than_alert_the_LOWER_pair()
    {
        using var svc = NewBaselineService(out var definitions);

        definitions.GetAlert("buffer_cache_hit")!.Operator.Should().Be("less_than",
            "the shipped definition is what makes this the interesting direction");

        var stats = AlertBaselineService.ComputeStats(
            "buffer_cache_hit", ".\\NEW2022", HealthyBufferCacheSamples(), NoTrend);
        SeedStats(svc, "buffer_cache_hit", ".\\NEW2022", stats);

        var (warn, crit) = svc.GetThresholds("buffer_cache_hit", ".\\NEW2022");

        warn.Should().Be(stats.ThresholdWarnLower);
        crit.Should().Be(stats.ThresholdCritLower);
        warn.Should().NotBe(stats.ThresholdWarn, "the upper pair is what the defect handed back");
        crit.Should().NotBe(stats.ThresholdCrit);

        // The live line, end to end through the service the evaluator actually calls.
        AlertEvaluationService.IsThresholdBreached(100.0, crit, "less_than").Should().BeFalse(
            "a 100 % buffer cache hit ratio cannot be Critical, whatever the baseline learned");
        AlertEvaluationService.IsThresholdBreached(50.0, crit, "less_than").Should().BeTrue();
    }

    [Fact]
    public void GetThresholds_hands_a_greater_than_alert_the_UPPER_pair()
    {
        using var svc = NewBaselineService(out var definitions);

        definitions.GetAlert("cpu_sql_usage")!.Operator.Should().Be("greater_than");

        var samples = new List<double> { 8, 9, 10, 10, 11, 12, 12, 13, 14, 15, 15, 16, 17, 18, 19, 20 };
        samples.Sort();
        var stats = AlertBaselineService.ComputeStats("cpu_sql_usage", ".\\NEW2022", samples, NoTrend);
        SeedStats(svc, "cpu_sql_usage", ".\\NEW2022", stats);

        var (warn, crit) = svc.GetThresholds("cpu_sql_usage", ".\\NEW2022");

        warn.Should().Be(stats.ThresholdWarn);
        crit.Should().Be(stats.ThresholdCrit);
        crit!.Value.Should().BeGreaterThanOrEqualTo(warn!.Value,
            "the critical band may never be the wider of the two");
    }

    [Fact]
    public void GetTrendSignal_suppresses_a_slope_that_points_the_way_the_alert_calls_good()
    {
        using var svc = NewBaselineService(out _);

        // buffer_cache_hit is less_than: it fears the metric FALLING. A rising slope is good news
        // however steep it is, and the stored booleans cannot know that -- they never saw an
        // operator.
        SeedStats(svc, "buffer_cache_hit", ".\\NEW2022",
            TrendingStats("buffer_cache_hit", slopePerHour: +5));

        svc.GetTrendSignal("buffer_cache_hit", ".\\NEW2022").Should().Be((false, false),
            "a buffer cache hit ratio climbing is the best thing that metric can do");

        SeedStats(svc, "buffer_cache_hit", ".\\NEW2022",
            TrendingStats("buffer_cache_hit", slopePerHour: -5));

        svc.GetTrendSignal("buffer_cache_hit", ".\\NEW2022").Should().Be((true, true),
            "the same magnitude, pointing the way this alert calls bad, must still fire");
    }

    [Fact]
    public void GetTrendSignal_declines_a_stale_stat_whose_window_no_longer_overlaps_now()   // alerts-r1-12
    {
        using var svc = NewBaselineService(out _);

        // The stat that WOULD fire if it were current — a falling buffer cache hit ratio — but
        // frozen 200 h ago, well past the 72 h trend window, the shape of a pair that stopped
        // receiving samples: RecomputeAllStatsAsync never revisits it, so is_trend_critical survives
        // the gap and would otherwise fire Critical on the first new sample.
        var stale = TrendingStats("buffer_cache_hit", slopePerHour: -5,
            lastComputed: DateTime.UtcNow.AddHours(-200));
        SeedStats(svc, "buffer_cache_hit", ".\\NEW2022", stale);

        svc.GetTrendSignal("buffer_cache_hit", ".\\NEW2022").Should().Be((false, false),
            "a trend summarises the last 72 h of samples; a stat frozen 200 h ago no longer describes "
            + "now and must not fire — this is the sample-count/freshness gate GetThresholds had and "
            + "GetTrendSignal lacked");
    }

    private static BaselineStats TrendingStats(string alertId, double slopePerHour, DateTime? lastComputed = null) => new()
    {
        AlertId = alertId,
        ServerName = ".\\NEW2022",
        SampleCount = 40,
        P50 = 100,
        TrendSlopePerHour = slopePerHour,
        TrendSampleCount = 40,
        // alerts-r1-12: GetTrendSignal now declines a stat whose trend window no longer overlaps
        // now. A stat that is genuinely trending was just recomputed, so it defaults to a fresh
        // LastComputed; the stale-gap case passes an old one.
        LastComputed = lastComputed ?? DateTime.UtcNow,
        IsTrendWarning = true,
        IsTrendCritical = true
    };

    // -- Tier 6: a zero-spread window produces NO learned fence (ruling 2026-08-23 #2) --

    /// <summary>
    /// The buffer-cache-hit samples on the SQL 2025 rig, server ".". Statistics measured from a
    /// read-only copy of the installed cache database (alert_baseline_samples, alert_id
    /// buffer_cache_hit, server_name "."): n = 68, min = 99.99096983926314, max = 100, and
    /// p05 = p25 = p50 = p75 = p95 = 100 with iqr = 0.
    ///
    /// <para>The VALUES below are a reconstruction to those measured statistics, not the 68 rows
    /// themselves: one reading at the measured minimum and 67 at a flat 100 reproduces every
    /// percentile the live data produced. The statistics are proved; the list is arithmetic.</para>
    /// </summary>
    private static List<double> LiveDotServerBufferCacheSamples()
    {
        var v = new List<double> { 99.99096983926314 };
        for (var i = 0; i < 67; i++) v.Add(100.00);
        v.Sort();
        return v;
    }

    /// <summary>
    /// THE RULING, DECISIONS 2026-08-23 05:00 #2: when the sample spread is zero the learned path
    /// produces no fence in either direction and only the alert's fixed thresholds apply.
    ///
    /// <para>THIS TEST WAS INVERTED. Until 2026-08-23 it was
    /// <c>A_flat_metric_still_fires_because_both_lower_fences_collapse_onto_the_constant</c> and it
    /// PINNED the opposite: the lane that made the fences direction-aware deliberately left the
    /// zero-spread case alone because nobody had ruled on it, and pinned what happened so the
    /// decision stayed visible instead of being assumed. The ruling arrived; the pin is now the
    /// ruled behaviour, and this paragraph is the record that it flipped on purpose.</para>
    ///
    /// <para>The live line it is about, service-20260822.log :219,</para>
    /// <para>
    ///     2026-08-22 18:38:39.853 +12:00 [WRN] Alert fired: Buffer Cache Hit Ratio on .
    ///                                          (Critical) - value: 99.99096983926314
    /// </para>
    /// <para>no longer has a learned fence to fire against. The fixed shipped thresholds for
    /// buffer_cache_hit are warning 95 and critical 90, and 99.99 breaches neither.</para>
    /// </summary>
    [Fact]
    public void A_flat_metric_produces_no_learned_fence_in_either_direction()
    {
        var stats = AlertBaselineService.ComputeStats(
            "buffer_cache_hit", ".", LiveDotServerBufferCacheSamples(), NoTrend);

        stats.Iqr.Should().Be(0, "67 of the 68 readings are the same number");
        stats.P05.Should().Be(100);

        // The arithmetic is still recorded honestly. The ruling is about whether a fence is
        // APPLIED, not a claim that ComputeStats measured the wrong numbers.
        stats.ThresholdWarnLower.Should().Be(100);
        stats.ThresholdCritLower.Should().Be(100);
        stats.ThresholdCritLower.Should().Be(stats.ThresholdWarnLower,
            "this collapse is the whole reason a zero-spread pair is not a usable pair");

        var (warn, crit) = AlertBaselineService.SelectFences(stats, "less_than");
        warn.Should().BeNull("ruling 2026-08-23 #2: no spread, no learned fence");
        crit.Should().BeNull();

        // Both directions, not just the one the live alert used.
        var (upperWarn, upperCrit) = AlertBaselineService.SelectFences(stats, "greater_than");
        upperWarn.Should().BeNull();
        upperCrit.Should().BeNull();

        // The 18:38:39 line, reproduced: with no fence there is nothing to breach.
        AlertEvaluationService.IsThresholdBreached(99.99096983926314, crit, "less_than").Should().BeFalse(
            "the second live line no longer fires on a learned fence");
        AlertEvaluationService.IsThresholdBreached(99.99096983926314, warn, "less_than").Should().BeFalse();
    }

    [Fact]
    public void A_near_flat_window_that_still_has_spread_keeps_its_fences()
    {
        // The control. One notch of spread is enough: the ruling suppresses ZERO spread, not
        // small spread, so this window must still produce a usable pair.
        var v = new List<double>();
        for (var i = 0; i < 34; i++) v.Add(99.98);
        for (var i = 0; i < 34; i++) v.Add(100.00);
        v.Sort();

        var stats = AlertBaselineService.ComputeStats("buffer_cache_hit", ".", v, NoTrend);
        stats.Iqr.Should().BeGreaterThan(0, "the control must actually have spread");

        var (warn, crit) = AlertBaselineService.SelectFences(stats, "less_than");
        warn.Should().NotBeNull("a window with spread is exactly what the learned path is for");
        crit.Should().NotBeNull();
        crit!.Value.Should().BeLessThanOrEqualTo(warn!.Value);
    }

    [Fact]
    public void A_greater_than_alert_on_an_all_zero_window_gets_no_fence_either()
    {
        // A counter that has not moved since startup reads a flat 0. Upper direction, same rule.
        var v = new List<double>();
        for (var i = 0; i < 68; i++) v.Add(0.0);

        var stats = AlertBaselineService.ComputeStats("page_splits", ".", v, NoTrend);
        stats.Iqr.Should().Be(0);
        stats.ThresholdWarn.Should().Be(stats.ThresholdCrit, "both collapse onto max(p75, p95) = 0");

        var (warn, crit) = AlertBaselineService.SelectFences(stats, "greater_than");
        warn.Should().BeNull();
        crit.Should().BeNull();

        // Without the ruling this fired on every reading above zero, at Critical, with no
        // warning band in between.
        AlertEvaluationService.IsThresholdBreached(1.0, crit, "greater_than").Should().BeFalse();
    }

    /// <summary>
    /// WHY "ZERO SPREAD" IS <c>iqr == 0</c> AND NOT <c>p05 == p95</c>. A 95/5 split has an iqr of
    /// zero while p05 and p95 are far apart, so a p05 == p95 rule would leave it alone -- and it
    /// is degenerate all the same: with no iqr the multiplier term vanishes and both fences are
    /// floored onto p95, so warn == crit and the first breach of any size reads Critical with no
    /// warning band. Same defect, wider shape.
    /// </summary>
    [Fact]
    public void A_zero_iqr_window_yields_no_fence_even_when_p05_and_p95_differ()
    {
        var samples = new List<double>();
        for (var i = 0; i < 95; i++) samples.Add(10.0);
        for (var i = 0; i < 5; i++) samples.Add(1000.0);
        samples.Sort();

        var stats = AlertBaselineService.ComputeStats("skewed_metric", "SRV1", samples, NoTrend);

        stats.Iqr.Should().Be(0, "the middle 50 % of this window is a single value");
        stats.P95.Should().NotBe(stats.P05, "a p05 == p95 rule would not have covered this window");
        stats.ThresholdWarn.Should().Be(stats.ThresholdCrit,
            "which is the degeneracy the ruling is about, and it is present here too");

        AlertBaselineService.SelectFences(stats, "greater_than").Should().Be(((double?)null, (double?)null));
    }

    /// <summary>
    /// WHY "ZERO SPREAD" IS NOT <c>min == max</c>. The window the ruling was made about is not
    /// perfectly flat: it holds one reading at 99.99096983926314 among 67 at 100. A min == max
    /// rule would have measured false on the very window Adrian ruled on and left the live line
    /// firing.
    /// </summary>
    [Fact]
    public void The_ruled_window_is_not_perfectly_flat_so_min_equals_max_would_have_missed_it()
    {
        var v = LiveDotServerBufferCacheSamples();

        v.Min().Should().NotBe(v.Max(), "there is a real reading below the constant in this window");
        v.Min().Should().Be(99.99096983926314);
        v.Max().Should().Be(100.0);

        var stats = AlertBaselineService.ComputeStats("buffer_cache_hit", ".", v, NoTrend);
        stats.Iqr.Should().Be(0, "the iqr rule does cover it, which is why the iqr rule was chosen");
        AlertBaselineService.SelectFences(stats, "less_than").Should().Be(((double?)null, (double?)null));
    }

    // -- Tier 7: the ruling through the real service, in memory and off disk ----

    [Fact]
    public void GetThresholds_hands_back_no_fence_for_a_flat_window()
    {
        using var svc = NewBaselineService(out var definitions);

        definitions.GetAlert("buffer_cache_hit")!.Operator.Should().Be("less_than");

        var stats = AlertBaselineService.ComputeStats(
            "buffer_cache_hit", ".", LiveDotServerBufferCacheSamples(), NoTrend);
        stats.SampleCount.Should().BeGreaterThanOrEqualTo(10,
            "below the seed minimum GetThresholds returns null for a different reason entirely");
        SeedStats(svc, "buffer_cache_hit", ".", stats);

        svc.GetThresholds("buffer_cache_hit", ".").Should().Be(((double?)null, (double?)null),
            "ruling 2026-08-23 #2, through the method the evaluator actually calls");
    }

    /// <summary>
    /// The rows an installed database has ALREADY written. A flat window persisted before the
    /// ruling carries iqr 0 with both lower fences at 100, and the read side must decline it on
    /// the next load rather than at the next nightly recompute -- which is the reason the gate
    /// sits in SelectFences and not in ComputeStats.
    /// </summary>
    [Fact]
    public void A_flat_window_already_persisted_in_the_database_reads_back_as_no_fence()
    {
        using var svc = NewBaselineService(out _);

        const string server = @".\ZEROSPREAD-RULING-TEST";
        using (var conn = _cache!.CreateExternalConnection())
        {
            using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM alert_baseline_stats WHERE server_name = @srv";
            del.Parameters.AddWithValue("@srv", server);
            del.ExecuteNonQuery();

            using var ins = conn.CreateCommand();
            ins.CommandText = @"
                INSERT INTO alert_baseline_stats
                    (alert_id, server_name, sample_count, p25, p50, p75, p95, iqr,
                     threshold_warn, threshold_crit, last_computed, baseline_locked,
                     trend_slope, trend_r_squared, trend_sample_count,
                     is_trend_warning, is_trend_critical,
                     p05, threshold_warn_lower, threshold_crit_lower)
                VALUES ('buffer_cache_hit', @srv, 68, 100, 100, 100, 100, 0,
                        100, 100, @lc, 0,
                        0, 0, 0, 0, 0,
                        100, 100, 100)";
            ins.Parameters.AddWithValue("@srv", server);
            ins.Parameters.AddWithValue("@lc", DateTime.UtcNow.ToString("o"));
            ins.ExecuteNonQuery();
        }

        try
        {
            // The service's OWN loader, not a re-implementation of it.
            var load = typeof(AlertBaselineService)
                .GetMethod("LoadSampleCountsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((Task)load.Invoke(svc, Array.Empty<object>())!).GetAwaiter().GetResult();

            svc.GetThresholds("buffer_cache_hit", server).Should().Be(((double?)null, (double?)null),
                "a row written before the ruling must stop firing at the next read, not at the "
                + "next nightly recompute");
        }
        finally
        {
            using var conn = _cache!.CreateExternalConnection();
            using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM alert_baseline_stats WHERE server_name = @srv";
            del.Parameters.AddWithValue("@srv", server);
            del.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// THE TREND PATH, and why the ruling is NOT extended to it.
    ///
    /// <para>A trend signal on a perfectly flat window would be absurd, so the first question is
    /// whether one can happen. It cannot: OLS on a constant y gives a slope of exactly zero, the
    /// magnitude gate is <c>|slope| / p50</c> against 0.005, and zero clears no gate. This test
    /// measures that rather than assuming it.</para>
    ///
    /// <para>Extending the suppression to any zero-IQR window was considered and rejected. A
    /// window whose middle 50 % is one value but whose recent samples are MOVING is precisely the
    /// onset of a problem on a previously stable metric, and it is the one thing the trend path
    /// exists to catch. Suppressing it would trade a false Critical for a missed first warning.
    /// Recorded here as an extension the gate may still rule on.</para>
    /// </summary>
    [Fact]
    public void A_perfectly_flat_window_produces_no_trend_signal_so_none_needs_suppressing()
    {
        var rows = new List<(string AlertId, string ServerName, double Value, DateTime SampledAt)>();
        var origin = new DateTime(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 40; i++)
            rows.Add(("buffer_cache_hit", ".", 100.0, origin.AddMinutes(15 * i)));

        var flat = new List<double>();
        for (var i = 0; i < 40; i++) flat.Add(100.0);

        var stats = AlertBaselineService.ComputeStats("buffer_cache_hit", ".", flat, rows);

        stats.TrendSampleCount.Should().Be(40, "the trend minimum is 20, so the gate is reached");
        stats.TrendSlopePerHour.Should().Be(0, "a regression through a constant has no slope");
        stats.IsTrendWarning.Should().BeFalse();
        stats.IsTrendCritical.Should().BeFalse();

        AlertBaselineService.ApplyTrendDirection(stats, "less_than").Should().Be((false, false));
        AlertBaselineService.ApplyTrendDirection(stats, "greater_than").Should().Be((false, false));
    }
}
