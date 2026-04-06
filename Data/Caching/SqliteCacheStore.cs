/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SqlHealthAssessment.Data.Models;
using SqlHealthAssessment.Data.Services;
using Microsoft.Data.Sqlite;

namespace SqlHealthAssessment.Data.Caching
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
            "cache_datatable", "cache_checkstatus", "cache_metadata"
        };

        private static string ValidateTableName(string table)
        {
            if (!AllowedTables.Contains(table))
                throw new ArgumentException($"Invalid cache table name: '{table}'");
            return table;
        }

        private readonly string _connectionString;
        private readonly DataProtectionService? _dataProtection;

        // Per-(queryId:instanceKey) semaphores for concurrent panel writes.
        // Global ops (eviction, vacuum) use _globalWriteLock exclusively.
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();
        private readonly SemaphoreSlim _globalWriteLock = new(1, 1);
        private bool _disposed;

        private SemaphoreSlim GetLockFor(string queryId, string instanceKey) =>
            _writeLocks.GetOrAdd($"{queryId}:{instanceKey}", _ => new SemaphoreSlim(1, 1));

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public liveQueriesCacheStore(DataProtectionService? dataProtection = null)
        {
            _dataProtection = dataProtection;
            var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SqlHealthAssessment-cache.db");
            _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";
            InitializeSchema();
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
        private string UnprotectValue(string value)
        {
            if (_dataProtection == null) return value;
            var result = _dataProtection.Unprotect(value);
            return string.IsNullOrEmpty(result) ? value : result; // Fallback for pre-encryption data
        }

        // ──────────────────────────── Schema ────────────────────────────

        /// <summary>
        /// Re-creates cache tables if the database file was deleted externally.
        /// Safe to call multiple times (uses CREATE TABLE IF NOT EXISTS).
        /// </summary>
        public void EnsureSchema() => InitializeSchema();

        private void InitializeSchema()
        {
            using var conn = CreateConnection();
            conn.Open();

            // Enable WAL mode for concurrent reads during writes
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
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

                CREATE INDEX IF NOT EXISTS idx_ts_query_time
                    ON cache_timeseries(query_id, instance_key, time_value);

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

                CREATE TABLE IF NOT EXISTS cache_datatable (
                    query_id     TEXT NOT NULL,
                    instance_key TEXT NOT NULL,
                    json_data    TEXT NOT NULL,
                    fetched_at   TEXT NOT NULL,
                    PRIMARY KEY (query_id, instance_key)
                );

                CREATE TABLE IF NOT EXISTS cache_checkstatus (
                    query_id     TEXT NOT NULL,
                    instance_key TEXT NOT NULL,
                    status       TEXT NOT NULL,
                    count        INTEGER NOT NULL,
                    fetched_at   TEXT NOT NULL,
                    PRIMARY KEY (query_id, instance_key, status)
                );

                CREATE TABLE IF NOT EXISTS cache_metadata (
                    query_id     TEXT NOT NULL,
                    instance_key TEXT NOT NULL,
                    last_fetch   TEXT NOT NULL,
                    PRIMARY KEY (query_id, instance_key)
                );
            ";
            cmd.ExecuteNonQuery();
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

            var lk = GetLockFor(queryId, instanceKey);
            await lk.WaitAsync();
            try
            {
                using var conn = CreateConnection();
                await conn.OpenAsync();
                using var transaction = conn.BeginTransaction();

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
                    cmd.ExecuteNonQuery();
                }

                // Enforce row limit: keep only newest 50000 rows (supports ~2 weeks at 1-min intervals)
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
                trimCmd.ExecuteNonQuery();

                transaction.Commit();
            }
            finally
            {
                lk.Release();
            }
        }

        /// <summary>
        /// Upserts a single stat value, replacing the previous cached value.
        /// </summary>
        public async Task UpsertStatValueAsync(string queryId, string instanceKey,
            StatValue value, DateTime fetchedAt)
        {
            var lk = GetLockFor(queryId, instanceKey);
            await lk.WaitAsync();
            try
            {
                using var conn = CreateConnection();
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
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
        }

        /// <summary>
        /// Upserts bar gauge data, replacing the previous cached snapshot.
        /// </summary>
        public async Task UpsertBarGaugeAsync(string queryId, string instanceKey,
            List<StatValue> rows, DateTime fetchedAt)
        {
            var lk = GetLockFor(queryId, instanceKey);
            await lk.WaitAsync();
            try
            {
                using var conn = CreateConnection();
                await conn.OpenAsync();
                using var transaction = conn.BeginTransaction();

                // Clear previous gauge data for this query
                using (var delCmd = conn.CreateCommand())
                {
                    delCmd.Transaction = transaction;
                    delCmd.CommandText = "DELETE FROM cache_bargauge WHERE query_id = @qid AND instance_key = @ikey";
                    delCmd.Parameters.AddWithValue("@qid", queryId);
                    delCmd.Parameters.AddWithValue("@ikey", instanceKey);
                    delCmd.ExecuteNonQuery();
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
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            finally
            {
                lk.Release();
            }
        }

        /// <summary>
        /// Stores a DataTable as a JSON blob, replacing any previous cached value.
        /// </summary>
        public async Task UpsertDataTableAsync(string queryId, string instanceKey,
            DataTable table, DateTime fetchedAt)
        {
            var json = SerializeDataTable(table);
            var stored = ProtectValue(json);   // Encrypt at rest

            var lk = GetLockFor(queryId, instanceKey);
            await lk.WaitAsync();
            try
            {
                using var conn = CreateConnection();
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
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
        }

        /// <summary>
        /// Upserts check status data, replacing the previous cached snapshot.
        /// </summary>
        public async Task UpsertCheckStatusAsync(string queryId, string instanceKey,
            List<CheckStatus> rows, DateTime fetchedAt)
        {
            var lk = GetLockFor(queryId, instanceKey);
            await lk.WaitAsync();
            try
            {
                using var conn = CreateConnection();
                await conn.OpenAsync();
                using var transaction = conn.BeginTransaction();

                using (var delCmd = conn.CreateCommand())
                {
                    delCmd.Transaction = transaction;
                    delCmd.CommandText = "DELETE FROM cache_checkstatus WHERE query_id = @qid AND instance_key = @ikey";
                    delCmd.Parameters.AddWithValue("@qid", queryId);
                    delCmd.Parameters.AddWithValue("@ikey", instanceKey);
                    delCmd.ExecuteNonQuery();
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
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            finally
            {
                lk.Release();
            }
        }

        /// <summary>
        /// Records the high-water mark (most recent successful fetch time) for a query.
        /// </summary>
        public async Task SetLastFetchTimeAsync(string queryId, string instanceKey, DateTime time)
        {
            var lk = GetLockFor(queryId, instanceKey);
            await lk.WaitAsync();
            try
            {
                using var conn = CreateConnection();
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
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
        }

        // ──────────────────────── Read Operations ───────────────────────

        /// <summary>
        /// Reads cached time-series data within the specified time window.
        /// </summary>
        // Maximum data points returned per chart series to prevent memory pressure
        private const int MaxChartDataPoints = 2000;

        public async Task<List<TimeSeriesPoint>> GetTimeSeriesAsync(
            string queryId, string instanceKey, DateTime from, DateTime to)
        {
            using var conn = CreateConnection();
            await conn.OpenAsync();

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
        /// Reads the cached stat value for a query, or null if not cached.
        /// </summary>
        public async Task<StatValue?> GetStatValueAsync(string queryId, string instanceKey)
        {
            using var conn = CreateConnection();
            await conn.OpenAsync();

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
            using var conn = CreateConnection();
            await conn.OpenAsync();

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
            using var conn = CreateConnection();
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT json_data FROM cache_datatable
                WHERE query_id = @qid AND instance_key = @ikey";
            cmd.Parameters.AddWithValue("@qid", queryId);
            cmd.Parameters.AddWithValue("@ikey", instanceKey);

            var raw = (string?)await cmd.ExecuteScalarAsync();
            if (raw == null) return null;
            var json = UnprotectValue(raw);   // Decrypt from cache
            return DeserializeDataTable(json);
        }

        /// <summary>
        /// Reads cached check status data for a query.
        /// </summary>
        public async Task<List<CheckStatus>> GetCheckStatusAsync(string queryId, string instanceKey)
        {
            using var conn = CreateConnection();
            await conn.OpenAsync();

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
            using var conn = CreateConnection();
            await conn.OpenAsync();

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
            using var conn = CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()";
            var result = await cmd.ExecuteScalarAsync();
            return result != null ? Convert.ToInt64(result) : 0;
        }

        /// <summary>
        /// Enforces a maximum cache size by evicting oldest data if limit is exceeded.
        /// </summary>
        public async Task EnforceSizeLimitAsync(long maxSizeBytes)
        {
            try
            {
                var currentSize = await GetCacheSizeBytes();
                if (currentSize > maxSizeBytes)
                {
                    await EvictOlderThanAsync(TimeSpan.FromHours(48));
                    await RunMaintenanceAsync(includeIntegrityCheck: false);
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                InitializeSchema(); // Recover from missing tables
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
                using var conn = CreateConnection();
                await conn.OpenAsync();

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
            var tables = new[] { "cache_timeseries", "cache_stat", "cache_bargauge",
                                 "cache_datatable", "cache_checkstatus" };

            foreach (var table in tables)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {ValidateTableName(table)} WHERE fetched_at < @cutoff";
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
                await cmd.ExecuteNonQueryAsync();
            }

            // Also clean metadata for queries with no remaining data
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
                using var conn = CreateConnection();
                await conn.OpenAsync();

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
                using var conn = CreateConnection();
                await conn.OpenAsync();

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

        /// <summary>
        /// Clears all cache tables. Called when the user changes time range or instance.
        /// If the schema is missing (DB file was recreated externally), re-creates it automatically.
        /// </summary>
        public async Task InvalidateAllAsync()
        {
            await _globalWriteLock.WaitAsync();
            try
            {
                using var conn = CreateConnection();
                await conn.OpenAsync();

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
                            case string s:         writer.WriteStringValue(s); break;
                            case int iv:           writer.WriteNumberValue(iv); break;
                            case long lv:          writer.WriteNumberValue(lv); break;
                            case double dv:        writer.WriteNumberValue(dv); break;
                            case float fv:         writer.WriteNumberValue(fv); break;
                            case decimal decv:     writer.WriteNumberValue(decv); break;
                            case bool bv:          writer.WriteBooleanValue(bv); break;
                            case DateTime dtv:     writer.WriteStringValue(dtv.ToString("o")); break;
                            case DateTimeOffset dto: writer.WriteStringValue(dto.ToString("o")); break;
                            case Guid gv:          writer.WriteStringValue(gv.ToString()); break;
                            case byte[] bytes:     writer.WriteBase64StringValue(bytes); break;
                            default:               writer.WriteStringValue(val.ToString()); break;
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
                using var conn = CreateConnection();
                await conn.OpenAsync();

                // PRAGMA optimize — lets liveQueries update internal statistics
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA optimize;";
                    await cmd.ExecuteNonQueryAsync();
                }
                result.OptimizeCompleted = true;

                // VACUUM — rebuilds the database file, reclaiming free pages
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "VACUUM;";
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

        private SqliteConnection CreateConnection() => new(_connectionString);

        public void Dispose()
        {
            if (!_disposed)
            {
                _globalWriteLock.Dispose();
                foreach (var sem in _writeLocks.Values) sem.Dispose();
                _writeLocks.Clear();
                _disposed = true;
            }
        }
    }
}
