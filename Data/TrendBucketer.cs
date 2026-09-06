/* In the name of God, the Merciful, the Compassionate */

using System.Globalization;
using SQLTriage.Data.Services;

namespace SQLTriage.Data;

/// <summary>
/// One bucketed point on the Governance Trend chart/table: either a calendar day or an ISO
/// week, holding the min/max/representative score across however many raw snapshots fell in
/// that period. Feeds both the on-screen /cio trend chart and the PDF trend table so a server
/// with a dense audit history renders as a compact, legible handful of buckets instead of one
/// bar per raw snapshot (2026-07-13 density fix).
/// </summary>
public sealed class TrendBucket
{
    /// <summary>
    /// Real start-of-bucket date — midnight for a day, the ISO week's Monday for a week.
    /// Always a genuine <see cref="DateTime"/>, never a raw strftime string, so downstream
    /// delta/span math never needs to re-parse it.
    /// </summary>
    public DateTime BucketStart { get; set; }

    /// <summary>Short, non-truncating label derived from BucketStart, e.g. "13 Jul" (daily) or "w/c 07 Jul" (weekly).</summary>
    public string Label { get; set; } = "";

    public double Min { get; set; }
    public double Max { get; set; }

    /// <summary>
    /// The last raw snapshot's score within the bucket — drives the bar/chip colour, matching
    /// how the un-bucketed chart always reflected the most recent state for a given period.
    /// </summary>
    public double Representative { get; set; }

    /// <summary>The Representative snapshot's own recorded Band (e.g. "Gold") — never recomputed here, so it always matches the band the app assigned that snapshot at record time.</summary>
    public string Band { get; set; } = "";

    public int SampleCount { get; set; }
}

/// <summary>
/// Governance Trend bucketing (2026-07-13). The on-screen chart and PDF trend table used to
/// plot every raw governance_history snapshot — for a server audited several times a day
/// that's dozens of bars over a 90-day window, each squeezed to a few px so a "07/13" label
/// ellipsis-truncated to "0.". This pure, static helper buckets raw points to min/max per
/// calendar day or ISO week (matching the existing Weekly/Daily toggle) so the on-screen chart
/// and the PDF export read from the exact same bucketing and can never disagree.
/// </summary>
public static class TrendBucketer
{
    private const int DailyCap = 30;
    private const int WeeklyCap = 12;

    /// <summary>
    /// Groups raw trend points by calendar day ("daily") or ISO week (any other value,
    /// including the default "weekly") and returns them oldest-first, capped to the most
    /// recent <see cref="DailyCap"/>/<see cref="WeeklyCap"/> buckets. Points whose RecordedAt
    /// doesn't parse as a date are skipped rather than crashing the chart over one bad row —
    /// in practice every row comes straight from a SQLite datetime() column, so this should
    /// never fire.
    /// </summary>
    public static List<TrendBucket> BucketTrend(IReadOnlyList<GovernanceTrendPoint> raw, string view)
    {
        var buckets = new List<TrendBucket>();
        if (raw == null || raw.Count == 0) return buckets;

        var daily = string.Equals(view, "daily", StringComparison.OrdinalIgnoreCase);

        var parsed = new List<(DateTime When, GovernanceTrendPoint Point)>();
        foreach (var p in raw)
        {
            if (DateTime.TryParse(p.RecordedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                parsed.Add((dt, p));
        }
        if (parsed.Count == 0) return buckets;

        // GetTrend's SQL already orders ASC, but sort defensively so "last in bucket" is
        // always the latest snapshot regardless of what the caller hands in.
        parsed.Sort((a, b) => a.When.CompareTo(b.When));

        var groups = daily
            ? parsed.GroupBy(x => x.When.Date)
            : parsed.GroupBy(x => IsoWeekStart(x.When));

        foreach (var g in groups.OrderBy(x => x.Key))
        {
            var ordered = g.OrderBy(x => x.When).ToList();
            var last = ordered[^1].Point;
            buckets.Add(new TrendBucket
            {
                BucketStart = g.Key,
                Label = daily
                    ? g.Key.ToString("dd MMM", CultureInfo.InvariantCulture)
                    : $"w/c {g.Key.ToString("dd MMM", CultureInfo.InvariantCulture)}",
                Min = ordered.Min(x => x.Point.OverallScore),
                Max = ordered.Max(x => x.Point.OverallScore),
                Representative = last.OverallScore,
                Band = last.Band,
                SampleCount = ordered.Count
            });
        }

        var cap = daily ? DailyCap : WeeklyCap;
        return buckets.Count > cap ? buckets.Skip(buckets.Count - cap).ToList() : buckets;
    }

    private static DateTime IsoWeekStart(DateTime dt)
    {
        var year = ISOWeek.GetYear(dt);
        var week = ISOWeek.GetWeekOfYear(dt);
        return ISOWeek.ToDateTime(year, week, DayOfWeek.Monday);
    }
}
