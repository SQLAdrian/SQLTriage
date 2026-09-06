/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Unit tests for the narrow per-statement <see cref="DangerousExecGuard"/> that gates the
    /// operator-authored free-form SQL surfaces (scheduled SqlQuery tasks + /query). The guard is
    /// SEPARATE code from the B2-calibrated <see cref="SqlSafetyValidator"/> wall.
    ///
    /// <para>Two contracts are pinned here and each is proved by execution: (1) every OS/system-exec
    /// pattern is blocked — including behind a leading sys.* read, which is exactly the batch-waiver
    /// bypass the shared wall structurally cannot close; and (2) every maintenance statement the
    /// operator surfaces exist to run passes untouched. A miss in either direction is a defect: a
    /// blocked maintenance statement guts the feature, a passed exec statement is the hole.</para>
    /// </summary>
    public class DangerousExecGuardTests
    {
        // ── Contract 1: the OS/system-exec class is BLOCKED, per statement ───────────────────────

        [Theory]
        // xp_cmdshell — direct OS command execution.
        [InlineData("EXEC xp_cmdshell 'whoami';")]
        [InlineData("EXEC master..xp_cmdshell 'dir c:\\';")]
        [InlineData("exec Xp_CmdShell 'net user';")]                    // case-insensitive
        // OLE automation — arbitrary COM reach.
        [InlineData("DECLARE @o int; EXEC sp_OACreate 'WScript.Shell', @o OUT;")]
        [InlineData("EXEC sp_OAMethod @o, 'Run', NULL, 'cmd /c whoami';")]
        [InlineData("EXEC sp_OAGetProperty @o, 'ExitCode', @rc OUT;")]
        [InlineData("EXEC sp_OASetProperty @o, 'X', 1;")]
        [InlineData("EXEC sp_OADestroy @o;")]
        // Registry STATE-CHANGE — xp_reg* / xp_instance_reg* writes, deletes, enumerates, multistring.
        // (Narrowed 2026-08-31: pure reads xp_regread / xp_instance_regread now PASS — see Contract 2.)
        [InlineData("EXEC master.dbo.xp_regwrite 'HKEY_LOCAL_MACHINE','SW','V','REG_SZ','x';")]
        [InlineData("EXEC xp_regdeletevalue 'HKEY_LOCAL_MACHINE','SW','V';")]
        [InlineData("EXEC xp_regdeletekey 'HKEY_LOCAL_MACHINE','SW';")]
        [InlineData("EXEC xp_instance_regwrite N'HKEY_LOCAL_MACHINE', N'SW', N'V', REG_DWORD, 1;")]
        [InlineData("EXEC xp_regenumvalues 'HKEY_LOCAL_MACHINE','SW';")]
        [InlineData("EXEC xp_regenumkeys 'HKEY_LOCAL_MACHINE','SW';")]
        [InlineData("EXEC xp_regaddmultistring 'HKEY_LOCAL_MACHINE','SW','V','x';")]
        [InlineData("EXEC xp_regremovemultistring 'HKEY_LOCAL_MACHINE','SW','V','x';")]
        // Filesystem enumeration — undocumented extended procs that reach the host OS filesystem
        // (added fix round 2, 2026-08-31). Plain form, sys.-prefixed form, and parameterised forms.
        [InlineData("EXEC xp_dirtree 'D:\\backups', 1, 1;")]
        [InlineData("EXEC master.sys.xp_dirtree 'D:\\backups', 1, 1;")]      // behind a sys.* prefix
        [InlineData("EXEC sys.xp_subdirs 'D:\\';")]                          // behind a sys.* prefix
        [InlineData("EXEC xp_subdirs 'C:\\data';")]
        [InlineData("EXEC master.dbo.xp_fileexist N'D:\\backups\\MyDb.bak';")]
        [InlineData("EXEC xp_fileexist 'D:\\backups\\MyDb.bak';")]
        [InlineData("EXEC xp_getfiledetails 'D:\\backups\\MyDb.bak';")]
        [InlineData("EXEC master.sys.xp_getfiledetails N'D:\\x.mdf';")]      // behind a sys.* prefix
        // CLR loading — arbitrary .NET code into the engine.
        [InlineData("CREATE ASSEMBLY MyAsm FROM 'C:\\x.dll' WITH PERMISSION_SET = UNSAFE;")]
        [InlineData("ALTER ASSEMBLY MyAsm FROM 'C:\\x.dll';")]
        [InlineData("EXEC sp_add_trusted_assembly @hash = 0xABCD, @description = N'x';")]
        // sp_configure toggles that ENABLE the OS/CLR-exec classes.
        [InlineData("EXEC sp_configure 'xp_cmdshell', 1; RECONFIGURE;")]
        [InlineData("EXEC sp_configure 'Ole Automation Procedures', 1; RECONFIGURE;")]
        [InlineData("EXEC sp_configure 'clr enabled', 1; RECONFIGURE;")]
        [InlineData("EXEC sp_configure 'clr strict security', 0; RECONFIGURE;")]
        [InlineData("EXEC sp_configure N'xp_cmdshell', 1;")]            // N-prefixed literal
        public void DangerousStatements_AreBlocked(string sql)
        {
            var verdict = DangerousExecGuard.Inspect(sql);
            Assert.False(verdict.IsAllowed, $"expected BLOCK for: {sql}");
            Assert.NotEmpty(verdict.Reason);
        }

        [Theory]
        // THE BYPASS THE RULING CLOSES: a leading sys.* read waives the shared wall's whole batch,
        // but this guard tests each statement, so the trailing exec is still blocked.
        [InlineData("SELECT 1 FROM sys.databases; EXEC xp_cmdshell 'whoami';")]
        [InlineData("SELECT * FROM sys.dm_exec_sessions; EXEC xp_cmdshell 'net user';")]
        [InlineData("SELECT name FROM sys.configurations; EXEC sp_configure 'xp_cmdshell', 1;")]
        // dangerous statement first, then a benign read — order must not matter.
        [InlineData("EXEC xp_cmdshell 'whoami'; SELECT 1 FROM sys.databases;")]
        // dangerous statement between two benign reads.
        [InlineData("SELECT 1; EXEC sp_OACreate 'WScript.Shell', @o OUT; SELECT 2 FROM sys.tables;")]
        // Filesystem enumeration behind a benign leading statement — the batch-waiver shape.
        [InlineData("SELECT 1 FROM sys.databases; EXEC xp_dirtree 'D:\\backups', 1, 1;")]
        [InlineData("SELECT name FROM sys.master_files; EXEC master.sys.xp_fileexist N'D:\\x.bak';")]
        public void DangerousStatement_BehindOrBesideSysRead_IsStillBlocked(string sql)
        {
            Assert.False(DangerousExecGuard.Inspect(sql).IsAllowed,
                $"a per-statement gate must not let a sys.* read shield: {sql}");
        }

        [Fact]
        public void DangerousExec_HiddenInGo_SeparatedBatch_IsBlocked()
        {
            const string sql = "SELECT 1 FROM sys.databases\nGO\nEXEC xp_cmdshell 'whoami'\nGO";
            Assert.False(DangerousExecGuard.Inspect(sql).IsAllowed);
        }

        // ── Contract 2: maintenance DDL/DML PASSES ───────────────────────────────────────────────

        [Theory]
        // Backup / integrity — the operator surfaces exist so a DBA can run these directly.
        [InlineData("BACKUP DATABASE [MyDb] TO DISK = 'D:\\bak\\MyDb.bak';")]
        [InlineData("BACKUP LOG [MyDb] TO DISK = 'D:\\bak\\MyDb.trn';")]
        [InlineData("DBCC CHECKDB('MyDb') WITH NO_INFOMSGS;")]
        // Index / statistics maintenance.
        [InlineData("CREATE INDEX IX_1 ON dbo.T(col);")]
        [InlineData("CREATE NONCLUSTERED INDEX IX_2 ON dbo.T(col) INCLUDE (other);")]
        [InlineData("ALTER INDEX ALL ON dbo.T REBUILD;")]
        [InlineData("DROP INDEX IX_1 ON dbo.T;")]
        [InlineData("UPDATE STATISTICS dbo.T;")]
        [InlineData("EXEC sp_updatestats;")]
        [InlineData("TRUNCATE TABLE dbo.Staging;")]
        // Plain DML.
        [InlineData("UPDATE dbo.T SET col = 1 WHERE id = 2;")]
        [InlineData("INSERT INTO dbo.T (col) VALUES (1);")]
        [InlineData("DELETE FROM dbo.T WHERE id = 3;")]
        [InlineData("SELECT * FROM sys.databases;")]
        [InlineData("MERGE dbo.T AS t USING dbo.S AS s ON t.id = s.id WHEN MATCHED THEN UPDATE SET t.col = s.col;")]
        // Agent job DDL.
        [InlineData("EXEC msdb.dbo.sp_add_job @job_name = N'Nightly';")]
        [InlineData("EXEC msdb.dbo.sp_add_jobstep @job_name = N'Nightly', @step_name = N'S1', @command = N'SELECT 1';")]
        // Ola Hallengren solution invocations (the shipped remediation path).
        [InlineData("EXEC dbo.IndexOptimize @Databases = 'ALL_DATABASES';")]
        [InlineData("EXEC dbo.DatabaseBackup @Databases = 'USER_DATABASES', @Directory = 'D:\\bak', @BackupType = 'FULL';")]
        // sp_configure of a MAINTENANCE option (never a dangerous one) passes, prerequisite included.
        [InlineData("EXEC sp_configure 'max degree of parallelism', 4; RECONFIGURE;")]
        [InlineData("EXEC sp_configure 'show advanced options', 1; RECONFIGURE;")]
        [InlineData("EXEC sp_configure 'cost threshold for parallelism', 50; RECONFIGURE;")]
        // A bare RECONFIGURE is used after benign sp_configure calls — never blocked on its own.
        [InlineData("RECONFIGURE;")]
        // A READ that only MENTIONS a dangerous proc name as string data is not an exec — it passes,
        // exactly as the shared wall's own allowed case does.
        [InlineData("SELECT name, value FROM sys.configurations WHERE name = 'xp_cmdshell';")]
        [InlineData("SELECT definition FROM sys.sql_modules WHERE definition LIKE '%xp_cmdshell%';")]
        // Same for the filesystem-enum procs: a read that only MENTIONS the proc name as string
        // data (audit query / definition search) is not an exec — the literal is blanked, so it passes.
        [InlineData("SELECT definition FROM sys.sql_modules WHERE definition LIKE '%xp_dirtree%';")]
        [InlineData("SELECT name FROM sys.objects WHERE name = 'xp_fileexist';")]
        [InlineData("SELECT 'xp_subdirs is a filesystem proc' AS note;")]
        // Registry READS are information retrieval, not OS execution or state-change — allowed after
        // the 2026-08-31 narrowing. A shipped read-only panel (security.failed_logins_1h) EXECs
        // xp_instance_regread to display the host's MSSQLServer AuditLevel; blocking it dropped the
        // panel from the cache for no security gain.
        [InlineData("EXEC xp_regread 'HKEY_LOCAL_MACHINE','SW','V';")]
        [InlineData("DECLARE @al INT; EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SW', N'V', @al OUTPUT;")]
        // A dangerous token that is only inside a COMMENT is stripped and does not run.
        [InlineData("SELECT 1 FROM sys.databases; -- EXEC xp_cmdshell 'whoami'")]
        [InlineData("SELECT 1 /* EXEC xp_cmdshell 'whoami' */ FROM sys.databases;")]
        public void MaintenanceStatements_Pass(string sql)
        {
            var verdict = DangerousExecGuard.Inspect(sql);
            Assert.True(verdict.IsAllowed,
                $"maintenance must not be blocked: {sql} (reason if blocked: {verdict.Reason})");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\n\t ")]
        public void EmptyOrWhitespace_IsAllowed(string? sql)
        {
            Assert.True(DangerousExecGuard.Inspect(sql).IsAllowed);
        }

        // ── Mutation guards: the reason names the class that fired ────────────────────────────────

        [Fact]
        public void Block_Reason_NamesTheOffendingClass()
        {
            Assert.Contains("xp_cmdshell", DangerousExecGuard.Inspect("EXEC xp_cmdshell 'x';").Reason);
            Assert.Contains("OLE Automation", DangerousExecGuard.Inspect("EXEC sp_OACreate 'x', @o OUT;").Reason);
            Assert.Contains("Registry", DangerousExecGuard.Inspect("EXEC xp_regwrite 'H','S','V','REG_SZ','x';").Reason);
            Assert.Contains("Filesystem enumeration", DangerousExecGuard.Inspect("EXEC xp_dirtree 'D:\\x', 1, 1;").Reason);
            Assert.Contains("CLR", DangerousExecGuard.Inspect("CREATE ASSEMBLY a FROM 'x';").Reason);
            Assert.Contains("clr enabled", DangerousExecGuard.Inspect("EXEC sp_configure 'clr enabled', 1;").Reason);
        }

        [Fact]
        public void ConfigToggle_OfMaintenanceOption_DoesNotFalselyMatchDangerousReason()
        {
            // Proves the config-toggle pattern is scoped to the four dangerous options only: a MAXDOP
            // toggle is allowed, so tightening its regex to also catch 'xp_cmdshell' would be caught
            // here by the maintenance theory, and loosening it to any sp_configure would be caught by
            // this allow.
            Assert.True(DangerousExecGuard.Inspect("EXEC sp_configure 'max degree of parallelism', 4;").IsAllowed);
        }

        [Fact]
        public void Registry_ReadAllowed_ButWriteDeleteEnumerate_Blocked()
        {
            // Pins the 2026-08-31 narrowing direction: a revert to the old catch-all `xp_reg[a-z]+`
            // pattern would re-block the reads (failing the first two asserts), and dropping the
            // registry gate entirely would let the writes through (failing the rest).
            Assert.True(DangerousExecGuard.Inspect("EXEC xp_regread 'H','S','V';").IsAllowed);
            Assert.True(DangerousExecGuard.Inspect("EXEC xp_instance_regread N'H', N'S', N'V', @o OUT;").IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("EXEC xp_regwrite 'H','S','V','REG_SZ','x';").IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("EXEC xp_instance_regwrite N'H',N'S',N'V',REG_DWORD,1;").IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("EXEC xp_regdeletekey 'H','S';").IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("EXEC xp_regenumvalues 'H','S';").IsAllowed);
        }

        [Fact]
        public void Filesystem_EnumerationProcs_AreBlocked_ButAStringMention_Passes()
        {
            // Pins the fix-round-2 addition (2026-08-31): the four filesystem-enum extended procs are
            // blocked as identifiers/execs — dropping the pattern would fail the first four asserts —
            // while a read that carries the name only as string DATA still passes, because the guard
            // blanks string literals before matching the exec patterns. All four names are exercised
            // so narrowing the alternation to miss one is caught here.
            Assert.False(DangerousExecGuard.Inspect("EXEC xp_dirtree 'D:\\backups', 1, 1;").IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("EXEC xp_subdirs 'D:\\';").IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("EXEC master.sys.xp_fileexist N'D:\\x.bak';").IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("EXEC xp_getfiledetails 'D:\\x.mdf';").IsAllowed);
            Assert.True(DangerousExecGuard.Inspect(
                "SELECT definition FROM sys.sql_modules WHERE definition LIKE '%xp_dirtree%';").IsAllowed);
            Assert.True(DangerousExecGuard.Inspect("SELECT 'xp_fileexist' AS mentioned_only;").IsAllowed);
        }
    }
}
