/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
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

        public AlertDefinitionService(ILogger<AlertDefinitionService> logger)
        {
            _logger = logger;

            // Try config/ (published layout) first, then Config/ (dev layout)
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _filePath = Path.Combine(baseDir, "config", "alert-definitions.json");
            if (!File.Exists(_filePath))
                _filePath = Path.Combine(baseDir, "Config", "alert-definitions.json");

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
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        _definitions = JsonSerializer.Deserialize<AlertDefinitionsFile>(json, JsonOptions) ?? new();
                        _logger.LogInformation("Loaded {Count} alert definitions from {Path}",
                            _definitions.Alerts.Count, _filePath);
                        return;
                    }
                }
                _logger.LogWarning("Alert definitions file not found: {Path}", _filePath);
                _definitions = new AlertDefinitionsFile();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load alert definitions");
                _definitions = new AlertDefinitionsFile();
            }
        }

        private void Save()
        {
            try
            {
                ConfigFileHelper.Save(_filePath, _definitions, JsonOptions);
                _logger.LogInformation("Saved {Count} alert definitions", _definitions.Alerts.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save alert definitions");
            }
        }
    }
}
