/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    // BM:AlertDefinitionService.Class — loads, caches, and persists alert definitions from JSON
    /// <summary>
    /// Loads, caches, and persists alert definitions from Config/alert-definitions.json.
    /// Provides lookup by ID/category and CRUD for user overrides (enable/disable, thresholds).
    /// </summary>
    public class AlertDefinitionService
    {
        private readonly ILogger<AlertDefinitionService> _logger;
        private readonly string _filePath;
        private readonly object _lock = new();
        private AlertDefinitionsFile _definitions = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public event Action? OnDefinitionsChanged;

        /// <summary>
        /// How alert-definitions.json came off disk.
        ///
        /// <para><b>ANNOUNCED with a quarantine, not refused.</b> The file holds the shipped alert
        /// catalogue plus the operator's enable/disable and threshold overrides on top of it. There
        /// is no secret in it and no security control: the worst case is that the catalogue reverts
        /// to what the install package contains and the overrides have to be re-set. So it takes the
        /// warn tier, on the same line as finding-owners.json and the threshold store.</para>
        ///
        /// <para>But it gets the quarantine, and that part is NOT decoration. This service's own
        /// writes are narrow — <see cref="UpdateAlert"/> and <see cref="SetAlertEnabled"/> find
        /// nothing to change in an empty catalogue and never call Save — yet
        /// <see cref="UpdateGlobalDefaults"/> saves unconditionally, and a damaged load makes that
        /// one click replace 80 curated alerts with a bare defaults object. The catalogue is
        /// recoverable from the install package, which is what keeps this out of tier 1; the
        /// operator's overrides are recoverable only from the <c>.rejected-</c> copy.</para>
        ///
        /// <para><b>This file no longer has a second schema pointed at it.</b> Until 2026-08-04
        /// <c>AlertingService</c> read the same path as a JSON ARRAY and wrote one over it. It now
        /// owns <c>alert-thresholds.json</c>; see <c>AlertingService.ThresholdStoreFileName</c>.</para>
        /// </summary>
        private ConfigLoadOutcome _load = ConfigLoadOutcome.Missing;

        private string? _quarantine;

        /// <summary>True when alert-definitions.json exists and did not load, so no alert definition is in force.</summary>
        public bool IsStoreDamaged { get { lock (_lock) return _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty; } }

        /// <summary>What to do about it. The ONE register — never a locally written sentence.</summary>
        public string DescribeStoreRecovery()
        {
            lock (_lock) return ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine);
        }

        public AlertDefinitionService(ILogger<AlertDefinitionService> logger)
            : this(logger, null) { }

        /// <param name="definitionsFilePath">
        /// Test seam (house pattern — AdminAuthService's configPathOverride, OwnerAssignmentStore's
        /// pathOverride): null ⇒ the real Config/ path, exactly as before.
        /// </param>
        internal AlertDefinitionService(ILogger<AlertDefinitionService> logger, string? definitionsFilePath)
        {
            _logger = logger;

            if (definitionsFilePath != null)
            {
                _filePath = definitionsFilePath;
            }
            else
            {
                // Try config/ (published layout) first, then Config/ (dev layout). On Windows these
                // resolve to one directory; the probe matters only where the case is meaningful.
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                _filePath = Path.Combine(baseDir, "config", "alert-definitions.json");
                if (!File.Exists(_filePath))
                    _filePath = Path.Combine(baseDir, "Config", "alert-definitions.json");
            }

            Load();
        }

        public AlertDefinitionsFile GetDefinitions()
        {
            lock (_lock) return _definitions;
        }

        public AlertGlobalDefaults GetGlobalDefaults()
        {
            lock (_lock) return _definitions.GlobalDefaults;
        }

        public List<AlertDefinition> GetAllAlerts()
        {
            lock (_lock) return _definitions.Alerts.ToList();
        }

        public List<AlertDefinition> GetEnabledAlerts()
        {
            lock (_lock) return _definitions.Alerts.Where(a => a.Enabled).ToList();
        }

        public List<AlertCategory> GetCategories()
        {
            lock (_lock) return _definitions.Categories.ToList();
        }

        public AlertDefinition? GetAlert(string alertId)
        {
            lock (_lock) return _definitions.Alerts.FirstOrDefault(a => a.Id == alertId);
        }

        public List<AlertDefinition> GetAlertsByCategory(string categoryId)
        {
            lock (_lock) return _definitions.Alerts.Where(a => a.Category == categoryId).ToList();
        }

        /// <summary>
        /// Returns the effective cooldown for an alert (per-alert override or global default).
        /// </summary>
        public TimeSpan GetCooldown(AlertDefinition alert)
        {
            var minutes = alert.CooldownMinutes ?? _definitions.GlobalDefaults.CooldownMinutes;
            return TimeSpan.FromMinutes(minutes);
        }

        public void UpdateAlert(AlertDefinition updated)
        {
            lock (_lock)
            {
                var index = _definitions.Alerts.FindIndex(a => a.Id == updated.Id);
                if (index >= 0)
                {
                    _definitions.Alerts[index] = updated;
                    Save();
                }
            }
            OnDefinitionsChanged?.Invoke();
        }

        public void UpdateGlobalDefaults(AlertGlobalDefaults defaults)
        {
            lock (_lock)
            {
                _definitions.GlobalDefaults = defaults;
                Save();
            }
            OnDefinitionsChanged?.Invoke();
        }

        public void SetAlertEnabled(string alertId, bool enabled)
        {
            lock (_lock)
            {
                var alert = _definitions.Alerts.FirstOrDefault(a => a.Id == alertId);
                if (alert != null)
                {
                    alert.Enabled = enabled;
                    Save();
                }
            }
            OnDefinitionsChanged?.Invoke();
        }

        private void Load()
        {
            try
            {
                _definitions = ConfigFileHelper.Load<AlertDefinitionsFile>(
                    _filePath, JsonOptions, out _load, out _quarantine);

                if (IsStoreDamaged)
                    _logger.LogError(
                        "[AlertDefinitions] {Path} exists and did not load ({Outcome}). NO alert definition is in "
                        + "force in this process — no alert can fire and no category will render. Saving any alert "
                        + "setting from here will replace the file with the {Count} definition(s) held in memory, so "
                        + "repair it first if you want the catalogue back. {Recovery}",
                        _filePath, _load, _definitions.Alerts.Count,
                        ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));
                else if (_load == ConfigLoadOutcome.Missing)
                    _logger.LogWarning("Alert definitions file not found: {Path}", _filePath);
                else
                    _logger.LogInformation("Loaded {Count} alert definitions from {Path}",
                        _definitions.Alerts.Count, _filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load alert definitions");
                _definitions = new AlertDefinitionsFile();
                _load = ConfigLoadOutcome.Unreadable;
            }
        }

        private void Save()
        {
            // Announced, not refused — see _load. Fires exactly once per damaged load, because a
            // successful write makes the store Loaded, and it is the only record of the moment the
            // damaged catalogue stopped existing.
            var replacingDamage = _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty;

            try
            {
                ConfigFileHelper.Save(_filePath, _definitions, JsonOptions);

                if (replacingDamage)
                    _logger.LogWarning(
                        "[AlertDefinitions] {Path} did not load ({Outcome}) and has now been REPLACED by the "
                        + "{Count} definition(s) held in memory. Every alert the damaged file defined, and every "
                        + "enable/disable set against it, is gone from the live file. {Recovery}",
                        _filePath, _load, _definitions.Alerts.Count,
                        ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));

                _load = ConfigLoadOutcome.Loaded;
                _quarantine = null;
                _logger.LogInformation("Saved {Count} alert definitions", _definitions.Alerts.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save alert definitions");
            }
        }
    }
}
