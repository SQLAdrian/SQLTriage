/* In the name of God, the Merciful, the Compassionate */

using System;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Covers <see cref="TimeSeriesValueMapper.MapValue"/>, the seam extracted from dashboards-r2-04:
    /// <c>DynamicDashboard.MapTimeSeriesPoint</c> once coerced a NULL measurement into a plotted
    /// <c>0.0</c>, a fabricated point that reads as "the queue is empty / the metric is at rest" — a
    /// false all-clear on an availability signal such as an AG log_send_queue_size that is NULL because
    /// the replica state is unknown. The load-bearing assertion is that a NULL/DBNull maps to NaN (an
    /// unmeasured instant the caching layer then drops as a gap), NEVER to 0. If the 0.0 coercion is
    /// re-added, this test goes red.
    /// </summary>
    public class TimeSeriesValueMapperTests
    {
        [Fact]
        public void DbNull_maps_to_NaN_not_zero()
        {
            var v = TimeSeriesValueMapper.MapValue(DBNull.Value);
            Assert.True(double.IsNaN(v));
            Assert.NotEqual(0.0, v);
        }

        [Fact]
        public void Null_reference_maps_to_NaN()
        {
            Assert.True(double.IsNaN(TimeSeriesValueMapper.MapValue(null)));
        }

        [Theory]
        [InlineData(5.0)]
        [InlineData(0.0)]       // a REAL measured zero survives — only NULL becomes NaN
        [InlineData(-3.5)]
        [InlineData(1234567.0)]
        public void Real_values_pass_through_unchanged(double raw)
        {
            Assert.Equal(raw, TimeSeriesValueMapper.MapValue(raw));
        }

        [Fact]
        public void Boxed_integer_converts()
        {
            Assert.Equal(42.0, TimeSeriesValueMapper.MapValue((object)42L));
        }
    }
}
