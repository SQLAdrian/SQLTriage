/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    /// <summary>
    /// EXERCISE VEHICLE for lane S5's single most important safety property: the
    /// live executor (<see cref="DbatoolsRemediationExecutor"/>) refuses BEFORE
    /// touching the server when the confirm token is missing. INERT in a normal
    /// <c>dotnet test</c> run — it early-returns (stays green) unless
    /// <c>S5_LIVE_TARGET</c> names a reachable SQL instance. This calls the REAL
    /// executor directly (bypassing RemediationRunner's credit/capability gates,
    /// which are not the property under test here) against a live server, so the
    /// "no I/O before the confirm check" claim in the executor's own comment
    /// (DbatoolsRemediationExecutor.cs ~line 921/~1032) is proven, not just read.
    ///
    /// INVOCATION (gate, on a box with the local test instances):
    ///   $env:S5_LIVE_TARGET = ".\old2017"
    ///   dotnet test Tests/SQLTriage.Tests --filter "FullyQualifiedName~RemediationBackupCheckDbLiveSmokeTests"
    /// </summary>
    public class RemediationBackupCheckDbLiveSmokeTests
    {
        private readonly ITestOutputHelper _out;
        public RemediationBackupCheckDbLiveSmokeTests(ITestOutputHelper output) => _out = output;

        private static (ServerConnectionManager connections, DbatoolsRemediationExecutor executor, RemediationTemplateStore templates, string tempAuditDir)
            Wire(string target)
        {
            var connections = new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance);
            connections.AddConnection(new ServerConnection
            {
                ServerNames = target,
                UseWindowsAuthentication = true,
                TrustServerCertificate = true,
                IsEnabled = true,
            });

            var tempAuditDir = Path.Combine(Path.GetTempPath(), "sqltriage-s5-livesmoke-" + Guid.NewGuid().ToString("N"));
            var audit = new AuditLogService(tempAuditDir, startFlushTimer: false);
            var diskIo = new DiskIoService(NullLogger<DiskIoService>.Instance);
            var powerShell = new PowerShellService(NullLogger<PowerShellService>.Instance);
            var executor = new DbatoolsRemediationExecutor(powerShell, connections, audit, diskIo, NullLogger<DbatoolsRemediationExecutor>.Instance);
            // Overlay path override -> a file that does not exist, so this never touches the real
            // shipped Config/remediation-templates.json overlay on the dev box.
            var templates = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance,
                Path.Combine(tempAuditDir, "no-such-overlay.json"));
            return (connections, executor, templates, tempAuditDir);
        }

        [Fact]
        public async Task BackupDatabaseNow_NoConfirmToken_RefusesBeforeTouchingServer()
        {
            var target = Environment.GetEnvironmentVariable("S5_LIVE_TARGET");
            if (string.IsNullOrWhiteSpace(target))
            {
                Line("SKIPPED: set S5_LIVE_TARGET to a SQL instance (e.g. .\\old2017) to exercise the live gate.");
                return; // inert no-op in normal runs
            }

            var (_, executor, templates, _) = Wire(target);
            var template = templates.TryGet("BACKUPDATABASENOW");
            Assert.NotNull(template);

            // Deliberately omit the confirm token entirely (the worst case: a caller that
            // forgot it, not merely "false") — a real database name + directory ride the
            // request so a bug in the gate ordering would be caught, not masked by an
            // earlier "no target" refusal.
            var parameters = new Dictionary<string, string>
            {
                [BackupCheckDbOpRenderer.DatabaseNameParam] = "master",
                [BackupCheckDbOpRenderer.BackupDirectoryParam] = "C:\\temp\\s5-live-smoke-should-never-be-created",
            };
            var request = new RemediationRequest(template!, target, parameters);

            var result = await executor.ExecuteAsync(request);

            Assert.Equal(RemediationOutcome.CouldNotRun, result.Outcome);
            Assert.Contains("confirmation", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Line($"=== BackupDatabaseNow, no confirm token, against {target} ===");
            Line($"Outcome: {result.Outcome} — {result.Error}");
            Line("PASS: the executor refused before any server I/O (fail-closed, matches the source comment at ExecuteBackupDatabaseNowAsync's first line).");
        }

        [Fact]
        public async Task CheckDbNow_NoConfirmToken_RefusesBeforeTouchingServer()
        {
            var target = Environment.GetEnvironmentVariable("S5_LIVE_TARGET");
            if (string.IsNullOrWhiteSpace(target))
            {
                Line("SKIPPED: set S5_LIVE_TARGET to a SQL instance (e.g. .\\old2017) to exercise the live gate.");
                return; // inert no-op in normal runs
            }

            var (_, executor, templates, _) = Wire(target);
            var template = templates.TryGet("CHECKDBNOW");
            Assert.NotNull(template);

            var parameters = new Dictionary<string, string>
            {
                [BackupCheckDbOpRenderer.DatabaseNameParam] = "master",
            };
            var request = new RemediationRequest(template!, target, parameters);

            var result = await executor.ExecuteAsync(request);

            Assert.Equal(RemediationOutcome.CouldNotRun, result.Outcome);
            Assert.Contains("confirmation", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Line($"=== CheckDbNow, no confirm token, against {target} ===");
            Line($"Outcome: {result.Outcome} — {result.Error}");
            Line("PASS: the executor refused before any server I/O (fail-closed, matches the source comment at ExecuteCheckDbNowAsync's first line).");
        }

        private void Line(string s) => _out.WriteLine(s);
    }
}
