/* In the name of God, the Merciful, the Compassionate */
/*
 * QuickCheckResultStore — JSON-per-server persistence for Quick Assessment results.
 *
 * Why: with 25 servers, holding every check result in CheckExecutionService's
 * in-memory list pushed the process to ~3 GB resident. This service writes each
 * run to disk and lets readers re-hydrate without keeping the full set in RAM.
 *
 * Layout:   <AppContext.BaseDirectory>/output/quickcheck/<safe-server>-<UTC>.json
 * Retention: most-recent 10 runs per server (older are deleted on write).
 * Read path: JSON primary, SQLite (GovernanceHistoryService) fallback when no
 *            file exists for a server.
 *
 * Filename matching (2026-07-19): a bare "<safe>-*.json" glob is AMBIGUOUS between
 * servers whose safe names prefix one another — "SQL01-*.json" also matches every
 * "SQL01-DR-<stamp>.json" — so SQL01's latest-run lookup could return SQL01-DR's file
 * and SQL01's retention trim could delete SQL01-DR's runs. Every lookup now anchors the
 * trailing segment to a real 16-char stamp via IsRunFileFor(), so "SQL01-DR" is only ever
 * a match for the server "SQL01-DR".
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    public class QuickCheckResultStore
    {
        private readonly ILogger<QuickCheckResultStore> _logger;
        private readonly string _rootDir;
        /// <summary>
        /// How many runs are kept per server. PUBLIC because the Export Pack's provenance states
        /// this window as a completeness caveat (plan §5 attack 10 — D4 answers "what ran in the
        /// runs still retained", not "everything this bundle ever executed"), and a caveat that
        /// carries its own copy of the number is one edit away from being false.
        /// </summary>
        public const int RetentionPerServer = 10;

        /// <summary>The UTC stamp appended to every run filename. Fixed width (16 chars) — the
        /// anchor <see cref="IsRunFileFor"/> uses to tell "SQL01"'s runs from "SQL01-DR"'s.</summary>
        private const string StampFormat = "yyyyMMdd'T'HHmmss'Z'";
        private const int StampLength = 16;

        /// <summary>Path of the most recent file <see cref="WriteRun"/> wrote, per server. Lets a
        /// caller that needs THE file this run produced (the --audit json export) use it directly
        /// instead of re-deriving it from a directory listing.</summary>
        private readonly ConcurrentDictionary<string, string> _lastWritten =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        public QuickCheckResultStore(ILogger<QuickCheckResultStore> logger)
        {
            _logger = logger;
            _rootDir = Path.Combine(AppContext.BaseDirectory, "output", "quickcheck");
            try
            {
                Directory.CreateDirectory(_rootDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not create QuickCheck output dir at {Path}", _rootDir);
            }
        }

        public string RootDir => _rootDir;

        /// <summary>
        /// Persist a run for a single server. Writes one JSON file with the full
        /// result list, then trims older files for this server to RetentionPerServer.
        /// Best-effort: failures are logged and swallowed so the live run keeps moving.
        /// </summary>
        /// <returns>The exact path written, or null if nothing was written (empty result set,
        /// blank server name, or an I/O failure — all three already logged). Returned so a
        /// caller that must act on THIS run's file (the --audit json export) does not have to
        /// re-find it by listing the directory. Existing callers ignore it.</returns>
        public string? WriteRun(string serverName, IReadOnlyList<CheckResult> results)
        {
            if (string.IsNullOrWhiteSpace(serverName) || results.Count == 0) return null;
            try
            {
                var safe = SafeFileName(serverName);
                var stamp = DateTime.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture);
                var path = Path.Combine(_rootDir, $"{safe}-{stamp}.json");
                var payload = new RunPayload
                {
                    ServerName = serverName,
                    WrittenAtUtc = DateTime.UtcNow,
                    SchemaVersion = 1,
                    Results = results.ToList(),
                };
                File.WriteAllText(path, JsonSerializer.Serialize(payload, _jsonOptions));
                _lastWritten[serverName] = path;
                TrimOldRuns(safe);
                return path;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write QuickCheck run for {Server}", serverName);
                return null;
            }
        }

        /// <summary>
        /// The path <see cref="WriteRun"/> last wrote for <paramref name="serverName"/> in THIS
        /// process, or null if this process has not written one. Only ever reports a file this
        /// store wrote — it is not a substitute for <see cref="GetLatestRunFile"/>, which also
        /// finds runs left by an earlier process.
        /// </summary>
        public string? GetLastWrittenPath(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return null;
            return _lastWritten.TryGetValue(serverName, out var path) && File.Exists(path) ? path : null;
        }

        /// <summary>
        /// The newest run file on disk for <paramref name="serverName"/>, or null if there is
        /// none. Matching is anchored (see <see cref="IsRunFileFor"/>) so a server whose safe
        /// name is a prefix of another's ("SQL01" vs "SQL01-DR") never picks up the other's file.
        /// </summary>
        public string? GetLatestRunFile(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return null;
            try
            {
                if (!Directory.Exists(_rootDir)) return null;
                return RunFilesFor(SafeFileName(serverName)).FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list QuickCheck runs for {Server}", serverName);
                return null;
            }
        }

        /// <summary>
        /// Return the latest run's results for a server, or null if no JSON exists.
        /// </summary>
        public List<CheckResult>? ReadLatestRun(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return null;
            try
            {
                var latest = GetLatestRunFile(serverName);
                if (latest is null) return null;

                var json = File.ReadAllText(latest);
                var payload = JsonSerializer.Deserialize<RunPayload>(json, _jsonOptions);
                return payload?.Results ?? new List<CheckResult>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read latest QuickCheck run for {Server}", serverName);
                return null;
            }
        }

        /// <summary>
        /// #84 results-import round-trip: parse and shape-validate a results JSON document
        /// (the same shape <see cref="WriteRun"/> writes) WITHOUT touching disk — the caller
        /// (ImportResultsService / the Import page / --import CLI) lands it via
        /// <see cref="WriteRun"/> only after a successful parse, so a malformed or short file
        /// never partially overwrites a server's history.
        ///
        /// Tolerant of a SchemaVersion other than 1 (returned via <paramref name="warning"/>,
        /// not a hard failure — the shape may still round-trip fine). Never throws: any
        /// deserialization failure or missing-required-field condition comes back as
        /// <paramref name="error"/> with a null return.
        /// </summary>
        public RunPayload? TryParseRunPayload(string json, out string? error, out string? warning)
        {
            error = null;
            warning = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "File is empty.";
                return null;
            }

            RunPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<RunPayload>(json, _jsonOptions);
            }
            catch (JsonException ex)
            {
                error = $"Malformed JSON: {ex.Message}";
                return null;
            }
            catch (Exception ex)
            {
                // Defensive: never let a hostile/corrupt file crash the caller (page or CLI).
                error = $"Could not parse file: {ex.Message}";
                return null;
            }

            if (payload is null)
            {
                error = "Malformed JSON: file did not deserialize to a results document.";
                return null;
            }
            if (string.IsNullOrWhiteSpace(payload.ServerName))
            {
                error = "Missing or empty 'ServerName' — cannot import.";
                return null;
            }
            if (payload.Results is null || payload.Results.Count == 0)
            {
                error = "'Results' is missing or empty — nothing to import.";
                return null;
            }
            if (payload.SchemaVersion != 1)
            {
                warning = $"SchemaVersion {payload.SchemaVersion} (expected 1) — importing anyway; " +
                          "the file may predate or postdate this build's result shape.";
            }

            return payload;
        }

        /// <summary>
        /// Return the list of original server names that have at least one JSON run on disk.
        /// Reads ServerName from the JSON payload to avoid safe-filename round-trip issues.
        /// </summary>
        public List<string> GetServersWithRuns()
        {
            try
            {
                if (!Directory.Exists(_rootDir)) return new List<string>();

                // Group files by safe-name prefix, pick the latest per group,
                // read the ServerName field from the payload.
                var groups = Directory.GetFiles(_rootDir, "*.json")
                    .Select(f => new { File = f, Base = StripStamp(Path.GetFileNameWithoutExtension(f)!) })
                    .Where(x => x.Base != null)
                    .GroupBy(x => x.Base, StringComparer.OrdinalIgnoreCase);

                var names = new List<string>();
                foreach (var g in groups)
                {
                    var latest = g.OrderByDescending(x => x.File).First().File;
                    try
                    {
                        var json = File.ReadAllText(latest);
                        var payload = JsonSerializer.Deserialize<RunPayload>(json, _jsonOptions);
                        if (!string.IsNullOrWhiteSpace(payload?.ServerName))
                            names.Add(payload.ServerName);
                    }
                    catch { /* skip corrupt file */ }
                }
                return names;
            }
            catch
            {
                return new List<string>();
            }
        }

        private static string? StripStamp(string fileNameNoExt)
        {
            // Expect <name>-yyyyMMddTHHmmssZ. Stamp length = 16. Strip trailing
            // "-yyyyMMddTHHmmssZ" if present.
            const int stampLen = 16;
            if (fileNameNoExt.Length > stampLen + 1
                && fileNameNoExt[fileNameNoExt.Length - stampLen - 1] == '-')
            {
                return fileNameNoExt.Substring(0, fileNameNoExt.Length - stampLen - 1);
            }
            return fileNameNoExt;
        }

        private void TrimOldRuns(string safeName)
        {
            try
            {
                foreach (var stale in RunFilesFor(safeName).Skip(RetentionPerServer))
                {
                    // A failed delete only means the retention cap is over by one file, but it
                    // was previously swallowed with no trace at all — so a directory that never
                    // stopped growing (file locked by AV, permissions changed) looked identical
                    // to a healthy one. Warn, then keep going: retention is never worth failing
                    // a run over.
                    try { File.Delete(stale); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not delete stale QuickCheck run {Path}", stale);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not trim QuickCheck runs for {Safe}", safeName);
            }
        }

        /// <summary>
        /// Every run file belonging to <paramref name="safeName"/>, newest first. The glob is a
        /// cheap prefilter only — <see cref="IsRunFileFor"/> does the actual (anchored) matching,
        /// because "SQL01-*.json" also matches "SQL01-DR-&lt;stamp&gt;.json".
        /// </summary>
        private List<string> RunFilesFor(string safeName) =>
            Directory.GetFiles(_rootDir, $"{safeName}-*.json")
                .Where(f => IsRunFileFor(Path.GetFileNameWithoutExtension(f), safeName))
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// True iff <paramref name="fileNameNoExt"/> is exactly "&lt;safeName&gt;-&lt;stamp&gt;",
        /// where stamp is a real <see cref="StampFormat"/> value. This is what stops "SQL01" from
        /// claiming "SQL01-DR"'s files: "DR-20260719T101500Z" is 19 chars and does not parse as a
        /// stamp, so it is not a run of "SQL01".
        /// </summary>
        private static bool IsRunFileFor(string? fileNameNoExt, string safeName)
        {
            if (fileNameNoExt is null) return false;
            if (fileNameNoExt.Length != safeName.Length + 1 + StampLength) return false;
            if (!fileNameNoExt.StartsWith(safeName, StringComparison.OrdinalIgnoreCase)) return false;
            if (fileNameNoExt[safeName.Length] != '-') return false;

            var stamp = fileNameNoExt.Substring(safeName.Length + 1);
            return DateTime.TryParseExact(
                stamp, StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);
        }

        private static string SafeFileName(string raw)
        {
            // Replace path-unsafe chars (\, /, :, etc.) so InstanceName like
            // SERVER\INSTANCE round-trips to a single file.
            var invalid = Path.GetInvalidFileNameChars();
            var chars = raw.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
            return new string(chars);
        }

        /// <summary>
        /// Public (was private) as of #84: <see cref="TryParseRunPayload"/> hands this shape
        /// back to importers (ImportResultsService, the Import page, --import CLI) so they can
        /// read ServerName/Results before deciding to land the run via <see cref="WriteRun"/>.
        /// </summary>
        public class RunPayload
        {
            public string ServerName { get; set; } = "";
            public DateTime WrittenAtUtc { get; set; }
            public int SchemaVersion { get; set; } = 1;
            public List<CheckResult> Results { get; set; } = new();
        }
    }
}
