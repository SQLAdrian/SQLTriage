/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The pure-logic honesty fixes of the alerts lane's Cluster D, each exercised on the exact
    /// decision the defect lived in — no re-implementation.
    /// </summary>
    public sealed class AlertClusterDHonestyTests
    {
        // ── alerts-r2-13: a trend fire must have a regression that actually explains the data ──

        [Fact]
        public void ComputeStats_doesNotTrendOnANoisySeriesWhoseSlopeFitsNothing()
        {
            // 40 hourly samples: a gentle +1/hour drift buried under an alternating ±40 that fits no
            // line. The OLS slope still clears the warn slope-ratio, so before the R² gate this fired;
            // the fit (R²) is what says the "trend" is an artefact of noise.
            var origin = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
            var rows = new List<(string, string, double, DateTime)>();
            var values = new List<double>();
            for (var i = 0; i < 40; i++)
            {
                var v = 100.0 + 1.0 * i + (i % 2 == 0 ? +40 : -40);
                rows.Add(("noisy_metric", "SRV1", v, origin.AddHours(i)));
                values.Add(v);
            }
            values.Sort();

            var stats = AlertBaselineService.ComputeStats("noisy_metric", "SRV1", values, rows);

            Assert.True(stats.TrendSampleCount >= 20, "the sample-count gate must be cleared, so it is not what suppresses this");
            Assert.True(Math.Abs(stats.TrendSlopePerHour) / stats.P50 >= 0.005,
                "the slope-ratio alone clears the warn bar — without the R² gate this series fires");
            Assert.True(stats.TrendRSquared < 0.3, "the fixture must genuinely be a poor fit");
            Assert.False(stats.IsTrendWarning, "a slope that fits nothing is a fabricated shape, not a trend");
            Assert.False(stats.IsTrendCritical);
        }

        [Fact]
        public void ComputeStats_stillTrendsOnACleanRamp_soTheGateIsAFloorNotABlanket()
        {
            var origin = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
            var rows = new List<(string, string, double, DateTime)>();
            var values = new List<double>();
            for (var i = 0; i < 40; i++)
            {
                var v = 100.0 + 5.0 * i;   // a perfect line — R² == 1
                rows.Add(("clean_metric", "SRV1", v, origin.AddHours(i)));
                values.Add(v);
            }
            values.Sort();

            var stats = AlertBaselineService.ComputeStats("clean_metric", "SRV1", values, rows);

            Assert.True(stats.TrendRSquared >= 0.3);
            Assert.True(stats.IsTrendCritical, "a real, well-fit steep trend must still fire");
        }

        // ── alerts-r1-12: a stored trend signal must still describe the present ──

        [Fact]
        public void IsTrendCurrent_trustsAFreshStat_andDeclinesAStaleOrUndersampledOne()
        {
            var now = DateTime.UtcNow;

            var fresh = new BaselineStats { TrendSampleCount = 40, LastComputed = now.AddMinutes(-30) };
            Assert.True(AlertBaselineService.IsTrendCurrent(fresh, now));

            // Frozen well beyond the 72 h trend window — a data gap. The "last 72 h of samples" claim
            // no longer overlaps now, so the stat must not fire on the first new sample.
            var stale = new BaselineStats { TrendSampleCount = 40, LastComputed = now.AddHours(-200) };
            Assert.False(AlertBaselineService.IsTrendCurrent(stale, now));

            // Fresh but never had enough recent samples for a meaningful slope.
            var thin = new BaselineStats { TrendSampleCount = 5, LastComputed = now };
            Assert.False(AlertBaselineService.IsTrendCurrent(thin, now));
        }

        // ── alerts-r1-05: auto-resolve means "re-checked and clear", not "stopped looking" ──

        [Fact]
        public void ShouldAutoResolveAsCleared_onlyResolvesWhatWasActuallyReEvaluated()
        {
            const int freq = 60;                       // stale cutoff = 180 s
            var now = DateTime.UtcNow;

            // Stale AND evaluated 10 s ago: we checked, it did not re-fire → clear.
            Assert.True(AlertEvaluationService.ShouldAutoResolveAsCleared(
                AlertStatus.Active, now.AddSeconds(-200), now.AddSeconds(-10), freq, now));

            // Stale but last evaluated 200 s ago (suppressed by a maintenance window all cycle):
            // nothing measured it → we do not know it cleared.
            Assert.False(AlertEvaluationService.ShouldAutoResolveAsCleared(
                AlertStatus.Active, now.AddSeconds(-200), now.AddSeconds(-200), freq, now));

            // Stale and never evaluated at all.
            Assert.False(AlertEvaluationService.ShouldAutoResolveAsCleared(
                AlertStatus.Active, now.AddSeconds(-200), null, freq, now));

            // Not stale yet — still within the window.
            Assert.False(AlertEvaluationService.ShouldAutoResolveAsCleared(
                AlertStatus.Active, now.AddSeconds(-10), now, freq, now));

            // Acknowledged is not Active; this method never resolves it.
            Assert.False(AlertEvaluationService.ShouldAutoResolveAsCleared(
                AlertStatus.Acknowledged, now.AddSeconds(-200), now, freq, now));
        }

        // ── alerts-r1-06: AlwaysAlert exempts an alert from the OPERATIONAL window only ──

        [Fact]
        public void AlwaysAlert_isStillSuppressedByAMaintenanceWindow()
        {
            // The tooltip used to read "Always Alert (ignores maintenance windows)". ShouldFire checks
            // maintenance BEFORE it ever looks at alwaysAlert, so the claim was false — this pins the
            // real rule the corrected tooltip states.
            var cfg = new AlertWindowConfig { MaintenanceActiveUntil = DateTime.Now.AddMinutes(30) };

            Assert.True(cfg.IsMaintenanceActive);
            Assert.False(cfg.ShouldFire(alwaysAlert: true),
                "maintenance suppresses ALL alerts, including AlwaysAlert ones");
            Assert.False(cfg.ShouldFire(alwaysAlert: false));
        }
    }
}
