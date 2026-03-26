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
    /// Loads and manages SQL checks from sql-checks.json file.
    /// Provides automatic backup on save operations.
    /// </summary>
    public class CheckRepositoryService
    {
        private readonly ILogger<CheckRepositoryService> _logger;
        private readonly string _checksFilePath;
        private readonly string _backupFilePath;
        private List<SqlCheck> _checks = new();

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Event raised after checks have been saved.
        /// </summary>
        public event Action? OnChecksChanged;

        /// <summary>
        /// The currently loaded checks.
        /// </summary>
        public IReadOnlyList<SqlCheck> Checks => _checks.AsReadOnly();

        public CheckRepositoryService(ILogger<CheckRepositoryService> logger)
        {
            _logger = logger;
            _checksFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "sql-checks.json");
            _backupFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "sql-checks.backup.json");
        }

        /// <summary>
        /// Load checks from the JSON file.
        /// </summary>
        public async Task LoadChecksAsync()
        {
            if (!File.Exists(_checksFilePath))
            {
                _checks = new List<SqlCheck>();
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_checksFilePath);
                _checks = JsonSerializer.Deserialize<List<SqlCheck>>(json, SerializerOptions) ?? new List<SqlCheck>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading checks from '{FilePath}'. Starting with empty check list", _checksFilePath);
                _checks = new List<SqlCheck>();
            }
        }

        /// <summary>
        /// Save checks to the JSON file with automatic backup.
        /// </summary>
        public async Task SaveChecksAsync()
        {
            // Create backup of existing file
            if (File.Exists(_checksFilePath))
            {
                File.Copy(_checksFilePath, _backupFilePath, overwrite: true);
            }

            var json = JsonSerializer.Serialize(_checks, SerializerOptions);
            await File.WriteAllTextAsync(_checksFilePath, json);

            OnChecksChanged?.Invoke();
        }

        /// <summary>
        /// Get all checks.
        /// </summary>
        public List<SqlCheck> GetAllChecks() => _checks;

        /// <summary>
        /// Get checks by category.
        /// </summary>
        public List<SqlCheck> GetChecksByCategory(string category)
            => _checks.Where(c => c.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>
        /// Get enabled checks only.
        /// </summary>
        public List<SqlCheck> GetEnabledChecks()
            => _checks.Where(c => c.Enabled).ToList();

        /// <summary>
        /// Get unique categories from all checks.
        /// </summary>
        public List<string> GetCategories()
            => _checks.Select(c => c.Category).Distinct().OrderBy(c => c).ToList();

        /// <summary>
        /// Find a check by ID.
        /// </summary>
        public SqlCheck? GetCheckById(string id)
            => _checks.FirstOrDefault(c => c.Id == id);

        /// <summary>
        /// Add a new check.
        /// </summary>
        public void AddCheck(SqlCheck check)
        {
            _checks.Add(check);
        }

        /// <summary>
        /// Remove a check by ID.
        /// </summary>
        public bool RemoveCheck(string id)
        {
            var check = GetCheckById(id);
            if (check != null)
            {
                _checks.Remove(check);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Update a check's enabled status.
        /// </summary>
        public bool SetCheckEnabled(string id, bool enabled)
        {
            var check = GetCheckById(id);
            if (check != null)
            {
                check.Enabled = enabled;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Update multiple checks' enabled status.
        /// </summary>
        public void SetChecksEnabled(IEnumerable<string> checkIds, bool enabled)
        {
            foreach (var id in checkIds)
            {
                SetCheckEnabled(id, enabled);
            }
        }

        /// <summary>
        /// Get the path to the current backup file.
        /// </summary>
        public string GetBackupPath() => _backupFilePath;

        /// <summary>
        /// Restore checks from backup file.
        /// </summary>
        public async Task RestoreFromBackupAsync()
        {
            if (File.Exists(_backupFilePath))
            {
                var json = await File.ReadAllTextAsync(_backupFilePath);
                _checks = JsonSerializer.Deserialize<List<SqlCheck>>(json, SerializerOptions) ?? new List<SqlCheck>();
                await SaveChecksAsync();
            }
        }

        /// <summary>
        /// Import checks from an external JSON file (e.g., SQLMonitoring export).
        /// Supports both camelCase (SqlHealthAssessment) and PascalCase (SQLMonitoring) property names.
        /// New checks are added, existing checks (by ID) are updated.
        /// Returns (added, updated, skipped) counts.
        /// </summary>
        public async Task<(int Added, int Updated, int Skipped)> ImportChecksFromFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Import file not found: {filePath}");

            var json = await File.ReadAllTextAsync(filePath);

            // Support both camelCase and PascalCase JSON
            var importOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            };

            var importedChecks = JsonSerializer.Deserialize<List<SqlCheck>>(json, importOptions);
            if (importedChecks == null || importedChecks.Count == 0)
                return (0, 0, 0);

            int added = 0, updated = 0, skipped = 0;

            foreach (var imported in importedChecks)
            {
                if (string.IsNullOrWhiteSpace(imported.Id))
                {
                    skipped++;
                    continue;
                }

                var existing = GetCheckById(imported.Id);
                if (existing != null)
                {
                    // Update existing check with imported values
                    existing.Name = imported.Name;
                    existing.Description = imported.Description;
                    existing.Category = imported.Category;
                    existing.Severity = imported.Severity;
                    existing.SqlQuery = imported.SqlQuery;
                    existing.ExpectedValue = imported.ExpectedValue;
                    existing.Enabled = imported.Enabled;
                    existing.RecommendedAction = imported.RecommendedAction;
                    existing.Source = imported.Source;
                    existing.ExecutionType = imported.ExecutionType;
                    existing.RowCountCondition = imported.RowCountCondition;
                    existing.ResultInterpretation = imported.ResultInterpretation;
                    existing.Priority = imported.Priority;
                    existing.SeverityScore = imported.SeverityScore;
                    existing.Weight = imported.Weight;
                    existing.ExpectedState = imported.ExpectedState;
                    existing.CheckTriggered = imported.CheckTriggered;
                    existing.CheckCleared = imported.CheckCleared;
                    existing.DetailedRemediation = imported.DetailedRemediation;
                    existing.SupportType = imported.SupportType;
                    existing.ImpactScore = imported.ImpactScore;
                    existing.AdditionalNotes = imported.AdditionalNotes;
                    updated++;
                }
                else
                {
                    _checks.Add(imported);
                    added++;
                }
            }

            await SaveChecksAsync();

            _logger.LogInformation("Import complete: {Added} added, {Updated} updated, {Skipped} skipped from '{FilePath}'",
                added, updated, skipped, filePath);

            return (added, updated, skipped);
        }

        /// <summary>
        /// Gets checks filtered by severity levels.
        /// </summary>
        public List<SqlCheck> GetChecksBySeverity(params string[] severities)
        {
            var severitySet = new HashSet<string>(severities, StringComparer.OrdinalIgnoreCase);
            return _checks.Where(c => c.Enabled && severitySet.Contains(c.Severity)).ToList();
        }

        /// <summary>
        /// Gets the path to the checks file.
        /// </summary>
        public string GetChecksFilePath() => _checksFilePath;
    }
}
