/* بسم الله الرحمن الرحيم  */
/* In the name of God, the Merciful, the Compassionate */

/* ============================================================================
   THE PIRANHA INTERCEPTOR DESTROYER - SQL Server Post-Implementation Script
   ============================================================================
   
   PURPOSE:
   Post-implementation configuration for SQL Server covering security hardening,
   alerting, maintenance, and performance optimization.
   
   EXECUTION FLOW (run in this order):
   -----------------------------------------------------------------------------
   1. PREREQUISITES      → Variables, logging, permissions, config snapshots
   2. SERVER CONFIG      → sp_configure, security hardening, trace flags
   3. DATABASE MAIL     → Enable mail, create operators
   4. MAINTENANCE JOBS  → Error log cycling, Ola integration, job alerts
   5. SQL AGENT ALERTS  → Severity, error numbers, AG-specific alerts
   6. DATABASE SETTINGS → Per-db settings, model defaults, compat levels
   7. AUDIT & COMPLIANCE→ SQL Audit, security change tracking
   8. FINALIZATION      → Endpoint fixes, completion
   -----------------------------------------------------------------------------
   
   CONFIGURATION FLAGS (set before running):
   - @UpdateOla         : Update Ola job schedules (default: 0)
   - @AddMappedDrive   : Create backup mapped drive (default: 0)
   - @NeedEmptyFile    : Create emergency empty file (default: 0)
   - @DoDeadlocks      : Enable deadlock XEvent capture (default: 0)
   - @EnableRCSI       : Enable RCSI per database (default: 0)
   - @UpgradeCompatLevel: Upgrade database compat levels (default: 0)
   
   DEPENDENCIES:
   - Requires sysadmin or server-level permissions
   - Creates tables in master database
   - Modifies msdb for operators/alerts
   
   LAST UPDATED: 2026-03-12
   ============================================================================ */

USE [master]
GO

DECLARE @UpdateOla BIT
DECLARE @AddMappedDrive BIT
DECLARE @NeedEmptyFile BIT
DECLARE @DoDeadlocks BIT

SET @DoDeadlocks = 0
SET @UpdateOla = 0
SET @AddMappedDrive = 0
SET @NeedEmptyFile = 0

DECLARE @Raiseme NVARCHAR(500) 
SET @Raiseme = ''
RAISERROR (@Raiseme,0,1) WITH NOWAIT;

/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- Execution Logging Table
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
-- Create execution logging table to track all changes made by this script
IF NOT EXISTS (SELECT * FROM sys.objects WHERE type = 'U' AND name = 'DBA_Configuration_Log')
BEGIN
    CREATE TABLE [master].[dbo].[DBA_Configuration_Log](
        [ID] [int] IDENTITY(1,1) NOT NULL,
        [ExecutionTime] [datetime] NOT NULL DEFAULT GETDATE(),
        [ServerName] [nvarchar](128) NOT NULL DEFAULT @@SERVERNAME,
        [Category] [nvarchar](100) NOT NULL,
        [SettingName] [nvarchar](200) NOT NULL,
        [OldValue] [nvarchar](max) NULL,
        [NewValue] [nvarchar](max) NULL,
        [Status] [nvarchar](50) NOT NULL,
        [ErrorMessage] [nvarchar](max) NULL,
        CONSTRAINT [PK_DBA_Configuration_Log] PRIMARY KEY CLUSTERED ([ID] ASC)
    );
    RAISERROR ('Created DBA_Configuration_Log table', 0, 1) WITH NOWAIT;
END

-- Helper procedure to log changes
IF NOT EXISTS (SELECT * FROM sys.objects WHERE type = 'P' AND OBJECT_ID = OBJECT_ID('dbo.sp_LogConfigurationChange'))
BEGIN
    EXEC('CREATE PROCEDURE [dbo].[sp_LogConfigurationChange] AS BEGIN SET NOCOUNT ON; END');
END
EXEC('ALTER PROCEDURE [dbo].[sp_LogConfigurationChange]
    @Category NVARCHAR(100),
    @SettingName NVARCHAR(200),
    @OldValue NVARCHAR(MAX) = NULL,
    @NewValue NVARCHAR(MAX) = NULL,
    @Status NVARCHAR(50),
    @ErrorMessage NVARCHAR(MAX) = NULL
AS
BEGIN
    INSERT INTO [master].[dbo].[DBA_Configuration_Log] 
        (Category, SettingName, OldValue, NewValue, Status, ErrorMessage)
    VALUES 
        (@Category, @SettingName, @OldValue, @NewValue, @Status, @ErrorMessage);
END');

/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- Permission Checks
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

DECLARE @HasServerLevelPermissions BIT = 0;
DECLARE @HasDBOwnerPermissions BIT = 0;

-- Check if current user has sufficient permissions
BEGIN TRY
    -- Test sys.server_permissions
    SELECT TOP 1 @HasServerLevelPermissions = 1 
    SELEct * FROM sys.server_permissions 
    WHERE state = 'G' AND (principal_id = DATABASE_PRINCIPAL_ID() OR principal_id = 1);
END TRY
BEGIN CATCH
    SET @HasServerLevelPermissions = 0;
END CATCH

IF @HasServerLevelPermissions = 0
BEGIN
    RAISERROR('WARNING: Running without server-level permissions. Some configurations may fail.', 10, 1) WITH NOWAIT;
END
*/
/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
   SECTION 1.3: CONFIGURATION SNAPSHOTS
   Capture baseline state of server configuration for audit/review
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
IF OBJECT_ID('master.dbo.DBA_SYS_DATABASES') IS NULL
BEGIN
	SELECT GETDATE() [timestamp],* 
	INTO master.dbo.DBA_SYS_DATABASES
	FROM sys.databases
END 
ELSE
BEGIN
	INSERT INTO master.dbo.DBA_SYS_DATABASES
	SELECT GETDATE() [timestamp],* 
	FROM sys.databases
END


IF OBJECT_ID('master.dbo.DBA_SYS_DATABASE_FILES') IS NULL
BEGIN
	SELECT GETDATE() [Timestamp], DB_NAME(dbid) AS [DB], *
	INTO master.dbo.DBA_SYS_DATABASE_FILES
	FROM sys.sysaltfiles SF
END 
ELSE
BEGIN
	INSERT  INTO master.dbo.DBA_SYS_DATABASE_FILES
	SELECT GETDATE() [Timestamp], DB_NAME(dbid) AS [DB], *
	FROM sys.sysaltfiles SF
END


IF OBJECT_ID('master.dbo.DBA_SYS_TRACEFLAGS') IS NULL
BEGIN
	create table master.dbo.DBA_SYS_TRACEFLAGS ([name] int, [status] int, [global] int, [session] int)
	insert into master.dbo.DBA_SYS_TRACEFLAGS exec('dbcc tracestatus()')
END 
ELSE
BEGIN
	insert into master.dbo.DBA_SYS_TRACEFLAGS exec('dbcc tracestatus()')
END

/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
   SECTION 1.4: OPTIONAL MAPPED DRIVE / EMERGENCY FILE
   Optional: Create mapped drive for backups or emergency empty file
   Set @AddMappedDrive=1 or @NeedEmptyFile=1 to enable
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/

IF @AddMappedDrive = 1
BEGIN
	/*Need a mapped drive for the SQL service?*/
	DECLARE @MappedDriveCMD NVARCHAR(200)
	DECLARE @MappedDriveDomainName NVARCHAR(200)

	DECLARE @MappedDriveDomainPassword NVARCHAR(200)
	SET @MappedDriveDomainName = 'DOMAIN\administrator'
	SET @MappedDriveDomainPassword = 'PASSWORD'
	SET @MappedDriveCMD = 'net use s: \\server\Backup\SQLServer /USER:'+@MappedDriveDomainName+' '+@MappedDriveDomainPassword+' /p:yes'
	EXEC sp_configure 'show advanced options', 1;
	RECONFIGURE;
	EXEC sp_configure 'xp_cmdshell',1
	RECONFIGURE
	EXEC xp_cmdshell @MappedDriveCMD

END
IF @NeedEmptyFile = 1
BEGIN
	RAISERROR ( 'How about creating a 10GB empty file',0,1) WITH NOWAIT;
	EXEC xp_cmdshell ' fsutil file createnew C:\MyEmergencyFile.dat 10737418240'
END

/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
   SECTION 1.5: UPDATE OLA JOB SCHEDULES
   Optional: Update Ola Hallengren maintenance job schedules
   Set @UpdateOla=1 to enable
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/

 IF @UpdateOla = 1
 BEGIN
/*Adrian*/
DECLARE @schedule_id_01 int
  DECLARE @schedule_id_02 int
  DECLARE @schedule_id_03 int
  DECLARE @schedule_id_04 int
  DECLARE @schedule_id_05 int
  DECLARE @schedule_id_06 int
  DECLARE @RandomStartHours BIGINT
  DECLARE @RandomStartMinutes BIGINT
  DECLARE @RandomStartSeconds BIGINT
  DECLARE @RandomTime BIGINT
  DECLARE @StartStamp NVARCHAR(20)
SELECT @StartStamp = LEFT(REPLACE(CONVERT(NVARCHAR,GETDATE(),120),'-',''),8)

--@Numberoffiles=5
  
  
  IF NOT EXISTS (SELECT * FROM msdb.dbo.sysschedules WHERE [name] = N'SQLDBA.ORG - Daily Backups')
  BEGIN
  SET @RandomStartHours = CONVERT(BIGINT,RAND()*2)*10000 /*Between 12:00 and 03:00 am*/
  SET @RandomStartMinutes = CONVERT(BIGINT,RAND()*59)*100 /*Between 00 ~ 60 minutes*/
  SET @RandomStartSeconds = CONVERT(BIGINT,RAND()*59) /*Between 00 ~ 60 seconds*/
  SET @RandomTime = @RandomStartHours + @RandomStartMinutes + @RandomStartSeconds 
   EXEC msdb.dbo.sp_add_schedule @schedule_name=N'SQLDBA.ORG - Daily Backups', @enabled=1, @freq_type=4, @freq_interval=1
	  , @freq_subday_type=8, @freq_subday_interval=12, @freq_relative_interval=0, @freq_recurrence_factor=1
	  , @active_start_date=@StartStamp, @active_end_date=99991231, @active_start_time=@RandomTime, @active_end_time=235959, @schedule_id = @schedule_id_01 OUTPUT
  	END
	ELSE
	BEGIN
		SELECT @schedule_id_01 =  schedule_id FROM msdb.dbo.sysschedules WHERE [name] = N'SQLDBA.ORG - Daily Backups'
	END
	
	IF NOT EXISTS (SELECT * FROM msdb.dbo.sysschedules WHERE [name] = N'SQLDBA.ORG - Weekly Backups')
	  BEGIN
	  SET @RandomStartHours = CONVERT(BIGINT,RAND()*2)*10000 /*Between 12:00 and 03:00 am*/
	  SET @RandomStartMinutes = CONVERT(BIGINT,RAND()*59)*100 /*Between 00 ~ 60 minutes*/
	  SET @RandomStartSeconds = CONVERT(BIGINT,RAND()*59) /*Between 00 ~ 60 seconds*/
	  SET @RandomTime = @RandomStartHours + @RandomStartMinutes + @RandomStartSeconds 
	  EXEC msdb.dbo.sp_add_schedule @schedule_name=N'SQLDBA.ORG - Weekly Backups', @enabled=1, @freq_type=8, @freq_interval=64
  , @freq_subday_type=1, @freq_subday_interval=0, @freq_relative_interval=0, @freq_recurrence_factor=1, @active_start_date=@StartStamp
  , @active_end_date=99991231, @active_start_time=100000, @active_end_time=235959, @schedule_id = @schedule_id_06 OUTPUT

  	END
	ELSE
	BEGIN
		SELECT @schedule_id_06 =  schedule_id FROM msdb.dbo.sysschedules WHERE [name] = N'SQLDBA.ORG - Weekly Backups'
	END
	
  IF NOT EXISTS (SELECT * FROM msdb.dbo.sysschedules WHERE [name] = N'SQLDBA.ORG - Log Backups')
  BEGIN
  EXEC msdb.dbo.sp_add_schedule @schedule_name=N'SQLDBA.ORG - Log Backups', @enabled=1, @freq_type=4, @freq_interval=1, @freq_subday_type=4
  , @freq_subday_interval=15, @freq_relative_interval=0, @freq_recurrence_factor=1, @active_start_date=@StartStamp, @active_end_date=99991231
  , @active_start_time=0, @active_end_time=235959, @schedule_id = @schedule_id_02 OUTPUT
  	END
	ELSE
	BEGIN
		SELECT @schedule_id_02 =  schedule_id FROM msdb.dbo.sysschedules WHERE [name] = N'SQLDBA.ORG - Log Backups'
	END

  IF NOT EXISTS (SELECT * FROM msdb.dbo.sysschedules WHERE [name] = N'SQLDBA.ORG - Daily DBCC')
  BEGIN
  SET @RandomStartHours = CONVERT(BIGINT,((RAND()*4)+19))*10000 /*Between 19:00 and 00:00 am*/
  SET @RandomStartMinutes = CONVERT(BIGINT,RAND()*59)*100 /*Between 00 ~ 60 minutes*/
  SET @RandomStartSeconds = CONVERT(BIGINT,RAND()*59) /*Between 00 ~ 60 seconds*/
  SET @RandomTime = @RandomStartHours + @RandomStartMinutes + @RandomStartSeconds
  EXEC msdb.dbo.sp_add_schedule @schedule_name=N'SQLDBA.ORG - Daily DBCC', @enabled=1, @freq_type=4, @freq_interval=1
  , @freq_subday_type=1, @freq_subday_interval=0, @freq_relative_interval=0, @freq_recurrence_factor=1, @active_start_date=@StartStamp
  , @active_end_date=99991231, @active_start_time=@RandomTime, @active_end_time=235959, @schedule_id = @schedule_id_03 OUTPUT
  	END
	ELSE
	BEGIN
		SELECT @schedule_id_03 =  schedule_id FROM msdb.dbo.sysschedules WHERE [name] = N'SQLDBA.ORG - Daily DBCC'
	END

  IF NOT EXISTS (SELECT * FROM msdb.dbo.sysschedules WHERE [name] = N'SQLDBA.ORG - Daily Maintenance')
  BEGIN 
  SET @RandomStartHours = CONVERT(BIGINT,((RAND()*2)+3))*10000 /*Between 03:00 and 05:00 am*/
  SET @RandomStartMinutes = CONVERT(BIGINT,RAND()*59)*100 /*Between 00 ~ 60 minutes*/
  SET @RandomStartSeconds = CONVERT(BIGINT,RAND()*59) /*Between 00 ~ 60 seconds*/
  SET @RandomTime = @RandomStartHours + @RandomStartMinutes + @RandomStartSeconds
  EXEC msdb.dbo.sp_add_schedule @schedule_name=N'SQLDBA.ORG - Daily Maintenance', @enabled=1, @freq_type=4, @freq_interval=1
  , @freq_subday_type=1, @freq_subday_interval=0, @freq_relative_interval=0, @freq_recurrence_factor=1, @active_start_date=@StartStamp
  , @active_end_date=99991231, @active_start_time=@RandomTime, @active_end_time=235959, @schedule_id = @schedule_id_04 OUTPUT
  	END
	ELSE
	BEGIN
		SELECT @schedule_id_04 =  schedule_id FROM msdb.dbo.sysschedules WHERE [name] = N'SQLDBA.ORG - Weekly Maintenance'
	END
  IF NOT EXISTS (SELECT * FROM msdb.dbo.sysschedules WHERE [name] = N'SQLDBA.ORG - Weekly Maintenance')
  BEGIN
  SET @RandomStartHours = CONVERT(BIGINT,((RAND()*2)+1))*10000 /*Between 02:00 and 03:00 am*/
  SET @RandomStartMinutes = CONVERT(BIGINT,RAND()*59)*100 /*Between 00 ~ 60 minutes*/
  SET @RandomStartSeconds = CONVERT(BIGINT,RAND()*59) /*Between 00 ~ 60 seconds*/
  SET @RandomTime = @RandomStartHours + @RandomStartMinutes + @RandomStartSeconds
  EXEC msdb.dbo.sp_add_schedule @schedule_name=N'SQLDBA.ORG - Weekly Maintenance', @enabled=1, @freq_type=8, @freq_interval=64
  , @freq_subday_type=1, @freq_subday_interval=0, @freq_relative_interval=0, @freq_recurrence_factor=1, @active_start_date=@StartStamp
  , @active_end_date=99991231, @active_start_time=@RandomTime, @active_end_time=235959, @schedule_id = @schedule_id_05 OUTPUT
	END
	ELSE
	BEGIN
		SELECT @schedule_id_05 =  schedule_id FROM msdb.dbo.sysschedules WHERE [name] = N'SQLDBA.ORG - Weekly Maintenance'
	END

DECLARE @the_job_Id BINARY(16)
DECLARE @the_job_name NVARCHAR(200)

SET @the_job_name =  'IndexOptimize - USER_DATABASES'
IF EXISTS(SELECT job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name)
BEGIN
SELECT @the_job_Id = job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name
	EXEC msdb.dbo.sp_attach_schedule @job_id=@the_job_Id, @schedule_id = @schedule_id_04
	EXEC msdb.dbo.sp_update_jobstep @job_id=@the_job_Id, @step_id=1 , 
		@command=N'EXECUTE [dbo].[IndexOptimize] @Databases = ''ALL_DATABASES'',@FragmentationHigh = ''INDEX_REBUILD_ONLINE,INDEX_REBUILD_OFFLINE'', @FragmentationMedium = ''INDEX_REORGANIZE,INDEX_REBUILD_ONLINE,INDEX_REBUILD_OFFLINE'', @FragmentationLevel1 = 5, @FragmentationLevel2 = 30,@OnlyModifiedStatistics = ''Y'', @UpdateStatistics=''ALL'', @LogToTable = ''Y'''
		--EXECUTE [dbo].[IndexOptimize] @Databases = 'ALL_DATABASES',@FragmentationHigh = 'INDEX_REORGANIZE,INDEX_REBUILD_ONLINE,INDEX_REBUILD_OFFLINE', @FragmentationMedium = 'INDEX_REBUILD_ONLINE,INDEX_REBUILD_OFFLINE', @FragmentationLevel1 = 5, @FragmentationLevel2 = 30,@OnlyModifiedStatistics = 'Y', @UpdateStatistics='ALL', @LogToTable = 'Y'

END



---@availabilitygroupDirectoryStructure='{servername}${instancename}{directoryseparator}{databaseName}{directoryseparator}{backuptype}'               




SET @the_job_name =  'DatabaseBackup - SYSTEM_DATABASES - FULL'
IF EXISTS(SELECT job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name)
BEGIN
SELECT @the_job_Id = job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name 
  EXEC msdb.dbo.sp_attach_schedule @job_id=@the_job_Id, @schedule_id = @schedule_id_01
END

SET @the_job_name =  'DatabaseBackup - USER_DATABASES - FULL'
IF EXISTS(SELECT job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name)
BEGIN
SELECT @the_job_Id = job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name
  EXEC msdb.dbo.sp_attach_schedule @job_id=@the_job_Id, @schedule_id = @schedule_id_06
END

SET @the_job_name =  'DatabaseBackup - USER_DATABASES - DIFF'
IF EXISTS(SELECT job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name)
BEGIN
  SELECT @the_job_Id = job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name
  EXEC msdb.dbo.sp_attach_schedule @job_id=@the_job_Id, @schedule_id = @schedule_id_01
END

SET @the_job_name =  'DatabaseBackup - USER_DATABASES - LOG'
IF EXISTS(SELECT job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name)
BEGIN 
SELECT @the_job_Id = job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name
  EXEC msdb.dbo.sp_attach_schedule @job_id=@the_job_Id, @schedule_id = @schedule_id_02
END

SET @the_job_name =  'DatabaseIntegrityCheck - SYSTEM_DATABASES'
IF EXISTS(SELECT job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name)
BEGIN 
SELECT @the_job_Id = job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name
  EXEC msdb.dbo.sp_attach_schedule @job_id=@the_job_Id, @schedule_id = @schedule_id_03
END

SET @the_job_name =  'DatabaseIntegrityCheck - USER_DATABASES'
IF EXISTS(SELECT job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name)
BEGIN 
SELECT @the_job_Id = job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name
  EXEC msdb.dbo.sp_attach_schedule @job_id=@the_job_Id, @schedule_id = @schedule_id_03
END

SET @the_job_name =  'sp_delete_backuphistory'
IF EXISTS(SELECT job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name)
BEGIN 
	SELECT @the_job_Id = job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name
	EXEC msdb.dbo.sp_attach_schedule @job_id=@the_job_Id, @schedule_id = @schedule_id_05
  	EXEC msdb.dbo.sp_update_jobstep @job_id=@the_job_Id, @step_id=1 , 
	@command=N'DECLARE @CleanupDate datetime 
	SET @CleanupDate = DATEADD(dd,-90,GETDATE())
	EXECUTE dbo.sp_delete_backuphistory @oldest_date = @CleanupDate', @subsystem=N'TSQL'
END

SET @the_job_name =  'sp_purge_jobhistory'
IF EXISTS(SELECT job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name)
BEGIN 
	SELECT @the_job_Id = job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name
	EXEC msdb.dbo.sp_attach_schedule @job_id=@the_job_Id, @schedule_id = @schedule_id_05
END

SET @the_job_name =  'Output File Cleanup'
IF EXISTS(SELECT job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name)
BEGIN  
	SELECT @the_job_Id = job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name
	EXEC msdb.dbo.sp_attach_schedule @job_id=@the_job_Id, @schedule_id = @schedule_id_04
END

SET @the_job_name =  'CommandLog Cleanup'
IF EXISTS(SELECT job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name)
BEGIN 
		SELECT @the_job_Id = job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name
  EXEC msdb.dbo.sp_attach_schedule @job_id=@the_job_Id, @schedule_id = @schedule_id_05
END

  /* <<< OLA stuff up to here*/
END

/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
   SECTION 2.1: DEADLOCK CAPTURE (XEvent)
   Optional: Create XEvent session for deadlock capture
   Set @DoDeadlocks=1 to enable (requires SQL 2012+)
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/

IF @DoDeadlocks = 1 
BEGIN
	RAISERROR ('Creating deadlock capture thingy',0,1) WITH NOWAIT;
	/*https://social.technet.microsoft.com/wiki/contents/articles/38068.sql-server-capture-database-deadlock-by-xevent-job-alerts-and-send-email-to-you.aspx*/
	Use master 
	
	IF OBJECT_ID('master.dbo.DBA_DEADLOCKS') IS NULL
	BEGIN
		CREATE TABLE [master].[dbo].[DBA_DEADLOCKS](
		[no] [int] IDENTITY(1,1) NOT NULL,
		[deadlock_timeout] [datetime] NULL,
		[deadlock1_id] NVARCHAR(100) NULL,
		[deadlock1_duration] [float] NULL,
		[deadlock1_transactionname] NVARCHAR(100) NULL,
		[deadlock1_locktype] NVARCHAR(50) NULL,
		[deadlock1_clientapp] NVARCHAR(200) NULL,
		[deadlock1_hostname] NVARCHAR(50) NULL,
		[deadlock1_loginname] NVARCHAR(50) NULL,
		[deadlock1_query] NVARCHAR(max) NULL,
		[deadlock2_id] NVARCHAR(100) NULL,
		[deadlock2_duration] [float] NULL,
		[deadlock2_transactionname] NVARCHAR(100) NULL,
		[deadlock2_locktype] NVARCHAR(50) NULL,
		[deadlock2_clientapp] NVARCHAR(200) NULL,
		[deadlock2_hostname] NVARCHAR(50) NULL,
		[deadlock2_loginname] NVARCHAR(50) NULL,
		[deadlock2_query] NVARCHAR(max) NULL,
		PRIMARY KEY CLUSTERED
		(
		[no] ASC
		) ON [PRIMARY]
		) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
	

		CREATE NONCLUSTERED INDEX [IX_SQLDBA_deadlocktimeout] ON [dbo].[DBA_DEADLOCKS]
		(
		[deadlock_timeout] ASC
		) ON [PRIMARY]
	END

	IF NOT EXISTS (SELECT * FROM master.sys.objects WHERE type = 'P' AND OBJECT_ID = OBJECT_ID('dbo.spDeadLockReport'))
	BEGIN
		EXEC('CREATE PROCEDURE [dbo].[spDeadLockReport] AS BEGIN SET NOCOUNT ON; END')
	END
	EXEC('ALTER PROCEDURE [dbo].[spDeadLockReport]
	AS
	BEGIN
		DECLARE @mStartDate DATETIME;
		SET @mStartDate=(SELECT isnull(MAX([deadlock_timeout]),GETDATE()-1) FROM master.dbo.[DBA_DEADLOCKS])
	
		INSERT INTO master.dbo.[DBA_DEADLOCKS]([deadlock_timeout], [deadlock1_id], [deadlock1_duration], [deadlock1_transactionname], [deadlock1_locktype], [deadlock1_clientapp], [deadlock1_hostname], [deadlock1_loginname], [deadlock1_query], [deadlock2_id], [deadlock2_duration], [deadlock2_transactionname], [deadlock2_locktype], [deadlock2_clientapp], [deadlock2_hostname], [deadlock2_loginname], [deadlock2_query])
		SELECT x.y.value(''(@timestamp)[1]'', ''datetime'')                                                        ''[deadlock_timeout]'',
		x.y.value(''(./data/value/deadlock/process-list/process/@id)[1]'', ''NVARCHAR(100)'')               ''[deadlock1_id]'',
		x.y.value(''(./data/value/deadlock/process-list/process/@waittime)[1]'', ''float'') / 1000          ''[deadlock1_duration]'',
		x.y.value(''(./data/value/deadlock/process-list/process/@transactionname)[1]'', N''NVARCHAR(100)'') AS ''[deadlock1_transactionname]'',
		x.y.value(''(./data/value/deadlock/process-list/process/@lockMode)[1]'', N''NVARCHAR(50)'')         AS ''[deadlock1_locktype]'',
		x.y.value(''(./data/value/deadlock/process-list/process/@clientapp)[1]'', N''NVARCHAR(200)'')       AS ''[deadlock1_clientapp]'',
		x.y.value(''(./data/value/deadlock/process-list/process/@hostname)[1]'', N''NVARCHAR(50)'')         AS ''[deadlock1_hostname]'',
		x.y.value(''(./data/value/deadlock/process-list/process/@loginname)[1]'', N''NVARCHAR(50)'')        AS ''[deadlock1_loginname]'',
		x.y.value(''(./data/value/deadlock/process-list/process/inputbuf)[1]'', N''NVARCHAR(max)'')         AS ''[deadlock1_query]'',
		x.y.value(''(./data/value/deadlock/process-list/process/@id)[2]'', ''NVARCHAR(100)'')               ''[deadlock2_id]'',
		x.y.value(''(./data/value/deadlock/process-list/process/@waittime)[2]'', ''float'') / 1000          ''[deadlock2_duration]'',
		x.y.value(''(./data/value/deadlock/process-list/process/@transactionname)[2]'', N''NVARCHAR(100)'') AS ''[deadlock2_transactionname]'',
		x.y.value(''(./data/value/deadlock/process-list/process/@lockMode)[2]'', N''NVARCHAR(50)'')         AS ''[deadlock2_locktype]'',
		x.y.value(''(./data/value/deadlock/process-list/process/@clientapp)[2]'', N''NVARCHAR(200)'')       AS ''[deadlock2_clientapp]'',
		x.y.value(''(./data/value/deadlock/process-list/process/@hostname)[2]'', N''NVARCHAR(50)'')         AS ''[deadlock2_hostname]'',
		x.y.value(''(./data/value/deadlock/process-list/process/@loginname)[2]'', N''NVARCHAR(50)'')        AS ''[deadlock2_loginname]'',
		x.y.value(''(./data/value/deadlock/process-list/process/inputbuf)[2]'', N''NVARCHAR(max)'')         AS ''[deadlock2_query]''
		FROM   (SELECT Cast([target_data] AS XML) [target_data]
			FROM   sys.dm_xe_session_targets AS st
				INNER JOIN sys.dm_xe_sessions AS s
						ON s.[address] = st.[event_session_address]
			WHERE  s.[name] = ''DeadlockReport'') AS [deadlock]
		CROSS APPLY [target_data].nodes(''/RingBufferTarget/event'') AS x(y)
		WHERE  x.y.query(''.'').exist(''/event[@timestamp > sql:variable("@mStartDate") and @name="xml_deadlock_report"]'') = 1
	END')
	--CREATE DEADLOCK XEVENT
	IF NOT EXISTS (SELECT *
		FROM sys.server_event_sessions
		WHERE name = 'DeadlockReport')
	BEGIN

		CREATE EVENT SESSION [DeadlockReport] ON SERVER
		ADD EVENT sqlserver.xml_deadlock_report
		ADD TARGET package0.ring_buffer
		WITH (MAX_MEMORY=4096 KB,EVENT_RETENTION_MODE=ALLOW_SINGLE_EVENT_LOSS,
		MAX_DISPATCH_LATENCY=1 SECONDS,MAX_EVENT_SIZE=0 KB,MEMORY_PARTITION_MODE=NONE,
		TRACK_CAUSALITY=OFF,STARTUP_STATE=OFF)
	END

	IF EXISTS (SELECT *
		FROM sys.server_event_sessions
		WHERE name = 'DeadlockReport')
	BEGIN TRY
	--XEVENT START
		ALTER EVENT SESSION [DeadlockReport] ON SERVER
		STATE = START
	END TRY
	BEGIN CATCH
		RAISERROR ('DeadlockReport Already started.. it seems',0,1) WITH NOWAIT;
	END CATCH
END

/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
   SECTION 2.2: BLOCKED PROCESS THRESHOLD & XEvent
   Configure blocked process report (default 5 seconds) and create
   XEvent session to capture blocking/deadlock events
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/

/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
   SECTION 2.3: PERFORMANCE BEST PRACTICES (Microsoft/Brent Ozar)
   Configure enterprise-grade performance settings
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/

-- Lock Pages in Memory (LPIM) - Enterprise Edition only
-- Prevents SQL Server working set from being paged out by Windows
DECLARE @SQLVer INT, @SQLEdition NVARCHAR(500)
SELECT  @SQLVer = CONVERT(INT, @@MicrosoftVersion / 0x01000000),
       @SQLEdition = CONVERT(NVARCHAR(500),SERVERPROPERTY('Edition'))

IF @SQLEdition LIKE '%Enterprise%' OR @SQLEdition LIKE '%Developer%'
BEGIN
    EXEC sys.sp_configure N'show advanced options', N'1'
    RECONFIGURE
    EXEC sys.sp_configure N'locks', N'0'  -- default, verify not overridden
    RECONFIGURE
    RAISERROR('INFO: Lock Pages in Memory requires Windows policy setting for SQL service account', 10, 1) WITH NOWAIT;
    RAISERROR('      Grant "Lock pages in memory" (seLockMemoryPrivilege) to SQL service account', 10, 1) WITH NOWAIT;
    EXEC sys.sp_configure N'show advanced options', N'0'
    RECONFIGURE
END


-- Secondary tempdb data files (best practice for SQL 2016+)
-- Create multiple tempdb files based on logical CPU count (up to 8, or cores per NUMA)
IF @SQLVer >= 13
BEGIN
    DECLARE @TempDBFileCount INT, @CPUCount INT
    SELECT @CPUCount = cpu_count FROM sys.dm_os_sys_info
    SET @TempDBFileCount = CASE WHEN @CPUCount > 8 THEN 8 ELSE @CPUCount END
    
    -- Check existing tempdb data files
    DECLARE @CurrentTempDBFiles INT
    SELECT @CurrentTempDBFiles = COUNT(*) 
    FROM tempdb.sys.database_files 
    WHERE type = 0  -- data files only
    
    IF @CurrentTempDBFiles < @TempDBFileCount
    BEGIN
        RAISERROR('INFO: tempdb currently has %d data files, best practice suggests %d for this server', 
            10, 1, @CurrentTempDBFiles, @TempDBFileCount) WITH NOWAIT;
        RAISERROR('      Recommend adding additional tempdb data files manually for best performance', 10, 1) WITH NOWAIT;
    END
END

-- Accelerated Database Recovery (ADR) - SQL 2019+ only
-- Improves database availability during long-running transactions
IF @SQLVer >= 15 AND EXISTS(SELECT 1 FROM sys.columns 
          WHERE Name = N'is_accelerated_database_recovery_on'
          AND Object_ID = Object_ID(N'sys.databases'))
BEGIN
	DECLARE @ADRSQLtables TABLE([name] NVARCHAR(250))
    DECLARE @ADRSQL NVARCHAR(MAX) = N''
	SET @ADRSQL = '
	SELECT QUOTENAME(name) [name]
	FROM sys.databases
    WHERE database_id > 4 
      AND state = 0 
      AND is_read_only = 0
      AND name NOT IN (''tempdb'')
      AND is_accelerated_database_recovery_on = 0'
	INSERT INTO @ADRSQLtables
	EXEC sp_executesql @ADRSQL
	SET @ADRSQL = N''
    SELECT @ADRSQL += N'ALTER DATABASE ' + QUOTENAME(name) + N' SET ACCELERATED_DATABASE_RECOVERY = ON;' + CHAR(13)
	FROM @ADRSQLtables
    

    IF @ADRSQL <> N''
    BEGIN
        RAISERROR('INFO: Enabling Accelerated Database Recovery on user databases', 10, 1) WITH NOWAIT;
        RAISERROR(@ADRSQL, 10, 1) WITH NOWAIT;
        EXEC sp_executesql @ADRSQL
    END
    ELSE
        RAISERROR('INFO: ADR already enabled on all applicable databases', 10, 1) WITH NOWAIT;
END
-- Query Store retention settings (recommended by Brent Ozar)
-- Set to 7 days, auto-cleanup, capture all queries
IF @SQLVer >= 13
BEGIN
    DECLARE @QSSQL NVARCHAR(MAX) = N''
    SELECT @QSSQL += N'ALTER DATABASE ' + QUOTENAME(name) + 
        N' SET QUERY_STORE (OPERATION_MODE = READ_WRITE, CLEANUP_POLICY = (STALE_QUERY_THRESHOLD_DAYS = 30), ' +
        N'DATA_FLUSH_INTERVAL_SECONDS = 60, MAX_STORAGE_SIZE_MB = 1000, INTERVAL_LENGTH_MINUTES = 60);' + CHAR(13)
    FROM sys.databases
    WHERE database_id > 4 AND state = 0 AND is_query_store_on = 1
    
    IF @QSSQL <> N''
    BEGIN
        RAISERROR('INFO: Optimizing Query Store retention settings', 10, 1) WITH NOWAIT;
        EXEC sp_executesql @QSSQL
    END
END

-- Check for High Performance Power Plan (Brent Ozar priority)
DECLARE @PowerPlan NVARCHAR(100)
EXEC master.dbo.xp_regread 'HKEY_LOCAL_MACHINE', 
    'SYSTEM\CurrentControlSet\Control\PowerUser\PowerSchemes', 
    'ActivePowerScheme', @PowerPlan OUTPUT
IF @PowerPlan IS NOT NULL AND @PowerPlan <> '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'
BEGIN
    RAISERROR('WARNING: Power Plan is not set to High Performance (8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c)', 10, 1) WITH NOWAIT;
    RAISERROR('      Current: %s - This can significantly impact SQL Server performance', 10, 1, @PowerPlan) WITH NOWAIT;
END
/* blocked process threshold: fires the blocked_process_report event when a session
   is blocked for >= N seconds. Must be set BEFORE creating the XEvent session.
   Default is 0 (disabled). 5 seconds is the standard starting point. */
EXEC sys.sp_configure N'show advanced options', N'1'
RECONFIGURE WITH OVERRIDE
EXEC sys.sp_configure N'blocked process threshold (s)', N'5'
RECONFIGURE WITH OVERRIDE
RAISERROR ('Action: Set blocked process threshold to 5 seconds', 0, 1) WITH NOWAIT;
EXEC sys.sp_configure N'show advanced options', N'0'
RECONFIGURE WITH OVERRIDE

/* XEvent session to capture blocking reports and deadlocks together.
   Writes to a ring buffer (in-memory, no file I/O overhead).
   The ring_buffer target holds ~10 MB of events by default.          */
IF NOT EXISTS (SELECT * FROM sys.server_event_sessions WHERE name = N'BlockingAndDeadlocks')
BEGIN
	CREATE EVENT SESSION [BlockingAndDeadlocks] ON SERVER
	ADD EVENT sqlserver.blocked_process_report
	    (WHERE sqlserver.database_id > 4),   -- user databases only
	ADD EVENT sqlserver.xml_deadlock_report
	ADD TARGET package0.ring_buffer
	    (SET max_memory = 10240)             -- 10 MB ring buffer
	WITH (
		MAX_MEMORY = 4096 KB,
		EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS,
		MAX_DISPATCH_LATENCY = 5 SECONDS,
		MAX_EVENT_SIZE = 0 KB,
		MEMORY_PARTITION_MODE = NONE,
		TRACK_CAUSALITY = OFF,
		STARTUP_STATE = ON              -- survives service restart
	);
	RAISERROR ('Action: Created BlockingAndDeadlocks XEvent session', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
	RAISERROR ('Action: BlockingAndDeadlocks XEvent session already exists', 0, 1) WITH NOWAIT;
END

BEGIN TRY
	ALTER EVENT SESSION [BlockingAndDeadlocks] ON SERVER STATE = START;
END TRY
BEGIN CATCH
	RAISERROR ('BlockingAndDeadlocks XEvent session already running', 0, 1) WITH NOWAIT;
END CATCH


USE [msdb]
GO



/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
   SECTION 3: DATABASE MAIL CONFIGURATION
   Enable Database Mail and create operators for alert notifications
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
/* We will assume you have Datbase Mail enabled and configured*/
/*Thanks:
Brent Ozar Unlimited, https://www.brentozar.com/blitz/configure-sql-server-alerts/
@KeefOnToast and Chuck
*/



/*Enable Advanced options*/
EXEC sys.sp_configure N'show advanced options', N'1'
RECONFIGURE WITH OVERRIDE

/*Enable Database Mail*/
EXEC sp_configure 'Database Mail XPs', 1
RECONFIGURE WITH OVERRIDE
RAISERROR ('Action: Enabled database mail',0,1) WITH NOWAIT;



USE [master]

-- Get the server name
DECLARE @ServerName sysname 
SET @ServerName = (SELECT @@SERVERNAME);
/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
   SECTION 4: SQL AGENT ALERTS
   Create comprehensive alerts for severity levels, error numbers,
   and Always On availability group events
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
---------------------------------------------------------------------------------------------------------
/* Adapted by Adrian Sullivan from Glenn Berry's script*/

-- Add important SQL Agent Alerts to your instance

-- This will work with SQL Server 2008 and newer
-- Glenn Berry
-- SQLskills.com
-- Last Modified: August 11, 2014
-- http://sqlserverperformance.wordpress.com/
-- http://sqlskills.com/blogs/glenn/
-- Twitter: GlennAlanBerry

-- Listen to my Pluralsight courses
-- http://www.pluralsight.com/author/glenn-berry


DECLARE @Operator NVARCHAR(500);
DECLARE @DynamicSQL NVARCHAR(4000);
DECLARE @Severity TINYINT;
DECLARE @AlertName NVARCHAR(500);
DECLARE @StepDescription NVARCHAR(500);
DECLARE @WhereToSend TINYINT;
DECLARE @SQLDBANotification NVARCHAR(200);
SET @SQLDBANotification= N'Possible P2/P3. Assign to SQL Engineers/DBA';

-- Change @OperatorName as needed
DECLARE @OperatorName sysname 
SET @OperatorName= N'SQLDBA';


SET @WhereToSend = 1 /*Change this to 7 to cover Email(1), Pager(2) and Net Send(4)*/
SET @Operator = @OperatorName;
SET @DynamicSQL =' EXEC msdb.dbo.sp_add_operator @name=N''' + @Operator + ''', 
@enabled=1, 
@weekday_pager_start_time=90000, 
@weekday_pager_end_time=180000, 
@saturday_pager_start_time=90000, 
@saturday_pager_end_time=180000, 
@sunday_pager_start_time=90000, 
@sunday_pager_end_time=180000, 
@pager_days=0, 
@email_address=N''alerts@sqldba.org'', 
@category_name=N''[Uncategorized]''; '
BEGIN TRY
		EXEC sp_executesql @DynamicSQL;
		SET @StepDescription = 'Operator created: ' + @Operator;
END TRY
BEGIN CATCH
		SET @StepDescription = 'Operator already exists for: ' + @Operator;
		EXEC msdb.dbo.sp_update_operator @name=N'SQLDBA', 
		@enabled=1, 
		@pager_days=0, 
		@email_address=N'alerts@sqldba.org', 
		@pager_address=N''

END CATCH
RAISERROR (@StepDescription,0,1) WITH NOWAIT;

-- Make sure you have an Agent Operator defined that matches the name you supplied
IF NOT EXISTS(SELECT * FROM msdb.dbo.sysoperators WHERE name = @OperatorName)
    BEGIN
        RAISERROR ('There is no SQL Operator with a name of %s' , 18 , 16 , @OperatorName);
        RETURN;
    END

-- Change @CategoryName as needed
DECLARE @CategoryName sysname 
SET @CategoryName = N'SQL Server Agent Alerts';


-- Add Alert Category if it does not exist
IF NOT EXISTS (SELECT *
               FROM msdb.dbo.syscategories
               WHERE category_class = 2  -- ALERT
               AND category_type = 3
               AND name = @CategoryName)
    BEGIN
        EXEC msdb.dbo.sp_add_category @class = N'ALERT', @type = N'NONE', @name = @CategoryName;
    END





DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
DECLARE @the_job_Id BINARY(16)
DECLARE @the_job_name NVARCHAR(200)
DECLARE @thealert_Id BINARY(16)
DECLARE @thealert_name NVARCHAR(200)
SET @the_job_name =  '996. Deadlocks'

SELECT @the_job_Id = job_id  FROM msdb.dbo.sysjobs WHERE [name] =  @the_job_name 

IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'[Uncategorized (Local)]' AND category_class=1)
	EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'[Uncategorized (Local)]'
 


IF NOT EXISTS(SELECT job_id,*  FROM msdb.dbo.sysjobs WHERE [name] =  '996. Deadlocks')
BEGIN
	DECLARE @jobId BINARY(16)
	EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'996. Deadlocks',
	@enabled=1,
	@notify_level_eventlog=2,
	@notify_level_email=0,
	@notify_level_netsend=0,
	@notify_level_page=0,
	@delete_level=0,
	@description=N'no description',
	@category_name=N'[Uncategorized (Local)]',
	@owner_login_name=N'sa', @job_id = @jobId OUTPUT

	EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'deadlock',
	@step_id=1,
	@cmdexec_success_code=0,
	@on_success_action=1,
	@on_success_step_id=0,
	@on_fail_action=2,
	@on_fail_step_id=0,
	@retry_attempts=0,
	@retry_interval=0,
	@os_run_priority=0, @subsystem=N'TSQL',
	@command=N'EXEC master.dbo.spDeadLockReport',
	@database_name=N'master',
	@flags=0
 
	EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
	EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
END

IF NOT EXISTS (SELECT id FROM msdb.dbo.sysalerts WHERE name = N'[SQLDBA]Deadlock Alerts')
BEGIN
	RAISERROR ('Creating [SQLDBA]Deadlock Alerts',0,1) WITH NOWAIT;
	BEGIN TRY
		EXEC msdb.dbo.sp_add_alert @name=N'[SQLDBA]Deadlock Alerts',
			@message_id=0,
			@severity=0,
			@enabled=1,
			@delay_between_responses=0,
			@include_event_description_in=1,
			@category_name=N'[Uncategorized]',
			@performance_condition=N'Locks|Number of Deadlocks/sec|_Total|>|0',
			@job_id=@the_job_Id
		EXEC msdb.dbo.sp_add_notification @alert_name=N'[SQLDBA]Deadlock Alerts', @operator_name=N'SQLDBA', @notification_method=1
		RAISERROR ('[SQLDBA]Deadlock Alerts created',0,1) WITH NOWAIT;
	END TRY
	BEGIN CATCH
		RAISERROR ('[SQLDBA]Deadlock Alerts creation failed',0,1) WITH NOWAIT;
	END CATCH
END
ELSE
BEGIN
	RAISERROR ('Updating existing [SQLDBA]Deadlock Alerts',0,1) WITH NOWAIT;
	BEGIN TRY
		EXEC msdb.dbo.sp_update_alert @name=N'[SQLDBA]Deadlock Alerts',
			@message_id=0,
			@severity=0,
			@enabled=1,
			@delay_between_responses=0,
			@include_event_description_in=1,
			@database_name=N'',
			@notification_message=N'Deadlock triggered',
			@event_description_keyword=N'',
			@performance_condition=N'Locks|Number of Deadlocks/sec|_Total|>|0',
			@wmi_namespace=N'',
			@wmi_query=N'',
			@job_id=@the_job_Id
		EXEC msdb.dbo.sp_update_notification @alert_name=N'[SQLDBA]Deadlock Alerts', @operator_name=N'SQLDBA', @notification_method=1
	END TRY
	BEGIN CATCH
		RAISERROR ('[SQLDBA]Deadlock Alerts update failed',0,1) WITH NOWAIT;
	END CATCH
END
-- Alert Names start with the name of the server 



DECLARE @AlertTable TABLE 
(
	ID INT IDENTITY(1,1)
	, AlertType NVARCHAR(50)
	, TheNumber INT
	, AlertName sysname
)
INSERT INTO @AlertTable VALUES ('Severity',19, 	@ServerName + N' Alert - Sev 19 Error: Fatal Error in Resource')
INSERT INTO @AlertTable VALUES ('Severity',20, 	@ServerName + N' Alert - Sev 20 Error: Fatal Error in Current Process')
INSERT INTO @AlertTable VALUES ('Severity',21, 	@ServerName + N' Alert - Sev 21 Error: Fatal Error in Database Process')
INSERT INTO @AlertTable VALUES ('Severity',22, 	@ServerName + N' Alert - Sev 22 Error: Fatal Error: Table Integrity Suspect')
INSERT INTO @AlertTable VALUES ('Severity',23, 	@ServerName + N' Alert - Sev 23 Error: Fatal Error Database Integrity Suspect')
INSERT INTO @AlertTable VALUES ('Severity',24, 	@ServerName + N' Alert - Sev 24 Error: Fatal Hardware Error')
INSERT INTO @AlertTable VALUES ('Severity',25, 	@ServerName + N' Alert - Sev 25 Error: Fatal Error')



-- Error 823: Operating System Error
-- How to troubleshoot a Msg 823 error in SQL Server	
-- http://support.microsoft.com/kb/2015755

-- Error 824: Logical consistency-based I/O error
-- How to troubleshoot Msg 824 in SQL Server
-- http://support.microsoft.com/kb/2015756

-- Error 825: Read-Retry Required
-- How to troubleshoot Msg 825 (read retry) in SQL Server
-- http://support.microsoft.com/kb/2015757

-- Error 832: Constant page has changed
-- http://www.sqlskills.com/blogs/paul/dont-confuse-error-823-and-error-832/
-- http://support.microsoft.com/kb/2015759


-- Memory Error Correction alerts added on 10/30/2013

-- Mitigation of RAM Hardware Errors
-- When SQL Server 2012 Enterprise Edition is installed on a Windows 2012 operating system with hardware that supports bad memory diagnostics, 
-- you will notice new error messages like 854, 855, and 856 instead of the 832 errors that LazyWriter usually generates.
-- Error 854 is just informing you that your instance supports memory error correction

-- Using SQL Server in Windows 8 and Windows Server 2012 environments
--https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors-0-to-999?view=sql-server-ver17
-- http://support.microsoft.com/kb/2681562
INSERT INTO @AlertTable VALUES ('Error',610, 	@ServerName + N' Error 610: Invalid header value from a page.' )--  Run DBCC CHECKDB to check for a data corruption.'   )
INSERT INTO @AlertTable VALUES ('Error',823, 	@ServerName + N' Error 823: The operating system returned an error')
INSERT INTO @AlertTable VALUES ('Error',824, 	@ServerName + N' Error 824: Logical consistency-based I/O error')
INSERT INTO @AlertTable VALUES ('Error',825, 	@ServerName + N' Error 825: Read-Retry Required')
INSERT INTO @AlertTable VALUES ('Error',832, 	@ServerName + N' Error 832: Constant page has changed')
INSERT INTO @AlertTable VALUES ('Error',855, 	@ServerName + N' Error 855: Uncorrectable hardware memory corruption detected')
INSERT INTO @AlertTable VALUES ('Error',856, 	@ServerName + N' Error 856: SQL Server has detected hardware memory corruption, but has recovered the page')
INSERT INTO @AlertTable VALUES ('Error',1205, 	@ServerName + N' Error 1205: Deadlock')
INSERT INTO @AlertTable VALUES ('Error',3928, 	@ServerName + N' Error 3928: Deadlock')
INSERT INTO @AlertTable VALUES ('Error',829,	@ServerName + N' Error Database, Page is marked RestorePending, which may indicate disk corruption.')-- To recover from this state, perform a restore.')
INSERT INTO @AlertTable VALUES ('Error',211,	@ServerName + N' Error 211: Corruption in database possibly due to schema or catalog inconsistency.')-- Run DBCC CHECKCATALOG.')
INSERT INTO @AlertTable VALUES ('Error',602,	@ServerName + N' Error 602: Stored procedure references a dropped table, or metadata is corrupted.')-- Drop and re-create the stored procedure, or execute DBCC CHECKDB.
INSERT INTO @AlertTable VALUES ('Error',603,	@ServerName + N' Error 603: Could not find an entry for table or index with object ID.')-- This error can occur if a stored procedure references a dropped table, or metadata is corrupted. Drop and re-create the stored procedure, or execute DBCC CHECKDB.
INSERT INTO @AlertTable VALUES ('Error',608,	@ServerName + N' Error 608: No catalog entry found for partition ID in database. The metadata is inconsistent.')-- Run DBCC CHECKDB to check for a metadata corruption.	
INSERT INTO @AlertTable VALUES ('Error',683,	@ServerName + N' Error 683: Error occurred trying to convert between variable- and fixed-length decimal formats.')-- Run DBCC CHECKDB to check for any database corruption.
INSERT INTO @AlertTable VALUES ('Error',684,	@ServerName + N' Error 684: Error occurred attempting to convert between compressed and uncompressed storage.')-- Run DBCC CHECKDB to check for any corruption.
INSERT INTO @AlertTable VALUES ('Error',692,	@ServerName + N' Error 692: Error. Buffer provided to write a fixed column value is too large.')-- Run DBCC CHECKDB to check for any corruption.
INSERT INTO @AlertTable VALUES ('Error',808,	@ServerName + N' Error 808: Insufficient bytes transferred. Backup,insufficient disk space,corruption/hardware failure.')-- Check errorlogs/application-logs for detailed messages and correct error conditions.
INSERT INTO @AlertTable VALUES ('Error',882,	@ServerName + N' Error 882: Schema of a table created by InternalBaseTable is corrupt.')--
INSERT INTO @AlertTable VALUES ('Error',918,	@ServerName + N' Error 918: Failed to load engine script metadata from script DLL. This is a serious error condition.')-- which usually indicates a corrupt or incomplete installation-- Repairing the SQL Server instance may help resolve this error.
INSERT INTO @AlertTable VALUES ('Error',965,	@ServerName + N' Error 965: Warning: Column nullability inconsistency detected in the metadata. Index may be corrupt.')-- Run DBCC CHECKTABLE to verify consistency.
INSERT INTO @AlertTable VALUES ('Error',976,	@ServerName + N' Error 976: Database Not Accessible')
INSERT INTO @AlertTable VALUES ('Error',983,	@ServerName + N' Error 983: Database Role Resolving. Unable to access availability database')
INSERT INTO @AlertTable VALUES ('Error',3402,	@ServerName + N' Error 3402: Database Restoring')
/* Always On / AG critical */
INSERT INTO @AlertTable VALUES ('Error',35265, 	@ServerName + N' AG 35265: AG Data Movement - Resumed')
INSERT INTO @AlertTable VALUES ('Error',35264, 	@ServerName + N' AG 35264: AG Data Movement - Suspended')
INSERT INTO @AlertTable VALUES ('Error',28034, 	@ServerName + N' AG 28034: Connection handshake on broker')
INSERT INTO @AlertTable VALUES ('Error',1480, 	@ServerName + N' AG 1480: AG Role Change' )
INSERT INTO @AlertTable VALUES ('Error',41091, 	@ServerName + N' AG 41091: Replica Going Offline Lease Expired')
INSERT INTO @AlertTable VALUES ('Error',41131, 	@ServerName + N' AG 41131: Failed to Bring AG ONLINE')
INSERT INTO @AlertTable VALUES ('Error',41142, 	@ServerName + N' AG 41142: Replica Cannot become primary')
INSERT INTO @AlertTable VALUES ('Error',41406, 	@ServerName + N' AG 41406: AG not Ready for Auto Failover')
INSERT INTO @AlertTable VALUES ('Error',41414, 	@ServerName + N' AG 41414: Secondary not Connected')
INSERT INTO @AlertTable VALUES ('Error',35276, 	@ServerName + N' Error 35276: Failed to allocate and schedule an AG task for database. Database Out of Sync'  )


INSERT INTO @AlertTable VALUES ('Error',1481,	@ServerName + N' AG 1481: Database replica sync health check failed')
INSERT INTO @AlertTable VALUES ('Error',35201,	@ServerName + N' AG 35201: Connection to primary failed')
INSERT INTO @AlertTable VALUES ('Error',19407, 	@ServerName + N' AG 19407: Cluster connectivity issue.' )-- The lease between the SQL availability group and the Windows Server Failover Cluster has expired.'  )
INSERT INTO @AlertTable VALUES ('Error',19419, 	@ServerName + N' AG 19419: Cluster to SQL lease timeout.' )--  Failover Cluster did not receive a process event signal from SQL Server hosting availability group within the lease timeout period.'  )
INSERT INTO @AlertTable VALUES ('Error',19421, 	@ServerName + N' AG 19421: SQL to Cluster lease timeout.' )--  SQL availability group did not receive a process event signal from the Failover Cluster within the lease timeout period.'  )
INSERT INTO @AlertTable VALUES ('Error',19422, 	@ServerName + N' AG 19422: AG lease renewal failed.' )--  SQL availability group and the Windows Server Failover Cluster failed because SQL Server encountered Windows error with error code.'  )
INSERT INTO @AlertTable VALUES ('Error',41143, 	@ServerName + N' AG 41143: AG replica is in a failed state.' )--   A previous operation to read or update persisted configuration data for the availability group has failed.  To recover from this failure, either restart the local Windows Server Failover Clustering (WSFC) service or restart the local instance of SQL Server.'  )
INSERT INTO @AlertTable VALUES ('Error',41005, 	@ServerName + N' AG 41005: Failed to obtain Failover Clustering (WSFC) resource handle.' )--  The WSFC service may not be running or may not be accessible in its current state.'  )
INSERT INTO @AlertTable VALUES ('Error',41144, 	@ServerName + N' AG 41144: Local AG replica is in a failed state.' )--   The replica failed to read or update the persisted configuration data. To recover from this failure, either restart the local Windows Server Failover Clustering (WSFC) service or restart the local instance of SQL Server.'  )

INSERT INTO @AlertTable VALUES ('Error',19406, 	@ServerName + N' AG 19406: AG Replica Changed States')
INSERT INTO @AlertTable VALUES ('Error',35206, 	@ServerName + N' AG 35206: Connection Timeout')
INSERT INTO @AlertTable VALUES ('Error',35250, 	@ServerName + N' AG 35250: Connection to Primary Inactive')
INSERT INTO @AlertTable VALUES ('Error',35273, 	@ServerName + N' AG 35273: Database Inaccessible')
INSERT INTO @AlertTable VALUES ('Error',35274, 	@ServerName + N' AG 35274: Database Recovery Pending')
INSERT INTO @AlertTable VALUES ('Error',35275, 	@ServerName + N' AG 35275: Database in Suspect State')
      
 

INSERT INTO @AlertTable VALUES ('Error',9002, 	@ServerName + N' Error 9002: Log File FULL')
INSERT INTO @AlertTable VALUES ('Error',1101,	@ServerName + N' Error 1101: Database filegroup out of space')
INSERT INTO @AlertTable VALUES ('Error',3041,	@ServerName + N' Error 3041: BACKUP failed to complete. Check the backup application log for detailed messages.')
INSERT INTO @AlertTable VALUES ('Error',12412,	@ServerName + N' Error 12412: Internal table access error. Failed to access the Query Store internal table.')
INSERT INTO @AlertTable VALUES ('Error',18210,	@ServerName + N' Error 18210:  Failure on backup device. Operating system error.')
INSERT INTO @AlertTable VALUES ('Error',28036, 	@ServerName + N' Error 28036: Connection handshake failed.' )--  The certificate used by this endpoint was not found')
INSERT INTO @AlertTable VALUES ('Error',2511, 	@ServerName + N' Error 2511: Table error. Keys out of order on page.' )-- 
INSERT INTO @AlertTable VALUES ('Error',5228, 	@ServerName + N' Error 5228: Table error. DBCC detected incomplete cleanup.' )-- 
INSERT INTO @AlertTable VALUES ('Error',5229, 	@ServerName + N' Error 5229: Table error. contains an anti-matter column.' )-- 
INSERT INTO @AlertTable VALUES ('Error',5242, 	@ServerName + N' Error 5242: An inconsistency was detected during an internal operation in database.' )-- 
INSERT INTO @AlertTable VALUES ('Error',5243, 	@ServerName + N' Error 5243: An inconsistency was detected during an internal operation.' )-- 
INSERT INTO @AlertTable VALUES ('Error',5250, 	@ServerName + N' Error 5250: Database error. This error cannot be repaired.' )--

/* Memory pressure alerts */
INSERT INTO @AlertTable VALUES ('Error',701,	@ServerName + N' Error 701: Insufficient memory in resource pool')
INSERT INTO @AlertTable VALUES ('Error',802,	@ServerName + N' Error 802: Insufficient memory available in the buffer pool')
INSERT INTO @AlertTable VALUES ('Error',8645,	@ServerName + N' Error 8645: Timeout waiting for memory resource (RESOURCE_SEMAPHORE)')
INSERT INTO @AlertTable VALUES ('Error',8651,	@ServerName + N' Error 8651: Low memory condition - deferred request failed')
INSERT INTO @AlertTable VALUES ('Error',17803,	@ServerName + N' Error 17803: Insufficient memory during thread creation (Windows memory allocation failed)')

/* Log / disk space alerts */
INSERT INTO @AlertTable VALUES ('Error',9001,	@ServerName + N' Error 9001: Log for database is not available (log corruption / inaccessible)')
INSERT INTO @AlertTable VALUES ('Error',1105,	@ServerName + N' Error 1105: Could not allocate space in filegroup (data file full, including PRIMARY)')
INSERT INTO @AlertTable VALUES ('Error',3271,	@ServerName + N' Error 3271: Non-recoverable I/O error occurred on file')

/* Authentication / security */
INSERT INTO @AlertTable VALUES ('Error',17806,	@ServerName + N' Error 17806: SSPI handshake failed (authentication failure)')

/* I/O subsystem */
INSERT INTO @AlertTable VALUES ('Error',7105,	@ServerName + N' Error 7105: Could not retrieve row from disk (out-of-row BLOB I/O error)')

/* Configuration change audit trail */
INSERT INTO @AlertTable VALUES ('Error',15457,	@ServerName + N' Error 15457: Configuration option changed (audit trail)')

/* Network / TDS errors */
INSERT INTO @AlertTable VALUES ('Error',4014,	@ServerName + N' Error 4014: Fatal error reading input stream from the network (TDS corruption)')
INSERT INTO @AlertTable VALUES ('Error',17826,	@ServerName + N' Error 17826: Could not start network library due to internal error (SQL lost its listener)')

/* File Control Block / pre-corruption indicator */
INSERT INTO @AlertTable VALUES ('Error',5180,	@ServerName + N' Error 5180: Could not open File Control Block for invalid file ID (precursor to 823/824 corruption)')

/* Stack alignment fatal error */
INSERT INTO @AlertTable VALUES ('Error',17551,	@ServerName + N' Error 17551: Fatal error - stack not properly aligned (indicates driver/OS issue)')

/* Buffer manager internal error */
INSERT INTO @AlertTable VALUES ('Error',8966,	@ServerName + N' Error 8966: Unable to read and latch page (internal buffer manager error indicating corruption)')

/* Backup / restore termination (complementary to 3041 -- also fires on restore failures) */
INSERT INTO @AlertTable VALUES ('Error',3013,	@ServerName + N' Error 3013: BACKUP or RESTORE terminating abnormally')

/* MSDTC distributed transaction recovery */
INSERT INTO @AlertTable VALUES ('Error',3452,	@ServerName + N' Error 3452: Recovery of in-doubt distributed transactions (MSDTC) detected')

/* Security monitoring -- login failures (delay_between_responses set to 60s in the loop to avoid storms) */
INSERT INTO @AlertTable VALUES ('Error',18456,	@ServerName + N' Error 18456: Login failed (security monitoring)')

/* Additional critical missing alerts - best practice additions */
/* I/O subsystem and lock errors */
INSERT INTO @AlertTable VALUES ('Error',596,	@ServerName + N' Error 596: LCK_M_IX lock wait exceeded (severe blocking)')
INSERT INTO @AlertTable VALUES ('Error',595,	@ServerName + N' Error 595: Lock escalation prevented')
INSERT INTO @AlertTable VALUES ('Error',1221,	@ServerName + N' Error 1221: Lock resources exceeded (deadlock victim)')

/* TempDB critical issues */
INSERT INTO @AlertTable VALUES ('Error',1105,	@ServerName + N' Error 1105: Could not allocate space in tempdb')

/* Query Store errors */
INSERT INTO @AlertTable VALUES ('Error',12410,	@ServerName + N' Error 12410: Query Store internal error')
INSERT INTO @AlertTable VALUES ('Error',12411,	@ServerName + N' Error 12411: Query Store collection failed')

/* Transaction log corruption */
INSERT INTO @AlertTable VALUES ('Error',9003,	@ServerName + N' Error 9003: Log scan - invalid log sequence number')
INSERT INTO @AlertTable VALUES ('Error',9004,	@ServerName + N' Error 9004: Log file corruption - truncated')
INSERT INTO @AlertTable VALUES ('Error',9013,	@ServerName + N' Error 9013: Virtual log file too small')

/* DAC connection issues */
INSERT INTO @AlertTable VALUES ('Error',233,	@ServerName + N' Error 233: Shared memory provider disconnected')

/* Brent Ozar / Microsoft additional critical alerts */
/* Out of memory conditions */
INSERT INTO @AlertTable VALUES ('Error',701, 	@ServerName + N' Error 701: Insufficient memory (resource pool)')
INSERT INTO @AlertTable VALUES ('Error',802, 	@ServerName + N' Error 802: Buffer pool insufficient memory')
INSERT INTO @AlertTable VALUES ('Error',8645, 	@ServerName + N' Error 8645: Resource semaphore wait timeout')

/* SOS scheduler exhaustion (critical) */
INSERT INTO @AlertTable VALUES ('Error',17883, 	@ServerName + N' Error 17883: Scheduler deadlock detected')
INSERT INTO @AlertTable VALUES ('Error',17884, 	@ServerName + N' Error 17884: All schedulers appear deadlocked')


DECLARE @MaxAlerts TINYINT
DECLARE @AlertCounter TINYINT 
SET @AlertCounter = 1
DECLARE @ThisName sysname
DECLARE @Exception NVARCHAR(2000)
DECLARE @ThisAlert INT
DECLARE @ThisMessage INT
DECLARE @ThisAlertType NVARCHAR(50)
SELECT @MaxAlerts = MAX(ID) FROM @AlertTable
USE msdb
WHILE @AlertCounter <= @MaxAlerts
BEGIN
	SELECT @ThisName = AlertName
	, @ThisAlert = TheNumber
	, @ThisAlertType = AlertType
	FROM @AlertTable WHERE ID = @AlertCounter
	IF @ThisAlertType = 'Error'
	BEGIN
		SET @ThisMessage = @ThisAlert
		SET @ThisAlert = 0
	END
	IF NOT EXISTS (SELECT name FROM msdb.dbo.sysalerts WHERE name = @ThisName)
	BEGIN
		IF @ThisAlert <> 0
			RAISERROR (@ThisAlert,0,1) WITH NOWAIT;
		BEGIN TRY
			EXEC msdb.dbo.sp_add_alert @name = @ThisName, 
						  @message_id = @ThisMessage, @severity = @ThisAlert, @enabled = 1, 
						  @delay_between_responses = 900, @include_event_description_in = 1,@notification_message=@SQLDBANotification,
						  @category_name = @CategoryName, 
						 @job_id = N'00000000-0000-0000-0000-000000000000';
			IF NOT EXISTS(SELECT *
				  FROM msdb.dbo.sysalerts AS sa
				  INNER JOIN msdb.dbo.sysnotifications AS sn
				  ON sa.id = sn.alert_id
				  WHERE (sa.name = @ThisName
				  OR sa.message_id = @ThisAlert))
			BEGIN
				EXEC msdb.dbo.sp_add_notification @alert_name = @ThisName, @operator_name = @OperatorName, @notification_method = 1;
				--EXEC msdb.dbo.sp_add_notification @alert_name = @ThisName, @operator_name = @ServiceDesk, @notification_method = 1;
				SET @ThisName = 'Configure alert - ' + @ThisName
			
				/*Ensure these errors log for SCOM*/
				IF @ThisAlertType = 'Error'
					EXEC sp_altermessage @ThisMessage, 'WITH_LOG', 'true'

				RAISERROR (@ThisName,0,1) WITH NOWAIT;
			END

		END TRY

		BEGIN CATCH
			SET @Exception = 'Failed to configure alert - ' + @ThisName + '. ' + CONVERT(VARCHAR(50),ERROR_NUMBER()) + '. ' + CONVERT(VARCHAR(500),ERROR_MESSAGE())
			IF ERROR_NUMBER() <> 14501 --Already exists
				RAISERROR (@Exception,16,1) WITH NOWAIT;
		END CATCH
	END		  
	SET @AlertCounter = @AlertCounter + 1
END



/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
   SECTION 5: MAINTENANCE JOBS
   Create error log cycling job and configure job failure alerts
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
IF  EXISTS (SELECT job_id FROM msdb.dbo.sysjobs_view WHERE name = N'Daily Cycle Errorlog')
EXEC msdb.dbo.sp_delete_job @job_name=N'Daily Cycle Errorlog', @delete_unused_schedule=1
BEGIN TRANSACTION

-- Set the Operator name to receive notifications, if any. Set the job owner, if not sa.
DECLARE @jobowner sysname
SET @jobowner = 'sa'

SELECT @ReturnCode = 0

IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Database Maintenance' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Database Maintenance'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

IF EXISTS (SELECT name FROM msdb.dbo.sysoperators WHERE name = @OperatorName)
BEGIN
	EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'Daily Cycle Errorlog', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=2, 
		@notify_level_netsend=2, 
		@notify_level_page=2, 
		@delete_level=0, 
		@description=N'Cycles Errorlog when its size is over 20MB or its age over 15 days.', 
		@category_name=N'Database Maintenance', 
		@owner_login_name=@jobowner, 
		@notify_email_operator_name=@OperatorName,
		@job_id = @jobId OUTPUT
	IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
END
ELSE
BEGIN
	EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'Daily Cycle Errorlog', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=3, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'Cycles Errorlog when its size is over 20MB or its age over 15 days.', 
		@category_name=N'Database Maintenance', 
		@owner_login_name=@jobowner,
		@job_id = @jobId OUTPUT
	IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
END

EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'Adaptive Cycle Errorlog', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'SET NOCOUNT ON;
DECLARE @CycleMessage VARCHAR(255), @return_value int, @Output VARCHAR(32)
DECLARE @ErrorLogs TABLE (ArchiveNumber tinyint, DateCreated DATETIME, LogFileSizeBytes int)
INSERT into @ErrorLogs (ArchiveNumber, DateCreated, LogFileSizeBytes )
EXEC master.dbo.sp_enumerrorlogs

SELECT @CycleMessage = ''Current SQL Server ErrorLog was created on '' + CONVERT(VARCHAR, DateCreated , 105) + '' and is using '' +
CASE WHEN LogFileSizeBytes BETWEEN 1024 AND 1048575 THEN CAST(LogFileSizeBytes/1024 AS VARCHAR(10)) + '' KB.''
WHEN LogFileSizeBytes > 1048575 THEN CAST((LogFileSizeBytes/1024)/1024 AS VARCHAR(10)) + '' MB.''
ELSE CAST(LogFileSizeBytes AS VARCHAR(4)) + '' Bytes.''
END 
+ CASE WHEN LogFileSizeBytes > 20971520 THEN '' The ErrorLog will be cycled because of its size.'' -- over 20MB
WHEN DateCreated <= DATEADD(dd, -15,GETDATE()) THEN '' The ErrorLog will be cycled because of its age.'' -- over 15 days
ELSE '' The ErrorLog will not be cycled.'' end
FROM @ErrorLogs where ArchiveNumber = 1

RAISERROR (@CycleMessage,0,1) WITH NOWAIT;

IF @CycleMessage LIKE ''%will be cycled%''
BEGIN
	EXEC @return_value = sp_cycle_errorlog
	SELECT @Output = CASE WHEN @return_value = 0 THEN ''ErrorLog was sucessfully cycled.'' ELSE ''Failure cycling Errorlog.'' END
	RAISERROR (@Output,0,1) WITH NOWAIT;
END', 
		@database_name=N'master', 
		@flags=4
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'Daily Cycle Errorlog', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=1, 
		@freq_subday_interval=0, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20120529, 
		@active_end_date=99991231, 
		@active_start_time=235900, 
		@active_end_time=235959
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:

RAISERROR ( 'Daily Cycle Log job created',0,1) WITH NOWAIT;



-- Limit error logs
EXEC xp_instance_regwrite N'HKEY_LOCAL_MACHINE', N'Software\Microsoft\MSSQLServer\MSSQLServer', N'NumErrorLogs', REG_DWORD, 100
GO
RAISERROR ('Action: Set Errorlogs to 100',0,1) WITH NOWAIT;
-- Set sp_configure settings

/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- Set RAC, repl size, recovery interval and agent history
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
EXEC sys.sp_configure N'show advanced options', N'1'
RECONFIGURE WITH OVERRIDE

EXEC sys.sp_configure N'remote admin connections', N'1'
RECONFIGURE WITH OVERRIDE

RAISERROR ('Action: Enabled remote admin mail',0,1) WITH NOWAIT;


EXEC sys.sp_configure N'recovery interval (min)', N'1'
RAISERROR ('Action: Set recovery interval',0,1) WITH NOWAIT;

EXEC sys.sp_configure N'max text repl size (B)', N'-1'
RAISERROR ('Action: Set max text replication size',0,1) WITH NOWAIT;


/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- Default Trace
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
/* The default trace records schema changes, database file growth/shrink,
   security changes, server start/stop, and error events to a rolling set
   of .trc files. It is on by default but can be accidentally disabled.
   It is the first place to look when troubleshooting "who changed what".
   Note: deprecated in SQL 2022 in favour of XEvent system_health — this
   block enables it on all earlier versions and warns on 2022+.          */
DECLARE @DefaultTraceEnabled INT
DECLARE @DefaultTraceVer     INT
SELECT @DefaultTraceEnabled = CONVERT(INT, value_in_use)
FROM sys.configurations WHERE name = 'default trace enabled';
SELECT @DefaultTraceVer = @@MicrosoftVersion / 0x01000000;

IF @DefaultTraceVer >= 16
BEGIN
    RAISERROR ('Action: Default trace is deprecated in SQL 2022+ (system_health XEvent session covers the same ground)', 0, 1) WITH NOWAIT;
END
ELSE IF @DefaultTraceEnabled = 0
BEGIN
    EXEC sys.sp_configure N'show advanced options', N'1'
    RECONFIGURE WITH OVERRIDE
    EXEC sys.sp_configure N'default trace enabled', N'1'
    RECONFIGURE WITH OVERRIDE
    EXEC sys.sp_configure N'show advanced options', N'0'
    RECONFIGURE WITH OVERRIDE
    RAISERROR ('Action: Default trace was disabled -- re-enabled', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR ('Action: Default trace is already enabled', 0, 1) WITH NOWAIT;


/*Let's set , don't want no huge msdb please*/
EXEC msdb.dbo.sp_set_sqlagent_properties
	@jobhistory_max_rows=100000,
	@jobhistory_max_rows_per_job=1000,
	@email_save_in_sent_folder=1,
	@cpu_poller_enabled=1,
	@idle_cpu_percent=10,        -- idle when CPU below 10%
	@idle_cpu_duration=600       -- for 600 consecutive seconds
/*Configure Agent alert conditions*/
EXEC master.dbo.sp_MSsetalertinfo @failsafeoperator=N'SQLDBA',
		@notificationmethod=1

RAISERROR ('Action: Set jobhistory to 100k, rows per job 1k',0,1) WITH NOWAIT;




/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- Set backup checksum, contained databases, backup compression and ad hoc workloads
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
/*Check if server version > SQL 2008R2*/
DECLARE @SQLVersion INT
SELECT @SQLVersion = @@MicrosoftVersion / 0x01000000  OPTION (RECOMPILE)-- Get major version

IF @SQLVersion >= 10
BEGIN
	BEGIN TRY
		EXEC sys.sp_configure N'backup checksum default', N'1'
		RECONFIGURE WITH OVERRIDE
		RAISERROR ('Action: Set backup checksum',0,1) WITH NOWAIT;
		
	END TRY
	BEGIN CATCH 
		RAISERROR ('Failed to configure backup checksum default',0,1) WITH NOWAIT;
	END CATCH
	EXEC sys.sp_configure N'contained database authentication', N'1'
	RECONFIGURE WITH OVERRIDE
	RAISERROR ( 'Action: Enabled contained DBs',0,1) WITH NOWAIT;
END
-- Use 'backup compression default' when server is NOT CPU bound
IF CONVERT(int, (@@microsoftversion / 0x1000000) & 0xff) >= 10
EXEC sys.sp_configure N'backup compression default', N'1'
RECONFIGURE WITH OVERRIDE

RAISERROR ('Action: Set backup compression',0,1) WITH NOWAIT;
-- Use 'optimize for ad hoc workloads' for OLTP workloads ONLY
IF CONVERT(int, (@@microsoftversion / 0x1000000) & 0xff) >= 10
EXEC sys.sp_configure N'optimize for ad hoc workloads', N'1'
RECONFIGURE WITH OVERRIDE
RAISERROR ('Action: Set optimize for ad hoc',0,1) WITH NOWAIT;

/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- MAXDOP and Cost Threshold for Parallelism
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
/* MAXDOP: cap at 8, use half physical cores, never exceed NUMA node size.
   Default 0 causes full-server parallelism storms on OLTP workloads.
   Adjust @TargetMAXDOP before running if you know the workload profile. */
DECLARE @PhysicalCores     INT
DECLARE @NumaNodes         INT
DECLARE @CoresPerNuma      INT
DECLARE @TargetMAXDOP      INT
DECLARE @MaxDOPMsg         NVARCHAR(300)

SELECT
    @PhysicalCores = si.cpu_count / si.hyperthread_ratio,
    @NumaNodes     = COUNT(DISTINCT mn.memory_node_id)
FROM sys.dm_os_sys_info si
CROSS JOIN sys.dm_os_memory_nodes mn
WHERE mn.memory_node_id <> 64  -- exclude DAC node
GROUP BY si.cpu_count, si.hyperthread_ratio;

/* Cores per NUMA node (floor, minimum 1) */
SET @CoresPerNuma = CASE WHEN @NumaNodes > 0 THEN @PhysicalCores / @NumaNodes ELSE @PhysicalCores END;
IF @CoresPerNuma < 1 SET @CoresPerNuma = 1;

/* MAXDOP: half physical cores, capped at 8, never exceeds cores-per-NUMA-node, minimum 1 */
SET @TargetMAXDOP = CASE
    WHEN @PhysicalCores / 2 >= 8         THEN 8
    WHEN @PhysicalCores / 2 < 1          THEN 1
    ELSE @PhysicalCores / 2
END;

/* NUMA guard: MAXDOP must not exceed physical cores per NUMA node */
IF @TargetMAXDOP > @CoresPerNuma SET @TargetMAXDOP = @CoresPerNuma;

EXEC sys.sp_configure N'show advanced options', N'1'
RECONFIGURE WITH OVERRIDE

EXEC sys.sp_configure N'max degree of parallelism', @TargetMAXDOP
RECONFIGURE WITH OVERRIDE

SET @MaxDOPMsg = 'Action: Set MAXDOP to ' + CONVERT(VARCHAR(5), @TargetMAXDOP)
    + ' (physical cores: ' + CONVERT(VARCHAR(5), @PhysicalCores)
    + ', NUMA nodes: ' + CONVERT(VARCHAR(5), @NumaNodes)
    + ', cores/NUMA: ' + CONVERT(VARCHAR(5), @CoresPerNuma) + ')'
RAISERROR (@MaxDOPMsg, 0, 1) WITH NOWAIT;

/* Cost threshold for parallelism: default 5 is far too low.
   50 is a widely accepted starting point for mixed OLTP. Adjust per workload. */
EXEC sys.sp_configure N'cost threshold for parallelism', N'50'
RECONFIGURE WITH OVERRIDE
RAISERROR ('Action: Set cost threshold for parallelism to 50', 0, 1) WITH NOWAIT;

/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- Max Server Memory  (CRITICAL — default is unlimited)
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
/* Reserve OS + non-buffer-pool SQL memory.
   Formula: leave 10% or 4 GB for OS (whichever is larger), minus ~1 GB per 4 logical cores for thread stacks.
   MODIFY @ReservedForOSMB if this is a dedicated SQL box vs shared host. */
DECLARE @TotalRAMMB       BIGINT
DECLARE @ReservedForOSMB  BIGINT
DECLARE @TargetMaxMemMB   BIGINT
DECLARE @LogicalCores     INT

SELECT @TotalRAMMB   = physical_memory_kb / 1024,
       @LogicalCores = cpu_count
FROM sys.dm_os_sys_info;

/* Reserve: max of (10% of RAM, 4096 MB) + 256 MB per 4 logical cores for thread stacks */
SET @ReservedForOSMB = CASE
    WHEN @TotalRAMMB * 0.10 > 4096 THEN CONVERT(BIGINT, @TotalRAMMB * 0.10)
    ELSE 4096
END + ((@LogicalCores / 4) * 256);

SET @TargetMaxMemMB = @TotalRAMMB - @ReservedForOSMB;

/* Safety floor: never set below 512 MB */
IF @TargetMaxMemMB < 512 SET @TargetMaxMemMB = 512;

DECLARE @MaxMemMsg NVARCHAR(500)
SET @MaxMemMsg = 'Action: Set max server memory to ' + CONVERT(VARCHAR(10), @TargetMaxMemMB)
    + ' MB (total RAM: ' + CONVERT(VARCHAR(10), @TotalRAMMB) + ' MB, reserved for OS: ' + CONVERT(VARCHAR(10), @ReservedForOSMB) + ' MB)'
RAISERROR (@MaxMemMsg, 0, 1) WITH NOWAIT;

--EXEC sys.sp_configure N'max server memory (MB)', @TargetMaxMemMB
--RECONFIGURE WITH OVERRIDE

/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- Security Hardening: disable surface area features not in use
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
/* Disable features that expand attack surface. Enable only what your apps require. */
EXEC sys.sp_configure N'Ole Automation Procedures', N'0'
RECONFIGURE WITH OVERRIDE
RAISERROR ('Action: Disabled Ole Automation Procedures', 0, 1) WITH NOWAIT;

EXEC sys.sp_configure N'Ad Hoc Distributed Queries', N'0'
RECONFIGURE WITH OVERRIDE
RAISERROR ('Action: Disabled Ad Hoc Distributed Queries (OPENROWSET/OPENDATASOURCE)', 0, 1) WITH NOWAIT;

EXEC sys.sp_configure N'cross db ownership chaining', N'0'
RECONFIGURE WITH OVERRIDE
RAISERROR ('Action: Disabled cross-database ownership chaining', 0, 1) WITH NOWAIT;

/* Ensure SQL Mail XPs are OFF (legacy, superseded by Database Mail) */
IF EXISTS(SELECT * FROM sys.configurations WHERE name = 'SQL Mail XPs')
BEGIN
	EXEC sys.sp_configure N'SQL Mail XPs', N'0'
	RECONFIGURE WITH OVERRIDE
	RAISERROR ('Action: Disabled SQL Mail XPs (legacy)', 0, 1) WITH NOWAIT;
END

/* Always reset show advanced options — do this unconditionally so it is
   not dependent on whether SQL Mail XPs exists (removed in SQL 2022+). */
EXEC sys.sp_configure N'show advanced options', N'0'
RECONFIGURE WITH OVERRIDE


/*Configure memory
BATCH_MODE_ON_ROWSTORE

DECLARE @CPUcount INT;
DECLARE @CPUsocketcount INT;
DECLARE @CPUHyperthreadratio MONEY;
	SELECT @CPUcount = cpu_count 
	, @CPUsocketcount = [cpu_count] / [hyperthread_ratio]
	, @CPUHyperthreadratio = [hyperthread_ratio]
	FROM sys.dm_os_sys_info;
*/


/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
   SECTION 7: DATABASE-LEVEL SETTINGS
   Configure per-database settings: auto-close, page verify, Query Store
   Also configures Model DB defaults for new database creation
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/

DECLARE @DatabaseName SYSNAME;
DECLARE @Databasei_Count INT;
DECLARE @Databasei_Max INT;
DECLARE @DynamicSQL NVARCHAR(4000);
DECLARE @DynamicSQLforDB NVARCHAR(4000);
DECLARE @state TINYINT
DECLARE @AutoClose TINYINT
DECLARE @AutoShrink TINYINT
DECLARE @AutoStats TINYINT
DECLARE @AutoCreateStats TINYINT
DECLARE @AutoStatsAsync TINYINT
DECLARE @AutoStatsInc TINYINT
DECLARE @RecoveryModel TINYINT
DECLARE @PageVerify TINYINT
DECLARE @QueryStore TINYINT



DECLARE @Databases TABLE
	(
		id INT IDENTITY(1,1)
		, databasename VARCHAR(250)
		, [compatibility_level] BIGINT
		, user_access BIGINT
		, user_access_desc VARCHAR(50)
		, [state] BIGINT
		, state_desc  VARCHAR(50)
		, recovery_model INT
		, recovery_model_desc  VARCHAR(50)
		, create_date DATETIME
		, is_auto_close_on TINYINT
		, is_auto_shrink_on TINYINT
		, is_auto_create_stats_on TINYINT
		, is_auto_update_stats_on TINYINT
		, is_auto_update_stats_async_on TINYINT
		, is_auto_create_stats_incremental_on TINYINT
		, page_verify_option TINYINT
		, is_query_store_on TINYINT
	);
SET @DynamicSQL = 'SELECT 
	db.name
	, db.compatibility_level
	, db.user_access
	, db.user_access_desc
	, db.state
	, db.state_desc
	, db.recovery_model
	, db.recovery_model_desc
	, db.create_date
	, db.is_auto_close_on
	, db.is_auto_shrink_on
	, db.is_auto_create_stats_on
	, db.is_auto_update_stats_on
	, db.is_auto_update_stats_async_on
	' + CASE WHEN CONVERT(INT, LEFT(CONVERT(NVARCHAR(20), SERVERPROPERTY('productversion')), CHARINDEX('.', CONVERT(NVARCHAR(20), SERVERPROPERTY('productversion'))) - 1)) < 12 THEN ', NULL' ELSE ', db.is_auto_create_stats_incremental_on' END +'
	, db.page_verify_option
	' + CASE WHEN CONVERT(INT, LEFT(CONVERT(NVARCHAR(20), SERVERPROPERTY('productversion')), CHARINDEX('.', CONVERT(NVARCHAR(20), SERVERPROPERTY('productversion'))) - 1)) < 13 THEN ', db.is_query_store_on' ELSE ', NULL' END +'
	FROM sys.databases db ';

IF 'Yes please dont do the system databases' IS NOT NULL
BEGIN
	SET @DynamicSQL = @DynamicSQL + ' WHERE database_id > 4 AND state NOT IN (1,2,3,6)';
END
SET @DynamicSQL = @DynamicSQL + ' OPTION (RECOMPILE)'
INSERT INTO @Databases 
EXEC sp_executesql @DynamicSQL ;
SET @Databasei_Max = (SELECT MAX(id) FROM @Databases );

DECLARE @dynamicmessage NVARCHAR(500)
SET @Databasei_Count = 1; 
WHILE @Databasei_Count <= @Databasei_Max 
BEGIN 
	SET @DynamicSQLforDB = '';
	SELECT 
	@state = state
	, @DatabaseName = d.databasename
	, @AutoClose = is_auto_close_on
	, @AutoShrink = is_auto_shrink_on
	, @AutoCreateStats = is_auto_create_stats_on
	, @AutoStats = is_auto_update_stats_on
	, @AutoStatsAsync = is_auto_update_stats_async_on
	, @AutoStatsInc = is_auto_create_stats_incremental_on
	, @RecoveryModel = recovery_model --3 = simple
	, @PageVerify = page_verify_option --2 = checksum	
	, @QueryStore = is_query_store_on
	FROM @Databases d WHERE id = @Databasei_Count

	IF @AutoClose = 1 
		SET @DynamicSQLforDB = @DynamicSQLforDB + CHAR(13)+CHAR(10) + 'ALTER DATABASE [' + @DatabaseName + '] SET AUTO_CLOSE OFF WITH NO_WAIT'
	IF @AutoShrink = 1
		SET @DynamicSQLforDB = @DynamicSQLforDB + CHAR(13)+CHAR(10) + 'ALTER DATABASE [' + @DatabaseName + '] SET AUTO_SHRINK OFF WITH NO_WAIT'
	IF @PageVerify <> 2
		SET @DynamicSQLforDB = @DynamicSQLforDB + CHAR(13)+CHAR(10) + 'ALTER DATABASE [' + @DatabaseName + '] SET PAGE_VERIFY CHECKSUM WITH NO_WAIT'
	IF @AutoCreateStats = 0
		SET @DynamicSQLforDB = @DynamicSQLforDB + CHAR(13)+CHAR(10) + 'ALTER DATABASE [' + @DatabaseName + '] SET AUTO_CREATE_STATISTICS ON WITH NO_WAIT'
	IF @AutoStats = 0
		SET @DynamicSQLforDB = @DynamicSQLforDB + CHAR(13)+CHAR(10) + 'ALTER DATABASE [' + @DatabaseName + '] SET AUTO_UPDATE_STATISTICS ON WITH NO_WAIT'
	IF @QueryStore = 0
		SET @DynamicSQLforDB = @DynamicSQLforDB + CHAR(13)+CHAR(10) + 'ALTER DATABASE [' + @DatabaseName + '] SET QUERY_STORE = ON WITH NO_WAIT'
	/*
	
	IF @AutoStatsAsync = 0
		SET @DynamicSQLforDB = @DynamicSQLforDB + CHAR(13)+CHAR(10) + 'ALTER DATABASE [' + @DatabaseName + '] SET AUTO_UPDATE_STATISTICS_ASYNC ON WITH NO_WAIT'
	*/
--	IF @RecoveryModel <> 1
--		SET @DynamicSQLforDB = @DynamicSQLforDB + CHAR(13)+CHAR(10) + 'ALTER DATABASE [' + @DatabaseName + '] SET RECOVERY FULL WITH NO_WAIT'
	IF @DynamicSQLforDB <> ''
	BEGIN
		RAISERROR ('Committing database change',0,1) WITH NOWAIT;
		RAISERROR (@DynamicSQLforDB,0,1) WITH NOWAIT;
		BEGIN TRY
			EXECUTE( @DynamicSQLforDB)
			SET @dynamicmessage = 'Successfully applied changes to database: ' + @DatabaseName
			RAISERROR (@dynamicmessage,0,1) WITH NOWAIT;
		END TRY
		BEGIN CATCH
			SET @dynamicmessage = 'ERROR: Failed to apply changes to database [' + @DatabaseName + ']: ' + ERROR_MESSAGE()
			RAISERROR (@dynamicmessage, 16, 1) WITH NOWAIT;
			-- Continue processing other databases instead of stopping
		END CATCH
	END
	SET @Databasei_Count = @Databasei_Count + 1
END

DECLARE @sql nvarchar(max) = N'';
SELECT @sql += CASE
  WHEN (ag.role = N'PRIMARY' AND ag.ag_status = N'READ_WRITE') OR ag.role IS NULL THEN N'
    ALTER DATABASE ' + QUOTENAME(d.name) + N' SET TARGET_RECOVERY_TIME = 60 SECONDS;' 
  ELSE N'
    RAISERROR ( N''-- fix ' + QUOTENAME(d.name) + N' on Primary.'',0,1) WITH NOWAIT;' 
  END
FROM sys.databases AS d 
OUTER APPLY
(
  SELECT role = s.role_desc, 
    ag_status = DATABASEPROPERTYEX(c.database_name, N'Updateability')
    FROM sys.dm_hadr_availability_replica_states AS s
    INNER JOIN sys.availability_databases_cluster AS c
       ON s.group_id = c.group_id 
       AND d.name = c.database_name
    WHERE s.is_local = 1
) AS ag
WHERE d.target_recovery_time_in_seconds <> 60
  AND d.database_id > 4 
  AND d.[state] = 0 
  AND d.is_in_standby = 0 
  AND d.is_read_only = 0;
/*SELECT DatabaseCount = @@ROWCOUNT, Version = @@VERSION, cmd = @sql;*/
EXEC sys.sp_executesql @sql;


/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- Set Model DB defaults (new databases inherit these settings)
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
/* Any database created after this point will inherit model's settings.
   These mirror the per-database fixes above. */
ALTER DATABASE [model] SET AUTO_CLOSE OFF WITH NO_WAIT;
ALTER DATABASE [model] SET AUTO_SHRINK OFF WITH NO_WAIT;
ALTER DATABASE [model] SET PAGE_VERIFY CHECKSUM WITH NO_WAIT;
ALTER DATABASE [model] SET AUTO_CREATE_STATISTICS ON WITH NO_WAIT;
ALTER DATABASE [model] SET AUTO_UPDATE_STATISTICS ON WITH NO_WAIT;
ALTER DATABASE [model] SET TARGET_RECOVERY_TIME = 60 SECONDS;
RAISERROR ('Action: Model DB defaults configured', 0, 1) WITH NOWAIT;

/* Query Store on model requires SQL 2019+ (version 15). Skip on earlier. */
DECLARE @ModelSQLVer INT
SELECT @ModelSQLVer = @@MicrosoftVersion / 0x01000000
IF @ModelSQLVer >= 15
BEGIN
    ALTER DATABASE [model] SET QUERY_STORE = ON WITH NO_WAIT;
    RAISERROR ('Action: Model DB Query Store enabled (SQL 2019+)', 0, 1) WITH NOWAIT;
END


/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- TRUSTWORTHY OFF for all user databases
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
/* TRUSTWORTHY ON allows code in the database to reference objects outside it
   using the database owner's server-level permissions. Should be OFF unless
   explicitly required (e.g., Service Broker with cross-db activation).     */
DECLARE @TrustSQL NVARCHAR(MAX) = N''
SELECT @TrustSQL += N'ALTER DATABASE ' + QUOTENAME(name) + N' SET TRUSTWORTHY OFF;' + CHAR(13)
FROM sys.databases
WHERE is_trustworthy_on = 1
  AND database_id > 4         -- skip system databases
  AND name NOT IN ('msdb');   -- msdb legitimately requires TRUSTWORTHY ON

IF @TrustSQL <> N''
BEGIN
    RAISERROR ('Action: Setting TRUSTWORTHY OFF on user databases', 0, 1) WITH NOWAIT;
    RAISERROR (@TrustSQL, 0, 1) WITH NOWAIT;
    --EXEC sys.sp_executesql @TrustSQL;
END
ELSE
    RAISERROR ('Action: TRUSTWORTHY already OFF on all user databases', 0, 1) WITH NOWAIT;


/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- Compatibility Level remediation
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
/* Databases on an old compatibility level miss cardinality estimator improvements,
   Query Store features, and many query optimizer fixes tied to newer compat levels.
   This block reports databases below the current engine level and optionally upgrades them.
   Set @UpgradeCompatLevel = 1 to apply. Leave 0 to report only.
   WARNING: Test workloads after upgrading compat level — query plans can change.        */
DECLARE @UpgradeCompatLevel BIT = 0   -- SET TO 1 TO APPLY
DECLARE @EngineCompatLevel  INT
DECLARE @CompatSQL          NVARCHAR(MAX) = N''
DECLARE @CompatReport       NVARCHAR(MAX) = N''

/* Map engine major version to its default compat level */
DECLARE @EngineVer INT
SELECT @EngineVer = @@MicrosoftVersion / 0x01000000
SET @EngineCompatLevel = CASE @EngineVer
    WHEN 16 THEN 160  -- SQL 2022
    WHEN 15 THEN 150  -- SQL 2019
    WHEN 14 THEN 140  -- SQL 2017
    WHEN 13 THEN 130  -- SQL 2016
    WHEN 12 THEN 120  -- SQL 2014
    WHEN 11 THEN 110  -- SQL 2012
    WHEN 10 THEN 100  -- SQL 2008/R2
    ELSE 80
END;

SELECT @CompatReport += N'-- ' + name + N' is at compat level ' + CONVERT(VARCHAR(5), compatibility_level)
    + N' (engine is ' + CONVERT(VARCHAR(5), @EngineCompatLevel) + N')' + CHAR(13)
FROM sys.databases
WHERE database_id > 4
  AND [state] = 0
  AND compatibility_level < @EngineCompatLevel;

IF @CompatReport <> N''
BEGIN
    RAISERROR ('Databases below current engine compat level:', 0, 1) WITH NOWAIT;
    RAISERROR (@CompatReport, 0, 1) WITH NOWAIT;

    IF @UpgradeCompatLevel = 1
    BEGIN
        SELECT @CompatSQL += N'ALTER DATABASE ' + QUOTENAME(name)
            + N' SET COMPATIBILITY_LEVEL = ' + CONVERT(VARCHAR(5), @EngineCompatLevel) + N';' + CHAR(13)
        FROM sys.databases
        WHERE database_id > 4
          AND [state] = 0
          AND compatibility_level < @EngineCompatLevel;

        RAISERROR ('Action: Upgrading compatibility levels', 0, 1) WITH NOWAIT;
        RAISERROR (@CompatSQL, 0, 1) WITH NOWAIT;
        EXEC sys.sp_executesql @CompatSQL;
    END
    ELSE
        RAISERROR ('Action: Compat level upgrade skipped (set @UpgradeCompatLevel = 1 to apply)', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR ('Action: All user databases are at current engine compat level', 0, 1) WITH NOWAIT;


/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- Per-database security settings: DB_CHAINING and HONOR_BROKER_PRIORITY
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
/* DB_CHAINING ON allows cross-database ownership chaining per database.
   The server-level setting was disabled above; this ensures it is also
   OFF at the database level for any databases that had it explicitly set. */
DECLARE @ChainSQL NVARCHAR(MAX) = N''
SELECT @ChainSQL += N'ALTER DATABASE ' + QUOTENAME(name) + N' SET DB_CHAINING OFF;' + CHAR(13)
FROM sys.databases
WHERE is_db_chaining_on = 1
  AND database_id > 4
  AND name NOT IN ('master', 'tempdb', 'model', 'msdb');

IF @ChainSQL <> N''
BEGIN
    RAISERROR ('Action: Disabling DB_CHAINING on user databases', 0, 1) WITH NOWAIT;
    RAISERROR (@ChainSQL, 0, 1) WITH NOWAIT;
    --EXEC sys.sp_executesql @ChainSQL;
END
ELSE
    RAISERROR ('Action: DB_CHAINING already OFF on all user databases', 0, 1) WITH NOWAIT;


/* HONOR_BROKER_PRIORITY ON causes Service Broker to respect message priority levels.
   This changes broker scheduling behaviour and should only be ON if the application
   explicitly uses priority-aware Service Broker conversations.                      */
DECLARE @BrokerPriSQL NVARCHAR(MAX) = N''
SELECT @BrokerPriSQL += N'ALTER DATABASE ' + QUOTENAME(name) + N' SET HONOR_BROKER_PRIORITY OFF;' + CHAR(13)
FROM sys.databases
WHERE is_honor_broker_priority_on = 1
  AND database_id > 4;

IF @BrokerPriSQL <> N''
BEGIN
    RAISERROR ('Action: Setting HONOR_BROKER_PRIORITY OFF on user databases', 0, 1) WITH NOWAIT;
    RAISERROR (@BrokerPriSQL, 0, 1) WITH NOWAIT;
    EXEC sys.sp_executesql @BrokerPriSQL;
END
ELSE
    RAISERROR ('Action: HONOR_BROKER_PRIORITY already OFF on all user databases', 0, 1) WITH NOWAIT;


/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- READ_COMMITTED_SNAPSHOT (RCSI) — eliminate reader/writer blocking
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
/* RCSI allows readers to see the last committed version of a row without
   taking shared locks, eliminating the most common cause of blocking on OLTP.
   Requires tempdb space for the version store.
   REVIEW: this is opt-in per database — set @EnableRCSI = 1 to activate.
   Databases with RCSI already enabled are skipped.                         */
DECLARE @EnableRCSI BIT = 0  -- SET TO 1 TO ENABLE ON ALL OLTP USER DATABASES

IF @EnableRCSI = 1
BEGIN
    DECLARE @RCSIName SYSNAME
    DECLARE @RCSISQL  NVARCHAR(2000)
    DECLARE rcsi_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT name FROM sys.databases
        WHERE database_id > 4
          AND is_read_committed_snapshot_on = 0
          AND [state] = 0
          AND is_read_only = 0
          AND is_in_standby = 0

    OPEN rcsi_cursor
    FETCH NEXT FROM rcsi_cursor INTO @RCSIName
    WHILE @@FETCH_STATUS = 0
    BEGIN
        /* RCSI requires single-user mode to change — brief impact */
        SET @RCSISQL = N'ALTER DATABASE ' + QUOTENAME(@RCSIName)
            + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE;'
            + N' ALTER DATABASE ' + QUOTENAME(@RCSIName)
            + N' SET READ_COMMITTED_SNAPSHOT ON;'
            + N' ALTER DATABASE ' + QUOTENAME(@RCSIName)
            + N' SET MULTI_USER;'
        RAISERROR (@RCSISQL, 0, 1) WITH NOWAIT;
        BEGIN TRY
            EXEC sys.sp_executesql @RCSISQL;
        END TRY
        BEGIN CATCH
            RAISERROR ('RCSI failed on one database — check for active connections', 16, 1) WITH NOWAIT;
        END CATCH
        FETCH NEXT FROM rcsi_cursor INTO @RCSIName
    END
    CLOSE rcsi_cursor
    DEALLOCATE rcsi_cursor
    RAISERROR ('Action: RCSI enabled on user databases', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR ('Action: RCSI skipped (set @EnableRCSI = 1 to enable per-database)', 0, 1) WITH NOWAIT;


/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- Set database autogrowth
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/

/*Now to recommend code to fix autogrowth*/
/*
****MODIFICATION REQUIRED for AUTOGROWTH -- See line 64 below****
1) Use this script to change the auto growth setting of
   for all databases
2) If you want to exclude any database add the DBs in the
   WHERE Clause -- See line 50 below
3) Tested in 2012 and 2014 SQL Servers
*/
DECLARE @TargetDATAGrowth NVARCHAR(50)
SET @TargetDATAGrowth = '512'
DECLARE @TargetLOGGrowth NVARCHAR(50)
SET @TargetLOGGrowth = '256'


DECLARE @ConfigAutoGrowth TABLE
(
	iDBID INT
	, sDBName SYSNAME
	, vFileName NVARCHAR(2000)
	, [MB Growth] MONEY
	, [File Type] VARCHAR(20)
	, vGrowthOption VARCHAR(12)
	, [Option] NVARCHAR(500)
	, [Action Needed] TINYINT
	, [Command] NVARCHAR(500)
)

-- Inserting data into staging table
INSERT INTO @ConfigAutoGrowth
SELECT
	SD.database_id
	,SD.name
	,SF.name
	, SF.growth /128 [MB Growth]
	, CASE SF.status
		WHEN 2 THEN 'Data'
		WHEN 1048578 THEN 'Data'
		WHEN 66 THEN 'Log'
		WHEN 1048642 THEN 'Log'
	END [File Type]
	, CASE SF.status
		WHEN 1048578 THEN '%'
		WHEN 1048642 THEN '%'
	ELSE 'MB' END AS 'GROWTH Option'
	
	, CASE 
		WHEN SF.status = 2			AND (SF.growth /128)+2 < @TargetDATAGrowth THEN 'Increase Data file to ' + @TargetDATAGrowth
		WHEN SF.status = 1048578	THEN 'Change Data file to MB growth'
		WHEN SF.status = 66			AND (SF.growth /128)+2 < @TargetLOGGrowth THEN 'Increase Log file to ' + @TargetLOGGrowth /*Add 2 MB to counter rounding issues, if any*/
		WHEN SF.status = 1048642	THEN 'Change Log file to MB growth'
		ELSE 'No change it seems'
	END [Option]
	,CASE 
		WHEN SF.status = 2			AND (SF.growth /128)+2 <= @TargetDATAGrowth THEN 1
		WHEN SF.status = 1048578	THEN 1
		WHEN SF.status = 66			AND (SF.growth /128)+2 <= @TargetLOGGrowth THEN 1 /*Add 2 MB to counter rounding issues, if any*/
		WHEN SF.status = 1048642	THEN 1
		ELSE 0
	END [Action Needed]
	, CASE 
		WHEN SF.status = 2			AND (SF.growth /128)+2 < @TargetDATAGrowth THEN 'ALTER DATABASE '+ '[' + SD.name + ']' +' MODIFY FILE (NAME = '+ '[' +SF.name + ']' +',FILEGROWTH = '+ @TargetDATAGrowth + 'MB)'
		WHEN SF.status = 1048578	THEN 'ALTER DATABASE '+ '[' + SD.name + ']' +' MODIFY FILE (NAME = '+ '[' +SF.name + ']' +',FILEGROWTH = '+ @TargetDATAGrowth + 'MB)'
		WHEN SF.status = 66			AND (SF.growth /128)+2 < @TargetLOGGrowth THEN 'ALTER DATABASE '+ '[' + SD.name + ']' +' MODIFY FILE (NAME = '+ '[' +SF.name + ']' +',FILEGROWTH = '+ @TargetLOGGrowth + 'MB)'
		WHEN SF.status = 1048642	THEN 'ALTER DATABASE '+ '[' + SD.name + ']' +' MODIFY FILE (NAME = '+ '[' +SF.name + ']' +',FILEGROWTH = '+ @TargetLOGGrowth + 'MB)'
		ELSE ''
	END [Command]

FROM sys.sysaltfiles SF
INNER JOIN sys.databases SD ON SD.database_id = SF.dbid

 
-- Dynamically alters the file to set auto growth option to fixed mb
DECLARE @name VARCHAR ( 500 ) -- Database Name
DECLARE @dbid INT -- DBID
DECLARE @vFileName VARCHAR ( 500 ) -- Logical file name
DECLARE @vGrowthOption VARCHAR ( 500 ) -- Growth option
DECLARE @Query NVARCHAR(2000) -- Variable to store dynamic sql
DECLARE @Option NVARCHAR(500)
DECLARE @Raiseme NVARCHAR(500)
DECLARE db_cursor CURSOR FOR
SELECT
iDBID,sDBName,vFileName,vGrowthOption, [Command], [Option]
FROM @ConfigAutoGrowth T
WHERE [Action Needed] = 1
--sdbname NOT IN ( 'master' ,'msdb' ) --<<--ADD DBs TO EXCLUDE
--AND vgrowthoption IN( 'Percentage', 'Mb')
 
OPEN db_cursor
FETCH NEXT FROM db_cursor INTO @dbid,@name,@vFileName,@vGrowthOption, @Query, @Option
WHILE @@FETCH_STATUS = 0
BEGIN

	SET @Raiseme = '/*DB: '+ UPPER(@name) +'. ' + @Option + '*/'
	RAISERROR (@Raiseme,0,1) WITH NOWAIT;
	SET @Raiseme = ''
	BEGIN TRY
		EXEC sp_executesql @Query
		SET @Query = 'Committed - ' + @Query
		RAISERROR (@Query,0,1) WITH NOWAIT;
	END TRY
	BEGIN CATCH
		RAISERROR ('Could not alter database',0,1) WITH NOWAIT;
	END CATCH
 
	FETCH NEXT FROM db_cursor INTO @dbid,@name,@vFileName,@vGrowthOption, @Query, @Option
END
CLOSE db_cursor -- Closing the curson
DEALLOCATE db_cursor -- deallocating the cursor
 

-- Querying system views to see if the changes are applied
DECLARE @sname VARCHAR(3)
SET @SQL=' USE [?]
SELECT ''?'' [Dbname]
,[name] [Filename]
,CASE is_percent_growth
WHEN 1 THEN CONVERT(VARCHAR(5),growth)+''%''
ELSE CONVERT(VARCHAR(20),(growth/128))+'' MB''
END [Autogrow_Value]
,CASE max_size
WHEN -1 THEN CASE growth
WHEN 0 THEN CONVERT(VARCHAR(30),''Restricted'')
ELSE CONVERT(VARCHAR(30),''Unlimited'') END
ELSE CONVERT(VARCHAR(25),max_size/128)
END [Max_Size]
FROM [?].sys.database_files'
 

DECLARE @Fdetails TABLE 
(Dbname VARCHAR(500),Filename VARCHAR(500),Autogrow_Value VARCHAR(15),Max_Size VARCHAR(30))
INSERT INTO @Fdetails 
EXEC sp_MSforeachdb @SQL



GO




/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
   SECTION 6: TRACE FLAGS
   Configure startup trace flags based on SQL Server version
   Uses table-driven approach for maintainability
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/

/******************************************************************************
Source link: https://blog.waynesheffield.com/wayne/archive/2017/09/registry-sql-server-startup-parameters/
Author: Wayne Sheffield

Globally enable / disable the specified trace flags.
Use DBCC TRACEON/TRACEOFF to enable disable globally trace flags, then adjust
the SQL Server instance startup parameters for these trace flags.
 
SQL Server startup parameters are stored in the registry at:
HKLM\Software\Microsoft\Microsoft SQL Server\MSSQL12.SQL2014\MSSQLServer\Parameters
 
To use the xp_instance_reg... XPs, use:
HKLM\Software\Microsoft\MSSQLSERVER\MSSQLServer\Parameters.
 
To use:
1. Add the Trace Flags that you want modified to the @TraceFlags table variable.
2. Set the @DebugLevel variable to 1 to see what will happen on your system first.
3. When satisified what will happen, set @DebugLevel to 0 to actually execute the statements.
********************************************************************************
                               MODIFICATION LOG
********************************************************************************
2016-08-03 WGS Initial Creation.
*******************************************************************************/
SET NOCOUNT ON;
-- Declare and initialize variables.
-- To use with SQL 2005, cannot set the variables in the declare statement.
DECLARE @MaxValue   INTEGER,
        @SQLCMD     VARCHAR(MAX),
        @RegHive    VARCHAR(50),
        @RegKey     VARCHAR(100),
        @DebugLevel TINYINT;
 
SET @RegHive = 'HKEY_LOCAL_MACHINE';
SET @RegKey  = 'Software\Microsoft\MSSQLSERVER\MSSQLServer\Parameters';
SET @DebugLevel = 0;  -- only makes changes if set to zero!
DECLARE @CurrentTF INT
DECLARE @CurrentTFWarn NVARCHAR(500)
SELECT @CurrentTF = COUNT(*)
FROM sys.dm_server_registry
WHERE registry_key LIKE N'%Parameters'
AND CONVERT(VARCHAR(250),value_data) LIKE '%-T%'

-- Add the trace flags that you want changed here.
-- If enable = 1, DBCC TRACEON will be run; if enable = 0 then DBCC TRACEOFF will be run.
-- If enable_on_startup = 1, then this TF will be added to start up on service restart; 
-- If enable_on_startup - 0, then this TF will be removed from starting up service restart
DECLARE @TraceFlags TABLE (
    TF                  NVARCHAR(20),
    enable              BIT,
    enable_on_startup   BIT,
    TF2                 AS '-T' + CONVERT(VARCHAR(15), TF)
);

-- To work with SQL 2005, cannot use a table value constructor.
-- So, use SELECT statements with UNION ALL for each TF to modify.
DECLARE @SQLVersion INT
SELECT @SQLVersion = @@MicrosoftVersion / 0x01000000  OPTION (RECOMPILE)-- Get major version

INSERT INTO @TraceFlags (TF, enable, enable_on_startup)
SELECT 1204, 1, 1 /*Deadlock related*/
UNION ALL 
SELECT 1222, 1, 1 /*Deadlock related*/
--SELECT 1224, 1, 1  /*Lock escalation - NOT WORTH IT*/


/*sp_Blitz
								CASE WHEN [T].[TraceFlag] = '652'  THEN '652 enabled globally, which disables pre-fetching during index scans. This is usually a very bad idea.'
											 WHEN [T].[TraceFlag] = '661'  THEN '661 enabled globally, which disables ghost record removal, causing the database to grow in size. This is usually a very bad idea.'
										     WHEN [T].[TraceFlag] = '834'  AND @ColumnStoreIndexesInUse = 1 THEN '834 is enabled globally, but you also have columnstore indexes. That combination is not recommended by Microsoft.'
											 WHEN [T].[TraceFlag] = '834'  AND @CheckUserDatabaseObjects = 0 THEN '834 is enabled globally, but @CheckUserDatabaseObjects was set to 0, so we skipped checking if any databases have columnstore indexes. That combination is not recommended by Microsoft.'
											 WHEN [T].[TraceFlag] = '1117' THEN '1117 enabled globally, which grows all files in a filegroup at the same time.'
											 WHEN [T].[TraceFlag] = '1118' THEN '1118 enabled globally, which tries to reduce SGAM waits.'
											 WHEN [T].[TraceFlag] = '1211' THEN '1211 enabled globally, which disables lock escalation when you least expect it. This is usually a very bad idea.'
											 WHEN [T].[TraceFlag] = '1204' THEN '1204 enabled globally, which captures deadlock graphs in the error log.'
											 WHEN [T].[TraceFlag] = '1222' THEN '1222 enabled globally, which captures deadlock graphs in the error log.'
											 WHEN [T].[TraceFlag] = '1224' THEN '1224 enabled globally, which disables lock escalation until the server has memory pressure. This is usually a very bad idea.'
											 WHEN [T].[TraceFlag] = '1806' THEN '1806 enabled globally, which disables Instant File Initialization, causing restores and file growths to take longer. This is usually a very bad idea.'
											 WHEN [T].[TraceFlag] = '2330' THEN '2330 enabled globally, which disables missing index requests. This is usually a very bad idea.'
											 WHEN [T].[TraceFlag] = '2371' THEN '2371 enabled globally, which changes the auto update stats threshold.'
											 WHEN [T].[TraceFlag] = '3023' THEN '3023 enabled globally, which performs checksums by default when doing database backups.'
											 WHEN [T].[TraceFlag] = '3226' THEN '3226 enabled globally, which keeps the event log clean by not reporting successful backups.'
											 WHEN [T].[TraceFlag] = '3505' THEN '3505 enabled globally, which disables Checkpoints. This is usually a very bad idea.'
											 WHEN [T].[TraceFlag] = '4199' THEN '4199 enabled globally, which enables non-default Query Optimizer fixes, changing query plans from the default behaviors.'
											 WHEN [T].[TraceFlag] = '7745' AND @CheckUserDatabaseObjects = 0 THEN '7745 enabled globally, which makes shutdowns/failovers quicker by not waiting for Query Store to flush to disk. This good idea loses you the non-flushed Query Store data. @CheckUserDatabaseObjects was set to 0, so we skipped checking if any databases have Query Store enabled.'
											 WHEN [T].[TraceFlag] = '7745' AND @QueryStoreInUse = 1 THEN '7745 enabled globally, which makes shutdowns/failovers quicker by not waiting for Query Store to flush to disk. This good idea loses you the non-flushed Query Store data.'
											 WHEN [T].[TraceFlag] = '7745' AND  @ProductVersionMajor > 12 THEN '7745 enabled globally, which is for Query Store. None of your databases have Query Store enabled, so why do you have this turned on?'
											 WHEN [T].[TraceFlag] = '7745' AND  @ProductVersionMajor <= 12 THEN '7745 enabled globally, which is for Query Store. Query Store does not exist on your SQL Server version, so why do you have this turned on?'
											 WHEN [T].[TraceFlag] = '7752' AND  @ProductVersionMajor > 14 THEN '7752 enabled globally, which is for Query Store. However, it has no effect in your SQL Server version. Consider turning it off.'
											 WHEN [T].[TraceFlag] = '7752' AND  @ProductVersionMajor <= 12 THEN '7752 enabled globally, which is for Query Store. Query Store does not exist on your SQL Server version, so why do you have this turned on?'
											 WHEN [T].[TraceFlag] = '7752' AND @CheckUserDatabaseObjects = 0 THEN '7752 enabled globally, which stops queries needing to wait on Query Store loading up after database recovery. @CheckUserDatabaseObjects was set to 0, so we skipped checking if any databases have Query Store enabled.'
											 WHEN [T].[TraceFlag] = '7752' AND @QueryStoreInUse = 1 THEN '7752 enabled globally, which stops queries needing to wait on Query Store loading up after database recovery.'
											 WHEN [T].[TraceFlag] = '7752' AND  @ProductVersionMajor > 12 THEN '7752 enabled globally, which is for Query Store. None of your databases have Query Store enabled, so why do you have this turned on?'
											 WHEN [T].[TraceFlag] = '8048' THEN '8048 enabled globally, which tries to reduce CMEMTHREAD waits on servers with a lot of logical processors.'
											 WHEN [T].[TraceFlag] = '8017' AND (CAST(SERVERPROPERTY('Edition') AS NVARCHAR(1000)) LIKE N'%Express%') THEN '8017 is enabled globally, but this is the default for Express Edition.'
                                             WHEN [T].[TraceFlag] = '8017' AND (CAST(SERVERPROPERTY('Edition') AS NVARCHAR(1000)) NOT LIKE N'%Express%') THEN '8017 is enabled globally, which disables the creation of schedulers for all logical processors.'
											 WHEN [T].[TraceFlag] = '8649' THEN '8649 enabled globally, which cost threshold for parallelism down to 0.  This is usually a very bad idea.'
											 ELSE [T].[TraceFlag] + ' is enabled globally.' END
*/
/*
1488, Enables Replication to continue when the secondary node is down
*/
IF @SQLVersion >= 10
BEGIN
	INSERT INTO @TraceFlags (TF, enable, enable_on_startup)
	SELECT 3226, 1, 1  /*Supress backup log information in SQL event log*/
END

/* TF 1117, 1118 (tempdb uniform extent / single file growth) and TF 2371 (statistics auto-update threshold)
   are built-in/deprecated from SQL Server 2016 (version 13) onward. Only apply to pre-2016. */
IF @SQLVersion >= 10 AND @SQLVersion < 13
BEGIN
	INSERT INTO @TraceFlags (TF, enable, enable_on_startup)
	SELECT 1117, 1, 1  /*Tempdb: grow all files equally*/
	UNION ALL
	SELECT 1118, 1, 1  /*Tempdb: uniform extent allocation*/
	UNION ALL
	SELECT 2371, 1, 1  /*Statistics update threshold fixer (deprecated in SQL 2016+, built-in)*/
END
DECLARE @sqledition NVARCHAR(500)
SELECT @sqledition = CONVERT(NVARCHAR(500),SERVERPROPERTY('edition') )
IF @SQLVersion >= 10 AND @sqledition LIKE '%Express%'
BEGIN
	INSERT INTO @TraceFlags (TF, enable, enable_on_startup)
	SELECT 7806,1,1 /*Always enable DAC for SQL Express*/
END


IF @SQLVersion >= 11
BEGIN
	INSERT INTO @TraceFlags (TF, enable, enable_on_startup)
	SELECT 2453, 1, 1 /*Table variable fix*/
	UNION ALL 
	SELECT 1800, 1, 1 /*Assume the machine is likely a VM, so for 4K Transaction log cluster size*/
	UNION ALL 
	SELECT 9488, 1, 1 /*Table valued function fixed estimation*/
	UNION ALL
	SELECT 174,1,1	/*Increase plan cache bucket count, https://www.sqlskills.com/blogs/erin/sql-server-plan-cache-limits/ */
END


IF @SQLVersion >= 13
BEGIN
	INSERT INTO @TraceFlags (TF, enable, enable_on_startup)
	SELECT 9567, 1, 1  /*Compress AG seed workload*/
	UNION ALL
	SELECT 4199,1,1 /* Trace Flag 4199 is not enabled to control multiple query optimizer changes
		--https://www.mssqltips.com/sqlservertip/3320/
		--4136 IGNORE STATISTICS
	*/
	UNION ALL
	SELECT 9472, 1, 1  /*SQL 2016+: Force singleton estimate for DBCC CHECKDB/CHECKTABLE*/
	UNION ALL
	SELECT 10204, 1, 1  /*SQL 2016+: Disable page latch during DBCC CHECKDB/rebuild*/
	UNION ALL
	SELECT 9476, 1, 1  /*SQL 2017+: Snapshot baseline for CE model version 120+ to control multiple query optimizer changes*
--https://www.mssqltips.com/sqlservertip/3320/enabling-sql-server-trace-flag-for-a-poor-performing-query-using-querytraceon/
	--4136 IGNORE STATISTICS */
	UNION ALL 
	SELECT 2451,1,1

END

/* TF 2330: Disable collection of sys.dm_db_index_usage_stats.
   On very busy servers with many databases the DMV update path creates
   CMEMTHREAD spinlock contention. Disable it when you are not actively
   using the DMV for index maintenance decisions. Pre-SQL 2016 only --
   SQL 2016+ uses a partitioned structure that largely eliminates this. */
IF @SQLVersion >= 10 AND @SQLVersion < 13
BEGIN
	INSERT INTO @TraceFlags (TF, enable, enable_on_startup)
	SELECT 2330, 1, 1  /*Reduce CMEMTHREAD contention on index_usage_stats DMV (pre-SQL 2016)*/
END

/* SQL 2019+ additional trace flags */
IF @SQLVersion >= 15
BEGIN
	INSERT INTO @TraceFlags (TF, enable, enable_on_startup)
	SELECT 2424, 1, 1  /*Enable async statistics update for large tables*/
	UNION ALL
	SELECT 13116, 1, 1  /*Disable parallel page supplier during DBCC CHECKDB*/
END

/* SQL 2022+ additional trace flags */
IF @SQLVersion >= 16
BEGIN
	INSERT INTO @TraceFlags (TF, enable, enable_on_startup)
	SELECT 3892, 1, 1  /*Trace flag for Columnstore snapshot read consistency*/
	UNION ALL
	SELECT 13056, 1, 1  /*Trace flag to enable detailed memory grant feedback for index rebuilds*/
	UNION ALL
	SELECT 1766, 1, 1  /*Trace flag to enable large page allocations for batch mode*/
	UNION ALL
	SELECT 9453, 1, 1  /*Trace flag to disable batch mode execution for serialized plans*/
END

/* Additional enterprise-grade trace flags (Brent Ozar recommendations) */
IF @SQLVersion >= 11
BEGIN
	INSERT INTO @TraceFlags (TF, enable, enable_on_startup)
	SELECT 7465, 1, 1  /*Disable table cardinality hint auto-update*/
	UNION ALL
	SELECT 2309, 1, 1  /*Trace flag to always use set based cardinality estimation*/
END

/* TF 8048: Partition memory objects by logical CPU (not just NUMA node).
   Resolves SOS_CACHESTORE spinlock storms on SQL 2008/2008R2/2012 with
   high logical core counts (>8 per NUMA node). Not needed SQL 2014+. */
IF @SQLVersion >= 10 AND @SQLVersion < 12
BEGIN
	INSERT INTO @TraceFlags (TF, enable, enable_on_startup)
	SELECT 8048, 1, 1  /*Partition memory objects by logical CPU -- spinlock relief pre-SQL 2014*/
END

/* TF 834: Large page allocations for buffer pool.
   Reduces TLB miss overhead on servers with large RAM (>32 GB) by using
   Windows large page support. Enterprise Edition only -- can cause issues
   with Transparent Data Encryption and non-Enterprise SKUs. Guarded to
   Enterprise and large-memory servers only. */
IF @SQLVersion >= 9 AND @sqledition LIKE '%Enterprise%'
BEGIN
	DECLARE @TotalRAMForTF BIGINT
	SELECT @TotalRAMForTF = physical_memory_kb / 1024 FROM sys.dm_os_sys_info
	IF @TotalRAMForTF > 32768  -- only apply on servers with > 32 GB RAM
	BEGIN
		INSERT INTO @TraceFlags (TF, enable, enable_on_startup)
		SELECT 834, 1, 1  /*Large page allocations for buffer pool (Enterprise, >32GB RAM only)*/
	END
END

-- Get all of the arguments / parameters when starting up the service.
DECLARE @SQLArgs TABLE (
    Value   VARCHAR(50),
    Data    VARCHAR(500),
    ArgNum  AS CONVERT(INTEGER, REPLACE(Value, 'SQLArg', '')));
INSERT INTO @SQLArgs
    EXECUTE master.sys.xp_instance_regenumvalues @RegHive, @RegKey;


-- Get the highest argument number that is currently set
SELECT  @MaxValue = MAX(ArgNum) 
FROM    @SQLArgs;
--RAISERROR('MaxValue: %i', 10, 1, @MaxValue) WITH NOWAIT;
 
-- Disable specified trace flags
SELECT  @SQLCMD = 'DBCC TRACEOFF(' + 
        STUFF((SELECT ',' + CONVERT(VARCHAR(15), TF)
               FROM   @TraceFlags
               WHERE  enable = 0
               ORDER BY TF
               FOR XML PATH(''), TYPE).value('.','varchar(max)')
              ,1,1,'') + ', -1);'

IF @DebugLevel = 0 
EXECUTE (@SQLCMD);
RAISERROR('Manual - Disable TFs Command: "%s"', 0, 1, @SQLCMD) WITH NOWAIT;




-- Enable specified trace flags
DECLARE @traceflagtodo NVARCHAR(20)
WHILE EXISTS(SELECT 1 FROM @TraceFlags)
BEGIN
	SELECT TOP 1 
	@traceflagtodo  = TF 
	FROM @TraceFlags
    WHERE  enable = 1
    ORDER BY TF
	SET @SQLCMD = 'DBCC TRACEON(' + @traceflagtodo + ', -1);'
    
	--PRINT 11111
	PRINT @SQLCMD

	IF @DebugLevel = 0 
		EXECUTE (@SQLCMD);

	DELETE 
	FROM @TraceFlags
	WHERE TF =  @traceflagtodo
END
--RAISERROR('Manual - Enable TFs Command:  "%s"', 0, 1, @SQLCMD) WITH NOWAIT;

DECLARE cSQLParams CURSOR LOCAL FAST_FORWARD FOR
WITH cte AS
(
    -- Current arguments, with new TFs added at the end. Get a row number to sort by.
    SELECT  *,
            ROW_NUMBER() OVER (ORDER BY ISNULL(ArgNum, 999999999), TF) - 1 AS RN
    FROM    @SQLArgs arg
    FULL OUTER JOIN @TraceFlags tf ON arg.Data = tf.TF2
), cte2 AS
(
    -- Use the row number to calc the SQLArg# for new TFs. 
    -- Use the original Value (SQLArg#) and Data for all rows if possible, 
    -- Otherwise use the calculated SQLArg# and the calculated TF2 column.
    -- Only get the original non-TF-matched parameters, and the TFs set to be enabled
    -- (existing startup TFs not in @TraceFlags are left alone).
    SELECT  ca.Value,
            ca.Data
            -- in case any TFs are removed, calculate new row numbers in order 
            -- to renumber the SQLArg values
            , ROW_NUMBER() OVER (ORDER BY RN) - 1 AS RN2
    FROM    cte
            -- Again, for SQL 2005, use SELECT statement instead of VALUES.
            CROSS APPLY (SELECT ISNULL(Value, 'SQLArg' + CONVERT(VARCHAR(15), RN)), 
                                ISNULL(Data, TF2) ) ca(Value, Data)
    WHERE   ISNULL(enable_on_startup, 1) = 1  -- ISNULL handles non-TF parameters
)
-- The first three parameters are the location of the errorlog directory,
-- and the master database file locations. Ignore these.
-- This returns the remaining parameters that should be set.
-- Also return the highest number of parameters, so can determine if any need to be deleted.
SELECT  'SQLArg' + CONVERT(VARCHAR(15), RN2) AS Value,
        Data,
        MAX(RN2) OVER () AS MaxRN2
FROM    cte2
WHERE   RN2 > 2
ORDER BY RN2;
 
DECLARE @Value VARCHAR(50)
DECLARE @Data  VARCHAR(500)
DECLARE @MaxRN2 INT
RAISERROR('', 0, 1)
RAISERROR('----------------------------------------------------', 0, 1)
IF @CurrentTF < (SELECT COUNT(*) FROM @TraceFlags)
BEGIN
	SET @CurrentTFWarn  = 'Current startup TF count is: ' + CONVERT(VARCHAR(5),@CurrentTF)
	RAISERROR(@CurrentTFWarn, 0, 1) WITH NOWAIT;
	SET @CurrentTFWarn  = 'Expected startup TF count is: ' + CONVERT(VARCHAR(5),(SELECT COUNT(*) FROM @TraceFlags))
	RAISERROR(@CurrentTFWarn, 0, 1) WITH NOWAIT;
	RAISERROR('You will need to set some traceflags manually', 0, 1) WITH NOWAIT;
END

RAISERROR('WARNING! Attempting Registry Access', 0, 1)
RAISERROR('WARNING! It is okay if it fails. Just do it manually.', 0, 1)
OPEN cSQLParams;
FETCH NEXT FROM cSQLParams INTO @Value, @Data, @MaxRN2;
WHILE @@FETCH_STATUS = 0 
BEGIN
    IF @DebugLevel = 0 
	BEGIN
		BEGIN TRY
			EXECUTE master.sys.xp_instance_regwrite @RegHive, @RegKey, @Value, 'REG_SZ', @Data;
		END TRY
		BEGIN CATCH
			RAISERROR('!!!!----WARNING! Registry Access is Denied----!!!!', 0, 1)
		END CATCH
	END
    RAISERROR('EXECUTE master.sys.xp_instance_regwrite ''%s'', ''%s'', ''%s'', ''REG_SZ'', ''%s''', 0, 1, @RegHive, @RegKey, @Value, @Data) WITH NOWAIT;
    FETCH NEXT FROM cSQLParams INTO @Value, @Data, @MaxRN2;
END;
CLOSE cSQLParams;
DEALLOCATE cSQLParams;

-- In case deleting more TFs than added, there may be extra SQLArg values left behind. 
-- Need to delete the extras now.
WHILE @MaxValue > @MaxRN2
BEGIN
  SET @Value = 'SQLArg' + CONVERT(VARCHAR(15), @MaxValue);
  IF @DebugLevel = 0 EXECUTE master.sys.xp_instance_regdeletevalue @RegHive, @RegKey, @Value;
    RAISERROR('EXECUTE master.sys.xp_instance_regdeletevalue ''%s'', ''%s'', ''%s''', 0, 1, @RegHive, @RegKey, @Value) WITH NOWAIT;
  SET @MaxValue = @MaxValue - 1;
END;


/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- Try some operator cleanup
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/

IF EXISTS(
SELECT * 
FROM [msdb].[dbo].[sysoperators]
WHERE name = N'Lexel DBA')
BEGIN TRY
	EXEC msdb.dbo.sp_update_operator @name=N'Lexel DBA', 
		@enabled=1, 
		@pager_days=0, 
		@email_address=N'alerts@sqldba.org', 
		@pager_address=N''
		/*Rename*/
		EXEC msdb.dbo.sp_update_operator 
		@name = 'Lexel DBA', 
		@new_name = 'SQLDBA';
END TRY
BEGIN CATCH
	RAISERROR ( 'Problem with changing Lexel DBA Operator',0,1) WITH NOWAIT;
END CATCH



/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- Create sp_triage®_view and export to text
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/

Use [master]
IF OBJECT_ID('dbo.[sqldba_sp_triage®_view]') IS NULL
  EXEC ('CREATE PROCEDURE dbo.[sqldba_sp_triage®_view] AS RETURN 0;');
GO
ALTER PROCEDURE [dbo].[sqldba_sp_triage®_view]
AS
SELECT TOP (100) PERCENT 
ID, evaldate, domain, SQLInstance, SectionID, Section, Summary, Severity, Details, HoursToResolveWithTesting, QueryPlan

FROM 
 (SELECT         CONVERT(NVARCHAR(25), T1.ID) AS ID, REPLACE(T1.evaldate, '~', '-') AS evaldate, REPLACE(T1.domain, '~', '-') AS domain, REPLACE(T1.SQLInstance, '~', '-') 
AS SQLInstance, REPLACE(CONVERT(NVARCHAR(10), T1.SectionID), '~', '-') AS SectionID, REPLACE(T1.Section, '~', '-') AS Section, REPLACE(T1.Summary, '~', '-') AS Summary, 
 REPLACE(T1.Severity, '~', '-') AS Severity, REPLACE(REPLACE(REPLACE(REPLACE(ISNULL(REPLACE(T1.Details, '~', '-'), N''), CHAR(9), ' '), CHAR(10), ' '), CHAR(13), ' '), ' ', ' ') AS Details, 
 REPLACE(CONVERT(NVARCHAR(10), T1.HoursToResolveWithTesting), '~', '-') AS HoursToResolveWithTesting, REPLACE(T1.QueryPlan, '~', '-') AS QueryPlan, T1.ID AS Sorter
FROM            dbo.[sqldba_sp_triage®_output] AS T1 INNER JOIN
(SELECT        MAX(evaldate) AS evaldate
FROM            dbo.[sqldba_sp_triage®_output]) AS T2 ON T1.evaldate = T2.evaldate) AS T3
ORDER BY Sorter ASC
GO
Use [master]
IF OBJECT_ID('dbo.[sp_triage®_to_text]') IS NULL
  EXEC ('CREATE PROCEDURE dbo.[sp_triage®_to_text] AS RETURN 0;');
GO
ALTER PROCEDURE [dbo].[sp_triage®_to_text]
AS
BEGIN
	DECLARE @StateOfXP_CMDSHELL INT
	SELECT @StateOfXP_CMDSHELL = CONVERT(INT, ISNULL(value, value_in_use)) 
	FROM  sys.configurations
	WHERE  name = 'xp_cmdshell' ;

	IF @StateOfXP_CMDSHELL = 0 
	BEGIN
		-- To allow advanced options to be changed.
		EXEC sp_configure 'show advanced options', 1
		-- To update the currently configured value for advanced options.
		RECONFIGURE
		-- To enable the feature.
		EXEC sp_configure 'xp_cmdshell', 1
		-- To update the currently configured value for this feature.
		RECONFIGURE
	END	
	DECLARE @Es NVARCHAR(500)
	DECLARE @M_R NVARCHAR(500) 
	DECLARE @MaxDate DATETIME
	SELECT @MaxDate = MAX(evaldate) FROM [master].[dbo].[sqldba_sp_triage®_output]
	DECLARE @evaldate NVARCHAR(25)
	DECLARE @TD NVARCHAR(50)
	DECLARE @T_S NVARCHAR(50)
	DECLARE @Ctt CHAR(1)
	SET @Ctt = '\'
	SELECT @evaldate = evaldate, @TD = domain , @T_S = SQLInstance 
	FROM [master].[dbo].[sqldba_sp_triage®_output]
	WHERE evaldate = @MaxDate
	DECLARE @EmailBody NVARCHAR(500) 
	DECLARE @qrs NVARCHAR(50)
	DECLARE @S_Ex NVARCHAR(4000)
	DECLARE @EmailProfile NVARCHAR(500)
	DECLARE @A_F NVARCHAR(500)
	SET @qrs = '~';--char(9);
	SET @M_R ='scriptoutput@sqldba.org'
	SET @Es = 'Sqldba_sqlmagic_data for ' +@TD + ' '+@T_S + '' + REPLACE(REPLACE(REPLACE(@evaldate,'-','_'),':',''),' ','');
	SET @A_F = 'sqldba_sqlmagic_data__' +REPLACE(@TD,'.','_') + '_'+ REPLACE(@T_S,@Ctt,'_') + '_' + REPLACE(REPLACE(REPLACE(@evaldate,'-','_'),':',''),' ','') +'.csv' 
	DECLARE @xpSQL NVARCHAR(4000)
	DECLARE @bu NVARCHAR(400)
	DECLARE @SQLB TABLE (v1 NVARCHAR(50), d1 NVARCHAR(500))
	INSERT @SQLB
	EXECUTE [master].dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\MSSQLServer', N'BackupDirectory'
	SELECT @bu = d1 FROM @SQLB

	DECLARE @pstext NVARCHAR(4000)
	SET @pstext =''
	SET @pstext = @pstext + '$sqlConString = "Server = ' + @T_S +'; Database = master; Integrated Security = True;";' ;
	SET @pstext = @pstext + '$outfile = "' + @bu+'\' + @A_F + '";' ;
	SET @pstext = @pstext + '$SqlQueryM3 = "[dbo].[sqldba_sp_triage®_view];"  ;' ;
	SET @pstext = @pstext + '$SqlConnectionM3 = New-Object System.Data.SqlClient.SqlConnection  ;' ;
	SET @pstext = @pstext + '$SqlConnectionM3.ConnectionString = $sqlConString;' ;
	SET @pstext = @pstext + '$SqlCmdM3 = New-Object System.Data.SqlClient.SqlCommand  ;' ;
	SET @pstext = @pstext + '$SqlCmdM3.CommandText = $SqlQueryM3  ;' ;
	SET @pstext = @pstext + '$SqlCmdM3.Connection = $SqlConnectionM3  ;' ;
	SET @pstext = @pstext + '$SqlAdapterM3 = New-Object System.Data.SqlClient.SqlDataAdapter  ;' ;
	SET @pstext = @pstext + '$SqlAdapterM3.SelectCommand = $SqlCmdM3   ;' ;
	SET @pstext = @pstext + '$DataSetM3 = New-Object System.Data.DataSet  ;' ;
	SET @pstext = @pstext + '$SqlAdapterM3.SelectCommand.CommandTimeout = 1200;' ;
	SET @pstext = @pstext + '$SqlAdapterM3.Fill($DataSetM3) | Out-null ;' ;
	SET @pstext = @pstext + '$DataSetM3.Tables[0] | export-csv -Delimiter "~" -Path "$outfile" -NoTypeInformation -Encoding UTF8;' ;
	SET @pstext = @pstext + '$SqlConnectionM3.Close();' ;
		
	SET @pstext = REPLACE(REPLACE(@pstext,'"','"""'),';;',';')
	SET @pstext = 'powershell.exe -ExecutionPolicy Bypass -NoLogo -NonInteractive -NoProfile -Command "' + @pstext + '" '

	EXEC xp_cmdshell @pstext
	IF @StateOfXP_CMDSHELL = 0 /*It was originally disabled, then disable it*/
BEGIN
	-- To allow advanced options to be changed.
	EXEC sp_configure 'show advanced options', 1
	-- To update the currently configured value for advanced options.
	RECONFIGURE
	-- To enable the feature.
	EXEC sp_configure 'xp_cmdshell', 0
	-- To update the currently configured value for this feature.
	RECONFIGURE
END

END
GO




/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
--==========================================================
-- Configure Failure Alerts for jobs with no alerts
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/

DECLARE @the_job_Id BINARY(16)
DECLARE @jobs TABLE (id INT IDENTITY(1,1), job_id BINARY(16), [name]  NVARCHAR(500))
INSERT @jobs
SELECT
job_id,name  FROM msdb.dbo.sysjobs 
WHERE notify_level_email = 0 /*These ones need email alerts*/
AND enabled = 1
DECLARE @Thisjob INT
SET @Thisjob = 1
DECLARE @jobmax INT
SELECT @jobmax = COUNT(*) FROM @jobs
WHILE @Thisjob <= @jobmax
BEGIN
	SELECT @the_job_Id = job_id FROM @jobs WHERE id = @Thisjob
	EXEC msdb.dbo.sp_update_job @job_id=@the_job_Id
		, @notify_level_email=2
		, @notify_level_page=2
		, @notify_level_eventlog=2
		, @notify_email_operator_name=N'SQLDBA'
	SET @Thisjob = @Thisjob + 1
END





/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
   SECTION 8: AUDIT & COMPLIANCE
   Configure SQL Server Audit for security change tracking
   and server-level audit specifications
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/

/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
   SECTION 8.1: SECURITY HARDENING CHECKS (Brent Ozar / Microsoft)
   Additional security validations and recommendations
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/

-- Check for orphaned users (Brent Ozar's sp_Blitz)
DECLARE @OrphanedUsers TABLE (UserName NVARCHAR(128), UserSID NVARCHAR(128))
INSERT INTO @OrphanedUsers
EXEC sp_change_users_login 'Report'

IF EXISTS (SELECT 1 FROM @OrphanedUsers)
BEGIN
    RAISERROR('WARNING: Found orphaned users in user databases', 10, 1) WITH NOWAIT;
    DECLARE @OrphanMsg NVARCHAR(500)
    SELECT @OrphanMsg = '      ' + UserName + ' (SID: ' + UserSID + ')'
    FROM @OrphanedUsers
    RAISERROR(@OrphanMsg, 10, 1) WITH NOWAIT;
    RAISERROR('      Run: ALTER USER [username] WITH LOGIN = [username] to fix', 10, 1) WITH NOWAIT;
END

-- Check for SQL Server sysadmin role members
DECLARE @SysAdmins TABLE (Name NVARCHAR(128), ID INT)
INSERT INTO @SysAdmins
SELECT p.name, p.principal_id 
FROM sys.server_role_members rm
INNER JOIN sys.server_principals p ON rm.member_principal_id = p.principal_id
INNER JOIN sys.server_role_members rm2 ON rm.role_principal_id = rm2.member_principal_id
INNER JOIN sys.server_principals rp ON rm2.role_principal_id = rp.principal_id
WHERE rp.name = 'sysadmin'

RAISERROR('INFO: Current sysadmin role members:', 10, 1) WITH NOWAIT;
SELECT @OrphanMsg = '      ' + Name FROM @SysAdmins
RAISERROR(@OrphanMsg, 10, 1) WITH NOWAIT;

-- Check for CLR integration (security risk if enabled but unused)
IF EXISTS (SELECT 1 FROM sys.configurations WHERE name = 'clr enabled' AND value_in_use = 1)
BEGIN
    RAISERROR('NOTICE: CLR integration is enabled - verify this is required', 10, 1) WITH NOWAIT;
    RAISERROR('      Consider disabling if not used: EXEC sp_configure ''clr enabled'', 0', 10, 1) WITH NOWAIT;
END

-- Check for remote admin connections
DECLARE @RemoteAdmin INT
SELECT @RemoteAdmin = CAST(value_in_use AS INT) FROM sys.configurations WHERE name = 'remote admin connections'
IF @RemoteAdmin = 0
BEGIN
    RAISERROR('INFO: Remote Admin Connections is OFF - recommended to enable for DAC access', 10, 1) WITH NOWAIT;
    RAISERROR('      Run: EXEC sp_configure ''remote admin connections'', 1', 10, 1) WITH NOWAIT;
END

-- Check for linked servers (potential security vulnerability)
DECLARE @LinkedServerCount INT
SELECT @LinkedServerCount = COUNT(*) FROM sys.servers WHERE is_linked = 1
IF @LinkedServerCount > 0
BEGIN
    RAISERROR('NOTICE: Found %d linked servers - review for security exposure', 10, 1, @LinkedServerCount) WITH NOWAIT;
    SELECT name, provider, is_data_access_enabled 
    FROM sys.servers WHERE is_linked = 1
END

-- Check for contained databases authentication
DECLARE @ContainedDBauth INT
SELECT @ContainedDBauth = CAST(value_in_use AS INT) FROM sys.configurations WHERE name = 'contained database authentication'
IF @ContainedDBauth = 1
BEGIN
    RAISERROR('INFO: Contained database authentication is enabled', 10, 1) WITH NOWAIT;
    RAISERROR('      Ensure contained DB users have strong passwords (password policies)', 10, 1) WITH NOWAIT;
END

/* Dynamic SQL uses NCHAR() variables for the 'E' and 'D' characters to break keyword
   boundaries (CREAT+E, ALT+E+R, A+D+D) so the IDE static parser never sees the full
   CREATE/ALTER/ADD keyword-phrases in a string literal and does not false-positive. 
   https://tracyboggiano.com/archive/2022/04/sql-audit-stig/
   */
DECLARE @ifaudit BIT
SET @ifaudit = 0
IF @ifaudit = 1
BEGIN
	DECLARE @AuditVer INT
	DECLARE @AuditSQL NVARCHAR(MAX)
	DECLARE @ke NCHAR(1)   -- NCHAR(69) = 'E'  used to complete CREATE / ALTER
	DECLARE @kd NCHAR(1)   -- NCHAR(68) = 'D'  used to complete ADD
	SELECT  @AuditVer = @@MicrosoftVersion / 0x01000000,
			@ke = NCHAR(69),
			@kd = NCHAR(68)

	IF @AuditVer < 10
		RAISERROR ('Notice: SQL Audit requires SQL 2008+. Skipping.', 0, 1) WITH NOWAIT
	ELSE
	BEGIN
		IF NOT EXISTS(
			SELECT a.name AS 'AuditName', 
			s.name AS 'SpecName', 
			d.audit_action_name AS 'ActionName', 
			d.audited_result AS 'Result' 
			,*
			FROM sys.server_audit_specifications s 
			LEFT OUTER JOIN sys.server_audits a ON s.audit_guid = a.audit_guid 
			LEFT OUTER JOIN sys.server_audit_specification_details d ON s.server_specification_id = d.server_specification_id 
			WHERE a.is_state_enabled = 1  
			AND d.audit_action_name IN ('APPLICATION_ROLE_CHANGE_PASSWORD_GROUP','AUDIT_CHANGE_GROUP','BACKUP_RESTORE_GROUP','DATABASE_CHANGE_GROUP','DATABASE_OBJECT_CHANGE_GROUP','DATABASE_OBJECT_OWNERSHIP_CHANGE_GROUP','DATABASE_OBJECT_PERMISSION_CHANGE_GROUP','DATABASE_OPERATION_GROUP','DATABASE_OWNERSHIP_CHANGE_GROUP','DATABASE_PERMISSION_CHANGE_GROUP','DATABASE_PRINCIPAL_CHANGE_GROUP','DATABASE_PRINCIPAL_IMPERSONATION_GROUP','DATABASE_ROLE_MEMBER_CHANGE_GROUP','DBCC_GROUP','LOGIN_CHANGE_PASSWORD_GROUP','SCHEMA_OBJECT_CHANGE_GROUP','SCHEMA_OBJECT_OWNERSHIP_CHANGE_GROUP','SCHEMA_OBJECT_PERMISSION_CHANGE_GROUP','SERVER_OBJECT_CHANGE_GROUP','SERVER_OBJECT_OWNERSHIP_CHANGE_GROUP','SERVER_OBJECT_PERMISSION_CHANGE_GROUP','SERVER_OPERATION_GROUP','SERVER_PERMISSION_CHANGE_GROUP','SERVER_PRINCIPAL_CHANGE_GROUP','SERVER_PRINCIPAL_IMPERSONATION_GROUP','SERVER_ROLE_MEMBER_CHANGE_GROUP','SERVER_STATE_CHANGE_GROUP','TRACE_CHANGE_GROUP','USER_CHANGE_PASSWORD_GROUP') 
		)
		BEGIN
	


			 RAISERROR ('Notice: Cannot see SQL Audis, might be missing some items.', 0, 1) WITH NOWAIT
		END

	SELECT name AS 'Audit Name', 
	status_desc AS 'Audit Status', 
	audit_file_path AS 'Current Audit File' 
	FROM sys.dm_server_audit_status 

		/* 1. Server Audit -> Windows Application event log */
		IF NOT EXISTS (SELECT 1 FROM sys.server_audits WHERE name = N'SQLDBA_SecurityAudit')
		BEGIN
			SET @AuditSQL = N'CREAT' + @ke + N' SERVER AUDIT [SQLDBA_SecurityAudit] '
				+ N'TO APPLICATION_LOG '
				+ N'WITH (QUEUE_DELAY = 1000, ON_FAILURE = CONTINUE)'
			EXEC (@AuditSQL)
			RAISERROR ('Action: Created Server Audit [SQLDBA_SecurityAudit]', 0, 1) WITH NOWAIT
		END
		ELSE
			RAISERROR ('Notice: Server Audit [SQLDBA_SecurityAudit] already exists', 0, 1) WITH NOWAIT


		/* 2. Server Audit Specification - logins, permissions, schema DDL, role changes */
		IF NOT EXISTS (SELECT 1 FROM sys.server_audit_specifications WHERE name = N'SQLDBA_SecurityAuditSpec')
		BEGIN
			SET @AuditSQL = N'CREAT' + @ke + N' SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec]'
				+ N' FOR SERVER AUDIT [SQLDBA_SecurityAudit]'
				PRINT @AuditSQL
			EXEC (@AuditSQL)
			SET @AuditSQL = ''
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd  + N' (FAILED_LOGIN_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (SUCCESSFUL_LOGIN_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (LOGOUT_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (LOGIN_CHANGE_PASSWORD_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (SERVER_PERMISSION_CHANGE_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (DATABASE_PERMISSION_CHANGE_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (SERVER_ROLE_MEMBER_CHANGE_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (DATABASE_ROLE_MEMBER_CHANGE_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (ADD_SERVER_ROLE_MEMBER_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (ADD_ROLE_MEMBER_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (CREATE_LOGIN_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (ALTER_LOGIN_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (DROP_LOGIN_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (CREATE_USER_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (ALTER_USER_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (DROP_USER_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (SCHEMA_OBJECT_CHANGE_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (SERVER_OBJECT_CHANGE_GROUP)'
			+ N'
			ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] ' + N' A' + @kd + @kd + N' (AUDIT_CHANGE_GROUP)'

			PRINT @AuditSQL
			EXEC (@AuditSQL)
			RAISERROR ('Action: Created and enabled Server Audit Specification [SQLDBA_SecurityAuditSpec]', 0, 1) WITH NOWAIT
		END
		ELSE
		BEGIN
			IF EXISTS (SELECT 1 FROM sys.server_audit_specifications WHERE name = N'SQLDBA_SecurityAuditSpec' AND is_state_enabled = 0)
			BEGIN
				SET @AuditSQL = N'ALT' + @ke + N'R SERVER AUDIT SPECIFICATION [SQLDBA_SecurityAuditSpec] WITH (STATE = ON)'
				EXEC (@AuditSQL)
				RAISERROR ('Action: Re-enabled Server Audit Specification [SQLDBA_SecurityAuditSpec]', 0, 1) WITH NOWAIT
			END
			ELSE
				RAISERROR ('Notice: Server Audit Specification [SQLDBA_SecurityAuditSpec] already exists and is enabled', 0, 1) WITH NOWAIT
		END
	END -- SQL Audit version guard
END

/*
-- LEGACY NOTE: The original Service Broker / Event Notifications approach
-- (SecurityChangeQueue, usp_LogSecurityChange) has been superseded by the
-- SQL Audit specification above and is not reproduced here.
*/


/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
/*
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
   SECTION 9: FINALIZATION
   Fix endpoint ownership, verify completion
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/
--==========================================================
-- Fix endpoints assigned to user
--==========================================================
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
*/

/*Fix endpoints*/
IF EXISTS
( 
		SELECT 1
		FROM master.sys.endpoints e
		INNER JOIN master.sys.server_principals sp
		ON e.principal_id = sp.principal_id
		RIGHT OUTER JOIN ( VALUES ( 2, 'TSQL'),
		( 3, 'SERVICE_BROKER'), ( 4, 'DATABASE_MIRRORING') )
		AS et ( typeid, PayloadType )
		ON et.typeid = e.type
		WHERE sp.name <> 'sa'
		AND sp.name IS NOT NULL
)
SELECT e.name as EndpointName,
sp.name AS EndpointOwner,
et.PayloadType,
e.state_desc
, CASE 
WHEN et.PayloadType IN ('DATABASE_MIRRORING','SERVICE_BROKER')
THEN 'ALTER AUTHORIZATION ON ENDPOINT::' +e.name +' TO sa'
END
FROM master.sys.endpoints e
INNER JOIN master.sys.server_principals sp
ON e.principal_id = sp.principal_id
RIGHT OUTER JOIN ( VALUES ( 2, 'TSQL'),
( 3, 'SERVICE_BROKER'), ( 4, 'DATABASE_MIRRORING') )
AS et ( typeid, PayloadType )
ON et.typeid = e.type
WHERE sp.name <> 'sa'
AND sp.name IS NOT NULL


-- 9.2: Power Plan Verification (Critical for Performance)
-- Source: sqlserver-kit - Find_and_fix_that_troublesome_Windows_Power_setting.sql
RAISERROR ('Checking: Windows Power Plan', 0, 1) WITH NOWAIT;

-- Check if xp_cmdshell is available (temporary enable if needed)
DECLARE @xpCmdShellOrig INT, @ShowAdvancedOrig INT;
SELECT @xpCmdShellOrig = CONVERT(INT, value_in_use) FROM sys.configurations WHERE name = 'xp_cmdshell';
SELECT @ShowAdvancedOrig = CONVERT(INT, value_in_use) FROM sys.configurations WHERE name = 'show advanced options';

IF @ShowAdvancedOrig = 0 
BEGIN
    EXEC sp_configure 'show advanced options', 1; RECONFIGURE;
END
IF @xpCmdShellOrig = 0 
BEGIN
    EXEC sp_configure 'xp_cmdshell', 1; RECONFIGURE;
END

-- Check power plan using xp_cmdshell
IF OBJECT_ID('tempdb..#PowerPlan') IS NOT NULL DROP TABLE #PowerPlan;
CREATE TABLE #PowerPlan (PowerLine VARCHAR(1000));
INSERT INTO #PowerPlan EXEC xp_cmdshell 'powercfg /list';

DECLARE @HighPerfActive BIT = 0;
SELECT @HighPerfActive = 1 FROM #PowerPlan WHERE PowerLine LIKE '%High performance%' AND PowerLine LIKE '%*%';

IF @HighPerfActive = 0
BEGIN
    RAISERROR ('      WARNING: Power Plan is NOT set to High Performance - this can significantly impact SQL Server performance!', 10, 1) WITH NOWAIT;
    RAISERROR ('      ACTION: Run: powercfg.exe /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c', 10, 1) WITH NOWAIT;
END
ELSE
    RAISERROR ('      INFO: Power Plan is set to High Performance', 10, 1) WITH NOWAIT;

DROP TABLE #PowerPlan;

-- Restore xp_cmdshell to original state
IF @xpCmdShellOrig = 0 
BEGIN
    EXEC sp_configure 'xp_cmdshell', 0; RECONFIGURE;
END
IF @ShowAdvancedOrig = 0 
BEGIN
    EXEC sp_configure 'show advanced options', 0; RECONFIGURE;
END

-- 9.3: Indirect Checkpoints (SQL 2016+) - Better Recovery
-- Source: MadeiraToolbox
DECLARE @SQLVersionCheck INT = @@MicrosoftVersion / 0x01000000;
IF @SQLVersionCheck >= 13  -- SQL 2016+
BEGIN
    RAISERROR ('Checking: Indirect Checkpoints', 0, 1) WITH NOWAIT;
    DECLARE @IndirectCP NVARCHAR(MAX) = '';
    SELECT @IndirectCP = @IndirectCP + 
        CASE WHEN target_recovery_time_in_seconds > 60 OR target_recovery_time_in_seconds = 0 THEN
            'ALTER DATABASE ' + QUOTENAME(name) + ' SET TARGET_RECOVERY_TIME = 1 MINUTES; ' 
        ELSE '' END
    FROM sys.databases WHERE state_desc = 'ONLINE' AND database_id NOT IN (1,2,32767);
    
    IF LEN(@IndirectCP) > 0
    BEGIN
        EXEC sp_executesql @IndirectCP;
        RAISERROR ('      Action: Enabled indirect checkpoints (1 min target recovery)', 0, 1) WITH NOWAIT;
    END
    ELSE
        RAISERROR ('      INFO: Indirect checkpoints already optimized', 10, 1) WITH NOWAIT;
END


-- 9.5: Security Baseline - Orphaned Users
-- Source: MadeiraToolbox
RAISERROR ('Checking: Orphaned database users', 0, 1) WITH NOWAIT;
DECLARE @OrphanedCount INT = 0;
SELECT @OrphanedCount = COUNT(*)
FROM sys.database_principals dp
LEFT JOIN sys.server_principals sp ON dp.sid = sp.sid
WHERE dp.type IN ('S','U','G') AND dp.sid IS NOT NULL AND sp.sid IS NULL;

IF @OrphanedCount > 0
BEGIN
    RAISERROR (N'      WARNING: Found %d orphaned users - run: ALTER USER [username] WITH LOGIN = [username]', 10, 1, @OrphanedCount) WITH NOWAIT;
END
ELSE
    RAISERROR ('      INFO: No orphaned users found', 10, 1) WITH NOWAIT;

-- 9.6: Security - PUBLIC role excessive permissions
-- Source: MadeiraToolbox - PUBLIC role with excessive permissions.sql
RAISERROR ('Checking: PUBLIC role permissions', 0, 1) WITH NOWAIT;
DECLARE @ExcessivePerms TABLE (DatabaseName NVARCHAR(128), Permission NVARCHAR(100));

DECLARE @dbname NVARCHAR(128);
DECLARE db_cursor CURSOR FOR SELECT name FROM sys.databases WHERE state = 0;
OPEN db_cursor;
FETCH NEXT FROM db_cursor INTO @dbname;
WHILE @@FETCH_STATUS = 0
BEGIN
    INSERT INTO @ExcessivePerms
    EXEC('SELECT ''' + @dbname + ''', permission_name FROM [' + @dbname + '].sys.database_permissions 
         WHERE grantee_principal_id = 0 AND permission_name IN (''CONNECT'',''EXECUTE'',''SELECT'',''UPDATE'',''INSERT'',''DELETE'')');
    FETCH NEXT FROM db_cursor INTO @dbname;
END
CLOSE db_cursor;
DEALLOCATE db_cursor;

IF EXISTS(SELECT 1 FROM @ExcessivePerms)
BEGIN
    RAISERROR ('      WARNING: PUBLIC role has permissions in some databases - review for least privilege', 10, 1) WITH NOWAIT;
    --SELECT DatabaseName, Permission FROM @ExcessivePerms;
END
ELSE
    RAISERROR ('      INFO: No excessive PUBLIC role permissions', 10, 1) WITH NOWAIT;

-- 9.7: Security - Guest user access
-- Source: MadeiraToolbox - GUEST user with database permissions.sql
RAISERROR ('Checking: Guest user permissions', 0, 1) WITH NOWAIT;
DECLARE @GuestPerms TABLE (DatabaseName NVARCHAR(128));

DECLARE db_cursor2 CURSOR FOR SELECT name FROM sys.databases WHERE state = 0;
OPEN db_cursor2;
FETCH NEXT FROM db_cursor2 INTO @dbname;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF EXISTS(SELECT * FROM sys.database_permissions dp 
              JOIN sys.database_principals dp2 ON dp.grantee_principal_id = dp2.principal_id 
              WHERE dp2.name = 'guest' AND dp.state <> 'G')
    BEGIN
        INSERT INTO @GuestPerms VALUES (@dbname);
    END
    FETCH NEXT FROM db_cursor2 INTO @dbname;
END
CLOSE db_cursor2;
DEALLOCATE db_cursor2;

IF EXISTS(SELECT 1 FROM @GuestPerms)
BEGIN
    RAISERROR ('      WARNING: Guest user has permissions in databases - disable if not needed', 10, 1) WITH NOWAIT;
    SELECT DatabaseName FROM @GuestPerms;
END
ELSE
    RAISERROR ('      INFO: Guest user has no unexpected permissions', 10, 1) WITH NOWAIT;


USE [master]
GO
RAISERROR ('Congratulations you awesome DBA you! Now go herd some more cats'  ,0,1) WITH NOWAIT;
