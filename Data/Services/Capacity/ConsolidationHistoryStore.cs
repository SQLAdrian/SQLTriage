/* In the name of God, the Merciful, the Compassionate */
/*
 * ConsolidationHistoryStore — SQLCipher-encrypted persistence for the consolidation
 * telemetry sampler. Same security umbrella as the rest of the app: opened via
 * SqliteCipherHelper (DPAPI-wrapped key, identical to governance-history.db et al).
 * Holds per-server hourly samples + the collector's own control config.
 * Metadata only — no query text, no plan XML ever lands here.
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;

namespace SQLTriage.Data.Services.Capacity;

/// <summary>One persisted telemetry sample for one server at one point in time.</summary>
public sealed class ConsolidationSample
{
    public string ServerName { get; set; } = "";
    public DateTime RecordedUtc { get; set; }
    public int Cores { get; set; }
    public double SnapCpuPct { get; set; }
    public double QsCpuCoresMean { get; set; }
    public double QsCpuCoresP95 { get; set; }
    public double QsWindowHours { get; set; }
    public double WorkerCpuCores { get; set; }
    public double LogicalReadsPerSec { get; set; }
    public double PhysicalReadsPerSec { get; set; }
    public long DailyIops { get; set; }
}

/// <summary>Rollup summary for one server's accumulated samples.</summary>
public sealed record ServerSampleSummary(string ServerName, int SampleCount, DateTime? FirstUtc, DateTime? LastUtc);

public sealed class ConsolidationHistoryStore
{
    private readonly ILogger<ConsolidationHistoryStore> _logger;
    private readonly string _connectionString;

    public ConsolidationHistoryStore(ILogger<ConsolidationHistoryStore> logger)
    {
        _logger = logger;
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "consolidation-history.db");
        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";
        InitializeSchema();
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
                PRAGMA auto_vacuum=INCREMENTAL;

                CREATE TABLE IF NOT EXISTS consolidation_samples (
                    server_name           TEXT    NOT NULL,
                    recorded_at           TEXT    NOT NULL,
                    cores                 INTEGER NOT NULL,
                    snap_cpu_pct          REAL    NOT NULL,
                    qs_cpu_cores_mean     REAL    NOT NULL,
                    qs_cpu_cores_p95      REAL    NOT NULL,
                    qs_window_hours       REAL    NOT NULL,
                    worker_cores          REAL    NOT NULL,
                    logical_reads_per_sec REAL    NOT NULL,
                    physical_reads_per_sec REAL   NOT NULL,
                    daily_iops            INTEGER NOT NULL,
                    PRIMARY KEY (server_name, recorded_at)
                );
                CREATE INDEX IF NOT EXISTS idx_cons_samples ON consolidation_samples (server_name, recorded_at);

                CREATE TABLE IF NOT EXISTS consolidation_collector_config (k TEXT PRIMARY KEY, v TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS consolidation_server_collection (server_name TEXT PRIMARY KEY, enabled INTEGER NOT NULL);
            ";
            cmd.ExecuteNonQuery();
            _logger.LogInformation("[Consolidation] history store schema initialised");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consolidation] failed to initialise history store schema");
        }
    }

    public async Task SaveSamplesAsync(IEnumerable<ConsolidationSample> samples, CancellationToken ct = default)
    {
        try
        {
            using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString);
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR REPLACE INTO consolidation_samples
                  (server_name, recorded_at, cores, snap_cpu_pct, qs_cpu_cores_mean, qs_cpu_cores_p95,
                   qs_window_hours, worker_cores, logical_reads_per_sec, physical_reads_per_sec, daily_iops)
                VALUES (@s,@t,@c,@snap,@qm,@qp,@qw,@wc,@lr,@pr,@io);";
            foreach (var s in samples)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@s", s.ServerName);
                cmd.Parameters.AddWithValue("@t", s.RecordedUtc.ToString("o"));
                cmd.Parameters.AddWithValue("@c", s.Cores);
                cmd.Parameters.AddWithValue("@snap", s.SnapCpuPct);
                cmd.Parameters.AddWithValue("@qm", s.QsCpuCoresMean);
                cmd.Parameters.AddWithValue("@qp", s.QsCpuCoresP95);
                cmd.Parameters.AddWithValue("@qw", s.QsWindowHours);
                cmd.Parameters.AddWithValue("@wc", s.WorkerCpuCores);
                cmd.Parameters.AddWithValue("@lr", s.LogicalReadsPerSec);
                cmd.Parameters.AddWithValue("@pr", s.PhysicalReadsPerSec);
                cmd.Parameters.AddWithValue("@io", s.DailyIops);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Consolidation] failed to persist sample batch");
        }
    }

    public List<ServerSampleSummary> GetServerSummaries()
    {
        var list = new List<ServerSampleSummary>();
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT server_name, COUNT(*) AS n, MIN(recorded_at) AS first_at, MAX(recorded_at) AS last_at
                FROM consolidation_samples GROUP BY server_name ORDER BY server_name;";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                DateTime? P(int i) => rd.IsDBNull(i) ? null : DateTime.Parse(rd.GetString(i), null, System.Globalization.DateTimeStyles.RoundtripKind);
                list.Add(new ServerSampleSummary(rd.GetString(0), rd.GetInt32(1), P(2), P(3)));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Consolidation] GetServerSummaries failed"); }
        return list;
    }

    public int PurgeOlderThan(int retentionDays)
    {
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM consolidation_samples WHERE recorded_at < @cut;";
            cmd.Parameters.AddWithValue("@cut", DateTime.UtcNow.AddDays(-retentionDays).ToString("o"));
            return cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Consolidation] purge failed"); return 0; }
    }

    // ── Collector control config (persisted in the encrypted store) ──
    public string? GetConfig(string key)
    {
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT v FROM consolidation_collector_config WHERE k=@k;";
            cmd.Parameters.AddWithValue("@k", key);
            return cmd.ExecuteScalar() as string;
        }
        catch { return null; }
    }

    public void SetConfig(string key, string value)
    {
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO consolidation_collector_config (k,v) VALUES (@k,@v);";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Consolidation] SetConfig failed"); }
    }

    /// <summary>Explicit per-server disables. Absent server ⇒ enabled (collect) when global is on.</summary>
    public Dictionary<string, bool> GetServerCollectionFlags()
    {
        var d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT server_name, enabled FROM consolidation_server_collection;";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) d[rd.GetString(0)] = rd.GetInt32(1) == 1;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Consolidation] GetServerCollectionFlags failed"); }
        return d;
    }

    public void SetServerCollection(string serverName, bool enabled)
    {
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO consolidation_server_collection (server_name, enabled) VALUES (@s,@e);";
            cmd.Parameters.AddWithValue("@s", serverName);
            cmd.Parameters.AddWithValue("@e", enabled ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Consolidation] SetServerCollection failed"); }
    }
}
