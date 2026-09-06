/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests.Cli
{
    /// <summary>
    /// Item 1's DI-feasibility question, PROVEN rather than merely read: does
    /// <c>--report audit-evidence</c>'s dependency graph (<see cref="ReportBundleService"/>,
    /// <see cref="SqlAssessmentService"/>, <see cref="VulnerabilityAssessmentStateService"/>, and
    /// everything they in turn require) construct headlessly through the SAME
    /// <see cref="WindowsServiceHost.RegisterAllServices"/> call
    /// <see cref="SQLTriage.Cli.CliAuditHost"/> makes — no Blazor circuit, no JSInterop, no
    /// NavigationManager?
    ///
    /// This resolves the WHOLE graph (every constructor on the path runs) against an in-memory,
    /// file-free <see cref="IConfiguration"/> — no <c>config/appsettings.json</c> needed, since
    /// every config read in <see cref="ServiceCollectionExtensions.AddSharedServices"/> and
    /// <see cref="WindowsServiceHost.RegisterAllServices"/> on this path has a literal default.
    /// It never opens a SQL connection (every constructor on the path is field assignment only —
    /// read, not asserted by magic: this test is what turns that reading into a proof), so it
    /// stays a build-time/DI check, not a live probe.
    /// </summary>
    public class CliAuditEvidenceDiFeasibilityTests : IDisposable
    {
        private readonly string _tempDir;

        public CliAuditEvidenceDiFeasibilityTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "cli-audit-evidence-di-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }

        [Fact]
        public void The_audit_evidence_dependency_graph_resolves_through_the_CLI_composition_root()
        {
            // Same shape as CliAuditHost.RunAsync: an empty/default IConfiguration (no
            // appsettings.json dependency here — see class doc) fed to the exact registration
            // method the CLI, the WPF app, and the Kestrel service all share.
            IConfiguration configuration = new ConfigurationBuilder().Build();

            var services = new ServiceCollection();
            services.AddSingleton(configuration);
            services.AddLogging(b => b.AddProvider(NullLoggerProviderStub.Instance));

            WindowsServiceHost.RegisterAllServices(services, configuration);

            // RegisterAllServices registers UserSettingsService with its parameterless ctor, which
            // binds the REAL %APPDATA%\SQLTriage\user-settings.json — RealUserProfileGuard refuses
            // that under a detected test host (by design: 2026-08-04, this suite once wrote a
            // fixture licence into the operator's real settings file). This override is TEST-ONLY
            // plumbing for that guard; the real CLI process is not a test host, so production
            // --report audit-evidence hits the parameterless ctor exactly as registered. DI resolves
            // the LAST registration for a type, so this wins over RegisterAllServices' own.
            services.AddSingleton(new UserSettingsService(Path.Combine(_tempDir, "user-settings.json")));

            using var provider = services.BuildServiceProvider();

            // Resolving ReportBundleService pulls its whole constructor graph (ExecutiveHealthService,
            // HealthCheckService, VulnerabilityAssessmentStateService, UserSettingsService,
            // CheckRepositoryService, OwnerAssignmentStore, AuditLogService, CheckExecutionService) —
            // if ANY of them needed a Blazor circuit or a live SQL connection at construction time,
            // this throws right here.
            var reportSvc = provider.GetRequiredService<ReportBundleService>();
            Assert.NotNull(reportSvc);

            // The engine --report audit-evidence drives (Pages/VulnerabilityAssessment.razor.cs's
            // own dependency) — same headless proof.
            var assessmentSvc = provider.GetRequiredService<SqlAssessmentService>();
            Assert.NotNull(assessmentSvc);

            var vaState = provider.GetRequiredService<VulnerabilityAssessmentStateService>();
            Assert.NotNull(vaState);
            Assert.False(vaState.HasRun, "a freshly-resolved singleton must start with no scan recorded.");

            // AuditLogService resolves via its IConfiguration constructor (the only one DI can
            // satisfy) — Path.Combine(AppContext.BaseDirectory, "audit-logs"), the SAME directory
            // the installed app/service use, because every host runs the same exe from the same
            // install location. Confirms no separate audit-log wiring is needed for the CLI.
            var auditLog = provider.GetRequiredService<AuditLogService>();
            Assert.NotNull(auditLog);
        }

        /// <summary>Minimal no-op logger provider — this test asserts DI construction, not logging.</summary>
        private sealed class NullLoggerProviderStub : ILoggerProvider
        {
            public static readonly NullLoggerProviderStub Instance = new();
            public ILogger CreateLogger(string categoryName) => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            public void Dispose() { }
        }
    }
}
