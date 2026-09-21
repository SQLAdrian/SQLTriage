/* In the name of God, the Merciful, the Compassionate */

// ── A breach must last before it pages: AlertDefinition.HoldSeconds (lane Q14 fix round 2, 2026-09-18) ──
//
// INVARIANT. An alert with a hold opens an episode only on a breaching measurement taken at least
// HoldSeconds after the first breaching measurement of an UNBROKEN run. A measured value that does not
// breach ENDS the run; an attempt that produces no value BREAKS it; so does a gap with no measurement
// longer than AlertEvaluationService.BreachRunMaxGapSeconds. The hold only decides whether a NEW episode
// opens. An alert with no hold (every alert that does not carry the property) fires on its first breach.
//
// WHY. Owner's ruling 2 of DECISIONS 2026-09-18 04:21: sql_response_time is timed inside SQLTriage, so a
// stalled SQLTriage process reads slow too. Q14 gate 2 starved the app's thread pool on a healthy
// .\NEW2022 and ONE cycle paged Warning at 1,418.6 ms (and Critical at 2,117.99 ms on a second build).
// The alert ships holdSeconds 120, so one stalled cycle cannot page.
//
// WHAT IS COMPRESSED HERE, AND WHAT IS NOT. Time is stepped through the hold's own clock seam
// (AlertEvaluationService.BreachHoldClockForTests), so a 120 s hold takes milliseconds. Nothing else is
// stubbed: the standard path runs ThrottledEvaluateAsync -> EvaluateAlertOnServerAsync ->
// ObserveAndApplyAsync -> ApplyObservedValueAsync with only the SQL scalar supplied by
// StandardQueryOverrideForTests; the handler path runs EvaluateSpecialAlertAsync against a dead endpoint,
// whose connectivity handler really measures 1; the cumulative route runs ObserveAndApplyAsync over a
// real SQLite cache. The live proof of the hold, in real time against a real instance, is in the lane's
// evidence (the project's private evidence archive, evidence/alerts-that-cannot-fire-2026-09-18/fix2/).
//
// EVERY ROUTE THAT ENDS OR BREAKS A RUN IS DRIVEN BELOW, one test per route family: a measured clear
// (both paths), a NULL scalar, a throw (the shared RecordEvaluationFailure), an orchestrator failure, a
// failed handler, a cumulative sample that measures nothing, and the gap rule.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    public sealed class AlertBreachHoldTests : IDisposable
    {
        private const string Server = @"HOLD-TEST\SQL1,1433";
        private static readonly DateTime T0 = new(2026, 9, 18, 5, 0, 0, DateTimeKind.Utc);

        private readonly List<IDisposable> _disposables = new();
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "breach-hold-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            foreach (var d in _disposables) { try { d.Dispose(); } catch { /* best effort */ } }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        // ── harness ─────────────────────────────────────────────────────────────────────────────────

        private sealed class Harness
        {
            public AlertEvaluationService Svc = null!;
            public DateTime Now = T0;
            public Func<object?> Answer = () => 0.0;
            public bool OrchestratorFails;
        }

        private Harness Build(liveQueriesCacheStore? cache = null)
        {
            var h = new Harness();
            var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
            var svc = new AlertEvaluationService(
                NullLogger<AlertEvaluationService>.Instance,
                new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance),
                new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
                new AlertingService(NullLogger<AlertingService>.Instance),
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                new ToastService(),
                new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates),
                cache ?? new liveQueriesCacheStore(),
                new SwitchableOrchestrator(h),
                evalFailureStorePath: Path.Combine(_dir, "eval-failures-" + Guid.NewGuid().ToString("N") + ".json"))
            {
                DryRun = true,
            };
            _disposables.Add(svc);
            svc.BreachHoldClockForTests = () => h.Now;
            svc.StandardQueryOverrideForTests = (_, _) =>
            {
                var a = h.Answer();
                return a is Exception ex ? Task.FromException<object?>(ex) : Task.FromResult(a);
            };
            h.Svc = svc;
            return h;
        }

        private static AlertDefinition StandardAlert(int holdSeconds = 120) => new()
        {
            Id = "hold_probe_standard",
            Name = "Hold probe (standard)",
            Enabled = true,
            Severity = "Warning",
            Thresholds = new AlertThresholds { Warning = 10, Critical = 20 },
            Operator = "greater_than",
            Unit = "milliseconds",
            FrequencySeconds = 60,
            HoldSeconds = holdSeconds,
            Query = "SELECT 1 AS value",
        };

        private static ServerConnection Connection(string serverNames) => new()
        {
            Id = Guid.NewGuid().ToString(),
            ServerNames = serverNames,
            UseWindowsAuthentication = true,
            ConnectionTimeout = 2,
            IsEnabled = true,
        };

        /// <summary>One standard cycle at <paramref name="atSeconds"/> after T0, answering <paramref name="answer"/>.</summary>
        private static async Task StandardCycle(Harness h, AlertDefinition alert, double atSeconds, Func<object?> answer)
        {
            h.Now = T0.AddSeconds(atSeconds);
            h.Answer = answer;
            await h.Svc.ThrottledEvaluateAsync(alert, Connection(Server), Server, new AlertGlobalDefaults(), CancellationToken.None);
        }

        private static Func<object?> Value(double v) => () => v;

        private static AlertState? StateOf(Harness h, string alertId, string server = Server) =>
            h.Svc.ActiveAlerts.FirstOrDefault(s => s.AlertId == alertId && string.Equals(s.ServerName, server, StringComparison.OrdinalIgnoreCase));

        private static void AssertNotFired(Harness h, AlertDefinition alert, string when) =>
            Assert.True(StateOf(h, alert.Id) == null,
                $"{alert.Id} FIRED {when}, inside its {alert.HoldSeconds} s hold. WHAT TO CHECK: that every breaching measurement "
                + "goes through AlertEvaluationService.ObserveBreachForHold before the new-episode arm, and which route should "
                + "have ended or broken the run (EndBreachRun, BreakBreachRun, the gap rule).");

        private static AlertState AssertFired(Harness h, AlertDefinition alert, string when)
        {
            var state = StateOf(h, alert.Id);
            Assert.True(state != null,
                $"{alert.Id} did NOT fire {when}, although every measurement of an unbroken run breached for at least its "
                + $"{alert.HoldSeconds} s hold. A hold that never lets an alert fire is the defect this lane exists to remove. "
                + "WHAT TO CHECK: ObserveBreachForHold's run arithmetic and BreachRunMaxGapSeconds.");
            return state!;
        }

        private sealed class SwitchableOrchestrator : IQueryOrchestrator
        {
            private readonly Harness _h;
            public SwitchableOrchestrator(Harness h) => _h = h;

            public async Task<QueryResult> EnqueueAsync(QueryRequest request, QueryPriority priority, CancellationToken ct = default)
            {
                if (_h.OrchestratorFails)
                    return new QueryResult { QueryId = request.QueryId, Success = false, Exception = new TimeoutException("queue timeout (test seam)") };
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

            public Task<OrchestratorHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new OrchestratorHealth());
            public Task<OrchestratorMetrics> GetMetricsAsync(CancellationToken ct = default) => Task.FromResult(new OrchestratorMetrics());
            public void UpdateLimits(int globalConcurrency, int perServerConcurrency) { }
            public void Start() { }
            public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        }

        // ── the shipped value ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void Sql_response_time_ships_a_two_minute_hold_and_no_other_alert_ships_one()
        {
            var alerts = AlertQueryCensusSource.Shipped();
            Assert.Equal(80, alerts.Count);
            var held = alerts.Where(a => a.HoldSeconds != 0).Select(a => a.Id + "=" + a.HoldSeconds).ToList();
            Assert.Equal(new[] { "sql_response_time=120" }, held);
            Assert.Equal(60, alerts.Single(a => a.Id == "sql_response_time").FrequencySeconds);
        }

        // ── the standard path ───────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task A_sustained_breach_fires_on_the_first_measurement_at_least_the_hold_after_the_run_began()
        {
            var h = Build();
            var alert = StandardAlert();

            await StandardCycle(h, alert, 0, Value(15));
            AssertNotFired(h, alert, "on the first breaching measurement");
            await StandardCycle(h, alert, 60, Value(15));
            AssertNotFired(h, alert, "60 s into the run");
            await StandardCycle(h, alert, 119.9, Value(15));
            AssertNotFired(h, alert, "119.9 s into the run");
            await StandardCycle(h, alert, 120, Value(15));
            var state = AssertFired(h, alert, "120 s into the run");
            Assert.Equal(15, state.LastValue);
            Assert.Equal("Warning", state.Severity);
        }

        /// <summary>The ruling's own case: ONE stalled cycle, at a Critical reading, between healthy ones.</summary>
        [Fact]
        public async Task One_stalled_cycle_between_healthy_cycles_never_fires()
        {
            var h = Build();
            var alert = StandardAlert();

            for (var cycle = 0; cycle < 10; cycle++)
            {
                var stalled = cycle % 2 == 1;
                await StandardCycle(h, alert, cycle * 60, Value(stalled ? 2117.99 : 1.0));
                AssertNotFired(h, alert, $"at cycle {cycle} ({(stalled ? "a stalled reading of 2117.99" : "a healthy reading")})");
            }
        }

        [Fact]
        public async Task An_alert_with_no_hold_fires_on_its_first_breach()
        {
            var h = Build();
            var alert = StandardAlert(holdSeconds: 0);

            await StandardCycle(h, alert, 0, Value(15));
            AssertFired(h, alert, "on its first breach with no hold");
        }

        [Fact]
        public async Task A_measured_clear_ends_the_run()
        {
            var h = Build();
            var alert = StandardAlert();

            await StandardCycle(h, alert, 0, Value(15));
            await StandardCycle(h, alert, 60, Value(15));
            await StandardCycle(h, alert, 90, Value(5));     // measured, not breaching: the run ends
            await StandardCycle(h, alert, 120, Value(15));   // a new run begins here
            AssertNotFired(h, alert, "at 120 s, 0 s into the run that began after the clear");
            await StandardCycle(h, alert, 180, Value(15));
            AssertNotFired(h, alert, "at 180 s, 60 s into the new run");
            await StandardCycle(h, alert, 240, Value(15));
            AssertFired(h, alert, "at 240 s, 120 s into the new run");
        }

        public static IEnumerable<object[]> NoValueRoutes() => new[]
        {
            new object[] { "a NULL scalar (the completed-with-no-value arm)" },
            new object[] { "a throw (RecordEvaluationFailure)" },
            new object[] { "an orchestrator failure" },
        };

        [Theory]
        [MemberData(nameof(NoValueRoutes))]
        public async Task An_attempt_with_no_value_breaks_the_run(string route)
        {
            var h = Build();
            var alert = StandardAlert();

            await StandardCycle(h, alert, 0, Value(15));
            await StandardCycle(h, alert, 60, Value(15));

            h.Now = T0.AddSeconds(90);
            switch (route)
            {
                case var r when r.StartsWith("a NULL", StringComparison.Ordinal):
                    await StandardCycle(h, alert, 90, () => DBNull.Value);
                    break;
                case var r when r.StartsWith("a throw", StringComparison.Ordinal):
                    await StandardCycle(h, alert, 90, () => new TimeoutException("the standard query timed out (test seam)"));
                    break;
                default:
                    h.OrchestratorFails = true;
                    await StandardCycle(h, alert, 90, Value(15));
                    h.OrchestratorFails = false;
                    break;
            }

            await StandardCycle(h, alert, 120, Value(15));
            AssertNotFired(h, alert, $"at 120 s after {route} at 90 s: the run that began at 0 was broken");
            await StandardCycle(h, alert, 180, Value(15));
            AssertNotFired(h, alert, $"at 180 s after {route}");
            await StandardCycle(h, alert, 240, Value(15));
            AssertFired(h, alert, $"at 240 s, 120 s into the run that began after {route}");
        }

        [Fact]
        public async Task A_gap_longer_than_the_gap_rule_starts_a_new_run_and_one_at_the_limit_does_not()
        {
            var alert = StandardAlert();

            var limit = Build();
            var maxGap = limit.Svc.BreachRunMaxGapSeconds(alert);
            Assert.Equal(150, maxGap);   // 2 x max(60 s frequency, 30 s tick) + 30 s tick

            await StandardCycle(limit, alert, 0, Value(15));
            await StandardCycle(limit, alert, maxGap, Value(15));
            AssertFired(limit, alert, $"after a gap of exactly {maxGap} s, which is still one run");

            var beyond = Build();
            await StandardCycle(beyond, alert, 0, Value(15));
            await StandardCycle(beyond, alert, maxGap + 1, Value(15));
            AssertNotFired(beyond, alert, $"after a gap of {maxGap + 1} s with no measurement");
            await StandardCycle(beyond, alert, maxGap + 1 + 120, Value(15));
            AssertFired(beyond, alert, "120 s into the run that began after the gap");
        }

        [Fact]
        public async Task An_active_alert_still_updates_on_every_breaching_cycle()
        {
            var h = Build();
            var alert = StandardAlert();

            await StandardCycle(h, alert, 0, Value(15));
            await StandardCycle(h, alert, 60, Value(15));
            await StandardCycle(h, alert, 120, Value(15));
            AssertFired(h, alert, "at 120 s");

            await StandardCycle(h, alert, 180, Value(25));
            var state = AssertFired(h, alert, "at 180 s");
            Assert.Equal("Critical", state.Severity);
            Assert.Equal(25, state.LastValue);
            Assert.Equal(2, state.HitCount);
        }

        // ── the built-in-handler path ───────────────────────────────────────────────────────────────

        private static AlertDefinition HandlerAlert() => new()
        {
            Id = "hold_probe_handler",
            Name = "Hold probe (handler)",
            Enabled = true,
            Severity = "Warning",
            Thresholds = new AlertThresholds { Warning = 0 },
            Operator = "greater_than",
            Unit = "boolean",
            FrequencySeconds = 60,
            HoldSeconds = 120,
            Query = "",
            QueryMode = "connectivity_check",
        };

        private static async Task HandlerCycle(Harness h, AlertDefinition alert, double atSeconds, string queryMode, double warning)
        {
            h.Now = T0.AddSeconds(atSeconds);
            alert.QueryMode = queryMode;
            alert.Thresholds.Warning = warning;
            // Nothing listens on 127.0.0.1,1: connectivity_check MEASURES 1 there, and response_time_probe fails.
            await h.Svc.EvaluateSpecialAlertAsync(alert, Connection("127.0.0.1,1"), "127.0.0.1,1", new AlertGlobalDefaults());
        }

        [Fact]
        public async Task The_handler_path_honours_the_hold_and_its_clear_and_failure_routes()
        {
            var h = Build();
            var alert = HandlerAlert();
            AlertState? State() => StateOf(h, alert.Id, "127.0.0.1,1");

            await HandlerCycle(h, alert, 0, "connectivity_check", 0);        // measures 1 > 0: breach
            Assert.Null(State());
            await HandlerCycle(h, alert, 60, "connectivity_check", 5);       // measures 1, not > 5: the run ends
            await HandlerCycle(h, alert, 120, "connectivity_check", 0);      // a new run begins
            Assert.True(State() == null, "the handler path fired 0 s into a run that began after a measured clear");
            await HandlerCycle(h, alert, 180, "response_time_probe", 0);     // the handler fails: the run is broken
            Assert.True(h.Svc.EvaluationFailures.Any(f => f.AlertId == alert.Id),
                "HARNESS: response_time_probe against a dead endpoint must record an evaluation failure, or this step does not drive the failure route");
            await HandlerCycle(h, alert, 240, "connectivity_check", 0);      // 120 s after the run at 120 began, but it was broken
            Assert.True(State() == null, "the handler path fired on a run broken by a failed measurement at 180 s");
            await HandlerCycle(h, alert, 300, "connectivity_check", 0);
            Assert.True(State() == null, "the handler path fired 60 s into the run that began at 240 s");
            await HandlerCycle(h, alert, 360, "connectivity_check", 0);
            Assert.True(State() != null, "the handler path did not fire 120 s into an unbroken run of breaching measurements");
        }

        // ── the cumulative route ────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task A_cumulative_sample_that_measures_nothing_breaks_the_run()
        {
            Directory.CreateDirectory(_dir);
            var cache = new liveQueriesCacheStore();
            _disposables.Add(cache);
            typeof(liveQueriesCacheStore).GetField("_connectionString", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(cache, "Data Source=" + Path.Combine(_dir, "hold-cache.db") + ";Mode=ReadWriteCreate;");
            typeof(liveQueriesCacheStore).GetMethod("InitializeSchema", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(cache, null);

            var h = Build(cache);
            var alert = StandardAlert();
            alert.Id = "hold_probe_cumulative";
            alert.Unit = "per_second";
            alert.ValueKind = AlertDefinition.CumulativeCounterKind;

            async Task Sample(double atSeconds, double raw)
            {
                h.Now = T0.AddSeconds(atSeconds);
                await h.Svc.ObserveAndApplyAsync(alert, Server, raw, h.Now);
            }

            await Sample(0, 1_000);        // first sample: measures nothing
            await Sample(60, 61_000);      // 1,000 per second: breach, the run begins at 60
            await Sample(120, 121_000);
            Assert.Null(StateOf(h, alert.Id));
            await Sample(150, 50);         // counter reset: measures nothing, the run is broken
            await Sample(180, 60_050);     // breach: a new run begins at 180
            Assert.True(StateOf(h, alert.Id) == null, "fired at 180 s, which is 120 s after a run that a counter reset broke at 150 s");
            await Sample(240, 120_050);
            Assert.Null(StateOf(h, alert.Id));
            await Sample(300, 180_050);
            Assert.True(StateOf(h, alert.Id) != null, "did not fire 120 s into the unbroken run that began at 180 s");
        }
    }
}
