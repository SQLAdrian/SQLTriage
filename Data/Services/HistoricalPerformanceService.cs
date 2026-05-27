/* In the name of God, the Merciful, the Compassionate */
/*
 * HistoricalPerformanceService — hourly + daily rollup of wait-stats raw history.
 *
 * Reads from wait_stats_history (WaitStatsHistoryService) and aggregates into
 * wait_stats_hourly and wait_stats_daily in the same governance-history.db.
 *
 * Retention (defaults): raw 14 d, hourly 90 d, daily 365 d.
 * Background loop ticks every hour; daily rollup runs once per UTC calendar day.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;

namespace SQLTriage.Data.Services;

// ── Public DTOs ─────────────────────────────────────────────────────────────

public class HourlyWaitStat
{
    public string ServerName { get; set; } = "";
    public DateTime HourUtc { get; set; }
    public string WaitType { get; set; } = "";
    public double AvgWaitMs { get; set; }
    public double MaxWaitMs { get; set; }
    public int SampleCount { get; set; }
}

public class DailyWaitStat
{
    public string ServerName { get; set; } = "";
    public DateTime DayUtc { get; set; }
    public string WaitType { get; set; } = "";
    public double AvgWaitMs { get; set; }
    public double MaxWaitMs { get; set; }
    public int SampleCount { get; set; }
}

// ── Service ──────────────────────────────────────────────────────────────────

public sealed class HistoricalPerformanceService : IDisposable
{
    private readonly ILogger<HistoricalPerformanceService> _logger;
    private readonly string _connectionString;

    private readonly int _rawRetentionDays;
    private readonly int _hourlyRetentionDays;
    private readonly int _dailyRetentionDays;

    private readonly System.Timers.Timer _rollupTimer;
    private string _lastDailyRollupDate = "";   // "YYYY-MM-DD" of last completed daily rollup

    // ── Constructor ──────────────────────────────────────────────────────────

    public HistoricalPerformanceService(
        ILogger<HistoricalPerformanceService> logger,
        int rawRetentionDays = 14,
        int hourlyRetentionDays = 90,
        int dailyRetentionDays = 365,
        string? dbPath = null)
    {
        _logger = logger;
        _rawRetentionDays = rawRetentionDays;
        _hourlyRetentionDays = hourlyRetentionDays;
        _dailyRetentionDays = dailyRetentionDays;

        var resolvedPath = dbPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "governance-history.db");
        _connectionString = $"Data Source={resolvedPath};Mode=ReadWriteCreate;Cache=Shared";

        InitializeSchema();

        // Tick every hour; first tick is staggered by 5 minutes to avoid startup contention.
        _rollupTimer = new System.Timers.Timer(TimeSpan.FromHours(1).TotalMilliseconds);
        _rollupTimer.Elapsed += (_, _) => _ = RunRollupAsync(CancellationToken.None);
        _rollupTimer.Start();
    }

    // ── Schema ───────────────────────────────────────────────────────────────

    private void InitializeSchema()
    {
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA foreign_keys=ON;
                PRAGMA auto_vacuum=INCREMENTAL;

                CREATE TABLE IF NOT EXISTS wait_stats_hourly (
                    server_name      TEXT    NOT NULL,
                    hour_utc         TEXT    NOT NULL,
                    wait_type        TEXT    NOT NULL,
                    avg_wait_time_ms REAL    NOT NULL,
                    max_wait_time_ms REAL    NOT NULL,
                    sample_count     INTEGER NOT NULL,
                    PRIMARY KEY (server_name, hour_utc, wait_type)
                );

                CREATE INDEX IF NOT EXISTS idx_wait_hourly_server_hour
                    ON wait_stats_hourly (server_name, hour_utc DESC);

                CREATE TABLE IF NOT EXISTS wait_stats_daily (
                    server_name      TEXT    NOT NULL,
                    day_utc          TEXT    NOT NULL,
                    wait_type        TEXT    NOT NULL,
                    avg_wait_time_ms REAL    NOT NULL,
                    max_wait_time_ms REAL    NOT NULL,
                    sample_count     INTEGER NOT NULL,
                    PRIMARY KEY (server_name, day_utc, wait_type)
                );

                CREATE INDEX IF NOT EXISTS idx_wait_daily_server_day
                    ON wait_stats_daily (server_name, day_utc DESC);
            ";
            cmd.ExecuteNonQuery();
            _logger.LogInformation("[ROLLUP] Historical performance schema initialised (hourly + daily tables)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ROLLUP] Failed to initialise historical performance schema");
        }
    }

    // ── Background rollup entry point ────────────────────────────────────────

    public async Task RunRollupAsync(CancellationToken ct)
    {
        try
        {
            var hourlyCount = await RollupHourlyAsync(ct).ConfigureAwait(false);

            // Daily rollup: run once per UTC calendar day (idempotent — INSERT OR REPLACE).
            var todayKey = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var dailyCount = 0;
            if (_lastDailyRollupDate != todayKey)
            {
                dailyCount = await RollupDailyAsync(ct).ConfigureAwait(false);
                _lastDailyRollupDate = todayKey;
            }

            await PurgeAsync(ct).ConfigureAwait(false);

            _logger.LogInformation("[ROLLUP] hourly +{H} rows, daily +{D} rows", hourlyCount, dailyCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ROLLUP] Rollup tick failed");
        }
    }

    // ── Hourly rollup ────────────────────────────────────────────────────────
    // Aggregates the previous complete hour from wait_stats_history into wait_stats_hourly.
    // Uses ISO-truncated hour string "YYYY-MM-DDTHH:00:00.0000000Z" as key.

    private async Task<int> RollupHourlyAsync(CancellationToken ct)
    {
        // Process only completed hours (current hour is excluded — still accumulating).
        var cutoffHour = DateTime.UtcNow.Date.Add(
            TimeSpan.FromHours(DateTime.UtcNow.Hour)); // truncated to this hour (exclusive)

        // Go back 2 hours to allow for slight clock skew / delayed snapshots.
        var fromHour = cutoffHour.AddHours(-2);

        int rowsWritten = 0;
        try
        {
            using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();

            // Aggregate raw rows into hourly buckets, then INSERT OR REPLACE.
            cmd.CommandText = @"
                INSERT OR REPLACE INTO wait_stats_hourly
                    (server_name, hour_utc, wait_type, avg_wait_time_ms, max_wait_time_ms, sample_count)
                SELECT
                    server_name,
                    strftime('%Y-%m-%dT%H:00:00.0000000Z', recorded_at) AS hour_utc,
                    wait_type,
                    AVG(delta_wait_ms)  AS avg_wait_time_ms,
                    MAX(delta_wait_ms)  AS max_wait_time_ms,
                    COUNT(*)            AS sample_count
                FROM wait_stats_history
                WHERE recorded_at >= @from
                  AND recorded_at <  @to
                GROUP BY server_name, hour_utc, wait_type;
            ";
            cmd.Parameters.AddWithValue("@from", fromHour.ToString("o"));
            cmd.Parameters.AddWithValue("@to", cutoffHour.ToString("o"));
            rowsWritten = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ROLLUP] Hourly rollup query failed");
        }
        return rowsWritten;
    }

    // ── Daily rollup ─────────────────────────────────────────────────────────
    // Aggregates yesterday's hourly rows into wait_stats_daily.

    private async Task<int> RollupDailyAsync(CancellationToken ct)
    {
        var yesterday = DateTime.UtcNow.Date.AddDays(-1).ToString("yyyy-MM-dd");
        int rowsWritten = 0;
        try
        {
            using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO wait_stats_daily
                    (server_name, day_utc, wait_type, avg_wait_time_ms, max_wait_time_ms, sample_count)
                SELECT
                    server_name,
                    substr(hour_utc, 1, 10) AS day_utc,
                    wait_type,
                    SUM(avg_wait_time_ms * sample_count) / SUM(sample_count) AS avg_wait_time_ms,
                    MAX(max_wait_time_ms)                                     AS max_wait_time_ms,
                    SUM(sample_count)                                         AS sample_count
                FROM wait_stats_hourly
                WHERE substr(hour_utc, 1, 10) = @day
                GROUP BY server_name, day_utc, wait_type;
            ";
            cmd.Parameters.AddWithValue("@day", yesterday);
            rowsWritten = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ROLLUP] Daily rollup query failed");
        }
        return rowsWritten;
    }

    // ── Retention purge ──────────────────────────────────────────────────────

    private async Task PurgeAsync(CancellationToken ct)
    {
        try
        {
            using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString).ConfigureAwait(false);

            // Each purge is independent: a failure in one (e.g. wait_stats_history is
            // owned by WaitStatsHistoryService and may not exist yet on this connection)
            // must NOT abort the others. Previously a single missing-table exception
            // bubbled to the method-level catch and silently skipped hourly+daily purge
            // entirely — retention then never ran. Fail loud per statement, continue.
            var rawDel = await PurgeOneAsync(conn, "wait_stats_history",
                "DELETE FROM wait_stats_history WHERE recorded_at < @cutoff;",
                DateTime.UtcNow.AddDays(-_rawRetentionDays).ToString("o"), ct).ConfigureAwait(false);

            var hrDel = await PurgeOneAsync(conn, "wait_stats_hourly",
                "DELETE FROM wait_stats_hourly WHERE hour_utc < @cutoff;",
                DateTime.UtcNow.AddDays(-_hourlyRetentionDays).ToString("o"), ct).ConfigureAwait(false);

            var dayDel = await PurgeOneAsync(conn, "wait_stats_daily",
                "DELETE FROM wait_stats_daily WHERE day_utc < @cutoff;",
                DateTime.UtcNow.AddDays(-_dailyRetentionDays).ToString("yyyy-MM-dd"), ct).ConfigureAwait(false);

            if (rawDel + hrDel + dayDel > 0)
                _logger.LogInformation("[ROLLUP] Purged raw={R}, hourly={H}, daily={D} expired rows", rawDel, hrDel, dayDel);

            using var vac = conn.CreateCommand();
            vac.CommandText = "PRAGMA incremental_vacuum;";
            await vac.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ROLLUP] Purge failed");
        }
    }

    /// <summary>
    /// Runs one retention DELETE in isolation. A failure (e.g. the target table is
    /// owned by another service and not yet created) is logged but does not abort
    /// sibling purges. Returns rows deleted, or 0 on failure.
    /// </summary>
    private async Task<int> PurgeOneAsync(
        SqliteConnection conn, string table, string sql, string cutoff, CancellationToken ct)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ROLLUP] Purge of {Table} skipped (continuing with other tables)", table);
            return 0;
        }
    }

    // ── Public query API ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns hourly rollup rows for a server within [fromUtc, toUtc].
    /// Caller filters by wait_type if needed (pass null for all types).
    /// </summary>
    public IReadOnlyList<HourlyWaitStat> GetHourlyWaitStats(
        string serverName, DateTime fromUtc, DateTime toUtc, string? waitType = null)
    {
        var rows = new List<HourlyWaitStat>();
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();

            var whereType = waitType != null ? "AND wait_type = @w " : "";
            cmd.CommandText = $@"
                SELECT server_name, hour_utc, wait_type, avg_wait_time_ms, max_wait_time_ms, sample_count
                FROM wait_stats_hourly
                WHERE server_name = @s
                  AND hour_utc >= @from
                  AND hour_utc <= @to
                  {whereType}
                ORDER BY hour_utc ASC;
            ";
            cmd.Parameters.AddWithValue("@s", serverName);
            cmd.Parameters.AddWithValue("@from", fromUtc.ToString("o"));
            cmd.Parameters.AddWithValue("@to", toUtc.ToString("o"));
            if (waitType != null) cmd.Parameters.AddWithValue("@w", waitType);

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                rows.Add(new HourlyWaitStat
                {
                    ServerName = rdr.GetString(0),
                    HourUtc = DateTime.Parse(rdr.GetString(1), null,
                                      System.Globalization.DateTimeStyles.RoundtripKind),
                    WaitType = rdr.GetString(2),
                    AvgWaitMs = rdr.GetDouble(3),
                    MaxWaitMs = rdr.GetDouble(4),
                    SampleCount = rdr.GetInt32(5),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ROLLUP] GetHourlyWaitStats failed for {Server}", serverName);
        }
        return rows;
    }

    /// <summary>
    /// Returns daily rollup rows for a server within [fromUtc, toUtc].
    /// </summary>
    public IReadOnlyList<DailyWaitStat> GetDailyWaitStats(
        string serverName, DateTime fromUtc, DateTime toUtc, string? waitType = null)
    {
        var rows = new List<DailyWaitStat>();
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();

            var whereType = waitType != null ? "AND wait_type = @w " : "";
            cmd.CommandText = $@"
                SELECT server_name, day_utc, wait_type, avg_wait_time_ms, max_wait_time_ms, sample_count
                FROM wait_stats_daily
                WHERE server_name = @s
                  AND day_utc >= @from
                  AND day_utc <= @to
                  {whereType}
                ORDER BY day_utc ASC;
            ";
            cmd.Parameters.AddWithValue("@s", serverName);
            cmd.Parameters.AddWithValue("@from", fromUtc.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@to", toUtc.ToString("yyyy-MM-dd"));
            if (waitType != null) cmd.Parameters.AddWithValue("@w", waitType);

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                rows.Add(new DailyWaitStat
                {
                    ServerName = rdr.GetString(0),
                    DayUtc = DateTime.Parse(rdr.GetString(1) + "T00:00:00Z", null,
                                      System.Globalization.DateTimeStyles.RoundtripKind),
                    WaitType = rdr.GetString(2),
                    AvgWaitMs = rdr.GetDouble(3),
                    MaxWaitMs = rdr.GetDouble(4),
                    SampleCount = rdr.GetInt32(5),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ROLLUP] GetDailyWaitStats failed for {Server}", serverName);
        }
        return rows;
    }

    /// <summary>
    /// Returns distinct wait types present in hourly rollup for a server.
    /// Used by the Trends UI to populate the filter dropdown.
    /// </summary>
    public IReadOnlyList<string> GetKnownWaitTypes(string serverName)
    {
        var types = new List<string>();
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT DISTINCT wait_type FROM wait_stats_hourly
                WHERE server_name = @s
                ORDER BY wait_type;
            ";
            cmd.Parameters.AddWithValue("@s", serverName);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) types.Add(rdr.GetString(0));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ROLLUP] GetKnownWaitTypes failed for {Server}", serverName);
        }
        return types;
    }

    public void Dispose() => _rollupTimer?.Dispose();
}
