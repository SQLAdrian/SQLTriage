/* In the name of God, the Merciful, the Compassionate */

// ── logon-failure-unmeasurable (2026-09-09): an alert that cannot measure must say WHY ──────────
//
// THE DEFECT, MEASURED ON THE LIVE SERVICE, NOT INFERRED. From 2026-09-08 22:31:58 NZST — about
// sixty seconds after the build-3995 deploy — the installed service recorded the `logon_failure`
// alert as unmeasurable on all three monitored instances (.\OLD2017 75 times, .\NEW2022 76, the
// default instance 76) and was still counting. The whole reason an operator could read, in
// Config/eval-failures.json and on the alerts panel, was:
//
//     queryMode 'error_log_scan' produced no measurement this cycle
//
// That sentence is AlertEvaluationService's own FALLBACK — the string it uses when `handlerError`
// is null. So the exception that stopped the measurement was not merely unlogged; it had already
// been destroyed before the caller looked. Every special handler caught its own exception, wrote it
// to `_logger.LogDebug`, and returned a bare `null`. Production emits no Debug, so 76 consecutive
// failures across three instances produced ZERO lines naming a cause anywhere an operator reads.
// Raising :870/:873 to Warning would have changed nothing: there was nothing there to raise.
//
// WHAT THESE TESTS PIN, all through the production evaluator against a real refused endpoint
// (127.0.0.1,1 — nothing listens, so the connection is refused immediately and a genuine exception
// travels the genuine path; no exception is hand-injected anywhere in this file):
//
//   1. the reason recorded for a handler failure NAMES THE EXCEPTION — type and message — and is
//      never the fallback sentence when an exception actually occurred;
//   2. the same text reaches the DURABLE store on disk (Config/eval-failures.json), which is what
//      the operator panel renders and what survives a restart;
//   3. a Warning-level log line carries that same text, so the cause is readable with the shipped
//      production log level and no Debug;
//   4. that Warning is written ONCE PER EPISODE. Three failing cycles produce one Warning and two
//      Debug repeats — the live case would otherwise have written 76 Warnings per instance;
//   5. the honesty behaviour is UNCHANGED: the alert still records an evaluation failure, so the
//      NOC reads Unknown and never a clean Ok, and nothing is dispatched;
//   6. and the anti-regression that dictated the design: `connectivity_check` still MEASURES an
//      unreachable server as 1 rather than reporting it as unmeasurable. A blanket rethrow from the
//      handlers would have surrendered the exception at the cost of turning a DOWN instance from a
//      fired Critical into an Unknown — which is why the handlers return a value/exception pair.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;
using Xunit;

// SQLTriage.Data declares a LogLevel of its own, so the bare name is ambiguous in this file.
// The one that matters here is the logging framework's — the level the shipped host actually filters on.
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace SQLTriage.Tests
{
    public class EvalFailureReasonReadableTests : IDisposable
    {
        private readonly string _tempDir;

        public EvalFailureReasonReadableTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "eval-failure-reason-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
        }

        /// <summary>The exact fallback the live service printed for 76 cycles. A reason equal to this
        /// while an exception was in flight is the defect, so every test below refuses it by name.</summary>
        private static string Fallback(string queryMode) =>
            $"queryMode '{queryMode}' produced no measurement this cycle";

        /// <summary>"SomethingException: some text" — the shape <c>DescribeEvaluationFailure</c> produces.
        /// Matched rather than hard-coded to <c>SqlException</c> so the pin is about the CONTRACT (a type
        /// name is present) and does not break if the data provider changes which exception it raises.</summary>
        private static readonly Regex TypeThenMessage = new(@"^[A-Za-z0-9_.]*Exception: \S", RegexOptions.Compiled);

        // ── 1 + 2 + 3: the cause is named, durable, and readable at Warning ──────────

        /// <summary>
        /// THE LANE'S CENTRAL CLAIM, on the very handler that failed in production. `error_log_scan`
        /// against a refused endpoint must record a reason that names the exception, persist that same
        /// text to disk, and say it once at Warning.
        /// </summary>
        [Fact]
        public async Task ErrorLogScan_thatThrows_namesTheExceptionInTheRecord_theStoreAndAWarning()
        {
            var storePath = Path.Combine(NewDir("scan"), "eval-failures.json");
            var log = new LevelCapturingLogger();
            var (connection, deadEndpoint) = DeadEndpoint();

            using (var svc = BuildEngine(storePath, log))
            {
                await svc.EvaluateSpecialAlertAsync(
                    LogonFailureShape(), connection, deadEndpoint, new AlertGlobalDefaults());

                var failure = Assert.Single(svc.EvaluationFailures);

                // (1) the reason names the exception TYPE and its MESSAGE, and is not the fallback.
                Assert.Matches(TypeThenMessage, failure.ErrorSummary);
                Assert.NotEqual(Fallback("error_log_scan"), failure.ErrorSummary);
                Assert.DoesNotContain("produced no measurement this cycle", failure.ErrorSummary, StringComparison.Ordinal);

                // (3) a WARNING — not a Debug — carries that same text. This is the line an operator
                // reads at the shipped production level.
                var warnings = log.At(LogLevel.Warning);
                Assert.Single(warnings);
                Assert.Contains(failure.ErrorSummary, warnings[0], StringComparison.Ordinal);
                Assert.Contains("logon_failure", warnings[0], StringComparison.Ordinal);
            }

            // (2) the durable store on disk — what the panel renders and what survives a restart —
            // carries the identical text, not a summarised or emptied version of it.
            var persisted = ReadStore(storePath);
            var row = Assert.Single(persisted);
            Assert.Matches(TypeThenMessage, row.ErrorSummary);
            Assert.NotEqual(Fallback("error_log_scan"), row.ErrorSummary);
        }

        // ── 4: once per EPISODE, not once per cycle ──────────────────────────────────

        /// <summary>
        /// The live failure ran 76 cycles. Three cycles here must produce exactly ONE Warning; the
        /// repeats drop to Debug. Without the episode gate this is 3 Warnings and the fix trades an
        /// unreadable log for an unreadable log of a different kind.
        /// </summary>
        [Fact]
        public async Task ThreeFailingCycles_produceOneWarning_andTwoDebugRepeats()
        {
            var storePath = Path.Combine(NewDir("episode"), "eval-failures.json");
            var log = new LevelCapturingLogger();
            var (connection, deadEndpoint) = DeadEndpoint();
            var alert = LogonFailureShape();

            using var svc = BuildEngine(storePath, log);
            for (var cycle = 0; cycle < 3; cycle++)
                await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());

            var failure = Assert.Single(svc.EvaluationFailures);
            Assert.Equal(3, failure.FailureCount); // all three cycles really ran and really failed

            var warnings = log.At(LogLevel.Warning);
            Assert.Single(warnings);

            // The repeats are not silent — they are Debug, carrying the same named cause for anyone
            // who does turn Debug on.
            var debugRepeats = log.At(LogLevel.Debug)
                .Where(t => t.Contains("still cannot be evaluated", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(2, debugRepeats.Count);
            Assert.All(debugRepeats, t => Assert.Contains(failure.ErrorSummary, t, StringComparison.Ordinal));
        }

        /// <summary>
        /// The episode boundary is real, not a once-ever latch: after the alert measures cleanly and the
        /// episode closes, the NEXT failure is a NEW episode and must warn again. A latch would silence
        /// every future outage of this alert for the life of the process.
        /// </summary>
        [Fact]
        public async Task AfterRecovery_aNewEpisode_warnsAgain()
        {
            var storePath = Path.Combine(NewDir("reopen"), "eval-failures.json");
            var log = new LevelCapturingLogger();
            var (connection, deadEndpoint) = DeadEndpoint();
            var alert = LogonFailureShape();
            var stateKey = $"{alert.Id}:{deadEndpoint}".ToLowerInvariant();

            using var svc = BuildEngine(storePath, log);

            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());
            Assert.Single(log.At(LogLevel.Warning));

            // Recovery, exactly as a clean measurement performs it.
            svc.ClearEvaluationFailure(stateKey);
            Assert.Empty(svc.EvaluationFailures);

            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());
            Assert.Equal(2, log.At(LogLevel.Warning).Count);
        }

        // ── the class, not just error_log_scan ───────────────────────────────────────

        /// <summary>
        /// Every special handler that can fail to measure must surrender its exception, not only the one
        /// that broke in production. `io_error_check`, `deadlock_count` and `registry_check` all reach
        /// the same catch-and-return-null shape; each is driven here through the real evaluator.
        /// </summary>
        [Theory]
        [InlineData("io_error_check")]
        [InlineData("deadlock_count")]
        [InlineData("registry_check")]
        public async Task EverySpecialHandler_thatCannotMeasure_namesItsException(string queryMode)
        {
            var storePath = Path.Combine(NewDir("class-" + queryMode), "eval-failures.json");
            var log = new LevelCapturingLogger();
            var (connection, deadEndpoint) = DeadEndpoint();

            using var svc = BuildEngine(storePath, log);
            await svc.EvaluateSpecialAlertAsync(
                new AlertDefinition
                {
                    Id = "probe_" + queryMode,
                    Name = "Probe",
                    QueryMode = queryMode,
                    Operator = "greater_than",
                    Thresholds = new AlertThresholds { Warning = 1 },
                },
                connection, deadEndpoint, new AlertGlobalDefaults());

            var failure = Assert.Single(svc.EvaluationFailures);
            Assert.Matches(TypeThenMessage, failure.ErrorSummary);
            Assert.NotEqual(Fallback(queryMode), failure.ErrorSummary);

            var warning = Assert.Single(log.At(LogLevel.Warning));
            Assert.Contains(failure.ErrorSummary, warning, StringComparison.Ordinal);
        }

        /// <summary>
        /// The honest counterpart: a queryMode with NO handler throws nothing, so there is no exception
        /// to name and the fallback sentence is the whole truth. This test is what stops the fix being
        /// implemented as "always claim an exception" — and it keeps the fallback string covered, so a
        /// future edit cannot quietly delete the honest branch.
        /// </summary>
        [Fact]
        public async Task AnUnwiredQueryMode_hasNoExceptionToName_soTheFallbackStands()
        {
            var storePath = Path.Combine(NewDir("unwired"), "eval-failures.json");
            var log = new LevelCapturingLogger();
            var (connection, deadEndpoint) = DeadEndpoint();

            using var svc = BuildEngine(storePath, log);
            await svc.EvaluateSpecialAlertAsync(
                new AlertDefinition
                {
                    Id = "mystery",
                    Name = "Mystery",
                    QueryMode = "an_unwired_query_mode",
                    Operator = "greater_than",
                    Thresholds = new AlertThresholds { Warning = 1 },
                },
                connection, deadEndpoint, new AlertGlobalDefaults());

            var failure = Assert.Single(svc.EvaluationFailures);
            Assert.Equal(Fallback("an_unwired_query_mode"), failure.ErrorSummary);

            // Still loud once: an unmeasurable alert is a monitoring gap whatever the cause.
            var warning = Assert.Single(log.At(LogLevel.Warning));
            Assert.Contains(Fallback("an_unwired_query_mode"), warning, StringComparison.Ordinal);
        }

        // ── 5: the honesty behaviour is unchanged ────────────────────────────────────

        /// <summary>
        /// Unknown, never a clean Ok, and never a page. The fix is about READABILITY of a failure; if it
        /// had moved the failure itself — cleared the record, or started dispatching — that would be a
        /// worse defect than the one it closes.
        /// </summary>
        [Fact]
        public async Task TheFailureItself_isUnchanged_stillUnknownNeverOk_andNeverDispatched()
        {
            var storePath = Path.Combine(NewDir("honesty"), "eval-failures.json");
            var toast = new ToastService();
            var shown = 0;
            toast.OnShow += _ => Interlocked.Increment(ref shown);
            var (connection, deadEndpoint) = DeadEndpoint();

            using var svc = BuildEngine(storePath, new LevelCapturingLogger(), toast);
            await svc.EvaluateSpecialAlertAsync(
                LogonFailureShape(), connection, deadEndpoint, new AlertGlobalDefaults());

            Assert.True(svc.HasEvaluationFailure(deadEndpoint),
                "an unmeasurable special alert must still record a failure so the NOC reads Unknown, not Ok");
            Assert.DoesNotContain(svc.ActiveAlerts, a => a.AlertId == "logon_failure");
            Assert.Equal(0, shown); // a monitoring gap is made visible, never paged
        }

        // ── 6: the anti-regression that chose the design ─────────────────────────────

        /// <summary>
        /// WHY THE HANDLERS RETURN A PAIR AND DO NOT RETHROW. `CheckConnectivityAsync` turns a refused
        /// connection into the VALUE 1 — that reading is what makes instance_unreachable fire. Had the
        /// handlers rethrown to surrender their exceptions, a down instance would have stopped firing a
        /// Critical and started reading Unknown: an observability fix that broke the alert it was meant
        /// to explain. So: against the same dead endpoint, connectivity must record NO failure and must
        /// still produce a measurement.
        /// </summary>
        [Fact]
        public async Task ConnectivityCheck_stillMeasuresAnUnreachableServer_andRecordsNoFailure()
        {
            var storePath = Path.Combine(NewDir("conn"), "eval-failures.json");
            var log = new LevelCapturingLogger();
            var (connection, deadEndpoint) = DeadEndpoint();

            using var svc = BuildEngine(storePath, log);
            svc.DryRun = true; // measure and compare, but do not dispatch the resulting Critical
            await svc.EvaluateSpecialAlertAsync(
                new AlertDefinition
                {
                    // The SHIPPED shape: operator greater_than over warning 0, so the handler's
                    // value of 1 clears it. IsThresholdBreached is strict (> never >=), which is
                    // exactly why warning is 0 and not 1 — see SpecialAlertPathNeverFiresTests.
                    Id = "instance_unreachable",
                    Name = "Instance unreachable",
                    QueryMode = "connectivity_check",
                    Operator = "greater_than",
                    Thresholds = new AlertThresholds { Warning = 0 },
                    Severity = "Critical",
                },
                connection, deadEndpoint, new AlertGlobalDefaults());

            Assert.False(svc.HasEvaluationFailure(deadEndpoint),
                "connectivity_check MEASURES an unreachable server as 1; it must never be recorded as unmeasurable");

            // special-alerts-fire-silently (2026-09-10): this read `Assert.Empty(log.At(Warning))`.
            // The claim it makes is "nothing was recorded as UNMEASURABLE" — it could be written as
            // "no Warning at all" only because the special path, uniquely, wrote nothing when it
            // FIRED. It now writes the same "Alert fired" line the standard path has always written,
            // so the assertion is narrowed to what it always meant. Narrowed and not deleted: an
            // eval-failure Warning here is still the regression this test exists to catch, and the
            // filter is anchored at the start of the line so a failure message merely CONTAINING
            // those words cannot slip through it.
            Assert.Empty(log.At(LogLevel.Warning)
                .Where(t => !t.StartsWith("Alert fired:", StringComparison.Ordinal)));
            Assert.Single(log.At(LogLevel.Warning));   // the fire line, and only it
            Assert.Contains(svc.ActiveAlerts, a => a.AlertId == "instance_unreachable");
        }

        // ── the reason format itself ─────────────────────────────────────────────────

        /// <summary>
        /// The wording contract in one line, so a reader of the failure store knows what they are looking
        /// at: exception type, colon, space, message. The type is the half the old `ex.Message` dropped,
        /// and it is often the whole diagnosis.
        /// </summary>
        [Fact]
        public void DescribeEvaluationFailure_carriesTypeThenMessage()
        {
            var text = AlertEvaluationService.DescribeEvaluationFailure(
                new InvalidOperationException("xp_readerrorlog is not available"));

            Assert.Equal("InvalidOperationException: xp_readerrorlog is not available", text);
            Assert.Matches(TypeThenMessage, text);
        }

        // ── helpers ──────────────────────────────────────────────────────────────────

        /// <summary>The shape of the alert that failed live: the shipped id and queryMode.</summary>
        private static AlertDefinition LogonFailureShape() => new()
        {
            Id = "logon_failure",
            Name = "Logon failures",
            QueryMode = "error_log_scan",
            Operator = "greater_than",
            Thresholds = new AlertThresholds { Warning = 5, Critical = 20 }, // the shipped thresholds
        };

        private string NewDir(string tag)
        {
            var d = Path.Combine(_tempDir, tag);
            Directory.CreateDirectory(d);
            return d;
        }

        private static List<AlertEvalFailure> ReadStore(string path)
        {
            Assert.True(File.Exists(path), "the durable failure store must have been written to disk");
            return JsonSerializer.Deserialize<List<AlertEvalFailure>>(File.ReadAllText(path)) ?? new();
        }

        private static AlertEvaluationService BuildEngine(
            string storePath, ILogger<AlertEvaluationService> logger, ToastService? toast = null)
        {
            var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
            var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);

            return new AlertEvaluationService(
                logger,
                new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance),
                new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
                new AlertingService(NullLogger<AlertingService>.Instance),
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                toast ?? new ToastService(),
                channels,
                new liveQueriesCacheStore(),
                new InlineOrchestrator(),
                evalFailureStorePath: storePath);
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

        /// <summary>Captures level AND rendered text. The whole lane turns on the pair: the old code
        /// said the right sort of thing at a level production never emits.</summary>
        private sealed class LevelCapturingLogger : ILogger<AlertEvaluationService>
        {
            private readonly List<(LogLevel Level, string Text)> _lines = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => Scope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_lines) _lines.Add((logLevel, formatter(state, exception)));
            }

            public List<string> At(LogLevel level)
            {
                lock (_lines) return _lines.Where(l => l.Level == level).Select(l => l.Text).ToList();
            }

            private sealed class Scope : IDisposable
            {
                public static readonly Scope Instance = new();
                public void Dispose() { }
            }
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
