/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// F6 — client-configurable "Accepted Findings" baseline. Lets an operator record
    /// that a specific FAIL/WARN finding is accepted-by-design on a specific instance
    /// (reason required + who/when + optional expiry). Accepted findings are annotated
    /// on read (see <see cref="CheckExecutionService.GetResults"/>), downgraded to the
    /// Passed tier for score math (see <see cref="GovernanceService"/> CountsAsPass),
    /// and badged in the UI — NEVER silently hidden.
    ///
    /// Storage: SQLCipher-encrypted SQLite at Data/check-baselines.db (mirrors
    /// <see cref="ServerConfigBaselineService"/>). Acceptances are LOCAL client state,
    /// independent of the licensed bundle; they key on the stable v2 check_id so they
    /// survive corpus rebuilds and version upgrades.
    ///
    /// The read path (<see cref="IsAccepted"/> / <see cref="GetAcceptance"/>) is
    /// SYNCHRONOUS and hot — called once per not-passed result inside GetResults — so it
    /// serves from an in-memory snapshot refreshed on every write, never touching disk.
    /// </summary>
    public sealed class AcceptedFindingsService : IDisposable
    {
        private readonly ILogger<AcceptedFindingsService> _logger;
        private readonly AuditLogService? _audit;
        private readonly string _connectionString;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        // In-memory snapshot keyed by (server|check|db|obj) for O(1) synchronous reads.
        // Rebuilt from disk at init and after each Accept/Revoke/Prune. Values are the
        // full DTO so the badge tooltip (reason/who/when/expiry) needs no second lookup.
        private volatile Dictionary<string, AcceptedFinding> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        private Task? _pruneLoop;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        public AcceptedFindingsService(
            ILogger<AcceptedFindingsService> logger,
            AuditLogService? audit = null,
            string? dbPath = null)
        {
            _logger = logger;
            _audit = audit;

            var path = dbPath ?? Path.Combine(AppContext.BaseDirectory, "Data", "check-baselines.db");
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _connectionString = $"Data Source={path};Mode=ReadWriteCreate;Cache=Shared";
            InitializeSchema();
            ReloadCache();
        }

        // ── Schema ────────────────────────────────────────────────────────

        private void InitializeSchema()
        {
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                // db/obj scope columns store '' (NOT NULL DEFAULT '') for "instance-wide", NEVER NULL:
                // SQLite treats NULLs as DISTINCT in UNIQUE indexes, so with nullable columns the
                // ON CONFLICT upsert in Accept() would never fire for instance-wide rows and every
                // re-accept would silently INSERT a duplicate (2026-07-06 review finding).
                cmd.CommandText = @"
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;

                    CREATE TABLE IF NOT EXISTS accepted_findings (
                        id            INTEGER PRIMARY KEY AUTOINCREMENT,
                        server_name   TEXT NOT NULL,
                        check_id      TEXT NOT NULL,
                        database_name TEXT NOT NULL DEFAULT '',
                        object_name   TEXT NOT NULL DEFAULT '',
                        accepted_at   TEXT NOT NULL,
                        accepted_by   TEXT,
                        reason        TEXT NOT NULL,
                        expires_at    TEXT,
                        UNIQUE(server_name, check_id, database_name, object_name)
                    );
                    CREATE INDEX IF NOT EXISTS idx_accepted_server_check
                        ON accepted_findings(server_name, check_id);

                    -- Migration for DBs created before 2026-07-06 (nullable scope columns): collapse
                    -- NULL-scope duplicates (keep the newest row per logical key), then normalise
                    -- NULL -> ''. Idempotent: no-ops on a clean/new database. Dedup MUST run before
                    -- the UPDATEs or collapsing NULLs to '' could violate the UNIQUE constraint.
                    DELETE FROM accepted_findings WHERE id NOT IN (
                        SELECT MAX(id) FROM accepted_findings
                        GROUP BY server_name, check_id, IFNULL(database_name,''), IFNULL(object_name,''));
                    UPDATE accepted_findings SET database_name = '' WHERE database_name IS NULL;
                    UPDATE accepted_findings SET object_name   = '' WHERE object_name   IS NULL;
                ";
                cmd.ExecuteNonQuery();
                _logger.LogInformation("[ACCEPTED-FINDINGS] Schema initialised");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ACCEPTED-FINDINGS] Schema initialisation failed");
            }
        }

        // ── Keying ────────────────────────────────────────────────────────

        // NULL db/obj collapse to empty string so the UNIQUE key and the cache key agree.
        // Fields joined with unit-separator U+001F, written as an explicit cast: the original
        // version embedded RAW control bytes (SOH, 0x01) invisible to editors/diffs - 2026-07-06 review.
        private static string Key(string server, string checkId, string? db, string? obj) =>
            string.Join((char)0x1F, server, checkId, db ?? string.Empty, obj ?? string.Empty);

        // ── Read path (synchronous, hot) ──────────────────────────────────

        /// <summary>
        /// True when a non-expired acceptance matches. An instance-wide row (db/obj NULL)
        /// matches ANY database/object; a scoped row matches only its exact db/obj. Called
        /// once per not-passed result — serves from the in-memory snapshot.
        /// </summary>
        public bool IsAccepted(string serverName, string checkId, string? db = null, string? obj = null)
            => GetAcceptance(serverName, checkId, db, obj) != null;

        /// <summary>
        /// Returns the matching non-expired acceptance (for the badge tooltip), or null.
        /// Prefers the most specific match (exact db/obj) then falls back to instance-wide.
        /// </summary>
        public AcceptedFinding? GetAcceptance(string serverName, string checkId, string? db = null, string? obj = null)
        {
            if (string.IsNullOrEmpty(serverName) || string.IsNullOrEmpty(checkId))
                return null;

            var snapshot = _cache;

            // 1. Exact scope (Phase 2 keys). 2. Instance-wide fallback (MVP).
            if (db != null || obj != null)
            {
                if (snapshot.TryGetValue(Key(serverName, checkId, db, obj), out var exact) && !exact.IsExpired)
                    return exact;
            }
            if (snapshot.TryGetValue(Key(serverName, checkId, null, null), out var wide) && !wide.IsExpired)
                return wide;

            return null;
        }

        // ── Write path ────────────────────────────────────────────────────

        /// <summary>
        /// Records (or updates, via UNIQUE upsert) an acceptance. <paramref name="reason"/>
        /// is required — an acceptance without justification is worthless for audit.
        /// </summary>
        public async Task Accept(string serverName, string checkId, string reason,
            string? acceptedBy = null, string? db = null, string? obj = null,
            DateTime? expiresAt = null)
        {
            if (string.IsNullOrWhiteSpace(serverName)) throw new ArgumentException("Server name is required.", nameof(serverName));
            if (string.IsNullOrWhiteSpace(checkId)) throw new ArgumentException("Check id is required.", nameof(checkId));
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required to accept a finding.", nameof(reason));

            acceptedBy ??= Environment.UserName;
            var acceptedAt = DateTime.UtcNow;

            await _writeLock.WaitAsync();
            try
            {
                using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString);
                using var cmd = conn.CreateCommand();
                // Upsert on the UNIQUE scope key so re-accepting refreshes reason/expiry.
                // db/obj bind '' (never NULL): NULLs are DISTINCT in SQLite UNIQUE indexes, so a
                // NULL scope means ON CONFLICT never fires and every re-accept INSERTs a duplicate
                // row (2026-07-06 review finding).
                cmd.CommandText = @"
                    INSERT INTO accepted_findings
                        (server_name, check_id, database_name, object_name, accepted_at, accepted_by, reason, expires_at)
                    VALUES ($s, $c, $db, $obj, $at, $by, $reason, $exp)
                    ON CONFLICT(server_name, check_id, database_name, object_name)
                    DO UPDATE SET accepted_at=$at, accepted_by=$by, reason=$reason, expires_at=$exp;";
                cmd.Parameters.AddWithValue("$s", serverName);
                cmd.Parameters.AddWithValue("$c", checkId);
                cmd.Parameters.AddWithValue("$db", db ?? string.Empty);
                cmd.Parameters.AddWithValue("$obj", obj ?? string.Empty);
                cmd.Parameters.AddWithValue("$at", acceptedAt.ToString("o"));
                cmd.Parameters.AddWithValue("$by", (object?)acceptedBy ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$reason", reason);
                cmd.Parameters.AddWithValue("$exp", (object?)expiresAt?.ToString("o") ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                _writeLock.Release();
            }

            ReloadCache();
            _audit?.LogConfigurationChange("AcceptedFinding", "accepted",
                $"{checkId} on {serverName}" +
                (db != null ? $" (db={db})" : "") +
                $" by {acceptedBy}: {reason}");
            _logger.LogInformation("[ACCEPTED-FINDINGS] Accepted {Check} on {Server} by {By} (expires {Exp})",
                checkId, serverName, acceptedBy, expiresAt?.ToString("o") ?? "never");
        }

        /// <summary>Removes an acceptance (exact scope match). No-op if none exists.</summary>
        public async Task Revoke(string serverName, string checkId, string? db = null, string? obj = null)
        {
            int deleted;
            await _writeLock.WaitAsync();
            try
            {
                using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString);
                using var cmd = conn.CreateCommand();
                // COLLATE NOCASE on server/check: the read cache matches OrdinalIgnoreCase, so a
                // case-variant server name must not turn Revoke into a silent no-op while the
                // badge persists (2026-07-06 review finding). IFNULL guards pre-migration rows.
                cmd.CommandText = @"
                    DELETE FROM accepted_findings
                    WHERE server_name = $s COLLATE NOCASE
                      AND check_id    = $c COLLATE NOCASE
                      AND IFNULL(database_name,'') = $db
                      AND IFNULL(object_name,'')   = $obj;";
                cmd.Parameters.AddWithValue("$s", serverName);
                cmd.Parameters.AddWithValue("$c", checkId);
                cmd.Parameters.AddWithValue("$db", db ?? string.Empty);
                cmd.Parameters.AddWithValue("$obj", obj ?? string.Empty);
                deleted = await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                _writeLock.Release();
            }

            ReloadCache();
            // A revoke re-opens a finding — at least as audit-relevant as the accept.
            // Logged only when a row was actually removed (not on no-op revokes).
            if (deleted > 0)
                _audit?.LogConfigurationChange("AcceptedFinding", "revoked",
                    $"{checkId} on {serverName}" +
                    (db != null ? $" (db={db})" : "") +
                    $" by {Environment.UserName}");
            _logger.LogInformation("[ACCEPTED-FINDINGS] Revoked {Check} on {Server}", checkId, serverName);
        }

        /// <summary>All acceptances for a server (for the "Manage acceptances" view), newest first.</summary>
        public IReadOnlyList<AcceptedFinding> GetAcceptedFindings(string serverName)
            => _cache.Values
                .Where(a => a.ServerName.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.AcceptedAt)
                .ToList();

        /// <summary>Deletes expired rows and refreshes the cache. Called on a timer.</summary>
        public async Task PruneExpired()
        {
            int deleted;
            await _writeLock.WaitAsync();
            try
            {
                using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM accepted_findings WHERE expires_at IS NOT NULL AND expires_at <= $now;";
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                deleted = await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                _writeLock.Release();
            }

            if (deleted > 0)
            {
                ReloadCache();
                _logger.LogInformation("[ACCEPTED-FINDINGS] Pruned {Count} expired acceptance(s)", deleted);
            }
        }

        // ── Cache + prune loop ────────────────────────────────────────────

        private void ReloadCache()
        {
            var fresh = new Dictionary<string, AcceptedFinding>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT server_name, check_id, database_name, object_name,
                           accepted_at, accepted_by, reason, expires_at
                    FROM accepted_findings;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    // '' is the stored form of "instance-wide" (see InitializeSchema); the DTO
                    // keeps null for that so consumers see one representation, not two.
                    var dbName  = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var objName = reader.IsDBNull(3) ? null : reader.GetString(3);
                    var af = new AcceptedFinding
                    {
                        ServerName   = reader.GetString(0),
                        CheckId      = reader.GetString(1),
                        DatabaseName = string.IsNullOrEmpty(dbName) ? null : dbName,
                        ObjectName   = string.IsNullOrEmpty(objName) ? null : objName,
                        AcceptedAt   = DateTime.TryParse(reader.GetString(4), null,
                                          System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : DateTime.UtcNow,
                        AcceptedBy   = reader.IsDBNull(5) ? null : reader.GetString(5),
                        Reason       = reader.GetString(6),
                        ExpiresAt    = reader.IsDBNull(7) ? null :
                                          (DateTime.TryParse(reader.GetString(7), null,
                                              System.Globalization.DateTimeStyles.RoundtripKind, out var ex) ? ex : (DateTime?)null),
                    };
                    fresh[Key(af.ServerName, af.CheckId, af.DatabaseName, af.ObjectName)] = af;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ACCEPTED-FINDINGS] Cache reload failed; keeping previous snapshot");
                return; // keep the last-good snapshot rather than blanking acceptances
            }
            _cache = fresh;
        }

        /// <summary>Start the periodic expiry-prune loop. Idempotent. Called from App startup.</summary>
        public void Start()
        {
            if (_pruneLoop != null) return;
            _pruneLoop = Task.Run(() => RunPruneLoopAsync(_cts.Token));
            _logger.LogInformation("[ACCEPTED-FINDINGS] Prune loop started (hourly)");
        }

        /// <summary>Stop the prune loop. Called from App shutdown.</summary>
        public void Stop()
        {
            try { _cts.Cancel(); } catch { }
        }

        private async Task RunPruneLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    try { await PruneExpired(); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _logger.LogWarning(ex, "[ACCEPTED-FINDINGS] Prune cycle failed"); }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
            _writeLock.Dispose();
        }
    }
}
