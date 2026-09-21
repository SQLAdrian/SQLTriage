/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Honesty hunt (remediation-safety lane, 2026-08-25), cluster 11 — remediation-r2-06.
    ///
    /// <para>The Backup-Now preview could not tell the operator why it refused. It looked up the
    /// BACKUP DIRECTORY's drive inline, swallowed that lookup's exception, and then printed the
    /// error text from ResolveDataDriveAsync — the DATABASE'S DATA-FILE drive, a different volume.
    /// When the data drive resolved fine there was no error text at all, so the refusal ended in a
    /// dangling sentence with no reason.</para>
    ///
    /// <para>Preview and apply now share one resolver,
    /// <c>DbatoolsRemediationExecutor.ResolveBackupDirectoryDriveAsync</c>, which names its own
    /// failure. These tests drive the REAL executor and touch no SQL Server: every case below is
    /// decided before any connection is opened. The reachable-server arm lives in
    /// <see cref="RemediationSafetyLiveSmokeTests"/>.</para>
    /// </summary>
    public class RemediationBackupDriveErrorTests
    {
        private static DbatoolsRemediationExecutor Wire(string? registeredServer, out string auditDir)
        {
            var connections = new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance);
            if (registeredServer is not null)
            {
                connections.AddConnection(new ServerConnection
                {
                    ServerNames = registeredServer,
                    UseWindowsAuthentication = true,
                    TrustServerCertificate = true,
                    IsEnabled = true,
                });
            }
            auditDir = Path.Combine(Path.GetTempPath(), "remsafe-drive-" + Guid.NewGuid().ToString("N"));
            return new DbatoolsRemediationExecutor(
                new PowerShellService(NullLogger<PowerShellService>.Instance),
                connections,
                new AuditLogService(auditDir, startFlushTimer: false),
                new DiskIoService(NullLogger<DiskIoService>.Instance),
                NullLogger<DbatoolsRemediationExecutor>.Instance);
        }

        [Theory]
        [InlineData(@"\\backupserver\sqlbackups")]   // UNC — no drive letter to measure
        [InlineData(@"backups\nightly")]             // relative path
        [InlineData(@"/var/opt/mssql/backup")]       // not a Windows drive path
        public async Task ADirectoryWithNoDriveLetter_IsNamedInTheRefusal(string directory)
        {
            var executor = Wire("SOME-SERVER", out _);

            var (drive, error) = await executor.ResolveBackupDirectoryDriveAsync("SOME-SERVER", directory, CancellationToken.None);

            Assert.Null(drive);                       // fails CLOSED — no reading means no permission
            Assert.NotNull(error);
            Assert.Contains(directory, error!, StringComparison.Ordinal);
            Assert.Contains("backup directory", error!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task AnUnregisteredServer_IsNamedInTheRefusal()
        {
            var executor = Wire(registeredServer: null, out _);

            var (drive, error) = await executor.ResolveBackupDirectoryDriveAsync("NO-SUCH-SERVER", @"D:\sqlbackups", CancellationToken.None);

            Assert.Null(drive);
            Assert.NotNull(error);
            Assert.Contains("NO-SUCH-SERVER", error!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TheRefusalNeverMentionsTheDataFileDrive()
        {
            // The defect in one assertion: the operator is fixing a BACKUP DIRECTORY problem, so
            // the reason must not be about the database's data files. ResolveDataDriveAsync is the
            // only producer of that phrasing, and this resolver must never reach for it.
            var executor = Wire("SOME-SERVER", out _);

            var (_, error) = await executor.ResolveBackupDirectoryDriveAsync("SOME-SERVER", @"\\backupserver\sqlbackups", CancellationToken.None);

            Assert.NotNull(error);
            Assert.DoesNotContain("data-file", error!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("data file", error!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("file inventory", error!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task EveryRefusalActuallyStatesAReason()
        {
            // The second arm of r2-06: when the data drive resolved fine there was NO reason at
            // all, and the refusal ended mid-sentence. A reason is now structural, not incidental.
            var executor = Wire("SOME-SERVER", out _);

            foreach (var directory in new[] { @"\\backupserver\share", @"relative\path", string.Empty })
            {
                var (drive, error) = await executor.ResolveBackupDirectoryDriveAsync("SOME-SERVER", directory, CancellationToken.None);
                Assert.Null(drive);
                Assert.False(string.IsNullOrWhiteSpace(error), $"No reason given for directory '{directory}'.");
            }
        }

        [Fact]
        public async Task TheDriveLetterCheckHappensBeforeAnyConnection()
        {
            // Proves the ordering the tests above rely on: a directory with no drive letter is
            // refused without a registered connection and therefore without touching a server.
            var executor = Wire(registeredServer: null, out _);

            var (drive, error) = await executor.ResolveBackupDirectoryDriveAsync("NO-SUCH-SERVER", @"\\backupserver\share", CancellationToken.None);

            Assert.Null(drive);
            Assert.NotNull(error);
            Assert.Contains("backupserver", error!, StringComparison.Ordinal);
            Assert.DoesNotContain("No connection registered", error!, StringComparison.Ordinal);
        }
    }
}
