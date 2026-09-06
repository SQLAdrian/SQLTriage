/* In the name of God, the Merciful, the Compassionate */
/*
 * RestoreVerifyService — MSP #9 automated restore-test, VERIFYONLY-lite tier.
 *
 * This is the PURE core (+ one IMPURE msdb seam) a background ScheduledTaskEngine RestoreVerify task
 * runs. It auto-generates a per-DB `RESTORE VERIFYONLY` for the MOST RECENT full backup, reading the
 * media paths from msdb (backupset ⋈ backupmediafamily — striped-backup safe: every stripe of one
 * media set goes into a SINGLE `RESTORE ... FROM DISK=a, DISK=b`).
 *
 * THE WHOLE POINT — HONEST CLASSIFICATION (design §4.5 + scope). msdb records only successful
 * restores, and RESTORE VERIFYONLY writes NO restorehistory row at all (empirically confirmed on SQL
 * 2017/2022), so this task's OWN logic must catch and classify failures. Every outcome is one of:
 *   • Passed       — the backup set verified.
 *   • Failed       — the backup is DAMAGED / CORRUPT. This is the real recoverability signal.
 *   • CouldNotRun  — the verify could not be performed: path unreadable / missing / access-denied to
 *                    the SQL service account, TDE certificate missing on this instance, or a timeout.
 *   • NotSupported — URL/Azure-blob backup (WITH CREDENTIAL) or non-disk media: honest omission in
 *                    the lite tier, NEVER a false pass.
 * Conflating Failed with CouldNotRun would over/under-claim recoverability — so the classifier keys
 * off SQL error NUMBERS (never the raw message, which can embed a path), and corruption ALWAYS wins
 * over an environment error so a real failure is never masked.
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services
{
    /// <summary>How a single database's most-recent-full backup verified.</summary>
    public enum RestoreVerifyOutcome
    {
        Passed,
        Failed,        // backup is corrupt/damaged — the real recoverability signal
        CouldNotRun,   // environment: path unreadable, TDE cert missing, timeout, unclassified
        NotSupported   // URL/blob or non-disk media — honest lite-tier omission
    }

    /// <summary>A classified verify outcome plus a FIXED, secret-free reason (never a raw ex.Message).</summary>
    public readonly record struct RestoreVerifyClassification(RestoreVerifyOutcome Outcome, string Reason);

    /// <summary>One stripe (media family) of a backup: its 1-based family sequence, msdb device_type,
    /// and physical path.</summary>
    public readonly record struct StripeFile(int Sequence, int DeviceType, string PhysicalName);

    /// <summary>One flat msdb row: a (database, media set) pairing with one of its stripes. The pure
    /// <see cref="RestoreVerifyService.SelectMostRecentFull"/> groups these into per-DB targets.</summary>
    public readonly record struct BackupCandidate(
        string DatabaseName, long MediaSetId, DateTime BackupFinishDate,
        bool HasChecksums, int FamilySequence, int DeviceType, string PhysicalName);

    /// <summary>The most-recent full backup of one database, with all its stripes assembled in order.</summary>
    public sealed record BackupToVerify(
        string DatabaseName, long MediaSetId, DateTime BackupFinishDate,
        bool HasChecksums, IReadOnlyList<StripeFile> Stripes);

    /// <summary>The per-DB verify result surfaced to the operator (the results artifact + summary).</summary>
    public sealed record RestoreVerifyResult(
        string DatabaseName, RestoreVerifyOutcome Outcome, string Reason,
        DateTime BackupFinishDate, int StripeCount, bool HadChecksums);

    /// <summary>One database's outcome as read back out of a saved results artifact
    /// (<see cref="RestoreVerifyService.SaveResults"/>) — the reason line only, not the full
    /// backup-finish-date/stripe/checksum detail (that lives only in the run's own
    /// <see cref="RestoreVerifyResult"/>, discarded after the artifact is written).</summary>
    public readonly record struct RestoreVerifyItem(string DatabaseName, RestoreVerifyOutcome Outcome, string Reason);

    /// <summary>Counts + per-DB detail parsed back out of a results artifact. Mirrors the exact
    /// summary block <see cref="RestoreVerifyService.SaveResults"/> writes.</summary>
    public readonly record struct RestoreVerifySummary(
        int Passed, int Failed, int CouldNotRun, int NotSupported,
        IReadOnlyList<RestoreVerifyItem> Items);

    /// <summary>Static, DI-free helpers for the MSP #9 restore-verify task. The classification /
    /// statement-generation / selection core is pure and unit-tested; a single impure seam reads msdb.</summary>
    public static class RestoreVerifyService
    {
        /// <summary>The results artifact root: AppContext.BaseDirectory\output\restore-verify\.</summary>
        public static string ResultsFolder =>
            Path.Combine(AppContext.BaseDirectory, "output", "restore-verify");

        // ── SQL error-number taxonomy (grounded in an empirical local-instance probe) ────────────

        /// <summary>The backup itself is DAMAGED / CORRUPT — a verify FAILURE (the real recoverability
        /// signal). Grounded in a LIVE probe on SQL 2017 (14.0.2110.2) AND SQL 2022 (16.0.4252.3),
        /// 2026-07-09 (see the MSP #9 report): a single content byte flipped in a WITH CHECKSUM backup,
        /// then RESTORE VERIFYONLY … WITH CHECKSUM, raised 3203 (+3013) — "Read on '…' failed: 13(The data
        /// is invalid.)" — on BOTH instances; a truncated backup raised 3203 likewise; the 3183/3189
        /// page/backup-set numbers NEVER fired on this path. So 3203 IS the WITH-CHECKSUM content-damage
        /// signal and MUST be corrupt-first (this REVERSES commit d33f509, which had mis-filed 3203 as
        /// could-not-run and thereby under-alarmed a genuinely corrupt backup as merely "could not be
        /// checked"). 3183 = RESTORE detected a page error; 3189 = "Damage to the backup set was detected";
        /// 3241/3242 = media family malformed / not a valid MTF set (empirically the garbage- and
        /// header-damaged-file signal — 3241 on both instances). NOTE: 3271 is deliberately NOT here — the
        /// probe could NOT provoke it from content damage (that path is 3203/3241), so it is a device I/O
        /// fault, not proof of corruption; see <see cref="NonrecoverableIoErrors"/>.</summary>
        private static readonly HashSet<int> CorruptBackupErrors = new() { 3183, 3189, 3203, 3241, 3242 };

        /// <summary>The backup FILE could not be opened/read (missing, path unreadable, or access
        /// denied to the SQL service account). Empirically 3201 for a missing path.</summary>
        private static readonly HashSet<int> PathUnreadableErrors = new() { 3201, 3202 };

        /// <summary>A NONRECOVERABLE device/OS I/O fault while reading the backup file — 3271 "A
        /// nonrecoverable I/O error occurred on file". Per the 2026-07-09 probe (SQL 2017 + 2022) this
        /// number could NOT be provoked by content corruption: a byte flip / truncation / garbage / header
        /// damage surfaced as 3203 or 3241, and a missing / exclusively-locked file surfaced as 3201 — 3271
        /// stayed absent throughout. So on the evidence 3271 does not, by itself, PROVE content corruption;
        /// it is a hard media read fault. It is classified could-not-run — but with a LOUD reason (it may
        /// equally mean damaged/failing storage and the backup was NOT confirmed readable), never a bland
        /// "could not be checked" that reads as safe. A distinct loud state beats false reassurance.</summary>
        private static readonly HashSet<int> NonrecoverableIoErrors = new() { 3271 };

        /// <summary>The TDE certificate/key needed to read an encrypted backup is not present on this
        /// instance — an environment gap, not corruption.</summary>
        private static readonly HashSet<int> TdeMissingErrors = new() { 33111, 33101, 33126 };

        /// <summary>Microsoft.Data.SqlClient surfaces a command timeout as SqlException.Number -2.</summary>
        private const int TimeoutErrorNumber = -2;

        /// <summary>
        /// HEADLINE CLASSIFIER (the pinned honesty rail). Maps the SQL error numbers raised by a
        /// failed RESTORE VERIFYONLY to a verify outcome + a fixed, secret-free reason.
        ///
        /// Precedence is deliberate: CORRUPTION WINS. A single RESTORE can raise several errors (the
        /// real cause plus the generic 3013 "VERIFY … terminating abnormally" terminator, or a device
        /// error alongside a media error); if ANY number signals a damaged backup we report Failed so a
        /// real recoverability failure is never masked as could-not-run. An error we cannot interpret
        /// falls through to CouldNotRun — we never FABRICATE a corrupt verdict, and never claim a pass.
        /// </summary>
        public static RestoreVerifyClassification Classify(IReadOnlyCollection<int> sqlErrorNumbers)
        {
            var nums = sqlErrorNumbers ?? Array.Empty<int>();

            // 1) Corruption first — the real recoverability signal must never be hidden.
            var corrupt = nums.FirstOrDefault(CorruptBackupErrors.Contains);
            if (CorruptBackupErrors.Contains(corrupt))
                return new(RestoreVerifyOutcome.Failed,
                    $"Backup verification FAILED — the backup set is damaged or corrupt (SQL error {corrupt}). " +
                    "This backup is not a reliable recovery point.");

            // 2) TDE certificate missing — an environment gap, not a corrupt backup.
            var tde = nums.FirstOrDefault(TdeMissingErrors.Contains);
            if (TdeMissingErrors.Contains(tde))
                return new(RestoreVerifyOutcome.CouldNotRun,
                    $"Could not verify — the TDE certificate/key needed to read this backup is not " +
                    $"present on this instance (SQL error {tde}). The backup may still be sound; it could not be checked.");

            // 3) Backup file unreadable / missing / access-denied to the SQL service account.
            var path = nums.FirstOrDefault(PathUnreadableErrors.Contains);
            if (PathUnreadableErrors.Contains(path))
                return new(RestoreVerifyOutcome.CouldNotRun,
                    $"Could not verify — the backup file is missing, unreadable, or access-denied to the " +
                    $"SQL Server service account (SQL error {path}). The backup could not be checked.");

            // 4) Nonrecoverable device I/O fault (3271) — a hard read failure. The live probe (SQL 2017 +
            //    2022, 2026-07-09) never produced 3271 from content damage — that path is 3203/3241, caught
            //    above as corrupt — so 3271 does not by itself PROVE the backup is corrupt. But it does mean
            //    the backup was NOT confirmed readable and may signal damaged/failing media, so it is
            //    could-not-run with a LOUD reason (never a bland "could not be checked"): a distinct loud
            //    state, not false reassurance.
            var io = nums.FirstOrDefault(NonrecoverableIoErrors.Contains);
            if (NonrecoverableIoErrors.Contains(io))
                return new(RestoreVerifyOutcome.CouldNotRun,
                    $"Could NOT confirm this backup — a nonrecoverable I/O error occurred while reading it (SQL error {io}). " +
                    "This may indicate damaged or failing storage media as well as an unreachable device; the backup was NOT " +
                    "verified readable. Investigate the storage and re-run — do not treat this as a good backup.");

            // 5) Timeout — the verify ran too long against CommandTimeout.
            if (nums.Contains(TimeoutErrorNumber))
                return new(RestoreVerifyOutcome.CouldNotRun,
                    "Could not verify — the verification timed out against the task's command timeout " +
                    $"(SQL error {TimeoutErrorNumber}). Increase the timeout or schedule off-peak.");

            // 6) Unrecognised — honest default. Not a pass, and NOT a fabricated corrupt verdict.
            var first = nums.FirstOrDefault();
            return new(RestoreVerifyOutcome.CouldNotRun,
                $"Could not verify — an unrecognised restore error prevented verification (SQL error {first}). " +
                "The backup could not be checked.");
        }

        /// <summary>Convenience overload: pull the error numbers off a <see cref="SqlException"/> and
        /// classify. Only the NUMBERS are read — the message (which can embed a path) is never used.</summary>
        public static RestoreVerifyClassification Classify(SqlException ex)
        {
            var numbers = new List<int>();
            foreach (SqlError e in ex.Errors)
                numbers.Add(e.Number);
            if (numbers.Count == 0) numbers.Add(ex.Number);
            return Classify(numbers);
        }

        // ── Statement generation (striped-safe, checksum-aware) ──────────────────────────────────

        /// <summary>
        /// Build the single <c>RESTORE VERIFYONLY</c> statement for one database's most-recent full
        /// backup. All stripes go into ONE statement (<c>FROM DISK = a, DISK = b</c>, ordered by family
        /// sequence) so a striped backup verifies as a whole. WITH CHECKSUM is emitted ONLY when the
        /// backup actually carries checksums — forcing it on a non-checksum backup raises SQL 3187
        /// ("RESTORE WITH CHECKSUM cannot be specified …"), which would be a FALSE could-not-run; a
        /// non-checksum backup is verified WITH NO_CHECKSUM (structure/header read) instead.
        /// </summary>
        public static string BuildVerifyStatement(BackupToVerify backup)
        {
            var disks = string.Join(", ", backup.Stripes
                .OrderBy(s => s.Sequence)
                .Select(s => $"DISK = N'{EscapeSqlLiteral(s.PhysicalName)}'"));
            var checksum = backup.HasChecksums ? "CHECKSUM" : "NO_CHECKSUM";
            return $"RESTORE VERIFYONLY FROM {disks} WITH {checksum}";
        }

        private static string EscapeSqlLiteral(string s) => (s ?? string.Empty).Replace("'", "''");

        // ── Lite-tier support gate (honest omission, never a false pass) ─────────────────────────

        // msdb.dbo.backupmediafamily.device_type: 2 = Disk, 5 = Tape, 7 = Virtual Device, 9 = URL/Azure.
        // Values ≥ 100 are the logical-device equivalents (102 disk, 105 tape, 107 vdi, 109 url).
        private const int DeviceDisk = 2;
        private const int DeviceDiskLogical = 102;

        /// <summary>
        /// Whether the lite tier can verify this backup. Supported = DISK media only. URL/Azure-blob
        /// (device_type 9/109 or an http(s) path) needs WITH CREDENTIAL and is out of scope; tape and
        /// virtual-device likewise. An unsupported backup is reported as NotSupported — an HONEST
        /// omission with a reason, never a false pass. Empty media (nothing recorded) is unsupported too.
        /// </summary>
        public static (bool Supported, string Reason) IsSupported(BackupToVerify backup)
        {
            if (backup.Stripes.Count == 0)
                return (false, "Not supported — no backup media files are recorded for the most recent full backup.");

            foreach (var s in backup.Stripes)
            {
                var p = s.PhysicalName ?? string.Empty;
                if (s.DeviceType == 9 || s.DeviceType == 109
                    || p.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return (false, "Not supported in the lite tier — this is a URL/Azure-blob backup " +
                                   "(requires WITH CREDENTIAL). Reported as not-checked, never a pass.");

                if (s.DeviceType != DeviceDisk && s.DeviceType != DeviceDiskLogical)
                    return (false, $"Not supported in the lite tier — non-disk backup media (device type {s.DeviceType}).");
            }
            return (true, string.Empty);
        }

        // ── Most-recent-full selection (pure) ────────────────────────────────────────────────────

        /// <summary>
        /// Group flat msdb candidate rows into one <see cref="BackupToVerify"/> per database: the
        /// MOST RECENT full backup (latest backup_finish_date; ties broken by the larger media_set_id),
        /// with that media set's stripes assembled in family-sequence order. Deterministic; ordered by
        /// database name. Pure — unit-tested without a live server.
        /// </summary>
        public static IReadOnlyList<BackupToVerify> SelectMostRecentFull(IEnumerable<BackupCandidate> candidates)
        {
            var result = new List<BackupToVerify>();

            foreach (var group in (candidates ?? Enumerable.Empty<BackupCandidate>())
                         .GroupBy(c => c.DatabaseName, StringComparer.OrdinalIgnoreCase))
            {
                // Winning media set = latest finish date, then largest media_set_id on a tie.
                var winner = group
                    .OrderByDescending(c => c.BackupFinishDate)
                    .ThenByDescending(c => c.MediaSetId)
                    .First();

                var stripes = group
                    .Where(c => c.MediaSetId == winner.MediaSetId)
                    .OrderBy(c => c.FamilySequence)
                    .Select(c => new StripeFile(c.FamilySequence, c.DeviceType, c.PhysicalName))
                    .ToList();

                result.Add(new BackupToVerify(
                    winner.DatabaseName, winner.MediaSetId, winner.BackupFinishDate,
                    winner.HasChecksums, stripes));
            }

            return result
                .OrderBy(b => b.DatabaseName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ── Results artifact (the dedicated store — NOT restorehistory) ──────────────────────────

        /// <summary>
        /// Write the per-DB verify results to output\restore-verify\ under a sanitized, timestamped
        /// name and return the absolute path. This is the operator-facing surface where the
        /// FAILED-vs-COULD-NOT-RUN distinction is visible per database. §4.5-safe: database names and
        /// outcomes only — NO backup paths, NO connection strings, NO raw error messages.
        /// </summary>
        public static string SaveResults(string server, IReadOnlyList<RestoreVerifyResult> results, DateTime nowUtc)
        {
            Directory.CreateDirectory(ResultsFolder);

            var sb = new StringBuilder();
            sb.AppendLine($"SQLTriage — Restore-Verify (VERIFYONLY-lite) — {server}");
            sb.AppendLine($"Generated {nowUtc:yyyy-MM-ddTHH:mmZ} (UTC)");
            sb.AppendLine("NOTE: RESTORE VERIFYONLY does not write msdb.dbo.restorehistory — these results are the record.");
            sb.AppendLine(new string('-', 78));
            sb.AppendLine($"Databases checked: {results.Count}");
            sb.AppendLine(
                $"  PASSED        : {results.Count(r => r.Outcome == RestoreVerifyOutcome.Passed)}");
            sb.AppendLine(
                $"  FAILED(corrupt): {results.Count(r => r.Outcome == RestoreVerifyOutcome.Failed)}");
            sb.AppendLine(
                $"  COULD-NOT-RUN : {results.Count(r => r.Outcome == RestoreVerifyOutcome.CouldNotRun)}");
            sb.AppendLine(
                $"  NOT-SUPPORTED : {results.Count(r => r.Outcome == RestoreVerifyOutcome.NotSupported)}");
            sb.AppendLine(new string('-', 78));

            foreach (var r in results.OrderBy(r => r.Outcome).ThenBy(r => r.DatabaseName, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(
                    $"[{Label(r.Outcome)}] {r.DatabaseName}  " +
                    $"(full backup {r.BackupFinishDate:yyyy-MM-dd HH:mm}, {r.StripeCount} stripe(s), " +
                    $"checksums {(r.HadChecksums ? "present" : "absent")})");
                sb.AppendLine($"        {r.Reason}");
            }

            return WriteWithoutOverwriting(server, nowUtc, sb.ToString());
        }

        /// <summary>
        /// Ordinals tried before falling back to a GUID. The number is a readability budget, not a
        /// correctness one: the GUID branch below has no ceiling.
        /// </summary>
        private const int MaxOrdinalAttempts = 99;

        /// <summary>
        /// Writes the results artifact to a name NO existing file holds, and returns that name.
        ///
        /// The name used to be stamped to the second, so two verifies of the same server inside one
        /// second wrote the same path and the second silently overwrote the first. That is data
        /// loss, not a naming wrinkle: this artifact IS the record of the run (RESTORE VERIFYONLY
        /// writes nothing to msdb.dbo.restorehistory, as the file's own header says), so the
        /// destroyed run leaves no trace anywhere. The scheduler fix that stopped tasks fanning out
        /// made a same-second pair much rarer; it did not make overwriting safe, and a manual run
        /// racing a scheduled one still collides.
        ///
        /// FileMode.CreateNew is the guarantee, not a File.Exists check: existence is asked and
        /// answered by the same syscall that creates the file, so a second writer arriving between
        /// the two loses the create and takes the next ordinal instead of the first writer's file.
        /// A caller reading the returned path always reads what THIS call wrote.
        /// </summary>
        private static string WriteWithoutOverwriting(string server, DateTime nowUtc, string content)
        {
            var stem = $"RestoreVerify_{SanitizeFileName(server)}_{nowUtc:yyyyMMdd_HHmmssZ}";
            var bytes = new UTF8Encoding(false).GetBytes(content);

            for (var ordinal = 1; ordinal <= MaxOrdinalAttempts; ordinal++)
            {
                var name = ordinal == 1 ? $"{stem}.txt" : $"{stem}_{ordinal}.txt";
                var path = Path.Combine(ResultsFolder, name);
                try
                {
                    Write(path, bytes);
                    return path;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // The name is taken by a run we must not destroy. Try the next ordinal.
                    // Any other IOException (disk full, path too long, the folder gone) is a real
                    // write failure and propagates: a caller must not be told a record was saved.
                }
            }

            // Ordinals exhausted. Still never overwrite — CreateNew on a GUID name, and if even
            // that fails the exception reaches the caller rather than a fabricated success path.
            var guidPath = Path.Combine(ResultsFolder, $"{stem}_{Guid.NewGuid():N}.txt");
            Write(guidPath, bytes);
            return guidPath;

            static void Write(string path, byte[] payload)
            {
                using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                fs.Write(payload, 0, payload.Length);
            }
        }

        private static string Label(RestoreVerifyOutcome o) => o switch
        {
            RestoreVerifyOutcome.Passed => "PASS",
            RestoreVerifyOutcome.Failed => "FAIL-CORRUPT",
            RestoreVerifyOutcome.CouldNotRun => "COULD-NOT-RUN",
            RestoreVerifyOutcome.NotSupported => "NOT-SUPPORTED",
            _ => "UNKNOWN"
        };

        /// <summary>
        /// Reads back the results artifact <see cref="SaveResults"/> writes — the summary counts
        /// plus the per-DB Failed/CouldNotRun/NotSupported outcomes and their fixed reason lines.
        /// Used by the HA/DR &amp; Backup Posture report to recompose a pass-% verdict from an
        /// already-completed run, without re-running any RESTORE VERIFYONLY. Pure text parsing
        /// against this class's OWN deterministic format (see the "PASSED/FAILED(corrupt)/
        /// COULD-NOT-RUN/NOT-SUPPORTED" summary block and "[LABEL] DbName (…)" / reason line pairs
        /// above). Returns null when the file is missing, empty, or unreadable — never a fabricated
        /// result standing in for one that could not be read back.
        /// </summary>
        public static RestoreVerifySummary? ReadResultsFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (!File.Exists(path)) return null;
                var lines = File.ReadAllLines(path);

                int passed = 0, failed = 0, couldNot = 0, notSupported = 0;
                var items = new List<RestoreVerifyItem>();
                string? pendingLabel = null;
                string? pendingDb = null;

                foreach (var raw in lines)
                {
                    var line = raw ?? string.Empty;
                    var trimmed = line.Trim();

                    if (trimmed.StartsWith("PASSED", StringComparison.OrdinalIgnoreCase))
                        passed = ExtractCount(trimmed);
                    else if (trimmed.StartsWith("FAILED(corrupt)", StringComparison.OrdinalIgnoreCase))
                        failed = ExtractCount(trimmed);
                    else if (trimmed.StartsWith("COULD-NOT-RUN", StringComparison.OrdinalIgnoreCase))
                        couldNot = ExtractCount(trimmed);
                    else if (trimmed.StartsWith("NOT-SUPPORTED", StringComparison.OrdinalIgnoreCase))
                        notSupported = ExtractCount(trimmed);
                    else if (line.StartsWith("[", StringComparison.Ordinal))
                    {
                        // "[LABEL] DbName  (full backup …, N stripe(s), checksums …)"
                        var close = line.IndexOf(']');
                        if (close > 0)
                        {
                            pendingLabel = line.Substring(1, close - 1).Trim();
                            var rest = line.Substring(close + 1).Trim();
                            var paren = rest.IndexOf('(');
                            pendingDb = (paren > 0 ? rest.Substring(0, paren) : rest).Trim();
                        }
                    }
                    else if (pendingLabel != null && pendingDb != null && trimmed.Length > 0)
                    {
                        // The line directly under a "[LABEL] DbName (…)" header is always its Reason.
                        items.Add(new RestoreVerifyItem(pendingDb, LabelToOutcome(pendingLabel), trimmed));
                        pendingLabel = null;
                        pendingDb = null;
                    }
                }

                return new RestoreVerifySummary(passed, failed, couldNot, notSupported, items);
            }
            catch (Exception)
            {
                // Best-effort read-back of our own artifact — a corrupt/half-written file must
                // never be misreported as a clean or a failing run.
                return null;
            }
        }

        private static int ExtractCount(string line)
        {
            var idx = line.IndexOf(':');
            if (idx < 0) return 0;
            return int.TryParse(line.Substring(idx + 1).Trim(), out var n) ? n : 0;
        }

        private static RestoreVerifyOutcome LabelToOutcome(string label) => label switch
        {
            "PASS" => RestoreVerifyOutcome.Passed,
            "FAIL-CORRUPT" => RestoreVerifyOutcome.Failed,
            "COULD-NOT-RUN" => RestoreVerifyOutcome.CouldNotRun,
            "NOT-SUPPORTED" => RestoreVerifyOutcome.NotSupported,
            _ => RestoreVerifyOutcome.CouldNotRun,   // unrecognised — never assume a pass
        };

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "server";
            var sanitized = name.Replace("\\", "_").Replace("/", "_");
            foreach (var c in Path.GetInvalidFileNameChars())
                sanitized = sanitized.Replace(c, '_');
            return sanitized.Length > 80 ? sanitized[..80] : sanitized;
        }

        // ── Impure seam: read the most-recent-full backup targets from msdb ──────────────────────

        // Latest FULL (type='D') media set per database, with EVERY stripe (media family). Restricted
        // to ONLINE databases (state=0). System DBs (database_id ≤ 4) are excluded unless the caller
        // opts in. has_backup_checksums drives whether the verify can force WITH CHECKSUM. Ages/paths
        // come from the server's own msdb, mirroring the backuphealth panel convention.
        private const string CandidateQueryTemplate = @"
;WITH latest AS (
    SELECT bs.database_name, bs.media_set_id, bs.has_backup_checksums, bs.backup_finish_date,
           ROW_NUMBER() OVER (PARTITION BY bs.database_name ORDER BY bs.backup_finish_date DESC, bs.media_set_id DESC) AS rn
    FROM msdb.dbo.backupset bs
    JOIN sys.databases d ON d.name = bs.database_name
    WHERE bs.type = 'D' AND d.state = 0 {0}
)
SELECT l.database_name, l.media_set_id, l.backup_finish_date,
       CAST(ISNULL(l.has_backup_checksums, 0) AS int) AS has_checksums,
       mf.family_sequence_number, mf.device_type, mf.physical_device_name
FROM latest l
JOIN msdb.dbo.backupmediafamily mf ON mf.media_set_id = l.media_set_id
WHERE l.rn = 1
ORDER BY l.database_name, mf.family_sequence_number;";

        /// <summary>
        /// Production seam: read one server's most-recent-full backup targets from msdb over the STORED
        /// connection (resolved by server name, connecting to <c>master</c> — the same pattern
        /// <see cref="Portal.BackupCollector.QueryBackupSnapshot"/> uses). Fully contained — any fault
        /// ⇒ empty list, logged by exception TYPE only (never the message / connection string).
        /// </summary>
        public static IReadOnlyList<BackupToVerify> QueryMostRecentFullTargets(
            ServerConnectionManager conns, string server, bool includeSystemDatabases,
            int commandTimeoutSeconds, ILogger? logger)
        {
            try
            {
                var conn = conns.GetConnections()
                    .FirstOrDefault(c => c.GetServerList().Contains(server, StringComparer.OrdinalIgnoreCase));
                if (conn == null)
                    return Array.Empty<BackupToVerify>();

                var systemFilter = includeSystemDatabases ? string.Empty : "AND d.database_id > 4";
                var query = string.Format(CandidateQueryTemplate, systemFilter);

                var candidates = new List<BackupCandidate>();
                using var sql = new SqlConnection(conn.GetConnectionString(server, "master"));
                sql.Open();
                using var cmd = new SqlCommand(query, sql)
                {
                    CommandTimeout = commandTimeoutSeconds > 0 ? commandTimeoutSeconds : 30
                };
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    candidates.Add(new BackupCandidate(
                        DatabaseName: reader.IsDBNull(0) ? "" : reader.GetString(0),
                        MediaSetId: reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1)),
                        BackupFinishDate: reader.IsDBNull(2) ? default : reader.GetDateTime(2),
                        HasChecksums: !reader.IsDBNull(3) && Convert.ToInt32(reader.GetValue(3)) == 1,
                        FamilySequence: reader.IsDBNull(4) ? 1 : Convert.ToInt32(reader.GetValue(4)),
                        DeviceType: reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5)),
                        PhysicalName: reader.IsDBNull(6) ? "" : reader.GetString(6)));
                }
                return SelectMostRecentFull(candidates.Where(c => !string.IsNullOrEmpty(c.DatabaseName)));
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    "Restore-verify backup-target query failed for a server ({ExType})", ex.GetType().Name);
                return Array.Empty<BackupToVerify>();
            }
        }
    }
}
