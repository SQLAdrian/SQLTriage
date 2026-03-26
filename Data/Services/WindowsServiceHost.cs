/* In the name of God, the Merciful, the Compassionate */

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Radzen;
using Serilog;
using SqlHealthAssessment.Data.Caching;
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

namespace SqlHealthAssessment.Data.Services
{
    /// <summary>
    /// Runs the Blazor Server UI as a headless Windows Service (no WPF).
    /// Shares the same service registration as the WPF app.
    /// </summary>
    public class WindowsServiceHost
    {
        public const string ServiceName = "SqlHealthAssessment";
        public const string ServiceDisplayName = "SQL Health Assessment Server";
        public const string ServiceDescription = "SQL Health Assessment — Blazor Server monitoring dashboard";

        public static void Run(string[] args)
        {
            // Handle install/uninstall commands
            if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
            {
                InstallService(args);
                return;
            }
            if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
            {
                UninstallService();
                return;
            }

            // Run as Windows Service or console
            RunServer(args);
        }

        private static void RunServer(string[] args)
        {
            // Configure Serilog for service mode
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "SqlHealthAssessment.Service")
                .Enrich.WithProperty("User", Environment.UserName)
                .Enrich.WithProperty("Machine", Environment.MachineName)
                .WriteTo.File(
                    path: Path.Combine(AppContext.BaseDirectory, "logs", "service-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information("SQL Health Assessment Service starting...");

            try
            {
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("config/appsettings.json", optional: false, reloadOnChange: true)
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
                try { builder.WebHost.UseStaticWebAssets(); } catch { }

                // Configure Kestrel — HTTP + HTTPS with ephemeral self-signed cert
                var httpsPort = FindAvailablePort(port + 1);
                X509Certificate2? selfSignedCert = null;
                builder.WebHost.ConfigureKestrel(kestrel =>
                {
                    kestrel.ListenAnyIP(port);

                    try
                    {
                        selfSignedCert = GenerateSelfSignedCertificate();
                        kestrel.ListenAnyIP(httpsPort, listenOptions =>
                        {
                            listenOptions.UseHttps(selfSignedCert);
                        });
                        Log.Information("Service HTTPS configured on port {HttpsPort} with ephemeral certificate", httpsPort);
                    }
                    catch (Exception certEx)
                    {
                        Log.Warning(certEx, "Failed to configure HTTPS for service — HTTP only");
                    }
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

                // Register all app services (same as App.xaml.cs)
                RegisterAllServices(builder.Services, configuration);

                var app = builder.Build();

                app.UseDeveloperExceptionPage();
                app.UseStaticFiles();
                app.UseRouting();
                app.UseAntiforgery();

                // Health endpoint
                app.MapGet("/_server/health", () => Results.Ok(new
                {
                    status = "ok",
                    mode = "service",
                    port,
                    httpsPort = selfSignedCert != null ? httpsPort : (int?)null,
                    httpsEnabled = selfSignedCert != null,
                    webRoot,
                    machineName = Environment.MachineName
                }));

                app.MapRazorComponents<Components.ServerApp>()
                    .AddInteractiveServerRenderMode();

                // Initialize background services
                InitializeBackgroundServices(app.Services);

                Log.Information("SQL Health Assessment Service started on port {Port}" + (selfSignedCert != null ? " (HTTPS: {HttpsPort})" : ""),
                    port, selfSignedCert != null ? httpsPort : 0);
                app.Run();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "SQL Health Assessment Service failed to start");
            }
            finally
            {
                Log.Information("SQL Health Assessment Service stopped");
                Log.CloseAndFlush();
            }
        }

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
            services.AddSingleton<QueryThrottleService>();
            services.AddSingleton<QueryExecutor>();
            services.AddScoped<DashboardDataService>();
            services.AddSingleton<AutoRefreshService>();
            services.AddSingleton<CheckRepositoryService>();
            services.AddSingleton<BPScriptService>();
            services.AddSingleton<DiagnosticScriptRunner>();
            services.AddSingleton<FullAuditStateService>();
            services.AddSingleton<AuditLogService>();
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
            services.AddSingleton<SqlConnectionPoolService>();
            services.AddSingleton<PrintService>();
            services.AddSingleton<SqlAssessmentService>();

            services.AddSingleton<ReportPageConfigService>();
            services.AddSingleton<XEventService>();
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

            // Caching layer
            services.AddSingleton<liveQueriesCacheStore>();
            services.AddSingleton<CacheStateTracker>();
            services.AddSingleton<CachingQueryExecutor>();
            services.AddSingleton<CacheEvictionService>();
            services.AddSingleton<liveQueriesMaintenanceService>();

            // WebView2Helper stub for service mode (not used but injected by some components)
            services.AddSingleton<WebView2Helper>();
        }

        private static void InitializeBackgroundServices(IServiceProvider services)
        {
            services.GetService<LogCleanupService>()?.Start();
            services.GetService<MemoryMonitorService>();
            services.GetService<CacheEvictionService>()?.Start();
            services.GetService<liveQueriesMaintenanceService>()?.Start();
            services.GetService<AuditLogService>()?.LogApplicationStart();
        }

        private static string ResolveWebRoot()
        {
            var manifestPath = Path.Combine(AppContext.BaseDirectory, "SqlHealthAssessment.staticwebassets.runtime.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    var json = File.ReadAllText(manifestPath);
                    var startIdx = json.IndexOf("\"ContentRoots\":[\"") + "\"ContentRoots\":[\"".Length;
                    var endIdx = json.IndexOf("\"", startIdx);
                    return json.Substring(startIdx, endIdx - startIdx).Replace("\\\\", "\\").TrimEnd('\\');
                }
                catch { }
            }
            return Path.Combine(AppContext.BaseDirectory, "wwwroot");
        }

        private static X509Certificate2 GenerateSelfSignedCertificate()
        {
            var hostName = Dns.GetHostName();
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName(hostName);
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddIpAddress(IPAddress.Loopback);
            sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);

            try
            {
                var hostEntry = Dns.GetHostEntry(hostName);
                foreach (var addr in hostEntry.AddressList)
                    sanBuilder.AddIpAddress(addr);
            }
            catch { }

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN=SQL Health Assessment Service ({hostName})",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: false));

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                    critical: false));

            request.CertificateExtensions.Add(sanBuilder.Build());

            var cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddDays(365));

            var pfxBytes = cert.Export(X509ContentType.Pfx);
            var result = new X509Certificate2(pfxBytes, (string?)null, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet);
            CryptographicOperations.ZeroMemory(pfxBytes);

            Log.Information("Generated ephemeral self-signed certificate: Thumbprint={Thumbprint}", result.Thumbprint);
            return result;
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
                catch { }
            }
            using var tmp = new TcpListener(IPAddress.Loopback, 0);
            tmp.Start();
            int p = ((IPEndPoint)tmp.LocalEndpoint).Port;
            tmp.Stop();
            return p;
        }

        #region Service Install / Uninstall

        public static void InstallService(string[] args)
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "SqlHealthAssessment.exe");

            // Parse optional service account
            string? username = null;
            string? password = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--username", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    username = args[++i];
                if (args[i].Equals("--password", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    password = args[++i];
            }

            // Build sc create command
            var binPath = $"\"{exePath}\" --service";
            var scArgs = $"create \"{ServiceName}\" binPath= \"{binPath}\" start= auto DisplayName= \"{ServiceDisplayName}\"";

            if (!string.IsNullOrEmpty(username))
            {
                scArgs += $" obj= \"{username}\"";
                if (!string.IsNullOrEmpty(password))
                    scArgs += $" password= \"{password}\"";
            }

            Console.WriteLine($"Installing service: {ServiceDisplayName}");
            var result = RunScCommand(scArgs);

            if (result == 0)
            {
                // Set description
                RunScCommand($"description \"{ServiceName}\" \"{ServiceDescription}\"");

                // Configure failure recovery (restart after 60s)
                RunScCommand($"failure \"{ServiceName}\" reset= 86400 actions= restart/60000/restart/60000/restart/60000");

                Console.WriteLine("Service installed successfully.");
                Console.WriteLine($"  Name: {ServiceName}");
                Console.WriteLine($"  Path: {binPath}");
                if (!string.IsNullOrEmpty(username))
                    Console.WriteLine($"  Account: {username}");
                Console.WriteLine("\nStart with: sc start SqlHealthAssessment");
            }
            else
            {
                Console.WriteLine("Failed to install service. Run as Administrator.");
            }
        }

        public static void UninstallService()
        {
            Console.WriteLine($"Stopping service: {ServiceName}...");
            RunScCommand($"stop \"{ServiceName}\"");
            Thread.Sleep(2000);

            Console.WriteLine($"Removing service: {ServiceName}...");
            var result = RunScCommand($"delete \"{ServiceName}\"");

            if (result == 0)
                Console.WriteLine("Service uninstalled successfully.");
            else
                Console.WriteLine("Failed to uninstall service. Run as Administrator.");
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
                catch { }

                return (true, running, account);
            }
            catch
            {
                return (false, false, null);
            }
        }

        private static int RunScCommand(string arguments)
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
                proc?.WaitForExit(30000);
                return proc?.ExitCode ?? -1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return -1;
            }
        }

        #endregion
    }
}
