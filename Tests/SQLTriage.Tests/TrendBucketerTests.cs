/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data;
using SQLTriage.Data.Services;

namespace SQLTriage.Tests;

/// <summary>
/// Governance Trend bucketing (2026-07-13): the on-screen chart and PDF trend table used to
/// plot every raw governance_history snapshot — dozens of bars over a 90-day window, each
/// label truncated to "0.". These pin the bucketing behind the fix: grouping by day/ISO week,
/// min/max/representative/band per bucket, the cap, and the specific historical bug where a
/// raw strftime week label ("2026-W28") failed DateTime.TryParse and a bare DateTime.Parse on
/// it silently threw (swallowed by a catch).
/// </summary>
public class TrendBucketerTests
{
    private static GovernanceTrendPoint Point(string recordedAt, double score, string band) => new()
    {
        RecordedAt = recordedAt,
        OverallScore = score,
        Band = band
    };

    [Fact]
    public void BucketTrend_EmptyRaw_ReturnsEmpty()
        => Assert.Empty(TrendBucketer.BucketTrend(new List<GovernanceTrendPoint>(), "daily"));

    [Fact]
    public void BucketTrend_NullRaw_ReturnsEmpty()
        => Assert.Empty(TrendBucketer.BucketTrend(null!, "daily"));

    [Fact]
    public void BucketTrend_SinglePoint_MinMaxRepresentativeAllEqualItsScore()
    {
        var raw = new List<GovernanceTrendPoint> { Point("2026-07-13 09:00:00", 91, "Platinum") };

        var buckets = TrendBucketer.BucketTrend(raw, "daily");

        var bucket = Assert.Single(buckets);
        Assert.Equal(91, bucket.Min);
        Assert.Equal(91, bucket.Max);
        Assert.Equal(91, bucket.Representative);
        Assert.Equal("Platinum", bucket.Band);
        Assert.Equal(1, bucket.SampleCount);
    }

    [Fact]
    public void BucketTrend_ManySnapshotsSameDay_CollapseToOneBucketWithCorrectMinMax()
    {
        var raw = new List<GovernanceTrendPoint>
        {
            Point("2026-07-13 06:00:00", 60, "Bronze"),
            Point("2026-07-13 12:00:00", 82, "Gold"),
            Point("2026-07-13 18:00:00", 74, "Silver"),
        };

        var buckets = TrendBucketer.BucketTrend(raw, "daily");

        var bucket = Assert.Single(buckets);
        Assert.Equal(60, bucket.Min);
        Assert.Equal(82, bucket.Max);
        // Representative/Band come from the LAST snapshot in the bucket (18:00), not the max.
        Assert.Equal(74, bucket.Representative);
        Assert.Equal("Silver", bucket.Band);
        Assert.Equal(3, bucket.SampleCount);
    }

    [Fact]
    public void BucketTrend_WeeklyView_SameIsoWeekDifferentDays_CollapseToOneBucket()
    {
        // Mon 2026-07-13 and Wed 2026-07-15 fall in the same ISO week.
        var raw = new List<GovernanceTrendPoint>
        {
            Point("2026-07-13 09:00:00", 60, "Bronze"),
            Point("2026-07-15 09:00:00", 70, "Silver"),
        };

        var buckets = TrendBucketer.BucketTrend(raw, "weekly");

        var bucket = Assert.Single(buckets);
        Assert.Equal(60, bucket.Min);
        Assert.Equal(70, bucket.Max);
        Assert.Equal(70, bucket.Representative);
        Assert.Equal(2, bucket.SampleCount);
    }

    [Fact]
    public void BucketTrend_WeeklyView_DifferentIsoWeeks_ProduceSeparateOldestFirstBuckets()
    {
        var raw = new List<GovernanceTrendPoint>
        {
            Point("2026-06-29 09:00:00", 55, "Bronze"),  // ISO week 27
            Point("2026-07-13 09:00:00", 80, "Gold"),    // ISO week 29
        };

        var buckets = TrendBucketer.BucketTrend(raw, "weekly");

        Assert.Equal(2, buckets.Count);
        Assert.True(buckets[0].BucketStart < buckets[1].BucketStart);
        Assert.Equal(55, buckets[0].Representative);
        Assert.Equal(80, buckets[1].Representative);
    }

    [Fact]
    public void BucketTrend_DailyView_CapsToMostRecent30Buckets()
    {
        var raw = new List<GovernanceTrendPoint>();
        var start = new DateTime(2026, 1, 1);
        for (int i = 0; i < 40; i++)
            raw.Add(Point(start.AddDays(i).ToString("yyyy-MM-dd HH:mm:ss"), 50 + i % 10, "Silver"));

        var buckets = TrendBucketer.BucketTrend(raw, "daily");

        Assert.Equal(30, buckets.Count);
        // Oldest 10 days dropped — the earliest returned bucket is day index 10.
        Assert.Equal(start.AddDays(10).Date, buckets[0].BucketStart);
        Assert.Equal(start.AddDays(39).Date, buckets[^1].BucketStart);
    }

    [Fact]
    public void BucketTrend_WeeklyView_CapsToMostRecent12Buckets()
    {
        var raw = new List<GovernanceTrendPoint>();
        var start = new DateTime(2026, 1, 1);
        for (int i = 0; i < 15; i++)
            raw.Add(Point(start.AddDays(i * 7).ToString("yyyy-MM-dd HH:mm:ss"), 50 + i, "Silver"));

        var buckets = TrendBucketer.BucketTrend(raw, "weekly");

        Assert.Equal(12, buckets.Count);
        Assert.True(buckets[0].BucketStart < buckets[^1].BucketStart);
    }

    [Fact]
    public void BucketTrend_DailyLabel_IsShortAndParseableAsARealDate()
    {
        var raw = new List<GovernanceTrendPoint> { Point("2026-07-13 09:00:00", 70, "Silver") };

        var buckets = TrendBucketer.BucketTrend(raw, "daily");

        var label = Assert.Single(buckets).Label;
        Assert.True(label.Length <= 10, $"expected a short label, got \"{label}\"");
        Assert.True(DateTime.TryParse(label, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _), $"expected \"{label}\" to parse as a date");
    }

    [Fact]
    public void BucketTrend_BucketStart_IsARealDateTime_NeverNeedsStringReparsing()
    {
        // Historical bug (fixed here): GetWeeklyAverages emitted a raw strftime label like
        // "2026-W28" as RecordedAt, which failed DateTime.TryParse in TrendLabel() and threw
        // (uncaught by TryParse) under the delta calc's bare DateTime.Parse — silently
        // swallowed by the surrounding try/catch. BucketStart is always a genuine DateTime,
        // computed once inside the bucketer, so date-span math downstream never touches a
        // string at all.
        var raw = new List<GovernanceTrendPoint>
        {
            Point("2026-06-29 10:00:00", 60, "Bronze"),
            Point("2026-07-13 10:00:00", 75, "Silver"),
        };

        var buckets = TrendBucketer.BucketTrend(raw, "weekly");

        Assert.True(buckets.Count >= 2);
        var spanDays = (buckets[^1].BucketStart.Date - buckets[0].BucketStart.Date).Days;
        Assert.True(spanDays > 0);
    }
}
