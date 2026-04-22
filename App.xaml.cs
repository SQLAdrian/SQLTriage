/* In the name of God, the Merciful, the Compassionate */

using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            _ = OnStartupAsync(e);
        }

        private async Task OnStartupAsync(StartupEventArgs e)
        {
            // Set up global exception handling
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // Configure Serilog - Single consolidated log file
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
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information("Application starting...");

            // Check WebView2 runtime availability before proceeding
            var webView2Helper = new WebView2Helper();
            var webView2Status = await webView2Helper.CheckWebView2StatusAsync();

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

            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("config/appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            var services = new ServiceCollection();

            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog(dispose: true);
            });
            services.AddWpfBlazorWebView();

            // Register WebView2 helper for runtime detection
            services.AddSingleton<WebView2Helper>();

            // Register ServerConnectionManager first - it will be used by SqlServerConnectionFactory
            services.AddSingleton<ServerConnectionManager>();
            services.AddSingleton<IServerConnectionManager>(sp => sp.GetRequiredService<ServerConnectionManager>());
            services.AddSingleton<GlobalInstanceSelector>();

            // Register SQL Server connection - uses ServerConnectionManager for dynamic server selection
            var connStr = configuration.GetConnectionString("SqlServer") ?? "Server=.;Database=SQLWATCH;Integrated Security=true;";
            var trustServerCert = configuration.GetValue<bool>("TrustServerCertificate", false);

            // Create a temporary factory to get the registered ServerConnectionManager
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
            services.AddSingleton<QueryRegistry>();
            services.AddSingleton<IQueryOrchestrator, QueryOrchestrator>();
            services.AddMemoryCache();
            services.AddSingleton<ICacheHotTier, CacheHotTier>();
            services.AddSingleton<QueryExecutor>();
            services.AddScoped<DashboardDataService>();
            services.AddSingleton<AutoRefreshService>();
            services.AddSingleton<CheckRepositoryService>();
            services.AddSingleton<BPScriptService>();
            services.AddSingleton<DiagnosticScriptRunner>();
            services.AddSingleton<FullAuditStateService>();
            services.AddSingleton<AuditLogService>();
            services.AddSingleton<Data.Services.NotificationChannelService>();
            services.AddSingleton<AlertingService>();
            services.AddSingleton<Data.Services.AlertDefinitionService>();
            services.AddSingleton<Data.Services.AlertTemplateService>();
            services.AddSingleton<Data.Services.AlertHistoryService>();
            services.AddSingleton<Data.Services.AlertBaselineService>();
            services.AddSingleton<Data.Services.AlertEvaluationService>();

            // Scheduled Tasks
            services.AddSingleton<Data.Services.ScheduledTaskDefinitionService>();
            services.AddSingleton<Data.Services.ScheduledTaskHistoryService>();
            services.AddSingleton<Data.Services.ScheduledTaskEngine>();
            services.AddSingleton<HealthCheckService>();
            services.AddSingleton<CheckExecutionService>();
            services.AddSingleton<liveQueriesTableService>();
            services.AddSingleton<SessionManager>();
            services.AddSingleton<UserSettingsService>();
            services.AddSingleton<Data.Services.IUserSettingsService>(sp => sp.GetRequiredService<UserSettingsService>());
            services.AddSingleton<IChartThemeService, ChartThemeService>();
            services.AddSingleton<SessionDataService>();
            services.AddSingleton<ToastService>();
            services.AddSingleton<LogCleanupService>();
            services.AddSingleton<MemoryMonitorService>();
            services.AddSingleton<ConfigurationValidator>();
            services.AddSingleton<AutoUpdateService>();
            services.AddSingleton<DatabaseAvailabilityService>();
            services.AddSingleton<StartupService>();
            services.AddSingleton<Data.Services.PrintService>();
            services.AddSingleton<Data.Services.IPrintService>(sp => sp.GetRequiredService<Data.Services.PrintService>());
            services.AddSingleton<Data.Services.ConnectionHealthService>();
            services.AddSingleton<Data.Services.KeyboardShortcutService>();
            services.AddSingleton<Data.Services.SqlAssessmentService>();

            services.AddSingleton<Data.Services.ReportPageConfigService>();
            services.AddSingleton<Data.Services.XEventService>();
            services.AddSingleton<Data.Services.AdminAuthService>();
            services.AddSingleton<QuickCheckStateService>();
            services.AddSingleton<Data.Services.VulnerabilityAssessmentStateService>();

            // Radzen Blazor component library
            services.AddRadzenComponents();
            services.AddSingleton<Data.Services.ThemeService>();
            services.AddSingleton<Data.Services.ServerModeService>();
            services.AddSingleton<Data.Services.DataProtectionService>();
            services.AddSingleton<Data.Services.AzureBlobExportService>();
            services.AddSingleton<Data.Services.ProcessGuard>();
            services.AddSingleton<Data.Services.ForecastService>();
            services.AddSingleton<Data.Services.ProductionReadinessGate>();
            services.AddSingleton<Data.Services.RbacService>();
            // Scoped per Blazor circuit — holds current user's role (WPF=Admin, Server=from cookie)
            services.AddScoped<Data.Services.AppUserState>();

            // Local log service — thin wrapper over ILogger, routes through Serilog
            services.AddSingleton<LocalLogService>();
            services.AddSingleton<Data.Services.PowerShellService>();

            // liveQueries caching layer — delta-fetch + offline resilience
            // liveQueriesCacheStore uses DataProtectionService for at-rest encryption
            services.AddSingleton<liveQueriesCacheStore>();
            services.AddSingleton<CacheStateTracker>();
            services.AddSingleton<CachingQueryExecutor>();
            services.AddSingleton<CacheEvictionService>();
            services.AddSingleton<liveQueriesMaintenanceService>();
            services.AddSingleton<CacheMetricsService>();
            services.AddSingleton<CacheMetricsService>();

            Services = services.BuildServiceProvider();

            // Apply saved proxy to AutoUpdateService (before background check starts)
            var savedProxy = Services.GetService<UserSettingsService>()?.GetUpdateProxyUrl();
            if (!string.IsNullOrWhiteSpace(savedProxy))
                Services.GetService<AutoUpdateService>()?.SetManualProxyUrl(savedProxy);

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
            Services.GetService<LogCleanupService>()?.Start();

            // Start memory monitoring
            Services.GetService<MemoryMonitorService>();

            // Start cache eviction timer (runs every 5 minutes)
            Services.GetService<CacheEvictionService>()?.Start();

            // Start liveQueries maintenance timer (VACUUM + optimize, default every 4 hours)
            Services.GetService<liveQueriesMaintenanceService>()?.Start();

            // Start alert baseline service (aggressive seeding for first 5 min, then hourly recompute)
            _ = Task.Run(async () =>
            {
                var baseline = Services.GetService<Data.Services.AlertBaselineService>();
                if (baseline != null) await baseline.StartAsync();
            });

            // Start alert evaluation engine
            Services.GetService<Data.Services.AlertEvaluationService>()?.Start();

            // Start scheduled task engine
            Services.GetService<Data.Services.ScheduledTaskEngine>()?.Start();

            // Start connection health monitor (30s ping per enabled server)
            Services.GetService<Data.Services.ConnectionHealthService>()?.Start();

            // Log application start for audit trail
            var auditLog = Services.GetService<AuditLogService>();
            auditLog?.LogApplicationStart();
            Log.Information("Application started successfully");

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
                        await Task.Delay(2000); // brief delay — let connection pool warm up first
                        await sessionSvc.PrefetchAsync();
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

            // ── Flush SQLite WAL to prevent corruption on abrupt shutdown ──
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

            // ── Explicitly stop all background services before DI dispose ──
            // This prevents deadlocks from IAsyncDisposable services waiting
            // on active timer callbacks or Kestrel connections.
            StopBackgroundServices();

            // Dispose the DI container with a timeout to prevent hanging
            try
            {
                var disposeTask = Task.Run(() =>
                {
                    if (Services is IAsyncDisposable asyncDisposable)
                        asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
                Services?.GetService<Data.Services.ScheduledTaskEngine>()?.Stop();
                Services?.GetService<CacheEvictionService>()?.Stop();
                Services?.GetService<liveQueriesMaintenanceService>()?.Stop();
                Services?.GetService<MemoryMonitorService>()?.Dispose();
                Services?.GetService<LogCleanupService>()?.Dispose();
                Services?.GetService<AutoRefreshService>()?.Dispose();

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
