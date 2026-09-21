/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Defect C, end-to-end. AlertBreakerOutcomeTests pins how the breaker responds to each
    /// reachability verdict; this pins the WIRING that produces the verdict — that a real alert
    /// evaluation against a server that cannot be reached ends up recording a breaker FAILURE, not
    /// the false success the orchestrator's <c>result.Success</c> flag used to produce.
    ///
    /// <para>The endpoint is 127.0.0.1 on TCP port 1 — nothing listens there, so the connection is
    /// refused immediately (no login-timeout wait) and SqlClient raises a SqlException, which is the
    /// exact exception class the production handler classifies as Unreachable. No SQL Server instance
    /// is touched, so this is not a live-instance test and does not depend on old2017/new2022.</para>
    /// </summary>
    public class AlertBreakerLiveEndpointTests
    {
        /// <summary>Runs the work inline, exactly as the real orchestrator does on the happy path:
        /// the delegate's own exception handling is what decides whether Success is true.</summary>
        private sealed class InlineOrchestrator : IQueryOrchestrator
        {
            public async Task<QueryResult> EnqueueAsync(
                QueryRequest request, QueryPriority priority, CancellationToken cancellationToken = default)
            {
                try
                {
                    await request.Work(cancellationToken);
                    return new QueryResult { QueryId = request.QueryId, Success = true };
                }
                catch (Exception ex)
                {
                    return new QueryResult { QueryId = request.QueryId, Success = false, Exception = ex };
                }
            }

            public Task<OrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new OrchestratorHealth());
            public Task<OrchestratorMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new OrchestratorMetrics());
            public void UpdateLimits(int globalConcurrency, int perServerConcurrency) { }
            public void Start() { }
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        [Fact]
        public async Task UnreachableServer_RecordsBreakerFailure_AndTheOrchestratorReportsSuccess()
        {
            var breaker = new ServerCircuitBreakerService(
                NullLogger<ServerCircuitBreakerService>.Instance, audit: null);

            var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
            var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);
            using var cache = new liveQueriesCacheStore();

            var svc = new AlertEvaluationService(
                NullLogger<AlertEvaluationService>.Instance,
                new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance),
                new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
                new AlertingService(NullLogger<AlertingService>.Instance),
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                new ToastService(),
                channels,
                cache,
                new InlineOrchestrator(),
                breaker: breaker);
            svc.DryRun = true; // nothing persisted, nothing dispatched

            const string deadEndpoint = "127.0.0.1,1"; // nothing listens here — refused, not timed out
            var connection = new ServerConnection
            {
                Id = Guid.NewGuid().ToString(),
                ServerNames = deadEndpoint,
                UseWindowsAuthentication = true,
                ConnectionTimeout = 2,
                IsEnabled = true
            };
            var alert = new AlertDefinition
            {
                Id = "breaker-wiring-probe",
                Name = "Breaker wiring probe",
                Query = "SELECT 1",
                Operator = "greater_than",
                Thresholds = new AlertThresholds { Warning = 0 }
            };

            // Three cycles, exactly as the alert loop would run them.
            for (int i = 0; i < 3; i++)
            {
                await svc.ThrottledEvaluateAsync(
                    alert, connection, deadEndpoint, new AlertGlobalDefaults(), CancellationToken.None);
            }

            // The evaluation swallowed its own SqlException (deliberate — it feeds AlertsNoc's
            // Unknown state), so the orchestrator saw three clean successes…
            Assert.True(svc.HasEvaluationFailure(deadEndpoint),
                "The evaluation should have recorded a real failure for this server.");

            // …and the breaker must nonetheless be OPEN. Pre-fix, result.Success drove
            // RecordSuccess here and the circuit stayed shut no matter how dead the server was.
            Assert.False(breaker.ShouldAttempt(deadEndpoint),
                "Three failed evaluations against an unreachable endpoint must open the circuit.");

            Assert.True(breaker.ShouldAttempt("some-other-server"),
                "Back-off must stay scoped to the failing server.");

            svc.Dispose();
        }
    }
}
