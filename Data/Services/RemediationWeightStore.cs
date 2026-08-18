/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationWeightStore — runtime overlay for per-check remediation effort (hours).
 *
 * Why: 310 of 372 checks ship with EffortHours = 0, which makes the CIO Dashboard
 * cost calc collapse to near-zero on real estates. The corpus YAMLs don't carry
 * numeric effort. This store lets the maintainer tune effort per-check without
 * touching the corpus, persisting overrides to Config/sql-check-weights-override.json.
 *
 * Read path: GetEffort(checkId) returns the override if present, else the baseline
 *            EffortHours from sql-checks.json.
 *
 * Worklist 2026-05-12 — replaces the reverse-from-governance remediation cost model.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services
{
    public class RemediationWeightStore
    {
        private readonly ILogger<RemediationWeightStore> _logger;
        private readonly string _overridePath;
        private readonly object _lock = new();
        private Dictionary<string, double> _overrides = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastUpdatedUtc = DateTime.MinValue;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public RemediationWeightStore(ILogger<RemediationWeightStore> logger)
        {
            _logger = logger;
            _overridePath = Path.Combine(AppContext.BaseDirectory, "Config", "sql-check-weights-override.json");
            Load();
        }

        public string OverridePath => _overridePath;
        public DateTime LastUpdatedUtc { get { lock (_lock) return _lastUpdatedUtc; } }

        private void Load()
        {
            try
            {
                if (!File.Exists(_overridePath))
                {
                    _logger.LogInformation("No remediation weight override file at {Path}; using baseline weights.", _overridePath);
                    return;
                }
                var json = File.ReadAllText(_overridePath);
                var payload = JsonSerializer.Deserialize<OverridePayload>(json, _jsonOptions);
                if (payload?.Weights == null) return;
                lock (_lock)
                {
                    _overrides = new Dictionary<string, double>(payload.Weights, StringComparer.OrdinalIgnoreCase);
                    _lastUpdatedUtc = payload.LastUpdatedUtc;
                }
                _logger.LogInformation("Loaded {Count} remediation weight overrides (last updated {Updated:u})",
                    _overrides.Count, _lastUpdatedUtc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load remediation weight overrides from {Path}", _overridePath);
            }
        }

        /// <summary>Returns the override hours for a check, or null if not overridden.</summary>
        public double? GetOverride(string checkId)
        {
            if (string.IsNullOrWhiteSpace(checkId)) return null;
            lock (_lock)
            {
                return _overrides.TryGetValue(checkId, out var v) ? v : (double?)null;
            }
        }

        /// <summary>
        /// Returns the effective effort hours for a check: override if set, else the baseline.
        /// Pass the baseline EffortHours from sql-checks.json as the fallback.
        /// </summary>
        public double GetEffort(string checkId, double baseline)
        {
            return GetOverride(checkId) ?? baseline;
        }

        /// <summary>
        /// Set or clear an override. Pass null to revert to baseline. Persists immediately.
        /// </summary>
        public void SetOverride(string checkId, double? hours)
        {
            if (string.IsNullOrWhiteSpace(checkId)) return;
            lock (_lock)
            {
                if (hours is null)
                    _overrides.Remove(checkId);
                else
                    _overrides[checkId] = Math.Max(0, hours.Value);
                _lastUpdatedUtc = DateTime.UtcNow;
                Save();
            }
        }

        /// <summary>Bulk update — caller already holds intent to overwrite all listed keys.</summary>
        public void SetOverrides(IDictionary<string, double?> updates)
        {
            if (updates == null || updates.Count == 0) return;
            lock (_lock)
            {
                foreach (var kvp in updates)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key)) continue;
                    if (kvp.Value is null)
                        _overrides.Remove(kvp.Key);
                    else
                        _overrides[kvp.Key] = Math.Max(0, kvp.Value.Value);
                }
                _lastUpdatedUtc = DateTime.UtcNow;
                Save();
            }
        }

        public int OverrideCount { get { lock (_lock) return _overrides.Count; } }

        public IReadOnlyDictionary<string, double> Snapshot()
        {
            lock (_lock)
            {
                return new Dictionary<string, double>(_overrides, StringComparer.OrdinalIgnoreCase);
            }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_overridePath)!);
                var payload = new OverridePayload
                {
                    SchemaVersion = 1,
                    LastUpdatedUtc = _lastUpdatedUtc,
                    Weights = new Dictionary<string, double>(_overrides, StringComparer.OrdinalIgnoreCase),
                };
                var tmp = _overridePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(payload, _jsonOptions));
                if (File.Exists(_overridePath)) File.Delete(_overridePath);
                File.Move(tmp, _overridePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save remediation weight overrides");
            }
        }

        private class OverridePayload
        {
            [JsonPropertyName("schemaVersion")]
            public int SchemaVersion { get; set; } = 1;

            [JsonPropertyName("lastUpdatedUtc")]
            public DateTime LastUpdatedUtc { get; set; }

            [JsonPropertyName("weights")]
            public Dictionary<string, double> Weights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
