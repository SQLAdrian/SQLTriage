/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data
{
    /// <summary>
    /// Maps a raw reader value for a TimeSeries point.
    ///
    /// A NULL / DBNull measurement is an UNMEASURED instant, not a zero. The old inline mapper
    /// coerced it to 0.0, which plotted a fabricated point that reads as "the queue is empty" /
    /// "the metric is at rest", a false all-clear on an availability signal (e.g. an AG
    /// log_send_queue_size that is NULL because the replica state is unknown). This helper returns
    /// <see cref="double.NaN"/> instead, a sentinel the caller drops so the series shows a gap
    /// rather than a false zero. It mirrors the StatCard mapper, which already refuses to render on
    /// a NULL value.
    /// </summary>
    public static class TimeSeriesValueMapper
    {
        public static double MapValue(object? raw)
            => raw is null || raw is DBNull ? double.NaN : Convert.ToDouble(raw);
    }
}
