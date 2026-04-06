/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SqlHealthAssessment.Data.Models;

namespace SqlHealthAssessment.Data
{
    /// <summary>
    /// Loads, saves, and queries dashboard configuration from a JSON file.
    /// Falls back to DefaultConfigGenerator when the file is missing or corrupt.
    /// Uses an O(1) dictionary cache for query lookups instead of scanning all panels.
    /// Monitors JSON files in the app directory for changes and reloads automatically.
    /// </summary>
    public class DashboardConfigService
    {
        private readonly ILogger<DashboardConfigService> _logger;
        private readonly string _configPath;
        private readonly string _backupPath;
        private DashboardConfigRoot _config;
        private FileSystemWatcher? _watcher;

        /// <summary>
        /// O(1) lookup cache mapping queryId -> QueryPair.
        /// Rebuilt on Load(), Save(), and ResetToDefault().
        /// </summary>
        private Dictionary<string, QueryPair> _queryCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// O(1) lookup cache mapping queryId -> PanelType string.
        /// Avoids O(dashboards × panels) scan on every delta-fetch call.
        /// </summary>
        private Dictionary<string, string> _panelTypeCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Optimization: Cache for parsed JSON to avoid repeated deserialization
        /// </summary>
        private string? _cachedJsonContent;
        private DateTime _lastJsonCheck = DateTime.MinValue;

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultBufferSize = 32768, // 32KB buffer for better performance
            PropertyNameCaseInsensitive = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            UnknownTypeHandling = System.Text.Json.Serialization.JsonUnknownTypeHandling.JsonNode,
            PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate
        };

        // Optimization: Use streams for large JSON parsing
        private async Task<DashboardConfigRoot> LoadConfigFromStreamAsync()
        {
            var configPath = _configPath;
            if (!File.Exists(configPath))
                configPath = _backupPath;

            if (!File.Exists(configPath))
                return DefaultConfigGenerator.Generate();

            try
            {
                await using var stream = File.OpenRead(configPath);
                return await JsonSerializer.DeserializeAsync<DashboardConfigRoot>(stream, SerializerOptions)
                       ?? DefaultConfigGenerator.Generate();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load config from stream, using defaults");
                return DefaultConfigGenerator.Generate();
            }
        }

        /// <summary>
        /// Event raised after the configuration has been saved and should be re-rendered.
        /// </summary>
        public event Action? OnConfigChanged;

        /// <summary>
        /// The currently loaded dashboard configuration.
        /// </summary>
        public DashboardConfigRoot Config => _config;

        /// <summary>
        /// Updates the in-memory configuration, saves to disk, and notifies subscribers of the change.
        /// Throws <see cref="SqlSafetyException"/> if the new configuration contains any blocked SQL patterns.
        /// </summary>
        public void UpdateConfig(DashboardConfigRoot newConfig)
        {
            var violations = CollectSafetyViolations(newConfig);
            if (violations.Count > 0)
            {
                throw new SqlSafetyException(
                    $"Dashboard configuration rejected: {violations.Count} unsafe query(s) detected. First: {violations[0]}",
                    "dashboard-config",
                    violations[0]);
            }
            _config = newConfig;
            RebuildQueryCache(_config);
            Save();
            NotifyChanged();
        }

        /// <summary>
        /// Scans all panel and support queries in the configuration for blocked SQL patterns.
        /// Returns a list of violation descriptions; empty if the configuration is safe.
        /// </summary>
        private static List<string> CollectSafetyViolations(DashboardConfigRoot config)
        {
            var violations = new List<string>();
            foreach (var dashboard in config.Dashboards ?? new List<DashboardDefinition>())
            {
                foreach (var panel in dashboard.Panels ?? new List<PanelDefinition>())
                {
                    if (string.IsNullOrEmpty(panel.Id) || panel.Query == null) continue;
                    var result = SqlSafetyValidator.Validate(panel.Query.SqlServer);
                    if (!result.IsSafe)
                        violations.Add($"Panel '{panel.Id}': {result.Reason}");
                }
            }
            foreach (var kvp in config.SupportQueries ?? new Dictionary<string, QueryPair>())
            {
                if (kvp.Value == null) continue;
                var result = SqlSafetyValidator.Validate(kvp.Value.SqlServer);
                if (!result.IsSafe)
                    violations.Add($"Support query '{kvp.Key}': {result.Reason}");
            }
            return violations;
        }

        public DashboardConfigService(ILogger<DashboardConfigService> logger)
        {
            _logger = logger;
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "dashboard-config.json");
            _backupPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "dashboard-config.backup.json");
            _config = Load();
            SetupFileWatcher();
        }

        /// <summary>
        /// Sets up a FileSystemWatcher to monitor JSON files in the app directory for changes.
        /// When a JSON file changes, reloads the dashboard configuration.
        /// </summary>
        private void SetupFileWatcher()
        {
            try
            {
                var configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
                if (!Directory.Exists(configDir))
                {
                    _logger.LogWarning("Config directory not found: {ConfigDir}", configDir);
                    return;
                }

                _watcher = new FileSystemWatcher(configDir)
                {
                    Filter = "*.json",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                    IncludeSubdirectories = false
                };

                _watcher.Changed += OnJsonFileChanged;
                _logger.LogDebug("File watcher started for *.json in {ConfigDir}", configDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to setup config file watcher");
            }
        }

        /// <summary>
        /// Handles JSON file change events. Only reloads dashboard-config.json.
        /// </summary>
        private void OnJsonFileChanged(object sender, FileSystemEventArgs e)
        {
            if (e.Name?.Equals("dashboard-config.json", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogInformation("Detected change in {FileName}, reloading configuration", e.Name);

                // Small delay to ensure file is fully written
                Task.Delay(100).ContinueWith(_ =>
                {
                    try
                    {
                        var newConfig = Load();
                        _logger.LogInformation("Reloaded config with {Count} dashboards", newConfig.Dashboards.Count);
                        NotifyChanged();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error reloading dashboard config");
                    }
                });
            }
        }

        /// <summary>
        /// Loads configuration from disk. If the file does not exist or deserialization fails,
        /// generates defaults, persists them, and returns the default configuration.
        /// </summary>
        private DashboardConfigRoot Load()
        {
            if (File.Exists(_configPath))
            {
                try
                {
                    // Optimization: Use stream for large configs
                    var config = LoadConfigFromStreamAsync().GetAwaiter().GetResult();
                    if (config != null)
                    {
                        _logger.LogDebug("Deserialized config with {Count} dashboards", config.Dashboards?.Count ?? 0);
                        
                        _config = config;
                        if (PatchMissingDashboards(config))
                        {
                            Save();
                        }
                        RebuildQueryCache(config);
                        return config;
                    }
                }
                catch (Exception ex)
                {
                    // Enterprise Polish: Backup the corrupt file for analysis instead of just swallowing the error
                    var corruptPath = _configPath + $".corrupt.{DateTime.Now:yyyyMMddHHmmss}.json";
                    try { File.Copy(_configPath, corruptPath, true); } catch { /* Best effort */ }

                    _logger.LogError(ex, "Error deserializing config from '{ConfigPath}'. Corrupt file backed up to '{CorruptPath}'. Falling back to defaults",
                        _configPath, corruptPath);
                }
            }

            var defaultConfig = DefaultConfigGenerator.Generate();
            _logger.LogInformation("Using default config with {Count} dashboards", defaultConfig.Dashboards.Count);
            _config = defaultConfig;
            RebuildQueryCache(defaultConfig);
            // Only save defaults if no valid config file exists - don't overwrite existing files on deserialization errors
            if (!File.Exists(_configPath))
            {
                Save();
            }
            return defaultConfig;
        }

        /// <summary>
        /// Ensures that dashboards defined in the default configuration exist in the loaded configuration.
        /// Returns true if any dashboards were added.
        /// </summary>
        private bool PatchMissingDashboards(DashboardConfigRoot loadedConfig)
        {
            var defaultConfig = DefaultConfigGenerator.Generate();
            bool modified = false;

            if (loadedConfig.Dashboards == null) loadedConfig.Dashboards = new List<DashboardDefinition>();
            if (loadedConfig.SupportQueries == null) loadedConfig.SupportQueries = new Dictionary<string, QueryPair>();

            foreach (var defaultDashboard in defaultConfig.Dashboards)
            {
                if (!loadedConfig.Dashboards.Any(d => string.Equals(d.Id, defaultDashboard.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    loadedConfig.Dashboards.Add(defaultDashboard);
                    modified = true;
                }
            }
            return modified;
        }

        /// <summary>
        /// Logs a debug warning if the given SQL text contains a blocked pattern.
        /// Used during cache rebuild so violations are visible at load/hot-reload time.
        /// </summary>
        private static void WarnIfUnsafe(string queryId, string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return;
            var result = SqlSafetyValidator.Validate(sql);
            if (!result.IsSafe)
                Serilog.Log.Warning("SECURITY — query '{QueryId}' blocked: {Reason}", queryId, result.Reason);
        }

        /// <summary>
        /// Builds the O(1) query lookup cache from panel definitions and support queries.
        /// Called once on load and after each save/reset.
        /// </summary>
        private void RebuildQueryCache(DashboardConfigRoot config)
        {
            var cache = new Dictionary<string, QueryPair>(StringComparer.OrdinalIgnoreCase);
            var typeCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Index all panels across all dashboards
            foreach (var dashboard in config.Dashboards)
            {
                foreach (var panel in dashboard.Panels)
                {
                    if (!string.IsNullOrEmpty(panel.Id))
                    {
                        cache[panel.Id] = panel.Query;
                        typeCache[panel.Id] = panel.PanelType;
                        WarnIfUnsafe(panel.Id, panel.Query?.SqlServer);
                    }
                }
            }

            // Index all support queries (support queries override panels if there's a collision)
            foreach (var kvp in config.SupportQueries)
            {
                cache[kvp.Key] = kvp.Value;
                WarnIfUnsafe(kvp.Key, kvp.Value?.SqlServer);
            }

            _queryCache = cache;
            _panelTypeCache = typeCache;
        }

        /// <summary>
        /// Persists the current configuration to disk. Creates a backup of the previous file first.
        /// </summary>
        public void Save()
        {
            if (File.Exists(_configPath))
            {
                File.Copy(_configPath, _backupPath, overwrite: true);
            }

            var json = JsonSerializer.Serialize(_config, SerializerOptions);
            File.WriteAllText(_configPath, json);

            // Rebuild cache after save in case config was mutated
            RebuildQueryCache(_config);
        }

        /// <summary>
        /// Fires the <see cref="OnConfigChanged"/> event so subscribers (dashboards) can re-render.
        /// </summary>
        public void NotifyChanged()
        {
            OnConfigChanged?.Invoke();
        }

        /// <summary>
        /// Saves the current configuration and notifies subscribers of the change.
        /// </summary>
        public void SaveAndNotify()
        {
            Save();
            NotifyChanged();
        }

        /// <summary>
        /// Replaces the current configuration with the default, saves it, and notifies subscribers.
        /// </summary>
        public void ResetToDefault()
        {
            _config = DefaultConfigGenerator.Generate();
            RebuildQueryCache(_config);
            Save();
            NotifyChanged();
        }

        /// <summary>
        /// Resolves the SQL query text for a given query identifier and data source type.
        /// Uses O(1) dictionary lookup instead of scanning all panels.
        /// </summary>
        /// <param name="queryId">The query/panel identifier (e.g., "repo.perf_counters").</param>
        /// <param name="dataSourceType">"SqlServer" or "liveQueries".</param>
        /// <returns>The SQL query string for the specified data source.</returns>
        /// <exception cref="KeyNotFoundException">Thrown when the query ID is not found.</exception>
        public string GetQuery(string queryId, string dataSourceType)
        {
            if (_queryCache.TryGetValue(queryId, out var queryPair))
            {
                return queryPair.SqlServer;
            }

            throw new KeyNotFoundException($"Query '{queryId}' not found in dashboard configuration.");
        }

        /// <summary>
        /// Returns true if the query ID exists in the cache.
        /// </summary>
        /// <param name="queryId">The query/panel identifier to look up.</param>
        public bool HasQuery(string queryId)
        {
            return _queryCache.ContainsKey(queryId);
        }

        /// <summary>
        /// Gets the effective default database for a panel, inheriting from dashboard if not specified.
        /// </summary>
        public string GetEffectiveDefaultDatabase(string queryId)
        {
            // Find the panel and its dashboard
            foreach (var dashboard in _config.Dashboards)
            {
                var panel = dashboard.Panels.FirstOrDefault(p => string.Equals(p.Id, queryId, StringComparison.OrdinalIgnoreCase));
                if (panel != null)
                {
                    return panel.GetEffectiveDefaultDatabase(dashboard.DefaultDatabase);
                }
            }
            return "master"; // fallback
        }

        /// <summary>
        /// Gets the panel type for a given query ID using O(1) cache lookup.
        /// </summary>
        public string GetPanelType(string queryId)
        {
            return _panelTypeCache.TryGetValue(queryId, out var panelType) ? panelType : "Unknown";
        }
    }
}
