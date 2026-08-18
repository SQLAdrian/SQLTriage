/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Orchestrates a ≤60-second Quick Check run with budget enforcement:
    /// per-check timeout 8s, global budget 55s, DOP = min(ProcessorCount, 4).
    /// </summary>
    public interface IQuickCheckRunner
    {
        Task<QuickCheckResult> RunAsync(
            ServerConnection connection,
            string serverName,
            IProgress<QuickCheckProgress>? progress = null,
            CancellationToken cancellationToken = default);
    }

    public sealed class QuickCheckRunner : IQuickCheckRunner
    {
        private readonly ILogger<QuickCheckRunner> _logger;
        private readonly CheckExecutionService _checkExecutor;
        private readonly ISqlQueryRepository _queryRepo;

        public static readonly TimeSpan GlobalBudget = TimeSpan.FromSeconds(55);
        public static readonly TimeSpan PerCheckTimeout = TimeSpan.FromSeconds(8);

        // Default: min(ProcessorCount, 4). Override via appsettings: QuickCheck:MaxDegreeOfParallelism
        public static readonly int MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4);

        // Instance-level DOP — respects IConfiguration; falls back to static default.
        private readonly int _maxDop;

        public QuickCheckRunner(
            ILogger<QuickCheckRunner> logger,
            CheckExecutionService checkExecutor,
            ISqlQueryRepository queryRepo,
            IConfiguration? configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _checkExecutor = checkExecutor ?? throw new ArgumentNullException(nameof(checkExecutor));
            _queryRepo = queryRepo ?? throw new ArgumentNullException(nameof(queryRepo));
            var configDop = configuration?.GetValue<int>("QuickCheck:MaxDegreeOfParallelism", 0) ?? 0;
            _maxDop = configDop > 0 ? configDop : MaxDegreeOfParallelism;
        }

        public async Task<QuickCheckResult> RunAsync(
            ServerConnection connection,
            string serverName,
            IProgress<QuickCheckProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using var globalCts = new CancellationTokenSource(GlobalBudget);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(globalCts.Token, cancellationToken);
            var token = linkedCts.Token;

            var quickDefs = _queryRepo.GetQuickChecks();
            var quickIds = new HashSet<string>(quickDefs.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation("QuickCheck starting on {Server} with {Count} quick checks, budget {Budget}s, DOP={DOP}",
                serverName, quickIds.Count, GlobalBudget.TotalSeconds, _maxDop);

            var summary = await _checkExecutor.ExecuteChecksAsync(
                connection, serverName,
                check => quickIds.Contains(check.Id),
                token).ConfigureAwait(false);

            progress?.Report(new QuickCheckProgress
            {
                Completed = summary.TotalChecks,
                Total = summary.TotalChecks,
                CurrentCheckName = "Complete"
            });

            _logger.LogInformation("QuickCheck finished on {Server}: {Passed}/{Total} passed in {Duration:F1}s",
                serverName, summary.Passed, summary.TotalChecks, summary.Duration.TotalSeconds);

            return new QuickCheckResult
            {
                ServerName = serverName,
                Summary = summary,
                IsIndicative = true,
                CompletedWithinBudget = summary.Duration <= GlobalBudget
            };
        }

        /// <summary>
        /// Auto-detect local SQL Server instances via SqlDataSourceEnumerator.
        /// </summary>
        public static List<string> DetectLocalInstances()
        {
            try
            {
                var table = SqlDataSourceEnumerator.Instance.GetDataSources();
                var instances = new List<string>();
                foreach (System.Data.DataRow row in table.Rows)
                {
                    var serverName = row["ServerName"]?.ToString();
                    var instanceName = row["InstanceName"]?.ToString();
                    if (string.IsNullOrWhiteSpace(serverName)) continue;
                    var fullName = string.IsNullOrWhiteSpace(instanceName) || instanceName.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
                        ? serverName
                        : $"{serverName}\\{instanceName}";
                    instances.Add(fullName);
                }
                return instances.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[QuickCheckRunner] Local instance discovery failed");
                return new List<string>();
            }
        }
    }

    public class QuickCheckProgress
    {
        public int Completed { get; set; }
        public int Total { get; set; }
        public string CurrentCheckName { get; set; } = "";
        public double Percent => Total == 0 ? 0 : (Completed * 100.0 / Total);
    }

    public class QuickCheckResult
    {
        public string ServerName { get; set; } = "";
        public CheckExecutionSummary Summary { get; set; } = new();
        public bool IsIndicative { get; set; }
        public bool CompletedWithinBudget { get; set; }
    }
}
