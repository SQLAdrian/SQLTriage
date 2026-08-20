/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Radzen;
using Serilog;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Scheduling;


#pragma warning disable CA1416 // Windows-only API — project targets net8.0-windows
namespace SQLTriage.Data.Services
{
    // BM:WindowsServiceHost.Class — runs the Blazor Server UI as a headless Windows Service
    /// <summary>
    /// Runs the Blazor Server UI as a headless Windows Service (no WPF).
    /// Shares the same service registration as the WPF app.
    /// </summary>
    public class WindowsServiceHost
    {
        public const string ServiceName = "SQLTriage";
        public const string ServiceDisplayName = "SQLTriage Server";
        public const string ServiceDescription = "SQLTriage — Blazor Server monitoring dashboard";

        /// <summary>
        /// Runtime-switchable Serilog minimum level for the headless lane — the counterpart of
        /// App.LogLevelSwitch. This host previously pinned Information, so Settings → Enable
        /// Debug Logging had no effect on a service/--server install and the only way to raise
        /// the level was a code change.
        /// </summary>
        private static readonly Serilog.Core.LoggingLevelSwitch ServiceLogLevelSwitch =
            new(Serilog.Events.LogEventLevel.Information);

        /// <summary>
        /// Entry point for --service/--server. Returns the PROCESS exit code: 0 only when the
        /// requested operation actually succeeded.
        ///
        /// This used to return void, so every path — a refused `sc create`, a Kestrel port clash,
        /// a fatal startup exception — exited 0. Two things read that code and both were misled:
        /// the Service Management page (`_actionSuccess = result.ExitCode == 0`, which painted the
        /// green success panel over the text "Failed to install service"), and the SCM itself,
        /// which treats a 0 exit as a clean run.
        /// </summary>
        public static int Run(string[] args)
        {
            // Handle install/uninstall commands. SQLTriage.exe is a WinExe (GUI subsystem), so it
            // has no console streams even when launched from an elevated prompt — without this the
            // install printed its entire result, success or failure, into a void. --audit has
            // always attached; this lane never did.
            if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
            {
                Cli.CliAuditHost.AttachToParentConsole();
                return InstallService(args);
            }

            if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
            {
                Cli.CliAuditHost.AttachToParentConsole();
                return UninstallService();
            }

            // Run as Windows Service or console
            return RunServer(args);
        }

        private static int RunServer(string[] args)
        {
            // QuestPDF needs its licence set once per PROCESS, before any document is generated.
            // App.xaml.cs:169 does it for the WPF desktop lane and Cli/CliAuditHost.cs:224 for the
            // CLI — this lane had neither, so every PDF export from --server/--service threw the
            // vendor's licence exception. It was not caught anywhere: the raw exception text
            // rendered into the buyer-facing report card and NO file was written, with no toast and
            // no failed state on the button.
            //
            // Found 2026-07-20 by the persona board, which captures through this lane. It cost
            // ctaFunnel points from all three budget-holder personas at once. Community licence is
            // correct here — the same value the other two lanes already pass.
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

            // Configure Serilog for service mode
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(ServiceLogLevelSwitch)
                .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "SQLTriage.Service")
                .Enrich.WithProperty("User", Environment.UserName)
                .Enrich.WithProperty("Machine", Environment.MachineName)
                .WriteTo.File(
                    path: Path.Combine(AppContext.BaseDirectory, "logs", "service-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information("SQLTriage Service starting...");

            // Hoisted out of the try so the finally can reach them: the engines are started inside
            // the try, and the ONE path that must still stop them is the path that throws.
            //
            // The teardown is held as CAPTURED ENGINE INSTANCES, not as a container reference. On the
            // failed-start path app.Run() has already disposed the provider by the time the bind
            // exception reaches the finally, so anything that re-resolved would get
            // ObjectDisposedException eleven times over and stop nothing.
            WebApplication? app = null;
            IReadOnlyList<BackgroundStop>? backgroundStops = null;
            int backgroundStopGuard = 0;

            // Hoisted for the same reason and released in the same finally. The certificate is
            // imported with MachineKeySet and WITHOUT PersistKeySet, which means the CNG container
            // under C:\ProgramData\Microsoft\Crypto is removed when this object is released — and
            // "released" is an act this host has to perform. It never did: measured on 2026-08-03,
            // 24 key containers accumulated in three hours of service restarts, one per start,
            // each one live key material for a listener that no longer exists. The SCM's restart
            // actions make that a loop, not an anecdote.
            //
            // It must NOT be disposed before then: Kestrel holds it for the life of the listener,
            // and SChannel builds server credentials from its key on every handshake. So the
            // lifetime is exactly the host's — created before the bind, released after app.Run()
            // returns, including on the failed-start path where nothing ever transitions to
            // "stopping" and no lifetime hook fires at all.
            X509Certificate2? verifiedCert = null;

            // Idempotent stop. Two callers race for it — ApplicationStopping on a graceful stop, and
            // the finally below on every path including a fatal startup — and whichever arrives
            // first does the work. Without the guard a graceful stop would run the whole teardown
            // twice and log two shutdowns for one.
            void StopBackgroundServicesOnce()
            {
                var stops = backgroundStops;
                // Nothing was ever started (a throw before InitializeBackgroundServices returned).
                // Return WITHOUT burning the guard: there is nothing to stop, and a later caller
                // that does have work must not find the guard already spent.
                if (stops == null || stops.Count == 0) return;
                if (Interlocked.Exchange(ref backgroundStopGuard, 1) != 0) return;
                StopBackgroundServices(stops);
            }

            try
            {
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("config/appsettings.json", optional: false, reloadOnChange: true)
                    // governance-weights.json removed from IConfiguration (Phase 2).
                    // Weights are now served by GovernanceWeightsProvider (lazy load / safe fallback).
                    // Phase 3 will serve them from the encrypted license bundle via IBundleAccessor.
                    .Build();

                // Read port from config or default to 5150
                var port = configuration.GetValue("ServicePort", 5150);
                port = FindAvailablePort(port);

                // Resolve wwwroot path from static web assets manifest
                var webRoot = ResolveWebRoot();

                var builder = WebApplication.CreateBuilder(args);
                builder.Environment.WebRootPath = webRoot;
                builder.Environment.ContentRootPath = AppContext.BaseDirectory;

                // Try to use static web assets (dev mode)
                try { builder.WebHost.UseStaticWebAssets(); } catch (Exception ex) { Log.Debug(ex, "[ServiceHost] UseStaticWebAssets not available"); }

                // RBAC config, read off disk before the container exists: Windows auth forces
                // the HTTPS listener to HTTP/1.1, and that is a listener option.
                var rbacConfig = RbacService.PeekConfig();

                // Configure Kestrel — HTTP always, HTTPS only once a real handshake has proven the
                // certificate can serve it.
                //
                // This used to generate a certificate, bind the listener, and log "Service HTTPS
                // configured on port {HttpsPort} with ephemeral certificate" on the strength of
                // having CONSTRUCTED an X509Certificate2. The flags were MachineKeySet |
                // EphemeralKeySet, which SChannel cannot build server credentials from, so the
                // installed service had logged that line at every startup since it was installed
                // while :5156 completed no handshake at all — a dead endpoint asserting it was
                // alive. See SelfSignedTlsCertificate for the measurement and the flag choice.
                var httpsPort = FindAvailablePort(port + 1);

                // Which interfaces to bind. Default is every interface, because "Server mode — share
                // via browser" is the point of this host and the security boundary is the admission
                // middleware, not the bind. This setting exists so a locked-down install can narrow
                // it further without a code change; it is defence in depth, not the control.
                var bindAddress = ResolveBindAddress(configuration, out var bindDescription, out var bindWarning);
                if (bindWarning != null) Log.Warning("{Warning}", bindWarning);

                builder.WebHost.ConfigureKestrel(kestrel =>
                {
                    if (bindAddress == null) kestrel.ListenAnyIP(port);
                    else kestrel.Listen(bindAddress, port);

                    verifiedCert = SelfSignedTlsCertificate.CreateVerified("SQLTriage Service", out var certDetail);
                    if (verifiedCert == null)
                    {
                        // No listener is bound. A port that refuses the connection is honest; a
                        // port that accepts it and then cannot handshake is what this replaces.
                        Log.Warning(
                            "[HTTPS] The generated certificate could not complete a TLS handshake ({Detail}). "
                            + "No HTTPS listener is bound — this service is HTTP only, on port {Port}.",
                            certDetail, port);
                        return;
                    }

                    void ConfigureHttps(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions listenOptions)
                    {
                        listenOptions.UseHttps(verifiedCert);
                        SqlTriageAuth.ApplyNegotiateProtocolConstraint(listenOptions, rbacConfig);
                    }

                    if (bindAddress == null) kestrel.ListenAnyIP(httpsPort, ConfigureHttps);
                    else kestrel.Listen(bindAddress, httpsPort, ConfigureHttps);

                    // States what was actually done and what was actually measured. Whether
                    // Kestrel then BOUND the port is a separate claim, made from
                    // ApplicationStarted below.
                    Log.Information(
                        "[HTTPS] Self-signed certificate verified ({Detail}); binding an HTTPS listener on port {HttpsPort}.",
                        certDetail, httpsPort);
                });

                // Windows Service support
                builder.Host.UseWindowsService(options =>
                {
                    options.ServiceName = ServiceName;
                });

                // Logging
                builder.Services.AddLogging(lb =>
                {
                    lb.ClearProviders();
                    lb.AddSerilog(dispose: false);
                });

                // Configuration
                builder.Services.AddSingleton<IConfiguration>(configuration);

                // Blazor Server
                builder.Services.AddRazorComponents()
                    .AddInteractiveServerComponents();
                builder.Services.AddRadzenComponents();
                builder.Services.AddScoped<Radzen.DialogService>();

                // Register all app services (same as App.xaml.cs)
                RegisterAllServices(builder.Services, configuration);

                // This host serves BROWSERS over a listener bound to every interface. Without
                // this override it inherited HostEnvironmentInfo.Desktop, ServerModeService
                // .IsRunning was false (nothing here ever starts it), and every circuit took the
                // "single-user desktop" branch and resolved to ADMIN — so any unauthenticated
                // client on the network rendered /settings, /query and /server-configuration as
                // an administrator on a host holding connections to client production servers.
                builder.Services.AddSingleton(HostEnvironmentInfo.BrowserHosted);

                // Authentication. This host previously had NO auth pipeline whatsoever — no
                // UseAuthentication, no /auth endpoints (both 404'd on the live service when
                // probed on 2026-08-01). Same shared stack as server mode, no second lane.
                builder.Services.AddSqlTriageAuth(rbacConfig);

                app = builder.Build();

                // THE FRONT DOOR: the interactive-application boundary, then static files, then
                // routing — in that order, decided once in InteractiveAppAdmission rather than
                // reproduced by hand in each of the two hosts.
                //
                // An unauthenticated caller from a non-loopback origin now gets the sign-in path
                // and nothing else: no document, no static asset, and crucially no /_blazor
                // circuit, so there is no interactive application to smuggle a control into and
                // the seventeenth evasion shape nobody has thought of is irrelevant too.
                // Everything the RBAC lane already built — the per-surface IsAuthorized gates, the
                // ShellGate boundary on the always-rendered shell, store-backed revocation — still
                // runs behind this, as defence in depth rather than as the thing holding the line.
                app.UseSqlTriageFrontDoor(app.Services.GetRequiredService<RbacService>());

                app.UseSqlTriageAuth(app.Services.GetRequiredService<RbacService>());
                app.UseAntiforgery();

                // Health endpoint. UNAUTHENTICATED on purpose (monitoring depends on it), so it
                // says only that this listener is alive. It used to return webRoot — the absolute
                // install path — and machineName, plus the HTTPS port and whether TLS was
                // configured, to any unauthenticated caller on the LAN. That is host
                // reconnaissance, not liveness; the caller already knows the port they reached.
                app.MapGet("/_server/health", () => Results.Ok(new { status = "ok" }));

                app.MapRazorComponents<Components.ServerApp>()
                    .AddInteractiveServerRenderMode();

                // License initialisation — mirrors App.xaml.cs. This lane never ran it, so the
                // bundle was never resolved and every consumer of IBundleAccessor saw the
                // default unlocked=false / Tier.Free accessor for the life of the service.
                // Non-fatal by design: the accessor stays in its degraded state on failure.
                try
                {
                    app.Services.GetRequiredService<Licensing.LicenseService>().Initialize();
                }
                catch (Exception licEx)
                {
                    Log.Error(licEx, "[STARTUP] LicenseService.Initialize threw unexpectedly — service will run unlicensed.");
                }

                // Stop symmetry, registered BEFORE anything is started. A hook registered after the
                // engines are running does not cover the window in between, and that window is not
                // theoretical: the engines start here, Kestrel binds at app.Run() below, and a bind
                // failure between the two throws straight past this line into the catch.
                //
                // ApplicationStopping covers an SCM stop and a host-triggered shutdown. It does NOT
                // cover a failed start — the host never reaches a "started" state, so it never
                // transitions to "stopping" and this hook never fires. (Nor does it cover Ctrl-C
                // here: SQLTriage.exe is a WinExe with no console attached and no CancelKeyPress
                // handler, so a graceful taskkill is simply ignored.) That is what the finally's
                // belt-and-braces call is for: with the SCM's configured restart actions, a bind
                // clash would otherwise loop — every attempt starting every engine again, and a due
                // scheduled assessment firing against production once per restart.
                app.Lifetime.ApplicationStopping.Register(StopBackgroundServicesOnce);

                // Apply the persisted diagnostic settings + emit the build identity line,
                // then start background services.
                InitializeHostDiagnostics(app.Services, configuration, ServiceLogLevelSwitch, "service");
                backgroundStops = InitializeBackgroundServices(app.Services);

                // "started" is claimed only once Kestrel has actually BOUND. This line used to be
                // logged before app.Run(), so a port clash produced "SQLTriage Service started on
                // port 5150" immediately followed by "Failed to bind to address ... address already
                // in use" — the log asserted a state the process never reached.
                //
                // The "(HTTPS: …)" half was the SAME defect one layer in, and survived the round
                // that fixed the certificate: it was conditioned on `verifiedCert != null`, which
                // is a statement about holding an object. CreateVerified proves the platform can
                // serve TLS with that key, on a socket it stands up itself; it does not prove
                // Kestrel bound THIS port, bound it with THIS certificate, or bound it at all.
                // IServerAddressesFeature is no better — measured, it reports the configured
                // endpoint whether or not anything is serving there. So the suffix is now
                // conditioned on a real client handshake against the bound port, made from
                // ApplicationStarted where the listener is live.
                //
                // Off the callback's thread: ApplicationStarted runs on the host's startup path
                // and the probe is a network round trip with a 15s ceiling. The started line is
                // logged from the probe's continuation, so it is still logged exactly once and
                // still says only what was measured.
                app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(() =>
                {
                    var httpsServing = false;
                    if (verifiedCert != null)
                    {
                        httpsServing = SelfSignedTlsCertificate.EndpointServesTls(httpsPort, verifiedCert, out var probe);
                        if (httpsServing)
                            Log.Information("[HTTPS] Bound endpoint verified: {Probe}", probe);
                        else
                            Log.Warning(
                                "[HTTPS] A certificate was verified and an HTTPS listener was configured on port "
                                + "{HttpsPort}, but that endpoint did NOT serve TLS when probed ({Probe}). "
                                + "Treat this service as HTTP only on port {Port}.",
                                httpsPort, probe, port);
                    }

                    if (httpsServing)
                        Log.Information(StartedLineTemplate(true), port, httpsPort);
                    else
                        Log.Information(StartedLineTemplate(false), port);

                    // ⚠ This line says "configured for", NOT "listening on", and the distinction is
                    // load-bearing. bindDescription is the RESOLVED SETTING; it is not a
                    // measurement. The comment above already records that IServerAddressesFeature
                    // reports the configured endpoint whether or not anything serves there, and no
                    // probe from another interface is attempted here — so claiming a bind would be
                    // the same over-claim as the "(HTTPS: …)" suffix this file already fixed twice.
                    // What it is good for: an operator who set ServiceBindAddress can see how it
                    // was PARSED, which is the part that silently went wrong before this existed.
                    Log.Information(
                        "[Bind] Kestrel was configured for {BindDescription}. Not probed from another interface — "
                        + "this states the resolved setting, not a verified listener.",
                        bindDescription);
                }));

                Log.Information("SQLTriage Service binding to port {Port}...", port);
                app.Run();
                return 0;
            }
            catch (Exception ex)
            {
                // A service that could not start MUST exit non-zero. Returning 0 here told the SCM
                // the run was clean, so a bind failure looked like a normal shutdown instead of a
                // fault the configured restart actions should act on.
                Log.Fatal(ex, "SQLTriage Service failed to start");
                return 1;
            }
            finally
            {
                // Belt and braces to the ApplicationStopping registration above. On a FAILED start
                // — the port clash being the proven case — the host never reaches "started", so it
                // never transitions to "stopping" and the hook never runs. This call is a no-op when
                // the hook already ran (see the guard), and it stops the CAPTURED instances, so the
                // provider app.Run() has already disposed by this point is never consulted.
                //
                // Observed order on that path (gate, live): the host disposes its container FIRST,
                // which runs each engine's own Dispose and genuinely stops it, and only THEN does
                // this finally run. So on a bind clash this is usually a safety net finding the work
                // already done — which is why StopBackgroundServices classifies an already-disposed
                // engine as stopped rather than failed. It stays because "usually" is not "always":
                // nothing guarantees that disposal order for every future failure mode, and an
                // engine left holding a timer through an SCM restart loop is the outcome that
                // matters.
                StopBackgroundServicesOnce();

                // Releases the key container. See the declaration above for why this is the
                // host's job and why it happens here rather than in a lifetime hook.
                verifiedCert?.Dispose();
                verifiedCert = null;

                Log.Information("SQLTriage Service stopped");
                Log.CloseAndFlush();
            }
        }

        /// <summary>
        /// The startup line's message template. Split out so the CONDITION on the "(HTTPS: …)"
        /// suffix is a thing a test can hold: <c>WindowsServiceHttpsClaimTests</c> stands a
        /// listener that accepts a socket and cannot handshake, holds a perfectly good certificate
        /// object, and asserts the composed line carries no HTTPS claim.
        ///
        /// <para>Internal for test visibility (InternalsVisibleTo SQLTriage.Tests).</para>
        /// </summary>
        /// <param name="httpsServing">
        /// Whether a real handshake against the BOUND port succeeded. Not "was a certificate
        /// created", not "was a listener configured", not what <c>IServerAddressesFeature</c>
        /// reports — measured, that names the unserved endpoint too.
        /// </param>
        internal static string StartedLineTemplate(bool httpsServing) =>
            "SQLTriage Service started on port {Port}" + (httpsServing ? " (HTTPS: {HttpsPort})" : "");

        /// <summary>
        /// Registers all application services — mirrors App.xaml.cs but for headless mode.
        /// </summary>
        internal static void RegisterAllServices(IServiceCollection services, IConfiguration configuration)
        {
            var connStr = configuration.GetConnectionString("SqlServer") ?? "Server=.;Database=SQLWATCH;Integrated Security=true;";
            var trustServerCert = configuration.GetValue<bool>("TrustServerCertificate", false);

            services.AddSingleton<ServerConnectionManager>();
            services.AddSingleton<GlobalInstanceSelector>();
            services.AddSingleton<IDbConnectionFactory>(sp =>
            {
                var serverManager = sp.GetRequiredService<ServerConnectionManager>();
                var instanceSelector = sp.GetRequiredService<GlobalInstanceSelector>();
                return new SqlServerConnectionFactory(serverManager, instanceSelector, connStr, trustServerCert);
            });
            services.AddSingleton<SqlServerConnectionFactory>(sp =>
            {
                var serverManager = sp.GetRequiredService<ServerConnectionManager>();
                var instanceSelector = sp.GetRequiredService<GlobalInstanceSelector>();
                return new SqlServerConnectionFactory(serverManager, instanceSelector, connStr, trustServerCert);
            });

            services.AddSingleton<ResilienceService>();
            services.AddSingleton<DashboardConfigService>();
            services.AddSingleton<QueryExecutor>();
            services.AddScoped<DashboardDataService>();
            services.AddSingleton<AutoRefreshService>();
            services.AddSingleton<CheckRepositoryService>();
            services.AddSingleton<BPScriptService>();
            services.AddSingleton<DiagnosticScriptRunner>();
            services.AddSingleton<FullAuditStateService>();
            services.AddSingleton<AuditLogService>();
            services.AddSingleton<AgentJobControlService>();
            services.AddSingleton<NotificationChannelService>();
            services.AddSingleton<AlertingService>();
            services.AddSingleton<HealthCheckService>();
            services.AddSingleton<CheckExecutionService>();
            services.AddSingleton<liveQueriesTableService>();
            services.AddSingleton<SessionManager>();
            services.AddSingleton<UserSettingsService>();
            services.AddSingleton<SessionDataService>();
            services.AddSingleton<ToastService>();
            services.AddSingleton<LogCleanupService>();
            services.AddSingleton<MemoryMonitorService>();
            services.AddSingleton<ConfigurationValidator>();
            services.AddSingleton<AutoUpdateService>();
            services.AddSingleton<DatabaseAvailabilityService>();
            services.AddSingleton<PrintService>();
            services.AddSingleton<Data.Services.IPrintService>(sp => sp.GetRequiredService<Data.Services.PrintService>());
            services.AddSingleton<SqlAssessmentService>();

            services.AddSingleton<ReportPageConfigService>();
            services.AddSingleton<XEventService>();
            services.AddSingleton<InstallProvenanceService>();
            services.AddSingleton<AdminAuthService>();
            services.AddSingleton<QuickCheckStateService>();
            services.AddSingleton<VulnerabilityAssessmentStateService>();
            services.AddSingleton<ThemeService>();
            services.AddSingleton<ServerModeService>();
            services.AddSingleton<DataProtectionService>();
            services.AddSingleton<AzureBlobExportService>();
            services.AddSingleton<ProcessGuard>();
            services.AddSingleton<ProductionReadinessGate>();
            services.AddSingleton<LocalLogService>();
            services.AddSingleton<PowerShellService>();

            // DI parity: services registered in App.xaml.cs but missing here
            services.AddSharedServices(configuration); /* BM: shared services registered via AddSharedServices */
        }

        /// <summary>
        /// Applies the persisted diagnostic settings that live OUTSIDE the DI graph — Serilog's
        /// runtime level switch, <see cref="LogAnon"/>, the audit-diag sink housekeeping — and
        /// emits one identity line naming the build this process is actually running.
        ///
        /// Shared by App.xaml.cs and <see cref="RunServer"/> (same reason <see cref="ResolveWebRoot"/>
        /// is shared): the settings were previously applied only in the WPF startup path, so the
        /// headless lane ignored Anonymise-server-names and Enable-debug-logging entirely.
        /// Call once per process, after the container is built.
        /// </summary>
        internal static void InitializeHostDiagnostics(
            IServiceProvider services,
            IConfiguration configuration,
            Serilog.Core.LoggingLevelSwitch levelSwitch,
            string hostLane)
        {
            var userSettings = services.GetService<UserSettingsService>();
            if (userSettings != null)
            {
                levelSwitch.MinimumLevel = userSettings.GetDebugLogging()
                    ? Serilog.Events.LogEventLevel.Debug
                    : Serilog.Events.LogEventLevel.Information;

                userSettings.OnDebugLoggingChanged += enabled =>
                {
                    levelSwitch.MinimumLevel = enabled
                        ? Serilog.Events.LogEventLevel.Debug
                        : Serilog.Events.LogEventLevel.Information;
                    Log.Information("Debug logging {State}", enabled ? "enabled" : "disabled");
                };

                LogAnon.Enabled = userSettings.GetAnonymiseServerNames();
            }
            else
            {
                Log.Warning("[STARTUP] UserSettingsService unavailable — debug-logging and " +
                            "anonymise-server-names settings are not applied in this host.");
            }

            // The per-check diagnostic sink writes into the same logs/ folder as Serilog but has
            // no rolling policy of its own; sweep it to the Serilog file-sink retention count.
            AuditDiagnosticSink.SweepOldFiles(
                configuration.GetSection("Serilog:FileSink").GetValue("RetainedFileCount", 30));

            // One line per host naming what is actually running. Version+build come from
            // Config/version.json via AutoUpdateService (already the reader for that file);
            // tier comes from the resolved bundle, so this must run after license init.
            var version = services.GetService<AutoUpdateService>()?.GetCurrentVersion() ?? "unknown";
            var bundle = services.GetService<Licensing.IBundleAccessor>();
            Log.Information("[STARTUP] SQLTriage {Version} | lane={Lane} | tier={Tier} | licensed={Unlocked} | machine={Machine}",
                version,
                hostLane,
                bundle?.Tier.ToString() ?? "unknown",
                bundle?.IsUnlocked ?? false,
                Environment.MachineName);

            // Feature gates, WARMED (not registered) here. The registration itself lives with the DI
            // registration in AddSharedServices and runs on the gate's first read whether or not this
            // line exists — that is what stops the two hosts diverging again (GATE-02, 2026-08-07:
            // registration used to be a startup call only App.xaml.cs made, and the headless host
            // rendered a nav with no gated entries as a result).
            //
            // What this line buys is observability, in the one method BOTH hosts already call after
            // licence init: the count lands in the boot log per lane, and a registration fault is
            // logged at startup instead of during somebody's first render. EnsureRegistered never
            // throws, so warming here cannot fail a service start.
            //
            // The count is what was REGISTERED. It says nothing about how many are licensed — that is
            // per-feature, evaluated per read against the bundle, and the tier/licensed fields above
            // are the only licence claim on this line.
            var featureGate = services.GetService<IFeatureGate>();
            if (featureGate != null)
            {
                featureGate.EnsureRegistered();
                Log.Information(
                    "[STARTUP] Feature gates: {Count} runtime-gated module(s) registered | lane={Lane}",
                    featureGate.Features.Count, hostLane);
            }
            else
            {
                // Deliberately does NOT say "gated surfaces stay hidden": that is what an EMPTY gate
                // does. An ABSENT one is a broken container — NavMenu and every page that injects
                // IFeatureGate throw "no registered service" at render instead.
                Log.Warning("[STARTUP] IFeatureGate could not be resolved in the {Lane} host. Feature "
                          + "gating cannot be evaluated at all here, and the shell nav plus every page "
                          + "that injects it will fail to render.", hostLane);
            }
        }

        /// <summary>
        /// Starts the background engines this host owns. Internal (not private) so the host-parity
        /// test can read it: the defect this block exists to prevent is DRIFT — App.xaml.cs starting
        /// an engine that this host silently never starts.
        ///
        /// <para>An INSTALLED SERVICE is the 24/7 host (Adrian's ruling 2026-07-31). Until then this
        /// method registered everything and started almost nothing: <see cref="ScheduledTaskEngine"/>,
        /// <see cref="ConnectionHealthService"/> and <see cref="AlertEvaluationService"/> were all
        /// resolvable but never resolved, so a service install ran no scheduled assessment, pinged no
        /// server, and evaluated no alert. Nothing logged a complaint, because nothing ran.</para>
        ///
        /// <para>RETURNS the teardown. Each engine is resolved ONCE, into a local, and the way to stop
        /// THAT INSTANCE is captured alongside it. The container is never consulted again — see
        /// <see cref="StopBackgroundServices"/> for the proven reason why.</para>
        /// </summary>
        internal static IReadOnlyList<BackgroundStop> InitializeBackgroundServices(IServiceProvider services)
        {
            var stops = new List<BackgroundStop>();

            // Records how to stop an instance we are holding. The closure captures the INSTANCE, not
            // the provider — that is the whole point (see StopBackgroundServices). A service that did
            // not resolve simply contributes no stop.
            void Capture<T>(T? instance, Action<T> stop) where T : class
            {
                if (instance != null) stops.Add(new BackgroundStop(typeof(T).Name, () => stop(instance)));
            }

            services.GetService<IQueryOrchestrator>()?.Start();

            var logCleanup = services.GetService<LogCleanupService>();
            logCleanup?.Start();
            Capture(logCleanup, s => s.Dispose());

            var memoryMonitor = services.GetService<MemoryMonitorService>();
            Capture(memoryMonitor, s => s.Dispose());

            var cacheEviction = services.GetService<CacheEvictionService>();
            cacheEviction?.Start();
            Capture(cacheEviction, s => s.Stop());

            var liveQueriesMaintenance = services.GetService<liveQueriesMaintenanceService>();
            liveQueriesMaintenance?.Start();
            Capture(liveQueriesMaintenance, s => s.Stop());

            // #57: live per-server health metrics for the DBA dashboard (CPU/memory/blocked/
            // deadlocks/top-wait) — headless/service host parity with App.xaml.cs.
            var healthMetrics = services.GetService<HealthMetricsCollectorService>();
            healthMetrics?.Start();
            Capture(healthMetrics, s => s.Stop());

            // Alert baselines seed aggressively for the first 5 minutes, then recompute hourly.
            // Started BEFORE the evaluation engine below for the same reason App.xaml.cs does:
            // evaluation against an unseeded baseline is evaluation against nothing.
            _ = Task.Run(async () =>
            {
                try
                {
                    var baseline = services.GetService<AlertBaselineService>();
                    if (baseline != null) await baseline.StartAsync();
                }
                catch (Exception ex) { Log.Error(ex, "[STARTUP] AlertBaselineService failed"); }
            });

            var alertEvaluation = services.GetService<AlertEvaluationService>();
            alertEvaluation?.Start();
            Capture(alertEvaluation, s => s.Stop());

            var blockingForensics = services.GetService<BlockingForensicsService>();
            blockingForensics?.Start();
            Capture(blockingForensics, s => s.Stop());

            // ── The engines the service lane was missing ──────────────────────────────────────
            //
            // ScheduledTaskEngine is the load-bearing one twice over. It runs the scheduled
            // assessments AND it is the single startup resolve that the gated runtime registration
            // decorates (AddPortalRuntimeServices' factory attaches its own subscribers on the way
            // out of the container). Nothing else resolves the engine in this host, so leaving it
            // unresolved left every one of those subscribers unattached — registered, constructible,
            // and permanently inert.
            var engine = services.GetService<ScheduledTaskEngine>();

            // #88: change-item follow-up, on the same neutral scan-completion seam the desktop host
            // uses — and subscribed BEFORE Start() so the first run of this process cannot slip past
            // an unwired seam. Mirrored here (rather than left as a desktop-only nicety) because it
            // does NOT self-heal: the engine raises the event whether or not anyone listens, so on a
            // service-only install a HandedToVendor item whose check is still failing past its due
            // date would never be flagged StillFailing, and no later page visit recovers the runs
            // that already went by. Evaluation runs off-thread and is fully isolated.
            try
            {
                var changeItems    = services.GetService<ChangeItemService>();
                var changeExecutor = services.GetService<Data.CheckExecutionService>();
                if (engine != null && changeItems != null && changeExecutor != null)
                {
                    engine.AssessmentRunCompleted += (_, e) =>
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var results = changeExecutor.GetResults(e.ServerName, int.MaxValue);
                                await changeItems.EvaluateFollowUpsAsync(e.ServerName, results);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "[STARTUP] change-item follow-up evaluation failed for {Server}", e.ServerName);
                            }
                        });
                    };
                    Log.Information("[STARTUP] Change-item follow-up wired to scan-completion seam");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[STARTUP] Change-item follow-up wiring failed");
            }

            engine?.Start();
            Capture(engine, s => s.Stop());

            // 30 s ping per enabled server. Feeds the UI's up/down state AND the availability
            // observations the engine's subscribers ride.
            var connectionHealth = services.GetService<ConnectionHealthService>();
            connectionHealth?.Start();
            Capture(connectionHealth, s => s.Dispose());

            // 30 s wait-stats snapshot loop per server.
            var waitStats = services.GetService<WaitStatsService>();
            waitStats?.StartBackgroundLoop();
            Capture(waitStats, s => s.Dispose());

            // F6: hourly prune of expired accepted findings. Not cosmetic in a 24/7 host — an
            // acceptance that has expired but never been pruned keeps suppressing its finding, so
            // every downstream count (report, summary, export) understates the estate until a human
            // opens the app. The desktop host has run this since F6; the service never did.
            var acceptedFindings = services.GetService<AcceptedFindingsService>();
            acceptedFindings?.Start();
            Capture(acceptedFindings, s => s.Stop());

            services.GetService<AuditLogService>()?.LogApplicationStart();
            // L4: CM-3 — config baseline drift check
            _ = Task.Run(() => services.GetService<ConfigBaselineService>()?.RunStartupCheck());
            // L2: A1.2 — start uptime tracking
            _ = services.GetService<UptimeTrackerService>();
            // Consolidation telemetry collector — runs headless 24/7 (opt-in + Premium-gated).
            services.GetService<Capacity.ConsolidationCollector>()?.EnsureStarted();

            return stops;
        }

        /// <summary>
        /// One stop the host owes on the way down: a name for the log, and the action that performs
        /// it. The action closes over the ENGINE INSTANCE captured at start time, never over the
        /// container — which is what makes <see cref="StopBackgroundServices"/> work on a path where
        /// the container is already gone.
        /// </summary>
        internal readonly record struct BackgroundStop(string Name, Action Stop);

        /// <summary>
        /// What a teardown actually did. Returned so the caller can log the truth.
        /// <para><paramref name="AlreadyStopped"/> is NOT a failure: the engine was found already torn
        /// down (see <see cref="StopBackgroundServices"/>), which on the failed-start path is the
        /// normal case, not an incident.</para>
        /// </summary>
        internal readonly record struct StopOutcome(int Attempted, int Failed, int AlreadyStopped);

        /// <summary>
        /// Stops what <see cref="InitializeBackgroundServices"/> started, on the way down. Timer and
        /// loop-based services must release their threads before the process leaves, or a callback in
        /// flight outlives the provider that owns its dependencies.
        ///
        /// <para>TAKES THE CAPTURED INSTANCES, NOT A CONTAINER — and this is the correction of a
        /// defect that was proven live, not a preference. The previous version re-resolved each
        /// engine through <c>IServiceProvider.GetService</c>. On the failed-start path that provider
        /// is ALREADY DISPOSED: <c>app.Run()</c> disposes it before the Kestrel bind exception
        /// propagates, so every one of the eleven resolves threw <see cref="ObjectDisposedException"/>,
        /// each was swallowed to a warning by the per-call containment, not one engine was stopped,
        /// and the method still logged "Background services stopped". The teardown RAN and did
        /// nothing, which is worse than not running, because the log said otherwise.</para>
        ///
        /// <para>Stops run in REVERSE capture order — the shipped order, and the right one: the
        /// engines started last depend on the ones started first. Each is individually contained, so
        /// one fault cannot skip the rest.</para>
        ///
        /// <para>ALREADY-STOPPED IS NOT A FAILURE, and on the failed-start path it is the COMMON
        /// case. Proven live by the gate: the host disposes its container FIRST, which runs each
        /// engine's own <c>Dispose</c> and genuinely stops it, and only then does the finally reach
        /// this method. Six of the eleven then threw <see cref="ObjectDisposedException"/> from
        /// <c>CancellationTokenSource.Cancel()</c> on a CTS their own Dispose had already disposed —
        /// <c>ScheduledTaskEngine</c>, <c>BlockingForensicsService</c>, <c>AlertEvaluationService</c>,
        /// <c>HealthMetricsCollectorService</c>, <c>liveQueriesMaintenanceService</c> and
        /// <c>CacheEvictionService</c>, all of the cancel-a-CTS shape. Counting those as failures
        /// produced "stopped with 6 of 11 failing", which an operator mid-incident reads as a broken
        /// teardown when the truth is that everything was already down.</para>
        ///
        /// <para>So <see cref="ObjectDisposedException"/> is classified, not counted. It means one
        /// thing here on either path — the target is disposed, therefore stopped — because these
        /// closures hold engine INSTANCES and nothing else that could be disposed underneath them.
        /// Every other exception is a genuine failure and is counted and warned as one, and the
        /// clean completion line is only ever printed when the genuine-failure count is zero.</para>
        ///
        /// <para>DOUBLE-STOP is otherwise safe. This runs at most once per process (the caller's
        /// run-once guard), and the second stop any engine sees is the container's own
        /// <c>Dispose</c> — the identical sequence the desktop host has shipped since App.OnExit.
        /// Checked engine by engine, and note the correction to an earlier claim in this file that
        /// cancel-a-CTS is simply "idempotent": Cancel-after-CANCEL is, Cancel-after-DISPOSE is not,
        /// it throws — which is exactly the six above. <c>ConnectionHealthService</c> and
        /// <c>AcceptedFindingsService</c> carry explicit <c>_disposed</c> guards;
        /// <c>WaitStatsService.Dispose</c>, <c>MemoryMonitorService</c> and <c>LogCleanupService</c>
        /// re-dispose already-disposed handles, which is the documented no-op contract of
        /// <see cref="IDisposable"/>. Those five are the ones that did not throw. Nothing here needs
        /// a second guard: the classification below is the handling.</para>
        /// </summary>
        internal static StopOutcome StopBackgroundServices(IReadOnlyList<BackgroundStop>? stops)
        {
            if (stops == null || stops.Count == 0) return new StopOutcome(0, 0, 0);

            int failed = 0;
            int alreadyStopped = 0;
            for (int i = stops.Count - 1; i >= 0; i--)
            {
                var s = stops[i];
                try { s.Stop(); }
                catch (ObjectDisposedException)
                {
                    // Not an error. The engine's own Dispose already tore it down — container
                    // disposal beat this teardown to it, which is the ordinary sequence on a failed
                    // start. Information, not Warning, and it does not touch the failure count.
                    // (ObjectDisposedException derives from InvalidOperationException, so this catch
                    // must stay ABOVE the general one or a real InvalidOperationException would be
                    // excused along with it.)
                    alreadyStopped++;
                    Log.Information("[SHUTDOWN] {Service} was already stopped (container disposal beat the teardown)", s.Name);
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.Warning(ex, "[SHUTDOWN] Error stopping {Service}", s.Name);
                }
            }

            // Never the clean line after GENUINE failures, and never the alarming line without them.
            // This file already carries the first half of that lesson (see the ApplicationStarted
            // comment in RunServer): a log that asserts a state the process never reached is how the
            // original defect stayed invisible. The second half is its mirror — a log that reports a
            // failure the process never had costs an operator the same trust, and costs it at the
            // worst moment, mid-incident.
            if (failed > 0)
                Log.Warning("Background services stopped with {Failed} of {Total} failing ({AlreadyStopped} were already stopped)",
                    failed, stops.Count, alreadyStopped);
            else if (alreadyStopped > 0)
                Log.Information("Background services stopped ({Total}) — {AlreadyStopped} were already stopped by container disposal",
                    stops.Count, alreadyStopped);
            else
                Log.Information("Background services stopped ({Total})", stops.Count);

            return new StopOutcome(stops.Count, failed, alreadyStopped);
        }

        /// <summary>
        /// Resolves the wwwroot folder to serve. Prefers a real folder on disk so a copied
        /// build output (whose dev manifest still points at the build machine's source path)
        /// does not silently serve a non-existent directory and 404 every asset:
        ///   1. {BaseDir}\wwwroot if it exists (published / self-contained deploy), else
        ///   2. the static-web-assets manifest's ContentRoot, but only if that path exists (dev run), else
        ///   3. {BaseDir}\wwwroot as a last resort (logs a warning — static assets may 404).
        /// Shared by ServerModeService so both hosts resolve identically.
        /// </summary>
        internal static string ResolveWebRoot()
        {
            var local = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (Directory.Exists(local))
                return local;

            var manifestPath = Path.Combine(AppContext.BaseDirectory, "SQLTriage.staticwebassets.runtime.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    var json = File.ReadAllText(manifestPath);
                    var startIdx = json.IndexOf("\"ContentRoots\":[\"") + "\"ContentRoots\":[\"".Length;
                    var endIdx = json.IndexOf("\"", startIdx);
                    var fromManifest = json.Substring(startIdx, endIdx - startIdx).Replace("\\\\", "\\").TrimEnd('\\');
                    if (Directory.Exists(fromManifest))
                        return fromManifest;
                    Log.Warning("[WebRoot] Static-web-assets manifest points to '{Path}', which does not exist on " +
                                "this machine — this build output was likely copied from another box. Use a published " +
                                "build (publish copies wwwroot next to the exe) or ship wwwroot alongside SQLTriage.exe.",
                                fromManifest);
                }
                catch (Exception ex) { Log.Debug(ex, "[WebRoot] Failed to parse static web assets manifest"); }
            }

            Log.Warning("[WebRoot] Falling back to '{Path}' (exists={Exists}). If it does not exist, browser assets will 404.",
                local, Directory.Exists(local));
            return local;
        }

        /// <summary>
        /// Resolves the optional <c>ServiceBindAddress</c> setting into the address Kestrel should
        /// bind. Returns <c>null</c> for "every interface", which is the DEFAULT and the shipped
        /// behaviour — "Server mode — share via browser" is what this host is for, and the security
        /// boundary is <see cref="InteractiveAppAdmission"/>, not the bind. This setting narrows the
        /// listener for a locked-down install; it is defence in depth, never the control.
        ///
        /// Accepted: <c>any</c> / <c>*</c> / <c>0.0.0.0</c> → every interface;
        /// <c>loopback</c> / <c>localhost</c> → loopback only; or any parseable IP address.
        ///
        /// ⚠ AN UNRECOGNISED VALUE BINDS LOOPBACK, and that direction is deliberate. The default is
        /// already "every interface", so the ONLY reason to set this key at all is to restrict —
        /// which means honouring a typo permissively would silently defeat the operator's evident
        /// intent to lock the service down. This wave was bitten once by a security decision whose
        /// polarity inverted on malformed input (a truncated user store re-opened an anonymous
        /// admin hatch), so the rule applied here is: if the operator asked for something about
        /// binding and we cannot tell what, take the narrow reading and say so loudly.
        ///
        /// <para>Internal for test visibility (InternalsVisibleTo SQLTriage.Tests) — the polarity
        /// above is the part worth pinning, see <c>WindowsServiceBindAddressTests</c>.</para>
        /// </summary>
        internal static IPAddress? ResolveBindAddress(IConfiguration configuration, out string description, out string? warning)
        {
            warning = null;
            var configured = configuration.GetValue<string?>("ServiceBindAddress");

            if (string.IsNullOrWhiteSpace(configured))
            {
                description = "every interface (default; ServiceBindAddress not set)";
                return null;
            }

            var value = configured.Trim();

            if (value.Equals("any", StringComparison.OrdinalIgnoreCase)
                || value.Equals("anyip", StringComparison.OrdinalIgnoreCase)
                || value == "*"
                || value == "0.0.0.0")
            {
                description = $"every interface (ServiceBindAddress='{value}')";
                return null;
            }

            if (value.Equals("loopback", StringComparison.OrdinalIgnoreCase)
                || value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                description = $"loopback only (ServiceBindAddress='{value}')";
                return IPAddress.Loopback;
            }

            // ⚠ IPAddress.TryParse is LENIENT on IPv4 and accepts partial dotted-quad notation:
            // measured 2026-08-03, "192.168.10" parses successfully as 192.168.0.10. A truncated
            // address would therefore bind a DIFFERENT interface than the operator typed, silently
            // — a config typo quietly changing which network the service answers on. So an IPv4
            // literal must have all four octets; anything else falls through to the narrow reading
            // below. IPv6 is left to TryParse, whose forms are not ambiguous this way and which
            // legitimately normalises (2001:0db8::1 -> 2001:db8::1), so a round-trip check would
            // wrongly reject valid input.
            var looksIPv6 = value.Contains(':');
            if (IPAddress.TryParse(value, out var parsed)
                && (looksIPv6 || value.Split('.').Length == 4))
            {
                description = $"{parsed} only (ServiceBindAddress='{value}')";
                return parsed;
            }

            description = $"loopback only (ServiceBindAddress='{value}' was not understood)";
            warning =
                $"[Bind] ServiceBindAddress='{value}' is not 'any', 'loopback', or a valid IP address. "
                + "Binding LOOPBACK ONLY, because the default is already every interface and the only "
                + "reason to set this setting is to restrict — so an unreadable value is treated as the "
                + "narrow reading rather than silently ignored. Browser access from other machines will "
                + "NOT work until this value is corrected or the setting is removed.";
            return IPAddress.Loopback;
        }

        private static int FindAvailablePort(int preferred)
        {
            for (int port = preferred; port < preferred + 20; port++)
            {
                try
                {
                    using var listener = new TcpListener(IPAddress.Any, port);
                    listener.Start();
                    listener.Stop();
                    return port;
                }
                catch (Exception ex) { Log.Debug(ex, "[ServiceHost] Port {Port} unavailable", port); }
            }
            using var tmp = new TcpListener(IPAddress.Loopback, 0);
            tmp.Start();
            int p = ((IPEndPoint)tmp.LocalEndpoint).Port;
            tmp.Stop();
            return p;
        }

        #region Service Install / Uninstall

        public static int InstallService(string[] args)
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "SQLTriage.exe");

            // Parse optional service account.
            //
            // --password is REJECTED, not ignored. The house rule is already stated and enforced in
            // Cli/AuditCliArgs.cs ("Plaintext passwords are never read from the command line or a
            // file"); this path simply had not been held to it. Silently ignoring the switch would
            // be worse than rejecting it - a script that passed --password would appear to install
            // an account it never authenticated, and the operator would learn otherwise only when
            // the service failed to start.
            string? username = null;
            bool promptCredentials = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--username", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    username = args[++i];
                }
                else if (args[i].Equals("--prompt-credentials", StringComparison.OrdinalIgnoreCase))
                {
                    promptCredentials = true;
                }
                else if (args[i].Equals("--password", StringComparison.OrdinalIgnoreCase))
                {
                    // Deliberately does not echo the value, and deliberately does not consume it.
                    Console.Error.WriteLine(PasswordSwitchRejected);
                    return 1;
                }
            }

            PromptedServiceCredential? credential = null;
            try
            {
                string? accountName = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
                IntPtr passwordPtr = IntPtr.Zero;

                if (accountName is not null && AccountRequiresPassword(accountName))
                {
                    // The prompt is OPT-IN, and this guard is not defensive tidiness - it was found
                    // by mutation. Environment.UserInteractive is true for a test runner, a CI agent
                    // and a scripted install just as it is for an operator at a desk, so an
                    // unattended caller naming an ordinary account would raise a modal dialog with
                    // nobody to answer it and block forever. Observed: a mutant that let --password
                    // through to this path hung a test run on a live "Windows Security" dialog until
                    // the process was killed. Refusing beats hanging.
                    if (!promptCredentials)
                    {
                        Console.Error.WriteLine($"Failed to install service. {PromptCredentialsRequired}");
                        return 1;
                    }

                    // The secret is collected HERE, inside the already-elevated process, and lives
                    // only in an unmanaged buffer handed straight to CreateServiceW. It never
                    // touches a command line, a file or an environment variable on any path.
                    credential = ServiceCredentialPrompt.Prompt(accountName, out var promptError);
                    if (credential is null)
                    {
                        Console.Error.WriteLine($"Failed to install service. {promptError}");
                        return 1;
                    }

                    // The operator may have corrected the account in the elevated dialog. Install
                    // what they confirmed, and report it below, rather than what the caller assumed.
                    accountName = credential.UserName;
                    passwordPtr = credential.PasswordPtr;
                }

                Console.WriteLine($"Installing service: {ServiceDisplayName}");

                var binPath = Win32ServiceInstaller.BuildBinaryPath(exePath);
                int createError = Win32ServiceInstaller.Create(
                    ServiceName, ServiceDisplayName, binPath, accountName, passwordPtr);

                if (createError != 0)
                {
                    // The old message named elevation as the cause of EVERY failure. It is one cause
                    // of several, and Windows already distinguishes them: 1073 = the service is
                    // already installed, 1060 = it does not exist, 5 = access denied. Telling an
                    // operator whose real problem is a name collision to "Run as Administrator"
                    // sends them to re-run the same command elevated, where it fails again for the
                    // same unstated reason.
                    Console.Error.WriteLine($"Failed to install service. {Win32ServiceInstaller.Describe(createError)}");
                    return createError;
                }

                username = accountName;
            }
            finally
            {
                credential?.Dispose();
            }

            // Set description
            RunScCommand($"description \"{ServiceName}\" \"{ServiceDescription}\"");

            // Configure failure recovery (restart after 60s)
            RunScCommand($"failure \"{ServiceName}\" reset= 86400 actions= restart/60000/restart/60000/restart/60000");

            Console.WriteLine("Service installed successfully.");
            Console.WriteLine($"  Name: {ServiceName}");
            // Print the binPath actually stored in the SCM, not a re-guess of it.
            Console.WriteLine($"  Path: {Win32ServiceInstaller.BuildBinaryPath(exePath)}");
            if (!string.IsNullOrEmpty(username))
                Console.WriteLine($"  Account: {username}");
            Console.WriteLine("\nStart with: sc start SQLTriage");
            return 0;
        }

        /// <summary>
        /// What the operator is told when --password appears on the command line. Held as a constant
        /// so the test asserts the SHIPPED sentence rather than a copy of it, and so it cannot drift
        /// into naming a mechanism that is no longer offered.
        /// </summary>
        internal const string PasswordSwitchRejected =
            "--password is not accepted. Plaintext passwords are never read from the command line - " +
            "any process running as you can read another process's command line. Pass --username " +
            "<account> --prompt-credentials and the elevated installer will ask for the password in a " +
            "Windows credential prompt.";

        /// <summary>
        /// What an unattended caller is told when it names an account that needs a password but did
        /// not ask for the interactive prompt. Refusing is the point: the alternative is a modal
        /// dialog nobody is there to answer.
        /// </summary>
        internal const string PromptCredentialsRequired =
            "That account needs a password, which is only ever collected interactively. Re-run with " +
            "--prompt-credentials to be asked for it, or use an account that needs none - a built-in " +
            "service identity (LocalSystem, NT AUTHORITY\\NetworkService) or a group managed service " +
            "account.";

        /// <summary>
        /// Whether an account needs a password supplied at install time.
        ///
        /// Two families legitimately take NONE, and treating either as needing one would put a
        /// credential prompt in front of an install that should be fully unattended:
        ///   * the built-in service identities (LocalSystem and the NT AUTHORITY accounts), and
        ///   * accounts whose name ends in '$' - group managed service accounts and computer
        ///     accounts, whose passwords Windows manages itself.
        /// </summary>
        internal static bool AccountRequiresPassword(string accountName)
        {
            if (string.IsNullOrWhiteSpace(accountName)) return false;

            var name = accountName.Trim();

            // gMSA / computer account: Windows rotates the password, nobody types it.
            if (name.EndsWith('$')) return false;

            return name.ToLowerInvariant() switch
            {
                "localsystem"                    => false,
                "system"                         => false,
                ".\\localsystem"                 => false,
                "nt authority\\system"           => false,
                "nt authority\\networkservice"   => false,
                "nt authority\\network service"  => false,
                "nt authority\\localservice"     => false,
                "nt authority\\local service"    => false,
                _                                => true
            };
        }

        /// <summary>
        /// Plain-English cause for the Win32 codes this path returns, so the caller stops asserting
        /// elevation as the universal explanation. sc.exe surfaces GetLastError as its exit code, so
        /// the uninstall path and the native install path share ONE mapping - a code that gains a
        /// sentence gains it for both, and neither can drift from the other.
        /// </summary>
        private static string DescribeScFailure(int exitCode) => Win32ServiceInstaller.Describe(exitCode);

        public static int UninstallService()
        {
            Console.WriteLine($"Stopping service: {ServiceName}...");
            // A stop failure is not fatal to the uninstall — the service may already be stopped, or
            // never installed. `sc delete` below is the operation whose result actually matters.
            RunScCommand($"stop \"{ServiceName}\"");
            Thread.Sleep(2000);

            Console.WriteLine($"Removing service: {ServiceName}...");
            var result = RunScCommand($"delete \"{ServiceName}\"");

            if (result.ExitCode == 0)
            {
                Console.WriteLine("Service uninstalled successfully.");
                return 0;
            }

            // Proven unelevated: with no service installed, `sc delete` returns 1060 (OpenService
            // failed — the service does not exist), yet this printed "Run as Administrator."
            // Elevation was never the problem, and following that advice changes nothing.
            Console.Error.WriteLine($"Failed to uninstall service. {DescribeScFailure(result.ExitCode)}");
            if (!string.IsNullOrWhiteSpace(result.Output))
                Console.Error.WriteLine($"  sc.exe said: {result.Output.Trim()}");
            return result.ExitCode;
        }

        public static (bool Installed, bool Running, string? Account) GetServiceStatus()
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                var running = sc.Status == ServiceControllerStatus.Running;
                string? account = null;

                // Query service config for account
                try
                {
                    var psi = new ProcessStartInfo("sc", $"qc \"{ServiceName}\"")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    var output = proc?.StandardOutput.ReadToEnd() ?? "";
                    proc?.WaitForExit();

                    foreach (var line in output.Split('\n'))
                    {
                        if (line.Trim().StartsWith("SERVICE_START_NAME", StringComparison.OrdinalIgnoreCase))
                        {
                            account = line.Split(':').LastOrDefault()?.Trim();
                            break;
                        }
                    }
                }
                catch (Exception ex) { Log.Debug(ex, "[ServiceHost] Failed to parse service account from sc qc output"); }

                return (true, running, account);
            }
            catch
            {
                return (false, false, null);
            }
        }

        /// <summary>
        /// Runs sc.exe and returns BOTH its exit code and its own output. The output was previously
        /// redirected and then discarded, which is why a failed install could only ever be reported
        /// with a canned guess — sc.exe's actual explanation was captured and thrown away.
        /// </summary>
        private static (int ExitCode, string Output) RunScCommand(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo("sc", arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return (-1, "Could not start sc.exe.");

                // Read both streams before waiting: sc.exe writes its failure text to stdout, and a
                // full pipe buffer would deadlock a WaitForExit that ran first.
                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();

                if (!proc.WaitForExit(30000))
                    return (-1, "sc.exe did not exit within 30 seconds.");

                var combined = string.Join("\n",
                    new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
                return (proc.ExitCode, combined);
            }
            catch (Exception ex)
            {
                return (-1, $"Could not run sc.exe: {ex.Message}");
            }
        }

        #endregion
    }
}
#pragma warning restore CA1416


