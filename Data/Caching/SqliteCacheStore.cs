/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;

namespace SQLTriage.Data.Caching
{
    /// <summary>
    /// Manages the local liveQueries cache database for dashboard query results.
    /// All writes are serialized through a SemaphoreSlim; reads are lock-free (WAL mode).
    /// The database file is created automatically in the application's base directory.
    /// When DataProtectionService is available, all cached data values are encrypted
    /// at rest using ephemeral AES-256-GCM session keys.
    /// </summary>
    public sealed class liveQueriesCacheStore : IDisposable
    {
        // Whitelist of valid cache table names — prevents SQL injection if table name
        // construction is ever refactored to accept external input.
        private static readonly HashSet<string> AllowedTables = new(StringComparer.Ordinal)
        {
            "cache_timeseries", "cache_stat", "cache_bargauge",
            "cache_datatable", "cache_checkstatus", "cache_metadata",
            "alert_baseline_samples", "alert_baseline_stats",
            // Retained history. Listed so this class's own prune SQL passes ValidateTableName.
            // It carries no fetched_at column, so the dynamic eviction sweep never discovers it and
            // the two hardcoded delete lists never name it — see MetricRetentionOptions.
            "metric_history"
        };

        private static string ValidateTableName(string table)
        {
            if (!AllowedTables.Contains(table))
                throw new ArgumentException($"Invalid cache table name: '{table}'");
            return table;
        }

        private readonly string _connectionString;
        private readonly DataProtectionService? _dataProtection;
        private readonly UserSettingsService? _userSettings;

        // Striped write locks for concurrent panel writes (B8).
        // A fixed array avoids unbounded memory/handle growth from per-key semaphores.
        // Hash collisions are acceptable — they only serialize two panels briefly.
        // Global ops (eviction, vacuum) use _globalWriteLock exclusively.
        private const int WriteLockStripes = 64;
        private readonly SemaphoreSlim[] _writeLocks;
        private readonly SemaphoreSlim _globalWriteLock = new(1, 1);
        private bool _disposed;

        // ── Batch writer ───────────────────────────────────────────────
        // Single background task batches SQLite writes into one transaction
        // to eliminate the single-writer bottleneck (D24).
        private readonly Channel<BatchOperation> _batchChannel;
        private readonly CancellationTokenSource _batchCts = new();
        private readonly Task _batchWriterTask;

        private SemaphoreSlim GetLockFor(string queryId, string instanceKey)
        {
            // FNV-like hash collapsed to the stripe count. Using string.GetHashCode()
            // is randomized per process, which is fine — we only need consistent
            // bucketing within a single run.
            var hash = ((uint)queryId.GetHashCode() * 397u) ^ (uint)instanceKey.GetHashCode();
            return _writeLocks[hash % WriteLockStripes];
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public liveQueriesCacheStore(DataProtectionService? dataProtection = null, UserSettingsService? userSettings = null)
        {
            _dataProtection = dataProtection;
            _userSettings = userSettings;

            _writeLocks = new SemaphoreSlim[WriteLockStripes];
            for (int i = 0; i < WriteLockStripes; i++)
                _writeLocks[i] = new SemaphoreSlim(1, 1);

            var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SQLTriage-cache.db");
            _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;";
            TryInitializeSchema();

            _batchChannel = Channel.CreateBounded<BatchOperation>(new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            _batchWriterTask = Task.Run(BatchWriterLoopAsync);
        }

        /// <summary>
        /// Encrypts a value for cache storage. Returns the original value if
        /// DataProtectionService is not available.
        /// </summary>
        private string ProtectValue(string value)
            => _dataProtection != null ? _dataProtection.Protect(value) : value;

        /// <summary>
        /// Decrypts a cached value. Falls back to returning the raw value if
        /// decryption fails (e.g., session key rotated after restart).
        /// </summary>
        private string? UnprotectValue(string value)
        {
            if (_dataProtection == null) return value;
            var result = _dataProtection.Unprotect(value);
            // Null/empty means decrypt failed (wrong key — stale cache from reinstall or key rotation).
            // Return null so callers treat it as a cache miss and do a fresh fetch.
            return string.IsNullOrEmpty(result) ? null : result;
        }

        // ──────────────────────────── Schema ────────────────────────────

        /// <summary>
        /// The CONSTRUCTOR's schema init, and the only difference from <see cref="EnsureSchema"/>
        /// is that this one cannot throw.
        ///
        /// <para>⚠ WHY IT EXISTS (Adrian's ruling, 2026-08-08, point 5). This type is registered
        /// <c>AddSingleton</c> and is a REQUIRED constructor dependency of CachingQueryExecutor,
        /// CacheEvictionService, liveQueriesMaintenanceService, CacheMetricsService, ForecastService,
        /// AlertBaselineService, AlertEvaluationService and BenchmarkService — so it is built while
        /// the container is being resolved at startup. Every other store's schema init is already
        /// wrapped this way (AlertHistoryService, BlockingHistoryService, ConsolidationHistoryStore,
        /// UptimeTrackerService, AcceptedFindingsService, ServerConfigBaselineService,
        /// ChangeItemService, SeatRegister and the rest — read, one at a time, on 2026-08-08). This
        /// chain was the one that was not, and after the 2026-08-06 re-init work SqliteCipherHelper
        /// THROWS on a store file it cannot clear rather than reopening on top of it. A cache file
        /// held by anything — a backup agent, an antivirus scan, a second copy of the app — would
        /// therefore take down the whole container, which under the Windows SCM is a restart loop.</para>
        ///
        /// <para>Degrading here means the cache misses and the dashboards fetch live; it does not
        /// mean the failure is hidden. The line below is the only notice anyone gets, so it says
        /// what stopped working, and it is at Error.</para>
        ///
        /// <para>Deliberately NOT applied to <see cref="EnsureSchema"/>: that one is called by
        /// someone who has asked for a repair and is entitled to be told it did not happen.</para>
        /// </summary>
        private void TryInitializeSchema()
        {
            try
            {
                InitializeSchema();
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex,
                    "[CACHE] Could not initialise the dashboard cache store at {ConnStr}. The cache is NOT available for this run: every dashboard panel will miss and fetch live, and cache writes will fail one by one. The application starts anyway — this store is not worth refusing to start over — but the underlying problem is real and is in the message above",
                    _connectionString);
            }
        }

        /// <summary>
        /// Re-creates cache tables if the database file was deleted externally.
        /// Safe to call multiple times (uses CREATE TABLE IF NOT EXISTS).
        /// Throws if the store cannot be opened — see <see cref="TryInitializeSchema"/> for why
        /// the constructor's call does not.
        /// </summary>
        public void EnsureSchema() => InitializeSchema();

        /// <summary>
        /// Creates and returns an open-able SqliteConnection to the same database file.
        /// Used by services (e.g. AlertBaselineService) that need direct table access
        /// without going through the cache abstraction layer.
        /// Caller is responsible for opening and disposing the connection.
        /// </summary>
        /// Returns an already-open, SQLCipher-keyed connection for callers outside this class.
        public SqliteConnection CreateExternalConnection()
            => SqliteCipherHelper.OpenEncrypted(_connectionString);

        private void InitializeSchema()
        {
            using var conn = CreateConnection(); // already open + keyed

            // DE-C4: page_size MUST be set before any tables exist (no-op on existing DBs).
            // 8192-byte pages reduce I/O amplification for the json_data TEXT blob in cache_datatable.
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA page_size=8192;";
                pragma.ExecuteNonQuery();
            }

            // Enable WAL mode for concurrent reads during writes
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }

            // Enable incremental vacuum to reclaim free pages without exclusive lock
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA auto_vacuum = INCREMENTAL;";
                pragma.ExecuteNonQuery();
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS cache_timeseries (
                    query_id     TEXT NOT NULL,
                    instance_key TEXT NOT NULL,
                    time_value   TEXT NOT NULL,
                    series       TEXT NOT NULL,
                    value        REAL NOT NULL,
                    fetched_at   TEXT NOT NULL,
                    PRIMARY KEY (query_id, instance_key, time_value, series)
                );

                -- idx_ts_query_time was a prefix of the composite PK (query_id, instance_key, time_value, series)
                -- and therefore redundant. Dropped for storage and write-amplification savings.
                DROP INDEX IF EXISTS idx_ts_query_time;

                CREATE INDEX IF NOT EXISTS idx_ts_fetched_at
                    ON cache_timeseries(fetched_at);

                CREATE TABLE IF NOT EXISTS cache_stat (
                    query_id     TEXT NOT NULL,
                    instance_key TEXT NOT NULL,
                    label        TEXT NOT NULL DEFAULT '',
                    value        REAL NOT NULL,
                    unit         TEXT NOT NULL DEFAULT '',
                    color        TEXT NOT NULL DEFAULT '',
                    fetched_at   TEXT NOT NULL,
                    PRIMARY KEY (query_id, instance_key)
                );

                CREATE INDEX IF NOT EXISTS idx_stat_fetched_at
                    ON cache_stat(fetched_at);

                CREATE TABLE IF NOT EXISTS cache_bargauge (
                    query_id     TEXT NOT NULL,
                    instance_key TEXT NOT NULL,
                    label        TEXT NOT NULL,
                    value        REAL NOT NULL,
                    unit         TEXT NOT NULL DEFAULT '',
                    instance     TEXT NOT NULL DEFAULT '',
                    color        TEXT NOT NULL DEFAULT '',
                    fetched_at   TEXT NOT NULL,
                    PRIMARY KEY (query_id, instance_key, label, instance)
                );

                CREATE INDEX IF NOT EXISTS idx_bargauge_fetched_at
                    ON cache_bargauge(fetched_at);

                CREATE TABLE IF NOT EXISTS cache_datatable (
                    query_id     TEXT NOT NULL,
                    instance_key TEXT NOT NULL,
                    json_data    TEXT NOT NULL,
                    fetched_at   TEXT NOT NULL,
                    PRIMARY KEY (query_id, instance_key)
                );

                CREATE INDEX IF NOT EXISTS idx_datatable_fetched_at
                    ON cache_datatable(fetched_at);

                CREATE TABLE IF NOT EXISTS cache_checkstatus (
                    query_id     TEXT NOT NULL,
                    instance_key TEXT NOT NULL,
                    status       TEXT NOT NULL,
                    count        INTEGER NOT NULL,
                    fetched_at   TEXT NOT NULL,
                    PRIMARY KEY (query_id, instance_key, status)
                );

                CREATE INDEX IF NOT EXISTS idx_checkstatus_fetched_at
                    ON cache_checkstatus(fetched_at);

                CREATE TABLE IF NOT EXISTS cache_metadata (
                    query_id     TEXT NOT NULL,
                    instance_key TEXT NOT NULL,
                    last_fetch   TEXT NOT NULL,
                    PRIMARY KEY (query_id, instance_key)
                );
                CREATE INDEX IF NOT EXISTS idx_metadata_last_fetch
                    ON cache_metadata(last_fetch);

                CREATE TABLE IF NOT EXISTS alert_baseline_samples (
                    alert_id     TEXT NOT NULL,
                    server_name  TEXT NOT NULL,
                    sampled_at   TEXT NOT NULL,
                    value        REAL NOT NULL,
                    hour_of_day  INTEGER NOT NULL,
                    day_of_week  INTEGER NOT NULL,
                    fetched_at   TEXT NOT NULL,
                    PRIMARY KEY (alert_id, server_name, sampled_at)
                );
                CREATE INDEX IF NOT EXISTS idx_baseline_alert_server
                    ON alert_baseline_samples(alert_id, server_name, sampled_at);
                CREATE INDEX IF NOT EXISTS idx_baseline_fetched_at
                    ON alert_baseline_samples(fetched_at);

                CREATE TABLE IF NOT EXISTS alert_baseline_stats (
                    alert_id             TEXT NOT NULL,
                    server_name          TEXT NOT NULL,
                    sample_count         INTEGER NOT NULL,
                    p25                  REAL NOT NULL,
                    p50                  REAL NOT NULL,
                    p75                  REAL NOT NULL,
                    p95                  REAL NOT NULL,
                    iqr                  REAL NOT NULL,
                    threshold_warn       REAL NOT NULL,
                    threshold_crit       REAL NOT NULL,
                    last_computed        TEXT NOT NULL,
                    baseline_locked      INTEGER NOT NULL DEFAULT 0,
                    trend_slope          REAL NOT NULL DEFAULT 0,
                    trend_r_squared      REAL NOT NULL DEFAULT 0,
                    trend_sample_count   INTEGER NOT NULL DEFAULT 0,
                    is_trend_warning     INTEGER NOT NULL DEFAULT 0,
                    is_trend_critical    INTEGER NOT NULL DEFAULT 0,
                    p05                  REAL NOT NULL DEFAULT 0,
                    threshold_warn_lower REAL NOT NULL DEFAULT 0,
                    threshold_crit_lower REAL NOT NULL DEFAULT 0,
                    PRIMARY KEY (alert_id, server_name)
                );

                -- Retained metric history (DECISIONS 2026-08-26 18:23 ruling 4). NOT a cache: this is
                -- the durable, downsampled record that makes the AG queue trend and the 7-day Baseline
                -- real. See MetricRetentionOptions for the full design and for why this table has
                -- deliberately NO fetched_at column. Adding one puts it under the 24-hour eviction
                -- sweep AND throws inside EvictOlderThanCore unless it is also in AllowedTables, which
                -- CacheEvictionService swallows as a generic failure that kills all eviction.
                CREATE TABLE IF NOT EXISTS metric_history (
                    query_id     TEXT NOT NULL,
                    instance_key TEXT NOT NULL,
                    bucket_utc   TEXT NOT NULL,
                    series       TEXT NOT NULL,
                    value        REAL NOT NULL,
                    PRIMARY KEY (query_id, instance_key, bucket_utc, series)
                );
                CREATE INDEX IF NOT EXISTS idx_metric_history_bucket
                    ON metric_history(bucket_utc);
            ";
            cmd.ExecuteNonQuery();

            MigrateAlertBaselineStats(conn);
            MigrateAlertRawSamples(conn);
            MigrateAlertSchemaMarkers(conn);
        }

        /// <summary>
        /// Creates <c>alert_schema_markers</c>, the record of which one-shot data repairs this
        /// database has already had. A repair that must run exactly once - purging measurements
        /// of a quantity the software no longer computes, say - claims its marker first and does
        /// nothing if the claim fails.
        /// </summary>
        internal static void MigrateAlertSchemaMarkers(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS alert_schema_markers (
                    marker      TEXT NOT NULL PRIMARY KEY,
                    applied_at  TEXT NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Claims a one-shot marker: true the first time it is asked for on this database, false
        /// every time after. The INSERT is the claim, so two callers racing cannot both win - the
        /// primary key decides. A failure to reach the store returns false, which skips the
        /// repair rather than repeating it.
        /// </summary>
        public async Task<bool> TryClaimSchemaMarkerAsync(string marker)
        {
            try
            {
                using var conn = CreateExternalConnection();
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT OR IGNORE INTO alert_schema_markers (marker, applied_at)
                                    VALUES (@m, @at)";
                cmd.Parameters.AddWithValue("@m", marker);
                cmd.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                return await cmd.ExecuteNonQueryAsync() == 1;
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "[CACHE] Could not claim schema marker {Marker}; the one-shot repair it guards is skipped", marker);
                return false;
            }
        }

        /// <summary>
        /// Creates <c>alert_raw_samples</c> on a database that predates it.
        ///
        /// <para>An alert whose query returns a cumulative counter (a PERF_COUNTER_BULK_COUNT
        /// "/sec" row, or a SUM over sys.dm_os_wait_stats) has to be differenced across two
        /// samples before its number means anything. This table is the previous sample: one row
        /// per alert/server, holding the raw counter and the UTC instant it was read. It is
        /// deliberately NOT the baseline sample table - baseline samples are the finished
        /// measurement (a rate), these are the unprocessed reading behind it.</para>
        ///
        /// <para>Separate and internal for the same reason as
        /// <see cref="MigrateAlertBaselineStats"/>: the upgrade of a database created with the
        /// OLD schema can then be exercised directly rather than asserted about.</para>
        ///
        /// <para>A row lost or missing costs one evaluation cycle, never a wrong number: with no
        /// previous sample the evaluator reports "not yet measured" and waits.</para>
        /// </summary>
        internal static void MigrateAlertRawSamples(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS alert_raw_samples (
                    alert_id     TEXT NOT NULL,
                    server_name  TEXT NOT NULL,
                    raw_value    REAL NOT NULL,
                    sampled_at   TEXT NOT NULL,
                    PRIMARY KEY (alert_id, server_name)
                );
            ";
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// The previous raw reading of a cumulative counter for this alert/server, or null when
        /// none has been stored yet (a fresh install, a purged row, or the first cycle after this
        /// alert was marked cumulative). Null means "we cannot compute a rate this cycle" - it
        /// must never be read as zero.
        /// </summary>
        public async Task<(double Raw, DateTime SampledAtUtc)?> GetLastRawSampleAsync(
            string alertId, string serverName)
        {
            try
            {
                using var conn = CreateExternalConnection();
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT raw_value, sampled_at FROM alert_raw_samples
                                    WHERE alert_id = @aid AND server_name = @srv";
                cmd.Parameters.AddWithValue("@aid", alertId);
                cmd.Parameters.AddWithValue("@srv", serverName);
                using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return null;
                var raw = reader.GetDouble(0);
                if (!DateTime.TryParse(reader.GetString(1), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var at)) return null;
                return (raw, at.ToUniversalTime());
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "[CACHE] Could not read the previous raw sample for {AlertId}/{Server}; this cycle reports not-yet-measured", alertId, serverName);
                return null;
            }
        }

        /// <summary>
        /// Stores this cycle's raw reading as the baseline for the next one. Raw value and
        /// timestamp go in as a single statement so a reader can never pair one sample's number
        /// with another sample's clock.
        ///
        /// <para>Returns false when the store could not be written, and the caller is expected to
        /// say so out loud. A single failure costs one cycle; a store that stays unwritable means
        /// no previous sample ever lands, so every cumulative alert reports "not yet measured"
        /// forever - no history row, no active alert, and a UI that reads as healthy. That
        /// failure mode is honest but invisible, which is why this reports rather than swallowing.</para>
        /// </summary>
        public async Task<bool> SaveLastRawSampleAsync(
            string alertId, string serverName, double raw, DateTime sampledAtUtc)
        {
            try
            {
                using var conn = CreateExternalConnection();
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT OR REPLACE INTO alert_raw_samples
                                        (alert_id, server_name, raw_value, sampled_at)
                                    VALUES (@aid, @srv, @val, @sat)";
                cmd.Parameters.AddWithValue("@aid", alertId);
                cmd.Parameters.AddWithValue("@srv", serverName);
                cmd.Parameters.AddWithValue("@val", raw);
                cmd.Parameters.AddWithValue("@sat", sampledAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "[CACHE] Could not store the raw sample for {AlertId}/{Server}; the next cycle will report not-yet-measured", alertId, serverName);
                return false;
            }
        }

        /// <summary>
        /// Brings an EXISTING <c>alert_baseline_stats</c> table up to the current column set.
        ///
        /// <para>Two generations of columns are added here. The trend-detection five arrived with
        /// the OLS slope work; the lower-fence three arrived with B (2026-08-22), when the learned
        /// IQR fences became direction-aware and a <c>less_than</c> alert stopped being handed a
        /// fence that sat above every sample it had ever seen.</para>
        ///
        /// <para>The check is a PRAGMA table_info read rather than an ALTER-and-swallow, so a
        /// genuine failure is still visible instead of being caught alongside the expected
        /// duplicate-column error. Every added column is NOT NULL DEFAULT 0, which SQLite
        /// backfills into existing rows: a stats row written before this migration therefore
        /// carries a lower fence of 0 until the next hourly recompute, and a 0 fence on a
        /// <c>less_than</c> alert fires nothing. Stale-quiet, not stale-noisy.</para>
        ///
        /// <para>Separated from <see cref="InitializeSchema"/> and made internal so the upgrade of
        /// an old DB can be exercised directly against a database created with the OLD schema
        /// (AlertBaselineFenceDirectionTests), rather than asserted about.</para>
        /// </summary>
        internal static void MigrateAlertBaselineStats(SqliteConnection conn)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var info = conn.CreateCommand())
            {
                info.CommandText = "PRAGMA table_info(alert_baseline_stats)";
                using var reader = info.ExecuteReader();
                while (reader.Read())
                    existing.Add(reader.GetString(1));
            }

            if (existing.Count == 0) return;   // table does not exist here — nothing to migrate

            foreach (var (column, ddl) in new[]
            {
                ("trend_slope",          "ALTER TABLE alert_baseline_stats ADD COLUMN trend_slope          REAL    NOT NULL DEFAULT 0"),
                ("trend_r_squared",      "ALTER TABLE alert_baseline_stats ADD COLUMN trend_r_squared      REAL    NOT NULL DEFAULT 0"),
                ("trend_sample_count",   "ALTER TABLE alert_baseline_stats ADD COLUMN trend_sample_count   INTEGER NOT NULL DEFAULT 0"),
                ("is_trend_warning",     "ALTER TABLE alert_baseline_stats ADD COLUMN is_trend_warning     INTEGER NOT NULL DEFAULT 0"),
                ("is_trend_critical",    "ALTER TABLE alert_baseline_stats ADD COLUMN is_trend_critical    INTEGER NOT NULL DEFAULT 0"),
                ("p05",                  "ALTER TABLE alert_baseline_stats ADD COLUMN p05                  REAL    NOT NULL DEFAULT 0"),
                ("threshold_warn_lower", "ALTER TABLE alert_baseline_stats ADD COLUMN threshold_warn_lower REAL    NOT NULL DEFAULT 0"),
                ("threshold_crit_lower", "ALTER TABLE alert_baseline_stats ADD COLUMN threshold_crit_lower REAL    NOT NULL DEFAULT 0"),
            })
            {
                if (existing.Contains(column)) continue;
                using var alt = conn.CreateCommand();
                alt.CommandText = ddl;
                alt.ExecuteNonQuery();
            }
        }

        // ── Batch Writer (D24) ─────────────────────────────────────────

        private sealed class BatchOperation
        {
            public required Func<SqliteConnection, SqliteTransaction, Task> Action { get; init; }
            public required TaskCompletionSource Completion { get; init; }
        }

        private async Task BatchWriterLoopAsync()
        {
            while (!_batchCts.Token.IsCancellationRequested)
            {
                var batch = new List<BatchOperation>(50);
                try
                {
                    // Read first operation (blocking)
                    var first = await _batchChannel.Reader.ReadAsync(_batchCts.Token);
                    batch.Add(first);

                    // Batch up to 49 more operations within 100ms
                    var deadline = Task.Delay(100, _batchCts.Token);
                    while (batch.Count < 50 && !deadline.IsCompleted)
                    {
                        if (_batchChannel.Reader.TryRead(out var op))
                        {
                            batch.Add(op);
                        }
                        else
                        {
                            var waitTask = _batchChannel.Reader.WaitToReadAsync(_batchCts.Token).AsTask();
                            var completed = await Task.WhenAny(deadline, waitTask);
                            if (completed == deadline) break;
                            // Try read again now that WaitToRead signaled
                            if (_batchChannel.Reader.TryRead(out op))
                                batch.Add(op);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (batch.Count == 0) continue;

                // Execute batch in single SQLite transaction
                try
                {
                    using var conn = await CreateConnectionAsync();
                    using var tx = conn.BeginTransaction();

                    foreach (var op in batch)
                    {
                        try
                        {
                            await op.Action(conn, tx);
                            op.Completion.TrySetResult();
                        }
                        catch (Exception ex)
                        {
                            op.Completion.TrySetException(ex);
                        }
                    }

                    tx.Commit();
                }
                catch (Exception ex)
                {
                    // If the whole transaction fails, mark all pending ops as failed
                    foreach (var op in batch.Where(o => !o.Completion.Task.IsCompleted))
                        op.Completion.TrySetException(ex);
                }
            }

            // Drain remaining ops on shutdown
            while (_batchChannel.Reader.TryRead(out var remaining))
            {
                remaining.Completion.TrySetCanceled();
            }
        }

        private async Task EnqueueWriteAsync(Func<SqliteConnection, SqliteTransaction, Task> action)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var op = new BatchOperation { Action = action, Completion = tcs };
            // R-L4: avoid blocking the caller (potentially UI) if SQLite is slow.
            // Drop the write and log rather than blocking indefinitely.
            using var writeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _batchChannel.Writer.WriteAsync(op, writeTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                Serilog.Log.Warning("[CACHE] Write timeout on channel — slow SQLite suspected");
                tcs.TrySetCanceled();
                return;
            }
            await tcs.Task;
        }

        // ──────────────────────── Write Operations ──────────────────────

        /// <summary>
        /// Upserts time-series rows into the cache. Uses INSERT OR REPLACE
        /// so duplicate (query_id, instance_key, time_value, series) rows
        /// are overwritten with the latest values.
        /// Enforces row limit per query to prevent unbounded growth.
        /// </summary>
        public async Task UpsertTimeSeriesAsync(string queryId, string instanceKey,
            List<TimeSeriesPoint> rows, DateTime fetchedAt)
        {
            if (rows.Count == 0) return;
            await EnqueueWriteAsync(async (conn, transaction) =>
            {
                var lk = GetLockFor(queryId, instanceKey);
                await lk.WaitAsync();
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT OR REPLACE INTO cache_timeseries
                            (query_id, instance_key, time_value, series, value, fetched_at)
                        VALUES (@qid, @ikey, @tv, @s, @v, @fa)";

                    var pQid = cmd.Parameters.Add("@qid", SqliteType.Text);
                    var pIkey = cmd.Parameters.Add("@ikey", SqliteType.Text);
                    var pTv = cmd.Parameters.Add("@tv", SqliteType.Text);
                    var pS = cmd.Parameters.Add("@s", SqliteType.Text);
                    var pV = cmd.Parameters.Add("@v", SqliteType.Real);
                    var pFa = cmd.Parameters.Add("@fa", SqliteType.Text);

                    var fetchedStr = fetchedAt.ToString("o");

                    foreach (var row in rows)
                    {
                        pQid.Value = queryId;
                        pIkey.Value = instanceKey;
                        pTv.Value = row.Time.ToString("o");
                        pS.Value = row.Series;
                        pV.Value = row.Value;
                        pFa.Value = fetchedStr;
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // Enforce row limit: keep only newest 50000 rows
                    using var trimCmd = conn.CreateCommand();
                    trimCmd.Transaction = transaction;
                    trimCmd.CommandText = @"
                        DELETE FROM cache_timeseries
                        WHERE query_id = @qid AND instance_key = @ikey
                        AND rowid NOT IN (
                            SELECT rowid FROM cache_timeseries
                            WHERE query_id = @qid AND instance_key = @ikey
                            ORDER BY time_value DESC LIMIT 50000
                        )";
                    trimCmd.Parameters.AddWithValue("@qid", queryId);
                    trimCmd.Parameters.AddWithValue("@ikey", instanceKey);
                    await trimCmd.ExecuteNonQueryAsync();
                }
                finally
                {
                    lk.Release();
                }
            });
        }

        /// <summary>
        /// Upserts a single stat value, replacing the previous cached value.
        /// </summary>
        public async Task UpsertStatValueAsync(string queryId, string instanceKey,
            StatValue value, DateTime fetchedAt)
        {
            await EnqueueWriteAsync(async (conn, transaction) =>
            {
                var lk = GetLockFor(queryId, instanceKey);
                await lk.WaitAsync();
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT OR REPLACE INTO cache_stat
                            (query_id, instance_key, label, value, unit, color, fetched_at)
                        VALUES (@qid, @ikey, @label, @val, @unit, @color, @fa)";
                    cmd.Parameters.AddWithValue("@qid", queryId);
                    cmd.Parameters.AddWithValue("@ikey", instanceKey);
                    cmd.Parameters.AddWithValue("@label", value.Label);
                    cmd.Parameters.AddWithValue("@val", value.Value);
                    cmd.Parameters.AddWithValue("@unit", value.Unit);
                    cmd.Parameters.AddWithValue("@color", value.Color);
                    cmd.Parameters.AddWithValue("@fa", fetchedAt.ToString("o"));
                    await cmd.ExecuteNonQueryAsync();
                }
                finally
                {
                    lk.Release();
                }
            });
        }

        /// <summary>
        /// Upserts bar gauge data, replacing the previous cached snapshot.
        /// </summary>
        public async Task UpsertBarGaugeAsync(string queryId, string instanceKey,
            List<StatValue> rows, DateTime fetchedAt)
        {
            await EnqueueWriteAsync(async (conn, transaction) =>
            {
                var lk = GetLockFor(queryId, instanceKey);
                await lk.WaitAsync();
                try
                {
                    // Clear previous gauge data for this query
                    using (var delCmd = conn.CreateCommand())
                    {
                        delCmd.Transaction = transaction;
                        delCmd.CommandText = "DELETE FROM cache_bargauge WHERE query_id = @qid AND instance_key = @ikey";
                        delCmd.Parameters.AddWithValue("@qid", queryId);
                        delCmd.Parameters.AddWithValue("@ikey", instanceKey);
                        await delCmd.ExecuteNonQueryAsync();
                    }

                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT INTO cache_bargauge
                            (query_id, instance_key, label, value, unit, instance, color, fetched_at)
                        VALUES (@qid, @ikey, @label, @val, @unit, @inst, @color, @fa)";

                    var pQid = cmd.Parameters.Add("@qid", SqliteType.Text);
                    var pIkey = cmd.Parameters.Add("@ikey", SqliteType.Text);
                    var pLabel = cmd.Parameters.Add("@label", SqliteType.Text);
                    var pVal = cmd.Parameters.Add("@val", SqliteType.Real);
                    var pUnit = cmd.Parameters.Add("@unit", SqliteType.Text);
                    var pInst = cmd.Parameters.Add("@inst", SqliteType.Text);
                    var pColor = cmd.Parameters.Add("@color", SqliteType.Text);
                    var pFa = cmd.Parameters.Add("@fa", SqliteType.Text);

                    var fetchedStr = fetchedAt.ToString("o");
                    foreach (var row in rows)
                    {
                        pQid.Value = queryId;
                        pIkey.Value = instanceKey;
                        pLabel.Value = row.Label;
                        pVal.Value = row.Value;
                        pUnit.Value = row.Unit;
                        pInst.Value = row.Instance;
                        pColor.Value = row.Color;
                        pFa.Value = fetchedStr;
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                finally
                {
                    lk.Release();
                }
            });
        }

        /// <summary>
        /// Stores a DataTable as a JSON blob, replacing any previous cached value.
        /// </summary>
        public async Task UpsertDataTableAsync(string queryId, string instanceKey,
            DataTable table, DateTime fetchedAt)
        {
            var json = SerializeDataTable(table);
            var stored = ProtectValue(json);   // Encrypt at rest

            await EnqueueWriteAsync(async (conn, transaction) =>
            {
                var lk = GetLockFor(queryId, instanceKey);
                await lk.WaitAsync();
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT OR REPLACE INTO cache_datatable
                            (query_id, instance_key, json_data, fetched_at)
                        VALUES (@qid, @ikey, @json, @fa)";
                    cmd.Parameters.AddWithValue("@qid", queryId);
                    cmd.Parameters.AddWithValue("@ikey", instanceKey);
                    cmd.Parameters.AddWithValue("@json", stored);
                    cmd.Parameters.AddWithValue("@fa", fetchedAt.ToString("o"));
                    await cmd.ExecuteNonQueryAsync();
                }
                finally
                {
                    lk.Release();
                }
            });
        }

        /// <summary>
        /// Upserts check status data, replacing the previous cached snapshot.
        /// </summary>
        public async Task UpsertCheckStatusAsync(string queryId, string instanceKey,
            List<CheckStatus> rows, DateTime fetchedAt)
        {
            await EnqueueWriteAsync(async (conn, transaction) =>
            {
                var lk = GetLockFor(queryId, instanceKey);
                await lk.WaitAsync();
                try
                {
                    using (var delCmd = conn.CreateCommand())
                    {
                        delCmd.Transaction = transaction;
                        delCmd.CommandText = "DELETE FROM cache_checkstatus WHERE query_id = @qid AND instance_key = @ikey";
                        delCmd.Parameters.AddWithValue("@qid", queryId);
                        delCmd.Parameters.AddWithValue("@ikey", instanceKey);
                        await delCmd.ExecuteNonQueryAsync();
                    }

                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT INTO cache_checkstatus
                            (query_id, instance_key, status, count, fetched_at)
                        VALUES (@qid, @ikey, @status, @count, @fa)";

                    var pQid = cmd.Parameters.Add("@qid", SqliteType.Text);
                    var pIkey = cmd.Parameters.Add("@ikey", SqliteType.Text);
                    var pStatus = cmd.Parameters.Add("@status", SqliteType.Text);
                    var pCount = cmd.Parameters.Add("@count", SqliteType.Integer);
                    var pFa = cmd.Parameters.Add("@fa", SqliteType.Text);

                    var fetchedStr = fetchedAt.ToString("o");
                    foreach (var row in rows)
                    {
                        pQid.Value = queryId;
                        pIkey.Value = instanceKey;
                        pStatus.Value = row.Status;
                        pCount.Value = row.Count;
                        pFa.Value = fetchedStr;
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                finally
                {
                    lk.Release();
                }
            });
        }

        /// <summary>
        /// Records the high-water mark (most recent successful fetch time) for a query.
        /// </summary>
        public async Task SetLastFetchTimeAsync(string queryId, string instanceKey, DateTime time)
        {
            await EnqueueWriteAsync(async (conn, transaction) =>
            {
                var lk = GetLockFor(queryId, instanceKey);
                await lk.WaitAsync();
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT OR REPLACE INTO cache_metadata (query_id, instance_key, last_fetch)
                        VALUES (@qid, @ikey, @lf)";
                    cmd.Parameters.AddWithValue("@qid", queryId);
                    cmd.Parameters.AddWithValue("@ikey", instanceKey);
                    cmd.Parameters.AddWithValue("@lf", time.ToString("o"));
                    await cmd.ExecuteNonQueryAsync();
                }
                finally
                {
                    lk.Release();
                }
            });
        }

        // ──────────────────────── Read Operations ───────────────────────

        /// <summary>
        /// Reads cached time-series data within the specified time window.
        /// </summary>
        // Maximum data points returned per chart series to prevent memory pressure.
        // Reads from UserSettings if available, falls back to 2000.
        private int MaxChartDataPoints => _userSettings?.GetChartDataPointCap() ?? 2000;

        public async Task<List<TimeSeriesPoint>> GetTimeSeriesAsync(
            string queryId, string instanceKey, DateTime from, DateTime to)
        {
            using var conn = await CreateConnectionAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT time_value, series, value
                FROM cache_timeseries
                WHERE query_id = @qid
                  AND instance_key = @ikey
                  AND time_value >= @from
                  AND time_value <= @to
                ORDER BY time_value
                LIMIT @maxPoints";
            cmd.Parameters.AddWithValue("@qid", queryId);
            cmd.Parameters.AddWithValue("@ikey", instanceKey);
            cmd.Parameters.AddWithValue("@from", from.ToString("o"));
            cmd.Parameters.AddWithValue("@to", to.ToString("o"));
            cmd.Parameters.AddWithValue("@maxPoints", MaxChartDataPoints);

            var results = new List<TimeSeriesPoint>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new TimeSeriesPoint
                {
                    Time = DateTime.Parse(reader.GetString(0)),
                    Series = reader.GetString(1),
                    Value = reader.GetDouble(2)
                });
            }
            return results;
        }

        /// <summary>
        /// Distinct instance keys that have time-series rows for a query within the
        /// window. Lets callers (e.g. capacity forecasts) discover which servers
        /// actually have cached data instead of guessing keys.
        /// </summary>
        public async Task<List<string>> GetTimeSeriesInstanceKeysAsync(
            string queryId, DateTime from, DateTime to)
        {
            using var conn = await CreateConnectionAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT DISTINCT instance_key
                FROM cache_timeseries
                WHERE query_id = @qid
                  AND time_value >= @from
                  AND time_value <= @to
                ORDER BY instance_key";
            cmd.Parameters.AddWithValue("@qid", queryId);
            cmd.Parameters.AddWithValue("@from", from.ToString("o"));
            cmd.Parameters.AddWithValue("@to", to.ToString("o"));

            var keys = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                keys.Add(reader.GetString(0));
            return keys;
        }

        /// <summary>
        /// Reads the cached stat value for a query, or null if not cached.
        /// </summary>
        public async Task<StatValue?> GetStatValueAsync(string queryId, string instanceKey)
        {
            using var conn = await CreateConnectionAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT label, value, unit, color
                FROM cache_stat
                WHERE query_id = @qid AND instance_key = @ikey";
            cmd.Parameters.AddWithValue("@qid", queryId);
            cmd.Parameters.AddWithValue("@ikey", instanceKey);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new StatValue
                {
                    Label = reader.GetString(0),
                    Value = reader.GetDouble(1),
                    Unit = reader.GetString(2),
                    Color = reader.GetString(3)
                };
            }
            return null;
        }

        /// <summary>
        /// Reads cached bar gauge data for a query.
        /// </summary>
        public async Task<List<StatValue>> GetBarGaugeAsync(string queryId, string instanceKey)
        {
            using var conn = await CreateConnectionAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT label, value, unit, instance, color
                FROM cache_bargauge
                WHERE query_id = @qid AND instance_key = @ikey";
            cmd.Parameters.AddWithValue("@qid", queryId);
            cmd.Parameters.AddWithValue("@ikey", instanceKey);

            var results = new List<StatValue>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new StatValue
                {
                    Label = reader.GetString(0),
                    Value = reader.GetDouble(1),
                    Unit = reader.GetString(2),
                    Instance = reader.GetString(3),
                    Color = reader.GetString(4)
                });
            }
            return results;
        }

        /// <summary>
        /// Reads a cached DataTable from JSON, or null if not cached.
        /// </summary>
        public async Task<DataTable?> GetDataTableAsync(string queryId, string instanceKey)
        {
            using var conn = await CreateConnectionAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT json_data FROM cache_datatable
                WHERE query_id = @qid AND instance_key = @ikey";
            cmd.Parameters.AddWithValue("@qid", queryId);
            cmd.Parameters.AddWithValue("@ikey", instanceKey);

            var raw = (string?)await cmd.ExecuteScalarAsync();
            if (raw == null) return null;
            var json = UnprotectValue(raw);   // Decrypt from cache
            if (json == null) return null;    // Stale/undecryptable entry — treat as cache miss
            return DeserializeDataTable(json);
        }

        /// <summary>
        /// Reads cached check status data for a query.
        /// </summary>
        public async Task<List<CheckStatus>> GetCheckStatusAsync(string queryId, string instanceKey)
        {
            using var conn = await CreateConnectionAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT status, count FROM cache_checkstatus
                WHERE query_id = @qid AND instance_key = @ikey";
            cmd.Parameters.AddWithValue("@qid", queryId);
            cmd.Parameters.AddWithValue("@ikey", instanceKey);

            var results = new List<CheckStatus>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new CheckStatus
                {
                    Status = reader.GetString(0),
                    Count = reader.GetInt32(1)
                });
            }
            return results;
        }

        /// <summary>
        /// Returns the high-water mark (last successful SQL Server fetch time) for a query,
        /// or null if the query has never been cached.
        /// </summary>
        public async Task<DateTime?> GetLastFetchTimeAsync(string queryId, string instanceKey)
        {
            using var conn = await CreateConnectionAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT last_fetch FROM cache_metadata
                WHERE query_id = @qid AND instance_key = @ikey";
            cmd.Parameters.AddWithValue("@qid", queryId);
            cmd.Parameters.AddWithValue("@ikey", instanceKey);

            var result = (string?)await cmd.ExecuteScalarAsync();
            return result != null ? DateTime.Parse(result) : null;
        }

        // ──────────────────────── Eviction / Invalidation ───────────────

        /// <summary>
        /// Gets the current cache database size in bytes.
        /// </summary>
        public async Task<long> GetCacheSizeBytes()
        {
            using var conn = await CreateConnectionAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()";
            var result = await cmd.ExecuteScalarAsync();
            return result != null ? Convert.ToInt64(result) : 0;
        }

        /// <summary>
        /// Enforces a maximum cache size by evicting oldest data until the live data set
        /// fits, then reclaims the freed pages. The age cutoff starts at 24h and halves
        /// down to a 5-minute floor — the routine time-based pass has already removed
        /// anything older than the retention threshold, so a fixed cutoff above it (the
        /// previous 48h implementation) was a guaranteed no-op and the cap was dead code.
        /// Progress is measured net of freelist pages, so each round's DELETEs count
        /// without an intermediate vacuum.
        ///
        /// <para>⚠ THIS CANNOT SHED RETAINED HISTORY, and that is deliberate. It works through
        /// <see cref="EvictOlderThanAsync"/>, which only sees tables with a <c>fetched_at</c> column,
        /// and <c>metric_history</c> has none. So retained history is bounded by its OWN row cap
        /// (<c>MetricRetention:MaxRows</c>, pruned by <c>MetricHistoryCollectorService</c>) and by
        /// nothing else. Keep that cap well inside <c>MaxCacheSizeMB</c>: if retained history alone
        /// ever exceeded the cap, this loop would squeeze the volatile cache down to its 5-minute
        /// floor on every pass and still not reach the target. It terminates either way — the floor
        /// is the exit — but the cache would be useless.</para>
        /// </summary>
        public async Task EnforceSizeLimitAsync(long maxSizeBytes)
        {
            try
            {
                if (await GetCacheSizeBytes() <= maxSizeBytes)
                    return;

                var floor = TimeSpan.FromMinutes(5);
                var cutoff = TimeSpan.FromHours(24);
                while (true)
                {
                    await EvictOlderThanAsync(cutoff);
                    if (await GetLiveCacheSizeBytesAsync() <= maxSizeBytes || cutoff <= floor)
                        break;
                    cutoff = TimeSpan.FromTicks(Math.Max(cutoff.Ticks / 2, floor.Ticks));
                }

                await ReclaimFreePagesAsync();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                InitializeSchema(); // Recover from missing tables
            }
        }

        /// <summary>
        /// Live data size in bytes: total pages minus freelist pages. DELETEs move pages
        /// to the freelist without shrinking the file, so this responds to eviction
        /// before any vacuum runs (GetCacheSizeBytes, which measures the file, does not).
        /// </summary>
        private async Task<long> GetLiveCacheSizeBytesAsync()
        {
            using var conn = await CreateConnectionAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT (page_count - freelist_count) * page_size " +
                              "FROM pragma_page_count(), pragma_freelist_count(), pragma_page_size()";
            var result = await cmd.ExecuteScalarAsync();
            return result != null ? Convert.ToInt64(result) : 0;
        }

        // Full incremental_vacuum (no page limit) rather than RunMaintenanceAsync's
        // 1000-page nibble — an over-cap cache may have hundreds of MB to hand back.
        // No-op on DBs created before the auto_vacuum=INCREMENTAL pragma, same
        // limitation RunMaintenanceAsync already has.
        private async Task ReclaimFreePagesAsync()
        {
            await _globalWriteLock.WaitAsync();
            try
            {
                using var conn = await CreateConnectionAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA incremental_vacuum;";
                await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                _globalWriteLock.Release();
            }
        }

        /// <summary>
        /// Removes all cached data older than the specified age across all tables.
        /// If the schema is missing (DB file was recreated externally), re-creates it automatically.
        /// </summary>
        public async Task EvictOlderThanAsync(TimeSpan maxAge)
        {
            var cutoff = DateTime.UtcNow.Subtract(maxAge).ToString("o");

            await _globalWriteLock.WaitAsync();
            try
            {
                using var conn = await CreateConnectionAsync();

                try
                {
                    await EvictOlderThanCore(conn, cutoff);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 1) // SQLITE_ERROR (no such table)
                {
                    InitializeSchema();
                    await EvictOlderThanCore(conn, cutoff);
                }
            }
            finally
            {
                _globalWriteLock.Release();
            }
        }

        private static async Task EvictOlderThanCore(SqliteConnection conn, string cutoff)
        {
            // Dynamically find all user tables that have a fetched_at column
            // so new tables (e.g. alert_baseline_samples) are covered automatically
            var tablesWithFetchedAt = new List<string>();
            using (var listCmd = conn.CreateCommand())
            {
                listCmd.CommandText = @"
                    SELECT m.name
                    FROM sqlite_master m
                    JOIN pragma_table_info(m.name) c ON c.name = 'fetched_at'
                    WHERE m.type = 'table'
                    AND m.name NOT LIKE 'sqlite_%'
                    AND m.name != 'cache_metadata'
                    AND m.name != 'alert_baseline_stats'
                    ORDER BY m.name;";
                using var reader = await listCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    tablesWithFetchedAt.Add(reader.GetString(0));
            }

            foreach (var table in tablesWithFetchedAt)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {ValidateTableName(table)} WHERE fetched_at < @cutoff";
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
                await cmd.ExecuteNonQueryAsync();
            }

            // Clean metadata for queries with no remaining data
            using var metaCmd = conn.CreateCommand();
            metaCmd.CommandText = "DELETE FROM cache_metadata WHERE last_fetch < @cutoff";
            metaCmd.Parameters.AddWithValue("@cutoff", cutoff);
            await metaCmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Hard retention purge: deletes all data older than the specified age
        /// across every cache table and returns the total number of rows removed.
        /// If the schema is missing, re-creates it automatically.
        /// </summary>
        public async Task<long> PurgeOlderThanAsync(TimeSpan maxAge)
        {
            var cutoff = DateTime.UtcNow.Subtract(maxAge).ToString("o");

            await _globalWriteLock.WaitAsync();
            try
            {
                using var conn = await CreateConnectionAsync();

                try
                {
                    return await PurgeOlderThanCore(conn, cutoff);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
                {
                    InitializeSchema();
                    return await PurgeOlderThanCore(conn, cutoff);
                }
            }
            finally
            {
                _globalWriteLock.Release();
            }
        }

        private static async Task<long> PurgeOlderThanCore(SqliteConnection conn, string cutoff)
        {
            long totalDeleted = 0;
            var tables = new[] { "cache_timeseries", "cache_stat", "cache_bargauge",
                                 "cache_datatable", "cache_checkstatus" };

            foreach (var table in tables)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {ValidateTableName(table)} WHERE fetched_at < @cutoff";
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
                totalDeleted += await cmd.ExecuteNonQueryAsync();
            }

            using var metaCmd = conn.CreateCommand();
            metaCmd.CommandText = "DELETE FROM cache_metadata WHERE last_fetch < @cutoff";
            metaCmd.Parameters.AddWithValue("@cutoff", cutoff);
            totalDeleted += await metaCmd.ExecuteNonQueryAsync();
            return totalDeleted;
        }

        /// <summary>
        /// Trims time-series cache rows older than the specified cutoff time
        /// for a specific query. Used to keep the cache tight to the active time window.
        /// </summary>
        public async Task TrimTimeSeriesAsync(string queryId, string instanceKey, DateTime olderThan)
        {
            var lk = GetLockFor(queryId, instanceKey);
            await lk.WaitAsync();
            try
            {
                using var conn = await CreateConnectionAsync();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    DELETE FROM cache_timeseries
                    WHERE query_id = @qid
                      AND instance_key = @ikey
                      AND time_value < @cutoff";
                cmd.Parameters.AddWithValue("@qid", queryId);
                cmd.Parameters.AddWithValue("@ikey", instanceKey);
                cmd.Parameters.AddWithValue("@cutoff", olderThan.ToString("o"));
                await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                lk.Release();
            }
        }

        // ───────────────────── Retained metric history ─────────────────────
        // Durable, downsampled, per-panel opt-in. Deliberately NOT a cache and deliberately NOT
        // touched by TrimTimeSeriesAsync, EvictOlderThanAsync, PurgeOlderThanAsync or
        // InvalidateAllAsync. Full design + the fetched_at trap: MetricRetentionOptions.

        internal const string MetricHistoryTable = "metric_history";

        /// <summary>
        /// Appends samples to retained history, downsampled into <paramref name="bucket"/>. One row
        /// survives per (panel, instance, series, bucket) — a later sample in the same bucket replaces
        /// the earlier one, which is what bounds the row count independently of refresh rate.
        /// NaN values are dropped: an unmeasured instant must never be retained as a number.
        /// Returns the number of samples written.
        /// </summary>
        public async Task<int> AppendMetricHistoryAsync(
            string queryId, string instanceKey, IReadOnlyList<TimeSeriesPoint> rows, TimeSpan bucket)
        {
            if (rows == null || rows.Count == 0) return 0;

            var written = 0;
            await EnqueueWriteAsync(async (conn, transaction) =>
            {
                var lk = GetLockFor(queryId, instanceKey);
                await lk.WaitAsync();
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT OR REPLACE INTO metric_history
                            (query_id, instance_key, bucket_utc, series, value)
                        VALUES (@qid, @ikey, @b, @s, @v)";

                    var pQid = cmd.Parameters.Add("@qid", SqliteType.Text);
                    var pIkey = cmd.Parameters.Add("@ikey", SqliteType.Text);
                    var pB = cmd.Parameters.Add("@b", SqliteType.Text);
                    var pS = cmd.Parameters.Add("@s", SqliteType.Text);
                    var pV = cmd.Parameters.Add("@v", SqliteType.Real);

                    foreach (var row in rows)
                    {
                        if (double.IsNaN(row.Value) || double.IsInfinity(row.Value)) continue;
                        pQid.Value = queryId;
                        pIkey.Value = instanceKey;
                        pB.Value = MetricHistoryTime.BucketKey(row.Time, bucket);
                        pS.Value = row.Series ?? "";
                        pV.Value = row.Value;
                        await cmd.ExecuteNonQueryAsync();
                        written++;
                    }
                }
                finally
                {
                    lk.Release();
                }
            });
            return written;
        }

        /// <summary>
        /// Reads retained history for one panel and instance inside a window. Times come back in local
        /// wall-clock, the axis the charts and the toolbar range are drawn in.
        ///
        /// <para>⚠ THE CHART POINT CAP REDUCES RESOLUTION, IT NEVER SHORTENS THE WINDOW. This read used
        /// to be <c>ORDER BY bucket_utc LIMIT @maxPoints</c>, which keeps the OLDEST cap rows and throws
        /// the newest away. At the shipped 60-second bucket that silently ended the line 33h20m into a
        /// one-series range and 16h40m into a two-series one, so the two ranges retention exists to
        /// serve, "Last 24 hours" and "Last 7 days", were exactly the ones it defeated. The coverage
        /// probe counts with no cap, so the honesty notice saw whole coverage and said nothing.</para>
        ///
        /// <para>Instead the whole window is strided: every stride-th retained bucket per series,
        /// counted back from the newest, so the line spans the range the operator asked for and its
        /// most recent point is the most recent reading. Every returned value is a stored measurement.
        /// Nothing is averaged or interpolated, because a plotted point that was never measured is a
        /// fabrication. Reduced resolution is a fact the operator is told:
        /// <see cref="RetentionCoverage.IsDownsampled"/> carries it to the panel notice.</para>
        ///
        /// <para>Both ends of every series are pinned. The stride alone lands the oldest kept point up
        /// to one stride inside the range, which is a shortened line again, just by less. The oldest
        /// bucket each series has in the window is added back, so the line starts and ends where the
        /// data does.</para>
        ///
        /// <para>The bound is exact. Each series can round its count up and can add its oldest bucket,
        /// so the stride is computed against <c>cap - 2 * seriesCount</c> and the total kept can never
        /// exceed <c>cap</c>.</para>
        /// </summary>
        public async Task<List<TimeSeriesPoint>> GetMetricHistoryAsync(
            string queryId, string instanceKey, DateTime from, DateTime to)
        {
            var cap = MaxChartDataPoints;
            var fromKey = MetricHistoryTime.Format(from);
            var toKey = MetricHistoryTime.Format(to);

            using var conn = await CreateConnectionAsync();

            int total, seriesCount;
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = @"
                    SELECT COUNT(*), COUNT(DISTINCT series)
                    FROM metric_history
                    WHERE query_id = @qid
                      AND instance_key = @ikey
                      AND bucket_utc >= @from
                      AND bucket_utc <= @to";
                probe.Parameters.AddWithValue("@qid", queryId);
                probe.Parameters.AddWithValue("@ikey", instanceKey);
                probe.Parameters.AddWithValue("@from", fromKey);
                probe.Parameters.AddWithValue("@to", toKey);

                using var probeReader = await probe.ExecuteReaderAsync();
                if (!await probeReader.ReadAsync()) return new List<TimeSeriesPoint>();
                total = probeReader.GetInt32(0);
                seriesCount = probeReader.GetInt32(1);
            }

            if (total == 0) return new List<TimeSeriesPoint>();

            var budget = Math.Max(1, cap - (2 * seriesCount));
            var stride = total <= cap ? 1 : (int)Math.Ceiling(total / (double)budget);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT bucket_utc, series, value
                FROM metric_history
                WHERE query_id = @qid
                  AND instance_key = @ikey
                  AND bucket_utc >= @from
                  AND bucket_utc <= @to
                ORDER BY bucket_utc DESC";
            cmd.Parameters.AddWithValue("@qid", queryId);
            cmd.Parameters.AddWithValue("@ikey", instanceKey);
            cmd.Parameters.AddWithValue("@from", fromKey);
            cmd.Parameters.AddWithValue("@to", toKey);

            var results = new List<TimeSeriesPoint>(Math.Min(total, cap));
            var seenPerSeries = new Dictionary<string, int>(StringComparer.Ordinal);
            var oldestSkipped = new Dictionary<string, TimeSeriesPoint>(StringComparer.Ordinal);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var series = reader.GetString(1);
                var point = new TimeSeriesPoint
                {
                    Time = MetricHistoryTime.ParseToLocal(reader.GetString(0)),
                    Series = series,
                    Value = reader.GetDouble(2)
                };

                seenPerSeries.TryGetValue(series, out var index);
                seenPerSeries[series] = index + 1;

                // Rows arrive newest first, so the last one skipped for a series is its oldest.
                if (stride > 1 && index % stride != 0)
                {
                    oldestSkipped[series] = point;
                    continue;
                }

                oldestSkipped.Remove(series);
                results.Add(point);

                // Unreachable while 2 * seriesCount < cap, which the stride arithmetic guarantees. A
                // panel that somehow retained more distinct series than the chart draws points is
                // bounded here rather than allowed to return the table, and the notice still fires.
                if (results.Count >= cap) break;
            }

            foreach (var oldest in oldestSkipped.Values)
            {
                if (results.Count >= cap) break;
                results.Add(oldest);
            }

            results.Sort((a, b) => a.Time.CompareTo(b.Time));
            return results;
        }

        /// <summary>
        /// What retained history actually covers for a panel and instance in a window. The caller uses
        /// this to tell the operator the truth when the window opens before retention started, instead
        /// of drawing a short line and calling it a trend.
        /// </summary>
        public async Task<RetentionCoverage> GetMetricHistoryCoverageAsync(
            string queryId, string instanceKey, DateTime from, DateTime to)
        {
            using var conn = await CreateConnectionAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    (SELECT COUNT(*) FROM metric_history
                      WHERE query_id = @qid AND instance_key = @ikey
                        AND bucket_utc >= @from AND bucket_utc <= @to),
                    (SELECT MIN(bucket_utc) FROM metric_history
                      WHERE query_id = @qid AND instance_key = @ikey),
                    (SELECT MAX(bucket_utc) FROM metric_history
                      WHERE query_id = @qid AND instance_key = @ikey)";
            cmd.Parameters.AddWithValue("@qid", queryId);
            cmd.Parameters.AddWithValue("@ikey", instanceKey);
            cmd.Parameters.AddWithValue("@from", MetricHistoryTime.Format(from));
            cmd.Parameters.AddWithValue("@to", MetricHistoryTime.Format(to));

            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return new RetentionCoverage(0, null, MaxChartDataPoints);

            var count = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            DateTime? earliest = reader.IsDBNull(1) ? null : MetricHistoryTime.ParseToLocal(reader.GetString(1));
            DateTime? latest = reader.IsDBNull(2) ? null : MetricHistoryTime.ParseToLocal(reader.GetString(2));

            // The cap travels with the count deliberately. It is what GetMetricHistoryAsync strides
            // against, so it is the only way the caller can tell a whole-resolution line from a
            // sampled one, and a sampled line the operator was not told about is the defect this
            // pairing closes.
            //
            // Both extents are store-wide and unfiltered by the window, deliberately. They are what
            // lets a caller separate three facts a row count alone cannot: history that starts too
            // late, history that STOPPED, and a window that fell in a hole between the two. Measuring
            // only the front of the window is how a panel that drew 14 hours of a 24-hour comparison,
            // and a panel that drew no line at all, both rendered in silence.
            return new RetentionCoverage(count, earliest, MaxChartDataPoints, latest);
        }

        /// <summary>Total retained rows across every panel. The number the row cap is enforced against.</summary>
        public async Task<long> GetMetricHistoryRowCountAsync()
        {
            using var conn = await CreateConnectionAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {ValidateTableName(MetricHistoryTable)}";
            var scalar = await cmd.ExecuteScalarAsync();
            return scalar is long l ? l : Convert.ToInt64(scalar ?? 0L);
        }

        /// <summary>
        /// Prunes retained history to BOTH bounds and reports what each one removed.
        ///
        /// <para>Age first, then the hard row cap. The cap deletes the oldest buckets, so it can never
        /// be crossed by adding series or opting in another panel — the newest history always wins.
        /// Both are needed: age alone is unbounded in the series dimension, and a row cap alone would
        /// keep a nearly-idle install's samples forever.</para>
        /// </summary>
        public async Task<(int ByAge, int ByCap)> PruneMetricHistoryAsync(DateTime cutoff, long maxRows)
        {
            var byAge = 0;
            var byCap = 0;

            await _globalWriteLock.WaitAsync();
            try
            {
                using var conn = await CreateConnectionAsync();

                using (var ageCmd = conn.CreateCommand())
                {
                    ageCmd.CommandText = $@"
                        DELETE FROM {ValidateTableName(MetricHistoryTable)}
                        WHERE bucket_utc < @cutoff";
                    ageCmd.Parameters.AddWithValue("@cutoff", MetricHistoryTime.Format(cutoff));
                    byAge = await ageCmd.ExecuteNonQueryAsync();
                }

                if (maxRows > 0)
                {
                    using var capCmd = conn.CreateCommand();
                    // Keep the newest maxRows rows; delete everything else. Ordering is by the stored
                    // canonical key, which is fixed-width and sortable precisely so this works.
                    capCmd.CommandText = $@"
                        DELETE FROM {ValidateTableName(MetricHistoryTable)}
                        WHERE rowid NOT IN (
                            SELECT rowid FROM {ValidateTableName(MetricHistoryTable)}
                            ORDER BY bucket_utc DESC, rowid DESC
                            LIMIT @maxRows
                        )";
                    capCmd.Parameters.AddWithValue("@maxRows", maxRows);
                    byCap = await capCmd.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                _globalWriteLock.Release();
            }

            return (byAge, byCap);
        }

        /// <summary>
        /// Deletes ALL retained history. Not called by any eviction or flush path — retained history is
        /// not cache and is not discarded when the cache is. Exposed so an operator-facing action, or a
        /// test, can clear it deliberately.
        /// </summary>
        public async Task<int> ClearMetricHistoryAsync()
        {
            await _globalWriteLock.WaitAsync();
            try
            {
                using var conn = await CreateConnectionAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {ValidateTableName(MetricHistoryTable)}";
                return await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                _globalWriteLock.Release();
            }
        }

        /// <summary>
        /// Clears all cache tables. Called when the user changes time range or instance.
        /// If the schema is missing (DB file was recreated externally), re-creates it automatically.
        /// Retained history is NOT a cache table and is deliberately absent from the list below.
        /// </summary>
        public async Task InvalidateAllAsync()
        {
            await _globalWriteLock.WaitAsync();
            try
            {
                using var conn = await CreateConnectionAsync();

                try
                {
                    await InvalidateAllCore(conn);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
                {
                    InitializeSchema();
                    // Tables are now empty after re-creation — no need to DELETE again
                }
            }
            finally
            {
                _globalWriteLock.Release();
            }
        }

        private static async Task InvalidateAllCore(SqliteConnection conn)
        {
            var tables = new[] { "cache_timeseries", "cache_stat", "cache_bargauge",
                                 "cache_datatable", "cache_checkstatus", "cache_metadata" };
            foreach (var table in tables)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {ValidateTableName(table)}";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // ──────────────────────── DataTable Serialization ───────────────

        /// <summary>
        /// Serializes a DataTable to JSON using a streaming Utf8JsonWriter.
        /// Avoids the intermediate List&lt;Dictionary&gt; allocation of the old approach,
        /// reducing GC pressure significantly on large result sets.
        /// </summary>
        private static string SerializeDataTable(DataTable dt)
        {
            var columns = dt.Columns;
            var colCount = columns.Count;

            using var ms = new MemoryStream(capacity: Math.Max(256, dt.Rows.Count * colCount * 16));
            using var writer = new Utf8JsonWriter(ms);

            writer.WriteStartArray();
            foreach (DataRow row in dt.Rows)
            {
                writer.WriteStartObject();
                for (int i = 0; i < colCount; i++)
                {
                    writer.WritePropertyName(columns[i].ColumnName);
                    var val = row[i];
                    if (val == DBNull.Value || val is null)
                    {
                        writer.WriteNullValue();
                    }
                    else
                    {
                        switch (val)
                        {
                            case string s: writer.WriteStringValue(s); break;
                            case int iv: writer.WriteNumberValue(iv); break;
                            case long lv: writer.WriteNumberValue(lv); break;
                            case double dv: writer.WriteNumberValue(dv); break;
                            case float fv: writer.WriteNumberValue(fv); break;
                            case decimal decv: writer.WriteNumberValue(decv); break;
                            case bool bv: writer.WriteBooleanValue(bv); break;
                            case DateTime dtv: writer.WriteStringValue(dtv.ToString("o")); break;
                            case DateTimeOffset dto: writer.WriteStringValue(dto.ToString("o")); break;
                            case Guid gv: writer.WriteStringValue(gv.ToString()); break;
                            case byte[] bytes: writer.WriteBase64StringValue(bytes); break;
                            default: writer.WriteStringValue(val.ToString()); break;
                        }
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.Flush();

            return Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <summary>
        /// Deserializes a JSON string back to a DataTable using JsonDocument,
        /// avoiding the intermediate List&lt;Dictionary&lt;string, JsonElement&gt;&gt; allocation.
        /// </summary>
        private static DataTable DeserializeDataTable(string json)
        {
            var dt = new DataTable();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return dt;

            var firstRow = root[0];
            foreach (var prop in firstRow.EnumerateObject())
                dt.Columns.Add(prop.Name);

            dt.BeginLoadData();
            foreach (var rowElement in root.EnumerateArray())
            {
                var row = dt.NewRow();
                foreach (var prop in rowElement.EnumerateObject())
                {
                    row[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null
                        ? DBNull.Value
                        : (object)prop.Value.ToString()!;
                }
                dt.Rows.Add(row);
            }
            dt.EndLoadData();
            return dt;
        }

        // ──────────────────────── Maintenance ─────────────────────────

        /// <summary>
        /// Runs liveQueries maintenance tasks: PRAGMA optimize (updates query planner
        /// statistics), VACUUM (reclaims free pages and defragments the database
        /// file), and optionally PRAGMA integrity_check.
        /// </summary>
        public async Task<MaintenanceResult> RunMaintenanceAsync(bool includeIntegrityCheck = false)
        {
            var result = new MaintenanceResult { StartedAt = DateTime.UtcNow };

            await _globalWriteLock.WaitAsync();
            try
            {
                using var conn = await CreateConnectionAsync();

                // PRAGMA optimize — lets liveQueries update internal statistics
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA optimize;";
                    await cmd.ExecuteNonQueryAsync();
                }
                result.OptimizeCompleted = true;

                // incremental_vacuum — reclaims up to 1000 free pages without exclusive lock
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA incremental_vacuum(1000);";
                    await cmd.ExecuteNonQueryAsync();
                }
                result.VacuumCompleted = true;

                // Optional integrity check
                if (includeIntegrityCheck)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "PRAGMA integrity_check;";
                    var checkResult = (string?)await cmd.ExecuteScalarAsync();
                    result.IntegrityCheckResult = checkResult ?? "unknown";
                    result.IntegrityOk = string.Equals(checkResult, "ok",
                        StringComparison.OrdinalIgnoreCase);
                }

                result.CompletedAt = DateTime.UtcNow;
                result.Success = true;
            }
            finally
            {
                _globalWriteLock.Release();
            }

            return result;
        }

        // ──────────────────────── Helpers ───────────────────────────────

        // Returns an already-open, SQLCipher-keyed connection.
        private SqliteConnection CreateConnection() => SqliteCipherHelper.OpenEncrypted(_connectionString);

        // Returns an already-open async SQLCipher-keyed connection.
        private async Task<SqliteConnection> CreateConnectionAsync()
            => await SqliteCipherHelper.OpenEncryptedAsync(_connectionString);

        public void Dispose()
        {
            if (!_disposed)
            {
                _batchCts.Cancel();
                _batchChannel.Writer.Complete();
                try { _batchWriterTask.Wait(TimeSpan.FromSeconds(5)); }
                catch { /* best effort */ }
                _batchCts.Dispose();
                _globalWriteLock.Dispose();
                foreach (var sem in _writeLocks) sem.Dispose();
                _disposed = true;
            }
        }
    }
}
