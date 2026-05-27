/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using SQLTriage.Data;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Captures and diffs sp_configure + surface-area settings for monitored SQL Servers.
    /// This is intentionally separate from <see cref="ConfigBaselineService"/>, which tracks
    /// SQLTriage's own appsettings drift (CM-3). This service answers: "what changed on the
    /// monitored SQL Server between patch windows?"
    ///
    /// Storage: SQLCipher-encrypted SQLite at Data/server-config-baselines.db.
    /// Retention: ConfigBaseline:RetentionMonths (default 12); always keeps the most-recent
    /// baseline per server so there is always a reference point.
    /// </summary>
    public sealed class ServerConfigBaselineService : IDisposable
    {
        private readonly ILogger<ServerConfigBaselineService> _logger;
        private readonly AuditLogService? _audit;
        private readonly ServerConnectionManager? _connectionManager;
        private readonly string _connectionString;
        private readonly int _retentionMonths;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private bool _disposed;

        // ── Constructor ───────────────────────────────────────────────────

        public ServerConfigBaselineService(
            ILogger<ServerConfigBaselineService> logger,
            ServerConnectionManager? connectionManager = null,
            AuditLogService? audit = null,
            IConfiguration? configuration = null,
            string? dbPath = null)
        {
            _logger = logger;
            _connectionManager = connectionManager;
            _audit = audit;
            _retentionMonths = configuration?.GetValue<int>("ConfigBaseline:RetentionMonths", 12) ?? 12;

            var path = dbPath ?? Path.Combine(AppContext.BaseDirectory, "Data", "server-config-baselines.db");
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _connectionString = $"Data Source={path};Mode=ReadWriteCreate;Cache=Shared";
            InitializeSchema();
        }

        // ── Schema ────────────────────────────────────────────────────────

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

                    CREATE TABLE IF NOT EXISTS server_config_baselines (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        server_name     TEXT NOT NULL,
                        captured_utc    TEXT NOT NULL,
                        captured_by     TEXT NOT NULL,
                        label           TEXT NOT NULL,
                        config_json     TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_baseline_server_time
                        ON server_config_baselines(server_name, captured_utc DESC);

                    CREATE TABLE IF NOT EXISTS server_surface_area_baselines (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        server_name     TEXT NOT NULL,
                        captured_utc    TEXT NOT NULL,
                        captured_by     TEXT NOT NULL,
                        label           TEXT NOT NULL,
                        surface_area_json TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_sa_baseline_server_time
                        ON server_surface_area_baselines(server_name, captured_utc DESC);
                ";
                cmd.ExecuteNonQuery();
                _logger.LogInformation("[SERVER-CONFIG-BASELINE] Schema initialised");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SERVER-CONFIG-BASELINE] Schema initialisation failed");
            }
        }

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Queries the live server and persists the result as a named baseline.
        /// Queries are READ-ONLY (sys.configurations + surface area DMVs).
        /// Emits <see cref="AuditEventType.ServerConfigBaselineCaptured"/>.
        /// </summary>
        public async Task<int> CaptureBaselineAsync(
            string serverName,
            string label,
            string? capturedBy = null,
            CancellationToken ct = default)
        {
            capturedBy ??= Environment.UserName;

            var (spConfig, surfaceArea) = await FetchLiveConfigAsync(serverName, ct);

            var configJson = JsonSerializer.Serialize(spConfig);
            var surfaceAreaJson = JsonSerializer.Serialize(surfaceArea);
            var capturedUtc = DateTime.UtcNow.ToString("o");
            int newId;

            await _writeLock.WaitAsync(ct);
            try
            {
                using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString);
                using var tx = conn.BeginTransaction();

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO server_config_baselines (server_name, captured_utc, captured_by, label, config_json)
                    VALUES ($s, $t, $u, $l, $j);
                    SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$s", serverName);
                cmd.Parameters.AddWithValue("$t", capturedUtc);
                cmd.Parameters.AddWithValue("$u", capturedBy);
                cmd.Parameters.AddWithValue("$l", label);
                cmd.Parameters.AddWithValue("$j", configJson);
                newId = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));

                using var saCmd = conn.CreateCommand();
                saCmd.Transaction = tx;
                saCmd.CommandText = @"
                    INSERT INTO server_surface_area_baselines (server_name, captured_utc, captured_by, label, surface_area_json)
                    VALUES ($s, $t, $u, $l, $j)";
                saCmd.Parameters.AddWithValue("$s", serverName);
                saCmd.Parameters.AddWithValue("$t", capturedUtc);
                saCmd.Parameters.AddWithValue("$u", capturedBy);
                saCmd.Parameters.AddWithValue("$l", label);
                saCmd.Parameters.AddWithValue("$j", surfaceAreaJson);
                await saCmd.ExecuteNonQueryAsync(ct);

                tx.Commit();
            }
            finally
            {
                _writeLock.Release();
            }

            _logger.LogInformation("[SERVER-CONFIG-BASELINE] Captured baseline '{Label}' for {Server} by {User} (id={Id})",
                label, serverName, capturedBy, newId);

            _audit?.LogServerConfigBaselineCaptured(serverName, label, capturedBy, spConfig.Count + surfaceArea.Count);

            _ = Task.Run(() => PurgeOldBaselines(serverName), CancellationToken.None);
            return newId;
        }

        /// <summary>
        /// Returns metadata for all baselines saved for a server, newest first.
        /// </summary>
        public async Task<List<BaselineInfo>> GetBaselinesAsync(string serverName)
        {
            var results = new List<BaselineInfo>();
            try
            {
                using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT id, captured_utc, captured_by, label
                    FROM server_config_baselines
                    WHERE server_name = $s
                    ORDER BY captured_utc DESC";
                cmd.Parameters.AddWithValue("$s", serverName);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    results.Add(new BaselineInfo(
                        Id: reader.GetInt32(0),
                        CapturedUtc: reader.GetString(1),
                        CapturedBy: reader.GetString(2),
                        Label: reader.GetString(3)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SERVER-CONFIG-BASELINE] GetBaselinesAsync failed for {Server}", serverName);
            }
            return results;
        }

        /// <summary>
        /// Loads the sp_configure dictionary from a stored baseline.
        /// Returns null if the id does not exist.
        /// </summary>
        public async Task<Dictionary<string, ConfigEntry>?> LoadBaselineAsync(int id)
        {
            try
            {
                using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT config_json FROM server_config_baselines WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);

                var raw = await cmd.ExecuteScalarAsync();
                if (raw is string json)
                    return JsonSerializer.Deserialize<Dictionary<string, ConfigEntry>>(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SERVER-CONFIG-BASELINE] LoadBaselineAsync failed for id={Id}", id);
            }
            return null;
        }

        /// <summary>
        /// Loads the surface-area dictionary from a stored baseline.
        /// Returns null if the id does not exist.
        /// </summary>
        public async Task<Dictionary<string, ConfigEntry>?> LoadSurfaceAreaBaselineAsync(int id)
        {
            try
            {
                using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT surface_area_json FROM server_surface_area_baselines WHERE server_name = (SELECT server_name FROM server_config_baselines WHERE id = $id) AND captured_utc = (SELECT captured_utc FROM server_config_baselines WHERE id = $id) LIMIT 1";
                cmd.Parameters.AddWithValue("$id", id);

                var raw = await cmd.ExecuteScalarAsync();
                if (raw is string json)
                    return JsonSerializer.Deserialize<Dictionary<string, ConfigEntry>>(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SERVER-CONFIG-BASELINE] LoadSurfaceAreaBaselineAsync failed for id={Id}", id);
            }
            return null;
        }

        /// <summary>
        /// Queries the live server (read-only) and returns the current sp_configure +
        /// surface-area dictionaries WITHOUT persisting anything.
        /// </summary>
        public async Task<(Dictionary<string, ConfigEntry> SpConfig, Dictionary<string, ConfigEntry> SurfaceArea)>
            GetCurrentConfigAsync(
                string serverName,
                CancellationToken ct = default)
        {
            return await FetchLiveConfigAsync(serverName, ct);
        }

        /// <summary>
        /// Computes a three-bucket diff between a stored baseline dict and a current dict.
        /// Key = config name. Both dicts must use the same schema (sp_configure or surface area).
        /// </summary>
        public static ConfigDiffResult DiffConfig(
            Dictionary<string, ConfigEntry> baseline,
            Dictionary<string, ConfigEntry> current)
        {
            var added = new List<(string Key, ConfigEntry Entry)>();
            var removed = new List<(string Key, ConfigEntry Entry)>();
            var modified = new List<(string Key, ConfigEntry Baseline, ConfigEntry Current)>();

            foreach (var kvp in current)
            {
                if (!baseline.TryGetValue(kvp.Key, out var baseEntry))
                    added.Add((kvp.Key, kvp.Value));
                else if (baseEntry.Value != kvp.Value.Value || baseEntry.ValueInUse != kvp.Value.ValueInUse)
                    modified.Add((kvp.Key, baseEntry, kvp.Value));
            }

            foreach (var kvp in baseline)
            {
                if (!current.ContainsKey(kvp.Key))
                    removed.Add((kvp.Key, kvp.Value));
            }

            return new ConfigDiffResult(
                Added: added,
                Removed: removed,
                Modified: modified);
        }

        // ── Internal helpers ──────────────────────────────────────────────

        private async Task<(Dictionary<string, ConfigEntry> SpConfig, Dictionary<string, ConfigEntry> SurfaceArea)>
            FetchLiveConfigAsync(
                string serverName,
                CancellationToken ct)
        {
            var spConfig = new Dictionary<string, ConfigEntry>(StringComparer.OrdinalIgnoreCase);
            var surfaceArea = new Dictionary<string, ConfigEntry>(StringComparer.OrdinalIgnoreCase);

            // Resolve connection string from ServerConnectionManager.
            // Use "master" per project convention (non-SQLWATCH servers).
            var serverConn = _connectionManager?.GetConnections()
                .FirstOrDefault(c => c.GetServerList().Contains(serverName, StringComparer.OrdinalIgnoreCase));

            string connStr;
            if (serverConn != null)
                connStr = serverConn.GetConnectionString(serverName, "master");
            else
                connStr = $"Server={serverName};Database=master;Integrated Security=true;TrustServerCertificate=true;";

            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);

            // sp_configure — temporarily enable 'show advanced options' so all rows are visible.
            // NOTE: This is a deliberate side-effect during a read-only capture.  We restore the
            //       prior value in the finally block even on exception.  If the caller lacks
            //       sysadmin/serveradmin, the EXEC will fail; we swallow that error and proceed
            //       with whatever rows are already visible.
            int priorShowAdvanced = 0;
            bool toggledAdvanced = false;
            try
            {
                using (var cmdCheck = new SqlCommand(
                    "SELECT CAST(value AS int) FROM sys.configurations WHERE name = 'show advanced options';", conn))
                {
                    var raw = await cmdCheck.ExecuteScalarAsync(ct);
                    priorShowAdvanced = raw is int i ? i : Convert.ToInt32(raw);
                }

                if (priorShowAdvanced == 0)
                {
                    using (var cmdEnable = new SqlCommand(
                        "EXEC sp_configure 'show advanced options', 1; RECONFIGURE;", conn))
                        await cmdEnable.ExecuteNonQueryAsync(ct);
                    toggledAdvanced = true;
                    Log.Information("[CONFIG-BASELINE] Temporarily enabled show advanced options for full capture; will restore to {Prior}", priorShowAdvanced);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[CONFIG-BASELINE] Could not enable show advanced options (likely insufficient permission); proceeding with visible rows only.");
            }

            try
            {
                // sp_configure (read-only sys view)
                using (var cmd = new SqlCommand("SELECT name, value, value_in_use, description FROM sys.configurations ORDER BY name;", conn))
                {
                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var name = reader.GetString(0);
                        spConfig[name] = new ConfigEntry(
                            Value: reader.IsDBNull(1) ? string.Empty : reader.GetValue(1).ToString() ?? string.Empty,
                            ValueInUse: reader.IsDBNull(2) ? string.Empty : reader.GetValue(2).ToString() ?? string.Empty,
                            Description: reader.IsDBNull(3) ? string.Empty : reader.GetString(3));
                    }
                }
            }
            finally
            {
                if (toggledAdvanced)
                {
                    try
                    {
                        using var cmdRestore = new SqlCommand(
                            $"EXEC sp_configure 'show advanced options', {priorShowAdvanced}; RECONFIGURE;", conn);
                        await cmdRestore.ExecuteNonQueryAsync(ct);
                        Log.Information("[CONFIG-BASELINE] Temporarily enabled show advanced options for full capture; restored to {Prior}", priorShowAdvanced);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[CONFIG-BASELINE] Could not restore show advanced options to {Prior}.", priorShowAdvanced);
                    }
                }
            }

            // Surface area — xp_cmdshell, OLE Automation, Ad Hoc Distributed Queries, CLR
            // These are all readable via sys.configurations as well; we key them separately
            // so the UI can render them in their own panel.
            var surfaceAreaKeys = new[]
            {
                "xp_cmdshell",
                "Ole Automation Procedures",
                "Ad Hoc Distributed Queries",
                "clr enabled",
                "clr strict security",
                "remote access",
                "remote admin connections",
                "show advanced options",
                "Database Mail XPs",
                "SMO and DMO XPs"
            };

            foreach (var key in surfaceAreaKeys)
            {
                if (spConfig.TryGetValue(key, out var entry))
                    surfaceArea[key] = entry;
            }

            _logger.LogDebug("[SERVER-CONFIG-BASELINE] Fetched {SpCount} sp_configure rows, {SaCount} surface-area rows from {Server}",
                spConfig.Count, surfaceArea.Count, serverName);

            return (spConfig, surfaceArea);
        }

        private void PurgeOldBaselines(string serverName)
        {
            try
            {
                _writeLock.Wait();
                try
                {
                    using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);

                    // Find id of the most recent baseline for this server — always keep it
                    using var keepCmd = conn.CreateCommand();
                    keepCmd.CommandText = @"
                        SELECT id FROM server_config_baselines
                        WHERE server_name = $s
                        ORDER BY captured_utc DESC
                        LIMIT 1";
                    keepCmd.Parameters.AddWithValue("$s", serverName);
                    var keepId = Convert.ToInt32(keepCmd.ExecuteScalar() ?? -1);

                    var cutoff = DateTime.UtcNow.AddMonths(-_retentionMonths).ToString("o");

                    using var purgeCmd = conn.CreateCommand();
                    purgeCmd.CommandText = @"
                        DELETE FROM server_config_baselines
                        WHERE server_name = $s
                          AND captured_utc < $cutoff
                          AND id <> $keepId;
                        DELETE FROM server_surface_area_baselines
                        WHERE server_name = $s
                          AND captured_utc < $cutoff";
                    purgeCmd.Parameters.AddWithValue("$s", serverName);
                    purgeCmd.Parameters.AddWithValue("$cutoff", cutoff);
                    purgeCmd.Parameters.AddWithValue("$keepId", keepId);
                    var deleted = purgeCmd.ExecuteNonQuery();

                    if (deleted > 0)
                        _logger.LogInformation("[SERVER-CONFIG-BASELINE] Purged {Count} expired baselines for {Server}", deleted, serverName);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SERVER-CONFIG-BASELINE] Purge failed for {Server}", serverName);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writeLock.Dispose();
        }
    }

    // ── DTOs ──────────────────────────────────────────────────────────────

    /// <summary>A single row from sys.configurations.</summary>
    public sealed record ConfigEntry(string Value, string ValueInUse, string Description);

    /// <summary>Metadata returned by <see cref="ServerConfigBaselineService.GetBaselinesAsync"/>.</summary>
    public sealed record BaselineInfo(int Id, string CapturedUtc, string CapturedBy, string Label);

    /// <summary>
    /// Three-bucket diff result from <see cref="ServerConfigBaselineService.DiffConfig"/>.
    /// </summary>
    public sealed record ConfigDiffResult(
        List<(string Key, ConfigEntry Entry)> Added,
        List<(string Key, ConfigEntry Entry)> Removed,
        List<(string Key, ConfigEntry Baseline, ConfigEntry Current)> Modified)
    {
        public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Modified.Count > 0;
    }
}
