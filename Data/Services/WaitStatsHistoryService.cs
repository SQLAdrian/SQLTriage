/* In the name of God, the Merciful, the Compassionate */
/*
 * WaitStatsHistoryService — SQLite persistence for the wait-stats trend rows.
 * Models on GovernanceHistoryService. Shares the governance-history.db so the
 * existing nightly retention sweep cleans it up for free.
 *
 * P1 of WAIT_STATS_DESIGN.md. Pure backend. No UI binding yet.
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

public class WaitStatsHistoryService : IDisposable
{
    private readonly ILogger<WaitStatsHistoryService> _logger;
    private readonly string _connectionString;
    private readonly int _retentionDays;
    private readonly System.Timers.Timer _purgeTimer;
    private readonly object _writeLock = new();

    public WaitStatsHistoryService(ILogger<WaitStatsHistoryService> logger, int retentionDays = 14)
    {
        _logger = logger;
        _retentionDays = retentionDays;
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "governance-history.db");
        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";
        InitializeSchema();

        _purgeTimer = new System.Timers.Timer(TimeSpan.FromHours(24).TotalMilliseconds);
        _purgeTimer.Elapsed += (_, _) => PurgeOld();
        _purgeTimer.Start();
    }

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
                -- DE-C3: MUST be set before any tables are created (no-op on existing DBs with auto_vacuum=NONE).
                PRAGMA auto_vacuum=INCREMENTAL;

                CREATE TABLE IF NOT EXISTS wait_stats_history (
                    server_name      TEXT    NOT NULL,
                    recorded_at      TEXT    NOT NULL,
                    wait_type        TEXT    NOT NULL,
                    category         TEXT    NOT NULL,
                    delta_wait_ms    INTEGER NOT NULL,
                    delta_tasks      INTEGER NOT NULL,
                    delta_signal_ms  INTEGER NOT NULL,
                    snapshot_id      TEXT    NOT NULL,
                    PRIMARY KEY (server_name, snapshot_id, wait_type)
                );

                CREATE INDEX IF NOT EXISTS idx_wait_history_lookup
                    ON wait_stats_history (server_name, wait_type, recorded_at);
                CREATE INDEX IF NOT EXISTS idx_wait_history_category
                    ON wait_stats_history (server_name, category, recorded_at);
            ";
            cmd.ExecuteNonQuery();
            _logger.LogInformation("WaitStats history schema initialised");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialise wait_stats_history schema");
        }
    }

    public async Task RecordSnapshotAsync(string serverName, WaitStatsSnapshot snap, CancellationToken ct = default)
    {
        if (snap?.Deltas == null || snap.Deltas.Count == 0) return;
        try
        {
            using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString);
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR REPLACE INTO wait_stats_history
                    (server_name, recorded_at, wait_type, category,
                     delta_wait_ms, delta_tasks, delta_signal_ms, snapshot_id)
                VALUES
                    (@s, @t, @w, @c, @dw, @dt, @ds, @sid);
            ";
            var recordedAt = snap.RecordedAtUtc.ToString("o");
            foreach (var d in snap.Deltas)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@s", serverName);
                cmd.Parameters.AddWithValue("@t", recordedAt);
                cmd.Parameters.AddWithValue("@w", d.WaitType);
                cmd.Parameters.AddWithValue("@c", d.Category.ToString());
                cmd.Parameters.AddWithValue("@dw", d.DeltaWaitMs);
                cmd.Parameters.AddWithValue("@dt", d.DeltaTasks);
                cmd.Parameters.AddWithValue("@ds", d.DeltaSignalMs);
                cmd.Parameters.AddWithValue("@sid", snap.SnapshotId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist wait-stats snapshot for {Server}", serverName);
        }
    }

    public List<WaitTrendPoint> GetTrend(string serverName, string waitType, int hours)
    {
        var rows = new List<WaitTrendPoint>();
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT recorded_at, delta_wait_ms, delta_tasks, delta_signal_ms
                FROM wait_stats_history
                WHERE server_name = @s AND wait_type = @w
                  AND recorded_at >= @cutoff
                ORDER BY recorded_at ASC;
            ";
            cmd.Parameters.AddWithValue("@s", serverName);
            cmd.Parameters.AddWithValue("@w", waitType);
            cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddHours(-hours).ToString("o"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new WaitTrendPoint
                {
                    RecordedAtUtc = DateTime.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    DeltaWaitMs = reader.GetInt64(1),
                    DeltaTasks = reader.GetInt64(2),
                    DeltaSignalMs = reader.GetInt64(3),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read wait-stats trend for {Server}/{Wait}", serverName, waitType);
        }
        return rows;
    }

    private void PurgeOld()
    {
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM wait_stats_history WHERE recorded_at < @cutoff;";
            cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-_retentionDays).ToString("o"));
            var deleted = cmd.ExecuteNonQuery();
            if (deleted > 0) _logger.LogInformation("Purged {Count} wait-stats rows older than {Days}d", deleted, _retentionDays);
            // DE-C3: reclaim space after purge.
            using var vac = conn.CreateCommand();
            vac.CommandText = "PRAGMA incremental_vacuum;";
            vac.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wait-stats purge failed");
        }
    }

    public void Dispose() => _purgeTimer?.Dispose();
}

public class WaitStatsSnapshot
{
    public string SnapshotId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
    public List<WaitDelta> Deltas { get; set; } = new();
}

public class WaitDelta
{
    public string WaitType { get; set; } = "";
    public WaitCategory Category { get; set; }
    public long DeltaWaitMs { get; set; }
    public long DeltaTasks { get; set; }
    public long DeltaSignalMs { get; set; }
}

public class WaitTrendPoint
{
    public DateTime RecordedAtUtc { get; set; }
    public long DeltaWaitMs { get; set; }
    public long DeltaTasks { get; set; }
    public long DeltaSignalMs { get; set; }
}
