/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
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
    /// EXERCISE VEHICLE for task #39's db_set_option apply engine — the net-new write path
    /// added alongside sp_configure. INERT in a normal <c>dotnet test</c> run — it early-returns
    /// (stays green) unless <c>DBSETOPTION_LIVE_TARGET</c> names a reachable SQL instance. Calls
    /// the REAL executor directly (same pattern as RemediationBackupCheckDbLiveSmokeTests),
    /// bypassing RemediationRunner's credit/capability gates (not under test here), against a
    /// disposable test database it creates and drops itself. Every claim is checked against an
    /// INDEPENDENT <see cref="SqlConnection"/> query of <c>sys.databases</c> — never the app's
    /// own self-report — so apply/rollback are proven, not just asserted.
    ///
    /// INVOCATION (gate, on a box with the local test instances):
    ///   $env:DBSETOPTION_LIVE_TARGET = ".\old2017"
    ///   dotnet test Tests/SQLTriage.Tests --filter "FullyQualifiedName~RemediationDbSetOptionLiveSmokeTests"
    /// </summary>
    public class RemediationDbSetOptionLiveSmokeTests
    {
        private readonly ITestOutputHelper _out;
        public RemediationDbSetOptionLiveSmokeTests(ITestOutputHelper output) => _out = output;

        private const string TestDb = "_SQLT_TEST_dbchain39";

        // Hyphens are ordinary in real database names (SharePoint GUID DBs, "Contoso-Prod")
        // but fail the renderer's identifier guard ([A-Za-z0-9_@$# ]) — the reachable corner
        // gate #39 found: an offender whose name can never be rendered must never read back
        // as an applied+verified fix.
        private const string HyphenTestDb = "_SQLT_TEST_dbchain39-hyphen";

        private static (ServerConnectionManager connections, DbatoolsRemediationExecutor executor, string tempAuditDir)
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

            var tempAuditDir = Path.Combine(Path.GetTempPath(), "sqltriage-dbsetoption-livesmoke-" + Guid.NewGuid().ToString("N"));
            var audit = new AuditLogService(tempAuditDir, startFlushTimer: false);
            var diskIo = new DiskIoService(NullLogger<DiskIoService>.Instance);
            var powerShell = new PowerShellService(NullLogger<PowerShellService>.Instance);
            var executor = new DbatoolsRemediationExecutor(powerShell, connections, audit, diskIo, NullLogger<DbatoolsRemediationExecutor>.Instance);
            return (connections, executor, tempAuditDir);
        }

        private static string MasterConnString(string target) =>
            $"Server={target};Database=master;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=15;";

        // Independent proof read — a plain SqlConnection query, never the app's own executor.
        private static bool? ReadDbChainingIndependently(string target, string dbName = TestDb)
        {
            using var conn = new SqlConnection(MasterConnString(target));
            conn.Open();
            using var cmd = new SqlCommand(
                "SELECT is_db_chaining_on FROM sys.databases WHERE name = @n;", conn);
            cmd.Parameters.AddWithValue("@n", dbName);
            var val = cmd.ExecuteScalar();
            if (val is null || val is DBNull) return null; // database doesn't exist
            return Convert.ToBoolean(val);
        }

        private static void ExecNonQuery(string target, string sql)
        {
            using var conn = new SqlConnection(MasterConnString(target));
            conn.Open();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            cmd.ExecuteNonQuery();
        }

        private static void DropTestDbIfExists(string target, string dbName = TestDb)
        {
            try
            {
                ExecNonQuery(target,
                    $"IF DB_ID('{dbName}') IS NOT NULL BEGIN ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]; END");
            }
            catch { /* best-effort teardown */ }
        }

        [Fact]
        public async Task DbSetOption_Preview_Apply_ChangesLiveDatabase_IndependentlyVerified()
        {
            var target = Environment.GetEnvironmentVariable("DBSETOPTION_LIVE_TARGET");
            if (string.IsNullOrWhiteSpace(target))
            {
                Line("SKIPPED: set DBSETOPTION_LIVE_TARGET to a SQL instance (e.g. .\\old2017) to exercise the live db_set_option apply.");
                return; // inert no-op in normal runs
            }

            DropTestDbIfExists(target);
            try
            {
                // SETUP: a real database with DB_CHAINING ON — the "offender" pre-state, mirroring
                // the corpus check's own Seed block (SQLT-VA-DB-CHAINING.md).
                ExecNonQuery(target, $"CREATE DATABASE [{TestDb}];");
                ExecNonQuery(target, $"ALTER DATABASE [{TestDb}] SET DB_CHAINING ON;");

                var before = ReadDbChainingIndependently(target);
                Assert.True(before, "setup sanity: test DB should start with DB_CHAINING ON");

                var (_, executor, _) = Wire(target);
                var op = new RemediationOperation
                {
                    OpKind = RemediationOpKind.DbSetOption,
                    OptionSql = "SET DB_CHAINING OFF",
                    OffendersQuery = $"SELECT name FROM sys.databases WHERE name = '{TestDb}' AND is_db_chaining_on = 1;",
                };
                var template = new RemediationTemplate
                {
                    Key = "TEST-DBCHAINING-LIVE",
                    DisplayName = "Test DB_CHAINING (live smoke)",
                    Kind = RemediationKind.Configuration,
                    Operation = op,
                    Reversible = true,
                };
                var request = new RemediationRequest(template, target);

                // 1) PREVIEW — must name our offending test DB, using the SAME offenders_query apply uses.
                var preview = await executor.PreviewAsync(request);
                Assert.True(preview.Succeeded, preview.Error);
                Assert.Contains(TestDb, preview.WhatIfText);
                Assert.Contains("ALTER DATABASE", preview.WhatIfText);
                Line($"=== Preview (offenders_query == apply's query — parity by construction) ===\n{preview.WhatIfText}");

                // 2) APPLY — the real executor, real SQL, real database.
                var execution = await executor.ExecuteAsync(request);
                Line($"Outcome: {execution.Outcome} — {execution.Error}");
                Assert.Equal(RemediationOutcome.AppliedVerified, execution.Outcome);

                // 3) INDEPENDENT PROOF — a separate SqlConnection query, not the app's self-report.
                var after = ReadDbChainingIndependently(target);
                Assert.False(after, "independent sys.databases read: DB_CHAINING should now be OFF");
                Line($"Independent proof: is_db_chaining_on BEFORE={before}, AFTER={after} — apply verified live.");
            }
            finally { DropTestDbIfExists(target); }
        }

        [Fact]
        public async Task DbSetOption_VerifyFailure_TriggersRollback_RestoringPreState_IndependentlyVerified()
        {
            var target = Environment.GetEnvironmentVariable("DBSETOPTION_LIVE_TARGET");
            if (string.IsNullOrWhiteSpace(target))
            {
                Line("SKIPPED: set DBSETOPTION_LIVE_TARGET to a SQL instance (e.g. .\\old2017) to exercise the live rollback.");
                return; // inert no-op in normal runs
            }

            DropTestDbIfExists(target);
            try
            {
                ExecNonQuery(target, $"CREATE DATABASE [{TestDb}];");
                ExecNonQuery(target, $"ALTER DATABASE [{TestDb}] SET DB_CHAINING ON;");
                var before = ReadDbChainingIndependently(target);
                Assert.True(before, "setup sanity: test DB should start with DB_CHAINING ON");

                var (_, executor, _) = Wire(target);
                // DELIBERATE test-only manufacture of a verify failure: this offenders_query names
                // the test DB unconditionally (no is_db_chaining_on predicate), so it ALWAYS reports
                // the DB as "still offending" after apply — forcing the executor down its real
                // rollback path against a real server, rather than asserting the happy path only.
                var op = new RemediationOperation
                {
                    OpKind = RemediationOpKind.DbSetOption,
                    OptionSql = "SET DB_CHAINING OFF",
                    OffendersQuery = $"SELECT name FROM sys.databases WHERE name = '{TestDb}';",
                };
                var template = new RemediationTemplate
                {
                    Key = "TEST-DBCHAINING-ROLLBACK-LIVE",
                    DisplayName = "Test DB_CHAINING rollback (live smoke)",
                    Kind = RemediationKind.Configuration,
                    Operation = op,
                    Reversible = true,
                };
                var request = new RemediationRequest(template, target);

                var execution = await executor.ExecuteAsync(request);
                Line($"Outcome: {execution.Outcome} — {execution.Error}");
                Assert.Equal(RemediationOutcome.AppliedVerifyFailed, execution.Outcome);
                Assert.True(execution.RolledBack, "executor should have attempted a rollback on verify failure");
                Assert.True(execution.RollbackSucceeded, execution.RollbackError);

                // INDEPENDENT PROOF: DB_CHAINING is back ON — the pre-apply state — not left OFF.
                var after = ReadDbChainingIndependently(target);
                Assert.True(after, "independent sys.databases read: rollback should have restored DB_CHAINING to ON");
                Line($"Independent proof: is_db_chaining_on BEFORE={before}, (mid-apply would have been OFF), AFTER-ROLLBACK={after} — rollback verified live.");
            }
            finally { DropTestDbIfExists(target); }
        }

        [Fact]
        public async Task DbSetOption_AllOffendersUnrenderable_ReportsCouldNotRun_NotAppliedVerified()
        {
            var target = Environment.GetEnvironmentVariable("DBSETOPTION_LIVE_TARGET");
            if (string.IsNullOrWhiteSpace(target))
            {
                Line("SKIPPED: set DBSETOPTION_LIVE_TARGET to a SQL instance (e.g. .\\old2017) to exercise this corner.");
                return; // inert no-op in normal runs
            }

            DropTestDbIfExists(target, HyphenTestDb);
            try
            {
                ExecNonQuery(target, $"CREATE DATABASE [{HyphenTestDb}];");
                ExecNonQuery(target, $"ALTER DATABASE [{HyphenTestDb}] SET DB_CHAINING ON;");
                var before = ReadDbChainingIndependently(target, HyphenTestDb);
                Assert.True(before, "setup sanity: hyphenated test DB should start with DB_CHAINING ON");

                var (_, executor, _) = Wire(target);
                var op = new RemediationOperation
                {
                    OpKind = RemediationOpKind.DbSetOption,
                    OptionSql = "SET DB_CHAINING OFF",
                    OffendersQuery = $"SELECT name FROM sys.databases WHERE name = '{HyphenTestDb}' AND is_db_chaining_on = 1;",
                };
                var template = new RemediationTemplate
                {
                    Key = "TEST-DBCHAINING-UNRENDERABLE-LIVE",
                    DisplayName = "Test DB_CHAINING unrenderable-name corner (live smoke)",
                    Kind = RemediationKind.Configuration,
                    Operation = op,
                    Reversible = true,
                };
                var request = new RemediationRequest(template, target);

                var execution = await executor.ExecuteAsync(request);
                Line($"Outcome: {execution.Outcome} — {execution.Error}");

                // THE FIX under gate: the only offender is unrenderable — ZERO statements
                // executed — so this must be CouldNotRun, never AppliedVerified.
                Assert.Equal(RemediationOutcome.CouldNotRun, execution.Outcome);
                Assert.Contains(HyphenTestDb, execution.Error);

                // INDEPENDENT PROOF: nothing ran — DB_CHAINING is untouched (still ON).
                var after = ReadDbChainingIndependently(target, HyphenTestDb);
                Assert.True(after, "independent sys.databases read: DB_CHAINING should be untouched (still ON) — apply never ran");
            }
            finally { DropTestDbIfExists(target, HyphenTestDb); }
        }

        [Fact]
        public async Task DbSetOption_MixedOffenders_SkippedUnrenderableName_ReportsVerifyFailed_NotAppliedVerified()
        {
            var target = Environment.GetEnvironmentVariable("DBSETOPTION_LIVE_TARGET");
            if (string.IsNullOrWhiteSpace(target))
            {
                Line("SKIPPED: set DBSETOPTION_LIVE_TARGET to a SQL instance (e.g. .\\old2017) to exercise this corner.");
                return; // inert no-op in normal runs
            }

            DropTestDbIfExists(target, TestDb);
            DropTestDbIfExists(target, HyphenTestDb);
            try
            {
                ExecNonQuery(target, $"CREATE DATABASE [{TestDb}];");
                ExecNonQuery(target, $"ALTER DATABASE [{TestDb}] SET DB_CHAINING ON;");
                ExecNonQuery(target, $"CREATE DATABASE [{HyphenTestDb}];");
                ExecNonQuery(target, $"ALTER DATABASE [{HyphenTestDb}] SET DB_CHAINING ON;");

                var (_, executor, _) = Wire(target);
                var op = new RemediationOperation
                {
                    OpKind = RemediationOpKind.DbSetOption,
                    OptionSql = "SET DB_CHAINING OFF",
                    OffendersQuery = $"SELECT name FROM sys.databases WHERE name IN ('{TestDb}', '{HyphenTestDb}') AND is_db_chaining_on = 1;",
                };
                var template = new RemediationTemplate
                {
                    Key = "TEST-DBCHAINING-MIXED-LIVE",
                    DisplayName = "Test DB_CHAINING mixed offenders (live smoke)",
                    Kind = RemediationKind.Configuration,
                    Operation = op,
                    Reversible = true,
                };
                var request = new RemediationRequest(template, target);

                var execution = await executor.ExecuteAsync(request);
                Line($"Outcome: {execution.Outcome} — {execution.Error}");

                // THE FIX under gate: one real database got fixed (TestDb); the hyphenated one
                // could never be rendered/applied and remains offending — this must surface as
                // AppliedVerifyFailed (needs human review), never a clean AppliedVerified.
                Assert.Equal(RemediationOutcome.AppliedVerifyFailed, execution.Outcome);
                Assert.Contains(HyphenTestDb, execution.Error);

                // INDEPENDENT PROOF: the safely-named DB was genuinely fixed...
                var testDbAfter = ReadDbChainingIndependently(target, TestDb);
                Assert.False(testDbAfter, "independent read: the safely-named offender should have been fixed");
                // ...and the hyphenated one was never touched — still offending, not silently "fixed".
                var hyphenAfter = ReadDbChainingIndependently(target, HyphenTestDb);
                Assert.True(hyphenAfter, "independent read: the unrenderable-name offender must remain untouched");
            }
            finally
            {
                DropTestDbIfExists(target, TestDb);
                DropTestDbIfExists(target, HyphenTestDb);
            }
        }

        private void Line(string s) => _out.WriteLine(s);
    }
}
