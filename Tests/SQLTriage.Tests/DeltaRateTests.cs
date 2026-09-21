// In the name of God, the Merciful, the Compassionate

using System;
using System.Collections.Generic;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The arithmetic behind the "per second" tiles. The defect these pin: a cumulative counter
    /// rendered under a per-second label. The rate is the number the label describes, and an
    /// unmeasured rate must not be printable as zero.
    /// </summary>
    public class DeltaRateTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 6, 9, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void One_sample_is_not_a_rate()
        {
            var samples = new List<(DateTime, double)> { (T0, 2_102_306d) };

            Assert.False(DeltaRate.TryCompute(samples, out var rate));
            Assert.Equal(0d, rate);
        }

        [Fact]
        public void No_samples_is_not_a_rate()
        {
            Assert.False(DeltaRate.TryCompute(new List<(DateTime, double)>(), out _));
        }

        [Fact]
        public void Null_samples_is_not_a_rate()
        {
            Assert.False(DeltaRate.TryCompute(null!, out _));
        }

        [Fact]
        public void Two_samples_ten_seconds_apart_give_the_per_second_difference()
        {
            var samples = new List<(DateTime, double)>
            {
                (T0, 1_000d),
                (T0.AddSeconds(10), 1_120d),
            };

            Assert.True(DeltaRate.TryCompute(samples, out var rate));
            Assert.Equal(12d, rate, 6);
        }

        [Fact]
        public void The_window_spans_oldest_to_newest_not_the_last_pair()
        {
            // Five samples, the queue depth the card keeps. The rate is measured across the whole
            // window, so a single quiet interval in the middle does not read as a stalled server.
            var samples = new List<(DateTime, double)>
            {
                (T0, 0d),
                (T0.AddSeconds(5), 50d),
                (T0.AddSeconds(10), 50d),
                (T0.AddSeconds(15), 150d),
                (T0.AddSeconds(20), 200d),
            };

            Assert.True(DeltaRate.TryCompute(samples, out var rate));
            Assert.Equal(10d, rate, 6);
        }

        [Fact]
        public void Identical_timestamps_are_not_a_rate()
        {
            var samples = new List<(DateTime, double)> { (T0, 100d), (T0, 250d) };

            Assert.False(DeltaRate.TryCompute(samples, out var rate));
            Assert.Equal(0d, rate);
        }

        [Fact]
        public void A_counter_that_went_backwards_is_a_reset_not_a_negative_rate()
        {
            // sys.dm_os_performance_counters counts up from instance start. It only decreases
            // across a restart or a counter clear; the difference across that discontinuity is not
            // a rate, and printing it would put a negative "per second" figure on a client screen.
            var samples = new List<(DateTime, double)>
            {
                (T0, 2_102_306d),
                (T0.AddSeconds(10), 41d),
            };

            Assert.False(DeltaRate.TryCompute(samples, out var rate));
            Assert.Equal(0d, rate);
        }

        [Fact]
        public void A_flat_counter_is_a_measured_zero()
        {
            // The one case where zero IS the reading: two samples, time elapsed, nothing counted.
            var samples = new List<(DateTime, double)>
            {
                (T0, 500d),
                (T0.AddSeconds(10), 500d),
            };

            Assert.True(DeltaRate.TryCompute(samples, out var rate));
            Assert.Equal(0d, rate);
        }
    }
}
