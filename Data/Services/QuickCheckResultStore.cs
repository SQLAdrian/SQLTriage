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
 */

using System;
using System.Collections.Generic;
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
        private const int RetentionPerServer = 10;
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
        public void WriteRun(string serverName, IReadOnlyList<CheckResult> results)
        {
            if (string.IsNullOrWhiteSpace(serverName) || results.Count == 0) return;
            try
            {
                var safe = SafeFileName(serverName);
                var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
                var path = Path.Combine(_rootDir, $"{safe}-{stamp}.json");
                var payload = new RunPayload
                {
                    ServerName = serverName,
                    WrittenAtUtc = DateTime.UtcNow,
                    SchemaVersion = 1,
                    Results = results.ToList(),
                };
                File.WriteAllText(path, JsonSerializer.Serialize(payload, _jsonOptions));
                TrimOldRuns(safe);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write QuickCheck run for {Server}", serverName);
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
                var safe = SafeFileName(serverName);
                var files = Directory.GetFiles(_rootDir, $"{safe}-*.json");
                if (files.Length == 0) return null;

                var latest = files.OrderByDescending(f => f).First();
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
                var files = Directory.GetFiles(_rootDir, $"{safeName}-*.json")
                    .OrderByDescending(f => f)
                    .ToList();
                foreach (var stale in files.Skip(RetentionPerServer))
                {
                    try { File.Delete(stale); } catch { /* best-effort */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not trim QuickCheck runs for {Safe}", safeName);
            }
        }

        private static string SafeFileName(string raw)
        {
            // Replace path-unsafe chars (\, /, :, etc.) so InstanceName like
            // SERVER\INSTANCE round-trips to a single file.
            var invalid = Path.GetInvalidFileNameChars();
            var chars = raw.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
            return new string(chars);
        }

        private class RunPayload
        {
            public string ServerName { get; set; } = "";
            public DateTime WrittenAtUtc { get; set; }
            public int SchemaVersion { get; set; } = 1;
            public List<CheckResult> Results { get; set; } = new();
        }
    }
}
