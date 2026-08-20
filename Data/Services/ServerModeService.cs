/* In the name of God, the Merciful, the Compassionate */

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Radzen;
using Serilog;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;

namespace SQLTriage.Data.Services
{
    // BM:ServerModeService.Class — manages a Blazor Server (Kestrel) host for browser access
    /// <summary>
    /// Manages a Blazor Server (Kestrel) host that serves the same UI via browser.
    /// Toggle on/off at runtime — shares all singleton services with the WPF host.
    /// </summary>
    public class ServerModeService : IAsyncDisposable
    {
        private readonly ILogger<ServerModeService> _logger;
        private WebApplication? _webApp;

        public bool IsRunning => _webApp != null;
        public string? Url { get; private set; }
        public string? HttpsUrl { get; private set; }
        public int Port { get; set; } = 5150;
        public int HttpsPort { get; set; } = 5151;
        public bool EnableHttps { get; set; } = true;
        public event Action? OnStateChanged;
        private X509Certificate2? _selfSignedCert;

        public ServerModeService(ILogger<ServerModeService> logger)
        {
            _logger = logger;
        }

        public async Task StartAsync()
        {
            if (_webApp != null) return;

            try
            {
                // Find an available port
                Port = FindAvailablePort(Port);

                // Resolve the wwwroot to serve. Shared with the headless host so dev / published /
                // copied-output all behave the same (prefers a folder that actually exists on disk).
                var webRoot = WindowsServiceHost.ResolveWebRoot();

                _logger.LogInformation("Server mode config: WebRoot={WebRoot}, Exists={Exists}, Port={Port}",
                    webRoot, Directory.Exists(webRoot), Port);

                // RBAC config is needed BEFORE ConfigureKestrel: Windows auth forces the HTTPS
                // listener to HTTP/1.1, and that is a listener option, not a middleware option.
                var rbacConfig = App.Services?.GetService<RbacService>()?.Config ?? RbacService.PeekConfig();

                var builder = WebApplication.CreateBuilder();
                builder.Environment.WebRootPath = webRoot;
                builder.Environment.ContentRootPath = AppContext.BaseDirectory;

                // Serve NuGet package static assets (_content/Radzen.Blazor/*, etc.)
                builder.WebHost.UseStaticWebAssets();

                // Configure Kestrel directly — UseUrls can get overridden by env vars
                builder.WebHost.ConfigureKestrel(kestrel =>
                {
                    kestrel.ListenAnyIP(Port);
                    _logger.LogInformation("Kestrel configured to listen on HTTP port {Port}", Port);

                    // HTTPS, but only once a real handshake has proven the certificate can serve
                    // it. The old shape generated a certificate, bound the listener and logged
                    // "Kestrel configured to listen on HTTPS port {Port} with ephemeral self-signed
                    // certificate" on the strength of the certificate having been CONSTRUCTED. Its
                    // flags were MachineKeySet | EphemeralKeySet, which SChannel cannot build
                    // server credentials from, so the endpoint never completed a handshake and the
                    // line asserted otherwise — on this host and, identically, on the installed
                    // service. See SelfSignedTlsCertificate for the measurement and the flags.
                    if (EnableHttps)
                    {
                        _selfSignedCert = SelfSignedTlsCertificate.CreateVerified("SQLTriage", out var certDetail);
                        if (_selfSignedCert == null)
                        {
                            // No listener bound, and HttpsUrl below stays null, so the Settings
                            // page does not offer an address that cannot be reached either.
                            _logger.LogWarning(
                                "[HTTPS] The generated certificate could not complete a TLS handshake ({Detail}). "
                                + "No HTTPS listener is bound — server mode is HTTP only, on port {Port}.",
                                certDetail, Port);
                        }
                        else
                        {
                            HttpsPort = FindAvailablePort(HttpsPort);
                            kestrel.ListenAnyIP(HttpsPort, listenOptions =>
                            {
                                listenOptions.UseHttps(_selfSignedCert);
                                SqlTriageAuth.ApplyNegotiateProtocolConstraint(listenOptions, rbacConfig);
                            });
                            _logger.LogInformation(
                                "[HTTPS] Self-signed certificate verified ({Detail}); binding an HTTPS listener on port {HttpsPort}.",
                                certDetail, HttpsPort);
                        }
                    }
                });

                // Add Blazor Server services
                builder.Services.AddRazorComponents()
                    .AddInteractiveServerComponents();
                builder.Services.AddRadzenComponents();
                builder.Services.AddScoped<Radzen.DialogService>();

                // Logging — reuse Serilog
                builder.Services.AddLogging(lb =>
                {
                    lb.ClearProviders();
                    lb.AddSerilog(dispose: false);
                });

                // Share all singleton services from the WPF container
                var wpf = App.Services!;
                RegisterSharedSingletons(builder.Services, wpf);

                // Authentication. Registered UNCONDITIONALLY — see AddSqlTriageAuth for why
                // gating it on rbac.Enabled is what left the other host with no auth at all.
                var rbac = wpf.GetService<RbacService>();
                builder.Services.AddSqlTriageAuth(rbac?.Config ?? rbacConfig);

                var app = builder.Build();

                // Show detailed errors in dev
                app.UseDeveloperExceptionPage();

                // Diagnostic: log every incoming request with duration
                app.Use(async (context, next) =>
                {
                    var stopwatch = Stopwatch.StartNew();
                    _logger.LogInformation("Server mode request: {Method} {Path} {Query}",
                        context.Request.Method, context.Request.Path, context.Request.QueryString);
                    try
                    {
                        await next();
                        stopwatch.Stop();
                        _logger.LogInformation("Server mode response: {StatusCode} for {Path} in {ElapsedMs}ms",
                            context.Response.StatusCode, context.Request.Path, stopwatch.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        stopwatch.Stop();
                        _logger.LogError(ex, "Server mode error processing {Path} after {ElapsedMs}ms", context.Request.Path, stopwatch.ElapsedMilliseconds);
                        throw;
                    }
                });

                // Health check endpoint — verifies Kestrel is responding.
                // UNAUTHENTICATED on purpose (monitoring depends on it), so it says only that this
                // listener is alive. It used to return webRoot, contentRoot and whether the web
                // root existed: absolute filesystem paths handed to any unauthenticated caller on
                // the LAN, naming the install directory and, through it, the account it runs as.
                // A liveness probe needs none of that.
                app.MapGet("/_server/health", () => Results.Ok(new { status = "ok" }));

                // Diagnostic HTML page — verifies rendering without Blazor
                app.MapGet("/_server/diag", () => Results.Content(@"<!DOCTYPE html>
<html><head><title>Server Mode Diagnostic</title></head>
<body style='background:#1a1a2e;color:#0f8;font-family:Consolas;padding:40px'>
<h1>Server Mode is Working</h1>
<p>Kestrel is serving pages. If you see this but the main page is blank, the issue is with Blazor rendering.</p>
<ul>
<li><a href='/_server/health' style='color:#4af'>Health Check (JSON)</a></li>
<li><a href='/' style='color:#4af'>Main App (Blazor)</a></li>
</ul>
</body></html>", "text/html"));

                // THE FRONT DOOR: the interactive-application boundary, then static files, then
                // routing — the same three, in the same order, as the headless host, decided once
                // in InteractiveAppAdmission rather than reproduced by hand here.
                //
                // An unauthenticated caller from a non-loopback origin receives the sign-in path
                // and nothing else — no document, no static asset, no /_blazor circuit — so there
                // is no interactive application to smuggle a control into. This is the lane where
                // round 6's Experimental-toggle exploit was driven from 192.10.10.32 and where
                // round 7's alias probe was; both are unreachable from here now, and so is the
                // shape nobody has thought of yet.
                //
                // Everything already built stays in force BEHIND this — the per-surface
                // IsAuthorized gates, ShellGate on the always-rendered shell, ApiAuthorization on
                // /api, store-backed revocation. They are the second layer now, not the boundary.
                //
                // (The request log above is deliberately AHEAD of the gate, so a refusal still
                // shows up in the trace an operator reads.)
                app.UseSqlTriageFrontDoor(rbac ?? app.Services.GetRequiredService<RbacService>());

                // API key authentication for /api/* routes. /api is on the gate's allow-list — it
                // is not an interactive application, it carries a real machine credential, and
                // ApiAuthorization is its boundary. See InteractiveAppAdmission.AlwaysReachablePrefixes.
                //
                // This middleware is NOT that boundary and never was: with no ApiKey configured
                // (the default) it degrades to a same-origin CSRF heuristic that passes every GET.
                // That is why the eleven ungated GET reads below it answered 200 to an anonymous
                // LAN caller until 2026-08-03. Every route in ApiEndpoints now carries a
                // RequirePermission, which is the precondition the allow-list entry rests on.
                app.UseApiKeyAuth();

                // Browser authentication + the loopback claim. Unconditional: even with RBAC
                // dormant the circuit has to learn whether it is on loopback, or the console
                // user cannot reach Settings to configure RBAC in the first place.
                app.UseSqlTriageAuth(rbac ?? app.Services.GetRequiredService<RbacService>());

                app.UseAntiforgery();

                // REST API with structured error handling
                app.Use(ApiEndpoints.ExceptionHandler);
                app.MapApiEndpoints();

                app.MapRazorComponents<Components.ServerApp>()
                    .AddInteractiveServerRenderMode();

                await app.StartAsync();
                _webApp = app;

                // Self-test: verify Kestrel is actually responding
                try
                {
                    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var testUrl = $"http://localhost:{Port}/_server/health";
                    var response = await httpClient.GetAsync(testUrl);
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogInformation("Server mode self-test: {StatusCode} from {Url} — {Body}",
                        (int)response.StatusCode, testUrl, body);
                }
                catch (Exception selfTestEx)
                {
                    _logger.LogError(selfTestEx, "Server mode self-test FAILED on port {Port}", Port);
                }

                var hostName = Dns.GetHostName();
                Url = $"http://{hostName}:{Port}";
                HttpsUrl = _selfSignedCert != null ? $"https://{hostName}:{HttpsPort}" : null;
                _logger.LogInformation("Server mode started at {Url}" + (HttpsUrl != null ? " and {HttpsUrl}" : ""),
                    Url, HttpsUrl ?? "");

                OnStateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start server mode");
                _webApp = null;
                Url = null;
                HttpsUrl = null;
                throw;
            }
        }

        public async Task StopAsync()
        {
            if (_webApp == null) return;

            try
            {
                await _webApp.StopAsync();
                await _webApp.DisposeAsync();
                _logger.LogInformation("Server mode stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping server mode");
            }
            finally
            {
                _webApp = null;
                Url = null;
                HttpsUrl = null;
                _selfSignedCert?.Dispose();
                _selfSignedCert = null;
                OnStateChanged?.Invoke();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }

        // Internal (not private) for test visibility (InternalsVisibleTo SQLTriage.Tests) —
        // see CompositionParityTests, which asserts the forwarded instances win over the
        // fresh ones AddSharedServices registers.
        internal static void RegisterSharedSingletons(IServiceCollection services, IServiceProvider wpf)
        {
            // Robust base: register the FULL shared service set first, so server-mode circuits can
            // resolve ANY service the shared shell / pages inject. This method previously hand-listed
            // registrations and 500'd whenever a component injected an unlisted one (WelcomeTourService,
            // IServerConnectionManager, PowerEstimateService, …). The TryAdd<> overrides below then
            // replace the stateful + background singletons with the RUNNING WPF app's instances, so the
            // browser and the desktop share one corpus / connection set / cache / scan state. Safe:
            // AddSharedServices registers no hosted services, so nothing double-starts.
            var sharedConfig = wpf.GetService<Microsoft.Extensions.Configuration.IConfiguration>()
                               ?? new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
            SQLTriage.Data.ServiceCollectionExtensions.AddSharedServices(services, sharedConfig);

            // This container serves BROWSERS, so its circuits are subject to RBAC. Registered
            // after AddSharedServices so it overrides the Desktop default — and deliberately NOT
            // forwarded from the WPF container, which is the whole point: one process, two
            // containers, two different answers.
            services.AddSingleton(HostEnvironmentInfo.BrowserHosted);

            // Forward all known singletons by resolved instance
            TryAdd<Microsoft.Extensions.Configuration.IConfiguration>(services, wpf);
            TryAdd<ServerConnectionManager>(services, wpf);
            TryAdd<GlobalInstanceSelector>(services, wpf);
            TryAdd<WebView2Helper>(services, wpf);
            TryAdd<AutoUpdateService>(services, wpf);
            TryAdd<UserSettingsService>(services, wpf);
            // KeyboardShortcutService is deliberately NOT forwarded. TryAdd<T> registers the
            // WPF container's RESOLVED INSTANCE as a singleton, which would hand every browser
            // circuit the desktop's shortcut bus and re-create the exact cross-circuit multicast
            // the scoped lifetime exists to remove. AddSharedServices already registers it
            // AddScoped for this container. DiLifetimeCensusTests fails if it comes back.
            TryAdd<ConnectionHealthService>(services, wpf);
            TryAdd<AlertingService>(services, wpf);
            TryAdd<AlertDefinitionService>(services, wpf);
            TryAdd<AlertHistoryService>(services, wpf);
            TryAdd<AlertEvaluationService>(services, wpf);
            TryAdd<ScheduledTaskDefinitionService>(services, wpf);
            TryAdd<ScheduledTaskHistoryService>(services, wpf);
            TryAdd<ScheduledTaskEngine>(services, wpf);
            TryAdd<MemoryMonitorService>(services, wpf);
            TryAdd<ConfigurationValidator>(services, wpf);
            TryAdd<LogCleanupService>(services, wpf);
            TryAdd<AuditLogService>(services, wpf);
            // CredentialProtector is static — no need to register
            TryAdd<IDbConnectionFactory>(services, wpf);
            TryAdd<SqlServerConnectionFactory>(services, wpf);
            TryAdd<DatabaseAvailabilityService>(services, wpf);
            TryAdd<DashboardConfigService>(services, wpf);
            TryAdd<QueryExecutor>(services, wpf);
            TryAdd<ResilienceService>(services, wpf);
            TryAdd<QueryRegistry>(services, wpf);
            TryAdd<IQueryOrchestrator, QueryOrchestrator>(services, wpf);
            TryAdd<IMemoryCache, MemoryCache>(services, wpf);
            TryAdd<ICacheHotTier, CacheHotTier>(services, wpf);
            TryAdd<CheckRepositoryService>(services, wpf);
            TryAdd<CheckExecutionService>(services, wpf);
            TryAdd<DiagnosticScriptRunner>(services, wpf);
            TryAdd<FullAuditStateService>(services, wpf);
            TryAdd<ToastService>(services, wpf);
            TryAdd<PrintService>(services, wpf);
            TryAdd<SqlAssessmentService>(services, wpf);

            TryAdd<ReportPageConfigService>(services, wpf);
            TryAdd<XEventService>(services, wpf);
            TryAdd<InstallProvenanceService>(services, wpf);
            TryAdd<AdminAuthService>(services, wpf);
            TryAdd<QuickCheckStateService>(services, wpf);
            TryAdd<VulnerabilityAssessmentStateService>(services, wpf);
            TryAdd<ThemeService>(services, wpf);
            TryAdd<ServerModeService>(services, wpf);
            TryAdd<DataProtectionService>(services, wpf);
            TryAdd<NotificationChannelService>(services, wpf);
            TryAdd<AzureBlobExportService>(services, wpf);
            TryAdd<ProcessGuard>(services, wpf);
            TryAdd<ProductionReadinessGate>(services, wpf);
            TryAdd<RbacService>(services, wpf);
            TryAdd<AutoRefreshService>(services, wpf);
            TryAdd<HealthCheckService>(services, wpf);
            TryAdd<BPScriptService>(services, wpf);
            TryAdd<liveQueriesTableService>(services, wpf);
            TryAdd<SessionDataService>(services, wpf);
            TryAdd<SessionManager>(services, wpf);
            TryAdd<LocalLogService>(services, wpf);
            TryAdd<PowerShellService>(services, wpf);
            TryAdd<Caching.liveQueriesCacheStore>(services, wpf);
            TryAdd<Caching.CacheStateTracker>(services, wpf);
            TryAdd<Caching.CachingQueryExecutor>(services, wpf);
            TryAdd<Caching.CacheEvictionService>(services, wpf);
            TryAdd<Caching.liveQueriesMaintenanceService>(services, wpf);

            // App-wide singletons the shared shell (MainLayout / NavMenu) injects. Shared from the
            // WPF container so server-mode circuits see the same bundle / feature / licensing state.
            // If you add an @inject of an app-wide singleton to a shared layout/component, register
            // it here too — otherwise server mode 500s on "no registered service" at render.
            TryAdd<SQLTriage.Data.Services.Licensing.IBundleAccessor>(services, wpf);
            TryAdd<SQLTriage.Data.Services.Capacity.IConsolidationModelProvider>(services, wpf);
            // Added 2026-08-05: Pages/QuickCheck.razor ships in EVERY profile and now asks this
            // before it offers a one-click fix, so a server-mode circuit rendering the results grid
            // needs it forwarded or the page 500s on "no registered service" — exactly what the
            // comment above warns about. The three /remediation-family pages already injected it
            // and were therefore already unrenderable in server mode; forwarding it here fixes
            // those as a side effect. Same instance as the desktop container, so the licence
            // reading cannot differ between the two lanes.
            TryAdd<SQLTriage.Data.Services.Remediation.IRemediationCapability>(services, wpf);
            TryAdd<IFeatureGate>(services, wpf);
            TryAdd<PanelMetricsService>(services, wpf);

            // Licensing: the CONCRETE BundleAccessor must be forwarded alongside the interface.
            // AddSharedServices registers BundleAccessor as a concrete singleton and maps
            // IBundleAccessor onto it; forwarding only the interface left this container with a
            // second, empty BundleAccessor — and LicenseService takes the CONCRETE type, so a
            // browser-lane activation called Replace() on that orphan while every consumer kept
            // reading the desktop instance. Activating from the browser appeared to succeed and
            // changed nothing.
            TryAdd<SQLTriage.Data.Services.Licensing.BundleAccessor>(services, wpf);
            TryAdd<SQLTriage.Data.Services.Licensing.LicenseService>(services, wpf);

            // Seat claims and the demo-run ledger are per-process state that gates real runs.
            // A second instance here would let the browser lane claim seats / demo runs the
            // desktop lane cannot see, so the two lanes would disagree about the same licence.
            TryAdd<SQLTriage.Data.Services.Licensing.ISeatRegister>(services, wpf);
            TryAdd<SQLTriage.Data.Services.Licensing.IDemoRunLedger>(services, wpf);

            // Services the WPF host explicitly STARTS at boot (see App.xaml.cs OnStartupAsync).
            // AddSharedServices would hand this container a fresh, never-started copy whose
            // in-memory buffers are empty, so the same page renders data on the desktop and
            // "no data" in the browser. Forward the running instances instead.
            TryAdd<AlertBaselineService>(services, wpf);
            TryAdd<BlockingForensicsService>(services, wpf);
            TryAdd<HealthMetricsCollectorService>(services, wpf);
            TryAdd<HistoricalPerformanceService>(services, wpf);
            TryAdd<CodeHotspotsCacheService>(services, wpf);
            TryAdd<WaitStatsService>(services, wpf);
            TryAdd<UptimeTrackerService>(services, wpf);
            TryAdd<PerformanceInspectorService>(services, wpf);
            TryAdd<AcceptedFindingsService>(services, wpf);
            TryAdd<ChangeItemService>(services, wpf);
            TryAdd<SQLTriage.Data.Services.Capacity.ConsolidationCollector>(services, wpf);

            // Interface forwards to shared concrete singletons (mirror AddSharedServices). The
            // concretes above are shared from the WPF container; map the interfaces some shell
            // services depend on (e.g. ServerContextService → IServerConnectionManager).
            services.AddSingleton<IServerConnectionManager>(sp => sp.GetRequiredService<ServerConnectionManager>());
            TryAdd<SqlWatchInstanceDiscovery>(services, wpf);

            // Scoped services — each browser tab/circuit gets its own instance. Mirror the
            // AddScoped set in ServiceCollectionExtensions for services the shared shell injects.
            services.AddScoped<DashboardDataService>();
            services.AddScoped<AppUserState>();
            services.AddScoped<CorrelationIdAccessor>();
            services.AddScoped<WelcomeTourService>();
            services.AddScoped<IServerContextService, ServerContextService>();

            // Circuit handler — monitors Blazor Server circuit lifecycle
            services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, AppCircuitHandler>();
        }

        private static void TryAdd<T>(IServiceCollection services, IServiceProvider provider) where T : class
        {
            try
            {
                var instance = provider.GetService<T>();
                if (instance != null)
                {
                    services.AddSingleton(instance);
                    Log.Debug("ServerMode: registered {Service}", typeof(T).Name);
                }
                else
                {
                    Log.Warning("ServerMode: {Service} not found in WPF container", typeof(T).Name);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ServerMode: failed to register {Service}", typeof(T).Name);
            }
        }

        private static void TryAdd<TService, TImplementation>(IServiceCollection services, IServiceProvider provider)
            where TService : class
            where TImplementation : class, TService
        {
            try
            {
                var instance = provider.GetService<TService>();
                if (instance != null)
                {
                    services.AddSingleton<TService>(instance);
                    Log.Debug("ServerMode: registered {Service}", typeof(TService).Name);
                }
                else
                {
                    Log.Warning("ServerMode: {Service} not found in WPF container", typeof(TService).Name);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ServerMode: failed to register {Service}", typeof(TService).Name);
            }
        }

        // Authentication (cookie + OAuth + Negotiate) and the /auth/* endpoints used to live
        // here as two private methods. They now live in SqlTriageAuth so the headless host and
        // the installed Windows service run the SAME pipeline — see AddSqlTriageAuth for the
        // live evidence that the other host had no authentication at all.

        private static int FindAvailablePort(int preferred)
        {
            for (int port = preferred; port < preferred + 20; port++)
            {
                try
                {
                    // Probe EXACTLY the way Kestrel binds, or the probe is blind.
                    //
                    // This used to open TcpListener(IPAddress.Any) with the comment "Kestrel binds
                    // 0.0.0.0" — which is wrong. ConfigureKestrel calls ListenAnyIP, and that binds
                    // [::] in DUAL-STACK mode. An IPv4-only probe is therefore strictly narrower
                    // than the real bind, so it can SUCCEED on a port the server then fails to take.
                    // Proven 2026-07-20: an IPv4 probe bound an occupied 5150 while a dual-stack
                    // bind on the same port threw; the same IPv4 probe on a free 5199 also bound,
                    // so it was blind, not merely erroring. The consequence was ugly — the service
                    // starts, fails to bind, dies, and the restart/60000 recovery policy loops it.
                    using var listener = Socket.OSSupportsIPv6
                        ? new TcpListener(IPAddress.IPv6Any, port) { Server = { DualMode = true } }
                        : new TcpListener(IPAddress.Any, port);
                    listener.Start();
                    listener.Stop();
                    return port;
                }
                catch { /* port in use, try next */ }
            }
            // Last resort: let OS pick
            using var tmp = new TcpListener(IPAddress.Loopback, 0);
            tmp.Start();
            int p = ((IPEndPoint)tmp.LocalEndpoint).Port;
            tmp.Stop();
            return p;
        }
    }
}
