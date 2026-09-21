/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace SQLTriage.Data.Caching
{
    /// <summary>
    /// THE RETENTION DESIGN (DECISIONS 2026-08-26 18:23 ruling 4). Read this before touching
    /// <c>metric_history</c> anywhere.
    ///
    /// <para><b>What the ruling asked for.</b> Two dashboard features were prose-honest but not real:
    /// the AG send/redo queue panels showed one sample per refresh and called it a trend, and the
    /// Baseline compare had no 7-day-ago window to draw. Both need retained history. The honesty bar
    /// is explicit: retention makes the features real <i>over time</i>, it does not backfill the past,
    /// so a range with insufficient retained history must still say so.</para>
    ///
    /// <para><b>Storage: a separate table, not a longer-lived cache.</b> The obvious cheaper route was
    /// to keep history in <c>cache_timeseries</c> and remove the three things that delete it — the
    /// per-fetch window trim in <see cref="CachingQueryExecutor"/>, the 24-hour <c>fetched_at</c> sweep
    /// in <see cref="CacheEvictionService"/>, and the full <c>InvalidateAllAsync</c> that any time-range,
    /// instance or timezone change fires. That was rejected. Those three exist for correctness, they
    /// govern all 35 TimeSeries panels, and disabling them would put retained history under the same
    /// 500 MB cap as volatile cache with no separate lifecycle. <c>metric_history</c> instead carries
    /// <b>no <c>fetched_at</c> column</b>, which is what makes it structurally immune to all three:
    /// <c>InvalidateAllCore</c> and <c>PurgeOlderThanCore</c> iterate hardcoded table lists that exclude
    /// it, and <c>EvictOlderThanCore</c> selects its victims by joining <c>pragma_table_info</c> on a
    /// column named <c>fetched_at</c>, so it never sees this table at all.</para>
    ///
    /// <para>⚠⚠ <b>NEVER add a <c>fetched_at</c> column to <c>metric_history</c>.</b> Two separate
    /// failures follow. The sweep would start deleting retained history at 24 hours, silently undoing
    /// the feature. Worse, <c>EvictOlderThanCore</c> passes each discovered table through
    /// <c>ValidateTableName</c>, so a discovered table missing from <c>AllowedTables</c> throws an
    /// <c>ArgumentException</c> that <c>CacheEvictionService</c> swallows as "Cache eviction failed" —
    /// killing <i>all</i> eviction for the whole process. The column name is the trigger, not the table
    /// name.</para>
    ///
    /// <para><b>Encryption posture is unchanged.</b> The table lives in the same
    /// <c>SQLTriage-cache.db</c>, so it is covered by the same SQLCipher key, which
    /// <c>SqliteCipherHelper</c> wraps with DPAPI and persists. Values are stored as plain REAL exactly
    /// like <c>cache_timeseries</c>, and deliberately NOT through <c>DataProtectionService</c>: that
    /// service's key is per-process and ephemeral, so protecting these values would make every retained
    /// sample unreadable after the restart retention exists to survive. Nothing here is a credential or
    /// a query body — a metric value, a series label and a timestamp, at rest behind the same
    /// encryption as the rest of the store.</para>
    ///
    /// <para><b>Bounds.</b> Retention is per-panel opt-in (<c>retainHistory</c> in
    /// dashboard-config.json), downsampled into fixed buckets, capped by BOTH a time window and a
    /// hard row count, and pruned on every collector tick. Defaults: 8 days, 60-second buckets,
    /// 200,000 rows. Arithmetic, not a measurement: the two shipped opt-in panels at 60-second buckets
    /// over 8 days cost 11,520 rows per series, so the cap covers roughly 17 series before it starts
    /// trimming the oldest. Turning retention on for all 35
    /// TimeSeries panels would breach the cache size cap and is deliberately NOT the default — that is
    /// a product decision, recorded for Adrian, not one a builder makes.</para>
    ///
    /// <para>⚠ <b>WHAT 8 DAYS DOES AND DOES NOT COVER.</b> An earlier version of this note called it
    /// "a 7-day baseline window plus a day of margin". That was wrong by six days, and the gate proved
    /// the consequence. The Baseline overlay reads the selected range shifted 7 days back, so for a
    /// range of length L it opens at <c>7 days + L</c> ago. A 24-hour range needs 8 days, which is
    /// exactly what ships and leaves no margin. A "Last 7 days" range needs 14, so at the shipped
    /// window that baseline can never cover more than a seventh of itself, in the steady state,
    /// permanently. The honesty bar is kept the only way a builder may keep it: the notice measures the
    /// SHIFTED window and says how much of it the comparison actually spans
    /// (<c>DynamicDashboard.BuildBaselineNotice</c>). Widening the window to cover a 7-day baseline
    /// roughly doubles retained disk on a client's production server, so the number itself is Adrian's
    /// call and is recorded for him, not taken here.</para>
    /// </summary>
    public sealed class MetricRetentionOptions
    {
        /// <summary>Master switch. Off means nothing is written, nothing is read, and the honesty
        /// notices say retention is off rather than pretending history is merely missing.</summary>
        public bool Enabled { get; init; } = true;

        /// <summary>How far back retained history is kept. Pruned by age on every collector tick.</summary>
        public TimeSpan Window { get; init; } = TimeSpan.FromDays(8);

        /// <summary>Downsample bucket. One row per (panel, instance, series, bucket).</summary>
        public TimeSpan Bucket { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>Hard upper bound on total retained rows. Enforced after the age prune by deleting
        /// the oldest buckets first, so the cap can never be crossed by adding series or panels.</summary>
        public long MaxRows { get; init; } = 200_000;

        /// <summary>How often the background collector samples the opt-in panels and prunes.</summary>
        public TimeSpan CollectInterval { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Binds from configuration under the <c>MetricRetention</c> section. Every value is clamped:
        /// a hostile or fat-fingered setting degrades to a safe bound rather than to unbounded disk.
        /// </summary>
        public static MetricRetentionOptions FromConfiguration(IConfiguration? configuration)
        {
            if (configuration == null) return new MetricRetentionOptions();

            var enabled = configuration.GetValue("MetricRetention:Enabled", true);
            var windowDays = Clamp(configuration.GetValue("MetricRetention:WindowDays", 8), 1, 90);
            var bucketSeconds = Clamp(configuration.GetValue("MetricRetention:BucketSeconds", 60), 10, 3600);
            var collectSeconds = Clamp(configuration.GetValue("MetricRetention:CollectSeconds", 60), 10, 3600);
            var maxRows = Clamp(configuration.GetValue("MetricRetention:MaxRows", 200_000L), 1_000L, 5_000_000L);

            return new MetricRetentionOptions
            {
                Enabled = enabled,
                Window = TimeSpan.FromDays(windowDays),
                Bucket = TimeSpan.FromSeconds(bucketSeconds),
                CollectInterval = TimeSpan.FromSeconds(collectSeconds),
                MaxRows = maxRows
            };
        }

        private static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);
        private static long Clamp(long value, long min, long max) => value < min ? min : (value > max ? max : value);
    }

    /// <summary>
    /// The one place retained timestamps are converted. Everything in <c>metric_history</c> is stored
    /// as a fixed-width canonical UTC string so that ordering, range predicates and the age prune are
    /// all plain string comparisons that cannot disagree.
    ///
    /// <para>⚠ WHY NOT <c>ToString("o")</c>, which is what <c>cache_timeseries</c> uses. Round-trip
    /// format emits an offset suffix for a <see cref="DateTimeKind.Local"/> value and nothing for an
    /// <see cref="DateTimeKind.Unspecified"/> one. Panel rows come from <c>GETDATE()</c> and arrive
    /// Unspecified; range bounds come from the toolbar as <c>DateTime.Now</c> arithmetic and arrive
    /// Local. Comparing the two as strings works only because the differing character is almost always
    /// left of the suffix. That is luck, not a contract, and the age prune is a DELETE — the one
    /// caller that must not rely on luck.</para>
    ///
    /// <para>An Unspecified value is read as local time, which is the existing contract everywhere
    /// else: <c>GETDATE()</c> is the monitored server's wall clock, and the dashboard already compares
    /// it directly against <c>DateTime.Now</c>.</para>
    /// </summary>
    public static class MetricHistoryTime
    {
        /// <summary>Fixed width, sortable, unambiguous. Second resolution: the smallest bucket the
        /// options allow is 10 seconds, so sub-second precision would only cost bytes.</summary>
        public const string CanonicalFormat = "yyyy-MM-ddTHH:mm:ssZ";

        /// <summary>Converts any DateTime to UTC under the rule above.</summary>
        public static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
        };

        /// <summary>Floors a UTC instant to the start of its retention bucket.</summary>
        public static DateTime FloorToBucket(DateTime utc, TimeSpan bucket)
        {
            var ticks = bucket.Ticks <= 0 ? TimeSpan.TicksPerMinute : bucket.Ticks;
            return new DateTime(utc.Ticks - (utc.Ticks % ticks), DateTimeKind.Utc);
        }

        /// <summary>The stored key for a sample: its instant, floored to a bucket, canonically formatted.</summary>
        public static string BucketKey(DateTime value, TimeSpan bucket)
            => Format(FloorToBucket(ToUtc(value), bucket));

        /// <summary>The stored form of an instant, used for range bounds and prune cutoffs.</summary>
        public static string Format(DateTime value)
            => ToUtc(value).ToString(CanonicalFormat, CultureInfo.InvariantCulture);

        /// <summary>Parses a stored key back to a UTC instant.</summary>
        public static DateTime Parse(string stored)
            => DateTime.SpecifyKind(
                DateTime.ParseExact(stored, CanonicalFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                DateTimeKind.Utc);

        /// <summary>Parses a stored key back to local wall-clock time, which is the axis the charts
        /// and the toolbar range are drawn in.</summary>
        public static DateTime ParseToLocal(string stored) => Parse(stored).ToLocalTime();
    }

    /// <summary>
    /// What retained history actually covers for one panel, so a caller can tell the operator the
    /// truth instead of drawing a short line and calling it a trend.
    /// </summary>
    /// <param name="RowCount">Retained rows inside the requested window.</param>
    /// <param name="EarliestRetainedLocal">Oldest retained sample for this panel and instance across
    /// the whole store, in local time. Null when nothing has ever been retained.</param>
    /// <param name="PointCap">The chart point cap the read strides against, from user settings.
    /// Zero when retention is off for this panel and no read will happen.</param>
    /// <param name="LatestRetainedLocal">Newest retained sample for this panel and instance across the
    /// whole store, in local time. The mirror of <paramref name="EarliestRetainedLocal"/>, and the only
    /// way a caller can tell history that STOPPED from history that is merely short at the front. Null
    /// when nothing has ever been retained, and null for a caller that did not measure it.</param>
    public readonly record struct RetentionCoverage(
        int RowCount, DateTime? EarliestRetainedLocal, int PointCap = 0, DateTime? LatestRetainedLocal = null)
    {
        public bool HasAnyHistory => EarliestRetainedLocal.HasValue;

        /// <summary>
        /// True when retained history starts AFTER the requested window opened, i.e. the earlier part
        /// of the range is not history that is missing, it is history that was never recorded. The
        /// caller must say so.
        /// </summary>
        public bool IsShortOfWindow(DateTime windowStartLocal)
            => !HasAnyHistory || EarliestRetainedLocal!.Value > windowStartLocal;

        /// <summary>
        /// True when the range holds more retained points than the chart draws, so the line is a
        /// strided sample of the retained buckets rather than all of them.
        ///
        /// <para>This is the second way a retained line can be less than it looks, and it is not the
        /// same fact as <see cref="IsShortOfWindow"/>. That one says history does not reach back far
        /// enough. This one says history reaches all the way and the chart is drawing a subset of it.
        /// Both must be said out loud: the panel notice is silent only when a line is whole.</para>
        /// </summary>
        public bool IsDownsampled => PointCap > 0 && RowCount > PointCap;

        /// <summary>
        /// True when retained history STOPS before the requested window closed, so the later part of
        /// the range is not history that is missing, it is history that was never recorded. The mirror
        /// of <see cref="IsShortOfWindow"/>, and a fact it cannot see: a panel whose collector went
        /// quiet has history reaching well past the start of the window and none at the end of it.
        ///
        /// <para>⚠ <b>Only sound for a window that has already closed.</b> A running collector's newest
        /// sample is one bucket old, so against a window ending at <c>now</c> this is true on every
        /// load and means nothing. The baseline window always ends at least 7 days in the past, which
        /// is what makes the predicate silent in normal operation: it fires only when a panel recorded
        /// nothing for the seven days or more between the window closing and now. Do NOT wire it to
        /// the toolbar-window notice.</para>
        ///
        /// <para>False when nothing was ever retained. That is <see cref="IsShortOfWindow"/>'s fact,
        /// and reporting it twice under two different explanations would blur them.</para>
        /// </summary>
        public bool EndsBeforeWindowCloses(DateTime windowEndLocal)
            => LatestRetainedLocal is DateTime latest && latest < windowEndLocal;

        /// <summary>
        /// True when the window falls inside a hole in the retained history: nothing at all was
        /// retained inside it, while history exists on BOTH sides. The panel draws no comparison line,
        /// and neither boundary predicate can see it, because history reaches past the start and past
        /// the end. Zero rows decides it, so there is no tolerance and no threshold to argue about.
        /// </summary>
        public bool WindowFallsInAGap(DateTime windowStartLocal, DateTime windowEndLocal)
            => RowCount == 0
               && HasAnyHistory
               && !IsShortOfWindow(windowStartLocal)
               && !EndsBeforeWindowCloses(windowEndLocal);
    }
}
