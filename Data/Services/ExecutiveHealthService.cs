/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Service for computing executive health scores (0-100) from server health status.
    /// Provides trend analysis and weighted scoring based on critical indicators.
    /// </summary>
    public class ExecutiveHealthService
    {
        private readonly HealthCheckService _healthCheckService;

        /// <summary>
        /// Historical health scores per server for trend analysis.
        /// Key: ServerName, Value: List of (DateTime, Score) tuples
        /// </summary>
        private readonly ConcurrentDictionary<string, List<(DateTime Timestamp, int Score)>> _historicalScores = new();

        public ExecutiveHealthService(HealthCheckService healthCheckService)
        {
            _healthCheckService = healthCheckService ?? throw new ArgumentNullException(nameof(healthCheckService));
        }

        /// <summary>
        /// Computes a weighted 0-100 health score for the specified server.
        /// Higher scores indicate better health.
        /// </summary>
        public async Task<ExecutiveHealthScore> GetHealthScoreAsync(string serverName)
        {
            var healthStatus = _healthCheckService.GetCachedHealth(serverName);
            if (healthStatus == null)
                return new ExecutiveHealthScore { Score = 0, Severity = HealthSeverity.Unknown, Message = "No health data available" };

            var score = ComputeWeightedScore(healthStatus);
            var severity = MapScoreToSeverity(score);

            // Store historical score
            StoreHistoricalScore(serverName, score);

            // Calculate trend
            var trend = CalculateTrend(serverName);

            return new ExecutiveHealthScore
            {
                Score = score,
                Severity = severity,
                Trend = trend,
                Message = GetHealthMessage(severity),
                LastUpdated = healthStatus.LastUpdated ?? DateTime.Now
            };
        }

        /// <summary>
        /// Gets health scores for all servers.
        /// </summary>
        public async Task<Dictionary<string, ExecutiveHealthScore>> GetAllHealthScoresAsync()
        {
            var allHealth = _healthCheckService.GetAllHealth();
            var results = new Dictionary<string, ExecutiveHealthScore>();

            foreach (var kvp in allHealth)
            {
                results[kvp.Key] = await GetHealthScoreAsync(kvp.Key).ConfigureAwait(false);
            }

            return results;
        }

        private int ComputeWeightedScore(ServerHealthStatus status)
        {
            if (status.IsOnline != true)
                return 0; // Completely offline = 0 health

            int score = 100; // Start with perfect health

            // Connection status (20% weight)
            if (status.ConnectionSeverity == HealthSeverity.Critical)
                score -= 20;
            else if (status.ConnectionSeverity == HealthSeverity.Warning)
                score -= 10;

            // CPU utilization (25% weight)
            if (status.TotalCpuPercent.HasValue)
            {
                if (status.TotalCpuPercent >= 95) score -= 25; // Critical
                else if (status.TotalCpuPercent >= 80) score -= 15; // Warning
                else if (status.TotalCpuPercent >= 60) score -= 5; // Elevated
            }

            // Memory pressure (20% weight)
            if (status.RequestsWaitingForMemory > 0)
                score -= 20; // Any memory waits = critical
            else if (status.MemorySeverity == HealthSeverity.Warning)
                score -= 10;

            // Blocking (15% weight)
            if (status.BlockingSeverity == HealthSeverity.Critical)
                score -= 15;
            else if (status.BlockingSeverity == HealthSeverity.Warning)
                score -= 8;

            // Thread starvation (10% weight)
            if (status.ThreadsSeverity == HealthSeverity.Critical)
                score -= 10;
            else if (status.ThreadsSeverity == HealthSeverity.Warning)
                score -= 5;

            // Deadlocks (5% weight)
            if (status.DeadlockSeverity == HealthSeverity.Critical)
                score -= 5;

            // Wait times (5% weight)
            if (status.WaitSeverity == HealthSeverity.Critical)
                score -= 5;
            else if (status.WaitSeverity == HealthSeverity.Warning)
                score -= 3;

            return Math.Max(0, Math.Min(100, score));
        }

        private HealthSeverity MapScoreToSeverity(int score)
        {
            if (score >= 80) return HealthSeverity.Healthy;
            if (score >= 60) return HealthSeverity.Warning;
            return HealthSeverity.Critical;
        }

        private string GetHealthMessage(HealthSeverity severity)
        {
            return severity switch
            {
                HealthSeverity.Healthy => "Server is healthy",
                HealthSeverity.Warning => "Server needs attention",
                HealthSeverity.Critical => "Server requires immediate action",
                _ => "Health status unknown"
            };
        }

        private void StoreHistoricalScore(string serverName, int score)
        {
            var history = _historicalScores.GetOrAdd(serverName, _ => new List<(DateTime, int)>());

            // Keep only last 30 days of data
            var cutoff = DateTime.Now.AddDays(-30);
            history.RemoveAll(x => x.Timestamp < cutoff);

            history.Add((DateTime.Now, score));

            // Keep only last 100 entries per server
            if (history.Count > 100)
            {
                history.RemoveRange(0, history.Count - 100);
            }
        }

        private HealthTrend CalculateTrend(string serverName)
        {
            var history = _historicalScores.GetValueOrDefault(serverName);
            if (history == null || history.Count < 2)
                return HealthTrend.Stable;

            // Compare last 2 scores
            var recent = history.TakeLast(2).ToList();
            var previousScore = recent[0].Score;
            var currentScore = recent[1].Score;

            var difference = currentScore - previousScore;

            if (difference > 5) return HealthTrend.Improving;
            if (difference < -5) return HealthTrend.Degrading;

            return HealthTrend.Stable;
        }
    }

    /// <summary>
    /// Executive health score result
    /// </summary>
    public class ExecutiveHealthScore
    {
        public int Score { get; set; }
        public HealthSeverity Severity { get; set; }
        public HealthTrend Trend { get; set; }
        public string Message { get; set; } = "";
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Health trend direction
    /// </summary>
    public enum HealthTrend
    {
        Improving,
        Stable,
        Degrading
    }
}
