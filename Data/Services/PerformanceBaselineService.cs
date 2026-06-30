/* In the name of God, the Merciful, the Compassionate */
/*
 * PerformanceBaselineService — per-(server, wait_type, day-of-week, hour-of-day) z-score
 * anomaly detection built on top of HistoricalPerformanceService rollups.
 *
 * v1 scope:
 *   - User-triggered learning (no background scheduler).
 *   - Z-score detection only (≥2σ by default).
 *   - Maintenance-window suppression deferred to v2.
 *   - Reads only from HistoricalPerformanceService — no monitored-server SQL.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services;

// ── Public DTOs ──────────────────────────────────────────────────────────────

/// <summary>A single detected anomaly point.</summary>
public class AnomalyPoint
{
    public DateTime Timestamp { get; set; }
    public string WaitType { get; set; } = "";
    public double ObservedValue { get; set; }
    public double BaselineMean { get; set; }
    public double BaselineStdDev { get; set; }
    public double ZScore { get; set; }
}

/// <summary>Persisted baseline bucket for one (server, wait_type, day_of_week, hour_of_day).</summary>
public class PerformanceBaseline
{
    public string ServerName { get; set; } = "";
    public string WaitType { get; set; } = "";
    public int DayOfWeek { get; set; }   // 0=Sun..6=Sat
    public int HourOfDay { get; set; }   // 0..23
    public double MeanWaitMs { get; set; }
    public double StdDevWaitMs { get; set; }
    public int SampleCount { get; set; }
    public DateTime LearnedAtUtc { get; set; }
}

// ── Service ──────────────────────────────────────────────────────────────────

public sealed class PerformanceBaselineService : IDisposable
{
    private readonly ILogger<PerformanceBaselineService> _logger;
    private readonly HistoricalPerformanceService _historical;
    private readonly string _connectionString;
    private bool _disposed;

    // Minimum hourly samples per bucket before the bucket is trusted.
    private const int MinBucketSamples = 3;

    // ── Constructor ──────────────────────────────────────────────────────────

    public PerformanceBaselineService(
        ILogger<PerformanceBaselineService> logger,
        HistoricalPerformanceService historical,
        string? dbPath = null)
    {
        _logger = logger;
        _historical = historical;

        var resolvedPath = dbPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "governance-history.db");
        _connectionString = $"Data Source={resolvedPath};Mode=ReadWriteCreate;Cache=Shared";

        InitializeSchema();
    }

    // ── Schema ───────────────────────────────────────────────────────────────

    private void InitializeSchema()
    {
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS performance_baselines (
                    server_name     TEXT    NOT NULL,
                    wait_type       TEXT    NOT NULL,
                    day_of_week     INTEGER NOT NULL,
                    hour_of_day     INTEGER NOT NULL,
                    mean_wait_ms    REAL    NOT NULL,
                    stddev_wait_ms  REAL    NOT NULL,
                    sample_count    INTEGER NOT NULL,
                    learned_at_utc  TEXT    NOT NULL,
                    PRIMARY KEY (server_name, wait_type, day_of_week, hour_of_day)
                );
                CREATE INDEX IF NOT EXISTS idx_perf_baselines_server_type
                    ON performance_baselines (server_name, wait_type);
            ";
            cmd.ExecuteNonQuery();
            _logger.LogInformation("[BASELINE] performance_baselines schema initialised");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BASELINE] Schema initialisation failed");
        }
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Pulls the last <paramref name="lookbackDays"/> of hourly rollups for the given
    /// server + wait type, computes mean + std-dev per (day-of-week, hour-of-day) bucket,
    /// and persists the results to <c>performance_baselines</c>.
    /// Returns the number of buckets written.
    /// </summary>
    public async Task<int> LearnBaselineAsync(
        string serverName,
        string waitType,
        int lookbackDays = 30,
        CancellationToken ct = default)
    {
        try
        {
            var toUtc = DateTime.UtcNow;
            var fromUtc = toUtc.AddDays(-lookbackDays);

            // Pull from HistoricalPerformanceService — no new SQL connections.
            var rows = await Task.Run(
                () => _historical.GetHourlyWaitStats(serverName, fromUtc, toUtc, waitType), ct)
                .ConfigureAwait(false);

            if (rows.Count == 0)
            {
                _logger.LogInformation("[BASELINE] No hourly data for {Server}/{WaitType} — nothing to learn", serverName, waitType);
                return 0;
            }

            // Group by (day_of_week, hour_of_day) and compute mean + population std-dev.
            var buckets = rows
                .GroupBy(r => ((int)r.HourUtc.DayOfWeek, r.HourUtc.Hour))
                .Select(g =>
                {
                    var vals = g.Select(r => r.AvgWaitMs).ToList();
                    var mean = vals.Average();
                    var variance = vals.Count > 1
                        ? vals.Sum(v => Math.Pow(v - mean, 2)) / vals.Count
                        : 0.0;
                    return new PerformanceBaseline
                    {
                        ServerName = serverName,
                        WaitType = waitType,
                        DayOfWeek = g.Key.Item1,
                        HourOfDay = g.Key.Item2,
                        MeanWaitMs = mean,
                        StdDevWaitMs = Math.Sqrt(variance),
                        SampleCount = vals.Count,
                        LearnedAtUtc = DateTime.UtcNow,
                    };
                })
                .Where(b => b.SampleCount >= MinBucketSamples)
                .ToList();

            // Persist all buckets in a single transaction.
            int written = 0;
            using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString).ConfigureAwait(false);
            using var txn = conn.BeginTransaction();
            foreach (var b in buckets)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = txn;
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO performance_baselines
                        (server_name, wait_type, day_of_week, hour_of_day,
                         mean_wait_ms, stddev_wait_ms, sample_count, learned_at_utc)
                    VALUES
                        (@srv, @wt, @dow, @hod, @mean, @sd, @sc, @lat);
                ";
                cmd.Parameters.AddWithValue("@srv", b.ServerName);
                cmd.Parameters.AddWithValue("@wt", b.WaitType);
                cmd.Parameters.AddWithValue("@dow", b.DayOfWeek);
                cmd.Parameters.AddWithValue("@hod", b.HourOfDay);
                cmd.Parameters.AddWithValue("@mean", b.MeanWaitMs);
                cmd.Parameters.AddWithValue("@sd", b.StdDevWaitMs);
                cmd.Parameters.AddWithValue("@sc", b.SampleCount);
                cmd.Parameters.AddWithValue("@lat", b.LearnedAtUtc.ToString("o"));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                written++;
            }
            txn.Commit();

            _logger.LogInformation("[BASELINE] Learned {N} buckets for {Server}/{WaitType} (lookback={Days}d, input rows={Rows})",
                written, serverName, waitType, lookbackDays, rows.Count);
            return written;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BASELINE] LearnBaselineAsync failed for {Server}/{WaitType}", serverName, waitType);
            return 0;
        }
    }

    /// <summary>
    /// Learns baselines for every distinct wait type present in hourly rollups for the server.
    /// Returns a dict of wait_type → buckets written.
    /// </summary>
    public async Task<Dictionary<string, int>> LearnAllBaselinesAsync(
        string serverName,
        int lookbackDays = 30,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var waitTypes = await Task.Run(
            () => _historical.GetKnownWaitTypes(serverName), ct)
            .ConfigureAwait(false);

        foreach (var wt in waitTypes)
        {
            if (ct.IsCancellationRequested) break;
            results[wt] = await LearnBaselineAsync(serverName, wt, lookbackDays, ct).ConfigureAwait(false);
        }
        return results;
    }

    /// <summary>
    /// Returns all persisted baseline buckets for (server, wait_type), or an empty list
    /// if no baseline has been learned yet.
    /// </summary>
    public IReadOnlyList<PerformanceBaseline> GetBaseline(string serverName, string waitType)
    {
        var rows = new List<PerformanceBaseline>();
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT server_name, wait_type, day_of_week, hour_of_day,
                       mean_wait_ms, stddev_wait_ms, sample_count, learned_at_utc
                FROM performance_baselines
                WHERE server_name = @srv AND wait_type = @wt;
            ";
            cmd.Parameters.AddWithValue("@srv", serverName);
            cmd.Parameters.AddWithValue("@wt", waitType);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                rows.Add(new PerformanceBaseline
                {
                    ServerName = rdr.GetString(0),
                    WaitType = rdr.GetString(1),
                    DayOfWeek = rdr.GetInt32(2),
                    HourOfDay = rdr.GetInt32(3),
                    MeanWaitMs = rdr.GetDouble(4),
                    StdDevWaitMs = rdr.GetDouble(5),
                    SampleCount = rdr.GetInt32(6),
                    LearnedAtUtc = DateTime.Parse(rdr.GetString(7), null,
                                       System.Globalization.DateTimeStyles.RoundtripKind),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BASELINE] GetBaseline failed for {Server}/{WaitType}", serverName, waitType);
        }
        return rows;
    }

    /// <summary>
    /// Returns true when at least one bucket exists in the database for (server, wait_type).
    /// </summary>
    public bool HasBaseline(string serverName, string waitType)
    {
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*) FROM performance_baselines
                WHERE server_name = @srv AND wait_type = @wt;
            ";
            cmd.Parameters.AddWithValue("@srv", serverName);
            cmd.Parameters.AddWithValue("@wt", waitType);
            var count = (long)(cmd.ExecuteScalar() ?? 0L);
            return count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BASELINE] HasBaseline check failed for {Server}/{WaitType}", serverName, waitType);
            return false;
        }
    }

    /// <summary>
    /// Detects z-score anomalies in hourly rollup data for [fromUtc, toUtc].
    /// Each observed hourly avg is compared against the learned (day-of-week, hour-of-day) bucket.
    /// Returns points where |z| >= zScoreThreshold, sorted by descending |z-score|.
    /// </summary>
    public async Task<IReadOnlyList<AnomalyPoint>> DetectAnomaliesAsync(
        string serverName,
        string waitType,
        DateTime fromUtc,
        DateTime toUtc,
        double zScoreThreshold = 2.0,
        CancellationToken ct = default)
    {
        var anomalies = new List<AnomalyPoint>();
        try
        {
            var baseline = GetBaseline(serverName, waitType);
            if (baseline.Count == 0) return anomalies;

            // Index buckets for O(1) lookup: (dow, hod) → baseline
            var bucketMap = baseline.ToDictionary(
                b => (b.DayOfWeek, b.HourOfDay),
                b => b);

            var rows = await Task.Run(
                () => _historical.GetHourlyWaitStats(serverName, fromUtc, toUtc, waitType), ct)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                var key = ((int)row.HourUtc.DayOfWeek, row.HourUtc.Hour);
                if (!bucketMap.TryGetValue(key, out var b)) continue;

                // Use a floor of 1ms std-dev so we never divide by zero.
                var sd = Math.Max(b.StdDevWaitMs, 1.0);
                var z = (row.AvgWaitMs - b.MeanWaitMs) / sd;

                if (Math.Abs(z) >= zScoreThreshold)
                {
                    anomalies.Add(new AnomalyPoint
                    {
                        Timestamp = row.HourUtc,
                        WaitType = row.WaitType,
                        ObservedValue = row.AvgWaitMs,
                        BaselineMean = b.MeanWaitMs,
                        BaselineStdDev = b.StdDevWaitMs,
                        ZScore = z,
                    });
                }
            }

            anomalies.Sort((a, b2) => Math.Abs(b2.ZScore).CompareTo(Math.Abs(a.ZScore)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BASELINE] DetectAnomaliesAsync failed for {Server}/{WaitType}", serverName, waitType);
        }
        return anomalies;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
