/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// #88 — the LOG branch of the accept-or-log decision. Where
    /// <see cref="AcceptedFindingsService"/> records "this finding is acceptable-by-design"
    /// (ACCEPT), this service records "we're not accepting this — we're CHANGING it, tracked
    /// as change item #N, optionally handed to the vendor with a due date" (LOG). Together
    /// they make the change-control loop closable in-app rather than in a spreadsheet.
    ///
    /// Storage mirrors <see cref="AcceptedFindingsService"/> exactly: SQLCipher-encrypted
    /// SQLite (Data/change-items.db), a required <c>rationale</c> (a logged change without
    /// justification is worthless for audit), created_by/at, and every state transition
    /// written to the HMAC-chained <see cref="AuditLogService"/> — a status never changes
    /// silently. Items key on the stable v2 check_id so they survive corpus rebuilds.
    ///
    /// Reads serve from an in-memory snapshot refreshed on every write: the ledger page reads
    /// the full list, and the audit grid's per-row "already logged?" badge reads the
    /// latest-per-(server,check) index synchronously (never touching disk in the render loop).
    /// </summary>
    public sealed class ChangeItemService : IDisposable
    {
        private readonly ILogger<ChangeItemService> _logger;
        private readonly AuditLogService? _audit;
        private readonly string _connectionString;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        // Full snapshot (newest first) for the ledger page, rebuilt from disk on every write.
        private volatile IReadOnlyList<ChangeItem> _all = Array.Empty<ChangeItem>();

        // Latest item per (server|check) for the O(1) synchronous "already logged?" badge.
        private volatile Dictionary<string, ChangeItem> _latestByKey =
            new(StringComparer.OrdinalIgnoreCase);

        private bool _disposed;

        public ChangeItemService(
            ILogger<ChangeItemService> logger,
            AuditLogService? audit = null,
            string? dbPath = null)
        {
            _logger = logger;
            _audit = audit;

            var path = dbPath ?? Path.Combine(AppContext.BaseDirectory, "Data", "change-items.db");
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
                cmd.CommandText = @"
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;

                    CREATE TABLE IF NOT EXISTS change_items (
                        id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                        server_name        TEXT NOT NULL,
                        check_id           TEXT NOT NULL,
                        check_name         TEXT NOT NULL DEFAULT '',
                        rationale          TEXT NOT NULL,
                        remediation_script TEXT,
                        status             TEXT NOT NULL,
                        decided_at         TEXT NOT NULL,
                        decided_by         TEXT,
                        handed_at          TEXT,
                        handed_to          TEXT,
                        due_at             TEXT,
                        resolved_at        TEXT,
                        resolved_by        TEXT,
                        resolution_note    TEXT,
                        updated_at         TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_change_items_server_check
                        ON change_items(server_name, check_id);
                    CREATE INDEX IF NOT EXISTS idx_change_items_status
                        ON change_items(status);
                ";
                cmd.ExecuteNonQuery();
                _logger.LogInformation("[CHANGE-ITEMS] Schema initialised");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CHANGE-ITEMS] Schema initialisation failed");
            }
        }

        // ── Keying ────────────────────────────────────────────────────────

        // Fields joined with the unit-separator U+001F (same convention as
        // AcceptedFindingsService.Key), so the cache key can never collide with a literal
        // name that contains a separator character an editor would show.
        private static string Key(string server, string checkId) =>
            string.Join((char)0x1F, server, checkId);

        // ── Read path (synchronous, hot) ──────────────────────────────────

        /// <summary>
        /// The most recent change item for a (server, check) pair, or null. Serves the audit
        /// grid's per-row badge from the in-memory snapshot — safe to call in the render loop.
        /// </summary>
        public ChangeItem? GetLatest(string serverName, string checkId)
        {
            if (string.IsNullOrEmpty(serverName) || string.IsNullOrEmpty(checkId))
                return null;
            return _latestByKey.TryGetValue(Key(serverName, checkId), out var item) ? item : null;
        }

        /// <summary>All change items, newest first (for the ledger page). Immutable snapshot.</summary>
        public IReadOnlyList<ChangeItem> GetAll() => _all;

        /// <summary>Single item by id (from the snapshot), or null.</summary>
        public ChangeItem? GetById(long id) => _all.FirstOrDefault(c => c.Id == id);

        // ── Write path ────────────────────────────────────────────────────

        /// <summary>
        /// Logs a new change item (status <see cref="ChangeItemStatus.Logged"/>).
        /// <paramref name="rationale"/> is required — an untracked reason defeats the audit story.
        /// Returns the created item (with its assigned id).
        /// </summary>
        public async Task<ChangeItem> LogChange(
            string serverName, string checkId, string checkName, string rationale,
            string? remediationScript = null, string? decidedBy = null)
        {
            if (string.IsNullOrWhiteSpace(serverName)) throw new ArgumentException("Server name is required.", nameof(serverName));
            if (string.IsNullOrWhiteSpace(checkId)) throw new ArgumentException("Check id is required.", nameof(checkId));
            if (string.IsNullOrWhiteSpace(rationale)) throw new ArgumentException("A rationale is required to log a change item.", nameof(rationale));

            decidedBy ??= Environment.UserName;
            var now = DateTime.UtcNow;
            long id;

            await _writeLock.WaitAsync();
            try
            {
                using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO change_items
                        (server_name, check_id, check_name, rationale, remediation_script,
                         status, decided_at, decided_by, updated_at)
                    VALUES ($s, $c, $name, $reason, $script, $status, $at, $by, $at);
                    SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$s", serverName);
                cmd.Parameters.AddWithValue("$c", checkId);
                cmd.Parameters.AddWithValue("$name", checkName ?? string.Empty);
                cmd.Parameters.AddWithValue("$reason", rationale);
                cmd.Parameters.AddWithValue("$script", (object?)remediationScript ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$status", ChangeItemStatus.Logged.ToString());
                cmd.Parameters.AddWithValue("$at", now.ToString("o"));
                cmd.Parameters.AddWithValue("$by", (object?)decidedBy ?? DBNull.Value);
                id = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }
            finally
            {
                _writeLock.Release();
            }

            ReloadCache();
            _audit?.LogConfigurationChange("ChangeItem", "logged",
                $"#{id} {checkId} on {serverName} by {decidedBy}: {rationale}");
            _logger.LogInformation("[CHANGE-ITEMS] Logged #{Id} {Check} on {Server} by {By}",
                id, checkId, serverName, decidedBy);

            return GetById(id) ?? new ChangeItem
            {
                Id = id, ServerName = serverName, CheckId = checkId, CheckName = checkName ?? string.Empty,
                Rationale = rationale, RemediationScript = remediationScript, Status = ChangeItemStatus.Logged,
                DecidedAt = now, DecidedBy = decidedBy, UpdatedAt = now,
            };
        }

        /// <summary>
        /// Transitions an item to <see cref="ChangeItemStatus.HandedToVendor"/>, recording the
        /// handed date, the vendor, and an optional due date. Valid from Logged or StillFailing
        /// (re-handing a still-failing change is legitimate).
        /// </summary>
        public async Task HandToVendor(long id, string? handedTo, DateTime? dueAt, string? by = null)
        {
            by ??= Environment.UserName;
            var now = DateTime.UtcNow;
            int updated;

            await _writeLock.WaitAsync();
            try
            {
                using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString);
                using var cmd = conn.CreateCommand();
                // Guard the transition in SQL so a stale UI cannot hand off an already-fixed item.
                cmd.CommandText = @"
                    UPDATE change_items
                       SET status = $status, handed_at = $at, handed_to = $to, due_at = $due,
                           resolved_at = NULL, resolved_by = NULL, resolution_note = NULL,
                           updated_at = $at
                     WHERE id = $id AND status IN ($logged, $stillFailing);";
                cmd.Parameters.AddWithValue("$status", ChangeItemStatus.HandedToVendor.ToString());
                cmd.Parameters.AddWithValue("$at", now.ToString("o"));
                cmd.Parameters.AddWithValue("$to", (object?)handedTo ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$due", (object?)dueAt?.ToString("o") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$logged", ChangeItemStatus.Logged.ToString());
                cmd.Parameters.AddWithValue("$stillFailing", ChangeItemStatus.StillFailing.ToString());
                updated = await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                _writeLock.Release();
            }

            if (updated == 0)
                throw new InvalidOperationException($"Change item #{id} cannot be handed to a vendor from its current state.");

            ReloadCache();
            _audit?.LogConfigurationChange("ChangeItem", "handed-to-vendor",
                $"#{id}" + (handedTo != null ? $" to {handedTo}" : "") +
                (dueAt.HasValue ? $" (due {dueAt.Value:yyyy-MM-dd})" : "") + $" by {by}");
            _logger.LogInformation("[CHANGE-ITEMS] Handed #{Id} to {Vendor} (due {Due})",
                id, handedTo ?? "(unnamed)", dueAt?.ToString("yyyy-MM-dd") ?? "no deadline");
        }

        /// <summary>
        /// Transitions an item to <see cref="ChangeItemStatus.ConfirmedFixed"/> — a human
        /// attestation that the change was made and the check now passes. Valid from
        /// HandedToVendor or StillFailing.
        /// </summary>
        public async Task ConfirmFixed(long id, string? note = null, string? by = null)
        {
            by ??= Environment.UserName;
            await ResolveTo(id, ChangeItemStatus.ConfirmedFixed, note, by,
                fromStates: new[] { ChangeItemStatus.HandedToVendor, ChangeItemStatus.StillFailing });
            _audit?.LogConfigurationChange("ChangeItem", "confirmed-fixed",
                $"#{id} by {by}" + (string.IsNullOrWhiteSpace(note) ? "" : $": {note}"));
            _logger.LogInformation("[CHANGE-ITEMS] Confirmed fixed #{Id} by {By}", id, by);
        }

        /// <summary>
        /// Transitions an item to <see cref="ChangeItemStatus.StillFailing"/>. Valid from
        /// HandedToVendor. Called manually from the ledger, or automatically by the follow-up
        /// sweep (<paramref name="automated"/> = true, attributed to "system").
        /// </summary>
        public async Task MarkStillFailing(long id, string? note = null, string? by = null, bool automated = false)
        {
            by = automated ? "system" : (by ?? Environment.UserName);
            await ResolveTo(id, ChangeItemStatus.StillFailing, note, by,
                fromStates: new[] { ChangeItemStatus.HandedToVendor });
            _audit?.LogConfigurationChange("ChangeItem", "still-failing",
                $"#{id} by {by}" + (automated ? " (auto: past due, check still fails)" : "") +
                (string.IsNullOrWhiteSpace(note) ? "" : $": {note}"));
            _logger.LogInformation("[CHANGE-ITEMS] Marked #{Id} still-failing (automated={Auto})", id, automated);
        }

        // Shared terminal-transition writer. Throws if the item is not in one of fromStates.
        private async Task ResolveTo(long id, ChangeItemStatus target, string? note, string? by,
            ChangeItemStatus[] fromStates)
        {
            var now = DateTime.UtcNow;
            int updated;

            await _writeLock.WaitAsync();
            try
            {
                using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString);
                using var cmd = conn.CreateCommand();
                var placeholders = string.Join(",", fromStates.Select((_, i) => "$from" + i));
                cmd.CommandText = $@"
                    UPDATE change_items
                       SET status = $status, resolved_at = $at, resolved_by = $by,
                           resolution_note = $note, updated_at = $at
                     WHERE id = $id AND status IN ({placeholders});";
                cmd.Parameters.AddWithValue("$status", target.ToString());
                cmd.Parameters.AddWithValue("$at", now.ToString("o"));
                cmd.Parameters.AddWithValue("$by", (object?)by ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$id", id);
                for (int i = 0; i < fromStates.Length; i++)
                    cmd.Parameters.AddWithValue("$from" + i, fromStates[i].ToString());
                updated = await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                _writeLock.Release();
            }

            if (updated == 0)
                throw new InvalidOperationException($"Change item #{id} cannot transition to {target} from its current state.");

            ReloadCache();
        }

        // ── Follow-up sweep ───────────────────────────────────────────────

        /// <summary>
        /// #88 follow-up flag: when new scan results land, any item HandedToVendor whose check
        /// STILL FAILS and whose due date has passed is auto-flagged
        /// <see cref="ChangeItemStatus.StillFailing"/>. Wired into the scan-completion seam
        /// (<see cref="ScheduledTaskEngine.AssessmentRunCompleted"/>). Items with no due date,
        /// or whose check now passes / is accepted / is absent from the results, are left as-is
        /// (a passing check is confirmed by a human, never auto-closed — the honest default).
        /// Returns the number of items flagged.
        /// </summary>
        public async Task<int> EvaluateFollowUpsAsync(string serverName, IEnumerable<CheckResult> latestResults)
        {
            if (string.IsNullOrWhiteSpace(serverName) || latestResults == null) return 0;

            // Fast index: check_id -> the newest run's bucket for this server.
            //
            // 2026-07-20 sweep. This read raw `!r.Passed`, and WARN/SKIP/INFO all ride Passed=true.
            // So an overdue vendor item whose check degraded FAIL → WARN scored failing=false and
            // ESCAPED the sweep entirely: the deadline passed, the remediation was never confirmed,
            // and nothing escalated — the item simply aged in HandedToVendor looking handled. That
            // is the same false-clean as the rest of this seam, on the workflow that exists to
            // catch a vendor who did not deliver.
            //
            // Three buckets, because two are not enough here:
            //   confirmed-failing → escalate, and say it still fails (we exercised it).
            //   unassessable      → escalate, but say only that it could NOT BE CONFIRMED. Claiming
            //                       "still failing" would assert a failure nobody observed.
            //   passing           → leave alone (a passing check is confirmed by a human, never
            //                       auto-closed — the pre-existing honest default).
            var stillFails   = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var unassessable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in latestResults)
            {
                if (string.IsNullOrEmpty(r.CheckId)) continue;
                var assessable = CheckClassification.IsScorable(r) && !r.IsCorrupted
                                 && string.IsNullOrEmpty(r.ErrorMessage);
                var failing = assessable && !r.Passed && !r.IsAccepted;
                var blind   = !assessable;
                // If a check appears more than once, any failing instance makes it failing, and any
                // blind instance makes it blind — neither signal may be masked by a sibling row.
                stillFails[r.CheckId]   = stillFails.TryGetValue(r.CheckId, out var pf) ? (pf || failing) : failing;
                unassessable[r.CheckId] = unassessable.TryGetValue(r.CheckId, out var pb) ? (pb || blind) : blind;
            }

            var now = DateTime.UtcNow;
            var overdue = _all
                .Where(c => c.Status == ChangeItemStatus.HandedToVendor
                            && c.ServerName.Equals(serverName, StringComparison.OrdinalIgnoreCase)
                            && c.DueAt.HasValue && c.DueAt.Value <= now)
                .ToList();

            var candidates = overdue
                .Where(c => (stillFails.TryGetValue(c.CheckId, out var f) && f)
                            || (unassessable.TryGetValue(c.CheckId, out var b) && b))
                .Select(c => (c.Id, Confirmed: stillFails.TryGetValue(c.CheckId, out var cf) && cf))
                .ToList();

            int flagged = 0;
            foreach (var (id, confirmed) in candidates)
            {
                try
                {
                    await MarkStillFailing(id, note: confirmed
                            ? "Auto-flagged: check still failing after the due date."
                            : "Auto-flagged: the due date passed and this check could NOT be fully assessed "
                              + "in the latest run, so the remediation is UNCONFIRMED. This is not a "
                              + "confirmed failure — re-run with an account that can see the whole instance.",
                        automated: true);
                    flagged++;
                }
                catch (InvalidOperationException)
                {
                    // Raced with a manual transition (e.g. someone confirmed it fixed) — skip.
                }
            }

            if (flagged > 0)
                _logger.LogInformation("[CHANGE-ITEMS] Follow-up sweep flagged {Count} overdue item(s) still-failing on {Server}",
                    flagged, serverName);
            return flagged;
        }

        // ── Cache ─────────────────────────────────────────────────────────

        private void ReloadCache()
        {
            var all = new List<ChangeItem>();
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT id, server_name, check_id, check_name, rationale, remediation_script,
                           status, decided_at, decided_by, handed_at, handed_to, due_at,
                           resolved_at, resolved_by, resolution_note, updated_at
                    FROM change_items
                    ORDER BY decided_at DESC, id DESC;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    all.Add(new ChangeItem
                    {
                        Id                = reader.GetInt64(0),
                        ServerName        = reader.GetString(1),
                        CheckId           = reader.GetString(2),
                        CheckName         = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                        Rationale         = reader.GetString(4),
                        RemediationScript = reader.IsDBNull(5) ? null : reader.GetString(5),
                        Status            = ParseStatus(reader.GetString(6)),
                        DecidedAt         = ParseUtc(reader.GetString(7)) ?? DateTime.UtcNow,
                        DecidedBy         = reader.IsDBNull(8) ? null : reader.GetString(8),
                        HandedAt          = reader.IsDBNull(9) ? null : ParseUtc(reader.GetString(9)),
                        HandedTo          = reader.IsDBNull(10) ? null : reader.GetString(10),
                        DueAt             = reader.IsDBNull(11) ? null : ParseUtc(reader.GetString(11)),
                        ResolvedAt        = reader.IsDBNull(12) ? null : ParseUtc(reader.GetString(12)),
                        ResolvedBy        = reader.IsDBNull(13) ? null : reader.GetString(13),
                        ResolutionNote    = reader.IsDBNull(14) ? null : reader.GetString(14),
                        UpdatedAt         = ParseUtc(reader.GetString(15)) ?? DateTime.UtcNow,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CHANGE-ITEMS] Cache reload failed; keeping previous snapshot");
                return; // keep the last-good snapshot rather than blanking the ledger
            }

            // Latest-per-(server,check): the list is already newest-first, so the first row seen
            // for a key is the most recent.
            var latest = new Dictionary<string, ChangeItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in all)
            {
                var key = Key(item.ServerName, item.CheckId);
                if (!latest.ContainsKey(key)) latest[key] = item;
            }

            _all = all;
            _latestByKey = latest;
        }

        private static ChangeItemStatus ParseStatus(string raw) =>
            Enum.TryParse<ChangeItemStatus>(raw, out var s) ? s : ChangeItemStatus.Logged;

        private static DateTime? ParseUtc(string raw) =>
            DateTime.TryParse(raw, null, DateTimeStyles.RoundtripKind, out var dt) ? dt : (DateTime?)null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writeLock.Dispose();
        }
    }
}
