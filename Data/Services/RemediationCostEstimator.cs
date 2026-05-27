/* Effort-to-cost mapping for remediation estimates.
   Reads CSV at startup, maps check names → effort hours → $295/hr. */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services
{
    public class RemediationCostEstimator
    {
        private readonly ILogger<RemediationCostEstimator> _logger;
        private readonly Dictionary<string, double> _effortByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _effortById = new(StringComparer.OrdinalIgnoreCase);
        private const double HourlyRate = 295.0;

        public RemediationCostEstimator(ILogger<RemediationCostEstimator> logger)
        {
            _logger = logger;
            LoadCsv();
        }

        private void LoadCsv()
        {
            var csvPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
                "research_output", "!🛠AllCheckTable🛠!.csv");

            // Also try the output directory copy
            if (!File.Exists(csvPath))
                csvPath = Path.Combine(AppContext.BaseDirectory, "research_output", "!🛠AllCheckTable🛠!.csv");

            if (!File.Exists(csvPath))
            {
                _logger.LogWarning("Effort CSV not found at {Path}. Cost estimates unavailable.", csvPath);
                return;
            }

            try
            {
                var lines = File.ReadAllLines(csvPath);
                for (int i = 1; i < lines.Length; i++) // skip header
                {
                    var cols = ParseCsvLine(lines[i]);
                    if (cols.Length < 14) continue;

                    var name = cols[13].Trim();    // Col 13: check name
                    var effortStr = cols[10].Trim(); // Col 10: effort hours

                    if (double.TryParse(effortStr, out var effort) && effort > 0)
                    {
                        _effortByName[name] = effort;
                        // Also map by internal ID (col 0) and blitz ID (col 2, col 4)
                        var internalId = cols[0].Trim();
                        var blitzId = cols[2].Trim();
                        var blitzOutputId = cols[4].Trim();
                        if (internalId.Length > 0) _effortById[internalId] = effort;
                        if (blitzId.Length > 0) _effortById[$"BLITZ_{blitzId}"] = effort;
                        if (blitzOutputId.Length > 0) _effortById[$"BLITZ_{blitzOutputId}"] = effort;
                    }
                }
                _logger.LogInformation("Loaded {Count} effort estimates from CSV", _effortByName.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse effort CSV");
            }
        }

        private static string[] ParseCsvLine(string line)
        {
            var result = new List<string>();
            bool inQuotes = false;
            int start = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') inQuotes = !inQuotes;
                else if (line[i] == ',' && !inQuotes)
                {
                    result.Add(line[start..i].Trim('"'));
                    start = i + 1;
                }
            }
            result.Add(line[start..].Trim('"'));
            return result.ToArray();
        }

        /// <summary>
        /// Get estimated remediation cost for a failing check.
        /// Returns null if no effort data available.
        /// </summary>
        public double? GetRemediationCost(string checkName)
        {
            if (string.IsNullOrWhiteSpace(checkName)) return null;
            if (_effortByName.TryGetValue(checkName, out var hours))
                return hours * HourlyRate;
            return null;
        }

        /// <summary>
        /// Calculate total estimated remediation cost for a set of failed checks.
        /// </summary>
        public (double TotalCost, int ChecksWithEstimates, int ChecksWithoutEstimates) CalculateTotalCost(
            IEnumerable<(string Name, bool Failed)> checkResults)
        {
            double total = 0;
            int withEstimates = 0;
            int withoutEstimates = 0;

            foreach (var (name, failed) in checkResults)
            {
                if (!failed) continue;
                var cost = GetRemediationCost(name);
                if (cost.HasValue)
                {
                    total += cost.Value;
                    withEstimates++;
                }
                else
                {
                    withoutEstimates++;
                }
            }

            return (total, withEstimates, withoutEstimates);
        }
    }
}
