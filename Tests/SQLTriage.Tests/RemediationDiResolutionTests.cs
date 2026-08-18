/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Remediation;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Guards the step-5 DI change: RemediationRunner is now registered as a real
    /// singleton, so its whole dependency graph (template store, capability,
    /// credit ledger, executor -> PowerShellService + IServerConnectionManager +
    /// AuditLogService) must be satisfiable. A unit test can't see a broken
    /// registration; this mirrors the registrations AddSharedServices declares for
    /// the lane and asserts the graph resolves.
    /// </summary>
    public class RemediationDiResolutionTests
    {
        [Fact]
        public void RemediationRunner_ResolvesWithItsFullDependencyGraph()
        {
            var auditDir = Path.Combine(Path.GetTempPath(), "rem-di-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(auditDir);
            try
            {
                var services = new ServiceCollection();
                services.AddLogging();

                // Lane dependencies (same shapes as AddSharedServices).
                services.AddSingleton<ServerConnectionManager>();
                services.AddSingleton<IServerConnectionManager>(sp => sp.GetRequiredService<ServerConnectionManager>());
                services.AddSingleton(_ => new AuditLogService(auditDir, startFlushTimer: false));
                services.AddSingleton<PowerShellService>();
                services.AddSingleton<RemediationTemplateStore>();
                // Mirror production: bundle-backed capability + persisted per-server ledger.
                services.AddSingleton<IBundleAccessor>(new FakeBundleAccessor());
                services.AddSingleton<IRemediationCapability, BundleBackedRemediationCapability>();
                services.AddSingleton<IRemediationCreditLedger>(sp => new PersistedRemediationCreditLedger(
                    sp.GetRequiredService<IBundleAccessor>(),
                    sp.GetRequiredService<ILogger<PersistedRemediationCreditLedger>>()));
                services.AddSingleton<IRemediationExecutor, DbatoolsRemediationExecutor>();
                services.AddSingleton<RemediationRunner>();

                using var sp = services.BuildServiceProvider(new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                });

                Assert.NotNull(sp.GetRequiredService<RemediationRunner>());
                Assert.NotNull(sp.GetRequiredService<IRemediationExecutor>());
            }
            finally
            {
                try { Directory.Delete(auditDir, recursive: true); } catch { }
            }
        }
    }
}
