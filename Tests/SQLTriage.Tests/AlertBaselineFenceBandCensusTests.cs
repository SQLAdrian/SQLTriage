/* In the name of God, the Merciful, the Compassionate */

// INVARIANT A (lane alert-correctness, 2026-09-19): A LEARNED BASELINE NEVER RETURNS A FENCE PAIR
// WITH NO WARNING BAND. The harm predicate is "crit does not exceed warn" (upper pair) or "critLower
// is not below warnLower" (lower pair): the evaluator tests the two fences independently, so a
// collapsed pair makes every learned fire Critical and Warning can never be the answer.
//
// The only method that hands a learned fence to the evaluator is AlertBaselineService.SelectFences
// (GetThresholds is its one caller, and AlertEvaluationService reads fences only through
// GetThresholds). So the census here enumerates INPUTS, not call sites: it drives generated windows
// through the real ComputeStats and the real SelectFences for both operators, and goes red on any
// returned pair without a strict band. It also goes red if the gate over-suppresses, i.e. refuses a
// pair that DOES have a band because the OTHER pair is degenerate (the control below).
//
// The three degenerate windows with spread are the brief's arithmetic, reproduced from
// the project's private evidence archive, evidence/validation-sweep-2-2026-09-18/a3a4/fence.py and fence2.py.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

public sealed class AlertBaselineFenceBandCensusTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;
    private liveQueriesCacheStore? _cache;

    public AlertBaselineFenceBandCensusTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "fence-band-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _cache?.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly List<(string AlertId, string ServerName, double Value, DateTime SampledAt)> NoTrend = new();

    private static List<double> Window(params (int Count, double Value)[] parts)
    {
        var v = new List<double>();
        foreach (var (count, value) in parts)
            for (var i = 0; i < count; i++) v.Add(value);
        v.Sort();
        return v;
    }

    // ── The three degenerate windows WITH spread (iqr > 0) ─────────────────────

    public static IEnumerable<object[]> DegenerateWindowsWithSpread() => new[]
    {
        // name, operator, window parts as (count, value) pairs flattened, collapsed fence value
        new object[] { "gt [40x0, 35x1, 20x2, 5x500]", "greater_than", new[] { 40.0, 0, 35, 1, 20, 2, 5, 500 }, 1.25, 26.9 },
        new object[] { "gt [70x0, 22x1, 8x300]", "greater_than", new[] { 70.0, 0, 22, 1, 8, 300 }, 1.0, 300.0 },
        new object[] { "lt [60x100.0, 20x99.9, 15x99.8, 5x10.0]", "less_than", new[] { 60.0, 100.0, 20, 99.9, 15, 99.8, 5, 10.0 }, 0.1, 95.31 },
    };

    private static List<double> FromFlat(double[] flat)
    {
        var parts = new List<(int, double)>();
        for (var i = 0; i < flat.Length; i += 2) parts.Add(((int)flat[i], flat[i + 1]));
        return Window(parts.ToArray());
    }

    [Theory]
    [MemberData(nameof(DegenerateWindowsWithSpread))]
    public void A_window_with_spread_whose_pair_collapsed_gets_no_learned_fence(
        string name, string op, double[] flat, double expectedIqr, double collapsedAt)
    {
        var stats = AlertBaselineService.ComputeStats("census", "SRV1", FromFlat(flat), NoTrend);

        // The fixture must BE the shape: spread, and a collapsed pair on the side the operator uses.
        stats.Iqr.Should().BeApproximately(expectedIqr, 1e-9, $"{name}: the fixture's iqr");
        stats.Iqr.Should().BeGreaterThan(0, "the Iqr <= 0 ruling would suppress it for a different reason");
        if (op == "less_than")
        {
            stats.ThresholdWarnLower.Should().BeApproximately(collapsedAt, 1e-9, name);
            stats.ThresholdCritLower.Should().Be(stats.ThresholdWarnLower,
                $"{name}: the P05 cap swallows the critical multiplier, so the lower pair collapsed");
        }
        else
        {
            stats.ThresholdWarn.Should().BeApproximately(collapsedAt, 1e-9, name);
            stats.ThresholdCrit.Should().Be(stats.ThresholdWarn,
                $"{name}: the P95 floor swallows the critical multiplier, so the upper pair collapsed");
        }

        AlertBaselineService.SelectFences(stats, op).Should().Be(((double?)null, (double?)null),
            $"{name}: a pair with no warning band would make every learned fire Critical");
    }

    // ── The control: only the RETURNED pair is gated ────────────────────────────

    [Fact]
    public void A_greater_than_window_keeps_its_upper_fence_although_its_lower_pair_is_degenerate()
    {
        var stats = AlertBaselineService.ComputeStats(
            "census", "SRV1", Window((6, 0), (25, 99), (44, 100), (25, 101)), NoTrend);

        stats.Iqr.Should().BeApproximately(1.25, 1e-9);
        stats.ThresholdWarnLower.Should().Be(stats.ThresholdCritLower,
            "the fixture's LOWER pair is degenerate (both 0), which is what makes it the control");

        var (warn, crit) = AlertBaselineService.SelectFences(stats, "greater_than");
        warn.Should().NotBeNull("a gate that suppresses on EITHER pair strips this valid upper fence");
        warn!.Value.Should().BeApproximately(102.125, 1e-9);
        crit!.Value.Should().BeApproximately(104.0, 1e-9);

        // The same window read by a less_than alert returns the degenerate lower pair, so no fence.
        AlertBaselineService.SelectFences(stats, "less_than").Should().Be(((double?)null, (double?)null));
    }

    // ── Rows already persisted: inverted (pre-clamp) and NaN ───────────────────

    [Fact]
    public void A_persisted_inverted_or_NaN_pair_is_refused_on_read()
    {
        var invertedUpper = new BaselineStats { SampleCount = 50, Iqr = 1, ThresholdWarn = 10, ThresholdCrit = 5, ThresholdWarnLower = 1, ThresholdCritLower = 0 };
        AlertBaselineService.SelectFences(invertedUpper, "greater_than").Should().Be(((double?)null, (double?)null),
            "a row written before the clamp existed can hold crit below warn");
        AlertBaselineService.SelectFences(invertedUpper, "less_than").Should().Be(((double?)1, (double?)0),
            "its lower pair has a real band and is still handed back");

        var invertedLower = new BaselineStats { SampleCount = 50, Iqr = 1, ThresholdWarn = 10, ThresholdCrit = 20, ThresholdWarnLower = 5, ThresholdCritLower = 7 };
        AlertBaselineService.SelectFences(invertedLower, "less_than").Should().Be(((double?)null, (double?)null));

        var nan = new BaselineStats { SampleCount = 50, Iqr = 1, ThresholdWarn = double.NaN, ThresholdCrit = 20, ThresholdWarnLower = double.NaN, ThresholdCritLower = 0 };
        AlertBaselineService.SelectFences(nan, "greater_than").Should().Be(((double?)null, (double?)null), "NaN compares false both ways");
        AlertBaselineService.SelectFences(nan, "less_than").Should().Be(((double?)null, (double?)null));
    }

    // ── The census: generated windows through the real arithmetic ──────────────

    [Fact]
    public void Census_no_generated_window_returns_a_pair_without_a_warning_band_and_none_is_over_suppressed()
    {
        var rng = new Random(20260919);
        var ops = new[] { "greater_than", "less_than", "not_equal_to_anything_else" };
        var kept = ops.ToDictionary(o => o, _ => 0);
        var refusedWithSpread = ops.ToDictionary(o => o, _ => 0);
        var refusedFlat = ops.ToDictionary(o => o, _ => 0);
        const int windows = 4000;

        for (var w = 0; w < windows; w++)
        {
            // A few distinct levels with random weights, sometimes a far outlier tail: the shapes
            // that make a P95 floor or a P05 cap swallow a multiplier.
            var levels = rng.Next(1, 6);
            var parts = new List<(int, double)>();
            var baseValue = rng.Next(0, 3) switch { 0 => 0.0, 1 => rng.Next(1, 200), _ => 90 + rng.NextDouble() * 10 };
            for (var l = 0; l < levels; l++)
                parts.Add((rng.Next(1, 60), Math.Round(baseValue + l * (rng.NextDouble() < 0.5 ? 0.1 : rng.Next(1, 5)), 3)));
            if (rng.NextDouble() < 0.5)
                parts.Add((rng.Next(1, 8), Math.Round(baseValue + (rng.NextDouble() < 0.5 ? -1 : 1) * rng.Next(10, 1000), 3)));

            var stats = AlertBaselineService.ComputeStats("census", "SRV1", Window(parts.ToArray()), NoTrend);

            foreach (var op in ops)
            {
                var (warn, crit) = AlertBaselineService.SelectFences(stats, op);
                var lower = op == "less_than";
                var pairHasBand = lower
                    ? stats.ThresholdCritLower < stats.ThresholdWarnLower
                    : stats.ThresholdCrit > stats.ThresholdWarn;

                if (warn.HasValue || crit.HasValue)
                {
                    warn.HasValue.Should().BeTrue();
                    crit.HasValue.Should().BeTrue();
                    (lower ? crit!.Value < warn!.Value : crit!.Value > warn!.Value).Should().BeTrue(
                        $"window {w} op {op}: returned warn {warn} crit {crit} with no warning band (iqr {stats.Iqr})");
                    kept[op]++;
                }
                else if (stats.Iqr <= 0)
                {
                    refusedFlat[op]++;
                }
                else
                {
                    pairHasBand.Should().BeFalse(
                        $"window {w} op {op}: iqr {stats.Iqr} and a real band, yet no fence was returned (over-suppression)");
                    refusedWithSpread[op]++;
                }
            }
        }

        foreach (var op in ops)
            _out.WriteLine($"{op}: kept {kept[op]}, refused with spread (invariant A) {refusedWithSpread[op]}, refused flat (iqr 0) {refusedFlat[op]}, of {windows}");

        // The haystack must contain every outcome, or the census proved nothing about one of them.
        foreach (var op in ops)
        {
            kept[op].Should().BeGreaterThan(0, $"{op}: the sweep must contain windows that keep a fence");
            refusedWithSpread[op].Should().BeGreaterThan(0, $"{op}: the sweep must contain the degenerate-with-spread shape");
            refusedFlat[op].Should().BeGreaterThan(0, $"{op}: the sweep must contain zero-spread windows");
        }
    }

    // ── Through the method the evaluator actually calls ─────────────────────────

    [Fact]
    public void GetThresholds_hands_back_no_fence_for_a_collapsed_pair_with_spread()
    {
        var shipped = ShippedConfig.Path("alert-definitions.json");
        var local = Path.Combine(_dir, "alert-definitions.json");
        File.Copy(shipped, local);
        var definitions = new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance, local);
        definitions.GetAlert("cpu_sql_usage")!.Operator.Should().Be("greater_than");
        definitions.GetAlert("cpu_sql_usage")!.CanBaseline.Should().BeTrue(
            "GetThresholds returns nothing for an alert that cannot baseline, for a different reason");

        var settings = new UserSettingsService(Path.Combine(_dir, "user-settings.json"));
        settings.SetAlertBaselineEnabled(true);
        settings.SetAlertBaselinePerServer(true);
        _cache = new liveQueriesCacheStore();

        using var svc = new AlertBaselineService(
            NullLogger<AlertBaselineService>.Instance, definitions,
            new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance), _cache, settings);

        var stats = AlertBaselineService.ComputeStats(
            "cpu_sql_usage", ".\\NEW2022", Window((40, 0), (35, 1), (20, 2), (5, 500)), NoTrend);
        stats.SampleCount.Should().BeGreaterThanOrEqualTo(10, "below the seed minimum the answer is null for another reason");
        stats.Iqr.Should().BeGreaterThan(0);

        var key = (string)typeof(AlertBaselineService)
            .GetMethod("Key", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { "cpu_sql_usage", ".\\NEW2022" })!;
        var dict = (System.Collections.Concurrent.ConcurrentDictionary<string, BaselineStats>)typeof(AlertBaselineService)
            .GetField("_stats", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(svc)!;
        dict[key] = stats;

        svc.GetThresholds("cpu_sql_usage", ".\\NEW2022").Should().Be(((double?)null, (double?)null),
            "invariant A through the method AlertEvaluationService calls");

        // Control through the same path: a window with a real band is handed back.
        var healthy = AlertBaselineService.ComputeStats(
            "cpu_sql_usage", ".\\NEW2022", Window((6, 0), (25, 99), (44, 100), (25, 101)), NoTrend);
        dict[key] = healthy;
        var (warn, crit) = svc.GetThresholds("cpu_sql_usage", ".\\NEW2022");
        warn.Should().NotBeNull();
        crit!.Value.Should().BeGreaterThan(warn!.Value);
    }
}
