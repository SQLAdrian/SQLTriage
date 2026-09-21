/* In the name of God, the Merciful, the Compassionate */

// ── special-alerts-fire-silently (2026-09-10): a special alert must fire IN THE LOG ─────────────
//
// THE DEFECT, PROVED LIVE BEFORE THIS CLASS WAS WRITTEN. AlertEvaluationService has two firing
// paths. EvaluateAlertAsync (the "standard" path, every metric alert) ends its new-alert branch
// with one Warning:
//
//     Alert fired: {AlertName} on {Server} ({Severity}) - value: {Value}{DryRun}
//
// EvaluateSpecialAlertAsync (the queryMode path) built the AlertState, upserted the history row,
// dispatched the notification, stamped _lastNotified — and logged NOTHING, at any level. A
// `grep -rIn 'Alert fired' --include=*.cs` over the tree returned exactly ONE hit, in the standard
// path. Seven shipped alerts route through the silent one, four of them Critical:
//
//     connectivity_check       instance_unreachable    Critical
//     host_connectivity_check  machine_unreachable     Critical
//     error_log_scan           error_log_fatal         Critical
//     registry_check           windows_power_plan      Critical
//     error_log_scan           error_log_severity      High
//     deadlock_count           deadlock                High
//     error_log_scan           logon_failure           Low
//
// That column is the DECLARED severity in alert-definitions.json, not what the log prints. The
// evaluator emits only Warning or Critical, chosen by RuntimeSeverity from which threshold was
// crossed - so logon_failure, declared Low, logs as Warning and then Critical. Counted by
// cardinality at this tip: 80 alerts, 7 carry a queryMode, all 7 enabled, 4 Critical by declaration.
//
// Measured on the installed service on 2026-09-10 (builds 4004/4008): `logon_failure` fired Warning
// and then Critical (24.0 against a threshold of 20.0) on `.\old2017`, was visible on /alerts-noc,
// and was ABSENT from the service log. Evidence: the project's private evidence archive,
// evidence/logon-failure-alert-fires-2026-09-10/.
//
// WHY IT IS NOT TIDINESS. After an incident the service log is what a support engineer reads, and
// it said nothing about a server having gone unreachable. It also cost this project a working
// conclusion: the lane that found this first concluded "the alert does not fire" from the log
// silence, and was wrong. The same trap waits for a client's DBA.
//
// THE ORACLE DISCIPLINE THIS CLASS OBEYS, because it is the lesson that produced the lane: a fire
// is PROVED by svc.ActiveAlerts — the state the NOC renders — and never by the log line. The log is
// the thing under test; it is never also the witness that the thing under test happened.
//
// WHAT IS PINNED HERE, all through the production evaluator against a real refused endpoint
// (127.0.0.1,1 — nothing listens, so CheckConnectivityAsync genuinely measures 1; nothing is
// hand-injected):
//
//   1. SHAPE — the special path's line equals the standard template filled from the state, field
//      for field. An emit that dropped the severity, or passed an empty string, cannot satisfy it.
//   2. CARDINALITY — one line per FIRST fire. Three cycles of a still-firing alert produce ONE
//      Warning, not three. (First-fire-only is the standard path's behaviour as written: its
//      re-fire branch does not log either. This lane matched it rather than inventing.)
//   3. RESOLVE — the special path says "Alert resolved" when the condition clears. Before this lane
//      it cleared the state, resolved the history row and said nothing, so the log carried a fire
//      that never ended.
//   4. CENSUS, over the source — every site that assigns a new active state into _activeStates also
//      emits the fire line, through the shared helper, passing the caller's OWN measured value.
//      That is the pin that catches the NEXT path someone adds, which is exactly how this defect
//      was born: a second firing path grew beside the first and inherited none of its telemetry.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

// SQLTriage.Data declares a LogLevel of its own, so the bare name is ambiguous here. The one that
// matters is the logging framework's — the level the shipped host actually filters on.
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace SQLTriage.Tests
{
    public class SpecialAlertFireIsVisibleTests : IDisposable
    {
        private readonly string _tempDir;

        public SpecialAlertFireIsVisibleTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "special-fire-visible-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
        }

        // ── 1. SHAPE: the whole line, field for field ────────────────────────────────

        /// <summary>
        /// THE LANE'S CENTRAL CLAIM. A special alert that fires must write the standard path's line,
        /// whole. The expected string is built from the AlertState the evaluator actually produced —
        /// not from literals — so the assertion fails if the emit hard-codes a field, drops one, or
        /// passes a value other than the one that was measured.
        ///
        /// <para>The alert is SYNTHETIC and deliberately so: its Id and Name differ
        /// (<c>pinned_special_fire</c> vs "Pinned Special Fire"), so an emit that logged
        /// <c>alert.Id</c> — the easy mistake, and invisible with the shipped definitions where the
        /// two read alike — goes RED here.</para>
        /// </summary>
        [Fact]
        public async Task SpecialFirstFire_writesTheStandardAlertFiredLine_wholeAndCorrect()
        {
            var log = new LevelCapturingLogger();
            var (connection, deadEndpoint) = DeadEndpoint();
            using var svc = BuildEngine(log);
            svc.DryRun = true;   // measure and record; dispatch nothing, write no history row

            await svc.EvaluateSpecialAlertAsync(
                PinnedSpecial(), connection, deadEndpoint, new AlertGlobalDefaults());

            // THE ORACLE IS THE STATE, NOT THE LOG. If this is empty the alert did not fire and any
            // claim about its log line would be vacuous.
            var state = Assert.Single(svc.ActiveAlerts);
            Assert.Equal("pinned_special_fire", state.AlertId);

            var fired = Assert.Single(log.At(LogLevel.Warning).Where(IsFireLine));

            // The template, filled from the state the evaluator built. Invariant culture because
            // that is what Microsoft.Extensions.Logging formats its arguments with.
            Assert.Equal(
                string.Format(CultureInfo.InvariantCulture,
                    "Alert fired: {0} on {1} ({2}) - value: {3} [DRY RUN]",
                    state.AlertName, LogAnon.S(deadEndpoint), state.Severity, state.LastValue),
                fired);

            // The same claim spelled out per field, so a future reader can see WHICH part broke.
            Assert.Contains("Pinned Special Fire", fired, StringComparison.Ordinal);   // Name, not Id
            Assert.DoesNotContain("pinned_special_fire", fired, StringComparison.Ordinal);
            Assert.Contains("(Critical)", fired, StringComparison.Ordinal);            // severity
            Assert.Contains("- value: 1", fired, StringComparison.Ordinal);            // the measurement
            Assert.Contains(LogAnon.S(deadEndpoint), fired, StringComparison.Ordinal); // the server
        }

        /// <summary>
        /// The same, driven by the SHIPPED <c>instance_unreachable</c> definition read off
        /// Config/alert-definitions.json rather than a hand-built shape — so the pin covers the bytes
        /// that install, thresholds and severity included, and not a fixture that happens to fire.
        /// This is the Critical an operator most needs to find in the log after an outage.
        /// </summary>
        [Fact]
        public async Task TheShippedInstanceUnreachable_isVisibleInTheLogWhenItFires()
        {
            var shipped = Shipped("instance_unreachable");
            var log = new LevelCapturingLogger();
            var (connection, deadEndpoint) = DeadEndpoint();
            using var svc = BuildEngine(log);
            svc.DryRun = true;

            await svc.EvaluateSpecialAlertAsync(shipped, connection, deadEndpoint, new AlertGlobalDefaults());

            var state = Assert.Single(svc.ActiveAlerts);
            Assert.Equal("instance_unreachable", state.AlertId);
            Assert.Equal("Critical", state.Severity);

            var fired = Assert.Single(log.At(LogLevel.Warning).Where(IsFireLine));
            Assert.Equal(
                string.Format(CultureInfo.InvariantCulture,
                    "Alert fired: {0} on {1} ({2}) - value: {3} [DRY RUN]",
                    shipped.Name, LogAnon.S(deadEndpoint), state.Severity, state.LastValue),
                fired);
        }

        // ── 2. CARDINALITY: one line per first fire, never one per cycle ─────────────

        /// <summary>
        /// A firing alert is re-evaluated every cycle for the life of the incident — every 60 s for
        /// the shipped connectivity alerts. Three cycles must leave ONE fire line. A per-cycle emit
        /// would trade an invisible alert for an unreadable log, which is not a fix.
        ///
        /// <para>Non-vacuity is inside the test: HitCount proves all three cycles really ran and
        /// really re-fired, so the single line is cardinality and not a silent second failure.</para>
        /// </summary>
        [Fact]
        public async Task ThreeFiringCycles_produceExactlyOneFireLine()
        {
            var log = new LevelCapturingLogger();
            var (connection, deadEndpoint) = DeadEndpoint();
            var alert = PinnedSpecial();
            using var svc = BuildEngine(log);
            svc.DryRun = true;

            for (var cycle = 0; cycle < 3; cycle++)
                await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());

            var state = Assert.Single(svc.ActiveAlerts);
            Assert.Equal(3, state.HitCount);   // all three cycles ran, and all three re-fired

            Assert.Single(log.At(LogLevel.Warning).Where(IsFireLine));
        }

        /// <summary>
        /// The standard path's cardinality, read off its own source, is what the claim above matches:
        /// its re-fire branch does not log either, so "first fire only" is the reference behaviour and
        /// not this lane's invention. Pinned in source because driving the standard path's re-fire
        /// needs a live instance and a real metric query; what can be checked without one is that the
        /// two paths are still the same shape.
        /// </summary>
        [Fact]
        public void NeitherPath_logsOnReFire()
        {
            var src = Source();

            // Every fire emit goes through the helper, and there are exactly as many call sites as
            // there are FIRST-fire branches (two). A third would mean a re-fire branch started
            // logging, or a new path appeared — either way, read this test's prose before changing it.
            Assert.Equal(2, CallSites(src).Count);
        }

        // ── 3. RESOLVE: the other half of the same defect ────────────────────────────

        /// <summary>
        /// Fixing only the fire would have made the log WORSE: it would say "Alert fired: Instance
        /// unreachable" and never say it cleared, which reads as a server still down. The standard
        /// path has written "Alert resolved" since it was written; the special path never did.
        ///
        /// <para>The clear is driven through the real evaluator by RAISING the threshold between
        /// cycles — the handler still measures 1, 1 no longer breaches, and the production
        /// condition-cleared branch runs. No state is reached into or hand-edited.</para>
        /// </summary>
        [Fact]
        public async Task WhenTheConditionClears_theSpecialPathSaysSo()
        {
            var log = new LevelCapturingLogger();
            var (connection, deadEndpoint) = DeadEndpoint();
            var alert = PinnedSpecial();
            using var svc = BuildEngine(log);
            svc.DryRun = true;

            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());
            Assert.Single(svc.ActiveAlerts);                       // it fired
            Assert.Single(log.At(LogLevel.Warning).Where(IsFireLine));
            Assert.Empty(log.At(LogLevel.Information).Where(IsResolveLine));   // and has not cleared

            // Same alert id, same server, same handler, same measurement of 1 — a threshold it no
            // longer breaches. This is the production "condition cleared" branch, not a poke at state.
            alert.Thresholds = new AlertThresholds { Warning = 5 };
            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());

            Assert.Empty(svc.ActiveAlerts);                        // the oracle: it really resolved
            var resolved = Assert.Single(log.At(LogLevel.Information).Where(IsResolveLine));
            Assert.Equal(
                $"Alert resolved: Pinned Special Fire on {LogAnon.S(deadEndpoint)}", resolved);

            // And the resolve did not double as a second fire.
            Assert.Single(log.At(LogLevel.Warning).Where(IsFireLine));
        }

        // ── 4. THE CENSUS — the pin that catches the path nobody has written yet ─────

        /// <summary>
        /// THE BEST PIN IN THIS FILE, and the one the brief asked for by name. The defect was born
        /// because a SECOND firing path grew beside the first and inherited none of its telemetry, so
        /// what is asserted here is the invariant rather than the two instances of it: <b>every site
        /// in AlertEvaluationService that assigns a new active state into <c>_activeStates</c> emits
        /// the fire line.</b> A third path added tomorrow fails this test on the day it is written.
        ///
        /// <para><b>Why a source census and not a behavioural one.</b> The property is "no path
        /// exists without the emit" — a statement about paths that do not exist yet, which no
        /// execution can witness. The census reads the shipped source (anchored on the repo root, not
        /// the test-output copy) and is written to fail LOUDLY with the offending line number.</para>
        ///
        /// <para><b>The argument list is part of the census.</b> Requiring the emit to pass the
        /// caller's own <c>value</c> is what stops a future site satisfying the census with a
        /// hard-coded constant — the one hole the behavioural tests above cannot close, because the
        /// connectivity handler can only ever measure 1.</para>
        /// </summary>
        [Fact]
        public void EverySiteThatSetsANewActiveState_alsoEmitsTheFireLine()
        {
            var src = Source();
            var lines = src.Replace("\r\n", "\n").Split('\n');

            // `_activeStates[key] = ...` — an ASSIGNMENT, so `==` is excluded. Both known sites are
            // the creation of a brand-new active AlertState.
            var assignment = new Regex(@"_activeStates\[[^\]]+\]\s*=(?!=)", RegexOptions.Compiled);

            var sites = Enumerable.Range(0, lines.Length)
                .Where(i => assignment.IsMatch(lines[i]))
                .ToList();

            // Non-vacuity: if the regex stops matching, this test must fail rather than pass on an
            // empty census. Two sites at the lane base — special :996, standard :1673.
            Assert.True(sites.Count >= 2,
                $"the census found {sites.Count} `_activeStates[...] =` sites in "
                + "Data/Services/AlertEvaluationService.cs. It found two when this pin was written; "
                + "fewer means the census regex has stopped seeing the firing paths, which is a "
                + "SILENT hole, not a pass.");

            var missing = new List<string>();
            foreach (var i in sites)
            {
                // The emit sits in the same statement block, within a few lines of the assignment.
                var window = lines.Skip(i).Take(WindowLines);
                if (!window.Any(l => l.Contains("LogAlertFired(", StringComparison.Ordinal)))
                    missing.Add($"line {i + 1}: {lines[i].Trim()}");
            }

            Assert.True(missing.Count == 0,
                "A path in AlertEvaluationService sets a new active alert state and does NOT emit the "
                + "\"Alert fired\" line within " + WindowLines + " lines. That is the "
                + "special-alerts-fire-silently defect, reborn: the alert will page an operator and "
                + "leave nothing in the service log a support engineer can read afterwards. Add "
                + "LogAlertFired(alert, serverName, severity, value) to:"
                + Environment.NewLine + string.Join(Environment.NewLine, missing));

            // One emit per site — a shared call hoisted out of a branch, or an extra one added to a
            // re-fire branch, both change this count.
            Assert.Equal(sites.Count, CallSites(src).Count);

            // Each call passes the CALLER'S OWN measured value. Without this a future site could
            // satisfy the census with LogAlertFired(alert, serverName, "Critical", 1).
            var expected = new Regex(
                @"^\s*LogAlertFired\(alert, serverName, severity, value(\.Value)?\);\s*$", RegexOptions.Compiled);
            foreach (var call in CallSites(src))
                Assert.True(expected.IsMatch(call),
                    "the fire emit must pass the caller's own alert, server, severity and measured "
                    + "value, not literals. Found: " + call.Trim());
        }

        /// <summary>
        /// ONE TEMPLATE, FOR EVER. A log parser reading these lines must never meet a second format,
        /// so the message string exists exactly once in the service — in the shared helper — and both
        /// paths reach it by calling that helper. A re-typed copy passes every behavioural test in
        /// this file on the day it is written and drifts from the original a year later.
        /// </summary>
        [Fact]
        public void TheFireAndResolveTemplates_existExactlyOnceEach()
        {
            var src = Source();

            Assert.Equal(1, Occurrences(src, "\"Alert fired: {AlertName} on {Server} ({Severity}) - value: {Value}{DryRun}\""));
            Assert.Equal(1, Occurrences(src, "\"Alert resolved: {AlertName} on {Server}\""));

            // And the sole copy of each lives in the helper, not inline in one of the paths.
            Assert.Contains("private void LogAlertFired(", src, StringComparison.Ordinal);
            Assert.Contains("private void LogAlertResolved(", src, StringComparison.Ordinal);

            // The anonymiser is INSIDE the helper, so no caller can leak a real server name into the
            // log by forgetting it. Both argument lists are pinned whole.
            Assert.Equal(1, Occurrences(src, "alert.Name, LogAnon.S(serverName), severity, value,"));
            Assert.Equal(1, Occurrences(src,
                "_logger.LogInformation(\"Alert resolved: {AlertName} on {Server}\", alert.Name, LogAnon.S(serverName));"));
        }

        // ── helpers ──────────────────────────────────────────────────────────────────

        /// <summary>How far past an <c>_activeStates[...] =</c> assignment the census looks for the
        /// emit. Both known sites emit within 7 lines; 12 leaves room for a comment without leaving
        /// room for the emit to have escaped into a different branch.</summary>
        private const int WindowLines = 12;

        private static bool IsFireLine(string text) =>
            text.StartsWith("Alert fired:", StringComparison.Ordinal);

        private static bool IsResolveLine(string text) =>
            text.StartsWith("Alert resolved:", StringComparison.Ordinal);

        /// <summary>The SHIPPED source, anchored on the solution root — never the test-output copy.</summary>
        private static string Source() => File.ReadAllText(Path.Combine(
            RawPassedScan.RepoRoot().FullName, "Data", "Services", "AlertEvaluationService.cs"));

        private static List<string> CallSites(string src) => src
            .Replace("\r\n", "\n").Split('\n')
            .Where(l => l.Contains("LogAlertFired(", StringComparison.Ordinal)
                     && !l.Contains("private void LogAlertFired(", StringComparison.Ordinal))
            .ToList();

        private static int Occurrences(string haystack, string needle)
        {
            var n = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        /// <summary>
        /// A special alert whose Id and Name deliberately differ, so the emit is pinned to the NAME.
        /// connectivity_check against a refused endpoint measures exactly 1, and greater_than is
        /// strict, which is why warning is 0 (see SpecialAlertPathNeverFiresTests for that lane).
        /// </summary>
        private static AlertDefinition PinnedSpecial() => new()
        {
            Id = "pinned_special_fire",
            Name = "Pinned Special Fire",
            QueryMode = "connectivity_check",
            Operator = "greater_than",
            Thresholds = new AlertThresholds { Warning = 0 },
            Severity = "Critical",
        };

        /// <summary>Read off the bytes that install, not a fixture. Anchored on the solution file:
        /// probing for Config/alert-definitions.json would find the test-output copy.</summary>
        private static AlertDefinition Shipped(string id)
        {
            var json = File.ReadAllText(Path.Combine(
                RawPassedScan.RepoRoot().FullName, "Config", "alert-definitions.json"));
            var file = System.Text.Json.JsonSerializer.Deserialize<AlertDefinitionsFile>(
                json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(file);
            return file!.Alerts.Single(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private AlertEvaluationService BuildEngine(ILogger<AlertEvaluationService> logger)
        {
            var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
            var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);

            return new AlertEvaluationService(
                logger,
                new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance),
                new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
                new AlertingService(NullLogger<AlertingService>.Instance),
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                new ToastService(),
                channels,
                new liveQueriesCacheStore(),
                new InlineOrchestrator(),
                // Never the installed Config/eval-failures.json.
                evalFailureStorePath: Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".json"));
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

        /// <summary>Captures level AND rendered text: the whole lane turns on the pair — a line at a
        /// level production never emits is the same as no line at all.</summary>
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
