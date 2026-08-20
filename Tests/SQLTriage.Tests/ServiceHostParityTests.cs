/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Host parity between the two startup lanes (2026-07-31).
    ///
    /// <para>The defect this guards is DRIFT, and it had already happened: the installed Windows
    /// service registered every background engine and STARTED almost none of them. App.xaml.cs
    /// explicitly resolved and Start()ed ScheduledTaskEngine, ConnectionHealthService and
    /// AlertEvaluationService; WindowsServiceHost.InitializeBackgroundServices resolved none of the
    /// three. Nothing failed loudly — a service install simply ran no scheduled assessment, pinged
    /// no server, and evaluated no alert, and every subscriber that attaches on the engine's single
    /// startup resolve stayed unattached for the life of the process.</para>
    ///
    /// <para>A runtime test cannot catch this: both hosts build real containers over real SQL
    /// dependencies. So the guard is over SOURCE — every service the desktop host resolves during
    /// startup must either be resolved somewhere in the service host too, or appear in
    /// <see cref="DesktopOnly"/> with a written reason. Adding a service to App.xaml.cs's startup
    /// and forgetting the service host now fails a test instead of shipping a silent no-op.</para>
    /// </summary>
    public class ServiceHostParityTests
    {
        /// <summary>
        /// Services the desktop host resolves at startup that the service host deliberately does
        /// NOT. Each entry is a decision, not an oversight — a name may only be added here with a
        /// reason that survives being read out loud.
        /// </summary>
        private static readonly Dictionary<string, string> DesktopOnly = new(StringComparer.Ordinal)
        {
            ["ICacheHotTier"] =
                "Only the compact-on-memory-pressure target for the WPF lane, whose dominant footprint " +
                "is WebView2 (Chromium child processes). A headless host has no WebView2 and no such " +
                "pressure profile; MemoryMonitorService itself IS resolved by the service host.",

            ["ConfigurationValidator"] =
                "Desktop startup validation that surfaces its findings in the WPF shell. Headless, the " +
                "same configuration is validated on the paths that use it.",

            ["PerformanceInspectorService"] =
                "Applies a persisted per-operator UI toggle before the first dashboard renders. Nothing " +
                "background depends on it.",

            ["DevBridgeService"] =
                "DEBUG + !SQLT_NO_DEVTOOLS only, and the dev bridge is a desktop developer affordance.",

            ["SessionDataService"] =
                "Live Monitor page pre-warm. Pure latency optimisation on first page view; the page " +
                "fetches on demand when it is opened.",

            ["HistoricalPerformanceService"] =
                "Hourly wait-stats rollup timer. SELF-HEALING: the singleton starts its timer when the " +
                "Performance Trends page first resolves it, so a service install loses at most the " +
                "pre-warm, never data. Starting it eagerly headless is a separate decision.",

            ["CodeHotspotsCacheService"] =
                "5-minute snapshot loop for the /code-hotspots Delta view. Same self-healing shape as " +
                "HistoricalPerformanceService above.",

            ["DashboardConfigService"] =
                "Resolved by the desktop host only as an argument to the liveQueries table provisioning " +
                "pass below.",
            ["IDbConnectionFactory"] =
                "As DashboardConfigService — an argument to the liveQueries table provisioning pass.",
            ["liveQueriesTableService"] =
                "Best-effort liveQueries table provisioning at desktop startup. The panels provision on " +
                "demand; a headless host that never opens a panel needs no tables.",
            ["ServerConnectionManager"] =
                "Resolved by the desktop host to wire the Live Monitor prefetch to OnConnectionChanged. " +
                "The service host resolves it transitively through every engine that takes it.",
        };

        /// <summary>
        /// Reads a source file relative to the repo root, failing loudly rather than passing over
        /// a file it could not find (a guard that silently scans nothing is worse than no guard).
        /// </summary>
        private static string ReadSource(string relativePath)
        {
            var path = Path.Combine(RawPassedScan.RepoRoot().FullName, relativePath);
            Assert.True(File.Exists(path),
                $"host-parity guard cannot read '{relativePath}' — it must scan real source, not pass over nothing.");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// Every <c>GetService&lt;T&gt;</c> type argument in <paramref name="source"/>, reduced to
        /// the bare type name (namespace qualifiers differ between the two files: App.xaml.cs writes
        /// <c>Data.Services.ScheduledTaskEngine</c>, WindowsServiceHost writes <c>ScheduledTaskEngine</c>).
        /// </summary>
        private static HashSet<string> ResolvedServices(string source) =>
            Regex.Matches(source, @"GetService<([A-Za-z0-9_.]+)>")
                 .Select(m => m.Groups[1].Value.Split('.').Last())
                 .ToHashSet(StringComparer.Ordinal);

        /// <summary>The desktop host's startup block: OnStartup up to (not including) OnExit.</summary>
        private static string DesktopStartupBlock()
        {
            var app = ReadSource("App.xaml.cs");
            var start = app.IndexOf("protected override void OnStartup", StringComparison.Ordinal);
            var end = app.IndexOf("protected override void OnExit", StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start,
                "App.xaml.cs no longer has an OnStartup..OnExit region — the parity guard cannot delimit " +
                "the desktop startup block and must be re-anchored rather than silently scanning everything.");
            return app.Substring(start, end - start);
        }

        // ── 1. The parity guard itself ───────────────────────────────────────────────────────

        [Fact]
        public void Every_service_the_desktop_host_starts_is_started_by_the_service_host_too()
        {
            var desktop = ResolvedServices(DesktopStartupBlock());
            var service = ResolvedServices(ReadSource(Path.Combine("Data", "Services", "WindowsServiceHost.cs")));

            var missing = desktop.Except(service).Except(DesktopOnly.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList();

            Assert.True(missing.Count == 0,
                "App.xaml.cs resolves these at startup but WindowsServiceHost never does, so an installed " +
                "service runs without them: " + string.Join(", ", missing) +
                ". Start them in WindowsServiceHost.InitializeBackgroundServices, or add each to " +
                nameof(DesktopOnly) + " with a reason.");
        }

        [Fact]
        public void The_desktop_only_list_carries_no_stale_entries()
        {
            var desktop = ResolvedServices(DesktopStartupBlock());
            var stale = DesktopOnly.Keys.Where(k => !desktop.Contains(k)).OrderBy(x => x, StringComparer.Ordinal).ToList();

            Assert.True(stale.Count == 0,
                "these are excused from host parity but the desktop host no longer resolves them at " +
                "startup, so the excuse is dead text: " + string.Join(", ", stale));
        }

        // ── 2. The three engines the portal lane rides — named, not merely counted ────────────

        [Theory]
        [InlineData("ScheduledTaskEngine")]
        [InlineData("ConnectionHealthService")]
        [InlineData("AlertEvaluationService")]
        public void The_service_host_starts_the_engines_the_portal_lane_rides(string engine)
        {
            Assert.Contains(engine,
                ResolvedServices(ReadSource(Path.Combine("Data", "Services", "WindowsServiceHost.cs"))));
        }

        // ── 3. Stop symmetry ─────────────────────────────────────────────────────────────────

        [Fact]
        public void The_service_host_registers_a_shutdown_that_stops_what_it_started()
        {
            var source = ReadSource(Path.Combine("Data", "Services", "WindowsServiceHost.cs"));

            Assert.Contains("ApplicationStopping.Register", source);
            Assert.Contains("StopBackgroundServices", source);
        }

        // ── 3b. Shutdown on the FAILED-START path ────────────────────────────────────────────
        //
        // Proven live by the verifier: with a port clash, every engine started, the host logged a
        // fatal and exited 1, and "Background services stopped" never appeared. ApplicationStopping
        // only fires once the host has STARTED, so a bind failure skips it entirely — and with the
        // SCM's configured restart actions that becomes a loop in which every attempt starts every
        // engine again and a due scheduled assessment can fire against production once per restart.
        //
        // RunServer builds a real container over real SQL dependencies and then blocks in app.Run(),
        // so there is no seam to drive it from a unit test. These guards are over SOURCE ORDER
        // instead — which is exactly what the defect was: two correct statements in the wrong order.

        [Fact]
        public void The_stop_hook_is_registered_before_any_engine_is_started()
        {
            var source = ReadSource(Path.Combine("Data", "Services", "WindowsServiceHost.cs"));

            var register = source.IndexOf("ApplicationStopping.Register", StringComparison.Ordinal);
            var start = source.IndexOf("InitializeBackgroundServices(app.Services)", StringComparison.Ordinal);

            Assert.True(register >= 0, "the ApplicationStopping registration is gone entirely.");
            Assert.True(start >= 0, "RunServer no longer calls InitializeBackgroundServices — re-anchor this guard.");
            Assert.True(register < start,
                "the stop hook is registered AFTER the engines are started, so it does not cover the window " +
                "between the two — and a throw inside that window leaves every timer running.");
        }

        [Fact]
        public void A_failed_start_still_stops_the_engines_from_the_finally()
        {
            var source = ReadSource(Path.Combine("Data", "Services", "WindowsServiceHost.cs"));

            // The tail of RunServer: everything after the fatal-exit catch, i.e. the finally.
            var fatal = source.IndexOf("Log.Fatal(ex, \"SQLTriage Service failed to start\")", StringComparison.Ordinal);
            Assert.True(fatal > 0, "RunServer's fatal-startup catch is gone — re-anchor this guard.");
            var close = source.IndexOf("Log.CloseAndFlush()", fatal, StringComparison.Ordinal);
            Assert.True(close > fatal, "RunServer's finally is gone — re-anchor this guard.");
            var tail = source.Substring(fatal, close - fatal);

            Assert.True(tail.Contains("StopBackgroundServicesOnce()", StringComparison.Ordinal),
                "nothing stops the background engines on the failed-start path. ApplicationStopping never " +
                "fires when the host does not reach a started state (a Kestrel bind clash), so the finally " +
                "must call the stop itself.");
        }

        [Fact]
        public void The_stop_is_idempotent_so_a_graceful_stop_does_not_run_it_twice()
        {
            var source = ReadSource(Path.Combine("Data", "Services", "WindowsServiceHost.cs"));

            Assert.True(source.Contains("Interlocked.Exchange(ref backgroundStopGuard", StringComparison.Ordinal),
                "the hook and the finally can both fire on a graceful stop. Without a run-once guard the " +
                "whole teardown runs twice and the log claims two shutdowns.");
        }

        [Fact]
        public void The_shutdown_comment_does_not_claim_a_ctrl_c_path_this_binary_does_not_have()
        {
            // SQLTriage.exe is a WinExe with no console attached and no CancelKeyPress handler — the
            // verifier proved a graceful taskkill is ignored. A comment naming Ctrl-C would send the
            // next reader looking for a path that does not exist.
            var source = ReadSource(Path.Combine("Data", "Services", "WindowsServiceHost.cs"));
            var lifetimeClaim = source.IndexOf("ApplicationStopping covers", StringComparison.Ordinal);

            Assert.True(lifetimeClaim > 0, "the lifetime comment is gone — re-anchor this guard.");
            var paragraph = source.Substring(lifetimeClaim, Math.Min(900, source.Length - lifetimeClaim));

            Assert.DoesNotContain("fires on an SCM stop, a Ctrl-C in console mode", paragraph);
            Assert.Contains("does NOT", paragraph);   // the limitation is stated, not implied
        }

        [Fact]
        public void Every_engine_started_by_the_service_host_is_also_captured_for_stopping()
        {
            var source = ReadSource(Path.Combine("Data", "Services", "WindowsServiceHost.cs"));
            var startIdx = source.IndexOf("InitializeBackgroundServices(IServiceProvider services)", StringComparison.Ordinal);
            var stopIdx = source.IndexOf("internal readonly record struct BackgroundStop", StringComparison.Ordinal);
            Assert.True(startIdx > 0 && stopIdx > startIdx,
                "InitializeBackgroundServices no longer returns a captured teardown — re-anchor this guard.");
            var startBlock = source.Substring(startIdx, stopIdx - startIdx);

            // Every Capture(...) call in the start block, by the local it captures.
            var captured = Regex.Matches(startBlock, @"Capture\((\w+),")
                                .Select(m => m.Groups[1].Value)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // The loop-and-timer engines. A pure fire-and-forget resolve (UptimeTrackerService) or a
            // one-shot call (AuditLogService.LogApplicationStart) has nothing to stop.
            foreach (var (engine, local) in new[]
                     {
                         ("ScheduledTaskEngine", "engine"),
                         ("ConnectionHealthService", "connectionHealth"),
                         ("AlertEvaluationService", "alertEvaluation"),
                         ("BlockingForensicsService", "blockingForensics"),
                         ("WaitStatsService", "waitStats"),
                         ("AcceptedFindingsService", "acceptedFindings"),
                         ("HealthMetricsCollectorService", "healthMetrics"),
                         ("CacheEvictionService", "cacheEviction"),
                         ("liveQueriesMaintenanceService", "liveQueriesMaintenance"),
                     })
            {
                Assert.True(captured.Contains(local),
                    $"{engine} is started by the service host but its stop is never captured — its timer/loop " +
                    "would outlive the process. Add a Capture(...) beside its Start().");
            }
        }

        // ── 3c. The teardown, exercised — not merely read ────────────────────────────────────
        //
        // Round 1's guards asserted source ORDER and all passed while the runtime effect was nil:
        // the teardown ran on the failed-start path and stopped nothing, because it re-resolved every
        // engine from a provider app.Run() had already disposed. Text cannot catch that. These do.

        private sealed class FakeEngine
        {
            public int Stops;
            public void Stop() => Stops++;
        }

        [Fact]
        public void The_teardown_runs_every_captured_stop_in_reverse_capture_order()
        {
            var order = new List<string>();
            var stops = new[]
            {
                new WindowsServiceHost.BackgroundStop("first", () => order.Add("first")),
                new WindowsServiceHost.BackgroundStop("second", () => order.Add("second")),
                new WindowsServiceHost.BackgroundStop("third", () => order.Add("third")),
            };

            var outcome = WindowsServiceHost.StopBackgroundServices(stops);

            // Reverse of start order: the engines started last are the ones depending on the rest.
            Assert.Equal(new[] { "third", "second", "first" }, order);
            Assert.Equal(3, outcome.Attempted);
            Assert.Equal(0, outcome.Failed);
        }

        [Fact]
        public void The_teardown_still_stops_every_engine_after_the_container_is_disposed()
        {
            // The proven defect, reproduced end to end. app.Run() disposes the provider BEFORE the
            // Kestrel bind exception propagates, so the finally's teardown meets a dead container.
            var provider = new ServiceCollection().AddSingleton<FakeEngine>().BuildServiceProvider();
            var engine = provider.GetRequiredService<FakeEngine>();
            var stops = new[] { new WindowsServiceHost.BackgroundStop(nameof(FakeEngine), engine.Stop) };

            provider.Dispose();

            var outcome = WindowsServiceHost.StopBackgroundServices(stops);

            Assert.Equal(0, outcome.Failed);
            Assert.Equal(1, engine.Stops);
        }

        [Fact]
        public void Re_resolving_from_a_disposed_container_is_exactly_what_used_to_break_this()
        {
            // The negative control for the test above: this is what the previous teardown did eleven
            // times over, and every throw was swallowed to a warning while the clean line still went
            // out. If this ever stops throwing, the test above is no longer proving anything.
            var provider = new ServiceCollection().AddSingleton<FakeEngine>().BuildServiceProvider();
            provider.GetRequiredService<FakeEngine>();
            provider.Dispose();

            Assert.Throws<ObjectDisposedException>(() => provider.GetService<FakeEngine>());
        }

        [Fact]
        public void One_failing_stop_neither_skips_the_rest_nor_is_swallowed_from_the_count()
        {
            var later = new FakeEngine();
            var earlier = new FakeEngine();
            var stops = new[]
            {
                new WindowsServiceHost.BackgroundStop("earlier", earlier.Stop),
                new WindowsServiceHost.BackgroundStop("boom", () => throw new InvalidOperationException("locked")),
                new WindowsServiceHost.BackgroundStop("later", later.Stop),
            };

            var outcome = WindowsServiceHost.StopBackgroundServices(stops);

            Assert.Equal(1, later.Stops);       // ran before the fault (reverse order)
            Assert.Equal(1, earlier.Stops);     // ran after it — one fault never skips the rest
            Assert.Equal(3, outcome.Attempted);
            Assert.Equal(1, outcome.Failed);    // and the fault is COUNTED, not merely logged
            Assert.Equal(0, outcome.AlreadyStopped);
        }

        // ── 3d. Already-stopped is not a failure ─────────────────────────────────────────────
        //
        // Proven live by the gate on the failed-start path: the host disposes its container FIRST,
        // each engine's own Dispose genuinely stops it, and only then does the finally run. Six of
        // the eleven then threw ObjectDisposedException from CancellationTokenSource.Cancel() on an
        // already-disposed CTS. Counting those as failures printed "stopped with 6 of 11 failing",
        // which mid-incident reads as a broken teardown when everything was in fact already down.

        [Fact]
        public void An_already_disposed_engine_is_reported_as_stopped_not_as_a_failure()
        {
            // The exact shape the six threw: Cancel() on a CTS its owner already disposed.
            var cts = new CancellationTokenSource();
            cts.Dispose();
            var stops = new[] { new WindowsServiceHost.BackgroundStop("ScheduledTaskEngine", () => cts.Cancel()) };

            var outcome = WindowsServiceHost.StopBackgroundServices(stops);

            Assert.Equal(1, outcome.Attempted);
            Assert.Equal(0, outcome.Failed);          // NOT a failure — the engine is already down
            Assert.Equal(1, outcome.AlreadyStopped);
        }

        [Fact]
        public void Cancel_after_dispose_really_does_throw_which_is_why_this_classification_exists()
        {
            // Negative control for the test above, and the correction of an earlier claim in this
            // file's history: cancel-a-CTS is idempotent for Cancel-after-CANCEL, NOT for
            // Cancel-after-DISPOSE. If this ever stops throwing, the classification is dead code.
            var cts = new CancellationTokenSource();
            cts.Cancel();
            cts.Cancel();                              // idempotent — no throw
            cts.Dispose();

            Assert.Throws<ObjectDisposedException>(() => cts.Cancel());
        }

        [Fact]
        public void Already_stopped_engines_never_mask_a_genuine_failure_beside_them()
        {
            var disposed = new CancellationTokenSource();
            disposed.Dispose();
            var live = new FakeEngine();
            var stops = new[]
            {
                new WindowsServiceHost.BackgroundStop("live", live.Stop),
                new WindowsServiceHost.BackgroundStop("alreadyStopped", () => disposed.Cancel()),
                new WindowsServiceHost.BackgroundStop("boom", () => throw new InvalidOperationException("locked")),
            };

            var outcome = WindowsServiceHost.StopBackgroundServices(stops);

            Assert.Equal(3, outcome.Attempted);
            Assert.Equal(1, outcome.Failed);          // the real fault still counts, and still warns
            Assert.Equal(1, outcome.AlreadyStopped);
            Assert.Equal(1, live.Stops);              // and neither one skipped the engine beside them
        }

        [Fact]
        public void A_plain_InvalidOperationException_is_not_excused_as_already_stopped()
        {
            // ObjectDisposedException DERIVES from InvalidOperationException, so a catch in the wrong
            // order would quietly excuse every InvalidOperationException as "already stopped".
            var stops = new[]
            {
                new WindowsServiceHost.BackgroundStop("boom", () => throw new InvalidOperationException("locked")),
            };

            var outcome = WindowsServiceHost.StopBackgroundServices(stops);

            Assert.Equal(1, outcome.Failed);
            Assert.Equal(0, outcome.AlreadyStopped);
        }

        [Fact]
        public void An_empty_or_absent_teardown_is_a_no_op_that_claims_nothing()
        {
            // A throw before the engines were started leaves nothing to stop. It must not report a
            // shutdown it did not perform.
            Assert.Equal(0, WindowsServiceHost.StopBackgroundServices(null).Attempted);
            Assert.Equal(0, WindowsServiceHost.StopBackgroundServices(Array.Empty<WindowsServiceHost.BackgroundStop>()).Attempted);
        }

        [Fact]
        public void The_clean_completion_line_is_reachable_only_when_the_teardown_was_clean()
        {
            // The honesty rule, held at the source: the Information line must sit on the else of a
            // failure count, never after it unconditionally. (The counts themselves are asserted
            // behaviourally above; this pins which line the counts select.)
            var source = ReadSource(Path.Combine("Data", "Services", "WindowsServiceHost.cs"));
            var stopIdx = source.IndexOf("internal static StopOutcome StopBackgroundServices", StringComparison.Ordinal);
            Assert.True(stopIdx > 0, "StopBackgroundServices is gone — re-anchor this guard.");
            var block = source.Substring(stopIdx);

            var guard = block.IndexOf("if (failed > 0)", StringComparison.Ordinal);
            var cleanLine = block.IndexOf("Log.Information(\"Background services stopped", StringComparison.Ordinal);

            Assert.True(guard > 0, "nothing counts the failures, so the completion line cannot be honest.");
            Assert.True(cleanLine > guard,
                "the clean 'Background services stopped' line is reachable without passing the failure " +
                "check — that is how a teardown that stopped nothing still claimed success.");
        }

        [Fact]
        public void The_alarming_completion_line_is_reachable_only_for_GENUINE_failures()
        {
            // The mirror of the rule above, and the one the gate's live run caught: an
            // already-disposed engine is not a failure, so it must not be able to reach the warning
            // line. Behaviourally asserted above via outcome.Failed; this pins that the WARNING is
            // selected by that same count and not by the total.
            var source = ReadSource(Path.Combine("Data", "Services", "WindowsServiceHost.cs"));
            var stopIdx = source.IndexOf("internal static StopOutcome StopBackgroundServices", StringComparison.Ordinal);
            var block = source.Substring(stopIdx);

            var guard = block.IndexOf("if (failed > 0)", StringComparison.Ordinal);
            var warnLine = block.IndexOf("Log.Warning(\"Background services stopped with", StringComparison.Ordinal);

            Assert.True(warnLine > guard && guard > 0,
                "the 'stopped with N failing' warning is not gated on the genuine-failure count.");

            // And the classification that keeps already-stopped out of that count must still exist.
            Assert.Contains("catch (ObjectDisposedException)", block);
        }
    }
}
