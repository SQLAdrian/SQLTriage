/* In the name of God, the Merciful, the Compassionate */

// ── The never-fires class on the SPECIAL path (strings lane fix round, 2026-08-28) ─────────────
//
// WHAT WAS WRONG, AND WHY THE LANE'S OWN GUARD COULD NOT SEE IT. Cluster 1 closed the
// strict-comparison defect for every alert whose reachable range could be read off its query text:
// IsThresholdBreached is `value > threshold`, never `>=`, so a query capped at 1 against a warning
// of 1 is silent for ever. The lint it left behind reads the shape off AlertDefinition.Query. An
// alert routed by queryMode has no executable query - the evaluator hands it to a built-in handler
// that runs its own SQL - so SmallestAlertableValue returned null for the whole class and all four
// lints skipped it. Three shipped alerts were sitting in that shadow at warning 1:
//
//   instance_unreachable   Critical, enabled. CheckConnectivityAsync returns exactly 1 for an
//                          unreachable server. 1 > 1 is false, so a DOWN INSTANCE was silence.
//   machine_unreachable    Critical, enabled. Same handler, same arithmetic, same silence.
//   deadlock               enabled, alwaysAlert. CountDeadlocksAsync returns 1 for one deadlock,
//                          under a description reading "One or more deadlocks detected ... in the
//                          last 5 minutes". It took two.
//
// WHAT IS PROVED HERE, BY EXECUTION. These tests drive the production evaluator,
// AlertEvaluationService.EvaluateSpecialAlertAsync, against a REAL dead endpoint (127.0.0.1,1 -
// nothing listens, the connection is refused immediately), with the definitions read off the
// shipped Config/alert-definitions.json rather than hand-built. The alert either appears in
// ActiveAlerts or it does not; nothing about the threshold is inferred.
//
// NON-VACUITY IS PART OF THE TEST, not a note beside it. The last test re-runs the identical path
// with the pre-fix threshold of 1 and asserts SILENCE. Without that row a broken evaluator that
// fired on everything would pass the rest.
//
// THE DEADLOCK HALF WAS ALSO PROVED LIVE, ONCE, AND IS NOT RE-RUN HERE. On .\new2022 a real
// deadlock was generated between two sessions on a scratch database; the victim reported
// "Msg 1205 ... was deadlocked on lock resources ... chosen as the deadlock victim", and the exact
// SQL CountDeadlocksAsync runs then returned 1 against master. Reproducing a deadlock inside a unit
// test needs a live instance and two connections, so what is pinned here is the arithmetic that 1
// then has to clear, through the real predicate.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    public class SpecialAlertPathNeverFiresTests
    {
        // Anchored on the solution file: probing for Config/alert-definitions.json would find the
        // test output copy instead of the bytes that install.
        private static AlertDefinition Shipped(string id)
        {
            var json = File.ReadAllText(
                Path.Combine(RawPassedScan.RepoRoot().FullName, "Config", "alert-definitions.json"));
            var file = JsonSerializer.Deserialize<AlertDefinitionsFile>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(file);
            return file!.Alerts.Single(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// THE BLOCKER, closed and measured. The shipped instance_unreachable, driven through the
        /// production special path against a dead endpoint, must produce an active alert.
        /// </summary>
        [Fact]
        public async Task Instance_unreachable_fires_against_a_dead_endpoint()
        {
            using var svc = BuildEngine();
            svc.DryRun = true;
            var (connection, deadEndpoint) = DeadEndpoint();

            await svc.EvaluateSpecialAlertAsync(
                Shipped("instance_unreachable"), connection, deadEndpoint, new AlertGlobalDefaults());

            Assert.True(svc.ActiveAlerts.Count > 0,
                "instance_unreachable did not fire against a dead endpoint. CheckConnectivityAsync "
                + "returns exactly 1 for unreachable and IsThresholdBreached is strict, so any "
                + "warning threshold at or above 1 makes a down instance produce silence.");
        }

        /// <summary>
        /// The sibling, which is the same handler. Its NAME says host and its old description said
        /// "The host machine does not respond to monitoring requests"; EvaluateSpecialAlertAsync maps
        /// both connectivity_check and host_connectivity_check to CheckConnectivityAsync, so what it
        /// actually measures is a SQL connection. The threshold fix is what makes that prose live, so
        /// the prose was corrected in the same commit and is pinned here.
        /// </summary>
        [Fact]
        public async Task Machine_unreachable_fires_against_a_dead_endpoint_and_says_what_it_measures()
        {
            var alert = Shipped("machine_unreachable");

            Assert.Contains("same connection test as Instance Unreachable", alert.Description,
                StringComparison.OrdinalIgnoreCase);

            using var svc = BuildEngine();
            svc.DryRun = true;
            var (connection, deadEndpoint) = DeadEndpoint();

            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());

            Assert.True(svc.ActiveAlerts.Count > 0,
                "machine_unreachable did not fire against a dead endpoint.");
        }

        /// <summary>
        /// A reachable server must stay quiet, or the fix would have swapped a silent alert for a
        /// permanent one - which is not a fix. Armed by SQLTRIAGE_LIVE_INSTANCE (set it to an
        /// instance name such as <c>.\NEW2022</c>); reports SKIPPED, never a vacuous pass, when it is
        /// unset. RUN ARMED on 2026-08-28 against <c>.\NEW2022</c>: no alert.
        /// </summary>
        [LiveFact("SQLTRIAGE_LIVE_INSTANCE")]
        public async Task A_reachable_instance_produces_no_connectivity_alert()
        {
            var target = Environment.GetEnvironmentVariable("SQLTRIAGE_LIVE_INSTANCE");
            Assert.False(string.IsNullOrWhiteSpace(target),
                "the attribute skips an unarmed run; this assertion is what makes a WEAKENED "
                + "attribute fail instead of passing on nothing");

            using var svc = BuildEngine();
            svc.DryRun = true;

            var connection = new ServerConnection
            {
                Id = Guid.NewGuid().ToString(),
                ServerNames = target!,
                UseWindowsAuthentication = true,
                // A local dev instance carries a self-signed certificate, and the driver encrypts by
                // default. Without this the handler returns 1 for a server that is plainly up, and
                // this test would "prove" the fix by measuring a certificate error.
                TrustServerCertificate = true,
                ConnectionTimeout = 5,
                IsEnabled = true,
            };

            await svc.EvaluateSpecialAlertAsync(
                Shipped("instance_unreachable"), connection, target!, new AlertGlobalDefaults());

            Assert.Empty(svc.ActiveAlerts);
        }

        /// <summary>
        /// The same claim without a server, through the real predicate: the handler answers 0 for a
        /// reachable instance, and 0 does not breach a warning of 0 under strict greater_than.
        /// </summary>
        [Fact]
        public void A_reachable_reading_of_zero_does_not_breach_the_new_threshold()
        {
            foreach (var id in new[] { "instance_unreachable", "machine_unreachable" })
            {
                var a = Shipped(id);
                Assert.False(AlertEvaluationService.IsThresholdBreached(0.0, a.Thresholds.Warning, a.Operator),
                    id + " must stay silent while the server answers");
                Assert.True(AlertEvaluationService.IsThresholdBreached(1.0, a.Thresholds.Warning, a.Operator),
                    id + " must speak the moment it does not");
            }
        }

        /// <summary>
        /// NON-VACUITY. The same path, the same dead endpoint, the same handler - with the threshold
        /// exactly as it shipped at 84beb1c. This is the defect, reproduced, and it is what tells a
        /// later reader that the three tests above measure the threshold and not the evaluator's
        /// willingness to fire at anything.
        /// </summary>
        [Fact]
        public async Task The_prefix_threshold_of_one_produced_silence_on_the_same_dead_endpoint()
        {
            using var svc = BuildEngine();
            svc.DryRun = true;
            var (connection, deadEndpoint) = DeadEndpoint();

            var prefix = Shipped("instance_unreachable");
            prefix.Thresholds = new AlertThresholds { Warning = 1 };   // as it shipped at 84beb1c

            await svc.EvaluateSpecialAlertAsync(prefix, connection, deadEndpoint, new AlertGlobalDefaults());

            Assert.Empty(svc.ActiveAlerts);
            Assert.False(svc.HasEvaluationFailure(deadEndpoint),
                "the handler MEASURED - it returned 1 for unreachable. The silence is the threshold, "
                + "not a failure to measure, which is precisely why nothing showed Unknown either.");
        }

        /// <summary>
        /// The deadlock arithmetic, through the real predicate: one deadlock must clear the shipped
        /// threshold, and a quiet five minutes must not.
        /// </summary>
        [Fact]
        public void One_deadlock_clears_the_shipped_deadlock_threshold()
        {
            var a = Shipped("deadlock");

            Assert.True(AlertEvaluationService.IsThresholdBreached(1.0, a.Thresholds.Warning, a.Operator),
                "its own description reads \"One or more deadlocks detected\"");
            Assert.False(AlertEvaluationService.IsThresholdBreached(0.0, a.Thresholds.Warning, a.Operator));
            Assert.False(AlertEvaluationService.IsThresholdBreached(5.0, a.Thresholds.Critical, a.Operator),
                "five is the band, so five is still a warning and six is critical");
            Assert.True(AlertEvaluationService.IsThresholdBreached(6.0, a.Thresholds.Critical, a.Operator));
        }

        // ── helpers (the shape AlertHonestyLaneTests established) ──

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
            const string deadEndpoint = "127.0.0.1,1"; // nothing listens here - refused immediately
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
                QueryRequest request, QueryPriority priority, System.Threading.CancellationToken ct = default)
            {
                try
                {
                    await request.Work(ct);
                    return new QueryResult { QueryId = request.QueryId, Success = true };
                }
                catch (Exception ex)
                {
                    return new QueryResult { QueryId = request.QueryId, Success = false, Exception = ex };
                }
            }

            public Task<OrchestratorHealth> GetHealthAsync(System.Threading.CancellationToken ct = default)
                => Task.FromResult(new OrchestratorHealth());
            public Task<OrchestratorMetrics> GetMetricsAsync(System.Threading.CancellationToken ct = default)
                => Task.FromResult(new OrchestratorMetrics());
            public void UpdateLimits(int globalConcurrency, int perServerConcurrency) { }
            public void Start() { }
            public Task StopAsync(System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        }
    }
}
