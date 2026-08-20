/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Cli;

/// <summary>
/// Composition root for <c>SQLTriage.exe --audit</c> — a non-interactive, headless audit mode,
/// fully free (incl. scheduling: the OS Task Scheduler invokes the exe unattended, there is no
/// in-app scheduler here). Builds its OWN plain ServiceCollection via
/// <see cref="WindowsServiceHost.RegisterAllServices"/> — the exact registration App.xaml.cs and
/// the Kestrel service host share — then resolves only what an audit run needs.
///
/// Deliberately COLD: never calls host.Run()/StartAsync. Kestrel, Blazor, and every hosted
/// background engine (alert evaluation, connection health, wait-stats, the consolidation
/// collector, …) stay unconstructed — DI singletons are lazy, and this class only resolves
/// <see cref="LicenseService"/>, <see cref="CheckRepositoryService"/>,
/// <see cref="CheckExecutionService"/> and <see cref="QuickCheckResultStore"/>, none of whose
/// constructors start a timer or background loop (those are only started by
/// App.xaml.cs.OnStartupAsync / WindowsServiceHost.InitializeBackgroundServices, which this
/// composition root never calls).
///
/// Exit codes (precedence 4 > 3 > 2 > 1 > 0):
///   4  the run was refused by the signed corpus-demo allocation (the same allocation the desktop
///      lane enforces, via the same IDemoRunLedger.TryAdmitRun seam) — always BEFORE any server is
///      touched, so nothing connected and nothing was written. Distinct from 3 because neither the
///      command nor the install is broken; stderr names the allowance and why.
///   3  bad args / missing or invalid bundle / unwritable --out — always BEFORE any run
///   2  ≥1 REQUESTED server was not assessed. Two causes, both reported by name on stderr:
///      unreachable / auth failed at preflight, and excluded by the licence's seat count
///      ("EXCLUDED (not covered by licence): ..."). Both mean the same thing to a script — the
///      estate you asked for is not the estate in the artifacts — so they share one code.
///   1  every requested server was assessed, but ≥1 check errored, OR ≥1 requested output
///      artifact (--format json/csv/pdf) failed to write. A "WARNING: [format] ... failed" line
///      names which one on stderr; exit 1 is the machine-readable signal that DOES include
///      artifact failures — a script that only checks the exit code still learns a requested file
///      is missing, it does not need to parse stderr. #68.
///   0  clean run
/// </summary>
public static class CliAuditHost
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
    private const int AttachParentProcess = -1;

    /// <summary>Synchronous entry point called from Program.Main (which is itself
    /// [STAThread] and must not go async) — runs the CLI to completion and returns the
    /// process exit code.</summary>
    public static int Run(string[] args)
    {
        AttachToParentConsole();

        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: --audit failed unexpectedly: {ex.Message}");
            return 3; // conservative: an unhandled crash never reads as a clean 0/1/2 outcome
        }
    }

    /// <summary>
    /// SQLTriage.exe is a WinExe (GUI subsystem) — it has no console streams wired at startup
    /// even when launched from cmd/PowerShell. AttachConsole binds the OS console, but
    /// Console.Out/Error must be re-opened against the now-attached handles or output is
    /// silently dropped.
    ///
    /// Internal (was private) so Program.Main can attach before printing its own usage/error for
    /// an unrecognised command line — without it that message would be written to a console that
    /// does not exist and the operator would see a silent non-zero exit.
    /// </summary>
    internal static void AttachToParentConsole()
    {
        try
        {
            if (AttachConsole(AttachParentProcess))
            {
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(stdout);
                var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetError(stderr);
            }
            // No parent console (double-clicked, or detached) — AttachConsole returns false;
            // Console.Out stays a harmless no-op sink either way.
        }
        catch { /* best-effort — never let console plumbing break the audit */ }
    }

    /// <summary>
    /// Exit code for a run refused by the signed corpus-demo allocation. DISTINCT from 3 (bad
    /// args / bad bundle) and from 2 (a server was not assessed): nothing is wrong with the
    /// command or the estate — the licence does not cover this much corpus assessment right now,
    /// and a scheduled job should back off rather than retry or alert on a broken install.
    /// </summary>
    internal const int ExitDemoAllocationRefused = 4;

    /// <summary>
    /// The CLI lane's half of the corpus-demo gate: ask the shared seam about the WHOLE requested
    /// server list, and translate a refusal into stderr + <see cref="ExitDemoAllocationRefused"/>.
    /// Internal so the lane-agreement tests exercise this exact code rather than a re-implementation
    /// of it — the rule itself lives in <see cref="IDemoRunLedger.TryAdmitRun"/> and is not restated
    /// here.
    /// </summary>
    internal static int EvaluateDemoAdmission(
        IDemoRunLedger ledger, IReadOnlyList<string> servers, TextWriter stderr)
    {
        var admission = ledger.TryAdmitRun(servers);
        if (admission.Allowed) return 0;

        // The seam's words, verbatim — a lane that paraphrases the refusal is a lane that can tell
        // a paid client a different story about the same licence.
        stderr.WriteLine($"ERROR: corpus allocation: {admission.BlockReason}");
        return ExitDemoAllocationRefused;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var parsed = AuditCliArgs.Parse(args);
        if (parsed.HelpRequested)
        {
            Console.WriteLine(AuditCliArgs.Usage);
            return 0;
        }
        if (parsed.VersionRequested)
        {
            Console.WriteLine($"SQLTriage {CliVersion.Read()}");
            return 0;
        }
        if (!parsed.Success)
        {
            Console.Error.WriteLine($"ERROR: {parsed.Error}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(AuditCliArgs.Usage);
            return 3;
        }
        var cli = parsed.Args!;

        // Serilog, mirroring the app's own convention (App.xaml.cs / WindowsServiceHost).
        //
        // The FILE sink is the point of this configuration. SQLTriage.exe is a GUI-subsystem
        // binary, so a scheduled `--audit` cannot have its console redirected to a file at all —
        // the parent shell gets an empty log and no exit code. Without a file sink that run left
        // no diagnostics anywhere. The console sink is restricted per --quiet; the FILE sink is
        // NOT, so a quiet scheduled run still records what happened.
        var levelSwitch = new Serilog.Core.LoggingLevelSwitch(Serilog.Events.LogEventLevel.Information);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "SQLTriage.Audit")
            .WriteTo.File(
                path: Path.Combine(AppContext.BaseDirectory, "logs", "audit-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 50L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Console(
                restrictedToMinimumLevel: cli.Quiet
                    ? Serilog.Events.LogEventLevel.Warning
                    : Serilog.Events.LogEventLevel.Information,
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("config/appsettings.json", optional: false, reloadOnChange: false)
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog(dispose: true);
            });

            // The exact registration the WPF app and the Kestrel service host share.
            WindowsServiceHost.RegisterAllServices(services, configuration);

            using var provider = services.BuildServiceProvider();

            // ── The two persisted logging settings the WPF app applies at startup
            //    (App.xaml.cs:258-274) and this composition root previously did not. A
            //    scheduled --audit therefore ignored both: debug logging stayed off however the
            //    operator had set it, and server names went to the log un-anonymised on a build
            //    where the operator had asked for anonymisation. ──
            var userSettings = provider.GetService<UserSettingsService>();
            if (userSettings != null)
            {
                if (userSettings.GetDebugLogging())
                    levelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Debug;

                // Affects Serilog messages routed through LogAnon.S() inside the services. The
                // console lines this class prints keep the real names — they are the operator's
                // own requested output, not a log.
                LogAnon.Enabled = userSettings.GetAnonymiseServerNames();
            }

            // QuestPDF license — the ONLY app-startup init this composition root replicates from
            // App.xaml.cs.OnStartupAsync ("QuestPDF runs under the Community licence" comment,
            // App.xaml.cs:165-167). No other App-startup phase runs here: no WebView2 probe, no
            // background services, no Kestrel.
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

            // ── License + corpus bundle. cli.BundlePath (--bundle, or the resolved shipped-bundle
            //    default) is threaded straight into the Free-bundle loader so the file that gets
            //    decoded is exactly the file the console line below claims — a wrong/garbage
            //    --bundle no longer silently falls back to the shipped bundle. Semantic bundle
            //    validation (the file existed — AuditCliArgs.Parse already checked that — but does
            //    it actually decode and yield checks?) still counts as exit 3, still before any
            //    server is touched (via checkRepo.LoadError below — TryUnlockFree failing leaves
            //    the bundle unlocked=false, which LoadChecksAsync surfaces as LoadError). ──
            var licenseService = provider.GetRequiredService<LicenseService>();
            try { licenseService.Initialize(cli.BundlePath); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: license/bundle initialisation failed: {ex.Message}");
                return 3;
            }

            var checkRepo = provider.GetRequiredService<CheckRepositoryService>();
            await checkRepo.LoadChecksAsync().ConfigureAwait(false);
            if (checkRepo.LoadError != null || checkRepo.GetEnabledChecks().Count == 0)
            {
                Console.Error.WriteLine(
                    $"ERROR: corpus bundle invalid or empty: {checkRepo.LoadError ?? "zero enabled checks"}");
                return 3;
            }

            // ── Corpus-DEMO allocation gate. The desktop lane has metered corpus runs against the
            //    signed per-bundle allocation since the gate shipped; --audit consulted nothing, so
            //    the headline commercial limit was one documented flag away from unlimited. Both
            //    lanes now ask the SAME seam (IDemoRunLedger.TryAdmitRun) the same question about
            //    the same whole request. Placed here deliberately: after the bundle is loaded (the
            //    allocation is read live off it) and BEFORE any server is touched, so a refused run
            //    connects to nothing and writes nothing. ──
            var demoLedger = provider.GetRequiredService<IDemoRunLedger>();
            var demoExit = EvaluateDemoAdmission(demoLedger, cli.Servers, Console.Error);
            if (demoExit != 0) return demoExit;

            // Mirrors CheckExecutionService.ResolveMaxConcurrentPerInstance's precedence exactly
            // (per-run override → persisted setting → shipped default) so the figure logged is the
            // figure that will be used. cli.Concurrency is still passed through as null when not
            // supplied — this resolution only decides what to REPORT, it does not pin the value.
            var resolvedConcurrency = cli.Concurrency
                                      ?? userSettings?.GetAuditMaxConcurrentPerInstance()
                                      ?? UserSettingsService.AuditConcurrencyDefault;
            var concurrencySource = cli.Concurrency.HasValue ? "--concurrency"
                                  : userSettings != null ? "Settings > Performance"
                                  : "shipped default";

            if (!cli.Quiet)
                Console.WriteLine(
                    $"SQLTriage --audit: {checkRepo.GetEnabledChecks().Count} checks loaded (bundle: {cli.BundlePath}), " +
                    $"{cli.Servers.Count} server(s), format={string.Join(",", cli.Formats)}, parallel={cli.Parallel}, " +
                    $"concurrency={resolvedConcurrency} (from {concurrencySource})");

            Log.Information(
                "[--audit] {Checks} check(s), {Servers} server(s), parallel={Parallel}, " +
                "concurrency={Concurrency} (source: {ConcurrencySource})",
                checkRepo.GetEnabledChecks().Count, cli.Servers.Count, cli.Parallel,
                resolvedConcurrency, concurrencySource);

            var checkExec = provider.GetRequiredService<CheckExecutionService>();
            var resultStore = provider.GetRequiredService<QuickCheckResultStore>();

            // One shared ephemeral connection for the whole run — never touches
            // ServerConnectionManager / Config/server-connections.json (see
            // EphemeralConnectionFactory's class doc).
            var connection = EphemeralConnectionFactory.Build(cli);

            // ── Preflight every server BEFORE running any check (exit 2 territory) ──
            var reachable = new List<string>();
            var connectionFailed = false;
            foreach (var server in cli.Servers)
            {
                var (ok, error) = await EphemeralConnectionFactory
                    .PreflightAsync(connection, server, CancellationToken.None)
                    .ConfigureAwait(false);
                if (ok)
                {
                    reachable.Add(server);
                }
                else
                {
                    connectionFailed = true;
                    Console.Error.WriteLine($"ERROR: [{server}] connection/auth preflight FAILED: {error}");
                }
            }

            // ── Run checks, parallel across SERVERS only (--parallel N). --parallel is therefore
            //    a DIFFERENT axis from the queries-per-instance throttle: within one server,
            //    CheckExecutionService.ExecuteChecksAsync throttles concurrent queries to
            //    --concurrency when given, else the "Concurrent queries per instance" setting
            //    (Settings > Performance; 1-16). cli.Concurrency is passed through as the
            //    per-run override, so a null keeps that service's own precedence intact.
            //    ExecuteChecksAsync also serializes the #28 ERRORLOG-family checks per-instance
            //    (inherited from fd669ed's mitigation) — running several DIFFERENT servers
            //    concurrently never touches that per-server lock, since it is keyed by server
            //    name (verified by reading CheckExecutionService.cs:59-71: ErrorLogFamilyCheckIds
            //    + _errorLogFamilyLocks are both scoped per-instance, not per-run). ──
            var checkErrors = false;
            using (var throttle = new SemaphoreSlim(Math.Max(1, cli.Parallel)))
            {
                var tasks = reachable.Select(async server =>
                {
                    await throttle.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (!cli.Quiet) Console.WriteLine($"[{server}] running...");
                        var summary = await checkExec
                            .ExecuteChecksAsync(connection, server, CancellationToken.None, cli.Concurrency)
                            .ConfigureAwait(false);
                        if (summary.Errors > 0) checkErrors = true;
                        if (!cli.Quiet)
                            // All five buckets, and they foot: CheckExecutionService's tally is an
                            // if/else-if chain, so every check lands in exactly one of
                            // Passed/Failed/Accepted/Informational/Errors and the five sum to
                            // TotalChecks. Printing only passed+failed+errors left the operator to
                            // work out where the missing ~170 of "575 checks loaded" went — and
                            // invited the reading that they had passed.
                            Console.WriteLine(
                                $"[{server}] done: {summary.Passed} passed, {summary.Failed} failed, " +
                                $"{summary.Accepted} accepted, {summary.Informational} skipped/info, " +
                                $"{summary.Errors} errors — {summary.TotalChecks} checks " +
                                $"({summary.Duration.TotalSeconds:F1}s)");
                    }
                    catch (Exception ex)
                    {
                        // Preflighted OK but blew up mid-run (rare — e.g. dropped mid-audit):
                        // counts as a check error, not a connection failure — it DID connect.
                        checkErrors = true;
                        Console.Error.WriteLine($"ERROR: [{server}] audit run failed: {ex.Message}");
                    }
                    finally
                    {
                        throttle.Release();
                    }
                });
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            // ── Seat gate. CheckExecutionService.GetResults is seat-filtered (it returns an
            //    EMPTY list for an instance the licence does not cover) and its own doc says
            //    callers listing servers must surface GetExcludedServers. This caller did not, so
            //    an unseated instance produced an empty-but-successful artifact at exit 0. The
            //    filter is scoped to the servers THIS run asked for — GetServerSeatFilter spans
            //    every server with results on disk, including other runs'. ──
            var seatFilter = checkExec.GetServerSeatFilter();
            var excluded = cli.Servers
                .Where(s => seatFilter.Excluded.Contains(s, StringComparer.OrdinalIgnoreCase))
                .ToList();
            foreach (var server in excluded)
                Console.Error.WriteLine($"ERROR: EXCLUDED (not covered by licence): {server}");

            var assessed = reachable
                .Where(s => !excluded.Contains(s, StringComparer.OrdinalIgnoreCase))
                .ToList();

            // Everything the operator asked for that produced no rows, whatever the reason. This
            // is what every artifact must name — a CSV holding 2 servers for a 3-server request
            // otherwise reads as a complete estate.
            var notAssessed = cli.Servers
                .Where(s => !assessed.Contains(s, StringComparer.OrdinalIgnoreCase))
                .ToList();

            // ── Output (only for servers that were actually assessed) ──
            var runTimestamp = DateTime.UtcNow;
            var allResults = new List<CheckResult>();
            foreach (var server in assessed)
                allResults.AddRange(checkExec.GetResults(server, maxCount: int.MaxValue));

            // ── Claim the demo slots. Same rule as the desktop lane (QuickCheck.RunChecks): claim
            //    only instances that ACTUALLY produced results, so a cancelled/failed/unseated
            //    instance never burns the operator's allowance. RecordRun is idempotent within the
            //    window and a no-op when unlimited. Claimed BEFORE the export so a failed write
            //    cannot silently un-meter a run that really did assess the estate. ──
            foreach (var inst in allResults.Select(r => r.InstanceName)
                                           .Where(n => !string.IsNullOrWhiteSpace(n))
                                           .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                demoLedger.RecordRun(inst!);
            }

            var exportFailed = WriteOutputs(cli, resultStore, assessed, notAssessed, allResults, runTimestamp);

            if (!cli.Quiet)
                Console.WriteLine(
                    $"SQLTriage --audit: complete. {assessed.Count}/{cli.Servers.Count} server(s) assessed, " +
                    $"{allResults.Count} result(s) written to '{cli.OutDir}'." +
                    (notAssessed.Count > 0 ? $" NOT assessed: {string.Join(", ", notAssessed)}." : ""));

            if (connectionFailed || excluded.Count > 0) return 2;
            if (checkErrors || exportFailed) return 1;
            return 0;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Routes each requested --format to its writer. json reuses QuickCheckResultStore's own
    /// per-server file — WriteRun already fired as a side effect of ExecuteChecksAsync — by
    /// copying THAT EXACT file (the path WriteRun recorded) into --out; csv and pdf are built
    /// directly from the run's combined CheckResult set.
    ///
    /// <paramref name="serversAssessed"/> is already seat-filtered by the caller, so json now
    /// covers the same servers as csv/pdf. Previously json globbed the store directory
    /// independently and exported files csv/pdf had excluded.
    /// </summary>
    /// <returns>True if any requested artifact failed to write — folds into the process exit code
    /// (see the class doc's exit-code contract). #68 LEG 1 tail (b): previously a WARNING-only
    /// stderr line that never affected the exit code, i.e. a requested artifact could silently not
    /// exist and the run would still exit 0.</returns>
    private static bool WriteOutputs(
        AuditCliArgs cli, QuickCheckResultStore resultStore,
        IReadOnlyList<string> serversAssessed, IReadOnlyList<string> serversNotAssessed,
        List<CheckResult> allResults, DateTime runTimestampUtc)
    {
        var anyExportFailed = false;

        if (cli.Formats.Contains("json"))
        {
            foreach (var server in serversAssessed)
            {
                try
                {
                    // The path WriteRun actually wrote for THIS run, falling back to the store's
                    // own anchored latest-run lookup. Both replace the old
                    // "<safe>-*.json, order by name, take first" glob, which also matched a
                    // DIFFERENT server whose safe name merely started with this one's
                    // ("SQL01-*.json" matches "SQL01-DR-<stamp>.json") — so a two-server estate
                    // could export SQL01-DR's results labelled as SQL01's.
                    var source = resultStore.GetLastWrittenPath(server)
                                 ?? resultStore.GetLatestRunFile(server);
                    if (source is null)
                    {
                        // Was a silent `continue`: --format json requested, no file produced, and
                        // the run still exited 0 claiming success.
                        anyExportFailed = true;
                        Console.Error.WriteLine(
                            $"WARNING: [json] no results file found for {server} — nothing exported.");
                        continue;
                    }

                    var dest = Path.Combine(cli.OutDir, Path.GetFileName(source));
                    File.Copy(source, dest, overwrite: true);
                    if (!cli.Quiet) Console.WriteLine($"[json] wrote {dest}");
                }
                catch (Exception ex)
                {
                    anyExportFailed = true;
                    Console.Error.WriteLine($"WARNING: [json] failed to export for {server}: {ex.Message}");
                }
            }
        }

        if (cli.Formats.Contains("csv"))
        {
            try
            {
                var path = CsvResultWriter.Write(allResults, cli.OutDir, runTimestampUtc, serversNotAssessed);
                if (!cli.Quiet) Console.WriteLine($"[csv] wrote {path}");
            }
            catch (Exception ex)
            {
                anyExportFailed = true;
                Console.Error.WriteLine($"WARNING: [csv] export failed: {ex.Message}");
            }
        }

        if (cli.Formats.Contains("pdf"))
        {
#if SQLT_NO_REPORT_FINDINGS_PDF
            // The findings PDF report is gated out of this edition (Adrian's ruling 2026-07-21):
            // the builder and this writer are compile-pruned. --format pdf fails closed, loudly.
            anyExportFailed = true;
            Console.Error.WriteLine("WARNING: [pdf] the findings PDF report is not included in this edition — nothing exported.");
#else
            try
            {
                var path = WriteFindingsPdf(
                    allResults, serversAssessed, serversNotAssessed, cli.OutDir, runTimestampUtc);
                if (!cli.Quiet) Console.WriteLine($"[pdf] wrote {path}");
            }
            catch (Exception ex)
            {
                anyExportFailed = true;
                Console.Error.WriteLine($"WARNING: [pdf] export failed: {ex.Message}");
            }
#endif
        }

        // Compliance PDF (AssessmentPdf.BuildComplianceReport) is DEFERRED — its ComplianceScorecard
        // is computed by ComplianceScoreService against a selected framework + ScopedResults, a
        // richer shape than a bare CheckResult/summary, and wiring it cleanly is out of scope for
        // this pass. --format pdf produces the findings report only.

        return anyExportFailed;
    }

    /// <summary>
    /// Maps a run's CheckResults onto the findings-report DTO.
    ///
    /// Classification goes through <see cref="CliResultState"/> — i.e. the shared
    /// <see cref="CheckClassification"/> predicates every other surface uses. The local
    /// IsSkipped this replaced tested for a "SKIP" prefix on <see cref="CheckResult.Message"/>,
    /// which the corpus does not use to signal a skip (it sets Verdict=="SKIP"), so it matched
    /// nothing: the cascade emitted only Pass and Fail, and SKIP/INFO results — which
    /// CheckExecutionService marks Passed=true — were counted and rendered as passes. Measured
    /// 2026-07-19 on a real run: the console reported 307 passed while the PDF that same run
    /// printed reported ~475 PASSED / 83%, and every one of the 27 Verdict=="SKIP" results
    /// (2 Critical, 11 High) rendered as a green Pass.
    /// </summary>
#if !SQLT_NO_REPORT_FINDINGS_PDF
    private static string WriteFindingsPdf(
        List<CheckResult> results, IReadOnlyList<string> servers, IReadOnlyList<string> serversNotAssessed,
        string outDir, DateTime runTimestampUtc)
    {
        var byState = results.ToLookup(CliResultState.Of);
        var passedCount   = byState[FindingState.Pass].Count();
        var failedCount   = byState[FindingState.Fail].Count();
        var acceptedCount = byState[FindingState.Accepted].Count();
        var errorCount    = byState[FindingState.Error].Count();
        var skippedCount  = byState[FindingState.Skipped].Count();
        var infoCount     = byState[FindingState.Info].Count();
        var warnCount     = byState[FindingState.Warn].Count();

        var runId = Guid.NewGuid().ToString("N")[..8];
        var scope = servers.Count == 1 ? servers[0] : $"{servers.Count} servers";

        var subtitle = servers.Count == 1 ? servers[0] : $"{servers.Count} servers · {string.Join(", ", servers.Take(6))}";
        if (serversNotAssessed.Count > 0)
            subtitle += $" · Servers not assessed: {string.Join(", ", serversNotAssessed)}";

        var report = new FindingsReport
        {
            Meta = new AssessmentMeta
            {
                Title = "Audit Assessment",
                Company = "",
                Subtitle = subtitle,
                Engine = "Corpus audit checks",
                GeneratedUtc = runTimestampUtc.ToString("yyyy-MM-ddTHH:mmZ"),
                TimezoneId = "UTC",
                RunId = runId,
                ColorBlind = false,
                Watermark = false,
                FooterMeta = $"SQLTriage — Audit Assessment — {scope} — {runTimestampUtc:yyyy-MM-ddTHH:mmZ} (UTC) — Run {runId} — CLI",
            },
            // SKIPPED, INFO and PARTIAL are always shown, including at zero: their absence is
            // exactly what let a reader take PASSED as "everything else". PARTIAL at zero is a
            // positive assertion — "every check could see what it needed to". Together with
            // ACCEPTED (added below when non-zero) the six result chips partition every result,
            // since CliResultState.Of returns exactly one state per result and these cover all seven.
            Stats =
            {
                new() { Label = "PASSED", Value = passedCount.ToString(), Color = AssessmentPdf.Pass(false) },
                new() { Label = "FINDINGS", Value = failedCount.ToString(), Color = AssessmentPdf.Fail(false) },
                new() { Label = "PARTIAL", Value = warnCount.ToString(), Color = AssessmentPdf.Warn(false) },
                new() { Label = "SKIPPED", Value = skippedCount.ToString(), Color = AssessmentPdf.Muted },
                new() { Label = "INFO", Value = infoCount.ToString(), Color = AssessmentPdf.Info },
                new() { Label = "ERRORS", Value = errorCount.ToString(), Color = AssessmentPdf.Fail(false) },
                new() { Label = "SERVERS", Value = servers.Count.ToString(), Color = "#2563eb" },
            },
        };
        if (acceptedCount > 0)
            report.Stats.Insert(2, new StatChip { Label = "ACCEPTED", Value = acceptedCount.ToString(), Color = AssessmentPdf.AcceptedCol });
        if (serversNotAssessed.Count > 0)
        {
            // "NOT ASSESSED" rather than the narrower "UNREACHABLE": this set also carries
            // servers excluded by the licence seat count, which are reachable.
            report.Stats.Add(new StatChip
            {
                Label = "NOT ASSESSED",
                Value = serversNotAssessed.Count.ToString(),
                Color = AssessmentPdf.Fail(false),
            });
        }

        foreach (var r in results)
        {
            var state = CliResultState.Of(r);
            var detail = !string.IsNullOrEmpty(r.ErrorMessage) ? r.ErrorMessage
                       : !string.IsNullOrEmpty(r.Message) ? r.Message
                       : r.Description ?? "";
            report.Findings.Add(new FindingRow
            {
                State = state, Name = r.CheckName, Category = r.Category, Severity = r.Severity,
                Server = r.InstanceName, Detail = detail,
            });
        }

        var bytes = AssessmentPdf.BuildFindingsReport(report);
        Directory.CreateDirectory(outDir);
        var label = (servers.Count == 1 ? servers[0] : $"{servers.Count}servers").Replace("\\", "_").Replace("/", "_");
        var path = Path.Combine(outDir, $"AuditAssessment_{label}_{runTimestampUtc:yyyyMMdd_HHmmssZ}_{runId}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }
#endif
}

/// <summary>
/// Reads the shipped product version for <c>--version</c>. Same source and same idiom as
/// AzureBlobExportService.GetAppVersion — Config/version.json beside the exe — so the CLI cannot
/// report a different version from the rest of the product. "unknown" when the file is absent or
/// unreadable; the version is never guessed from the assembly.
/// </summary>
internal static class CliVersion
{
    public static string Read()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Config", "version.json");
            if (File.Exists(path))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.GetProperty("version").GetString() ?? "unknown";
            }
        }
        catch { /* a missing or malformed version.json must never fail --version */ }
        return "unknown";
    }
}
