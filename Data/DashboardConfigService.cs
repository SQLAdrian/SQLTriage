/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data
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
        /// Rebuilt on Load() and Save().
        /// </summary>
        private Dictionary<string, QueryPair> _queryCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// O(1) lookup cache mapping queryId -> PanelType string.
        /// Avoids O(dashboards × panels) scan on every delta-fetch call.
        /// </summary>
        private Dictionary<string, string> _panelTypeCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The TimeSeries panels that opted into retained history (<c>retainHistory: true</c>).
        /// Rebuilt with the other caches. Read on every TimeSeries fetch and on every collector tick,
        /// so it is a set lookup rather than a scan.
        /// </summary>
        private HashSet<string> _retainHistoryCache = new(StringComparer.OrdinalIgnoreCase);

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

        // Optimization: Use streams for large JSON parsing.
        // A parse failure is deliberately NOT caught here: it must propagate to Load()'s handler,
        // which backs the corrupt file up to dashboard-config.json.corrupt.<timestamp>.json and logs
        // at Error. A local catch that swallowed it (returning defaults with only a Warning) hid the
        // corruption — no backup, no Error line — and silently collapsed the product from its full
        // dashboard set to DefaultConfigGenerator's three (honesty-hunt dashboards-r1-11).
        private DashboardConfigRoot LoadConfigFromDisk()
        {
            var configPath = _configPath;
            if (!File.Exists(configPath))
                configPath = _backupPath;

            if (!File.Exists(configPath))
                return DefaultConfigGenerator.Generate();

            using var stream = File.OpenRead(configPath);
            return JsonSerializer.Deserialize<DashboardConfigRoot>(stream, SerializerOptions)
                   ?? DefaultConfigGenerator.Generate();
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
            // Deliver any panel this product shipped broken and has since corrected to an install that
            // still carries the stock body, BEFORE Load reads the file. Only rewrites panels an operator
            // never edited; announces and returns on anything unreadable (never throws — this is a
            // constructor). Same shape as AlertDefinitionMigrator / ScriptConfigurationMigrator.
            DashboardConfigMigrator.RepairSupersededPanels(_configPath, _logger);
            _config = Load();
            SetupFileWatcher();
        }

        /// <summary>
        /// Test seam (house pattern): builds the query caches directly from an in-memory config,
        /// with no disk I/O and no file watcher. Exercises the load-path cache rebuild in isolation
        /// (the fail-closed skip of blocked queries). Mirrors ScheduledTaskDefinitionService's path
        /// seam.
        /// </summary>
        internal DashboardConfigService(ILogger<DashboardConfigService> logger, DashboardConfigRoot config)
        {
            _logger = logger;
            _configPath = string.Empty;
            _backupPath = string.Empty;
            _config = config;
            RebuildQueryCache(_config);
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
                    var config = LoadConfigFromDisk();
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
        /// Judges a query for the cache rebuild: warns when the SQL carries a dangerous OS/system-exec
        /// pattern, and returns whether it is safe to place in the executable cache. A null/empty query
        /// has nothing to run and is treated as cacheable.
        ///
        /// <para>This is the load-path counterpart to <see cref="UpdateConfig"/>'s throw. The save
        /// path rejects an unsafe config outright; the load path cannot throw (a hand-edited or
        /// tampered config file would take the whole dashboard down), so instead it FAILS CLOSED —
        /// the blocked query is left out of the cache and the executor can never pull it — while
        /// still logging the violation. Before the exec-surface-safety lane, the load path warned but
        /// cached and ran the blocked query anyway (security-5).</para>
        ///
        /// <para><b>Why the per-statement guard here, not the wall (fix round 1, 2026-08-31).</b> The
        /// load path gates with <see cref="DangerousExecGuard.Inspect"/> (a per-statement OS/system-exec
        /// gate), NOT the <see cref="SqlSafetyValidator"/> read-only wall the save path uses. The wall
        /// waives a whole BATCH that contains any <c>SELECT … FROM sys.*</c> read, so a tampered panel
        /// <c>SELECT 1 FROM sys.databases; EXEC xp_cmdshell …</c> would be waived, cached, and run raw
        /// (the batch-waiver bypass this lane closed on the operator surfaces stayed open on the
        /// dashboard). The wall also FALSE-POSITIVED on a shipped read-only panel whose result string
        /// merely displays a <c>CREATE INDEX</c> recommendation, dropping it from the cache. The
        /// per-statement guard closes the bypass (each statement is judged alone) and clears the
        /// false-positive (it blanks string literals and does not block maintenance DDL such as
        /// CREATE INDEX). The save path keeps the stricter wall as defense in depth — its stricter
        /// save-time posture is deliberate; only the LOAD path was mis-gated.</para>
        /// </summary>
        private static bool WarnIfUnsafe(string queryId, string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return true;
            var verdict = DangerousExecGuard.Inspect(sql);
            if (!verdict.IsAllowed)
            {
                Serilog.Log.Warning("SECURITY: query '{QueryId}' blocked: {Reason}", queryId, verdict.Reason);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Builds the O(1) query lookup cache from panel definitions and support queries.
        /// Called once on load and after each save/reset.
        /// </summary>
        private void RebuildQueryCache(DashboardConfigRoot config)
        {
            var cache = new Dictionary<string, QueryPair>(StringComparer.OrdinalIgnoreCase);
            var typeCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var retainCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void IndexPanel(PanelDefinition panel)
            {
                if (string.IsNullOrEmpty(panel.Id)) return;
                // Fail closed: if the panel's query trips the per-statement dangerous-exec guard it is
                // left out of the executable cache entirely (query, type, and retention), so no executor
                // can pull it. The violation is still logged. See WarnIfUnsafe for why the load path
                // uses the guard rather than the wall, and skips rather than throws.
                if (!WarnIfUnsafe(panel.Id, panel.Query?.SqlServer))
                    return;
                cache[panel.Id] = panel.Query!;
                typeCache[panel.Id] = panel.PanelType!;
                // Retention is only meaningful for a series of measurements over time. A retainHistory
                // flag on any other panel type is ignored rather than honoured, so a stray flag in an
                // edited config cannot start writing rows nothing can read.
                if (panel.RetainHistory && string.Equals(panel.PanelType, "TimeSeries", StringComparison.OrdinalIgnoreCase))
                    retainCache.Add(panel.Id);
            }

            // Index all panels across all dashboards — includes tab panels for merged dashboards.
            foreach (var dashboard in config.Dashboards)
            {
                foreach (var panel in dashboard.Panels)
                    IndexPanel(panel);
                if (dashboard.Tabs != null)
                    foreach (var tab in dashboard.Tabs)
                        foreach (var panel in tab.Panels)
                            IndexPanel(panel);
            }

            // Index all support queries (support queries override panels if there's a collision).
            // Fail closed here too: a blocked support query is skipped, not cached (security-5).
            foreach (var kvp in config.SupportQueries)
            {
                if (!WarnIfUnsafe(kvp.Key, kvp.Value?.SqlServer))
                    continue;
                cache[kvp.Key] = kvp.Value!;
            }

            _queryCache = cache;
            _panelTypeCache = typeCache;
            _retainHistoryCache = retainCache;
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
        /// Returns (true, null) if json deserialises into a DashboardConfigRoot, otherwise
        /// (false, errorMessage). Used by DashboardEditor for live validation.
        /// </summary>
        public (bool valid, string? error) ValidateJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return (false, "JSON is empty.");
            try
            {
                var config = JsonSerializer.Deserialize<DashboardConfigRoot>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (config == null)
                    return (false, "JSON parsed to null.");
                return (true, null);
            }
            catch (JsonException ex)
            {
                return (false, $"Line {ex.LineNumber + 1}, position {ex.BytePositionInLine}: {ex.Message}");
            }
        }

        /// <summary>
        /// The dashboards an operator can restore to the state this build ships, in catalogue order.
        /// Empty when the shipped catalogue is not embedded in this build, so the Settings picker offers
        /// nothing rather than offering a control that would refuse every id.
        /// </summary>
        public IReadOnlyList<(string Id, string Title)> GetRestorableDashboards() =>
            DashboardFactoryReset.ListShipped().Select(d => (d.Id, d.Title)).ToList();

        /// <summary>What a restore did, in the terms the Settings page reports to the operator.</summary>
        /// <param name="Success">The operator got what they asked for. False means nothing was written
        /// AND the dashboard is not at its shipped state.</param>
        /// <param name="Changed">The file on disk changed. False with <paramref name="Success"/> true is
        /// the already-at-shipped-state case.</param>
        public sealed record DashboardResetReport(bool Success, bool Changed, string Message);

        /// <summary>
        /// Restores ONE dashboard to the state this build ships and reloads the configuration.
        ///
        /// <para>This is the real factory reset: it replaces that dashboard's panels, queries, titles,
        /// thresholds, layout, tabs and every <c>enabled</c> flag with the shipped ones, and reads or
        /// writes no other dashboard. The previous file is copied to dashboard-config.backup.json first,
        /// and only when there is something to write. It replaced <c>ResetToDefault()</c>, which called
        /// <c>DefaultConfigGenerator.Generate()</c> — three dashboards where the product ships 27 — and
        /// saved that over the operator's file. That method had no production caller, which is the only
        /// reason it never cost anyone 24 dashboards; it is gone rather than left for the next caller.
        /// DECISIONS 2026-08-26 18:23, ruling 2.</para>
        /// </summary>
        public DashboardResetReport ResetDashboardToShipped(string dashboardId)
        {
            if (string.IsNullOrWhiteSpace(dashboardId))
                return new DashboardResetReport(false, false, "Choose a dashboard to restore first.");

            var result = DashboardFactoryReset.ResetDashboard(_configPath, _backupPath, dashboardId, _logger);

            if (result.Changed)
            {
                // Read back what was written rather than patching the in-memory model, so what the app
                // now serves is what is on disk. The file watcher would also fire; doing it here means
                // the operator's next render is correct whether or not it did.
                _config = Load();
                NotifyChanged();
            }

            var message = result.Outcome switch
            {
                DashboardFactoryReset.ResetOutcome.Restored =>
                    $"Restored \"{result.Title}\" to the shipped defaults: {result.PanelCount} panel(s). "
                    + "No other dashboard was changed. The previous file is in Config/dashboard-config.backup.json.",
                DashboardFactoryReset.ResetOutcome.Added =>
                    $"\"{result.Title}\" was not in your configuration and has been added back at the shipped "
                    + $"defaults: {result.PanelCount} panel(s). No other dashboard was changed.",
                DashboardFactoryReset.ResetOutcome.AlreadyShipped =>
                    $"\"{result.Title}\" already matches the shipped defaults. Nothing was written.",
                DashboardFactoryReset.ResetOutcome.NotInCatalogue =>
                    $"This version ships no dashboard called \"{dashboardId}\", so there is no shipped state "
                    + "to restore. Nothing was written.",
                _ =>
                    "Could not read Config/dashboard-config.json, so nothing was restored. The file has not "
                    + "been replaced or rewritten. Check the log for the reason."
            };

            var success = result.Outcome is DashboardFactoryReset.ResetOutcome.Restored
                or DashboardFactoryReset.ResetOutcome.Added
                or DashboardFactoryReset.ResetOutcome.AlreadyShipped;

            return new DashboardResetReport(success, result.Changed, message);
        }

        /// <summary>
        /// Resolves the SQL query text for a given query identifier and data source type.
        /// Uses O(1) dictionary lookup instead of scanning all panels.
        /// </summary>
        /// <param name="queryId">The query/panel identifier (e.g., "repo.perf_counters").</param>
        /// <param name="dataSourceType">"SqlServer" or "liveQueries".</param>
        /// <returns>The SQL query string for the specified data source.</returns>
        /// <exception cref="KeyNotFoundException">Thrown when the query ID is not found.</exception>
        public string GetQuery(string queryId, string dataSourceType, int sqlMajorVersion = 0)
        {
            if (_queryCache.TryGetValue(queryId, out var queryPair))
            {
                // Use legacy query for SQL Server 2017 and earlier (major version < 15)
                if (sqlMajorVersion > 0 && sqlMajorVersion < 15 && !string.IsNullOrEmpty(queryPair.SqlServerLegacy))
                    return queryPair.SqlServerLegacy;
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
        /// Finds a panel by query ID, searching both a dashboard's flat <c>Panels</c> list and,
        /// for tabbed dashboards (e.g. memory's Overview/Analysis split), every tab's panels.
        /// Returns the panel together with the dashboard that owns it (needed for default-database
        /// inheritance). Not cached — called only from the two lookups below, which are per-query,
        /// per-load operations, not hot-path per-row work.
        /// </summary>
        private (PanelDefinition panel, DashboardDefinition dashboard)? FindPanelAndDashboard(string queryId)
        {
            foreach (var dashboard in _config.Dashboards)
            {
                var panel = dashboard.Panels.FirstOrDefault(p => string.Equals(p.Id, queryId, StringComparison.OrdinalIgnoreCase));
                if (panel != null)
                    return (panel, dashboard);

                if (dashboard.Tabs != null)
                {
                    foreach (var tab in dashboard.Tabs)
                    {
                        var tabPanel = tab.Panels.FirstOrDefault(p => string.Equals(p.Id, queryId, StringComparison.OrdinalIgnoreCase));
                        if (tabPanel != null)
                            return (tabPanel, dashboard);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Gets the effective default database for a panel, inheriting from dashboard if not specified.
        /// </summary>
        public string GetEffectiveDefaultDatabase(string queryId)
        {
            var found = FindPanelAndDashboard(queryId);
            return found.HasValue
                ? found.Value.panel.GetEffectiveDefaultDatabase(found.Value.dashboard.DefaultDatabase)
                : "master"; // fallback
        }

        /// <summary>
        /// Gets a panel's own <c>requiresDatabase</c> value (raw, not inherited) for a query ID.
        /// <c>null</c> means the panel is absent (falls back to "no opt-out") or its
        /// <c>requiresDatabase</c> field is unset (inherits the dashboard's gate — today's
        /// behaviour). <c>""</c> means the panel explicitly opted out of the dashboard's database
        /// requirement (see <see cref="PanelDefinition.RequiresDatabase"/>) — the discriminator
        /// <see cref="DashboardDatabaseResolver.Resolve"/> uses to decide whether the panel should
        /// ever be retargeted by <see cref="ServerConnectionManager.CurrentServer"/>'s Database
        /// override (e.g. the dashboard-forced catalog, or the Query Store database selector).
        /// </summary>
        public string? GetPanelRequiresDatabase(string queryId)
        {
            return FindPanelAndDashboard(queryId)?.panel.RequiresDatabase;
        }

        /// <summary>
        /// Gets the panel type for a given query ID using O(1) cache lookup.
        /// </summary>
        public string GetPanelType(string queryId)
        {
            return _panelTypeCache.TryGetValue(queryId, out var panelType) ? panelType : "Unknown";
        }

        /// <summary>
        /// True when this panel opted into retained history. O(1); a panel that is absent, disabled at
        /// the config level or not a TimeSeries returns false.
        /// </summary>
        public bool RetainsHistory(string queryId) => _retainHistoryCache.Contains(queryId);

        /// <summary>
        /// Every panel id that opted into retained history. This is the collector's work list, so it is
        /// also the exact set of panels that cost disk — the answer to "what is retention keeping?".
        /// </summary>
        public IReadOnlyCollection<string> GetHistoryRetainedPanelIds() => _retainHistoryCache.ToArray();
    }
}
