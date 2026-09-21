/* In the name of God, the Merciful, the Compassionate */

// INVARIANT B (lane alert-correctness, 2026-09-19), AGAINST REAL SERVERS. The rule's unit tests
// (ServerAnswerClassifierTests) build SqlExceptions through SqlClient's own factory; these take them
// from the two local instances, so a SqlClient upgrade or a server that reports a refusal differently
// turns them red.
//
// INVOCATION. Inert unless armed: set ALERTCORRECTNESS_LIVE_INSTANCES to a semicolon-separated list
// of instances reachable with WINDOWS authentication, e.g.
//     ALERTCORRECTNESS_LIVE_INSTANCES=lpc:MSI\NEW2022;lpc:MSI\OLD2017
// Each instance must have a SQL login named sqlt_plain with no VIEW SERVER STATE and no msdb
// rights. Both local instances are Windows-auth-only, so nothing can LOG IN as sqlt_plain: every
// statement runs on a Windows-auth connection as EXECUTE AS LOGIN = N'sqlt_plain', then REVERT.
// Read-only: SELECTs, WAITFOR, THROW and EXECUTE AS/REVERT only, on Pooling=false connections so no
// impersonated session is ever returned to a pool. The transport case resolves a name under the
// reserved .invalid suffix and touches no server.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    internal static class AlertCorrectnessLive
    {
        internal const string InstancesVar = "ALERTCORRECTNESS_LIVE_INSTANCES";

        /// <summary>The armed instance list. Fails, never skips: LiveFact is what skips an unarmed run.</summary>
        internal static string[] RequireInstances()
        {
            var raw = Environment.GetEnvironmentVariable(InstancesVar);
            Assert.False(string.IsNullOrWhiteSpace(raw),
                $"{InstancesVar} must name at least one instance for this harness to mean anything");
            var list = raw!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.NotEmpty(list);
            return list;
        }

        internal static string ConnectionString(string instance, int connectTimeoutSeconds = 10) =>
            new SqlConnectionStringBuilder
            {
                DataSource = instance,
                IntegratedSecurity = true,
                Pooling = false,
                TrustServerCertificate = true,
                ConnectTimeout = connectTimeoutSeconds,
                ApplicationName = "SQLTriage-alert-correctness-live-test",
                InitialCatalog = "master",
            }.ConnectionString;

        internal const string AsPlain = "EXECUTE AS LOGIN = N'sqlt_plain';\n";

        /// <summary>A private copy of the shipped definitions: the service may migrate and save its
        /// file, and the shipped Config file in the tree must never be written by a test.</summary>
        internal static AlertDefinitionService ShippedDefinitionsCopy(string dir)
        {
            var local = Path.Combine(dir, Guid.NewGuid().ToString("N") + "-alert-definitions.json");
            File.Copy(ShippedConfig.Path("alert-definitions.json"), local);
            return new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance, local);
        }

        /// <summary>Runs one batch and returns the SqlException it raised, or null if it completed.</summary>
        internal static async Task<SqlException?> RunAsync(string connectionString, string sql, int commandTimeoutSeconds = 30)
        {
            await using var conn = new SqlConnection(connectionString);
            try
            {
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = commandTimeoutSeconds };
                await cmd.ExecuteScalarAsync();
                return null;
            }
            catch (SqlException ex)
            {
                return ex;
            }
        }

        internal static string Describe(SqlException ex) =>
            string.Join(" + ", ex.Errors.Cast<SqlError>().Select(e => $"{e.Number}/sev{e.Class}"));
    }

    public class ServerAnswerClassifierLiveTests
    {
        private readonly ITestOutputHelper _out;
        public ServerAnswerClassifierLiveTests(ITestOutputHelper output) => _out = output;

        [LiveFact(AlertCorrectnessLive.InstancesVar)]
        public async Task Real_refusals_are_answers_and_real_timeouts_and_dead_names_are_not()
        {
            var instances = AlertCorrectnessLive.RequireInstances();
            var dir = Path.Combine(Path.GetTempPath(), "classifier-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var shipped = AlertCorrectnessLive.ShippedDefinitionsCopy(dir);
            var fragmentation = shipped.GetAlert("index_fragmentation");
            Assert.NotNull(fragmentation);

            foreach (var instance in instances)
            {
                var cs = AlertCorrectnessLive.ConnectionString(instance);
                var prefix = SqlSessionSafety.DefaultPrefix + AlertCorrectnessLive.AsPlain;

                var whoami = await AlertCorrectnessLive.RunAsync(cs, "SELECT SUSER_SNAME();");
                Assert.True(whoami == null, $"{instance}: the harness could not even run SELECT SUSER_SNAME(): {whoami?.Message}");

                var answered = new (string Name, string Sql, int[] MustContain)[]
                {
                    ("VIEW SERVER STATE denial", prefix + "SELECT COUNT(*) FROM sys.dm_os_wait_stats;\nREVERT;", new[] { 300, 297 }),
                    ("object permission", prefix + "SELECT TOP (1) 1 FROM msdb.dbo.sysjobactivity;\nREVERT;", new[] { 229 }),
                    ("index_fragmentation, the SHIPPED query", prefix + fragmentation!.Query + "\nREVERT;", new[] { 297 }),
                    ("THROW 50001", prefix + "THROW 50001, N'alert-correctness live test', 1;", new[] { 50001 }),
                };
                foreach (var (name, sql, mustContain) in answered)
                {
                    var ex = await AlertCorrectnessLive.RunAsync(cs, sql);
                    Assert.True(ex != null, $"{instance} {name}: expected the server to refuse, and it completed. Is sqlt_plain still unprivileged?");
                    _out.WriteLine($"{instance} {name}: {AlertCorrectnessLive.Describe(ex!)}");
                    var numbers = ex!.Errors.Cast<SqlError>().Select(e => e.Number).ToHashSet();
                    Assert.True(mustContain.All(numbers.Contains), $"{instance} {name}: raised {AlertCorrectnessLive.Describe(ex)}");
                    Assert.True(ServerAnswerClassifier.ServerAnswered(ex), $"{instance} {name}: {AlertCorrectnessLive.Describe(ex)} must read as answered");
                }

                var timeout = await AlertCorrectnessLive.RunAsync(cs, SqlSessionSafety.DefaultPrefix + "WAITFOR DELAY '00:00:04'; SELECT 1;", commandTimeoutSeconds: 1);
                Assert.True(timeout != null, $"{instance}: a 4 s WAITFOR under a 1 s command timeout completed");
                _out.WriteLine($"{instance} command timeout: {AlertCorrectnessLive.Describe(timeout!)}");
                Assert.Equal(-2, timeout!.Number);
                Assert.False(ServerAnswerClassifier.ServerAnswered(timeout), "a command timeout is a hung server and must stay a breaker failure");
            }

            var dead = await AlertCorrectnessLive.RunAsync(
                AlertCorrectnessLive.ConnectionString("tcp:zz-alert-correctness-" + Guid.NewGuid().ToString("N")[..8] + ".invalid", connectTimeoutSeconds: 5),
                "SELECT 1;");
            Assert.True(dead != null, "a connection to a .invalid name succeeded");
            _out.WriteLine($"unresolvable name: {AlertCorrectnessLive.Describe(dead!)}");
            Assert.Contains(dead!.Errors.Cast<SqlError>(), e => e.Class >= 20);
            Assert.False(ServerAnswerClassifier.ServerAnswered(dead));

            // And the breaker, fed the real exceptions.
            var breaker = new ServerCircuitBreakerService(NullLogger<ServerCircuitBreakerService>.Instance, audit: null);
            var refusal = await AlertCorrectnessLive.RunAsync(AlertCorrectnessLive.ConnectionString(instances[0]),
                SqlSessionSafety.DefaultPrefix + AlertCorrectnessLive.AsPlain + "SELECT COUNT(*) FROM sys.dm_os_wait_stats;\nREVERT;");
            for (var i = 0; i < 6; i++) breaker.RecordFailure("refusing", refusal!);
            Assert.True(breaker.ShouldAttempt("refusing"));
            for (var i = 0; i < 3; i++) breaker.RecordFailure("dead", dead);
            Assert.False(breaker.ShouldAttempt("dead"), "the fix must not blind the breaker to a server that is really down");
        }
    }

    /// <summary>
    /// B1 end to end: the REAL engine, the REAL breaker, and each enabled standard alert's REAL shipped
    /// query run on the server as sqlt_plain, through AlertEvaluationService.StandardQueryOverrideForTests,
    /// so the engine's own SqlException catch receives the server's own exception. Before this lane that
    /// catch wrote Unreachable for every one of them and three refusals opened the breaker.
    /// </summary>
    public class AlertBreakerPermissionLiveTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _dir;

        public AlertBreakerPermissionLiveTests(ITestOutputHelper output)
        {
            _out = output;
            _dir = Path.Combine(Path.GetTempPath(), "breaker-perm-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private sealed class InlineOrchestrator : IQueryOrchestrator
        {
            public async Task<QueryResult> EnqueueAsync(QueryRequest request, QueryPriority priority, CancellationToken cancellationToken = default)
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
            public Task<OrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(new OrchestratorHealth());
            public Task<OrchestratorMetrics> GetMetricsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new OrchestratorMetrics());
            public void UpdateLimits(int globalConcurrency, int perServerConcurrency) { }
            public void Start() { }
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private AlertEvaluationService NewEngine(ServerCircuitBreakerService breaker)
        {
            var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
            var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);
            var svc = new AlertEvaluationService(
                NullLogger<AlertEvaluationService>.Instance,
                AlertCorrectnessLive.ShippedDefinitionsCopy(_dir),
                new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
                new AlertingService(NullLogger<AlertingService>.Instance),
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                new ToastService(),
                channels,
                new liveQueriesCacheStore(),
                new InlineOrchestrator(),
                breaker: breaker,
                evalFailureStorePath: Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-eval-failures.json"));
            svc.DryRun = true;
            return svc;
        }

        [LiveFact(AlertCorrectnessLive.InstancesVar)]
        public async Task A_login_without_VIEW_SERVER_STATE_never_opens_the_breaker_and_every_refused_alert_reads_Unknown()
        {
            var instances = AlertCorrectnessLive.RequireInstances();
            var shipped = AlertCorrectnessLive.ShippedDefinitionsCopy(_dir);
            var standard = shipped.GetAllAlerts()
                .Where(a => a.Enabled && !AlertEvaluationService.IsRoutedToBuiltInHandler(a) && !string.IsNullOrWhiteSpace(a.Query))
                .ToList();
            _out.WriteLine($"enabled standard alerts in the shipped definitions: {standard.Count}");
            Assert.NotEmpty(standard);

            foreach (var instance in instances)
            {
                var cs = AlertCorrectnessLive.ConnectionString(instance);
                var raised = new Dictionary<string, SqlException?>(StringComparer.OrdinalIgnoreCase);

                async Task<object?> RealQueryAsPlain(AlertDefinition alert, string _)
                {
                    await using var conn = new SqlConnection(cs);
                    await conn.OpenAsync();
                    await using var cmd = new SqlCommand(SqlSessionSafety.DefaultPrefix + AlertCorrectnessLive.AsPlain + alert.Query, conn) { CommandTimeout = 30 };
                    try
                    {
                        var value = await cmd.ExecuteScalarAsync();
                        raised[alert.Id] = null;
                        return value;
                    }
                    catch (SqlException ex)
                    {
                        raised[alert.Id] = ex;
                        throw;
                    }
                }

                // Pass 1: every enabled standard alert, to find out which ones this server refuses.
                var breaker1 = new ServerCircuitBreakerService(NullLogger<ServerCircuitBreakerService>.Instance, audit: null);
                var svc1 = NewEngine(breaker1);
                svc1.StandardQueryOverrideForTests = RealQueryAsPlain;
                var connection = new ServerConnection { Id = Guid.NewGuid().ToString(), ServerNames = instance, UseWindowsAuthentication = true, IsEnabled = true };
                foreach (var alert in standard)
                    await svc1.ThrottledEvaluateAsync(alert, connection, instance, new AlertGlobalDefaults(), CancellationToken.None);

                var refused = standard.Where(a => raised.TryGetValue(a.Id, out var ex) && ServerAnswerClassifier.ServerAnswered(ex)).ToList();
                var otherErrors = standard.Where(a => raised.TryGetValue(a.Id, out var ex) && ex != null && !ServerAnswerClassifier.ServerAnswered(ex)).ToList();
                var completed = standard.Count(a => raised.TryGetValue(a.Id, out var ex) && ex == null);
                _out.WriteLine($"{instance}: of {standard.Count} enabled standard alerts, completed {completed}, refused {refused.Count}, other SQL errors {otherErrors.Count}, never ran {standard.Count - raised.Count}");
                foreach (var a in otherErrors) _out.WriteLine($"  OTHER {a.Id}: {AlertCorrectnessLive.Describe(raised[a.Id]!)}");
                Assert.True(refused.Count >= 3, $"{instance}: fewer than three refused alerts, so this harness cannot tell a fixed breaker from a broken one");

                // Every refused alert still records an evaluation failure (Unknown, never Ok).
                var failed = svc1.EvaluationFailures.Where(f => string.Equals(f.ServerName, instance, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.AlertId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var refusedButNotFailed = refused.Where(a => !failed.Contains(a.Id)).Select(a => a.Id).ToList();
                Assert.True(refusedButNotFailed.Count == 0,
                    $"{instance}: refused but not recorded as an evaluation failure: {string.Join(", ", refusedButNotFailed)}");

                // Pass 2, the discriminating one: ONLY the refused alerts, on a fresh breaker. Three
                // consecutive breaker failures open it, so before this lane it was open after the third.
                var breaker2 = new ServerCircuitBreakerService(NullLogger<ServerCircuitBreakerService>.Instance, audit: null);
                var svc2 = NewEngine(breaker2);
                svc2.StandardQueryOverrideForTests = RealQueryAsPlain;
                var closedAfter = 0;
                foreach (var alert in refused)
                {
                    await svc2.ThrottledEvaluateAsync(alert, connection, instance, new AlertGlobalDefaults(), CancellationToken.None);
                    Assert.True(breaker2.ShouldAttempt(instance),
                        $"{instance}: the breaker opened after refused alert {closedAfter + 1} ({alert.Id}, {AlertCorrectnessLive.Describe(raised[alert.Id]!)}). "
                        + "WHAT TO CHECK: whether the SqlException catch in EvaluateAlertOnServerAsync still hands its exception to "
                        + "ServerReachabilityProbe.MarkFailed, and whether ServerAnswerClassifier still reads every entry in Errors.");
                    closedAfter++;
                }
                _out.WriteLine($"{instance}: breaker stayed closed across all {closedAfter} refused alerts in a row");

                svc1.Dispose();
                svc2.Dispose();
            }
        }
    }
}
