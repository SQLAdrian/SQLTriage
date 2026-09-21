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
/// <see cref="CheckExecutionService"/> and <see cref="QuickCheckResultStore"/> for the ordinary
/// corpus run, plus <see cref="SqlAssessmentService"/>, <see cref="ReportBundleService"/> and
/// <see cref="VulnerabilityAssessmentStateService"/> for <c>--report audit-evidence</c> (see
/// <see cref="RunAuditEvidenceReportAsync"/>) — none of whose constructors start a timer or
/// background loop (those are only started by App.xaml.cs.OnStartupAsync /
/// WindowsServiceHost.InitializeBackgroundServices, which this composition root never calls).
///
/// Exit codes (precedence 4 > 3 > 2 > 1 > 0):
///   4  the run was refused by the signed corpus-demo allocation (the same allocation the desktop
///      lane enforces, via the same IDemoRunLedger.TryAdmitRun seam) — always BEFORE any server is
///      touched, so nothing connected and nothing was written. Distinct from 3 because neither the
///      command nor the install is broken; stderr names the allowance and why.
///   3  bad args / missing or invalid bundle / unwritable --out — always BEFORE any run.
///      <c>--report audit-evidence</c> also uses 3 when two or more requested names resolve to
///      the SAME server (proven via @@SERVERNAME during preflight) — see
///      <see cref="AuditEvidenceIdentityGrouping"/>: refused before any VA scan runs, same as
///      any other bad-args case, naming the colliding aliases on stderr.
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

    /// <summary>
    /// The final stdout line of a corpus <c>--audit</c> run. Internal + pure so the honesty rule it
    /// enforces is testable without a live run: when <paramref name="exportFailed"/> is true the run
    /// exited non-zero (see the exit-code contract) but the stdout line USED TO print an unqualified
    /// "complete. … result(s) written to …" — a human or a scheduled-task transcript that reads only
    /// stdout then saw success while a requested artifact had actually failed to write
    /// (platform-r1-04). The line now states the failure and stops claiming the results were written.
    /// </summary>
    internal static string ComposeAuditCompletionLine(
        bool exportFailed, int assessedCount, int totalServers, int resultCount,
        string outDir, IReadOnlyList<string> notAssessed)
    {
        var notAssessedNote = notAssessed.Count > 0
            ? $" NOT assessed: {string.Join(", ", notAssessed)}."
            : "";

        if (exportFailed)
            return
                $"SQLTriage --audit: completed WITH ERRORS. {assessedCount}/{totalServers} server(s) assessed. " +
                $"One or more requested output artifacts FAILED to write (see the WARNING line(s) above). " +
                $"The export in '{outDir}' is INCOMPLETE.{notAssessedNote}";

        return
            $"SQLTriage --audit: complete. {assessedCount}/{totalServers} server(s) assessed, " +
            $"{resultCount} result(s) written to '{outDir}'.{notAssessedNote}";
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
            // KEY-FILE PICKUP IS OFF FOR --audit, DELIBERATELY (board #19, design §4.1).
            // This is a short-lived process an operator may run interactively under their own
            // desktop account on the same box as the service. If it consumed the key file, the
            // phrase would be DPAPI-wrapped into the DESKTOP profile and deleted from disk, and the
            // LocalSystem service would still be on Free -- the exact account split the feature
            // exists to close, re-created by a race between two processes.
            try { licenseService.Initialize(cli.BundlePath, KeyFilePickupMode.Disabled); }
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

            // ── --report audit-evidence branches out HERE, before the corpus-demo gate below: that
            //    gate meters CORPUS check runs (CheckExecutionService) specifically, and this report
            //    runs a wholly different engine (SqlAssessmentService, the SQL Vulnerability
            //    Assessment API) that the corpus-demo allocation was never written to cover. It
            //    returns its own exit code and never falls through to the corpus flow below. ──
            if (!string.IsNullOrEmpty(cli.Report))
                return await RunAuditEvidenceReportAsync(cli, provider).ConfigureAwait(false);

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
                Console.WriteLine(ComposeAuditCompletionLine(
                    exportFailed, assessed.Count, cli.Servers.Count, allResults.Count, cli.OutDir, notAssessed));

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
    /// The <c>--report audit-evidence</c> path: runs the SQL Vulnerability Assessment engine
    /// (<see cref="SqlAssessmentService"/>) against every reachable <c>--servers</c> target — the
    /// SAME engine <c>Pages/VulnerabilityAssessment.razor.cs</c> drives from the UI — then hands the
    /// results to <see cref="ReportBundleService"/> exactly as the desktop/portal do
    /// (<c>Pages/ReportBundles.razor</c>'s <c>GeneratePdfAsync</c>), so the artifact this produces is
    /// what the app itself would generate for the same scan. <see cref="ReportBundleService"/> and
    /// every service it depends on (<see cref="VulnerabilityAssessmentStateService"/>,
    /// <see cref="AuditLogService"/>, <see cref="CheckRepositoryService"/>, …) are plain DI
    /// singletons registered by the SAME <c>WindowsServiceHost.RegisterAllServices</c> call
    /// <see cref="RunAsync"/> already made — none of them touch a Blazor circuit or JSInterop, so
    /// they resolve here exactly as they do in the WPF/Kestrel hosts. In particular
    /// <see cref="AuditLogService"/> resolves via its <c>IConfiguration</c> constructor (the only
    /// one DI can satisfy — the raw-<c>string</c> overload is not DI-visible), which points at
    /// <c>AppContext.BaseDirectory\audit-logs</c> — the SAME directory the installed app/service
    /// read and write, because all three run the SAME exe from the SAME install location. No
    /// separate audit-log wiring was needed for that reason.
    ///
    /// Deliberately does NOT call <see cref="SqlAssessmentService.RunMultiServerAssessmentAsync"/>:
    /// its <c>MergeResults</c> collapses identical (CheckId, Message) rows from DIFFERENT servers
    /// into one row and sets that row's <c>ThisServer</c> to null, and
    /// <c>ReportBundleService.GatherAuditEvidence</c> treats a null <c>ThisServer</c> as belonging to
    /// EVERY server — a genuinely cross-server finding would then print inside every server's
    /// "single-source" attestation. Each server is scanned and kept separate here, so every server's
    /// PDF hash covers only what that server actually produced.
    ///
    /// Never consults <see cref="IDemoRunLedger"/>: the corpus-demo allocation meters
    /// CheckExecutionService runs. The desktop VA page consults no ledger either (grepped —
    /// zero hits in Pages/VulnerabilityAssessment.razor.cs), so this mirrors the app's existing,
    /// already-unmetered VA path rather than inventing a new exemption.
    /// </summary>
    private static async Task<int> RunAuditEvidenceReportAsync(AuditCliArgs cli, IServiceProvider provider)
    {
        var assessmentSvc = provider.GetRequiredService<SqlAssessmentService>();
        var vaState = provider.GetRequiredService<VulnerabilityAssessmentStateService>();
        var reportSvc = provider.GetRequiredService<ReportBundleService>();

        // Same ephemeral, never-persisted connection + preflight shape as the corpus flow above
        // (EphemeralConnectionFactory's class doc) — duplicated rather than shared so this new path
        // can never change the corpus flow's own preflight behaviour.
        var connection = EphemeralConnectionFactory.Build(cli);
        var reachable = new List<string>();
        var resolvedIdentities = new List<(string RequestedName, string? ResolvedIdentity)>();
        var connectionFailed = false;
        foreach (var server in cli.Servers)
        {
            var (ok, error, identity) = await EphemeralConnectionFactory
                .PreflightWithIdentityAsync(connection, server, CancellationToken.None)
                .ConfigureAwait(false);
            if (ok)
            {
                reachable.Add(server);
                resolvedIdentities.Add((server, identity));
            }
            else
            {
                connectionFailed = true;
                Console.Error.WriteLine($"ERROR: [{server}] connection/auth preflight FAILED: {error}");
            }
        }

        if (reachable.Count == 0)
        {
            Console.Error.WriteLine("ERROR: --report audit-evidence: no server was reachable, nothing to attest.");
            return 2;
        }

        // A compliance attestation must not silently notarise one server's findings twice under
        // two names — see AuditEvidenceIdentityGrouping's class doc for why this refuses rather
        // than dedupes. Checked BEFORE any VA scan runs, so a refusal here (exit 3, same class as
        // any other bad-args case) never touches the artifact-building path at all.
        var duplicates = AuditEvidenceIdentityGrouping.FindDuplicates(resolvedIdentities);
        if (duplicates.Count > 0)
        {
            foreach (var dup in duplicates)
            {
                Console.Error.WriteLine(
                    $"ERROR: --report audit-evidence: requested names {string.Join(", ", dup.RequestedNames)} " +
                    $"all resolve to the SAME server ({dup.ResolvedIdentity}). Refusing to build a compliance " +
                    "artifact that would notarise its findings twice. Remove the duplicate alias and re-run.");
            }
            return 3;
        }

        if (!cli.Quiet)
            Console.WriteLine(
                $"SQLTriage --audit --report audit-evidence: running the Vulnerability Assessment engine " +
                $"against {reachable.Count} server(s)...");

        var allResults = new List<AssessmentResult>();
        var scanFailed = false;
        foreach (var server in reachable)
        {
            try
            {
                var connString = connection.GetConnectionString(server, "master");
                // autoSaveCsv: false — this path already writes its own artifact under --out;
                // the exe's own output\ folder is where the DESKTOP VA page's Import modal and
                // VA File Browser look (VulnerabilityAssessment.razor.cs:689-699, :1303-1320), and
                // a CLI-planted file there was an unrequested side effect (app-lane follow-up
                // item 8), not a CLI deliverable.
                var summary = await assessmentSvc
                    .RunServerAssessmentAsync(connString, CancellationToken.None, autoSaveCsv: false)
                    .ConfigureAwait(false);
                allResults.AddRange(summary.Results);
                if (!cli.Quiet)
                    Console.WriteLine($"[{server}] VA scan complete: {summary.FailedChecks} finding(s), {summary.Results.Count} row(s)");
                Log.Information("[--report audit-evidence] {Server}: {Total} row(s), {Failed} finding(s)",
                    server, summary.Results.Count, summary.FailedChecks);
            }
            catch (Exception ex)
            {
                scanFailed = true;
                Console.Error.WriteLine($"ERROR: [{server}] VA scan failed: {ex.Message}");
                Log.Warning(ex, "[--report audit-evidence] VA scan failed for {Server}", server);
            }
        }

        // Populate the SAME service the UI populates (Pages/VulnerabilityAssessment.razor.cs:452,476)
        // so ReportBundleService.GatherAuditEvidence sees a real, HasRun=true scan — GatherAuditEvidence
        // and its SHA-256 composition are UNCHANGED here; this only feeds it real input.
        vaState.Results = allResults;
        vaState.HasRun = true;
        vaState.AssessedServers = reachable;

        if (allResults.Count == 0)
        {
            Console.Error.WriteLine("ERROR: --report audit-evidence: the VA scan produced no results for any reachable server, nothing to attest.");
            return 1;
        }

        // platform-r2-05 (#10 top-ten). allResults.Count > 0 is NOT enough: the zip is built from
        // ScannedServers() (results with a non-null ThisServer), a DIFFERENT predicate. When the
        // identity query fails or times out, SqlAssessmentService stamps ThisServer=null on every
        // result — results exist, ScannedServers() is empty, and the zip loop writes ZERO entries.
        // The enforcing seam is the same one the Razor page uses (HasVaResultsFor(null) ==
        // ScannedServers().Count > 0, Adrian's 2026-07-16 ruling: never attest nothing) — the CLI
        // just never consulted it. An attestation of nothing is worse than none, so refuse here.
        var scanned = reportSvc.ScannedServers();
        if (scanned.Count == 0)
        {
            Console.Error.WriteLine(
                "ERROR: --report audit-evidence: the scan produced results but none carry a server " +
                "identity (the @@SERVERNAME identity query failed or timed out), so the attestation " +
                "would be empty. Nothing attested.");
            return 1;
        }

        byte[] bytes;
        // platform-r2-06. BuildAuditEvidenceEstateZipAsync drops any server whose per-server PDF
        // build throws (logged warning, then continue). Collect those names so a partial zip is
        // reported and exits non-zero, rather than "[audit-evidence] wrote <path>" + exit 0 over a
        // zip that is silently missing servers the scan actually covered.
        var skippedServers = new List<string>();
        try
        {
            // One zip regardless of server count — the same shape the app's own "All Servers" Audit
            // Evidence export produces (ReportBundles.razor: Zip(BuildAuditEvidenceEstateZipAsync(...))):
            // every server keeps its own independently-verifiable per-server hash, never blended into one.
            bytes = await reportSvc
                .BuildAuditEvidenceEstateZipAsync(watermarkAll: false, skippedServers)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: --report audit-evidence: PDF generation failed: {ex.Message}");
            return 1;
        }

        Directory.CreateDirectory(cli.OutDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssZ");
        var path = Path.Combine(cli.OutDir, $"AuditEvidence_{stamp}.zip");
        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);

        var incomplete = skippedServers.Count > 0;
        if (incomplete)
        {
            Console.Error.WriteLine(
                $"ERROR: --report audit-evidence: the attestation is INCOMPLETE. {skippedServers.Count} of " +
                $"{scanned.Count} scanned server(s) were dropped because their per-server report failed to " +
                $"build: {string.Join(", ", skippedServers)}. The zip at '{path}' does NOT attest them.");
            Log.Warning("[--report audit-evidence] incomplete: {Skipped} of {Scanned} server(s) dropped: {Servers}",
                skippedServers.Count, scanned.Count, string.Join(", ", skippedServers));
        }
        else if (!cli.Quiet)
        {
            Console.WriteLine($"[audit-evidence] wrote {path}");
        }
        Log.Information("[--report audit-evidence] wrote {Path} ({Attested}/{Scanned} server(s) attested)",
            path, scanned.Count - skippedServers.Count, scanned.Count);

        if (connectionFailed) return 2;
        if (scanFailed || incomplete) return 1;
        return 0;
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
