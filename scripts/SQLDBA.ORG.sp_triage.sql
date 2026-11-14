/* بسم الله الرحمن الرحيم  */
/* In the name of God, the Merciful, the Compassionate */
/* ALTER PROCEDURE below saves this session's QUOTED_IDENTIFIER and ANSI_NULLS with the procedure, and the xml methods in
   the procedure do not compile when QUOTED_IDENTIFIER was OFF (Msg 1934). sqlcmd without -I connects with it OFF. A SET
   carries into every later batch of the session, so both are ON here, before the GO. Guarded by
   SpTriageInstallSessionOptionsCensusTests (lane sp-triage-section-18-index-suggestions, design 9). */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
IF OBJECT_ID('dbo.sp_triage®') IS NULL
  EXEC ('CREATE PROCEDURE dbo.[sp_triage®] AS RETURN 0;');
GO

ALTER PROCEDURE [dbo].[sp_triage®]  --@Debug = 0]
--DECLARE
/* 
WANT [sp_triage®]?
https://github.com/SQLDBA-ORG/sqldba/

Supported platforms. Tested by recorded runs on SQL Server 2017 (14.0.2140.2) and SQL Server 2022 (16.0.4305.5), as a sysadmin login. A run as a login that is not sysadmin was proved on SQL Server 2022 (16.0.4305.5) and SQL Server 2017 (14.0.2140.2), as six login permission shapes, none sysadmin (with and without VIEW SERVER STATE, VIEW ANY DATABASE and VIEW ANY DEFINITION): it sends no error and returns its report, and each step the login may not run shows in the Could not run section. Not tested as a login that is not sysadmin: Azure SQL Managed Instance, Azure SQL Database, or a login that holds CONTROL SERVER without sysadmin. SQL Server 2008: the script parses as SQL Server 2008 T-SQL (a parse check only); it has never been installed or run on 2008. A step that needs a newer version shows in the Could not run section with its reason. Not tested on Azure SQL Managed Instance, Azure SQL Database, Amazon RDS or SQL Server on Linux: a run there starts with a "Not tested" first row.

Sample command:
	EXEC  [dbo].[sp_triage®]  @MailResults = 1
	
RAISERROR (N'SQL server evaluation script  adrian@sqldba.org ?',0,1) WITH NOWAIT;
Thanks:
Robert Wylie
Nav Mukkasa
RAISERROR (NCHAR(65021),0,1) WITH NOWAIT;
--Clean up
DROP PROCEDURE [master].[dbo].[sp_triage®]

*/ 
 /*@TopQueries. How many queries need to be looked at, TOP xx*/
  @TopQueries [INT]  = 500 
/*@FTECost. Average price in $$$ that you pay someone at your company every year.*/
, @FTECost MONEY   = 70000
, @CleanUpIsle9 [INT] = 0 /*No effect; kept so positional callers do not shift.*/
/*@ShowQueryPlan. Set to 1 to include the Query plan in the output*/
, @ShowQueryPlan [INT]  = 0
/*@PrepForExport. No effect; kept so positional callers do not shift.*/
, @PrepForExport [INT]  = 1 
/*@ShowMigrationRelatedOutputs. WHEN you need to show migration stuff, like possible breaking connections and DMA script outputs, set to 1 to show information*/
, @ShowMigrationRelatedOutputs [INT] = 1 
, @SkipHeaps [INT] = 0 /*Set to 1 to Skip Heap Table Checks. These can be intensive*/
/* @EnableXpCmdShell. 0 (default) = no host command runs, even on a server where xp_cmdshell is ALREADY on:
   the script calls no sp_configure and no xp_cmdshell, and the one host-probe section (SPN registration
   via setspn) reports "skipped: opt-in (@EnableXpCmdShell = 1 runs setspn)".
   1 (opt-in) = if xp_cmdshell is OFF, switch xp_cmdshell (and show advanced options, if that is off
   too) on for this run, and put BOTH settings back when the run reaches its end. A timeout, a cancel
   or a KILL leaves both on, because a script cannot run code after an attention; SQLTriage's own
   restore covers runs started from SQLTriage. If the switch-on fails, the SPN section reports
   "skipped: xp_cmdshell could not be enabled". Default changed 1 -> 0 on 2026-10-10; with 0 the probe
   stopped running on already-on servers on 2026-10-16. */
, @EnableXpCmdShell BIT = 0

/* DEPRECATED 2026-07-02: mail delivery was removed (client-PII egress + EXECUTE AS 'sa'; see the
   REMOVED note near the housekeeping section). These two params are RETAINED as no-ops purely so
   existing callers that still pass @MailResults / @EmailRecipients don't fail with a parameter
   error; they do nothing. Remove them once no caller references them. */
, @MailResults BIT = 0
, @EmailRecipients [NVARCHAR] (500) = NULL
 /*Screen / Table*/
, @Export [NVARCHAR] (10) = 'TABLE'
, @ShowOnScreenWhenResultsToTable [INT] = 1 

, @ExportSchema [NVARCHAR] (25)  = 'dbo'
, @ExportDBName  [NVARCHAR] (20) = 'master'
, @ExportTableName [NVARCHAR] (55) = 'sqldba_sp_triage®_output'
, @ExportCleanupDays [INT] = 180 /*TABLE runs only: export rows older than the SMALLER of @ExportCleanupDays and @CleanupTime (days) are deleted; NULL on both, or a negative value, = keep everything. ONE delete, inside the @Export = TABLE block (2026-10-16); a SCREEN run deletes nothing.*/
/* @PrintMatrixHeader. Added to turn it off since some control chars coming through stopping a copy/paste from the messages window in SSMS */
, @PrintMatrixHeader [INT] = 1
, @Debug BIT =0 /*0 is off, 1 is on. Default OFF so a client-facing run is quiet; set 1 for internal step/error RAISERROR trace*/
, @ShowDebugTime BIT =0 /*0 is off, 1 is on, shows execution times*/
, @CleanupTime [INT] = 180 /*TABLE runs only: export rows older than the SMALLER of @CleanupTime and @ExportCleanupDays (days) are deleted; NULL on both, or a negative value, = keep everything. Both parameters are kept so positional callers do not shift; ONE delete reads them (2026-10-16).*/
, @BufferScanMaxGB [INT] = 64 /*Per-DB buffer-pool residency (sys.dm_os_buffer_descriptors) is only scanned when the pool holds at most this many GB of data pages - the scan takes minutes on big-memory servers. NULL or 0 = always skip; section 14 and the usage-likelihood section then report 0 buffer pages. Appended after @CleanupTime on purpose - legacy positional callers passing through @CleanupTime must not shift.*/
, @PerfCounterSampleSeconds [INT] = 10 /*Length of the in-run performance-counter sample behind section 121 (right now): two readings this many seconds apart. 0 or NULL = off (section 121 says so); clamped to 1..300. Every run that samples takes this much longer. Appended LAST on purpose - positional callers must not shift (lane sp-triage-perf-counter-summaries, 2026-10-21; hub ruling on the 10 s default).*/
, @ExcludeSystemDBs BIT = 0 /*1 = the per-database checks skip master, model, msdb and tempdb. Other sections still read them.*/
, @TopIORankDBs [INT] = 20 /*Per-database checks run on every user database when there are at most this many. Above that: the N busiest by disk reads and writes since SQL Server started, plus the 5 largest by allocated size. The output names the rest. NULL or 0 = every database.*/
/*@Version, @VersionDate, @VersionCheckMode. The version record, the sp_Blitz shape. @VersionCheckMode = 1 fills the two OUTPUT parameters and returns at once, so a person can read which copy is installed without a run. The Version row in the output (SectionID 0) shows the same value.*/
, @Version [VARCHAR] (30) = NULL OUTPUT
, @VersionDate DATETIME = NULL OUTPUT
, @VersionCheckMode BIT = 0
, @ErrorLogMaxMB [INT] = 50 /*Error log files over this many MB are not read: the SQL Server log archives sp_triage reads and the Agent log it reads. Each one left out gets a row in its error-log section. The current SQL Server log (archive 0) over the cap is read for its last 7 days only. Default 50. NULL = no cap: every log is read as before. Appended LAST on purpose - positional callers must not shift (lane bundle-sp-triage-5).*/
WITH RECOMPILE/*, ENCRYPTION*/
AS
BEGIN
	SET NOCOUNT ON;
	SET ANSI_NULLS ON;
	SET ANSI_PADDING ON;
	SET ANSI_WARNINGS ON;
	SET ARITHABORT ON;
	SET CONCAT_NULL_YIELDS_NULL ON;
	SET QUOTED_IDENTIFIER ON;
	SET STATISTICS IO OFF;
	SET STATISTICS TIME OFF;
	SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
	/* 2026-10-16 (member 5): no step waits on a lock longer than 5000 ms. A step that times out raises 1222
	   inside its TRY and records its Could not run row. LOCK_TIMEOUT bounds lock waits only (not scans, latches, xp
	   calls or DBCC). Both settings revert when the procedure returns; they are reset before the export-table block.
	   A constant, not a parameter: SET LOCK_TIMEOUT takes no variable (Msg 102, PROVED on OLD2017 2026-10-16). */
	SET LOCK_TIMEOUT 5000;
	SET DEADLOCK_PRIORITY LOW;

	SELECT @Version = '2026.11.02', @VersionDate = '20261102';
	IF @VersionCheckMode = 1 RETURN;
	/* The run's start (lane bundle-sp-triage-5, M3a): read by the one total-time line at the end of the run (IF @Debug = 1). */
	DECLARE @starttime DATETIME;
	SET @starttime = GETDATE();

	/* Invariant 2 (2026-10-16): every step either emits its rows or records ONE skip here. The FIRST
	   statement of every BEGIN CATCH before the output fix-up is
	     INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'<step>', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	   and ONE INSERT near the end turns these rows into "Could not run" output rows (Severity 8).
	   Guarded by SpTriageSkipRowCensusTests (a ScriptDom walk). Created before the first TRY. */
	CREATE TABLE #sp_triage_skipped
	(
		Seq INT IDENTITY(1,1)
		, Step [NVARCHAR] (400)
		, ErrorNumber INT
		, ErrorMessage [NVARCHAR] (4000)
	);

	/* Invariant 3 (lane sp-triage-offline-database-aborts-run, 2026-10-20): NO DATABASE STATE MAKES sp_triage ABORT.
	   OFFLINE, RESTORING, RECOVERING, RECOVERY_PENDING, SUSPECT, EMERGENCY, SINGLE_USER or RESTRICTED_USER: a step that
	   cannot read a database leaves it out and the run goes on to the output. Every statement that OPENS a database
	   other than the current one takes its databases from the ONE guard below:
	     #sp_triage_readable_db   online (state 0), MULTI_USER, HAS_DBACCESS, and not an AG secondary that refuses
	                              connections. Per-database loops (@Databases, @DatabasesForLOG, @cc_list) are filled from it.
	     #sp_triage_readable_plan the captured plan handles whose plan dbid is in that list (or the resource database),
	                              filled once after the query-stats capture. Every dm_exec_query_plan / dm_exec_sql_text /
	                              dm_exec_text_query_plan reads only these handles, and sits inside a TRY whose CATCH
	                              records the skip (a plan that names an unreadable database from another one still fails
	                              only its own step). dbid is the plan's own database (dm_exec_plan_attributes), so a
	                              section that needs only that number never opens the plan (lane bundle-sp-triage-fixes-b).
	   Reading the ROWS of sys.databases / sys.master_files is not opening a database and stays legal: 3001 must still see an
	   offline database to report it. Guarded by SpTriageDatabaseStateGuardCensusTests (a ScriptDom walk; dynamic SQL is text
	   to it, and that class says what it cannot see). */
	CREATE TABLE #sp_triage_readable_db (database_id INT NOT NULL PRIMARY KEY, name SYSNAME NOT NULL);
	CREATE TABLE #sp_triage_readable_plan (plan_handle VARBINARY(64) NOT NULL PRIMARY KEY, dbid INT NULL);
	BEGIN TRY
		INSERT #sp_triage_readable_db (database_id, name)
		SELECT d.database_id, d.name
		FROM sys.databases AS d
		WHERE CASE WHEN d.state = 0 AND d.user_access = 0 THEN ISNULL(HAS_DBACCESS(d.name), 0) ELSE 0 END = 1;
		IF CONVERT(INT, ISNULL(SERVERPROPERTY('IsHadrEnabled'), 0)) = 1
			EXEC sp_executesql N'DELETE r FROM #sp_triage_readable_db AS r
				WHERE EXISTS (SELECT 1 FROM sys.dm_hadr_database_replica_states AS rs
					INNER JOIN sys.availability_replicas AS rep ON rep.replica_id = rs.replica_id
					WHERE rs.database_id = r.database_id AND ISNULL(rs.is_local, 0) = 1
					AND ISNULL(rs.is_primary_replica, 1) = 0 AND ISNULL(rep.secondary_role_allow_connections, 1) = 0);';
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Readable database list', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	END CATCH

	DECLARE @MagicVersion [NVARCHAR] (25)
	SET @MagicVersion = @Version /*the version record above*/
	DECLARE @License [NVARCHAR] (4000)
	SET @License = '----------------
	GNU GENERAL PUBLIC LICENSE
	
	sp_triage® is a SQL server diagnostic tool created and maintained by Adrian Sullivan.
	Copyright (C) ' + CONVERT([VARCHAR](4),DATEPART(YEAR,GETDATE())) + ' Adrian Sullivan and SQLDBA.ORG
	sp_triage® is a REGISTERED TRADEMARK

    This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation,
	either version 3 of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
    You can find a copy of the GNU General Public License here <https://www.gnu.org/licenses/>.

	WHEN things start going poorly for you WHEN you run this script, get in touch with me +64274241489, or on linkedin.com/in/milliondollardba/, or adrian@sqldba.org

	The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
	----------------
	Calibrating flux capacitor..
	Engaging time circuits..
	1.19 jigowatts..
	1.20 jigowatts..
	1.21 jigowatts....
	Ready!
	'

/* Guidelines:
	1. Each DECLARE on a new line
	2. Each column on a new line
	3. ", " in from of new lines
	4. All (table columns) have ( and ) in new lines with tab indent
	5. All ends with ";"
	6. All comments in / * * / not --
	7. All descriptive comments above DECLAREs
		Comments can also be in SET @comment = ''
	8. All Switches are 0=off and 1=on and [INT] type
	9. SELECT -option- <first column>
	, <column>
	FROM.. where more than 1 column is returned, or whatever reads better
	OPTION (RECOMPILE)
	Section:
	DECLARE section variables
	--------
	Do stuff
*/


/*Escape the export schema, database and table names with QUOTENAME (a ] inside a name is doubled) unless the caller passed them already bracketed. Guarded by SpTriageDbNameQuotingCensusTests (2026-10-16).*/
IF(LEFT(@ExportSchema,1) <> '[')
	BEGIN
		SET @ExportSchema = QUOTENAME(@ExportSchema)
	END
IF(LEFT(@ExportDBName,1) <> '[')
	BEGIN
		SET @ExportDBName = QUOTENAME(@ExportDBName)
	END
IF(LEFT(@ExportTableName,1) <> '[')
	BEGIN
		SET @ExportTableName = QUOTENAME(@ExportTableName)
	END

/*Before anything, start a trace and get some information.*/
DECLARE @IsSQLAzure BIT
DECLARE @IsSQLMI BIT
SET @IsSQLAzure = 0
SET @IsSQLMI = 0   /* init (was left NULL; only SET=1 on EngineEdition 8) so BIT comparisons aren't 3-valued */
IF (SELECT ServerProperty('EngineEdition')) = 5 
	SET @IsSQLAzure = 1
IF (SELECT ServerProperty('EngineEdition')) = 8
	SET @IsSQLMI = 1
/* Platforms (lane bundle-sp-triage-5, M4). Azure SQL Database (@IsSQLAzure, EngineEdition 5) and Managed Instance
   (@IsSQLMI, EngineEdition 8) are set just above. Amazon RDS: the vendored sp_Blitz test (both machine names start
   EC2AMAZ- and an rdsadmin database exists). Linux: @@VERSION says so (sys.dm_os_host_info would need a 2008 guard).
   Every predicate is a 2008 built-in. */
DECLARE @IsRDS BIT
DECLARE @IsLinux BIT
SET @IsRDS = 0
SET @IsLinux = 0
IF LEFT(CAST(SERVERPROPERTY('ComputerNamePhysicalNetBIOS') AS [VARCHAR] (8000)), 8) = 'EC2AMAZ-'
	AND LEFT(CAST(SERVERPROPERTY('MachineName') AS [VARCHAR] (8000)), 8) = 'EC2AMAZ-'
	AND DB_ID(N'rdsadmin') IS NOT NULL
	SET @IsRDS = 1
IF @@VERSION LIKE N'%on Linux%'
	SET @IsLinux = 1
/* M4 platform arm (lane bundle-sp-triage-5; Adrian Q2 = B). Invariant: on every platform no
   recorded run proves, the report's FIRST row says so; nothing else about the run changes. It never RETURNs and skips no
   check. One row, precedence MI > Azure SQL Database > RDS > Linux. The row is written right after
   #output_sqldba_org_sp_triage is created, so it holds the lowest ID: the screen, the export table and the app all read
   ORDER BY ID. */
DECLARE @PlatformNotTested [NVARCHAR] (100)
SET @PlatformNotTested = CASE WHEN @IsSQLMI = 1 THEN N'Azure SQL Managed Instance' WHEN @IsSQLAzure = 1 THEN N'Azure SQL Database'
	WHEN @IsRDS = 1 THEN N'Amazon RDS' WHEN @IsLinux = 1 THEN N'SQL Server on Linux' ELSE NULL END


DECLARE @DebugTime DATETIME
DECLARE @DebugTimeMSG [VARCHAR](500)	
DECLARE @errMessage [VARCHAR](MAX) 
SET @errMessage = ERROR_MESSAGE()
SET @DebugTime = GETDATE()
DECLARE @ThisServer [NVARCHAR] (500)
DECLARE @CharToCheck [NVARCHAR] (5) 
SET @CharToCheck = CHAR(92)
BEGIN TRY
  IF (select CHARINDEX(@CharToCheck,@@SERVERNAME)) > 0
  /*Named instance will always use NetBIOS name*/
    SELECT @ThisServer = @@SERVERNAME
  IF (select CHARINDEX(@CharToCheck,@@SERVERNAME)) = 0
  /*Not named, use the NetBIOS name instead of @@ServerName*/
    SELECT @ThisServer = CAST( Serverproperty( 'ComputerNamePhysicalNetBIOS' ) AS [NVARCHAR] (500))
	IF @IsSQLAzure = 1 OR @IsSQLMI = 1
		SELECT @ThisServer = REPLACE(@@SERVERNAME,'','')
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Server name lookup', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  SELECT @errMessage  = ERROR_MESSAGE()
  IF @Debug = 1
  BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH


DECLARE @dynamicSQL [NVARCHAR] (4000)
/*Check and Set xp_cmdshell
  @StateOfXP_CMDSHELL   = the CURRENT effective state of xp_cmdshell (read here, raised by the opt-in below).
  @HostProbesAllowed    = the ONE gate every host command tests (IF @HostProbesAllowed = 1), set below.
  @XpCmdShellWasOff     = remembers whether WE turned it on, so we can restore it to OFF at the end.
                          Fixes the old mutation bug: the script no longer leaves xp_cmdshell enabled
                          on a server where it started disabled. */
DECLARE @StateOfXP_CMDSHELL INT
DECLARE @XpCmdShellWasOff BIT = 0
DECLARE @AdvancedOptionsWasOff BIT = 0  /* 1 = THIS run switched show advanced options on; undo it at the end */
SELECT @StateOfXP_CMDSHELL = CONVERT(INT, ISNULL(value, value_in_use))
FROM  [sys].configurations
WHERE  name = 'xp_cmdshell' ;

IF @StateOfXP_CMDSHELL = 0
BEGIN
	IF @EnableXpCmdShell = 1
	BEGIN
		/* Try to enable it just for this run. If we lack ALTER SETTINGS (locked-down / Azure), fail
		   SAFE: leave @StateOfXP_CMDSHELL = 0 so every host-probe section skips gracefully. */
		BEGIN TRY
			IF (SELECT CONVERT(INT, ISNULL(value, value_in_use)) FROM sys.configurations WHERE name = 'show advanced options') = 0
			BEGIN
				EXEC sp_configure 'show advanced options', 1;
				RECONFIGURE;
				SET @AdvancedOptionsWasOff = 1; /* we changed it; remember to undo at the end */
			END;
			EXEC sp_configure 'xp_cmdshell', 1;
			RECONFIGURE;
			SET @XpCmdShellWasOff = 1;      /* we changed it; remember to undo at the end */
			SET @StateOfXP_CMDSHELL = 1;    /* probes may now run */
			RAISERROR ('xp_cmdshell was OFF; enabled for this run only. xp_cmdshell and show advanced options will be restored to their state before this run at the end.',0,1) WITH NOWAIT;
		END TRY
		BEGIN CATCH
			INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Enable xp_cmdshell for this run', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
			SET @StateOfXP_CMDSHELL = 0;    /* stay skipped */
			IF @AdvancedOptionsWasOff = 1
			BEGIN TRY
				EXEC sp_configure 'show advanced options', 0;
				RECONFIGURE;
				SET @AdvancedOptionsWasOff = 0;
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Restore show advanced options after a failed enable', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				RAISERROR ('WARNING: could not restore show advanced options to 0 - please set it manually.',0,1) WITH NOWAIT;
			END CATCH
			RAISERROR ('Could not enable xp_cmdshell (insufficient rights or Azure); host-probe sections will be skipped.',0,1) WITH NOWAIT;
		END CATCH
	END
	ELSE
	BEGIN
		RAISERROR ('xp_cmdshell disabled and @EnableXpCmdShell=0; host-probe sections (SPN) will be skipped.',0,1) WITH NOWAIT;
	END
END

/* Invariant 1 (2026-10-16): with default parameters NO host command runs (xp_cmdshell, sp_OA*,
   sp_execute_external_script, a CmdExec/PowerShell job step), even when xp_cmdshell is already on.
   Every such call sits in the THEN branch of IF @HostProbesAllowed = 1 and nowhere else; guarded by
   SpTriageHostCommandCensusTests (a ScriptDom walk). Do not test @StateOfXP_CMDSHELL in a probe. */
DECLARE @HostProbesAllowed BIT = 0;
IF @EnableXpCmdShell = 1 AND @StateOfXP_CMDSHELL = 1
	SET @HostProbesAllowed = 1;

/* Invariant (2026-11-06): a run by a login that is not sysadmin sends the client NO error and returns its report.
   (a) A registry read (the extended procedures xp_regread and xp_instance_regread) that the login may not run is not
   a catchable error: even inside TRY it flags its statement and SqlClient throws. So every registry read sits in the
   THEN branch of IF @CanReadRegistry = 1, its ELSE writes one skip row, and the SET below is the ONE write of the
   flag. (b) Every other read such a login can be denied (a DMV, an msdb table, the error log, another tool's output
   table in master that the login can see but not read) sits in a TRY whose CATCH writes the skip row: proved on SQL
   Server 2022 16.0.4305.5 and 2017 14.0.2140.2 with six login permission shapes, none sysadmin. Two reads stay outside
   TRY because none of those shapes was denied them: xp_fixeddrives (INSERT ... EXEC) and the msdb backupset read
   (a skip there would also need the backup findings that read it held back). Guarded by
   SpTriageRegistryReadGuardCensusTests (a ScriptDom walk) and run as each login shape by
   SpTriageLowPrivilegeRunLiveTests. */
DECLARE @CanReadRegistry BIT;
SET @CanReadRegistry = CASE WHEN @IsSQLAzure = 0 AND @IsSQLMI = 0 AND ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0) = 1 THEN 1 ELSE 0 END;

/* Invariant (2026-11-06): a finding row is written only from data that was read. When the read a finding
   depends on was skipped (a CATCH skip row, a guard's ELSE, or a column the login cannot see), the finding is not
   written and the skip row says why. One flag per skipped source, each set to 1 in ONE place. Run as each login
   shape that is not sysadmin, beside one sysadmin run on the same instance, by SpTriageLowPrivilegeRunLiveTests.
   @CanReadPasswordHash: sys.sql_logins.password_hash reads NULL for a login without CONTROL SERVER, so the password
     checks read the logins only with it; else one skip row 'Password checks'.
   @RestartInfoRead: 1 only when the restart-info error-log read completed and no archive the startup lines could be in
     was left out by the size cap. The Locked Pages and Instant File Initialization rows judge by what that read found
     or did not find, so they are written only when it is 1.
   @QueryStatsCaptured: 1 when the plan-cache capture (#dadatafor_exec_query_stats) ran without an error. It can still
     hold no row (an empty plan cache), so the cache-age bar and the section 99 'Oldest Cache' figure need it AND a read
     cache age (@oldestcachequery IS NOT NULL).
   @ProcIORead: 1 only when dm_exec_procedure_stats was read. The section 99 PROC I/O figure and its DELTA row need it. */
DECLARE @CanReadPasswordHash BIT;
SET @CanReadPasswordHash = CASE WHEN ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'CONTROL SERVER'), 0) = 1 THEN 1 ELSE 0 END;
DECLARE @RestartInfoRead BIT = 0;
DECLARE @QueryStatsCaptured BIT = 0;
DECLARE @ProcIORead BIT = 0;

    DECLARE @Result_Good [NVARCHAR] (2);
    DECLARE @Result_NA [NVARCHAR] (2);
    DECLARE @Result_Warning [NVARCHAR] (2);
    DECLARE @Result_Bad [NVARCHAR] (2);
    DECLARE @Result_ReallyBad [NVARCHAR] (2);
    DECLARE @Result_YourServerIsDead [NVARCHAR] (2);
	DECLARE @Sparkle [NVARCHAR] (2);

    SET @Result_Good =  N'1';
	/*NCHAR(10004)*/;
    SET @Result_NA = N'2';
	/*NCHAR(9940)*/;
    SET @Result_Warning = N'3';
	/*NCHAR(9888)*/;
    SET @Result_Bad = N'4';
	/*NCHAR(10006)*/;
    SET @Result_ReallyBad = N'5';
	/*NCHAR(9763)*/;
    SET @Result_YourServerIsDead = N'6';
	/*NCHAR(9760)*/;
	SET @Sparkle = N'7';
	/* 8 = a step that could not run: one "Could not run" row per failed step, written in ONE place from
	   #sp_triage_skipped just before the final output fix-up (Invariant 2). */
	DECLARE @Result_CouldNotRun [NVARCHAR] (2);
	SET @Result_CouldNotRun = N'8';
	

	
    IF (@PrintMatrixHeader <> 0)
    BEGIN
        DECLARE @matrixthis [BIGINT];
        SET @matrixthis = 0;
        DECLARE @matrixthisline [INT];
        SET @matrixthisline= 0;
        DECLARE @sliverofawesome [NVARCHAR] (200);
        DECLARE @thischar [NVARCHAR] (1);

        WHILE @matrixthis < 25
        BEGIN
            SET @matrixthisline = 0;
            SET @sliverofawesome = '';

            WHILE @matrixthisline < 90
            BEGIN
                SET @thischar = NCHAR(CONVERT(INT,RAND() *  1252));
                IF LEN(@thischar) = 0 OR RAND() < 0.8
                    SET @thischar = ' ';
                SET @sliverofawesome = @sliverofawesome + @thischar;
                SET @matrixthisline = @matrixthisline + 1;
            END
            --PRINT (@sliverofawesome) ;
            SET @matrixthis = @matrixthis + 1;
            --WAITFOR DELAY '00:00:00.011';
        END


        DECLARE @c_r AS CHAR(2) ;
        SET @c_r = CHAR(13) + CHAR(10);

	   -- PRINT REPLACE(REPLACE(REPLACE(REPLACE(''+@c_r+'	[   ....,,:,,....[[ '+@c_r+'[   ,???????????????????:.[   '+@c_r+'[ .???????????????????????,[  '+@c_r+'s=.  ??????&&&$$??????. .7s '+@c_r+'s~$.. ...&&&&&... ..7Is '+@c_r+'s~&$+....[[.. =7777Is '+@c_r+'s~&&&&$$7I777Iv7777I[[  '+@c_r+'s~&&&&$$Ivv7777Is '+@c_r+'s~&$$... &$.. ..777?..vIs '+@c_r+'s~&$  &$$.  77?..77? .vIs '+@c_r+'s~&$. .&$  $I77=  7? .vIs '+@c_r+'s~&$$,. .$$ .$I777..7? .vIs '+@c_r+'s~&&$+ .$  ~I77. ,7? .vIs '+@c_r+'s~&$..   & ...  :77? ....77Is '+@c_r+'s~&&&&$$I:..vv7I[ '+@c_r+'s~&&&&$$Ivv7777Is '+@c_r+'s.&&&&$$Ivv7777.s '+@c_r+'s .&&&&$Ivv777.['+@c_r+'[ ..7&&&Ivv..[  '+@c_r+'[[........... ..[[ ', '&','$$$'),'v', '77777'),'[', '      '),'s','    ')
	   -- PRINT REPLACE(REPLACE(REPLACE(REPLACE('.m__._. _.m__. __.__.. _.. _. _. m_..m_.m_. m.m_.m__ '+@c_r+' |_. _|g g| m_g.\/.i / \. | \ g / mi/ m|i_ \ |_ _|i_ \|_. _|'+@c_r+'. g.g_gi_i g\/g./ _ \.i\g \m \ g..g_) g g |_) g i'+@c_r+'. g.i_.|gm.g.g / m \ g\.im) |gm i_ <.g i__/.g.'+@c_r+'. |_i|_g_||m__g_i|_|/_/. \_\|_| \_gm_/.\m_||_| \_\|m||_i. |_i'+@c_r+'........................................... ','i','|.'),'.','  '),'m','___'),'g','| |')
    END

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @License) WITH NOWAIT; 
		SELECT @errMessage  = ERROR_MESSAGE();
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
	--PRINT 'Let''s do this!';
	
	/*@ShowWarnings = 0 > Only show warnings */
	DECLARE @ShowWarnings [INT] ;
	SET @ShowWarnings = 0;

	/*Script wide variables*/
	DECLARE @DaysUptime NUMERIC(23,2);
	
	SET @dynamicSQL = N'';
	DECLARE @MinWorkerTime [BIGINT] ;
	SET @MinWorkerTime = CONVERT(INT,0.01 * 1000000);
	DECLARE @MinChangePercentage MONEY ;
	DECLARE @DoStatistics MONEY ;
	SET @MinChangePercentage = 5; /*Assume 5% on */
	DECLARE @LeftText [INT] ;
	SET @LeftText = 1000; /*The length that you want to trim text*/
	DECLARE @oldestcachequery DATETIME ;
	DECLARE @minutesSinceRestart [BIGINT];
	DECLARE @CPUcount INT;
	DECLARE @CPUsocketcount INT;
	DECLARE @CPUHyperthreadratio MONEY ;
	DECLARE @TempDBFileCount INT;
	DECLARE @lastservericerestart DATETIME;
	DECLARE @serverinstalldate DATETIME;
	DECLARE @DaysOldestCachedQuery MONEY ;
	DECLARE @CachevsUpdate MONEY ;
	DECLARE @Databasei_Count INT;
	DECLARE @Databasei_Max INT;
	DECLARE @DatabaseName SYSNAME;
	DECLARE @DatabaseState INT;
	DECLARE @RecoveryModel INT;
	DECLARE @comment [NVARCHAR] (MAX);
	DECLARE @StartTest DATETIME 
	DECLARE @EndTest DATETIME; 
	DECLARE @ThisistoStandardisemyOperatorCostMate INT;
	DECLARE @secondsperoperator FLOAT;
	DECLARE @totalMemoryGB MONEY 
	DECLARE @AvailableMemoryGB MONEY 
	DECLARE @UsedMemory MONEY ;
	DECLARE @MemoryStateDesc [NVARCHAR] (50);
	DECLARE @VMType [NVARCHAR] (200)
	DECLARE @ServerType [NVARCHAR] (20);
	DECLARE @MaxRamServer INT
	DECLARE @SQLVersion INT;
	DECLARE @ts [BIGINT];
	DECLARE @Kb FLOAT;
	DECLARE @PageSize FLOAT;
	DECLARE @VLFcount INT;
	DECLARE @ReadSkippedMsg [NVARCHAR] (2000); /* per-database read failures, reported at severity 0 (2026-10-09) */
	DECLARE @LogInfoInsert [NVARCHAR] (1000);
	DECLARE @ErrorSeverity int;
	DECLARE @ErrorState int;
	DECLARE @ErrorMessage [NVARCHAR] (400);
	DECLARE @CustomErrorText [NVARCHAR] (500);
	DECLARE @sql [NVARCHAR] (4000);
	/*Performance section variables*/
	DECLARE @cnt INT;
	DECLARE @record_count INT;
	DECLARE @dbid INT;
	DECLARE @objectid INT;
	DECLARE @grand_total_worker_time FLOAT ; 
	DECLARE @grand_total_IO FLOAT ; 
	DECLARE @evaldate [NVARCHAR] (20);
	DECLARE @TotalIODailyWorkload MONEY ;
	DECLARE @TotalProcIODailyWorkload MONEY ;  /*2026-07-02: proc-level I/O proxy from dm_exec_procedure_stats, same units + same @DaysOldestCachedQuery divisor as @TotalIODailyWorkload so the two are comparable*/
	
	SET @evaldate = CONVERT([VARCHAR](20),GETDATE(),120);

	SELECT @SQLVersion = @@MicrosoftVersion / 0x01000000  OPTION (RECOMPILE);-- Get major version
	DECLARE @sqlrun [NVARCHAR] (4000), @rebuildonline [NVARCHAR] (30), @isEnterprise INT, @i_Count INT, @i_Max INT;
	
DECLARE @SP_MachineName [NVARCHAR] (50);
DECLARE @SP_INSTANCENAME [NVARCHAR] (50);
DECLARE @SP_PRODUCTVERSION [NVARCHAR] (50);
DECLARE @SP_SQL_VERSION [NVARCHAR] (50);
DECLARE @SP_PRODUCTLEVEL [NVARCHAR] (50);
DECLARE @SP_EDITION [NVARCHAR] (50);
DECLARE @SP_ISCLUSTERED [NVARCHAR] (50);



/*Populate some SQL Server Properties first*/
DECLARE @ThisDomain [NVARCHAR] (100);
IF @IsSQLAzure = 0 AND @IsSQLMI = 0  /* was OR: two mutually-exclusive flags OR'd is always TRUE, so xp_regread ran on Azure/MI too (survived only via the TRY/CATCH) */
	BEGIN
	BEGIN TRY
		/* A direct EXEC since 2026-11-06 (it ran as dynamic SQL inside INSERT ... EXEC from the first commit, no reason
		   recorded), so a parse of the script sees the call beside its guard. */
		IF @CanReadRegistry = 1
			EXEC master.dbo.xp_regread N'HKEY_LOCAL_MACHINE', N'SYSTEM\CurrentControlSet\services\Tcpip\Parameters', N'Domain', @ThisDomain OUTPUT;
		ELSE
			INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (N'Domain name', 0, N'skipped: reading the Windows registry needs sysadmin');
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'SQL Server properties', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
		PRINT 'The domain name was not read from the registry';
	END CATCH
END
IF (@IsSQLAzure = 1 OR @IsSQLMI = 1) 
		SELECT @ThisDomain = RIGHT(SYSTEM_USER,LEN(SYSTEM_USER) - CHARINDEX('@',SYSTEM_USER)) /*CONVERT([NVARCHAR] (100),SERVERPROPERTY('ServerName'))+'.database.windows.net';*/
SET @ThisDomain = ISNULL(@ThisDomain, DEFAULT_DOMAIN());

SELECT 
@SP_MachineName = CONVERT([NVARCHAR] (50),SERVERPROPERTY('MACHINENAME') )
, @SP_INSTANCENAME = ISNULL(CONVERT([NVARCHAR] (50),SERVERPROPERTY('INSTANCENAME') ),'')
, @SP_PRODUCTVERSION = CONVERT([NVARCHAR] (50),SERVERPROPERTY('PRODUCTVERSION') )
, @SP_SQL_VERSION = CASE
WHEN LEFT(CONVERT([NVARCHAR] (5),SERVERPROPERTY('PRODUCTVERSION')),2) = '17'
	THEN 'SQL SERVER 2025'
WHEN LEFT(CONVERT([NVARCHAR] (5),SERVERPROPERTY('PRODUCTVERSION')),2) = '16'
	THEN 'SQL SERVER 2022'
WHEN LEFT(CONVERT([NVARCHAR] (5),SERVERPROPERTY('PRODUCTVERSION')),2) = '15'
	THEN 'SQL SERVER 2019'
WHEN LEFT(CONVERT([NVARCHAR] (5),SERVERPROPERTY('PRODUCTVERSION')),2) = '14'	
	THEN 'SQL SERVER 2017' 
WHEN LEFT(CONVERT([NVARCHAR] (5),SERVERPROPERTY('PRODUCTVERSION')),2) = '13'	
	THEN 'SQL SERVER 2016' 
WHEN LEFT(CONVERT([NVARCHAR] (5),SERVERPROPERTY('PRODUCTVERSION')),2) = '12'	
	THEN 'SQL SERVER 2014' 
WHEN LEFT(CONVERT([NVARCHAR] (5),SERVERPROPERTY('PRODUCTVERSION')),2) = '11'	
	THEN 'SQL SERVER 2012' 
WHEN LEFT(CONVERT([NVARCHAR] (5),SERVERPROPERTY('PRODUCTVERSION')),4) ='10.5'	
	THEN 'SQL SERVER 2008R2' 
WHEN LEFT(CONVERT([NVARCHAR] (5),SERVERPROPERTY('PRODUCTVERSION')),4) ='10.0'	
	THEN 'SQL SERVER 2008' 
WHEN LEFT(CONVERT([NVARCHAR] (5),SERVERPROPERTY('PRODUCTVERSION')),1) ='9'		
	THEN 'SQL SERVER 2005' 
WHEN LEFT(CONVERT([NVARCHAR] (5),SERVERPROPERTY('PRODUCTVERSION')),1) ='8'
	THEN 'SQL SERVER 2000'
ELSE 'SQL SERVER (major ' + PARSENAME(CONVERT([NVARCHAR](50),SERVERPROPERTY('PRODUCTVERSION')),4) + ')' /*future-proof: never NULL on a newer/unmapped build*/
END
, @SP_PRODUCTLEVEL = CONVERT([NVARCHAR] (50),SERVERPROPERTY('PRODUCTLEVEL') )
, @SP_EDITION = CONVERT([NVARCHAR] (50),SERVERPROPERTY('EDITION'))
, @SP_ISCLUSTERED = CONVERT([NVARCHAR] (50),SERVERPROPERTY('ISCLUSTERED')  );

/*Create #temp tables*/

DECLARE @sysdatabasesTable TABLE([database_id] INT
	, [name] [NVARCHAR] (500)
	, [compatibility_level]TINYINT
	, [create_date] DATETIME
	, [recovery_model_desc] [NVARCHAR] (500)
	, [is_ansi_nulls_on] BIT
	, [is_ansi_padding_on] BIT
	, [is_ansi_warnings_on] BIT
	, [is_arithabort_on] BIT
	, [is_concat_null_yields_null_on] BIT
	, [is_numeric_roundabort_on] BIT
	, [is_quoted_identifier_on] BIT
	, [is_published] BIT
	, [is_subscribed] BIT
	, [is_merge_published] BIT
	, [state] TINYINT
	, [user_access] TINYINT)

SET @dynamicSQL = 'SELECT 
	[database_id]
	, [name]
	, [compatibility_level]
	, [create_date]
	, [recovery_model_desc]
	, [is_ansi_nulls_on]
	, [is_ansi_padding_on]
	, [is_ansi_warnings_on]
	, [is_arithabort_on]
	, [is_concat_null_yields_null_on]
	, [is_numeric_roundabort_on]
	, [is_quoted_identifier_on]
	, [is_published]
	, [is_subscribed]
	, [is_merge_published]
	, [state]
	, [user_access]
	FROM [sys].databases'
INSERT @sysdatabasesTable
EXEC sp_executesql @dynamicSQL ;
	DECLARE @FileSize TABLE
	(
		[DatabaseName] sysname
		, [FileName] [NVARCHAR] (500) NULL
		, [FileSize] [BIGINT] NULL
		, [FileGroupName] [NVARCHAR] (500)NULL
		, [LogicalName] [NVARCHAR] (500) NULL
		, [maxsize] MONEY  NULL
		, [growth] MONEY  NULL
		, rid INT IDENTITY(1,1) NOT NULL
		, UNIQUE NONCLUSTERED ([DatabaseName], rid) /*perf 2026-07-02: this table var is GROUP BY/JOINed on DatabaseName in 2 output queries; the key enables seeks + real cardinality (no logic change). SQL Server 2008 has no inline INDEX on a table variable, so a UNIQUE key made unique by rid stands in for the index*/ /*census: RawDiagnosticsFootprintTests.Shipped_sp_triage_parses_as_SQL_Server_2008*/
	);
	DECLARE @FileStats TABLE 
	(  
		[FileID] INT
		, [FileGroup][INT]  NULL
		, [TotalExtents] [INT]  NULL
		, [UsedExtents] [INT]  NULL
		, [LogicalName] [NVARCHAR] (500)  NULL
		, [FileName] [NVARCHAR] (500)  NULL
	);
	DECLARE @LogSpace TABLE 
	( 
		[DatabaseName] [NVARCHAR] (500) NULL
		, [LogSize] FLOAT NULL
		, [SpaceUsedPercent] FLOAT NULL
		, [Status] bit NULL
	);

	IF OBJECT_ID('tempdb..#NeverUsedIndex') IS NOT NULL
				DROP TABLE #NeverUsedIndex;
			CREATE TABLE #NeverUsedIndex 
			(
				[DB] [NVARCHAR] (250)
				, [Consideration] [NVARCHAR] (50)
				, [TableName] [NVARCHAR] (50)
				, [TypeDesc] [NVARCHAR] (50)
				, [IndexName] [NVARCHAR] (250)
				, [Updates] [BIGINT]
				, [last_user_scan] DATETIME
				, [last_user_seek] DATETIME
				
			);
	IF OBJECT_ID('tempdb..#dadatafor_exec_query_stats') IS NOT NULL
			DROP TABLE #dadatafor_exec_query_stats;

		CREATE TABLE #dadatafor_exec_query_stats(
				[sql_handle] [VARBINARY](64) NOT NULL
				, [statement_start_offset] [int] NOT NULL
				, [statement_end_offset] [int] NOT NULL
				, [plan_generation_num] [BIGINT] NULL
				, [plan_handle] [VARBINARY](64) NOT NULL
				, [creation_time] [DATETIME] NULL
				, [last_execution_time] [DATETIME] NULL
				, [execution_count] [BIGINT] NOT NULL
				, [total_worker_time] [BIGINT] NOT NULL
				, [last_worker_time] [BIGINT] NOT NULL
				, [min_worker_time] [BIGINT] NOT NULL
				, [max_worker_time] [BIGINT] NOT NULL
				, [total_physical_reads] [BIGINT] NOT NULL
				, [last_physical_reads] [BIGINT] NOT NULL
				, [min_physical_reads] [BIGINT] NOT NULL
				, [max_physical_reads] [BIGINT] NOT NULL
				, [total_logical_writes] [BIGINT] NOT NULL
				, [last_logical_writes] [BIGINT] NOT NULL
				, [min_logical_writes] [BIGINT] NOT NULL
				, [max_logical_writes] [BIGINT] NOT NULL
				, [total_logical_reads] [BIGINT] NOT NULL
				, [last_logical_reads] [BIGINT] NOT NULL
				, [min_logical_reads] [BIGINT] NOT NULL
				, [max_logical_reads] [BIGINT] NOT NULL
				, [total_clr_time] [BIGINT] NOT NULL
				, [last_clr_time] [BIGINT] NOT NULL
				, [min_clr_time] [BIGINT] NOT NULL
				, [max_clr_time] [BIGINT] NOT NULL
				, [total_elapsed_time] [BIGINT] NOT NULL
				, [last_elapsed_time] [BIGINT] NOT NULL
				, [min_elapsed_time] [BIGINT] NOT NULL
				, [max_elapsed_time] [BIGINT] NOT NULL
				, [query_hash] [binary](8) NULL
				, [query_plan_hash] [binary](8) NULL
				, [total_rows] [BIGINT] NULL
				, [last_rows] [BIGINT] NULL
				, [min_rows] [BIGINT] NULL
				, [max_rows] [BIGINT] NULL
				, [statement_sql_handle] [VARBINARY](64) NULL
				, [statement_context_id] [BIGINT] NULL
				, [total_dop] [BIGINT] NULL
				, [last_dop] [BIGINT] NULL
				, [min_dop] [BIGINT] NULL
				, [max_dop] [BIGINT] NULL
				, [total_grant_kb] [BIGINT] NULL
				, [last_grant_kb] [BIGINT] NULL
				, [min_grant_kb] [BIGINT] NULL
				, [max_grant_kb] [BIGINT] NULL
				, [total_used_grant_kb] [BIGINT] NULL
				, [last_used_grant_kb] [BIGINT] NULL
				, [min_used_grant_kb] [BIGINT] NULL
				, [max_used_grant_kb] [BIGINT] NULL
				, [total_ideal_grant_kb] [BIGINT] NULL
				, [last_ideal_grant_kb] [BIGINT] NULL
				, [min_ideal_grant_kb] [BIGINT] NULL
				, [max_ideal_grant_kb] [BIGINT] NULL
				, [total_reserved_threads] [BIGINT] NULL
				, [last_reserved_threads] [BIGINT] NULL
				, [min_reserved_threads] [BIGINT] NULL
				, [max_reserved_threads] [BIGINT] NULL
				, [total_used_threads] [BIGINT] NULL
				, [last_used_threads] [BIGINT] NULL
				, [min_used_threads] [BIGINT] NULL
				, [max_used_threads] [BIGINT] NULL
				, [total_columnstore_segment_reads] [BIGINT] NULL
				, [last_columnstore_segment_reads] [BIGINT] NULL
				, [min_columnstore_segment_reads] [BIGINT] NULL
				, [max_columnstore_segment_reads] [BIGINT] NULL
				, [total_columnstore_segment_skips] [BIGINT] NULL
				, [last_columnstore_segment_skips] [BIGINT] NULL
				, [min_columnstore_segment_skips] [BIGINT] NULL
				, [max_columnstore_segment_skips] [BIGINT] NULL
				, [total_spills] [BIGINT] NULL
				, [last_spills] [BIGINT] NULL
				, [min_spills] [BIGINT] NULL
				, [max_spills] [BIGINT] NULL
				, [total_num_physical_reads] [BIGINT] NULL
				, [last_num_physical_reads] [BIGINT] NULL
				, [min_num_physical_reads] [BIGINT] NULL
				, [max_num_physical_reads] [BIGINT] NULL
				, [total_page_server_reads] [BIGINT] NULL
				, [last_page_server_reads] [BIGINT] NULL
				, [min_page_server_reads] [BIGINT] NULL
				, [max_page_server_reads] [BIGINT] NULL
				, [total_num_page_server_reads] [BIGINT] NULL
				, [last_num_page_server_reads] [BIGINT] NULL
				, [min_num_page_server_reads] [BIGINT] NULL
				, [max_num_page_server_reads] [BIGINT] NULL
			);
BEGIN TRY	

SET @dynamicSQL = 'SELECT sql_handle
, statement_start_offset
, statement_end_offset
, plan_generation_num
, plan_handle
, creation_time
, last_execution_time
, execution_count
, total_worker_time
, last_worker_time
, min_worker_time
, max_worker_time
, total_physical_reads
, last_physical_reads
, min_physical_reads
, max_physical_reads
, total_logical_writes
, last_logical_writes
, min_logical_writes
, max_logical_writes
, total_logical_reads
, last_logical_reads
, min_logical_reads
, max_logical_reads
, total_clr_time
, last_clr_time
, min_clr_time
, max_clr_time
, total_elapsed_time
, last_elapsed_time
, min_elapsed_time
, max_elapsed_time'
/* 2026-10-16 (lane bundle-sp-triage-fixes-a, member 2). Invariant: sp_triage reads no DMV column the running
   instance lacks. Every column after max_elapsed_time is selected as itself when COL_LENGTH finds it on this
   instance, else as NULL, from ONE list in the INSERT's column order (SpTriageDbNameQuotingCensusTests checks the
   order). This replaced six version branches: the documented version is not a reliable gate. SQL Server 2017 RTM
   has the columnstore columns but not total_spills, so the 2016/2017 branch failed with error 207 and left
   #dadatafor_exec_query_stats empty for every section that reads it. */
DECLARE @qsOptionalColumns [NVARCHAR] (4000);
DECLARE @qsColumn SYSNAME;
DECLARE @qsComma INT;
SET @qsOptionalColumns = N'query_hash,query_plan_hash,total_rows,last_rows,min_rows,max_rows,statement_sql_handle,statement_context_id,total_dop,last_dop,min_dop,max_dop,total_grant_kb,last_grant_kb,min_grant_kb,max_grant_kb,total_used_grant_kb,last_used_grant_kb,min_used_grant_kb,max_used_grant_kb,total_ideal_grant_kb,last_ideal_grant_kb,min_ideal_grant_kb,max_ideal_grant_kb,total_reserved_threads,last_reserved_threads,min_reserved_threads,max_reserved_threads,total_used_threads,last_used_threads,min_used_threads,max_used_threads,total_columnstore_segment_reads,last_columnstore_segment_reads,min_columnstore_segment_reads,max_columnstore_segment_reads,total_columnstore_segment_skips,last_columnstore_segment_skips,min_columnstore_segment_skips,max_columnstore_segment_skips,total_spills,last_spills,min_spills,max_spills,total_num_physical_reads,last_num_physical_reads,min_num_physical_reads,max_num_physical_reads,total_page_server_reads,last_page_server_reads,min_page_server_reads,max_page_server_reads,total_num_page_server_reads,last_num_page_server_reads,min_num_page_server_reads,max_num_page_server_reads,';
WHILE LEN(@qsOptionalColumns) > 0
BEGIN
	SET @qsComma = CHARINDEX(N',', @qsOptionalColumns);
	SET @qsColumn = LEFT(@qsOptionalColumns, @qsComma - 1);
	SET @qsOptionalColumns = SUBSTRING(@qsOptionalColumns, @qsComma + 1, 4000);
	SET @dynamicSQL = @dynamicSQL + N'
	, ' + CASE WHEN COL_LENGTH(N'sys.dm_exec_query_stats', @qsColumn) IS NOT NULL THEN QUOTENAME(@qsColumn) ELSE N'NULL' END;
END

SET @dynamicSQL = @dynamicSQL + '
	FROM [sys].dm_exec_query_stats
	WHERE (ISNULL(total_logical_writes,0) + ISNULL(total_logical_reads,0)) * execution_count >200';

	INSERT INTO #dadatafor_exec_query_stats (
				[sql_handle]
				, [statement_start_offset]
				, [statement_end_offset]
				, [plan_generation_num]
				, [plan_handle] 
				, [creation_time]
				, [last_execution_time]
				, [execution_count]
				, [total_worker_time]
				, [last_worker_time]
				, [min_worker_time]
				, [max_worker_time]
				, [total_physical_reads]
				, [last_physical_reads]
				, [min_physical_reads]
				, [max_physical_reads]
				, [total_logical_writes]
				, [last_logical_writes]
				, [min_logical_writes]
				, [max_logical_writes]
				, [total_logical_reads]
				, [last_logical_reads]
				, [min_logical_reads]
				, [max_logical_reads]
				, [total_clr_time]
				, [last_clr_time]
				, [min_clr_time]
				, [max_clr_time]
				, [total_elapsed_time]
				, [last_elapsed_time]
				, [min_elapsed_time]
				, [max_elapsed_time]
				, [query_hash] 
				, [query_plan_hash] 
				, [total_rows]
				, [last_rows]
				, [min_rows]
				, [max_rows]
				, [statement_sql_handle] 
				, [statement_context_id]
				, [total_dop]
				, [last_dop]
				, [min_dop]
				, [max_dop]
				, [total_grant_kb]
				, [last_grant_kb]
				, [min_grant_kb]
				, [max_grant_kb]
				, [total_used_grant_kb]
				, [last_used_grant_kb]
				, [min_used_grant_kb]
				, [max_used_grant_kb]
				, [total_ideal_grant_kb]
				, [last_ideal_grant_kb]
				, [min_ideal_grant_kb]
				, [max_ideal_grant_kb]
				, [total_reserved_threads]
				, [last_reserved_threads]
				, [min_reserved_threads]
				, [max_reserved_threads]
				, [total_used_threads]
				, [last_used_threads]
				, [min_used_threads]
				, [max_used_threads]
				, [total_columnstore_segment_reads]
				, [last_columnstore_segment_reads]
				, [min_columnstore_segment_reads]
				, [max_columnstore_segment_reads]
				, [total_columnstore_segment_skips]
				, [last_columnstore_segment_skips]
				, [min_columnstore_segment_skips]
				, [max_columnstore_segment_skips]
				, [total_spills]
				, [last_spills]
				, [min_spills]
				, [max_spills]
				, [total_num_physical_reads]
				, [last_num_physical_reads]
				, [min_num_physical_reads]
				, [max_num_physical_reads]
				, [total_page_server_reads]
				, [last_page_server_reads]
				, [min_page_server_reads]
				, [max_page_server_reads]
				, [total_num_page_server_reads]
				, [last_num_page_server_reads]
				, [min_num_page_server_reads]
				, [max_num_page_server_reads]
				)
	EXEC sp_executesql @dynamicSQL;
	SET @QueryStatsCaptured = 1; /* see its declaration */

	/* Index the query-stats snapshot AFTER the one-shot load (indexing before the insert would slow
	   the load). ~9 later sections GROUP BY / JOIN on plan_handle & sql_handle; without these they
	   full-scan+sort the whole heap each time. Added 2026-07-02. */
	IF OBJECT_ID('tempdb..#dadatafor_exec_query_stats') IS NOT NULL
	BEGIN TRY
		CREATE CLUSTERED INDEX CX_qs_plan ON #dadatafor_exec_query_stats([plan_handle]);
		CREATE NONCLUSTERED INDEX IX_qs_sql ON #dadatafor_exec_query_stats([sql_handle]);
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Query stats capture index', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		RAISERROR(N'Could not index #dadatafor_exec_query_stats; continuing unindexed',0,1) WITH NOWAIT;
	END CATCH



END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Query stats capture', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  IF @Debug = 1
  BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Problem inserting data into #dadatafor_exec_query_stats',0,1) WITH NOWAIT;
		SELECT @errMessage  = ERROR_MESSAGE();
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	END
END CATCH

/* Invariant 3: the ONE plan-handle guard (declared with #sp_triage_readable_db). A captured plan whose dbid is a database
   that cannot be read now is left out of every query section, and ONE skip row names those databases. dm_exec_plan_attributes
   reads the cached plan's attributes, not the database. */
DECLARE @sp_triage_plan_dbid TABLE (plan_handle VARBINARY(64) NOT NULL PRIMARY KEY, dbid INT NULL);
DECLARE @sp_triage_unreadable [NVARCHAR] (MAX);
DECLARE @sp_triage_unreadable_db TABLE (name SYSNAME NOT NULL PRIMARY KEY); /* named in the ONE skip row, after the plan probe */
BEGIN TRY
	INSERT @sp_triage_plan_dbid (plan_handle, dbid)
	SELECT q.plan_handle, a.dbid
	FROM (SELECT DISTINCT qs.plan_handle FROM #dadatafor_exec_query_stats AS qs) AS q
	OUTER APPLY (SELECT TOP (1) CONVERT(INT, pa.value) AS dbid FROM [sys].dm_exec_plan_attributes(q.plan_handle) AS pa WHERE pa.attribute = N'dbid') AS a;
	INSERT #sp_triage_readable_plan (plan_handle, dbid)
	SELECT p.plan_handle, p.dbid
	FROM @sp_triage_plan_dbid AS p
	WHERE p.dbid = 32767 OR p.dbid IN (SELECT r.database_id FROM #sp_triage_readable_db AS r);
	INSERT @sp_triage_unreadable_db (name)
	SELECT d.name
	FROM sys.databases AS d
	WHERE d.database_id IN (SELECT p.dbid FROM @sp_triage_plan_dbid AS p)
	AND d.database_id NOT IN (SELECT r.database_id FROM #sp_triage_readable_db AS r);
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Readable query plan list', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
END CATCH


BEGIN TRY
/*If this is an ancient version of SQL, add some missing columns*/
IF NOT EXISTS(
SELECT 1
FROM [tempdb].[sys].[columns]
WHERE columns.Name = N'total_used_threads'
AND Object_ID = Object_ID(N'tempdb..#dadatafor_exec_query_stats'))
BEGIN
	SET @dynamicSQL = 'ALTER TABLE #dadatafor_exec_query_stats ADD total_used_threads INT'
	EXEC sp_executesql @dynamicSQL;
END

IF NOT EXISTS(
SELECT 1 
FROM [tempdb].[sys].[columns] 
WHERE Name = N'total_grant_kb'
AND Object_ID = Object_ID(N'tempdb..#dadatafor_exec_query_stats'))
BEGIN
	SET @dynamicSQL = 'ALTER TABLE #dadatafor_exec_query_stats ADD total_grant_kb INT'
	EXEC sp_executesql @dynamicSQL;
END
IF NOT EXISTS(
SELECT 1 
FROM [tempdb].[sys].[columns] 
WHERE Name = N'total_used_grant_kb'
AND Object_ID = Object_ID(N'tempdb..#dadatafor_exec_query_stats'))
BEGIN
	SET @dynamicSQL = 'ALTER TABLE #dadatafor_exec_query_stats ADD total_used_grant_kb INT'
	EXEC sp_executesql @dynamicSQL;
END

END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Query stats extra columns', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Problem with adding columns to #dadatafor_exec_query_stats',0,1) WITH NOWAIT;
		SELECT @errMessage  = ERROR_MESSAGE();
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	END
END CATCH


	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_HeapTable') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_HeapTable;
			CREATE TABLE #output_sqldba_org_sp_triage_HeapTable 
			( 
				DB [NVARCHAR] (250)
				, [schema] [NVARCHAR] (250)
				, [table] [NVARCHAR] (250)
				, ForwardedCount [BIGINT]
				, AvgFrag MONEY 
				, PageCount [BIGINT]
				, [rows] [BIGINT]
				, user_seeks [BIGINT]
				, user_scans [BIGINT]
				, user_lookups [BIGINT]
				, user_updates [BIGINT]
				, last_user_seek DATETIME
				, last_user_scan DATETIME
				, last_user_lookup DATETIME
			);



	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_LogSpace') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_LogSpace;
			CREATE TABLE #output_sqldba_org_sp_triage_LogSpace  
			( 
				DatabaseName sysname NULL
				, LogSize FLOAT NULL
				, SpaceUsedPercent FLOAT NULL
				, Status bit NULL
				, VLFCount [INT] NULL
			);

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_Action_Statistics') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_Action_Statistics;
			CREATE TABLE #output_sqldba_org_sp_triage_Action_Statistics 
			(
				[Id] [INT] IDENTITY(1,1)
				, [DBname] [NVARCHAR] (100)
				, [TableName] [NVARCHAR] (100)
				, [StatsID] INT
				, [StatisticsName] [NVARCHAR] (500)
				, [SchemaName] [NVARCHAR] (100)
				, [ModificationCount] [BIGINT]
				, [LastUpdated] DATETIME
				, [Rows] [BIGINT]
				, [ModPerc] MONEY 
				, [EstPerc] MONEY
			);

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_MissingIndex') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_MissingIndex;
			/* Section 18 working tables (lane sp-triage-section-18-index-suggestions, 2026-11-14). Text columns are NVARCHAR(MAX)
			   so a long name or a long column list never fails the insert and loses every row; only the final Details is
			   bounded, by the 4,000 note rule in section 18. One row per missing-index handle; Keep = 0 for a row under the
			   benefit threshold or collapsed into an identical suggestion with a higher benefit. */
			CREATE TABLE #output_sqldba_org_sp_triage_MissingIndex
			(
				index_handle INT NOT NULL PRIMARY KEY
				, database_id INT NULL
				, [object_id] INT NULL
				, [magic_benefit_number] FLOAT NULL
				, [Table] [NVARCHAR] (MAX) NULL
				, [equality_columns] [NVARCHAR] (MAX) NULL
				, [inequality_columns] [NVARCHAR] (MAX) NULL
				, [included_columns] [NVARCHAR] (MAX) NULL
				, DBName [sysname] NULL
				, SchemaName [sysname] NULL
				, TableName [sysname] NULL
				, Keep BIT NULL
				, KeyCount INT NULL
				, IncludeCount INT NULL
				, IncludeSum INT NULL
				, NameStem [NVARCHAR] (1000) NULL
				, NameHashInput [NVARCHAR] (4000) NULL
				, NameHash VARBINARY (8000) NULL
				, NameSuffix INT NULL
				, KeyList [NVARCHAR] (MAX) NULL
				, IncludeList [NVARCHAR] (MAX) NULL
				, OrderComment [NVARCHAR] (MAX) NULL
				, LastMeasured BIT NULL
				, NoteTarget [NVARCHAR] (MAX) NULL
				, NoteReason [NVARCHAR] (MAX) NULL
				, NoteBudget INT NULL
				, NoteKeys [NVARCHAR] (MAX) NULL
				, NoteKeysMore INT NULL
				, NoteIncludes [NVARCHAR] (MAX) NULL
				, NoteIncludesMore INT NULL
				, IndexStatement [NVARCHAR] (MAX) NULL
				, Details [NVARCHAR] (MAX) NULL
			);
	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_MissingIndexColumn') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_MissingIndexColumn;
			CREATE TABLE #output_sqldba_org_sp_triage_MissingIndexColumn
			(
				index_handle INT NOT NULL
				, column_id INT NULL
				, column_name [sysname] NULL
				, column_usage [NVARCHAR] (20) NULL
				, AsciiName [NVARCHAR] (128) NULL
				, NoteName [NVARCHAR] (MAX) NULL
				, distinct_est [BIGINT] NULL
				, KeyOrder INT NULL
				, IdOrder INT NULL
			);
	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_MissingIndexMeasure') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_MissingIndexMeasure;
			CREATE TABLE #output_sqldba_org_sp_triage_MissingIndexMeasure
			(
				database_id INT NULL
				, [object_id] INT NULL
				, column_id INT NULL
				, distinct_est [BIGINT] NULL
			);

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_SqueezeMe') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_SqueezeMe;
			CREATE TABLE #output_sqldba_org_sp_triage_SqueezeMe 
			(
				[DB] [NVARCHAR] (250)
				, [Just compress] [NVARCHAR] (4000)
				, [For LOB data] [NVARCHAR] (4000)
				, [reserved_page_count] [BIGINT]
				, [row_count] [BIGINT]
				, [data_compression_desc] [NVARCHAR] (400)
			);


	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_compressionstates') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_compressionstates;
			CREATE TABLE #output_sqldba_org_sp_triage_compressionstates 
			(
				[dbname] [NVARCHAR] (250)
				, [IndexName] [NVARCHAR] (250)
				, [index_id] TINYINT
				, [partition_number] TINYINT
				, [TableName] [NVARCHAR] (250)
				, [is_disabled] TINYINT
				, [is_hypothetical] TINYINT
				, [IndexSizeKB] [BIGINT]
				, [RowCounts] [BIGINT]
				, [Compression] [NVARCHAR] (25)
				, [CompressionObject] [NVARCHAR] (25)
				, [Just compress] [NVARCHAR] (2500)
				, [For LOB data] [NVARCHAR] (2500)
			);

	IF OBJECT_ID('TempDB..#output_sqldba_org_sp_triage_indexusage') IS NOT NULL 
			DROP TABLE #output_sqldba_org_sp_triage_indexusage
			CREATE TABLE [dbo].[#output_sqldba_org_sp_triage_indexusage](
				dbname [NVARCHAR] (128) null
				, [ObjectName] [NVARCHAR] (128) NULL
				, [IndexName] [sysname] NULL
				, [index_id] [int] NOT NULL
				, [Reads] [BIGINT] NULL
				, [Writes] [BIGINT] NOT NULL
				, [IndexType] [NVARCHAR] (60) NULL
				, [FillFactor] [tinyint] NOT NULL
				, [has_filter] [bit] NULL
				, [filter_definition] [NVARCHAR] (MAX) NULL
				, [last_user_scan] [DATETIME] NULL
				, [last_user_lookup] [DATETIME] NULL
				, [last_user_seek] [DATETIME] NULL
				, [user_seeks] [BIGINT] NULL
				, [user_scans] [BIGINT] NULL
				, [user_lookups ] [BIGINT] NULL
				, [TableReadActivity%] MONEY
				, [TotalReadActivity%] MONEY
			) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];


	/*Note, if you add columns to this table, please make sure to add them in the ADD Column clause at the bottom of the script where it writes outputs to a table.*/
	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage;
			CREATE TABLE #output_sqldba_org_sp_triage 
			(
				evaldate [NVARCHAR] (50)
				, domain [NVARCHAR] (505) NULL--DEFAULT @ThisDomain
				, SQLInstance [NVARCHAR] (505) NULL --DEFAULT @ThisServer
				, SectionID [INT] NULL
				, Section [NVARCHAR] (4000)
				, Summary [NVARCHAR] (4000)
				, Severity [NVARCHAR] (5)
				, Details [NVARCHAR] (4000)
				, QueryPlan XML NULL
				, HoursToResolveWithTesting MONEY  NULL
				, ID [INT] IDENTITY(1,1)
			);
	/* M4 platform arm: the FIRST row of the report (see @PlatformNotTested). */
	IF @PlatformNotTested IS NOT NULL
		INSERT #output_sqldba_org_sp_triage (SectionID, Section, Summary, Severity)
		VALUES (0, N'Platform', N'Not tested on ' + @PlatformNotTested + N'; results may be partial.', @Result_Warning);

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_ConfigurationDefaults') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_ConfigurationDefaults;
			CREATE TABLE #output_sqldba_org_sp_triage_ConfigurationDefaults
				(
				  name [NVARCHAR] (128) 
				  , DefaultValue [BIGINT]
				  , CheckID INT
				);

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_db_sps') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_db_sps;
	CREATE TABLE #output_sqldba_org_sp_triage_db_sps 
				(
					[dbname] [NVARCHAR] (500)
					, [SP Name] [NVARCHAR] (500)
					, [TotalLogicalWrites] [BIGINT]
					, [AvgLogicalWrites] [BIGINT]
					, execution_count [BIGINT]
					, [Calls/Second] INT
					, [total_elapsed_time] [BIGINT]
					, [avg_elapsed_time] [BIGINT]
					, cached_time DATETIME
				);
				
	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_querystats') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_querystats
	CREATE TABLE #output_sqldba_org_sp_triage_querystats
				(
					 Id [INT] IDENTITY(1,1)
					, RankIOTime INT
					, [execution_count] [BIGINT] NOT NULL
					, [total_logical_reads] [BIGINT] NOT NULL
					, [Total_MBsRead] [MONEY] NULL
					, [total_logical_writes] [BIGINT] NOT NULL
					, [Total_MBsWrite] [MONEY] NULL
					, [total_worker_time] [BIGINT] NOT NULL
					, [total_elapsed_time_in_S] [MONEY] NULL
					, [total_elapsed_time] [MONEY] NULL
					, [last_execution_time] [DATETIME] NOT NULL
					, [plan_handle] [VARBINARY](64) NOT NULL
					, [sql_handle] [VARBINARY](64) NOT NULL
				);

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_notrust') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_notrust
	CREATE TABLE #output_sqldba_org_sp_triage_notrust
				(
				  KeyType [NVARCHAR] (20)
				, Tablename [NVARCHAR] (500)
				, KeyName [NVARCHAR] (500)
				, DBCCcommand [NVARCHAR] (2000)
				, Fix [NVARCHAR] (2000)
				);

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_whatsets') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_whatsets
	CREATE TABLE #output_sqldba_org_sp_triage_whatsets
				(
				  DBname [NVARCHAR] (500)
				, [compatibility_level] [NVARCHAR] (10)
				, [SETs] [NVARCHAR] (500)
				);

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_dbccloginfo') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_dbccloginfo
	CREATE TABLE #output_sqldba_org_sp_triage_dbccloginfo  
			(
				id [INT] NULL
			);

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_SQLVersionsDump') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_SQLVersionsDump		
	CREATE TABLE #output_sqldba_org_sp_triage_SQLVersionsDump 
			(
				  ID [INT] IDENTITY(0,1)
				, Output [NVARCHAR] (250)
			);
	
	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_SQLVersions') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_SQLVersions
	CREATE TABLE #output_sqldba_org_sp_triage_SQLVersions 
			(
			  Id INT
			, [Products Released] [NVARCHAR] (250)
			, [Lifecycle Start Date]  [NVARCHAR] (250)
			, [Mainstream Support End Date]  [NVARCHAR] (250)
			, [Extended Support End Date]  [NVARCHAR] (250)
			, [Service Pack Support End Date]  [NVARCHAR] (250)
			);
			
	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_spnCheck') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_spnCheck
	CREATE TABLE #output_sqldba_org_sp_triage_spnCheck
			(
			output [VARCHAR](1024) NULL
			, Seq INT IDENTITY(1,1) /* setspn output order: the "could not read SPNs" row quotes the lines in order */
			);

 IF(OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_InvalidLogins') IS NOT NULL)
        BEGIN
            EXEC sp_executesql N'DROP TABLE #output_sqldba_org_sp_triage_InvalidLogins;';
        END;
								 
		CREATE TABLE #output_sqldba_org_sp_triage_InvalidLogins (
			LoginSID    varbinary(85)
			, LoginName   [VARCHAR](256)
		);
	

--The blitz


        IF OBJECT_ID ('tempdb..#output_sqldba_org_sp_triage_Recompile') IS NOT NULL
            DROP TABLE #output_sqldba_org_sp_triage_Recompile;
        CREATE TABLE #output_sqldba_org_sp_triage_Recompile(
            DBName [VARCHAR](200)
            , ProcName [VARCHAR](300)
            , RecompileFlag [VARCHAR](1)
            , SPSchema [VARCHAR](50)
        );

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_DatabaseDefaults') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_DatabaseDefaults;
		CREATE TABLE #output_sqldba_org_sp_triage_DatabaseDefaults
			(
				name [NVARCHAR] (128) 
				, DefaultValue [NVARCHAR] (200)
				, CheckID INT
		        , Priority INT
		        , Finding [VARCHAR](200)
		        , URL [VARCHAR](200)
		        , Details [NVARCHAR] (4000)
			);

		IF OBJECT_ID('tempdb..#DatabaseScopedConfigurationDefaults') IS NOT NULL
			DROP TABLE #DatabaseScopedConfigurationDefaults;
		CREATE TABLE #DatabaseScopedConfigurationDefaults
			(ID [INT] IDENTITY(1,1)
			, configuration_id INT
			, [name] [NVARCHAR] (60)
			, default_value sql_variant
			, default_value_for_secondary sql_variant
			, CheckID INT
			);

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_DBCCs') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_DBCCs;
		CREATE TABLE #output_sqldba_org_sp_triage_DBCCs
			(
			  ID [INT] IDENTITY(1, 1) PRIMARY KEY 
			  , ParentObject [VARCHAR](255) 
			  , Object [VARCHAR](255) 
			  , Field [VARCHAR](255) 
			  , Value [VARCHAR](255) 
			  , DbName [NVARCHAR] (128) NULL
			);

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_LogInfo2012') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_LogInfo2012;
		CREATE TABLE #output_sqldba_org_sp_triage_LogInfo2012
			(
			  recoveryunitid [INT] 
			  , FileID SMALLINT 
			  , FileSize [BIGINT] 
			  , StartOffset [BIGINT] 
			  , FSeqNo [BIGINT] 
			  , [Status] TINYINT
			  , Parity TINYINT 
			  , CreateLSN NUMERIC(38)
			);

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_LogInfo') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_LogInfo;
		CREATE TABLE #output_sqldba_org_sp_triage_LogInfo
			(
			  FileID SMALLINT 
			  , FileSize [BIGINT] 
			  , StartOffset [BIGINT] 
			  , FSeqNo [BIGINT] 
			  , [Status] TINYINT 
			  , Parity TINYINT 
			  , CreateLSN NUMERIC(38)
			);

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_partdb') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_partdb;
		CREATE TABLE #output_sqldba_org_sp_triage_partdb
			(
			  dbname [NVARCHAR] (128) 
			  , objectname [NVARCHAR] (200) 
			  , type_desc [NVARCHAR] (128)
			);

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_TraceStatus') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_TraceStatus;
		CREATE TABLE #output_sqldba_org_sp_triage_TraceStatus
			(
			  TraceFlag [VARCHAR](10) 
			  , status BIT 
			  , Global BIT 
			  , Session BIT
			);

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_driveInfo') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_driveInfo;
		CREATE TABLE #output_sqldba_org_sp_triage_driveInfo
			(
			  drive [NVARCHAR]
              , logical_volume_name [NVARCHAR] (32) --Limit is 32 for NTFS, 11 for FAT
			  , available_MB DECIMAL(18, 0)
              , total_MB DECIMAL(18, 0)
              , used_percent DECIMAL(18, 2)
			);


		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_blockinghistory') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_blockinghistory;
		CREATE TABLE #output_sqldba_org_sp_triage_blockinghistory
			(
			DatabaseName [NVARCHAR] (2000)
			, ObjectName [NVARCHAR] (2000)
			, LocksCount [BIGINT]
			, BlocksCount [BIGINT]
			, BlocksWaitTimeMs [BIGINT]
			, index_id [BIGINT]
			, page_io_latch_wait_count [BIGINT]
			, page_io_latch_wait_in_ms [BIGINT]
			, page_compression_success_count [BIGINT]
			, range_scan_count [BIGINT]
			, singleton_lookup_count [BIGINT]
			, forwarded_fetch_count [BIGINT]
			, lob_fetch_in_bytes [BIGINT]
			, lob_orphan_create_count [BIGINT]
			, lob_orphan_insert_count [BIGINT] 
			, leaf_ghost_count [BIGINT] 
			, insert_count [BIGINT] 
			, delete_count [BIGINT]
			, update_count [BIGINT]
			, allocation_count [BIGINT]
			, page_merge_count [BIGINT]
			);
		

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_FKNOIndex') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_FKNOIndex;	
		CREATE TABLE #output_sqldba_org_sp_triage_FKNOIndex
			(
			DatabaseName [NVARCHAR] (2000)
			, TableName [NVARCHAR] (2000)
			, Column_Name [NVARCHAR] (2000)
			, IndexStatement [NVARCHAR] (2000)
			);


		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_lockinghistory') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_lockinghistory;	
		CREATE TABLE #output_sqldba_org_sp_triage_lockinghistory
			(
			DatabaseName [NVARCHAR] (2000)
			, ObjectName [NVARCHAR] (2000)
			, LocksCount [BIGINT]
			, BlocksCount [BIGINT]
			, blocksWaitTimeMs [BIGINT]
			, index_id [BIGINT]
			);

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_ErrorLog') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_ErrorLog;
		CREATE TABLE #output_sqldba_org_sp_triage_ErrorLog
			(
			  LogDate DATETIME 
			  , ProcessInfo [NVARCHAR] (20) 
			  , [Text] [NVARCHAR] (1000)
			);

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_fnTraceGettable') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_fnTraceGettable;
		CREATE TABLE #output_sqldba_org_sp_triage_fnTraceGettable
			(
			  TextData [NVARCHAR] (4000) 
			  , DatabaseName [NVARCHAR] (256) 
			  , EventClass [INT] 
			  , Severity [INT] 
			  , StartTime DATETIME 
			  , EndTime DATETIME 
			  , Duration [BIGINT] 
			  , NTUserName [NVARCHAR] (256) 
			  , NTDomainName [NVARCHAR] (256) 
			  , HostName [NVARCHAR] (256) 
			  , ApplicationName [NVARCHAR] (256) 
			  , LoginName [NVARCHAR] (256) 
			  , DBUserName [NVARCHAR] (256)
			 );

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_Instances') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_Instances;
		CREATE TABLE #output_sqldba_org_sp_triage_Instances
            (
              Instance_Number [NVARCHAR] (500) 
              , Instance_Name [NVARCHAR] (500) 
              , Data_Field [NVARCHAR] (500)
            );

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_db_size') IS NOT NULL DROP TABLE #output_sqldba_org_sp_triage_db_size
		CREATE TABLE #output_sqldba_org_sp_triage_db_size
		(
			[name] [sysname] NOT NULL
			, [Size_MBs] [BIGINT] NULL
			, [log_size_mb] [BIGINT] NULL
		);

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_ImportantWaits') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_ImportantWaits;
		CREATE TABLE #output_sqldba_org_sp_triage_ImportantWaits (
			wait_type [NVARCHAR] (60)
			, [Weight] TINYINT /*1-5 to act as a multiplier, more is worse*/
			, Details [NVARCHAR] (1500)
			
		);

		INSERT INTO #output_sqldba_org_sp_triage_ImportantWaits 
		SELECT DISTINCT wait_type, [Weight],  Details
		FROM (
		VALUES 
		/*Paul*/
		('ASYNC_NETWORK_IO'		,2	,'The classic cause of this wait type is RBAR (Row-By-Agonizing-Row) processing of results in a client, instead of caching the results client-side and telling SQL Server to send more. A common misconception is that this wait type is usually caused by network problems – that’s rarely the case in my experience.')
		,('CXPACKET'			,1	,'This wait type always accrues when parallelism happens, as the control thread in a parallel operation waits until all threads have completed. However, when parallel threads are given unbalanced amounts of work to do, the threads that finish early also accrue this wait type, leading to it maybe becoming the most prevalent. So this one could be benign, as the workload has lots of good parallelism, but could be malignant if there’s unwanted parallelism or problems causing skewed distribution of work among parallel threads.')
		,('LCK_M_IX'			,3	,'This wait type occurs when a thread is waiting for a table or page IX lock so that a row insert or update can occur. It could be from lock escalation to a table X or S lock causing all other threads to wait to be able to insert/update.')
		,('LCK_M_X'				,3	,'This wait type commonly occurs when lock escalation is happening. It could also be caused by using a restrictive isolation level like REPEATABLE_READ or SERIALIZABLE that requires S and IS locks to be held until the end of a transaction. Note that distributed transactions change the isolation level to SERIALIZABLE under the covers – something that’s bitten several of our clients before we helped them. Someone could also have inhibited row locks on a clustered index causing all inserts to acquire page X locks – this is very uncommon though.')
		,('PAGEIOLATCH_SH'		,4	,'This wait type occurs when a thread is waiting for a data file page to be read into memory. Common causes of this wait being the most prevalent are when the workload doesn’t fit in memory and the buffer pool has to keep evicting pages and reading others in from disk, or when query plans are using table scans instead of index seeks, or when the buffer pool is under memory pressure which reduces the amount of space available for data.')
		,('PAGELATCH_EX'		,5	,'The two classic causes of this wait type are tempdb allocation bitmap contention (from lots of concurrent threads creating and dropping temp tables combined with a small number of tempdb files and not having TF1118 enabled) and an insert hotspot (from lots of concurrent threads inserting small rows into a clustered index with an identity value, leading to contention on the index leaf-level pages). There are plenty of other causes of this wait type too, but none that would commonly lead to it being the leading wait type over the course of a week.')
		,('SOS_SCHEDULER_YIELD'	,5	,'The most common cause of this wait type is that the workload is memory resident and there is no contention for resources, so threads are able to repeatedly exhaust their scheduling quanta (4ms), registering SOS_SCHEDULER_YIELD when they voluntarily yield the processor. An example would be scanning through a large number of pages in an index. This may or may not be a good thing.')
		,('WRITELOG'			,1	,'This wait type is common to see in the first few top waits on servers as the transaction log is often one of the chief bottlenecks on a busy server. This could be caused by the I/O subsystem not being able to keep up with the rate of log flushing combined with lots of tiny transactions forcing frequent flushes of minimal-sized log blocks.')
		/*https://www.brentozar.com/archive/2017/08/what-are-poison-waits/ */		
		,('RESOURCE_SEMAPHORE_QUERY_COMPILE',5,'This means a query came in, and SQL Server didn’t have an execution plan cached for it. In order to build an execution plan, SQL Server needs a little memory - not a lot, just a little - but that memory wasn’t available. SQL Server had to wait for memory to become available before it could even build an execution plan. For more details and a reproduction script, check out my Bad Idea Jeans: Dynamically Generating Ugly Queries post. In this scenario, cached query plans (and small ones) may be able to proceed just fine (depending on how much pressure the server is under), but the ugly ones will feel frozen.')
		,('RESOURCE_SEMAPHORE'	,5	,'This means we got past the compilation stage (or the query was cached), but now we need memory in order to run the query. Other queries are using a lot of memory, though, and our query can’t even get started executing because there’s not enough memory available for our query. In this case, like with the prior poison, small queries may be able to get through just fine, but large ones will just sit around waiting. For more details and a repro, performance subscribers can watch my training video on RESOURCE_SEMAPHORE waits.')
		,('THREADPOOL'			,5	,'While the first two poisons involved memory issues, this one is about CPU availability. At startup, SQL Server allocates a certain number of worker threads based on the number of logical processors in your server. As queries come in, they get assigned to worker threads – but there’s only a finite number available. If enough queries pile up – especially when queries get blocked and can’t make progress – you can run out of available worker threads. The first temptation might be to increase max worker threads, but then you might simply escalate the problem to a RESOURCE_SEMAPHORE issue.')
		) AS X(wait_type, [Weight],  Details);



		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_IgnorableWaits') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_IgnorableWaits;
		CREATE TABLE #output_sqldba_org_sp_triage_IgnorableWaits (
			wait_type [NVARCHAR] (60)
			, Source [NVARCHAR] (500)
		);

		INSERT INTO #output_sqldba_org_sp_triage_IgnorableWaits 
		SELECT DISTINCT wait_type,  Source
		FROM (
		VALUES 
		('BROKER_EVENTHANDLER','')
		, ('BROKER_RECEIVE_WAITFOR','')
		, ('BROKER_TASK_STOP','')
		, ('BROKER_TO_FLUSH','')
		, ('BROKER_TRANSMITTER','')
		, ('CHECKPOINT_QUEUE','')
		, ('CLR_AUTO_EVENT','')
		, ('CLR_MANUAL_EVENT','')
		, ('CLR_SEMAPHORE','')
		, ('DBMIRROR_DBM_EVENT','')
		, ('DBMIRROR_DBM_MUTEX','')
		, ('DBMIRROR_EVENTS_QUEUE','')
		, ('DBMIRROR_WORKER_QUEUE','')
		, ('DBMIRRORING_CMD','')
		, ('DIRTY_PAGE_POLL','')
		, ('DISPATCHER_QUEUE_SEMAPHORE','')
		, ('FT_IFTS_SCHEDULER_IDLE_WAIT','')
		, ('FT_IFTSHC_MUTEX','')
		, ('HADR_CLUSAPI_CALL','')
		, ('HADR_FABRIC_CALLBACK','')
		, ('HADR_FILESTREAM_IOMGR_IOCOMPLETION','')
		, ('HADR_LOGCAPTURE_WAIT','')
		, ('HADR_NOTIFICATION_DEQUEUE','')
		, ('HADR_TIMER_TASK','')
		, ('HADR_WORK_QUEUE','')
		, ('LAZYWRITER_SLEEP','')
		, ('LOGMGR_QUEUE','')
		, ('ONDEMAND_TASK_QUEUE','')
		, ('PARALLEL_REDO_DRAIN_WORKER','')
		, ('PARALLEL_REDO_LOG_CACHE','')
		, ('PARALLEL_REDO_TRAN_LIST','')
		, ('PARALLEL_REDO_WORKER_SYNC','')
		, ('PARALLEL_REDO_WORKER_WAIT_WORK','')
		, ('PREEMPTIVE_HADR_LEASE_MECHANISM','')
		, ('PREEMPTIVE_SP_SERVER_DIAGNOSTICS','')
		, ('QDS_ASYNC_QUEUE','')
		, ('QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP','')
		, ('QDS_PERSIST_TASK_MAIN_LOOP_SLEEP','')
		, ('QDS_SHUTDOWN_QUEUE','')
		, ('REDO_THREAD_PENDING_WORK','')
		, ('REQUEST_FOR_DEADLOCK_SEARCH','')
		, ('SLEEP_SYSTEMTASK','')
		, ('SLEEP_TASK','')
		, ('SOS_WORK_DISPATCHER','')
		, ('SP_SERVER_DIAGNOSTICS_SLEEP','')
		, ('SQLTRACE_BUFFER_FLUSH','')
		, ('SQLTRACE_INCREMENTAL_FLUSH_SLEEP','')
		, ('UCS_SESSION_REGISTRATION','')
		, ('WAIT_XTP_OFFLINE_CKPT_NEW_LOG','')
		, ('WAITFOR','')
		, ('XE_DISPATCHER_WAIT','')
		, ('XE_LIVE_TARGET_TVF','')
		, ('XE_TIMER_EVENT','')
        , ('CHKPT', 'https://www.sqlskills.com/help/waits/CHKPT')
        , ('EXECSYNC', 'https://www.sqlskills.com/help/waits/EXECSYNC')
        , ('FSAGENT', 'https://www.sqlskills.com/help/waits/FSAGENT')
        , ('KSOURCE_WAKEUP', 'https://www.sqlskills.com/help/waits/KSOURCE_WAKEUP')
        , ('MEMORY_ALLOCATION_EXT', 'https://www.sqlskills.com/help/waits/MEMORY_ALLOCATION_EXT')
        , ('PREEMPTIVE_XE_GETTARGETSTATE', 'https://www.sqlskills.com/help/waits/PREEMPTIVE_XE_GETTARGETSTATE')
        , ('PWAIT_ALL_COMPONENTS_INITIALIZED', 'https://www.sqlskills.com/help/waits/PWAIT_ALL_COMPONENTS_INITIALIZED')
        , ('PWAIT_DIRECTLOGCONSUMER_GETNEXT', 'https://www.sqlskills.com/help/waits/PWAIT_DIRECTLOGCONSUMER_GETNEXT')
        , ('RESOURCE_QUEUE', 'https://www.sqlskills.com/help/waits/RESOURCE_QUEUE')
        , ('SERVER_IDLE_CHECK', 'https://www.sqlskills.com/help/waits/SERVER_IDLE_CHECK')
        , ('SLEEP_BPOOL_FLUSH', 'https://www.sqlskills.com/help/waits/SLEEP_BPOOL_FLUSH')
        , ('SLEEP_DBSTARTUP', 'https://www.sqlskills.com/help/waits/SLEEP_DBSTARTUP')
        , ('SLEEP_DCOMSTARTUP', 'https://www.sqlskills.com/help/waits/SLEEP_DCOMSTARTUP')
        , ('SLEEP_MASTERDBREADY', 'https://www.sqlskills.com/help/waits/SLEEP_MASTERDBREADY')
        , ('SLEEP_MASTERMDREADY', 'https://www.sqlskills.com/help/waits/SLEEP_MASTERMDREADY')
        , ('SLEEP_MASTERUPGRADED', 'https://www.sqlskills.com/help/waits/SLEEP_MASTERUPGRADED')
        , ('SLEEP_MSDBSTARTUP', 'https://www.sqlskills.com/help/waits/SLEEP_MSDBSTARTUP')
        , ('SLEEP_TEMPDBSTARTUP', 'https://www.sqlskills.com/help/waits/SLEEP_TEMPDBSTARTUP')
        , ('SNI_HTTP_ACCEPT', 'https://www.sqlskills.com/help/waits/SNI_HTTP_ACCEPT')
		, ('SQLTRACE_WAIT_ENTRIES', 'https://www.sqlskills.com/help/waits/SQLTRACE_WAIT_ENTRIES')
        , ('WAIT_FOR_RESULTS', 'https://www.sqlskills.com/help/waits/WAIT_FOR_RESULTS')
		, ('WAIT_XTP_CKPT_CLOSE', 'https://www.sqlskills.com/help/waits/WAIT_XTP_CKPT_CLOSE')
		, ('XE_DISPATCHER_JOIN', 'https://www.sqlskills.com/help/waits/XE_DISPATCHER_JOIN')
		, ('WAITFOR_TASKSHUTDOWN', 'https://www.sqlskills.com/help/waits/WAITFOR_TASKSHUTDOWN')
        , ('WAIT_XTP_RECOVERY', 'https://www.sqlskills.com/help/waits/WAIT_XTP_RECOVERY')
        , ('WAIT_XTP_HOST_WAIT', 'https://www.sqlskills.com/help/waits/WAIT_XTP_HOST_WAIT')
		, ('PARALLEL_REDO_LOG_CAPTURE','')
		, ('POPULATE_LOCK_ORDINALS','')
		, ('PREEMPTIVE_OS_FLUSHFILEBUFFERS','')
		, ('PWAIT_EXTENSIBILITY_CLEANUP_TASK','')
		, ('SLEEP_BUFFERPOOL_HELPLW','')
		, ('SLEEP_DIAG','')
		, ('SLEEP_TEMPDBSHRINK','')
		, ('SLEEP_WORKER','')
		, ('SLEEP_WORKSPACE_MANAGER','')
		, ('STARTUP_DEPENDENCY_MANAGER','')
		, ('VDI_CLIENT_OTHER','')
		, ('XE_FILE_TARGET_TVF','')
		, ('PVS_PREALLOCATE','')
		, ('PREEMPTIVE_XE_DISPATCHER','')
		, ('SLEEP_MASTERDBUPGRADED_ISDONE','')
		 ) AS X(wait_type,  Source);
	
	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_TraceTypes') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_TraceTypes;	
		CREATE TABLE #output_sqldba_org_sp_triage_TraceTypes (
			[Value] INT
			, Definition [NVARCHAR] (200)
		);

		INSERT INTO #output_sqldba_org_sp_triage_TraceTypes 
		SELECT DISTINCT Value,  Definition
		FROM (
		VALUES 
		  (8259		, 'Check Constraint')
		, (8260		, 'Default (constraint or standalone)')
		, (8262		, 'Foreign-key Constraint')
		, (8272		, 'Stored Procedure')
		, (8274		, 'Rule')
		, (8275		, 'System Table')
		, (8276		, 'Trigger on Server')
		, (8277		, '(User-defined) Table')
		, (8278		, 'View')
		, (8280		, 'Extended Stored Procedure')
		, (16724	, 'CLR Trigger')
		, (16964	, 'Database')
		, (16975	, 'Object')
		, (17222	, 'FullText Catalog')
		, (17232	, 'CLR Stored Procedure')
		, (17235	, 'Schema')
		, (17475	, 'Credential')
		, (17491	, 'DDL Event')
		, (17741	, 'Management Event')
		, (17747	, 'Security Event')
		, (17749	, 'User Event')
		, (17985	, 'CLR Aggregate Function')
		, (17993	, 'Inline Table-valued SQL Function')
		, (18000	, 'Partition Function')
		, (18002	, 'Replication Filter Procedure')
		, (18004	, 'Table-valued SQL Function')
		, (18259	, 'Server Role')
		, (18263	, 'Microsoft Windows Group')
		, (19265	, 'Asymmetric Key')
		, (19277	, 'Master Key')
		, (19280	, 'Primary Key')
		, (19283	, 'ObfusKey')
		, (19521	, 'Asymmetric Key Login')
		, (19523	, 'Certificate Login')
		, (19538	, 'Role')
		, (19539	, 'SQL Login')
		, (19543	, 'Windows Login')
		, (20034	, 'Remote Service Binding')
		, (20036	, 'Event Notification on Database')
		, (20037	, 'Event Notification')
		, (20038	, 'Scalar SQL Function')
		, (20047	, 'Event Notification on Object')
		, (20051	, 'Synonym')
		, (20307	, 'Sequence')
		, (20549	, 'End Point')
		, (20801	, 'Adhoc Queries which may be cached')
		, (20816	, 'Prepared Queries which may be cached')
		, (20819	, 'Service Broker Service Queue')
		, (20821	, 'Unique Constraint')
		, (21057	, 'Application Role')
		, (21059	, 'Certificate')
		, (21075	, 'Server')
		, (21076	, 'Transact-SQL Trigger')
		, (21313	, 'Assembly')
		, (21318	, 'CLR Scalar Function')
		, (21321	, 'Inline scalar SQL Function')
		, (21328	, 'Partition Scheme')
		, (21333	, 'User')
		, (21571	, 'Service Broker Service Contract')
		, (21572	, 'Trigger on Database')
		, (21574	, 'CLR Table-valued Function')
		, (21577	, 'Internal Table (For example, XML Node Table, Queue Table.)')
		, (21581	, 'Service Broker Message Type')
		, (21586	, 'Service Broker Route')
		, (21587	, 'Statistics')
		, (21825	, '')
		, (21827	, '')
		, (21831	, '')
		, (21843	, '')
		, (21847	, 'User')
		, (22099	, 'Service Broker Service')
		, (22601	, 'Index')
		, (22604	, 'Certificate Login')
		, (22611	, 'XMLSchema')
		, (22868	, 'Type')
		 ) AS X(Value,  Definition);

--the blitz

	IF CONVERT( TINYINT ,@SQLVersion) >= 11 -- post-SQL2012 
	BEGIN
		SET @dynamicSQL =  'Alter table #output_sqldba_org_sp_triage_dbccloginfo Add [RecoveryUnitId] int'
		EXEC sp_executesql @dynamicSQL;
	END

	ALTER TABLE #output_sqldba_org_sp_triage_dbccloginfo 
		Add fileid smallint ;
	ALTER TABLE #output_sqldba_org_sp_triage_dbccloginfo 
		Add file_size [BIGINT];
	ALTER TABLE #output_sqldba_org_sp_triage_dbccloginfo 
		Add start_offset [BIGINT] ; 
	ALTER TABLE #output_sqldba_org_sp_triage_dbccloginfo 
		Add fseqno int;
	ALTER TABLE #output_sqldba_org_sp_triage_dbccloginfo 
		Add [status] INT;
	ALTER TABLE #output_sqldba_org_sp_triage_dbccloginfo 
		Add parity INT;
	ALTER TABLE #output_sqldba_org_sp_triage_dbccloginfo 
		Add create_lsn numeric(25,0);  

/*Done with #temp tables, now is a good time to check settings for this session*/



/* Check for Numeric RoundAbort, turn it off if it is on. We'll turn it on later again. */
DECLARE @TurnNumericRoundabortOn BIT;
	IF ( (8192 & @@OPTIONS) = 8192 ) 
		BEGIN
		IF EXISTS (/*If we find this on any database, just disable it for now. */
		SELECT 1 
		FROM @sysdatabasesTable
		WHERE is_numeric_roundabort_on = 1
				) 
			BEGIN
			SET @TurnNumericRoundabortOn = 1;
			SET NUMERIC_ROUNDABORT OFF;
			END;
		END;

	
IF @Debug = 1  /*gate 2026-07-02: was ungated, so it reset timing + RAISERRORed on every client run unlike the ~123 sibling blocks*/
BEGIN
	SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' startup step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
	SET @DebugTime = GETDATE()  /*was missing: next step's timing double-counted the startup step*/
	IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT;
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END




SELECT @errMessage  = 'Checking xp_msver table'
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
BEGIN TRY
  	DECLARE @msversion TABLE([Index] INT, Name [NVARCHAR] (50), [Internal_Value] [NVARCHAR] (50), [Character_Value] [NVARCHAR] (250))
	INSERT @msversion
	EXEC xp_msver;
	/*Rather useful this one*/
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'xp_msver server details', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));

  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
		SELECT @errMessage  = ERROR_MESSAGE();
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	END
END CATCH



	

	--DECLARE @quicksql [NVARCHAR] (500)
	--SET @quicksql = N'EXEC Get_xp_msver '
	--
	--EXEC sp_executesql @quicksql
/*Prep out the fist instance of the errorlog that contains restart information*/
DECLARE @xp_errorlog  TABLE
	(
		LogDate DATETIME
		, ProcessInfo [NVARCHAR] (250)
		, Text [NVARCHAR] (4000)
	);

	/*First find the errorlog that contains the startup info, might have been cycled*/
	DECLARE @xp_errorlogs TABLE
		(
			Archive INT
			/* NVARCHAR on purpose (2026-07-08): sp_enumerrorlogs returns a fixed US
			   'MM/DD/YYYY hh:mm' STRING. Stored as DATETIME it was parsed with the SESSION
			   dateformat - dmy sessions (British/NZ/AU login language) either aborted the
			   insert (Msg 8114, day>12) or silently swapped day/month. Convert with explicit
			   style 101 at the point of use instead. */
			, [Date] NVARCHAR(30)
			,  [Log File Size (Byte)] BIGINT
		)
	DECLARE @RestartInfoLog INT;
	DECLARE @SvcStartTime DATETIME;
	DECLARE @LogWindowStart DATETIME;
	DECLARE @LogWindowEnd DATETIME;
	/* @ErrorLogMaxMB (lane bundle-sp-triage-5, M3c): every error-log file a read leaves out (or reads for 7 days only, or
	   reads with its size unknown) gets ONE row here, written as a row of the error-log section (5) or the Agent-log
	   section (6). Never a #sp_triage_skipped row: those become "Could not run" rows, and a capped read did not fail. */
	DECLARE @ErrorLogCapped TABLE (LogKind [NVARCHAR] (20), Archive INT, SizeBytes BIGINT NULL, Action [NVARCHAR] (10));
	DECLARE @ErrLogSize BIGINT;
	DECLARE @ErrLogReadFrom DATETIME;
	DECLARE @ErrLogSkip BIT;
	DECLARE @AgentLogSkip BIT;
	/* Guarded 2026-07-02: sp_enumerrorlogs / xp_ReadErrorLog / xp_readerrorlog do not exist on
	   Azure SQL DB / MI and this block was NOT in a TRY/CATCH, so it aborted the whole batch there.
	   Now Azure/MI-gated AND wrapped so a failure degrades to an empty restart-info section. */
	IF @IsSQLAzure = 0 AND @IsSQLMI = 0
	BEGIN TRY
		INSERT @xp_errorlogs
		EXEC sys.sp_enumerrorlogs;

		/* Rewritten 2026-07-08: the old locator WHILE loop full-scanned EVERY archived log
		   searching for 'Command Line Startup Parameters:' - minutes per log on servers with
		   huge error logs. The startup block can only live in the log that was current at the
		   last service start = the OLDEST log whose last-write date is on/after
		   sqlserver_start_time (a log closed before the service started cannot contain it).
		   That candidate comes from pure date math over sp_enumerrorlogs; because enum dates
		   are minute-granular and a log closed during the preceding shutdown can share that
		   minute, the candidate is VERIFIED by a date-windowed probe for the startup-parameters
		   line, walking toward the current log on a miss - normally zero extra probes, and a
		   probe is never a full-log scan. All reads below are likewise date-bounded to the
		   startup window so a huge current log is not re-scanned end-to-end per search term. */
		SELECT @SvcStartTime = sqlserver_start_time
			, @LogWindowStart = DATEADD(MINUTE,-5,sqlserver_start_time)
			, @LogWindowEnd = DATEADD(MINUTE,60,sqlserver_start_time)
		FROM [sys].dm_os_sys_info;

		/* Archive <= 99: see the error-log reader's invariant at 'Iterate through each log file' (2026-10-24). */
		SELECT @RestartInfoLog = ISNULL(MAX(Archive),0)
		FROM @xp_errorlogs
		WHERE CONVERT(DATETIME, [Date], 101) >= DATEADD(MINUTE,-1,@SvcStartTime) /*style 101 = fixed US parse, immune to session DATEFORMAT*/
		AND Archive BETWEEN 0 AND 99;

		WHILE @RestartInfoLog >= 0
		BEGIN
			/* @ErrorLogMaxMB (M3c): an archive over the cap is not probed; archive 0 always is (these reads are date-bounded). */
			IF @RestartInfoLog > 0 AND @ErrorLogMaxMB IS NOT NULL
				AND EXISTS (SELECT 1 FROM @xp_errorlogs WHERE Archive = @RestartInfoLog AND [Log File Size (Byte)] > CONVERT(BIGINT, @ErrorLogMaxMB) * 1048576)
			BEGIN
				INSERT @ErrorLogCapped (LogKind, Archive, SizeBytes, Action)
				SELECT TOP 1 N'SQL Server', Archive, [Log File Size (Byte)], N'skipped' FROM @xp_errorlogs WHERE Archive = @RestartInfoLog;
			END
			ELSE
			BEGIN
			INSERT @xp_errorlog
			EXEC [sys].xp_readerrorlog @RestartInfoLog, 1, N'Command Line Startup Parameters:', NULL, @LogWindowStart, @LogWindowEnd;
			IF EXISTS(SELECT 1 FROM @xp_errorlog WHERE [Text] LIKE 'Command Line Startup Parameters:%')
				BREAK;
			END
			SET @RestartInfoLog = @RestartInfoLog - 1;
		END
		IF @RestartInfoLog < 0 SET @RestartInfoLog = 0; /*probe never hit (clock skew/odd locale): degrade to current log*/

		INSERT @xp_errorlog
		EXEC [sys].xp_readerrorlog @RestartInfoLog, 1, N'locked pages', NULL, @LogWindowStart, @LogWindowEnd;
		INSERT @xp_errorlog
		EXEC [sys].xp_readerrorlog @RestartInfoLog, 1, N'Database Instant File Initialization: enabled', NULL, @LogWindowStart, @LogWindowEnd;
		INSERT @xp_errorlog
		EXEC [sys].xp_readerrorlog @RestartInfoLog, 1, N'System Manufacturer:', NULL, @LogWindowStart, @LogWindowEnd;
		INSERT @xp_errorlog
		EXEC [sys].xp_readerrorlog @RestartInfoLog, 1, N'(SPN)', NULL, @LogWindowStart, @LogWindowEnd;
		/* end-unbounded on purpose: AG listeners created or brought online AFTER startup also
		   write 'Server is listening on' and belong in the port inventory (verify 2026-07-08) */
		INSERT @xp_errorlog
		EXEC [sys].xp_readerrorlog @RestartInfoLog, 1, N'Server is listening on', NULL, @LogWindowStart, NULL;
		/* 2026-07-08: the old extra read for 'A self-generated certificate was successfully
		   loaded for encryption.' was a strict substring-superset of the read below, so those
		   rows were inserted TWICE and Section 109 emitted duplicates. One read only. */
		INSERT @xp_errorlog
		EXEC [sys].xp_readerrorlog @RestartInfoLog, 1, N'certificate was successfully loaded for encryption.', NULL, @LogWindowStart, @LogWindowEnd;
		/* @RestartInfoRead (see its declaration): an archive left out by the cap holds the startup lines unless they were found. */
		IF EXISTS (SELECT 1 FROM @xp_errorlog WHERE [Text] LIKE 'Command Line Startup Parameters:%')
			OR NOT EXISTS (SELECT 1 FROM @ErrorLogCapped WHERE LogKind = N'SQL Server')
			SET @RestartInfoRead = 1;
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Restart info from the error log', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		RAISERROR (N'Restart-info error-log read skipped/failed (expected on Azure)',0,1) WITH NOWAIT;
	END CATCH
	

	--SELECT CONVERT(MONEY,LEFT(Character_Value,3)) FROM @msversion WHERE Name = 'WindowsVersion'

SELECT @errMessage  = 'Checking power plan';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
	END

		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 

BEGIN TRY
	DECLARE @value [NVARCHAR] (64);
	DECLARE @key [NVARCHAR] (512); 
	DECLARE @WindowsVersion [NVARCHAR] (50);
	DECLARE @PowerPlan [NVARCHAR] (20);
	SET @key = 'SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes';
	SELECT @WindowsVersion = CONVERT(MONEY,LEFT(Character_Value,3)) 
	FROM @msversion 
	WHERE Name = 'WindowsVersion';

	/* 2026-07-08: duplicate 'locked pages' read removed - the restart-info block above
	   already inserted those rows once. */

	/*CASE WHEN windows_release IN ('6.3','10.0') AND (@@VERSION LIKE '%Build 10586%' OR @@VERSION LIKE '%Build 14393%')THEN '10.0' ELSE CONVERT([VARCHAR](5),windows_release) END 
	FROM [sys].dm_os_windows_info (NOLOCK);*/


	IF CONVERT(DECIMAL(3,1), @WindowsVersion) >= 6.0
	BEGIN
	
		DECLARE @cpu_name [NVARCHAR] (150);
		DECLARE @cpu_ghz [NVARCHAR] (50);

		DECLARE @cpu_speed_mhz int;
        DECLARE @cpu_speed_ghz decimal(18,2);

										
		IF @CanReadRegistry = 1
		BEGIN
		EXEC xp_regread @rootkey = 'HKEY_LOCAL_MACHINE',
		@key = 'HARDWARE\DESCRIPTION\System\CentralProcessor\0',
		@value_name = 'ProcessorNameString',
		@value = @cpu_name OUTPUT;
		
		EXEC xp_regread @rootkey = 'HKEY_LOCAL_MACHINE',
        @key = 'HARDWARE\DESCRIPTION\System\CentralProcessor\0',
        @value_name = '~MHz',
        @value = @cpu_speed_mhz OUTPUT;

										

		SELECT @cpu_speed_ghz = CAST(CAST(@cpu_speed_mhz AS DECIMAL) / 1000 AS DECIMAL(18,2));
		SELECT @cpu_ghz = @cpu_speed_ghz;

		
		EXEC xp_regread 
		@rootkey = 'HKEY_LOCAL_MACHINE',
		@key = 'SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes',
		@value_name = 'ActivePowerScheme',
		@value = @value OUTPUT;

		
		IF LEN(ISNULL(@value,0)) < 5
		BEGIN
			EXEC master..xp_regread
			@rootkey = 'HKEY_LOCAL_MACHINE',
			@key = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ControlPanel\NameSpace\{025A5937-A6BE-4686-A844-36FE4BEC8B6D}',
			@value_name = 'PreferredPlan',
			@value = @value OUTPUT;
		END
		
		SELECT @PowerPlan = CASE @value 
			WHEN '381b4222-f694-41f0-9685-ff5bb260df2e' THEN 'Balanced'
			WHEN '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c' THEN 'High performance'
			WHEN 'a1841308-3541-4fab-bc81-f71556f20b4a' THEN 'Power saver'
			ELSE 'Custom Power Scheme'
		END;
		END
		ELSE IF @IsSQLAzure = 0 AND @IsSQLMI = 0 /* on Azure or MI the registry is not read by anyone: no skip row */
			INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (N'Power plan', 0, N'skipped: reading the Windows registry needs sysadmin');
		/*PRINT @PowerPlan*/
		IF @Debug = 1
			BEGIN
				SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
				SET @DebugTime = GETDATE();
				IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
			RAISERROR (N'Power Options checked',0,1) WITH NOWAIT;
		END
		
	END

END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Power plan', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  SELECT @errMessage  = ERROR_MESSAGE()
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH
	DECLARE @DTUtable TABLE
	(
		end_time DATETIME
		, avg_DTU_percent MONEY
	);
IF @IsSQLAzure = 1
BEGIN
	BEGIN TRY
		SET @dynamicSQL = '
		SELECT  end_time
		, (
			SELECT Max(v) 
			FROM (VALUES (avg_cpu_percent), (avg_data_io_percent), (avg_log_write_percent)
		) AS value(v)) 
		AS [avg_DTU_percent] 
		FROM [sys].[dm_elastic_pool_resource_stats]  
		ORDER BY end_time DESC;';
		INSERT @DTUtable
		EXEC sp_executesql @dynamicSQL;
		SELECT @PowerPlan = 'MAX DTU '+ CONVERT([VARCHAR](10),MAX(avg_DTU_percent)) 
		FROM @DTUtable;
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Azure DTU usage', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		/* Not an elastic pool: no DTU history. Still Azure SQL DB. */
	END CATCH
	
END




SELECT @errMessage  = 'Checking server properties'
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
BEGIN TRY

	SET @rebuildonline = 'OFF';				/* Assume this is not Enterprise, we will test in the next line and if it is , woohoo. */
	SELECT @isEnterprise = PATINDEX('%enterprise%',@@Version) OPTION (RECOMPILE);
	IF (@isEnterprise > 0) 
	BEGIN 
		SET @rebuildonline = 'ON'; /*Can also use CAST(SERVERPROPERTY('EngineEdition') AS INT), thanks http://www.brentozar.com/ */
	END;

	SELECT @CPUcount = cpu_count 
	, @CPUsocketcount = [cpu_count] / [hyperthread_ratio]
	, @CPUHyperthreadratio = [hyperthread_ratio]
	FROM [sys].dm_os_sys_info;
		
	SELECT @TempDBFileCount = COUNT(*)
	FROM [tempdb].[sys].database_files
	WHERE state = 0 /*Online*/ 
	AND type = 0; /*Rows*/
		
	
	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, Details
	) 
	SELECT 0
	,'@' + CONVERT([VARCHAR](20),GETDATE(),120)
	,'------'
	,'------';
	
	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
		SELECT
		0
		, 'Version'
		, @MagicVersion;

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
		SELECT
		0
		, 'Domain'
		, @ThisDomain;

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
		SELECT
		0
		,'Server'
		, @ThisServer;

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
		SELECT
		0
		, 'ServerProperties'
		, ISNULL(@SP_MachineName ,'')
		+ ';' + ISNULL(@SP_INSTANCENAME  ,'')
		+ ';' + ISNULL(@SP_PRODUCTVERSION  ,'')
		+ ';' + ISNULL(@SP_SQL_VERSION  ,'')
		+ ';' + ISNULL(@SP_PRODUCTLEVEL  ,'')
		+ ';' + ISNULL(@SP_EDITION  ,'')
		+ ';' + ISNULL(@SP_ISCLUSTERED  ,'');

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
		SELECT
		0
		, 'UTCTimeStamp'
		, GETUTCDATE() ;

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
		SELECT
		0
		, 'UTCTimeStampOffset'
		, DATEDIFF(hh, getutcdate(), getdate()) ;

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, Details
	)
		SELECT 
		DISTINCT
			0
			, 'Check 3rd Party Backups'
			, [Check 3rd Party Backups]
			, type
		FROM
		(
		SELECT  
		DISTINCT
			CONVERT(CHAR(100), SERVERPROPERTY('Servername')) AS Server
			,database_name
			--msdb.dbo.backupset.database_name,  
			--MAX(msdb.dbo.backupset.backup_finish_date) AS last_db_backup_date 
			--SELECT *
			--,physical_device_name
			--, backup_start_date
			, type
			, recovery_model
			,CASE 
				WHEN physical_device_name LIKE '%:\%bak%' THEN 'Seems like normal file paths'
				WHEN physical_device_name LIKE '%:\%trn%' THEN 'Seems like normal file paths'
				WHEN physical_device_name LIKE '%{%}%' THEN '!3rd party tool fool!'
				ELSE 'Likely a 3rd party tool'
			END [Check 3rd Party Backups]
			FROM msdb.dbo.backupmediafamily  
			INNER JOIN msdb.dbo.backupset ON msdb.dbo.backupmediafamily.media_set_id = msdb.dbo.backupset.media_set_id 
			WHERE backup_start_date > DATEADD(WEEK,-4,GETDATE())
		)T



	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
		SELECT DISTINCT
		0
		, 'Port - from connections'
		, cast(local_tcp_port as [VARCHAR](10))
		FROM [sys].dm_exec_connections 
		WHERE local_tcp_port IS NOT NULL;

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
		SELECT
		0
		, 'Port - from configuration'
		,  [Text]
		FROM @xp_errorlog
		WHERE [Text] 
		LIKE '%Server is listening on%';

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
		SELECT 
		0
		,'User'
		,CURRENT_USER;

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
		SELECT 
		0
		,'Logged in'
		, SYSTEM_USER;

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
		SELECT
		0
		,'Server Install Date'
		, CONVERT([NVARCHAR] (25),CONVERT([VARCHAR],create_date,120)) as 'SQL Server Installation Date' 
		FROM [sys].server_principals  
		WHERE name='NT AUTHORITY\SYSTEM';

	/* Not written when the registry read was skipped (a login that is not sysadmin): a plan never read is not a finding. */
	IF @PowerPlan IS NOT NULL OR @CanReadRegistry = 1
	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, Severity
	)
		SELECT 
		0
		, 'Power Plan'
		, @PowerPlan
		, CASE 
		WHEN @PowerPlan = 'High performance' THEN @Result_Good 
		ELSE @Result_Warning 
		END;
	
	BEGIN 
		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary
			, Severity
		)
		SELECT 
		0
		, CASE 
		WHEN @CPUHyperthreadratio <> @CPUcount THEN 'Bad CPU balance' 
		ELSE 'CPU balance' 
		END
		, '['+REPLICATE('#', CONVERT(MONEY,(@CPUcount /  @CPUHyperthreadratio))) +'] CPU Sockets ['+REPLICATE(@Sparkle, CONVERT(MONEY,(@CPUcount))) +'] CPUs'
		, @Result_Warning 
	END;
	
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Server properties', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH


DECLARE @HostName [NVARCHAR] (100);
DECLARE @Address [NVARCHAR] (100);
DECLARE @Customer [NVARCHAR] (50);
DECLARE @MachineKey1 VARBINARY(100) ; 
DECLARE @MachineKey [NVARCHAR] (100);

DECLARE @HashMe [NVARCHAR] (250);
SET @CharToCheck = CHAR(92);


SELECT  @HashMe = ISNULL(@ThisDomain,'') + 'SQLDBA.ORG' + ISNULL(hostname,'') + ISNULL(@ThisServer,'') + ISNULL(net_address ,'')
, @HostName = REPLACE(hostname,' ','')
, @Address = net_address
FROM 
(
	SELECT 
	DISTINCT 
	TOP 1 
		hostname
		,net_address
	FROM dbo.sysprocesses
	where 1=1--spid = @@SPID
	AND program_name IN
	(
		'SQLAgent - Generic Refresher'
		,'Core Microsoft SqlClient Data Provider'                                                                                         
		,'Microsoft SQL Server Extension Agent' 
	)
	AND LEN(hostname) > 1
) sp


SET @MachineKey1 = HASHBYTES('SHA1',@HashMe);
SET @MachineKey = UPPER(CONVERT([VARCHAR](34),@MachineKey1, 1));


--select @ThisDomain [Domain], @HostName [HostName],@ThisServer [SQLInstance] ,@Address [Address]
--, HASHBYTES('SHA1 ',@HashMe) [MachineKey]
--from master.dbo.sysprocesses
--where spid = @@SPID

DECLARE @serverdisplayname [NVARCHAR] (250);
SET @serverdisplayname = 'SQL Notifications'
+ '|'+RIGHT(CONVERT([NVARCHAR] (250),@MachineKey,1),7)
+ CONVERT([NVARCHAR] (8),@MachineKey,1)
+ 'x'+SUBSTRING(CONVERT([NVARCHAR] (100),@MachineKey,1),7,6);


BEGIN TRY
		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary
		)
		SELECT 
		0
		, 'MackKey'
		, RIGHT(CONVERT([NVARCHAR] (250),@MachineKey,1),7)
		+ CONVERT([NVARCHAR] (8),@MachineKey,1)
		+ 'x'+SUBSTRING(CONVERT([NVARCHAR] (100),@MachineKey,1),7,6);
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Machine key', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH

/*----------------------------------------
			--Check for current supported build of SQL server
-------------------------------------*/

SELECT @errMessage  = 'Checking server build versions';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
END
BEGIN TRY
	DECLARE @CurrentBuild [NVARCHAR] (50);
	SELECT @CurrentBuild = [Character_Value] 
	FROM @msversion 
	WHERE [Name] = 'ProductVersion' ;

	DECLARE @pstext [NVARCHAR] (4000);

	/*What does Microsoft say about support*/
	DECLARE @SQLproductlevel [NVARCHAR] (50);
	DECLARE @SQLVersionText [NVARCHAR] (200);

	SELECT @SQLproductlevel = CONVERT([VARCHAR](50),SERVERPROPERTY ('productlevel'))
	IF @SQLproductlevel = 'RTM'
		SET @SQLproductlevel = '';


	DECLARE @TrimVersion [NVARCHAR] (250);
	SET @TrimVersion = RTRIM(LTRIM(REPLACE(LEFT(@@VERSION,PATINDEX('% - %',@@VERSION)), 'Microsoft SQL Server ','')));
		
		
	DECLARE @MyBuild [NVARCHAR] (50);
	SELECT @MyBuild = CONVERT([NVARCHAR] (50),SERVERPROPERTY('productversion'));


	DECLARE @BuildTable TABLE
	(
		[Server] [NVARCHAR] (250)
		, MajorBuild [NVARCHAR] (5)
		, SupportEnds [NVARCHAR] (25)
		, MinBuild [NVARCHAR] (25)
		, MaxBuild [NVARCHAR] (25)
	)
	/*Kind of not being used anymore. The checks are done in the AI engine*/
	INSERT INTO @BuildTable 
		SELECT '2000', '8', '2007-10-07', '8.0.047', '8.0.997' 
		UNION ALL SELECT '2000', '8', '2013-09-04', '8.0.2039', '8.0.2305' 
		UNION ALL SELECT '2005', '9', '2016-12-04', '9.0.1399', '9.0.5324.00' 
		UNION ALL SELECT '2008', '10', '2019-09-07', '10.0.1019.17', '10.0.6556.0' 
		UNION ALL SELECT '2008R2', '10', '2019-09-07', '10.50.1092.20', '10.50.6560.0' 
		UNION ALL SELECT '2012', '11', '2014-01-14', '11.0.1103.9', '11.0.2100.60' 
		UNION ALL SELECT '2012', '11', '2015-07-14', '11.0.2214.0', '11.0.3128.0' 
		UNION ALL SELECT '2012', '11', '2017-01-10', '11.0.3153.0', '11.0.5678.0' 
		UNION ALL SELECT '2012', '11', '2018-10-09', '11.0.6020.0', '11.0.6615.2' 
		UNION ALL SELECT '2012', '11', '2022-07-12', '11.0.7001.0', '11.0.7493.4' 
		UNION ALL SELECT '2014', '12', '2016-07-12', '12.0.1524.0', '12.0.2569.0' 
		UNION ALL SELECT '2014', '12', '2020-01-14', '12.0.4050.0', '12.0.5687.1' 
		UNION ALL SELECT '2014', '12', '2024-07-09', '12.0.6024.0', '12.0.6372.1' 
		UNION ALL SELECT '2016', '13', '2018-01-09', '13.0.1000.281', '13.0.3900.73' 
		UNION ALL SELECT '2016', '13', '2019-07-09', '13.0.4001.0', '13.0.4604.0' 
		UNION ALL SELECT '2016', '13', '2026-07-14', '13.0.5026.0', '13.0.5820.21' 
		UNION ALL SELECT '2017', '14', '2027-10-12', '14.0.1.246', '14.0.900.75' 
		UNION ALL SELECT '2019', '15', '2030-01-08', '15.0.1000.34', '15.0.4999.99'
		UNION ALL SELECT '2022', '16', '2033-01-11', '16.0.1000.6', '16.0.4999.99'
		UNION ALL SELECT '2025', '17', '2036-01-01', '17.0.1000.0', '17.9.9999.99';
	
	/*This step requires administrative permissions on the local machine for SQL server Service account, at least it does not play nicely with "NT xx" accounts*/
	INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary
			, Severity
			, Details
		)
	SELECT  0 
		, 'Supported'
		, CASE WHEN SupportEnds < GETDATE() THEN '!BUILD NOT SUPPORTED!' ELSE 'Build in support' END
		, CASE WHEN SupportEnds < GETDATE() THEN @Result_YourServerIsDead ELSE @Result_Good END 
		, 'Build:' + @MyBuild 
		+ ISNULL('; [Mainstream Support End Date]:' + CONVERT([VARCHAR],SupportEnds,120),'')
	 FROM @BuildTable
	 WHERE @MyBuild BETWEEN MinBuild AND MaxBuild ;
	
	IF @Debug = 1
		RAISERROR (N'Evaluated build support END date',0,1) WITH NOWAIT;

	IF @Debug = 1
		RAISERROR (N'Check Server name',0,1) WITH NOWAIT;
	IF (@ThisServer <> @@servername )
	BEGIN
		INSERT #output_sqldba_org_sp_triage (SectionID,Section,Summary, Severity)
		SELECT 0, 'ServerName is wrong','Current Server Name:' + CAST (Serverproperty( 'ComputerNamePhysicalNetBIOS' ) AS [NVARCHAR] (250)) + '; SQL Instance Name:' + CAST (@@SERVERNAME AS [NVARCHAR] (250)) , @Result_Warning 
	END

 
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Server build versions', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH


  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'CPU NUMA node details',0,1) WITH NOWAIT;
END
BEGIN TRY
	IF @CPUHyperthreadratio <> @CPUcount
		INSERT #output_sqldba_org_sp_triage (SectionID,Section,Summary, Severity)
		SELECT 0
		, 'CPU NUMA node details'
		, 'Socket ID;cpu_id;is_online;FlagMe;load_factor;%current_tasks;%current_workers;%active_workers;%context_switches;%preemptive_switches;%idle_switches'
		, @Result_Warning ;
	INSERT #output_sqldba_org_sp_triage (SectionID,Section,Summary, Severity)
	SELECT 0
	, 'CPU NUMA node details'
	, CONVERT([VARCHAR](2),parent_node_id)
	+';'+ CONVERT([VARCHAR](2),cpu_id)
	+';'+ CONVERT([VARCHAR](2),is_online)
	+';'+ ''
	+ CASE WHEN failed_to_create_worker /*This generally occurs because of memory constraint*/ > 0 THEN 'failed_to_create_worker,' ELSE '' END
	+ CASE WHEN work_queue_count > 0 THEN 'work_queue_count,' ELSE '' END
	+ CASE WHEN pending_disk_io_count > 0 THEN 'pending_disk_io_count,' ELSE '' END 
	+';'+ CONVERT([VARCHAR](2),load_factor)
	+';'+ CONVERT([VARCHAR](20),CONVERT(MONEY,current_tasks_count)/SUM(CONVERT([BIGINT],current_tasks_count)) OVER(PARTITION BY  parent_node_id) *100)
	+';'+ CONVERT([VARCHAR](20),CONVERT(MONEY,current_workers_count)/SUM(CONVERT([BIGINT],current_workers_count)) OVER(PARTITION BY  parent_node_id) *100)
	+';'+ CONVERT([VARCHAR](20),CONVERT(MONEY,active_workers_count)/SUM(CONVERT([BIGINT],active_workers_count)) OVER(PARTITION BY  parent_node_id) *100 )
	/*, current_tasks_count
	, current_workers_count
	, active_workers_count */
	/*SQL Server 2016 +
	,    DATEADD(ms, total_cpu_usage_ms, DATEADD(DAY,total_cpu_usage_ms/1000/60/60/24 ,0)) [total_cpu_usage]
	,  +  DATEADD(ms, total_scheduler_delay_ms, DATEADD(DAY,total_scheduler_delay_ms/1000/60/60/24 ,0)) [total_scheduler_delay]
	*/
	+';'+ CONVERT([VARCHAR](20),CONVERT(MONEY,context_switches_count)/SUM(CONVERT([BIGINT],context_switches_count)) OVER(PARTITION BY  parent_node_id) *100 )
	+';'+ CONVERT([VARCHAR](20),CONVERT(MONEY,preemptive_switches_count)/SUM(CONVERT([BIGINT],preemptive_switches_count)) OVER(PARTITION BY  parent_node_id) *100)
	+';'+ CONVERT([VARCHAR](20),CONVERT(MONEY,idle_switches_count)/SUM(CONVERT([BIGINT],idle_switches_count)) OVER(PARTITION BY  parent_node_id) *100 )
	, @Result_Warning 
	FROM [sys].dm_os_schedulers
	WHERE status = 'VISIBLE ONLINE';
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'CPU NUMA node details', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH



SELECT @errMessage  = 'Checking Antivirus scanning SQL process';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
BEGIN TRY

	BEGIN
	SET @dynamicSQL = 'SELECT 0
		, ''Antivirus scanning SQL process''
		, ISNULL(company,'''')
		, '''+@Result_Warning +'''
		, ISNULL(description,'''') + '';''+name
		FROM [sys].dm_os_loaded_modules
		WHERE company <> ''Microsoft Corporation'' '

		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary
			, Severity
			, Details
		)
		EXEC sp_executesql @dynamicSQL;
		
	END
		
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Antivirus scanning SQL process', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH



SELECT @errMessage  = 'Checking tempDB file count';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
BEGIN TRY
	/* Both counts are set by the Server properties read; NULL when it was skipped (its skip row says why). */
	IF @CPUsocketcount IS NOT NULL AND @TempDBFileCount IS NOT NULL
	BEGIN
		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary
			, Severity
			, Details
		)
		SELECT 0
		, 'Interesting TempDB file count' 
		, '['+REPLICATE('#', @CPUsocketcount) +'] CPU Sockets ['+REPLICATE(@Sparkle, CONVERT(MONEY,(@TempDBFileCount))) +'] TempDB Files'
		, @Result_Warning
		, 'Check disk latency on the TempDB files';
	END
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'TempDB file count', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH

SELECT @errMessage  = 'Looking for tempdb latching';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
BEGIN TRY
	IF EXISTS
	(
		SELECT 1 
		FROM  [sys].dm_os_waiting_tasks
		WHERE wait_type LIKE 'PAGE%LATCH_%'
		AND resource_description Like '2:%'
	)
	BEGIN
	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, Severity
		, Details 
	)
	SELECT 0
		, 'Latches in TempDB', 'Session: ' + CONVERT([VARCHAR](10),ISNULL(session_id,'')) 
		+ '; Wait Type: ' + CONVERT([VARCHAR](50), ISNULL(wait_type,''))
		+ '; Wait Duraion: ' + CONVERT([VARCHAR](25), ISNULL(wait_duration_ms,''))
		+ '; Blocking SPID: ' + CONVERT([VARCHAR](20), ISNULL(blocking_session_id,''))
		+ '; Description: ' + CONVERT([VARCHAR](200), ISNULL(resource_description,''))
		/* tempdb allocation pages (lane tempdb-page-twins, 2026-10-08). Page 1 or a positive multiple of 8088 is PFS;
		   page 2 or a positive multiple of 511232 is GAM; page 3 or a positive multiple of 511232 plus 1 is SGAM; page 0 is the
		   file header, which both modulo terms would match, so p.pg > 0 comes first. Until 2026-10-08 this read
		   "<page> - 1 % 8088 = 0", which T-SQL parses as "<page> - (1 % 8088) = 0": pages 1, 2 and 3 only.
		   The tempdb_contention alert uses the same predicate; TempdbAllocationPageCensusTests checks which pages every shipped
		   copy selects and records the one vendored exception. */
		, Case
			WHEN p.pg > 0 AND (p.pg = 1 OR p.pg % 8088 = 0 OR p.pg = 2 OR p.pg % 511232 = 0 OR p.pg = 3 OR p.pg % 511232 = 1) Then @Result_Warning
			ELSE @Result_Good
			End 
		, CONVERT([VARCHAR](200), Case
			WHEN p.pg > 0 AND (p.pg = 1 OR p.pg % 8088 = 0) Then 'Is PFS Page'
			WHEN p.pg > 0 AND (p.pg = 2 OR p.pg % 511232 = 0) Then 'Is GAM Page'
			WHEN p.pg > 0 AND (p.pg = 3 OR p.pg % 511232 = 1) Then 'Is SGAM Page'
			ELSE 'Is Not PFS, GAM, or SGAM page'
			End
		)
		FROM [sys].dm_os_waiting_tasks
		CROSS APPLY (SELECT Cast(Right(resource_description, Len(resource_description) - Charindex(':', resource_description, 3)) As Int) AS pg) AS p
		WHERE wait_type LIKE 'PAGE%LATCH_%'
		AND resource_description LIKE '2:%';

	END	
			
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'TempDB latching', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH


SELECT @errMessage  = 'Checking errorlog';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
END
BEGIN TRY
	/* @RestartInfoRead (see its declaration): with the read skipped, both rows below would judge an empty table. */
	IF @RestartInfoRead = 1
	BEGIN

	IF EXISTS
	( 
		SELECT * 
		FROM @xp_errorlog 
		WHERE [Text] 
		LIKE '%locked pages%'
	)
	BEGIN
		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary
			, Severity
		)
		SELECT 0
		, 'Locked Pages in Memory'
		, 'Nice. Best practice has a way of coming around.'
		, @Result_Warning;
	END
	ELSE
	BEGIN
		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary
			, Severity
		)
		SELECT 0
		, 'Locked Pages in Memory'
		, 'Consider locking pages in memory. Especially for stacking.'
		, @Result_Warning;
	END
	IF NOT EXISTS 
	( 
		SELECT * 
		FROM @xp_errorlog 
		WHERE [Text] LIKE '%File Initialization%'
	)
	BEGIN
		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary
			, Severity
		)
		SELECT 0
		, 'Instant File Initialization is OFF'
		, 'Consider enabling this. Speeds up database data file growth.'
		, @Result_Warning;
	END
	END
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Locked Pages in Memory', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH

			/*----------------------------------------
			--Check for current service account
			----------------------------------------*/

SELECT @errMessage  = 'Checking service account details';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
BEGIN TRY
		DECLARE @SQLsn [NVARCHAR] (128);
/*
			SELECT DSS.servicename,
			DSS.startup_type_desc,
			DSS.status_desc,
			DSS.last_startup_time,
			DSS.service_account,
			DSS.is_clustered,
			DSS.cluster_nodename,
			DSS.filename,
			DSS.startup_type,
			DSS.status,
			DSS.process_id
			FROM [sys].dm_server_services AS DSS
			WHERE servicename like 'SQL Server%';*/
	BEGIN TRY
		DECLARE @DBEngineLogin [VARCHAR](100);
		DECLARE @DBAgentLogin [VARCHAR](100);

		IF @SQLVersion >= 11 
		BEGIN 
		
			SET @dynamicSQL = '
			SELECT 0
			, ''SQL Service Account''
			, [services].service_account
			FROM [sys].dm_server_services AS [services]
			WHERE servicename like ''SQL Server (%'';';
			
			INSERT #output_sqldba_org_sp_triage 
			(
				SectionID
				, Section
				, Summary
			)
			EXEC sp_executesql @dynamicSQL ;

			SELECT @DBEngineLogin = Summary
			FROM #output_sqldba_org_sp_triage 
			WHERE SectionID = 0
			AND Section = 'SQL Service Account';


			SET @dynamicSQL = '
			SELECT 0
			, ''SQL Service Agent Account''
			, [services].service_account
			FROM [sys].dm_server_services AS [services]
			WHERE servicename like ''SQL Server Agent (%'';';

			INSERT #output_sqldba_org_sp_triage 
			(
				SectionID
				, Section
				, Summary
			)
			EXEC sp_executesql @dynamicSQL ;
		END
		
		If @SQLVersion < 11 
		BEGIN 

 
		IF @CanReadRegistry = 1
		BEGIN
		EXECUTE xp_instance_regread
		   @rootkey = N'HKEY_LOCAL_MACHINE',
		   @key = N'SYSTEM\CurrentControlSet\Services\MSSQLServer',
		   @value_name = N'ObjectName',
		   @value = @DBEngineLogin OUTPUT;
 
		INSERT #output_sqldba_org_sp_triage 
			(
				SectionID
				, Section
				, Summary
			)
			SELECT 0
			,'SQL Service Account'
			, @DBEngineLogin service_account;
		
		
		
		EXECUTE xp_instance_regread
		   @rootkey = N'HKEY_LOCAL_MACHINE',
		   @key = N'SYSTEM\CurrentControlSet\Services\SQLSERVERAGENT',
		   @value_name = N'ObjectName',
		   @value = @DBAgentLogin OUTPUT;
		
		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary
		)
		SELECT 0
		,'SQL Service Agent Account'
		, @DBAgentLogin service_account;

		END
		ELSE
			INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (N'Service accounts', 0, N'skipped: reading the Windows registry needs sysadmin');
		END

	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Service account names', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	  SELECT @errMessage  = ERROR_MESSAGE()
	  IF @Debug = 1
		BEGIN
			SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
			SET @DebugTime = GETDATE();
			IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
			RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
		END
	END CATCH

END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Service account details', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH
	
			/*----------------------------------------
			--Check for high worker thread usage
			----------------------------------------*/
SELECT @errMessage  = 'Checking for high worker thread usage';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
BEGIN TRY

	DECLARE @workerthreadspercentage FLOAT;
	SELECT @workerthreadspercentage  = 
		(
		SELECT CONVERT(MONEY,SUM(current_workers_count)) as [Current worker thread] 
		FROM [sys].dm_os_schedulers
		)*100/max_workers_count 
	FROM [sys].dm_os_sys_info ;
	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	) 
	SELECT 0
		, 'HIGH Worker Thread Usage'
		, '------';

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, Severity
	)
		SELECT 0
		, 'Worker threads'
		, CONVERT([VARCHAR](20)
		, (
			SELECT 
			CONVERT(MONEY,SUM(current_workers_count)) as [Current worker thread] 
			FROM [sys].dm_os_schedulers)*100/max_workers_count) 
			+ '% workes used. With average work queue count'
			+ CONVERT([VARCHAR](15),(
			SELECT AVG (CONVERT(MONEY,work_queue_count))
			FROM  [sys].dm_os_schedulers 
			WHERE STATUS = 'VISIBLE ONLINE' )
			)
		, CASE 
		WHEN @workerthreadspercentage > 65 THEN @Result_Warning 
		ELSE @Result_Good 
		END
		FROM [sys].dm_os_sys_info;

IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Looked at worker thread usage',0,1) WITH NOWAIT;
	END

END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Worker thread usage', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  SELECT @errMessage  = ERROR_MESSAGE();
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH	
			 /*----------------------------------------
			--Performance counters
			----------------------------------------*/


	BEGIN TRY
	SELECT @ts = 
	(
		SELECT cpu_ticks/(cpu_ticks/ms_ticks)
		FROM [sys].dm_os_sys_info 
	) OPTION (RECOMPILE);
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'CPU clock for the CPU history', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE();
		IF @Debug = 1 RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	END CATCH
	
DECLARE @PerformanceCounterList TABLE
	(
	[counter_name] [VARCHAR](500) NULL
	, [is_captured_in] [BIT] NOT NULL
	)
DECLARE @PerformanceCounter TABLE
	(
	[CounterName] [VARCHAR](250)  NULL
	, [CounterValue] [VARCHAR](250) NULL
	, [DateSampled] [DATETIME] NOT NULL
	)


DECLARE @loops INT;
SET @loops = 5;

BEGIN TRY
	   DECLARE @perfStr [VARCHAR](100);
       DECLARE @instStr [VARCHAR](100);
	   IF @IsSQLAzure = 1
	   BEGIN
			SET @instStr = 'MSSQLSERVER';
			SET @ExportDBName = QUOTENAME(DB_NAME());
	   END
	   ELSE
	   BEGIN
	   	DECLARE @servicenametable TABLE 
			(
				ServiceName [NVARCHAR] (250)
	   		)
	  
			SET @dynamicSQL = 'SELECT @@SERVICENAME';
		 	INSERT @servicenametable
		 	EXEC sp_executesql @dynamicSQL 
		 	SELECT @instStr = ServiceName 
		 	FROM @servicenametable;
	   END
       

       IF(@instStr = 'MSSQLSERVER')
              SET @perfStr = '\SQLServer';
       ELSE 
              SET @perfStr = '\MSSQL$' + @instStr;

		INSERT INTO @PerformanceCounterList(
			counter_name
			, is_captured_in
		) 
		SELECT 
			counter_name
			, is_captured_in
		FROM 
		(
			VALUES ('\Memory\Pages/sec',1)
			, ('\Memory\Pages Input/sec',1)
			, ('\Memory\Available MBytes',1)
			, ('\Processor(_Total)\% Processor Time',1)
			, ('\Processor(_Total)\% Privileged Time',1)
			, ('\Process(sqlservr)\% Privileged Time',1)
			, ('\Process(sqlservr)\% Processor Time',1)
			, ('\Paging File(_Total)\% Usage',1)
			, ('\Paging File(_Total)\% Usage Peak',1)
			, ('\PhysicalDisk(_Total)\Avg. Disk sec/Read',1)
			, ('\PhysicalDisk(_Total)\Avg. Disk sec/Write',1)
			, ('\PhysicalDisk(_Total)\Disk Reads/sec',1)
			, ('\PhysicalDisk(_Total)\Disk Writes/sec',1)
			, ('\System\Processor Queue Length',1)
			, ('\System\Context Switches/sec',1)
			, (@perfStr + ':Buffer Manager\Page life expectancy',1)
			, (@perfStr + ':Buffer Manager\Buffer cache hit ratio',1)
			, (@perfStr + ':Buffer Manager\Checkpoint Pages/Sec',1)
			, (@perfStr + ':Buffer Manager\Lazy Writes/Sec',1)
			, (@perfStr + ':Buffer Manager\Page Reads/Sec',1)
			, (@perfStr + ':Buffer Manager\Page Writes/Sec',1)
			, (@perfStr + ':Buffer Manager\Page Lookups/Sec',1)
			, (@perfStr + ':Buffer Manager\Free List Stalls/sec',1)
			, (@perfStr + ':Buffer Manager\Readahead pages/sec',1)
			, (@perfStr + ':Buffer Manager\Database Pages',1)
			, (@perfStr + ':Buffer Manager\Target Pages',1)
			, (@perfStr + ':Buffer Manager\Total Pages',1)
			, (@perfStr + ':Buffer Manager\Stolen Pages',1)
			, (@perfStr + ':General Statistics\User Connections',1)
			, (@perfStr + ':General Statistics\Processes blocked',1)
			, (@perfStr + ':General Statistics\Logins/Sec',1)
			, (@perfStr + ':General Statistics\Logouts/Sec',1)
			, (@perfStr + ':Memory Manager\Memory Grants Pending',1)
			, (@perfStr + ':Memory Manager\Total Server Memory (KB)',1)
			, (@perfStr + ':Memory Manager\Target Server Memory (KB)',1)
			, (@perfStr + ':Memory Manager\Granted Workspace Memory (KB)',1)
			, (@perfStr + ':Memory Manager\Maximum Workspace Memory (KB)',1)
			, (@perfStr + ':Memory Manager\Memory Grants Outstanding',1)
			, (@perfStr + ':SQL Statistics\Batch Requests/sec',1)
			, (@perfStr + ':SQL Statistics\SQL Compilations/sec',1)
			, (@perfStr + ':SQL Statistics\SQL Re-Compilations/sec',1)
			, (@perfStr + ':SQL Statistics\Auto-Param Attempts/sec',1)
			, (@perfStr + ':Locks(_Total)\Lock Waits/sec',1)
			, (@perfStr + ':Locks(_Total)\Lock Requests/sec',1)
			, (@perfStr + ':Locks(_Total)\Lock Timeouts/sec',1)
			, (@perfStr + ':Locks(_Total)\Number of Deadlocks/sec',1)
			, (@perfStr + ':Locks(_Total)\Lock Wait Time (ms)',1)
			, (@perfStr + ':Locks(_Total)\Average Wait Time (ms)',1)
			, (@perfStr + ':Latches\Total Latch Wait Time (ms)',1)
			, (@perfStr + ':Latches\Latch Waits/sec',1)
			, (@perfStr + ':Latches\Average Latch Wait Time (ms)',1)
			, (@perfStr + ':Access Methods\Forwarded Records/Sec',1)
			, (@perfStr + ':Access Methods\Full Scans/Sec',1)
			, (@perfStr + ':Access Methods\Page Splits/Sec',1)
			, (@perfStr + ':Access Methods\Index Searches/Sec',1)
			, (@perfStr + ':Access Methods\Workfiles Created/Sec',1)
			, (@perfStr + ':Access Methods\Worktables Created/Sec',1)
			, (@perfStr + ':Access Methods\Table Lock Escalations/sec',1)
			, (@perfStr + ':Cursor Manager by Type(_Total)\Active cursors',1)
			, (@perfStr + ':Transactions\Longest Transaction Running Time',1)
			, (@perfStr + ':Transactions\Free Space in tempdb (KB)',1)
			, (@perfStr + ':Transactions\Version Store Size (KB)',1)
				--, ('\LogicalDisk(*)\Avg. Disk Queue Length',1)
				--, ('\LogicalDisk(*)\Avg. Disk sec/Read',1)
				--, ('\LogicalDisk(*)\Avg. Disk sec/Transfer',1)
				--, ('\LogicalDisk(*)\Avg. Disk sec/Write',1)
			, ('\LogicalDisk(*)\Current Disk Queue Length',1)
			, ('\Paging File(*)\*',1)
			, ('\LogicalDisk(_Total)\Disk Reads/sec',1)
			, ('\LogicalDisk(_Total)\Disk Writes/sec',1)
			, ('\SQLServer:Databases(_Total)\Log Bytes Flushed/sec',1)
		) AS X(counter_name,is_captured_in);
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Performance counter setup', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
    SELECT @errMessage  = ERROR_MESSAGE();
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH


SELECT @errMessage  = 'Working those perfom counters';
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT;
		SELECT @errMessage  = ERROR_MESSAGE();
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
BEGIN TRY

BEGIN
/* 2026-10-16: the dead 'Skip syscounters' block (a PowerShell get-counter through xp_cmdshell that never ran)
   was deleted. @syscountertable stays: the perf-counter INSERTs below still read it (always empty). */
	DECLARE @syscountertable TABLE
	(
		id [INT] IDENTITY(1,1)
		, [output] [VARCHAR](500)
	);
	BEGIN TRY
		/* SPN registration for the service account: setspn, a HOST COMMAND. It runs only when the caller
		   opted in (@HostProbesAllowed = 1) AND the account name passes the allow-list below; otherwise the
		   section says why it was skipped. Never "No SPNs registered" for a probe that did not run. */
		DECLARE @spnCheckCmd [NVARCHAR] (4000);
		DECLARE @spnSkipText [NVARCHAR] (200);
		SET @spnSkipText = NULL;
		IF @HostProbesAllowed = 0
		BEGIN
			IF @EnableXpCmdShell = 1
				SET @spnSkipText = N'skipped: xp_cmdshell could not be enabled';
			ELSE
				SET @spnSkipText = N'skipped: opt-in (@EnableXpCmdShell = 1 runs setspn)';
		END
		/* Allow-list for a value placed on a cmd.exe line: letters, digits, space, . \ _ $ @ and hyphen
		   (hyphen FIRST in the class so it is literal; BIN2 so ranges are ASCII only), no leading hyphen
		   (setspn would read it as a switch). % stays out: cmd.exe expands it even inside double quotes. */
		ELSE IF NOT (@DBEngineLogin IS NOT NULL
			AND LEN(@DBEngineLogin) > 0
			AND LEFT(@DBEngineLogin, 1) <> '-'
			AND @DBEngineLogin COLLATE Latin1_General_BIN2 NOT LIKE '%[^-A-Za-z0-9 .\_$@]%')
		BEGIN
			SET @spnSkipText = N'skipped: service account name has characters unsafe for a shell command';
		END
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT;
			RAISERROR (N'Checking SPNs',0,1) WITH NOWAIT;
		END
		IF @HostProbesAllowed = 1
		BEGIN
			IF @spnSkipText IS NULL
			BEGIN
				SET @spnCheckCmd = N'setspn -L "' + CONVERT([NVARCHAR] (100), @DBEngineLogin) + N'"';
				INSERT #output_sqldba_org_sp_triage_spnCheck ([output])
				EXEC xp_cmdshell @spnCheckCmd;
			END
		END

		IF @spnSkipText IS NOT NULL
		BEGIN
			INSERT #output_sqldba_org_sp_triage
			(
				SectionID
				, Section
				, Summary
			)
			SELECT
			0
			, N'SPNs for server'
			, @spnSkipText;
		END
		ELSE IF EXISTS
		(
			SELECT 1
			FROM #output_sqldba_org_sp_triage_spnCheck
			WHERE  output like 'Registered%' OR output like 'MS%/%'
		)
		BEGIN
			INSERT #output_sqldba_org_sp_triage
			(
				SectionID
				, Section
				, Summary
			)
			SELECT
			0
			, 'SPNs for server'
			, output
			FROM #output_sqldba_org_sp_triage_spnCheck
			WHERE  output like 'Registered%'
			OR output like 'MS%/%';
		END
		ELSE
		BEGIN
			/* setspn ran but printed no "Registered ..." header: it failed (it prints the header even for an
			   account with no SPNs). Say what it said, never "No SPNs registered": its output lines in order,
			   joined with ' | ' (on a workgroup box the first line is "FindDomainForAccount: ..." and the line
			   that names the account, "Could not find account ...", comes after it). */
			INSERT #output_sqldba_org_sp_triage
			(
				SectionID
				, Section
				, Summary
			)
			SELECT
			0
			, N'SPNs for server'
			, N'could not read SPNs: ' + LEFT(ISNULL(STUFF(
				(
					SELECT N' | ' + CONVERT([NVARCHAR] (1024), [output])
					FROM #output_sqldba_org_sp_triage_spnCheck
					WHERE [output] IS NOT NULL
					ORDER BY Seq ASC
					FOR XML PATH(''), TYPE
				).value('.', 'NVARCHAR(MAX)'), 1, 3, N''), N'(no output)'), 3900);
		END

		INSERT #output_sqldba_org_sp_triage
		(
			SectionID
			, Section
			, Summary
		)
		SELECT
		0
		, 'SPNs for server'
		,  [Text]
		FROM @xp_errorlog
		WHERE [Text]
		LIKE '%(SPN)%';


	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'SPNs for server', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT;
			RAISERROR (N'Error checking SPNs',0,1) WITH NOWAIT;
		END
	END CATCH

	DECLARE @sqlnamedinstance sysname;
	DECLARE @networkname sysname;
	if (select CHARINDEX('\',@@SERVERNAME)) = 0
	BEGIN
		INSERT @PerformanceCounter 
		(
			CounterName
			, CounterValue
			, DateSampled
		)
		SELECT  REPLACE(REPLACE(REPLACE(ct.[output],'\\'+@@SERVERNAME+'\',''),' :',''),'sqlserver:','')[CounterName]
		, CONVERT([VARCHAR](20),ct2.[output]) [CounterValue]
		, GETDATE() [DateSampled]
		FROM @syscountertable ct
		LEFT OUTER JOIN 
		(
			SELECT id - 1 [id], [output]
			FROM @syscountertable
			WHERE PATINDEX('%[0-9]%', LEFT([output],1)) > 0  
		) ct2 ON ct.id = ct2.id
		WHERE  ct.[output] LIKE '\\%'
		AND  ct.[output] IS NOT NULL
		ORDER BY [CounterName] ASC;
	END

	ELSE

	BEGIN
		SELECT @networkname=RTRIM(left(@@SERVERNAME, CHARINDEX('\', @@SERVERNAME) - 1));
		SELECT @sqlnamedinstance=RIGHT(@@SERVERNAME,CHARINDEX('\',REVERSE(@@SERVERNAME))-1);

		INSERT @PerformanceCounter 
		(
			CounterName
			, CounterValue
			, DateSampled
		)
		SELECT  REPLACE(REPLACE(REPLACE(ct.[output],'\\'+@networkname+'\',''),' :',''),'mssql$'+@sqlnamedinstance+':','')[CounterName] , CONVERT([VARCHAR](20),ct2.[output]) [CounterValue], GETDATE() [DateSampled]
		FROM @syscountertable ct
		LEFT OUTER JOIN 
		(
			SELECT id - 1 [id], [output]
			FROM @syscountertable
			WHERE PATINDEX('%[0-9]%', LEFT([output],1)) > 0  
		) ct2 ON ct.id = ct2.id
		WHERE  ct.[output] LIKE '\\%'
		ORDER BY [CounterName] ASC;
	END
END


END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Performance counters', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  SELECT @errMessage  = ERROR_MESSAGE();
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH

/*Generate DTU calculations*/

SELECT @errMessage  = 'Checking Azure calculations';
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
BEGIN TRY


	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 
	0
	,'For Azure Calculations'
	,'------'
	,'------';

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
	SELECT 
	0
	, 'Number of CPUs exposed to OS' [Measure]
	, CONVERT([VARCHAR](3),@CPUcount) [Value] ;

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
	SELECT 
	0
	, 'Databases(_total)\log bytes flushed/sec (MB)'
	, AVG(CONVERT(MONEY,CounterValue))/1024/1024
	FROM @PerformanceCounter T1
	WHERE T1.CounterName LIKE '%databases(_total)\log bytes flushed/sec';

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
	SELECT 
	0
	, 'Average IOPS'
	, SUM(CONVERT(MONEY,CounterValue))/@loops
	FROM @PerformanceCounter T1
	WHERE T1.CounterName LIKE '%LogicalDisk(_Total)\Disk Reads/sec'
	OR  T1.CounterName LIKE '%LogicalDisk(_Total)\Disk Writes/sec';

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
	SELECT 
	0
	, 'Disk Read IOPS'
	, AVG(CONVERT(MONEY,CounterValue))
	FROM @PerformanceCounter T1
	WHERE T1.CounterName LIKE '%LogicalDisk(_Total)\Disk Reads/sec';

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
	SELECT 
	0
	, 'Disk Write IOPS'
	, AVG(CONVERT(MONEY,CounterValue))
	FROM @PerformanceCounter T1
	WHERE T1.CounterName LIKE '%LogicalDisk(_Total)\Disk Writes/sec';



	DECLARE @CPURingBuffer TABLE
		(
			SQLProcessUtilization SMALLINT
			, SystemIdle SMALLINT
			, [Event_Time] DATETIME
		);

		INSERT @CPURingBuffer
		SELECT SQLProcessUtilization
			, SystemIdle
			, DATEADD(ms,-1 *(@ts - [timestamp]), GETDATE())AS [Event_Time]
			FROM 
			(
				SELECT 
				record.value('(./Record/@id)[1]','int') AS record_id
				, record.value('(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]','int') AS [SystemIdle]
				, record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]','int') AS [SQLProcessUtilization]
				, [timestamp]
				FROM 
				(
					SELECT
					[timestamp]
					, CONVERT(XML, record) AS [record] 
					FROM [sys].dm_os_ring_buffers 
					WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR'
					AND record LIKE'%%'
				)AS x
			) as y;
		

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
	SELECT 
	0 
	,'SQL Avg Usage %. From: ' + ISNULL(CONVERT([VARCHAR], MIN([Event_Time]),120),'') + ' to: ' +ISNULL( CONVERT([VARCHAR], MAX([Event_Time]),120),'')  
	, (
		SELECT  CONVERT([VARCHAR](10),AVG(avg_DTU_percent)) 
		FROM @DTUtable
	) 
	FROM @CPURingBuffer T1;

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
	SELECT 
	0 
	,'SQL Avg NOT 0 %. From: ' + ISNULL(CONVERT([VARCHAR], MIN([Event_Time]),120),'') + ' to: ' + ISNULL(CONVERT([VARCHAR], MAX([Event_Time]),120) ,'') 
	, AVG(SQLProcessUtilization) 
	FROM @CPURingBuffer T1 WHERE SQLProcessUtilization <> 0;

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
	)
	SELECT 
	0 
	, 'SQL MAX Usage %. From: ' + CONVERT([VARCHAR], MIN([Event_Time]),120) + ' to: ' + CONVERT([VARCHAR], MAX([Event_Time]),120) 
	, MAX(SQLProcessUtilization) 
	FROM @CPURingBuffer T1;

	INSERT #output_sqldba_org_sp_triage (
		SectionID
		, Section
		, Summary
	)
	SELECT 
	0 
	, 'OS idle CPU %. From: ' + CONVERT([VARCHAR], MIN([Event_Time]),120) + ' to: ' + CONVERT([VARCHAR], MAX([Event_Time]),120) 
	, AVG(SystemIdle) 
	FROM @CPURingBuffer T1 ;


END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Azure calculations', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  SELECT @errMessage  = ERROR_MESSAGE();
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH

IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Finished rough IOPS calculation',0,1) WITH NOWAIT;
	END


			/*----------------------------------------
			--Check for any pages marked suspect for corruption
			----------------------------------------*/
SELECT @errMessage  = 'Checking for suspiciouns pages';
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
BEGIN TRY

	DECLARE @syspectpagescount FLOAT;
	DECLARE @suspectpagesTable TABLE
	( 
		database_id TINYINT
		, file_id [VARCHAR](20)
		, page_id [VARCHAR](20)
		, event_type [VARCHAR](20)
		, error_count [VARCHAR](20)
		, last_update_date DATETIME
	);
	SET @dynamicSQL = 'SELECT 
		p.database_id
		, p.file_id
		, p.page_id
		, p.event_type
		, p.error_count
		, p.last_update_date
		FROM msdb.dbo.suspect_pages p';
	INSERT @suspectpagesTable
	EXEC sp_executesql @dynamicSQL;


	SELECT 
	@syspectpagescount = COUNT(*) 
	FROM @suspectpagesTable;
	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, Details
	) 
	SELECT 
	0
	, 'SUSPECT PAGES'
	, '------'
	, '------';

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, Severity
		, Details
	)
	SELECT 
	0,
	'DB: ' + db_name(database_id)
	+ '; FileID: ' + CONVERT([VARCHAR](20),file_id)
	+ '; PageID: ' + CONVERT([VARCHAR](20), page_id)
	, 'Event Type: ' + CONVERT([VARCHAR](20),event_type)
	+ '; Count: ' + CONVERT([VARCHAR](20),error_count)
	, CASE WHEN @syspectpagescount > 0 THEN @Result_YourServerIsDead WHEN @syspectpagescount = 0 THEN @Result_Good END
	, 'Last Update: ' + CONVERT([VARCHAR](20),last_update_date,120)
	
	FROM @suspectpagesTable
	OPTION (RECOMPILE);

IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Included Suspect Pages, if any',0,1) WITH NOWAIT;
	END

END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Suspect pages', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
  SELECT @errMessage  = ERROR_MESSAGE();
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
	END
END CATCH
			/*----------------------------------------
			--Before anything else, look for things that might point to breaking behaviour. Look for out of support SQL bits floating around
			--WORKAROUND - create all indexes using the deafult SET settings of the applications connecting into the server
			--DANGER WILL ROBINSON

			----------------------------------------*/
	
	BEGIN

		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary
			, Details
		) 
		SELECT 
		1,
		'!!! WARNING - CHECK SET - MAKES INDEXES BREAK THINGS!!!'
		,'------'
		,'------';

		BEGIN TRY
		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary
			, Severity
			, Details
		)
		SELECT 
		DISTINCT 
		1
		, 'User Connections' [Section]
		, '{"Version":"'+ISNULL(
		CASE 
			WHEN T.client_version < 3 THEN 'SQL 6'
			WHEN T.client_version = 3 THEN 'SQL 7'
			WHEN T.client_version = 4 THEN 'SQL 2000'
			WHEN T.client_version = 5 THEN 'SQL 2005'
			WHEN T.client_version = 6 THEN 'SQL 2008'
			WHEN T.client_version = 7 THEN 'SQL 2012+'
			ELSE 'SQL 2014+'
		END ,'')
		+ '", "Database":"' +   ISNULL(DB_NAME(  R.database_id),'')
		+ '", "App":"' + REPLACE(ISNULL(T.program_name,''),':','_')
		+ '", "Driver":"' + ISNULL(
		CASE SUBSTRING(CAST(C.protocol_version AS BINARY(4)), 1,1)
			WHEN 0x04 THEN 'Pre-version SQL Server 7.0 - DBLibrary/ ISQL'
			WHEN 0x70 THEN 'SQL Server 7.0'
			WHEN 0x71 THEN 'SQL Server 2000'
			WHEN 0x72 THEN 'SQL Server 2005'
			WHEN 0x73 THEN 'SQL Server 2008'
			WHEN 0x74 THEN 'SQL Server 2012+'
			ELSE 'Unknown driver'
		END ,'')
		+ '", "Interface":"'+ ISNULL(T.client_interface_name,'')
		+ '", "User":"' + ISNULL(T.nt_user_name,'')
		+ '", "Host":"' + ISNULL(T.host_name,'')
		+ '", "ClientVersion":"' + ISNULL(CONVERT([VARCHAR](4),T.client_version),'') 
		+ '"}'[Summary]
		, '' 
		+ CASE 
			WHEN ISNULL(CASE WHEN T.quoted_identifier = 0 THEN 1 ELSE 0 
		END
		+ CASE 
			WHEN T.ansi_nulls = 0 THEN 1 ELSE 0 
		  END
		+ CASE 
			WHEN T.ansi_padding = 0 THEN 1 ELSE 0 
		  END
		+ CASE 
			WHEN T.ansi_warnings = 0 THEN 1 ELSE 0 
		  END
		+ CASE 
			WHEN T.arithabort = 0 THEN 1 ELSE 0 
		  END
		+ CASE 
			WHEN T.concat_null_yields_null = 0 THEN 1 ELSE 0 
		  END
		,0) > 0 THEN @Result_Warning ELSE @Result_Good END

		, '' + ISNULL(
		CASE 
			WHEN T.quoted_identifier = 0 THEN ';quoted_identifier = OFF' ELSE '' END
		+ '' +  
		CASE
			WHEN T.ansi_nulls = 0 THEN ';ansi_nulls = OFF' ELSE '' 
		END
		+ '' +  
		CASE
			WHEN T.ansi_padding = 0 THEN ';ansi_padding = OFF' ELSE '' 
		END
		+ '' +  
		CASE
			WHEN T.ansi_warnings = 0 THEN ';ansi_warnings = OFF' ELSE '' 
		END
		+ '' +  
		CASE
			WHEN T.arithabort = 0 THEN ';arithabort = OFF' ELSE '' 
		END
		+ ''+   
		CASE
			WHEN T.concat_null_yields_null = 0 THEN ';concat_null_yields_null = OFF' ELSE '' 
		END,'')
		FROM [sys].dm_exec_sessions T
		LEFT OUTER JOIN [sys].dm_exec_connections C ON C.session_id = T.session_id
		LEFT OUTER JOIN [sys].dm_exec_requests R ON R.session_id = T.session_id
		WHERE T.client_version > 0
		--AND T.program_name NOT LIKE 'SQLAgent - %' 
		--OR T.client_version < 6 
		ORDER BY Section, [Summary];
		END TRY
		BEGIN CATCH
			INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'User connections', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
			SELECT @errMessage  = ERROR_MESSAGE();
			IF @Debug = 1 RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
		END CATCH

	END
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done checking for possible breaking SQL 2000 things',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Before anything else, look for things that might point to breaking behaviour. Like database with bad default settings
			----------------------------------------*/

	IF EXISTS
		(
			SELECT 1
			FROM @sysdatabasesTable
			WHERE is_ansi_nulls_on = 0
			OR is_ansi_padding_on= 0
			OR is_ansi_warnings_on= 0
			OR is_arithabort_on= 0
			OR is_concat_null_yields_null_on= 0
			OR is_numeric_roundabort_on= 0
			OR is_quoted_identifier_on= 1
		)
	BEGIN

		INSERT INTO #output_sqldba_org_sp_triage_whatsets
		(
			DBname
			, [compatibility_level]
			, [SETs]
		)
		
		SELECT 
		QUOTENAME(name)
		, [compatibility_level]
		, '' +  
		CASE 
			WHEN is_quoted_identifier_on = 0 THEN '; SET quoted_identifier OFF' ELSE '' 
		END
		+ '' +  
		CASE 
			WHEN is_ansi_nulls_on = 0 THEN '; SET ansi_nulls OFF' ELSE '' 
		END
		+ '' +  
		CASE 
			WHEN is_ansi_padding_on = 0 THEN '; SET ansi_padding OFF' ELSE '' 
		END
		+ '' +  
		CASE 
			WHEN is_ansi_warnings_on = 0 THEN '; SET ansi_warnings OFF' ELSE '' 
		END
		+ '' +  
		CASE 
			WHEN is_arithabort_on = 0 THEN '; SET arithabort OFF' ELSE '' 
		END
		+ '' +  
		CASE 
			WHEN is_concat_null_yields_null_on = 0 THEN '; SET concat_null_yields_null OFF' ELSE '' 
		END
		+ '' +  
		CASE 
			WHEN is_numeric_roundabort_on = 1 THEN '; SET is_numeric_roundabort_on ON' ELSE '' 
		END
		FROM @sysdatabasesTable

	END

	IF EXISTS(SELECT * FROM #output_sqldba_org_sp_triage_whatsets)
	BEGIN
		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary
			, Details
		) 
		SELECT 
		1
		, '!!! WARNING - POTENTIALLY BREAKING DB SETTINGS!!!'
		, '------'
		, '------';

		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary
		)
		SELECT 
		1
		, DBname + ' [' + CONVERT([VARCHAR](10), [compatibility_level])  + ']'
		, [SETs]
		FROM #output_sqldba_org_sp_triage_whatsets
		ORDER BY DBname DESC;
	END

IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done checking compatability levels and sets for database things',0,1) WITH NOWAIT;
	END
			/*----------------------------------------
			--Benchmark, not for anything ELSE besides getting a number
			----------------------------------------*/
IF 'I think this is a great idea, just needs to find a use' = 'Yup'
BEGIN
	SET @StartTest = GETDATE();
	DECLARE @testloop [INT] 
	SET @testloop = 0
	WHILE @secondsperoperator IS NULL AND @testloop < 10
	BEGIN
	
		WITH  E00(N)	AS (SELECT 1 UNION ALL SELECT 1)
			, E02(N)	AS (SELECT 1 FROM E00 a, E00 b)
			, E04(N)	AS (SELECT 1 FROM E02 a, E02 b)
			, E08(N)	AS (SELECT 1 FROM E04 a, E04 b)
			, E16(N)	AS (SELECT 1 FROM E08 a, E08 b)
			, cteTally(N) AS (SELECT ROW_NUMBER() OVER (ORDER BY N) FROM E16)
		SELECT 
			@ThisistoStandardisemyOperatorCostMate = count(N) 
		FROM cteTally OPTION (RECOMPILE);
		
		WITH  E00(N)	AS (SELECT 1 UNION ALL SELECT 1)
			, E02(N)	AS (SELECT 1 FROM E00 a, E00 b)
			, E04(N)	AS (SELECT 1 FROM E02 a, E02 b)
			, E08(N)	AS (SELECT 1 FROM E04 a, E04 b)
			, E16(N)	AS (SELECT 1 FROM E08 a, E08 b)
			, cteTally(N) AS (SELECT ROW_NUMBER() OVER (ORDER BY N) FROM E16)
		SELECT 
			@ThisistoStandardisemyOperatorCostMate = count(N) 
		FROM cteTally OPTION (RECOMPILE);
		SET @EndTest = GETDATE();
		BEGIN TRY
		SELECT   
			@secondsperoperator = AVG(qs.total_worker_time/qs.execution_count/1000)/0.7248/1000  
		FROM  #dadatafor_exec_query_stats qs
		CROSS APPLY [sys].dm_exec_sql_text(qs.sql_handle) qt
		WHERE qs.total_logical_reads = 0 
		AND qs.plan_handle IN (SELECT rp.plan_handle FROM #sp_triage_readable_plan AS rp)
		AND qs.last_execution_time BETWEEN DATEADD(MINUTE,-2,@StartTest) AND GETDATE()
		AND PATINDEX('%ThisistoStandardisemyOperatorCostMate%',CAST(qt.TEXT AS [NVARCHAR] (MAX))) > 0
		END TRY
		BEGIN CATCH
			INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) SELECT LEFT(N'Operator cost calibration', 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000) WHERE NOT EXISTS (SELECT 1 FROM #sp_triage_skipped AS k WHERE k.Step = N'Operator cost calibration');
		END CATCH
			
		WAITFOR DELAY '00:00:00.5'
		SET @testloop = @testloop + 1
		--PRINT ISNULL(CONVERT([VARCHAR](50),@secondsperoperator),'null...')
			
	END
END
	IF @secondsperoperator IS NULL
		SET @secondsperoperator = 0.00413907

	--PRINT N'Your cost (in seconds) per operator roughly equates to around '+ CONVERT([VARCHAR](20),ISNULL(@secondsperoperator,0)) + ' seconds' ;
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Benchmarking done',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Build database table to use throughout this script
			----------------------------------------*/

DECLARE @Databases TABLE
	(
		id [INT] IDENTITY(1,1)
		, database_id INT
		, databasename [NVARCHAR] (250)
		, [compatibility_level] [BIGINT]
		, user_access [BIGINT]
		, user_access_desc [NVARCHAR] (50)
		, [state] [BIGINT]
		, state_desc  [NVARCHAR] (50)
		, recovery_model [BIGINT]
		, recovery_model_desc  [NVARCHAR] (50)
		, create_date DATETIME
		, AGReplicaRole INT
		, [BackupPref] [NVARCHAR] (250)
		, [CurrentLocation] [NVARCHAR] (250)
		, AGName [NVARCHAR] (250)
		, [ReadSecondary] [NVARCHAR] (250)
		, [TotalIO] BIGINT
		, [TotalIORank] INT
		, [SkipForDBLoop] BIT

	);
	SET @dynamicSQL = 'SELECT 
	db.database_id
	, db.name
	, db.compatibility_level
	, db.user_access
	, db.user_access_desc
	, db.state
	, db.state_desc
	, db.recovery_model
	, db.recovery_model_desc
	, db.create_date
	, 1
	, NULL
	, NULL
	, NULL
	, NULL
	,DS.[TotalIO]
	, DS.[Rank]
	,NULL
	FROM 
	[sys].databases db
	LEFT OUTER JOIN (
	SELECT Name AS [DatabaseName]
		, d.database_id
		, SUM(num_of_reads) AS[Number of Reads]
		, SUM(num_of_writes) AS[Number of Writes]
		, SUM(num_of_writes) +  SUM(num_of_reads) [TotalIO]
		, RANK() OVER (ORDER BY SUM(num_of_writes) +  SUM(num_of_reads) DESC) [Rank]
		FROM [sys].dm_io_virtual_file_stats(NULL,NULL) I
		INNER JOIN [sys].databases d ON I.database_id = d.database_id
		GROUP BY Name, d.database_id
	) DS on DS.database_id = db.database_id



	WHERE 1 = 1 ';
	IF @SQLVersion >= 11 
	BEGIN 
		SET @dynamicSQL = @dynamicSQL + ' AND replica_id IS NULL /*Don''t touch anything AG related*/';
	END
	
	SET @dynamicSQL = @dynamicSQL + ' AND 1=1/*db.database_id > 4*/ AND db.user_access = 0 AND db.State = 0 AND db.database_id IN (SELECT r.database_id FROM #sp_triage_readable_db AS r) ';
	
	BEGIN TRY

	IF @SQLVersion >= 11 AND @IsSQLAzure = 0 
		BEGIN

			IF EXISTS(SELECT OBJECT_ID('master.[sys].availability_groups', 'V')) /*You have active AGs*/

			SET @dynamicSQL = @dynamicSQL + '
			UNION ALL
			SELECT 
			db.database_id
			, db.name
			, db.compatibility_level
			, db.user_access
			, db.user_access_desc
			, db.state
			, db.state_desc
			, db.recovery_model
			, db.recovery_model_desc
			, db.create_date
			, LocalReplicaRole
			, [BackupPref]
			, [CurrentLocation]
			, AGName
			, [ReadSecondary]
			, DS.TotalIO
			, DS.[Rank]
			,NULL
	FROM 
	[sys].databases db
	LEFT OUTER JOIN (
	SELECT Name AS [DatabaseName]
		, d.database_id
		, SUM(num_of_reads) AS[Number of Reads]
		, SUM(num_of_writes) AS[Number of Writes]
		, SUM(num_of_writes) +  SUM(num_of_reads) [TotalIO]
		, RANK() OVER (ORDER BY SUM(num_of_writes) +  SUM(num_of_reads) DESC) [Rank]
		FROM [sys].dm_io_virtual_file_stats(NULL,NULL) I
		INNER JOIN [sys].databases d ON I.database_id = d.database_id
		GROUP BY Name, d.database_id
	) DS on DS.database_id = db.database_id
			 
			LEFT OUTER JOIN(
			
			SELECT top 100 percent
			AG.name AS [AvailabilityGroupName]
			, ISNULL(agstates.primary_replica, NULL) AS [PrimaryReplicaServerName]
			, ISNULL(arstates.role, 3) AS [LocalReplicaRole]
			, dbcs.database_name AS [DatabaseName]
			, ISNULL(dbrs.synchronization_state, 0) AS [SynchronizationState]
			, ISNULL(dbrs.is_suspended, 0) AS [IsSuspended]
			, ISNULL(dbcs.is_database_joined, 0) AS [IsJoined]
			, AG.automated_backup_preference_desc [BackupPref]
			, AR.availability_mode_desc
			, agstates.primary_replica [CurrentLocation]
			, AG.name AGName
			, AR.secondary_role_allow_connections_desc [ReadSecondary]
			FROM '+ 
			CASE 
				WHEN @IsSQLAzure = 1 THEN '' ELSE 'master.' 
			END + '[sys].availability_groups AS AG
			LEFT OUTER JOIN '+ 
			CASE 
				WHEN @IsSQLAzure = 1 THEN '' ELSE 'master.' 
			END + '[sys].dm_hadr_availability_group_states as agstates
			ON AG.group_id = agstates.group_id
			INNER JOIN '+ 
			CASE 
				WHEN @IsSQLAzure = 1 THEN '' ELSE 'master.' 
			END + '[sys].availability_replicas AS AR
			ON AG.group_id = AR.group_id
			INNER JOIN '+ 
			CASE 
				WHEN @IsSQLAzure = 1 THEN '' ELSE 'master.' 
			END + '[sys].dm_hadr_availability_replica_states AS arstates
			ON AR.replica_id = arstates.replica_id AND arstates.is_local = 1
			INNER JOIN '+ 
			CASE 
				WHEN @IsSQLAzure = 1 THEN '' ELSE 'master.' 
			END + '[sys].dm_hadr_database_replica_cluster_states AS dbcs
			ON arstates.replica_id = dbcs.replica_id
			LEFT OUTER JOIN '+ 
			CASE 
				WHEN @IsSQLAzure = 1 THEN '' ELSE 'master.' 
			END + '[sys].dm_hadr_database_replica_states AS dbrs
			ON dbcs.replica_id = dbrs.replica_id AND dbcs.group_database_id = dbrs.group_database_id
			WHERE dbcs.is_database_joined = 1 /*AND agstates.primary_replica = ''' + @ThisServer + '''*/
			ORDER BY AG.name ASC, dbcs.database_name
			
			) t1 on t1.DatabaseName = db.name 
			WHERE /*db.database_id > 4 AND*/ db.user_access = 0 AND db.State = 0 AND db.database_id IN (SELECT r.database_id FROM #sp_triage_readable_db AS r)
			AND t1.LocalReplicaRole IS NOT NULL
			';
		END
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Availability Group database list', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
			RAISERROR (N'Trouble with Availability Group database list',0,1) WITH NOWAIT;
		END
	END CATCH

	SET @dynamicSQL = @dynamicSQL + ' OPTION (RECOMPILE);';
	BEGIN TRY
	INSERT INTO @Databases 
	EXEC sp_executesql @dynamicSQL ;
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Database list', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE();
		IF @Debug = 1 RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	END CATCH
	SET @Databasei_Max = (
		SELECT MAX(id) 
		FROM @Databases );

			/*----------------------------------------
			--Get uptime and cache age
			----------------------------------------*/

	SET @oldestcachequery = (SELECT MIN(creation_time) 
	FROM  #dadatafor_exec_query_stats WITH (NOLOCK)); /* NULL when the capture is empty: see the fallback after the = 0 guard (R3) */

	SET @lastservericerestart = (SELECT create_date 
	FROM @sysdatabasesTable WHERE name = 'tempdb');

	SET @serverinstalldate = (SELECT create_date 
	FROM @sysdatabasesTable WHERE name = 'master');

	IF @lastservericerestart IS NULL
	BEGIN
		DECLARE @lastservericerestartTable TABLE 
		(
			sqlserver_start_time DATETIME
		)
		SET @dynamicSQL = '
		SELECT sqlserver_start_time 
		FROM [sys].[dm_os_sys_info] OPTION (RECOMPILE)';

		BEGIN TRY
		INSERT @lastservericerestartTable
		EXEC sp_executesql @dynamicSQL ;
		END TRY
		BEGIN CATCH
			INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Server start time', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
			SELECT @errMessage  = ERROR_MESSAGE();
			IF @Debug = 1 RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
		END CATCH

		SELECT @lastservericerestart = sqlserver_start_time 
		FROM @lastservericerestartTable;
	END

	SET @minutesSinceRestart = (
			SELECT DATEDIFF(MINUTE,@lastservericerestart,GETDATE())
		);
	
	SELECT @DaysUptime = CAST(DATEDIFF(hh,@lastservericerestart,GETDATE())/24. AS NUMERIC (23,2)) OPTION (RECOMPILE);
	SELECT @DaysOldestCachedQuery = CAST(DATEDIFF(hh,@oldestcachequery,GETDATE())/24. AS NUMERIC (23,2)) OPTION (RECOMPILE);

	IF @DaysUptime = 0 
		SET @DaysUptime = .1;
	IF @DaysOldestCachedQuery = 0 
		SET @DaysOldestCachedQuery = .1;
	/* R3 (lane bundle-sp-triage-5): the divisor is never older than the server's uptime. An empty plan-cache capture used to
	   read as a 1900 date (46,329 days), which emptied section 18 and shrank every per-day figure. The missing-index DMV
	   resets at restart, so uptime is its true age. */
	IF @oldestcachequery IS NULL SET @DaysOldestCachedQuery = ISNULL(@DaysUptime, .1);

	SET @CachevsUpdate = @DaysOldestCachedQuery*100/@DaysUptime;
	IF @CachevsUpdate < 1
		SET @CachevsUpdate = 1
	IF @CachevsUpdate > 100
		SET @CachevsUpdate = 100  /*upper clamp: REPLICATE('.',100-@CachevsUpdate) with a negative length returns NULL and NULLs the whole bar string*/
	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 
	2
	, 'CACHE - Cache Age As portion of Overall Uptime'
	, '------'
	, '------';

	IF @QueryStatsCaptured = 1 AND @oldestcachequery IS NOT NULL /* see the @QueryStatsCaptured declaration: no capture, or an empty one, read no cache age */
	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, HoursToResolveWithTesting
	)
	SELECT 
	2
	, '['+REPLICATE('|', @CachevsUpdate) + REPLICATE('.',100-@CachevsUpdate ) +']'
	, 'Uptime:'
		+ ISNULL(CONVERT([VARCHAR](20),@DaysUptime),'')
		+ '; Oldest Cache:'
		+ ISNULL(CONVERT([VARCHAR](20),@DaysOldestCachedQuery ),'')
		+ '; Cache Timestamp:'
		+ ISNULL(CONVERT([VARCHAR](20),@oldestcachequery,120),'')
	, CASE 
		WHEN @CachevsUpdate > 50 AND @DaysUptime > 1 THEN 0.5 ELSE 2 
	END ;


IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Server uptime and cache age established',0,1) WITH NOWAIT;
	END

	   /*----------------------------------------
			--Internals and Memory usage
		----------------------------------------*/

	SELECT @VMType = RIGHT(@@version,CHARINDEX('(',REVERSE(@@version)));

	BEGIN TRY
	IF @SQLVersion > 10
	BEGIN
		EXEC sp_executesql N'set @_MaxRamServer= (select physical_memory_kb/1024 from [sys].dm_os_sys_info);'
		, N'@_MaxRamServer [INT] OUTPUT'
		, @_MaxRamServer = @MaxRamServer OUTPUT;
		
		IF @IsSQLAzure = 0
		BEGIN
			EXEC sp_executesql N'SELECT @_UsedMemory =  CONVERT(MONEY,physical_memory_in_use_kb)/1024 /1000 FROM [sys].dm_os_process_memory WITH (NOLOCK) OPTION (RECOMPILE)'
			, N'@_UsedMemory MONEY  OUTPUT'
			, @_UsedMemory = @UsedMemory OUTPUT;

			EXEC sp_executesql N'SELECT @_totalMemoryGB = CONVERT(MONEY,total_physical_memory_kb)/1024/1000 FROM [sys].dm_os_sys_memory WITH (NOLOCK) OPTION (RECOMPILE)'
			, N'@_totalMemoryGB MONEY  OUTPUT'
			, @_totalMemoryGB = @totalMemoryGB OUTPUT;

			EXEC sp_executesql N'SELECT @_AvailableMemoryGB =  CONVERT(MONEY,available_physical_memory_kb)/1024/1000 FROM [sys].dm_os_sys_memory WITH (NOLOCK) OPTION (RECOMPILE);'
			, N'@_AvailableMemoryGB MONEY  OUTPUT'
			, @_AvailableMemoryGB = @AvailableMemoryGB OUTPUT;

			EXEC sp_executesql N'SELECT @_MemoryStateDesc =   system_memory_state_desc from  [sys].dm_os_sys_memory;'
			, N'@_MemoryStateDesc [NVARCHAR] (50) OUTPUT'
			, @_MemoryStateDesc = @MemoryStateDesc OUTPUT;
		END
		ELSE
		BEGIN
			DECLARE @_UsedMemory MONEY;
			DECLARE @_totalMemoryGB MONEY;
			DECLARE @_AvailableMemoryGB MONEY;
			DECLARE @_MemoryStateDesc [NVARCHAR] (50);
			SET @_MemoryStateDesc ='';
			DECLARE @AzureMemoryTable TABLE 
			(
				totalMemoryGB MONEY
				, UsedMemoryGB MONEY
				, AvailableMemoryGB MONEY
			);

			SET @dynamicSQL = '
			SELECT  
			 totalMemoryGB = visible_target_kb/1024/1024
			, UsedMemoryGB = committed_kb/1024/1024
			, AvailableMemoryGB = (visible_target_kb - committed_kb) /1024/1024
			FROM [sys].[dm_os_sys_info] 
			OPTION (RECOMPILE)';

			INSERT INTO @AzureMemoryTable
			EXEC sp_executesql @dynamicSQL;

			SELECT
				 @_totalMemoryGB = totalMemoryGB
				, @_UsedMemory =UsedMemoryGB
				, @_AvailableMemoryGB = AvailableMemoryGB
			FROM @AzureMemoryTable;

			SET @MaxRamServer = @_totalMemoryGB;
			SET @totalMemoryGB = @_totalMemoryGB;
			SET @UsedMemory = @_UsedMemory;
			SET @AvailableMemoryGB = @_AvailableMemoryGB;

		END
		--SELECT @UsedMemory = CONVERT(MONEY,physical_memory_in_use_kb)/1024 /1000 FROM [sys].dm_os_process_memory WITH (NOLOCK) OPTION (RECOMPILE)
		--SELECT @totalMemoryGB = CONVERT(MONEY,total_physical_memory_kb)/1024/1000 FROM [sys].dm_os_sys_memory WITH (NOLOCK) OPTION (RECOMPILE);
		--SELECT @AvailableMemoryGB =  CONVERT(MONEY,available_physical_memory_kb)/1024/1000 FROM [sys].dm_os_sys_memory WITH (NOLOCK) OPTION (RECOMPILE);
	END
	ELSE
	IF @SQLVersion in (10,9)
	BEGIN

		EXEC sp_executesql N'set @_MaxRamServer= (select physical_memory_in_bytes/1024/1000 from [sys].dm_os_sys_info) ;'
		, N'@_MaxRamServer [INT] OUTPUT'
		, @_MaxRamServer = @MaxRamServer OUTPUT;

		EXEC sp_executesql N'SELECT @_UsedMemory = CONVERT(MONEY,physical_memory_in_bytes)/1024/1024/1000 FROM [sys].dm_os_sys_info WITH (NOLOCK) OPTION (RECOMPILE)'
		, N'@_UsedMemory MONEY  OUTPUT'
		, @_UsedMemory = @UsedMemory OUTPUT;

		EXEC sp_executesql N'SELECT @_totalMemoryGB = CONVERT(MONEY,physical_memory_in_bytes)/1024/1024/1000 FROM [sys].dm_os_sys_info WITH (NOLOCK) OPTION (RECOMPILE)'
		, N'@_totalMemoryGB MONEY  OUTPUT'
		, @_totalMemoryGB = @totalMemoryGB OUTPUT;

		EXEC sp_executesql N'SELECT @_AvailableMemoryGB =  0;'
		, N'@_AvailableMemoryGB MONEY  OUTPUT'
		, @_AvailableMemoryGB = @AvailableMemoryGB OUTPUT;

		SET @MemoryStateDesc = '';
		
	END
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Memory figures', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE();
		IF @Debug = 1 RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	END CATCH

	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 
	3
	,'MEMORY - SQL Memory usage of total allocated'
	,'------'
	,'------';

	BEGIN TRY
	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details] 
	)

	SELECT 
	3
	, '['+REPLICATE('|', CONVERT(MONEY,CONVERT(FLOAT,@UsedMemory)/CONVERT(FLOAT,@totalMemoryGB)) * 100) + REPLICATE('.',100-(CONVERT(MONEY,CONVERT(FLOAT,@UsedMemory)/CONVERT(FLOAT,@totalMemoryGB)) * 100) ) +']' 
	, 'Sockets:' +  ISNULL(replace(replace(replace(replace(CONVERT([NVARCHAR],CONVERT([VARCHAR](20),(@CPUsocketcount ) )), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' '),'')
	+ '; Virtual CPUs:' +  ISNULL(replace(replace(replace(replace(CONVERT([NVARCHAR],CONVERT([VARCHAR](20),@CPUcount   )), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ') ,'')
	+ '; VM Type:' +  ISNULL(replace(replace(replace(replace(CONVERT([NVARCHAR],ISNULL(@VMType,'')), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ') ,'')
	+ '; CPU Affinity:'+  ISNULL(replace(replace(replace(replace(CONVERT([NVARCHAR],ISNULL('','')), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ') ,'')
	+ '; MemoryGB:' + ISNULL(CONVERT([VARCHAR](20), CONVERT(MONEY,CONVERT(FLOAT,@totalMemoryGB))),'')
	+ '; SQL Allocated:' +ISNULL(CONVERT([VARCHAR](20), CONVERT(MONEY,CONVERT(FLOAT,@UsedMemory))) ,'')
	+ '; Suggested MAX:' + ISNULL( CONVERT([VARCHAR](20), 
	CASE 
	 	WHEN @MaxRamServer < = 1024*2 THEN @MaxRamServer - 512  /*WHEN the RAM is Less than or equal to 2GB*/
	 	WHEN @MaxRamServer < = 1024*4 THEN @MaxRamServer - 1024 /*WHEN the RAM is Less than or equal to 4GB*/
	 	WHEN @MaxRamServer < = 1024*16 THEN @MaxRamServer - 1024 - Ceiling((@MaxRamServer-4096) / (4.0*1024))*1024 /*WHEN the RAM is Less than or equal to 16GB*/

		-- My machines memory calculation
		-- RAM= 16GB
		-- Case 3 as above:- 16384 RAM-> MaxMem= 16384-1024-[(16384-4096)/4096] *1024
		-- MaxMem= 12106

		WHEN @MaxRamServer > 1024*16 THEN @MaxRamServer - 4096 - Ceiling((@MaxRamServer-1024*16) / (8.0*1024))*1024 /*WHEN the RAM is Greater than or equal to 16GB*/
	END) ,'')
	+ '; Used by SQL:'+ ISNULL(CONVERT([VARCHAR](20), CONVERT(FLOAT,@UsedMemory)),'')
	+ '; Memory State:' + ISNULL((@MemoryStateDesc),'')  [Internals: Details] 
	, ('ServerName:'+ ISNULL(replace(replace(replace(replace(CONVERT([NVARCHAR],SERVERPROPERTY('ServerName')), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ') ,'')
		+ '; Version:'+ ISNULL(replace(replace(replace(replace(CONVERT([NVARCHAR],LEFT( @@version, PATINDEX('%-%',( @@version))-2) ), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ') ,'')
		+ '; VersionNr:'+ ISNULL(replace(replace(replace(replace(CONVERT([NVARCHAR],SERVERPROPERTY('ProductVersion')), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ') ,'')
		+ '; OS:'+  ISNULL(replace(replace(replace(replace(CONVERT([NVARCHAR],RIGHT( @@version, LEN(@@version) - PATINDEX('% on %',( @@version))-3) ), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ') ,'')
		+ '; Edition:'+ ISNULL(replace(replace(replace(replace(CONVERT([NVARCHAR],SERVERPROPERTY('Edition')), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ') ,'')
		+ '; HADR:'+ ISNULL(replace(replace(replace(replace(CONVERT([NVARCHAR],SERVERPROPERTY('IsHadrEnabled')), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ') ,'')
		+ '; SA:'+ ISNULL(replace(replace(replace(replace(CONVERT([NVARCHAR],SERVERPROPERTY('IsIntegratedSecurityOnly' )), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' '),'')
		+ '; Licenses:'+ ISNULL(replace(replace(replace(replace(CONVERT([NVARCHAR],SERVERPROPERTY('NumLicenses' )), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ') ,'')
		+ '; Level:'+ ISNULL(replace(replace(replace(replace(CONVERT([NVARCHAR],SERVERPROPERTY('ProductLevel')), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' '),''))  [More Details] 
		FROM [sys].[dm_os_sys_info] OPTION (RECOMPILE);
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'SQL memory usage', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE();
		IF @Debug = 1 RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	END CATCH


			/*----------------------------------------
			--Get some CPU history
			----------------------------------------*/

	

	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 
	4
	, 'CPU - Average CPU usage of SQL process as % of total CPU usage'
	, 'Speed; Avg CPU; CPU Idle; Other; From; To; Full Details'
	, '------';

	BEGIN TRY
	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, HoursToResolveWithTesting 
	)
	SELECT 4, '['+REPLICATE('|', AVG(CONVERT(MONEY,SQLProcessUtilization))) + REPLICATE('.',100-(AVG(CONVERT(MONEY,SQLProcessUtilization)) )) +']'
	, (
	ISNULL(@cpu_ghz, N'speed not read')
	+ ';'+   ISNULL(CONVERT([VARCHAR](20),AVG(SQLProcessUtilization)),'')
	+ '%;' + ISNULL(CONVERT([VARCHAR](20),AVG(SystemIdle)),'')
	+ '%; '+ ISNULL(CONVERT([VARCHAR](20), 100 - AVG(SQLProcessUtilization) - AVG(SystemIdle)),'')
	+ '%;'+  ISNULL(CONVERT([VARCHAR](20), MIN([Event_Time]),120),'')
	+ ';' +  ISNULL(CONVERT([VARCHAR](20), MAX([Event_Time]),120),'')
	+ ';' +  ISNULL(@cpu_name,'')
	) 
	, CASE 
		WHEN AVG(SQLProcessUtilization) > 50 THEN 2 
		ELSE 0 
	END 
	FROM 
	(
		SELECT SQLProcessUtilization
		, SystemIdle
		, DATEADD(ms,-1 *(@ts - [timestamp])
		, GETDATE())AS [Event_Time]
		FROM 
		(
			SELECT 
			record.value('(./Record/@id)[1]','int') AS record_id
			, record.value('(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]','int') AS [SystemIdle]
			, record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]','int') AS [SQLProcessUtilization]
			, [timestamp]
			FROM 
			(
				SELECT
				[timestamp]
				, CONVERT(xml, record) AS [record] 
				FROM [sys].dm_os_ring_buffers 
				WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR'
				AND record LIKE'%%'
			) AS x
		) AS y
	) T1
	HAVING AVG(T1.SQLProcessUtilization) >= (
		CASE 
			WHEN @ShowWarnings = 1 THEN 20 
			ELSE 0 
		END)
	OPTION (RECOMPILE);
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'CPU history', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE();
		IF @Debug = 1 RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	END CATCH

IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Checked CPU usage for the last 5 hours',0,1) WITH NOWAIT;
	END
	



			/*----------------------------------------
			--Error Log issues on the server
			----------------------------------------*/
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Reading Error Log.. this should take a couple of seconds',0,1) WITH NOWAIT;
	END

	DECLARE @LoginLog TABLE
	(
		LogDate DATETIME
		, ProcessInfo [NVARCHAR] (2000)
		, [Text] [NVARCHAR] (MAX)
	);
	IF  @ShowWarnings = 0 
	BEGIN

		DECLARE @ErrorLogFiles TABLE
		(
			 [Id] [INT] IDENTITY(1,1)
			, [Archive #] [INT]
			, [Date] [NVARCHAR] (259)
			, [Log File Size (Byte)] [BIGINT]
		);

		BEGIN TRY
		IF @IsSQLAzure = 0
		BEGIN
			INSERT INTO @ErrorLogFiles
			(
				[Archive #]
			   , [Date]
			   , [Log File Size (Byte)]
			)
			EXEC xp_enumerrorlogs;
		END
 
		 DECLARE  @SQLLogData TABLE
		(
			 LogDate  DATETIME
			, ProcessInfo [NVARCHAR] (1200)
			, LogText [NVARCHAR] (3999)
		);


		--Iterate through each log file and output to a table (separate results)
		/* 2026-10-24 (lane sp-triage-screen-control-chars). Invariant: every error-log read in this proc passes an
		   archive number from 0 to 99. sp_readerrorlog / xp_readerrorlog reject 100 and above with 'Invalid parameter
		   value'; inside INSERT ... EXEC that error is swallowed but still flags the batch, so SSMS and SqlClient end the
		   whole run with 'Msg 0, Level 11 ... A severe error occurred' after 'All done' (PROVED on SQL 2022 16.0.4305.5
		   with 101 archives). xp_enumerrorlogs returns archives in TEXT order (0, 1, 10, 100, 11, ...), so the old
		   'Id <= 5' read archives 0, 1, 10, 100 and 11 instead of the five newest. Now: the five newest, by number. */
		DECLARE @logCount INT;
		SELECT @logCount = COUNT(*)
		FROM @ErrorLogFiles;

		DECLARE @i [INT] ;
		SET @i = 0;
		DECLARE @tableName [NVARCHAR] (128);
		DECLARE @datecheck DATETIME;
		DECLARE curReadSQLErrorLogs CURSOR LOCAL FAST_FORWARD READ_ONLY FOR
			   SELECT TOP (5) [Archive #]
			   FROM @ErrorLogFiles
			   WHERE [Archive #] BETWEEN 0 AND 99
			   ORDER BY [Archive #]
		OPEN curReadSQLErrorLogs
		FETCH NEXT FROM curReadSQLErrorLogs INTO @i
		WHILE @@FETCH_STATUS = 0
		BEGIN
			--SELECT @datecheck = Date FROM @ErrorLogFiles WHERE [Archive #] = @i
			/*IF @datecheck > DATEADD(MONTH,-1,GETDATE())*/
			/*Only use log files that were created in the last 3 months*/
			/* @ErrorLogMaxMB (lane bundle-sp-triage-5, M3c). NULL = no cap: read as before. Over the cap, an archive is not read;
			   the current log (archive 0) is read for its last 7 days instead. A size xp_enumerrorlogs does not give is read in
			   full and said so. Each case writes one @ErrorLogCapped row. */
			SET @ErrLogSize = NULL;
			SET @ErrLogReadFrom = NULL;
			SET @ErrLogSkip = 0;
			SELECT @ErrLogSize = [Log File Size (Byte)] FROM @ErrorLogFiles WHERE [Archive #] = @i;
			IF @ErrorLogMaxMB IS NOT NULL AND @ErrLogSize IS NULL
				INSERT @ErrorLogCapped (LogKind, Archive, SizeBytes, Action) VALUES (N'SQL Server', @i, NULL, N'unknown');
			IF @ErrorLogMaxMB IS NOT NULL AND @ErrLogSize > CONVERT(BIGINT, @ErrorLogMaxMB) * 1048576
			BEGIN
				IF @i = 0 SET @ErrLogReadFrom = DATEADD(DAY, -7, GETDATE()) ELSE SET @ErrLogSkip = 1;
				INSERT @ErrorLogCapped (LogKind, Archive, SizeBytes, Action) VALUES (N'SQL Server', @i, @ErrLogSize, CASE WHEN @i = 0 THEN N'7 days' ELSE N'skipped' END);
			END
			IF @ErrLogReadFrom IS NOT NULL
			BEGIN
				INSERT @SQLLogData
				EXEC [sys].xp_readerrorlog 0, 1, N'RESOLVING', NULL, @ErrLogReadFrom, NULL;
				INSERT @SQLLogData
				EXEC [sys].xp_readerrorlog 0, 1, N'Failover', NULL, @ErrLogReadFrom, NULL;
				INSERT @SQLLogData
				EXEC [sys].xp_readerrorlog 0, 1, N'Login failed', NULL, @ErrLogReadFrom, NULL;
				INSERT @SQLLogData
				EXEC [sys].xp_readerrorlog 0, 1, N'Error:', NULL, @ErrLogReadFrom, NULL;
			END
			ELSE IF @ErrLogSkip = 0
			BEGIN
			   SELECT @sql = 'EXEC sp_readerrorlog ' + CAST(@i AS [NVARCHAR] (8)) + ',1, ''RESOLVING'';'
			  -- PRINT @sql
			   INSERT @SQLLogData
			   EXEC [sys].sp_executesql @sql;
			   SELECT @sql = 'EXEC sp_readerrorlog ' + CAST(@i AS [NVARCHAR] (8)) + ',1, ''Failover'';'
				--PRINT @sql
			   INSERT @SQLLogData
			   EXEC [sys].sp_executesql @sql;
				SELECT @sql = 'EXEC sp_readerrorlog ' + CAST(@i AS [NVARCHAR] (8)) + ',1, ''Login failed'';'
				--PRINT @sql
			   INSERT @SQLLogData
			   EXEC [sys].sp_executesql @sql;
			   SELECT @sql = 'EXEC sp_readerrorlog ' + CAST(@i AS [NVARCHAR] (8)) + ',1, ''Error:'';'
				--PRINT @sql
			   INSERT @SQLLogData
			   EXEC [sys].sp_executesql @sql;
			END
			   SET @sql = '';
			   SET @i = @i + 1;
			FETCH NEXT FROM curReadSQLErrorLogs INTO @i
		END
		CLOSE curReadSQLErrorLogs
		DEALLOCATE curReadSQLErrorLogs;
		END TRY
		BEGIN CATCH
			INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'SQL Server error log', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
			SELECT @errMessage  = ERROR_MESSAGE();
			IF @Debug = 1 RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
		END CATCH

 
		IF EXISTS (
			SELECT 1 FROM @SQLLogData
				)
			OR EXISTS (SELECT 1 FROM @ErrorLogCapped WHERE LogKind = N'SQL Server')
		BEGIN
			INSERT #output_sqldba_org_sp_triage 
			(
				SectionID
				, Section
				, Summary
				, Details
			) 
			SELECT 
			5
			, 'ERRORS - LOG Errors'
			,'------'
			,'------';

			INSERT #output_sqldba_org_sp_triage 
			(
				SectionID
				, Section
				, Summary
				, Severity
				, HoursToResolveWithTesting 
			)
			SELECT TOP 50 5
			,L.ProcessInfo
			, LEFT(CONVERT(NVARCHAR(50),[EventCount]) +' events in ' + CONVERT(NVARCHAR(50), DATEDIFF(HOUR,L.[From],L.[To])) + ' hours. ' + L.LogText +'. ' + M.text, 4000) /* was CONVERT(NVARCHAR(500)): Summary is 4000 (SpTriageDbNameQuotingCensusTests) */
			--,L.[Error]
			--,L.[From]
			--,L.[To]
			, @Result_Warning 
			, 0.25
			FROM(
			SELECT L.ProcessInfo, L.LogText
			,REPLACE(LEFT(L.LogText, CHARINDEX(',',L.LogText)-1),'Error: ','') [Error]
			, COUNT(1) [EventCount]
			,MIN(L.LogDate) [From]
			,MAX(L.LogDate) [To]
			FROM @SQLLogData L
			GROUP BY L.ProcessInfo, L.LogText
			)L
			INNER JOIN  sys.messages M ON M.message_id = L.Error AND M.language_id = 1033
			WHERE L.LogText LIKE 'Error:%'

			UNION ALL
			SELECT TOP 35 5, 'Date:'
			+ CONVERT([VARCHAR](20),LogDate,120)
			, replace(replace(replace(replace(CONVERT([NVARCHAR] (500),LogText), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ')  
			, @Result_Warning 
			, 0.25

			FROM @SQLLogData
			WHERE LogDate > DATEADD(MONTH,-1,GETDATE())
			AND LogText NOT LIKE 'Error:%'
			OPTION (RECOMPILE);

			/* @ErrorLogMaxMB (M3c): one row per SQL Server log file the cap kept from a full read (restart info and this cursor
			   can both name one archive: DISTINCT). */
			INSERT #output_sqldba_org_sp_triage
			(
				SectionID
				, Section
				, Summary
				, Severity
			)
			SELECT DISTINCT 5
			, N'Error log read cap'
			, CASE c.Action
				WHEN N'skipped' THEN N'Error log archive ' + CONVERT([NVARCHAR] (10), c.Archive) + N' (' + CONVERT([NVARCHAR] (30), CONVERT(DECIMAL(19, 1), c.SizeBytes / 1048576.0)) + N' MB) not read: over @ErrorLogMaxMB = ' + CONVERT([NVARCHAR] (12), @ErrorLogMaxMB)
				WHEN N'7 days' THEN N'Error log archive 0, the current log (' + CONVERT([NVARCHAR] (30), CONVERT(DECIMAL(19, 1), c.SizeBytes / 1048576.0)) + N' MB), is over @ErrorLogMaxMB = ' + CONVERT([NVARCHAR] (12), @ErrorLogMaxMB) + N': read for its last 7 days only'
				ELSE N'Error log archive ' + CONVERT([NVARCHAR] (10), c.Archive) + N' size unknown: read in full'
			END
			, @Result_NA
			FROM @ErrorLogCapped c
			WHERE c.LogKind = N'SQL Server'
			OPTION (RECOMPILE);
		END
	END
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Server logins have been checked from the log',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Agent log for errors
			----------------------------------------*/

	DECLARE @Errorlog TABLE
	(
		LogDate DATETIME
		, ErrorLevel VARCHAR(250)
		, [Text] [NVARCHAR] (4000)
	)
	DECLARE @AgentLogFiles TABLE ([Archive #] INT, [Date] [NVARCHAR] (259), [Log File Size (Byte)] BIGINT); /* xp_enumerrorlogs 2, read only under @ErrorLogMaxMB (M3c) */
	/*Ignore the agent logs if you cannot find it, ELSE errors will come*/
	BEGIN TRY

		IF DATEADD(MINUTE,5,@lastservericerestart) <  (
			SELECT MIN(Login_time) 
			FROM dbo.sysprocesses 
			WHERE LEFT(program_name, 8) = 'SQLAgent'
		)
		BEGIN
			IF @Debug = 1
			BEGIN
				SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
				SET @DebugTime = GETDATE();
				IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
				RAISERROR (N'Agent started much later than Service. Might point to Agent never being restarted before. If you see the following error, just restart the agent and run this script again >>',0,1) WITH NOWAIT;
				RAISERROR (N'Msg 0, Level 11, State 0, Line 2032 A severe error occurred on the current command.  The results, if any, should be discarded.',0,1) WITH NOWAIT;
			END
		END

		IF EXISTS (
			SELECT 1
			,* 
			FROM dbo.sysprocesses 
			WHERE LEFT(program_name, 8) = 'SQLAgent'
		)
		BEGIN   
			/* @ErrorLogMaxMB (lane bundle-sp-triage-5, M3c): the Agent log archive read here (1) is not read over the cap; its
			   size comes from xp_enumerrorlogs 2, called only when a cap is set. An archive 1 that list does not give (no
			   such file yet) gets no row: it is read exactly as with no cap (round-2 shape row A1, brief s8). */
			SET @AgentLogSkip = 0;
			IF @ErrorLogMaxMB IS NOT NULL
			BEGIN
				INSERT @AgentLogFiles ([Archive #], [Date], [Log File Size (Byte)])
				EXEC xp_enumerrorlogs 2;
				SET @ErrLogSize = NULL;
				SELECT @ErrLogSize = [Log File Size (Byte)] FROM @AgentLogFiles WHERE [Archive #] = 1;
				IF @ErrLogSize > CONVERT(BIGINT, @ErrorLogMaxMB) * 1048576
				BEGIN
					SET @AgentLogSkip = 1;
					INSERT @ErrorLogCapped (LogKind, Archive, SizeBytes, Action) VALUES (N'SQL Agent', 1, @ErrLogSize, N'skipped');
				END
			END
			IF @AgentLogSkip = 0
			BEGIN
			SET @dynamicSQL = 'EXEC sp_readerrorlog 1, 2, ''Error:'' ';
			INSERT @Errorlog
			EXEC sp_executesql @dynamicSQL;
			END
		END 

		/*Already checked before
		BEGIN   
			SET @dynamicSQL = 'EXEC sp_readerrorlog 1, 1, ''Error:'' '
			INSERT @Errorlog
			EXEC sp_executesql @dynamicSQL;
		END
		*/
		IF EXISTS (SELECT * FROM @Errorlog)
			OR EXISTS (SELECT 1 FROM @ErrorLogCapped WHERE LogKind = N'SQL Agent')
		BEGIN
			INSERT #output_sqldba_org_sp_triage 
			(
				[SectionID]
				, [Section]
				, [Summary]
				, [Details]
			) 
			SELECT 
			6
			, 'AGENT LOG Errors'
			, '------'
			, '------';

			INSERT #output_sqldba_org_sp_triage 
			(
				SectionID
				, Section
				, Summary
				, Severity
				, Details
				, HoursToResolveWithTesting  
			)
			SELECT TOP 100
			6
			, 'Date:'+ CONVERT([VARCHAR](20),LogDate ,120)
			, 'ErrorLevel:'+ CONVERT([VARCHAR](20),ErrorLevel)
			, @Result_Warning ,[Text], 1  
			FROM @Errorlog 
			ORDER BY LogDate DESC
			OPTION (RECOMPILE);

			/* @ErrorLogMaxMB (M3c): the Agent log file the cap kept from a read. */
			INSERT #output_sqldba_org_sp_triage
			(
				SectionID
				, Section
				, Summary
				, Severity
			)
			SELECT DISTINCT 6
			, N'Agent log read cap'
			, CASE c.Action
				WHEN N'skipped' THEN N'Agent log archive ' + CONVERT([NVARCHAR] (10), c.Archive) + N' (' + CONVERT([NVARCHAR] (30), CONVERT(DECIMAL(19, 1), c.SizeBytes / 1048576.0)) + N' MB) not read: over @ErrorLogMaxMB = ' + CONVERT([NVARCHAR] (12), @ErrorLogMaxMB)
				ELSE N'Agent log archive ' + CONVERT([NVARCHAR] (10), c.Archive) + N' size unknown: read in full'
			END
			, @Result_NA
			FROM @ErrorLogCapped c
			WHERE c.LogKind = N'SQL Agent'
			OPTION (RECOMPILE);
		END  
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Agent log', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT; 
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Error reading agent log',0,1) WITH NOWAIT;
	END
	END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
			RAISERROR (N'Agent log parsed for errors',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Look for failed agent jobs
			----------------------------------------*/
DECLARE @master_filesTable TABLE
(
	database_id INT
	, size MONEY
	, type_desc [VARCHAR](15)
	, file_id INT
	, physical_name [NVARCHAR] (500)
)
SET @dynamicSQL = 'SELECT database_id
,size
,type_desc
,file_id
,physical_name
FROM [sys].master_files';

IF @IsSQLAzure = 0
BEGIN
	INSERT @master_filesTable
	EXEC sp_executesql @dynamicSQL ;
END

DECLARE @sysjobhistoryTable TABLE
(
	job_id uniqueidentifier
	, step_id INT
	, instance_id INT
	, rid INT IDENTITY(1,1) NOT NULL
	, UNIQUE NONCLUSTERED (instance_id, rid) /*perf 2026-07-02: joined on instance_id; the key enables seeks (no logic change). SQL Server 2008 has no inline INDEX on a table variable, so a UNIQUE key made unique by rid stands in for the index*/
)
SET @dynamicSQL = '
	SELECT 
	DBSysJobHistory.job_id
	, DBSysJobHistory.step_id
	, MAX(DBSysJobHistory.instance_id) as instance_id
	FROM msdb.dbo.sysjobhistory DBSysJobHistory
	GROUP BY DBSysJobHistory.job_id, DBSysJobHistory.step_id';

BEGIN TRY
IF @IsSQLAzure = 0
BEGIN
	INSERT @sysjobhistoryTable
	EXEC sp_executesql @dynamicSQL ;
END


DECLARE @sysjobhistoryTable2 TABLE
(
	job_id uniqueidentifier
	, step_id INT
	, run_status INT
	, instance_id [BIGINT]
	, sql_message_id [BIGINT]
	, sql_severity INT
	, [message] [NVARCHAR] (4000)
	, run_date INT
	, run_time INT
	, run_duration INT
	, retries_attempted INT
	, [server] [NVARCHAR] (400)
	, rid INT IDENTITY(1,1) NOT NULL
	, UNIQUE NONCLUSTERED (instance_id, rid) /*perf 2026-07-02: joined on instance_id; the key enables seeks (no logic change). SQL Server 2008 has no inline INDEX on a table variable, so a UNIQUE key made unique by rid stands in for the index*/
);

SET @dynamicSQL = '
SELECT 
	job_id
	, step_id
	, run_status
	, instance_id
	, sql_message_id
	, DBSysJobHistory.sql_severity
	, LEFT(DBSysJobHistory.message, 3988)
	, run_date
	, run_time
	, DBSysJobHistory.run_duration
	, DBSysJobHistory.retries_attempted
	, DBSysJobHistory.server
FROM msdb.dbo.sysjobhistory DBSysJobHistory';

IF @IsSQLAzure = 0
BEGIN
	INSERT @sysjobhistoryTable2
	EXEC sp_executesql @dynamicSQL ;
END

DECLARE @sysjobsTable TABLE
(
	job_id uniqueidentifier
	, [name] [NVARCHAR] (500)
);

SET @dynamicSQL = '
SELECT 
	job_id 
	, [name] 
FROM msdb.dbo.sysjobs SysJobs';

IF @IsSQLAzure = 0
BEGIN
	INSERT @sysjobsTable
	EXEC sp_executesql @dynamicSQL ;
END


DECLARE @sysjobsstepsTable TABLE
(
	job_id uniqueidentifier
	, step_id INT
	, [name] [NVARCHAR] (500)
);
SET @dynamicSQL = '
SELECT 
	job_id
	, step_id
	, step_name
FROM msdb.dbo.sysjobsteps';

IF @IsSQLAzure = 0
BEGIN
	INSERT @sysjobsstepsTable
	EXEC sp_executesql @dynamicSQL ;
END
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Agent job history', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE();
	IF @Debug = 1 RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH
	IF EXISTS 
	(
		SELECT *  
		FROM @sysjobhistoryTable2 DBSysJobHistory
			JOIN 
			(
				SELECT 
					DBSysJobHistory.job_id
					, DBSysJobHistory.step_id
					, MAX(DBSysJobHistory.instance_id) as instance_id
				FROM @sysjobhistoryTable DBSysJobHistory
				GROUP BY DBSysJobHistory.job_id
					, DBSysJobHistory.step_id
			) AS Instance ON DBSysJobHistory.instance_id = Instance.instance_id
		WHERE DBSysJobHistory.run_status <> 1
	)
	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 
	7
	, 'FAILED AGENT JOBS'
	, '------'
	, '------';

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, Severity
		, Details
		, HoursToResolveWithTesting
	)
	SELECT  
		7
		, 'Job Name:' + ISNULL(SysJobs.name,'')
		+ '; Step:'+ ISNULL(SysJobSteps.name ,'')
		+ ' - '+ ISNULL(Job.run_status,'')
		, 'MessageId: ' +CONVERT([VARCHAR](20),ISNULL(Job.sql_message_id,''))
		+ '; Severity:'+ CONVERT([VARCHAR](20),ISNULL(Job.sql_severity,''))
		, ''
		, 'Message:'+ Job.message
		+ '; Date:' + CONVERT([VARCHAR](20), ISNULL(Job.exec_date,''),120)
		, 2
		/*, Job.run_duration
		, Job.server
		, SysJobSteps.output_file_name
		*/
	FROM
	(
		SELECT 
			Instance.instance_id
			, DBSysJobHistory.job_id
			, DBSysJobHistory.step_id
			, DBSysJobHistory.sql_message_id
			, DBSysJobHistory.sql_severity
			, DBSysJobHistory.message
			, (CASE DBSysJobHistory.run_status 
				WHEN 0 THEN 'Failed' 
				WHEN 1 THEN 'Succeeded' 
				WHEN 2 THEN 'Retry' 
				WHEN 3 THEN 'Canceled' 
				WHEN 4 THEN 'In progress'
			  END
			) as run_status
			,((SUBSTRING(CAST(DBSysJobHistory.run_date AS [NVARCHAR] (8)), 5, 2) + '/'
			  + SUBSTRING(CAST(DBSysJobHistory.run_date AS [NVARCHAR] (8)), 7, 2) + '/'
			  + SUBSTRING(CAST(DBSysJobHistory.run_date AS [NVARCHAR] (8)), 1, 4) + ' '
			  + SUBSTRING((REPLICATE('0',6-LEN(CAST(DBSysJobHistory.run_time AS [NVARCHAR])))
			  + CAST(DBSysJobHistory.run_time AS [NVARCHAR])), 1, 2) + ':'
			  + SUBSTRING((REPLICATE('0',6-LEN(CAST(DBSysJobHistory.run_time AS [NVARCHAR])))
			  + CAST(DBSysJobHistory.run_time AS [NVARCHAR])), 3, 2) + ':'
			  + SUBSTRING((REPLICATE('0',6-LEN(CAST(DBSysJobHistory.run_time as [NVARCHAR])))
			  + CAST(DBSysJobHistory.run_time AS [NVARCHAR])), 5, 2))) [exec_date]
			, DBSysJobHistory.run_duration
			, DBSysJobHistory.retries_attempted
			, DBSysJobHistory.server
		FROM @sysjobhistoryTable2 DBSysJobHistory
		JOIN 
		(
			SELECT 
				DBSysJobHistory.job_id
				, DBSysJobHistory.step_id
				, MAX(DBSysJobHistory.instance_id) as instance_id
			FROM @sysjobhistoryTable DBSysJobHistory
			GROUP BY DBSysJobHistory.job_id
				, DBSysJobHistory.step_id
		) AS Instance ON DBSysJobHistory.instance_id = Instance.instance_id
		WHERE DBSysJobHistory.run_status <> 1
	) AS Job
	JOIN @sysjobsTable SysJobs
		   ON (Job.job_id = SysJobs.job_id)
	JOIN @sysjobsstepsTable SysJobSteps
		   ON (Job.job_id = SysJobSteps.job_id 
		   AND Job.step_id = SysJobSteps.step_id)
	OPTION (RECOMPILE);

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Checked for failed agent jobs',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Look for failed backups
			----------------------------------------*/
DECLARE @backupset TABLE
( 
	id [BIGINT] IDENTITY(1,1)
	, [database_name] [NVARCHAR] (100)
	, [recovery_model] [NVARCHAR] (50)
	, [backup_start_date] DATETIME
	, [backup_finish_date] DATETIME
	, [type] [NVARCHAR] (20)
	, backup_size [BIGINT]
	, compressed_backup_size [BIGINT]
	, first_lsn numeric(25,0)
	, database_backup_lsn numeric(25,0)
	, PRIMARY KEY CLUSTERED (id,[backup_start_date],[database_name],[recovery_model])
	, UNIQUE NONCLUSTERED ([database_name],[backup_finish_date], id) /*perf 2026-07-02: the correlated MAX(backup_finish_date) per database_name subquery couldn't seek (clustered PK leads with id); this NC key lets it seek instead of scanning the whole table var per outer row (no logic change). SQL Server 2008 has no inline INDEX on a table variable, so a UNIQUE key made unique by id stands in for the index*/
);

SET @dynamicSQL = '
SELECT 
[database_name]
, [recovery_model]
, [backup_start_date]
, [backup_finish_date]
, [type]
, backup_size
, compressed_backup_size
, first_lsn
, database_backup_lsn
FROM  msdb.[dbo].[backupset]';

IF @IsSQLAzure = 0
BEGIN
	INSERT @backupset
	EXEC sp_executesql @dynamicSQL ;
END


		
	IF EXISTS
	(
		SELECT *
		FROM (
			SELECT *
			FROM @backupset x  
			WHERE backup_finish_date = 
			(
				SELECT max(backup_finish_date) 
				FROM @backupset b
				WHERE b.database_name =   x.database_name 
			)    
		) a  
		RIGHT OUTER JOIN @sysdatabasesTable b  ON a.database_name =   b.name  
		INNER JOIN @Databases D ON b.database_id = D.database_id
		WHERE b.name <> 'tempdb' /*Exclude tempdb*/
		AND (backup_finish_date < DATEADD(d,-1,GETDATE())  
		OR backup_finish_date IS NULL) 
	)
	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 
	8
	,'DATABASE - No recent Backups'
	,'------'
	,'------';

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, Severity
		, HoursToResolveWithTesting
	)

	SELECT 
		8
		, name [Section] 
		, ('; Backup Finish Date:' + ISNULL(CONVERT([VARCHAR](20),backup_finish_date,120),'')
		+ '; Type:' +coalesce(type,'NO BACKUP')) [Summary]
		, @Result_YourServerIsDead
		, 2
	FROM (
		SELECT 
			database_name
			, backup_finish_date
			, CASE 
				WHEN  type = 'D' THEN 'Full'    
			  	WHEN  type = 'I' THEN 'Differential'                
			  	WHEN  type = 'L' THEN 'Transaction Log'                
			  	WHEN  type = 'F' THEN 'File'                
			  	WHEN  type = 'G' THEN 'Differential File'                
			  	WHEN  type = 'P' THEN 'Partial'                
			  	WHEN  type = 'Q' THEN 'Differential partial'   
			  END AS type 
		FROM @backupset x  
		WHERE backup_finish_date = 
		(
			SELECT max(backup_finish_date) 
			FROM @backupset b 
			WHERE b.database_name =   x.database_name 
		)    
	) a  
	RIGHT OUTER JOIN @sysdatabasesTable b  ON a.database_name =   b.name  
	INNER JOIN @Databases D ON b.database_id = D.database_id
	WHERE b.name <> 'tempdb' /*Exclude tempdb*/
	AND (backup_finish_date < DATEADD(d,-1,GETDATE())  
	OR backup_finish_date IS NULL)
	OPTION (RECOMPILE); 

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Checked for failed backups',0,1) WITH NOWAIT;
	END



			/*----------------------------------------
			--Check the Log chain for LOG backups, and VLFs - Thanks Rob
			----------------------------------------*/
DECLARE @DatabasesForLOG TABLE
	(
		[id] [INT] 
		, [database_id] INT
		, [databasename] [NVARCHAR] (250)
		, [compatibility_level] [BIGINT]
		, [user_access] [BIGINT]
		, [user_access_desc] [NVARCHAR] (50)
		, [state] [BIGINT]
		, [state_desc]  [NVARCHAR] (50)
		, [recovery_model] [BIGINT]
		, [recovery_model_desc]  [NVARCHAR] (50)
		, [create_date] DATETIME
		, [AGReplicaRole] INT
		, [BackupPref] [NVARCHAR] (250)
		, [CurrentLocation] [NVARCHAR] (250)
		, AGName [NVARCHAR] (250)
		, [ReadSecondary] [NVARCHAR] (250)
		, [TotalIO] BIGINT
		, [TotalIORank] INT
		, [SkipForDBLoop] BIT
	);

INSERT INTO @DatabasesForLOG
SELECT * 
FROM @Databases;
--WHERE (CurrentLocation = @ThisServer AND BackupPref ='primary')

--variables to hold each 'iteration'  
DECLARE @query [NVARCHAR] (1000) ; 
DECLARE @dbname [sysname] ; 
DECLARE @vlfs [INT]  ;
 
  
--table variable to hold results  
DECLARE @vlfcounts table  
    (
		[dbname] [sysname]
		,  [vlfcount] [int]
	)  ;

DECLARE @avg_max_log_size table 
	(
		[dbname] [sysname]
		, [avgsize] [MONEY] 
		, [maxsize] [MONEY]
	);
 
 
--table variable to capture DBCC loginfo output  
--changes in the output of DBCC loginfo from SQL2012 mean we have to determine the version 
IF NOT EXISTS 
(
	SELECT databasename 
	FROM @DatabasesForLOG 
	WHERE databasename = 'tempdb'
)
BEGIN /*Make sure we check tempdb VLFs*/
	INSERT INTO @DatabasesForLOG 
	(
		[databasename]
	)
	SELECT 'tempdb';
END

DECLARE @MajorVersion  [TINYINT];   
SET @MajorVersion = LEFT(CAST(SERVERPROPERTY('ProductVersion') AS [NVARCHAR] (max)),CHARINDEX('.',CAST(SERVERPROPERTY('ProductVersion') AS [NVARCHAR] (max)))-1) 
DECLARE @dbccloginfo table  
    (  
        [fileid]  [TINYINT] 
        , [file_size] [BIGINT]
        , [start_offset] [BIGINT]
        , [fseqno] [INT]
        , [status]  [TINYINT]  
        , [parity]  [TINYINT]   
        , [create_lsn] [numeric] (25,0)  
    );
IF @MajorVersion < 11 -- pre-SQL2012 
BEGIN 

    WHILE EXISTS
	(
		SELECT 
		TOP 1 
		T1.databasename 
		FROM @DatabasesForLOG T1
	)  
    BEGIN  
  
        SET @dbname = 
		(
			SELECT 
			TOP 1  
			databasename 
			FROM @DatabasesForLOG
		)  
        /* Quote-safe literal; a failed read leaves @vlfs NULL (never 0 or @@ROWCOUNT) and the run continues (2026-10-09). */
        SET @query = N'dbcc loginfo (N''' + REPLACE(@dbname, N'''', N'''''') + N''') ' ;
		SET @vlfs = NULL;
  
		IF @IsSQLAzure = 0 AND EXISTS (SELECT 1 FROM #sp_triage_readable_db AS r WHERE r.name = @dbname)
		BEGIN
			BEGIN TRY
				INSERT into @dbccloginfo
				exec (@query)  ;
				SET @vlfs = @@rowcount ;
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'VLF count (dbcc loginfo)' + ISNULL(N' ' + QUOTENAME(@dbname), N''), N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SET @ReadSkippedMsg = N'sp_triage: VLF count (dbcc loginfo) skipped for [' + @dbname + N']: ' + CONVERT([NVARCHAR] (20), ERROR_NUMBER()) + N' ' + ERROR_MESSAGE();
				RAISERROR(N'%s', 0, 1, @ReadSkippedMsg) WITH NOWAIT;
			END CATCH
		END
  
        INSERT @vlfcounts  
        values(@dbname, @vlfs) ; 
  
        DELETE 
		FROM @DatabasesForLOG 
		WHERE databasename = @dbname ; 
  
    END --while 
END 
ELSE 
BEGIN 
    DECLARE @dbccloginfo2012 TABLE  
    (  
        [RecoveryUnitId] [INT]
        , [fileid]  [TINYINT] 
        , [file_size] [BIGINT]
        , [start_offset] [BIGINT]
        , [fseqno] [INT] 
        , [status]  [TINYINT]  
        , [parity]  [TINYINT]   
        , [create_lsn] [numeric] (25,0)  
    )  
  
    WHILE EXISTS
	(
		SELECT 
		TOP 1 
			databasename 
		FROM @DatabasesForLOG
	)  
    BEGIN  
  
        SET @dbname = 
		(
			SELECT 
			TOP 1 
			databasename 
			FROM @DatabasesForLOG
		)  
        /* Quote-safe literal; a failed read leaves @vlfs NULL (never 0 or @@ROWCOUNT) and the run continues (2026-10-09). */
        SET @query = N'dbcc loginfo (N''' + REPLACE(@dbname, N'''', N'''''') + N''') '  ;
		SET @vlfs = NULL;
  
		IF @IsSQLAzure = 0 AND EXISTS (SELECT 1 FROM #sp_triage_readable_db AS r WHERE r.name = @dbname)
		BEGIN
			BEGIN TRY
				INSERT into @dbccloginfo2012
				exec (@query) ;
				SET @vlfs = @@rowcount  ;
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'VLF count (dbcc loginfo)' + ISNULL(N' ' + QUOTENAME(@dbname), N''), N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SET @ReadSkippedMsg = N'sp_triage: VLF count (dbcc loginfo) skipped for [' + @dbname + N']: ' + CONVERT([NVARCHAR] (20), ERROR_NUMBER()) + N' ' + ERROR_MESSAGE();
				RAISERROR(N'%s', 0, 1, @ReadSkippedMsg) WITH NOWAIT;
			END CATCH
		END
  
        INSERT @vlfcounts  
        values(@dbname, @vlfs);  
  
        DELETE
		FROM @DatabasesForLOG
		WHERE databasename = @dbname  ;
  
    END --while 
	
	
	INSERT INTO @dbccloginfo 
	(
		fileid
		, file_size
		, start_offset
		, fseqno
		, [status]
		, parity
		, create_lsn 
	)
	SELECT 
		fileid
		, file_size
		, start_offset
		, fseqno
		, [status]
		, parity
		, create_lsn  
	FROM @dbccloginfo2012;
		
END 
  
----output the full list  
--select dbname, vlfcount  
--from @vlfcounts  
--order by dbname

IF @IsSQLAzure = 0
BEGIN

	INSERT INTO #output_sqldba_org_sp_triage_db_size
	SELECT 
		d.name
		, ROUND(SUM(
			CASE 
				WHEN type =0 then cast(mf.size as [BIGINT]) 
				ELSE 0
			END) * 8 / 1024, 0) AS  [Size_MBs]
		,ROUND(SUM(
			CASE 
				WHEN type =1 then cast(mf.size as [BIGINT]) 
				ELSE 0 
			END) * 8 / 1024, 0) AS [log_size_mb]
	
	FROM [sys].master_files mf
	INNER JOIN @sysdatabasesTable d ON d.database_id = mf.database_id   
	INNER JOIN @Databases DB ON d.database_id = DB.database_id
	WHERE 1=1 /*d.database_id > 4   -- DO NOT Skip system databases */
	GROUP BY d.name
	ORDER BY Size_MBs desc,d.name;
	

	INSERT into @avg_max_log_size
	(
		dbname
		, avgsize
		, maxsize
	)
	SELECT  
		[database_name] AS [DATABASE] 
		, AVG([backup_size] / 1024 / 1024) AS [AVG BACKUP SIZE MB]
		, MAX([backup_size] / 1024 / 1024) AS [max BACKUP SIZE MB]
	FROM    @backupset
	WHERE   [type] = 'L'
	AND [backup_start_date] >= dateadd(mm,-3,getdate())
	GROUP BY [database_name];


	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, Details
		, Severity
	)
	SELECT 
		8
		, 'Log and VLF Checks'
		, '[DBName].[Comments]'
		, '[vlfcount];[Size_MBs];[log_size_mb];[AvgBackupSizeMB];[MaxBackupSizeMB]'
		, '';

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, Details
		, Severity
	)

	SELECT 8
	, 'Log and VLF Checks'

	---
	, ds.name

	+ CASE 
		WHEN log_size_mb>Size_MBs THEN ' .LOG too big compared to Data' 
		ELSE ''
	END --as LogSizeCommentary1
	+ CASE 
		WHEN vlfcount>50 THEN ' .High VLFs' 
		WHEN vlfcount IS NULL THEN ' .VLF count unavailable' /* read failed or skipped; see the severity-0 message */
		ELSE ''
	END --as VLFCommentary 
	+ CASE 
		WHEN log_size_mb/2 > ls.maxsize  THEN ' .LOG too big compared to Max LOG Backup' /*'Log Size is more than twice the size of the maximum needed based on 3 month history' */
		ELSE ''
	END --as LogSizeCOmmentary2
	 [Summary]

	---
	--[vlfcount];[Size_MBs];[log_size_mb];[AvgBackupSizeMB];[MaxBackupSizeMB]
	, ISNULL(CONVERT([NVARCHAR] (20),v.vlfcount), N'?') /* NULL = not read; CONCAT_NULL_YIELDS_NULL would blank the row */
	 + ';' + CONVERT([NVARCHAR] (20),ds.Size_MBs)
	 + ';' + CONVERT([NVARCHAR] (20),ds.log_size_mb)
	 + ';' + CONVERT([NVARCHAR] (20),ls.avgsize)
	 + ';' + CONVERT([NVARCHAR] (20),ls.maxsize) 
	 ---
	, CASE 
	WHEN 
		( CASE WHEN log_size_mb > Size_MBs then 1 ELSE 0 end
		+ CASE WHEN vlfcount > 50 then 1 ELSE 0 end
		+ CASE WHEN log_size_mb / 2 > ls.maxsize  then 1 ELSE 0 end
		) > 0 THEN 4 
	END [Severity]
	FROM #output_sqldba_org_sp_triage_db_size ds
	JOIN @vlfcounts v on ds.name=v.dbname
	JOIN @avg_max_log_size ls on v.dbname=ls.dbname;
	
END
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done with Log and VLF Checks',0,1) WITH NOWAIT;
	END


			/*----------------------------------------
			--Look for backups and recovery model information
			----------------------------------------*/
	DECLARE @stdevmultiplier INT;
	SET @stdevmultiplier = 3 ;
	DECLARE @LookBackDays INT;
	SET @LookBackDays = 60 ;/*How many days, you know, to filter back on..*/
	DECLARE @StartFilterDate DATETIME;
	SET @StartFilterDate = DATEADD(DAY,-@LookBackDays,GETDATE());

	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 
	9
	, 'DATABASE - RPO in minutes and RTO in 15 min slices'
	, 'DB;Compat;recovery_model;Best RTO HH:MM:SS ;Last Full;Last TL;DateCreated;AGName;ReadSecondary;CurrentLocation;RecordsStart;RecordsEnd;AVGBackupChain_Seconds;CompressedBackupSize_GB;BackupSize_GB;LOG_size_GB;DATA_size_GB;TOTAL_size_GB'
	, 'HH:MM:SS';

	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		, HoursToResolveWithTesting
	) /* Had to change to DAYS thanks to some clients*/
	/*It is still basic, we are only filtering DIFFs and not checking chains from FULL > DIFF > LOGs
	LOGS still need to be filtered for DIFFs, but I see very little use in doing it now, as we still just want daily averages to gauge RPO RTO
	This is not rocket science, and likely nobody will die.. unles you don't have HA/DR configured, then this will not help you even if it was 100% accurate
	Prioritise
	We added Standard Deviation to get rid of outlyers, see why the LOG DIFF thing doesnt really matter.
	We'll include 3 standard deviations, which should cover 99.7% of results*/

	SELECT 
	9
	, CONVERT([VARCHAR](20),DATEDIFF(HOUR,
	CASE 
		WHEN recovery_model = 'FULL' AND oldcheck.[Last Transaction Log] > oldcheck.[Last Full] THEN oldcheck.[Last Transaction Log]
		WHEN recovery_model = 'FULL' AND oldcheck.[Last Transaction Log] <= oldcheck.[Last Full] THEN [Last Full]
		ELSE oldcheck.[Last Full] 
	END, GETDATE())) + ' hours'
	, (x.database_name
	+ '; ' +CONVERT([VARCHAR](10),[compatibility_level])
	+ '; ' + ISNULL(recovery_model,'')
	+ '; ' + ISNULL(LEFT(CONVERT([VARCHAR](20),DATEADD(SECOND,x.Timetaken,0) ,114),8),'')
	+ '; ' + ISNULL(CONVERT([VARCHAR](20),oldcheck.[Last Full],120),'')
	+ '; ' + ISNULL(CONVERT([VARCHAR](20),oldcheck.[Last Transaction Log],120),'')
	+ '; ' + ISNULL(CONVERT([VARCHAR](20),oldcheck.create_date,120),'')
	+ '; ' + ISNULL(x.AGName COLLATE DATABASE_DEFAULT,'')
	+ '; ' + ISNULL(x.ReadSecondary COLLATE DATABASE_DEFAULT,'')
	+ '; ' + ISNULL(x.[CurrentLocation] COLLATE DATABASE_DEFAULT,'')
	+ '; ' + ISNULL(CONVERT([VARCHAR],RecordsStart,120),'')
	+ '; ' + ISNULL(CONVERT([VARCHAR],RecordsEnd,120),'')
	+ '; ' + ISNULL(CONVERT([VARCHAR](20),[AVGBackupChain_Seconds]),'')
	+ '; ' + ISNULL(CONVERT([VARCHAR](20),[CompressedBackupSize_GB]),'')
	+ '; ' + ISNULL(CONVERT([VARCHAR](20),[BackupSize_GB]),'')
	+ '; ' + ISNULL(CONVERT([VARCHAR](20),[LOG_size_GB]),'')
	+ '; ' + ISNULL(CONVERT([VARCHAR](20),[DATA_size_GB]),'')
	+ '; ' + ISNULL(CONVERT([VARCHAR](20),[TOTAL_size_GB]),'')
	)
	, ISNULL(LEFT(CONVERT([VARCHAR](20),DATEADD(SECOND,x.Timetaken,0) ,114),8),'')
	, 	CONVERT([VARCHAR](20),
	CASE 
		WHEN 
		DATEDIFF(
			HOUR,
			CASE 
				WHEN recovery_model = 'FULL' AND oldcheck.[Last Transaction Log] > oldcheck.[Last Full] THEN oldcheck.[Last Transaction Log]
				WHEN recovery_model = 'FULL' AND oldcheck.[Last Transaction Log] <= oldcheck.[Last Full] THEN [Last Full]
				ELSE oldcheck.[Last Full] 
			END, GETDATE()
		) < 1 THEN 0
		WHEN 
		DATEDIFF(
			HOUR,CASE 
				WHEN recovery_model = 'FULL' AND oldcheck.[Last Transaction Log] > oldcheck.[Last Full] THEN oldcheck.[Last Transaction Log]
				WHEN recovery_model = 'FULL' AND oldcheck.[Last Transaction Log] <= oldcheck.[Last Full] THEN [Last Full]
				ELSE oldcheck.[Last Full] 
			END, GETDATE()
		) BETWEEN 1 AND 2 THEN 2
		WHEN 
		DATEDIFF(
			HOUR,CASE 
					WHEN recovery_model = 'FULL' AND oldcheck.[Last Transaction Log] > oldcheck.[Last Full] THEN oldcheck.[Last Transaction Log]
					WHEN recovery_model = 'FULL' AND oldcheck.[Last Transaction Log] <= oldcheck.[Last Full] THEN [Last Full]
					ELSE oldcheck.[Last Full] 
				END, GETDATE()) BETWEEN 2 AND 8 THEN 4
		WHEN 
		DATEDIFF(
			HOUR,CASE 
					WHEN recovery_model = 'FULL' AND oldcheck.[Last Transaction Log] > oldcheck.[Last Full] THEN oldcheck.[Last Transaction Log]
					WHEN recovery_model = 'FULL' AND oldcheck.[Last Transaction Log] <= oldcheck.[Last Full] THEN [Last Full]
					ELSE oldcheck.[Last Full] 
				END, GETDATE()) BETWEEN 8 AND 24 THEN 6

		ELSE 8 
	END
	) 
	FROM 
	(
		SELECT 
		dbs.name [database_name]
		, dbs.[compatibility_level] , dbs.recovery_model_desc [recovery_model],D.AGName, D.ReadSecondary, [CurrentLocation]
		, MIN(backup_start_date) RecordsStart
		, MAX(backup_finish_date)RecordsEnd
		, AVG([LogChainTotalSeconds]) [AVGBackupChain_Seconds]
		, AVG(backupsize.compressed_backup_size) [CompressedBackupSize_GB]
		, AVG(backupsize.backup_size) [BackupSize_GB]
		, AVG([Size].LOG_size_GB) [LOG_size_GB]
		, AVG([Size].DATA_size_GB ) [DATA_size_GB]
		, AVG([Size].TOTAL_size_GB) [TOTAL_size_GB]
		, AVG([LogChainTotalSeconds]) 'Timetaken' /*Longest time taken for FULL backup only*/

		FROM  @sysdatabasesTable dbs
			INNER JOIN @Databases D ON dbs.database_id = D.database_id
			INNER JOIN
			(
			SELECT 
			database_name
			, compatibility_level
			, recovery_model
			, type
			, backup_start_date
			, backup_finish_date
			, Timetaken
			, [Avg_compressed_backup_size]
			, [Avg_backup_size]
			, [LSN_for_matching]
			, [LogChainTotalSeconds]
			, AverageTime
			, stddevpopulation
			FROM 
			(
				SELECT 
				database_name
				, compatibility_level
				, recovery_model
				, type
				, backup_start_date
				, backup_finish_date
				, Timetaken
				, AVG(compressed_backup_size) OVER (PARTITION BY database_name,type) [Avg_compressed_backup_size]
				, AVG(backup_size) OVER (PARTITION BY database_name,type) [Avg_backup_size]
				, [LSN_for_matching]
				, SUM(Timetaken) OVER (PARTITION BY database_name,[LSN_for_matching]) [LogChainTotalSeconds]
				, AVG(Timetaken) OVER (PARTITION BY database_name,type) AS AverageTime
				, STDEVP(Timetaken) OVER (PARTITION BY database_name,type) AS stddevpopulation
				FROM 
				(
					SELECT  
						DB_NAME(dbs.database_id) [database_name]
						, dbs.[compatibility_level]
						, dbs.recovery_model_desc [recovery_model]
						--,D.AGName, D.ReadSecondary, [CurrentLocation]
						, [type]
						, backup_start_date,backup_finish_date
						, (DATEDIFF(SECOND,backup_start_date, backup_finish_date)) 'Timetaken'
						, CONVERT(MONEY,compressed_backup_size/1024/1024/1024) [compressed_backup_size]
						, CONVERT(MONEY,backup_size/1024/1024/1024) [backup_size]
						, 
						CASE 
							WHEN  type = 'D' THEN first_lsn 
							ELSE database_backup_lsn 
						END [LSN_for_matching]
					FROM  @sysdatabasesTable dbs
					INNER JOIN @Databases D ON dbs.database_id = D.database_id
					LEFT OUTER JOIN  @backupset bs ON dbs.name = bs.database_name  
					AND dbs.recovery_model_desc COLLATE DATABASE_DEFAULT = bs.recovery_model COLLATE DATABASE_DEFAULT
					WHERE type IN ('I','D')
					AND backup_start_date >  @StartFilterDate 

					/* THEN DO DIFFS..*/
					UNION ALL
					SELECT 
						database_name
						, compatibility_level
						, recovery_model
						, type
						, backup_start_date
						, backup_finish_date
						, Timetaken
						, compressed_backup_size
						, backup_size
						, database_backup_lsn [LSN_for_matching]
					--,IRank
					FROM
					(
						SELECT  
							DB_NAME(dbs.database_id) [database_name]
							, dbs.[compatibility_level] 
							, dbs.recovery_model_desc [recovery_model]--,D.AGName, D.ReadSecondary, [CurrentLocation]
							,type
							, backup_start_date
							,backup_finish_date
							, (DATEDIFF(SECOND,backup_start_date, backup_finish_date)) 'Timetaken'
							, CONVERT(MONEY,compressed_backup_size/1024/1024/1024) [compressed_backup_size]
							, CONVERT(MONEY,backup_size/1024/1024/1024) [backup_size]
							, database_backup_lsn
							, RANK() OVER (PARTITION BY database_backup_lsn ORDER BY backup_start_date DESC) [IRank] 
						FROM  @sysdatabasesTable dbs
						--INNER JOIN @Databases D ON dbs.database_id = D.database_id
						LEFT OUTER JOIN  @backupset bs ON dbs.name = bs.database_name  
						AND dbs.recovery_model_desc COLLATE DATABASE_DEFAULT = bs.recovery_model COLLATE DATABASE_DEFAULT
						WHERE type IN ('I')
						--AND backup_start_date >  @StartFilterDate 
					) T
					WHERE [IRank] = 1
			) F
		) Final
		WHERE Timetaken 
		BETWEEN AverageTime - (@stdevmultiplier * stddevpopulation ) 
		AND AverageTime + (@stdevmultiplier * stddevpopulation )
	) summary ON dbs.name = summary.database_name
	LEFT OUTER JOIN
	(
		SELECT
			database_name = DB_NAME(database_id)
			, CONVERT(MONEY,(SUM(
				CASE 
					WHEN type_desc = 'LOG' THEN size 
				END) * 8. / 1024 / 1024)) [LOG_size_GB]
			, CONVERT(MONEY,(SUM(
				CASE 
					WHEN type_desc = 'ROWS' THEN size 
				END) * 8. / 1024 / 1024)) [DATA_size_GB]
			, CONVERT(MONEY,(SUM(size) * 8. / 1024 / 1024)) [TOTAL_size_GB]
		FROM @master_filesTable 
		GROUP BY database_id
	) [Size] ON Size.database_name = dbs.name
	LEFT OUTER JOIN
	(
		SELECT 
			[database_name]
			, SUM([compressed_backup_size])[compressed_backup_size]
			, SUM([backup_size])[backup_size]
		FROM 
		(
			SELECT  
				DB_NAME(dbs.database_id) [database_name]
				, type
				, AVG(CONVERT(MONEY,compressed_backup_size/1024/1024/1024)) [compressed_backup_size]
				, AVG(CONVERT(MONEY,backup_size/1024/1024/1024)) [backup_size]
			FROM  @sysdatabasesTable dbs
			INNER JOIN @Databases D ON dbs.database_id = D.database_id
			LEFT OUTER JOIN @backupset bs ON dbs.name = bs.database_name  
			AND dbs.recovery_model_desc COLLATE DATABASE_DEFAULT = bs.recovery_model COLLATE DATABASE_DEFAULT
			WHERE backup_start_date >  @StartFilterDate 
			GROUP BY DB_NAME(dbs.database_id) ,type
		)anotherone
		GROUP BY [database_name]
	) backupsize ON backupsize.database_name = dbs.name
	WHERE backup_start_date >  @StartFilterDate 
	GROUP BY dbs.name,dbs.[compatibility_level] , dbs.[recovery_model_desc],D.AGName, D.ReadSecondary, [CurrentLocation]
	) x 
	LEFT OUTER JOIN
		(
			SELECT  
				DB_NAME(dbs.database_id) [database_name]
				, MAX(CASE WHEN  type = 'D' THEN DATEDIFF(SECOND,backup_start_date, backup_finish_date) ELSE 0 END) 'Timetaken'
				, MAX(CASE WHEN  type = 'D' THEN backup_finish_date ELSE 0 END) 'Last Full'   
				, MIN(CASE WHEN  type = 'D' THEN backup_start_date ELSE 0 END) 'First Full'             
				, MAX(CASE WHEN  type = 'L' THEN backup_finish_date ELSE 0 END) 'Last Transaction Log'  
				, MIN(CASE WHEN  type = 'L' THEN backup_start_date ELSE 0 END) 'First Transaction Log'  
				, MAX(CASE WHEN  type = 'I' THEN backup_finish_date ELSE 0 END) 'Last Diff'  
				, MIN(CASE WHEN  type = 'I' THEN backup_start_date ELSE 0 END) 'First Diff'  
				, MAX(dbs.create_date) create_date
				FROM  @sysdatabasesTable dbs
				INNER JOIN @Databases D ON dbs.database_id = D.database_id
				LEFT OUTER JOIN  @backupset bs  ON dbs.name = bs.database_name  
				
				AND dbs.recovery_model_desc COLLATE DATABASE_DEFAULT = bs.recovery_model COLLATE DATABASE_DEFAULT
				/*Do not filter out only databases with backups.. some have never had.. --WHERE type IN ('D', 'L')*/
				GROUP BY dbs.database_id, dbs.[compatibility_level],dbs.recovery_model_desc
		) oldcheck ON oldcheck.database_name = x.database_name

	ORDER BY [Last Full] ASC
	OPTION (RECOMPILE);

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Recovery Model information matched with backups',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Check for disk space and latency on the server
			----------------------------------------*/

	DECLARE @fixeddrives TABLE
	(
		drive [NVARCHAR] (5)
		, FreeSpaceMB MONEY 
	)

	SET @dynamicSQL = 'EXEC xp_fixeddrives ';
	IF @IsSQLAzure = 0
	BEGIN
		INSERT @fixeddrives
		EXEC sp_executesql @dynamicSQL ;
	END

	/* more useful info
	SELECT * FROM [sys].dm_os_sys_info 
	EXEC xp_msver
	*/
	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT
	10
	, 'Disk Latency and Space'
	,'------'
	,'------';
/* Deprecated @ 09-04-2018 Adrian

	INSERT #output_sqldba_org_sp_triage (
		SectionID
		, Section
		, Summary
		, HoursToResolveWithTesting
	)

	SELECT 10, UPPER([Drive]) + '\ ' + REPLICATE('|',CASE WHEN (num_of_reads = 0 AND num_of_writes = 0) THEN 0 ELSE (io_stall/(num_of_reads + num_of_writes)) END) +' '+ CONVERT([VARCHAR](20), CASE WHEN (num_of_reads = 0 AND num_of_writes = 0) THEN 0 ELSE (io_stall/(num_of_reads + num_of_writes)) END) + ' ms' 
	, 'FreeSpace:'+ CONVERT([VARCHAR](20),[AvailableGBs]) + 'GB'
	+ '; Read:' + CONVERT([VARCHAR](20),CASE WHEN num_of_reads = 0 THEN 0 ELSE (io_stall_read_ms/num_of_reads) END )
	+ '; Write:' + CONVERT([VARCHAR](20), CASE WHEN io_stall_write_ms = 0 THEN 0 ELSE (io_stall_write_ms/num_of_writes) END )
	+ '; Total:' + CONVERT([VARCHAR](20), CASE WHEN (num_of_reads = 0 AND num_of_writes = 0) THEN 0 ELSE (io_stall/(num_of_reads + num_of_writes)) END) 
	+ ' (Latency in ms)'
	, CASE WHEN CASE WHEN (num_of_reads = 0 AND num_of_writes = 0) THEN 0 ELSE (io_stall/(num_of_reads + num_of_writes)) END > 25 THEN 0.4 * (io_stall/(num_of_reads + num_of_writes))/5  ELSE 0 END

	, CASE WHEN num_of_reads = 0 THEN 0 ELSE (num_of_bytes_read/num_of_reads) END AS [Avg Bytes/Read]
	, CASE WHEN io_stall_write_ms = 0 THEN 0 ELSE (num_of_bytes_written/num_of_writes) END AS [Avg Bytes/Write]
	, CASE WHEN (num_of_reads = 0 AND num_of_writes = 0) THEN 0 ELSE ((num_of_bytes_read + num_of_bytes_written)/(num_of_reads + num_of_writes)) END AS [Avg Bytes/Transfer]

	FROM (
	SELECT LEFT(mf.physical_name, 2) AS Drive
		, MAX(CAST(fd.FreeSpaceMB / 1024 as decimal(20,2))) [AvailableGBs]
		, SUM(num_of_reads) AS num_of_reads
		, SUM(io_stall_read_ms) AS io_stall_read_ms
		, SUM(num_of_writes) AS num_of_writes
		, SUM(io_stall_write_ms) AS io_stall_write_ms
		, SUM(num_of_bytes_read) AS num_of_bytes_read
		, SUM(num_of_bytes_written) AS num_of_bytes_written
		, SUM(io_stall) AS io_stall
		  FROM [sys].dm_io_virtual_file_stats(NULL, NULL) AS vfs
		  INNER JOIN [sys].master_files AS mf WITH (NOLOCK)
		  ON vfs.database_id = mf.database_id AND vfs.file_id = mf.file_id
		  INNER JOIN @fixeddrives fd ON fd.drive COLLATE DATABASE_DEFAULT = LEFT(mf.physical_name, 1) COLLATE DATABASE_DEFAULT
	  
		  GROUP BY LEFT(mf.physical_name, 2)) AS tab
	ORDER BY CASE WHEN (num_of_reads = 0 AND num_of_writes = 0) THEN 0 ELSE (io_stall/(num_of_reads + num_of_writes)) END OPTION (RECOMPILE);
	*/
	
	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	)
	SELECT 
	10
	, '[Drive]; [Latency (ms)];[PhysicalDailyIO_GB];[Details]'
	, '[READ latency (ms)]; [WRITE latency (ms)]'
	, '[FileName]; [Type]';
	
	BEGIN TRY
	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, Severity
	)
	SELECT 
		10
		, LEFT(mf.physical_name, 2) + '\ '
		+ ' ; ' + CONVERT([VARCHAR](250),SUM(io_stall)/SUM(num_of_reads+num_of_writes)) + ' (ms)'
		+ ' ; ' + CONVERT([VARCHAR](25),(CONVERT(MONEY,SUM([num_of_reads])) + SUM([num_of_writes])) * 8 /1024/1024/ CONVERT(MONEY,@DaysUptime))+ 'GB/day'
		+ '; Free space: ' + CONVERT([VARCHAR](20), MAX(CAST(fd.FreeSpaceMB / 1024 as decimal(20,2)))) + 'GB'
		, ' ; ' + 
		CASE 
			WHEN SUM(num_of_reads) = 0 THEN '0' 
			ELSE CONVERT([VARCHAR](25),SUM(io_stall_read_ms)/SUM(num_of_reads)) 
		END + ' (ms)'
			 
		+ ' ; ' + 
		CASE 
			WHEN SUM(num_of_writes) = 0 THEN '0' 
			ELSE CONVERT([VARCHAR](25),SUM(io_stall_write_ms)/SUM(num_of_writes)) 
		END + ' (ms)' 
		, CASE 
			WHEN SUM(num_of_reads+num_of_writes) = 0 THEN ''
			WHEN SUM(io_stall)/SUM(num_of_reads+num_of_writes) < 10 THEN @Result_Good
			WHEN SUM(io_stall)/SUM(num_of_reads+num_of_writes) BETWEEN 10 AND 100 THEN @Result_Warning
			WHEN SUM(io_stall)/SUM(num_of_reads+num_of_writes) > 100 THEN @Result_YourServerIsDead
			ELSE ''
		END 
	
		FROM [sys].dm_io_virtual_file_stats(NULL, NULL) AS vfs
		INNER JOIN @master_filesTable AS mf 
		ON vfs.database_id = mf.database_id AND vfs.file_id = mf.file_id
		INNER JOIN @fixeddrives fd ON fd.drive COLLATE DATABASE_DEFAULT = LEFT(mf.physical_name, 1) COLLATE DATABASE_DEFAULT
		
		GROUP BY LEFT(mf.physical_name, 2);

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, Severity
		, Details
	)
	SELECT
		10
		, LEFT ([f].[physical_name], 2) + '\ '
		+ '; ' + DB_NAME ([s].[database_id]) 
		+ '; '+ CONVERT([VARCHAR](20), CASE WHEN ([num_of_reads] = 0 AND [num_of_writes] = 0) THEN 0 ELSE ([io_stall] / ([num_of_reads] + [num_of_writes])) END) + ' (ms)'
		+ '; '+ CONVERT([VARCHAR](20),CASE WHEN [num_of_reads] + [num_of_writes] = 0 THEN 0 ELSE CONVERT(MONEY,([num_of_reads] + [num_of_writes])) * 8 /1024/1024/ CONVERT(MONEY,@DaysUptime) END ) + 'GB/day'
		, CONVERT([VARCHAR](20),CASE WHEN [num_of_reads] = 0 THEN 0 ELSE ([io_stall_read_ms] / [num_of_reads]) END ) + ' (ms)'
		+ '; '+CONVERT([VARCHAR](20),CASE WHEN [num_of_writes] = 0 THEN 0 ELSE ([io_stall_write_ms] / [num_of_writes]) END ) + ' (ms)'
		, CASE 
			WHEN (num_of_reads+num_of_writes) = 0 THEN ''
			WHEN (io_stall)/(num_of_reads+num_of_writes) < 10 THEN @Result_Good
			WHEN (io_stall)/(num_of_reads+num_of_writes) BETWEEN 10 AND 100 THEN @Result_Warning
			WHEN (io_stall)/(num_of_reads+num_of_writes) > 100 THEN @Result_YourServerIsDead
			ELSE ''
		END
		, [f].type_desc COLLATE DATABASE_DEFAULT
		+ '; '+ [f].[physical_name]  COLLATE DATABASE_DEFAULT

	FROM [sys].dm_io_virtual_file_stats (NULL,NULL) AS [s]
	JOIN @master_filesTable AS [f] ON [s].[database_id] = [f].[database_id] AND [s].[file_id] = [f].[file_id]
	ORDER BY [f].[database_id], [f].[file_id],LEFT ([f].[physical_name], 2);
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Disk latency', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE();
		IF @Debug = 1 RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	END CATCH
	
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Checked for disk latency and space',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Check for disk space on the server
			----------------------------------------*/


	/*How about diggin in some sybase, yes, the spt_values table*/
	SELECT @Kb = 1024.0;
	BEGIN TRY
		DECLARE @PageSizeTable TABLE 
		(
			PageSize MONEY
		);

		SET @dynamicSQL = 'DECLARE  @Kb MONEY = 1024.0;
		SELECT v.low/@Kb 
			FROM spt_values v 
			WHERE v.number=1 AND v.type=''E'';';

		INSERT @PageSizeTable
		EXEC sp_executesql @dynamicSQL ;

		SELECT @PageSize = PageSize
		FROM @PageSizeTable;

	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Page size from spt_values', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
		RAISERROR (N'Server does not seem to have spt_values table ',0,1) WITH NOWAIT;
		SET @PageSize = 8 /*as in 8 KB*/;
	END CATCH

	/* Guarded 2026-07-02: DBCC sqlperf(logspace) is unsupported on Azure SQL DB and was outside any
	   TRY/CATCH, so it aborted the batch there. Azure-gated + wrapped to degrade to an empty section. */
	IF @IsSQLAzure = 0
	BEGIN TRY
		INSERT @LogSpace
		EXEC sp_executesql N'DBCC sqlperf(logspace) WITH NO_INFOMSGS';

		INSERT #output_sqldba_org_sp_triage_LogSpace
		SELECT DatabaseName
			, LogSize
			, SpaceUsedPercent
			, Status
			, NULL
		FROM @LogSpace
		OPTION (RECOMPILE);
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'DBCC sqlperf(logspace)', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		RAISERROR (N'DBCC sqlperf(logspace) skipped/failed (expected on Azure SQL DB)',0,1) WITH NOWAIT;
	END CATCH

	DECLARE @SecondaryReadRole [NVARCHAR] (250);
	DECLARE @AGBackupPref [NVARCHAR] (250);
	DECLARE @AGCurrentLocation [NVARCHAR] (250);

	SET @Databasei_Count = 1; 
	WHILE @Databasei_Count <= @Databasei_Max 
	BEGIN 
		/* Reset before the SELECT: if this id's DB is offline/recovering (state 2/6) the row is
		   filtered out and a `SELECT @var = ...` with no matching row leaves the vars holding the
		   PRIOR iteration's DB, so the DBCC block below would run against the wrong database. */
		SELECT @DatabaseName = NULL, @DatabaseState = NULL, @AGBackupPref = NULL, @SecondaryReadRole = NULL, @VLFcount = NULL;
		/* @VLFcount too: a failed loginfo read must leave VLFCount NULL, never 0 or the previous database's count. */
		SELECT 
			@DatabaseName = d.databasename
			, @DatabaseState = d.state 
			, @AGBackupPref = BackupPref
			, @SecondaryReadRole = ReadSecondary
		FROM @Databases d 
		WHERE id = @Databasei_Count 
		AND d.state NOT IN (2,6);

		/* Non-AG databases arrive with BackupPref/ReadSecondary NULL (first UNION branch), so the bare <> was UNKNOWN and
		   this block never ran on a server without an AG: section 11 was empty. ISNULL keeps the AG 'primary' exclusion. */
		IF @DatabaseName IS NOT NULL
		AND EXISTS (SELECT 1 FROM #sp_triage_readable_db AS r WHERE r.name = @DatabaseName)
		AND (
			ISNULL(@SecondaryReadRole, N'') <> N'NO'
			AND ISNULL(@AGBackupPref, N'') <> N'primary'
		) 
		BEGIN
			/* Each read is isolated: a failure skips that read for this database only, reported at severity 0.
			   No ROLLBACK or XACT_STATE test in the CATCH: the proc opens no transaction (PROVED 2026-10-09). */
			BEGIN TRY
				SET @dynamicSQL = N'USE ' + QUOTENAME(@DatabaseName) + N';
			DBCC showfilestats WITH NO_INFOMSGS;';

				INSERT @FileStats
				EXEC sp_executesql @dynamicSQL;

				/* Same TRY as showfilestats: a data file with no @FileStats row would take the LOG's SpaceUsedPercent
				   fallback in section 11 (a wrong Used % graded Good; gate D1, PROVED 2026-10-09). If either read fails,
				   the database is left out of section 11 and the message below names it. */
				SET @dynamicSQL = N'USE ' + QUOTENAME(@DatabaseName) + N';
			SELECT N''' + REPLACE(@DatabaseName, N'''', N'''''') + N''', filename, size, ISNULL(FILEGROUP_NAME(groupid),''LOG''), [name] ,maxsize, growth  FROM dbo.sysfiles sf ; ';

				INSERT @FileSize
				EXEC sp_executesql @dynamicSQL;
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'File reads (DBCC showfilestats, sysfiles)' + ISNULL(N' ' + QUOTENAME(@DatabaseName), N''), N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SET @ReadSkippedMsg = N'sp_triage: file reads (DBCC showfilestats, sysfiles) skipped for [' + @DatabaseName + N'], left out of section 11: ' + CONVERT([NVARCHAR] (20), ERROR_NUMBER()) + N' ' + ERROR_MESSAGE();
				RAISERROR(N'%s', 0, 1, @ReadSkippedMsg) WITH NOWAIT;
			END CATCH

			SET @dynamicSQL = N'USE ' + QUOTENAME(@DatabaseName) + N';
			DBCC loginfo WITH NO_INFOMSGS;';

			IF @IsSQLAzure = 0
			BEGIN
				/* No SET IDENTITY_INSERT: the id column is not IDENTITY, so it raised Msg 8106 and aborted the run on any AG
				   server (guard: ShippedScriptIdentityInsertCensusTests). Explicit column list (DBCC gives 8 columns, 7 before
				   SQL 2012; the table also has id), built dynamically so pre-2012 never compiles a RecoveryUnitId reference. */
				SET @LogInfoInsert = N'INSERT #output_sqldba_org_sp_triage_dbccloginfo ('
					+ CASE WHEN @SQLVersion >= 11 THEN N'[RecoveryUnitId], ' ELSE N'' END
					+ N'[fileid], [file_size], [start_offset], [fseqno], [status], [parity], [create_lsn]) EXEC sp_executesql @LogInfoSQL;';
				BEGIN TRY
					EXEC sp_executesql @LogInfoInsert, N'@LogInfoSQL [NVARCHAR] (4000)', @LogInfoSQL = @dynamicSQL;

					SELECT @VLFcount = COUNT(*)
					FROM #output_sqldba_org_sp_triage_dbccloginfo ;
				END TRY
				BEGIN CATCH
					INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'VLF count (DBCC loginfo)' + ISNULL(N' ' + QUOTENAME(@DatabaseName), N''), N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
					SET @ReadSkippedMsg = N'sp_triage: VLF count (DBCC loginfo) skipped for [' + @DatabaseName + N']: ' + CONVERT([NVARCHAR] (20), ERROR_NUMBER()) + N' ' + ERROR_MESSAGE();
					RAISERROR(N'%s', 0, 1, @ReadSkippedMsg) WITH NOWAIT;
				END CATCH
			END

			DELETE 
			FROM #output_sqldba_org_sp_triage_dbccloginfo;

			UPDATE #output_sqldba_org_sp_triage_LogSpace 
			SET VLFCount =  @VLFcount 
			WHERE DatabaseName = @DatabaseName;
		END
		SET @Databasei_Count = @Databasei_Count + 1;
	END

	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 
	11
	, 'DATABASE FILES - Disk Usage Ordered by largest'
	,'------'
	,'------';

	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Severity]
		, [Details]
	)
	SELECT 
	11
	, REPLICATE('|',100-[FreeSpace %]) + REPLICATE('.',[FreeSpace %]) +' ('+ CONVERT([VARCHAR](20),CONVERT(INT,ROUND(100-[FreeSpace %],0))) + '% of ' + CONVERT([VARCHAR](20),CONVERT(MONEY,FileSize/1024)) + ')'
	, (
		+ 'DB size:'
		+ CONVERT([VARCHAR](20),CONVERT(MONEY,TotalSize/1024))
		+ ' GB; DB:'
		+ DatabaseName 
		+ '; SizeGB:'
		+ CONVERT([VARCHAR](20),CONVERT(MONEY,FileSize/1024))
		+ '; Growth:'
		+ CASE WHEN growth <= 100 THEN CONVERT([VARCHAR](20),growth) + '%' ELSE CONVERT([VARCHAR](20),growth/128) + 'MB' END 
	)
	, CASE WHEN [FreeSpace %] < 5 THEN @Result_ReallyBad WHEN [FreeSpace %] < 10 THEN @Result_Warning ELSE @Result_Good END
	, (
		UPPER(DriveLetter)
		+ ' FG:'
		+ FileGroupName 
		+ CASE WHEN FileGroupName = 'LOG' THEN '(' + ISNULL(CONVERT([VARCHAR](20),VLFCount),'?') + 'vlfs)' ELSE '' END /* '?' = not read */
		--, LogicalName  
		+ '; MAX:'
		+ CONVERT([VARCHAR](20),maxsize)
		+ '; Used:' 
		+ CONVERT([VARCHAR](20),100-[FreeSpace %] )
		+ '%'
		+ '; Path:'
		+ [FileName] 
	)
	FROM 
	(
		SELECT
			DatabaseName = fsi.DatabaseName
			, fs2.TotalSize
			, FileGroupName = fsi.FileGroupName
			, maxsize
			, growth
			, LogicalName = RTRIM(fsi.LogicalName)
			, [FileName] = RTRIM(fsi.FileName)
			, DriveLetter = LEFT(RTRIM(fsi.FileName),2)
			, FileSize = CAST(fsi.FileSize*@PageSize/@Kb as decimal(15,2))
			, UsedSpace = CAST(ISNULL((fs.UsedExtents*@PageSize*8.0/@Kb), fsi.FileSize*@PageSize/@Kb * ls.SpaceUsedPercent/100.0) as MONEY )
			, FreeSpace = CAST(ISNULL(((fsi.FileSize - UsedExtents*8.0)*@PageSize/@Kb), (100.0-ls.SpaceUsedPercent)/100.0 * fsi.FileSize*@PageSize/@Kb) as MONEY )
			, [FreeSpace %] = CAST(ISNULL(((fsi.FileSize - UsedExtents*8.0) / fsi.FileSize * 100.0), 100-ls.SpaceUsedPercent) as MONEY ) 
			, VLFCount 
			FROM @FileSize fsi  
			LEFT JOIN @FileStats fs ON fs.FileName = fsi.FileName  
			LEFT JOIN #output_sqldba_org_sp_triage_LogSpace ls ON ls.DatabaseName COLLATE DATABASE_DEFAULT = fsi.DatabaseName   COLLATE DATABASE_DEFAULT
			LEFT OUTER JOIN  (SELECT DatabaseName, SUM(CAST(FileSize*@PageSize/@Kb as decimal(15,2))) TotalSize FROM @FileSize F1 GROUP BY DatabaseName) fs2 ON  fs2.DatabaseName COLLATE DATABASE_DEFAULT =  fsi.DatabaseName COLLATE DATABASE_DEFAULT
	) T1
	WHERE T1.[FreeSpace %] < (CASE WHEN @ShowWarnings = 1 THEN 20 ELSE 100 END)
	ORDER BY TotalSize DESC, DatabaseName ASC, FileSize DESC
	OPTION (RECOMPILE);
	
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Checked free space',0,1) WITH NOWAIT;
	END


			/*----------------------------------------
			--Look at caching plans,  size matters here
			----------------------------------------*/
	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 
	12
	, 'CACHING PLANS - as % of total memory used by SQL'
	, '------'
	, '------';

	BEGIN TRY
	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	)
	SELECT 
	12
	, REPLICATE('|',[1 use size]/[Size MB]*100) + REPLICATE('.',100- [1 use size]/[Size MB]*100) +' '+ CONVERT([VARCHAR](20),CONVERT(INT,[1 use size]/[Size MB]*100)) +'% of '
	+ CONVERT([VARCHAR](20),CONVERT([BIGINT],[Size MB])) +'MB is 1 use' 
	, objtype 
	+ '; Plans:'+ CONVERT([VARCHAR](20),[Total Use])
	+ '; Total Refs:'+ CONVERT([VARCHAR](20),[Total Rfs])
	+ '; Avg Use:'+ CONVERT([VARCHAR](20),[Avg Use])
	, CONVERT([VARCHAR](20),[Size MB]) + 'MB'
	+ '; Single use:'+ CONVERT([VARCHAR](20),[1 use size]*100/[Size MB]) + '%'
	+ '; Single plans:'+ CONVERT([VARCHAR](20),[1 use count])

	FROM 
	(
		SELECT objtype
			, SUM(refcounts)[Total Rfs]
			, AVG(refcounts) [Avg Refs]
			, SUM(cast(usecounts as [BIGINT])) [Total Use]
			, AVG(cast(usecounts as [BIGINT])) [Avg Use]
			, CONVERT(MONEY,SUM(size_in_bytes*0.000000953613)) [Size MB]
			, SUM(CASE WHEN usecounts = 1 THEN 1 ELSE 0 END) [1 use count]
			, SUM(CASE WHEN usecounts = 1 THEN CONVERT(MONEY,size_in_bytes*0.000000953613) ELSE 0 END) [1 use size]
		FROM [sys].dm_exec_cached_plans 
		GROUP BY objtype
	) TCP
	OPTION (RECOMPILE);
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Cached plans', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE();
		IF @Debug = 1 RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Got cached plan statistics',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Get the top 10 query plan bloaters for single use queries
			----------------------------------------*/

	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 
	13
	,'CACHING PLANS - TOP 10 single use plans'
	,'------'
	,'------';

	BEGIN TRY
	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	)
	SELECT 
	TOP(10) 
	13
	, CONVERT([VARCHAR](20),CONVERT(MONEY,cp.size_in_bytes)/1024) + 'KB'
	, cp.cacheobjtype
	+ ' '+ cp.objtype
	+ '; SizeMB:' + CONVERT([VARCHAR](20),CONVERT(MONEY,cp.size_in_bytes)/1024/1000)
	, ''AS [QueryText]
	/*Need to become more clever to do this bit
	replace(replace(replace(replace(LEFT(CONVERT([NVARCHAR] (4000),[text]),@LeftText), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ') */
	FROM [sys].dm_exec_cached_plans AS cp WITH (NOLOCK)
	/* perf 2026-07-02: removed dead CROSS APPLY dm_exec_sql_text(plan_handle) - its text was never used (QueryText='') */
	WHERE cp.cacheobjtype = N'Compiled Plan' 
	AND cp.objtype IN (N'Adhoc', N'Prepared') 
	AND cp.usecounts = 1
	ORDER BY cp.size_in_bytes DESC 
	OPTION (RECOMPILE);
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Single-use plans', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE();
		IF @Debug = 1 RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Got cached plan statistics - Biggest single use plans',0,1) WITH NOWAIT;
	END

	/* 2026-10-16 (lane bundle-sp-triage-fixes-a, member 5). Invariant: every sys.dm_os_buffer_descriptors read sits
	   behind ONE @BufferScanMaxGB gate, computed once from one perf-counter read placed above its first use. That DMV
	   enumerates EVERY buffer-pool page (minutes on big-memory servers), so it is read ONCE, here, and only when the
	   pool holds at most @BufferScanMaxGB of data pages. Section 14 and the migration section's usage-likelihood
	   estimate both read @bufferPagesPerDB. Past the gate, or with the pool size unknown, nothing is scanned. */
	DECLARE @BufferPoolPagesMB BIGINT;
	DECLARE @bufferPagesPerDB TABLE (database_id INT PRIMARY KEY, db_buffer_pages BIGINT);
	BEGIN TRY
		SELECT @BufferPoolPagesMB = cntr_value*8/1024
		FROM [sys].dm_os_performance_counters
		WHERE [counter_name] = 'Database pages'
		AND [object_name] LIKE '%Buffer Manager%';
		IF @BufferPoolPagesMB IS NOT NULL /*fail CLOSED: unknown pool size (broken perf counters, a real production state) must not trigger the scan*/
			AND @BufferPoolPagesMB <= CONVERT(BIGINT, ISNULL(@BufferScanMaxGB,0)) * 1024 /*BIGINT: INT*1024 overflows (error 8115) past @BufferScanMaxGB=2097151*/
			AND ISNULL(@BufferScanMaxGB,0) > 0
		BEGIN
			INSERT @bufferPagesPerDB
			SELECT database_id, COUNT_BIG(*)
			FROM [sys].dm_os_buffer_descriptors
			WHERE database_id IS NOT NULL /*metadata-nullable; a NULL row would violate the PK*/
			GROUP BY database_id;
		END
		ELSE
			RAISERROR (N'Buffer-pool residency scan skipped (@BufferScanMaxGB gate: pool too large, size unknown, or scan disabled); Pages(MB) in memory reported as 0',0,1) WITH NOWAIT;
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Buffer-pool residency scan', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	END CATCH

			/*----------------------------------------
			--Find cpu load, io and memory per DB
			----------------------------------------*/
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Reading buffer pages takes longer on higher memory servers',0,1) WITH NOWAIT;
	END
	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 
	14
	, 'Database: CPU IO Memory DISK DiskIO Latency'
	, '------'
	, '------';

	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 
	14
	, 'Breakdown'
	, 'DBName; CPU; IO; Buffer; DiskUsage(GB); Disk IO daily (GB); Latency (ms)'
	, 'CPU time(s); Total IO; Buffer Pages; Buffer MB'
	
	BEGIN TRY
	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 
		14
		,  REPLICATE('|',CONVERT(MONEY,T2.[TotalIO])/ SUM(T2.[TotalIO]) OVER()* 100.0) 
		+ REPLICATE('.',100 - CONVERT(MONEY,T2.[TotalIO])/ SUM(T2.[TotalIO]) OVER()* 100.0) + '' + CONVERT([VARCHAR](20), CONVERT(INT,ROUND(CONVERT(MONEY,T2.[TotalIO])/ SUM(T2.[TotalIO]) OVER()* 100.0,0))) +'% IO '
		, T2.DatabaseName
		+ '; ' + ISNULL(CONVERT([VARCHAR](20),CONVERT(INT,ROUND([CPU_Time(Ms)]/1000 * 1.0 /SUM([CPU_Time(Ms)]/1000) OVER()* 100.0,0))),'0') +'%'
		+ '; ' +  ISNULL(CONVERT([VARCHAR](20),CONVERT(INT,ROUND(CONVERT(MONEY,T2.[TotalIO])/ SUM(T2.[TotalIO]) OVER()* 100.0 ,0))) ,'0')+'%'
		+ '; ' +  ISNULL(CONVERT([VARCHAR](20),CONVERT(INT,ROUND(CONVERT(MONEY,src.db_buffer_pages )/ SUM(src.db_buffer_pages ) OVER()* 100.0 ,0))),'0')+'%'
		+ '; ' +  + ISNULL(CONVERT([VARCHAR](20),CONVERT(MONEY,TotalSize/1024)),'')
		+ '; ' + ISNULL(DBlatency.[GB/day] +'(GB)','')
		+ '; ' + ISNULL(DBlatency.[Latency],'')
		,  ISNULL(CONVERT([VARCHAR](20),[CPU_Time(Ms)]) + ' (' + CONVERT([VARCHAR](20),CAST([CPU_Time(Ms)]/1000 * 1.0 /SUM([CPU_Time(Ms)]/1000) OVER()* 100.0 AS DECIMAL(5, 2))) + '%)','') 
		+ '; ' +  ISNULL(CONVERT([VARCHAR](20),[TotalIO]) + ' ; Reads: ' + CONVERT([VARCHAR](20),T2.[Number of Reads]) +' ; Writes: '+ CONVERT([VARCHAR](20),T2.[Number of Writes]),'')
		+ '; ' +  ISNULL(CONVERT([VARCHAR](20),src.db_buffer_pages),'')
		+ '; '+ ISNULL(CONVERT([VARCHAR](20),src.db_buffer_pages / 128) ,'')

	FROM
	(
	SELECT
		name AS 'DatabaseName'
		, d.database_id
		, SUM(num_of_reads) AS'Number of Reads'
		, SUM(num_of_writes) AS'Number of Writes'
		, SUM(num_of_writes) +  SUM(num_of_reads) [TotalIO]
		FROM [sys].dm_io_virtual_file_stats(NULL,NULL) I
		INNER JOIN @sysdatabasesTable d ON I.database_id = d.database_id
		GROUP BY name, d.database_id
	) T2
	LEFT OUTER JOIN 
	(
		SELECT 
		TOP 100 PERCENT
			DatabaseID
			, DB_Name(DatabaseID)AS [DatabaseName]
			, SUM(total_worker_time)AS [CPU_Time(Ms)]
		FROM  #dadatafor_exec_query_stats AS qs
		CROSS APPLY
		(
			SELECT 
				CONVERT(int, value)AS [DatabaseID]
			FROM [sys].dm_exec_plan_attributes(qs.plan_handle)
			WHERE attribute =N'dbid'
		)AS epa
		GROUP BY DatabaseID
		ORDER BY SUM(total_worker_time) DESC
	) T1 ON T1.DatabaseName = T2.DatabaseName
	LEFT OUTER JOIN 
	(
		SELECT 
			database_id
			, db_buffer_pages
		FROM @bufferPagesPerDB /*the ONE gated buffer-descriptor read above (member 5)*/
	) src ON src.database_id = T2.database_id
	LEFT OUTER JOIN  
	(
		SELECT 
			DatabaseName
			, SUM(CAST(FileSize*@PageSize/@Kb as decimal(15,2))) TotalSize 
		FROM @FileSize F1 
		GROUP BY DatabaseName
	) fs2 ON  fs2.DatabaseName COLLATE DATABASE_DEFAULT =  T2.DatabaseName COLLATE DATABASE_DEFAULT
	
	LEFT OUTER JOIN 
	(
		SELECT  
			DB_NAME ([s].[database_id]) [DBName] 
			, CONVERT([VARCHAR](20), 
			CASE 
				WHEN (SUM([num_of_reads]) = 0 AND SUM([num_of_writes]) = 0) THEN 0 
				ELSE (SUM([io_stall]) / (SUM([num_of_reads]) + SUM([num_of_writes]))) 
			END) + ' (ms)' [Latency]
			, CONVERT([VARCHAR](20),
			CASE 
				WHEN SUM([num_of_reads]) + SUM([num_of_writes]) = 0 THEN 0 
				ELSE CONVERT(MONEY,(SUM([num_of_reads]) + SUM([num_of_writes]))) * 8 /1024/1024/ CONVERT(MONEY,@DaysUptime) 
			END ) [GB/day]
			, CONVERT([VARCHAR](20),
			CASE 
				WHEN SUM([num_of_reads]) + SUM([num_of_writes]) = 0 THEN 0 
				ELSE CONVERT(MONEY,(SUM(CONVERT(MONEY,[num_of_reads])) + SUM([num_of_writes]))) 
			END) [DBTotalIO]
		FROM [sys].dm_io_virtual_file_stats (NULL,NULL) AS [s]
		JOIN @master_filesTable AS [f] ON [s].[database_id] = [f].[database_id] AND [s].[file_id] = [f].[file_id]
		GROUP BY DB_NAME ([s].[database_id]) 
	) DBlatency ON DBlatency.DBName =  T2.DatabaseName
	WHERE T2.DatabaseName IS NOT NULL
	ORDER BY [TotalIO] DESC,[CPU_Time(Ms)] DESC
	OPTION (RECOMPILE) ;
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Database resource use', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE();
		IF @Debug = 1 RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Checked CPU, IO  and memory usage',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Get to wait types, the TOP 10 would be good for now
			----------------------------------------*/

	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 
	15
	, 'TOP 15 WAIT STATS'
	,'------'
	,'------';
	
	--INSERT @Waits 
	BEGIN TRY
	INSERT #output_sqldba_org_sp_triage (SectionID, Section,Summary,Severity,HoursToResolveWithTesting )
	SELECT TOP 15 15,
	REPLICATE ('|', 100.0 * [wait_time_ms] / SUM ([wait_time_ms]) OVER())+ REPLICATE ('''', 100- 100.0 * [wait_time_ms] / SUM ([wait_time_ms]) OVER()) + CONVERT([VARCHAR](20), CONVERT(INT,ROUND(100.0 * [wait_time_ms] / SUM ([wait_time_ms]) OVER(),0))) + '%'
	, S.[wait_type] + ':' 
	+ ';HH:' + CONVERT([VARCHAR](20),CONVERT(MONEY,SUM(wait_time_ms / 1000.0 / 60 / 60) OVER (PARTITION BY S.[wait_type])))
	+ ':MM/HH/VCPU:' + CONVERT([VARCHAR](20),CONVERT(MONEY,SUM(60.0 * wait_time_ms) OVER (PARTITION BY S.[wait_type]) / @minutesSinceRestart /60000/@CPUcount))
	+'; Wait(s):'+ CONVERT([VARCHAR](20),CONVERT([BIGINT],[wait_time_ms] / 1000.0)) + '(s)'
	+'; Wait count:' + CONVERT([VARCHAR](20),[waiting_tasks_count])
	+'; AVGTime:' + CONVERT([VARCHAR](20),CONVERT(MONEY,[wait_time_ms] / 1000.0)/[waiting_tasks_count])
	, CASE 
		WHEN CONVERT(MONEY,SUM(60.0 * wait_time_ms) OVER (PARTITION BY S.[wait_type]) / @minutesSinceRestart /60000/@CPUcount) BETWEEN 10 AND 30 THEN @Result_Warning
		WHEN CONVERT(MONEY,SUM(60.0 * wait_time_ms) OVER (PARTITION BY S.[wait_type]) / @minutesSinceRestart /60000/@CPUcount) > 30 THEN  @Result_YourServerIsDead
		WHEN S.[wait_type] LIKE 'PREEMPTIVE%' THEN @Result_YourServerIsDead
		WHEN S.[wait_type] LIKE 'LATCH%' THEN @Result_YourServerIsDead
		WHEN I.[Weight] <= 2 THEN @Result_Warning
		WHEN I.[Weight] > 2 THEN @Result_YourServerIsDead
		ELSE @Result_Good 
	END
	, CASE /*1 - 5*/
		WHEN S.[wait_type] LIKE 'PREEMPTIVE%' THEN 5
		WHEN S.[wait_type] LIKE 'LATCH%' THEN 3
		WHEN I.[Weight] > 0 THEN I.[Weight]
		ELSE 0
	END


	FROM [sys].dm_os_wait_stats S
	LEFT OUTER JOIN #output_sqldba_org_sp_triage_ImportantWaits I ON I.wait_type COLLATE DATABASE_DEFAULT = S.[wait_type] COLLATE DATABASE_DEFAULT
	LEFT OUTER JOIN #output_sqldba_org_sp_triage_IgnorableWaits W ON W.wait_type COLLATE DATABASE_DEFAULT = S.[wait_type] COLLATE DATABASE_DEFAULT
	WHERE  W.wait_type IS NULL
	AND [waiting_tasks_count] > 0
	ORDER BY [wait_time_ms] DESC
	OPTION (RECOMPILE)
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Wait statistics', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE();
		IF @Debug = 1 RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Filtered wait stats have been prepared',0,1) WITH NOWAIT;
	END

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Looking at query stats.. this might take a wee while',0,1) WITH NOWAIT;
	END
			/*----------------------------------------
			--Look at Plan Cache and DMV to find missing index impacts
			----------------------------------------*/

	INSERT #output_sqldba_org_sp_triage_querystats
	(
		RankIOTime
		, [execution_count]
		, [total_logical_reads] 
		, [Total_MBsRead] 
		, [total_logical_writes] 
		, [Total_MBsWrite] 
		, [total_worker_time] 
		, [total_elapsed_time_in_S]
		, [total_elapsed_time]
		, [last_execution_time] 
		, [plan_handle]
		, [sql_handle]
	)
		SELECT 
		TOP 100 PERCENT
			(RANK() OVER(ORDER BY(qs.total_logical_writes + qs.total_logical_reads)) 
			+ RANK() OVER(ORDER BY qs.total_elapsed_time DESC) )/2 [RankIOTime]
			, qs.execution_count
			, qs.total_logical_reads
			, CONVERT(MONEY,qs.total_logical_reads)/1000 [Total_MBsRead]
			, qs.total_logical_writes
			, CONVERT(MONEY,qs.total_logical_writes)/1000 [Total_MBsWrite]
			, qs.total_worker_time,  CONVERT(MONEY,qs.total_elapsed_time)/1000000 total_elapsed_time_in_S
			, qs.total_elapsed_time
			, qs.last_execution_time
			, qs.plan_handle
			, qs.sql_handle
			FROM #dadatafor_exec_query_stats qs WITH (NOLOCK)
			WHERE  CONVERT(MONEY,qs.total_logical_writes + qs.total_logical_reads)/1024 > 500 /*500MB total activity*/
			/* Change order by ORDER BY [RankIOTime] ASC*/
			ORDER BY total_elapsed_time/execution_count DESC
	/* Plan probe (lane bundle-sp-triage-fixes-b, member 1). A plan compiled in a readable database (often master) can name an
	   OFFLINE or SINGLE_USER database by three-part name; opening that plan raises 942 or 924, and the error ends the whole
	   section that APPLYs it. So, only when some database cannot be read, each plan the plan-reading sections use
	   (#output_sqldba_org_sp_triage_querystats, Id <= @TopQueries) is opened ALONE in its own TRY. A plan that raises leaves
	   #sp_triage_readable_plan, and the database its error names (942 and 924 quote it) joins the ONE skip row below.
	   Census: SpTriageDatabaseStateGuardCensusTests (the handle variables are assigned only from the guard). */
	IF EXISTS (SELECT 1 FROM sys.databases AS d WHERE d.database_id NOT IN (SELECT r.database_id FROM #sp_triage_readable_db AS r))
	BEGIN
		DECLARE @sp_triage_probe TABLE (rn INT IDENTITY(1,1) PRIMARY KEY, plan_handle VARBINARY(64) NOT NULL, sql_handle VARBINARY(64) NOT NULL);
		DECLARE @sp_triage_probe_rn INT, @sp_triage_probe_max INT, @sp_triage_probe_n BIGINT;
		DECLARE @sp_triage_probe_ph VARBINARY(64), @sp_triage_probe_sh VARBINARY(64);
		INSERT @sp_triage_probe (plan_handle, sql_handle)
		SELECT qs.plan_handle, MIN(qs.sql_handle)
		FROM #output_sqldba_org_sp_triage_querystats AS qs
		WHERE qs.Id <= @TopQueries
		AND qs.plan_handle IN (SELECT rp.plan_handle FROM #sp_triage_readable_plan AS rp)
		GROUP BY qs.plan_handle;
		SELECT @sp_triage_probe_rn = 1, @sp_triage_probe_max = COUNT(1) FROM @sp_triage_probe;
		WHILE @sp_triage_probe_rn <= @sp_triage_probe_max
		BEGIN
			SELECT @sp_triage_probe_ph = NULL, @sp_triage_probe_sh = NULL;
			SELECT @sp_triage_probe_ph = rp.plan_handle, @sp_triage_probe_sh = p.sql_handle
			FROM @sp_triage_probe AS p
			INNER JOIN #sp_triage_readable_plan AS rp ON rp.plan_handle = p.plan_handle
			WHERE p.rn = @sp_triage_probe_rn;
			IF @sp_triage_probe_ph IS NOT NULL
			BEGIN TRY
				SELECT @sp_triage_probe_n = DATALENGTH(qp.query_plan) FROM [sys].dm_exec_query_plan(@sp_triage_probe_ph) AS qp;
				SELECT @sp_triage_probe_n = DATALENGTH(st.text) FROM [sys].dm_exec_sql_text(@sp_triage_probe_sh) AS st;
			END TRY
			BEGIN CATCH
				/* One skip row per error number, and only when the error names no database that cannot be read. */
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage)
				SELECT N'Readable query plan list', ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000)
				WHERE NOT EXISTS (SELECT 1 FROM sys.databases AS d
					WHERE d.database_id NOT IN (SELECT r.database_id FROM #sp_triage_readable_db AS r)
					AND CHARINDEX(N'''' + d.name + N'''', ISNULL(ERROR_MESSAGE(), N'')) > 0)
				AND NOT EXISTS (SELECT 1 FROM #sp_triage_skipped AS s WHERE s.Step = N'Readable query plan list' AND s.ErrorNumber = ISNULL(ERROR_NUMBER(), 0));
				DELETE FROM #sp_triage_readable_plan WHERE plan_handle = @sp_triage_probe_ph;
				INSERT @sp_triage_unreadable_db (name)
				SELECT d.name
				FROM sys.databases AS d
				WHERE d.database_id NOT IN (SELECT r.database_id FROM #sp_triage_readable_db AS r)
				AND CHARINDEX(N'''' + d.name + N'''', ISNULL(ERROR_MESSAGE(), N'')) > 0
				AND d.name NOT IN (SELECT u.name FROM @sp_triage_unreadable_db AS u);
			END CATCH
			SET @sp_triage_probe_rn = @sp_triage_probe_rn + 1;
		END
	END
	/* The ONE skip row for plans of databases that cannot be read: the guard's plan-dbid list plus the probe's names. */
	SELECT @sp_triage_unreadable = STUFF((
		SELECT N', ' + QUOTENAME(u.name)
		FROM @sp_triage_unreadable_db AS u
		ORDER BY u.name
		FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, N'');
	IF @sp_triage_unreadable IS NOT NULL
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage)
		VALUES (N'Query plans of databases that cannot be read', 0, LEFT(N'left out of the query sections: ' + @sp_triage_unreadable, 4000));

	BEGIN TRY

	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		, QueryPlan
	) 
	SELECT 
	16
	, 'PLAN INSIGHT - MISSING INDEX'
	,'------'
	,'------'
	,NULL

	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		, QueryPlan,HoursToResolveWithTesting
	)
	SELECT 
	16
	, REPLICATE('|',TFF.[SecondsSavedPerDay]/28800*100) + ' $' + CONVERT([VARCHAR](20),CONVERT(MONEY,TFF.[SecondsSavedPerDay]/28800) * @FTECost) + 'pa ('+CONVERT([VARCHAR](20),CONVERT(MONEY,TFF.[SecondsSavedPerDay]/28800) )+ 'FTE)' [Section]
	,CONVERT([VARCHAR](20),TFF.execution_count) + ' executions'
		+ '; Cost:' + CONVERT([VARCHAR](20),TFF.SubTreeCost)
		+ '; GuessingCost(s):' + CONVERT([VARCHAR](20),(ISNULL(TFF.SubTreeCost * TFF.execution_count * (100-TFF.impact),0)))
		+ '(@secondsperoperator) ' + CONVERT([VARCHAR](20),ISNULL(@secondsperoperator,0)) +'; Impact:' +CONVERT([VARCHAR](20), TFF.impact)
		+ '; EstRows:' + CONVERT([VARCHAR](20),TFF.estRows)
		+ '; Magic:' + CONVERT([VARCHAR](20),TFF.Magic)
		+ '; ' + CONVERT([VARCHAR](20), TFF.SecondsSavedPerDay) + '(s)'
		+ '; Total time:' + CONVERT([VARCHAR](20),TFF.total_elapsed_time/1000/1000) + '(s)'  AS [Summary]
	, ';'+TFF.[statement] 
		+ ISNULL(':EQ:'+ TFF.equality_columns,'')
		+ ISNULL(':INEQ:'+ TFF.inequality_columns,'')
		+ ISNULL(':INC:'+ TFF.include_columns,'')  AS [Details]
		, tp.query_plan
		, CONVERT([VARCHAR](20),CONVERT(MONEY,TFF.[SecondsSavedPerDay]/28800 * 8 * 3))
		FROM 
		(
			SELECT 
			SUM(ISNULL(TF.SubTreeCost,0))  AS SubTreeCost
			, SUM(CONVERT(FLOAT,ISNULL(TF.estRows,0) ))  AS estRows
			, SUM(ISNULL([Magic],0))  AS [Magic]
			, SUM(ISNULL(TF.impact,0)/100 * ISNULL(TF.total_elapsed_time,0) )/1000000/@DaysOldestCachedQuery   AS [SecondsSavedPerDay]
			, ISNULL(TF.impact	,0)  AS [impact]
			, ISNULL(TF.execution_count	,0) AS [execution_count]
			, ISNULL(TF.total_elapsed_time,0)  AS [total_elapsed_time]	
			, ISNULL(TF.database_id,'')	 AS [database_id]
			, ISNULL(TF.OBJECT_ID,'')	 AS [OBJECT_ID]
			, ISNULL(TF.statement	,'')  AS [statement]
			, ISNULL(TF.equality_columns,'') AS [equality_columns]	
			, ISNULL(TF.inequality_columns,'')	 AS [inequality_columns]
			, ISNULL(TF.include_columns,'')  AS [include_columns]
			, TF.plan_handle
			FROM
			(
				SELECT 
				--, query_plan
				--, n.value('(@StatementText)[1]', '[VARCHAR](4000)') AS sql_text
				CONVERT(FLOAT,n.value('(@StatementSubTreeCost)', '[VARCHAR](40)')) AS SubTreeCost
				, n.value('(@StatementEstRows)', '[VARCHAR](40)') AS estRows
				, CONVERT(FLOAT,n.value('(//MissingIndexGroup/@Impact)[1]', 'FLOAT')) AS impact
				, tab.execution_count
				, tab.total_elapsed_time
				, tab.plan_handle
				, DB_ID(REPLACE(REPLACE(n.value('(//MissingIndex/@Database)[1]', '[VARCHAR](128)'),'[',''),']','')) AS database_id
				, CASE WHEN DB_ID(PARSENAME(n.value('(//MissingIndex/@Database)[1]', '[VARCHAR](128)'), 1)) IN (SELECT r.database_id FROM #sp_triage_readable_db AS r)
				  THEN OBJECT_ID(n.value('(//MissingIndex/@Database)[1]', '[VARCHAR](128)') + '.' + 
				   n.value('(//MissingIndex/@Schema)[1]', '[VARCHAR](128)') + '.' + 
				   n.value('(//MissingIndex/@Table)[1]', '[VARCHAR](128)')) END AS OBJECT_ID, /* OBJECT_ID opens the named database */
			   	   n.value('(//MissingIndex/@Database)[1]', '[VARCHAR](128)') + '.' + 
				   n.value('(//MissingIndex/@Schema)[1]', '[VARCHAR](128)') + '.' + 
				   n.value('(//MissingIndex/@Table)[1]', '[VARCHAR](128)')  
			   AS statement, 
			   (   SELECT DISTINCT c.value('(@Name)[1]', '[VARCHAR](128)') + ', ' 
				   FROM n.nodes('//ColumnGroup') AS t(cg) 
				   CROSS APPLY cg.nodes('Column') AS r(c) 
				   WHERE cg.value('(@Usage)[1]', '[VARCHAR](128)') = 'EQUALITY' 
				   FOR  XML PATH('') 
			   ) AS equality_columns, 
				(  SELECT DISTINCT c.value('(@Name)[1]', '[VARCHAR](128)') + ', ' 
				   FROM n.nodes('//ColumnGroup') AS t(cg) 
				   CROSS APPLY cg.nodes('Column') AS r(c) 
				   WHERE cg.value('(@Usage)[1]', '[VARCHAR](128)') = 'INEQUALITY' 
				   FOR  XML PATH('') 
			   ) AS inequality_columns, 
			   (   SELECT DISTINCT c.value('(@Name)[1]', '[VARCHAR](128)') + ', ' 
				   FROM n.nodes('//ColumnGroup') AS t(cg) 
				   CROSS APPLY cg.nodes('Column') AS r(c) 
				   WHERE cg.value('(@Usage)[1]', '[VARCHAR](128)') = 'INCLUDE' 
				   FOR  XML PATH('') 
			   ) AS include_columns 

		FROM  
		( 
		   SELECT query_plan
		   , qs.*
		   FROM 
		   (    
				SELECT plan_handle
					,SUM(qs.execution_count) AS execution_count
					,MAX(qs.total_elapsed_time) AS total_elapsed_time
				 FROM #output_sqldba_org_sp_triage_querystats qs WITH(NOLOCK)
				 WHERE qs.Id <= @TopQueries  
				 GROUP BY  plan_handle
				 HAVING SUM(qs.total_elapsed_time ) > @MinWorkerTime
			) AS qs 
			OUTER APPLY [sys].dm_exec_query_plan(qs.plan_handle) tp  
			WHERE tp.query_plan.exist('//MissingIndex')=1 
			AND qs.plan_handle IN (SELECT rp.plan_handle FROM #sp_triage_readable_plan AS rp)
		) AS tab 
		CROSS APPLY query_plan.nodes('//StmtSimple') AS q(n) 
		) TF
		INNER JOIN @Databases d ON d.database_id = TF.database_id
		LEFT OUTER JOIN 
		(
			SELECT  
				(( (ISNULL(user_seeks,0) + ISNULL(user_scans,0 )) * avg_total_user_cost * avg_user_impact)/1) [Magic] /*fixed precedence: (seeks+scans) must be multiplied by cost*impact, not just scans*/
				, user_seeks 
				, user_scans
				, user_seeks + user_scans AllScans
				, avg_total_user_cost
				, avg_user_impact
				, mid.object_id
				, [statement]
				, equality_columns
				, inequality_columns
				, included_columns
			FROM [sys].dm_db_missing_index_group_stats AS migs 
			INNER JOIN [sys].dm_db_missing_index_groups AS mig ON migs.group_handle = mig.index_group_handle 
			INNER JOIN [sys].dm_db_missing_index_details AS mid ON mig.index_handle = mid.index_handle 
			LEFT OUTER JOIN [sys].objects WITH (nolock) ON mid.OBJECT_ID = [sys].objects.OBJECT_ID 
		) TStats ON TStats.object_id = TF.OBJECT_ID
		AND TStats.statement = TF.statement
		AND ISNULL(TStats.equality_columns +', ',0) COLLATE DATABASE_DEFAULT = ISNULL(TF.equality_columns ,0)COLLATE DATABASE_DEFAULT
		AND ISNULL(TStats.inequality_columns +', ',0)COLLATE DATABASE_DEFAULT = ISNULL(TF.inequality_columns,0)COLLATE DATABASE_DEFAULT
		AND ISNULL(TStats.included_columns +', ',0)COLLATE DATABASE_DEFAULT  = ISNULL(TF.include_columns,0)COLLATE DATABASE_DEFAULT

		GROUP BY
		TF.impact	
		, TF.execution_count
		, TF.total_elapsed_time	
		, TF.database_id	
		, TF.OBJECT_ID	
		, TF.statement	
		, TF.equality_columns	
		, TF.inequality_columns	
		, TF.include_columns
		, TF.plan_handle
	) TFF
	CROSS APPLY [sys].dm_exec_query_plan(TFF.plan_handle) tp  
	WHERE TFF.plan_handle IN (SELECT rp.plan_handle FROM #sp_triage_readable_plan AS rp)
	ORDER BY  [SecondsSavedPerDay] DESC, total_elapsed_time DESC 
	OPTION (RECOMPILE);
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Plan insight - missing index', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
			RAISERROR	  (N'ERROR Section 16 looking for missing indexes in Query plan',0,1) WITH NOWAIT;
		END
	END CATCH


	BEGIN TRY
	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		, QueryPlan
	) 
	SELECT 
		17
		,'PLAN INSIGHT - EVERYTHING'
		,'------'
		,'------'
		,NULL

	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		, QueryPlan
	) 
	
	SELECT /*Bismillah, Find most intensive query*/
	TOP 35 
		17
		, REPLICATE ('|', 
			CASE 
				WHEN [Total_GBsRead]*[Impact%] = 0 THEN 0 
				ELSE 100.0 * [Total_GBsRead]*[Impact%]  / SUM ([Total_GBsRead]*[Impact%]) OVER() 
			END)   
		+ CASE 
			WHEN [Impact%] > 0 THEN CONVERT([VARCHAR](20),CONVERT(INT,ROUND(100.0 * [Total_GBsRead]*[Impact%]  / SUM ([Total_GBsRead]*[Impact%]) OVER(),0))) + '%' 
			ELSE '' 
		END [Section]

		, CONVERT([VARCHAR](20),[execution_count])
		+' events'
		+ CASE 
			WHEN [Impact%] > 0 AND [ImpactType] = 'Missing Index'   THEN ' Impacted by: Missing Index (' + CONVERT([VARCHAR](20),[Impact%]) + '%)'
			WHEN [Impact%] > 0 AND [ImpactType] ='CONVERT_IMPLICIT' THEN ' Impacted by: CONVERT_IMPLICIT' 
			ELSE '' 
		END  
		+ '; ' + CONVERT([VARCHAR](20),[Total_GBsRead]) +'GBs of I/O'
		+ '(' + CONVERT([VARCHAR](20),[total_logical_reads]) + ' pages)'
		+' took:' + CONVERT([VARCHAR](20),[total_elapsed_time_in_S]) +'(seconds)' [Summary]
		, ISNULL([Database] +':','')
		+ CASE 
			WHEN [Impact%] > 0 THEN 'Could reduce to: ' + CONVERT([VARCHAR](20), [Total_GBsRead] -([Impact%]/100 * [Total_GBsRead])) + 'GB'+ ' in ' + CONVERT([VARCHAR](20), CONVERT(INT,[total_elapsed_time_in_S] -([Impact%]/100) * [total_elapsed_time_in_S])) +'(s)'
			ELSE ''
		END
		+ '; Writes:'+ CONVERT([VARCHAR](20),[total_logical_writes])
		+ '(' + CONVERT([VARCHAR](20),[Total_GBsWrite]) + 'GB)' [Details]
		--, T1.[total_worker_time], T1.[last_execution_time]
		, [query_plan] /*This makes the query crawl, only add back WHEN you have time or need to see the full plans, but you dont want this for 10k rows*/
		FROM 
		(
	
			SELECT 
			TOP 100 PERCENT
				CASE 
					WHEN PATINDEX('%MissingIndexes%',CAST(qp.query_plan AS [NVARCHAR] (MAX))) > 0 THEN 'Missing Index' 
					WHEN PATINDEX('%PlanAffectingConvert%',CAST(qp.query_plan AS [NVARCHAR] (MAX))) > 0 THEN 'CONVERT_IMPLICIT' 
					ELSE NULL 
				END  [ImpactType]
				, CASE 
					WHEN PATINDEX('%MissingIndexGroup Impact%',CAST(qp.query_plan AS [NVARCHAR] (MAX))) > 0  
					THEN CONVERT(MONEY,REPLACE(REPLACE(REPLACE(SUBSTRING(CONVERT([NVARCHAR] (MAX),qp.query_plan),PATINDEX('%MissingIndexGroup Impact%',CAST(qp.query_plan AS [NVARCHAR] (MAX)))+26,6),'"><',''),'"',''),'>',''))
				ELSE NULL 
				END [Impact%]
				, T1.[execution_count]
				, T1.[total_logical_reads]
				, T1.[total_logical_writes]
				, [Total_MBsRead]/1000 [Total_GBsRead]
				, [Total_MBsWrite]/1000 [Total_GBsWrite]
				, T1.[total_worker_time]
				, T1.[total_elapsed_time_in_S]
				,  T1.[last_execution_time]
				, CASE 
					WHEN @ShowQueryPlan = 1 THEN replace(replace(replace(replace(replace(CONVERT([NVARCHAR] (MAX),qt.[Text]),CHAR(13)+CHAR(10),' '),CHAR(10)+CHAR(13),' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ')
					ELSE '' 
				END [QueryText]
				, qp.[query_plan]
				, DB_NAME(qp.dbid) [Database]
				, OBJECT_NAME(qp.objectid) [Object]
			FROM 
			#output_sqldba_org_sp_triage_querystats T1
			CROSS APPLY [sys].dm_exec_query_plan(T1.plan_handle) qp
			CROSS APPLY [sys].dm_exec_sql_text(T1.sql_handle) qt
			INNER JOIN @Databases d ON d.database_id = qp.dbid
			WHERE T1.Id <= @TopQueries
			AND T1.plan_handle IN (SELECT rp.plan_handle FROM #sp_triage_readable_plan AS rp)
			--WHERE PATINDEX('%MissingIndex%',CAST(query_plan AS [NVARCHAR] (MAX))) > 0
			ORDER BY 
			CASE 
				WHEN  PATINDEX('%MissingIndexes%',CAST(qp.query_plan AS [NVARCHAR] (MAX)))  > 0 THEN 1 
				ELSE 0 
			END DESC
			,CASE 
				WHEN  PATINDEX('%MissingIndexes%',CAST(qp.query_plan AS [NVARCHAR] (MAX))) > 0 THEN  PATINDEX('%MissingIndexes%',CAST(qp.query_plan AS [NVARCHAR] (MAX))) * [Total_MBsRead]  
				ELSE 0 
			END DESC 
		) q 
		ORDER BY CASE WHEN [Impact%] > 0 THEN 1 ELSE 0 END DESC, [Total_GBsRead]*[Impact%] DESC OPTION (RECOMPILE);
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Plan insight - most intensive query', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		IF @Debug = 1
	BEGIN
		SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
			RAISERROR	  (N'ERROR Section 17 Find most intensive query',0,1) WITH NOWAIT;
		END
	END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR	  (N'Evaluated execution plans for missing indexes',0,1) WITH NOWAIT;
	END


			/*----------------------------------------
			--Get missing index information for each database
			----------------------------------------*/

		SET @CustomErrorText = 'Looking for missing indexes in DMVs' /* server-wide: no database name (lane bundle-sp-triage-fixes-b, member 3) */
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END

			/* Section 18 (lane sp-triage-section-18-index-suggestions, 2026-11-14; Adrian's ruling 2026-11-14 18:57). Invariant:
			   every Details row of a missing-index suggestion is EITHER one plain CREATE NONCLUSTERED INDEX ...; plus at most one
			   comment, which parses and runs on a writable copy of the table, with a name unique among the rows; OR one note
			   comment (NOTE sp_triage) of at most 4,000 characters that says why no statement is shown. Nothing is spliced and
			   nothing in Details is a query to run: names come from sys.dm_db_missing_index_columns, every identifier goes through
			   QUOTENAME, keys are EQUALITY then INEQUALITY, each group most distinct values first (estimate from the statistics
			   histogram; unmeasured columns after measured ones, in column_id order), INCLUDE columns in column_id order. Lists
			   are built by WHILE loops over the ordered rows, not by xml methods, so a copy created with QUOTED_IDENTIFIER OFF
			   still runs this. The name is IX_SQLDBA_ + an ASCII part (table, up to three keys in column_id order) cut to 108 +
			   _ + 8 hex of a hash over the database, schema, table and column ids [+ _ + index_handle on a name collision],
			   at most 128 characters. A name holding a character the export rewrites (tilde, tab, line break, a double space)
			   gets a note, not a CREATE: the exported text would name a different object; every name in a note is made free of
			   those characters (lane design 10, shape S23). Guarded by SpTriageFixTextShapeCensusTests (d) The_missing_index_name_keeps_its_hash_inside_128_characters
			   and (e) No_text_or_column_brings_back_the_being_clever_splice (a tripwire). Proved once, live, by the lane's runner at merge;
			   no standing test runs the emitted CREATEs, so a
			   change here must be re-run live. */
			DECLARE @s18sql [NVARCHAR] (MAX);
			DECLARE @s18body [NVARCHAR] (MAX);
			DECLARE @s18dbid INT;
			DECLARE @s18k INT;
			DECLARE @s18kmax INT;
			DECLARE @s18keylimit INT;
			SET @s18keylimit = CASE WHEN @SQLVersion >= 13 THEN 32 ELSE 16 END;
			DECLARE @s18rewritten [NVARCHAR] (10);
			SET @s18rewritten = N'%[~' + NCHAR(9) + NCHAR(10) + NCHAR(13) + N']%';

			BEGIN TRY
				INSERT #output_sqldba_org_sp_triage_MissingIndex
				(
					index_handle
					, database_id
					, [object_id]
					, magic_benefit_number
					, [Table]
					, equality_columns
					, inequality_columns
					, included_columns
					, DBName
					, SchemaName
					, TableName
					, Keep
				)
				SELECT mid.index_handle
					, mid.database_id
					, mid.[object_id]
					, ((migs.user_seeks + migs.user_scans) * migs.avg_total_user_cost * migs.avg_user_impact) / CONVERT(FLOAT, @DaysOldestCachedQuery)
					, mid.[statement]
					, mid.equality_columns
					, mid.inequality_columns
					, mid.included_columns
					, r.name
					, CASE WHEN r.database_id IS NOT NULL THEN OBJECT_SCHEMA_NAME(mid.[object_id], mid.database_id) END
					, CASE WHEN r.database_id IS NOT NULL THEN OBJECT_NAME(mid.[object_id], mid.database_id) END
					, 0
				FROM [sys].dm_db_missing_index_group_stats AS migs 
				INNER JOIN [sys].dm_db_missing_index_groups AS mig ON migs.group_handle = mig.index_group_handle 
				INNER JOIN [sys].dm_db_missing_index_details AS mid ON mig.index_handle = mid.index_handle 
				LEFT OUTER JOIN #sp_triage_readable_db AS r ON r.database_id = mid.database_id
				OPTION (RECOMPILE);

				/* The same benefit filter as the output below (> 50000). */
				UPDATE #output_sqldba_org_sp_triage_MissingIndex SET Keep = 1 WHERE magic_benefit_number > 50000;

				INSERT #output_sqldba_org_sp_triage_MissingIndexColumn (index_handle, column_id, column_name, column_usage)
				SELECT m.index_handle, c.column_id, c.column_name, c.column_usage
				FROM #output_sqldba_org_sp_triage_MissingIndex AS m
				LEFT OUTER JOIN #sp_triage_readable_db AS r ON r.database_id = m.database_id
				CROSS APPLY [sys].dm_db_missing_index_columns(m.index_handle) AS c
				WHERE m.Keep = 1
				OPTION (RECOMPILE);

				/* Two handles with the same table and the same columns in the same roles: keep the higher benefit only. */
				UPDATE m SET Keep = 0
				FROM #output_sqldba_org_sp_triage_MissingIndex AS m
				WHERE m.Keep = 1
				AND EXISTS (SELECT 1 FROM #output_sqldba_org_sp_triage_MissingIndex AS o
					WHERE o.Keep = 1 AND o.index_handle <> m.index_handle AND o.database_id = m.database_id AND o.[object_id] = m.[object_id]
					AND (o.magic_benefit_number > m.magic_benefit_number OR (o.magic_benefit_number = m.magic_benefit_number AND o.index_handle < m.index_handle))
					AND (SELECT COUNT(*) FROM #output_sqldba_org_sp_triage_MissingIndexColumn AS a WHERE a.index_handle = o.index_handle)
						= (SELECT COUNT(*) FROM #output_sqldba_org_sp_triage_MissingIndexColumn AS b WHERE b.index_handle = m.index_handle)
					AND NOT EXISTS (SELECT 1 FROM #output_sqldba_org_sp_triage_MissingIndexColumn AS b WHERE b.index_handle = m.index_handle
						AND NOT EXISTS (SELECT 1 FROM #output_sqldba_org_sp_triage_MissingIndexColumn AS a
							WHERE a.index_handle = o.index_handle AND a.column_id = b.column_id AND a.column_usage = b.column_usage)));

				UPDATE m SET KeyCount = x.KeyCount, IncludeCount = x.IncludeCount, IncludeSum = x.IncludeSum
				FROM #output_sqldba_org_sp_triage_MissingIndex AS m
				INNER JOIN (SELECT c.index_handle
						, SUM(CASE WHEN c.column_usage = N'INCLUDE' THEN 0 ELSE 1 END) AS KeyCount
						, SUM(CASE WHEN c.column_usage = N'INCLUDE' THEN 1 ELSE 0 END) AS IncludeCount
						, CHECKSUM_AGG(CASE WHEN c.column_usage = N'INCLUDE' THEN BINARY_CHECKSUM(c.column_id) END) AS IncludeSum
					FROM #output_sqldba_org_sp_triage_MissingIndexColumn AS c
					GROUP BY c.index_handle) AS x ON x.index_handle = m.index_handle
				WHERE m.Keep = 1;

				/* Rows that get a note instead of a statement (the 4,000 note is decided after the statement is built). */
				UPDATE #output_sqldba_org_sp_triage_MissingIndex SET
					NoteReason = CASE
						WHEN database_id = 2 THEN N' is a temporary table in tempdb, so no CREATE INDEX is shown.'
						WHEN DBName IS NULL OR SchemaName IS NULL OR TableName IS NULL THEN N': its names could not be read (database offline or not accessible), so no CREATE INDEX is shown.'
						WHEN (DBName + NCHAR(31) + SchemaName + NCHAR(31) + TableName) LIKE @s18rewritten OR (DBName + NCHAR(31) + SchemaName + NCHAR(31) + TableName) LIKE N'%  %'
							OR EXISTS (SELECT 1 FROM #output_sqldba_org_sp_triage_MissingIndexColumn AS c WHERE c.index_handle = #output_sqldba_org_sp_triage_MissingIndex.index_handle
								AND (c.column_name LIKE @s18rewritten OR c.column_name LIKE N'%  %'))
							THEN N': a name in it contains a character the sp_triage export rewrites (tilde, tab, line break or a double space), so its CREATE INDEX is not shown.'
						WHEN KeyCount > @s18keylimit THEN N' has ' + CONVERT([NVARCHAR] (12), KeyCount) + N' key columns, over SQL Server''s limit of ' + CONVERT([NVARCHAR] (12), @s18keylimit) + N', so its CREATE INDEX is not shown.'
					END
					, NoteTarget = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(CASE WHEN DBName IS NOT NULL AND SchemaName IS NOT NULL AND TableName IS NOT NULL
						THEN QUOTENAME(DBName) + N'.' + QUOTENAME(SchemaName) + N'.' + QUOTENAME(TableName) ELSE [Table] END, N'*/', N'* /'), N'/*', N'/ *'), N'~', N':'), NCHAR(9), N' '), NCHAR(13), N' '), NCHAR(10), N' '), N'  ', N' ?')
					, NoteKeysMore = 0
					, NoteIncludesMore = 0
					, NameStem = N'IX_SQLDBA_'
					, NameHashInput = DBName + NCHAR(31) + SchemaName + NCHAR(31) + TableName + NCHAR(31)
				WHERE Keep = 1;
				UPDATE #output_sqldba_org_sp_triage_MissingIndex SET NoteBudget = (3600 - LEN(NoteTarget)) / 2 WHERE Keep = 1;
				/* Names in a note: comment-safe, and free of every character the export rewrites (tilde to :, tab and line
				   breaks to a space, the second space of a pair to ?), so the export leaves the note as written. */
				UPDATE #output_sqldba_org_sp_triage_MissingIndexColumn SET NoteName = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(QUOTENAME(column_name), N'*/', N'* /'), N'/*', N'/ *'), N'~', N':'), NCHAR(9), N' '), NCHAR(13), N' '), NCHAR(10), N' '), N'  ', N' ?');

				/* The uniqueness measure: per readable database that has a suggestion (note rows included, so a note lists its keys
				   in the order a statement would), the statistic whose LEADING column is the
				   key column (unfiltered, most rows sampled, then newest), distinct values = histogram steps + SUM(distinct_range_rows).
				   Metadata only: no table data is read. No histogram function (before SQL 2016 SP1 CU2), statistics hidden by
				   permission (empty rowsets), no unfiltered statistic: the column is unmeasured (NULL, never 0). A measure that
				   raises records a Could not run row and leaves that database's keys unmeasured. Azure SQL Database: the current
				   database only, without USE. */
				IF OBJECT_ID(N'sys.dm_db_stats_histogram') IS NOT NULL
				BEGIN
					SET @s18body = N'SELECT @dbid, x.[object_id], x.column_id, NULLIF(h.est, 0)
					FROM (SELECT DISTINCT m.[object_id], c.column_id
						FROM #output_sqldba_org_sp_triage_MissingIndex AS m
						INNER JOIN #output_sqldba_org_sp_triage_MissingIndexColumn AS c ON c.index_handle = m.index_handle
						WHERE m.database_id = @dbid AND m.Keep = 1 AND c.column_usage IN (N''EQUALITY'', N''INEQUALITY'')) AS x
					OUTER APPLY (SELECT TOP (1) (SELECT COUNT_BIG(*) + ISNULL(SUM(CONVERT([BIGINT], hh.distinct_range_rows)), 0)
							FROM sys.dm_db_stats_histogram(st.[object_id], st.stats_id) AS hh) AS est
						FROM sys.stats AS st
						INNER JOIN sys.stats_columns AS sc ON sc.[object_id] = st.[object_id] AND sc.stats_id = st.stats_id AND sc.stats_column_id = 1
						CROSS APPLY sys.dm_db_stats_properties(st.[object_id], st.stats_id) AS sp
						WHERE st.[object_id] = x.[object_id] AND sc.column_id = x.column_id AND st.has_filter = 0 AND sp.[rows] > 0
						ORDER BY sp.rows_sampled DESC, sp.last_updated DESC) AS h
					OPTION (RECOMPILE);';
					DECLARE s18_measure_db CURSOR LOCAL FAST_FORWARD FOR
						SELECT r.database_id, r.name
						FROM #sp_triage_readable_db AS r
						WHERE r.database_id IN (SELECT m.database_id FROM #output_sqldba_org_sp_triage_MissingIndex AS m WHERE m.Keep = 1)
						AND (@IsSQLAzure = 0 OR r.database_id = DB_ID());
					OPEN s18_measure_db;
					FETCH NEXT FROM s18_measure_db INTO @s18dbid, @dbname;
					WHILE @@FETCH_STATUS = 0
					BEGIN
						SET @s18sql = CASE WHEN @IsSQLAzure = 1 THEN N'' ELSE N'USE ' + QUOTENAME(@dbname) + N'; ' END + @s18body;
						BEGIN TRY
							INSERT #output_sqldba_org_sp_triage_MissingIndexMeasure (database_id, [object_id], column_id, distinct_est)
							EXEC sp_executesql @s18sql, N'@dbid INT', @dbid = @s18dbid;
						END TRY
						BEGIN CATCH
							INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(N'Missing index key order (statistics) ' + QUOTENAME(@dbname), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
						END CATCH
						FETCH NEXT FROM s18_measure_db INTO @s18dbid, @dbname;
					END
					CLOSE s18_measure_db;
					DEALLOCATE s18_measure_db;
				END

				UPDATE c SET distinct_est = u.distinct_est
				FROM #output_sqldba_org_sp_triage_MissingIndexColumn AS c
				INNER JOIN #output_sqldba_org_sp_triage_MissingIndex AS m ON m.index_handle = c.index_handle
				INNER JOIN #output_sqldba_org_sp_triage_MissingIndexMeasure AS u ON u.database_id = m.database_id AND u.[object_id] = m.[object_id] AND u.column_id = c.column_id
				WHERE c.column_usage IN (N'EQUALITY', N'INEQUALITY');

				/* A note's key entry (lane design 11, rows N2 and N3): the name as a note writes it, then its distinct-value estimate
				   or "not measured", the facts the statement's comment gives. NoteName holds the whole entry, so the note's list
				   budget below counts the whole entry, not the name alone. */
				UPDATE #output_sqldba_org_sp_triage_MissingIndexColumn SET NoteName = NoteName + CASE WHEN distinct_est IS NOT NULL THEN N' ' + CONVERT([NVARCHAR] (20), distinct_est) ELSE N' not measured' END
				WHERE column_usage IN (N'EQUALITY', N'INEQUALITY');

				/* KeyOrder: the statement's order (EQUALITY, INEQUALITY, each most distinct first, unmeasured and ties by
				   column_id; then INCLUDE by column_id). IdOrder: column_id order inside each role, for the stable name. */
				;WITH o AS
				(
					SELECT c.KeyOrder, c.IdOrder
						, ROW_NUMBER() OVER (PARTITION BY c.index_handle ORDER BY CASE c.column_usage WHEN N'EQUALITY' THEN 0 WHEN N'INEQUALITY' THEN 1 ELSE 2 END
							, CASE WHEN c.column_usage <> N'INCLUDE' AND c.distinct_est IS NOT NULL THEN 0 ELSE 1 END
							, CASE WHEN c.column_usage <> N'INCLUDE' THEN c.distinct_est END DESC, c.column_id) AS k
						, ROW_NUMBER() OVER (PARTITION BY c.index_handle ORDER BY CASE c.column_usage WHEN N'EQUALITY' THEN 0 WHEN N'INEQUALITY' THEN 1 ELSE 2 END, c.column_id) AS i
					FROM #output_sqldba_org_sp_triage_MissingIndexColumn AS c
				)
				UPDATE o SET KeyOrder = k, IdOrder = i;

				/* The ASCII part of the name: characters outside [A-Za-z0-9_] dropped, one per pass. */
				UPDATE #output_sqldba_org_sp_triage_MissingIndexColumn SET AsciiName = column_name WHERE column_usage <> N'INCLUDE' AND IdOrder <= 3;
				UPDATE #output_sqldba_org_sp_triage_MissingIndex SET NameStem = N'IX_SQLDBA_' + TableName WHERE Keep = 1 AND NoteReason IS NULL;
				SET @s18k = 1;
				WHILE @s18k > 0
				BEGIN
					UPDATE #output_sqldba_org_sp_triage_MissingIndexColumn SET AsciiName = STUFF(AsciiName, PATINDEX(N'%[^A-Za-z0-9_]%', AsciiName COLLATE Latin1_General_BIN2), 1, N'')
					WHERE PATINDEX(N'%[^A-Za-z0-9_]%', AsciiName COLLATE Latin1_General_BIN2) > 0;
					SET @s18k = @@ROWCOUNT;
					UPDATE #output_sqldba_org_sp_triage_MissingIndex SET NameStem = STUFF(NameStem, PATINDEX(N'%[^A-Za-z0-9_]%', NameStem COLLATE Latin1_General_BIN2), 1, N'')
					WHERE PATINDEX(N'%[^A-Za-z0-9_]%', NameStem COLLATE Latin1_General_BIN2) > 0;
					SET @s18k = @s18k + @@ROWCOUNT;
				END
				UPDATE #output_sqldba_org_sp_triage_MissingIndex SET NameStem = N'IX_SQLDBA_x' WHERE NameStem = N'IX_SQLDBA_';

				/* ONE pass per position: each UPDATE appends at most one column to each suggestion's lists. */
				SELECT @s18kmax = MAX(c.KeyOrder) FROM #output_sqldba_org_sp_triage_MissingIndexColumn AS c;
				SET @s18k = 1;
				WHILE @s18k <= ISNULL(@s18kmax, 0)
				BEGIN
					UPDATE m SET
						KeyList = CASE WHEN c.column_usage = N'INCLUDE' THEN m.KeyList ELSE ISNULL(m.KeyList + N', ', N'') + QUOTENAME(c.column_name) END
						, IncludeList = CASE WHEN c.column_usage = N'INCLUDE' THEN ISNULL(m.IncludeList + N', ', N'') + QUOTENAME(c.column_name) ELSE m.IncludeList END
						, OrderComment = CASE WHEN c.column_usage = N'INCLUDE' THEN m.OrderComment
							WHEN c.distinct_est IS NOT NULL THEN ISNULL(m.OrderComment + CASE WHEN m.LastMeasured = 0 THEN N' not measured; ' ELSE N', ' END, N'')
								+ REPLACE(REPLACE(QUOTENAME(c.column_name), N'*/', N'* /'), N'/*', N'/ *') + N' ' + CONVERT([NVARCHAR] (20), c.distinct_est)
							ELSE ISNULL(m.OrderComment + CASE WHEN m.LastMeasured = 0 THEN N', ' ELSE N'; ' END, N'')
								+ REPLACE(REPLACE(QUOTENAME(c.column_name), N'*/', N'* /'), N'/*', N'/ *') END
						, LastMeasured = CASE WHEN c.column_usage = N'INCLUDE' THEN m.LastMeasured WHEN c.distinct_est IS NOT NULL THEN 1 ELSE 0 END
						, NoteKeys = CASE WHEN c.column_usage <> N'INCLUDE' AND m.NoteKeysMore = 0
								AND LEN(ISNULL(m.NoteKeys, N'')) + 2 + LEN(c.NoteName) <= m.NoteBudget
							THEN ISNULL(m.NoteKeys + N', ', N'') + c.NoteName ELSE m.NoteKeys END
						, NoteKeysMore = CASE WHEN c.column_usage <> N'INCLUDE' AND NOT (m.NoteKeysMore = 0
								AND LEN(ISNULL(m.NoteKeys, N'')) + 2 + LEN(c.NoteName) <= m.NoteBudget)
							THEN m.NoteKeysMore + 1 ELSE m.NoteKeysMore END
						, NoteIncludes = CASE WHEN c.column_usage = N'INCLUDE' AND m.NoteIncludesMore = 0
								AND LEN(ISNULL(m.NoteIncludes, N'')) + 2 + LEN(c.NoteName) <= m.NoteBudget
							THEN ISNULL(m.NoteIncludes + N', ', N'') + c.NoteName ELSE m.NoteIncludes END
						, NoteIncludesMore = CASE WHEN c.column_usage = N'INCLUDE' AND NOT (m.NoteIncludesMore = 0
								AND LEN(ISNULL(m.NoteIncludes, N'')) + 2 + LEN(c.NoteName) <= m.NoteBudget)
							THEN m.NoteIncludesMore + 1 ELSE m.NoteIncludesMore END
					FROM #output_sqldba_org_sp_triage_MissingIndex AS m
					INNER JOIN #output_sqldba_org_sp_triage_MissingIndexColumn AS c ON c.index_handle = m.index_handle AND c.KeyOrder = @s18k
					WHERE m.Keep = 1;

					UPDATE m SET
						NameStem = CASE WHEN c.column_usage <> N'INCLUDE' AND c.IdOrder <= 3 THEN m.NameStem + N'_' + ISNULL(NULLIF(c.AsciiName, N''), N'x') ELSE m.NameStem END
						, NameHashInput = CASE WHEN c.column_usage <> N'INCLUDE' AND c.IdOrder <= 64 THEN m.NameHashInput + LEFT(c.column_usage, 1) + CONVERT([NVARCHAR] (12), c.column_id) + N',' ELSE m.NameHashInput END
					FROM #output_sqldba_org_sp_triage_MissingIndex AS m
					INNER JOIN #output_sqldba_org_sp_triage_MissingIndexColumn AS c ON c.index_handle = m.index_handle AND c.IdOrder = @s18k
					WHERE m.Keep = 1 AND m.NoteReason IS NULL;
					SET @s18k = @s18k + 1;
				END

				/* SHA2_256 from SQL Server 2012; HASHBYTES returns NULL for it on 2008 and 2008 R2, which use SHA1. Input stays
				   under 8,000 bytes: three names, at most 64 key ids, the include count and CHECKSUM_AGG of the include ids. */
				UPDATE #output_sqldba_org_sp_triage_MissingIndex SET NameHashInput = NameHashInput + N'N' + CONVERT([NVARCHAR] (12), ISNULL(IncludeCount, 0)) + N',' + CONVERT([NVARCHAR] (12), ISNULL(IncludeSum, 0))
				WHERE Keep = 1 AND NoteReason IS NULL;
				UPDATE #output_sqldba_org_sp_triage_MissingIndex SET NameHash = ISNULL(HASHBYTES('SHA2_256', NameHashInput), HASHBYTES('SHA1', NameHashInput))
				WHERE Keep = 1 AND NoteReason IS NULL;
				;WITH n AS
				(
					SELECT NameSuffix, index_handle
						, ROW_NUMBER() OVER (PARTITION BY SUBSTRING(NameHash, 1, 4), CONVERT([NVARCHAR] (108), NameStem) ORDER BY magic_benefit_number DESC, index_handle) AS rn
					FROM #output_sqldba_org_sp_triage_MissingIndex
					WHERE Keep = 1 AND NoteReason IS NULL
				)
				UPDATE n SET NameSuffix = CASE WHEN rn > 1 THEN index_handle END;

				/* The statement. The name: the readable part cut to 108, + _ + 8 hex, + _ + index_handle (at most 11) on a
				   collision: at most 128, so QUOTENAME never returns NULL. */
				UPDATE #output_sqldba_org_sp_triage_MissingIndex SET IndexStatement = N'CREATE NONCLUSTERED INDEX '
					+ QUOTENAME(LEFT(NameStem, 108) + N'_' + LOWER(CONVERT([NVARCHAR] (8), NameHash, 2)) + ISNULL(N'_' + CONVERT([NVARCHAR] (10), NameSuffix), N''))
					+ N' ON ' + QUOTENAME(DBName) + N'.' + QUOTENAME(SchemaName) + N'.' + QUOTENAME(TableName)
					+ N' (' + KeyList + N')' + ISNULL(N' INCLUDE (' + IncludeList + N')', N'') + N';'
				WHERE Keep = 1 AND NoteReason IS NULL;
				UPDATE #output_sqldba_org_sp_triage_MissingIndex SET NoteReason = N' is ' + CONVERT([NVARCHAR] (12), LEN(IndexStatement)) + N' characters, over the 4000-character export limit, so its CREATE INDEX is not shown.'
				WHERE Keep = 1 AND NoteReason IS NULL AND LEN(IndexStatement) > 4000;

				/* Details: the statement and its key-order comment; the statement with a short comment when both pass 4,000; the
				   note when the statement alone passes 4,000 or no statement is possible. Every note is at most 4,000: fixed
				   text, the target (at most three QUOTENAME'd names) and two lists cut at whole entries to NoteBudget each. NoteBudget
				   is (3600 - LEN(target)) / 2, so the fixed text has 400 characters; the longest (the export-rewritten reason, the
				   key-list label, both "and N more" tails, the INCLUDE label and the frame) is at most 300 (lane design 11, row N3). */
				UPDATE #output_sqldba_org_sp_triage_MissingIndex SET Details = CASE
					WHEN NoteReason IS NULL AND LEN(IndexStatement) + LEN(OrderComment) + 64 <= 4000
						THEN IndexStatement + N' /* key order by distinct values (statistics): ' + OrderComment + CASE WHEN LastMeasured = 0 THEN N' not measured' ELSE N'' END + N' */'
					WHEN NoteReason IS NULL AND LEN(IndexStatement) <= 3960 THEN IndexStatement + N' /* key order: see Summary */'
					WHEN NoteReason IS NULL THEN IndexStatement
					ELSE N'/* NOTE sp_triage: the suggested index on ' + NoteTarget + NoteReason
						+ N' Key columns in order (distinct values from statistics): ' + ISNULL(NoteKeys, N'') + CASE WHEN NoteKeysMore > 0 THEN N' and ' + CONVERT([NVARCHAR] (12), NoteKeysMore) + N' more' ELSE N'' END + N'.'
						+ CASE WHEN IncludeCount > 0 THEN N' INCLUDE columns (' + CONVERT([NVARCHAR] (12), IncludeCount) + N'): ' + ISNULL(NoteIncludes, N'')
							+ CASE WHEN NoteIncludesMore > 0 THEN N' and ' + CONVERT([NVARCHAR] (12), NoteIncludesMore) + N' more' ELSE N'' END + N'.' ELSE N'' END
						+ N' */'
					END
				WHERE Keep = 1;
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Looking for missing indexes in DMVs', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END CATCH
			

			/*----------------------------------------
			--Loop all the user databases to run database specific commands against them
			Loop logic would set the variables then delete the entry from the @databases table
			----------------------------------------*/
	/* Which databases the per-database loop checks. Invariants:
	   - At most @TopIORankDBs user databases (database_id > 4) in @Databases: every one of them is checked.
	   - More than that: the @TopIORankDBs busiest user databases by TotalIO (disk reads + writes since SQL Server
	     started; no I/O row = least busy; ties go to the lower database_id), PLUS the 5 largest user databases by
	     allocated size from @master_filesTable, whatever their I/O. System databases never take one of those places.
	   - @TopIORankDBs NULL or <= 0 = every database.
	   - System databases (database_id 1-4) are checked unless @ExcludeSystemDBs = 1.
	   - Every user database left out is named in ONE #sp_triage_skipped row (message at most 3800 characters, cut
	     after a whole name with " and N more").
	   - Only the loop's input changes: every other reader of @Databases runs above this point.
	   - After any delete, @Databasei_Max is the row count, so "Looping Database i of N" counts what is checked. */
	DECLARE @PerDbUserCount INT, @PerDbKeptCount INT, @PerDbSkipCount INT, @PerDbShown INT, @PerDbRn INT;
	DECLARE @PerDbSkipName [NVARCHAR] (300), @PerDbSkipList [NVARCHAR] (4000), @PerDbHead [NVARCHAR] (4000), @PerDbMsg [NVARCHAR] (4000);
	DECLARE @PerDbKeep TABLE (database_id INT PRIMARY KEY);
	DECLARE @PerDbSkipped TABLE (rn INT IDENTITY(1,1) PRIMARY KEY, dbname [NVARCHAR] (250));

	IF @ExcludeSystemDBs = 1
		DELETE FROM @Databases
		WHERE database_id <= 4;

	SELECT @PerDbUserCount = COUNT(1) FROM @Databases WHERE database_id > 4;

	IF @TopIORankDBs > 0 AND @PerDbUserCount > @TopIORankDBs
	BEGIN
		INSERT @PerDbKeep (database_id)
		SELECT io.database_id
		FROM (
			SELECT d.database_id
				, ROW_NUMBER() OVER (ORDER BY ISNULL(d.TotalIO, 0) DESC, d.database_id ASC) AS rn
			FROM @Databases d
			WHERE d.database_id > 4
		) io
		WHERE io.rn <= @TopIORankDBs
		UNION
		SELECT sz.database_id
		FROM (
			SELECT mf.database_id
				, ROW_NUMBER() OVER (ORDER BY SUM(CONVERT(BIGINT, mf.size)) DESC, mf.database_id ASC) AS rn
			FROM @master_filesTable mf
			WHERE mf.database_id > 4
			AND mf.database_id IN (SELECT d.database_id FROM @Databases d)
			GROUP BY mf.database_id
		) sz
		WHERE sz.rn <= 5;

		INSERT @PerDbSkipped (dbname)
		SELECT d.databasename
		FROM @Databases d
		WHERE d.database_id > 4
		AND d.database_id NOT IN (SELECT k.database_id FROM @PerDbKeep k)
		ORDER BY d.databasename;

		SELECT @PerDbSkipCount = COUNT(1) FROM @PerDbSkipped;
		SET @PerDbKeptCount = @PerDbUserCount - @PerDbSkipCount;

		SET @PerDbHead = N'Checked ' + CONVERT([NVARCHAR] (12), @PerDbKeptCount) + N' of ' + CONVERT([NVARCHAR] (12), @PerDbUserCount)
			+ N' user databases that can be read. @TopIORankDBs = ' + CONVERT([NVARCHAR] (12), @TopIORankDBs)
			+ N' picks the ' + CONVERT([NVARCHAR] (12), @TopIORankDBs) + N' busiest by disk reads and writes since SQL Server started'
			+ CASE WHEN EXISTS (SELECT 1 FROM @master_filesTable) THEN N', plus the 5 largest' ELSE N'' END
			+ N'. Not checked: ';
		/* Names in name order until the next one would push the message past 3800 characters; the 80 kept back
		   hold " and N more" and the closing sentence. */
		SELECT @PerDbSkipList = N'', @PerDbRn = 1, @PerDbShown = 0;
		WHILE @PerDbRn <= @PerDbSkipCount
		BEGIN
			SELECT @PerDbSkipName = QUOTENAME(s.dbname) FROM @PerDbSkipped s WHERE s.rn = @PerDbRn;
			IF LEN(@PerDbHead) + LEN(@PerDbSkipList) + 2 + LEN(@PerDbSkipName) + 80 > 3800 BREAK;
			SET @PerDbSkipList = @PerDbSkipList + CASE WHEN @PerDbRn = 1 THEN N'' ELSE N', ' END + @PerDbSkipName;
			SET @PerDbShown = @PerDbRn;
			SET @PerDbRn = @PerDbRn + 1;
		END
		SET @PerDbMsg = @PerDbHead + @PerDbSkipList
			+ CASE WHEN @PerDbShown < @PerDbSkipCount THEN N' and ' + CONVERT([NVARCHAR] (12), @PerDbSkipCount - @PerDbShown) + N' more' ELSE N'' END
			+ N'. Set @TopIORankDBs = 0 to check every database.';

		IF @PerDbSkipCount > 0
		BEGIN
			INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage)
			VALUES (N'Per-database checks for some user databases', 0, @PerDbMsg);
			RAISERROR (N'%s', 0, 1, @PerDbMsg) WITH NOWAIT;
		END

		DELETE FROM @Databases
		WHERE database_id > 4
		AND database_id NOT IN (SELECT k.database_id FROM @PerDbKeep k);

		/* 2026-10-16 (member 4): a debug result set. Unguarded, it reached the client as an extra result set on every
		   server with more than 10 user databases. */
		IF @Debug = 1
			SELECT * from @Databases
	END

	SET @Databasei_Max = (SELECT COUNT(1) FROM @Databases);

	DECLARE @SkipthisDB BIT	
	SET @SkipthisDB = 0
	SET @dynamicSQL = ''
	SET @Databasei_Count = 1; 
	/* Per-database loop. Invariant (2026-10-16, member 1): every database name concatenated into SQL that runs goes
	   through QUOTENAME when it is an identifier, and through REPLACE(x, N'''', N'''''') when it sits inside a string
	   literal of that SQL (fix text: REPLACE(QUOTENAME(x), ...)). Guarded by SpTriageDbNameQuotingCensusTests.
	   Each iteration starts by resetting @SkipthisDB and the SELECT TOP 1 targets; the loop ends when that SELECT
	   finds no row, so a database is never processed twice on stale values. */
	WHILE @Databasei_Count <= @Databasei_Max 
	BEGIN 
		SET @SkipthisDB = 0;
		SELECT @DatabaseName = NULL, @DatabaseState = NULL, @AGBackupPref = NULL, @AGCurrentLocation = NULL, @SecondaryReadRole = NULL;
		SELECT TOP 1
			@DatabaseName = d.databasename
			, @DatabaseState = d.state 
			, @AGBackupPref = BackupPref
			, @AGCurrentLocation = CurrentLocation
			, @SecondaryReadRole = ReadSecondary
		FROM @Databases d 
		WHERE 1=1
		--AND id = @Databasei_Count 
		AND d.state NOT IN (2,6)
		OPTION (RECOMPILE)
		IF @DatabaseName IS NULL BREAK;

		/*Now we have the variables, so delete the entry so the table loops become simpler*/
		DELETE FROM @Databases
		WHERE databasename = @DatabaseName

		SET @ErrorMessage = 'Looping Database ' + CONVERT([VARCHAR](4),@Databasei_Count) +' of ' + CONVERT([VARCHAR](4),@Databasei_Max ) + ': [' + @DatabaseName + '] ';
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
			RAISERROR (N'%s', 0, 1, @ErrorMessage) WITH NOWAIT;
		END

		--IF (ISNULL(@SecondaryReadRole,'YES') <> 'NO' AND ISNULL(@AGBackupPref,'') <> 'primary') AND EXISTS( SELECT @DatabaseName)
		BEGIN  
			IF (ISNULL(@SecondaryReadRole,'YES') = 'NO' AND @AGCurrentLocation = @ThisServer) 
			SET	@SkipthisDB = 1
			IF @SecondaryReadRole IS NULL
			SET	@SkipthisDB = 0
			IF @SkipthisDB = 0
			BEGIN

		/* Index usage (sections 36 and 37 read #output_sqldba_org_sp_triage_indexusage). Moved 2026-10-26 (lane
		   bundle-sp-triage-fixes-b, member 3) from before this loop, where it ran ONCE on whatever @DatabaseName the VLF
		   loop left behind (or on NULL), and mixed other databases' usage rows in: dm_db_index_usage_stats is server-wide,
		   so the s.database_id = DB_ID() filter is required. Invariant: no code reads a loop variable outside its loop
		   (SpTriageSectionLabelCensusTests). */
			SET @CustomErrorText = '['+@DatabaseName+'] Index usage checks'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END

			SELECT @dynamicSQL = '
			USE '+QUOTENAME(@DatabaseName)+'
			SELECT --TOP 5000 
				N'''+REPLACE(@DatabaseName, N'''', N'''''')+N'''
				, OBJECT_NAME(s.[object_id]) AS [ObjectName]
				, i.name AS [IndexName]
				, i.index_id
				, user_seeks + user_scans + user_lookups AS [Reads]
				, s.user_updates AS [Writes]
				, i.type_desc AS [IndexType]
				, i.fill_factor AS [FillFactor]
				, i.has_filter
				, i.filter_definition
				, s.last_user_scan
				, s.last_user_lookup
				, s.last_user_seek
				, user_seeks
				, user_scans
				, user_lookups  
				,CONVERT(MONEY,(user_seeks + user_scans + user_lookups)) 
				/ SUM(user_seeks + user_scans + user_lookups) OVER(PARTITION BY s.[object_id]) * 100 [TableReadActivity%]
				,CONVERT(MONEY,(user_seeks + user_scans + user_lookups)) 
				/ SUM(user_seeks + user_scans + user_lookups) OVER() * 100 [TotalReadActivity%]
			FROM [sys].dm_db_index_usage_stats AS s WITH (NOLOCK)
			INNER JOIN [sys].indexes AS i WITH (NOLOCK)
				ON s.[object_id] = i.[object_id]
			WHERE OBJECTPROPERTY(s.[object_id],''IsUserTable'') = 1
			AND s.database_id = DB_ID()
			AND i.index_id = s.index_id
			AND i.is_primary_key = 0 --This line excludes primary key constarint
			--AND i.is_unique = 0 --This line excludes unique key constarint
			AND user_seeks + user_scans + user_lookups > 0
			ORDER BY 
			user_seeks + user_scans + user_lookups DESC 
			OPTION (RECOMPILE); -- Order by reads'
			BEGIN TRY
			INSERT #output_sqldba_org_sp_triage_indexusage 
			EXEC sp_executesql @dynamicSQL;
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Index usage checks' + ISNULL(N' ' + QUOTENAME(@DatabaseName), N''), N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END CATCH

		/*13. Find idle indexes*/

			/*---------------------------------------Shows Indexes that have never been used---------------------------------------*/
				SET @CustomErrorText = '['+@DatabaseName+'] Looking at unused indexes'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END
			SET ANSI_WARNINGS OFF
			
			SET @dynamicSQL = '
			USE '+QUOTENAME(@DatabaseName)+'
			DECLARE @DaysAgo INT
			DECLARE @TheDate DATETIME
			SET @DaysAgo = 15
			SET @TheDate =  CONVERT(DATETIME,CONVERT(INT,DATEADD(DAY,-@DaysAgo,GETDATE())))
			DECLARE @db_id smallint
			SET @db_id=db_id()

			SELECT 
				db_name(db_id())
				, CASE 
					WHEN b.type_desc = ''CLUSTERED'' THEN ''Consider Carefully'' 
					ELSE ''May remove'' 
				END Consideration
				, t.name TableName
				, b.type_desc TypeDesc
				, b.name IndexName
				, a.user_updates Updates
				, a.last_user_scan
				, a.last_user_seek
			--, SUM(aa.page_count) Pages
			FROM [sys].dm_db_index_usage_stats as a
			JOIN [sys].indexes AS b ON a.object_id = b.object_id AND a.index_id = b.index_id
			LEFT OUTER JOIN [sys].tables AS t ON b.[object_id] = t.[object_id]
			--LEFT OUTER JOIN INFORMATION_SCHEMA.TABLES isc ON isc.TABLE_NAME = t.name
			--LEFT OUTER JOIN [sys].dm_db_index_physical_stats (@db_id,NULL,NULL, NULL, SAMPLED) AS aa ON aa.object_id = a.object_id
			WHERE b.[type_desc] NOT LIKE ''Heap''
			AND ISNULL(a.user_seeks,0) + ISNULL(a.user_scans,0) + ISNULL(a.system_scans,0) + ISNULL(a.user_lookups,0) = 0
			--AND (DATEDIFF(DAY,a.last_user_scan,GETDATE()) > @DaysAgo AND DATEDIFF(DAY,a.last_user_seek,GETDATE()) > @DaysAgo)
			--AND t.name NOT LIKE ''sys%''
			GROUP BY t.name, b.type_desc, b.name, a.user_updates, a.last_user_scan, a.last_user_seek
			ORDER BY [Updates] DESC OPTION (RECOMPILE)
			'
			
			BEGIN TRY
				INSERT #NeverUsedIndex
				EXEC sp_executesql @dynamicSQL;
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Looking at unused indexes' + ISNULL(N' ' + QUOTENAME(@DatabaseName), N''), N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END CATCH
			SET ANSI_WARNINGS ON


		/*14. Find heaps*/

			/*---------------------------------------Shows tables without primary key. Heaps---------------------------------------*/

				SET @CustomErrorText = '['+@DatabaseName+'] Looking for heap tables'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END

			SET @dynamicSQL = CASE 
			WHEN @IsSQLAzure = 0 THEN 'USE '+QUOTENAME(@DatabaseName)
			ELSE '' END
+'
	SELECT 
		DB
		,[Schema]
		,table_name
		, IDXPS.forwarded_record_count
		, IDXPS.avg_fragmentation_in_percent Fragmentation_Percentage
		, IDXPS.page_count
		,rows
		,user_seeks
		,user_scans
		,user_lookups,user_updates
		,last_user_seek,last_user_scan
		,last_user_lookup

	FROM
	(
		SELECT 
			DB_NAME(DB_ID()) DB /* raw names: the heap emit site brackets them with QUOTENAME (lane bundle-sp-triage-fixes-b, member 4) */
			, OBJECT_SCHEMA_NAME(IDX.object_id) [Schema] 
			, OBJECT_NAME(IDX.object_id) AS table_name
			, IDX.object_id 
			, IDX.index_id		
			, p.rows
			, p.partition_number
			, user_seeks
			, user_scans
			, user_lookups,user_updates
			, last_user_seek,last_user_scan
			, last_user_lookup
			/*, forwarded_record_count, record_count, page_count*/

		FROM '+QUOTENAME(@DatabaseName)+'.[sys].indexes IDX  
		INNER JOIN '+QUOTENAME(@DatabaseName)+'.[sys].dm_db_index_usage_stats ius ON IDX.object_id = ius.object_id AND IDX.index_id = ius.index_id --AND IDXPS.database_id = ius.database_id
		INNER JOIN '+QUOTENAME(@DatabaseName)+'.[sys].partitions p ON IDX.object_id = p.object_id AND IDX.index_id = p.index_id

		WHERE  IDX.type = 0
		AND p.rows > 0  /*perf 2026-07-02: skip empty heaps - SAMPLED dm_db_index_physical_stats reads pages per heap; empty heaps never have meaningful fragmentation, so this cuts needless page reads without dropping a real finding*/
	) T1
	CROSS APPLY
	'+QUOTENAME(@DatabaseName)+'.[sys].dm_db_index_physical_stats(DB_ID(), T1.object_id, T1.index_id, partition_number, ''SAMPLED'') IDXPS '
			IF @SkipHeaps = 0
			BEGIN
				
				BEGIN TRY
					INSERT #output_sqldba_org_sp_triage_HeapTable
					EXEC sp_executesql @dynamicSQL;
				END TRY
				BEGIN CATCH
					INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Looking for heap tables' + ISNULL(N' ' + QUOTENAME(@DatabaseName), N''), N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
					SELECT @errMessage  = ERROR_MESSAGE()
					RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
					SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
					RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
				END CATCH
			END
		
			
			SET @CustomErrorText = '['+@DatabaseName+'] Looking for stale statistics'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END
			SET @dynamicSQL = 'SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
			USE '+QUOTENAME(@DatabaseName)+';
			SELECT 
				N'''+REPLACE(@DatabaseName, N'''', N'''''')+N''' [DbName]
				, ObjectNm
				, StatsID
				, StatsName
				, SchemaName
				, ModificationCount
				, [LastUpdated] 
				, Rows
				, CONVERT(MONEY,ModificationCount) * 100 / Rows [ModPerc]
				, EstPerc
			FROM 
			(
				SELECT 
					OBJECT_NAME(p.object_id) ObjectNm
					, p.index_id StatsID
					, s.name StatsName
					, MAX(p.rows) Rows
					, MAX(CASE WHEN p.rows > 0 THEN 
					 CASE 
							WHEN LOG ( p.rows ) BETWEEN 0 AND 9 THEN 20
							WHEN LOG ( p.rows ) BETWEEN 9 AND 10 THEN 15
							WHEN LOG ( p.rows ) BETWEEN 10 AND 11 THEN 10
							WHEN LOG ( p.rows ) BETWEEN 11 AND 12 THEN 5
							WHEN LOG ( p.rows ) BETWEEN 12 AND 13.1 THEN 3
							WHEN LOG ( p.rows ) BETWEEN 13.1 AND 13.8 THEN 1.7
							WHEN LOG ( p.rows ) BETWEEN 13.8 AND 14.7 THEN 1.2
							WHEN LOG ( p.rows ) BETWEEN 14.7 AND 17 THEN 0.15
							WHEN LOG ( p.rows ) > 17 THEN 0.015
							ELSE 0.015
							 END
					ELSE 0 END)[EstPerc]
					, sce.name SchemaName' +
					CASE 
						WHEN OBJECT_ID(N'[sys].dm_db_stats_properties') IS NOT NULL 
						THEN ', sum(ddsp.modification_counter) ' 
						ELSE ', sum(pc.modified_count) ' 
					END
						+' ModificationCount
						, MAX(
								STATS_DATE(s.object_id, s.stats_id)
							 ) AS [LastUpdated]
				FROM [sys].system_internals_partition_columns pc
				INNER JOIN [sys].partitions p ON pc.partition_id = p.partition_id
				INNER JOIN [sys].stats s ON s.object_id = p.object_id 
				AND s.stats_id = p.index_id
				INNER JOIN [sys].stats_columns sc ON sc.object_id = s.object_id 
				AND sc.stats_id = s.stats_id 
				AND sc.stats_column_id = pc.partition_column_id
				INNER JOIN [sys].tables t ON t.object_id = s.object_id
				INNER JOIN [sys].schemas sce ON sce.schema_id = t.schema_id' + 
				CASE WHEN OBJECT_ID(N'[sys].dm_db_stats_properties') IS NOT NULL 
				THEN ' OUTER APPLY [sys].dm_db_stats_properties(s.object_id, s.stats_id) ddsp 
				WHERE ddsp.modification_counter > 0 
				GROUP BY p.object_id, p.index_id, s.name,sce.name' 
				ELSE ' GROUP BY p.object_id, p.index_id, s.name,sce.name 
				HAVING sum(pc.modified_count)> 0 ' END
				+'
			) stats
			WHERE ObjectNm NOT LIKE ''sys%'' 
			AND ModificationCount != 0
			AND ObjectNm NOT LIKE ''ifts_comp_fragment%''
			AND ObjectNm NOT LIKE ''fulltext_%''
			AND ObjectNm NOT LIKE ''filestream_%''
			AND ObjectNm NOT LIKE ''queue_messages_%''
			AND Rows > 500
			AND  CASE WHEN Rows = 0 THEN 0 ELSE CONVERT(MONEY,ModificationCount)*100/Rows END >= [EstPerc]
			AND LastUpdated < DATEADD(DAY, - 1, GETDATE())
			ORDER BY ObjectNm, StatsName 
			OPTION (RECOMPILE);
			';

			BEGIN TRY
				INSERT #output_sqldba_org_sp_triage_Action_Statistics
			EXEC sp_executesql @dynamicSQL;
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Looking for stale statistics' + ISNULL(N' ' + QUOTENAME(@DatabaseName), N''), N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END CATCH
		
			
		SET @CustomErrorText = '['+@DatabaseName+'] Skipping bad NC Indexes tables'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END

		   SET @dynamicSQL = 'SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
			USE '+QUOTENAME(@DatabaseName)+';
		   -- Possible Bad NC Indexes (writes > reads)  (Query 52) (Bad NC Indexes)
			SELECT OBJECT_NAME(s.[object_id]) AS [Table Name], i.name AS [Index Name], i.index_id, 
			i.is_disabled, i.is_hypothetical, i.has_filter, i.fill_factor,
			user_updates AS [Total Writes], user_seeks + user_scans + user_lookups AS [Total Reads],
			user_updates - (user_seeks + user_scans + user_lookups) AS [Difference]
			FROM [sys].dm_db_index_usage_stats AS s WITH (NOLOCK)
			INNER JOIN [sys].indexes AS i WITH (NOLOCK)
			ON s.[object_id] = i.[object_id]
			AND i.index_id = s.index_id
			WHERE OBJECTPROPERTY(s.[object_id],''IsUserTable'') = 1
			AND s.database_id = DB_ID()
			AND user_updates > (user_seeks + user_scans + user_lookups)
			AND i.index_id > 1
			ORDER BY [Difference] DESC, [Total Writes] DESC, [Total Reads] ASC OPTION (RECOMPILE);
			'
			--EXEC sp_executesql @dynamicSQL;
			IF 1 = 1234 /*DEPRECATED*/
			BEGIN
			BEGIN TRY
				/*INSERT #output_sqldba_org_sp_triage_indexusage
				EXEC sp_executesql @dynamicSQL;*/
				PRINT ''
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Bad nonclustered indexes' + ISNULL(N' ' + QUOTENAME(@DatabaseName), N''), N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END CATCH
			END
			/*----------------------------------------
			--Find badly behaving constraints
			----------------------------------------*/

			/* Constraints behaving badly*/
	SET @CustomErrorText = '['+@DatabaseName+'] Looking for bad constraints'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END

			/* Fix text runs as written (lane bundle-sp-triage-5): DBCC CHECKCONSTRAINTS takes no database name, so each row starts
			   with USE <database>; and names the constraint as N'[schema].[constraint]' (a constraint outside dbo is not found by
			   its bare name, error 2501). The ALTER TABLE row carries the database in its three-part name. Guarded by
			   SpTriageFixTextShapeCensusTests. */
			SET @dynamicSQL = 'SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
				USE '+QUOTENAME(@DatabaseName)+';
			IF EXISTS
			(
				SELECT 1
				FROM [sys].check_constraints i 
				WHERE i.is_not_trusted = 1 AND i.is_not_for_replication = 0 AND i.is_disabled = 0 
			)
			BEGIN
				INSERT  #output_sqldba_org_sp_triage_notrust (KeyType, Tablename, KeyName, DBCCcommand, Fix)
				SELECT 
					''Check'' as [KeyType]
					, '''+REPLACE(QUOTENAME(@DatabaseName), N'''', N'''''')+'.'' + QUOTENAME(s.name) + ''.'' + QUOTENAME(o.name) [tablename]
					, '''+REPLACE(QUOTENAME(@DatabaseName), N'''', N'''''')+'.'' + QUOTENAME(s.name) + ''.'' + QUOTENAME(o.name) + ''.'' + QUOTENAME(i.name) AS keyname
					, ''USE '+REPLACE(QUOTENAME(@DatabaseName), N'''', N'''''')+'; DBCC CHECKCONSTRAINTS (N'''''' + REPLACE(QUOTENAME(s.name) + ''.'' + QUOTENAME(i.name), '''''''', '''''''''''') + '''''') WITH ALL_ERRORMSGS'' [DBCC]
					, ''ALTER TABLE '+REPLACE(QUOTENAME(@DatabaseName), N'''', N'''''')+'.'' + QUOTENAME(s.name) + ''.'' + QUOTENAME(o.name) + '' WITH CHECK CHECK CONSTRAINT '' + QUOTENAME(i.name) [Fix]
				FROM [sys].check_constraints i
				INNER JOIN [sys].objects o ON i.parent_object_id = o.object_id
				INNER JOIN [sys].schemas s ON o.schema_id = s.schema_id
				WHERE i.is_not_trusted = 1 
				AND i.is_not_for_replication = 0 
				AND i.is_disabled = 0

				OPTION (RECOMPILE)
			END
			;

			IF EXISTS
			(
				SELECT 1
				FROM [sys].foreign_keys i
					INNER JOIN [sys].objects o ON i.parent_object_id = o.OBJECT_ID
					INNER JOIN [sys].schemas s ON o.schema_id = s.schema_id
				WHERE   i.is_not_trusted = 1
				AND i.is_not_for_replication = 0
				AND i.is_disabled = 0 	
			)
			BEGIN
				INSERT  #output_sqldba_org_sp_triage_notrust (KeyType, Tablename, KeyName, DBCCcommand, Fix)
				SELECT 
					''FK'' as[ KeyType]
					,  '''+REPLACE(QUOTENAME(@DatabaseName), N'''', N'''''')+'.'' + QUOTENAME(s.name) + ''.'' + QUOTENAME(o.name) AS TableName
					, '''+REPLACE(QUOTENAME(@DatabaseName), N'''', N'''''')+'.'' + QUOTENAME(s.name) + ''.'' + QUOTENAME(o.name) + ''.'' + QUOTENAME(i.name) AS FKName
					,''USE '+REPLACE(QUOTENAME(@DatabaseName), N'''', N'''''')+'; DBCC CHECKCONSTRAINTS (N'''''' + REPLACE(QUOTENAME(s.name) + ''.'' + QUOTENAME(i.name), '''''''', '''''''''''') + '''''') WITH ALL_ERRORMSGS'' [DBCC]
					, ''ALTER TABLE '+REPLACE(QUOTENAME(@DatabaseName), N'''', N'''''')+'.'' + QUOTENAME(s.name) + ''.'' + QUOTENAME(o.name) + '' WITH CHECK CHECK CONSTRAINT '' + QUOTENAME(i.name) [Fix]
				FROM    [sys].foreign_keys i
					INNER JOIN [sys].objects o ON i.parent_object_id = o.OBJECT_ID
					INNER JOIN [sys].schemas s ON o.schema_id = s.schema_id
				WHERE   i.is_not_trusted = 1
					AND i.is_not_for_replication = 0
					AND i.is_disabled = 0
				ORDER BY o.name  
				OPTION (RECOMPILE)
			END
		'
		--PRINT @dynamicSQL

			BEGIN TRY
				EXEC sp_executesql @dynamicSQL;
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Looking for bad constraints' + ISNULL(N' ' + QUOTENAME(@DatabaseName), N''), N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END CATCH


			SET @CustomErrorText = '['+@DatabaseName+'] Looking for indexes with possible compression benefit'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END
			SET @dynamicSQL = '
			USE '+QUOTENAME(@DatabaseName)+'
			SELECT TOP 10 N'''+REPLACE(@DatabaseName, N'''', N'''''')+N'''
			, CASE 
				WHEN i.index_id >0 THEN ''ALTER INDEX ''+ QUOTENAME(i.[name])  + '' ON '' 
				ELSE''ALTER TABLE '' 
			END  
			+ '''+REPLACE(QUOTENAME(@DatabaseName), N'''', N'''''')+''' + ''.'' + QUOTENAME(s.[name]) + ''.'' + QUOTENAME(o.[name]) 
			+ '' REBUILD WITH (DATA_COMPRESSION=PAGE);''
			[Just compress]
			, CASE 
				WHEN ps.lob_used_page_count > 0 THEN  ''ALTER INDEX ''+ QUOTENAME(i.[name]) + '' ON '' + '''+REPLACE(QUOTENAME(@DatabaseName), N'''', N'''''')+''' + ''.'' +  QUOTENAME(s.[name]) + ''.'' + QUOTENAME(o.[name]) + '' reorganize WITH (LOB_COMPACTION = ON);''
				ELSE '''' END [For LOB data]
			, ps.[reserved_page_count]
			, ps.row_count
			, p.data_compression_desc
			FROM [sys].objects AS o WITH (NOLOCK)
				INNER JOIN [sys].indexes AS i WITH (NOLOCK)
				ON o.[object_id] = i.[object_id]
				INNER JOIN [sys].schemas s WITH (NOLOCK)
				ON o.[schema_id] = s.[schema_id]
				INNER JOIN [sys].partitions p
				ON  i.object_id = p.object_id
				AND i.index_id = p.index_id
				INNER JOIN [sys].dm_db_partition_stats AS ps WITH (NOLOCK)
				ON i.[object_id] = ps.[object_id]
				AND ps.[index_id] = i.[index_id]
			LEFT OUTER JOIN
			(
				SELECT
					TABLE_CATALOG
					,TABLE_SCHEMA [schema]
					,TABLE_NAME [table]
					, COUNT(COLUMN_NAME) [LobColumns]
				FROM INFORMATION_SCHEMA.COLUMNS 
				where DATA_TYPE in 
					(''TEXT'', ''NTEXT'',''IMAGE'' ,''XML'', ''VARBINARY'')
				OR
					(DATA_TYPE IN( ''[VARCHAR]'',''[NVARCHAR]'') and CHARACTER_MAXIMUM_LENGTH = -1)
				GROUP BY TABLE_CATALOG,TABLE_SCHEMA,TABLE_NAME
			) Lobs ON Lobs.[schema] = s.name AND Lobs.[table] = o.name AND TABLE_CATALOG = DB_NAME()
			WHERE 
			o.type = ''U'' AND i.[index_id] > 0
			AND i.type_desc IN (''CLUSTERED'', ''NONCLUSTERED'')
			AND ps.row_count > 1000
			AND p.data_compression_desc NOT IN (''PAGE'',''ROW'')
			ORDER BY ps.[reserved_page_count] DESC'
			BEGIN TRY
			--PRINT @dynamicSQL
			INSERT #output_sqldba_org_sp_triage_SqueezeMe
			EXEC sp_executesql @dynamicSQL;
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Looking for indexes with possible compression benefit' + ISNULL(N' ' + QUOTENAME(@DatabaseName), N''), N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END CATCH
		

		SET @CustomErrorText = '['+@DatabaseName+'] Intelligent compression evaluation'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
	END

			SET @dynamicSQL=' 
			SELECT 
			TOP 10 
			N'''+REPLACE(@DatabaseName, N'''', N'''''')+N'''
			, i.[name] AS IndexName
			, i.index_id,p.partition_number
			, t.[name] AS TableName
			, i.is_disabled
			, i.is_hypothetical
			, SUM(s.[used_page_count]) * 8 AS IndexSizeKB
			, SUM(p.rows) AS RowCounts
			, p.data_compression_desc Compression
			, CASE 
				WHEN i.index_id in (0, 1) then ''Table'' 
				ELSE ''Index'' 
			END CompressionObject
			, CASE 
				WHEN i.index_id >0 THEN ''ALTER INDEX ''+ QUOTENAME(i.[name])  + '' ON '' 
				ELSE ''ALTER TABLE '' 
			END  + '''+REPLACE(QUOTENAME(@DatabaseName), N'''', N'''''')+''' 
			+ ''.'' + QUOTENAME(sc.[name]) + ''.'' + QUOTENAME(o.[name]) 
			+ '' REBUILD WITH (DATA_COMPRESSION=PAGE);''
			[Just compress]
			, CASE 
				WHEN SUM(s.lob_used_page_count )> 0 THEN  ''ALTER INDEX ''+ QUOTENAME(i.[name]) + '' ON '' + '''+REPLACE(QUOTENAME(@DatabaseName), N'''', N'''''')+''' + ''.'' +  QUOTENAME(sc.[name]) + ''.'' + QUOTENAME(o.[name]) + '' reorganize WITH (LOB_COMPACTION = ON);''
				ELSE '''' 
			END [For LOB data]
			FROM '+QUOTENAME(@DatabaseName)+'.[sys].dm_db_partition_stats AS s
			INNER JOIN '+QUOTENAME(@DatabaseName)+'.[sys].indexes AS i 
				ON s.[object_id] = i.[object_id] AND s.[index_id] = i.[index_id]
			INNER JOIN '+QUOTENAME(@DatabaseName)+'.[sys].tables t 
				ON t.OBJECT_ID = i.object_id
			INNER JOIN '+QUOTENAME(@DatabaseName)+'.[sys].partitions p 
				ON i.object_id = p.OBJECT_ID AND i.index_id = p.index_id
			INNER JOIN '+QUOTENAME(@DatabaseName)+'.[sys].objects AS o WITH (NOLOCK)	ON o.[object_id] = i.[object_id]
			INNER JOIN '+QUOTENAME(@DatabaseName)+'.[sys].schemas sc WITH (NOLOCK)	ON o.[schema_id] = sc.[schema_id]
			WHERE p.data_compression_desc NOT IN (''ROW'',''PAGE'')
			GROUP BY i.[name],sc.[name], t.[name],o.[name] ,i.index_id,i.is_disabled
			, i.is_hypothetical, p.data_compression_desc,p.partition_number
			HAVING SUM(s.[used_page_count]) * 8 > 512
			ORDER BY  SUM(s.[used_page_count]) DESC, t.[name],i.[name]
			' 
			BEGIN TRY
			--PRINT @dynamicSQL
			INSERT #output_sqldba_org_sp_triage_compressionstates 
			EXEC sp_executesql @dynamicSQL;
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Intelligent compression evaluation' + ISNULL(N' ' + QUOTENAME(@DatabaseName), N''), N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END CATCH


	SET @CustomErrorText = '['+@DatabaseName+'] Blocking tables'
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
	END

			SELECT @dynamicSQL = ' USE '+QUOTENAME(@DatabaseName)+'
			SELECT 
			TOP 5 
				N'''+REPLACE(@DatabaseName, N'''', N'''''')+N''' DatabaseName
				, object_name(object_id) as ObjectName
				, ISNULL(row_lock_count,0) + ISNULL(page_lock_count,0) as LocksCount
				, ISNULL(row_lock_wait_count,0) + ISNULL(page_lock_wait_count,0) as BlocksCount
				, ISNULL(row_lock_wait_in_ms,0) + ISNULL(page_lock_wait_in_ms,0) as BlocksWaitTimeMs	
				, index_id
				, ISNULL(page_io_latch_wait_count,0) + ISNULL(tree_page_io_latch_wait_count,0) [page_io_latch_wait_count]
				, ISNULL(page_io_latch_wait_in_ms,0) + ISNULL(tree_page_io_latch_wait_in_ms,0) [page_io_latch_wait_in_ms]
				, page_compression_success_count
				/*, page_io_latch_wait_in_ms*/
				, ISNULL(range_scan_count,0)		[range_scan_count]
				, ISNULL(singleton_lookup_count,0)	[singleton_lookup_count]
				, ISNULL(forwarded_fetch_count,0)	[forwarded_fetch_count]
				, ISNULL(lob_fetch_in_bytes,0)		[lob_fetch_in_bytes]
				, ISNULL(lob_orphan_create_count,0)	[lob_orphan_create_count]
				, ISNULL(lob_orphan_insert_count,0)	[lob_orphan_insert_count]
				, ISNULL(leaf_ghost_count,0)		[leaf_ghost_count]
				, ISNULL(leaf_insert_count,0) + ISNULL(nonleaf_insert_count,0) [insert_count]
				, ISNULL(leaf_delete_count,0) + ISNULL(nonleaf_delete_count,0) [delete_count]
				, ISNULL(leaf_update_count,0) + ISNULL(nonleaf_update_count,0) [update_count]
				, ISNULL(leaf_allocation_count,0) + ISNULL(nonleaf_allocation_count,0) [allocation_count]
				, ISNULL(leaf_page_merge_count,0) + ISNULL(nonleaf_page_merge_count,0) [page_merge_count]
			FROM [sys].dm_db_index_operational_stats(DB_ID(),NULL,NULL,NULL) /*perf 2026-07-02: scope to current DB at source (was NULL=whole-instance scan repeated per looped DB)*/
			WHERE db_name(database_id) = DB_NAME()
			AND row_lock_count + page_lock_count > 0
			AND object_name(object_id) NOT LIKE ''sys%''
			ORDER BY row_lock_count + page_lock_count DESC
			' 
			BEGIN TRY
			--PRINT 'blocking'
			--PRINT @dynamicSQL
			INSERT #output_sqldba_org_sp_triage_blockinghistory 
			EXEC sp_executesql @dynamicSQL;
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Blocking tables' + ISNULL(N' ' + QUOTENAME(@DatabaseName), N''), N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END CATCH



		SET @CustomErrorText = '['+@DatabaseName+'] FKs without Indexes'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END

		/* The FK index name is LEFT(..., 128) before QUOTENAME, so a long column name gives a name, not NULL (lane
		   bundle-sp-triage-5; SpTriageFixTextShapeCensusTests). */
		SELECT @dynamicSQL = ' USE '+QUOTENAME(@DatabaseName)+'

		SELECT 
			DISTINCT 
			N'''+REPLACE(@DatabaseName, N'''', N'''''')+N''' DatabaseName
			, OBJECT_NAME(a.parent_object_id) AS TableName
			, b.[name]  AS Column_Name
			, N''CREATE NONCLUSTERED INDEX '' + QUOTENAME(LEFT(N''IX_SQLDBA_FKI_'' + b.[name], 128)) + N'' ON ''
			+ QUOTENAME(OBJECT_SCHEMA_NAME(a.parent_object_id)) + N''.'' + QUOTENAME(OBJECT_NAME(a.parent_object_id)) + N''('' + QUOTENAME(b.[name]) + N'' ASC);'' [IndexStatement]
		FROM 
			[sys].foreign_key_columns a
		   ,[sys].all_columns b
		   ,[sys].objects c
		WHERE 
		   a.parent_column_id = b.column_id
		   AND a.parent_object_id = b.object_id
		   AND b.object_id = c.object_id
		   AND c.is_ms_shipped = 0
		   AND NOT EXISTS (
		SELECT 
		   Object_name(a1.object_id)
		FROM 
		   [sys].index_columns a1
		WHERE 
		   a1.object_id = b.object_id
		   AND a1.key_ordinal = 1
		   AND a1.column_id = b.column_id
		   AND a1.object_id = c.object_id)
			' 
			BEGIN TRY
			--PRINT 'blocking'
			--PRINT @dynamicSQL
			INSERT #output_sqldba_org_sp_triage_FKNOIndex 
			EXEC sp_executesql @dynamicSQL;
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'FKs without Indexes' + ISNULL(N' ' + QUOTENAME(@DatabaseName), N''), N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END CATCH

/*
LOOPING of databases ends here, add new sections above this portion
*/

/*Now increment databases*/
		END
		END
		SET @Databasei_Count = @Databasei_Count + 1; 
	END
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Evaluated all databases',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Output results from all databases into results table
			----------------------------------------*/
			SET @CustomErrorText = 'Looking for Stored Procedure Workload' /* after the per-database loop: no database name (member 3 class) */
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END

			BEGIN TRY

				INSERT #output_sqldba_org_sp_triage_db_sps
				(
					[dbname] 
					, [SP Name]
					, [TotalLogicalWrites]
					, [AvgLogicalWrites]
					, execution_count
					, [Calls/Second]
					, [total_elapsed_time] 
					, [avg_elapsed_time]
					, cached_time 
				)
				SELECT 
					DB_NAME(st.dbid)
					, CASE WHEN rdb.database_id IS NOT NULL THEN OBJECT_NAME(st.objectid, st.dbid) END AS [SP Name]
					, SUM(total_logical_writes) [TotalLogicalWrites]
					, SUM(total_logical_writes) / SUM(usecounts) AS [AvgLogicalWrites]
					, SUM(usecounts) [execution_count]
					, ISNULL(
					CASE 
						WHEN DATEDIFF(SECOND, MIN(qs.creation_time), GETDATE()) <5 
						THEN SUM(usecounts)/DATEDIFF(MILLISECOND, MIN(qs.creation_time), GETDATE())/1000
						ELSE SUM(usecounts)/DATEDIFF(SECOND, MIN(qs.creation_time), GETDATE())
					END ,0) [Calls/Second]
					, SUM(total_elapsed_time) [total_elapsed_time]
					, SUM(total_elapsed_time) / SUM(usecounts) AS [avg_elapsed_time]
					, MIN(qs.creation_time) [cached_time]
					FROM  #dadatafor_exec_query_stats qs  
				JOIN [sys].dm_exec_cached_plans cp on qs.plan_handle = cp.plan_handle 
				CROSS APPLY [sys].dm_exec_sql_text(qs.sql_handle) AS st /* member 1: the SQL handle, see Top queries */
				LEFT OUTER JOIN #sp_triage_readable_db AS rdb ON rdb.database_id = st.dbid
				WHERE 1=1
				AND qs.plan_handle IN (SELECT rp.plan_handle FROM #sp_triage_readable_plan AS rp)
				AND rdb.database_id IS NOT NULL
				AND st.dbid IS NOT NULL
				AND DB_NAME(st.dbid) IS NOT NULL
				AND st.objectid is not null
				GROUP BY cp.plan_handle, st.dbid, st.objectid, rdb.database_id

			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Looking for Stored Procedure Workload', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END CATCH


BEGIN TRY
	IF EXISTS (SELECT 1 FROM #output_sqldba_org_sp_triage_MissingIndex ) 
	BEGIN
				SET @CustomErrorText = 'MISSING INDEXES - !Benefit > 1mm!'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
	END
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;

				BEGIN TRY
		INSERT #output_sqldba_org_sp_triage 
		(
			[SectionID]
			, [Section]
			, [Summary]
			, [Details]
		) 
		SELECT 
		18
		, 'MISSING INDEXES - !Benefit > 1mm!'
		,'------'
		,'SELECT ''All your index are belong to us, SET STATISTICS PROFILE ON;
		SELECT sp.spid,sp.cmd,sp.hostname,sp.last_batch,node_id,physical_operator_name,SUM(row_count) row_count,SUM(estimate_row_count) AS estimate_row_count,
		CAST(SUM(row_count)*100 AS float)/SUM(estimate_row_count) as EST_COMPLETE_PERCENT
		FROM [sys].dm_exec_query_profiles eqp
		INNER JOIN [sys].sysprocesses sp on sp.spid=eqp.session_id AND (sp.cmd like ''''%INDEX%'''' OR sp.cmd like ''''%ALTER%'''')
		GROUP BY spid, node_id, physical_operator_name, sp.cmd, sp.hostname, sp.last_batch
		ORDER BY spid, node_id desc;'' '

		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary 
			, Severity
			, Details
			, HoursToResolveWithTesting 
		)
			SELECT 
			18
			, REPLICATE('|',ROUND(LOG(T1.magic_benefit_number),0)) + ' ' + CONVERT([VARCHAR](20),LOG(T1.magic_benefit_number)) + '' 
			/* Member 2 (lane bundle-sp-triage-fixes-b): the Summary column is NVARCHAR(4000); CONVERT(NVARCHAR(40)) cut every row to
			   ~20 characters of table name. Token order and separators are unchanged: SQLDBA.ORG step6 parses them. */
			, LEFT('Benefit:'+  CONVERT([VARCHAR](20),CONVERT([BIGINT],T1.magic_benefit_number),0)
				+ '; ' + T1.[Table]
				+ '; Eq:' + ISNULL(T1.equality_columns,'')
				+ '; Ineq:' +  ISNULL(T1.inequality_columns,'')
				+ '; Incl:' +  ISNULL(T1.included_columns,''), 4000)
			, CASE 
				WHEN LOG(T1.magic_benefit_number)  < 13 THEN @Result_Warning 
				WHEN LOG(T1.magic_benefit_number) >= 13 AND LOG(T1.magic_benefit_number) < 20 THEN @Result_YourServerIsDead  
				WHEN LOG(T1.magic_benefit_number) >= 20  THEN @Result_ReallyBad
			END
			/* Details: ONE CREATE NONCLUSTERED INDEX ...; plus one comment, or ONE NOTE sp_triage comment, at most 4,000 characters,
			   built above (lane sp-triage-section-18-index-suggestions). */
			, T1.Details
			, CASE 
				WHEN LOG(T1.magic_benefit_number) >= 10 AND LOG(T1.magic_benefit_number) < 12 THEN 1
				WHEN LOG(T1.magic_benefit_number) >= 12 AND LOG(T1.magic_benefit_number) < 14 THEN 2
				WHEN LOG(T1.magic_benefit_number) >= 14 AND LOG(T1.magic_benefit_number) < 16 THEN 4
				WHEN LOG(T1.magic_benefit_number) >= 16 AND LOG(T1.magic_benefit_number) < 20 THEN 6
				WHEN LOG(T1.magic_benefit_number) >= 20 AND LOG(T1.magic_benefit_number) < 25  THEN 8
				WHEN LOG(T1.magic_benefit_number) > 25  THEN 12
			END
			FROM #output_sqldba_org_sp_triage_MissingIndex T1 
			WHERE T1.magic_benefit_number > 50000
			AND T1.Keep = 1 /* an identical suggestion with a lower benefit is collapsed into the higher one */
			ORDER BY magic_benefit_number DESC OPTION (RECOMPILE)

			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Missing index details output', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END CATCH

	END
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Missing index details', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	RAISERROR (N'Error with missing index details',0,1) WITH NOWAIT;
	SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
	SELECT @errMessage  = ERROR_MESSAGE()
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
			RAISERROR (N'Completed missing index details',0,1) WITH NOWAIT;
		END


BEGIN TRY
		IF EXISTS (SELECT 1 FROM #output_sqldba_org_sp_triage_HeapTable ) 
	BEGIN
					SET @CustomErrorText = 'HEAP TABLES - Bad news'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END

				BEGIN TRY
		INSERT #output_sqldba_org_sp_triage 
		(
			[SectionID]
			, [Section]
			, [Summary]
			, [Details]
		) 
		SELECT 
			19
			, 'HEAP TABLES - Bad news'
			,'------'
			,'------'

		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary 
			, Severity
			, Details
			, HoursToResolveWithTesting 
		)
			SELECT 
				19
				, LEFT(REPLICATE('|', (ISNULL(user_scans,0)+ ISNULL(user_seeks,0) + ISNULL(user_lookups,0) + ISNULL(user_updates,0))/100) + CONVERT([VARCHAR](20),(ISNULL(user_scans,0)+ ISNULL(user_seeks,0) + ISNULL(user_lookups,0) + ISNULL(user_updates,0))/100) ,2500)
				, LEFT('Rows:' + CONVERT([VARCHAR](20),T1.rows)
					+ ';'+ QUOTENAME(T1.DB) + '.' + QUOTENAME(T1.[schema]) + '.' + QUOTENAME(T1.[table]) 
					+ '; Scan:' + CONVERT([VARCHAR](20),ISNULL(T1.last_user_scan,0) ,120)
					+ '; Seek:' + CONVERT([VARCHAR](20),ISNULL(T1.last_user_seek,0) ,120)
					+ '; Lookup:' + CONVERT([VARCHAR](20),ISNULL(T1.last_user_lookup,0) ,120),3800)
				, @Result_Warning
				, LEFT('/*DIRTY FIX, assuming forwarded records*/ALTER TABLE ' + QUOTENAME(T1.DB) + '.' + QUOTENAME(T1.[schema]) + '.' + QUOTENAME(T1.[table]) + ' REBUILD ; RAISERROR (N''Completed heap '
					+ REPLACE(REPLACE(QUOTENAME(T1.DB) + '.' + QUOTENAME(T1.[schema]) + '.' + QUOTENAME(T1.[table]), '''', ''''''), '%', '%%') + ''' ,0,1) WITH NOWAIT',3800)
				, 3
				/*SELECT
				OBJECT_NAME(ps.object_id) as TableName,
				i.name as IndexName,
				ps.index_type_desc,
				ps.page_count,
				ps.avg_fragmentation_in_percent,
				ps.forwarded_record_count
			FROM [sys].dm_db_index_physical_stats (DB_ID(), NULL, NULL, NULL, 'DETAILED') AS ps
			INNER JOIN [sys].indexes AS i
				ON ps.OBJECT_ID = i.OBJECT_ID  
				AND ps.index_id = i.index_id
			WHERE */
			FROM #output_sqldba_org_sp_triage_HeapTable T1 
			WHERE ForwardedCount > 0
			--WHERE T1.rows > 500
			ORDER BY (ISNULL(user_scans,0)+ ISNULL(user_seeks,0) + ISNULL(user_lookups,0) + ISNULL(user_updates,0)) DESC,  DB OPTION (RECOMPILE);
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Heap tables output', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END CATCH
	END
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Heap tables', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	RAISERROR (N'Error with finding heaps',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
	SELECT @errMessage  = ERROR_MESSAGE()
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Found heap tables',0,1) WITH NOWAIT;
	END


BEGIN TRY
		IF EXISTS (SELECT 1 FROM #NeverUsedIndex ) 
	BEGIN
		SET @CustomErrorText = 'STALE INDEXES - Consider removing them at some stage'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END

		BEGIN TRY
		INSERT #output_sqldba_org_sp_triage 
		(
			[SectionID]
			, [Section]
			, [Summary]
			, [Details]
		) 
		SELECT 
			20
			, 'STALE INDEXES - Consider removing them at some stage'
			,'------'
			,'------'

		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary 
			, Details 
		)

		SELECT 
			20
			, REPLICATE('|', LOG(CONVERT([BIGINT],rows)))
						, 'Table: ' + nui.TableName
						+ '; Updates: '+  CONVERT([VARCHAR](20),Updates)
						+ '; Rows: ' +CONVERT([NVARCHAR] (20),rows) 
						, 'DB:' + DB
						+ '; Table:' + nui.TableName	
						+ '; StaleIndexes: ' + CONVERT([NVARCHAR] (20),IndexCount)
			FROM 
			(
				SELECT 
				DB
				, TableName
				, COUNT(DISTINCT IndexName) As IndexCount
				, MAX(Updates) As Updates
				FROM #NeverUsedIndex 
				WHERE TypeDesc <> 'CLUSTERED'
				GROUP BY DB
				, TableName
			) 
			nui
			INNER JOIN (
				SELECT OBJECT_NAME(object_id) TableName, SUM(row_count) rows 
				FROM [sys].dm_db_partition_stats 
				WHERE index_id < 2
				GROUP BY object_id

			)t2 ON nui.TableName COLLATE DATABASE_DEFAULT = t2.TableName COLLATE DATABASE_DEFAULT
			WHERE Updates/rows > 1
			AND rows > 0
			AND rows > 90
			ORDER BY rows DESC, nui.TableName ASC
			OPTION (RECOMPILE)
	 	END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Stale statistics output', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
			END CATCH
	END
		IF EXISTS (SELECT 1 FROM #output_sqldba_org_sp_triage_Action_Statistics ) 
	BEGIN
	SET @CustomErrorText = 'Working through statistics'
		INSERT #output_sqldba_org_sp_triage 
		(
			[SectionID]
			, [Section]
			, [Summary]
			, [Details]
		)
		SELECT 
			21
			, 'STALE STATS - Consider updating these'
			,'------'
			,'------'

		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary
			, Severity
			, Details
			, HoursToResolveWithTesting 
		)
		SELECT 
			21
			, CONVERT([VARCHAR](20),DATEDIFF(DAY,s.LastUpdated,GETDATE())) +' days old'
			, '%Change:' + CONVERT([VARCHAR](15),s.[ModPerc]) +'%; Rows:' + CONVERT([VARCHAR](15),Rows) + ';Modifications:' + CONVERT([VARCHAR](20),s.ModificationCount) +'; '+ QUOTENAME(DBname) + '.' + QUOTENAME(SchemaName) + '.' + QUOTENAME(TableName) + ':' + QUOTENAME(StatisticsName)
			, CASE WHEN DATEDIFF(DAY,s.LastUpdated,GETDATE()) < 14 THEN @Result_Warning ELSE @Result_Bad END
			, 'UPDATE STATISTICS ' + QUOTENAME(DBname) + '.' + QUOTENAME(SchemaName) + '.' + QUOTENAME(TableName) + ' ' + QUOTENAME(StatisticsName) + ' ' 
			+ CASE 
				WHEN s.Rows BETWEEN 0 AND 500000 THEN 'WITH FULLSCAN' 
				WHEN s.Rows BETWEEN 500000 AND 5000000 THEN 'WITH SAMPLE 20 PERCENT'
				WHEN s.Rows BETWEEN 5000000 AND 50000000 THEN 'WITH SAMPLE 10 PERCENT'
				WHEN s.Rows > 50000000 THEN 'WITH SAMPLE 5 PERCENT'
				ELSE 'WITH SAMPLE ' + CONVERT([VARCHAR](3),CONVERT(INT,EstPerc)*2) + 'PERCENT' 
				END +'; PRINT ''' + REPLACE(QUOTENAME(DBname) + '.' + QUOTENAME(SchemaName) + '.' + QUOTENAME(TableName) + ' ' + QUOTENAME(StatisticsName), '''', '''''') + ' Done ''' [UpdateStats]
			, 0.15
			 FROM #output_sqldba_org_sp_triage_Action_Statistics s 
			 ORDER BY s.[ModPerc] DESC OPTION (RECOMPILE);/*They are like little time capsules.. just sitting there.. waiting*/

	END
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Stale statistics', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with stale stats',0,1) WITH NOWAIT;
	SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Listed state stats',0,1) WITH NOWAIT;
	END

		 /*----------------------------------------
			--Most used database stored procedures
			----------------------------------------*/
BEGIN TRY
		IF EXISTS( SELECT 1 FROM #output_sqldba_org_sp_triage_db_sps)
	BEGIN
		SET @CustomErrorText = 'Top 10 Stored Procedure workload'
		INSERT #output_sqldba_org_sp_triage 
		(
			[SectionID]
			, [Section]
			, [Summary]
			, [Details]
		) 
		SELECT 
		22
		, 'STORED PROCEDURE WORKLOAD - TOP 10'
		,'------'
		,'------'

		INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary 
			, Details 
		)

		SELECT 
		TOP 10 
		22
		, REPLICATE('|', CONVERT(MONEY,execution_count*100) / SUM (execution_count) OVER() ) + ' '+ CONVERT([VARCHAR](20),CONVERT(INT,ROUND(CONVERT(MONEY,execution_count*100) / SUM (execution_count) OVER(),0))) + '%'
		,  [SP Name] + '; Executions:'+ CONVERT([VARCHAR](20),execution_count)
			+ '; Per second:' + CONVERT([VARCHAR](20),[Calls/Second])
		, dbname
			+ '; Avg Time:' + CONVERT([VARCHAR](20), avg_elapsed_time/1000/1000 ) + '(s)'
			+ '; Total time:' + CONVERT([VARCHAR](20), total_elapsed_time/1000/1000 ) + '(s)'
			+ '; Overall time:' + CONVERT([VARCHAR](20),CONVERT(MONEY,total_elapsed_time*100) / SUM (total_elapsed_time) OVER()) +'%'
		FROM #output_sqldba_org_sp_triage_db_sps
		ORDER BY execution_count DESC OPTION (RECOMPILE)

	END
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Top 10 Stored Procedure workload', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with SP workload',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Database stored procedure details',0,1) WITH NOWAIT;
	END
			/*----------------------------------------
			--General server settings and items of note
			----------------------------------------*/
BEGIN TRY
	SET @CustomErrorText = 'Server Details, PLE'
	INSERT #output_sqldba_org_sp_triage 
	(
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 24, 'Server details','------','------'

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary  
	)
	SELECT 24,  @ThisServer AS [Server Name]
	,'Evauation date: ' + CONVERT([VARCHAR](20),GETDATE(),120)

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary  
	)
	SELECT 24,  @ThisServer AS [Server Name]
	,'' +  replace(replace(replace(replace(CONVERT([NVARCHAR] (500),@@VERSION  ), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ')

	INSERT #output_sqldba_org_sp_triage (SectionID,Summary,HoursToResolveWithTesting  )
	SELECT 24, 'Page Life Expectancy: ' + CONVERT([VARCHAR](20), cntr_value)
	, CASE WHEN cntr_value < 100 THEN 4 ELSE NULL END
	FROM [sys].dm_os_performance_counters WITH (NOLOCK)
	WHERE [object_name] LIKE N'%Buffer Node%' -- Handles named instances
	AND counter_name = N'Page life expectancy'  OPTION (RECOMPILE)


	INSERT #output_sqldba_org_sp_triage (SectionID,Summary  )
	SELECT 24, 'Memory Grants Pending:' + CONVERT([VARCHAR](20), cntr_value)                                                                                                    
	FROM [sys].dm_os_performance_counters WITH (NOLOCK)
	WHERE [object_name] LIKE N'%Memory Manager%' -- Handles named instances
	AND counter_name = N'Memory Grants Pending' OPTION (RECOMPILE);
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Server Details, PLE', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with server details',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Listed general instance stats',0,1) WITH NOWAIT;
	END


	/* The default settings have been copied from sp_Blitz from http://FirstResponderKit.org
	Believe it or not, SQL Server doesn't track the default values
	for sp_configure options! We'll make our own list here.*/

	INSERT  INTO #output_sqldba_org_sp_triage_ConfigurationDefaults 
	  
	SELECT name,DefaultValue, CheckID
	FROM
	(
	VALUES ( 'access check cache bucket count', 0, 1001 )
	,( 'access check cache quota', 0, 1002 )
	,( 'Ad Hoc Distributed Queries', 0, 1003 )
	,( 'affinity I/O mask', 0, 1004 )
	,( 'affinity mask', 0, 1005 )
	,( 'affinity64 mask', 0, 1066 )
	,( 'affinity64 I/O mask', 0, 1067 )
	,( 'Agent XPs', 0, 1071 )
	,( 'allow updates', 0, 1007 )
	,( 'awe enabled', 0, 1008 )
	,( 'backup checksum default', 0, 1070 )
	,( 'backup compression default', 0, 1073 )
	,( 'blocked process threshold', 0, 1009 )
	,( 'blocked process threshold (s)', 0, 1009 )
	,( 'c2 audit mode', 0, 1010 )
	,( 'clr enabled', 0, 1011 )
	,( 'common criteria compliance enabled', 0, 1074 )
	,( 'contained database authentication', 0, 1068 )
	,( 'cost threshold for parallelism', 5, 1012 )
	,( 'cross db ownership chaining', 0, 1013 )
	,( 'cursor threshold', -1, 1014 )
	,( 'Database Mail XPs', 0, 1072 )
	,( 'default full-text language', 1033, 1016 )
	,( 'default language', 0, 1017 )
	,( 'default trace enabled', 1, 1018 )
	,( 'disallow results from triggers', 0, 1019 )
	,( 'EKM provider enabled', 0, 1075 )
	,( 'filestream access level', 0, 1076 )
	,( 'fill factor (%)', 0, 1020 )
	,( 'ft crawl bandwidth (max)', 100, 1021 )
	,( 'ft crawl bandwidth (min)', 0, 1022 )
	,( 'ft notify bandwidth (max)', 100, 1023 )
	,( 'ft notify bandwidth (min)', 0, 1024 )
	,( 'index create memory (KB)', 0, 1025 )
	,( 'in-doubt xact resolution', 0, 1026 )
	,( 'lightweight pooling', 0, 1027 )
	,( 'locks', 0, 1028 )
	,( 'max degree of parallelism', 0, 1029 )
	,( 'max full-text crawl range', 4, 1030 )
	,( 'max server memory (MB)', 2147483647, 1031 )
	,( 'max text repl size (B)', 65536, 1032 )
	,( 'max worker threads', 0, 1033 )
	,( 'media retention', 0, 1034 )
	,( 'min memory per query (KB)', 1024, 1035 )
	,( 'nested triggers', 1, 1037 )
	,( 'network packet size (B)', 4096, 1038 )
	,( 'Ole Automation Procedures', 0, 1039 )
	,( 'open objects', 0, 1040 )
	,( 'optimize for ad hoc workloads', 0, 1041 )
	,( 'PH timeout (s)', 60, 1042 )
	,( 'precompute rank', 0, 1043 )
	,( 'priority boost', 0, 1044 )
	,( 'query governor cost limit', 0, 1045 )
	,( 'query wait (s)', -1, 1046 )
	,( 'recovery interval (min)', 0, 1047 )
	,( 'remote access', 1, 1048 )
	,( 'remote admin connections', 0, 1049 )
	,( 'remote proc trans', 0, 1050 )
	,( 'remote query timeout (s)', 600, 1051 )
	,( 'Replication XPs', 0, 1052 )
	,( 'RPC parameter data validation', 0, 1053 )
	,( 'scan for startup procs', 0, 1054 )
	,( 'server trigger recursion', 1, 1055 )
	,( 'set working set size', 0, 1056 )
	,( 'show advanced options', 0, 1057 )
	,( 'SMO and DMO XPs', 1, 1058 )
	,( 'SQL Mail XPs', 0, 1059 )
	,( 'transform noise words', 0, 1060 )
	,( 'two digit year cutoff', 2049, 1061 )
	,( 'user connections', 0, 1062 )
	,( 'user options', 0, 1063 )
	,( 'Web Assistant Procedures', 0, 1064 )
	,( 'xp_cmdshell', 0, 1065 )
	) AS X(name,DefaultValue, CheckID)
	/* Accepting both 0 and 16 below because both have been seen in the wild as defaults. */
	IF EXISTS ( SELECT  *
				FROM    [sys].configurations
				WHERE   name = 'min server memory (MB)'
						AND value_in_use IN ( 0, 16 ) )
		INSERT  INTO #output_sqldba_org_sp_triage_ConfigurationDefaults
				SELECT  'min server memory (MB)' ,
						CAST(value_in_use AS [BIGINT]), 1036
				FROM    [sys].configurations
				WHERE   name = 'min server memory (MB)'
	ELSE
		INSERT  INTO #output_sqldba_org_sp_triage_ConfigurationDefaults
		VALUES  ( 'min server memory (MB)', 0, 1036 );


	/* SQL Server 2012 changes a configuration default */
	IF @@VERSION LIKE '%Microsoft SQL Server 2005%'
		OR @@VERSION LIKE '%Microsoft SQL Server 2008%'
		BEGIN
			INSERT  INTO #output_sqldba_org_sp_triage_ConfigurationDefaults
			VALUES  ( 'remote login timeout (s)', 20, 1069 );
		END
	ELSE
		BEGIN
			INSERT  INTO #output_sqldba_org_sp_triage_ConfigurationDefaults
			VALUES  ( 'remote login timeout (s)', 10, 1069 );
		END
	

BEGIN TRY
	SET @CustomErrorText = 'Non-default server settings'
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	)
	SELECT 25, 'Server details - Non default settings','------','------'
	INSERT #output_sqldba_org_sp_triage (
	SectionID
	, Section
	, Summary
	, Details
	)
	SELECT 25, [description] name
	, '['+CONVERT([VARCHAR](20),cd.[DefaultValue]) + '] changed to [' + CONVERT([VARCHAR](20),value_in_use) + ']'
	, 'Blitz CheckID:' +  CONVERT([VARCHAR](20),cd.CheckID)
	+ '; MIN:' + CONVERT([VARCHAR](20),minimum)
	+ '; MAX:' + CONVERT([VARCHAR](20),maximum)
	+ '; IsDynamic:' + CONVERT([VARCHAR](20),is_dynamic)
	+ '; IsAdvanced:' + CONVERT([VARCHAR](20),is_advanced)
	FROM [sys].configurations cr WITH (NOLOCK)
	INNER JOIN #output_sqldba_org_sp_triage_ConfigurationDefaults cd ON cd.name COLLATE DATABASE_DEFAULT = cr.name COLLATE DATABASE_DEFAULT
	LEFT OUTER JOIN #output_sqldba_org_sp_triage_ConfigurationDefaults cdUsed ON cdUsed.name COLLATE DATABASE_DEFAULT = cr.name COLLATE DATABASE_DEFAULT AND cdUsed.DefaultValue = cr.value_in_use
	WHERE cdUsed.name IS NULL
	OPTION (RECOMPILE);
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Non-default server settings', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with non default server settings',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Listed non-default settings',0,1) WITH NOWAIT;
	END
			/*----------------------------------------
			--Current active logins on this instance
			----------------------------------------*/
BEGIN TRY
	SET @CustomErrorText = 'Current active users'
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 26,'CURRENT ACTIVE USERS - TOP 10','------','------'
	INSERT #output_sqldba_org_sp_triage (SectionID, Section,Summary)
	SELECT TOP 10 26, 'User: ' + login_name
	, '[' + CONVERT([VARCHAR](20), COUNT(session_id) ) + '] sessions using: ' + [program_name]
	FROM [sys].dm_exec_sessions WITH (NOLOCK)
	GROUP BY login_name, [program_name]
	ORDER BY COUNT(session_id) DESC OPTION (RECOMPILE);
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Current active users', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with current active users',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Connections listed',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Insert trust issues into output table
			----------------------------------------*/
BEGIN TRY
	SET @CustomErrorText = 'FK trust checks'
	IF EXISTS(SELECT 1 FROM #output_sqldba_org_sp_triage_notrust )
	BEGIN
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 27,'TRUST ISSUES','------','------'
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		, Severity
	)

	SELECT 27, KeyType + '; Table: '+ Tablename
	+ '; KeyName: ' + KeyName
	, DBCCcommand
	, Fix
	, @Result_Warning
	FROM #output_sqldba_org_sp_triage_notrust 
	OPTION (RECOMPILE)
	END
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'FK trust checks', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with trust issues.. it happens',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH	
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Included Constraint trust issues',0,1) WITH NOWAIT;
	END


			/*----------------------------------------
			--Current active connections on each database
			----------------------------------------*/
BEGIN TRY	
	SET @CustomErrorText = 'Database connected users'
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 29,'DATABASE CONNECTED USERS','------','------'
	INSERT #output_sqldba_org_sp_triage (
	SectionID
	, Section
	, Summary
	, Details
	)
	SELECT  29, dtb.name
	, 'Active: ' + CONVERT([VARCHAR](20),(select count(*) 
	FROM sysprocesses p 
	WHERE dtb.database_id=p.dbid))
	+ '; LastActivity:' +CONVERT([VARCHAR], ISNULL(lastactive.LastActivity,lastactive.create_date),120)
	, 'Updatable: ' + ( CASE LOWER(CONVERT( [NVARCHAR] (128), DATABASEPROPERTYEX(dtb.name, 'Updateability'))) WHEN 'read_write' THEN 'Yes' ELSE 'No' END)
	+ '; ReplicationOptions:' + CONVERT([VARCHAR](20),(dtb.is_published*1+dtb.is_subscribed*2+dtb.is_merge_published*4))
	FROM @sysdatabasesTable AS dtb 

	INNER JOIN (
			SELECT d.name
			, MAX(d.create_date) create_date
			, [LastActivity] =
			(SELECT X1= max(bb.xx) 
			FROM (
				SELECT xx = max(last_user_seek) 
					WHERE max(last_user_seek) is not null 
				UNION ALL 
				SELECT xx = max(last_user_scan) 
					WHERE max(last_user_scan) is not null 
				UNION ALL 
				SELECT xx = max(last_user_lookup) 
					WHERE max(last_user_lookup) is not null 
				UNION ALL 
					SELECT xx = max(last_user_update) 
					WHERE max(last_user_update) is not null) bb) 
			, last_user_seek = MAX(last_user_seek)
			, last_user_scan = MAX(last_user_scan)
			, last_user_lookup = MAX(last_user_lookup)
			, last_user_update = MAX(last_user_update)
			FROM @sysdatabasesTable AS d 
			LEFT OUTER JOIN [sys].dm_db_index_usage_stats AS i ON i.database_id=d.database_id
			GROUP BY d.name
	) lastactive ON lastactive.name = dtb.name
	
	
	OPTION (RECOMPILE);
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Database connected users', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with connected users',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Database Connections counted',0,1) WITH NOWAIT;
	END


			/*----------------------------------------
			--Current likely active databases
			----------------------------------------*/
	
SET @CustomErrorText = 'Current likely active databases'
DECLARE @confidence TABLE (DBName [NVARCHAR] (500), EstHoursSinceActive [BIGINT])
DECLARE @ConfidenceLevel TABLE ( Bionmial MONEY , ConfidenceLevel [NVARCHAR] (10))
INSERT INTO @ConfidenceLevel VALUES(1.96,'95%')

BEGIN TRY
INSERT INTO @confidence
/* Rewritten 2026-07-08: was a correlated 4-way UNION-ALL scalar subquery evaluated per
   database group over the full sysdatabases x index-usage join - pathological on instances
   with many databases/indexes. Same result in one pass: unpivot the four last_user_* columns,
   aggregate per database once, then join. Semantics preserved: databases with NO usage rows
   were (and are) excluded; all-NULL usage dates still fall back to @lastservericerestart. */
SELECT d.name
	, DATEDIFF(HOUR, ISNULL(x.LastActivity, @lastservericerestart), GETDATE())
FROM sysdatabases d
INNER JOIN (
	SELECT s.database_id, MAX(v.LastDate) AS LastActivity
	FROM [sys].dm_db_index_usage_stats s
	CROSS APPLY (VALUES (s.last_user_seek),(s.last_user_scan),(s.last_user_lookup),(s.last_user_update)) v(LastDate)
	WHERE s.database_id > 4
	GROUP BY s.database_id
) x ON d.dbid = x.database_id
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Current likely active databases', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Issue with @confidence',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH

BEGIN TRY
IF @ShowMigrationRelatedOutputs = 1
BEGIN
	/* 2026-07-08, 2026-10-16: the per-DB residency comes from the ONE gated buffer-descriptor read above section 14
	   (@bufferPagesPerDB, behind the @BufferScanMaxGB gate). Past the gate it is empty, so Pages(MB) in memory
	   reports 0 and the 100%-likely shortcut falls through to the connections/confidence estimate. */
	DECLARE @pagesPerDB TABLE (database_id INT PRIMARY KEY, TotalPagesMB BIGINT);
	INSERT @pagesPerDB
	SELECT database_id, db_buffer_pages*8/1024
	FROM @bufferPagesPerDB;

	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	)
	SELECT 30, 'Database usage likelyhood','------','------'
	INSERT #output_sqldba_org_sp_triage (
	SectionID
	, Section
	, Summary
	, Details
	)
	
SELECT  30 [SectionID]
	, base.name [Section]
	/* 2026-07-08 honesty fixes: (a) a database with NO usage-stats rows (untouched since
	   restart) used to render a blank percentage - the strongest decommission signal hidden;
	   (b) open connections are corroborating evidence of use, yet that branch reported the
	   LOWER confidence bound while no-connections got the UPPER - both now use the upper
	   bound and connections can only raise it (min 90); (c) NULL bounds (uptime under ~1h
	   wipes the interval math) now say so instead of rendering blank; (d) 'HoursActive' was
	   server uptime, identical for every row - renamed ServerUptimeHours. The interval is a
	   recency heuristic, NOT a true confidence interval; the Details timestamps are the
	   real evidence. */
	,CASE
		WHEN pages.TotalPagesMB > 0 THEN '100.00% likely'
		WHEN confidence.DBName IS NULL AND ISNULL(con.number_of_connections,0) = 0
			THEN '0.00% likely - no recorded activity since restart'
		WHEN confidence.DBName IS NULL
			THEN 'Unknown - connected now, but no recorded index activity since restart'
		WHEN confidence.high IS NULL
			THEN 'Unknown - server restarted under an hour ago'
		WHEN con.number_of_connections > 0
			THEN CONVERT([VARCHAR](10),CASE WHEN confidence.high < 90 THEN CONVERT(MONEY,90) ELSE confidence.high END) + '% likely'
		ELSE CONVERT([VARCHAR](10),confidence.high) + '% likely'
	END
	+  '; Connections: ' + ISNULL(CONVERT([VARCHAR](10),con.number_of_connections ),'')
	+ '; ServerUptimeHours: '+ ISNULL(CONVERT([VARCHAR](10),DATEDIFF(HOUR,@lastservericerestart,GETDATE())),'')
	+ '; Pages(MB) in memory: ' + ISNULL(CONVERT([VARCHAR](10), ISNULL(pages.TotalPagesMB,0)),'')
	 [Summary]
 
	,  'DB Created: '   + ISNULL(CONVERT([VARCHAR],base.DBcreatedate,120),'')
	+ '; Last seek: '   + ISNULL(CONVERT([VARCHAR],base.[last_user_seek],120),'')
	+ '; Last scan:'    + ISNULL(CONVERT([VARCHAR],base.[last_user_scan],120),'')
	+ '; Last lookup: ' + ISNULL(CONVERT([VARCHAR],base.[last_user_lookup],120),'')
	+ '; Last update: ' + ISNULL(CONVERT([VARCHAR],base.[last_user_update],120),'') 
	+ '; LogSpaceMB:' + ISNULL(CONVERT([VARCHAR](30),[log_size_mb]),'')
    + '; DataSpaceMB:'+ ISNULL(CONVERT([VARCHAR](30),[row_size_mb]),'')
    + '; TotalSizeMB:'+ ISNULL(CONVERT([VARCHAR](30),[total_size_mb]),'')[Details]

	FROM (
	SELECT db.name, db.database_id
	, MAX(db.create_date) [DBcreatedate]
	/* 2026-07-08: MAX(o.modify_date) [ObjectModifyDate] and its sys.all_objects join removed -
	   the column was never used in the output, and the join was both a row explosion (every
	   cross-database index-usage row matched against the CURRENT database's objects) and
	   semantically wrong (object_ids from other databases resolved against local objects). */
	, MAX(ius.last_user_seek)    [last_user_seek]
	, MAX(ius.last_user_scan)   [last_user_scan]
	, MAX(ius.last_user_lookup) [last_user_lookup]
	, MAX(ius.last_user_update) [last_user_update]
	FROM
		@sysdatabasesTable db
		LEFT OUTER JOIN [sys].dm_db_index_usage_stats ius  ON db.database_id = ius.database_id
	WHERE
		db.database_id > 4 AND state NOT IN (1,2,3,6) AND user_access = 0
	GROUP BY 
		db.name, db.database_id
	) base

	LEFT OUTER JOIN (
		SELECT name AS dbname
		 ,COUNT(status) AS number_of_connections
		FROM @sysdatabasesTable sd
		LEFT JOIN [sys].sysprocesses sp ON sd.database_id = sp.dbid
		WHERE database_id > 4
		GROUP BY name
	) con ON con.dbname = base.name
	LEFT OUTER JOIN @pagesPerDB pages ON pages.database_id = base.database_id

	LEFT OUTER JOIN (
	SELECT DBName
	  , intervals.n as [Hours]
	  , intervals.x as [TargetActiveHours]
	  , CONVERT(MONEY,(p - se * 1.96)*100) as low
	  , CONVERT(MONEY,(intervals.p * 100)) as mid
	  , CONVERT(MONEY,CASE WHEN (p + se * 1.96) > 1 THEN 100 ELSE (p + se * 1.96)*100 END) as high -- 2026-07-08: capped at 100 (the normal-approx upper bound can exceed it)
	FROM (
	  SELECT 
		rates.*,
		sqrt(p * (1 - p) / NULLIF(n,0)) as se -- calculate se (NULLIF guards divide-by-zero on a freshly-restarted server where n=hours-since-restart=0)
	  FROM (
		SELECT 
		  conversions.*, 
		  (CASE WHEN x = 0 THEN 1 ELSE x END + 1.92) / CONVERT(FLOAT,(n + 3.84)) as p -- calculate p
		FROM ( 
		  -- Our conversion rate table from above
		  SELECT DBName
		   , DATEDIFF(HOUR,@lastservericerestart,GETDATE()) as n 
		   , DATEDIFF(HOUR,@lastservericerestart,GETDATE()) - EstHoursSinceActive as x
		   FROM @confidence
		) conversions
	  ) rates
	) intervals
	) confidence ON confidence.DBName COLLATE DATABASE_DEFAULT = base.name COLLATE DATABASE_DEFAULT 
	LEFT OUTER JOIN @sysdatabasesTable dbs ON dbs.database_id = base.database_id
	LEFT OUTER JOIN (
		SELECT 
		database_id
     	, DB_NAME(database_id) [database_name]
    	, CONVERT(DECIMAL(8,2),SUM(CASE WHEN type_desc = 'LOG' THEN size END) * 8. / 1024) [log_size_mb]
    	, CONVERT(DECIMAL(8,2),SUM(CASE WHEN type_desc = 'ROWS' THEN size END) * 8. / 1024) [row_size_mb]
    	, CONVERT(DECIMAL(8,2),SUM(size) * 8. / 1024) [total_size_mb]
		FROM @master_filesTable
		GROUP BY database_id
	) dbspace ON dbspace.database_id = base.database_id

	ORDER BY base.name

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Database usage likelyhood measured',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
		RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
	END
			
END
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Database usage likelihood', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with database usage measures',0,1) WITH NOWAIT;
END CATCH
			/*----------------------------------------
			--Create DMA commands
			----------------------------------------*/
BEGIN TRY
	SET @CustomErrorText = 'DMA Output'
IF @ShowMigrationRelatedOutputs = 1
	BEGIN
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		)
		SELECT 31, 'Database Migration Assistant commands','------','------'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		)

		SELECT 31, 'DMA', 'Run in PowerShell', '.\DmaCmd.exe /AssessmentName="' + @ThisServer + '_' + name + '" /AssessmentDatabases="Server=' + @ThisServer 
			+ ';Initial Catalog=' + name + ';Integrated Security=true" /AssessmentEvaluateCompatibilityIssues /AssessmentOverwriteResult /AssessmentTargetPlatform="SqlServerWindows2017" /AssessmentResultCsv="'
			+ 'C:\Temp\DMA\AssessmentReport_' + REPLACE(@ThisServer,@CharToCheck,'_') + '_' + name + '.csv"'
			 FROM @sysdatabasesTable
			WHERE database_id > 4

		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Create DMA commands',0,1) WITH NOWAIT;
	END
	END
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'DMA Output', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with creating DMA commands',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	

			/*----------------------------------------
			--Getting Trace data
			----------------------------------------*/

--https://www.ptr.co.uk/blog/how-improve-your-sql-server-speed
--The script below can be use to search for automatic Log File Growths, using the background profiler trace that SQL Server maintains.
DECLARE @tracepath [NVARCHAR] (260)
/* The default trace is read ONCE (lane bundle-sp-triage-5, M3c): one fn_trace_gettable call fills #sp_triage_deftrace,
   with the path passed as a parameter (a path that holds a quote no longer breaks the text), and the section 32 and 33
   reads use the #table. TextData is kept only for EventClass 115 (Audit Backup/Restore Event), the one read that uses it.
   The column names are spelled as those reads spell them. */
IF OBJECT_ID('tempdb..#sp_triage_deftrace') IS NOT NULL
	DROP TABLE #sp_triage_deftrace;
CREATE TABLE #sp_triage_deftrace
(
	StartTime DATETIME
	, eventclass INT
	, ObjectType INT
	, DatabaseID INT
	, DatabaseName [NVARCHAR] (256)
	, ApplicationName [NVARCHAR] (256)
	, [FileName] [NVARCHAR] (256)
	, IntegerData INT
	, Duration BIGINT
	, TextData [NVARCHAR] (MAX)
);

BEGIN TRY
--Pick up the path of the background profiler trace for the instance
DECLARE @tracepathTable TABLE (tracepath [NVARCHAR] (500))
SET @dynamicSQL = '
SELECT 
 tracepath = path 
FROM [sys].traces 
WHERE is_default = 1'

INSERT @tracepathTable
EXEC sp_executesql @dynamicSQL 

SELECT @tracepath = tracepath 
FROM @tracepathTable

	IF @IsSQLAzure = 0 AND @tracepath IS NOT NULL
	BEGIN
		INSERT #sp_triage_deftrace (StartTime, eventclass, ObjectType, DatabaseID, DatabaseName, ApplicationName, [FileName], IntegerData, Duration, TextData)
		EXEC sp_executesql N'SELECT StartTime, EventClass, ObjectType, DatabaseID, DatabaseName, ApplicationName, [FileName], IntegerData, Duration, CASE WHEN EventClass = 115 THEN CONVERT(NVARCHAR(MAX), TextData) END FROM fn_trace_gettable(@p, DEFAULT)'
			, N'@p NVARCHAR(260)', @p = @tracepath;
	END

	SET @CustomErrorText = 'Default trace data'
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 32, 'Trace Data','------','------'

	IF @IsSQLAzure = 0 AND @tracepath IS NULL
		INSERT #output_sqldba_org_sp_triage ([SectionID], [Section], [Summary])
		SELECT 32, 'Trace Data', N'The default trace is off.';

	IF @IsSQLAzure = 0 AND @tracepath IS NOT NULL
	BEGIN

		SET @dynamicSQL = '
		SELECT 
		[Section]
		, [Summary]
		, [More]
		, [Details]
		FROM (
		SELECT 32 [Section]
				, 1 [Sort]
				,''[Start-Time]:''+CONVERT([VARCHAR],MIN(StartTime),120)  [Summary]
				,''[End-Time]:''+CONVERT([VARCHAR],MAX(StartTime),120) [More]
				,''[TimeSpan-Minutes]:''+ CONVERT([NVARCHAR] (20),DATEDIFF(MINUTE, MIN(StartTime), MAX(StartTime))) [Details]
		FROM #sp_triage_deftrace g
		cross apply [sys].trace_events te 
		LEFT OUTER JOIN #output_sqldba_org_sp_triage_TraceTypes T on T.Value = g.ObjectType
		WHERE g.eventclass = te.trace_event_id
		
		UNION ALL
		
		SELECT 32 [Section]
				, 2 [Sort]
				,name  [Summary]
				,ISNULL(DatabaseName,'''') [More]
				,''EventDefinition:''+ISNULL(T.Definition,'''')
				+'';Application:''+ISNULL(ApplicationName,'''' )
				+'';Events:''+ CONVERT([NVARCHAR] (50),count(*))
				+ '';TimeSpan-Minutes:''+ CONVERT([NVARCHAR] (20),DATEDIFF(MINUTE, MIN(StartTime), MAX(StartTime))) [Details]
		FROM #sp_triage_deftrace g
		cross apply [sys].trace_events te 
		LEFT OUTER JOIN #output_sqldba_org_sp_triage_TraceTypes T on T.Value = g.ObjectType
		WHERE g.eventclass = te.trace_event_id
		AND name <> ''Audit Backup/Restore Event''
		GROUP BY name,T.Definition,DatabaseName, ApplicationName

		UNION ALL
		
		SELECT 32 [Section]
				, 3 [Sort]
				,name  [Summary]
				,ISNULL(DatabaseName,'''') [More]
				,''[EventDefinition]:''+ISNULL(T.Definition,'''')
				+'',[Action]:''+
				CASE 
				WHEN PATINDEX(''%BACKUP LOG%'',TextData) > 0 THEN ''LOG BACKUP''
				WHEN PATINDEX(''%BACKUP DATABASE%'',TextData) > 0 THEN ''DATABASE BACKUP''
				WHEN PATINDEX(''%ONLY FROM%'',TextData) > 0 THEN ''VERIFY''
				END 
				+'',[Events]:''+ CONVERT([NVARCHAR] (50),count(*))
				+'',[TimeSpan-Minutes]:''+ CONVERT([NVARCHAR] (20),DATEDIFF(MINUTE, MIN(StartTime), MAX(StartTime))) [Details]
		FROM #sp_triage_deftrace g
		cross apply [sys].trace_events te 
		LEFT OUTER JOIN #output_sqldba_org_sp_triage_TraceTypes T on T.Value = g.ObjectType
		WHERE g.eventclass = te.trace_event_id
		AND name =''Audit Backup/Restore Event''
		GROUP BY name,T.Definition,DatabaseName, CASE 
		WHEN PATINDEX(''%BACKUP LOG%'',TextData) > 0 THEN ''LOG BACKUP''
		WHEN PATINDEX(''%BACKUP DATABASE%'',TextData) > 0 THEN ''DATABASE BACKUP''
		WHEN PATINDEX(''%ONLY FROM%'',TextData) > 0 THEN ''VERIFY''
		END 
		) T1
		ORDER BY [Sort] ASC, [Summary]
		
		'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		)
		EXEC sp_executesql @dynamicSQL 
END

END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Default trace data', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with trace part 1 before autogrowth',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH

--Query the background trace files
BEGIN TRY
SET @CustomErrorText = 'Trace data autogrowth'
INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 33, 'Trace Data Autogrowth','------','------'
		
		
SET @dynamicSQL=' SELECT 33
		,DBName
		,''%Change:''+ CONVERT([NVARCHAR] (20),SUM(EventGrowthMB)*100/(MAX(CurrentFileSizeMB) - SUM(EventGrowthMB)))

	, ''EventGrowthMB:''+CONVERT([NVARCHAR] (20),SUM(EventGrowthMB)) 
	+'';FileType:''+FileType
	+'';TotalDurationSec:''+ CONVERT([NVARCHAR] (20),SUM(EventDurationSec) )
	+'';Period:''+ CONVERT([NVARCHAR] (20),CONVERT([VARCHAR],DATEADD(SECOND,DATEDIFF(SECOND,MIN(EventTime), MAX(EventTime)),0),114) )
	+'';CurrentFileSizeMB:''+ CONVERT([NVARCHAR] (20),MAX(CurrentFileSizeMB) )
	FROM (
	SELECT 
	 DBName    = g.DatabaseName
	, DBFileName   = mf.physical_name
	, FileType   = CASE mf.type WHEN 0 THEN ''Row'' WHEN 1 THEN ''Log'' WHEN 2 THEN ''FILESTREAM'' WHEN 4 THEN ''Full-text'' END
	, EventName   = te.name
	, EventGrowthMB  = CONVERT(MONEY,g.IntegerData*8/1024.) -- Number of 8-kilobyte (KB) pages by which the file increased.
	, EventTime   = g.StartTime
	, EventDurationSec = CONVERT(MONEY,g.Duration/1000./1000.) -- Length of time necessary to extEND the file.
	, CurrentAutoGrowthSet= CASE
			WHEN mf.is_percent_growth = 1
			THEN CONVERT(char(2), mf.growth) + ''%'' 
			ELSE CONVERT([VARCHAR](30), CONVERT(MONEY, mf.growth*8./1024.)) + ''MB''
		   END
	, CurrentFileSizeMB = CONVERT(MONEY,mf.size* 8./1024.)
	, MaxFileSizeMB  = CASE WHEN mf.max_size = -1 THEN ''Unlimited'' ELSE CONVERT([VARCHAR](30), CONVERT(MONEY,mf.max_size*8./1024.)) END
	FROM #sp_triage_deftrace g
	cross apply [sys].trace_events te 
	inner join sys.master_files mf
	on mf.database_id = g.DatabaseID
	and g.FileName = mf.name
	WHERE g.eventclass = te.trace_event_id
	and  te.name in (''Data File Auto Grow'',''Log File Auto Grow'')
	) T
	GROUP BY DBName
	, FileType
	ORDER BY DBName'
INSERT #output_sqldba_org_sp_triage (
SectionID
, Section
, Summary
, Details
)
EXEC sp_executesql @dynamicSQL 

END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Trace data autogrowth', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Issue with Trace data insert',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
	PRINT @dynamicSQL
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done with Trace data',0,1) WITH NOWAIT;
	END


			/*----------------------------------------
			--Compression options
			----------------------------------------*/
BEGIN TRY
	SET @CustomErrorText = 'Index compression checks'
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		) 
		SELECT 34, 'Good index compression candidates','------','------'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		)
		SELECT TOP 10
		34
		
		, DB
		, '[CurrentCompression]:' + data_compression_desc 
		, '[SizeGB]:' + CONVERT([VARCHAR](30),CONVERT(MONEY,SUM(reserved_page_count))*8/1024/1024)
		+ ',[Reserved Pages]:' + CONVERT([VARCHAR](30),SUM(reserved_page_count))
		+ ',[Rows]:' + CONVERT([VARCHAR](30),SUM(row_count))
		FROM  #output_sqldba_org_sp_triage_SqueezeMe  S
		GROUP BY DB,data_compression_desc

		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details])
		SELECT TOP 10
		34
		, DB 
		, '[CurrentCompression]:' + data_compression_desc 
		+ ',[Reserved Pages]:' + CONVERT([VARCHAR](10),reserved_page_count) 
		+',[RowCount]:' + CONVERT([VARCHAR](10),row_count)
		, [Just compress] + ';' + [For LOB data]
		FROM  #output_sqldba_org_sp_triage_SqueezeMe  S
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Index compression checks', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with compression candidates',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done reading compression options',0,1) WITH NOWAIT;
	END
			/*----------------------------------------
			--Compression evaluation
			----------------------------------------*/

BEGIN TRY
	SET @CustomErrorText = 'Actual index compression validation'
INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	)
	SELECT 35, 'Actual Index compression validation','------','------'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		)
		SELECT 5
		, '[DB]:'+dbname 
		+',[TableName]:'+TableName
		+',[partition_number]:'+CONVERT([VARCHAR](3),partition_number)
		+',[is_disabled]:'+CONVERT([VARCHAR](3),is_disabled)
		+',[is_hypothetical]:'+ CONVERT([VARCHAR](3),is_hypothetical)

		, '[Compression]:'+ Compression
		+',[CompressionObject]:'+ CompressionObject
		+',[IndexSizeKB]:' + CONVERT([VARCHAR](10),IndexSizeKB)
		+',[RowCount]:' + CONVERT([VARCHAR](10),RowCounts)
		, [Just compress] /*+ ';' + [For LOB data]*/
		FROM  #output_sqldba_org_sp_triage_compressionstates  S
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Actual index compression validation', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error compression validation',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done with compression evaluation',0,1) WITH NOWAIT;
	END
			/*----------------------------------------
			-- TOP n Index usage patterns
			----------------------------------------*/
BEGIN TRY
	SET @CustomErrorText = 'TOP 25 index isage'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		) 
		SELECT 36, 'Index Usage Statistics TOP 25','------','------'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		)
		SELECT 36
		, '[DB]:'+dbname+',[TableName]:'+ISNULL(ObjectName,'')+',[IndexName]:'+ISNULL(IndexName,'')
		, '[Reads]:' + CONVERT([VARCHAR](25),Reads)
		+',[Writes]:' + CONVERT([VARCHAR](25),Writes) 
		+',[FillFactor]:'+ CONVERT([VARCHAR](25),[FillFactor]) 
		+',[has_filter]:'+ CONVERT([VARCHAR](25),has_filter) 

		, '[IndexType]:' + ISNULL(IndexType,'')
		+',[last_user_scan]:' + ISNULL(CONVERT([VARCHAR],last_user_scan,120),'') /*fixed copy-paste: was CONVERT(IndexType) x3, now the real date columns*/
		+',[last_user_lookup]:'+ ISNULL(CONVERT([VARCHAR],last_user_lookup,120),'')
		+',[last_user_seek]:'+ ISNULL(CONVERT([VARCHAR],last_user_seek,120),'')
		FROM
		(
			SELECT RANK() OVER(ORDER BY([Reads] + [Writes]) DESC) RankMe
			,dbname
			,ObjectName
			,IndexName
			,IndexType
			,Reads
			,Writes
			,[FillFactor]
			,has_filter
			,last_user_scan
			,last_user_lookup
			,last_user_seek
		FROM #output_sqldba_org_sp_triage_indexusage
		)  S
		WHERE RankMe < 25
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'TOP 25 index usage', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with index usage stats',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done with index usage patterns',0,1) WITH NOWAIT;
	END


			/*----------------------------------------
			--Our INdex usage statistics evaluation
			----------------------------------------*/
BEGIN TRY
SET @CustomErrorText = 'Our index usage evaluation'
INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 37, 'Our Index Usage Evaluation','------','------'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		)
		SELECT 37
		, '[ServerTotal]'
		, '[IndexCount]:'+CONVERT([VARCHAR](25),COUNT(IndexName)) 
		, '[TotalReadActivity%]:' + CONVERT([VARCHAR](25),SUM([TotalReadActivity%]))
	
		FROM  
		(
			SELECT RANK() OVER(ORDER BY([Reads] + [Writes]) DESC) RankMe
			,[dbname]
			,[ObjectName]
			,[IndexName]
			,[IndexType]
			,[Reads]
			,[Writes]
			,[FillFactor]
			,[has_filter]
			,[TableReadActivity%]
			,[TotalReadActivity%]
		FROM #output_sqldba_org_sp_triage_indexusage
		)  S
		WHERE UPPER(IndexName) LIKE '%SQLDBA%'
		

	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 37, 'Our Index Usage Evaluation','------','------'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		)
		SELECT 37
		, '[DB]:'+dbname
		+ '[TableName]:' + [ObjectName]
		, '[IndexCount]:'+CONVERT([VARCHAR](25),COUNT(IndexName)) 
		, '[TableReadActivity%]:' + CONVERT([VARCHAR](25),SUM([TableReadActivity%]))
		+ ',[TotalReadActivity%]:' + CONVERT([VARCHAR](25),SUM([TotalReadActivity%]))
		+ ',[Reads]:' + CONVERT([VARCHAR](25),SUM(Reads))
		+ ',[Writes]:' + CONVERT([VARCHAR](25),SUM(Writes))  
	
		FROM  
		(
			SELECT RANK() OVER(ORDER BY([Reads] + [Writes]) DESC) RankMe
			,[dbname]
			,[ObjectName]
			,[IndexName]
			,[IndexType]
			,[Reads]
			,[Writes]
			,[FillFactor]
			,[has_filter]
			,[TableReadActivity%]
			,[TotalReadActivity%]
		FROM #output_sqldba_org_sp_triage_indexusage
		)  S
		WHERE UPPER(IndexName) LIKE '%SQLDBA%'
		GROUP BY dbname,[ObjectName]
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Our index usage evaluation', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with custom index evaluation',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done with index evaluation',0,1) WITH NOWAIT;
	END
			/*----------------------------------------
			--Blocking tables
			----------------------------------------*/
BEGIN TRY
SET @CustomErrorText = 'Current blocking tables'
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		) 
		SELECT 38, 'Currently blocking tables','------','------'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		)
		SELECT 38
		, 'DatabaseName:'+DatabaseName
		  + '; ObjectName:'							+ ObjectName
		  + '; index_id:'							+ CONVERT([VARCHAR](20),index_id)
		  + '; LocksCount:'						+ CONVERT([VARCHAR](20),LocksCount)
		  + '; BlocksCount:'						+ CONVERT([VARCHAR](20),BlocksCount)
		  + '; BlocksWaitTimeMs:'					+ CONVERT([VARCHAR](20),BlocksWaitTimeMs)

		, '; page_io_latch_wait_in_ms:'			+ CONVERT([VARCHAR](20),page_io_latch_wait_in_ms)
		  + '; range_scan_count:'					+ CONVERT([VARCHAR](20),range_scan_count)
		  + '; singleton_lookup_count:'			+ CONVERT([VARCHAR](20),singleton_lookup_count)
		  + '; forwarded_fetch_count:'			+ CONVERT([VARCHAR](20),forwarded_fetch_count)
		  + '; DML_count:'						+ CONVERT([VARCHAR](20),insert_count + delete_count + update_count)

		, 'page_io_latch_wait_count:'			+ CONVERT([VARCHAR](20),page_io_latch_wait_count)
		  + '; page_compression_success_count:'	+ CONVERT([VARCHAR](20),page_compression_success_count)
		  + '; lob_fetch_in_bytes:'				+ CONVERT([VARCHAR](20),lob_fetch_in_bytes)
		  + '; lob_orphan_create_count:'			+ CONVERT([VARCHAR](20),lob_orphan_create_count)
		  + '; lob_orphan_insert_count:'			+ CONVERT([VARCHAR](20),lob_orphan_insert_count)
		  + '; leaf_ghost_count:'					+ CONVERT([VARCHAR](20),leaf_ghost_count)
		  + '; insert_count:'						+ CONVERT([VARCHAR](20),insert_count)
		  + '; delete_count:'						+ CONVERT([VARCHAR](20),delete_count)
		  + '; update_count:'						+ CONVERT([VARCHAR](20),update_count)
		  + '; allocation_count:'					+ CONVERT([VARCHAR](20),allocation_count)
		  + '; page_merge_count:'					+ CONVERT([VARCHAR](20),page_merge_count)

		FROM  #output_sqldba_org_sp_triage_blockinghistory  b
		ORDER BY [page_io_latch_wait_in_ms] DESC
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Current blocking tables', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with currently blocking tables',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done looking at blocking queries',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Usage top xx queries
			----------------------------------------*/
BEGIN TRY
	SET @CustomErrorText = 'Top queries'
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 39, 'Top Queries - minus the plan','------','------'
	INSERT #output_sqldba_org_sp_triage (
	SectionID
	, Section
	, Summary
	, Details
	)
	SELECT 39
	, '[DB]:'+DBs.name
	+ '; [Total Elapsed Time in S]:' + CONVERT([VARCHAR](20),s.[Total Elapsed Time in S])
	+ '; [Total Execution Count]:' +CONVERT([VARCHAR](20),s.[Total Execution Count])
	+ '; [RankIO]:' + CONVERT([VARCHAR](20),s.[RankIO])
	+ '; [RankExec]:' + CONVERT([VARCHAR](20),s.[RankExec])
	+ '; [RankCompute]:'+CONVERT([VARCHAR](20),s.[RankCompute])
	
	
	
	, '[Pages]:'+CONVERT([VARCHAR](20),s.[Pages])
	+ '; [I/O GB]:'+CONVERT([VARCHAR](20),s.[I/O GB])
	+ '; [Avg CPU Time in MS]:'+CONVERT([VARCHAR](20),s.[Avg CPU Time in MS])
	+ '; [Total physical Reads]:'+CONVERT([VARCHAR](20),s.[Total physical Reads])
	+ '; [Total Logical Reads]:'+CONVERT([VARCHAR](20),s.[Total Logical Reads])
	+ '; [Total Logical Writes]:'+CONVERT([VARCHAR](20),s.[Total Logical Writes])

	, LEFT('[MAX plan generation Number]:'+CONVERT([VARCHAR](20),s.[MAX plan generation Number])
	+ '; [MAX plan generation Number]:'+CONVERT([VARCHAR](20),s.[MAX plan generation Number])
	+ '; [Avg Logical Reads]:'+CONVERT([VARCHAR](20),s.[Avg Logical Reads])
	+ '; [Total CPU Time in S]:'+CONVERT([VARCHAR](20),s.[Total CPU Time in S])
	+ '; [Min CPU Time in MS]:'+CONVERT([VARCHAR](20),s.[Min CPU Time in MS])
	+ '; [Max CPU Time in MS]:'+CONVERT([VARCHAR](20),s.[Max CPU Time in MS])
	+ '; [Last CPU Time in MS]:'+CONVERT([VARCHAR](20),s.[Last CPU Time in MS])
	+ '; [AVG used_threads]:'+CONVERT([VARCHAR](20),s.[AVG used_threads])
	+ '; [Avg Logical Writes]:'+CONVERT([VARCHAR](20),s.[Avg Logical Writes])
	+ '; [Max Logical Reads]:'+CONVERT([VARCHAR](20),s.[Max Logical Reads])
	+ '; [Max Logical Writes]:'+CONVERT([VARCHAR](20),s.[Max Logical Writes])
	+ '; [total_grant_kb]:'+CONVERT([VARCHAR](20),s.[total_grant_kb])
	+ '; [total_used_grant_kb]:'+CONVERT([VARCHAR](20),s.[total_used_grant_kb])
	+ '; [Total CLR Time]:'+CONVERT([VARCHAR](20),s.[Total CLR Time])
	+ '; [Avg CLR Time in MS]:'+CONVERT([VARCHAR](20),s.[Avg CLR Time in MS])
	+ '; [Plan Handle]:'+CONVERT([VARCHAR](20),s.[Plan Handle])
	+ '; [Last Execution Time]:'+CONVERT([VARCHAR],s.[Last Execution Time],120)
	+ '; [Query]:'+ replace(replace(replace(replace(CONVERT([NVARCHAR] (3600),st.text), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), ' ',' ') ,3800)

FROM
/* Perf 2026-07-02: TOP (35) ORDER BY RankIO applied to the aggregate BEFORE the plan/text DMV
   APPLYs, so dm_exec_query_plan/dm_exec_sql_text run for ~35 rows not every plan_handle. */
(
	SELECT TOP (35) * FROM
	(
	SELECT
		CONVERT(MONEY,SUM(qs.total_elapsed_time) / 1000000.0)  [Total Elapsed Time in S]
		, RANK() OVER(ORDER BY((SUM(qs.total_physical_reads) + SUM(qs.total_logical_reads) + SUM(qs.total_logical_writes))) DESC) [RankIO]
		, RANK() OVER(ORDER BY(SUM(qs.execution_count)) DESC) [RankExec]
		, RANK() OVER(ORDER BY(SUM(qs.total_worker_time)) DESC) [RankCompute]
		, SUM(qs.execution_count)  [Total Execution Count]
		, MAX(qs.plan_generation_num) [MAX plan generation Number]
		, MAX(qs.last_execution_time) AS [Last Execution Time]
		/*ALL I/O*/
		,(SUM(qs.total_physical_reads) + SUM(qs.total_logical_reads) + SUM(qs.total_logical_writes))  [Pages]
		, CONVERT(MONEY,SUM(qs.total_physical_reads) + SUM(qs.total_logical_reads) + SUM(qs.total_logical_writes))  * 8 /1024/1024 [I/O GB]
		/*Compute*/
		, CONVERT(MONEY,SUM(qs.total_worker_time) / 1000000.0 )  [Total CPU Time in S]
		, CONVERT(MONEY,SUM(qs.total_worker_time) / SUM(qs.execution_count) / 1000.0 )  [Avg CPU Time in MS]
		, CONVERT(MONEY,SUM(qs.min_worker_time) / 1000.0 ) AS [Min CPU Time in MS]
		, CONVERT(MONEY,SUM(qs.max_worker_time) / 1000.0 ) AS [Max CPU Time in MS]
		, CONVERT(MONEY,SUM(qs.last_worker_time) / 1000.0 ) AS [Last CPU Time in MS]
		, AVG(qs.total_used_threads) [AVG used_threads]
		/*Storage Physical*/
		, SUM(qs.total_physical_reads) AS [Total physical Reads]
		, CONVERT(MONEY,SUM(qs.total_physical_reads) / SUM(qs.execution_count) )  [Avg physical Reads]
		/*Storage Memory*/
		, SUM(qs.total_logical_reads) AS [Total Logical Reads]
		, CAST(CAST(SUM(qs.total_logical_reads) AS FLOAT) / CAST(SUM(qs.execution_count) AS FLOAT) AS DECIMAL(20, 2))  [Avg Logical Reads]
		, SUM(qs.total_logical_writes) AS [Total Logical Writes]
		, CAST(CAST(SUM(qs.total_logical_writes) AS FLOAT) / CAST(SUM(qs.execution_count) AS FLOAT) AS DECIMAL(20, 2))  [Avg Logical Writes]
		, SUM(qs.min_logical_reads)  [Min Logical Reads]
		, SUM(qs.max_logical_reads)  [Max Logical Reads]
		, SUM(qs.min_logical_writes)  [Min Logical Writes]
		, SUM(qs.max_logical_writes)  [Max Logical Writes]
		/*Memory grants*/
		, CONVERT(MONEY,SUM(qs.total_grant_kb))/SUM(qs.execution_count) [total_grant_kb]
		, CONVERT(MONEY,SUM(qs.total_used_grant_kb))/SUM(qs.execution_count) [total_used_grant_kb]
		, SUM(qs.total_clr_time) AS [Total CLR Time]
		, CAST(SUM(qs.total_clr_time) / SUM(qs.execution_count) / 1000.0 AS DECIMAL(20, 2))  [Avg CLR Time in MS]
		, qs.plan_handle AS [Plan Handle]
		, MIN(qs.sql_handle) AS [Sql Handle]
	FROM
		#dadatafor_exec_query_stats qs
	WHERE qs.plan_handle IN (SELECT rp.plan_handle FROM #sp_triage_readable_plan AS rp)
	GROUP BY qs.plan_handle
	--HAVING CONVERT(MONEY,(SUM(qs.total_elapsed_time) / 1000000.0)) > 1
	) sAgg
	WHERE sAgg.[RankIO] <= 35
	ORDER BY sAgg.[RankIO] ASC
	) s
/* member 1 (lane bundle-sp-triage-fixes-b): the plan was opened only for its dbid, and opening a plan that names an
   unreadable database raised 942/924 and lost the section. The guard holds the dbid; the text comes from the SQL handle. */
LEFT OUTER JOIN
	#sp_triage_readable_plan AS rp ON rp.plan_handle = s.[Plan Handle]
LEFT OUTER JOIN
	@sysdatabasesTable DBs ON rp.dbid = DBs.database_id
OUTER APPLY
	[sys].dm_exec_sql_text(s.[Sql Handle]) AS st
WHERE [RankIO] <= 35
ORDER BY [RankIO]  ASC
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Top queries', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with selecting the top queries',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Usage top xx queries completed',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Disk Volumes  (SectionID 40)
			--
			-- Converted 2026-07-02 from a WMI/xp_cmdshell (Get-WmiObject Win32_Volume) call to pure
			-- T-SQL via sys.dm_os_volume_stats. No xp_cmdshell needed here anymore -> SPN is the ONLY
			-- xp_cmdshell consumer left in the script. This is RICHER than the old output (mount point,
			-- filesystem, total/free/used GB, %free, compression) but it does NOT expose the NTFS
			-- allocation-unit / cluster size (BytesPerCluster) -- that field only comes from WMI/fsutil.
			-- Accepted trade-off: the 64K-allocation-unit best-practice check is dropped in favour of
			-- never touching the host. dm_os_volume_stats is 2008R2+ and works on Azure SQL MI; on
			-- Azure SQL DB it returns nothing (no OS volumes) which is fine.
			----------------------------------------*/
SET @CustomErrorText = 'Checking disk volumes'
IF OBJECT_ID(N'sys.dm_os_volume_stats') IS NOT NULL /*census: RawDiagnosticsFootprintTests.Shipped_sp_triage_guards_every_post_2008_object*/
BEGIN
BEGIN TRY
	DECLARE @volumes TABLE
	(
		Volume [NVARCHAR] (520)
		, FileSystem [NVARCHAR] (50)
		, TotalGB DECIMAL(18,1)
		, FreeGB DECIMAL(18,1)
		, PercentFree DECIMAL(5,1)
		, IsCompressed BIT
	)
	/* One row per distinct volume that holds a database file. MIN() collapses the
	   multiple (database_id,file_id) rows that map to the same physical volume. */
	INSERT INTO @volumes (Volume, FileSystem, TotalGB, FreeGB, PercentFree, IsCompressed)
	SELECT
		vs.volume_mount_point
		, MIN(vs.file_system_type)
		, CONVERT(DECIMAL(18,1), MIN(vs.total_bytes)     / 1073741824.0)
		, CONVERT(DECIMAL(18,1), MIN(vs.available_bytes) / 1073741824.0)
		, CONVERT(DECIMAL(5,1), CASE WHEN MIN(vs.total_bytes) > 0
			THEN MIN(vs.available_bytes) * 100.0 / MIN(vs.total_bytes) ELSE 0 END)
		, MIN(CONVERT(INT, vs.is_compressed))
	FROM sys.master_files AS mf
	CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) AS vs
	WHERE mf.database_id IN (SELECT r.database_id FROM #sp_triage_readable_db AS r)
	GROUP BY vs.volume_mount_point;
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Checking disk volumes', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	RAISERROR (N'Error reading sys.dm_os_volume_stats (expected on Azure SQL DB)',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
	SELECT @errMessage  = ERROR_MESSAGE()
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH
END
ELSE
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (N'Checking disk volumes', 0, N'sys.dm_os_volume_stats is not on this server. It needs a newer SQL Server version.');

BEGIN TRY
	SET @CustomErrorText = 'Disk volumes'
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	)
	SELECT 40, 'Disk Volumes','------','------'
	INSERT #output_sqldba_org_sp_triage (
	SectionID
	, Section
	, Summary
	, Details
	)
	SELECT 40
		,'Disk Volumes'
		, Volume
		, CONVERT([NVARCHAR](4000),
			FileSystem + N' | Total ' + CONVERT([NVARCHAR](20), TotalGB) + N' GB'
			+ N' | Free ' + CONVERT([NVARCHAR](20), FreeGB) + N' GB (' + CONVERT([NVARCHAR](10), PercentFree) + N'%)'
			+ CASE WHEN IsCompressed = 1 THEN N' | COMPRESSED' ELSE N'' END)
	FROM @volumes V1
	ORDER BY Volume
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Disk volumes', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error writing disk volumes to output table',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done with disk volumes',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Maintenance Summary
			----------------------------------------*/
			/*First check if Ola table exists in master.. sorry, too dumb to look anywhwere else*/
BEGIN TRY
	SET @CustomErrorText = 'Maintenance summary'
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 98, 'Maintenance Summary','------','------'
IF EXISTS (SELECT 1 
	FROM INFORMATION_SCHEMA.TABLES 
	WHERE TABLE_NAME = N'CommandLog'
)
BEGIN

	SET @dynamicSQL = '
	SELECT 98
	, [CommandType]
	, CASE [CommandType]
	WHEN ''ALTER_INDEX'' THEN ''IndexMaintenanceSummary''
	WHEN ''BACKUP_DATABASE'' THEN ''Database Backup''
	WHEN ''BACKUP_LOG'' THEN ''Log Backup''
	WHEN ''DBCC_CHECKDB'' THEN ''Consistency Check''
	WHEN ''RESTORE_VERIFYONLY'' THEN ''Database Restore test''
	WHEN ''xp_delete_file'' THEN ''Old Backup file removed''
	END [JobType]
	, ''[AvgJobs]:'' + CONVERT([VARCHAR](20),AVG([Jobs]))
	+ ''; [AvgPagesDone]:''+ ISNULL(CONVERT([VARCHAR](20),AVG([PagesDone]) ),'''')
	+ ''; [AvgIO]:''+ ISNULL(CONVERT([VARCHAR](20),AVG(CONVERT(MONEY,[PagesDone])*8/1024/1024) ),'''')
	+ ''; [AvgDurationInSeconds]:''+ ISNULL(CONVERT([VARCHAR](20),AVG([DurationInSeconds]) ),'''')
	FROM(
	
		 SELECT [CommandType]
		 , LEFT([StartTime],10) [Date]
		 , COUNT(*) [Jobs]
		 , SUM([pagecount]) [PagesDone]
		 ,SUM([DurationInSeconds]) [DurationInSeconds]
		 FROM (
		 SELECT 
			 [ID]
			 ,[DatabaseName]
			 ,[SchemaName]
			 ,[ObjectName]
			 ,[ObjectType]
			 ,[IndexName]
			 ,[IndexType]
			 ,[StatisticsName]
			 ,[PartitionNumber]
			 ,[ExtendedInfo]
			 ,[Command]
			 ,[CommandType]
			 ,[StartTime]
			 ,[EndTime]
			 ,[ErrorNumber]
			 ,[ErrorMessage]
			 ,datediff(second,StartTime,EndTime)  [DurationInSeconds]
			 ,ExtendedInfo.value(''(/ExtendedInfo/PageCount)[1]'',''[BIGINT]'') as [pagecount]
			 ,ExtendedInfo.value(''(/ExtendedInfo/Fragmentation)[1]'',''numeric(7,5)'') as [Fragmentation]
		 FROM [dbo].[CommandLog] 
		 --WHERE IndexName IS NOT NULL
	 ) OlaMaintenance
	   GROUP BY [CommandType], LEFT([StartTime],10)
	) OlaMaintenanceSummary
	  GROUP BY [CommandType]'

	INSERT #output_sqldba_org_sp_triage (
	SectionID
	, Section
	, Summary
	, Details
	)
	EXEC sp_executesql @dynamicSQL
END
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Maintenance summary', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with reading or finding CommandLog table for Ola output',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done reading  CommandLog table for Ola output0',0,1) WITH NOWAIT;
	END



			/*----------------------------------------
			--Calculate daily IO workload
			----------------------------------------*/

SET @CustomErrorText = 'Daily workload'
BEGIN TRY
	set @dbid = db_id(); 
	SET @cnt = 0; 
	SET @record_count = 0; 
	DECLARE @sql_handle varbinary(64); 
	DECLARE @sql_handle_string [NVARCHAR] (130); 
	SET @grand_total_worker_time = 0 ; 
	SET @grand_total_IO = 0 ; 


	--REWRITE FROM HERE

	/* SELECT @TotalIODailyWorkload = SUM(CONVERT(MONEY,CONVERT(FLOAT,t_total_IO) * 8 /*KB*/ /1024/*MB*//1024)/@DaysOldestCachedQuery)  
	*/
 SELECT @TotalIODailyWorkload = CONVERT(MONEY,SUM(qs.total_physical_reads) + SUM(qs.total_logical_reads) + SUM(qs.total_logical_writes))  * 8 /1024/1024 /@DaysOldestCachedQuery
	FROM #dadatafor_exec_query_stats qs

	/* 2026-07-02: proc-level I/O from dm_exec_procedure_stats (per-proc aggregates), same GB/day units +
	   same @DaysOldestCachedQuery divisor as @TotalIODailyWorkload above. TRY/CATCH because it's an
	   additive read; leaves NULL->0 if unavailable. NOTE the two totals are NOT strict subsets:
	   @TotalIODailyWorkload comes from the FILTERED capture (the (reads+writes)*execs>200 predicate),
	   while proc-stats is unfiltered - so proc CAN exceed it, which is why the DELTA below uses ABS(). */
	BEGIN TRY
		SELECT @TotalProcIODailyWorkload = CONVERT(MONEY,SUM(ps.total_physical_reads) + SUM(ps.total_logical_reads) + SUM(ps.total_logical_writes)) * 8 /1024/1024 /@DaysOldestCachedQuery
		FROM [sys].dm_exec_procedure_stats ps;
		SET @ProcIORead = 1; /* see its declaration */
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Proc-level I/O (dm_exec_procedure_stats)', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SET @TotalProcIODailyWorkload = NULL;
	END CATCH
	SET @TotalProcIODailyWorkload = ISNULL(@TotalProcIODailyWorkload, 0);

	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 99, 'Workload details I/O','------','------'
	INSERT #output_sqldba_org_sp_triage (
	SectionID
	, Section
	, Summary
	, Details
	)
	SELECT 99 [SectionID]
	, LEFT('Oldest Cache: ' + CASE WHEN @QueryStatsCaptured = 1 AND @oldestcachequery IS NOT NULL THEN CONVERT([VARCHAR](15), @DaysOldestCachedQuery) ELSE 'not read' END + '; Days Uptime: ' + CONVERT([VARCHAR](15),@DaysUptime),3500) [Section]
	, LEFT(CONVERT([VARCHAR](50),@TotalIODailyWorkload)
	+ 'GB/day; ' + CONVERT([VARCHAR](50),SUM(qs.execution_count)) +' executions/day; '
	+ CONVERT([VARCHAR](50),CONVERT(MONEY,SUM(qs.total_elapsed_time) / 1000000.0)/1000) +' (avg) *[DailyGB; DailyExecutions; AverageTime(s)]', 3500) Summary
	, 'Total' [Details]
	FROM #dadatafor_exec_query_stats qs

	/* 2026-07-02: proc-vs-non-proc split of the cached workload. Total PROC I/O = the stored-proc share
	   (dm_exec_procedure_stats); DELTA NON-PROC = ABS(all-query-stats - proc) ~= ad-hoc/prepared share.
	   ABS() because the query-stats total is filtered and proc-stats is not (see note above). */
	INSERT #output_sqldba_org_sp_triage (
		SectionID
		, Section
		, Summary
		, Details
	)
	SELECT 99
		, 'Total PROC I/O per day: ' + CASE WHEN @ProcIORead = 1 THEN CONVERT([VARCHAR](50), @TotalProcIODailyWorkload) + 'GB/day' ELSE 'not read' END
		, 'Stored-proc share of cached I/O workload (dm_exec_procedure_stats)'
		, 'Proc'
	UNION ALL
	SELECT 99
		, 'DELTA NON-PROC I/O per day: ' + CONVERT([VARCHAR](50), ABS(@TotalIODailyWorkload - @TotalProcIODailyWorkload)) + 'GB/day'
		, 'Approx ad-hoc/prepared share = ABS(total exec-stats I/O - proc I/O)'
		, 'Non-Proc'
	WHERE @ProcIORead = 1 /* see its declaration */

	INSERT #output_sqldba_org_sp_triage (
	SectionID
	, Section
	)
	SELECT  99, 'Total Disk I/O per day: '
		+ CONVERT([VARCHAR](20),CASE WHEN SUM([num_of_reads]) + SUM([num_of_writes]) = 0 THEN 0 
			ELSE CONVERT(MONEY,(SUM([num_of_reads]) + SUM([num_of_writes]))) * 8 /1024/1024/ CONVERT(MONEY,@DaysUptime) END ) + 'GB/day'
	FROM [sys].dm_io_virtual_file_stats (NULL,NULL) AS [s]
	
	INSERT #output_sqldba_org_sp_triage (
	SectionID
	, Section
	, Summary
	, Details
	)
	
	SELECT
		 99 [SectionID]
		, REPLICATE('|', CONVERT(INT,(s.[I/O GB]/@DaysOldestCachedQuery )/@TotalIODailyWorkload *100)) + ' ' + CONVERT([VARCHAR](10),((s.[I/O GB]/@DaysOldestCachedQuery )/@TotalIODailyWorkload *100)) + '%' [Section]
		, CONVERT([VARCHAR](15),(s.[I/O GB]/@DaysOldestCachedQuery) )
		+ 'GB/day; ' + CONVERT([VARCHAR](15),(CASE WHEN s.[Total Execution Count] = 1 THEN 1 ELSE CONVERT(MONEY,s.[Total Execution Count])/@DaysOldestCachedQuery END) ) 
		+ ' executions/day; ' +  CONVERT([VARCHAR](15),([Avg CPU Time in MS]/1000))
		+ ' s(avg) *[DailyGB; DailyExecutions; AverageTime(s)]' [Summary]
		, LEFT('/*' + ISNULL(O.type_desc COLLATE DATABASE_DEFAULT,'') + '; '+ ISNULL(CASE WHEN rdb.database_id IS NOT NULL THEN OBJECT_NAME(st.objectid, st.dbid) END COLLATE DATABASE_DEFAULT,'') + ' ' + QUOTENAME(ISNULL(DBs.name COLLATE DATABASE_DEFAULT,'')) + '.' + QUOTENAME(ISNULL(CASE WHEN rdb.database_id IS NOT NULL THEN OBJECT_SCHEMA_NAME(st.objectid, st.dbid) END COLLATE DATABASE_DEFAULT,'')) + ' > */' + ISNULL(left(replace(replace(replace(replace(CONVERT([NVARCHAR] (3600),st.text COLLATE DATABASE_DEFAULT), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), ' ',' ') ,3800),''),3850) [Details]
	FROM
	/* Perf 2026-07-02: TOP (35) ORDER BY RankIO applied to the aggregate BEFORE the plan/text DMV
	   APPLYs below, so dm_exec_query_plan/dm_exec_sql_text run for ~35 rows not every plan_handle.
	   @TotalIODailyWorkload (SUM over the full table, above) is unaffected. */
	(
		SELECT TOP (35) * FROM
		(
		SELECT
			CONVERT(MONEY,SUM(qs.total_elapsed_time) / 1000000.0)  [Total Elapsed Time in S]
			, RANK() OVER(ORDER BY((SUM(qs.total_physical_reads) + SUM(qs.total_logical_reads) + SUM(qs.total_logical_writes))) DESC) [RankIO]
			, RANK() OVER(ORDER BY(SUM(qs.execution_count)) DESC) [RankExec]
			, RANK() OVER(ORDER BY(SUM(qs.total_worker_time)) DESC) [RankCompute]
			, SUM(qs.execution_count)  [Total Execution Count]
			, MAX(qs.plan_generation_num) [MAX plan generation Number]
			, MAX(qs.last_execution_time) AS [Last Execution Time]
			/*ALL I/O*/
			,(SUM(qs.total_physical_reads) + SUM(qs.total_logical_reads) + SUM(qs.total_logical_writes))  [Pages]
			, CONVERT(MONEY,SUM(qs.total_physical_reads) + SUM(qs.total_logical_reads) + SUM(qs.total_logical_writes))  * 8 /1024/1024 [I/O GB]
			/*Compute*/
			, CONVERT(MONEY,SUM(qs.total_worker_time) / 1000000.0 )  [Total CPU Time in S]
			, CONVERT(MONEY,SUM(qs.total_worker_time) / SUM(qs.execution_count) / 1000.0 )  [Avg CPU Time in MS]
			, CONVERT(MONEY,SUM(qs.min_worker_time) / 1000.0 ) AS [Min CPU Time in MS]
			, CONVERT(MONEY,SUM(qs.max_worker_time) / 1000.0 ) AS [Max CPU Time in MS]
			, CONVERT(MONEY,SUM(qs.last_worker_time) / 1000.0 ) AS [Last CPU Time in MS]
			, AVG(qs.total_used_threads) [AVG used_threads]
			/*Storage Physical*/
			, SUM(qs.total_physical_reads) AS [Total physical Reads]
			, CONVERT(MONEY,SUM(qs.total_physical_reads) / SUM(qs.execution_count) )  [Avg physical Reads]
			/*Storage Memory*/
			, SUM(qs.total_logical_reads) AS [Total Logical Reads]
			, CAST(CAST(SUM(qs.total_logical_reads) AS FLOAT) / CAST(SUM(qs.execution_count) AS FLOAT) AS DECIMAL(20, 2))  [Avg Logical Reads]
			, SUM(qs.total_logical_writes) AS [Total Logical Writes]
			, CAST(CAST(SUM(qs.total_logical_writes) AS FLOAT) / CAST(SUM(qs.execution_count) AS FLOAT) AS DECIMAL(20, 2))  [Avg Logical Writes]
			, SUM(qs.min_logical_reads)  [Min Logical Reads]
			, SUM(qs.max_logical_reads)  [Max Logical Reads]
			, SUM(qs.min_logical_writes)  [Min Logical Writes]
			, SUM(qs.max_logical_writes)  [Max Logical Writes]
			/*Memory grants*/
			, CONVERT(MONEY,SUM(qs.total_grant_kb))/SUM(qs.execution_count) [total_grant_kb]
			, CONVERT(MONEY,SUM(qs.total_used_grant_kb))/SUM(qs.execution_count) [total_used_grant_kb]
			, SUM(qs.total_clr_time) AS [Total CLR Time]
			, CAST(SUM(qs.total_clr_time) / SUM(qs.execution_count) / 1000.0 AS DECIMAL(20, 2))  [Avg CLR Time in MS]
			, qs.plan_handle AS [Plan Handle]
			, MIN(qs.sql_handle) AS [Sql Handle]
		
		FROM
			#dadatafor_exec_query_stats qs
		WHERE qs.plan_handle IN (SELECT rp.plan_handle FROM #sp_triage_readable_plan AS rp)
		GROUP BY qs.plan_handle
		--HAVING CONVERT(MONEY,(SUM(qs.total_elapsed_time) / 1000000.0)) > 1
		) sAgg
		WHERE sAgg.[RankIO] <= 35
		ORDER BY sAgg.[RankIO] ASC
		) s
	/* member 1 (lane bundle-sp-triage-fixes-b): no plan open (see Top queries). The object was joined to the CURRENT database's
	   sys.objects whatever database the plan came from; it is now named in its own database, only when that database can be
	   read. Its type is known only for objects of the current database. */
	LEFT OUTER JOIN
		#sp_triage_readable_plan AS rp ON rp.plan_handle = s.[Plan Handle]
	LEFT OUTER JOIN
		@sysdatabasesTable DBs ON rp.dbid = DBs.database_id
	OUTER APPLY
		[sys].dm_exec_sql_text(s.[Sql Handle]) AS st
	LEFT OUTER JOIN #sp_triage_readable_db AS rdb ON rdb.database_id = st.dbid
	LEFT OUTER JOIN [sys].objects O On O.object_id = st.objectid AND st.dbid = DB_ID()
	WHERE [RankIO] <= 35
	ORDER BY [RankIO]  ASC
	OPTION (RECOMPILE);
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Daily workload', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Daily workload calculated',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Top Procs by I/O  (SectionID 100 - clustered with the 99 workload section)  [added 2026-07-02]
			--
			-- Attribution for the "Total PROC I/O per day" summary line: which stored procs actually
			-- drive the cached I/O workload. dm_exec_procedure_stats holds per-proc aggregates directly
			-- (keyed by database_id/object_id), so no plan/text DMV APPLY is needed - proc name comes
			-- from OBJECT_NAME. Ranked by the same I/O metric (phys+logical reads + logical writes) as
			-- the query report, capped at TOP(@TopQueries). Valid on box / Azure SQL DB / MI. TRY/CATCH
			-- so it degrades to an empty section rather than failing the run.
			----------------------------------------*/
	SET @CustomErrorText = 'Top procs by I/O'
	BEGIN TRY
		INSERT #output_sqldba_org_sp_triage (
			[SectionID], [Section], [Summary], [Details]
		)
		SELECT 100, 'Top Procs by I/O (dm_exec_procedure_stats)','------','------'

		INSERT #output_sqldba_org_sp_triage (
			SectionID, Section, Summary, Details
		)
		SELECT 100
			, '[DB]:' + ISNULL(DB_NAME(ps.database_id),'')
			  + '; [Proc]:' + ISNULL(OBJECT_NAME(ps.object_id, ps.database_id),'(cross-db/unresolved)')
			  + '; [RankIO]:' + CONVERT([VARCHAR](20), ps.[RankIO])
			  + '; [I/O GB]:' + CONVERT([VARCHAR](20), ps.[I/O GB])
			  + '; [I/O GB/day]:' + CONVERT([VARCHAR](20), CONVERT(MONEY, ps.[I/O GB] / @DaysOldestCachedQuery))
			, '[Executions]:' + CONVERT([VARCHAR](20), ps.execution_count)
			  + '; [Total Elapsed S]:' + CONVERT([VARCHAR](20), CONVERT(MONEY, ps.total_elapsed_time/1000000.0))
			  + '; [Total CPU S]:' + CONVERT([VARCHAR](20), CONVERT(MONEY, ps.total_worker_time/1000000.0))
			  + '; [Avg CPU MS]:' + CONVERT([VARCHAR](20), CONVERT(MONEY, ps.total_worker_time/NULLIF(ps.execution_count,0)/1000.0))
			, '[Total physical Reads]:' + CONVERT([VARCHAR](20), ps.total_physical_reads)
			  + '; [Total Logical Reads]:' + CONVERT([VARCHAR](20), ps.total_logical_reads)
			  + '; [Total Logical Writes]:' + CONVERT([VARCHAR](20), ps.total_logical_writes)
			  + '; [Last Execution Time]:' + CONVERT([VARCHAR](20), ps.last_execution_time, 120)
		FROM
		(
			SELECT TOP (@TopQueries)
				database_id, object_id, execution_count, total_elapsed_time, total_worker_time
				, total_physical_reads, total_logical_reads, total_logical_writes, last_execution_time
				, (total_physical_reads + total_logical_reads + total_logical_writes) [Pages]
				, CONVERT(MONEY, total_physical_reads + total_logical_reads + total_logical_writes) * 8 /1024/1024 [I/O GB]
				, RANK() OVER(ORDER BY (total_physical_reads + total_logical_reads + total_logical_writes) DESC) [RankIO]
			FROM [sys].dm_exec_procedure_stats
			WHERE database_id IN (SELECT r.database_id FROM #sp_triage_readable_db AS r)
			ORDER BY (total_physical_reads + total_logical_reads + total_logical_writes) DESC
		) ps
		ORDER BY ps.[RankIO] ASC
		OPTION (RECOMPILE);
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Top procs by I/O', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage = ERROR_MESSAGE(); RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
		RAISERROR (N'Error with top procs by I/O (dm_exec_procedure_stats)',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
		RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
	END CATCH

		IF @Debug = 1
		BEGIN
			SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
			SET @DebugTime = GETDATE()
			IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT;
			RAISERROR (N'Top procs by I/O completed',0,1) WITH NOWAIT;
		END


			/*----------------------------------------
			--Check those passwords
			----------------------------------------*/
			/* Thanks Eitan, MadeiraData/MadeiraToolbox	https://github.com/EitanBlumin*/

			/*Just to make sure someone is not leaving the key in the front door*/
SET @CustomErrorText = 'Password checks'
DECLARE @OutputPasswords BIT
SET @OutputPasswords = 0
BEGIN TRY
INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
) SELECT 101, 'Leaving the door open','------','------'

--IF EXISTS (SELECT 1 FROM master.INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = N'CommandLog')
BEGIN


DECLARE @sysloginsTable TABLE(sid varbinary (85)
, principal_id INT
, [name] [NVARCHAR] (250)
, is_disabled INT
, [password_hash] VARBINARY(256)


)
SET @dynamicSQL = 'SELECT s.sid, s.principal_id, RTRIM(name) AS [LoginName], is_disabled, [password_hash]
		FROM [sys].sql_logins s' 
/* @CanReadPasswordHash (see its declaration): both deviation arms below read password_hash, which is NULL without
   CONTROL SERVER, so an unread hash would report every login as an Empty Password. */
IF @IsSQLAzure = 0 AND @CanReadPasswordHash = 1
BEGIN
	INSERT INTO @sysloginsTable
	EXEC sp_executesql @dynamicSQL
END
ELSE IF @IsSQLAzure = 0
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (N'Password checks', 0, N'skipped: password checks need CONTROL SERVER');

INSERT #output_sqldba_org_sp_triage (SectionID, Section,Summary,Details,Severity)

  SELECT 101
  , Deviation [Section]
  , LoginName [Summary]
  , LoginName + ' has ' +ServerRoles + ' permissions' Details
  , @Result_YourServerIsDead

  FROM(
		SELECT 
		Deviation = dev.Deviation + CASE WHEN @OutputPasswords = 1 THEN N' (' + Pwd + N')' ELSE N'' END
		, dev.[sid]
		, dev.principal_id
		, [LoginName]
		, ServerRoles =
		STUFF((
			SELECT N', ' + roles.name
			FROM [sys].server_role_members AS srm
			INNER JOIN [sys].server_principals AS roles ON srm.role_principal_id = roles.principal_id
			WHERE srm.member_principal_id = dev.principal_id
			FOR XML PATH('')
			), 1, 2, N'')
		FROM
		(
		SELECT 'Empty Password' AS Deviation, s.sid, s.principal_id, RTRIM(name) AS [LoginName], '' AS Pwd
		FROM @sysloginsTable AS s

		WHERE is_disabled = 0
		AND ([password_hash] IS NULL OR PWDCOMPARE('', [password_hash]) = 1)
		AND name NOT IN ('MSCRMSqlClrLogin')
		AND name NOT LIKE '##MS[_]%##'

		UNION ALL

		SELECT DISTINCT 'Login name is the same as password' AS Deviation, s.sid, s.principal_id, RTRIM(s.name) AS [Name] , u.usrname
		FROM @sysloginsTable s
		CROSS APPLY
		( 
			SELECT
			RTRIM(RTRIM(s.name)) usrname
			UNION ALL
			SELECT
			REVERSE(RTRIM(RTRIM(s.name))) 
		) AS u(usrname)
		WHERE s.is_disabled = 0
		AND PWDCOMPARE(u.usrname, s.[password_hash]) = 1
		) dev
	) T1

	OPTION (RECOMPILE); -- avoid saving this in plan cache


END
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Password checks', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error checking password issues',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done checking password issues',0,1) WITH NOWAIT;
	END




			/*----------------------------------------
			--Deprecated Features
			----------------------------------------*/

			/*Explicitly check for Deprecated Features*/
SET @CustomErrorText = 'Deprecated features'
BEGIN TRY
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 102, 'Deprecated Features','------','------'
BEGIN

	INSERT #output_sqldba_org_sp_triage (
	SectionID
	, Section
	, Summary
	, Details
	)

	SELECT 102
	, 'Deprecated' [Section]
	, [instance_name] [Summary]
	, LEFT(cntr_value,1500) [Details]
	FROM [sys].[dm_os_performance_counters] 
	WHERE ([object_name] LIKE '%Deprecated Features%') 
	AND ([cntr_value] > 0);

END
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Deprecated features', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with reading or finding Depreacted features',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done looking for Deprecated Features',0,1) WITH NOWAIT;
	END


		/*----------------------------------------
			--Foreign Keys with no indexes
			----------------------------------------*/
BEGIN TRY
	SET @CustomErrorText = 'FK no indexes'
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		) 
		SELECT 103, 'FKs with no indexes','------','------'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		)
		SELECT 103
		, 'FK with no index. DatabaseName:'+DatabaseName
		, COUNT(DISTINCT TableName) Tables
		, COUNT(DISTINCT Column_Name) FKcount
		FROM  #output_sqldba_org_sp_triage_FKNOIndex  b
		GROUP BY DatabaseName
		
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'FK no indexes', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with FKs with no indexes',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done looking at FKs with no indexes',0,1) WITH NOWAIT;
	END

	/*----------------------------------------
	--AllPerfmonCounters
	----------------------------------------*/
BEGIN TRY
	SET @CustomErrorText = 'The thing'
	DECLARE @CounterTypes TABLE(
	CounterTypeName [NVARCHAR] (50)
	, CounterType [NVARCHAR] (50)
	, CounterTypeReference [NVARCHAR] (50)
	, CounterTypeDescription [NVARCHAR] (2000)
	)
	INSERT INTO @CounterTypes
	(
		CounterTypeName 
		, CounterType 
		, CounterTypeReference 
		, CounterTypeDescription
	)

	SELECT 
	'PERF_RAW_FRACTION'
	,'Decimal'
	,'537003008'
	,'Ratio of a subset to its set as a percentage. This counter type displays the current percentage only, not an average over time.'
	UNION ALL SELECT
	'PERF_SAMPLE_FRACTION'
	,'Decimal'
	,'549585920'
	,'Average ratio of hits to all operations during the last two sample intervals. This counter type requires a base property with the PERF_SAMPLE_BASE counter type.'
	UNION ALL SELECT
	'PERF_COUNTER_DELTA'
	,'Decimal'
	,'4195328'
	,'This counter type shows the change in the measured attribute between the two most recent sample intervals.'
	UNION ALL SELECT
	'PERF_COUNTER_LARGE_DELTA'
	,'Decimal'
	,'4195584'
	,'Same as PERF_COUNTER_DELTA but a 64-bit representation for larger values.'
	UNION ALL SELECT
	'PERF_ELAPSED_TIME'
	,'Decimal'
	,'807666944'
	,'Total time between when the process started and the time when this value is calculated.'
	UNION ALL SELECT
	'PERF_AVERAGE_BASE'
	,'Decimal'
	,'1073939458'
	,'Base value used to calculate the PERF_AVERAGE_TIMER and PERF_AVERAGE_BULK counter types.'
	UNION ALL SELECT
	'PERF_COUNTER_MULTI_BASE'
	,'Decimal'
	,'1107494144'
	,'Base value used to calculate the PERF_COUNTER_MULTI_TIMER, PERF_COUNTER_MULTI_TIMER_INV, PERF_100NSEC_MULTI_TIMER, and PERF_100NSEC_MULTI_TIMER_INV counter types.'
	UNION ALL SELECT
	'PERF_LARGE_RAW_BASE'
	,'Decimal'
	,'1073939712'
	,'Base value found in the calculation of PERF_RAW_FRACTION, 64 bits.'
	UNION ALL SELECT
	'PERF_RAW_BASE'
	,'Decimal'
	,'1073939459'
	,'Base value used to calculate the PERF_RAW_FRACTION counter type.'
	UNION ALL SELECT
	'PERF_SAMPLE_BASE'
	,'Decimal'
	,'1073939457'
	,'Base value used to calculate the PERF_SAMPLE_COUNTER and PERF_SAMPLE_FRACTION counter types.'
	UNION ALL SELECT
	'PERF_AVERAGE_BULK'
	,'Decimal'
	,'1073874176'
	,'Number of items processed, on average, during an operation. This counter type displays a ratio of the items processed (such as bytes sent) to the number of operations completed, and requires a base property with PERF_AVERAGE_BASE as the counter type.'
	UNION ALL SELECT
	'PERF_COUNTER_COUNTER'
	,'Decimal'
	,'272696320'
	,'Average number of operations completed during each second of the sample interval.'
	UNION ALL SELECT
	'PERF_SAMPLE_COUNTER'
	,'Decimal'
	,'4260864'
	,'Average number of operations completed in one second. This counter type requires a base property with the counter type PERF_SAMPLE_BASE.'
	UNION ALL SELECT
	'PERF_COUNTER_BULK_COUNT'
	,'Decimal'
	,'272696576'
	,'Average number of operations completed during each second of the sample interval. This counter type is the same as the PERF_COUNTER_COUNTER type, but it uses larger fields to accommodate larger values.'
	UNION ALL SELECT
	'PERF_COUNTER_TEXT'
	,'Decimal'
	,'2816'
	,'This counter type shows a variable-length text string in Unicode. It does not display calculated values.'
	UNION ALL SELECT
	'PERF_COUNTER_RAWCOUNT'
	,'Decimal'
	,'65536'
	,'Raw counter value that does not require calculations, and represents one sample which is the last observed value only.'
	UNION ALL SELECT
	'PERF_COUNTER_LARGE_RAWCOUNT'
	,'Decimal'
	,'65792'
	,'Same as PERF_COUNTER_RAWCOUNT, but a 64-bit representation for larger values.'
	UNION ALL SELECT
	'PERF_COUNTER_RAWCOUNT_HEX0'
	,''
	,''
	,'Most recently observed value in hexaDecimal format. It does not display an average.'
	UNION ALL SELECT
	'PERF_COUNTER_LARGE_RAWCOUNT_HEX'
	,'Decimal'
	,'256'
	,'Same as PERF_COUNTER_RAWCOUNT_HEX, but a 64-bit representation in hexaDecimal for use with large values.'
	UNION ALL SELECT
	'PERF_PRECISION_SYSTEM_TIMER'
	,'Decimal'
	,'541525248'
	,'Similar to PERF_COUNTER_TIMER except that it uses a counter defined time base instead of the system timestamp.'
	UNION ALL SELECT
	'PERF_PRECISION_100NS_TIMER'
	,'Decimal'
	,'542573824'
	,'Similar to PERF_100NSEC_TIMER except that it uses a 100ns counter defined time base instead of the system 100ns timestamp.'
	UNION ALL SELECT
	'PERF_COUNTER_QUEUELEN_TYPE'
	,'Decimal'
	,'4523008'
	,'Average length of a queue to a resource over time. It shows the difference between the queue lengths observed during the last two sample intervals divided by the duration of the interval.'
	UNION ALL SELECT
	'PERF_COUNTER_LARGE_QUEUELEN_TYPE'
	,'Decimal'
	,'4523264'
	,'Average length of a queue to a resource over time. Counters of this type display the difference between the queue lengths observed during the last two sample intervals, divided by the duration of the interval.'
	UNION ALL SELECT
	'PERF_COUNTER_100NS_QUEUELEN_TYPE'
	, 'Decimal'
	,'5571840'
	,'Average length of a queue to a resource over time in 100 nanosecond units.'
	UNION ALL SELECT
	'PERF_COUNTER_OBJ_TIME_QUEUELEN_TYPE'
	,'Decimal'
	,'6620416'
	,'Time an object is in a queue.'
	UNION ALL SELECT
	'PERF_COUNTER_TIMER'
	,'Decimal'
	,'541132032'
	,'Average time that a component is active as a percentage of the total sample time.'
	UNION ALL SELECT
	'PERF_COUNTER_TIMER_INV'
	,'Decimal'
	,'557909248'
	,'Average percentage of time observed during sample interval that the object is not active. This counter type is the same as PERF_100NSEC_TIMER_INV except that it measures time in units of ticks of the system performance timer rather than in 100ns units.'
	UNION ALL SELECT
	'PERF_AVERAGE_TIMER'
	,'Decimal'
	,'805438464'
	,'Average time to complete a process or operation. This counter type displays a ratio of the total elapsed time of the sample interval to the number of processes or operations completed during that time..This counter type requires a base property with PERF_AVERAGE_BASE as the counter type.'
	UNION ALL SELECT
	'PERF_100NSEC_TIMER'
	,'Decimal'
	,'542180608'
	,'Active time of one component as a percentage of the total elapsed time in units of 100ns of the sample interval.'
	UNION ALL SELECT
	'PERF_100NSEC_TIMER_INV'
	,'Decimal'
	,'558957824'
	,'Percentage of time the object was not in use. This counter type is the same as PERF_COUNTER_TIMER_INV except that it measures time in 100ns units rather than in system performance timer ticks.'
	UNION ALL SELECT
	'PERF_COUNTER_MULTI_TIMER'
	,'Decimal'
	,'574686464'
	,'Active time of one or more components as a percentage of the total time of the sample interval. This counter type differs from PERF_100NSEC_MULTI_TIMER in that it measures time in units of ticks of the system performance timer, rather than in 100ns units.This counter type requires a base property with the PERF_COUNTER_MULTI_BASE counter type.'
	UNION ALL SELECT
	'PERF_COUNTER_MULTI_TIMER_INV'
	,'Decimal'
	,'591463680'
	,'Inactive time of one or more components as a percentage of the total time of the sample interval. This counter type differs from PERF_100NSEC_MULTI_TIMER_INV in that it measures time in units of ticks of the system performance timer, rather than in 100ns units..This counter type requires a base property with the PERF_COUNTER_MULTI_BASE counter type.'
	UNION ALL SELECT
	'PERF_100NSEC_MULTI_TIMER'
	,'Decimal'
	,'575735040'
	,'This counter type shows the active time of one or more components as a percentage of the total time (100ns units) of the sample interval. This counter type requires a base property with the PERF_COUNTER_MULTI_BASE counter type.'
	UNION ALL SELECT
	'PERF_100NSEC_MULTI_TIMER_INV'
	,'Decimal'
	,'592512256'
	,'Inactive time of one or more components as a percentage of the total time of the sample interval. Counters of this type measure time in 100ns units. This counter type requires a base property with the PERF_COUNTER_MULTI_BASE counter type.'
	UNION ALL SELECT
	'PERF_OBJ_TIME_TIMER'
	,'Decimal'
	,'543229184'
	,'A 64-bit timer in object-specific units.'



	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		) 
		SELECT 104, 'AllCounters','------','------'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		)
		
		SELECT
			104 
			, '[Object]:' + REPLACE(PC.[object_name],'  ','')
			+ ',[Counter]:' + REPLACE(PC.[counter_name],'  ','')
			, REPLACE(PC.instance_name,'  ','')
			, REPLACE(PC.cntr_value,'  ','')
			/*, cntr_type
			, CT.CounterType*/
			FROM [sys].dm_os_performance_counters PC
			LEFT OUTER JOIN @CounterTypes CT ON CT.CounterTypeReference = cntr_type

		
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Perfmon counters', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with Perfmon Counters',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done looking at Perfmon Counters.. they are amazing!',0,1) WITH NOWAIT;
	END


	/*----------------------------------------
	--Batch averages from perfmon
	----------------------------------------*/
BEGIN TRY
	SET @CustomErrorText = 'Batch averages from perfmon'
	/*https://learn.microsoft.com/en-us/archive/blogs/sql_pfe_blog/create-a-quick-and-easy-performance-baseline */
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		) 
		SELECT 105, @CustomErrorText,'------','------'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		)
		SELECT 105
		, bcount.counter_name
		, '[AvgRunTimeMS]:' + CONVERT([VARCHAR](50),CASE WHEN bcount.cntr_value = 0 THEN 0 ELSE btime.cntr_value/bcount.cntr_value END )
		+ ',[StatementCount]:' + CONVERT([VARCHAR](50), CAST(bcount.cntr_value AS BIGINT))
		+ ',[TotalElapsedTimeMS]:' + CONVERT([VARCHAR](50),btime.cntr_value)
		, '[Time%]:' + CONVERT([VARCHAR](7),CAST((100.0 * btime.cntr_value/SUM (btime.cntr_value) OVER()) AS DECIMAL(5,2)))
		+ '[Count%]:' + CONVERT([VARCHAR](7),CAST((100.0 * bcount.cntr_value/SUM (bcount.cntr_value) OVER()) AS DECIMAL(5,2)))
		FROM
		(
		SELECT *
		FROM (
		SELECT *
		--INTO #BatchResponses
		FROM [sys].dm_os_performance_counters
		WHERE object_name LIKE '%Batch Resp Statistics%'
		AND instance_name IN('Elapsed Time:Requests','Elapsed Time:Total(ms)')
		)br
		WHERE instance_name = 'Elapsed Time:Requests'
		) bcount
		JOIN
		(
		SELECT *
		FROM (
		SELECT *
		--INTO #BatchResponses
		FROM [sys].dm_os_performance_counters
		WHERE object_name LIKE '%Batch Resp Statistics%'
		AND instance_name IN('Elapsed Time:Requests','Elapsed Time:Total(ms)')
		)br
		WHERE instance_name = 'Elapsed Time:Total(ms)'
		) btime ON bcount.counter_name = btime.counter_name
		ORDER BY bcount.counter_name ASC

		
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Batch averages from perfmon', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with Batch averages from perfmon',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done looking at Batch averages from perfmon',0,1) WITH NOWAIT;
	END

	/*----------------------------------------
	--Performance counter summaries: 120 since start, 121 right now (in-run sample), 122 since the last run, 123 batch response time
	----------------------------------------*/
/* PERFORMANCE COUNTER SUMMARIES (lane sp-triage-perf-counter-summaries, stages S1, S2 and S3, 2026-10-21/22). Stage S1 built
   the catalog, the snapshots, the rules and sections 120 (since start) and 121 (right now). Stage S2 added the history read
   and its segmentation, section 122 (since the last run) and section 123 (batch response-time profile). Stage S3 makes the
   history read two-phase and sampled, and adds normal ranges, trends, first-seen dates and R14 (HISTORY f-k below).
   INVARIANTS (the lane brief, sections 0 and 8):
   1. Every number is computed by its counter KIND, and the live cntr_type is checked against the family the catalog expects
      (#pcs_catalog.ExpectTypes / DenExpectTypes; a ratio or average base must be 1073939712):
        rate    = 272696576 or 272696320, cumulative: delta / seconds.
        count   = cumulative, read as "how many in the window": delta. Log Growths, Log Shrinks, Deprecated Features Usage and
                  the Batch Resp Statistics buckets are 65792 but cumulative, so the catalog marks them by hand; Number of
                  Deadlocks/sec and Lock Timeouts are rate types read as counts because their rules ask "any in the window".
        ratio   = 537003264 with its base: value / base x 100 at the reading; base 0 = no data. Section 120 only.
        average = 1073874176 with its base: delta value / delta base; a base delta of 0 = no data.
        gauge   = 65792 point-in-time reading; with a denominator gauge, reading / denominator x Scale.
        per     = delta(numerator) / delta(denominator) x Scale, both cumulative, over the SAME window; a denominator delta
                  of 0 = no data.
      A live cntr_type outside the family gives a "could not be computed" row naming the type. Nothing is guessed.
   2. Every row names its window: [Window] and [WindowSeconds] in Details, and the window in the sentence.
   3. Nothing is differenced across a restart. "Since start" divides by UptimeSeconds = (ms_ticks -
      sqlserver_start_time_ms_ticks) / 1000 from sys.dm_os_sys_info (clock- and DST-proof). A cumulative value that goes DOWN
      inside a window is a reset: that pair gives no value (critic M3).
   4. Sections 104 and 105 are not edited. The three SCREEN selects at the end hide SectionID 104; the export table still
      receives every 104 row in its old shape (SpTriageScreenHides104CensusTests).
   5. SQL Server 2008 syntax only in this block: no TRY_CONVERT/TRY_CAST, IIF, CONCAT, FORMAT, LAG/LEAD, PERCENTILE_*,
      STRING_AGG, THROW. Lists are built with FOR XML PATH.
   6. Every Summary sentence and every threshold lives in #pcs_rules below and nowhere else.
   7. No SELECT here sends the client a result set: every SELECT feeds an INSERT or a variable
      (SpTriageDbNameQuotingCensusTests.Only_the_output_table_reaches_the_client_as_a_result_set).
   DETAILS FORMAT (machine-readable, for the app and for the history read of later runs): a run of [Key]:value; pairs, in any
   order, each ending in ';'. Numbers use '.' decimals and no thousands separators (CONVERT, never FORMAT). A database,
   node or feature name inside Details is escaped: '%' -> '%25', ';' -> '%3B', ',' -> '%2C', so ';' always ends a pair and
   ',' always separates list items. Keys: [Metric] catalog key; [Kind]; [Value]; [Unit]; [Window]; [WindowSeconds];
   [CntrType] the live type; [Status] ok|nodata|reset|type|missing; [Entity]; [Rule] and [Threshold] when a rule fired;
   [Numerator], [Denominator], [Scale] for per; [Base] for ratio and average; [Readings] first,last for a gauge in the
   sample; [Reason] when Status is not ok; roll-up rows: [Entities], [NonZero], [Zero], [Hits], [Max], [MaxAt],
   [NotComputed], [NonZeroNames], [ZeroNames] (names lists cut at 1500 characters, then ",...(N in all)").
   Header of 120: [sqlserver_start_time] (style 120), [UptimeSeconds], [WindowSeconds]. These make a later run's history
   exact: two runs in one segment are UptimeSeconds apart, never local-clock apart. Then the history basis: [History]
   read|absent|cap|error, [HistoryRuns], [HistoryRunsRead] (runs whose counters were read), [HistoryDays],
   [RestartsSeenAtLeast], [HistorySegments], [HistoryTableRows], [HistoryTableMB], [HistoryRowsRead], [HistoryReadMs].
   History keys on a metric row of 120 (stage S3): [Normal7d], [Normal30d], [Normal90d] (low,high,N or thin,N),
   [NormalShown] 30d|90d|thin, [NormalOut] below|above with [NormalRule]:R_NORM30, [Trend] rising|falling|steady|thin with
   [TrendWindow], [TrendPoints], [TrendFirst], [TrendSecond], [FirstSeen] (a run's evaldate, 'now' or 'none'). Roll-up
   rows: [FirstSeen] as name:evaldate,... for Deprecated features; the log-growth trend keys on Log growths. R14 rows:
   [Rule]:R14, [Runs], [Days], [FirstRun], [LastRun], [Evidence] (evaldate:value,... cut at 1500 characters).
   Header of 121: [SampleSeconds] (measured), [Requested], [From], [To], [WindowSeconds].
   Header of 122: [Window]:lastrun, [LastRun] (its evaldate), [LastRunStart], [Elapsed] uptime|clock, [WindowSeconds]; a
   skipped 122 is its header alone with [Status]:skip and [Reason] none|restart|down|absent|cap|error.
   Rows of 123: [Metric] BATCH_BANDS | BATCH_TAIL | BATCH_MEDIAN_P95 | BATCH_TAIL_CHANGE, [Window], [WindowSeconds], [Batches],
   [ElapsedMs], [CountPct] and [TimePct] (five bands, in band order), [Buckets] (all 17: low-high:batches:ms, high empty
   for the last), [Median] and [P95] (low-high in ms: a bucket RANGE, never an exact time), [TailCountPct], [TailTimePct].
   HISTORY (stage S2, read made two-phase in S3; brief section 2d, critic H2, H4, M3, M4, M5, ruling R9.2):
   a. The export table @ExportDBName.@ExportSchema.@ExportTableName (already bracketed at the top of the proc: concatenated
      as-is, never QUOTENAME'd again, never SET here) is read whatever @Export is. Absent table = no history, said. Before
      reading, ONE metadata read of its rows and reserved MB (sys.dm_db_partition_stats); above HIST_MAXROWS rows (#pcs_rules)
      history is not read, and the reason is said. Both reads are INSERT #pcs_* ... EXEC sp_executesql, so no dynamic SELECT
      reaches the client (critic M5; guarded by SpTriagePerfCounterHistoryCensusTests). A read error = a could-not-run row and no history, said.
      PHASE 1 lists the runs from cheap predicates only: this SQLInstance, evaldate in the last 90 days and before this run's,
      SectionID 2 (Uptime:), 120 (this version's header) and the one 104 row of Batch Requests/sec (tested on the last 32
      characters of Section, never a leading-wildcard scan of the whole text). PHASE 2 picks at most HIST_MAXRUNS runs that
      stored counters (every run of the last HIST_RECENTRUNS.Threshold2 days up to HIST_RECENTRUNS.Threshold of them, newest
      first, so the latest run is always read; the rest spread evenly by rank over the older runs) and reads only SectionID 104
      rows of those evaldates, by equality on a VALUES list built from the validated run dates. The counter rows are parsed
      with CHARINDEX and matched to the catalog by equality (critic L1); Deprecated Features rows at 0 are dropped there.
      Every sentence that rests on history says how many runs were read ("N of M runs read").
   b. A run's start is EXACT from a 120 header ([sqlserver_start_time], [UptimeSeconds]); else ESTIMATED as evaldate minus
      its legacy Uptime: days (hour-grained, up to ~67 min out); Uptime:0.10 means "under an hour" and is UNKNOWN.
   c. SEGMENTS: consecutive runs A, B share a segment only when both starts are known and agree within @pcs_Tol minutes,
      B's start is before A's run (by @pcs_Tol minutes when B's start is an estimate), Batch Requests/sec (instance '') did
      not go down, and, when both are exact, B's UptimeSeconds is not below A's (critic H4). Otherwise B starts a new segment
      (invariant 3: a restart that cannot be ruled out is a restart). "Restarts seen" counts the boundaries that ARE
      restarts (a start that moved, counters that went down), never an unknown start: hence "at least".
   d. 122 compares this run with the LAST run that stored counters, only when that run's evaldate is at or after this
      run's sqlserver_start_time and Batch Requests/sec did not go down. The elapsed time is the difference of the two
      UptimeSeconds when the last run wrote them, else the clock from its evaldate. Within the window any series that went
      down is a reset and gives no value; a series absent from the last run is no data, not zero (critic M3).
   e. 123 bands the Batch Resp Statistics buckets (cntr_type 65792, cumulative since start: kind count, critic M2). Its
      tail change compares the time share of batches of 1 s or more SINCE the last run with the share UP TO the last run.
   ROWS: one header row per section; a metric over one instance gives one row; per NUMA node one row per node; per database,
   per availability database and per deprecated feature, a row ONLY for an entity that hits a rule, plus ONE roll-up row
   per metric; one "not available on this server" row per area whose counters are missing; one "not an AG replica" row
   when the AG test (critic H1) fails. Severity is NULL (information) unless a #pcs_rules threshold with a severity fires.
   Section 122 is the window 'lastrun' (FromSnap 3 = the last run's 104 rows, ToSnap 1); gauges and ratios are readings,
   not windows, so 122 carries the cumulative kinds only. A rule judges 122 when ',lastrun,' is in its Windows.
   f. POINTS (stage S3, critic M6): one value per (metric, entity) per run read, by the metric's kind, from that run's own
      cumulative values: a rate = value / that run's uptime seconds; a per, ratio or average = its cumulative numerator over
      its cumulative denominator or base; a gauge = the reading. The uptime of a run is its exact [UptimeSeconds]; else, when
      a run of the SAME segment carries an exact start, the clock from that start; else its legacy Uptime: days; an unknown
      start gives no rate point. So a point's window never leaves its own segment. Counts have no normal: a count since
      start grows with uptime. "Now" (section 120) is the same quantity for this run, so it compares like with like.
   g. NORMAL RANGES: per (metric, entity) over the last 7, 30 and 90 days of points, the 10th to 90th percentile by nearest
      rank (ROW_NUMBER; rank = CEILING(p x N)), only with at least NORM_MINPOINTS points; fewer = "history too thin", said
      with the count. Section 120 shows the 30-day range when it has the points, else the 90-day range; Details carry all
      three ([Normal7d]:low,high,N or thin,N). R_NORM30: every threshold rule's metric ALSO fires a Warning when now sits
      outside its 30-day range on the rule's own side (below for a '<' rule, above for a '>' rule).
   h. TRENDS use ADJACENT PAIRS of runs read, only inside one segment (invariant 3), or per-run readings for a gauge: PLE
      (readings), log growths per week (all databases, a database that went down in a pair is skipped, M3) and the long-tail
      share of section 123. The points of the window (30 days when it has NORM_MINPOINTS points, else 90) are split in time
      order into a first and a second half (the middle point dropped when odd); a change of at most TREND_STEADY percent of
      the first half is steady, else rising or falling.
   i. FIRST SEEN: the earliest run read with a value above 0 (Memory Grants Pending, Lock Timeouts, each Deprecated Feature);
      none in history but above 0 now = first seen in this run.
   j. R14: a database whose every observation (each run read, then now) shows no transaction started since the previous
      observation of the same segment, or since start for the first observation of a segment, across at least R14.Threshold
      observations spanning at least R14.Threshold2 days. Information only (critic M8).
   k. Everything in f-j has its own TRY: a failure there is a could-not-run row and sections 120-123 still write. */
DECLARE @pcs_Ready BIT = 0;
DECLARE @pcs_Step NVARCHAR(200);
DECLARE @pcs_Sec INT;
DECLARE @pcs_StartTime DATETIME;
DECLARE @pcs_UptimeSec BIGINT;
DECLARE @pcs_SampleReq INT;
DECLARE @pcs_Delay CHAR(8);
DECLARE @pcs_Snap1At DATETIME;
DECLARE @pcs_Snap2At DATETIME;
DECLARE @pcs_SampleSec DECIMAL(19,3) = 0;
DECLARE @pcs_HadrEnabled INT = 0;
DECLARE @pcs_IsAgReplica BIT = 0;
DECLARE @pcs_RateTypes VARCHAR(60) = ',272696576,272696320,';
DECLARE @pcs_RawTypes VARCHAR(60) = ',65792,';
/* History (stage S2). @pcs_HistState: read | absent | cap | error. @pcs_LastState: ok | none | restart | down | hist. */
DECLARE @pcs_Tol INT = 70; /* minutes: the legacy Uptime: is hour-grained (critic H4) */
DECLARE @pcs_HistState VARCHAR(10) = 'error';
DECLARE @pcs_HistReason NVARCHAR(400) = N'the history step did not run';
DECLARE @pcs_HistTable NVARCHAR(400);
DECLARE @pcs_HistInst NVARCHAR(505);
DECLARE @pcs_HistCut NVARCHAR(20);
DECLARE @pcs_HistRows BIGINT;
DECLARE @pcs_HistMB DECIMAL(19,1);
DECLARE @pcs_HistMax BIGINT;
DECLARE @pcs_HistSql NVARCHAR(MAX);
DECLARE @pcs_HistT0 DATETIME;
DECLARE @pcs_HistReadMs INT;
DECLARE @pcs_HistRead INT = 0;
DECLARE @pcs_Runs INT = 0;
DECLARE @pcs_Segs INT = 0;
DECLARE @pcs_Restarts INT = 0;
DECLARE @pcs_HistDays DECIMAL(19,1) = 0;
DECLARE @pcs_HistSentence NVARCHAR(600);
DECLARE @pcs_LastState VARCHAR(10) = 'hist';
DECLARE @pcs_LastEvalText NVARCHAR(20);
DECLARE @pcs_LastEval DATETIME;
DECLARE @pcs_LastStart DATETIME;
DECLARE @pcs_LastUptimeSec BIGINT;
DECLARE @pcs_LastBatches BIGINT;
DECLARE @pcs_NowBatches BIGINT;
DECLARE @pcs_PrevEval DATETIME;
DECLARE @pcs_LastSec DECIMAL(19,3) = 0;
DECLARE @pcs_LastHeader NVARCHAR(1000);
DECLARE @pcs_LastDetails NVARCHAR(1000);
DECLARE @pcs_i INT;
DECLARE @pcs_Msg NVARCHAR(400);
/* Stage S3: the sampled read, normals, trends, first seen, R14. */
DECLARE @pcs_MaxRuns INT;
DECLARE @pcs_RecentMax INT;
DECLARE @pcs_RecentDays INT;
DECLARE @pcs_MinPts INT;
DECLARE @pcs_Steady DECIMAL(19,4);
DECLARE @pcs_RunsRead INT = 0;
DECLARE @pcs_M2 INT;
DECLARE @pcs_K2 INT;
DECLARE @pcs_PickList NVARCHAR(MAX);
DECLARE @pcs_P1Ms INT;
DECLARE @pcs_P2Ms INT;
DECLARE @pcs_P1Rows INT = 0;
DECLARE @pcs_NowSeg INT;
DECLARE @pcs_TailTrendSum NVARCHAR(1000);
DECLARE @pcs_TailTrendDet NVARCHAR(1000);

CREATE TABLE #pcs_rules (
	RuleId VARCHAR(20) NOT NULL /* R1..R15 (a/b/c = the clauses of one rule); T_* = a sentence that is not a rule; HIST_* = a history setting */
	, Stage VARCHAR(4) NOT NULL /* S1, S2 = evaluated by this version; S3 = stated here, evaluated from stage S3 */
	, Status VARCHAR(10) NOT NULL /* APPROVED (ruled) or DRAFT (awaits a ruling) */
	, Area NVARCHAR(60) NULL
	, MetricKeys VARCHAR(200) NULL /* ',KEY,KEY,': the #pcs_catalog metrics the rule judges */
	, Windows VARCHAR(60) NULL /* ',start,sample,lastrun,': the windows it is judged in */
	, Op CHAR(1) NULL /* '>' or '<' against Threshold; NULL = not a threshold rule */
	, Threshold DECIMAL(19,4) NULL
	, Threshold2 DECIMAL(19,4) NULL /* the second number of a two-number rule (R14: days) */
	, Normalise VARCHAR(10) NULL /* 'week': count x 604800 / MAX(window seconds, 604800), so a window shorter than a week compares its whole count */
	, UseReading VARCHAR(10) NULL /* 'max': in the sample window, a gauge with no denominator is judged by the higher of its two readings (critic L4) */
	, MinUptimeSeconds BIGINT NULL
	, Severity NVARCHAR(5) NULL /* NULL = information */
	, SeverityInSample NVARCHAR(5) NULL /* replaces Severity in the sample window when set */
	, Sentence NVARCHAR(600) NOT NULL
);
CREATE TABLE #pcs_catalog (
	MetricKey VARCHAR(40) NOT NULL
	, SortOrder INT NOT NULL
	, Area NVARCHAR(60) NOT NULL /* the existing sp_triage topic the metric informs */
	, Label NVARCHAR(200) NOT NULL
	, Kind VARCHAR(10) NOT NULL /* rate | count | ratio | average | gauge | per (invariant 1) */
	, ObjSuffix NVARCHAR(128) NOT NULL /* object_name after its FIRST colon: 'Buffer Manager' matches SQLServer:Buffer Manager and MSSQL$X:Buffer Manager */
	, CounterName NVARCHAR(128) NOT NULL
	, InstScope VARCHAR(10) NOT NULL /* exact (InstName) | pernode | perdb | perinst; per-entity scopes skip _Total and mssqlsystemresource */
	, InstName NVARCHAR(128) NULL
	, BaseCounter NVARCHAR(128) NULL /* ratio and average: the base counter, same object and instance */
	, DenObjSuffix NVARCHAR(128) NULL /* per, and a gauge with a denominator */
	, DenCounter NVARCHAR(128) NULL
	, DenInstName NVARCHAR(128) NULL /* NULL = the numerator's own instance */
	, Scale DECIMAL(19,4) NOT NULL
	, ExpectTypes VARCHAR(60) NOT NULL /* ',type,': the live cntr_type family of the numerator */
	, DenExpectTypes VARCHAR(60) NULL
	, Unit NVARCHAR(60) NOT NULL
	, EntityNoun NVARCHAR(60) NULL /* per-entity scopes: what the roll-up counts */
	, AgOnly BIT NOT NULL /* 1 = read only when the H1 AG test passes */
);
CREATE TABLE #pcs_want (obj NVARCHAR(128) NOT NULL, counter NVARCHAR(128) NOT NULL);
CREATE TABLE #pcs_snap (snap TINYINT NOT NULL, taken_at DATETIME NOT NULL, obj NVARCHAR(128) NOT NULL, counter NVARCHAR(128) NOT NULL
	, inst NVARCHAR(128) NOT NULL, cntr_type INT NOT NULL, value BIGINT NOT NULL);
CREATE TABLE #pcs_win (Win VARCHAR(10) NOT NULL, SectionID INT NOT NULL, FromSnap TINYINT NULL /* NULL = zero at sqlserver_start_time */
	, ToSnap TINYINT NULL, SampleOff BIT NOT NULL, Seconds DECIMAL(19,3) NOT NULL, SecondsText VARCHAR(30) NOT NULL, WindowText NVARCHAR(200) NOT NULL
	, Title NVARCHAR(200) NOT NULL, HeaderSummary NVARCHAR(1000) NULL, HeaderDetails NVARCHAR(1000) NULL);
CREATE TABLE #pcs_val (Win VARCHAR(10) NOT NULL, MetricKey VARCHAR(40) NOT NULL, Entity NVARCHAR(128) NULL
	, NumTo BIGINT NULL, NumFrom BIGINT NULL, NumType INT NULL, BaseTo BIGINT NULL, BaseFrom BIGINT NULL, BaseType INT NULL
	, DenTo BIGINT NULL, DenFrom BIGINT NULL, DenType INT NULL
	, StatusCode VARCHAR(20) NULL, Status VARCHAR(10) NULL, Reason NVARCHAR(400) NULL
	, Value DECIMAL(38,6) NULL, ShownValue DECIMAL(38,6) NULL, ValueText VARCHAR(50) NULL
	, FiredRule VARCHAR(12) NULL, Severity NVARCHAR(5) NULL, WindowText NVARCHAR(200) NULL, WindowSecondsText VARCHAR(30) NULL
	/* stage S3, section 120 only: the history clause appended to the Summary and the keys appended to the Details */
	, NormOut VARCHAR(5) NULL, HistSummary NVARCHAR(1500) NULL, HistDetails NVARCHAR(1500) NULL);
/* History (stage S2; two-phase in S3): phase 1 rows (run list), phase 2 rows (counters of the runs read), the parsed
   counter rows, one row per run. */
CREATE TABLE #pcs_hrun (evaldate NVARCHAR(50) NULL, SectionID INT NULL, Section NVARCHAR(4000) NULL, Summary NVARCHAR(4000) NULL, Details NVARCHAR(4000) NULL);
CREATE TABLE #pcs_hist (evaldate NVARCHAR(50) NULL, SectionID INT NULL, Section NVARCHAR(4000) NULL, Summary NVARCHAR(4000) NULL, Details NVARCHAR(4000) NULL);
CREATE TABLE #pcs_hrow (EvalText NVARCHAR(20) NOT NULL, obj NVARCHAR(128) NOT NULL, counter NVARCHAR(128) NOT NULL, inst NVARCHAR(128) NOT NULL, value BIGINT NOT NULL);
CREATE INDEX pcs_hrow_key ON #pcs_hrow (EvalText, obj, counter, inst);
CREATE TABLE #pcs_runs (RunNo INT NOT NULL, EvalText NVARCHAR(20) NOT NULL, EvalDate DATETIME NOT NULL, UptimeDays DECIMAL(23,2) NULL
	, UptimeSec BIGINT NULL, StartExact DATETIME NULL, StartBest DATETIME NULL, StartKind VARCHAR(10) NULL, Batches BIGINT NULL
	, HasCounters BIT NOT NULL, SegReason VARCHAR(10) NULL /* NULL = same segment as the run before; first | unknown | counters | restart */
	, Seg INT NULL
	, Picked BIT NOT NULL DEFAULT 0 /* stage S3: 1 = its counters were read in phase 2 */
	, PtSec BIGINT NULL /* stage S3 (HISTORY f): the uptime seconds a rate point divides by; NULL = no rate point */);
/* Stage S3: points (HISTORY f), normals (g), trend points and results (h), first seen (i), R14 observations (j). */
CREATE TABLE #pcs_pt (MetricKey VARCHAR(40) NOT NULL, Entity NVARCHAR(128) NOT NULL, RunNo INT NOT NULL, EvalDate DATETIME NOT NULL, Value FLOAT NOT NULL);
CREATE TABLE #pcs_norm (MetricKey VARCHAR(40) NOT NULL, Entity NVARCHAR(128) NOT NULL, Days INT NOT NULL, N INT NOT NULL, Lo FLOAT NULL, Hi FLOAT NULL
	, LoText VARCHAR(50) NULL, HiText VARCHAR(50) NULL, KeyText VARCHAR(120) NULL);
CREATE TABLE #pcs_pair (A NVARCHAR(20) NOT NULL, B NVARCHAR(20) NOT NULL, BDate DATETIME NOT NULL, Sec BIGINT NOT NULL);
CREATE TABLE #pcs_tpt (Trend VARCHAR(20) NOT NULL, EvalDate DATETIME NOT NULL, Value FLOAT NOT NULL);
CREATE TABLE #pcs_trend (Trend VARCHAR(20) NOT NULL, Days INT NULL, N INT NOT NULL, FirstMean FLOAT NULL, SecondMean FLOAT NULL, Direction VARCHAR(10) NOT NULL
	, SumText NVARCHAR(1000) NULL, DetText VARCHAR(400) NULL);
CREATE TABLE #pcs_first (MetricKey VARCHAR(40) NOT NULL, Entity NVARCHAR(128) NOT NULL, FirstText NVARCHAR(20) NOT NULL);
CREATE TABLE #pcs_rollx (Win VARCHAR(10) NOT NULL, MetricKey VARCHAR(40) NOT NULL, XSum NVARCHAR(1500) NULL, XDet NVARCHAR(2000) NULL);
CREATE TABLE #pcs_obs (Entity NVARCHAR(128) NOT NULL, EvalDate DATETIME NOT NULL, EvalText NVARCHAR(20) NOT NULL, Seg INT NOT NULL, Value BIGINT NOT NULL, Ord INT NULL, Ok INT NULL);
CREATE TABLE #pcs_r14 (Entity NVARCHAR(128) NOT NULL, N INT NOT NULL, Days DECIMAL(19,1) NOT NULL, FirstText NVARCHAR(20) NOT NULL, LastText NVARCHAR(20) NOT NULL, Evidence NVARCHAR(2000) NULL);
/* Section 123 (stage S2): bucket deltas per window ('prior' = up to the last run), then one row of figures per window. */
CREATE TABLE #pcs_brs (Win VARCHAR(10) NOT NULL, LowMs BIGINT NOT NULL, HighMs BIGINT NULL, Band TINYINT NOT NULL
	, CntTo BIGINT NULL, CntFrom BIGINT NULL, MsTo BIGINT NULL, MsFrom BIGINT NULL, TypeTo INT NULL);
CREATE TABLE #pcs_brw (Win VARCHAR(10) NOT NULL, Ord INT NOT NULL, WinLabel NVARCHAR(40) NOT NULL, WindowText NVARCHAR(200) NULL
	, SecondsText VARCHAR(30) NULL, Status VARCHAR(10) NULL, Reason NVARCHAR(400) NULL, N BIGINT NULL, T BIGINT NULL
	, CountPct NVARCHAR(200) NULL, TimePct NVARCHAR(200) NULL, CountList VARCHAR(100) NULL, TimeList VARCHAR(100) NULL
	, TailN BIGINT NULL, TailCP DECIMAL(38,1) NULL, TailTP DECIMAL(38,1) NULL
	, MedLow BIGINT NULL, MedHigh BIGINT NULL, P95Low BIGINT NULL, P95High BIGINT NULL, MedText NVARCHAR(80) NULL, P95Text NVARCHAR(80) NULL
	, Buckets VARCHAR(2000) NULL);

BEGIN TRY
	SET @CustomErrorText = 'Performance counters: snapshots'
	/* THE RULES. Thresholds R1-R15 APPROVED by Adrian 2026-10-21; the verdict
	   sentences approved in advance the same evening; the gate checks each sentence is true to
	   its number. HIST_MAXROWS is a DRAFT (orchestrator ruling, critic M4, overrulable), used from stage S2. Every sentence
	   in sections 120-123 comes from this block: a ruling on wording or on a number is a one-place edit here.
	   Tokens: {Subject} label with entity, {Entity}, {Label}, {Value}, {Unit}, {Window}, {Threshold}, {Reason}, and the
	   header, roll-up and history tokens named in each sentence. Each rule ALSO fires as a Warning when "now" sits outside
	   the instance's own 30-day normal range once 5 history points exist: stage S3. */
	INSERT #pcs_rules (RuleId, Stage, Status, Area, MetricKeys, Windows, Op, Threshold, Threshold2, Normalise, UseReading, MinUptimeSeconds, Severity, SeverityInSample, Sentence)
	VALUES
	  ('R1', 'S1', 'APPROVED', N'Memory', ',PLE,PLE_NODE,', ',start,', '<', 300, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'{Subject} is {Value} s ({Window}), below {Threshold} s: pages leave the buffer pool quickly. This is the lighter tier; section 24 flags below 100 s more heavily.')
	, ('R2', 'S1', 'APPROVED', N'Memory', ',GRANTS_PENDING,', ',start,sample,', '>', 0, NULL, NULL, 'max', NULL, @Result_Warning, @Result_Bad
		, N'Memory grants pending is {Value} ({Window}), above {Threshold}: queries are waiting for memory before they can start.')
	, ('R3a', 'S1', 'APPROVED', N'Memory', ',FREE_LIST_STALLS,', ',start,sample,lastrun,', '>', 2, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'Free list stalls run at {Value} per second ({Window}), above {Threshold}: requests waited for a free buffer page.')
	, ('R3b', 'S1', 'APPROVED', N'Memory', ',LAZY_WRITES,', ',start,sample,lastrun,', '>', 20, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'Lazy writes run at {Value} per second ({Window}), above {Threshold}: the buffer pool is writing pages out to make room.')
	, ('R4', 'S1', 'APPROVED', N'Memory', ',TOTAL_PCT_TARGET,', ',start,', '<', 90, NULL, NULL, NULL, 86400, NULL, NULL
		, N'Total server memory is {Value}% of target ({Window}), below {Threshold}% after more than a day of uptime: SQL Server has not grown to the memory it may use.')
	, ('R5a', 'S1', 'APPROVED', N'Plan cache / ad hoc', ',COMPILES_PER100,', ',start,sample,lastrun,', '>', 10, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'Compilations are {Value} per 100 batches ({Window}), above {Threshold}: many plans are compiled rather than reused.')
	, ('R5b', 'S1', 'APPROVED', N'Plan cache / ad hoc', ',RECOMPILES_PER100,', ',start,sample,lastrun,', '>', 10, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'Re-compilations are {Value} per 100 compilations ({Window}), above {Threshold}.')
	, ('R6', 'S1', 'APPROVED', N'Heaps', ',FWD_PER100,', ',start,sample,lastrun,', '>', 10, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'Forwarded records are {Value} per 100 batches ({Window}), above {Threshold}: heap reads are following forwarding pointers.')
	, ('R7a', 'S1', 'APPROVED', N'Indexes', ',SEARCH_PER_SCAN,', ',start,sample,lastrun,', '<', 1000, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'Index searches are {Value} per full scan ({Window}), fewer than {Threshold}: full scans are a large share of data access.')
	, ('R7b', 'S1', 'APPROVED', N'Indexes', ',SPLITS_PER100,', ',start,sample,lastrun,', '>', 20, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'Page splits are {Value} per 100 batches ({Window}), above {Threshold}.')
	, ('R8a', 'S1', 'APPROVED', N'Blocking', ',DEADLOCKS,', ',start,sample,lastrun,', '>', 0, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'{Value} deadlocks ({Window}).')
	, ('R8b', 'S1', 'APPROVED', N'Blocking', ',LOCK_TIMEOUTS,', ',start,sample,lastrun,', '>', 0, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'{Value} lock timeouts ({Window}).')
	, ('R8c', 'S1', 'APPROVED', N'Blocking', ',BLOCKED,', ',start,', '>', 0, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'{Value} processes are blocked ({Window}).')
	, ('R8d', 'S1', 'APPROVED', N'Blocking', ',AVG_LOCK_WAIT,', ',start,sample,lastrun,', '>', 500, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'The average lock wait is {Value} ms ({Window}), above {Threshold} ms.')
	, ('R9', 'S1', 'APPROVED', N'Connection pooling', ',LOGINS_PER100,', ',start,sample,lastrun,', '>', 2, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'Logins are {Value} per 100 batches ({Window}), above {Threshold}: connections do not look pooled.')
	, ('R10a', 'S1', 'APPROVED', N'Autogrowth / maintenance', ',LOG_SHRINKS,', ',start,sample,lastrun,', '>', 0, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'Database {Entity}: {Value} log shrinks ({Window}).')
	, ('R10b', 'S1', 'APPROVED', N'Autogrowth / maintenance', ',LOG_GROWTHS,', ',start,lastrun,', '>', 10, NULL, 'week', NULL, NULL, @Result_Warning, NULL
		, N'Database {Entity}: {Value} log growths ({Window}), above {Threshold} per week.')
	, ('R10c', 'S1', 'APPROVED', N'Autogrowth / maintenance', ',LOG_PCT_USED,', ',start,', '>', 80, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'Database {Entity}: the log is {Value}% used ({Window}), above {Threshold}%.')
	, ('R11', 'S1', 'APPROVED', N'Deprecated features', ',DEPRECATED,', ',start,lastrun,', '>', 0, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Deprecated feature {Entity} was used {Value} times ({Window}).')
	, ('R12a', 'S1', 'APPROVED', N'AG lag', ',AG_SEND_Q,', ',start,', '>', 102400, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'Availability database {Entity}: the log send queue is {Value} KB ({Window}), above {Threshold} KB.')
	, ('R12b', 'S1', 'APPROVED', N'AG lag', ',AG_RECOVERY_Q,', ',start,', '>', 102400, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'Availability database {Entity}: the recovery queue is {Value} KB ({Window}), above {Threshold} KB.')
	, ('R12c', 'S1', 'APPROVED', N'AG lag', ',AG_DELAY_PER_TXN,', ',start,sample,lastrun,', '>', 10, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'Availability database {Entity}: transaction delay is {Value} ms per mirrored transaction ({Window}), above {Threshold} ms. This is read on the primary replica.')
	, ('R13a', 'S1', 'APPROVED', N'tempdb', ',LONGEST_TXN,', ',start,', '>', 3600, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'The longest running transaction has run {Value} s ({Window}), above {Threshold} s.')
	, ('R13b', 'S1', 'APPROVED', N'tempdb', ',VERSION_STORE,', ',start,', '>', 10485760, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'The version store is {Value} KB ({Window}), above {Threshold} KB.')
	, ('R14', 'S3', 'APPROVED', N'Database usage', ',DB_TXN,', NULL, NULL, 5, 14, NULL, NULL, NULL, NULL, NULL
		, N'Database {Entity}: no transactions started across {Runs} runs over {Days} days (read-only use is not counted by this counter).')
	, ('R15a', 'S2', 'APPROVED', N'Batch response time', NULL, NULL, '>', 50, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'Batches of 1 s or more take {Value}% of elapsed time ({Window}), above {Threshold}%.')
	, ('R15b', 'S2', 'APPROVED', N'Batch response time', NULL, NULL, '>', 10, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'The share of elapsed time in batches of 1 s or more rose {Value} points since the last run, above {Threshold}.')
	, ('HIST_MAXROWS', 'S2', 'DRAFT', NULL, NULL, NULL, '>', 5000000, NULL, NULL, NULL, NULL, NULL, NULL
		, N'History not read: the export table holds {Rows} rows, above the {Threshold}-row cap.')
	/* Stage S3 settings and sentences. HIST_MAXRUNS, HIST_RECENTRUNS and TREND_STEADY are orchestrator DRAFTS (ruling R9.2 and
	   the S3 builder), overrulable here; NORM_MINPOINTS and R_NORM30 are section 6 of the brief as approved. */
	, ('HIST_MAXRUNS', 'S3', 'DRAFT', NULL, NULL, NULL, NULL, 40, NULL, NULL, NULL, NULL, NULL, NULL
		, N'At most {Threshold} runs have their counters read; the latest run is always one of them.')
	, ('HIST_RECENTRUNS', 'S3', 'DRAFT', NULL, NULL, NULL, NULL, 10, 7, NULL, NULL, NULL, NULL, NULL
		, N'Every run of the last {Threshold2} days is read, up to {Threshold} of them; the other runs read are spread evenly over the older history.')
	, ('NORM_MINPOINTS', 'S3', 'APPROVED', NULL, NULL, NULL, '<', 5, NULL, NULL, NULL, NULL, NULL, NULL
		, N'History too thin for a normal range: {Points} runs with this counter in the last 90 days, {Threshold} needed ({Read} of {Runs} runs read).')
	, ('TREND_STEADY', 'S3', 'DRAFT', NULL, NULL, NULL, '>', 10, NULL, NULL, NULL, NULL, NULL, NULL
		, N'A change of at most {Threshold}% between the first and the second half of the window is steady.')
	, ('R_NORM30', 'S3', 'APPROVED', NULL, NULL, ',start,', NULL, NULL, NULL, NULL, NULL, NULL, @Result_Warning, NULL
		, N'Now {Side} its own 30-day normal of {Low} to {High} {Unit} (10th to 90th percentile of {Points} runs).')
	, ('T_NORM', 'S3', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Normal over the last {Days} days: {Low} to {High} {Unit} (10th to 90th percentile of {Points} runs).')
	, ('T_TREND_PLE', 'S3', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Trend over the last {Days} days: {Direction}, from {First} s to {Second} s on average ({Points} runs, first half against second half).')
	, ('T_TREND_LOG', 'S3', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Log growths per week, all databases, over the last {Days} days: {Direction}, from {First} to {Second} a week on average ({Points} pairs of runs, first half against second half).')
	, ('T_TREND_TAIL', 'S3', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Between runs over the last {Days} days, the share of elapsed time in batches of 1 s or more is {Direction}: from {First}% to {Second}% on average ({Points} pairs of runs, first half against second half).')
	, ('T_TREND_THIN_RUNS', 'S3', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Trend: history too thin, {Points} runs in the last 90 days, {Threshold} needed.')
	, ('T_TREND_THIN_PAIRS', 'S3', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Trend: history too thin, {Points} pairs of runs read with no restart between them in the last 90 days, {Threshold} needed.')
	, ('T_FIRST', 'S3', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'First seen above 0 in the run at {First}, the earliest of the {Read} runs read in which it was above 0.')
	, ('T_FIRST_NOW', 'S3', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Not above 0 in any of the {Read} runs read: first seen in this run.')
	, ('T_FIRST_LIST', 'S3', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'First seen above 0 (in the {Read} runs read, or now): {List}.')
	, ('T_HDR_120', 'S1', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Counters since SQL Server started at {Start}, {Days} days ago. {History}')
	, ('T_HDR_121', 'S1', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Counters over a {Seconds} s sample taken during this run, from {From} to {To}.')
	, ('T_OFF_121', 'S1', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'The in-run sample is off (@PerfCounterSampleSeconds is 0 or NULL), so there are no right-now rates.')
	, ('T_NA', 'S1', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Not available on this server: {Labels}.')
	, ('T_NOTAG', 'S1', 'APPROVED', N'AG lag', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Not an AG replica: HADR is off or no availability database is hosted here, so the AG lag counters do not apply.')
	, ('T_INFO', 'S1', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'{Subject}: {Value} {Unit} ({Window}).')
	, ('T_TYPE', 'S1', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'{Subject} could not be computed: {Reason}.')
	, ('T_NODATA', 'S1', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'{Subject}: no data ({Window}): {Reason}.')
	, ('T_RESET', 'S1', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'{Subject}: the counter went down during the window ({Window}), so it was reset; no value for this window.')
	, ('T_ROLLUP', 'S1', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'{Label} ({Window}): {N} {Entities}; {NZ} above zero, {Zero} at zero; highest {Max} {Unit} ({MaxAt}); {Hits} hit a rule.')
	, ('T_ROLLUP0', 'S1', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'{Label} ({Window}): no {Entities} report this counter.')
	, ('T_ROLLUPZ', 'S1', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'{Label} ({Window}): {N} {Entities}, all at zero.')
	, ('T_HIST', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'History: {Runs} runs over {HistDays} days, at least {Restarts} restarts; {Read} of {Runs} runs read.')
	, ('T_HIST_NONE', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'History: no earlier run of this instance in the last 90 days.')
	, ('T_HIST_ABSENT', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'History: none, the export table {Table} does not exist.')
	, ('T_HIST_ERROR', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'History: could not be read: {Reason}.')
	, ('T_HDR_122', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Counters since the last run at {Last}, {Days} days ago: {Seconds} s, measured {How}.')
	, ('T_SKIP_122_NONE', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'No comparison with the last run: no earlier run of this instance stored performance counters in the last 90 days.')
	, ('T_SKIP_122_RESTART', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'SQL Server started at {Start}; the last run at {Last} belongs to an earlier start: counters reset, no comparison.')
	, ('T_SKIP_122_DOWN', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Batch requests went down since the last run at {Last} although SQL Server did not restart: the counters were reset, no comparison.')
	, ('T_SKIP_122_HIST', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'No comparison with the last run. {History}')
	, ('T_HDR_123', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Batch response time in five bands, from the Batch Resp Statistics counters, for each window below. Medians and 95th percentiles are bucket ranges, never exact times.')
	, ('T_BANDS', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'{N} batches completed ({Window}). Share of batches: {CountPct}. Share of elapsed time: {TimePct}.')
	, ('T_TAIL', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'Batches of 1 s or more are {CountPct}% of the count but {TimePct}% of the elapsed time ({Window}).')
	, ('T_TAIL0', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'No batch took 1 s or more ({Window}).')
	, ('T_MEDP95', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'The median batch took {Median} and the 95th percentile {P95} ({Window}). These are bucket ranges, not exact times.')
	, ('T_TAILCHG', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'The share of elapsed time in batches of 1 s or more was {Before}% up to the last run and {After}% since ({Window}): a change of {Value} points.')
	, ('T_TAILCHG0', 'S2', 'APPROVED', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
		, N'No tail change since the last run: {Reason}.');

	/* THE CATALOG: one row per metric (brief section 2b, critic H1/M1/M2). Counter names were read live on SQL 2017, 2022
	   and 2025. Area names the existing sp_triage topic each metric informs. */
	INSERT #pcs_catalog (MetricKey, SortOrder, Area, Label, Kind, ObjSuffix, CounterName, InstScope, InstName, BaseCounter, DenObjSuffix, DenCounter, DenInstName, Scale, ExpectTypes, DenExpectTypes, Unit, EntityNoun, AgOnly)
	VALUES
	  ('BATCHES', 10, N'Workload', N'Batch requests', 'rate', N'SQL Statistics', N'Batch Requests/sec', 'exact', N'', NULL, NULL, NULL, NULL, 1, @pcs_RateTypes, NULL, N'per second', NULL, 0)
	, ('FWD_PER100', 20, N'Heaps', N'Forwarded records', 'per', N'Access Methods', N'Forwarded Records/sec', 'exact', N'', NULL, N'SQL Statistics', N'Batch Requests/sec', N'', 100, @pcs_RateTypes, @pcs_RateTypes, N'per 100 batches', NULL, 0)
	, ('SEARCH_PER_SCAN', 30, N'Indexes', N'Index searches', 'per', N'Access Methods', N'Index Searches/sec', 'exact', N'', NULL, N'Access Methods', N'Full Scans/sec', N'', 1, @pcs_RateTypes, @pcs_RateTypes, N'per full scan', NULL, 0)
	, ('SPLITS_PER100', 40, N'Indexes', N'Page splits', 'per', N'Access Methods', N'Page Splits/sec', 'exact', N'', NULL, N'SQL Statistics', N'Batch Requests/sec', N'', 100, @pcs_RateTypes, @pcs_RateTypes, N'per 100 batches', NULL, 0)
	, ('COMPILES_PER100', 50, N'Plan cache / ad hoc', N'Compilations', 'per', N'SQL Statistics', N'SQL Compilations/sec', 'exact', N'', NULL, N'SQL Statistics', N'Batch Requests/sec', N'', 100, @pcs_RateTypes, @pcs_RateTypes, N'per 100 batches', NULL, 0)
	, ('RECOMPILES_PER100', 60, N'Plan cache / ad hoc', N'Re-compilations', 'per', N'SQL Statistics', N'SQL Re-Compilations/sec', 'exact', N'', NULL, N'SQL Statistics', N'SQL Compilations/sec', N'', 100, @pcs_RateTypes, @pcs_RateTypes, N'per 100 compilations', NULL, 0)
	, ('PLAN_HIT', 70, N'Plan cache / ad hoc', N'Plan cache hit ratio', 'ratio', N'Plan Cache', N'Cache Hit Ratio', 'exact', N'_Total', N'Cache Hit Ratio Base', NULL, NULL, NULL, 1, ',537003264,', NULL, N'%', NULL, 0)
	, ('PLE', 80, N'Memory', N'Page life expectancy', 'gauge', N'Buffer Manager', N'Page life expectancy', 'exact', N'', NULL, NULL, NULL, NULL, 1, @pcs_RawTypes, NULL, N's', NULL, 0)
	, ('PLE_NODE', 90, N'Memory', N'Page life expectancy', 'gauge', N'Buffer Node', N'Page life expectancy', 'pernode', NULL, NULL, NULL, NULL, NULL, 1, @pcs_RawTypes, NULL, N's', N'nodes', 0)
	, ('LAZY_WRITES', 100, N'Memory', N'Lazy writes', 'rate', N'Buffer Manager', N'Lazy writes/sec', 'exact', N'', NULL, NULL, NULL, NULL, 1, @pcs_RateTypes, NULL, N'per second', NULL, 0)
	, ('FREE_LIST_STALLS', 110, N'Memory', N'Free list stalls', 'rate', N'Buffer Manager', N'Free list stalls/sec', 'exact', N'', NULL, NULL, NULL, NULL, 1, @pcs_RateTypes, NULL, N'per second', NULL, 0)
	, ('BUFFER_HIT', 120, N'Memory', N'Buffer cache hit ratio', 'ratio', N'Buffer Manager', N'Buffer cache hit ratio', 'exact', N'', N'Buffer cache hit ratio base', NULL, NULL, NULL, 1, ',537003264,', NULL, N'%', NULL, 0)
	, ('GRANTS_PENDING', 130, N'Memory', N'Memory grants pending', 'gauge', N'Memory Manager', N'Memory Grants Pending', 'exact', N'', NULL, NULL, NULL, NULL, 1, @pcs_RawTypes, NULL, N'requests', NULL, 0)
	, ('TARGET_MEM', 140, N'Memory', N'Target server memory', 'gauge', N'Memory Manager', N'Target Server Memory (KB)', 'exact', N'', NULL, NULL, NULL, NULL, 1, @pcs_RawTypes, NULL, N'KB', NULL, 0)
	, ('TOTAL_MEM', 150, N'Memory', N'Total server memory', 'gauge', N'Memory Manager', N'Total Server Memory (KB)', 'exact', N'', NULL, NULL, NULL, NULL, 1, @pcs_RawTypes, NULL, N'KB', NULL, 0)
	, ('TOTAL_PCT_TARGET', 160, N'Memory', N'Total server memory against target', 'gauge', N'Memory Manager', N'Total Server Memory (KB)', 'exact', N'', NULL, N'Memory Manager', N'Target Server Memory (KB)', N'', 100, @pcs_RawTypes, @pcs_RawTypes, N'% of target', NULL, 0)
	, ('LOCK_WAITS', 170, N'Blocking', N'Lock waits', 'rate', N'Locks', N'Lock Waits/sec', 'exact', N'_Total', NULL, NULL, NULL, NULL, 1, @pcs_RateTypes, NULL, N'per second', NULL, 0)
	, ('LOCK_TIMEOUTS', 180, N'Blocking', N'Lock timeouts (timeout > 0)', 'count', N'Locks', N'Lock Timeouts (timeout > 0)/sec', 'exact', N'_Total', NULL, NULL, NULL, NULL, 1, @pcs_RateTypes, NULL, N'lock timeouts', NULL, 0)
	, ('DEADLOCKS', 190, N'Blocking', N'Deadlocks', 'count', N'Locks', N'Number of Deadlocks/sec', 'exact', N'_Total', NULL, NULL, NULL, NULL, 1, @pcs_RateTypes, NULL, N'deadlocks', NULL, 0)
	, ('AVG_LOCK_WAIT', 200, N'Blocking', N'Average lock wait', 'average', N'Locks', N'Average Wait Time (ms)', 'exact', N'_Total', N'Average Wait Time Base', NULL, NULL, NULL, 1, ',1073874176,', NULL, N'ms', NULL, 0)
	, ('BLOCKED', 210, N'Blocking', N'Processes blocked', 'gauge', N'General Statistics', N'Processes blocked', 'exact', N'', NULL, NULL, NULL, NULL, 1, @pcs_RawTypes, NULL, N'processes', NULL, 0)
	, ('DB_TXN', 220, N'Database usage', N'Transactions started', 'rate', N'Databases', N'Transactions/sec', 'perdb', NULL, NULL, NULL, NULL, NULL, 1, @pcs_RateTypes, NULL, N'per second', N'databases', 0)
	, ('LOG_GROWTHS', 230, N'Autogrowth / maintenance', N'Log growths', 'count', N'Databases', N'Log Growths', 'perdb', NULL, NULL, NULL, NULL, NULL, 1, @pcs_RawTypes, NULL, N'log growths', N'databases', 0)
	, ('LOG_SHRINKS', 240, N'Autogrowth / maintenance', N'Log shrinks', 'count', N'Databases', N'Log Shrinks', 'perdb', NULL, NULL, NULL, NULL, NULL, 1, @pcs_RawTypes, NULL, N'log shrinks', N'databases', 0)
	, ('LOG_PCT_USED', 250, N'Autogrowth / maintenance', N'Percent log used', 'gauge', N'Databases', N'Percent Log Used', 'perdb', NULL, NULL, NULL, NULL, NULL, 1, @pcs_RawTypes, NULL, N'%', N'databases', 0)
	, ('DEPRECATED', 260, N'Deprecated features', N'Deprecated feature use', 'count', N'Deprecated Features', N'Usage', 'perinst', NULL, NULL, NULL, NULL, NULL, 1, @pcs_RawTypes, NULL, N'uses', N'features', 0)
	, ('AG_SEND_Q', 270, N'AG lag', N'Log send queue', 'gauge', N'Database Replica', N'Log Send Queue', 'perdb', NULL, NULL, NULL, NULL, NULL, 1, @pcs_RawTypes, NULL, N'KB', N'availability databases', 1)
	, ('AG_RECOVERY_Q', 280, N'AG lag', N'Recovery queue', 'gauge', N'Database Replica', N'Recovery Queue', 'perdb', NULL, NULL, NULL, NULL, NULL, 1, @pcs_RawTypes, NULL, N'KB', N'availability databases', 1)
	, ('AG_DELAY_PER_TXN', 290, N'AG lag', N'Transaction delay', 'per', N'Database Replica', N'Transaction Delay', 'perdb', NULL, NULL, N'Database Replica', N'Mirrored Write Transactions/sec', NULL, 1, @pcs_RateTypes, @pcs_RateTypes, N'ms per mirrored transaction', N'availability databases', 1)
	, ('VERSION_STORE', 300, N'tempdb', N'Version store size', 'gauge', N'Transactions', N'Version Store Size (KB)', 'exact', N'', NULL, NULL, NULL, NULL, 1, @pcs_RawTypes, NULL, N'KB', NULL, 0)
	, ('LONGEST_TXN', 310, N'tempdb', N'Longest transaction running time', 'gauge', N'Transactions', N'Longest Transaction Running Time', 'exact', N'', NULL, NULL, NULL, NULL, 1, @pcs_RawTypes, NULL, N's', NULL, 0)
	, ('WORKFILES', 320, N'tempdb', N'Workfiles created', 'rate', N'Access Methods', N'Workfiles Created/sec', 'exact', N'', NULL, NULL, NULL, NULL, 1, @pcs_RateTypes, NULL, N'per second', NULL, 0)
	, ('WORKTABLES', 330, N'tempdb', N'Worktables created', 'rate', N'Access Methods', N'Worktables Created/sec', 'exact', N'', NULL, NULL, NULL, NULL, 1, @pcs_RateTypes, NULL, N'per second', NULL, 0)
	, ('LOGINS_PER100', 340, N'Connection pooling', N'Logins', 'per', N'General Statistics', N'Logins/sec', 'exact', N'', NULL, N'SQL Statistics', N'Batch Requests/sec', N'', 100, @pcs_RateTypes, @pcs_RateTypes, N'per 100 batches', NULL, 0)
	, ('USER_CONN', 350, N'Connection pooling', N'User connections', 'gauge', N'General Statistics', N'User Connections', 'exact', N'', NULL, NULL, NULL, NULL, 1, @pcs_RawTypes, NULL, N'connections', NULL, 0);

	INSERT #pcs_want (obj, counter)
	SELECT ObjSuffix, CounterName FROM #pcs_catalog
	UNION SELECT ObjSuffix, BaseCounter FROM #pcs_catalog WHERE BaseCounter IS NOT NULL
	UNION SELECT DenObjSuffix, DenCounter FROM #pcs_catalog WHERE DenCounter IS NOT NULL;

	/* The start time and the uptime in seconds from ONE read (ms_ticks: clock- and DST-proof, invariant 3). */
	SELECT @pcs_StartTime = sqlserver_start_time
		, @pcs_UptimeSec = (ms_ticks - sqlserver_start_time_ms_ticks) / 1000
	FROM sys.dm_os_sys_info;

	/* @PerfCounterSampleSeconds: 0 or NULL = off; otherwise clamped to 1..300 (brief 2a). */
	SET @pcs_SampleReq = CASE WHEN ISNULL(@PerfCounterSampleSeconds, 0) <= 0 THEN 0 WHEN @PerfCounterSampleSeconds > 300 THEN 300 ELSE @PerfCounterSampleSeconds END;

	/* Snapshot 1. Each base and denominator is read by the SAME statement as its counter (brief 2c). The Batch Resp
	   Statistics rows are kept for section 123 (stage S2). */
	SET @pcs_Snap1At = GETDATE();
	INSERT #pcs_snap (snap, taken_at, obj, counter, inst, cntr_type, value)
	SELECT 1, @pcs_Snap1At, s.obj, s.counter, s.inst, s.cntr_type, s.cntr_value
	FROM (
		SELECT LTRIM(RTRIM(SUBSTRING(pc.[object_name], CHARINDEX(':', pc.[object_name]) + 1, 256))) COLLATE DATABASE_DEFAULT AS obj
		, RTRIM(pc.counter_name) COLLATE DATABASE_DEFAULT AS counter
		, RTRIM(pc.instance_name) COLLATE DATABASE_DEFAULT AS inst
		, pc.cntr_type
		, pc.cntr_value
		FROM [sys].dm_os_performance_counters pc
	) s
	WHERE EXISTS (SELECT 1 FROM #pcs_want w WHERE w.obj = s.obj AND w.counter = s.counter)
		OR (s.obj = N'Batch Resp Statistics' AND s.inst IN (N'Elapsed Time:Requests', N'Elapsed Time:Total(ms)'));

	IF @pcs_SampleReq > 0
	BEGIN
		SET @pcs_Delay = CONVERT(CHAR(8), DATEADD(SECOND, @pcs_SampleReq, 0), 108);
		IF @Debug = 1 RAISERROR (N'Performance counters: sampling for %d s',0,1,@pcs_SampleReq) WITH NOWAIT;
		WAITFOR DELAY @pcs_Delay;
		SET @pcs_Snap2At = GETDATE();
		INSERT #pcs_snap (snap, taken_at, obj, counter, inst, cntr_type, value)
		SELECT 2, @pcs_Snap2At, s.obj, s.counter, s.inst, s.cntr_type, s.cntr_value
		FROM (
			SELECT LTRIM(RTRIM(SUBSTRING(pc.[object_name], CHARINDEX(':', pc.[object_name]) + 1, 256))) COLLATE DATABASE_DEFAULT AS obj
			, RTRIM(pc.counter_name) COLLATE DATABASE_DEFAULT AS counter
			, RTRIM(pc.instance_name) COLLATE DATABASE_DEFAULT AS inst
			, pc.cntr_type
			, pc.cntr_value
			FROM [sys].dm_os_performance_counters pc
		) s
		WHERE EXISTS (SELECT 1 FROM #pcs_want w WHERE w.obj = s.obj AND w.counter = s.counter)
			OR (s.obj = N'Batch Resp Statistics' AND s.inst IN (N'Elapsed Time:Requests', N'Elapsed Time:Total(ms)'));
		SET @pcs_SampleSec = DATEDIFF(MILLISECOND, @pcs_Snap1At, @pcs_Snap2At) / 1000.0;
	END

	/* H1 (critic): an AG replica is HADR enabled AND has a Database Replica row other than _Total. SQL 2017/2022 expose
	   Database Replica _Total with no AG, so the counter existing proves nothing. */
	SET @pcs_HadrEnabled = ISNULL(CONVERT(INT, SERVERPROPERTY('IsHadrEnabled')), 0);
	IF @pcs_HadrEnabled = 1 AND EXISTS (SELECT 1 FROM #pcs_snap WHERE snap = 1 AND obj = N'Database Replica' AND inst <> N'_Total')
		SET @pcs_IsAgReplica = 1;

	/* HISTORY (stage S2; the HISTORY notes in the header comment). Its own TRY: a failure here is a could-not-run row and
	   "history could not be read" in 120 and 122, never the loss of 120 and 121. */
	BEGIN TRY
		SET @pcs_HistTable = @ExportDBName + N'.' + @ExportSchema + N'.' + @ExportTableName;
		SET @pcs_HistInst = REPLACE(@ThisServer, N'~', N':');
		SET @pcs_HistCut = CONVERT(NVARCHAR(20), DATEADD(DAY, -90, GETDATE()), 120);
		SET @pcs_HistMax = (SELECT CONVERT(BIGINT, Threshold) FROM #pcs_rules WHERE RuleId = 'HIST_MAXROWS');
		SELECT @pcs_MaxRuns = MAX(CASE WHEN RuleId = 'HIST_MAXRUNS' THEN CONVERT(INT, Threshold) END)
			, @pcs_RecentMax = MAX(CASE WHEN RuleId = 'HIST_RECENTRUNS' THEN CONVERT(INT, Threshold) END)
			, @pcs_RecentDays = MAX(CASE WHEN RuleId = 'HIST_RECENTRUNS' THEN CONVERT(INT, Threshold2) END)
		FROM #pcs_rules;
		IF OBJECT_ID(@pcs_HistTable) IS NULL
			SET @pcs_HistState = 'absent';
		ELSE
		BEGIN
			/* M4: one metadata read before the scan. */
			SET @pcs_HistSql = N'SELECT @r = SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.row_count ELSE 0 END), @mb = SUM(ps.reserved_page_count) * 8 / 1024.0 FROM '
				+ @ExportDBName + N'.sys.dm_db_partition_stats ps WHERE ps.[object_id] = OBJECT_ID(@t);';
			EXEC sp_executesql @pcs_HistSql, N'@t NVARCHAR(400), @r BIGINT OUTPUT, @mb DECIMAL(19,1) OUTPUT', @t = @pcs_HistTable, @r = @pcs_HistRows OUTPUT, @mb = @pcs_HistMB OUTPUT;
			IF ISNULL(@pcs_HistRows, 0) > @pcs_HistMax
				SET @pcs_HistState = 'cap';
			ELSE
			BEGIN
				/* PHASE 1 (HISTORY a): the run list from cheap predicates. The Batch Requests/sec row is found on the last 32
				   characters of Section (the 104 writer strips double spaces, so at most one trailing space is left): no
				   leading-wildcard test over the whole text of every row, which is what made the S2 read cost 25.7 s at
				   1,000,000 rows (R9.2). */
				SET @pcs_HistSql = N'SELECT h.evaldate, h.SectionID, h.Section, h.Summary, h.Details FROM ' + @pcs_HistTable
					+ N' h WHERE h.SQLInstance = @inst AND h.evaldate >= @cut AND h.evaldate < @now AND h.SectionID IN (2, 104, 120)'
					+ N' AND ((h.SectionID = 2 AND h.Summary LIKE N''Uptime:%'')'
					+ N' OR (h.SectionID = 120 AND h.Details LIKE N''[[]sqlserver[_]start[_]time]:%'')'
					+ N' OR (h.SectionID = 104 AND h.Summary = N'''' AND RIGHT(h.Section, 32) LIKE N''%,[[]Counter]:Batch Requests/sec%''));';
				SET @pcs_HistT0 = GETDATE();
				INSERT #pcs_hrun (evaldate, SectionID, Section, Summary, Details)
				EXEC sp_executesql @pcs_HistSql, N'@inst NVARCHAR(505), @cut NVARCHAR(20), @now NVARCHAR(20)', @inst = @pcs_HistInst, @cut = @pcs_HistCut, @now = @evaldate;
				SET @pcs_P1Rows = @@ROWCOUNT;
				SET @pcs_P1Ms = DATEDIFF(MILLISECOND, @pcs_HistT0, GETDATE());

				/* Runs: one per style-120 evaldate. The date is built from its digits (never a language-dependent parse). */
				INSERT #pcs_runs (RunNo, EvalText, EvalDate, HasCounters)
				SELECT ROW_NUMBER() OVER (ORDER BY e.E), e.E
					, DATEADD(SECOND, CONVERT(INT, SUBSTRING(e.E, 12, 2)) * 3600 + CONVERT(INT, SUBSTRING(e.E, 15, 2)) * 60 + CONVERT(INT, SUBSTRING(e.E, 18, 2))
						, DATEADD(DAY, CONVERT(INT, SUBSTRING(e.E, 9, 2)) - 1, DATEADD(MONTH, CONVERT(INT, SUBSTRING(e.E, 6, 2)) - 1, DATEADD(YEAR, CONVERT(INT, LEFT(e.E, 4)) - 1900, CONVERT(DATETIME, 0)))))
					, 0
				FROM (SELECT DISTINCT CASE WHEN h.evaldate LIKE N'[12][0-9][0-9][0-9]-[01][0-9]-[0-3][0-9] [0-2][0-9]:[0-5][0-9]:[0-5][0-9]' THEN LEFT(h.evaldate, 19) END AS E
					FROM #pcs_hrun h) e
				WHERE e.E IS NOT NULL;

				/* Legacy Uptime: (days, 2 decimals), from the section 2 row. */
				UPDATE r SET UptimeDays = u.D
				FROM #pcs_runs r
				JOIN (
					SELECT LEFT(h.evaldate, 20) AS E
						, MAX(CASE WHEN LEN(t.T) BETWEEN 1 AND 20 AND t.T NOT LIKE N'%[^0-9.]%' AND t.T NOT LIKE N'%.%.%' AND t.T LIKE N'%[0-9]%' THEN CONVERT(DECIMAL(23,2), t.T) END) AS D
					FROM #pcs_hrun h
					CROSS APPLY (SELECT CASE WHEN h.Summary LIKE N'Uptime:%' THEN LTRIM(RTRIM(SUBSTRING(h.Summary, 8, CHARINDEX(N';', h.Summary + N';') - 8))) END AS T) t
					WHERE h.SectionID = 2 AND h.Summary LIKE N'Uptime:%'
					GROUP BY LEFT(h.evaldate, 20)
				) u ON u.E = r.EvalText;

				/* This version's 120 header: the exact start and UptimeSeconds. */
				UPDATE r SET StartExact = x.S, UptimeSec = x.U
				FROM #pcs_runs r
				JOIN (
					SELECT LEFT(h.evaldate, 20) AS E
						, MAX(CASE WHEN a.ST LIKE N'[12][0-9][0-9][0-9]-[01][0-9]-[0-3][0-9] [0-2][0-9]:[0-5][0-9]:[0-5][0-9]'
							THEN DATEADD(SECOND, CONVERT(INT, SUBSTRING(a.ST, 12, 2)) * 3600 + CONVERT(INT, SUBSTRING(a.ST, 15, 2)) * 60 + CONVERT(INT, SUBSTRING(a.ST, 18, 2))
								, DATEADD(DAY, CONVERT(INT, SUBSTRING(a.ST, 9, 2)) - 1, DATEADD(MONTH, CONVERT(INT, SUBSTRING(a.ST, 6, 2)) - 1, DATEADD(YEAR, CONVERT(INT, LEFT(a.ST, 4)) - 1900, CONVERT(DATETIME, 0)))))
							END) AS S
						, MAX(CASE WHEN LEN(b.UT) BETWEEN 1 AND 18 AND b.UT NOT LIKE N'%[^0-9]%' THEN CONVERT(BIGINT, b.UT) END) AS U
					FROM #pcs_hrun h
					CROSS APPLY (SELECT SUBSTRING(h.Details, 24, 19) AS ST, CHARINDEX(N';[UptimeSeconds]:', h.Details) AS UP) a
					CROSS APPLY (SELECT CASE WHEN a.UP > 0 THEN SUBSTRING(h.Details, a.UP + 17, CHARINDEX(N';', h.Details + N';', a.UP + 17) - a.UP - 17) END AS UT) b
					WHERE h.SectionID = 120 AND h.Details LIKE N'[[]sqlserver[_]start[_]time]:%'
					GROUP BY LEFT(h.evaldate, 20)
				) x ON x.E = r.EvalText
				WHERE x.S IS NOT NULL AND x.U IS NOT NULL;

				/* Batch Requests/sec of each run (the segment counter test), parsed per critic L1 like every counter row. */
				UPDATE r SET Batches = bq.V
				FROM #pcs_runs r
				JOIN (
					SELECT LEFT(h.evaldate, 20) AS E, MAX(CONVERT(BIGINT, d.D)) AS V
					FROM #pcs_hrun h
					CROSS APPLY (SELECT CASE WHEN LEFT(h.Section, 9) = N'[Object]:' THEN SUBSTRING(h.Section, 10, 4000) ELSE h.Section END AS S
						, LTRIM(RTRIM(ISNULL(h.Details, N''))) AS D) a
					CROSS APPLY (SELECT CHARINDEX(N',[Counter]:', a.S) AS K) k
					CROSS APPLY (SELECT CASE WHEN k.K > 0 THEN LEFT(a.S, k.K - 1) ELSE N'' END AS ObjPart
						, CASE WHEN k.K > 0 THEN SUBSTRING(a.S, k.K + 11, 4000) ELSE N'' END AS CntPart) o
					CROSS APPLY (SELECT CASE WHEN LEN(a.D) BETWEEN 1 AND 18 AND a.D NOT LIKE N'%[^0-9]%' THEN a.D END AS D) d
					WHERE h.SectionID = 104 AND k.K > 0 AND d.D IS NOT NULL
						AND LTRIM(RTRIM(SUBSTRING(o.ObjPart, CHARINDEX(N':', o.ObjPart) + 1, 4000))) = N'SQL Statistics'
						AND LTRIM(RTRIM(o.CntPart)) = N'Batch Requests/sec'
					GROUP BY LEFT(h.evaldate, 20)
				) bq ON bq.E = r.EvalText;

				UPDATE r SET StartBest = CASE WHEN r.StartExact IS NOT NULL THEN r.StartExact
						WHEN r.UptimeDays IS NOT NULL AND r.UptimeDays <> 0.10 THEN DATEADD(MINUTE, 0 - CONVERT(INT, ROUND(r.UptimeDays * 1440, 0)), r.EvalDate) END
					, StartKind = CASE WHEN r.StartExact IS NOT NULL THEN 'exact' WHEN r.UptimeDays IS NOT NULL AND r.UptimeDays <> 0.10 THEN 'estimate' ELSE 'unknown' END
				FROM #pcs_runs r;

				/* SEGMENTS (header note c). SegReason NULL = same segment as the run before. */
				UPDATE b SET SegReason = CASE
						WHEN a.RunNo IS NULL THEN 'first'
						WHEN a.StartBest IS NULL OR b.StartBest IS NULL THEN 'unknown'
						WHEN a.Batches IS NOT NULL AND b.Batches IS NOT NULL AND b.Batches < a.Batches THEN 'counters'
						WHEN a.StartKind = 'exact' AND b.StartKind = 'exact' AND b.UptimeSec < a.UptimeSec THEN 'restart'
						WHEN ABS(DATEDIFF(SECOND, a.StartBest, b.StartBest)) > @pcs_Tol * 60 THEN 'restart'
						WHEN b.StartBest >= DATEADD(MINUTE, 0 - CASE WHEN b.StartKind = 'exact' THEN 0 ELSE @pcs_Tol END, a.EvalDate) THEN 'restart'
						END
				FROM #pcs_runs b
				LEFT JOIN #pcs_runs a ON a.RunNo = b.RunNo - 1;
				UPDATE r SET Seg = (SELECT COUNT(*) FROM #pcs_runs r2 WHERE r2.RunNo <= r.RunNo AND r2.SegReason IS NOT NULL)
				FROM #pcs_runs r;

				SELECT @pcs_Runs = COUNT(*), @pcs_Segs = ISNULL(MAX(Seg), 0)
					, @pcs_Restarts = SUM(CASE WHEN SegReason IN ('counters', 'restart') THEN 1 ELSE 0 END)
					, @pcs_HistDays = ISNULL(CONVERT(DECIMAL(19,1), DATEDIFF(MINUTE, MIN(EvalDate), GETDATE()) / 1440.0), 0)
					, @pcs_PrevEval = MAX(EvalDate)
				FROM #pcs_runs;
				SET @pcs_Restarts = ISNULL(@pcs_Restarts, 0);
				/* This run against the run before it: a restart when that run is older than this start. */
				IF @pcs_PrevEval < @pcs_StartTime SET @pcs_Restarts = @pcs_Restarts + 1;

				/* PHASE 2 (HISTORY a): pick the runs to read. Candidates are the runs whose Batch Requests/sec row was found.
				   First every run of the last @pcs_RecentDays days, newest first, up to @pcs_RecentMax (so the latest run is
				   always read); then the other candidates spread evenly by rank: of M older runs, K are read, rank
				   1 + ROUND(j x (M - 1) / (K - 1)) for j = 0 .. K - 1 (the oldest and the newest of them always among them). */
				UPDATE r SET Picked = 1
				FROM #pcs_runs r
				WHERE r.RunNo IN (SELECT TOP (@pcs_RecentMax) x.RunNo FROM #pcs_runs x
					WHERE x.Batches IS NOT NULL AND x.EvalDate >= DATEADD(DAY, 0 - @pcs_RecentDays, GETDATE()) ORDER BY x.RunNo DESC);
				SET @pcs_M2 = (SELECT COUNT(*) FROM #pcs_runs WHERE Batches IS NOT NULL AND Picked = 0);
				SET @pcs_K2 = @pcs_MaxRuns - (SELECT COUNT(*) FROM #pcs_runs WHERE Picked = 1);
				IF @pcs_K2 >= @pcs_M2
				BEGIN
					UPDATE #pcs_runs SET Picked = 1 WHERE Batches IS NOT NULL;
				END
				ELSE IF @pcs_K2 = 1
				BEGIN
					UPDATE #pcs_runs SET Picked = 1 WHERE RunNo = (SELECT MAX(RunNo) FROM #pcs_runs WHERE Batches IS NOT NULL AND Picked = 0);
				END
				ELSE IF @pcs_K2 > 1
				BEGIN
					UPDATE r SET Picked = 1
					FROM #pcs_runs r
					JOIN (SELECT x.RunNo, ROW_NUMBER() OVER (ORDER BY x.RunNo) AS rn FROM #pcs_runs x WHERE x.Batches IS NOT NULL AND x.Picked = 0) o ON o.RunNo = r.RunNo
					CROSS APPLY (SELECT ((o.rn - 1) * (@pcs_K2 - 1) * 2 + (@pcs_M2 - 1)) / (2 * (@pcs_M2 - 1)) AS j) jj
					WHERE 1 + (jj.j * (@pcs_M2 - 1) * 2 + (@pcs_K2 - 1)) / (2 * (@pcs_K2 - 1)) = o.rn;
				END
				/* The picked evaldates, as a VALUES list: each EvalText passed the digit pattern above, so it holds no quote. */
				SET @pcs_PickList = STUFF((SELECT N',(N''' + r.EvalText + N''')' FROM #pcs_runs r WHERE r.Picked = 1 ORDER BY r.RunNo
					FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, N'');
				IF @pcs_PickList IS NULL SET @pcs_PickList = N'(N'''')';
				SET @pcs_HistSql = N'SELECT h.evaldate, h.SectionID, h.Section, h.Summary, h.Details FROM ' + @pcs_HistTable
					+ N' h JOIN (VALUES ' + @pcs_PickList + N') p (E) ON h.evaldate = p.E WHERE h.SQLInstance = @inst AND h.SectionID = 104;';
				SET @pcs_HistT0 = GETDATE();
				INSERT #pcs_hist (evaldate, SectionID, Section, Summary, Details)
				EXEC sp_executesql @pcs_HistSql, N'@inst NVARCHAR(505), @cut NVARCHAR(20), @now NVARCHAR(20)', @inst = @pcs_HistInst, @cut = @pcs_HistCut, @now = @evaldate;
				SET @pcs_HistRead = @pcs_P1Rows + @@ROWCOUNT;

				/* Counter rows, parsed per critic L1: strip '[Object]:', split on ',[Counter]:', trim, take the object after
				   its FIRST colon, then match the catalog by equality. A value that is not 1-18 digits is not read (L3); a
				   Deprecated Features row at 0 is dropped (as the S2 read did). Every step is CASE-guarded, so a bad row is
				   skipped, never an error. */
				INSERT #pcs_hrow (EvalText, obj, counter, inst, value)
				SELECT LEFT(h.evaldate, 20), p.obj, p.counter, p.inst, MAX(CONVERT(BIGINT, d.D))
				FROM #pcs_hist h
				CROSS APPLY (SELECT CASE WHEN LEFT(h.Section, 9) = N'[Object]:' THEN SUBSTRING(h.Section, 10, 4000) ELSE h.Section END AS S
					, LTRIM(RTRIM(ISNULL(h.Details, N''))) AS D) a
				CROSS APPLY (SELECT CHARINDEX(N',[Counter]:', a.S) AS K) k
				CROSS APPLY (SELECT CASE WHEN k.K > 0 THEN LEFT(a.S, k.K - 1) ELSE N'' END AS ObjPart
					, CASE WHEN k.K > 0 THEN SUBSTRING(a.S, k.K + 11, 4000) ELSE N'' END AS CntPart) o
				CROSS APPLY (SELECT LEFT(LTRIM(RTRIM(SUBSTRING(o.ObjPart, CHARINDEX(N':', o.ObjPart) + 1, 4000))), 128) AS obj
					, LEFT(LTRIM(RTRIM(o.CntPart)), 128) AS counter
					, LEFT(LTRIM(RTRIM(ISNULL(h.Summary, N''))), 128) AS inst) p
				CROSS APPLY (SELECT CASE WHEN LEN(a.D) BETWEEN 1 AND 18 AND a.D NOT LIKE N'%[^0-9]%' THEN a.D END AS D) d
				WHERE h.SectionID = 104 AND k.K > 0 AND d.D IS NOT NULL AND LEN(h.evaldate) = 19
					AND NOT (p.obj = N'Deprecated Features' AND d.D = N'0')
					AND (EXISTS (SELECT 1 FROM #pcs_want w WHERE w.obj = p.obj AND w.counter = p.counter)
						OR (p.obj = N'Batch Resp Statistics' AND p.inst IN (N'Elapsed Time:Requests', N'Elapsed Time:Total(ms)')))
				GROUP BY LEFT(h.evaldate, 20), p.obj, p.counter, p.inst;
				SET @pcs_P2Ms = DATEDIFF(MILLISECOND, @pcs_HistT0, GETDATE());
				SET @pcs_HistReadMs = @pcs_P1Ms + @pcs_P2Ms;

				UPDATE r SET HasCounters = CASE WHEN r.Picked = 1 AND EXISTS (SELECT 1 FROM #pcs_hrow hr WHERE hr.EvalText = r.EvalText) THEN 1 ELSE 0 END
				FROM #pcs_runs r;
				SET @pcs_RunsRead = (SELECT COUNT(*) FROM #pcs_runs WHERE HasCounters = 1);

				/* The uptime a rate point divides by (HISTORY f): exact; else the clock from an exact start of the SAME segment;
				   else the legacy Uptime: days; an unknown start gives none. */
				UPDATE r SET PtSec = CASE
						WHEN r.UptimeSec IS NOT NULL THEN r.UptimeSec
						WHEN sx.S IS NOT NULL AND sx.S < r.EvalDate THEN DATEDIFF(SECOND, sx.S, r.EvalDate)
						WHEN r.StartKind = 'estimate' THEN CONVERT(BIGINT, r.UptimeDays * 86400) END
				FROM #pcs_runs r
				OUTER APPLY (SELECT MAX(x.StartExact) AS S FROM #pcs_runs x WHERE x.Seg = r.Seg) sx
				WHERE r.HasCounters = 1;
				SET @pcs_HistState = 'read';

				IF @Debug = 1
				BEGIN
					SET @pcs_i = 1;
					WHILE @pcs_i <= @pcs_Runs
					BEGIN
						SELECT @pcs_Msg = N'pcs history run ' + CONVERT(NVARCHAR(12), RunNo) + N' ' + EvalText + N' seg ' + CONVERT(NVARCHAR(12), Seg)
							+ N' reason ' + ISNULL(SegReason, N'same') + N' start ' + ISNULL(CONVERT(NVARCHAR(19), StartBest, 120), N'unknown') + N' ' + StartKind
							+ N' uptime ' + ISNULL(CONVERT(NVARCHAR(30), UptimeDays), N'-') + N' d ' + ISNULL(CONVERT(NVARCHAR(30), UptimeSec), N'-') + N' s batches '
							+ ISNULL(CONVERT(NVARCHAR(30), Batches), N'-') + N' picked ' + CONVERT(NVARCHAR(1), Picked) + N' counters ' + CONVERT(NVARCHAR(1), HasCounters)
							+ N' ptsec ' + ISNULL(CONVERT(NVARCHAR(30), PtSec), N'-')
						FROM #pcs_runs WHERE RunNo = @pcs_i;
						RAISERROR (N'%s', 0, 1, @pcs_Msg) WITH NOWAIT;
						SET @pcs_i = @pcs_i + 1;
					END
					RAISERROR (N'pcs history read: phase 1 %d ms %d rows, phase 2 + parse %d ms, %d rows in all, %d of %d runs read', 0, 1
						, @pcs_P1Ms, @pcs_P1Rows, @pcs_P2Ms, @pcs_HistRead, @pcs_RunsRead, @pcs_Runs) WITH NOWAIT;
				END
			END
		END
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Performance counters: history read', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SET @pcs_HistState = 'error';
		SET @pcs_HistReason = LEFT(ISNULL(ERROR_MESSAGE(), N'unknown error'), 400);
		DELETE FROM #pcs_hrun;
		DELETE FROM #pcs_hist;
		DELETE FROM #pcs_hrow;
		DELETE FROM #pcs_runs;
		SELECT @pcs_Runs = 0, @pcs_Segs = 0, @pcs_Restarts = 0, @pcs_HistDays = 0, @pcs_RunsRead = 0;
		RAISERROR (N'Error with Performance counters: history read',0,1) WITH NOWAIT;
	END CATCH

	/* The history basis sentence (120) and the last-run decision (122). */
	SET @pcs_HistSentence = CASE @pcs_HistState
		WHEN 'read' THEN (SELECT CASE WHEN @pcs_Runs = 0 THEN tn.Sentence ELSE REPLACE(REPLACE(REPLACE(REPLACE(t.Sentence, N'{Runs}', CONVERT(NVARCHAR(12), @pcs_Runs))
				, N'{HistDays}', CONVERT(NVARCHAR(30), @pcs_HistDays)), N'{Restarts}', CONVERT(NVARCHAR(12), @pcs_Restarts)), N'{Read}', CONVERT(NVARCHAR(12), @pcs_RunsRead)) END
			FROM #pcs_rules t JOIN #pcs_rules tn ON tn.RuleId = 'T_HIST_NONE' WHERE t.RuleId = 'T_HIST')
		WHEN 'absent' THEN (SELECT REPLACE(Sentence, N'{Table}', @pcs_HistTable) FROM #pcs_rules WHERE RuleId = 'T_HIST_ABSENT')
		WHEN 'cap' THEN (SELECT REPLACE(REPLACE(Sentence, N'{Rows}', CONVERT(NVARCHAR(30), @pcs_HistRows)), N'{Threshold}', CONVERT(NVARCHAR(30), @pcs_HistMax)) FROM #pcs_rules WHERE RuleId = 'HIST_MAXROWS')
		ELSE (SELECT REPLACE(Sentence, N'{Reason}', @pcs_HistReason) FROM #pcs_rules WHERE RuleId = 'T_HIST_ERROR') END;

	SET @pcs_NowBatches = (SELECT MAX(value) FROM #pcs_snap WHERE snap = 1 AND obj = N'SQL Statistics' AND counter = N'Batch Requests/sec' AND inst = N'');
	SELECT TOP 1 @pcs_LastEvalText = EvalText, @pcs_LastEval = EvalDate, @pcs_LastStart = StartBest, @pcs_LastUptimeSec = UptimeSec, @pcs_LastBatches = Batches
	FROM #pcs_runs
	WHERE HasCounters = 1
	ORDER BY RunNo DESC;
	SET @pcs_LastState = CASE
		WHEN @pcs_HistState <> 'read' THEN 'hist'
		WHEN @pcs_LastEval IS NULL THEN 'none'
		WHEN @pcs_LastEval < @pcs_StartTime OR @pcs_LastUptimeSec > @pcs_UptimeSec OR ABS(DATEDIFF(MINUTE, @pcs_LastStart, @pcs_StartTime)) > @pcs_Tol THEN 'restart' /* gate F1/F2 2026-10-22: a stored uptime above now's, or a start that moved, is a restart (invariant 3); NULL on either side compares false */
		WHEN @pcs_LastBatches IS NOT NULL AND @pcs_NowBatches IS NOT NULL AND @pcs_NowBatches < @pcs_LastBatches THEN 'down'
		ELSE 'ok' END;
	IF @pcs_LastState = 'ok'
	BEGIN
		SET @pcs_LastSec = CASE WHEN @pcs_LastUptimeSec IS NOT NULL THEN @pcs_UptimeSec - @pcs_LastUptimeSec ELSE DATEDIFF(SECOND, @pcs_LastEval, @pcs_Snap1At) END;
		/* Snapshot 3: the last run's counters (its 104 rows), the 'from' side of 122. */
		INSERT #pcs_snap (snap, taken_at, obj, counter, inst, cntr_type, value)
		SELECT 3, @pcs_LastEval, hr.obj, hr.counter, hr.inst, 0, MAX(hr.value)
		FROM #pcs_hrow hr
		WHERE hr.EvalText = @pcs_LastEvalText
		GROUP BY hr.obj, hr.counter, hr.inst;
	END
	SET @pcs_LastHeader = LEFT(CASE @pcs_LastState
		WHEN 'ok' THEN (SELECT REPLACE(REPLACE(REPLACE(REPLACE(Sentence, N'{Last}', @pcs_LastEvalText)
				, N'{Days}', CONVERT(NVARCHAR(30), CONVERT(DECIMAL(19,2), @pcs_LastSec / 86400.0))), N'{Seconds}', CONVERT(NVARCHAR(30), CONVERT(DECIMAL(19,0), @pcs_LastSec)))
				, N'{How}', CASE WHEN @pcs_LastUptimeSec IS NOT NULL THEN N'by SQL Server uptime, exact' ELSE N'by the clock from the last run''s start time (that run stored no uptime in seconds)' END)
			FROM #pcs_rules WHERE RuleId = 'T_HDR_122')
		WHEN 'none' THEN (SELECT Sentence FROM #pcs_rules WHERE RuleId = 'T_SKIP_122_NONE')
		WHEN 'restart' THEN (SELECT REPLACE(REPLACE(Sentence, N'{Start}', CONVERT(NVARCHAR(19), @pcs_StartTime, 120)), N'{Last}', @pcs_LastEvalText) FROM #pcs_rules WHERE RuleId = 'T_SKIP_122_RESTART')
		WHEN 'down' THEN (SELECT REPLACE(Sentence, N'{Last}', @pcs_LastEvalText) FROM #pcs_rules WHERE RuleId = 'T_SKIP_122_DOWN')
		ELSE (SELECT REPLACE(Sentence, N'{History}', ISNULL(@pcs_HistSentence, N'')) FROM #pcs_rules WHERE RuleId = 'T_SKIP_122_HIST') END, 1000);
	SET @pcs_LastDetails = LEFT('[Window]:lastrun;[WindowSeconds]:' + CASE WHEN @pcs_LastState = 'ok' THEN CONVERT(VARCHAR(30), CONVERT(DECIMAL(19,3), @pcs_LastSec)) ELSE '0' END
		+ CASE WHEN @pcs_LastState = 'ok' THEN ';[Status]:ok' ELSE ';[Status]:skip;[Reason]:' + CASE WHEN @pcs_LastState = 'hist' THEN @pcs_HistState ELSE @pcs_LastState END END
		+ ';[LastRun]:' + ISNULL(@pcs_LastEvalText, '') + ';[LastRunStart]:' + ISNULL(CONVERT(VARCHAR(19), @pcs_LastStart, 120), '')
		+ ';[Elapsed]:' + CASE WHEN @pcs_LastState <> 'ok' THEN '' WHEN @pcs_LastUptimeSec IS NOT NULL THEN 'uptime' ELSE 'clock' END
		+ ';[sqlserver_start_time]:' + CONVERT(VARCHAR(19), @pcs_StartTime, 120) + ';[HistoryRuns]:' + CONVERT(VARCHAR(12), @pcs_Runs) + ';', 1000);

	/* THE WINDOWS. Every value, rule and row below is keyed by Win. */
	INSERT #pcs_win (Win, SectionID, FromSnap, ToSnap, SampleOff, Seconds, SecondsText, WindowText, Title)
	VALUES ('start', 120, NULL, 1, 0, @pcs_UptimeSec, CONVERT(VARCHAR(30), @pcs_UptimeSec)
		, N'since start, ' + CONVERT(VARCHAR(30), CONVERT(DECIMAL(19,2), @pcs_UptimeSec / 86400.0)) + N' days'
		, N'Performance counters: since start');
	INSERT #pcs_win (Win, SectionID, FromSnap, ToSnap, SampleOff, Seconds, SecondsText, WindowText, Title)
	VALUES ('sample', 121, 1, 2, CASE WHEN @pcs_SampleReq > 0 THEN 0 ELSE 1 END, @pcs_SampleSec, CONVERT(VARCHAR(30), @pcs_SampleSec)
		, CONVERT(VARCHAR(30), @pcs_SampleSec) + N' s sample'
		, N'Performance counters: right now (' + CASE WHEN @pcs_SampleReq > 0 THEN CONVERT(VARCHAR(10), @pcs_SampleReq) + N' s sample)' ELSE N'sample off)' END);
	/* 122: SampleOff = 1 means the window is skipped; its header then carries the reason (@pcs_LastHeader). */
	INSERT #pcs_win (Win, SectionID, FromSnap, ToSnap, SampleOff, Seconds, SecondsText, WindowText, Title)
	VALUES ('lastrun', 122, 3, 1, CASE WHEN @pcs_LastState = 'ok' THEN 0 ELSE 1 END, @pcs_LastSec, CONVERT(VARCHAR(30), CONVERT(DECIMAL(19,3), @pcs_LastSec))
		, CASE WHEN @pcs_LastState = 'ok' THEN N'since the last run at ' + @pcs_LastEvalText + N', ' + CONVERT(VARCHAR(30), CONVERT(DECIMAL(19,2), @pcs_LastSec / 86400.0)) + N' days'
			ELSE N'since the last run' END
		, N'Performance counters: since last run');

	UPDATE w SET
		HeaderSummary = CASE
			WHEN w.Win = 'start' THEN LEFT(REPLACE(REPLACE(REPLACE(t.Sentence, N'{Start}', CONVERT(VARCHAR(19), @pcs_StartTime, 120)), N'{Days}', CONVERT(VARCHAR(30), CONVERT(DECIMAL(19,2), @pcs_UptimeSec / 86400.0)))
				, N'{History}', ISNULL(@pcs_HistSentence, N'')), 1000)
			WHEN w.Win = 'lastrun' THEN @pcs_LastHeader
			WHEN w.SampleOff = 1 THEN t.Sentence
			ELSE REPLACE(REPLACE(REPLACE(t.Sentence, N'{Seconds}', w.SecondsText), N'{From}', CONVERT(VARCHAR(23), @pcs_Snap1At, 121)), N'{To}', CONVERT(VARCHAR(23), @pcs_Snap2At, 121)) END
		, HeaderDetails = CASE
			WHEN w.Win = 'start' THEN '[sqlserver_start_time]:' + CONVERT(VARCHAR(19), @pcs_StartTime, 120) + ';[UptimeSeconds]:' + CONVERT(VARCHAR(30), @pcs_UptimeSec)
				+ ';[WindowSeconds]:' + w.SecondsText + ';[History]:' + @pcs_HistState + ';[HistoryRuns]:' + CONVERT(VARCHAR(12), @pcs_Runs) + ';[HistoryRunsRead]:' + CONVERT(VARCHAR(12), @pcs_RunsRead)
				+ ';[HistoryDays]:' + CONVERT(VARCHAR(30), @pcs_HistDays) + ';[RestartsSeenAtLeast]:' + CONVERT(VARCHAR(12), @pcs_Restarts)
				+ ';[HistorySegments]:' + CONVERT(VARCHAR(12), @pcs_Segs) + ';[HistoryTableRows]:' + ISNULL(CONVERT(VARCHAR(30), @pcs_HistRows), '')
				+ ';[HistoryTableMB]:' + ISNULL(CONVERT(VARCHAR(30), @pcs_HistMB), '') + ';[HistoryRowsRead]:' + CONVERT(VARCHAR(12), @pcs_HistRead)
				+ ';[HistoryReadMs]:' + ISNULL(CONVERT(VARCHAR(12), @pcs_HistReadMs), '') + ';'
			WHEN w.Win = 'lastrun' THEN @pcs_LastDetails
			WHEN w.SampleOff = 1 THEN '[SampleSeconds]:0;[Requested]:' + ISNULL(CONVERT(VARCHAR(12), @PerfCounterSampleSeconds), 'NULL') + ';[Window]:off;[WindowSeconds]:0;'
			ELSE '[SampleSeconds]:' + w.SecondsText + ';[Requested]:' + CONVERT(VARCHAR(12), @pcs_SampleReq) + ';[From]:' + CONVERT(VARCHAR(23), @pcs_Snap1At, 121)
				+ ';[To]:' + CONVERT(VARCHAR(23), @pcs_Snap2At, 121) + ';[WindowSeconds]:' + w.SecondsText + ';' END
	FROM #pcs_win w
	JOIN #pcs_rules t ON t.RuleId = CASE WHEN w.Win = 'start' THEN 'T_HDR_120' WHEN w.Win = 'lastrun' THEN 'T_HDR_122' WHEN w.SampleOff = 1 THEN 'T_OFF_121' ELSE 'T_HDR_121' END;

	/* VALUES: one row per (window, metric, entity) found at the window's end snapshot. A ratio is a point reading, so it
	   is reported in section 120 only. A metric with no counter at all is a 'missing' row (Entity NULL). */
	INSERT #pcs_val (Win, MetricKey, Entity, NumTo, NumType)
	SELECT w.Win, c.MetricKey, s.inst, s.value, s.cntr_type
	FROM #pcs_win w
	JOIN #pcs_catalog c ON (c.AgOnly = 0 OR @pcs_IsAgReplica = 1)
	JOIN #pcs_snap s ON s.snap = w.ToSnap AND s.obj = c.ObjSuffix AND s.counter = c.CounterName
	WHERE w.SampleOff = 0
		AND NOT (w.Win <> 'start' AND c.Kind = 'ratio')
		AND NOT (w.Win = 'lastrun' AND c.Kind = 'gauge')
		AND ((c.InstScope = 'exact' AND s.inst = c.InstName)
			OR (c.InstScope <> 'exact' AND s.inst NOT IN (N'_Total', N'mssqlsystemresource')));

	INSERT #pcs_val (Win, MetricKey, Entity, StatusCode, Status, Reason)
	SELECT w.Win, c.MetricKey, NULL, 'missing', 'missing', N'counter ' + c.ObjSuffix + N': ' + c.CounterName + N' not found'
	FROM #pcs_win w
	JOIN #pcs_catalog c ON (c.AgOnly = 0 OR @pcs_IsAgReplica = 1)
	WHERE w.SampleOff = 0
		AND NOT (w.Win <> 'start' AND c.Kind = 'ratio')
		AND NOT (w.Win = 'lastrun' AND c.Kind = 'gauge')
		AND NOT EXISTS (SELECT 1 FROM #pcs_val v WHERE v.Win = w.Win AND v.MetricKey = c.MetricKey)
		AND (c.InstScope = 'exact' OR NOT EXISTS (SELECT 1 FROM #pcs_snap s WHERE s.snap = w.ToSnap AND s.obj = c.ObjSuffix AND s.counter = c.CounterName));

	UPDATE v SET
		NumFrom = CASE WHEN w.FromSnap IS NULL THEN 0 ELSE nf.value END
		, BaseTo = bt.value
		, BaseType = bt.cntr_type
		, BaseFrom = CASE WHEN w.FromSnap IS NULL THEN 0 ELSE bf.value END
		, DenTo = dt.value
		, DenType = dt.cntr_type
		, DenFrom = CASE WHEN w.FromSnap IS NULL THEN 0 ELSE df.value END
	FROM #pcs_val v
	JOIN #pcs_win w ON w.Win = v.Win
	JOIN #pcs_catalog c ON c.MetricKey = v.MetricKey
	LEFT JOIN #pcs_snap nf ON nf.snap = w.FromSnap AND nf.obj = c.ObjSuffix AND nf.counter = c.CounterName AND nf.inst = v.Entity
	LEFT JOIN #pcs_snap bt ON bt.snap = w.ToSnap AND bt.obj = c.ObjSuffix AND bt.counter = c.BaseCounter AND bt.inst = v.Entity
	LEFT JOIN #pcs_snap bf ON bf.snap = w.FromSnap AND bf.obj = c.ObjSuffix AND bf.counter = c.BaseCounter AND bf.inst = v.Entity
	LEFT JOIN #pcs_snap dt ON dt.snap = w.ToSnap AND dt.obj = c.DenObjSuffix AND dt.counter = c.DenCounter AND dt.inst = ISNULL(c.DenInstName, v.Entity)
	LEFT JOIN #pcs_snap df ON df.snap = w.FromSnap AND df.obj = c.DenObjSuffix AND df.counter = c.DenCounter AND df.inst = ISNULL(c.DenInstName, v.Entity)
	WHERE v.StatusCode IS NULL;

	/* STATUS by kind (invariant 1), first match wins: a wrong type, then a missing reading, then a reset, then no data. */
	UPDATE v SET StatusCode = CASE
			WHEN CHARINDEX(',' + CONVERT(VARCHAR(12), v.NumType) + ',', c.ExpectTypes) = 0 THEN 'type:num'
			WHEN v.NumTo < 0 THEN 'type:neg'
			WHEN c.Kind IN ('ratio', 'average') AND v.BaseTo IS NULL THEN 'type:basemissing'
			WHEN c.Kind IN ('ratio', 'average') AND v.BaseType <> 1073939712 THEN 'type:base'
			WHEN c.DenCounter IS NOT NULL AND v.DenTo IS NULL THEN 'type:denmissing'
			WHEN c.DenCounter IS NOT NULL AND CHARINDEX(',' + CONVERT(VARCHAR(12), v.DenType) + ',', c.DenExpectTypes) = 0 THEN 'type:den'
			WHEN c.Kind IN ('rate', 'count', 'average', 'per') AND (v.NumFrom IS NULL OR (c.Kind = 'average' AND v.BaseFrom IS NULL) OR (c.Kind = 'per' AND v.DenFrom IS NULL)) THEN 'nodata:first'
			WHEN c.Kind IN ('rate', 'count', 'average', 'per') AND v.NumTo < v.NumFrom THEN 'reset'
			WHEN c.Kind = 'average' AND v.BaseTo < v.BaseFrom THEN 'reset'
			WHEN c.Kind = 'per' AND v.DenTo < v.DenFrom THEN 'reset'
			WHEN c.Kind = 'rate' AND w.Seconds <= 0 THEN 'nodata:secs'
			WHEN c.Kind = 'ratio' AND v.BaseTo = 0 THEN 'nodata:base0'
			WHEN c.Kind = 'average' AND v.BaseTo - v.BaseFrom = 0 THEN 'nodata:basedelta'
			WHEN c.Kind = 'per' AND v.DenTo - v.DenFrom = 0 THEN 'nodata:dendelta'
			WHEN c.Kind = 'gauge' AND c.DenCounter IS NOT NULL AND v.DenTo = 0 THEN 'nodata:den0'
			ELSE 'ok' END
	FROM #pcs_val v
	JOIN #pcs_win w ON w.Win = v.Win
	JOIN #pcs_catalog c ON c.MetricKey = v.MetricKey
	WHERE v.StatusCode IS NULL;

	UPDATE v SET
		Status = CASE WHEN CHARINDEX(':', v.StatusCode) > 0 THEN LEFT(v.StatusCode, CHARINDEX(':', v.StatusCode) - 1) ELSE v.StatusCode END
		, Reason = CASE v.StatusCode
			WHEN 'type:num' THEN N'the live cntr_type ' + ISNULL(CONVERT(VARCHAR(12), v.NumType), 'NULL') + N' is not in the family ' + c.ExpectTypes + N' expected for kind ' + c.Kind
			WHEN 'type:neg' THEN N'the raw value is negative, so it was not read'
			WHEN 'type:basemissing' THEN N'its base counter ' + ISNULL(c.BaseCounter, N'?') + N' was not found'
			WHEN 'type:base' THEN N'the base counter has cntr_type ' + ISNULL(CONVERT(VARCHAR(12), v.BaseType), 'NULL') + N', not 1073939712'
			WHEN 'type:denmissing' THEN N'its denominator ' + ISNULL(c.DenObjSuffix, N'?') + N': ' + ISNULL(c.DenCounter, N'?') + N' was not found'
			WHEN 'type:den' THEN N'the denominator has cntr_type ' + ISNULL(CONVERT(VARCHAR(12), v.DenType), 'NULL') + N', not in ' + ISNULL(c.DenExpectTypes, '')
			WHEN 'nodata:first' THEN CASE WHEN v.Win = 'lastrun' THEN N'not in the last run''s counters (absent then is no data, not zero)' ELSE N'not present at the start of the window' END
			WHEN 'reset' THEN N'the counter went down inside the window'
			WHEN 'nodata:secs' THEN N'the window has no length'
			WHEN 'nodata:base0' THEN N'its base is 0'
			WHEN 'nodata:basedelta' THEN N'its base did not move in the window'
			WHEN 'nodata:dendelta' THEN N'its denominator (' + ISNULL(c.DenCounter, N'?') + N') did not move in the window'
			WHEN 'nodata:den0' THEN N'its denominator is 0'
			ELSE v.Reason END
	FROM #pcs_val v
	JOIN #pcs_catalog c ON c.MetricKey = v.MetricKey;

	/* VALUE by kind (invariant 1). Every divisor is NULLIF-guarded as well as status-guarded. */
	UPDATE v SET Value = CONVERT(DECIMAL(38,6), CASE c.Kind
			WHEN 'rate' THEN CONVERT(FLOAT, v.NumTo - v.NumFrom) / NULLIF(CONVERT(FLOAT, w.Seconds), 0)
			WHEN 'count' THEN CONVERT(FLOAT, v.NumTo - v.NumFrom)
			WHEN 'ratio' THEN CONVERT(FLOAT, v.NumTo) * 100.0 / NULLIF(CONVERT(FLOAT, v.BaseTo), 0)
			WHEN 'average' THEN CONVERT(FLOAT, v.NumTo - v.NumFrom) / NULLIF(CONVERT(FLOAT, v.BaseTo - v.BaseFrom), 0)
			WHEN 'per' THEN CONVERT(FLOAT, v.NumTo - v.NumFrom) * CONVERT(FLOAT, c.Scale) / NULLIF(CONVERT(FLOAT, v.DenTo - v.DenFrom), 0)
			WHEN 'gauge' THEN CASE WHEN c.DenCounter IS NULL THEN CONVERT(FLOAT, v.NumTo) ELSE CONVERT(FLOAT, v.NumTo) * CONVERT(FLOAT, c.Scale) / NULLIF(CONVERT(FLOAT, v.DenTo), 0) END
			END)
	FROM #pcs_val v
	JOIN #pcs_win w ON w.Win = v.Win
	JOIN #pcs_catalog c ON c.MetricKey = v.MetricKey
	WHERE v.Status = 'ok';

	/* RULES: the most severe S1 rule that fires for the metric in this window. */
	UPDATE v SET FiredRule = (
		SELECT TOP 1 r.RuleId
		FROM #pcs_rules r
		CROSS APPLY (SELECT CASE
				WHEN r.Normalise = 'week' THEN v.Value * 604800 / CASE WHEN w.Seconds > 604800 THEN w.Seconds ELSE 604800 END
				WHEN r.UseReading = 'max' AND v.Win = 'sample' AND v.NumFrom > v.Value THEN v.NumFrom
				ELSE v.Value END AS Judged) j
		WHERE r.Stage IN ('S1', 'S2') AND r.Op IS NOT NULL
			AND CHARINDEX(',' + v.MetricKey + ',', r.MetricKeys) > 0
			AND CHARINDEX(',' + v.Win + ',', r.Windows) > 0
			AND (r.MinUptimeSeconds IS NULL OR @pcs_UptimeSec >= r.MinUptimeSeconds)
			AND ((r.Op = '>' AND j.Judged > r.Threshold) OR (r.Op = '<' AND j.Judged < r.Threshold))
		ORDER BY ISNULL(CASE WHEN v.Win = 'sample' THEN r.SeverityInSample END, ISNULL(r.Severity, N'0')) DESC, r.RuleId)
	FROM #pcs_val v
	JOIN #pcs_win w ON w.Win = v.Win
	WHERE v.Status = 'ok';

	UPDATE v SET
		ShownValue = CASE WHEN r.UseReading = 'max' AND v.Win = 'sample' AND v.NumFrom > v.Value THEN v.NumFrom ELSE v.Value END
		, Severity = CASE WHEN v.Win = 'sample' AND r.SeverityInSample IS NOT NULL THEN r.SeverityInSample ELSE r.Severity END
	FROM #pcs_val v
	LEFT JOIN #pcs_rules r ON r.RuleId = v.FiredRule
	WHERE v.Status = 'ok';

	UPDATE v SET
		ValueText = CASE WHEN v.ShownValue IS NULL THEN ''
			WHEN c.Kind = 'count' OR (c.Kind = 'gauge' AND c.DenCounter IS NULL) THEN CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,0), v.ShownValue))
			ELSE CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,2), v.ShownValue)) END
		, WindowText = CASE
			WHEN c.Kind IN ('gauge', 'ratio') AND w.Win = 'start' THEN N'reading now'
			WHEN c.Kind IN ('gauge', 'ratio') THEN N'reading at the end of the ' + w.SecondsText + N' s sample'
			WHEN c.InstScope = 'perdb' AND w.Win = 'start' THEN N'since the database came online, at most since start, ' + CONVERT(VARCHAR(30), CONVERT(DECIMAL(19,2), @pcs_UptimeSec / 86400.0)) + N' days'
			ELSE w.WindowText END
		, WindowSecondsText = CASE WHEN c.Kind IN ('gauge', 'ratio') THEN '0' ELSE w.SecondsText END
	FROM #pcs_val v
	JOIN #pcs_win w ON w.Win = v.Win
	JOIN #pcs_catalog c ON c.MetricKey = v.MetricKey;

	SET @pcs_Ready = 1;
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Performance counters: snapshots', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with Performance counters: snapshots',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH

/* HISTORY SUMMARIES (stage S3; HISTORY f-k in the header comment): points, normal ranges and R_NORM30, trends, first seen
   and R14. Its own TRY (k): a failure here is a could-not-run row, and sections 120-123 still write without these clauses. */
IF @pcs_Ready = 1 AND @pcs_HistState = 'read'
BEGIN
	BEGIN TRY
		SET @pcs_Step = N'Performance counters: normals, trends, first seen';
		SELECT @pcs_MinPts = MAX(CASE WHEN RuleId = 'NORM_MINPOINTS' THEN CONVERT(INT, Threshold) END)
			, @pcs_Steady = MAX(CASE WHEN RuleId = 'TREND_STEADY' THEN Threshold END)
		FROM #pcs_rules;

		/* f. POINTS: one per (metric, entity) per run read, by kind, from that run's own cumulative values (critic M6). */
		INSERT #pcs_pt (MetricKey, Entity, RunNo, EvalDate, Value)
		SELECT c.MetricKey, n.inst, r.RunNo, r.EvalDate, x.V
		FROM #pcs_runs r
		JOIN #pcs_hrow n ON n.EvalText = r.EvalText
		JOIN #pcs_catalog c ON c.ObjSuffix = n.obj AND c.CounterName = n.counter
			AND c.Kind IN ('rate', 'per', 'ratio', 'average', 'gauge') AND (c.AgOnly = 0 OR @pcs_IsAgReplica = 1)
			AND ((c.InstScope = 'exact' AND n.inst = c.InstName) OR (c.InstScope <> 'exact' AND n.inst NOT IN (N'_Total', N'mssqlsystemresource')))
		LEFT JOIN #pcs_hrow b ON b.EvalText = r.EvalText AND b.obj = c.ObjSuffix AND b.counter = c.BaseCounter AND b.inst = n.inst
		LEFT JOIN #pcs_hrow d ON d.EvalText = r.EvalText AND d.obj = c.DenObjSuffix AND d.counter = c.DenCounter AND d.inst = ISNULL(c.DenInstName, n.inst)
		CROSS APPLY (SELECT CASE c.Kind
				WHEN 'rate' THEN CASE WHEN r.PtSec > 0 THEN CONVERT(FLOAT, n.value) / CONVERT(FLOAT, r.PtSec) END
				WHEN 'per' THEN CASE WHEN d.value > 0 THEN CONVERT(FLOAT, n.value) * CONVERT(FLOAT, c.Scale) / CONVERT(FLOAT, d.value) END
				WHEN 'ratio' THEN CASE WHEN b.value > 0 THEN CONVERT(FLOAT, n.value) * 100.0 / CONVERT(FLOAT, b.value) END
				WHEN 'average' THEN CASE WHEN b.value > 0 THEN CONVERT(FLOAT, n.value) / CONVERT(FLOAT, b.value) END
				WHEN 'gauge' THEN CASE WHEN c.DenCounter IS NULL THEN CONVERT(FLOAT, n.value)
					WHEN d.value > 0 THEN CONVERT(FLOAT, n.value) * CONVERT(FLOAT, c.Scale) / CONVERT(FLOAT, d.value) END
				END AS V) x
		WHERE r.HasCounters = 1 AND x.V IS NOT NULL;

		/* g. NORMAL RANGES: 10th and 90th percentile by nearest rank, per (metric, entity), over 7, 30 and 90 days. */
		INSERT #pcs_norm (MetricKey, Entity, Days, N, Lo, Hi)
		SELECT q.MetricKey, q.Entity, q.Days, MAX(q.N)
			, MAX(CASE WHEN q.rn = CEILING(0.1 * q.N) THEN q.Value END)
			, MAX(CASE WHEN q.rn = CEILING(0.9 * q.N) THEN q.Value END)
		FROM (
			SELECT p.MetricKey, p.Entity, dd.Days, p.Value
				, ROW_NUMBER() OVER (PARTITION BY p.MetricKey, p.Entity, dd.Days ORDER BY p.Value, p.RunNo) AS rn
				, COUNT(*) OVER (PARTITION BY p.MetricKey, p.Entity, dd.Days) AS N
			FROM #pcs_pt p
			CROSS JOIN (SELECT 7 AS Days UNION ALL SELECT 30 UNION ALL SELECT 90) dd
			WHERE p.EvalDate >= DATEADD(DAY, 0 - dd.Days, @pcs_Snap1At)
		) q
		GROUP BY q.MetricKey, q.Entity, q.Days;
		UPDATE n SET
			LoText = CASE WHEN c.Kind = 'gauge' AND c.DenCounter IS NULL THEN CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,0), n.Lo)) ELSE CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,2), n.Lo)) END
			, HiText = CASE WHEN c.Kind = 'gauge' AND c.DenCounter IS NULL THEN CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,0), n.Hi)) ELSE CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,2), n.Hi)) END
		FROM #pcs_norm n
		JOIN #pcs_catalog c ON c.MetricKey = n.MetricKey;
		UPDATE #pcs_norm SET KeyText = CASE WHEN N >= @pcs_MinPts THEN LoText + ',' + HiText + ',' + CONVERT(VARCHAR(12), N) ELSE 'thin,' + CONVERT(VARCHAR(12), N) END;

		/* R_NORM30: now outside the 30-day range on the side of a threshold rule that judges this metric in section 120. */
		UPDATE v SET NormOut = CASE
				WHEN v.Value < n30.Lo AND EXISTS (SELECT 1 FROM #pcs_rules r WHERE r.Stage IN ('S1', 'S2') AND r.Op = '<' AND CHARINDEX(',' + v.MetricKey + ',', r.MetricKeys) > 0
					AND CHARINDEX(',start,', r.Windows) > 0 AND (r.MinUptimeSeconds IS NULL OR @pcs_UptimeSec >= r.MinUptimeSeconds)) THEN 'below'
				WHEN v.Value > n30.Hi AND EXISTS (SELECT 1 FROM #pcs_rules r WHERE r.Stage IN ('S1', 'S2') AND r.Op = '>' AND CHARINDEX(',' + v.MetricKey + ',', r.MetricKeys) > 0
					AND CHARINDEX(',start,', r.Windows) > 0 AND (r.MinUptimeSeconds IS NULL OR @pcs_UptimeSec >= r.MinUptimeSeconds)) THEN 'above' END
		FROM #pcs_val v
		JOIN #pcs_norm n30 ON n30.MetricKey = v.MetricKey AND n30.Entity = v.Entity AND n30.Days = 30
		WHERE v.Win = 'start' AND v.Status = 'ok' AND n30.N >= @pcs_MinPts;

		/* The normal clause and keys on every section 120 row of a kind that has a normal. */
		UPDATE v SET
			HistSummary = CASE
				WHEN v.NormOut IS NOT NULL THEN REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(tOut.Sentence, N'{Side}', v.NormOut), N'{Low}', n30.LoText), N'{High}', n30.HiText), N'{Unit}', c.Unit), N'{Points}', CONVERT(NVARCHAR(12), n30.N))
				WHEN n30.N >= @pcs_MinPts THEN REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(tN.Sentence, N'{Days}', N'30'), N'{Low}', n30.LoText), N'{High}', n30.HiText), N'{Unit}', c.Unit), N'{Points}', CONVERT(NVARCHAR(12), n30.N))
				WHEN n90.N >= @pcs_MinPts THEN REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(tN.Sentence, N'{Days}', N'90'), N'{Low}', n90.LoText), N'{High}', n90.HiText), N'{Unit}', c.Unit), N'{Points}', CONVERT(NVARCHAR(12), n90.N))
				WHEN @pcs_Runs > 0 THEN REPLACE(REPLACE(REPLACE(REPLACE(tT.Sentence, N'{Points}', CONVERT(NVARCHAR(12), ISNULL(n90.N, 0))), N'{Threshold}', CONVERT(NVARCHAR(12), @pcs_MinPts))
					, N'{Read}', CONVERT(NVARCHAR(12), @pcs_RunsRead)), N'{Runs}', CONVERT(NVARCHAR(12), @pcs_Runs)) END
			, HistDetails = '[Normal7d]:' + ISNULL(n7.KeyText, 'thin,0') + ';[Normal30d]:' + ISNULL(n30.KeyText, 'thin,0') + ';[Normal90d]:' + ISNULL(n90.KeyText, 'thin,0')
				+ ';[NormalShown]:' + CASE WHEN n30.N >= @pcs_MinPts THEN '30d' WHEN n90.N >= @pcs_MinPts THEN '90d' ELSE 'thin' END
				+ CASE WHEN v.NormOut IS NOT NULL THEN ';[NormalOut]:' + v.NormOut + ';[NormalRule]:R_NORM30' ELSE '' END + ';'
		FROM #pcs_val v
		JOIN #pcs_catalog c ON c.MetricKey = v.MetricKey AND c.Kind IN ('rate', 'per', 'ratio', 'average', 'gauge')
		LEFT JOIN #pcs_norm n7 ON n7.MetricKey = v.MetricKey AND n7.Entity = v.Entity AND n7.Days = 7
		LEFT JOIN #pcs_norm n30 ON n30.MetricKey = v.MetricKey AND n30.Entity = v.Entity AND n30.Days = 30
		LEFT JOIN #pcs_norm n90 ON n90.MetricKey = v.MetricKey AND n90.Entity = v.Entity AND n90.Days = 90
		JOIN #pcs_rules tN ON tN.RuleId = 'T_NORM'
		JOIN #pcs_rules tT ON tT.RuleId = 'NORM_MINPOINTS'
		JOIN #pcs_rules tOut ON tOut.RuleId = 'R_NORM30'
		WHERE v.Win = 'start' AND v.Entity IS NOT NULL AND v.Status <> 'missing';

		/* h. TRENDS. Pairs: adjacent runs read, both in ONE segment (invariant 3); the window between them is their uptime
		   difference when both stored it, else the clock. */
		INSERT #pcs_pair (A, B, BDate, Sec)
		SELECT a.EvalText, b.EvalText, b.EvalDate
			, CASE WHEN a.UptimeSec IS NOT NULL AND b.UptimeSec IS NOT NULL THEN b.UptimeSec - a.UptimeSec ELSE DATEDIFF(SECOND, a.EvalDate, b.EvalDate) END
		FROM (SELECT EvalText, EvalDate, Seg, UptimeSec, ROW_NUMBER() OVER (ORDER BY RunNo) AS k FROM #pcs_runs WHERE HasCounters = 1) b
		JOIN (SELECT EvalText, EvalDate, Seg, UptimeSec, ROW_NUMBER() OVER (ORDER BY RunNo) AS k FROM #pcs_runs WHERE HasCounters = 1) a
			ON a.k = b.k - 1 AND a.Seg = b.Seg;
		DELETE FROM #pcs_pair WHERE Sec <= 0;

		INSERT #pcs_tpt (Trend, EvalDate, Value)
		SELECT 'PLE', p.EvalDate, p.Value FROM #pcs_pt p WHERE p.MetricKey = 'PLE' AND p.Entity = N'';
		/* Log growths per week over a pair: the sum over the databases in both runs; a database whose count went down is skipped (M3). */
		INSERT #pcs_tpt (Trend, EvalDate, Value)
		SELECT 'LOG_GROWTHS', p.BDate, SUM(CONVERT(FLOAT, nb.value - na.value)) * 604800.0 / NULLIF(CONVERT(FLOAT, p.Sec), 0)
		FROM #pcs_pair p
		JOIN #pcs_catalog c ON c.MetricKey = 'LOG_GROWTHS'
		JOIN #pcs_hrow nb ON nb.EvalText = p.B AND nb.obj = c.ObjSuffix AND nb.counter = c.CounterName AND nb.inst NOT IN (N'_Total', N'mssqlsystemresource')
		JOIN #pcs_hrow na ON na.EvalText = p.A AND na.obj = nb.obj AND na.counter = nb.counter AND na.inst = nb.inst
		WHERE nb.value >= na.value
		GROUP BY p.A, p.B, p.BDate, p.Sec;
		/* The long-tail share over a pair: the elapsed ms of the buckets from 1 s up, over the elapsed ms of all buckets; a pair
		   with a bucket missing in the earlier run or a bucket that went down is skipped. */
		INSERT #pcs_tpt (Trend, EvalDate, Value)
		SELECT 'TAIL', p.BDate
			, 100.0 * SUM(CASE WHEN k.LowMs >= 1000 THEN CONVERT(FLOAT, mb.value - ma.value) ELSE 0 END) / NULLIF(SUM(CONVERT(FLOAT, mb.value - ma.value)), 0)
		FROM #pcs_pair p
		JOIN #pcs_hrow mb ON mb.EvalText = p.B AND mb.obj = N'Batch Resp Statistics' AND mb.inst = N'Elapsed Time:Total(ms)'
		LEFT JOIN #pcs_hrow ma ON ma.EvalText = p.A AND ma.obj = mb.obj AND ma.counter = mb.counter AND ma.inst = mb.inst
		CROSS APPLY (SELECT CHARINDEX(N'>=', mb.counter) AS P1) a1
		CROSS APPLY (SELECT CASE WHEN a1.P1 > 0 THEN CHARINDEX(N'ms', mb.counter, a1.P1) ELSE 0 END AS P2) a2
		CROSS APPLY (SELECT CASE WHEN a2.P2 > a1.P1 + 2 THEN SUBSTRING(mb.counter, a1.P1 + 2, a2.P2 - a1.P1 - 2) END AS LowT) t
		CROSS APPLY (SELECT CASE WHEN LEN(t.LowT) BETWEEN 1 AND 12 AND t.LowT NOT LIKE N'%[^0-9]%' THEN CONVERT(BIGINT, t.LowT) END AS LowMs) k
		GROUP BY p.A, p.B, p.BDate
		HAVING COUNT(ma.value) = COUNT(*) AND MIN(mb.value - ma.value) >= 0 AND SUM(mb.value - ma.value) > 0;

		INSERT #pcs_trend (Trend, Days, N, Direction)
		SELECT x.Trend, CASE WHEN x.N30 >= @pcs_MinPts THEN 30 WHEN x.N90 >= @pcs_MinPts THEN 90 END
			, CASE WHEN x.N30 >= @pcs_MinPts THEN x.N30 ELSE x.N90 END, 'thin'
		FROM (SELECT tr.Trend
				, SUM(CASE WHEN p.EvalDate >= DATEADD(DAY, -30, @pcs_Snap1At) THEN 1 ELSE 0 END) AS N30
				, SUM(CASE WHEN p.EvalDate >= DATEADD(DAY, -90, @pcs_Snap1At) THEN 1 ELSE 0 END) AS N90
			FROM (SELECT 'PLE' AS Trend UNION ALL SELECT 'LOG_GROWTHS' UNION ALL SELECT 'TAIL') tr
			LEFT JOIN #pcs_tpt p ON p.Trend = tr.Trend
			GROUP BY tr.Trend) x;
		/* First half against second half of the window's points in time order; the middle point is dropped when N is odd. */
		UPDATE t SET FirstMean = m.F, SecondMean = m.S
		FROM #pcs_trend t
		JOIN (SELECT q.Trend
				, AVG(CASE WHEN q.rn <= q.N / 2 THEN q.Value END) AS F
				, AVG(CASE WHEN q.rn > q.N - q.N / 2 THEN q.Value END) AS S
			FROM (SELECT p.Trend, p.Value
					, ROW_NUMBER() OVER (PARTITION BY p.Trend ORDER BY p.EvalDate) AS rn
					, COUNT(*) OVER (PARTITION BY p.Trend) AS N
				FROM #pcs_tpt p
				JOIN #pcs_trend t2 ON t2.Trend = p.Trend AND t2.Days IS NOT NULL
				WHERE p.EvalDate >= DATEADD(DAY, 0 - t2.Days, @pcs_Snap1At)) q
			GROUP BY q.Trend) m ON m.Trend = t.Trend;
		UPDATE #pcs_trend SET Direction = CASE
				WHEN Days IS NULL OR FirstMean IS NULL OR SecondMean IS NULL THEN 'thin'
				WHEN FirstMean = 0 THEN CASE WHEN SecondMean > 0 THEN 'rising' ELSE 'steady' END
				WHEN ABS(SecondMean - FirstMean) * 100.0 / FirstMean <= @pcs_Steady THEN 'steady'
				WHEN SecondMean > FirstMean THEN 'rising'
				ELSE 'falling' END;
		UPDATE t SET
			SumText = CASE WHEN t.Direction = 'thin'
					THEN REPLACE(REPLACE(CASE WHEN t.Trend = 'PLE' THEN tr.ThinRuns ELSE tr.ThinPairs END, N'{Points}', CONVERT(NVARCHAR(12), t.N)), N'{Threshold}', CONVERT(NVARCHAR(12), @pcs_MinPts))
				ELSE REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(CASE t.Trend WHEN 'PLE' THEN tr.SPle WHEN 'LOG_GROWTHS' THEN tr.SLog ELSE tr.STail END
					, N'{Days}', CONVERT(NVARCHAR(12), t.Days)), N'{Direction}', t.Direction), N'{Points}', CONVERT(NVARCHAR(12), t.N))
					, N'{First}', f.FirstText), N'{Second}', f.SecondText) END
			, DetText = '[Trend]:' + t.Direction + ';[TrendWindow]:' + ISNULL(CONVERT(VARCHAR(12), t.Days) + 'd', '') + ';[TrendPoints]:' + CONVERT(VARCHAR(12), t.N)
				+ ';[TrendFirst]:' + ISNULL(f.FirstText, '') + ';[TrendSecond]:' + ISNULL(f.SecondText, '') + ';'
		FROM #pcs_trend t
		CROSS APPLY (SELECT
				CASE WHEN t.Trend = 'PLE' THEN CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,0), t.FirstMean)) WHEN t.Trend = 'TAIL' THEN CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,1), t.FirstMean)) ELSE CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,2), t.FirstMean)) END AS FirstText
				, CASE WHEN t.Trend = 'PLE' THEN CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,0), t.SecondMean)) WHEN t.Trend = 'TAIL' THEN CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,1), t.SecondMean)) ELSE CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,2), t.SecondMean)) END AS SecondText) f
		CROSS JOIN (SELECT MAX(CASE WHEN RuleId = 'T_TREND_PLE' THEN Sentence END) AS SPle
				, MAX(CASE WHEN RuleId = 'T_TREND_LOG' THEN Sentence END) AS SLog
				, MAX(CASE WHEN RuleId = 'T_TREND_TAIL' THEN Sentence END) AS STail
				, MAX(CASE WHEN RuleId = 'T_TREND_THIN_RUNS' THEN Sentence END) AS ThinRuns
				, MAX(CASE WHEN RuleId = 'T_TREND_THIN_PAIRS' THEN Sentence END) AS ThinPairs
			FROM #pcs_rules) tr;

		/* The PLE trend joins the PLE row of section 120; the log-growth trend joins the Log growths roll-up of 120; the
		   long-tail trend joins section 123 (since start, long tail). */
		UPDATE v SET HistSummary = ISNULL(v.HistSummary + N' ', N'') + t.SumText, HistDetails = ISNULL(v.HistDetails, '') + t.DetText
		FROM #pcs_val v
		JOIN #pcs_trend t ON t.Trend = 'PLE'
		WHERE v.Win = 'start' AND v.MetricKey = 'PLE' AND v.Entity = N'';
		INSERT #pcs_rollx (Win, MetricKey, XSum, XDet)
		SELECT 'start', 'LOG_GROWTHS', t.SumText, t.DetText FROM #pcs_trend t WHERE t.Trend = 'LOG_GROWTHS';
		SELECT @pcs_TailTrendSum = t.SumText, @pcs_TailTrendDet = t.DetText FROM #pcs_trend t WHERE t.Trend = 'TAIL';

		/* i. FIRST SEEN: the earliest run read above 0 (a style-120 evaldate sorts as its date); else 'now' when this run is above 0. */
		INSERT #pcs_first (MetricKey, Entity, FirstText)
		SELECT c.MetricKey, n.inst, MIN(r.EvalText)
		FROM #pcs_runs r
		JOIN #pcs_hrow n ON n.EvalText = r.EvalText
		JOIN #pcs_catalog c ON c.ObjSuffix = n.obj AND c.CounterName = n.counter AND c.MetricKey IN ('GRANTS_PENDING', 'LOCK_TIMEOUTS', 'DEPRECATED')
			AND ((c.InstScope = 'exact' AND n.inst = c.InstName) OR (c.InstScope <> 'exact' AND n.inst NOT IN (N'_Total', N'mssqlsystemresource')))
		WHERE r.HasCounters = 1 AND n.value > 0
		GROUP BY c.MetricKey, n.inst;
		INSERT #pcs_first (MetricKey, Entity, FirstText)
		SELECT v.MetricKey, v.Entity, N'now'
		FROM #pcs_val v
		WHERE v.Win = 'start' AND v.MetricKey IN ('GRANTS_PENDING', 'LOCK_TIMEOUTS', 'DEPRECATED') AND v.Entity IS NOT NULL AND v.NumTo > 0
			AND NOT EXISTS (SELECT 1 FROM #pcs_first f WHERE f.MetricKey = v.MetricKey AND f.Entity = v.Entity);
		UPDATE v SET
			HistSummary = ISNULL(v.HistSummary + N' ', N'') + CASE WHEN f.FirstText = N'now' THEN REPLACE(tFN.Sentence, N'{Read}', CONVERT(NVARCHAR(12), @pcs_RunsRead))
				ELSE REPLACE(REPLACE(tF.Sentence, N'{First}', f.FirstText), N'{Read}', CONVERT(NVARCHAR(12), @pcs_RunsRead)) END
			, HistDetails = ISNULL(v.HistDetails, '') + '[FirstSeen]:' + f.FirstText + ';'
		FROM #pcs_val v
		JOIN #pcs_first f ON f.MetricKey = v.MetricKey AND f.Entity = v.Entity
		JOIN #pcs_rules tF ON tF.RuleId = 'T_FIRST'
		JOIN #pcs_rules tFN ON tFN.RuleId = 'T_FIRST_NOW'
		WHERE v.Win = 'start';
		UPDATE v SET HistDetails = ISNULL(v.HistDetails, '') + '[FirstSeen]:none;'
		FROM #pcs_val v
		WHERE v.Win = 'start' AND v.MetricKey IN ('GRANTS_PENDING', 'LOCK_TIMEOUTS', 'DEPRECATED') AND v.Entity IS NOT NULL AND v.Status <> 'missing'
			AND NOT EXISTS (SELECT 1 FROM #pcs_first f WHERE f.MetricKey = v.MetricKey AND f.Entity = v.Entity);
		/* Every deprecated feature seen, on the Deprecated feature use roll-up of 120 (a feature seen only in history has no
		   row of its own): the first 10 in the Summary, all of them in Details (cut at 1500 characters). */
		INSERT #pcs_rollx (Win, MetricKey, XSum, XDet)
		SELECT 'start', 'DEPRECATED'
			, REPLACE(REPLACE(t.Sentence, N'{Read}', CONVERT(NVARCHAR(12), @pcs_RunsRead)), N'{List}'
				, STUFF((SELECT TOP 10 N', ' + f.Entity + N' ' + f.FirstText FROM #pcs_first f WHERE f.MetricKey = 'DEPRECATED'
					ORDER BY CASE WHEN f.FirstText = N'now' THEN 1 ELSE 0 END, f.FirstText, f.Entity FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, N'')
					+ CASE WHEN g.N > 10 THEN N', and ' + CONVERT(NVARCHAR(12), g.N - 10) + N' more' ELSE N'' END)
			, '[FirstSeen]:' + LEFT(STUFF((SELECT ',' + f.FirstText + '=' + REPLACE(REPLACE(REPLACE(f.Entity, '%', '%25'), ';', '%3B'), ',', '%2C') FROM #pcs_first f WHERE f.MetricKey = 'DEPRECATED'
				ORDER BY CASE WHEN f.FirstText = N'now' THEN 1 ELSE 0 END, f.FirstText, f.Entity FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, N''), 1500) + ';'
		FROM (SELECT COUNT(*) AS N FROM #pcs_first WHERE MetricKey = 'DEPRECATED') g
		JOIN #pcs_rules t ON t.RuleId = 'T_FIRST_LIST'
		WHERE g.N > 0;

		/* j. R14: observations of Transactions started per database (each run read, then now), each judged against the one
		   before it in the same segment; now is in the last run's segment only when 122 compares with it. */
		INSERT #pcs_obs (Entity, EvalDate, EvalText, Seg, Value)
		SELECT n.inst, r.EvalDate, r.EvalText, r.Seg, n.value
		FROM #pcs_runs r
		JOIN #pcs_catalog c ON c.MetricKey = 'DB_TXN'
		JOIN #pcs_hrow n ON n.EvalText = r.EvalText AND n.obj = c.ObjSuffix AND n.counter = c.CounterName AND n.inst NOT IN (N'_Total', N'mssqlsystemresource')
		WHERE r.HasCounters = 1;
		SET @pcs_NowSeg = CASE WHEN @pcs_LastState = 'ok' THEN (SELECT MAX(Seg) FROM #pcs_runs WHERE EvalText = @pcs_LastEvalText) ELSE ISNULL((SELECT MAX(Seg) FROM #pcs_runs), 0) + 1 END;
		INSERT #pcs_obs (Entity, EvalDate, EvalText, Seg, Value)
		SELECT s.inst, @pcs_Snap1At, CONVERT(NVARCHAR(20), @pcs_Snap1At, 120), @pcs_NowSeg, s.value
		FROM #pcs_snap s
		JOIN #pcs_catalog c ON c.MetricKey = 'DB_TXN'
		WHERE s.snap = 1 AND s.obj = c.ObjSuffix AND s.counter = c.CounterName AND s.inst NOT IN (N'_Total', N'mssqlsystemresource');
		UPDATE o SET Ord = q.rn
		FROM #pcs_obs o
		JOIN (SELECT Entity, EvalDate, ROW_NUMBER() OVER (PARTITION BY Entity ORDER BY EvalDate) AS rn FROM #pcs_obs) q ON q.Entity = o.Entity AND q.EvalDate = o.EvalDate;
		/* No transaction started: 0 since start (or since the database came online), or the same value as the observation
		   before it in the same segment. */
		UPDATE o SET Ok = CASE WHEN o.Value = 0 THEN 1 WHEN p.Seg = o.Seg AND p.Value = o.Value THEN 1 ELSE 0 END
		FROM #pcs_obs o
		LEFT JOIN #pcs_obs p ON p.Entity = o.Entity AND p.Ord = o.Ord - 1;
		INSERT #pcs_r14 (Entity, N, Days, FirstText, LastText, Evidence)
		SELECT g.Entity, g.N, CONVERT(DECIMAL(19,1), g.Mins / 1440.0), g.FirstText, g.LastText
			, LEFT(STUFF((SELECT ',' + o2.EvalText + '=' + CONVERT(VARCHAR(30), o2.Value) FROM #pcs_obs o2 WHERE o2.Entity = g.Entity ORDER BY o2.Ord
				FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, N''), 1500)
		FROM (SELECT o.Entity, COUNT(*) AS N, DATEDIFF(MINUTE, MIN(o.EvalDate), MAX(o.EvalDate)) AS Mins, MIN(o.EvalText) AS FirstText, MAX(o.EvalText) AS LastText
			FROM #pcs_obs o
			GROUP BY o.Entity
			HAVING MIN(o.Ok) = 1) g
		JOIN #pcs_rules r ON r.RuleId = 'R14'
		WHERE g.N >= r.Threshold AND g.Mins >= r.Threshold2 * 1440;

		/* Last, so a failure above leaves every severity as the threshold rules set it: R_NORM30 raises a row to Warning. */
		UPDATE v SET Severity = CASE WHEN ISNULL(v.Severity, N'0') < rn.Severity THEN rn.Severity ELSE v.Severity END
		FROM #pcs_val v
		JOIN #pcs_rules rn ON rn.RuleId = 'R_NORM30'
		WHERE v.Win = 'start' AND v.NormOut IS NOT NULL;
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Performance counters: normals, trends, first seen', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE()
			RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
		RAISERROR (N'Error with Performance counters: normals, trends, first seen',0,1) WITH NOWAIT;
		UPDATE #pcs_val SET HistSummary = NULL, HistDetails = NULL, NormOut = NULL;
		DELETE FROM #pcs_rollx;
		DELETE FROM #pcs_r14;
		SELECT @pcs_TailTrendSum = NULL, @pcs_TailTrendDet = NULL;
	END CATCH
END

/* SECTIONS 120, 121 and 122: ONE row-writing body, run once per window, each run in its own TRY/CATCH (brief 2e), so a failure
   in one section becomes that section's "could not run" row and the other still writes. */
IF @pcs_Ready = 1
BEGIN
	SET @pcs_Sec = (SELECT MIN(SectionID) FROM #pcs_win);
	WHILE @pcs_Sec IS NOT NULL
	BEGIN
		BEGIN TRY
			SET @pcs_Step = (SELECT Title FROM #pcs_win WHERE SectionID = @pcs_Sec);
			/* Header: the window, and in 120 the keys that make a later run's history exact. */
			INSERT #output_sqldba_org_sp_triage ([SectionID], [Section], [Summary], [Details])
			SELECT w.SectionID, w.Title, w.HeaderSummary, w.HeaderDetails
			FROM #pcs_win w
			WHERE w.SectionID = @pcs_Sec;

			/* H1: one "not an AG replica" row instead of the AG lag counters. */
			INSERT #output_sqldba_org_sp_triage ([SectionID], [Section], [Summary], [Details])
			SELECT w.SectionID, N'AG lag - not an AG replica', t.Sentence
				, '[Area]:AG lag;[IsHadrEnabled]:' + CONVERT(VARCHAR(5), @pcs_HadrEnabled) + ';[Status]:notag;[Window]:' + w.WindowText + ';[WindowSeconds]:' + w.SecondsText + ';'
			FROM #pcs_win w
			JOIN #pcs_rules t ON t.RuleId = 'T_NOTAG'
			WHERE w.SectionID = @pcs_Sec AND w.SampleOff = 0 AND @pcs_IsAgReplica = 0
				AND EXISTS (SELECT 1 FROM #pcs_catalog c WHERE c.AgOnly = 1);

			/* One "not available on this server" row per area with missing counters. */
			INSERT #output_sqldba_org_sp_triage ([SectionID], [Section], [Summary], [Details])
			SELECT w.SectionID, a.Area + N' - not available'
				, REPLACE(t.Sentence, N'{Labels}', STUFF((
					SELECT N', ' + c2.Label
					FROM #pcs_val v2 JOIN #pcs_catalog c2 ON c2.MetricKey = v2.MetricKey
					WHERE v2.Win = w.Win AND v2.Status = 'missing' AND c2.Area = a.Area
					ORDER BY c2.SortOrder
					FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, N''))
				, '[Area]:' + a.Area + ';[Status]:missing;[Missing]:' + STUFF((
					SELECT ',' + v2.MetricKey
					FROM #pcs_val v2 JOIN #pcs_catalog c2 ON c2.MetricKey = v2.MetricKey
					WHERE v2.Win = w.Win AND v2.Status = 'missing' AND c2.Area = a.Area
					ORDER BY c2.SortOrder
					FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, N'') + ';[Window]:' + w.WindowText + ';[WindowSeconds]:' + w.SecondsText + ';'
			FROM #pcs_win w
			JOIN (SELECT DISTINCT v.Win, c.Area FROM #pcs_val v JOIN #pcs_catalog c ON c.MetricKey = v.MetricKey WHERE v.Status = 'missing') a ON a.Win = w.Win
			JOIN #pcs_rules t ON t.RuleId = 'T_NA'
			WHERE w.SectionID = @pcs_Sec;

			/* Metric rows: every single-instance and per-node metric; a per-database, per-replica or per-feature entity only
			   when it hits a rule. The sentence is the fired rule's, else the information, no-data, reset or type sentence. */
			INSERT #output_sqldba_org_sp_triage ([SectionID], [Section], [Summary], [Details], [Severity])
			SELECT w.SectionID
				, c.Area + N' - ' + c.Label + CASE WHEN c.InstScope = 'exact' THEN N'' WHEN c.InstScope = 'pernode' THEN N' [node ' + v.Entity + N']' ELSE N' [' + v.Entity + N']' END
				, LEFT(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
					CASE v.Status WHEN 'ok' THEN ISNULL(r.Sentence, tInfo.Sentence) WHEN 'type' THEN tType.Sentence WHEN 'reset' THEN tReset.Sentence ELSE tNoData.Sentence END
					, N'{Subject}', c.Label + CASE WHEN c.InstScope = 'exact' THEN N'' WHEN c.InstScope = 'pernode' THEN N' (node ' + v.Entity + N')' ELSE N' (' + v.Entity + N')' END)
					, N'{Entity}', ISNULL(v.Entity, N''))
					, N'{Label}', c.Label)
					, N'{Value}', ISNULL(v.ValueText, ''))
					, N'{Unit}', c.Unit)
					, N'{Window}', ISNULL(v.WindowText, w.WindowText))
					, N'{Threshold}', ISNULL(CASE WHEN r.Threshold = FLOOR(r.Threshold) THEN CONVERT(VARCHAR(40), CONVERT(DECIMAL(38,0), r.Threshold)) ELSE CONVERT(VARCHAR(40), CONVERT(DECIMAL(38,2), r.Threshold)) END, ''))
					, N'{Reason}', ISNULL(v.Reason, N'')) + ISNULL(N' ' + v.HistSummary, N''), 4000)
				, LEFT('[Metric]:' + c.MetricKey + ';[Kind]:' + c.Kind + ';[Value]:' + ISNULL(v.ValueText, '') + ';[Unit]:' + c.Unit
					+ ';[Window]:' + ISNULL(v.WindowText, w.WindowText) + ';[WindowSeconds]:' + ISNULL(v.WindowSecondsText, w.SecondsText)
					+ ';[CntrType]:' + ISNULL(CONVERT(VARCHAR(12), v.NumType), '') + ';[Status]:' + v.Status
					+ CASE WHEN c.InstScope = 'exact' THEN '' ELSE ';[Entity]:' + REPLACE(REPLACE(REPLACE(v.Entity, '%', '%25'), ';', '%3B'), ',', '%2C') END
					+ CASE WHEN v.FiredRule IS NULL THEN '' ELSE ';[Rule]:' + v.FiredRule + ';[Threshold]:'
						+ CASE WHEN r.Threshold = FLOOR(r.Threshold) THEN CONVERT(VARCHAR(40), CONVERT(DECIMAL(38,0), r.Threshold)) ELSE CONVERT(VARCHAR(40), CONVERT(DECIMAL(38,2), r.Threshold)) END END
					+ CASE WHEN v.Status = 'ok' AND (c.Kind = 'per' OR c.DenCounter IS NOT NULL)
						THEN ';[Numerator]:' + CONVERT(VARCHAR(30), CASE WHEN c.Kind = 'per' THEN v.NumTo - v.NumFrom ELSE v.NumTo END)
							+ ';[Denominator]:' + CONVERT(VARCHAR(30), CASE WHEN c.Kind = 'per' THEN v.DenTo - v.DenFrom ELSE v.DenTo END)
							+ ';[Scale]:' + CONVERT(VARCHAR(30), CONVERT(DECIMAL(19,0), c.Scale)) ELSE '' END
					+ CASE WHEN v.Status = 'ok' AND c.Kind = 'ratio' THEN ';[Base]:' + CONVERT(VARCHAR(30), v.BaseTo)
						WHEN v.Status = 'ok' AND c.Kind = 'average' THEN ';[Base]:' + CONVERT(VARCHAR(30), v.BaseTo - v.BaseFrom) ELSE '' END
					+ CASE WHEN v.Status = 'ok' AND c.Kind = 'gauge' AND c.DenCounter IS NULL AND w.FromSnap IS NOT NULL
						THEN ';[Readings]:' + ISNULL(CONVERT(VARCHAR(30), v.NumFrom), '') + ',' + CONVERT(VARCHAR(30), v.NumTo) ELSE '' END
					+ CASE WHEN v.Status <> 'ok' THEN ';[Reason]:' + REPLACE(ISNULL(v.Reason, N''), ';', ',') ELSE '' END
					+ ';' + ISNULL(v.HistDetails, ''), 4000)
				, v.Severity
			FROM #pcs_val v
			JOIN #pcs_win w ON w.Win = v.Win
			JOIN #pcs_catalog c ON c.MetricKey = v.MetricKey
			LEFT JOIN #pcs_rules r ON r.RuleId = v.FiredRule
			JOIN #pcs_rules tInfo ON tInfo.RuleId = 'T_INFO'
			JOIN #pcs_rules tType ON tType.RuleId = 'T_TYPE'
			JOIN #pcs_rules tReset ON tReset.RuleId = 'T_RESET'
			JOIN #pcs_rules tNoData ON tNoData.RuleId = 'T_NODATA'
			WHERE w.SectionID = @pcs_Sec
				AND v.Status <> 'missing'
				AND (c.InstScope IN ('exact', 'pernode') OR v.FiredRule IS NOT NULL OR v.NormOut IS NOT NULL)
			ORDER BY c.SortOrder, v.Entity;

			/* Roll-up rows: ONE per per-database, per-replica or per-feature metric. [Value] is the sum for a rate or a
			   count (the instance total), the highest reading otherwise. */
			INSERT #output_sqldba_org_sp_triage ([SectionID], [Section], [Summary], [Details])
			SELECT w.SectionID
				, c.Area + N' - ' + c.Label + N' (all ' + c.EntityNoun + N')'
				, LEFT(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
					CASE WHEN g.N = 0 THEN t0.Sentence WHEN g.NZ = 0 THEN tz.Sentence ELSE t.Sentence END
					, N'{Label}', c.Label)
					, N'{Window}', ISNULL(g.WindowText, w.WindowText))
					, N'{N}', CONVERT(VARCHAR(12), g.N))
					, N'{Entities}', c.EntityNoun)
					, N'{NZ}', CONVERT(VARCHAR(12), g.NZ))
					, N'{Zero}', CONVERT(VARCHAR(12), g.Z))
					, N'{Max}', ISNULL(mx.ValueText, N'-'))
					, N'{Unit}', c.Unit)
					, N'{MaxAt}', CASE WHEN g.NZ = 0 THEN N'-' ELSE ISNULL(mx.Entity, N'-') END)
					, N'{Hits}', CONVERT(VARCHAR(12), g.Hits)) + ISNULL(N' ' + x.XSum, N''), 4000)
				, LEFT('[Metric]:' + c.MetricKey + ';[Kind]:' + c.Kind
					+ ';[Value]:' + ISNULL(CASE WHEN c.Kind = 'count' THEN CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,0), g.SumValue))
						WHEN c.Kind = 'rate' THEN CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,2), g.SumValue))
						WHEN c.Kind = 'gauge' AND c.DenCounter IS NULL THEN CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,0), g.MaxValue))
						ELSE CONVERT(VARCHAR(50), CONVERT(DECIMAL(38,2), g.MaxValue)) END, '')
					+ ';[Unit]:' + c.Unit + ';[Window]:' + ISNULL(g.WindowText, w.WindowText) + ';[WindowSeconds]:' + ISNULL(g.WindowSecondsText, w.SecondsText)
					+ ';[Status]:rollup;[Entities]:' + CONVERT(VARCHAR(12), g.N) + ';[NonZero]:' + CONVERT(VARCHAR(12), g.NZ) + ';[Zero]:' + CONVERT(VARCHAR(12), g.Z)
					+ ';[Hits]:' + CONVERT(VARCHAR(12), g.Hits) + ';[NotComputed]:' + CONVERT(VARCHAR(12), g.NotOk)
					+ ';[Max]:' + ISNULL(mx.ValueText, '') + ';[MaxAt]:' + CASE WHEN g.NZ = 0 THEN '' ELSE ISNULL(REPLACE(REPLACE(REPLACE(mx.Entity, '%', '%25'), ';', '%3B'), ',', '%2C'), '') END
					+ ';[NonZeroNames]:' + ISNULL(CASE WHEN LEN(nz.Names) > 1500 THEN LEFT(nz.Names, 1500) + ',...(' + CONVERT(VARCHAR(12), g.NZ) + ' in all)' ELSE nz.Names END, '')
					+ ';[ZeroNames]:' + ISNULL(CASE WHEN LEN(zn.Names) > 1500 THEN LEFT(zn.Names, 1500) + ',...(' + CONVERT(VARCHAR(12), g.Z) + ' in all)' ELSE zn.Names END, '')
					+ ';' + ISNULL(x.XDet, ''), 4000)
			FROM #pcs_win w
			JOIN #pcs_catalog c ON c.InstScope IN ('perdb', 'perinst')
			JOIN (
				SELECT v.Win, v.MetricKey
					, SUM(CASE WHEN v.Status = 'ok' THEN 1 ELSE 0 END) AS N
					, SUM(CASE WHEN v.Status = 'ok' AND v.Value > 0 THEN 1 ELSE 0 END) AS NZ
					, SUM(CASE WHEN v.Status = 'ok' AND v.Value = 0 THEN 1 ELSE 0 END) AS Z
					, SUM(CASE WHEN v.FiredRule IS NOT NULL OR v.NormOut IS NOT NULL THEN 1 ELSE 0 END) AS Hits
					, SUM(CASE WHEN v.Status <> 'ok' THEN 1 ELSE 0 END) AS NotOk
					, SUM(CASE WHEN v.Status = 'ok' THEN v.Value END) AS SumValue
					, MAX(CASE WHEN v.Status = 'ok' THEN v.Value END) AS MaxValue
					, MAX(v.WindowText) AS WindowText
					, MAX(v.WindowSecondsText) AS WindowSecondsText
				FROM #pcs_val v
				WHERE v.Status <> 'missing'
				GROUP BY v.Win, v.MetricKey
			) g ON g.Win = w.Win AND g.MetricKey = c.MetricKey
			LEFT JOIN #pcs_rollx x ON x.Win = w.Win AND x.MetricKey = c.MetricKey
			JOIN #pcs_rules t ON t.RuleId = 'T_ROLLUP'
			JOIN #pcs_rules t0 ON t0.RuleId = 'T_ROLLUP0'
			JOIN #pcs_rules tz ON tz.RuleId = 'T_ROLLUPZ' /* every entity at zero: name none of them (S1 gate LOW) */
			OUTER APPLY (
				SELECT TOP 1 vm.Entity, vm.ValueText
				FROM #pcs_val vm
				WHERE vm.Win = w.Win AND vm.MetricKey = c.MetricKey AND vm.Status = 'ok'
				ORDER BY vm.Value DESC, vm.Entity
			) mx
			OUTER APPLY (
				SELECT STUFF((
					SELECT ',' + REPLACE(REPLACE(REPLACE(vn.Entity, '%', '%25'), ';', '%3B'), ',', '%2C')
					FROM #pcs_val vn
					WHERE vn.Win = w.Win AND vn.MetricKey = c.MetricKey AND vn.Status = 'ok' AND vn.Value > 0
					ORDER BY vn.Entity
					FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, N'') AS Names
			) nz
			OUTER APPLY (
				SELECT STUFF((
					SELECT ',' + REPLACE(REPLACE(REPLACE(vz.Entity, '%', '%25'), ';', '%3B'), ',', '%2C')
					FROM #pcs_val vz
					WHERE vz.Win = w.Win AND vz.MetricKey = c.MetricKey AND vz.Status = 'ok' AND vz.Value = 0
					ORDER BY vz.Entity
					FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, N'') AS Names
			) zn
			WHERE w.SectionID = @pcs_Sec
			ORDER BY c.SortOrder;

			/* R14 (stage S3, HISTORY j): one Information row per database with no transaction started across the history. */
			INSERT #output_sqldba_org_sp_triage ([SectionID], [Section], [Summary], [Details])
			SELECT 120, c.Area + N' - no transactions [' + q.Entity + N']'
				, REPLACE(REPLACE(REPLACE(r.Sentence, N'{Entity}', q.Entity), N'{Runs}', CONVERT(NVARCHAR(12), q.N)), N'{Days}', CONVERT(NVARCHAR(30), q.Days))
				, LEFT('[Metric]:' + c.MetricKey + ';[Kind]:' + c.Kind + ';[Status]:history;[Rule]:R14;[Threshold]:' + CONVERT(VARCHAR(40), CONVERT(DECIMAL(38,0), r.Threshold))
					+ ';[Entity]:' + REPLACE(REPLACE(REPLACE(q.Entity, '%', '%25'), ';', '%3B'), ',', '%2C') + ';[Runs]:' + CONVERT(VARCHAR(12), q.N)
					+ ';[Days]:' + CONVERT(VARCHAR(30), q.Days) + ';[FirstRun]:' + q.FirstText + ';[LastRun]:' + q.LastText + ';[Evidence]:' + ISNULL(q.Evidence, '') + ';', 4000)
			FROM #pcs_r14 q
			JOIN #pcs_catalog c ON c.MetricKey = 'DB_TXN'
			JOIN #pcs_rules r ON r.RuleId = 'R14'
			WHERE @pcs_Sec = 120
			ORDER BY q.Entity;
		END TRY
		BEGIN CATCH
			INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(@pcs_Step, N'Performance counters'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
			SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
			RAISERROR (N'Error with Performance counters',0,1) WITH NOWAIT;
		END CATCH
		SET @pcs_Sec = (SELECT MIN(SectionID) FROM #pcs_win WHERE SectionID > @pcs_Sec);
	END

	/* SECTION 123: batch response-time profile (stage S2; header note e). Its own TRY/CATCH (brief 2e). */
	BEGIN TRY
		SET @pcs_Step = N'Batch response-time profile';
		/* Bucket deltas per window: the bucket edges are parsed from the counter name ('Batches >=000010ms & <000020ms'),
		   CASE-guarded. 'prior' = the last run's own since-start profile (snapshot 3 against zero). */
		INSERT #pcs_brs (Win, LowMs, HighMs, Band, CntTo, CntFrom, MsTo, MsFrom, TypeTo)
		SELECT x.Win, k.LowMs, k.HighMs
			, CASE WHEN k.LowMs < 10 THEN 1 WHEN k.LowMs < 100 THEN 2 WHEN k.LowMs < 1000 THEN 3 WHEN k.LowMs < 10000 THEN 4 ELSE 5 END
			, rt.value, CASE WHEN x.FromSnap IS NULL THEN 0 ELSE rf.value END
			, mt.value, CASE WHEN x.FromSnap IS NULL THEN 0 ELSE mf.value END
			, rt.cntr_type
		FROM (SELECT w.Win, w.FromSnap, w.ToSnap FROM #pcs_win w WHERE w.SampleOff = 0
			UNION ALL SELECT 'prior', NULL, 3 WHERE @pcs_LastState = 'ok') x
		JOIN #pcs_snap rt ON rt.snap = x.ToSnap AND rt.obj = N'Batch Resp Statistics' AND rt.inst = N'Elapsed Time:Requests'
		CROSS APPLY (SELECT CHARINDEX(N'>=', rt.counter) AS P1, CHARINDEX(N'<', rt.counter) AS P3) a
		CROSS APPLY (SELECT CASE WHEN a.P1 > 0 THEN CHARINDEX(N'ms', rt.counter, a.P1) ELSE 0 END AS P2
			, CASE WHEN a.P3 > 0 THEN CHARINDEX(N'ms', rt.counter, a.P3) ELSE 0 END AS P4) b
		CROSS APPLY (SELECT CASE WHEN b.P2 > a.P1 + 2 THEN SUBSTRING(rt.counter, a.P1 + 2, b.P2 - a.P1 - 2) END AS LowT
			, CASE WHEN b.P4 > a.P3 + 1 THEN SUBSTRING(rt.counter, a.P3 + 1, b.P4 - a.P3 - 1) END AS HighT) t
		CROSS APPLY (SELECT CASE WHEN LEN(t.LowT) BETWEEN 1 AND 12 AND t.LowT NOT LIKE N'%[^0-9]%' THEN CONVERT(BIGINT, t.LowT) END AS LowMs
			, CASE WHEN LEN(t.HighT) BETWEEN 1 AND 12 AND t.HighT NOT LIKE N'%[^0-9]%' THEN CONVERT(BIGINT, t.HighT) END AS HighMs) k
		LEFT JOIN #pcs_snap rf ON rf.snap = x.FromSnap AND rf.obj = rt.obj AND rf.counter = rt.counter AND rf.inst = rt.inst
		LEFT JOIN #pcs_snap mt ON mt.snap = x.ToSnap AND mt.obj = rt.obj AND mt.counter = rt.counter AND mt.inst = N'Elapsed Time:Total(ms)'
		LEFT JOIN #pcs_snap mf ON mf.snap = x.FromSnap AND mf.obj = rt.obj AND mf.counter = rt.counter AND mf.inst = N'Elapsed Time:Total(ms)'
		WHERE k.LowMs IS NOT NULL;

		INSERT #pcs_brw (Win, Ord, WinLabel, WindowText, SecondsText, Status)
		SELECT w.Win, CASE w.Win WHEN 'start' THEN 1 WHEN 'sample' THEN 2 ELSE 3 END
			, CASE w.Win WHEN 'start' THEN N'since start' WHEN 'sample' THEN N'right now' ELSE N'since the last run' END
			, CASE WHEN w.SampleOff = 1 AND w.Win = 'sample' THEN N'off' ELSE w.WindowText END, w.SecondsText
			, CASE WHEN w.SampleOff = 1 THEN 'off' END
		FROM #pcs_win w
		UNION ALL
		SELECT 'prior', 4, N'up to the last run', N'up to the last run at ' + @pcs_LastEvalText, ISNULL(CONVERT(VARCHAR(30), @pcs_LastUptimeSec), ''), NULL
		WHERE @pcs_LastState = 'ok';

		/* Status per window, first match wins (invariant 1 and critic M3): no buckets, a wrong type, a bucket missing at the
		   window's start, a bucket that went down (reset), no batch at all. */
		UPDATE b SET Status = CASE
				WHEN NOT EXISTS (SELECT 1 FROM #pcs_brs s WHERE s.Win = b.Win) THEN 'missing'
				WHEN EXISTS (SELECT 1 FROM #pcs_brs s WHERE s.Win = b.Win AND b.Win <> 'prior' AND s.TypeTo <> 65792) THEN 'type'
				WHEN EXISTS (SELECT 1 FROM #pcs_brs s WHERE s.Win = b.Win AND (s.CntFrom IS NULL OR s.MsFrom IS NULL OR s.MsTo IS NULL)) THEN 'nodata'
				WHEN EXISTS (SELECT 1 FROM #pcs_brs s WHERE s.Win = b.Win AND (s.CntTo < s.CntFrom OR s.MsTo < s.MsFrom)) THEN 'reset'
				WHEN (SELECT SUM(s.CntTo - s.CntFrom) FROM #pcs_brs s WHERE s.Win = b.Win) = 0 THEN 'nobatch'
				ELSE 'ok' END
		FROM #pcs_brw b
		WHERE b.Status IS NULL;
		UPDATE b SET Reason = CASE b.Status
				WHEN 'missing' THEN N'the Batch Resp Statistics buckets were not found'
				WHEN 'type' THEN N'a Batch Resp Statistics bucket has cntr_type ' + ISNULL((SELECT CONVERT(NVARCHAR(12), MAX(s.TypeTo)) FROM #pcs_brs s WHERE s.Win = b.Win AND s.TypeTo <> 65792), N'?') + N', not 65792'
				WHEN 'nodata' THEN CASE WHEN b.Win = 'lastrun' THEN N'the last run stored no Batch Resp Statistics buckets' ELSE N'a bucket is missing at the start of the window' END
				WHEN 'nobatch' THEN N'no batch completed in the window'
				END
		FROM #pcs_brw b;

		UPDATE b SET N = g.N, T = g.T, TailN = g.TailN
			, TailCP = CONVERT(DECIMAL(38,1), 100.0 * CONVERT(FLOAT, g.TailN) / CONVERT(FLOAT, g.N))
			, TailTP = CASE WHEN g.T > 0 THEN CONVERT(DECIMAL(38,1), 100.0 * CONVERT(FLOAT, g.TailT) / CONVERT(FLOAT, g.T)) END
		FROM #pcs_brw b
		JOIN (SELECT s.Win, SUM(s.CntTo - s.CntFrom) AS N, SUM(s.MsTo - s.MsFrom) AS T
				, SUM(CASE WHEN s.LowMs >= 1000 THEN s.CntTo - s.CntFrom ELSE 0 END) AS TailN
				, SUM(CASE WHEN s.LowMs >= 1000 THEN s.MsTo - s.MsFrom ELSE 0 END) AS TailT
			FROM #pcs_brs s GROUP BY s.Win) g ON g.Win = b.Win
		WHERE b.Status = 'ok';

		/* Band shares, the 17 buckets, and the median and p95 BUCKETS (the first bucket whose running count reaches 50% / 95%
		   of the batches): ranges, never an invented exact time. */
		UPDATE b SET
			CountPct = STUFF((SELECT N', ' + bn.Label + N' ' + CONVERT(NVARCHAR(20), CONVERT(DECIMAL(38,1), 100.0 * CONVERT(FLOAT, ISNULL(bs.C, 0)) / CONVERT(FLOAT, b.N))) + N'%'
				FROM (VALUES (1, N'under 10 ms'), (2, N'10-100 ms'), (3, N'100 ms-1 s'), (4, N'1-10 s'), (5, N'10 s or more')) bn (Band, Label)
				OUTER APPLY (SELECT SUM(s.CntTo - s.CntFrom) AS C FROM #pcs_brs s WHERE s.Win = b.Win AND s.Band = bn.Band) bs
				ORDER BY bn.Band FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, N'')
			, TimePct = CASE WHEN b.T > 0 THEN STUFF((SELECT N', ' + bn.Label + N' ' + CONVERT(NVARCHAR(20), CONVERT(DECIMAL(38,1), 100.0 * CONVERT(FLOAT, ISNULL(bs.M, 0)) / CONVERT(FLOAT, b.T))) + N'%'
				FROM (VALUES (1, N'under 10 ms'), (2, N'10-100 ms'), (3, N'100 ms-1 s'), (4, N'1-10 s'), (5, N'10 s or more')) bn (Band, Label)
				OUTER APPLY (SELECT SUM(s.MsTo - s.MsFrom) AS M FROM #pcs_brs s WHERE s.Win = b.Win AND s.Band = bn.Band) bs
				ORDER BY bn.Band FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, N'') ELSE N'not measurable, no elapsed time was recorded' END
			, CountList = STUFF((SELECT ',' + CONVERT(VARCHAR(20), CONVERT(DECIMAL(38,1), 100.0 * CONVERT(FLOAT, ISNULL(bs.C, 0)) / CONVERT(FLOAT, b.N)))
				FROM (VALUES (1), (2), (3), (4), (5)) bn (Band)
				OUTER APPLY (SELECT SUM(s.CntTo - s.CntFrom) AS C FROM #pcs_brs s WHERE s.Win = b.Win AND s.Band = bn.Band) bs
				ORDER BY bn.Band FOR XML PATH(''), TYPE).value('.', 'VARCHAR(MAX)'), 1, 1, '')
			, TimeList = CASE WHEN b.T > 0 THEN STUFF((SELECT ',' + CONVERT(VARCHAR(20), CONVERT(DECIMAL(38,1), 100.0 * CONVERT(FLOAT, ISNULL(bs.M, 0)) / CONVERT(FLOAT, b.T)))
				FROM (VALUES (1), (2), (3), (4), (5)) bn (Band)
				OUTER APPLY (SELECT SUM(s.MsTo - s.MsFrom) AS M FROM #pcs_brs s WHERE s.Win = b.Win AND s.Band = bn.Band) bs
				ORDER BY bn.Band FOR XML PATH(''), TYPE).value('.', 'VARCHAR(MAX)'), 1, 1, '') ELSE '' END
			, Buckets = LEFT(STUFF((SELECT ',' + CONVERT(VARCHAR(20), s.LowMs) + '-' + ISNULL(CONVERT(VARCHAR(20), s.HighMs), '') + ':' + CONVERT(VARCHAR(30), s.CntTo - s.CntFrom) + ':' + CONVERT(VARCHAR(30), s.MsTo - s.MsFrom)
				FROM #pcs_brs s WHERE s.Win = b.Win
				ORDER BY s.LowMs FOR XML PATH(''), TYPE).value('.', 'VARCHAR(MAX)'), 1, 1, ''), 2000)
			, MedLow = m.LowMs, MedHigh = m.HighMs, P95Low = p.LowMs, P95High = p.HighMs
		FROM #pcs_brw b
		OUTER APPLY (SELECT TOP 1 s.LowMs, s.HighMs FROM #pcs_brs s
			WHERE s.Win = b.Win AND 2 * (SELECT SUM(s2.CntTo - s2.CntFrom) FROM #pcs_brs s2 WHERE s2.Win = s.Win AND s2.LowMs <= s.LowMs) >= b.N
			ORDER BY s.LowMs) m
		OUTER APPLY (SELECT TOP 1 s.LowMs, s.HighMs FROM #pcs_brs s
			WHERE s.Win = b.Win AND 100 * (SELECT SUM(s2.CntTo - s2.CntFrom) FROM #pcs_brs s2 WHERE s2.Win = s.Win AND s2.LowMs <= s.LowMs) >= 95 * b.N
			ORDER BY s.LowMs) p
		WHERE b.Status = 'ok';
		UPDATE b SET
			MedText = CASE WHEN b.MedHigh IS NULL THEN CASE WHEN b.MedLow >= 1000 THEN CONVERT(NVARCHAR(20), b.MedLow / 1000) + N' s' ELSE CONVERT(NVARCHAR(20), b.MedLow) + N' ms' END + N' or more'
				WHEN b.MedLow = 0 THEN N'under ' + CASE WHEN b.MedHigh >= 1000 THEN CONVERT(NVARCHAR(20), b.MedHigh / 1000) + N' s' ELSE CONVERT(NVARCHAR(20), b.MedHigh) + N' ms' END
				WHEN b.MedHigh < 1000 THEN N'between ' + CONVERT(NVARCHAR(20), b.MedLow) + N' and ' + CONVERT(NVARCHAR(20), b.MedHigh) + N' ms'
				WHEN b.MedLow >= 1000 THEN N'between ' + CONVERT(NVARCHAR(20), b.MedLow / 1000) + N' and ' + CONVERT(NVARCHAR(20), b.MedHigh / 1000) + N' s'
				ELSE N'between ' + CONVERT(NVARCHAR(20), b.MedLow) + N' ms and ' + CONVERT(NVARCHAR(20), b.MedHigh / 1000) + N' s' END
			, P95Text = CASE WHEN b.P95High IS NULL THEN CASE WHEN b.P95Low >= 1000 THEN CONVERT(NVARCHAR(20), b.P95Low / 1000) + N' s' ELSE CONVERT(NVARCHAR(20), b.P95Low) + N' ms' END + N' or more'
				WHEN b.P95Low = 0 THEN N'under ' + CASE WHEN b.P95High >= 1000 THEN CONVERT(NVARCHAR(20), b.P95High / 1000) + N' s' ELSE CONVERT(NVARCHAR(20), b.P95High) + N' ms' END
				WHEN b.P95High < 1000 THEN N'between ' + CONVERT(NVARCHAR(20), b.P95Low) + N' and ' + CONVERT(NVARCHAR(20), b.P95High) + N' ms'
				WHEN b.P95Low >= 1000 THEN N'between ' + CONVERT(NVARCHAR(20), b.P95Low / 1000) + N' and ' + CONVERT(NVARCHAR(20), b.P95High / 1000) + N' s'
				ELSE N'between ' + CONVERT(NVARCHAR(20), b.P95Low) + N' ms and ' + CONVERT(NVARCHAR(20), b.P95High / 1000) + N' s' END
		FROM #pcs_brw b
		WHERE b.Status = 'ok';

		/* Rows: the header, then per window (since start, right now, since the last run) the bands, the long tail (R15a), the
		   median and p95 and, since the last run, the tail change (R15b); a window that could not be computed is ONE row
		   saying why. Without the counters at all, one "not available" row. */
		INSERT #output_sqldba_org_sp_triage ([SectionID], [Section], [Summary], [Details])
		SELECT 123, N'Batch response-time profile', t.Sentence
			, '[Source]:Batch Resp Statistics;[Windows]:' + ISNULL(STUFF((SELECT ',' + b.Win FROM #pcs_brw b WHERE b.Ord < 4 AND b.Status = 'ok' ORDER BY b.Ord FOR XML PATH(''), TYPE).value('.', 'VARCHAR(MAX)'), 1, 1, ''), '')
				+ ';[Bands]:0-10,10-100,100-1000,1000-10000,10000-;'
		FROM #pcs_rules t WHERE t.RuleId = 'T_HDR_123';

		IF NOT EXISTS (SELECT 1 FROM #pcs_snap WHERE snap = 1 AND obj = N'Batch Resp Statistics')
			INSERT #output_sqldba_org_sp_triage ([SectionID], [Section], [Summary], [Details])
			SELECT 123, N'Batch response time - not available', REPLACE(t.Sentence, N'{Labels}', N'Batch response-time profile (the Batch Resp Statistics counters)')
				, '[Area]:Batch response time;[Status]:missing;[Missing]:BATCH_RESP;'
			FROM #pcs_rules t WHERE t.RuleId = 'T_NA';
		ELSE
			INSERT #output_sqldba_org_sp_triage ([SectionID], [Section], [Summary], [Details], [Severity])
			SELECT 123
				, N'Batch response time - ' + b.WinLabel + CASE WHEN b.Status <> 'ok' THEN N'' WHEN k.RowKind = 1 THEN N' - bands' WHEN k.RowKind = 2 THEN N' - long tail'
					WHEN k.RowKind = 3 THEN N' - median and p95' ELSE N' - tail change' END
				, CASE
					WHEN b.Status <> 'ok' THEN CASE b.Status
						WHEN 'off' THEN CASE WHEN b.Win = 'lastrun' THEN @pcs_LastHeader ELSE s.SOff END
						WHEN 'reset' THEN REPLACE(REPLACE(s.SReset, N'{Subject}', N'Batch response time'), N'{Window}', ISNULL(b.WindowText, N''))
						WHEN 'type' THEN REPLACE(REPLACE(s.SType, N'{Subject}', N'Batch response time'), N'{Reason}', ISNULL(b.Reason, N''))
						ELSE REPLACE(REPLACE(REPLACE(s.SNoData, N'{Subject}', N'Batch response time'), N'{Window}', ISNULL(b.WindowText, N'')), N'{Reason}', ISNULL(b.Reason, N'')) END
					WHEN k.RowKind = 1 THEN REPLACE(REPLACE(REPLACE(REPLACE(s.SBands, N'{N}', CONVERT(NVARCHAR(30), b.N)), N'{Window}', b.WindowText), N'{CountPct}', b.CountPct), N'{TimePct}', b.TimePct)
					WHEN k.RowKind = 2 AND b.TailTP > s.T15a THEN REPLACE(REPLACE(REPLACE(s.S15a, N'{Value}', CONVERT(NVARCHAR(30), b.TailTP)), N'{Window}', b.WindowText), N'{Threshold}', CONVERT(NVARCHAR(40), CONVERT(DECIMAL(38,0), s.T15a)))
					WHEN k.RowKind = 2 AND b.TailN = 0 THEN REPLACE(s.STail0, N'{Window}', b.WindowText)
					WHEN k.RowKind = 2 THEN REPLACE(REPLACE(REPLACE(s.STail, N'{CountPct}', CONVERT(NVARCHAR(30), b.TailCP)), N'{TimePct}', ISNULL(CONVERT(NVARCHAR(30), b.TailTP), N'n/a')), N'{Window}', b.WindowText)
					WHEN k.RowKind = 3 THEN REPLACE(REPLACE(REPLACE(s.SMedP95, N'{Median}', b.MedText), N'{P95}', b.P95Text), N'{Window}', b.WindowText)
					WHEN c.Chg > s.T15b THEN REPLACE(REPLACE(s.S15b, N'{Value}', CONVERT(NVARCHAR(30), c.Chg)), N'{Threshold}', CONVERT(NVARCHAR(40), CONVERT(DECIMAL(38,0), s.T15b)))
					WHEN c.Chg IS NOT NULL THEN REPLACE(REPLACE(REPLACE(REPLACE(s.STailChg, N'{Before}', CONVERT(NVARCHAR(30), p.TailTP)), N'{After}', CONVERT(NVARCHAR(30), b.TailTP)), N'{Value}', CONVERT(NVARCHAR(30), c.Chg)), N'{Window}', b.WindowText)
					ELSE REPLACE(s.STailChg0, N'{Reason}', CASE WHEN p.Status IS NULL THEN N'there is no profile up to the last run'
						WHEN p.Status = 'nobatch' THEN N'no batch had completed up to the last run'
						WHEN p.Status IN ('missing', 'nodata') THEN N'the last run stored no Batch Resp Statistics buckets'
						WHEN p.Status = 'ok' THEN N'no elapsed time was recorded'
						ELSE ISNULL(p.Reason, p.Status) END) END
					+ CASE WHEN b.Status = 'ok' AND k.RowKind = 2 AND b.Win = 'start' THEN ISNULL(N' ' + @pcs_TailTrendSum, N'') ELSE N'' END
				, '[Metric]:' + CASE WHEN b.Status <> 'ok' OR k.RowKind = 1 THEN 'BATCH_BANDS' WHEN k.RowKind = 2 THEN 'BATCH_TAIL' WHEN k.RowKind = 3 THEN 'BATCH_MEDIAN_P95' ELSE 'BATCH_TAIL_CHANGE' END
					+ ';[Kind]:count;[Window]:' + ISNULL(b.WindowText, N'') + ';[WindowSeconds]:' + CASE WHEN b.Status = 'off' THEN '0' ELSE ISNULL(b.SecondsText, '') END
					+ ';[Status]:' + b.Status
					+ CASE WHEN b.Status <> 'ok' THEN ';[Reason]:' + REPLACE(ISNULL(b.Reason, CASE WHEN b.Win <> 'lastrun' THEN N'sample off' WHEN @pcs_LastState = 'hist' THEN @pcs_HistState ELSE @pcs_LastState END), ';', ',')
						WHEN k.RowKind = 1 THEN ';[Value]:' + CONVERT(VARCHAR(30), b.N) + ';[Unit]:batches;[Batches]:' + CONVERT(VARCHAR(30), b.N) + ';[ElapsedMs]:' + CONVERT(VARCHAR(30), b.T)
							+ ';[CountPct]:' + ISNULL(b.CountList, '') + ';[TimePct]:' + ISNULL(b.TimeList, '') + ';[Buckets]:' + ISNULL(b.Buckets, '')
						WHEN k.RowKind = 2 THEN ';[Value]:' + ISNULL(CONVERT(VARCHAR(30), b.TailTP), '') + ';[Unit]:% of elapsed time;[TailBatches]:' + CONVERT(VARCHAR(30), b.TailN)
							+ ';[TailCountPct]:' + CONVERT(VARCHAR(30), b.TailCP) + ';[TailTimePct]:' + ISNULL(CONVERT(VARCHAR(30), b.TailTP), '')
							+ CASE WHEN b.TailTP > s.T15a THEN ';[Rule]:R15a;[Threshold]:' + CONVERT(VARCHAR(40), CONVERT(DECIMAL(38,0), s.T15a)) ELSE '' END
						WHEN k.RowKind = 3 THEN ';[Unit]:ms;[Median]:' + CONVERT(VARCHAR(20), b.MedLow) + '-' + ISNULL(CONVERT(VARCHAR(20), b.MedHigh), '')
							+ ';[P95]:' + CONVERT(VARCHAR(20), b.P95Low) + '-' + ISNULL(CONVERT(VARCHAR(20), b.P95High), '')
						ELSE ';[Value]:' + ISNULL(CONVERT(VARCHAR(30), c.Chg), '') + ';[Unit]:points;[Before]:' + ISNULL(CONVERT(VARCHAR(30), p.TailTP), '') + ';[After]:' + ISNULL(CONVERT(VARCHAR(30), b.TailTP), '')
							+ CASE WHEN c.Chg > s.T15b THEN ';[Rule]:R15b;[Threshold]:' + CONVERT(VARCHAR(40), CONVERT(DECIMAL(38,0), s.T15b)) ELSE '' END END
					+ ';' + CASE WHEN b.Status = 'ok' AND k.RowKind = 2 AND b.Win = 'start' THEN ISNULL(@pcs_TailTrendDet, '') ELSE '' END
				, CASE WHEN b.Status = 'ok' AND k.RowKind = 2 AND b.TailTP > s.T15a THEN s.Sev15a
					WHEN b.Status = 'ok' AND k.RowKind = 4 AND c.Chg > s.T15b THEN s.Sev15b END
			FROM #pcs_brw b
			CROSS JOIN (VALUES (1), (2), (3), (4)) k (RowKind)
			LEFT JOIN #pcs_brw p ON p.Win = 'prior'
			CROSS APPLY (SELECT CONVERT(DECIMAL(38,1), b.TailTP - p.TailTP) AS Chg) c
			CROSS JOIN (SELECT MAX(CASE WHEN RuleId = 'T_BANDS' THEN Sentence END) AS SBands
					, MAX(CASE WHEN RuleId = 'T_TAIL' THEN Sentence END) AS STail
					, MAX(CASE WHEN RuleId = 'T_TAIL0' THEN Sentence END) AS STail0
					, MAX(CASE WHEN RuleId = 'T_MEDP95' THEN Sentence END) AS SMedP95
					, MAX(CASE WHEN RuleId = 'T_TAILCHG' THEN Sentence END) AS STailChg
					, MAX(CASE WHEN RuleId = 'T_TAILCHG0' THEN Sentence END) AS STailChg0
					, MAX(CASE WHEN RuleId = 'T_OFF_121' THEN Sentence END) AS SOff
					, MAX(CASE WHEN RuleId = 'T_RESET' THEN Sentence END) AS SReset
					, MAX(CASE WHEN RuleId = 'T_TYPE' THEN Sentence END) AS SType
					, MAX(CASE WHEN RuleId = 'T_NODATA' THEN Sentence END) AS SNoData
					, MAX(CASE WHEN RuleId = 'R15a' THEN Sentence END) AS S15a
					, MAX(CASE WHEN RuleId = 'R15a' THEN Threshold END) AS T15a
					, MAX(CASE WHEN RuleId = 'R15a' THEN Severity END) AS Sev15a
					, MAX(CASE WHEN RuleId = 'R15b' THEN Sentence END) AS S15b
					, MAX(CASE WHEN RuleId = 'R15b' THEN Threshold END) AS T15b
					, MAX(CASE WHEN RuleId = 'R15b' THEN Severity END) AS Sev15b
				FROM #pcs_rules) s
			WHERE b.Ord < 4
				AND (k.RowKind = 1 OR (b.Status = 'ok' AND (k.RowKind IN (2, 3) OR (k.RowKind = 4 AND b.Win = 'lastrun'))))
			ORDER BY b.Ord, k.RowKind;
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Batch response-time profile', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SELECT @errMessage  = ERROR_MESSAGE()
			RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
		RAISERROR (N'Error with Batch response-time profile',0,1) WITH NOWAIT;
	END CATCH
END
	/* One grammar pass over the summary sentences (orchestrator, 2026-10-22): a count of 1 reads singular. Sentence templates
	   say '{Runs} runs', '{Restarts} restarts' and so on; this fixes the whole class here once, rather than in each template. */
	UPDATE #output_sqldba_org_sp_triage
	SET Summary = REPLACE(REPLACE(REPLACE(REPLACE(Summary, N' 1 runs', N' 1 run'), N' 1 restarts', N' 1 restart'), N' 1 databases', N' 1 database'), N' 1 pairs', N' 1 pair')
	WHERE SectionID IN (120, 121, 122, 123);
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT;
		RAISERROR (N'Done looking at Performance counter summaries',0,1) WITH NOWAIT;
	END

	/*----------------------------------------
	--TCP listener states
	----------------------------------------*/
IF OBJECT_ID(N'sys.dm_tcp_listener_states') IS NOT NULL /*census: RawDiagnosticsFootprintTests.Shipped_sp_triage_guards_every_post_2008_object*/
BEGIN
BEGIN TRY
	SET @CustomErrorText = 'TCP listener states'
	
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		) 
		SELECT 106, @CustomErrorText,'------','------'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		--, [Summary]
		, [Details]
		)
		SELECT 106
		, @CustomErrorText
		, '{'
		+ '"listener_id":"'+ CONVERT(VARCHAR(10),ISNULL(listener_id,''))
		+ '","IP_ADDRESS":"'+ CASE ip_address 
				WHEN '::' THEN 'IPv6 Default All'
				WHEN '0.0.0.0' THEN 'IPv4 Default All'
				ELSE ISNULL(ip_address,'') END
		+ '","IP_TYPE":"'+ CASE is_ipv4 when 1 THEN 'IPv4' ELSE 'IPv6' END 
		+ '","port":"'+ CONVERT(VARCHAR(10),ISNULL(port,''))
		+ '","type_desc":"'+ ISNULL(type_desc,'')
		+ '","state_desc":"'+ ISNULL(state_desc,'')

		+'"}'
		FROM sys.dm_tcp_listener_states

		
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'TCP listener states', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with TCP listener states',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
END
ELSE
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (N'TCP listener states', 0, N'sys.dm_tcp_listener_states is not on this server. It needs a newer SQL Server version.');
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done looking at TCP listener states',0,1) WITH NOWAIT;
	END

/*
		SELECT * 
		FROM @xp_errorlog 

LogDate	ProcessInfo	Text
2025-06-03 17:44:46.900	Server	Command Line Startup Parameters:
	 -s "MSSQLSERVER"
2025-06-03 17:44:48.650	Server	Database Instant File Initialization: enabled. For security and performance considerations see the topic 'Database Instant File Initialization' in SQL Server Books Online. This is an informational message only. No user action is required.
2025-06-03 17:44:46.900	Server	System Manufacturer: 'Micro-Star International Co., Ltd.', System Model: 'Stealth 16 AI Studio A1VIG'.
2025-06-03 17:44:49.100	Server	SQL Server is attempting to register a Service Principal Name (SPN) for the SQL Server service. Kerberos authentication will not be possible until a SPN is registered for the SQL Server service. This is an informational message. No user action is required.
2025-06-03 17:44:49.100	Server	The SQL Server Network Interface library could not register the Service Principal Name (SPN) [ MSSQLSvc/MSI ] for the SQL Server service. Windows return code: 0xffffffff, state: 63. Failure to register a SPN might cause integrated authentication to use NTLM instead of Kerberos. This is an informational message. Further action is only required if Kerberos authentication is required by authentication policies and if the SPN has not been manually registered.
2025-06-03 17:44:49.100	Server	Server is listening on [ 'any' <ipv6> 1434] accept sockets 1.
2025-06-03 17:44:49.100	Server	Server is listening on [ 'any' <ipv4> 1434] accept sockets 1.
2025-06-03 17:44:49.090	spid53s	A self-generated certificate was successfully loaded for encryption.
*/

	/*----------------------------------------
	--Endpoints
	----------------------------------------*/
BEGIN TRY
	SET @CustomErrorText = 'Endpoints'
	
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		) 
		SELECT 107, @CustomErrorText,'------','------'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		--, [Summary]
		, [Details]
		)
		SELECT 107
		, @CustomErrorText
		, '{'
		+ '"name":"'+					ISNULL([name],'')
		+ '","endpoint_id":"'+			CONVERT(VARCHAR(10),ISNULL([endpoint_id],''))	COLLATE DATABASE_DEFAULT
		+ '","principal_id":"'+	    CONVERT(VARCHAR(10),ISNULL([principal_id],''))	COLLATE DATABASE_DEFAULT
		+ '","protocol":"'+		    CONVERT(VARCHAR(10),ISNULL([protocol],''))		COLLATE DATABASE_DEFAULT
		+ '","protocol_desc":"'+		ISNULL([protocol_desc],'')
		+ '","type":"'+				CONVERT(VARCHAR(10),ISNULL([type],''))			COLLATE DATABASE_DEFAULT
		+ '","type_desc":"'+			ISNULL([type_desc],'')
		+ '","state":"'+				CONVERT(VARCHAR(10),ISNULL([state],''))			COLLATE DATABASE_DEFAULT
		+ '","state_desc":"'+		    ISNULL([state_desc],'')
		+ '","is_admin_endpoint":"'+	CONVERT(VARCHAR(5),ISNULL([is_admin_endpoint],''))COLLATE DATABASE_DEFAULT
		+'"}'
		FROM [master].[sys].[endpoints]

		
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Endpoints', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with Endpoints',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done looking at Endpoints',0,1) WITH NOWAIT;
	END

	/*----------------------------------------
	--Connections ports and IPs
	----------------------------------------*/
BEGIN TRY
	SET @CustomErrorText = 'Connections ports and IPs'
	
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		) 
		SELECT 108, @CustomErrorText,'------','------'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		--, [Summary]
		, [Details]
		)
		SELECT DISTINCT 108
		, @CustomErrorText
		, '{'
		+ '"net_transport":"'+			ISNULL(CONVERT(VARCHAR(50),[net_transport]),'')
		+ '","protocol_type":"'+			ISNULL(CONVERT(VARCHAR(50),[protocol_type]),'')
		+ '","endpoint_id":"'+				ISNULL(CONVERT(VARCHAR(10),[endpoint_id]),'')
		+ '","encrypt_option":"'+			ISNULL(CONVERT(VARCHAR(10),[encrypt_option]),'')
		+ '","auth_scheme":"'+				ISNULL(CONVERT(VARCHAR(50),[auth_scheme]),'')
		+ '","client_net_address":"'+		ISNULL([client_net_address],'')
		+ '","local_net_address":"'+		ISNULL([local_net_address],'')
		+ '","local_tcp_port":"'+			ISNULL(CONVERT(VARCHAR(10),[local_tcp_port]),'')
		+'"}'
		FROM sys.dm_exec_connections AS dec

		
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Connections ports and IPs', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with Connections ports and IPs',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done looking at Connections ports and IPs',0,1) WITH NOWAIT;
	END

	/*----------------------------------------
	--Server registry
	----------------------------------------*/
IF OBJECT_ID(N'sys.dm_server_registry') IS NOT NULL /*census: RawDiagnosticsFootprintTests.Shipped_sp_triage_guards_every_post_2008_object*/
BEGIN
BEGIN TRY
	SET @CustomErrorText = 'Server registry'
	
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		) 
		SELECT 108, @CustomErrorText,'------','------'
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		)
		SELECT DISTINCT 108
		, @CustomErrorText
		, registry_key
		, '"'+ISNULL(CONVERT(NVARCHAR(250),value_name),'') +'":' +ISNULL(CONVERT(NVARCHAR(250),value_data) ,'') 
		FROM sys.dm_server_registry

	
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Server registry', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with Server registry',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
END
ELSE
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (N'Server registry', 0, N'sys.dm_server_registry is not on this server. It needs a newer SQL Server version.');
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done looking at Server registry',0,1) WITH NOWAIT;
	END


	/*----------------------------------------
	--Check certificates
	----------------------------------------*/
BEGIN TRY
	SET @CustomErrorText = 'Check certificates'
	
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		) 
	SELECT 109, @CustomErrorText,'------','------'
	INSERT #output_sqldba_org_sp_triage 
		(
			SectionID
			, Section
			, Summary
		)		
	SELECT
		109
		, 'Certificates - from configuration'
		,  [Text]
		FROM @xp_errorlog
		WHERE [Text] 
		LIKE '%certificate%';
		/*A self-generated certificate was successfully loaded for encryption.*/


		
		SET @dynamicSQL = 'SELECT 
		DISTINCT 
			109
			, @CustomErrorText
			,''"State":''
			+ CASE 
				WHEN GETDATE() > expiry_date THEN ''"!Expired!"''
				WHEN DATEADD(MONTH,3,GETDATE()) > expiry_date THEN ''"Expiring Soon"''
				ELSE ''"Ok"''
			END
			+
			'',"CertName":"'' + ISNULL(name,'''') +''"''
			+
			'',"CertSubject":"'' + ISNULL(subject,'''') +''"''
			+
			'',"CertIssuer":"'' + ISNULL(issuer_name,'''') +''"''

			, ''"DaysToExpire":"'' +CONVERT(VARCHAR(10),DATEDIFF(DAY,GETDATE(),expiry_date)) +''"''
			+ 
			'',"StartDate":"'' +CONVERT(VARCHAR,start_date,120) +''"''
			+ 
			'',"ExpiryDate":"'' +CONVERT(VARCHAR,expiry_date,120) +''"''

			+ 
			'',"Type":"'' + CASE
				WHEN name LIKE ''##%'' THEN ''Internal''
				ELSE ''External''
			END +''"''
			+ 
			'',"pvt_key_encryption_type_desc":"'' + ISNULL(pvt_key_encryption_type_desc,'''') + ''"''
			--Does not work in SQL 2014, 12
			' + CASE WHEN CONVERT( TINYINT ,@SQLVersion) <= 12 -- 2012- 
			THEN '+ '',"KeyLength":"'' + CONVERT(VARCHAR(10),ISNULL(key_length,'''')) + ''"'' '
			ELSE ''
			END
			+'
			

		FROM [master].sys.certificates
		WHERE 1=1
		AND name NOT LIKE ''##%'' '
		
		INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
		)
		EXEC sp_executesql @dynamicSQL, N'@CustomErrorText nvarchar(500)', @CustomErrorText = @CustomErrorText

		SET @CustomErrorText = 'Done looking at '+ @CustomErrorText
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Check certificates', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	SET @CustomErrorText = 'Error with '+ @CustomErrorText
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
	END


	/*----------------------------------------
	--Availability Group synchronization lag
	----------------------------------------*/
IF OBJECT_ID(N'sys.dm_hadr_database_replica_states') IS NOT NULL
	AND OBJECT_ID(N'sys.availability_replicas') IS NOT NULL
	AND OBJECT_ID(N'sys.dm_hadr_availability_replica_states') IS NOT NULL /*census: RawDiagnosticsFootprintTests.Shipped_sp_triage_guards_every_post_2008_object*/
BEGIN
BEGIN TRY
	SET @CustomErrorText = 'Availability Group synchronization lag'
	
	INSERT #output_sqldba_org_sp_triage 
		(
			[SectionID]
			, [Section]
			, [Summary]
			, [Details]
		) 
		SELECT 
		110
		, @CustomErrorText
		,'------'
		,'------'

		INSERT #output_sqldba_org_sp_triage 
		(
			[SectionID]
			, [Section]
			, [Summary]
			, [Details]
		)
		SELECT 
		DISTINCT 
			110
			, @CustomErrorText
			, '"PRIMARY":"' +ISNULL([primary].replica_server_name,'')  +'"'
				+',"SECONDARY":"' +ISNULL([secondary].replica_server_name,'') +'"'
			, '"DatabaseName":"' +ISNULL([primary].[DBName],'') +'"'
				+',"SyncLagSeconds":"'+  ISNULL(CONVERT(VARCHAR(10),DATEDIFF(ss,[secondary].last_commit_time,[primary].last_commit_time)),'')+'"'
		FROM 
		(
			SELECT	replica_server_name
					, DBName
					, last_commit_time
			FROM
			(
			SELECT AR.replica_server_name,
				   HARS.role_desc, 
				   Db_name(DRS.database_id) [DBName], 
				   DRS.last_commit_time
			FROM   sys.dm_hadr_database_replica_states DRS 
			INNER JOIN sys.availability_replicas AR ON DRS.replica_id = AR.replica_id 
			INNER JOIN sys.dm_hadr_availability_replica_states HARS ON AR.group_id = HARS.group_id 
				AND AR.replica_id = HARS.replica_id 
			)AG_Stats
			WHERE	role_desc = 'PRIMARY'
		) [primary]
		LEFT JOIN 
		(
			SELECT	replica_server_name
					, DBName
					, last_commit_time
			FROM	
			(
			SELECT AR.replica_server_name,
				   HARS.role_desc, 
				   Db_name(DRS.database_id) [DBName], 
				   DRS.last_commit_time
			FROM   sys.dm_hadr_database_replica_states DRS 
			INNER JOIN sys.availability_replicas AR ON DRS.replica_id = AR.replica_id 
			INNER JOIN sys.dm_hadr_availability_replica_states HARS ON AR.group_id = HARS.group_id 
				AND AR.replica_id = HARS.replica_id 
			)AG_Stats
			WHERE	role_desc = 'SECONDARY'
		) [secondary] ON [secondary].[DBName] = [primary].[DBName]

	
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Availability Group synchronization lag', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	SET @CustomErrorText = 'Error with '+ @CustomErrorText
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
END
ELSE
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (N'Availability Group synchronization lag', 0, N'sys.dm_hadr_database_replica_states, sys.availability_replicas and sys.dm_hadr_availability_replica_states are not on this server. They need a newer SQL Server version.');
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
	END


	/*----------------------------------------
	-- Core checks (corpus parity, basic set)
	----------------------------------------*/
	/* Eleven checks that SQLTriage's own check library also makes, written here so a client who runs this script
	   without the app still gets them. Invariant: each step means exactly what its library check means (same
	   predicate, exclusions, threshold, verdict and count) and is NOT a copy of its text. Each step writes ONE
	   summary row whose Summary starts with PASS: FAIL: WARN: INFO: or SKIP: and whose Details start with count=,
	   then optional object rows (Summary = the object, Details = the measure). Section is a literal naming the
	   library id and the public sp_Blitz CheckID. SectionIDs 3001-3011 are disjoint from every sp_Blitz CheckID.
	   No step here creates a persistent object or changes server state. Every view or column newer than the oldest
	   version this script supports is read through dynamic SQL after an existence test. A coverage gap is a WARN
	   summary row with count=NULL, never silence; a CATCH that implements one still records its skip row first.
	   Guarded by SpTriageCorpusParityCensusTests (the id list, the CheckIDs, the SectionIDs) and by
	   SpTriageSkipRowCensusTests and SpTriageDbNameQuotingCensusTests. */
	SET @CustomErrorText = 'Core checks (corpus parity, basic set)';
	DECLARE @cc_verdict [NVARCHAR] (4), @cc_count INT, @cc_msg [NVARCHAR] (3000), @cc_sev [NVARCHAR] (2);
	DECLARE @cc_seeall INT, @cc_band INT, @cc_errs INT, @cc_total INT, @cc_listok BIT, @cc_hadr INT, @cc_userdbs INT;
	DECLARE @cc_sql [NVARCHAR] (4000), @cc_good DATETIME, @cc_c INT, @cc_note [NVARCHAR] (400);
	DECLARE @cc_certs INT, @cc_keys INT, @cc_canviewdef INT, @cc_gap BIT, @cc_srvrows INT, @cc_llrows INT, @cc_dbdef INT;
	DECLARE @cc_list TABLE (dbname SYSNAME NOT NULL);
	DECLARE @cc_obj TABLE (rn INT NOT NULL, objname [NVARCHAR] (400) NULL, measure [NVARCHAR] (1000) NULL);
	DECLARE @cc_agids TABLE (database_id INT NOT NULL);

	/* 3001 Database not ONLINE */
BEGIN TRY
	DELETE FROM @cc_obj;
	SET @cc_seeall = CONVERT(INT, HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DATABASE'));
	INSERT INTO @cc_obj (rn, objname, measure)
		SELECT ROW_NUMBER() OVER (ORDER BY sdb.name), QUOTENAME(sdb.name), N'state=' + sdb.state_desc
		FROM sys.databases AS sdb
		WHERE sdb.state > 1;
	SELECT @cc_count = COUNT(*) FROM @cc_obj;
	SET @cc_verdict = CASE WHEN @cc_count > 0 THEN N'FAIL' WHEN @cc_seeall = 0 THEN N'WARN' ELSE N'PASS' END;
	IF @cc_verdict = N'WARN' SET @cc_count = NULL;
	SET @cc_msg = CASE @cc_verdict
		WHEN N'FAIL' THEN CONVERT([NVARCHAR] (12), @cc_count) + N' database(s) are in a state other than ONLINE or RESTORING, such as OFFLINE, SUSPECT, RECOVERY_PENDING or EMERGENCY.'
		WHEN N'WARN' THEN N'Coverage unknown. This login lacks VIEW ANY DATABASE, so it cannot see every database. This is not a clean result.'
		ELSE N'No database is in a state other than ONLINE or RESTORING.' END;
	SET @cc_sev = CASE @cc_verdict WHEN N'FAIL' THEN @Result_Bad WHEN N'WARN' THEN @Result_Warning ELSE @Result_Good END;
	INSERT #output_sqldba_org_sp_triage (SectionID, Section, Summary, Severity, Details)
		SELECT 3001, hdr.Section, x.Summary, @cc_sev, x.Details
		FROM (SELECT N'Core check: Database not online [SQLT-BLITZ-UNUSUAL-DATABASE-STATE; public equivalent: sp_Blitz CheckID 102]' AS Section) AS hdr
		CROSS JOIN (
			SELECT 0 AS rn, @cc_verdict + N': ' + @cc_msg AS Summary
				, N'count=' + ISNULL(CONVERT([NVARCHAR] (12), @cc_count), N'NULL') + N'. Counts databases whose state is not ONLINE (0) or RESTORING (1).' AS Details
			UNION ALL
			SELECT o.rn, o.objname, o.measure FROM @cc_obj AS o
		) AS x
		ORDER BY x.rn;
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check: Database not online', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage = ERROR_MESSAGE();
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH

	/* 3002 Last good integrity check. The database list is built here from sys.databases, never from @Databases. */
BEGIN TRY
	DELETE FROM @cc_obj;
	DELETE FROM @cc_list;
	SELECT @cc_count = 0, @cc_band = 0, @cc_errs = 0, @cc_total = 0, @cc_listok = 1, @cc_note = N'';
	SET @cc_hadr = CONVERT(INT, ISNULL(SERVERPROPERTY('IsHadrEnabled'), 0));
	SET @cc_seeall = CONVERT(INT, HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DATABASE'));
	BEGIN TRY
		INSERT INTO @cc_list (dbname)
			SELECT sdb.name
			FROM sys.databases AS sdb
			WHERE sdb.database_id > 4 AND sdb.state = 0 AND sdb.user_access = 0 AND sdb.is_read_only = 0
				AND sdb.database_id IN (SELECT r.database_id FROM #sp_triage_readable_db AS r)
				AND sdb.source_database_id IS NULL AND sdb.name <> N'tempdb'
				AND sdb.create_date < DATEADD(DAY, -14, GETDATE());
		/* AG secondaries that refuse connections cannot be read; the HADR views are touched only when HADR is on. */
		IF @cc_hadr = 1
		BEGIN
			SET @cc_sql = N'SELECT rs.database_id FROM sys.dm_hadr_database_replica_states AS rs
				INNER JOIN sys.availability_replicas AS rep ON rep.replica_id = rs.replica_id
				WHERE ISNULL(rs.is_local, 0) = 1 AND ISNULL(rs.is_primary_replica, 1) = 0
				AND ISNULL(rep.secondary_role_allow_connections, 1) = 0;';
			INSERT INTO @cc_agids (database_id) EXEC sp_executesql @cc_sql;
			DELETE l FROM @cc_list AS l WHERE DB_ID(l.dbname) IN (SELECT a.database_id FROM @cc_agids AS a);
		END
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check CHECKDB: database list', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SET @cc_listok = 0;
	END CATCH
	IF @cc_listok = 0
		SELECT @cc_verdict = N'WARN', @cc_count = NULL
			, @cc_msg = N'Coverage unknown. The list of databases to check could not be built, so no database was checked. This is not a clean result.';
	ELSE IF NOT EXISTS (SELECT 1 FROM @cc_list)
	BEGIN
		IF @cc_seeall = 0
			SELECT @cc_verdict = N'WARN', @cc_count = NULL
				, @cc_msg = N'Coverage unknown. This login lacks VIEW ANY DATABASE, so no database could be listed or checked. This is not a clean result.';
		ELSE
		BEGIN
			SELECT @cc_userdbs = COUNT(*) FROM sys.databases WHERE database_id > 4 AND name <> N'tempdb';
			SELECT @cc_verdict = N'SKIP', @cc_count = NULL
				, @cc_msg = CASE WHEN @cc_userdbs = 0 THEN N'This instance has no user database, so there is nothing to check.'
					ELSE CONVERT([NVARCHAR] (12), @cc_userdbs) + N' user database(s) exist, but every one is left out: offline or restoring, read-only, single-user, a snapshot, created in the last 14 days, an AG secondary that refuses connections, or this login cannot open them. Nothing was checked, so this says nothing about CHECKDB.' END;
		END
	END
	ELSE
	BEGIN
		IF OBJECT_ID('tempdb..#cc_dbinfo') IS NOT NULL
			DROP TABLE #cc_dbinfo;
		CREATE TABLE #cc_dbinfo (ParentObject [NVARCHAR] (255), [Object] [NVARCHAR] (255), Field [NVARCHAR] (255), [Value] [NVARCHAR] (4000));
		DECLARE cc_checkdb_cur CURSOR LOCAL FAST_FORWARD FOR SELECT dbname FROM @cc_list ORDER BY dbname;
		OPEN cc_checkdb_cur;
		FETCH NEXT FROM cc_checkdb_cur INTO @dbname;
		WHILE @@FETCH_STATUS = 0
		BEGIN
			SET @cc_total = @cc_total + 1;
			SET @cc_good = NULL;
			DELETE FROM #cc_dbinfo;
			SET @cc_sql = N'USE ' + QUOTENAME(@dbname) + N'; DBCC DBINFO WITH TABLERESULTS, NO_INFOMSGS;';
			BEGIN TRY
				INSERT INTO #cc_dbinfo (ParentObject, [Object], Field, [Value]) EXEC sp_executesql @cc_sql;
				/* style 121 text; the pattern test stands in for TRY_CONVERT, which 2008 lacks */
				SELECT @cc_good = CASE WHEN i.[Value] LIKE N'[12][0-9][0-9][0-9]-[01][0-9]-[0-3][0-9] [0-2][0-9]:[0-5][0-9]:[0-5][0-9]%'
						THEN CONVERT(DATETIME, LEFT(i.[Value], 23), 121) END
					FROM #cc_dbinfo AS i
					WHERE i.Field = N'dbi_dbccLastKnownGood';
				IF @cc_good IS NULL OR @cc_good < DATEADD(DAY, -14, GETDATE())
				BEGIN
					SET @cc_count = @cc_count + 1;
					INSERT INTO @cc_obj (rn, objname, measure)
						VALUES (@cc_total, QUOTENAME(@dbname), N'last good CHECKDB=' + ISNULL(CONVERT([NVARCHAR] (19), @cc_good, 120), N'none on record'));
				END
				ELSE IF @cc_good < DATEADD(DAY, -7, GETDATE())
				BEGIN
					SET @cc_band = @cc_band + 1;
					INSERT INTO @cc_obj (rn, objname, measure)
						VALUES (@cc_total, QUOTENAME(@dbname), N'last good CHECKDB=' + CONVERT([NVARCHAR] (19), @cc_good, 120) + N' (7 to 14 days ago)');
				END
			END TRY
			BEGIN CATCH
				INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check CHECKDB: ' + @dbname, N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
				SET @cc_errs = @cc_errs + 1;
				INSERT INTO @cc_obj (rn, objname, measure) VALUES (@cc_total, QUOTENAME(@dbname), N'could not be read');
			END CATCH
			FETCH NEXT FROM cc_checkdb_cur INTO @dbname;
		END
		CLOSE cc_checkdb_cur;
		DEALLOCATE cc_checkdb_cur;
		DROP TABLE #cc_dbinfo;
		/* M2-a: the suffix only when some, not all, databases could not be read; all unreadable keeps 'None of the <M> ...' alone. */
		IF @cc_errs > 0 AND @cc_errs < @cc_total
			SET @cc_note = N' ' + CONVERT([NVARCHAR] (12), @cc_errs) + N' of ' + CONVERT([NVARCHAR] (12), @cc_total) + N' databases could not be read; the result covers the other ' + CONVERT([NVARCHAR] (12), @cc_total - @cc_errs) + N'.';
		SET @cc_verdict = CASE WHEN @cc_count > 0 THEN N'FAIL' WHEN @cc_band > 0 THEN N'WARN' ELSE N'PASS' END;
		IF @cc_verdict = N'PASS' AND (@cc_seeall = 0 OR @cc_errs > 0)
			SELECT @cc_verdict = N'WARN', @cc_count = NULL
				, @cc_msg = CASE WHEN @cc_total > 0 AND @cc_errs >= @cc_total THEN N'Coverage unknown. None of the ' + CONVERT([NVARCHAR] (12), @cc_total) + N' databases to check could be read. This is not a clean result.'
					WHEN @cc_seeall = 0 THEN N'Coverage unknown. This login lacks VIEW ANY DATABASE, so only the databases it can see were checked. This is not a clean result.'
					ELSE N'Coverage unknown. Some databases could not be read. This is not a clean result.' END + @cc_note;
		ELSE
			SET @cc_msg = CASE @cc_verdict
				WHEN N'FAIL' THEN CONVERT([NVARCHAR] (12), @cc_count) + N' database(s) have no successful CHECKDB in the last 14 days, or none on record.'
					+ CASE WHEN @cc_band > 0 THEN N' A further ' + CONVERT([NVARCHAR] (12), @cc_band) + N' last had one 7 to 14 days ago.' ELSE N'' END
				WHEN N'WARN' THEN CONVERT([NVARCHAR] (12), @cc_band) + N' database(s) last had a successful CHECKDB 7 to 14 days ago. None is past 14 days yet.'
				ELSE N'Every checked database had a successful CHECKDB in the last 7 days.' END
				+ CASE WHEN @cc_seeall = 0 THEN N' Counted only in the databases this login can see.' ELSE N'' END + @cc_note;
	END
	SET @cc_sev = CASE @cc_verdict WHEN N'FAIL' THEN @Result_ReallyBad WHEN N'WARN' THEN @Result_Warning WHEN N'PASS' THEN @Result_Good ELSE @Result_NA END;
	INSERT #output_sqldba_org_sp_triage (SectionID, Section, Summary, Severity, Details)
		SELECT 3002, hdr.Section, x.Summary, @cc_sev, x.Details
		FROM (SELECT N'Core check: Last good integrity check (CHECKDB) [SQLT-BLITZ-DBCC-CHECKDB-NOT-PERFORMED-RECENTLY; public equivalent: sp_Blitz CheckID 68]' AS Section) AS hdr
		CROSS JOIN (
			SELECT 0 AS rn, @cc_verdict + N': ' + @cc_msg AS Summary
				, N'count=' + ISNULL(CONVERT([NVARCHAR] (12), @cc_count), N'NULL') + N'. Checks user databases that are online, read-write, multi-user, not snapshots and older than 14 days; the count is the databases past 14 days. sp_Blitz also checks system databases and new databases and has no 7-day warning.' AS Details
			UNION ALL
			SELECT o.rn, o.objname, o.measure FROM @cc_obj AS o
		) AS x
		ORDER BY x.rn;
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check: Last good integrity check (CHECKDB)', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage = ERROR_MESSAGE();
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH

	/* 3003 Page verify */
BEGIN TRY
	DELETE FROM @cc_obj;
	SET @cc_seeall = CONVERT(INT, HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DATABASE'));
	INSERT INTO @cc_obj (rn, objname, measure)
		SELECT ROW_NUMBER() OVER (ORDER BY sdb.name), QUOTENAME(sdb.name), N'page_verify=' + sdb.page_verify_option_desc
		FROM sys.databases AS sdb
		WHERE sdb.name <> N'tempdb' AND sdb.state NOT IN (1, 6) AND sdb.page_verify_option <> 2;
	SELECT @cc_count = COUNT(*) FROM @cc_obj;
	SET @cc_verdict = CASE WHEN @cc_count > 0 THEN N'FAIL' WHEN @cc_seeall = 0 THEN N'WARN' ELSE N'PASS' END;
	IF @cc_verdict = N'WARN' SET @cc_count = NULL;
	SET @cc_msg = CASE @cc_verdict
		WHEN N'FAIL' THEN CONVERT([NVARCHAR] (12), @cc_count) + N' database(s) do not use PAGE_VERIFY CHECKSUM, so a damaged page can go unnoticed.'
		WHEN N'WARN' THEN N'Coverage unknown. This login lacks VIEW ANY DATABASE, so it cannot see every database. This is not a clean result.'
		ELSE N'Every database checked uses PAGE_VERIFY CHECKSUM. tempdb, offline and restoring databases are not checked.' END;
	SET @cc_sev = CASE @cc_verdict WHEN N'FAIL' THEN @Result_Bad WHEN N'WARN' THEN @Result_Warning ELSE @Result_Good END;
	INSERT #output_sqldba_org_sp_triage (SectionID, Section, Summary, Severity, Details)
		SELECT 3003, hdr.Section, x.Summary, @cc_sev, x.Details
		FROM (SELECT N'Core check: Page verify not CHECKSUM [SQLT-VA-PAGE-VERIFY-CHECKSUM; public equivalent: sp_Blitz CheckID 14]' AS Section) AS hdr
		CROSS JOIN (
			SELECT 0 AS rn, @cc_verdict + N': ' + @cc_msg AS Summary
				, N'count=' + ISNULL(CONVERT([NVARCHAR] (12), @cc_count), N'NULL') + N'. Checks every database except tempdb, system databases included; RESTORING and OFFLINE databases are left out.' AS Details
			UNION ALL
			SELECT o.rn, o.objname, o.measure FROM @cc_obj AS o
		) AS x
		ORDER BY x.rn;
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check: Page verify not CHECKSUM', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage = ERROR_MESSAGE();
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH

	/* 3004 AUTO_CLOSE. The count covers every offender; the object rows show the first five by name. */
BEGIN TRY
	DELETE FROM @cc_obj;
	SET @cc_seeall = CONVERT(INT, HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DATABASE'));
	INSERT INTO @cc_obj (rn, objname, measure)
		SELECT ROW_NUMBER() OVER (ORDER BY sdb.name), QUOTENAME(sdb.name), N'AUTO_CLOSE=ON'
		FROM sys.databases AS sdb
		WHERE sdb.database_id > 4 AND sdb.state_desc = N'ONLINE' AND sdb.is_auto_close_on = 1;
	SELECT @cc_count = COUNT(*) FROM @cc_obj;
	SET @cc_verdict = CASE WHEN @cc_count > 0 THEN N'FAIL' WHEN @cc_seeall = 0 THEN N'WARN' ELSE N'PASS' END;
	IF @cc_verdict = N'WARN' SET @cc_count = NULL;
	SET @cc_msg = CASE @cc_verdict
		WHEN N'FAIL' THEN CONVERT([NVARCHAR] (12), @cc_count) + N' user database(s) have AUTO_CLOSE on: each one closes when idle and is slow to reopen.'
		WHEN N'WARN' THEN N'Coverage unknown. This login lacks VIEW ANY DATABASE, so it cannot see every database. This is not a clean result.'
		ELSE N'No user database has AUTO_CLOSE on.' END;
	SET @cc_sev = CASE @cc_verdict WHEN N'FAIL' THEN @Result_Bad WHEN N'WARN' THEN @Result_Warning ELSE @Result_Good END;
	INSERT #output_sqldba_org_sp_triage (SectionID, Section, Summary, Severity, Details)
		SELECT 3004, hdr.Section, x.Summary, @cc_sev, x.Details
		FROM (SELECT N'Core check: AUTO_CLOSE on [SQLT-BLITZ-AUTO-CLOSE; public equivalent: sp_Blitz CheckID 12]' AS Section) AS hdr
		CROSS JOIN (
			SELECT 0 AS rn, @cc_verdict + N': ' + @cc_msg AS Summary
				, N'count=' + ISNULL(CONVERT([NVARCHAR] (12), @cc_count), N'NULL') + N'. Checks ONLINE user databases and lists up to five by name. sp_Blitz checks every database in any state.' AS Details
			UNION ALL
			SELECT o.rn, o.objname, o.measure FROM @cc_obj AS o WHERE o.rn <= 5
		) AS x
		ORDER BY x.rn;
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check: AUTO_CLOSE on', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage = ERROR_MESSAGE();
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH

	/* 3005 AUTO_SHRINK. The count covers every offender; the object rows show the first five by name. */
BEGIN TRY
	DELETE FROM @cc_obj;
	SET @cc_seeall = CONVERT(INT, HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DATABASE'));
	INSERT INTO @cc_obj (rn, objname, measure)
		SELECT ROW_NUMBER() OVER (ORDER BY sdb.name), QUOTENAME(sdb.name), N'AUTO_SHRINK=ON'
		FROM sys.databases AS sdb
		WHERE sdb.database_id > 4 AND sdb.state_desc = N'ONLINE' AND sdb.is_auto_shrink_on = 1;
	SELECT @cc_count = COUNT(*) FROM @cc_obj;
	SET @cc_verdict = CASE WHEN @cc_count > 0 THEN N'FAIL' WHEN @cc_seeall = 0 THEN N'WARN' ELSE N'PASS' END;
	IF @cc_verdict = N'WARN' SET @cc_count = NULL;
	SET @cc_msg = CASE @cc_verdict
		WHEN N'FAIL' THEN CONVERT([NVARCHAR] (12), @cc_count) + N' user database(s) have AUTO_SHRINK on: each one shrinks its files in the background, which fragments indexes, and then grows them again.'
		WHEN N'WARN' THEN N'Coverage unknown. This login lacks VIEW ANY DATABASE, so it cannot see every database. This is not a clean result.'
		ELSE N'No user database has AUTO_SHRINK on.' END;
	SET @cc_sev = CASE @cc_verdict WHEN N'FAIL' THEN @Result_Bad WHEN N'WARN' THEN @Result_Warning ELSE @Result_Good END;
	INSERT #output_sqldba_org_sp_triage (SectionID, Section, Summary, Severity, Details)
		SELECT 3005, hdr.Section, x.Summary, @cc_sev, x.Details
		FROM (SELECT N'Core check: AUTO_SHRINK on [SQLT-BLITZ-AUTO-SHRINK; public equivalent: sp_Blitz CheckID 13]' AS Section) AS hdr
		CROSS JOIN (
			SELECT 0 AS rn, @cc_verdict + N': ' + @cc_msg AS Summary
				, N'count=' + ISNULL(CONVERT([NVARCHAR] (12), @cc_count), N'NULL') + N'. Checks ONLINE user databases and lists up to five by name. sp_Blitz checks every database that is not OFFLINE.' AS Details
			UNION ALL
			SELECT o.rn, o.objname, o.measure FROM @cc_obj AS o WHERE o.rn <= 5
		) AS x
		ORDER BY x.rn;
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check: AUTO_SHRINK on', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage = ERROR_MESSAGE();
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH

	/* 3006 Query Store off and not requested. 2016 and later; the database list is built here, never from @Databases. */
BEGIN TRY
	DELETE FROM @cc_obj;
	DELETE FROM @cc_list;
	SELECT @cc_count = 0, @cc_errs = 0, @cc_total = 0, @cc_listok = 1, @cc_note = N'';
	SET @cc_hadr = CONVERT(INT, ISNULL(SERVERPROPERTY('IsHadrEnabled'), 0));
	SET @cc_seeall = CONVERT(INT, HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DATABASE'));
	IF @SQLVersion < 13
		SELECT @cc_verdict = N'SKIP', @cc_count = NULL, @cc_msg = N'Query Store needs SQL Server 2016 or later.';
	ELSE
	BEGIN
		BEGIN TRY
			SET @cc_sql = N'SELECT sdb.name FROM sys.databases AS sdb
				WHERE sdb.database_id > 4 AND sdb.state = 0 AND sdb.user_access = 0 AND sdb.is_read_only = 0
				AND sdb.database_id IN (SELECT r.database_id FROM #sp_triage_readable_db AS r)
				AND (@h = 0 OR NOT EXISTS (SELECT 1 FROM sys.dm_hadr_database_replica_states AS rs
					INNER JOIN sys.availability_replicas AS rep ON rep.replica_id = rs.replica_id
					WHERE rs.database_id = sdb.database_id AND ISNULL(rs.is_local, 0) = 1
					AND ISNULL(rs.is_primary_replica, 1) = 0 AND ISNULL(rep.secondary_role_allow_connections, 1) = 0));';
			INSERT INTO @cc_list (dbname) EXEC sp_executesql @cc_sql, N'@h INT', @h = @cc_hadr;
		END TRY
		BEGIN CATCH
			INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check Query Store: database list', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
			SET @cc_listok = 0;
		END CATCH
		IF @cc_listok = 0
			SELECT @cc_verdict = N'WARN', @cc_count = NULL
				, @cc_msg = N'Coverage unknown. The list of databases to check could not be built, so no database was checked. This is not a clean result.';
		ELSE IF NOT EXISTS (SELECT 1 FROM @cc_list)
		BEGIN
			IF @cc_seeall = 0
				SELECT @cc_verdict = N'WARN', @cc_count = NULL
					, @cc_msg = N'Coverage unknown. This login lacks VIEW ANY DATABASE, so no database could be listed or checked. This is not a clean result.';
			ELSE
				SELECT @cc_verdict = N'SKIP', @cc_count = NULL
					, @cc_msg = N'No user database was checked. Only online, read-write, multi-user databases this login can read are checked; an AG secondary that refuses connections is left out.';
		END
		ELSE
		BEGIN
			DECLARE cc_qs_cur CURSOR LOCAL FAST_FORWARD FOR SELECT dbname FROM @cc_list ORDER BY dbname;
			OPEN cc_qs_cur;
			FETCH NEXT FROM cc_qs_cur INTO @dbname;
			WHILE @@FETCH_STATUS = 0
			BEGIN
				SET @cc_total = @cc_total + 1;
				SET @cc_c = 0;
				/* read inside the database (USE), not by a three-part name: an AUTO_CLOSE database read from master dropped out of the count */
				SET @cc_sql = N'USE ' + QUOTENAME(@dbname) + N'; SELECT @n = COUNT(*) FROM sys.database_query_store_options AS qso WHERE qso.desired_state = 0 AND qso.actual_state = 0;';
				BEGIN TRY
					EXEC sp_executesql @cc_sql, N'@n INT OUTPUT', @n = @cc_c OUTPUT;
					IF ISNULL(@cc_c, 0) > 0
					BEGIN
						SET @cc_count = @cc_count + 1;
						INSERT INTO @cc_obj (rn, objname, measure) VALUES (@cc_total, QUOTENAME(@dbname), N'Query Store=OFF, not requested');
					END
				END TRY
				BEGIN CATCH
					INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check Query Store: ' + @dbname, N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
					SET @cc_errs = @cc_errs + 1;
					INSERT INTO @cc_obj (rn, objname, measure) VALUES (@cc_total, QUOTENAME(@dbname), N'could not be read');
				END CATCH
				FETCH NEXT FROM cc_qs_cur INTO @dbname;
			END
			CLOSE cc_qs_cur;
			DEALLOCATE cc_qs_cur;
			/* M2-a: the suffix only when some, not all, databases could not be read; all unreadable keeps 'None of the <M> ...' alone. */
			IF @cc_errs > 0 AND @cc_errs < @cc_total
				SET @cc_note = N' ' + CONVERT([NVARCHAR] (12), @cc_errs) + N' of ' + CONVERT([NVARCHAR] (12), @cc_total) + N' databases could not be read; the result covers the other ' + CONVERT([NVARCHAR] (12), @cc_total - @cc_errs) + N'.';
			SET @cc_verdict = CASE WHEN @cc_count > 0 THEN N'FAIL' ELSE N'PASS' END;
			IF @cc_verdict = N'PASS' AND (@cc_seeall = 0 OR @cc_errs > 0)
				SELECT @cc_verdict = N'WARN', @cc_count = NULL
					, @cc_msg = CASE WHEN @cc_total > 0 AND @cc_errs >= @cc_total THEN N'Coverage unknown. None of the ' + CONVERT([NVARCHAR] (12), @cc_total) + N' databases to check could be read. This is not a clean result.'
						WHEN @cc_seeall = 0 THEN N'Coverage unknown. This login lacks VIEW ANY DATABASE, so only the databases it can see were checked. This is not a clean result.'
						ELSE N'Coverage unknown. Some databases could not be read. This is not a clean result.' END + @cc_note;
			ELSE
				SET @cc_msg = CASE @cc_verdict
					WHEN N'FAIL' THEN CONVERT([NVARCHAR] (12), @cc_count) + N' user database(s) have Query Store off, and nobody has asked for it to be on.'
					ELSE N'Every checked user database has Query Store on, or has asked for it.' END
					+ CASE WHEN @cc_seeall = 0 THEN N' Counted only in the databases this login can see.' ELSE N'' END + @cc_note;
		END
	END
	SET @cc_sev = CASE @cc_verdict WHEN N'FAIL' THEN @Result_Bad WHEN N'WARN' THEN @Result_Warning WHEN N'PASS' THEN @Result_Good ELSE @Result_NA END;
	INSERT #output_sqldba_org_sp_triage (SectionID, Section, Summary, Severity, Details)
		SELECT 3006, hdr.Section, x.Summary, @cc_sev, x.Details
		FROM (SELECT N'Core check: Query Store off [SQLT-BLITZ-QUERY-STORE-DISABLED; public equivalent: sp_Blitz CheckID 163]' AS Section) AS hdr
		CROSS JOIN (
			SELECT 0 AS rn, @cc_verdict + N': ' + @cc_msg AS Summary
				, N'count=' + ISNULL(CONVERT([NVARCHAR] (12), @cc_count), N'NULL') + N'. Checks online, read-write, multi-user user databases. sp_Blitz counts a database where Query Store was not asked for even if it is running, and skips some databases by name.' AS Details
			UNION ALL
			SELECT o.rn, o.objname, o.measure FROM @cc_obj AS o
		) AS x
		ORDER BY x.rn;
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check: Query Store off', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage = ERROR_MESSAGE();
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH

	/* 3007 Global trace flags. Fills #output_sqldba_org_sp_triage_TraceStatus, which nothing filled before. */
BEGIN TRY
	DELETE FROM @cc_obj;
	DELETE FROM #output_sqldba_org_sp_triage_TraceStatus;
	SET @cc_listok = 1;
	BEGIN TRY
		INSERT INTO #output_sqldba_org_sp_triage_TraceStatus (TraceFlag, status, Global, Session)
			EXEC sp_executesql N'DBCC TRACESTATUS(-1) WITH NO_INFOMSGS';
	END TRY
	BEGIN CATCH
		INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check trace flags: TRACESTATUS', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
		SET @cc_listok = 0;
	END CATCH
	IF @cc_listok = 0
		SELECT @cc_verdict = N'WARN', @cc_count = NULL
			, @cc_msg = N'Coverage unknown. The list of global trace flags could not be read. This is not a clean result.';
	ELSE
	BEGIN
		INSERT INTO @cc_obj (rn, objname, measure)
			SELECT ROW_NUMBER() OVER (ORDER BY CONVERT(INT, ts.TraceFlag)), N'Trace flag ' + ts.TraceFlag, N'global=1'
			FROM #output_sqldba_org_sp_triage_TraceStatus AS ts
			WHERE ts.Global = 1;
		SELECT @cc_count = COUNT(*) FROM @cc_obj;
		SET @cc_verdict = CASE WHEN @cc_count > 0 THEN N'INFO' ELSE N'PASS' END;
		SET @cc_msg = CASE WHEN @cc_count > 0
			THEN CONVERT([NVARCHAR] (12), @cc_count) + N' global trace flag(s) are on. Make sure each one is meant to be on and is written down.'
			ELSE N'No global trace flag is on.' END;
	END
	SET @cc_sev = CASE @cc_verdict WHEN N'WARN' THEN @Result_Warning WHEN N'PASS' THEN @Result_Good ELSE @Result_NA END;
	INSERT #output_sqldba_org_sp_triage (SectionID, Section, Summary, Severity, Details)
		SELECT 3007, hdr.Section, x.Summary, @cc_sev, x.Details
		FROM (SELECT N'Core check: Global trace flags [SQLT-BLITZ-GLOBAL-TRACE-FLAGS-ENABLED; public equivalent: sp_Blitz CheckID 74]' AS Section) AS hdr
		CROSS JOIN (
			SELECT 0 AS rn, @cc_verdict + N': ' + @cc_msg AS Summary
				, N'count=' + ISNULL(CONVERT([NVARCHAR] (12), @cc_count), N'NULL') + N'. Reads DBCC TRACESTATUS(-1) and counts the flags that are on globally.' AS Details
			UNION ALL
			SELECT o.rn, o.objname, o.measure FROM @cc_obj AS o
		) AS x
		ORDER BY x.rn;
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check: Global trace flags', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage = ERROR_MESSAGE();
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH

	/* 3008 TDE certificate key backup. sys.certificates hides rows from a login without VIEW ANY DEFINITION, and the
	   encryption-key view needs VIEW SERVER STATE: either gap is a coverage WARN, not a clean result. */
BEGIN TRY
	DELETE FROM @cc_obj;
	SELECT @cc_count = NULL, @cc_keys = NULL, @cc_gap = 0;
	IF NOT EXISTS (SELECT 1 FROM sys.all_columns AS ac WHERE ac.[object_id] = OBJECT_ID(N'sys.certificates') AND ac.name = N'pvt_key_last_backup_date')
		SELECT @cc_verdict = N'SKIP', @cc_msg = N'This SQL Server version does not record when a certificate''s private key was backed up.';
	ELSE
	BEGIN
		SELECT @cc_certs = COUNT(*) FROM master.sys.certificates;
		SET @cc_canviewdef = ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DEFINITION'), 0);
		BEGIN TRY
			SET @cc_sql = N'SELECT @k = COUNT(*) FROM sys.dm_database_encryption_keys;
				SELECT @s = COUNT(*) FROM master.sys.certificates AS ce
					INNER JOIN sys.dm_database_encryption_keys AS ek ON ek.encryptor_thumbprint = ce.thumbprint
					WHERE ce.pvt_key_last_backup_date IS NULL OR ce.pvt_key_last_backup_date <= DATEADD(DAY, -30, GETDATE());';
			EXEC sp_executesql @cc_sql, N'@k INT OUTPUT, @s INT OUTPUT', @k = @cc_keys OUTPUT, @s = @cc_count OUTPUT;
			SET @cc_sql = N'SELECT ROW_NUMBER() OVER (ORDER BY ce.name), QUOTENAME(ce.name)
					, N''databases=''+ CONVERT(NVARCHAR(12), COUNT(*)) + N'', last private key backup='' + ISNULL(CONVERT(NVARCHAR(19), ce.pvt_key_last_backup_date, 120), N''none on record'')
				FROM master.sys.certificates AS ce
					INNER JOIN sys.dm_database_encryption_keys AS ek ON ek.encryptor_thumbprint = ce.thumbprint
				WHERE ce.pvt_key_last_backup_date IS NULL OR ce.pvt_key_last_backup_date <= DATEADD(DAY, -30, GETDATE())
				GROUP BY ce.name, ce.pvt_key_last_backup_date;';
			INSERT INTO @cc_obj (rn, objname, measure) EXEC sp_executesql @cc_sql;
		END TRY
		BEGIN CATCH
			INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check TDE certificate: encryption keys', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
			SET @cc_gap = 1;
		END CATCH
		IF @cc_gap = 1 OR @cc_count IS NULL
			SELECT @cc_gap = 1, @cc_msg = N'Coverage unknown. The database encryption keys could not be read (this needs VIEW SERVER STATE), so no encrypted database was matched to its certificate. This is not a clean result.';
		ELSE IF ISNULL(@cc_certs, 0) = 0 AND ISNULL(@cc_keys, 0) > 0
			SELECT @cc_gap = 1, @cc_msg = N'Coverage unknown. ' + CONVERT([NVARCHAR] (12), @cc_keys) + N' database(s) are encrypted, but this login sees no certificate in master, so the certificates are hidden from it. This is not a clean result.';
		ELSE IF ISNULL(@cc_certs, 0) = 0 AND @cc_canviewdef = 0
			SELECT @cc_gap = 1, @cc_msg = N'Coverage unknown. This login sees no certificate in master and lacks VIEW ANY DEFINITION, so certificates may be hidden from it. This is not a clean result.';
		IF @cc_gap = 1
			SELECT @cc_verdict = N'WARN', @cc_count = NULL;
		ELSE IF @cc_count = 0
			SELECT @cc_verdict = N'PASS', @cc_msg = N'No encrypted database uses a certificate whose private key lacks a backup from the last 30 days.';
		ELSE
			SELECT @cc_verdict = N'FAIL', @cc_msg = CONVERT([NVARCHAR] (12), @cc_count) + N' encrypted database(s) use a certificate whose private key has no backup from the last 30 days. Without that backup, the databases and their backups cannot be restored on another server.';
	END
	SET @cc_sev = CASE @cc_verdict WHEN N'FAIL' THEN @Result_ReallyBad WHEN N'WARN' THEN @Result_Warning WHEN N'PASS' THEN @Result_Good ELSE @Result_NA END;
	INSERT #output_sqldba_org_sp_triage (SectionID, Section, Summary, Severity, Details)
		SELECT 3008, hdr.Section, x.Summary, @cc_sev, x.Details
		FROM (SELECT N'Core check: TDE certificate key backup [SQLT-BLITZ-TDE-CERT-BACKUP; public equivalent: sp_Blitz CheckID 119]' AS Section) AS hdr
		CROSS JOIN (
			SELECT 0 AS rn, @cc_verdict + N': ' + @cc_msg AS Summary
				, N'count=' + ISNULL(CONVERT([NVARCHAR] (12), @cc_count), N'NULL') + N'. Counts encrypted databases whose certificate in master has no private key backup in the last 30 days, or none on record.' AS Details
			UNION ALL
			SELECT o.rn, o.objname, o.measure FROM @cc_obj AS o
		) AS x
		ORDER BY x.rn;
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check: TDE certificate key backup', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage = ERROR_MESSAGE();
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH

	/* 3009 Backup-encryption certificate key backup. Reads master.sys.certificates; VIEW DEFINITION is tested in master.
	   M2-f: SKIP only below SQL Server 2014 (@SQLVersion < 12), never from an msdb catalog this login cannot read: on 2014+
	   an unreadable msdb reaches the coverage WARN below. */
BEGIN TRY
	DELETE FROM @cc_obj;
	SELECT @cc_count = NULL, @cc_gap = 0, @cc_dbdef = NULL;
	IF @SQLVersion < 12
		OR NOT EXISTS (SELECT 1 FROM sys.all_columns AS ac WHERE ac.[object_id] = OBJECT_ID(N'sys.certificates') AND ac.name = N'pvt_key_last_backup_date')
		SELECT @cc_verdict = N'SKIP', @cc_msg = N'Backup encryption needs SQL Server 2014 or later, so there is nothing to check.';
	ELSE
	BEGIN
		EXEC master.sys.sp_executesql N'SELECT @p = CASE WHEN CONVERT(INT, HAS_PERMS_BY_NAME(NULL, N''DATABASE'', N''VIEW DEFINITION'')) = 1 THEN 1 ELSE 0 END;'
			, N'@p INT OUTPUT', @p = @cc_dbdef OUTPUT;
		BEGIN TRY
			SET @cc_sql = N'SELECT @s = COUNT(DISTINCT ce.thumbprint) FROM master.sys.certificates AS ce
					INNER JOIN msdb.dbo.backupset AS bks ON bks.encryptor_thumbprint = ce.thumbprint
					WHERE ce.pvt_key_last_backup_date IS NULL OR ce.pvt_key_last_backup_date <= DATEADD(DAY, -30, GETDATE());';
			EXEC sp_executesql @cc_sql, N'@s INT OUTPUT', @s = @cc_count OUTPUT;
			SET @cc_sql = N'SELECT ROW_NUMBER() OVER (ORDER BY ce.name), QUOTENAME(ce.name)
					, N''encrypted backups in history='' + CONVERT(NVARCHAR(12), COUNT(*)) + N'', last private key backup='' + ISNULL(CONVERT(NVARCHAR(19), ce.pvt_key_last_backup_date, 120), N''none on record'')
				FROM master.sys.certificates AS ce
					INNER JOIN msdb.dbo.backupset AS bks ON bks.encryptor_thumbprint = ce.thumbprint
				WHERE ce.pvt_key_last_backup_date IS NULL OR ce.pvt_key_last_backup_date <= DATEADD(DAY, -30, GETDATE())
				GROUP BY ce.name, ce.pvt_key_last_backup_date;';
			INSERT INTO @cc_obj (rn, objname, measure) EXEC sp_executesql @cc_sql;
		END TRY
		BEGIN CATCH
			INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check backup certificate: read', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
			SET @cc_gap = 1;
		END CATCH
		IF @cc_gap = 1 OR @cc_count IS NULL
			SELECT @cc_verdict = N'WARN', @cc_count = NULL
				, @cc_msg = N'Coverage unknown. The certificates or the msdb backup history could not be read. This is not a clean result.';
		ELSE IF @cc_count > 0
			SELECT @cc_verdict = N'FAIL'
				, @cc_msg = CONVERT([NVARCHAR] (12), @cc_count) + N' certificate(s) used to encrypt backups have no private key backup from the last 30 days. Without that key, those backups cannot be restored.';
		ELSE IF ISNULL(@cc_dbdef, 0) = 0
			SELECT @cc_verdict = N'WARN', @cc_count = NULL
				, @cc_msg = N'Coverage unknown. This login lacks VIEW DEFINITION in master, so certificates may be hidden from it. This is not a clean result.';
		ELSE
			SELECT @cc_verdict = N'PASS'
				, @cc_msg = N'No certificate used to encrypt a backup lacks a private key backup from the last 30 days.';
	END
	SET @cc_sev = CASE @cc_verdict WHEN N'FAIL' THEN @Result_ReallyBad WHEN N'WARN' THEN @Result_Warning WHEN N'PASS' THEN @Result_Good ELSE @Result_NA END;
	INSERT #output_sqldba_org_sp_triage (SectionID, Section, Summary, Severity, Details)
		SELECT 3009, hdr.Section, x.Summary, @cc_sev, x.Details
		FROM (SELECT N'Core check: Backup encryption certificate key backup [SQLT-BLITZ-BACKUP-ENCRYPTION-CERT-BACKUP; public equivalent: sp_Blitz CheckID 202]' AS Section) AS hdr
		CROSS JOIN (
			SELECT 0 AS rn, @cc_verdict + N': ' + @cc_msg AS Summary
				, N'count=' + ISNULL(CONVERT([NVARCHAR] (12), @cc_count), N'NULL') + N'. Counts certificates in master that encrypted a backup in the msdb history and have no private key backup in the last 30 days, or none on record.' AS Details
			UNION ALL
			SELECT o.rn, o.objname, o.measure FROM @cc_obj AS o
		) AS x
		ORDER BY x.rn;
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check: Backup encryption certificate key backup', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage = ERROR_MESSAGE();
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH

	/* 3010 Memory dumps in the last 30 days. sys.dm_server_memory_dumps is read only on 2014 and later. */
BEGIN TRY
	DELETE FROM @cc_obj;
	SELECT @cc_count = NULL, @cc_listok = 1;
	IF @SQLVersion < 12
		SELECT @cc_verdict = N'SKIP', @cc_msg = N'This check reads memory dumps on SQL Server 2014 and later only. Look for SQLDump files in the error log folder instead.';
	ELSE
	BEGIN
		BEGIN TRY
			SET @cc_sql = N'SELECT @d = COUNT(*) FROM sys.dm_server_memory_dumps AS md WHERE md.creation_time > DATEADD(DAY, -30, SYSUTCDATETIME());';
			EXEC sp_executesql @cc_sql, N'@d INT OUTPUT', @d = @cc_count OUTPUT;
		END TRY
		BEGIN CATCH
			INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check memory dumps: read', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
			SET @cc_listok = 0;
		END CATCH
		IF @cc_listok = 0
			SELECT @cc_verdict = N'WARN', @cc_count = NULL
				, @cc_msg = N'Coverage unknown. The list of memory dumps could not be read. This is not a clean result.';
		ELSE IF @cc_count = 0
			SELECT @cc_verdict = N'PASS', @cc_msg = N'No memory dump was written in the last 30 days.';
		ELSE
			SELECT @cc_verdict = N'WARN'
				, @cc_msg = CONVERT([NVARCHAR] (12), @cc_count) + N' memory dump(s) were written in the last 30 days. Read the SQLDump files in the error log folder to find the cause.';
	END
	SET @cc_sev = CASE @cc_verdict WHEN N'WARN' THEN @Result_Warning WHEN N'PASS' THEN @Result_Good ELSE @Result_NA END;
	INSERT #output_sqldba_org_sp_triage (SectionID, Section, Summary, Severity, Details)
		SELECT 3010, hdr.Section, x.Summary, @cc_sev, x.Details
		FROM (SELECT N'Core check: Memory dumps [SQLT-BLITZ-MEMORY-DUMPS-HAVE-OCCURRED-SQLDUMP-FILES-PRESENT; public equivalent: sp_Blitz CheckID 171]' AS Section) AS hdr
		CROSS JOIN (
			SELECT 0 AS rn, @cc_verdict + N': ' + @cc_msg AS Summary
				, N'count=' + ISNULL(CONVERT([NVARCHAR] (12), @cc_count), N'NULL') + N'. Counts memory dumps from the last 30 days. sp_Blitz reports dumps from the last year.' AS Details
		) AS x;
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check: Memory dumps', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage = ERROR_MESSAGE();
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH

	/* 3011 Linked servers. sys.servers always holds the local server's own row, so an empty sys.servers means the
	   catalog is hidden from this login, not that it is bare. */
BEGIN TRY
	DELETE FROM @cc_obj;
	SELECT @cc_srvrows = COUNT(*) FROM sys.servers;
	SELECT @cc_llrows = COUNT(*) FROM sys.linked_logins;
	SET @cc_canviewdef = ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DEFINITION'), 0);
	INSERT INTO @cc_obj (rn, objname, measure)
		SELECT ROW_NUMBER() OVER (ORDER BY srv.name), QUOTENAME(srv.name), CONVERT([NVARCHAR] (12), COUNT(*))
		FROM sys.servers AS srv
			INNER JOIN sys.linked_logins AS lnk ON lnk.server_id = srv.server_id
		WHERE srv.is_linked = 1
		GROUP BY srv.name;
	SELECT @cc_count = ISNULL(SUM(CONVERT(INT, o.measure)), 0) FROM @cc_obj AS o;
	UPDATE @cc_obj SET measure = N'login mappings=' + measure;
	IF @cc_count = 0 AND (ISNULL(@cc_srvrows, 0) = 0 OR @cc_canviewdef = 0)
		SELECT @cc_verdict = N'WARN', @cc_count = NULL
			, @cc_msg = CASE WHEN ISNULL(@cc_srvrows, 0) = 0
				THEN N'Coverage unknown. sys.servers shows no rows at all, not even this server''s own row, so linked servers are hidden from this login. This is not a clean result.'
				ELSE N'Coverage unknown. This login lacks VIEW ANY DEFINITION, so linked servers may be hidden from it (it sees ' + CONVERT([NVARCHAR] (12), ISNULL(@cc_srvrows, 0)) + N' server row(s) and ' + CONVERT([NVARCHAR] (12), ISNULL(@cc_llrows, 0)) + N' login mapping row(s)). This is not a clean result.' END;
	ELSE IF @cc_count > 0
		SELECT @cc_verdict = N'WARN'
			, @cc_msg = CONVERT([NVARCHAR] (12), @cc_count) + N' linked server login mapping(s) found. Check that each linked server is still needed and that its logins have no more rights than they need.';
	ELSE
		SELECT @cc_verdict = N'PASS', @cc_msg = N'No linked server login mapping was found on this instance.';
	SET @cc_sev = CASE @cc_verdict WHEN N'WARN' THEN @Result_Warning ELSE @Result_Good END;
	INSERT #output_sqldba_org_sp_triage (SectionID, Section, Summary, Severity, Details)
		SELECT 3011, hdr.Section, x.Summary, @cc_sev, x.Details
		FROM (SELECT N'Core check: Linked servers [SQLT-BLITZ-LINKED-SERVER-CONFIGURED; public equivalent: sp_Blitz CheckID 49]' AS Section) AS hdr
		CROSS JOIN (
			SELECT 0 AS rn, @cc_verdict + N': ' + @cc_msg AS Summary
				, N'count=' + ISNULL(CONVERT([NVARCHAR] (12), @cc_count), N'NULL') + N'. Counts linked server login mappings: each row of sys.linked_logins that belongs to a linked server.' AS Details
			UNION ALL
			SELECT o.rn, o.objname, o.measure FROM @cc_obj AS o
		) AS x
		ORDER BY x.rn;
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Core check: Linked servers', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage = ERROR_MESSAGE();
	RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
END CATCH
	SET @CustomErrorText = 'Core checks (corpus parity, basic set)';
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT;
		RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
	END

/*ADD NEW sections before this line*/
/*ADD NEW sections before this line*/
/*ADD NEW sections before this line*/
/*ADD NEW sections before this line*/
	/*----------------------------------------
	--TEMPLATE
	----------------------------------------*/
BEGIN TRY
	SET @CustomErrorText = 'The thing'
	--INSERT #output_sqldba_org_sp_triage (
	--	[SectionID]
	--	, [Section]
	--	, [Summary]
	--	, [Details]
	--	) 
	--	SELECT 104, 'The thing','------','------'
	--	INSERT #output_sqldba_org_sp_triage (
	--	[SectionID]
	--	, [Section]
	--	, [Summary]
	--	, [Details]
	--	)
	--	SELECT 104
	--	, 'Something:'+Section
	--	, Summary
	--	, [Details]
	--	FROM  #output_sqldba_org_sp_triage 
	--	WHERE [SectionID] = 132324593242
		
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'End-of-checks template (no check runs here)', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	RAISERROR (N'Error with the thing',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Done with the end-of-checks template',0,1) WITH NOWAIT;
	END
	/*----------------------------------------
	--TEMPLATE
	----------------------------------------*/
/*ADD NEW sections before this line*/
/*ADD NEW sections before this line*/

/*----------------------------------------
--Add Latest Blitz output
----------------------------------------*/
/* A login can see this table (VIEW ANY DEFINITION) and still be refused SELECT on it: the CATCH writes the skip row. */
BEGIN TRY
SET @CustomErrorText = 'Add Blitz'
IF OBJECT_ID('master.dbo.sqldba_sp_Blitz_output') IS NULL
/*If no Blitz table, run Blitz*/
BEGIN
	IF @Debug = 1
		RAISERROR (N'Skipping sqldba_sp_Blitz results, cannot find output table',0,1) WITH NOWAIT;
	--EXEC [dbo].[sp_Blitz] @CheckUserDatabaseObjects = 1 , @CheckProcedureCache = 1 , @OutputType = 'TABLE' , @OutputProcedureCache = 0 , @CheckServerInfo = 1, @OutputDatabaseName = 'master', @OutputSchemaName = 'dbo', @OutputTableName = 'sp_Blitz_output', @BringThePain = 1;
END
IF OBJECT_ID('master.dbo.sqldba_sp_Blitz_output') IS NOT NULL
BEGIN
	IF @Debug = 1
		RAISERROR (N'Found sqldba_sp_Blitz results, only recent results will be evaluated',0,1) WITH NOWAIT;
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
) SELECT 999, 'Blitz from here','------','------'
	INSERT INTO #output_sqldba_org_sp_triage ( 
	domain
	,SQLInstance
	,evaldate
	,SectionID
	,Section
	,Summary
	, Details )
	EXEC ('
	SELECT  '''+@ThisDomain+''' [Domain]
	,ServerName
	, CONVERT([VARCHAR],CheckDate,120)
	,CheckID --Priority + 1000
	--, FindingsGroup,
	, ''sp_Blitz:'' + Finding
	, DatabaseName
	, CONVERT([NVARCHAR] (4000),Details)
	FROM master.dbo.sqldba_sp_Blitz_output 
	WHERE CheckDate = (SELECT max([CheckDate]) 
		FROM master.dbo.sqldba_sp_Blitz_output 
		HAVING DATEADD(DAY,-2,GETDATE()) < max([CheckDate]) )
	ORDER BY ID ASC'
	)

END
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'sp_Blitz results', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	SET @CustomErrorText = 'Error with '+ @CustomErrorText
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH

/*----------------------------------------
--Add Latest Security Checklist output
----------------------------------------*/
/*

*/
BEGIN TRY
	SET @CustomErrorText = 'Security checklist results'
IF OBJECT_ID(N'master.dbo.stpSecurity_Checklist_Table') IS NULL
/*If no Blitz table, run Blitz*/
BEGIN
	IF @Debug = 1
		RAISERROR (N'Skipping sqldba_stpSecurity_Checklist results, cannot find output table',0,1) WITH NOWAIT;
	
END
IF OBJECT_ID(N'master.dbo.stpSecurity_Checklist_Table') IS NOT NULL
	AND COL_LENGTH(N'master.dbo.stpSecurity_Checklist_Table', N'How to Fix') IS NULL
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (N'Security checklist results', 0, N'master.dbo.stpSecurity_Checklist_Table has the Portuguese column names (stpSecurity_Checklist was run with @language = ''pt''), so its results were not read. Run it with @language = ''en''.');
IF OBJECT_ID(N'master.dbo.stpSecurity_Checklist_Table') IS NOT NULL
	AND COL_LENGTH(N'master.dbo.stpSecurity_Checklist_Table', N'How to Fix') IS NOT NULL
BEGIN
	IF @Debug = 1
		RAISERROR (N'Found sqldba_stpSecurity_Checklist results, only recent results will be evaluated',0,1) WITH NOWAIT;
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
) SELECT 1001, 'sqldba_stpSecurity_Checklist from here','------','------'
	INSERT INTO #output_sqldba_org_sp_triage ( 
	domain
	,SQLInstance
	,evaldate
	,SectionID
	,Section
	,Summary
	, Details )
	EXEC ('SELECT  [Domain]
	,[SQLInstance]
	, [evaldate]
	,[code]+1000
	, ''stpSecurity:'' + [Category] + '';'' +[Title]
	, ISNULL([Result],'''') + '';'' +[How this can be an Issue] + '';''+[Technical explanation]
	, [How to Fix] 
	FROM master.dbo.stpSecurity_Checklist_Table 
	WHERE [evaldate] = (SELECT max([evaldate]) 
	FROM master.dbo.stpSecurity_Checklist_Table 
	HAVING DATEADD(DAY,-2,GETDATE()) < max([evaldate]) )
	ORDER BY [code] ASC'
	)

END

		SET @CustomErrorText = 'Done looking at '+ @CustomErrorText
END TRY
BEGIN CATCH
	INSERT #sp_triage_skipped (Step, ErrorNumber, ErrorMessage) VALUES (LEFT(ISNULL(N'Security checklist results', N'?'), 400), ISNULL(ERROR_NUMBER(), 0), LEFT(ISNULL(ERROR_MESSAGE(), N''), 4000));
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
	SET @CustomErrorText = 'Error with '+ @CustomErrorText
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
END CATCH



			/*----------------------------------------
			--select output
			----------------------------------------*/
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Cleaning up output table',0,1) WITH NOWAIT;
	END

/* Invariant 2: the ONE place a recorded skip becomes an output row. A row with no SQL error (ErrorNumber 0 or NULL, e.g. a
   deliberate skip) reads 'could not run: <step>, <message>'; a real error keeps '<step>, <number>, <message>'
   (SpTriageCouldNotRunShapeLiveTests runs this SELECT). Placed before the fix-up UPDATE so
   the SCREEN and TABLE paths both carry it. A CATCH after this point cannot reach the output; it keeps
   its RAISERROR (SpTriageSkipRowCensusTests counts those by position). */
INSERT #output_sqldba_org_sp_triage
(
	SectionID
	, Section
	, Summary
	, Severity
)
SELECT
	0
	, N'Could not run'
	, LEFT(N'could not run: ' + ISNULL(s.Step, N'?') + N', ' + CASE WHEN ISNULL(s.ErrorNumber, 0) = 0 THEN N'' ELSE CONVERT([NVARCHAR] (12), s.ErrorNumber) + N', ' END + ISNULL(s.ErrorMessage, N''), 4000)
	, @Result_CouldNotRun
FROM #sp_triage_skipped s
ORDER BY s.Seq ASC;

UPDATE  #output_sqldba_org_sp_triage
SET evaldate = @evaldate
, SQLInstance = @ThisServer
, domain = @ThisDomain

/* 2026-07-08: restore xp_cmdshell BEFORE any result set streams to the client - a severe
   mid-stream error (seen live: Msg 0, Level 11) aborts the batch and skipped the end-of-run
   restore, leaving xp_cmdshell enabled. Hoisted ABOVE both the SCREEN and TABLE output paths
   (last xp_cmdshell consumer in the proc is far earlier); the end-of-run restore still runs
   on healthy paths and re-running it is a no-op. */
IF @XpCmdShellWasOff = 1
BEGIN TRY
	EXEC sp_configure 'xp_cmdshell', 0;
	RECONFIGURE;
	SET @XpCmdShellWasOff = 0;
	IF @AdvancedOptionsWasOff = 1
	BEGIN
		EXEC sp_configure 'show advanced options', 0;
		RECONFIGURE;
		SET @AdvancedOptionsWasOff = 0;
	END;
	RAISERROR ('xp_cmdshell and show advanced options restored to their state before this run.',0,1) WITH NOWAIT;
END TRY
BEGIN CATCH
	RAISERROR ('WARNING: could not restore xp_cmdshell to OFF - please disable it manually.',0,1) WITH NOWAIT;
END CATCH

/* 2026-10-23 (lane sp-triage-screen-control-chars). Invariant: #output_sqldba_org_sp_triage holds no control character
   by the time ANY consumer reads it: the @Export = SCREEN select, the export-table INSERT (which the app and the upload
   read) and the TABLE path's screen echo. @OutputSanitiseSQL runs ONCE, here, before the @Export branches.
   Sections 39 (the plan_handle bytes, query text) and 108 (REG_BINARY registry values) carry NCHAR(1-31). They were
   suspected of the 2026-10-23 'A severe error occurred' and cleared: that was an error-log archive number of 100 (see
   'Iterate through each log file'). This stays so raw bytes never reach a reader. It replaces NCHAR(0-8, 11, 12, 14-31) with a space, in one UPDATE, under COLLATE
   Latin1_General_BIN2 (any other collation treats NCHAR(0) as ignorable and REPLACE is a no-op). TAB, LF and CR are left
   to the consumers' own REPLACEs. Nested REPLACE, not TRANSLATE: the script still installs on SQL 2008. */
DECLARE @OutputSanitiseSQL NVARCHAR(MAX), @CtrlExpr NVARCHAR(MAX), @CtrlChar INT;
SET @CtrlExpr = N'{c} COLLATE Latin1_General_BIN2';
SET @CtrlChar = 0;
WHILE @CtrlChar < 32
BEGIN
	IF @CtrlChar NOT IN (9, 10, 13)
		SET @CtrlExpr = N'REPLACE(' + @CtrlExpr + N', NCHAR(' + CONVERT(NVARCHAR(2), @CtrlChar) + N'), N'' '')';
	SET @CtrlChar = @CtrlChar + 1;
END
SET @OutputSanitiseSQL = N'UPDATE #output_sqldba_org_sp_triage SET domain = ' + REPLACE(@CtrlExpr, N'{c}', N'domain')
	+ N', SQLInstance = ' + REPLACE(@CtrlExpr, N'{c}', N'SQLInstance')
	+ N', Section = ' + REPLACE(@CtrlExpr, N'{c}', N'Section')
	+ N', Summary = ' + REPLACE(@CtrlExpr, N'{c}', N'Summary')
	+ N', Severity = ' + REPLACE(@CtrlExpr, N'{c}', N'Severity')
	+ N', Details = ' + REPLACE(@CtrlExpr, N'{c}', N'Details') + N';';

BEGIN TRY
	EXEC sp_executesql @OutputSanitiseSQL;
END TRY
BEGIN CATCH
	SELECT @errMessage = ERROR_MESSAGE();
	RAISERROR (N'Output sanitise failed: %s',0,1,@errMessage) WITH NOWAIT;
END CATCH

IF UPPER(LEFT(@Export,1)) = 'S'
BEGIN
	SELECT T1.ID
	,  evaldate
	, T1.domain
	, T1.SQLInstance
	, T1.SectionID
	, T1.Section
	, ISNULL(T1.Summary,'') [Summary]
	, ISNULL(T1.Severity,'') [Severity]
	, replace(replace(replace(replace( ISNULL(T1.Details,''), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ') [Details]
	, ISNULL(T1.HoursToResolveWithTesting,'') [HoursToResolveWithTesting]
	, NULL QueryPlan
	FROM #output_sqldba_org_sp_triage T1
	WHERE ISNULL(T1.SectionID, -1) <> 104 /*SectionID 104 (every raw perfmon counter, ~2,100 rows) goes to the export table only, never to the screen (lane sp-triage-perf-counter-summaries; SpTriageScreenHides104CensusTests). Sections 120-123 summarise it.*/
	ORDER BY ID ASC
	OPTION (RECOMPILE)
END



/* 2026-10-16 (member 5): the export-table block (CREATE, the one DELETE, ALTER, INSERT) runs with no lock timeout
   and normal deadlock priority. The app reads its results from this table, so the triage's own lock timeout must
   never cost a TABLE run its output. */
SET LOCK_TIMEOUT -1;
SET DEADLOCK_PRIORITY NORMAL;
IF UPPER(LEFT(@Export,1)) = 'T'
BEGIN
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Export to table. Creating table.',0,1) WITH NOWAIT;
	END

	IF OBJECT_ID(@ExportDBName + '.' + @ExportSchema  + '.' + @ExportTableName) IS NULL
	BEGIN
	/*
	If the table does not exist, then create it. These are the core columns used in the output table.
	No indexes have been added, I don't believe there's a need for it, and I've only been using this for 10 years on 5000+ servers ~ Adrian
	*/
		SET @dynamicSQL = 'CREATE TABLE ' + @ExportDBName + '.' + @ExportSchema  + '.' + @ExportTableName + '
	( 
	ID INT
	,  evaldate [NVARCHAR] (20)
	, domain [NVARCHAR] (505)
	, SQLInstance [NVARCHAR] (505)
	, SectionID INT
	, Section [NVARCHAR] (4000)
	, Summary [NVARCHAR] (4000)
	, Severity [NVARCHAR] (5)
	, Details [NVARCHAR] (4000)
	, HoursToResolveWithTesting MONEY 
	, QueryPlan [NVARCHAR] (MAX)
	);'
		BEGIN TRY
			EXEC sp_executesql @dynamicSQL;	/*wrapped 2026-07-02 (was bare like the sibling DELETE below): a CREATE-permission failure in the export DB no longer aborts the whole run at the tail after all work is done*/
		END TRY
		BEGIN CATCH
			RAISERROR (N'Could not create export table (permission?); results still returned on screen',0,1) WITH NOWAIT;
			SELECT @errMessage = ERROR_MESSAGE(); RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
		END CATCH
	END
	/* 2026-10-16 (lane bundle-sp-triage-fixes-a, member 3). Invariant: a run whose @Export does not select the table
	   never deletes from the export table. This is the ONE delete on it: inside the @Export = TABLE block, keeping rows
	   newer than the SMALLER of @CleanupTime and @ExportCleanupDays (days); NULL on both, or a negative value, deletes
	   nothing. evaldate is NVARCHAR(20) in style 120, so the cutoff is compared as a style-120 string: a DATETIME
	   compare fails under SET LANGUAGE British. */
	DECLARE @ExportCleanupCutoffDays INT;
	SET @ExportCleanupCutoffDays = CASE
		WHEN @CleanupTime IS NULL THEN @ExportCleanupDays
		WHEN @ExportCleanupDays IS NULL OR @CleanupTime < @ExportCleanupDays THEN @CleanupTime
		ELSE @ExportCleanupDays END;
	IF @ExportCleanupCutoffDays >= 0 AND OBJECT_ID(@ExportDBName + '.' + @ExportSchema  + '.' + @ExportTableName) IS NOT NULL
	BEGIN TRY
		RAISERROR (N'Cleaning output table',0,1) WITH NOWAIT;
		SET @dynamicSQL = N'DELETE FROM ' + @ExportDBName + N'.' + @ExportSchema + N'.' + @ExportTableName + N'
		WHERE evaldate < ''' + CONVERT([NVARCHAR] (20), DATEADD(DAY, 0 - @ExportCleanupCutoffDays, GETDATE()), 120) + N'''';
		EXEC sp_executesql @dynamicSQL;
	END TRY
	BEGIN CATCH
		SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
		RAISERROR (N'Failed to clean old records in output table',0,1) WITH NOWAIT;
	END CATCH
	/*
	Okay, time to be clever. Let's assume the table was built in a previous version, let's compare columns and see what needs to be added.Error with modifying output table
	2026-10-26 (lane bundle-sp-triage-fixes-b, member 3): both sides are found by object id. The old filters never matched:
	the target side looked for a table LIKE '%output_man_script%', and the export side compared t.name with the already
	bracketed @ExportTableName, so no missing column was ever added.
	Look at columns in the table and evaluate columsn still to be added.
	*/
	SET @dynamicSQL = '
  DECLARE @ColumnsToAdd TABLE 
  (
	ID [INT] IDENTITY(1,1)
	, ColumnName [NVARCHAR] (500)
	, [order] INT
	, [length] INT
  )
  INSERT INTO @ColumnsToAdd
  SELECT 
  targetcolumns.name
  , targetcolumns.column_id
  , targetcolumns.max_length
  FROM (
	  SELECT 
	  c.name
	  , column_id
	  , max_length
	  FROM tempdb.[sys].columns c
	  WHERE c.object_id = OBJECT_ID(N''tempdb..#output_sqldba_org_sp_triage'')
	  ) targetcolumns
  LEFT OUTER JOIN (
	  SELECT 
	  c.name
	  , column_id
	  , max_length
	  FROM '+@ExportDBName +'.[sys].columns c
  WHERE c.object_id = OBJECT_ID(N''' + REPLACE(@ExportDBName + '.' + @ExportSchema + '.' + @ExportTableName, '''', '''''') + ''')
  ) currentcolumns ON targetcolumns.name = currentcolumns.name
  WHERE currentcolumns.name IS NULL

  DECLARE @MaxcolumnsToAdd [INT] 
  SET @MaxcolumnsToAdd = 0;
  DECLARE @ColumnCountLoop INT
  SET @ColumnCountLoop = 1; 
  DECLARE @ColumnToAdd [NVARCHAR] (500);
  DECLARE @ColumnToAddLen INT
  SET @ColumnToAddLen= 0;
  SET @MaxcolumnsToAdd  = (SELECT MAX(ID) FROM @ColumnsToAdd)
  IF @MaxcolumnsToAdd > 0
  BEGIN
	WHILE @ColumnCountLoop <= @MaxcolumnsToAdd
		BEGIN
			SELECT @ColumnToAdd = ColumnName
			, @ColumnToAddLen = [length]
			FROM @ColumnsToAdd WHERE ID = @ColumnCountLoop

			IF @ColumnToAdd = ''evaldate''
				ALTER TABLE '+  @ExportDBName +'.' + @ExportSchema + '.' + @ExportTableName + ' ADD evaldate [NVARCHAR] (20)
			IF @ColumnToAdd = ''domain''
				ALTER TABLE '+  @ExportDBName +'.' + @ExportSchema + '.' + @ExportTableName + ' ADD domain [NVARCHAR] (505) DEFAULT '''+@ThisDomain+'''
			IF @ColumnToAdd = ''SQLInstance''
				ALTER TABLE '+  @ExportDBName +'.' + @ExportSchema + '.' + @ExportTableName + ' ADD SQLInstance [NVARCHAR] (505) DEFAULT ''' +@ThisServer +'''
			IF @ColumnToAdd = ''SectionID''
				ALTER TABLE '+  @ExportDBName +'.' + @ExportSchema + '.' + @ExportTableName + ' ADD SectionID [INT] NULL
			IF @ColumnToAdd = ''Section''
				ALTER TABLE '+  @ExportDBName +'.' + @ExportSchema + '.' + @ExportTableName + ' ADD Section [NVARCHAR] (4000)
			IF @ColumnToAdd = ''Summary''
				ALTER TABLE '+  @ExportDBName +'.' + @ExportSchema + '.' + @ExportTableName + ' ADD Summary [NVARCHAR] (4000)
			IF @ColumnToAdd = ''Severity''
				ALTER TABLE '+  @ExportDBName +'.' + @ExportSchema + '.' + @ExportTableName + ' ADD Severity [NVARCHAR] (5)
			IF @ColumnToAdd = ''Details''
				ALTER TABLE '+  @ExportDBName +'.' + @ExportSchema + '.' + @ExportTableName + ' ADD Details [NVARCHAR] (4000)
			IF @ColumnToAdd = ''QueryPlan''
				ALTER TABLE '+  @ExportDBName +'.' + @ExportSchema + '.' + @ExportTableName + ' ADD QueryPlan [NVARCHAR] (MAX) NULL /*was XML; matches the CREATE-TABLE path (10352) - the INSERT feeds a whitespace-collapsed NVARCHAR string, not guaranteed-valid XML*/
			IF @ColumnToAdd = ''HoursToResolveWithTesting''
				ALTER TABLE '+  @ExportDBName +'.' + @ExportSchema + '.' + @ExportTableName + ' ADD HoursToResolveWithTesting MONEY  NULL
			SET @ColumnCountLoop = @ColumnCountLoop + 1;
		END
	END
	';
		BEGIN TRY
			EXEC sp_executesql @dynamicSQL;
		END TRY
		BEGIN CATCH
			SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
			RAISERROR (N'Error with modifying output table',0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
			RAISERROR (N'%s', 0, 1, @CustomErrorText) WITH NOWAIT;
		END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Populating table',0,1) WITH NOWAIT;
	END
	SET @dynamicSQL = 'INSERT INTO ' + @ExportDBName + '.' + @ExportSchema  + '.' + @ExportTableName + '
			(ID
			, evaldate
			, domain
			, SQLInstance
			, SectionID
			, Section
			, Summary
			, Severity
			, Details
			, HoursToResolveWithTesting
			, QueryPlan)
	SELECT T1.ID
	, evaldate
	, REPLACE(T1.domain, ''~'','':'') domain
	, REPLACE('''+ @ThisServer  +''', ''~'','':'') [Server]
	, REPLACE(replace(replace(replace(replace( ISNULL(T1.SectionID,''''), CHAR(9), '' ''),CHAR(10),'' ''), CHAR(13), '' ''), ''  '','' ''), ''~'','':'') SectionID
	, REPLACE(replace(replace(replace(replace( ISNULL(T1.Section,''''), CHAR(9), '' ''),CHAR(10),'' ''), CHAR(13), '' ''), ''  '','' ''), ''~'','':'') Section
	, REPLACE(replace(replace(replace(replace( ISNULL(T1.Summary,''''), CHAR(9), '' ''),CHAR(10),'' ''), CHAR(13), '' ''), ''  '','' ''), ''~'','':'') Summary
	, T1.Severity
	, REPLACE(replace(replace(replace(replace( ISNULL(T1.Details,''''), CHAR(9), '' ''),CHAR(10),'' ''), CHAR(13), '' ''), ''  '','' ''), ''~'','':'') [Details]
	, T1.HoursToResolveWithTesting
	, CASE WHEN  ' + CONVERT([VARCHAR](5),@ShowQueryPlan) + ' = 1 THEN ISNULL(replace(replace(replace(replace(ISNULL(CONVERT([NVARCHAR] (MAX),QueryPlan),''''), CHAR(9), '' ''),CHAR(10),'' ''), CHAR(13), '' ''), ''  '','' ''),'''')   ELSE NULL END QueryPlan
	FROM #output_sqldba_org_sp_triage T1
	ORDER BY ID ASC'
	BEGIN TRY
		--PRINT @dynamicSQL
		EXEC sp_executesql @dynamicSQL;	
	END TRY
	
	BEGIN CATCH
		SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (N'%s', 0, 1, @errMessage) WITH NOWAIT;
		PRINT @dynamicSQL
	END CATCH


	IF @ShowOnScreenWhenResultsToTable = 1 
	BEGIN
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
			RAISERROR (N'Results to screen',0,1) WITH NOWAIT;
		END
		/* xp_cmdshell restore for this path was hoisted above the @Export branching
		   (2026-07-08 verify round: the SCREEN-only path streamed before any restore). */
		BEGIN TRY
		/*And after all that hard work, how about we select to the screen as well*/
		SELECT T1.ID
		,  @evaldate evaldate
		, REPLACE(T1.domain, '~',':') domain
		, REPLACE(@ThisServer, '~',':') [Server]
		, T1.SectionID
		, REPLACE(T1.Section, '~',':') Section
		, REPLACE(T1.Summary, '~',':') Summary
		, REPLACE(T1.Severity, '~',':') Severity
		, REPLACE(
			REPLACE(
				REPLACE(
					REPLACE(
						REPLACE(ISNULL(T1.Details,''), CHAR(9), ' ')
					,CHAR(10),' ')
				, CHAR(13), ' ')
			, '  ',' ')
		, '~',':') [Details]
		, T1.HoursToResolveWithTesting
		, CASE WHEN  @ShowQueryPlan = 1 THEN ISNULL(
			REPLACE(
				REPLACE(
					REPLACE(
						REPLACE(ISNULL(CONVERT([NVARCHAR] (MAX),QueryPlan),''), CHAR(9), ' ')
					,CHAR(10),' ')
				, CHAR(13), ' ')
			, '  ',' '),'')   ELSE NULL END QueryPlan
		FROM #output_sqldba_org_sp_triage T1
		WHERE ISNULL(T1.SectionID, -1) <> 104 /*104 is export-table only; see the SCREEN select above*/
		ORDER BY ID ASC
		END TRY
		BEGIN CATCH
			/* Seen live 2026-07-08: 'Msg 0, Level 11 ... A severe error occurred on the current
			   command' killed this screen SELECT after a clean table export, and used to abort
			   the batch before the housekeeping below. Retry once without the LOB QueryPlan
			   column and with CHAR(0) stripped - the usual TDS stream-breakers. The export
			   table already holds the full results either way. */
			SELECT @errMessage = ERROR_MESSAGE();
			RAISERROR (N'%s',0,1,@errMessage) WITH NOWAIT; /*%s form: a literal % in the error text printf-mangles the bare form*/
			RAISERROR (N'Results-to-screen SELECT failed; retrying without QueryPlan',0,1) WITH NOWAIT;
			BEGIN TRY
				SELECT T1.ID
				,  @evaldate evaldate
				, REPLACE(T1.domain, '~',':') domain
				, REPLACE(@ThisServer, '~',':') [Server]
				, T1.SectionID
				/* COLLATE BIN2 on the NUL strips: under any non-binary collation REPLACE
				   treats CHAR(0) as ignorable and the strip is a no-op (verify 2026-07-08) */
				, REPLACE(REPLACE(ISNULL(T1.Section,'') COLLATE Latin1_General_BIN2, NCHAR(0),N''), '~',':') Section
				, REPLACE(REPLACE(ISNULL(T1.Summary,'') COLLATE Latin1_General_BIN2, NCHAR(0),N''), '~',':') Summary
				, REPLACE(T1.Severity, '~',':') Severity
				, REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ISNULL(T1.Details,'') COLLATE Latin1_General_BIN2, NCHAR(0),N''), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' '), '~',':') [Details]
				, T1.HoursToResolveWithTesting
				, NULL QueryPlan
				FROM #output_sqldba_org_sp_triage T1
				WHERE ISNULL(T1.SectionID, -1) <> 104 /*104 is export-table only; see the SCREEN select above*/
				ORDER BY ID ASC
			END TRY
			BEGIN CATCH
				RAISERROR (N'Results-to-screen retry also failed; results remain in the export table.',0,1) WITH NOWAIT;
			END CATCH
		END CATCH
	END
END

/* Email/export-by-mail path REMOVED 2026-07-02.
   Rationale: it mailed the full output table (server/DB names, findings, query plans = client PII)
   as a CSV attachment to a hard-coded sqldba.org address, running the extract query EXECUTE AS
   LOGIN = 'sa'. That is an unauthorized cross-org data egress + privilege escalation on a client box.
   Results are delivered via screen (@Export='SCREEN') or the export table (@Export='TABLE'); the app
   collects from there. Do NOT reintroduce mail without an explicit, consented, per-run recipient and
   no EXECUTE AS 'sa'. Prior dead + live blocks deleted in full. */

	/*Before cleaning out tables, check if any other settings need to be turned OFF/ON*/
IF @TurnNumericRoundabortOn = 1
BEGIN
	SET NUMERIC_ROUNDABORT ON;
END

	/* Restore xp_cmdshell to OFF if (and only if) THIS run turned it on. Leaves a server that
	   already had it enabled untouched. Best-effort: a failure here must not fail the whole run. */
	IF @XpCmdShellWasOff = 1
	BEGIN TRY
		EXEC sp_configure 'xp_cmdshell', 0;
		RECONFIGURE;
		SET @XpCmdShellWasOff = 0;
		IF @AdvancedOptionsWasOff = 1
		BEGIN
			EXEC sp_configure 'show advanced options', 0;
			RECONFIGURE;
			SET @AdvancedOptionsWasOff = 0;
		END;
		RAISERROR ('xp_cmdshell and show advanced options restored to their state before this run.',0,1) WITH NOWAIT;
	END TRY
	BEGIN CATCH
		RAISERROR ('WARNING: could not restore xp_cmdshell to OFF - please disable it manually.',0,1) WITH NOWAIT;
	END CATCH

	/*Housekeeping: the export-table delete runs only inside the @Export = TABLE block above (one delete, 2026-10-16).*/


	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'Cleaning up #temp tables',0,1) WITH NOWAIT;
	END
	 IF(OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_InvalidLogins') IS NOT NULL)
        BEGIN
            EXEC sp_executesql N'DROP TABLE #output_sqldba_org_sp_triage_InvalidLogins;';
        END;
	
	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage') IS NOT NULL
		DROP TABLE #output_sqldba_org_sp_triage  

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_Action_Statistics') IS NOT NULL
		DROP TABLE #output_sqldba_org_sp_triage_Action_Statistics

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_db_sps') IS NOT NULL
		DROP TABLE #output_sqldba_org_sp_triage_db_sps

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_ConfigurationDefaults') IS NOT NULL
		DROP TABLE #output_sqldba_org_sp_triage_ConfigurationDefaults

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_querystats') IS NOT NULL
		DROP TABLE #output_sqldba_org_sp_triage_querystats

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_dbccloginfo') IS NOT NULL
		DROP TABLE #output_sqldba_org_sp_triage_dbccloginfo

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_notrust') IS NOT NULL
		DROP TABLE #output_sqldba_org_sp_triage_notrust

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_MissingIndex') IS NOT NULL
		DROP TABLE #output_sqldba_org_sp_triage_MissingIndex;

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_MissingIndexColumn') IS NOT NULL
		DROP TABLE #output_sqldba_org_sp_triage_MissingIndexColumn;

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_MissingIndexMeasure') IS NOT NULL
		DROP TABLE #output_sqldba_org_sp_triage_MissingIndexMeasure;

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_HeapTable') IS NOT NULL
		DROP TABLE #output_sqldba_org_sp_triage_HeapTable;

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_whatsets') IS NOT NULL
		DROP TABLE #output_sqldba_org_sp_triage_whatsets

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_dbccloginfo') IS NOT NULL
		DROP TABLE #output_sqldba_org_sp_triage_dbccloginfo

	IF OBJECT_ID('tempdb..output_sqldba_org_sp_triage_SQLVersionsDump') IS NOT NULL
		DROP TABLE #output_sqldba_org_sp_triage_SQLVersionsDump

	IF OBJECT_ID('tempdb..output_sqldba_org_sp_triage_SQLVersions') IS NOT NULL
		DROP TABLE #output_sqldba_org_sp_triage_SQLVersions

	IF OBJECT_ID('tempdb..#dadatafor_exec_query_stats') IS NOT NULL
		DROP TABLE #dadatafor_exec_query_stats;

	IF OBJECT_ID('tempdb..#sql_handle_convert_table') IS NOT NULL
		DROP TABLE #sql_handle_convert_table;

	IF OBJECT_ID('tempdb..#perf_report_objects') IS NOT NULL
		DROP TABLE #perf_report_objects;

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_spnCheck') IS NOT NULL
				DROP TABLE #output_sqldba_org_sp_triage_spnCheck

	IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_db_size') IS NOT NULL 
		DROP TABLE #output_sqldba_org_sp_triage_db_size
--The blitz

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_ConfigurationDefaults') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_ConfigurationDefaults;


        IF OBJECT_ID ('tempdb..#output_sqldba_org_sp_triage_Recompile') IS NOT NULL
            DROP TABLE #output_sqldba_org_sp_triage_Recompile;


		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_DatabaseDefaults') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_DatabaseDefaults;


		IF OBJECT_ID('tempdb..#DatabaseScopedConfigurationDefaults') IS NOT NULL
			DROP TABLE #DatabaseScopedConfigurationDefaults;
	
		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_DBCCs') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_DBCCs;


		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_LogInfo2012') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_LogInfo2012;


		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_LogInfo') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_LogInfo;


		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_partdb') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_partdb;

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_TraceStatus') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_TraceStatus;
	

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_driveInfo') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_driveInfo;
	

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_ErrorLog') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_ErrorLog;


		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_fnTraceGettable') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_fnTraceGettable;

		IF OBJECT_ID('tempdb..#sp_triage_deftrace') IS NOT NULL
			DROP TABLE #sp_triage_deftrace;

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_Instances') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_Instances;

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_lockinghistory') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_lockinghistory;	

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_IgnorableWaits') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_IgnorableWaits;

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_blockinghistory') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_blockinghistory;

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_FKNOIndex') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_FKNOIndex;	

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_SqueezeMe') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_SqueezeMe;
	
		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_compressionstates') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_compressionstates;

		IF OBJECT_ID('TempDB..#output_sqldba_org_sp_triage_indexusage') IS NOT NULL 
			DROP TABLE #output_sqldba_org_sp_triage_indexusage

		IF OBJECT_ID('tempdb..#output_sqldba_org_sp_triage_TraceTypes') IS NOT NULL
			DROP TABLE #output_sqldba_org_sp_triage_TraceTypes;	
--the blitz


	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](20),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT; 
		RAISERROR (N'All done',0,1) WITH NOWAIT;
	END
	/* Total run time (lane bundle-sp-triage-5, M3a): one line on the debug trace, whatever @ShowDebugTime is. A batch that
	   aborts prints no line. */
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = 'sp_triage started ' + CONVERT([VARCHAR](19), @starttime, 120) + ', total ' + CONVERT([VARCHAR](20), DATEDIFF(SECOND, @starttime, GETDATE())) + ' s';
		RAISERROR (N'%s', 0, 1, @DebugTimeMSG) WITH NOWAIT;
	END
    SET NOCOUNT OFF;
	
END
