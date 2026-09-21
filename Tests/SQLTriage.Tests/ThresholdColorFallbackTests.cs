/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Covers dashboards-r1-03 / r1-04: <see cref="ThresholdConfig.GetColor"/> returned Green for any
    /// metric absent from its dictionary, so every threshold-less tile — and every health-check tile
    /// whose Label is CRITICAL/WARNING/ERROR/OK (none of which is a dictionary key) — painted the same
    /// healthy green regardless of value. The fix makes the unknown-metric fallback neutral Gray: no
    /// verdict, never a fabricated all-clear. Known metrics still resolve their real threshold colour.
    /// </summary>
    public class ThresholdColorFallbackTests
    {
        [Theory]
        [InlineData("CRITICAL", 95.0)]   // the exact r1-03 case: a CRITICAL check value
        [InlineData("WARNING", 3.0)]
        [InlineData("OK", 323.0)]
        [InlineData("checks.critical_failing", 7.0)]  // r1-04 threshold-less panel key
        [InlineData("", 0.0)]                          // empty key (StatThresholdKey ?? "" path)
        [InlineData("some.metric.nobody.registered", 999.0)]
        public void Unknown_metric_falls_back_to_neutral_gray_not_green(string metric, double value)
        {
            var color = ThresholdConfig.GetColor(metric, value);
            Assert.Equal(ThresholdConfig.Gray, color);
            Assert.NotEqual(ThresholdConfig.Green, color);
        }

        [Fact]
        public void Unknown_metric_thresholds_band_is_gray_not_green()
        {
            var bands = ThresholdConfig.GetThresholds("no.such.metric");
            Assert.Single(bands);
            Assert.Equal(ThresholdConfig.Gray, bands[0].Color);
        }

        [Theory]
        // Known metrics keep resolving their real bands — the fix must not neutralise real verdicts.
        [InlineData("CPU Usage %", 96.0, "#f44336")]   // Red at >= 95
        [InlineData("CPU Usage %", 10.0, "#1b5e20")]   // DarkGreen at 0
        [InlineData("Disk Space Used %", 85.0, "#ff9800")] // LightOrange at >= 80
        public void Known_metric_resolves_its_real_threshold_color(string metric, double value, string expected)
        {
            Assert.Equal(expected, ThresholdConfig.GetColor(metric, value));
        }
    }
}
