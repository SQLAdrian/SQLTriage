/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// CM-3 — On first run after install, snapshots appsettings.json + Config/*.json into
    /// Config/.baseline-snapshot.json (relative-path → SHA-256 hex).
    /// On every startup, recomputes hashes and logs any drift as AuditEventType.ConfigDriftDetected.
    /// An admin can explicitly re-baseline via ReBaseline(actor) after confirming the new state.
    /// </summary>
    public sealed class ConfigBaselineService
    {
        private readonly ILogger<ConfigBaselineService> _logger;
        private readonly AuditLogService? _audit;
        private readonly string _baseDir;

        // Files tracked: appsettings.json + all Config/*.json except credential files.
        private static readonly string[] TrackedGlobs = { "appsettings.json", "Config/*.json" };
        private static readonly string[] ExcludedPatterns =
        {
            ".baseline-snapshot.json",
            "sqlite-cipher-key",
            "cipher-key",
            ".credentials",
        };

        private const string BaselineFileName = ".baseline-snapshot.json";

        /// <summary>
        /// True if drift was detected on the most recent startup check.
        /// Informational only — never blocks startup.
        /// </summary>
        public bool DriftDetected { get; private set; }

        /// <summary>List of files that differed from baseline (populated by startup check).</summary>
        public IReadOnlyList<string> DriftedFiles { get; private set; } = Array.Empty<string>();

        public ConfigBaselineService(
            ILogger<ConfigBaselineService> logger,
            AuditLogService? audit = null,
            string? baseDir = null)
        {
            _logger = logger;
            _audit = audit;
            _baseDir = baseDir ?? AppContext.BaseDirectory;
        }

        // ── Startup initialisation ────────────────────────────────────────

        /// <summary>
        /// Call once on startup. Creates baseline if missing; otherwise diffs against it.
        /// Non-throwing: all errors are logged and swallowed so startup is never blocked.
        /// </summary>
        public void RunStartupCheck()
        {
            try
            {
                var baselinePath = Path.Combine(_baseDir, "Config", BaselineFileName);
                if (!File.Exists(baselinePath))
                {
                    CreateBaseline(baselinePath, actor: "auto-install");
                    return;
                }

                CheckDrift(baselinePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CONFIG-BASELINE] Startup check failed — continuing without drift detection");
            }
        }

        // ── Drift detection ───────────────────────────────────────────────

        private void CheckDrift(string baselinePath)
        {
            var stored = LoadBaseline(baselinePath);
            if (stored == null) return;

            var current = ComputeHashes();
            var drifted = new List<string>();

            // Files in baseline that are now different or missing.
            foreach (var (relPath, storedHash) in stored)
            {
                if (!current.TryGetValue(relPath, out var currentHash))
                    drifted.Add($"{relPath} (deleted)");
                else if (!string.Equals(storedHash, currentHash, StringComparison.OrdinalIgnoreCase))
                    drifted.Add($"{relPath} (modified)");
            }

            // Files now present that were not in the baseline.
            foreach (var relPath in current.Keys)
            {
                if (!stored.ContainsKey(relPath))
                    drifted.Add($"{relPath} (added)");
            }

            DriftedFiles = drifted;
            DriftDetected = drifted.Count > 0;

            if (DriftDetected)
            {
                var details = string.Join("; ", drifted);
                _logger.LogWarning("[CONFIG-BASELINE] Drift detected: {Details}", details);
                _audit?.LogConfigDriftDetected(details, drifted.Count);
            }
            else
            {
                _logger.LogInformation("[CONFIG-BASELINE] No configuration drift detected");
            }
        }

        // ── Re-baseline ───────────────────────────────────────────────────

        /// <summary>
        /// Snapshots the current config state as the new baseline.
        /// Emits AuditEventType.ConfigBaselineUpdated.
        /// </summary>
        public void ReBaseline(string actor)
        {
            try
            {
                var baselinePath = Path.Combine(_baseDir, "Config", BaselineFileName);
                CreateBaseline(baselinePath, actor);
                DriftDetected = false;
                DriftedFiles = Array.Empty<string>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CONFIG-BASELINE] Re-baseline failed");
                throw;
            }
        }

        // ── Internal helpers ──────────────────────────────────────────────

        private void CreateBaseline(string baselinePath, string actor)
        {
            var hashes = ComputeHashes();
            var json = JsonSerializer.Serialize(hashes, new JsonSerializerOptions { WriteIndented = true });

            var dir = Path.GetDirectoryName(baselinePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // Windows: File.WriteAllText over an existing Hidden file (we set
            // Hidden below) — or a ReadOnly file from MSBuild content-copy of
            // Config/* — throws UnauthorizedAccessException. Clear attributes
            // first so re-baseline can overwrite the prior snapshot.
            if (File.Exists(baselinePath))
            {
                try { File.SetAttributes(baselinePath, FileAttributes.Normal); } catch { }
            }

            File.WriteAllText(baselinePath, json, Encoding.UTF8);
            try { File.SetAttributes(baselinePath, FileAttributes.Hidden); } catch { }

            _logger.LogInformation("[CONFIG-BASELINE] Baseline written by {Actor} ({Count} files)", actor, hashes.Count);
            _audit?.LogConfigBaselineUpdated(actor, hashes.Count);
        }

        private Dictionary<string, string> ComputeHashes()
        {
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // appsettings.json at base directory
            var appSettings = Path.Combine(_baseDir, "appsettings.json");
            if (File.Exists(appSettings))
                hashes["appsettings.json"] = HashFile(appSettings);

            // Config/*.json
            var configDir = Path.Combine(_baseDir, "Config");
            if (Directory.Exists(configDir))
            {
                foreach (var file in Directory.EnumerateFiles(configDir, "*.json"))
                {
                    var name = Path.GetFileName(file);
                    if (ShouldExclude(name)) continue;
                    var relPath = Path.Combine("Config", name);
                    hashes[relPath] = HashFile(file);
                }
            }

            return hashes;
        }

        private static bool ShouldExclude(string fileName)
        {
            foreach (var pattern in ExcludedPatterns)
            {
                if (fileName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string HashFile(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs));
        }

        private static Dictionary<string, string>? LoadBaseline(string path)
        {
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            }
            catch { return null; }
        }
    }
}
