/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Radzen;
using Serilog;
using Serilog.Core;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;

#pragma warning disable CA1416 // Windows-only API — project targets net8.0-windows

namespace SQLTriage
{
    public partial class App : Application
    {
        public static IServiceProvider? Services { get; private set; }
        public static WebView2Helper? WebView2Helper { get; private set; }
        public static bool WebView2Available { get; private set; } = true;
        public static string? WebView2ErrorMessage { get; private set; }

        /// <summary>Runtime-switchable Serilog minimum level. Toggle via Settings → Enable Debug Logging.</summary>
        public static readonly LoggingLevelSwitch LogLevelSwitch = new(Serilog.Events.LogEventLevel.Information);

        protected override void OnStartup(StartupEventArgs e)
        {
            // Configure Serilog EARLY so we get logs even if startup fails
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    path: System.IO.Path.Combine(AppContext.BaseDirectory, "logs", "app-.log"),
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 50L * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information("Application starting...");

            // Keep the process from consuming all CPU if a background loop misbehaves.
            // BelowNormal means the OS scheduler favours all other normal-priority processes.
            System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
                System.Diagnostics.ProcessPriorityClass.BelowNormal;
            Log.Information("[STARTUP] Process priority set to BelowNormal (CPU runaway guard)");

            base.OnStartup(e);
            _ = OnStartupAsync(e);
        }

        private async Task OnStartupAsync(StartupEventArgs e)
        {
            // Set up global exception handling
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // Configure Serilog - Single consolidated log file (defaults applied; reconfigured after config loads)
            var configStart = DateTime.Now;
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(LogLevelSwitch)
                .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "SQLTriage")
                .Enrich.WithProperty("User", Environment.UserName)
                .Enrich.WithProperty("Machine", Environment.MachineName)
                .WriteTo.File(
                    path: System.IO.Path.Combine(AppContext.BaseDirectory, "logs", "app-.log"),
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 50L * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            // ===== STARTUP PHASE 1: WebView2 Check =====
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var webView2Helper = new WebView2Helper();
            var webView2Status = await webView2Helper.CheckWebView2StatusAsync();
            sw.Stop();
            Log.Information("[STARTUP] WebView2 check completed in {ElapsedMs}ms - Version: {Version}, IsCompatible: {Compatible}",
                sw.ElapsedMilliseconds, webView2Status.Version, webView2Status.IsCompatible);

            WebView2Helper = webView2Helper;

            if (!webView2Status.IsInstalled || !webView2Status.IsCompatible)
            {
                WebView2Available = false;
                WebView2ErrorMessage = webView2Status.ErrorMessage ?? "WebView2 runtime is not available";

                Log.Warning("WebView2 runtime check failed: {ErrorMessage}. Windows Version: {WindowsVersion}. " +
                            "Application will attempt to start but may fail.",
                        WebView2ErrorMessage, WebView2Helper.GetWindowsVersion());

                // Don't block startup - let the MainWindow handle the error display
                // This allows users on servers with WebView2 to still run the app
            }
            else
            {
                Log.Information("WebView2 runtime verified. Version: {Version}", webView2Status.Version);
            }

            // ===== STARTUP PHASE 2: Configuration Loading =====
            sw.Restart();
            IConfiguration configuration;
            try
            {
                configuration = new ConfigurationBuilder()
                    .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                    .AddJsonFile("config/appsettings.json", optional: false, reloadOnChange: true)
                    // governance-weights.json removed from IConfiguration (Phase 2).
                    // Weights are now served by GovernanceWeightsProvider (lazy load / safe fallback).
                    // Phase 3 will serve them from the encrypted license bundle via IBundleAccessor.
                    .Build();
            }
            catch (Exception cfgEx) when (cfgEx is FileNotFoundException
                                       || cfgEx is System.Text.Json.JsonException
                                       || cfgEx is InvalidOperationException)
            {
                var cfgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "appsettings.json");
                Log.Fatal(cfgEx, "[STARTUP] Configuration file missing or corrupt: {Path}", cfgPath);
                System.Windows.MessageBox.Show(
                    $"SQLTriage configuration file is missing or corrupt.\n\nPath: {cfgPath}\n\nPlease reinstall or restore the file.\n\nDetail: {cfgEx.Message}",
                    "SQLTriage - Configuration Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                Environment.Exit(1);
                return; // unreachable — satisfies compiler flow analysis
            }
            sw.Stop();
            Log.Information("[STARTUP] Configuration loaded in {ElapsedMs}ms", sw.ElapsedMilliseconds);

            // Reconfigure Serilog with config-driven file-sink bounds (Serilog:FileSink:* keys).
            // Caps log storage to ~1.5 GB (50 MB × 30 files) by default.
            {
                var fileSinkCfg = configuration.GetSection("Serilog:FileSink");
                var fileSizeMb = fileSinkCfg.GetValue<int>("FileSizeLimitMb", 50);
                var retainCount = fileSinkCfg.GetValue<int>("RetainedFileCount", 30);
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.ControlledBy(LogLevelSwitch)
                    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .Enrich.WithProperty("Application", "SQLTriage")
                    .Enrich.WithProperty("User", Environment.UserName)
                    .Enrich.WithProperty("Machine", Environment.MachineName)
                    .WriteTo.File(
                        path: System.IO.Path.Combine(AppContext.BaseDirectory, "logs", "app-.log"),
                        rollingInterval: RollingInterval.Day,
                        fileSizeLimitBytes: (long)fileSizeMb * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: retainCount,
                        buffered: false,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .CreateLogger();
                Log.Information("[STARTUP] Serilog reconfigured: {SizeMb}MB per file, {Count} files retained (max ~{TotalMb}MB)",
                    fileSizeMb, retainCount, fileSizeMb * retainCount);
            }

            // QuestPDF runs under the Community licence (free for our usage). Must be set once,
            // before any document is generated.
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

            // ===== STARTUP PHASE 3: Service Registration =====
            sw.Restart();
            var services = new ServiceCollection();

            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog(dispose: true);
            });
            services.AddWpfBlazorWebView();
            services.AddRadzenComponents();

            // Register WebView2 helper for runtime detection
            services.AddSingleton<WebView2Helper>();

            // Register ServerConnectionManager first - it will be used by SqlServerConnectionFactory
            services.AddSharedServices(configuration); /* BM: shared services registered via AddSharedServices — see Data/ServiceCollectionExtensions.cs */
            // PerformanceInspectorService + PanelMetricsService moved INTO AddSharedServices so the
            // headless host gets them too (was a WPF-only registration → headless 500 on every page).

            // DevBridge — only registered when --devbridge is on the command line.
            // See Data/Services/DevBridgeService.cs and DEVBRIDGE.md for the
            // security posture and usage. Off by default; never auto-on in release.
            // !SQLT_NO_DEVTOOLS: DevBridge sources (and the BlazorHybridBridge sibling
            // ProjectReference) are compiled out of community builds entirely.
#if DEBUG && !SQLT_NO_DEVTOOLS
            var devBridgeArg = e.Args?.FirstOrDefault(a => a.StartsWith("--devbridge", StringComparison.OrdinalIgnoreCase));
            if (devBridgeArg != null)
            {
                services.AddSingleton<Data.Services.DevBridgeService>();
                Data.BuildMode.MarkDevBridgeActive();  // drives the title suffix + window accent
            }
#endif

            Services = services.BuildServiceProvider();
            sw.Stop();
            Log.Information("[STARTUP] DI container built in {ElapsedMs}ms", sw.ElapsedMilliseconds);

            // ===== STARTUP PHASE 3.5: License initialisation =====
            // Must run after DI builds and BEFORE any service that depends on IBundleAccessor.
            // Failure is non-fatal — accessor resets to unlocked=false and the app boots in
            // a degraded state ("Bundle missing — reinstall" shown in Audit Assessment).
            sw.Restart();
            try
            {
                Services.GetRequiredService<Data.Services.Licensing.LicenseService>().Initialize();
            }
            catch (Exception licEx)
            {
                Log.Error(licEx, "[STARTUP] LicenseService.Initialize threw unexpectedly — app will boot in unlicensed state.");
            }
            sw.Stop();
            Log.Information("[STARTUP] LicenseService.Initialize completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);

            // ===== STARTUP PHASE 3.6: Feature gate registration =====
            // Declare the master superset of gated modules + apply the persisted public profile.
            // Must run AFTER LicenseService.Initialize so HARD gates see the resolved bundle/tier.
            try
            {
                Data.Services.FeatureRegistrar.RegisterAll(Services);
            }
            catch (Exception fgEx)
            {
                Log.Error(fgEx, "[STARTUP] FeatureRegistrar.RegisterAll threw — gated features fall back to ungated nav.");
            }

            // Apply saved proxy to AutoUpdateService (before background check starts)
            var savedProxy = Services.GetService<UserSettingsService>()?.GetUpdateProxyUrl();
            if (!string.IsNullOrWhiteSpace(savedProxy))
                Services.GetService<AutoUpdateService>()?.SetManualProxyUrl(savedProxy);

            // Start DevBridge if requested via --devbridge[=PORT]. The service was
            // only registered above when the flag was present, so a missing service
            // here means the flag was absent and the bridge is intentionally off.
#if DEBUG && !SQLT_NO_DEVTOOLS
            if (devBridgeArg != null)
            {
                int port = 5179;
                var eq = devBridgeArg.IndexOf('=');
                if (eq > 0 && int.TryParse(devBridgeArg.AsSpan(eq + 1), out var p) && p > 0 && p < 65536)
                    port = p;

                var devBridge = Services.GetService<Data.Services.DevBridgeService>();
                devBridge?.SetServiceProvider(Services);
                devBridge?.Start(port);
            }
#endif

            // Wire up debug logging toggle — switches Serilog level at runtime without restart
            var userSettings = Services.GetService<UserSettingsService>();
            if (userSettings != null)
            {
                if (userSettings.GetDebugLogging())
                    LogLevelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Debug;

                userSettings.OnDebugLoggingChanged += enabled =>
                {
                    LogLevelSwitch.MinimumLevel = enabled
                        ? Serilog.Events.LogEventLevel.Debug
                        : Serilog.Events.LogEventLevel.Information;
                    Log.Information("Debug logging {State}", enabled ? "enabled" : "disabled");
                };

                // Apply server name anonymisation setting on startup
                Data.LogAnon.Enabled = userSettings.GetAnonymiseServerNames();
            }

            // Validate configuration
            var configValidator = Services.GetService<ConfigurationValidator>();
            var (isValid, errors) = configValidator?.Validate() ?? (true, new List<string>());
            if (!isValid)
            {
                Log.Warning("Configuration validation failed: {Errors}", string.Join(", ", errors));
            }

            // Start log cleanup (runs now + every 24 hours)
            Log.Information("[STARTUP] Starting background services...");
            Services.GetService<LogCleanupService>()?.Start();

            // Start memory monitoring + wire to cache hot-tier compaction.
            // When working set exceeds 80% of available memory, compact 25% of the cache.
            var memMon = Services.GetService<MemoryMonitorService>();
            var hotTier = Services.GetService<Data.Caching.ICacheHotTier>();
            if (memMon != null && hotTier != null)
            {
                memMon.MemoryPressureChanged += (_, args) =>
                {
                    if (args.IsUnderPressure)
                    {
                        // Shed 25% of the in-memory hot-tier cache and let the GC reclaim the dropped
                        // refs on its own schedule. We deliberately do NOT force a GC / LOH compaction:
                        // forced collects freeze the UI and prematurely promote to Gen2 (see the note in
                        // FullAuditStateService), and the dominant footprint here is WebView2 (Chromium
                        // child processes), which the managed GC cannot reclaim regardless.
                        hotTier.Compact(0.25);
                    }
                };
            }
            Log.Information("[STARTUP] MemoryMonitorService started (compact-on-pressure wired)");

            // Start cache eviction timer (runs every 5 minutes)
            Services.GetService<CacheEvictionService>()?.Start();
            Log.Information("[STARTUP] CacheEvictionService started");

            // Start liveQueries maintenance timer (VACUUM + optimize, default every 4 hours)
            Services.GetService<liveQueriesMaintenanceService>()?.Start();
            Log.Information("[STARTUP] liveQueriesMaintenanceService started");

            // Start alert baseline service (aggressive seeding for first 5 min, then hourly recompute)
            Log.Information("[STARTUP] AlertBaselineService starting async...");
            _ = Task.Run(async () =>
            {
                try
                {
                    var baseline = Services.GetService<Data.Services.AlertBaselineService>();
                    if (baseline != null) await baseline.StartAsync();
                    Log.Information("[STARTUP] AlertBaselineService completed");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[STARTUP] AlertBaselineService failed");
                }
            });

            // Start unified query orchestrator
            Services.GetService<IQueryOrchestrator>()?.Start();
            Log.Information("[STARTUP] QueryOrchestrator started");
            // Start alert evaluation engine
            Services.GetService<Data.Services.AlertEvaluationService>()?.Start();
            Log.Information("[STARTUP] AlertEvaluationService started");

            // Start blocking/deadlock forensics collector (captures across enabled instances)
            Services.GetService<Data.Services.BlockingForensicsService>()?.Start();
            Log.Information("[STARTUP] BlockingForensicsService started");

            // Start scheduled task engine
            Services.GetService<Data.Services.ScheduledTaskEngine>()?.Start();
            Log.Information("[STARTUP] ScheduledTaskEngine started");

            // Start connection health monitor (30s ping per enabled server)
            Services.GetService<Data.Services.ConnectionHealthService>()?.Start();
            Log.Information("[STARTUP] ConnectionHealthService started");

            // Start wait-stats background snapshot loop (30s cadence per server)
            Services.GetService<Data.Services.WaitStatsService>()?.StartBackgroundLoop();
            Log.Information("[STARTUP] WaitStatsService background loop started");

            // Eagerly resolve HistoricalPerformanceService so its hourly rollup timer
            // starts at boot (not only when Performance Trends is first opened), and
            // kick an immediate rollup so the hourly/daily tables populate from any
            // already-collected raw snapshots — otherwise Performance Trends shows
            // "no data" until an hour after the page is first viewed.
            var histPerf = Services.GetService<Data.Services.HistoricalPerformanceService>();
            if (histPerf != null)
            {
                _ = Task.Run(async () =>
                {
                    try { await histPerf.RunRollupAsync(System.Threading.CancellationToken.None, fullBackfill: true); }
                    catch (Exception ex) { Log.Warning(ex, "[STARTUP] initial wait-stats rollup failed"); }
                });
                Log.Information("[STARTUP] HistoricalPerformanceService rollup started (initial pass kicked)");
            }

            // Start CodeHotspots snapshot loop (5min cadence per enabled server) for /code-hotspots Delta view
            Services.GetService<Data.Services.CodeHotspotsCacheService>()?.Start();
            Log.Information("[STARTUP] CodeHotspotsCacheService snapshot loop started");

            // Eagerly resolve the consolidation telemetry collector so its hourly timer
            // starts at boot. Opt-in + Premium-gated: it stays dormant until an operator
            // Starts it and the licensed model is present.
            Services.GetService<Data.Services.Capacity.ConsolidationCollector>()?.EnsureStarted();
            Log.Information("[STARTUP] ConsolidationCollector resolved (opt-in, Premium-gated)");

            // Apply persisted perf inspector toggle so it's active before any dashboard loads
            var perfInspector = Services.GetService<PerformanceInspectorService>();
            perfInspector?.SetEnabled(Services.GetService<UserSettingsService>()?.GetEnablePerfInspector() ?? true);

            // Log application start for audit trail
            var auditLog = Services.GetService<AuditLogService>();
            auditLog?.LogApplicationStart();

            // L4: CM-3 — config baseline drift check (informational; never blocks startup)
            _ = Task.Run(() =>
            {
                try { Services.GetService<Data.Services.ConfigBaselineService>()?.RunStartupCheck(); }
                catch (Exception ex) { Log.Error(ex, "[STARTUP] fire-and-forget task failed: {Context}", "ConfigBaselineService.RunStartupCheck"); }
            });

            // L2: A1.2 — resolve UptimeTrackerService singleton so it writes session_start immediately
            _ = Services.GetService<Data.Services.UptimeTrackerService>();

            Log.Information("Application started successfully");
            Log.Information("[STARTUP] All services started - entering UI phase");

            // Pre-warm Live Monitor session data so the page loads instantly
            // Fires on startup (if connections exist) and whenever connections change
            var sessionSvc = Services.GetService<SessionDataService>();
            var connManager = Services.GetService<ServerConnectionManager>();
            if (sessionSvc != null && connManager != null)
            {
                void TriggerPrefetch()
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(2000); // brief delay — let connection pool warm up first
                            await sessionSvc.PrefetchAsync();
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "[STARTUP] fire-and-forget task failed: {Context}", "SessionDataService.PrefetchAsync");
                        }
                    });
                }

                connManager.OnConnectionChanged += TriggerPrefetch;

                // Prefetch now if we already have connections
                if (connManager.GetConnections().Any())
                    TriggerPrefetch();
            }

            // Ensure liveQueries tables exist for all panels on startup (best-effort, non-blocking)
            _ = Task.Run(async () =>
            {
                try
                {
                    var tableService = Services?.GetService<liveQueriesTableService>();
                    var connFactory = Services?.GetService<IDbConnectionFactory>();
                    var configService = Services?.GetService<DashboardConfigService>();

                    if (tableService != null && connFactory != null && configService != null)
                    {
                        var (succeeded, failed, errors) = await tableService.EnsureTablesForAllPanelsAsync(
                            connFactory, configService.Config);

                        Log.Information("liveQueries table provisioning: {Succeeded} OK, {Failed} failed", succeeded, failed);
                        foreach (var err in errors)
                            Log.Warning("liveQueries provisioning issue: {Error}", err);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "liveQueries table provisioning error");
                }
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Apply staged update if one was downloaded
            try
            {
                var updateService = Services?.GetService<AutoUpdateService>();
                if (updateService?.HasStagedUpdate == true)
                {
                    Log.Information("Applying staged update on exit...");
                    updateService.ApplyUpdateOnExit();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to apply update on exit");
            }

            // â”€â”€ Flush SQLite WAL to prevent corruption on abrupt shutdown â”€â”€
            try
            {
                var cacheStore = Services?.GetService<Data.Caching.liveQueriesCacheStore>();
                if (cacheStore != null)
                {
                    using var conn = cacheStore.CreateExternalConnection();
                    conn.Open();
                    using var wal = conn.CreateCommand();
                    wal.CommandText = "PRAGMA wal_checkpoint(FULL);";
                    wal.ExecuteNonQuery();
                    Log.Debug("SQLite WAL checkpoint completed on exit");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "SQLite WAL checkpoint failed on exit");
            }

            // â”€â”€ Explicitly stop all background services before DI dispose â”€â”€
            // This prevents deadlocks from IAsyncDisposable services waiting
            // on active timer callbacks or Kestrel connections.
            StopBackgroundServices();

            // Dispose the DI container with a timeout to prevent hanging.
            // We run the async dispose on a thread-pool task and wait on the Task
            // directly — no GetAwaiter().GetResult() on the continuation, which
            // avoids the UI-thread deadlock risk if a disposable's continuation
            // captures SynchronizationContext.
            try
            {
                var disposeTask = Task.Run(async () =>
                {
                    if (Services is IAsyncDisposable asyncDisposable)
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    else if (Services is IDisposable disposable)
                        disposable.Dispose();
                });

                if (!disposeTask.Wait(TimeSpan.FromSeconds(5)))
                    Log.Warning("Service provider dispose timed out after 5 seconds");
            }
            catch (Exception disposeEx)
            {
                Log.Warning(disposeEx, "Error disposing service provider");
            }

            Log.Information("Application exiting...");
            Log.CloseAndFlush();
            base.OnExit(e);
        }

        /// <summary>
        /// Explicitly stops all timer-based and background services so they release
        /// their threads before the DI container is disposed. Without this, services
        /// with active timer callbacks can keep the process alive indefinitely.
        /// </summary>
        private static void StopBackgroundServices()
        {
            try
            {
                // Stop server mode (Kestrel) — may already be stopped by MainWindow
                var serverMode = Services?.GetService<Data.Services.ServerModeService>();
                if (serverMode?.IsRunning == true)
                {
                    _ = serverMode.StopAsync().ContinueWith(t =>
                    {
                        if (t.IsFaulted) Log.Warning(t.Exception, "Error stopping server mode on exit");
                    }, TaskScheduler.Default);
                }

                // Stop timer-based services
                Services?.GetService<Data.Services.AlertEvaluationService>()?.Stop();
                Services?.GetService<Data.Services.BlockingForensicsService>()?.Stop();
                Services?.GetService<Data.Services.ScheduledTaskEngine>()?.Stop();
                Services?.GetService<CacheEvictionService>()?.Stop();
                Services?.GetService<liveQueriesMaintenanceService>()?.Stop();
                Services?.GetService<MemoryMonitorService>()?.Dispose();
                Services?.GetService<LogCleanupService>()?.Dispose();
                Services?.GetService<AutoRefreshService>()?.Dispose();
#if DEBUG && !SQLT_NO_DEVTOOLS
                Services?.GetService<Data.Services.DevBridgeService>()?.Stop();
#endif
                // R-L2: explicitly stop connection health + wait stats (cancel their CTS)
                Services?.GetService<Data.Services.ConnectionHealthService>()?.Dispose();
                Services?.GetService<Data.Services.CodeHotspotsCacheService>()?.Stop();
                Services?.GetService<Data.Services.WaitStatsService>()?.Dispose();

                Log.Information("Background services stopped");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error stopping background services");
            }
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            Log.Fatal(ex, "Unhandled exception occurred. IsTerminating: {IsTerminating}", e.IsTerminating);
        }

        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error(e.Exception, "Unhandled dispatcher exception occurred");

            // If WebView2 is missing, the BlazorWebView throws asynchronously through the
            // dispatcher as TargetInvocationException → WebView2RuntimeNotFoundException.
            // Catch it here and trigger server mode fallback on the main window.
            if (IsWebView2Exception(e.Exception))
            {
                Log.Warning("WebView2 runtime exception caught — triggering server mode fallback");
                var mainWindow = MainWindow as MainWindow;
                mainWindow?.FallbackToServerMode();
            }

            e.Handled = true; // Prevent application crash
        }

        private static bool IsWebView2Exception(Exception? ex)
        {
            while (ex != null)
            {
                if (ex.GetType().Name.Contains("WebView2Runtime", StringComparison.OrdinalIgnoreCase))
                    return true;
                ex = ex.InnerException;
            }
            return false;
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Log.Error(e.Exception, "Unobserved task exception occurred");
            e.SetObserved(); // Prevent application crash
        }
    }
}



