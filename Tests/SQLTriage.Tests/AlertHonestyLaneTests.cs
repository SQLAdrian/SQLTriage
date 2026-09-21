/* In the name of God, the Merciful, the Compassionate */

using System;
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
    /// Cluster A of the 2026-08-26 alert honesty lane, exercised without a live server:
    /// the severity floor (r1-07), the error-log severity filter (r1-01), and the fail-closed
    /// contract for a special handler that cannot measure (r2-09 / the registry_check silence r2-01
    /// wires a handler for). The registry read itself is proved live against .\new2022 in the lane
    /// report; here we pin the structural behaviour a unit test can hold.
    /// </summary>
    public class AlertHonestyLaneTests
    {
        // ── r1-07: declared severity is a floor on the routed/displayed severity ──

        [Theory]
        // A shipped-Critical alert with only a warning threshold used to fire runtime "Warning"
        // and get dropped by every critical-only channel. It now floors at Critical.
        [InlineData("Critical", false, "Critical")]
        [InlineData("Critical", true, "Critical")]
        // The critical threshold always wins regardless of the declared label.
        [InlineData("Info", true, "Critical")]
        [InlineData("warning", true, "Critical")]
        // Non-critical declarations do not lift the floor: only an explicit "critical" does.
        [InlineData("High", false, "Warning")]
        [InlineData("Medium", false, "Warning")]
        [InlineData("warning", false, "Warning")]
        public void RuntimeSeverity_floors_at_the_declared_critical(string declared, bool criticalCrossed, string expected)
        {
            var alert = new AlertDefinition { Severity = declared };
            Assert.Equal(expected, AlertEvaluationService.RuntimeSeverity(alert, criticalCrossed));
        }

        // ── r1-01: error_log_severity counts sev>=17 lines, not every non-backup line ──

        [Fact]
        public void ErrorLogSeverity_scan_filters_by_severity_not_every_non_backup_line()
        {
            var sql = AlertEvaluationService.BuildErrorLogScanSql("error_log_severity");

            Assert.NotNull(sql);
            // The defect: a bare NOT LIKE '%Backup%' that counted every non-backup line in the
            // window while the catalogue showed a Sev>=17 filter nothing ran.
            Assert.DoesNotContain("NOT LIKE '%Backup%'", sql!, StringComparison.Ordinal);
            // The fix matches the shipped catalogue query: keep only Severity 17-19 and 2x.
            Assert.Contains("Severity: 1[7-9]", sql!, StringComparison.Ordinal);
            Assert.Contains("Severity: 2", sql!, StringComparison.Ordinal);
        }

        [Fact]
        public void ErrorLogScanSql_isNull_forAnUnknownAlertId()
        {
            Assert.Null(AlertEvaluationService.BuildErrorLogScanSql("not_a_scan_alert"));
        }

        // ── r2-01: an unreadable power plan is Unknown, never a fabricated "not High Performance" ──

        [Fact]
        public void PowerPlanCheck_sql_failsClosed_whenTheSchemeIsUnreadable()
        {
            var sql = AlertEvaluationService.BuildPowerPlanCheckSql();

            // The defect: xp_instance_regread does not throw when the key/value is absent — it prints
            // an informational message and leaves @scheme NULL, and the batch runs on. Without an
            // explicit NULL arm the CASE fell to ELSE and returned 2.0, firing a false Critical about a
            // host whose plan was never read (and clearing any prior evaluation-failure record). The
            // fail-closed arm makes the unread case surface as SQL NULL, which the handler's DBNull
            // guard turns into a null return = evaluation failure = Unknown.
            Assert.Contains("WHEN @scheme IS NULL THEN NULL", sql, StringComparison.Ordinal);
            // High Performance is the one healthy scheme; anything readable-but-different is a real fire.
            Assert.Contains("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", sql, StringComparison.Ordinal);
            // It reads the correct key (r2-01's other half), not the dead PowerUser path the JSON named.
            Assert.Contains(@"Control\Power\User\PowerSchemes", sql, StringComparison.Ordinal);
            Assert.DoesNotContain(@"Control\PowerUser\PowerSchemes", sql, StringComparison.Ordinal);
        }

        // ── r2-09 / r2-01: a special handler that cannot measure fails closed and loud ──

        [Fact]
        public async Task SpecialAlert_thatCannotReachTheServer_recordsAnEvaluationFailure()
        {
            using var svc = BuildEngine();
            svc.DryRun = true; // nothing persisted, nothing dispatched

            var (connection, deadEndpoint) = DeadEndpoint();
            var alert = new AlertDefinition
            {
                Id = "io_error",
                Name = "SQL I/O Error",
                QueryMode = "io_error_check",
                Operator = "greater_than",
                Thresholds = new AlertThresholds { Warning = 1 },
            };

            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());

            // Pre-fix the handler swallowed its SqlException, returned null, and EvaluateSpecialAlert
            // returned in silence — AlertsNoc then read a clean Ok for a server it never measured.
            Assert.True(svc.HasEvaluationFailure(deadEndpoint),
                "A special handler that could not reach the server must record an evaluation failure "
                + "so the NOC renders Unknown, not a clean Ok.");
        }

        [Fact]
        public async Task SpecialAlert_withNoHandlerForItsQueryMode_failsClosed()
        {
            using var svc = BuildEngine();
            svc.DryRun = true;

            var (connection, deadEndpoint) = DeadEndpoint();
            // This is exactly the shape windows_power_plan's registry_check was before r2-01 wired a
            // handler: a shipped queryMode with no case, falling to _ => null and reading as healthy.
            var alert = new AlertDefinition
            {
                Id = "mystery_mode",
                Name = "Mystery",
                QueryMode = "an_unwired_query_mode",
                Operator = "greater_than",
                Thresholds = new AlertThresholds { Warning = 1 },
            };

            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());

            Assert.True(svc.HasEvaluationFailure(deadEndpoint),
                "A special alert whose queryMode has no handler produced no measurement; it must fail "
                + "closed (record a failure), never read as a clean Ok.");
        }

        [Fact]
        public async Task SpecialAlert_thatMeasuresCleanly_clearsAPriorFailure()
        {
            using var svc = BuildEngine();
            svc.DryRun = true;

            var (connection, deadEndpoint) = DeadEndpoint();
            // connectivity_check turns unreachability into a VALUE (1) rather than an error, so it
            // always produces a measurement — the one special mode that never returns null.
            var alert = new AlertDefinition
            {
                Id = "instance_unreachable",
                Name = "Instance Unreachable",
                QueryMode = "connectivity_check",
                Operator = "greater_than",
                Thresholds = new AlertThresholds { Warning = 1 },
            };

            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());

            Assert.False(svc.HasEvaluationFailure(deadEndpoint),
                "connectivity_check returns a value (reachable/unreachable), so it measured this cycle "
                + "and must not be recorded as an evaluation failure.");
        }

        // ── helpers ──

        private static AlertEvaluationService BuildEngine()
        {
            var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
            var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);
            var cache = new liveQueriesCacheStore();

            return new AlertEvaluationService(
                NullLogger<AlertEvaluationService>.Instance,
                new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance),
                new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
                new AlertingService(NullLogger<AlertingService>.Instance),
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                new ToastService(),
                channels,
                cache,
                new InlineOrchestrator());
        }

        private static (ServerConnection, string) DeadEndpoint()
        {
            const string deadEndpoint = "127.0.0.1,1"; // nothing listens here — refused immediately
            var connection = new ServerConnection
            {
                Id = Guid.NewGuid().ToString(),
                ServerNames = deadEndpoint,
                UseWindowsAuthentication = true,
                ConnectionTimeout = 2,
                IsEnabled = true,
            };
            return (connection, deadEndpoint);
        }

        /// <summary>Runs the work inline, exactly as the real orchestrator does on the happy path.</summary>
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
    }
}
