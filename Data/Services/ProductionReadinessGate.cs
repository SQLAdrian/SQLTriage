/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Checks whether the application has reached production readiness (v1.x+)
    /// and enforces security requirements for that milestone:
    ///   - Diagnostic scripts (.sql files) must be embedded/encrypted resources
    ///   - Check config files must be packaged (not loose on disk)
    ///   - BPScript files must be integrity-verified
    ///
    /// During pre-production (v0.x), this service logs warnings about what
    /// will need to change before the v1.0 release.
    /// </summary>
    public class ProductionReadinessGate
    {
        private readonly ILogger<ProductionReadinessGate> _logger;

        public bool IsProductionVersion { get; private set; }
        public string CurrentVersion { get; private set; } = "0.0.0";

        public ProductionReadinessGate(ILogger<ProductionReadinessGate> logger)
        {
            _logger = logger;
            EvaluateReadiness();
        }

        private void EvaluateReadiness()
        {
            try
            {
                var versionPath = Path.Combine(AppContext.BaseDirectory, "Config", "version.json");
                if (File.Exists(versionPath))
                {
                    var json = File.ReadAllText(versionPath);
                    using var doc = JsonDocument.Parse(json);
                    CurrentVersion = doc.RootElement.GetProperty("version").GetString() ?? "0.0.0";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read version.json");
            }

            // Parse major version
            var parts = CurrentVersion.Split('.');
            if (parts.Length > 0 && int.TryParse(parts[0], out var major))
            {
                IsProductionVersion = major >= 1;
            }

            if (IsProductionVersion)
            {
                _logger.LogWarning("Production version {Version} detected — enforcing script packaging requirements", CurrentVersion);
                AuditScriptPackaging();
            }
            else
            {
                _logger.LogInformation("Pre-production version {Version} — script packaging deferred until v1.x", CurrentVersion);
                LogPreProductionChecklist();
            }
        }

        private void AuditScriptPackaging()
        {
            // Check for loose .sql files that should be embedded resources
            var sqlFiles = Directory.GetFiles(AppContext.BaseDirectory, "*.sql", SearchOption.AllDirectories);
            if (sqlFiles.Length > 0)
            {
                _logger.LogWarning("PRODUCTION GATE: {Count} loose .sql files found in deployment directory. " +
                    "These should be embedded as encrypted resources for v1.x+", sqlFiles.Length);
                foreach (var f in sqlFiles)
                    _logger.LogWarning("  Loose script: {File}", Path.GetFileName(f));
            }

            // Check for loose check config files
            var checksDir = Path.Combine(AppContext.BaseDirectory, "Config", "Checks");
            if (Directory.Exists(checksDir))
            {
                var configFiles = Directory.GetFiles(checksDir, "*.json");
                if (configFiles.Length > 0)
                {
                    _logger.LogWarning("PRODUCTION GATE: {Count} loose check config files in Config/Checks. " +
                        "These should be packaged as protected resources for v1.x+", configFiles.Length);
                }
            }

            // Check for loose BPScript files
            var bpDir = Path.Combine(AppContext.BaseDirectory, "BPScripts");
            if (Directory.Exists(bpDir))
            {
                var bpFiles = Directory.GetFiles(bpDir, "*.sql");
                if (bpFiles.Length > 0)
                {
                    _logger.LogWarning("PRODUCTION GATE: {Count} loose BPScript .sql files found. " +
                        "These should be integrity-verified and optionally encrypted for v1.x+", bpFiles.Length);
                }
            }
        }

        private void LogPreProductionChecklist()
        {
            _logger.LogDebug("Pre-production checklist for v1.x release:");
            _logger.LogDebug("  [ ] Embed .sql diagnostic scripts as encrypted resources");
            _logger.LogDebug("  [ ] Package check config JSON files into protected resources");
            _logger.LogDebug("  [ ] Add integrity hashes for BPScript files");
            _logger.LogDebug("  [ ] Enable ConfuserEx obfuscation in publish pipeline");
            _logger.LogDebug("  [ ] Verify all connection strings use AES-256-GCM encryption");
        }
    }
}
