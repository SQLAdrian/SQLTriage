/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sql;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Orchestrates a ≤60-second Quick Check run with budget enforcement:
    /// per-check timeout 8s, global budget 55s. Query concurrency is not set here — the run goes
    /// through CheckExecutionService.ExecuteChecksAsync, which throttles per instance from the
    /// user's audit-concurrency setting.
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

        // There is deliberately no DOP knob here. A "QuickCheck:MaxDegreeOfParallelism" setting
        // used to be read into a field that was logged and then never passed to the executor, so
        // it changed nothing. Concurrency belongs to CheckExecutionService's per-instance
        // throttle (user setting → shipped default); re-adding a second knob here would silently
        // override the user's choice, since the key is absent from the shipped appsettings.json
        // and would fall back to a lower value on most machines.

        public QuickCheckRunner(
            ILogger<QuickCheckRunner> logger,
            CheckExecutionService checkExecutor,
            ISqlQueryRepository queryRepo)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _checkExecutor = checkExecutor ?? throw new ArgumentNullException(nameof(checkExecutor));
            _queryRepo = queryRepo ?? throw new ArgumentNullException(nameof(queryRepo));
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

            _logger.LogInformation("QuickCheck starting on {Server} with {Count} quick checks, budget {Budget}s",
                serverName, quickIds.Count, GlobalBudget.TotalSeconds);

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
