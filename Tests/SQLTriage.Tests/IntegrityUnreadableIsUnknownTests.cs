/* In the name of God, the Merciful, the Compassionate */

// INVARIANT C (lane alert-correctness 2, 2026-09-19): AN ALERT THAT COULD NOT READ ITS EVIDENCE IS
// DISTINGUISHABLE ON THE WALL FROM ONE THAT READ IT AND FOUND NOTHING WRONG. Ruled by Adrian
// 2026-09-18, "show it as Unknown now", for integrity_check_overdue. When no online user database's
// last clean DBCC CHECKDB date can be read (PROVED on .\OLD2017 as a login without sysadmin: neither
// the property nor DBCC DBINFO yields a date for that login), the shipped query now THROWs a user error
// (number 50000 or above) before its final SELECT instead of answering NULL. The engine already turns
// that into an evaluation failure, and ServerAnswerClassifier reads it as "the server answered", so the
// circuit breaker does not count it. No engine change.
//
// THE COST, MEASURED HERE OLD VS NEW (memory: a safe direction has a cost, measure it against the old
// behaviour). The old query answered NULL, and a completed query stamps the measurement clock (ruling
// 2026-09-17), so an Active alert that became unreadable auto-cleared once its last reading went stale.
// The new query throws, a thrown cycle stamps nothing, so the same alert STAYS Active and also reads
// Unknown. Accepted as the honest state: an alert that cannot be read is not resolved.
//
// The SqlException is REAL, built through SqlClient's own internal factory exactly as
// ServerAnswerClassifierTests.MakeSqlException does, carrying the number, severity and message the
// shipped THROW raises; the live run of the real query is in the lane's gate harness.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public sealed class IntegrityUnreadableIsUnknownTests : IDisposable
    {
        private readonly string _dir;
        private readonly ITestOutputHelper _out;

        /// <summary>The stale window is FrequencySeconds x 3. One second gives a three second window.</summary>
        private static readonly TimeSpan PastTheWindow = TimeSpan.FromMilliseconds(3_500);

        public IntegrityUnreadableIsUnknownTests(ITestOutputHelper output)
        {
            _out = output;
            _dir = Path.Combine(Path.GetTempPath(), "integrity-unreadable-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private AlertDefinitionService ShippedDefinitionsCopy()
        {
            var local = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-alert-definitions.json");
            File.Copy(ShippedConfig.Path("alert-definitions.json"), local);
            return new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance, local);
        }

        /// <summary>The THROW the shipped query raises: its number and its message, read off the query text so
        /// this test follows the copy rather than restating it.</summary>
        internal static (int Number, string Message) ShippedThrow(AlertDefinition integrity)
        {
            var m = Regex.Match(integrity.Query, @"THROW\s+(\d+)\s*,\s*N'([^']*)'\s*,\s*1\s*;");
            Assert.True(m.Success,
                "the shipped integrity_check_overdue query carries no THROW <number>, N'<message>', 1; so an unreadable "
                + "evidence set is not signalled. CHECK: the query in Config/alert-definitions.json against invariant C.");
            return (int.Parse(m.Groups[1].Value), m.Groups[2].Value);
        }

        /// <summary>A real SqlException with ONE error entry, as the THROW produces (PROVED live, brief section 3:
        /// exactly one Errors entry, 50001/16, no InfoMessage). Built the way ServerAnswerClassifierTests does.</summary>
        private static SqlException ThrowAsTheServerRaisesIt(int number, byte severity, string message)
        {
            var asm = typeof(SqlException).Assembly;
            var collectionType = asm.GetType("Microsoft.Data.SqlClient.SqlErrorCollection")!;
            var errorType = asm.GetType("Microsoft.Data.SqlClient.SqlError")!;
            var collection = Activator.CreateInstance(collectionType, nonPublic: true)!;
            var add = collectionType.GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var ctor = errorType.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception) },
                null)
                ?? throw new InvalidOperationException("SqlError(int,byte,byte,string,string,string,int,Exception) not found.");
            add.Invoke(collection, new[] { ctor.Invoke(new object?[] { number, (byte)1, severity, "ac-l2-test", message, "", 1, null }) });
            var create = typeof(SqlException).GetMethod("CreateException",
                BindingFlags.NonPublic | BindingFlags.Static, null, new[] { collectionType, typeof(string) }, null)
                ?? throw new InvalidOperationException("SqlException.CreateException(SqlErrorCollection,string) not found.");
            return (SqlException)create.Invoke(null, new[] { collection, (object)"" })!;
        }

        private sealed class InlineOrchestrator : IQueryOrchestrator
        {
            public async Task<QueryResult> EnqueueAsync(QueryRequest request, QueryPriority priority, CancellationToken cancellationToken = default)
            {
                try { await request.Work(cancellationToken); return new QueryResult { QueryId = request.QueryId, Success = true }; }
                catch (Exception ex) { return new QueryResult { QueryId = request.QueryId, Success = false, Exception = ex }; }
            }
            public Task<OrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(new OrchestratorHealth());
            public Task<OrchestratorMetrics> GetMetricsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new OrchestratorMetrics());
            public void UpdateLimits(int globalConcurrency, int perServerConcurrency) { }
            public void Start() { }
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private AlertEvaluationService NewEngine(AlertDefinitionService defs, ServerCircuitBreakerService breaker)
        {
            var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
            var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);
            var svc = new AlertEvaluationService(
                NullLogger<AlertEvaluationService>.Instance, defs,
                new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
                new AlertingService(NullLogger<AlertingService>.Instance),
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                new ToastService(), channels, new liveQueriesCacheStore(), new InlineOrchestrator(),
                breaker: breaker,
                evalFailureStorePath: Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-eval-failures.json"));
            svc.DryRun = true;
            return svc;
        }

        private static int ConsecutiveFailures(ServerCircuitBreakerService breaker, string server)
        {
            var states = typeof(ServerCircuitBreakerService).GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(breaker) as System.Collections.IDictionary;
            Assert.NotNull(states);
            if (!states!.Contains(server)) return 0;
            var state = states[server]!;
            var field = state.GetType().GetField("ConsecutiveFailures", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var prop = state.GetType().GetProperty("ConsecutiveFailures", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.True(field != null || prop != null, "ServerCircuitBreakerService's per-server state no longer has ConsecutiveFailures; CHECK this reader");
            return (int)(field != null ? field.GetValue(state)! : prop!.GetValue(state)!);
        }

        [Fact]
        public void The_shipped_query_throws_a_user_error_the_classifier_reads_as_an_answer()
        {
            var integrity = ShippedDefinitionsCopy().GetAlert("integrity_check_overdue")!;
            var (number, message) = ShippedThrow(integrity);
            _out.WriteLine($"THROW {number}, N'{message}', 1;");
            _out.WriteLine("Rendered in the evaluation-failure panel: " + AlertEvaluationService.DescribeEvaluationFailure(ThrowAsTheServerRaisesIt(number, 16, message)));

            Assert.True(number >= ServerAnswerClassifier.FirstUserDefinedErrorNumber,
                $"THROW {number}: below {ServerAnswerClassifier.FirstUserDefinedErrorNumber} it is not a user error and the classifier "
                + "would count every unreadable cycle as a breaker failure. CHECK: the THROW number in the shipped query.");
            Assert.True(ServerAnswerClassifier.ServerAnswered(ThrowAsTheServerRaisesIt(number, 16, message)),
                "the classifier does not read the shipped THROW as an answer. CHECK: ServerAnswerClassifier.IsAnsweredError.");

            // The THROW sits before the final SELECT, so no value is computed on the unreadable path.
            var q = integrity.Query;
            Assert.True(q.IndexOf("THROW", StringComparison.Ordinal) < q.LastIndexOf("SELECT CASE WHEN EXISTS", StringComparison.Ordinal),
                "the THROW is not before the final SELECT. CHECK: the order of statements at the end of the shipped query.");
        }

        /// <summary>
        /// COST 1, old vs new, through the engine. Both cases fire the shipped integrity alert (Critical on
        /// 700 hours), let that reading go stale, and then run ONE cycle in which the evidence cannot be
        /// read. OLD (the 4082 query): the cycle completes with a NULL scalar, stamps the clock, and the
        /// reaper clears the alert. NEW: the cycle throws the shipped THROW, stamps nothing, the alert
        /// stays Active and an evaluation failure carrying the THROW text is recorded (Unknown).
        /// </summary>
        [Theory]
        [InlineData("old-null")]
        [InlineData("new-throw")]
        public async Task An_active_integrity_alert_that_becomes_unreadable(string route)
        {
            var defs = ShippedDefinitionsCopy();
            var alert = defs.GetAlert("integrity_check_overdue")!;
            alert.AlwaysAlert = true;      // an operational window in the test tree must not suppress the fire
            alert.FrequencySeconds = 1;    // a three second stale window
            var (number, message) = ShippedThrow(alert);
            var server = "integrity-unreadable-" + Guid.NewGuid().ToString("N")[..6];
            var breaker = new ServerCircuitBreakerService(NullLogger<ServerCircuitBreakerService>.Instance, audit: null);
            using var svc = NewEngine(defs, breaker);
            var connection = new ServerConnection { Id = Guid.NewGuid().ToString(), ServerNames = server, UseWindowsAuthentication = true, IsEnabled = true };

            Func<Task<object?>> answer = () => Task.FromResult<object?>(700.0);
            svc.StandardQueryOverrideForTests = (_, _) => answer();

            // -- setup: the alert fires from a real breaching value -------------------------------------
            await svc.ThrottledEvaluateAsync(alert, connection, server, new AlertGlobalDefaults(), CancellationToken.None);
            var fired = svc.ActiveAlerts.SingleOrDefault(a => a.AlertId == alert.Id && a.ServerName == server);
            Assert.True(fired != null && fired.Severity == "Critical",
                "the setup cycle did not fire Critical on 700 hours, so nothing below is measured. CHECK: the shipped thresholds (672 critical).");

            await Task.Delay(PastTheWindow);
            foreach (var s in svc.ActiveAlerts.Where(s => s.AlertId == alert.Id)) s.LastTriggered = DateTime.UtcNow.AddSeconds(-20);

            // -- CONTROL: with only the aged setup stamp in play the reaper spares the state -------------
            svc.ResolveCleared();
            Assert.True(svc.ActiveAlerts.Any(a => a.AlertId == alert.Id && a.ServerName == server),
                "CONTROL FAILED: the reaper cleared the alert before the unreadable cycle ran, so the outcome below would not be about that cycle.");

            // -- the unreadable cycle, three times (the breaker opens on three no-answers) --------------
            answer = route == "new-throw"
                ? () => throw ThrowAsTheServerRaisesIt(number, 16, message)
                : () => Task.FromResult<object?>(DBNull.Value);
            for (var i = 0; i < 3; i++)
                await svc.ThrottledEvaluateAsync(alert, connection, server, new AlertGlobalDefaults(), CancellationToken.None);

            foreach (var s in svc.ActiveAlerts.Where(s => s.AlertId == alert.Id)) s.LastTriggered = DateTime.UtcNow.AddSeconds(-20);
            svc.ResolveCleared();

            var stillActive = svc.ActiveAlerts.Any(a => a.AlertId == alert.Id && a.ServerName == server && a.Status == AlertStatus.Active);
            var failure = svc.EvaluationFailures.FirstOrDefault(f => f.AlertId == alert.Id && string.Equals(f.ServerName, server, StringComparison.OrdinalIgnoreCase));
            var failures = ConsecutiveFailures(breaker, server);
            _out.WriteLine($"route={route} stillActive={stillActive} unknown={svc.HasEvaluationFailure(server)} "
                + $"reason='{failure?.ErrorSummary ?? "(none)"}' breakerShouldAttempt={breaker.ShouldAttempt(server)} breakerConsecutiveFailures={failures}");

            if (route == "new-throw")
            {
                Assert.True(stillActive,
                    "NEW: the alert was cleared after cycles that could not read the evidence. A thrown cycle must stamp no clock. "
                    + "CHECK: where RecordServerEvaluation sits relative to the SqlException catch in EvaluateAlertOnServerAsync.");
                Assert.True(failure != null && failure.ErrorSummary == "SqlException: " + message,
                    "NEW: expected an evaluation failure reading 'SqlException: " + message + "', got '" + (failure?.ErrorSummary ?? "(none)") + "'. "
                    + "CHECK: the SqlException catch in EvaluateAlertOnServerAsync and DescribeEvaluationFailure.");
                Assert.True(breaker.ShouldAttempt(server) && failures == 0,
                    $"NEW: the breaker counted the THROW as a no-answer (ShouldAttempt {breaker.ShouldAttempt(server)}, {failures} consecutive failures). "
                    + "CHECK: ServerAnswerClassifier and ApplyBreakerOutcome.");
            }
            else
            {
                Assert.False(stillActive,
                    "OLD: the alert stayed Active after cycles that completed with no value; the old behaviour this lane measures against is an auto-clear. "
                    + "CHECK: the NULL route of EvaluateAlertOnServerAsync and ShouldAutoResolveAsCleared.");
                Assert.True(failure == null, "OLD: a NULL cycle recorded an evaluation failure: " + failure?.ErrorSummary);
            }
        }

        /// <summary>CONTROL for the breaker reading above: the same engine and breaker DO count a real
        /// no-answer (a command timeout, -2/11), so "0 consecutive failures" on the THROW route is a
        /// measurement and not the silence of an unwired breaker.</summary>
        [Fact]
        public async Task Control_the_same_harness_counts_a_command_timeout_as_a_no_answer()
        {
            var defs = ShippedDefinitionsCopy();
            var alert = defs.GetAlert("integrity_check_overdue")!;
            var server = "integrity-timeout-" + Guid.NewGuid().ToString("N")[..6];
            var breaker = new ServerCircuitBreakerService(NullLogger<ServerCircuitBreakerService>.Instance, audit: null);
            using var svc = NewEngine(defs, breaker);
            var connection = new ServerConnection { Id = Guid.NewGuid().ToString(), ServerNames = server, UseWindowsAuthentication = true, IsEnabled = true };
            svc.StandardQueryOverrideForTests = (_, _) => throw ServerAnswerClassifierTests.MakeSqlException((-2, 11));

            for (var i = 0; i < 3; i++)
                await svc.ThrottledEvaluateAsync(alert, connection, server, new AlertGlobalDefaults(), CancellationToken.None);

            Assert.True(ConsecutiveFailures(breaker, server) == 3 && !breaker.ShouldAttempt(server),
                $"CONTROL FAILED: three command timeouts left {ConsecutiveFailures(breaker, server)} consecutive failures and ShouldAttempt "
                + $"{breaker.ShouldAttempt(server)}; this harness cannot show the breaker counting, so the THROW route's 0 proves nothing.");
        }
    }
}
