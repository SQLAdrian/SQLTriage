/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using SQLTriage.Cli;
using Xunit;

namespace SQLTriage.Tests.Cli
{
    /// <summary>
    /// The 2026-07-19 additions to <see cref="AuditCliArgs.Parse"/>: --help / --version (which
    /// short-circuit BEFORE validation, so they answer even on an otherwise-broken command line)
    /// and --concurrency (rejected out of range rather than clamped).
    ///
    /// Kept separate from AuditCliArgsTests only to avoid editing that file concurrently; the
    /// bundle-in-a-private-temp-file discipline is copied from it — the ambient
    /// AppContext.BaseDirectory/Config/free-bundle.dat is shared mutable state under a parallel
    /// run.
    /// </summary>
    public class AuditCliArgsHelpAndConcurrencyTests : IDisposable
    {
        private readonly string _bundlePath =
            Path.Combine(Path.GetTempPath(), "sqlt-cli-hc-bundle-" + Guid.NewGuid().ToString("N") + ".dat");

        private readonly string _outDir =
            Path.Combine(Path.GetTempPath(), "sqlt-cli-hc-out-" + Guid.NewGuid().ToString("N"));

        public AuditCliArgsHelpAndConcurrencyTests() => File.WriteAllBytes(_bundlePath, new byte[] { 1, 2, 3 });

        public void Dispose()
        {
            try { File.Delete(_bundlePath); } catch { /* best-effort */ }
            try { if (Directory.Exists(_outDir)) Directory.Delete(_outDir, true); } catch { /* best-effort */ }
        }

        private string[] Base(params string[] extra)
        {
            var head = new[] { "--audit", "--servers", ".\\old2017", "--bundle", _bundlePath, "--out", _outDir };
            var all = new string[head.Length + extra.Length];
            Array.Copy(head, all, head.Length);
            Array.Copy(extra, 0, all, head.Length, extra.Length);
            return all;
        }

        [Theory]
        [InlineData("--help")]
        [InlineData("-h")]
        [InlineData("-?")]
        [InlineData("/?")]
        public void Parse_HelpForms_RequestHelp(string flag)
        {
            var result = AuditCliArgs.Parse(new[] { flag });

            Assert.True(result.HelpRequested);
            Assert.False(result.VersionRequested);
            Assert.Null(result.Args);
        }

        [Fact]
        public void Parse_Help_AnswersEvenWithoutRequiredArgs()
        {
            // The point of the short-circuit: --servers is REQUIRED, but asking for help on a
            // command line that is missing it must produce help, not "--servers is required".
            var result = AuditCliArgs.Parse(new[] { "--audit", "--help" });

            Assert.True(result.HelpRequested);
            Assert.Null(result.Error);
        }

        [Fact]
        public void Parse_Version_RequestsVersionOnly()
        {
            var result = AuditCliArgs.Parse(new[] { "--audit", "--version" });

            Assert.True(result.VersionRequested);
            Assert.False(result.HelpRequested);
        }

        [Fact]
        public void Parse_NoHelpFlag_RequestsNeither()
        {
            // CONTROL: the two flags above are read from argv, not set unconditionally.
            var result = AuditCliArgs.Parse(Base());

            Assert.False(result.HelpRequested);
            Assert.False(result.VersionRequested);
            Assert.True(result.Success);
        }

        [Fact]
        public void Parse_NoConcurrency_LeavesItNull()
        {
            // Null is load-bearing: it is what keeps CheckExecutionService's own precedence
            // (persisted setting → shipped default) intact instead of pinning a value here.
            var result = AuditCliArgs.Parse(Base());

            Assert.True(result.Success);
            Assert.Null(result.Args!.Concurrency);
        }

        [Theory]
        [InlineData(AuditCliArgs.ConcurrencyMin)]
        [InlineData(4)]
        [InlineData(AuditCliArgs.ConcurrencyMax)]
        public void Parse_ConcurrencyInRange_IsAccepted(int value)
        {
            var result = AuditCliArgs.Parse(Base("--concurrency", value.ToString()));

            Assert.True(result.Success);
            Assert.Equal(value, result.Args!.Concurrency);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("17")]
        [InlineData("-1")]
        [InlineData("many")]
        [InlineData("")]
        public void Parse_ConcurrencyOutOfRangeOrNonNumeric_FailsRatherThanClamping(string value)
        {
            // Silently clamping 64 to 16 is the "an out-of-range value resolves to something
            // else" shape that has bitten this codebase; the operator is told instead.
            var result = AuditCliArgs.Parse(Base("--concurrency", value));

            Assert.False(result.Success);
            Assert.Contains("--concurrency", result.Error);
        }

        [Fact]
        public void Parse_ConcurrencyWithoutValue_Fails()
        {
            var result = AuditCliArgs.Parse(new[] { "--audit", "--servers", ".\\old2017", "--concurrency" });

            Assert.False(result.Success);
            Assert.Contains("--concurrency", result.Error);
        }

        [Fact]
        public void Usage_NamesTheRealConcurrencyBounds()
        {
            // The help text is generated from the same constants the parser enforces, so it
            // cannot drift into promising a range that would be rejected.
            Assert.Contains($"{AuditCliArgs.ConcurrencyMin}-{AuditCliArgs.ConcurrencyMax}", AuditCliArgs.Usage);
        }
    }
}
