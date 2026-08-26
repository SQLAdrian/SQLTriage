/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Regression for the 2026-08-11 incident: at the first start of build 3511,
    /// <see cref="LogCleanupService"/> swept .\audit-logs by last-write time and deleted the audit
    /// signing key, its archive, and a 492 KB evidence segment. The audit service then minted a
    /// fresh key by first-mint and 469 chain entries became unverifiable.
    ///
    /// <para>These tests drive the REAL <see cref="LogCleanupService"/>. Nothing here re-implements
    /// the sweep loop or mocks the filesystem: the fixture is written to a real temporary base
    /// directory, ages are set with real <see cref="File.SetLastWriteTime(string, DateTime)"/>, and
    /// the assertions read the files back off disk. The only seam is the internal base-directory
    /// override, which replaces the value production reads from
    /// <c>AppDomain.CurrentDomain.BaseDirectory</c> and changes nothing else about the sweep.</para>
    ///
    /// <para>MUTATION-PROVED 2026-08-11: with "audit-logs" restored to
    /// <c>LogCleanupService._directories</c>, <see cref="AuditLogsDirectoryIsNeverSwept"/> fails and
    /// names the eaten file. The 8-day ages below are what makes that so, and they are not
    /// arbitrary: every file in a real audit-logs directory ages past the 7-day cutoff by design,
    /// because segments roll and keys freeze at their last write.</para>
    /// </summary>
    public class LogCleanupAuditBoundaryTests : IDisposable
    {
        private readonly List<string> _dirs = new();

        /// <summary>Older than the service's 7-day cutoff, by a margin no clock skew closes.</summary>
        private static readonly DateTime EightDaysOld = DateTime.Now.AddDays(-8);

        private string NewBaseDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "logcleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _dirs.Add(dir);
            return dir;
        }

        public void Dispose()
        {
            foreach (var dir in _dirs)
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* test cleanup; ignore */ }
            }
            GC.SuppressFinalize(this);
        }

        private static string WriteAged(string dir, string name, byte[] bytes, DateTime lastWrite)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTime(path, lastWrite);
            return path;
        }

        private static string Sha256Of(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        private static LogCleanupService Service(string baseDir) =>
            new(NullLogger<LogCleanupService>.Instance, baseDir);

        /// <summary>
        /// The four shapes the incident actually destroyed or could have destroyed, all aged past
        /// the cutoff: an evidence segment, the live signing key, an archived key, and a dot-marker
        /// (the <c>.chain-anchor</c> class of file). Every one must survive byte-identical.
        /// </summary>
        [Fact]
        public void AuditLogsDirectoryIsNeverSwept()
        {
            var baseDir = NewBaseDir();
            var auditDir = Path.Combine(baseDir, "audit-logs");

            var segment = WriteAged(auditDir, "audit-2026-07-31.jsonl",
                System.Text.Encoding.UTF8.GetBytes("{\"Id\":1,\"Message\":\"evidence\"}\n"), EightDaysOld);
            var key = WriteAged(auditDir, "hmac.key", RandomNumberGenerator.GetBytes(32), EightDaysOld);
            var keyArchive = WriteAged(auditDir, "hmac.key.72D832D61F2D1E14",
                RandomNumberGenerator.GetBytes(32), EightDaysOld);
            var marker = WriteAged(auditDir, ".chain-anchor",
                System.Text.Encoding.UTF8.GetBytes("anchor\n"), EightDaysOld);

            var expected = new[] { segment, key, keyArchive, marker }
                .ToDictionary(p => p, Sha256Of);

            Service(baseDir).Cleanup();

            foreach (var (path, hash) in expected)
            {
                Assert.True(File.Exists(path),
                    $"LogCleanupService deleted {Path.GetFileName(path)} from audit-logs. " +
                    "That directory is off limits to this sweep: it is how the 2026-08-11 " +
                    "incident destroyed the signing key and 469 entries' verifiability.");
                Assert.Equal(hash, Sha256Of(path));
            }

            // Nothing was added or renamed either, so "survived" means the directory is untouched.
            Assert.Equal(4, Directory.GetFiles(auditDir, "*", SearchOption.AllDirectories).Length);
        }

        /// <summary>
        /// The other half of the boundary: removing audit-logs must not have disabled the sweep the
        /// service exists for. A stale service log still goes; a fresh one still stays.
        /// </summary>
        [Fact]
        public void ServiceLogsKeepTheirSevenDaySweep()
        {
            var baseDir = NewBaseDir();
            var logsDir = Path.Combine(baseDir, "logs");

            var stale = WriteAged(logsDir, "sqltriage-20260803.log",
                System.Text.Encoding.UTF8.GetBytes("old service log\n"), EightDaysOld);
            var fresh = WriteAged(logsDir, "sqltriage-20260811.log",
                System.Text.Encoding.UTF8.GetBytes("today's service log\n"), DateTime.Now);
            var freshHash = Sha256Of(fresh);

            Service(baseDir).Cleanup();

            Assert.False(File.Exists(stale), "the 8-day-old service log should have been swept");
            Assert.True(File.Exists(fresh), "the fresh service log should have survived");
            Assert.Equal(freshHash, Sha256Of(fresh));
        }

        /// <summary>
        /// Both directories in one base, which is the production shape. Proves the sweep discriminates
        /// by directory rather than by anything about the individual files: the audit segment and the
        /// service log here are the same age and only one of them goes.
        /// </summary>
        [Fact]
        public void SweepDiscriminatesByDirectoryNotByFileAge()
        {
            var baseDir = NewBaseDir();
            var auditDir = Path.Combine(baseDir, "audit-logs");
            var logsDir = Path.Combine(baseDir, "logs");

            var auditSegment = WriteAged(auditDir, "audit-2026-07-31.jsonl",
                System.Text.Encoding.UTF8.GetBytes("{\"Id\":1}\n"), EightDaysOld);
            var staleLog = WriteAged(logsDir, "sqltriage-20260803.log",
                System.Text.Encoding.UTF8.GetBytes("old\n"), EightDaysOld);

            Service(baseDir).Cleanup();

            Assert.True(File.Exists(auditSegment), "the audit segment should have survived");
            Assert.False(File.Exists(staleLog), "the equally old service log should have been swept");
        }

        /// <summary>
        /// The seam does not move production. The public constructor is the one dependency injection
        /// resolves, and <see cref="LogCleanupService.EffectiveBaseDirectory"/> is the single
        /// expression the sweep reads, so this asserts the production path rather than a copy.
        /// </summary>
        [Fact]
        public void PublicConstructorStillSweepsTheApplicationBaseDirectory()
        {
            var production = new LogCleanupService(NullLogger<LogCleanupService>.Instance);

            Assert.Equal(AppDomain.CurrentDomain.BaseDirectory, production.EffectiveBaseDirectory);
        }
    }
}
