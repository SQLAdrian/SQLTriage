/* In the name of God, the Merciful, the Compassionate */

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Radzen;
using Serilog;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SQLTriage.Data.Services
{
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

                // Resolve the source project wwwroot (not in build output for Blazor Hybrid)
                var manifestPath = Path.Combine(AppContext.BaseDirectory, "SQLTriage.staticwebassets.runtime.json");
                string webRoot;
                if (File.Exists(manifestPath))
                {
                    // Dev: read first ContentRoot from the manifest (points to source wwwroot)
                    var json = File.ReadAllText(manifestPath);
                    var startIdx = json.IndexOf("\"ContentRoots\":[\"") + "\"ContentRoots\":[\"".Length;
                    var endIdx = json.IndexOf("\"", startIdx);
                    webRoot = json.Substring(startIdx, endIdx - startIdx).Replace("\\\\", "\\").TrimEnd('\\');
                }
                else
                {
                    webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                }

                _logger.LogInformation("Server mode config: WebRoot={WebRoot}, Exists={Exists}, Port={Port}",
                    webRoot, Directory.Exists(webRoot), Port);

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

                    // HTTPS with ephemeral self-signed certificate
                    if (EnableHttps)
                    {
                        try
                        {
                            HttpsPort = FindAvailablePort(HttpsPort);
                            _selfSignedCert = GenerateSelfSignedCertificate();
                            kestrel.ListenAnyIP(HttpsPort, listenOptions =>
                            {
                                listenOptions.UseHttps(_selfSignedCert);
                            });
                            _logger.LogInformation("Kestrel configured to listen on HTTPS port {Port} with ephemeral self-signed certificate", HttpsPort);
                        }
                        catch (Exception certEx)
                        {
                            _logger.LogWarning(certEx, "Failed to configure HTTPS — continuing with HTTP only");
                        }
                    }
                });

                // Add Blazor Server services
                builder.Services.AddRazorComponents()
                    .AddInteractiveServerComponents();
                builder.Services.AddRadzenComponents();

                // Logging — reuse Serilog
                builder.Services.AddLogging(lb =>
                {
                    lb.ClearProviders();
                    lb.AddSerilog(dispose: false);
                });

                // Share all singleton services from the WPF container
                var wpf = App.Services!;
                RegisterSharedSingletons(builder.Services, wpf);

                // Configure OAuth authentication if RBAC is enabled
                var rbac = wpf.GetService<RbacService>();
                if (rbac?.Config.Enabled == true)
                {
                    ConfigureAuthentication(builder.Services, rbac.Config, Port);
                }

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

                // Health check endpoint — verifies Kestrel is responding
                app.MapGet("/_server/health", () => Results.Ok(new
                {
                    status = "ok",
                    webRoot = app.Environment.WebRootPath,
                    webRootExists = Directory.Exists(app.Environment.WebRootPath),
                    contentRoot = app.Environment.ContentRootPath
                }));

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

                app.UseStaticFiles();
                app.UseRouting();

                // API key authentication for /api/* routes
                app.UseApiKeyAuth();

                // OAuth authentication for browser-based access
                if (rbac?.Config.Enabled == true)
                {
                    app.UseAuthentication();
                    app.UseAuthorization();
                    MapAuthEndpoints(app, rbac);
                }

                app.UseAntiforgery();

                // REST API endpoints for RMM/PSA integration
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

        private static void RegisterSharedSingletons(IServiceCollection services, IServiceProvider wpf)
        {
            // Forward all known singletons by resolved instance
            TryAdd<Microsoft.Extensions.Configuration.IConfiguration>(services, wpf);
            TryAdd<ServerConnectionManager>(services, wpf);
            TryAdd<GlobalInstanceSelector>(services, wpf);
            TryAdd<WebView2Helper>(services, wpf);
            TryAdd<AutoUpdateService>(services, wpf);
            TryAdd<UserSettingsService>(services, wpf);
            TryAdd<KeyboardShortcutService>(services, wpf);
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
            TryAdd<SqlConnectionPoolService>(services, wpf);
            TryAdd<ResilienceService>(services, wpf);
            TryAdd<QueryThrottleService>(services, wpf);
            TryAdd<CheckRepositoryService>(services, wpf);
            TryAdd<CheckExecutionService>(services, wpf);
            TryAdd<DiagnosticScriptRunner>(services, wpf);
            TryAdd<FullAuditStateService>(services, wpf);
            TryAdd<ToastService>(services, wpf);
            TryAdd<PrintService>(services, wpf);
            TryAdd<SqlAssessmentService>(services, wpf);

            TryAdd<ReportPageConfigService>(services, wpf);
            TryAdd<XEventService>(services, wpf);
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

            // Scoped services — each browser tab/circuit gets its own instance
            services.AddScoped<DashboardDataService>();
            services.AddScoped<AppUserState>();

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

        /// <summary>
        /// Generates an ephemeral self-signed X.509 certificate for HTTPS.
        /// The certificate is created in memory (never written to disk) and is valid
        /// for 365 days. A new certificate is generated on each server start,
        /// ensuring key material doesn't persist between sessions.
        /// </summary>
        private X509Certificate2 GenerateSelfSignedCertificate()
        {
            var hostName = Dns.GetHostName();
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName(hostName);
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddIpAddress(IPAddress.Loopback);
            sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);

            // Add all local IP addresses
            try
            {
                var hostEntry = Dns.GetHostEntry(hostName);
                foreach (var addr in hostEntry.AddressList)
                    sanBuilder.AddIpAddress(addr);
            }
            catch { /* Best effort */ }

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN=SQLTriage ({hostName})",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: false));

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Authentication
                    critical: false));

            request.CertificateExtensions.Add(sanBuilder.Build());

            var cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),  // Small backdate to avoid clock skew
                DateTimeOffset.UtcNow.AddDays(365));

            // Export and re-import to get an exportable key (required by Kestrel on Windows)
            var pfxBytes = cert.Export(X509ContentType.Pfx);
            var result = new X509Certificate2(pfxBytes, (string?)null, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet);
            CryptographicOperations.ZeroMemory(pfxBytes);

            _logger.LogInformation("Generated ephemeral self-signed certificate: Subject={Subject}, Thumbprint={Thumbprint}, Expiry={Expiry}",
                result.Subject, result.Thumbprint, result.NotAfter);

            return result;
        }

        /// <summary>
        /// Configures cookie + OAuth authentication services for Server Mode.
        /// </summary>
        private static void ConfigureAuthentication(IServiceCollection services, RbacConfig config, int port)
        {
            var authBuilder = services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.LoginPath = "/auth/login";
                options.LogoutPath = "/auth/logout";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
            });

            if (config.Google.Enabled && !string.IsNullOrEmpty(config.Google.ClientId))
            {
                authBuilder.AddGoogle(options =>
                {
                    options.ClientId = config.Google.ClientId;
                    options.ClientSecret = CredentialProtector.Decrypt(config.Google.ClientSecret);
                    options.CallbackPath = "/auth/callback/google";
                });
            }

            if (config.Microsoft.Enabled && !string.IsNullOrEmpty(config.Microsoft.ClientId))
            {
                authBuilder.AddMicrosoftAccount(options =>
                {
                    options.ClientId = config.Microsoft.ClientId;
                    options.ClientSecret = CredentialProtector.Decrypt(config.Microsoft.ClientSecret);
                    options.CallbackPath = "/auth/callback/microsoft";
                });
            }

            services.AddAuthorization();
        }

        /// <summary>
        /// Maps login/logout/callback endpoints for OAuth flows.
        /// </summary>
        private static void MapAuthEndpoints(WebApplication app, RbacService rbac)
        {
            // Login page — shows available providers
            app.MapGet("/auth/login", (HttpContext ctx) =>
            {
                var config = rbac.Config;
                var providers = new List<string>();
                if (config.Google.Enabled) providers.Add("<a href='/auth/challenge/google'>Sign in with Google</a>");
                if (config.Microsoft.Enabled) providers.Add("<a href='/auth/challenge/microsoft'>Sign in with Microsoft</a>");

                var html = $@"<!DOCTYPE html>
<html><head><title>Sign In</title></head>
<body style='background:#1a1a2e;color:#eee;font-family:Consolas;padding:40px;text-align:center'>
<h1>SQLTriage</h1>
<h2>Sign In</h2>
<div style='margin:20px'>{string.Join("<br/><br/>", providers)}</div>
</body></html>";
                return Results.Content(html, "text/html");
            });

            // Challenge — redirects to OAuth provider
            app.MapGet("/auth/challenge/{provider}", (string provider, HttpContext ctx) =>
            {
                var scheme = provider.ToLower() switch
                {
                    "google" => GoogleDefaults.AuthenticationScheme,
                    "microsoft" => MicrosoftAccountDefaults.AuthenticationScheme,
                    _ => null
                };
                if (scheme == null) return Results.BadRequest("Unknown provider");

                return Results.Challenge(new AuthenticationProperties { RedirectUri = "/auth/complete" }, new[] { scheme });
            });

            // Completion — processes the OAuth callback and creates the session
            app.MapGet("/auth/complete", async (HttpContext ctx) =>
            {
                var result = await ctx.AuthenticateAsync();
                if (!result.Succeeded)
                    return Results.Redirect("/auth/login?error=auth_failed");

                var email = result.Principal?.FindFirstValue(ClaimTypes.Email) ?? "";
                var name = result.Principal?.FindFirstValue(ClaimTypes.Name) ?? email;
                var provider = result.Principal?.Identity?.AuthenticationType ?? "unknown";

                if (string.IsNullOrEmpty(email))
                    return Results.Redirect("/auth/login?error=no_email");

                // Check domain restriction
                var config = rbac.Config;
                var providerConfig = provider.Contains("Google", StringComparison.OrdinalIgnoreCase)
                    ? config.Google : config.Microsoft;
                if (!string.IsNullOrEmpty(providerConfig.AllowedDomain))
                {
                    var domain = email.Split('@').LastOrDefault() ?? "";
                    if (!domain.Equals(providerConfig.AllowedDomain, StringComparison.OrdinalIgnoreCase))
                        return Results.Redirect("/auth/login?error=domain_restricted");
                }

                // Record login and get role
                var user = rbac.RecordLogin(email, name, provider);
                if (user == null)
                    return Results.Redirect("/auth/login?error=access_denied");

                // Create claims principal with role
                var claims = new List<Claim>
                {
                    new(ClaimTypes.Email, email),
                    new(ClaimTypes.Name, name),
                    new(ClaimTypes.Role, user.Role),
                    new("provider", provider)
                };
                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                await ctx.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(identity));

                return Results.Redirect("/");
            });

            // Logout
            app.MapGet("/auth/logout", async (HttpContext ctx) =>
            {
                await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Redirect("/auth/login");
            });

            // Current user info (for Blazor UI)
            app.MapGet("/auth/me", (HttpContext ctx) =>
            {
                if (ctx.User.Identity?.IsAuthenticated != true)
                    return Results.Json(new { authenticated = false });

                return Results.Json(new
                {
                    authenticated = true,
                    email = ctx.User.FindFirstValue(ClaimTypes.Email),
                    name = ctx.User.FindFirstValue(ClaimTypes.Name),
                    role = ctx.User.FindFirstValue(ClaimTypes.Role),
                    provider = ctx.User.FindFirstValue("provider")
                });
            });
        }

        private static int FindAvailablePort(int preferred)
        {
            for (int port = preferred; port < preferred + 20; port++)
            {
                try
                {
                    // Check both loopback and any — Kestrel binds 0.0.0.0
                    using var listener = new TcpListener(IPAddress.Any, port);
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
