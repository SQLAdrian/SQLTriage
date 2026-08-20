/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using SQLTriage.Cli;
using Xunit;

namespace SQLTriage.Tests.Cli
{
    /// <summary>
    /// #33 headless CLI — arg parsing + pre-run validation. Every failure path here must map to
    /// exit code 3 (CliAuditHost), BEFORE any server connection is attempted — these tests only
    /// assert on <see cref="AuditCliArgsResult"/>, never touch a real SQL Server.
    ///
    /// Every success-path assertion passes an explicit --bundle pointing at a private temp file
    /// (never the ambient default AppContext.BaseDirectory/Config/free-bundle.dat) — that shared
    /// path is written and deleted by Licensing/LicenseServiceTests' own setup/teardown, which
    /// runs concurrently (a different xUnit collection) with this class. Depending on the
    /// ambient file made these tests flaky under a full-suite parallel run; an explicit,
    /// private bundle removes the shared mutable state entirely.
    /// </summary>
    public class AuditCliArgsTests : IDisposable
    {
        private readonly string _bundlePath =
            Path.Combine(Path.GetTempPath(), "sqlt-cli-test-bundle-" + Guid.NewGuid().ToString("N") + ".dat");

        public AuditCliArgsTests()
        {
            File.WriteAllBytes(_bundlePath, new byte[] { 1, 2, 3 });
        }

        public void Dispose()
        {
            try { File.Delete(_bundlePath); } catch { /* best-effort */ }
        }

        private string[] WithBundle(params string[] args)
        {
            var all = new string[args.Length + 2];
            Array.Copy(args, all, args.Length);
            all[^2] = "--bundle";
            all[^1] = _bundlePath;
            return all;
        }

        [Fact]
        public void Parse_MissingServers_Fails()
        {
            var result = AuditCliArgs.Parse(new[] { "--audit" });

            Assert.False(result.Success);
            Assert.Contains("--servers", result.Error);
        }

        [Fact]
        public void Parse_CommaServerList_SplitsAndTrims()
        {
            var result = AuditCliArgs.Parse(WithBundle("--servers", " .\\old2017 , .\\new2022 "));

            Assert.True(result.Success);
            Assert.Equal(new[] { ".\\old2017", ".\\new2022" }, result.Args!.Servers);
        }

        // ── Parse before pricing ────────────────────────────────────────────────────────────────
        //  A comma is SQL Server's PORT separator. Splitting on it turned one ported server into
        //  two, and the next thing to look at the list was the corpus-demo allocation, which
        //  counts entries: the user asking for one server was told "You asked for 2" and quoted
        //  the limits of their plan. These pin the count at the parser, where the mistake was.

        [Theory]
        [InlineData("SQL01,56510")]
        [InlineData(".\\old2017,56510")]
        [InlineData("SQL01\\INST,56510")]
        [InlineData("10.1.2.3,1433")]
        public void Parse_HostCommaPort_IsOneServer_NotTwo(string address)
        {
            var result = AuditCliArgs.Parse(WithBundle("--servers", address));

            Assert.True(result.Success, result.Error);
            var only = Assert.Single(result.Args!.Servers);
            Assert.Equal(address, only);
        }

        [Fact]
        public void Parse_AtFile_HostCommaPortOnOneLine_IsOneServer()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(path, new[] { "# ported", "SQL01\\INST,56510" });
            try
            {
                var result = AuditCliArgs.Parse(WithBundle("--servers", "@" + path));

                Assert.True(result.Success, result.Error);
                Assert.Equal("SQL01\\INST,56510", Assert.Single(result.Args!.Servers));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Parse_Semicolon_IsAListSeparator_NotAMalformedAddress()
        {
            // On the record rather than accidental: ServerAddress.SplitList treats ';' as a list
            // separator, and has since it was written, because ';' has never been legal inside a
            // server name. So "A;B" is two servers, and the allocation counting two is CORRECT.
            // The defect this wave fixed was a comma being read the same way; a comma is a port
            // separator and a semicolon is not.
            var result = AuditCliArgs.Parse(WithBundle("--servers", "SQL01;SQL02"));

            Assert.True(result.Success, result.Error);
            Assert.Equal(new[] { "SQL01", "SQL02" }, result.Args!.Servers);
        }

        [Theory]
        [InlineData("SQL01<script>")]
        [InlineData("SQL01 with spaces")]
        [InlineData("SQL01|SQL02")]
        [InlineData("SQL01'; DROP")]
        public void Parse_MalformedAddress_FailsAsAParseError_NotAsAPriceQuote(string address)
        {
            var result = AuditCliArgs.Parse(WithBundle("--servers", address));

            Assert.False(result.Success);
            Assert.NotNull(result.Error);

            // It says what it measured: which address, and what was wrong with it.
            Assert.Contains("not a valid server address", result.Error!, StringComparison.Ordinal);

            // And it says nothing about allocation, plans, or how many servers were asked for.
            // That vocabulary belongs to the licence seam and must not answer a typo.
            foreach (var commercial in new[] { "You asked for", "allocation", "Run fewer", "licence", "license", "upgrade" })
                Assert.DoesNotContain(commercial, result.Error!, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The four inputs item 2c named. Each is a PORT the operator got wrong, and each used to
        /// survive parsing as a second server: the character-set check passed both halves, so the
        /// only thing left to notice was the allocation, which counts entries and answers in
        /// commercial vocabulary.
        ///
        /// <para>The assertion is on the vocabulary of the ANSWER, not on an exit code, because
        /// the defect was never that the run failed — it was WHICH failure the operator was
        /// handed.</para>
        /// </summary>
        [Theory]
        [InlineData(@"SQL01\INST,5651Z")]
        [InlineData(@"SQL01\INST,566510")]
        [InlineData(@"SQL01\INST,0x1F")]
        [InlineData(@"SQL01\INST,port")]
        public void Parse_MistypedPort_AnswersAsAParseError_NeverAsAPriceQuote(string address)
        {
            var result = AuditCliArgs.Parse(WithBundle("--servers", address));

            Assert.False(result.Success, "a mistyped port must not parse into a two-server list");
            Assert.NotNull(result.Error);

            // It names the segment it could not read.
            Assert.Contains(address[(address.IndexOf(',') + 1)..], result.Error!, StringComparison.Ordinal);

            // And it stays out of the licence seam's vocabulary entirely.
            foreach (var commercial in new[] { "You asked for", "allocation", "Run fewer", "licence", "license", "upgrade" })
                Assert.DoesNotContain(commercial, result.Error!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Parse_AtFile_MistypedPortOnOneLine_FailsAndNamesTheFile()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(path, new[] { ".\\old2017", "SQL01\\INST,5651Z" });
            try
            {
                var result = AuditCliArgs.Parse(WithBundle("--servers", "@" + path));

                Assert.False(result.Success);
                Assert.Contains(path, result.Error!, StringComparison.Ordinal);
                Assert.Contains("5651Z", result.Error!, StringComparison.Ordinal);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Parse_MalformedAddress_NamesTheOffendingCharacter()
        {
            var result = AuditCliArgs.Parse(WithBundle("--servers", "SQL01|SQL02"));

            Assert.False(result.Success);
            Assert.Contains("'|'", result.Error!, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_OneMalformedEntry_RefusesTheWholeList_NeverSilentlyShortensIt()
        {
            // Dropping the bad entry would run an audit over a list the operator did not ask for,
            // and the report would read clean for an estate one server short.
            var result = AuditCliArgs.Parse(WithBundle("--servers", ".\\old2017\n SQL01 with spaces \n.\\new2022"));

            Assert.False(result.Success);
            Assert.Contains("SQL01 with spaces", result.Error!, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_AtFile_MalformedLine_FailsAndNamesTheFile()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(path, new[] { ".\\old2017", "SQL01<bad>" });
            try
            {
                var result = AuditCliArgs.Parse(WithBundle("--servers", "@" + path));

                Assert.False(result.Success);
                Assert.Contains(path, result.Error!, StringComparison.Ordinal);
                Assert.Contains("not a valid server address", result.Error!, StringComparison.Ordinal);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Parse_AtFile_EmptyLineList_StillReportsTheEmptyFile()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(path, new[] { "# nothing but a comment", "" });
            try
            {
                var result = AuditCliArgs.Parse(WithBundle("--servers", "@" + path));

                Assert.False(result.Success);
                Assert.Contains("no server names", result.Error!, StringComparison.OrdinalIgnoreCase);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Parse_AtFile_MissingFile_Fails()
        {
            var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
            var result = AuditCliArgs.Parse(new[] { "--servers", "@" + missing });

            Assert.False(result.Success);
            Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Parse_AtFile_ReadsOneServerPerLine_SkipsBlankAndComments()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(path, new[] { "# a comment", "", ".\\old2017", "  ", ".\\new2022" });
            try
            {
                var result = AuditCliArgs.Parse(WithBundle("--servers", "@" + path));

                Assert.True(result.Success);
                Assert.Equal(new[] { ".\\old2017", ".\\new2022" }, result.Args!.Servers);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Parse_PlaintextPasswordArg_IsAlwaysRejected()
        {
            var result = AuditCliArgs.Parse(new[]
            {
                "--servers", ".\\old2017", "--auth", "sql", "--user", "sa", "--password", "hunter2",
            });

            Assert.False(result.Success);
            Assert.Contains("--password is not accepted", result.Error);
        }

        [Fact]
        public void Parse_SqlAuth_MissingUser_Fails()
        {
            var result = AuditCliArgs.Parse(new[]
            {
                "--servers", ".\\old2017", "--auth", "sql", "--password-env", "SQLT_TEST_PW_" + Guid.NewGuid().ToString("N"),
            });

            Assert.False(result.Success);
            Assert.Contains("--user", result.Error);
        }

        [Fact]
        public void Parse_SqlAuth_MissingPasswordEnv_Fails()
        {
            var result = AuditCliArgs.Parse(new[]
            {
                "--servers", ".\\old2017", "--auth", "sql", "--user", "sa",
            });

            Assert.False(result.Success);
            Assert.Contains("--password-env", result.Error);
        }

        [Fact]
        public void Parse_SqlAuth_PasswordEnvVarUnset_Fails()
        {
            var varName = "SQLT_TEST_PW_UNSET_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(varName, null); // ensure unset

            var result = AuditCliArgs.Parse(new[]
            {
                "--servers", ".\\old2017", "--auth", "sql", "--user", "sa", "--password-env", varName,
            });

            Assert.False(result.Success);
            Assert.Contains("not set", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Parse_SqlAuth_PasswordEnvVarSet_ReadsRealValue_NeverFromArgv()
        {
            var varName = "SQLT_TEST_PW_SET_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(varName, "s3cr3t-value");
            try
            {
                var result = AuditCliArgs.Parse(WithBundle(
                    "--servers", ".\\old2017", "--auth", "sql", "--user", "sa", "--password-env", varName));

                Assert.True(result.Success);
                Assert.Equal("s3cr3t-value", result.Args!.Password);
            }
            finally { Environment.SetEnvironmentVariable(varName, null); }
        }

        [Fact]
        public void Parse_IntegratedAuth_WithUserOrPasswordEnv_Fails()
        {
            var result = AuditCliArgs.Parse(new[]
            {
                "--servers", ".\\old2017", "--auth", "integrated", "--user", "sa",
            });

            Assert.False(result.Success);
        }

        [Fact]
        public void Parse_InvalidAuthValue_Fails()
        {
            var result = AuditCliArgs.Parse(new[] { "--servers", ".\\old2017", "--auth", "kerberos" });

            Assert.False(result.Success);
            Assert.Contains("--auth", result.Error);
        }

        [Theory]
        [InlineData("json,csv,pdf", new[] { "json", "csv", "pdf" })]
        [InlineData(" JSON , Csv ", new[] { "json", "csv" })]
        public void Parse_FormatList_ParsesCaseInsensitiveCommaList(string raw, string[] expected)
        {
            var result = AuditCliArgs.Parse(WithBundle("--servers", ".\\old2017", "--format", raw));

            Assert.True(result.Success);
            Assert.Equal(expected, result.Args!.Formats);
        }

        [Fact]
        public void Parse_FormatDefault_IsJsonOnly()
        {
            var result = AuditCliArgs.Parse(WithBundle("--servers", ".\\old2017"));

            Assert.True(result.Success);
            Assert.Equal(new[] { "json" }, result.Args!.Formats);
        }

        [Fact]
        public void Parse_UnrecognizedFormat_Fails()
        {
            var result = AuditCliArgs.Parse(new[] { "--servers", ".\\old2017", "--format", "xml" });

            Assert.False(result.Success);
            Assert.Contains("xml", result.Error);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("notanumber")]
        public void Parse_InvalidParallel_Fails(string value)
        {
            var result = AuditCliArgs.Parse(new[] { "--servers", ".\\old2017", "--parallel", value });

            Assert.False(result.Success);
            Assert.Contains("--parallel", result.Error);
        }

        [Fact]
        public void Parse_ParallelDefault_IsFour()
        {
            var result = AuditCliArgs.Parse(WithBundle("--servers", ".\\old2017"));

            Assert.True(result.Success);
            Assert.Equal(4, result.Args!.Parallel);
        }

        [Fact]
        public void Parse_UnknownArgument_Fails()
        {
            var result = AuditCliArgs.Parse(new[] { "--servers", ".\\old2017", "--not-a-real-flag" });

            Assert.False(result.Success);
            Assert.Contains("Unknown argument", result.Error);
        }

        [Fact]
        public void Parse_BundlePath_MissingFile_Fails()
        {
            var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dat");
            var result = AuditCliArgs.Parse(new[] { "--servers", ".\\old2017", "--bundle", missing });

            Assert.False(result.Success);
            Assert.Contains("--bundle", result.Error);
        }

        [Fact]
        public void Parse_BundlePath_ExistingFile_Succeeds()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dat");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            try
            {
                var result = AuditCliArgs.Parse(new[] { "--servers", ".\\old2017", "--bundle", path });

                Assert.True(result.Success);
                Assert.Equal(path, result.Args!.BundlePath);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Parse_OutDir_UnwritablePath_Fails()
        {
            // A path nested under a file (not a directory) can never be created — deterministic
            // "unwritable" case that doesn't depend on filesystem ACLs.
            var blockingFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            File.WriteAllText(blockingFile, "x");
            try
            {
                var badOutDir = Path.Combine(blockingFile, "output");
                var result = AuditCliArgs.Parse(WithBundle("--servers", ".\\old2017", "--out", badOutDir));

                Assert.False(result.Success);
                Assert.Contains("--out", result.Error);
            }
            finally { File.Delete(blockingFile); }
        }

        [Fact]
        public void Parse_Quiet_DefaultsFalse_FlagSetsTrue()
        {
            var withoutQuiet = AuditCliArgs.Parse(WithBundle("--servers", ".\\old2017"));
            var withQuiet = AuditCliArgs.Parse(WithBundle("--servers", ".\\old2017", "--quiet"));

            Assert.True(withoutQuiet.Success);
            Assert.False(withoutQuiet.Args!.Quiet);
            Assert.True(withQuiet.Success);
            Assert.True(withQuiet.Args!.Quiet);
        }
    }
}
