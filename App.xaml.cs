/* In the name of God, the Merciful, the Compassionate */

using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using SqlHealthAssessment.Data;
using SqlHealthAssessment.Data.Caching;
using SqlHealthAssessment.Data.Models;

namespace SqlHealthAssessment
{
    public partial class App : Application
    {
        public static IServiceProvider? Services { get; private set; }
        public static WebView2Helper? WebView2Helper { get; private set; }
        public static bool WebView2Available { get; private set; } = true;
        public static string? WebView2ErrorMessage { get; private set; }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Set up global exception handling
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // Configure Serilog - Single consolidated log file
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "SqlHealthAssessment")
                .Enrich.WithProperty("User", Environment.UserName)
                .Enrich.WithProperty("Machine", Environment.MachineName)
                .WriteTo.File(
                    path: "logs/app-.log",
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
            services.AddSingleton<QueryThrottleService>();
            services.AddSingleton<QueryExecutor>();
            services.AddScoped<DashboardDataService>();
            services.AddSingleton<AutoRefreshService>();
            services.AddSingleton<CheckRepositoryService>();
            services.AddSingleton<BPScriptService>();
            services.AddSingleton<DiagnosticScriptRunner>();
            services.AddSingleton<FullAuditStateService>();
            services.AddSingleton<AuditLogService>();
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
            services.AddSingleton<SqlConnectionPoolService>();
            services.AddSingleton<StartupService>();
            services.AddSingleton<Data.Services.PrintService>();
            services.AddSingleton<Data.Services.SqlAssessmentService>();
            services.AddSingleton<Data.Services.XEventService>();
            services.AddSingleton<Data.Services.AdminAuthService>();
            services.AddSingleton<QuickCheckStateService>();
            services.AddSingleton<Data.Services.VulnerabilityAssessmentStateService>();

            // Local log service — thin wrapper over ILogger, routes through Serilog
            services.AddSingleton<LocalLogService>();

            // liveQueries caching layer — delta-fetch + offline resilience
            services.AddSingleton<liveQueriesCacheStore>();
            services.AddSingleton<CacheStateTracker>();
            services.AddSingleton<CachingQueryExecutor>();
            services.AddSingleton<CacheEvictionService>();
            services.AddSingleton<liveQueriesMaintenanceService>();

            Services = services.BuildServiceProvider();

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

            // Log application start for audit trail
            var auditLog = Services.GetService<AuditLogService>();
            auditLog?.LogApplicationStart();
            Log.Information("Application started successfully");

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

                        System.Diagnostics.Debug.WriteLine(
                            $"[App] liveQueries table provisioning: {succeeded} OK, {failed} failed.");
                        foreach (var err in errors)
                            System.Diagnostics.Debug.WriteLine($"[App]   - {err}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[App] liveQueries table provisioning error: {ex.Message}");
                }
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Information("Application exiting...");
            Log.CloseAndFlush();
            base.OnExit(e);
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            Log.Fatal(ex, "Unhandled exception occurred. IsTerminating: {IsTerminating}", e.IsTerminating);
        }

        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error(e.Exception, "Unhandled dispatcher exception occurred");
            e.Handled = true; // Prevent application crash
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Log.Error(e.Exception, "Unobserved task exception occurred");
            e.SetObserved(); // Prevent application crash
        }
    }
}
