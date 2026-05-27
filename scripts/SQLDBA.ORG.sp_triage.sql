/* بسم الله الرحمن الرحيم  */
/* In the name of God, the Merciful, the Compassionate */
IF OBJECT_ID('dbo.sp_triage®') IS NULL
  EXEC ('CREATE PROCEDURE dbo.[sp_triage®] AS RETURN 0;');
GO

ALTER PROCEDURE [dbo].[sp_triage®]  --@Debug = 0]
--DECLARE
/* 
WANT [sp_triage®]?
https://github.com/SQLDBA-ORG/sqldba/

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
, @CleanUpIsle9 [INT] = 0 /*Set to 1 to remove export tables after results - Still testing*/
/*@ShowQueryPlan. Set to 1 to include the Query plan in the output*/
, @ShowQueryPlan [INT]  = 0
/*@PrepForExport. WHEN the intent of this script is to use this for some type of hocus-pocus magic metrics, set this to 1*/
, @PrepForExport [INT]  = 1 
/*@ShowMigrationRelatedOutputs. WHEN you need to show migration stuff, like possible breaking connections and DMA script outputs, set to 1 to show information*/
, @ShowMigrationRelatedOutputs [INT] = 1 
, @SkipHeaps [INT] = 0 /*Set to 1 to Skip Heap Table Checks. These can be intensive*/

/*Email results*/
, @MailResults BIT = 0
, @EmailRecipients [NVARCHAR] (500) ='scriptoutput@sqldba.org'
 /*Screen / Table*/
, @Export [NVARCHAR] (10) = 'TABLE'
, @ShowOnScreenWhenResultsToTable [INT] = 1 

, @ExportSchema [NVARCHAR] (25)  = 'dbo'
, @ExportDBName  [NVARCHAR] (20) = 'master'
, @ExportTableName [NVARCHAR] (55) = 'sqldba_sp_triage®_output'
, @ExportCleanupDays [INT] = 180
/* @PrintMatrixHeader. Added to turn it off since some control chars coming through stopping a copy/paste from the messages window in SSMS */
, @PrintMatrixHeader [INT] = 1
, @Debug BIT =1 /*0 is off, 1 is on, no internal raiserror will be shown*/
, @ShowDebugTime BIT =0 /*0 is off, 1 is on, shows execution times*/
, @CleanupTime [INT] = 180 /*If output goes to a table then clean up records older than this many days*/
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

	DECLARE @MagicVersion [NVARCHAR] (25)
	SET @MagicVersion = '09/03/2026' /*DD/MM/YYYY*/
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


/*Check that all schema related objects are escaped with brackets [ ] */
IF(LEFT(@ExportSchema,1) <> '[')
	BEGIN
		SET @ExportSchema = '[' + @ExportSchema + ']'
	END
IF(LEFT(@ExportDBName,1) <> '[')
	BEGIN
		SET @ExportDBName = '[' + @ExportDBName + ']'
	END
IF(LEFT(@ExportTableName,1) <> '[')
	BEGIN
		SET @ExportTableName = '[' + @ExportTableName + ']'
	END

/*Before anything, start a trace and get some information.*/
DECLARE @IsSQLAzure BIT
DECLARE @IsSQLMI BIT
SET @IsSQLAzure = 0
IF (SELECT ServerProperty('EngineEdition')) = 5 
	SET @IsSQLAzure = 1
IF (SELECT ServerProperty('EngineEdition')) = 8
	SET @IsSQLMI = 1


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
  SELECT @errMessage  = ERROR_MESSAGE()
  IF @Debug = 1
  BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
END CATCH


DECLARE @dynamicSQL [NVARCHAR] (4000)
/*Check and Set xp_cmdshell*/
DECLARE @StateOfXP_CMDSHELL INT
SELECT @StateOfXP_CMDSHELL = CONVERT(INT, ISNULL(value, value_in_use)) 
FROM  [sys].configurations
WHERE  name = 'xp_cmdshell' ;


SET @dynamicSQL = '
BEGIN
	-- To allow advanced options to be changed.
	EXEC sp_configure ''show advanced options'', 1
	-- To update the currently configured value for advanced options.
	RECONFIGURE
	-- To enable the feature.
	EXEC sp_configure ''xp_cmdshell'', 1
	-- To update the currently configured value for this feature.
	RECONFIGURE
END'
IF @StateOfXP_CMDSHELL = 0 
BEGIN
	RAISERROR ('xp_cmdshell disabled, some functionality may be impacted',0,1) WITH NOWAIT;
END
/*
BEGIN TRY
	EXEC sp_executesql @dynamicSQL 
END TRY

BEGIN CATCH
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	PRINT 'Failed to reconfigure, likely Azure'
END CATCH
*/

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
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@License,0,1) WITH NOWAIT; 
		SELECT @errMessage  = ERROR_MESSAGE();
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
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
	DECLARE @starttime DATETIME;
	DECLARE @ErrorSeverity int;
	DECLARE @ErrorState int;
	DECLARE @ErrorMessage [NVARCHAR] (400);
	DECLARE @CustomErrorText [NVARCHAR] (500);
	DECLARE @sql [NVARCHAR] (4000);
	DECLARE @powershellrun VARCHAR(4000)
	/*Performance section variables*/
	DECLARE @cnt INT;
	DECLARE @record_count INT;
	DECLARE @dbid INT;
	DECLARE @objectid INT;
	DECLARE @grand_total_worker_time FLOAT ; 
	DECLARE @grand_total_IO FLOAT ; 
	DECLARE @evaldate [NVARCHAR] (20);
	DECLARE @TotalIODailyWorkload MONEY ;
	
	SET @evaldate = CONVERT([VARCHAR](20),GETDATE(),120);

	SET @starttime = GETDATE();

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
IF @IsSQLAzure = 0 OR @IsSQLMI = 0
	BEGIN
	BEGIN TRY
		DECLARE @ThisDomainTable TABLE 
		( 
			ThisDomain [NVARCHAR] (100)
		);
		SET @dynamicSQL = '
		DECLARE @ThisDomain [NVARCHAR] (100);
		EXEC master.dbo.xp_regread ''HKEY_LOCAL_MACHINE'', ''SYSTEM\CurrentControlSet\services\Tcpip\Parameters'', N''Domain'',@ThisDomain OUTPUT
		SELECT @ThisDomain';
		INSERT @ThisDomainTable
		EXEC sp_executesql @dynamicSQL 

		SELECT @ThisDomain = ThisDomain FROM @ThisDomainTable;
		
	END TRY
	BEGIN CATCH
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
		PRINT 'Failed xp_regread, likely Azure';
	END CATCH
END
IF (@IsSQLAzure = 1 OR @IsSQLMI = 1) 
		SELECT @ThisDomain = RIGHT(SYSTEM_USER,LEN(SYSTEM_USER) - CHARINDEX('@',SYSTEM_USER)) /*CONVERT([NVARCHAR] (100),SERVERPROPERTY('ServerName'))+'.database.windows.net';*/
SET @ThisDomain = ISNULL(@ThisDomain, DEFAULT_DOMAIN());

SELECT 
@SP_MachineName = CONVERT([NVARCHAR] (50),SERVERPROPERTY('MACHINENAME') )
, @SP_INSTANCENAME = ISNULL(CONVERT([NVARCHAR] (50),SERVERPROPERTY('INSTANCENAME') ),'')
, @SP_PRODUCTVERSION = CONVERT([NVARCHAR] (50),SERVERPROPERTY('PRODUCTVERSION ') )
, @SP_SQL_VERSION = CASE 
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
IF CONVERT( TINYINT ,@SQLVersion) < 10
BEGIN
	/*add blanks to size for sql 2005*/
	SET @dynamicSQL = @dynamicSQL + '
, NULL
, NULL
, NULL
, NULL
, NULL
, NULL
';

END

IF CONVERT( TINYINT ,@SQLVersion) >= 10 -- 2008+ 
BEGIN
	SET @dynamicSQL = @dynamicSQL + '
	, query_hash
	, query_plan_hash
	, total_rows
	, last_rows
	, min_rows
	, max_rows
	';
END

IF CONVERT( TINYINT ,@SQLVersion) < 12 -- 2012- 
BEGIN
	SET @dynamicSQL = @dynamicSQL + '
	, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
	';
END


IF CONVERT( TINYINT ,@SQLVersion) = 12
BEGIN
SET @dynamicSQL = @dynamicSQL +  '
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
	, NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL
';
END


IF CONVERT( TINYINT ,@SQLVersion) IN (13,14) -- 2008+ 
BEGIN
	SET @dynamicSQL = @dynamicSQL + '
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
	, NULL--[total_num_physical_reads]
	, NULL--[last_num_physical_reads]
	, NULL--[min_num_physical_reads]
	, NULL--[max_num_physical_reads]
	, NULL--[total_page_server_reads]
	, NULL--[last_page_server_reads]
	, NULL--[min_page_server_reads]
	, NULL--[max_page_server_reads]
	, NULL--[total_num_page_server_reads]
	, NULL--[last_num_page_server_reads]
	, NULL--[min_num_page_server_reads]
	, NULL--[max_num_page_server_reads]
	';
END

IF CONVERT( TINYINT ,@SQLVersion) >= 15 
BEGIN
	SET @dynamicSQL = @dynamicSQL + '
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
	';
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



END TRY
BEGIN CATCH
  IF @Debug = 1
  BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Problem inserting data into #dadatafor_exec_query_stats',0,1) WITH NOWAIT; 
		SELECT @errMessage  = ERROR_MESSAGE();
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	END
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
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Problem with adding columns to #dadatafor_exec_query_stats',0,1) WITH NOWAIT;
		SELECT @errMessage  = ERROR_MESSAGE();
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
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
			CREATE TABLE #output_sqldba_org_sp_triage_MissingIndex 
			(
				[DB] [NVARCHAR] (250)
				, [magic_benefit_number] FLOAT
				, [Table] [NVARCHAR] (2000)
				, [ChangeIndexStatement] [NVARCHAR] (4000)
				, [equality_columns] [NVARCHAR] (4000)
				, [inequality_columns] [NVARCHAR] (4000)
				, [included_columns] [NVARCHAR] (4000)
				, [BeingClever] [NVARCHAR] (4000)
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
        , ('WAIT_XTP_OFFLINE_CKPT_NEW_LOG', 'https://www.sqlskills.com/help/waits/WAIT_XTP_OFFLINE_CKPT_NEW_LOG')
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

	
SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' startup step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'

IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	
	


SELECT @errMessage  = 'Checking xp_msver table'
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
BEGIN TRY
  	DECLARE @msversion TABLE([Index] INT, Name [NVARCHAR] (50), [Internal_Value] [NVARCHAR] (50), [Character_Value] [NVARCHAR] (250))
	INSERT @msversion
	EXEC xp_msver;
	/*Rather useful this one*/
END TRY
BEGIN CATCH

  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
		SELECT @errMessage  = ERROR_MESSAGE();
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
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
			, [Date] DATETIME
			,  [Log File Size (Byte)] BIGINT
		)
	INSERT @xp_errorlogs
	EXEC sys.sp_enumerrorlogs;

	DECLARE @logcounter INT = 0 ;
	DECLARE @lastlog INT;
	DECLARE @RestartInfoLog INT;
	SELECT @lastlog = MAX(Archive) FROM @xp_errorlogs;
	WHILE @logcounter <= @lastlog
	BEGIN
		INSERT @xp_errorlog
		EXEC xp_ReadErrorLog @logcounter, 1, N'Command Line Startup Parameters:';
		IF EXISTS(SELECT 1 FROM @xp_errorlog WHERE [Text] LIKE 'Command Line Startup Parameters:%')
		BEGIN
			SET @RestartInfoLog = @logcounter;
			SET @logcounter = @lastlog + 1;
			--BREAK
		END
		SET @logcounter = @logcounter + 1;
	END

	INSERT @xp_errorlog
	EXEC [sys].xp_readerrorlog @RestartInfoLog, 1, N'locked pages';
	INSERT @xp_errorlog
	EXEC [sys].xp_readerrorlog @RestartInfoLog, 1, N'Database Instant File Initialization: enabled';
	INSERT @xp_errorlog
	EXEC [sys].xp_readerrorlog @RestartInfoLog, 1, N'System Manufacturer:';
	INSERT @xp_errorlog
	EXEC [sys].xp_readerrorlog @RestartInfoLog, 1, N'(SPN)'; 
	INSERT @xp_errorlog
	EXEC [sys].xp_readerrorlog @RestartInfoLog, 1, N'Server is listening on';
	INSERT @xp_errorlog
	EXEC [sys].xp_readerrorlog @RestartInfoLog, 1, N'A self-generated certificate was successfully loaded for encryption.';
	INSERT @xp_errorlog
	EXEC [sys].xp_readerrorlog @RestartInfoLog, 1, N'certificate was successfully loaded for encryption.';
	

	--SELECT CONVERT(MONEY,LEFT(Character_Value,3)) FROM @msversion WHERE Name = 'WindowsVersion'

SELECT @errMessage  = 'Checking power plan';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
	END

		RAISERROR (@errMessage,0,1) WITH NOWAIT; 

BEGIN TRY
	DECLARE @value [NVARCHAR] (64);
	DECLARE @key [NVARCHAR] (512); 
	DECLARE @WindowsVersion [NVARCHAR] (50);
	DECLARE @PowerPlan [NVARCHAR] (20);
	SET @key = 'SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes';
	SELECT @WindowsVersion = CONVERT(MONEY,LEFT(Character_Value,3)) 
	FROM @msversion 
	WHERE Name = 'WindowsVersion';

	INSERT @xp_errorlog
	EXEC [sys].xp_readerrorlog @RestartInfoLog, 1, N'locked pages';

	/*CASE WHEN windows_release IN ('6.3','10.0') AND (@@VERSION LIKE '%Build 10586%' OR @@VERSION LIKE '%Build 14393%')THEN '10.0' ELSE CONVERT([VARCHAR](5),windows_release) END 
	FROM [sys].dm_os_windows_info (NOLOCK);*/


	IF CONVERT(DECIMAL(3,1), @WindowsVersion) >= 6.0
	BEGIN
	
		DECLARE @cpu_name [NVARCHAR] (150);
		DECLARE @cpu_ghz [NVARCHAR] (50);

		DECLARE @cpu_speed_mhz int;
        DECLARE @cpu_speed_ghz decimal(18,2);

										
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
		/*PRINT @PowerPlan*/
		IF @Debug = 1
			BEGIN
				SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
				SET @DebugTime = GETDATE();
				IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
			RAISERROR (N'Power Options checked',0,1) WITH NOWAIT;
		END
		
	END

END TRY
BEGIN CATCH
  SELECT @errMessage  = ERROR_MESSAGE()
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
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
		/*Well this is not Azure*/
		SET @IsSQLAzure = 1
	END CATCH
	
END




SELECT @errMessage  = 'Checking server properties'
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
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
		WHEN @PowerPlan = 'High-Performance' THEN @Result_Good 
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
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
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
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
END CATCH

/*----------------------------------------
			--Check for current supported build of SQL server
-------------------------------------*/

SELECT @errMessage  = 'Checking server build versions';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
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
		UNION ALL SELECT '2019', '15', '2030-01-08', '15.0.1000.34', '15.0.4043.16';
	
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
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
END CATCH


  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
END CATCH



SELECT @errMessage  = 'Checking Antivirus scanning SQL process';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
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
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
END CATCH



SELECT @errMessage  = 'Checking tempDB file count';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
BEGIN TRY
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
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
END CATCH

SELECT @errMessage  = 'Looking for tempdb latching';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
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
		, Case
                 WHEN Cast(Right(resource_description, Len(resource_description) - Charindex(':', resource_description, 3)) As Int) - 1 % 8088 = 0 Then @Result_Warning
                                     WHEN Cast(Right(resource_description, Len(resource_description) - Charindex(':', resource_description, 3)) As Int) - 2 % 511232 = 0 Then @Result_Warning
                                     WHEN Cast(Right(resource_description, Len(resource_description) - Charindex(':', resource_description, 3)) As Int) - 3 % 511232 = 0 Then @Result_Warning
                                     ELSE @Result_Good
                                     End 
		, CONVERT([VARCHAR](200), Case
		WHEN Cast(Right(resource_description, Len(resource_description) - Charindex(':', resource_description, 3)) As Int) - 1 % 8088 = 0 Then 'Is PFS Page'
					WHEN Cast(Right(resource_description, Len(resource_description) - Charindex(':', resource_description, 3)) As Int) - 2 % 511232 = 0 Then 'Is GAM Page'
					WHEN Cast(Right(resource_description, Len(resource_description) - Charindex(':', resource_description, 3)) As Int) - 3 % 511232 = 0 Then 'Is SGAM Page'
					ELSE 'Is Not PFS, GAM, or SGAM page'
					End
				)
		FROM [sys].dm_os_waiting_tasks
		WHERE wait_type LIKE 'PAGE%LATCH_%'
		AND resource_description LIKE '2:%';

	END	
			
END TRY
BEGIN CATCH
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
END CATCH


SELECT @errMessage  = 'Checking errorlog';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
END
BEGIN TRY

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
END TRY
BEGIN CATCH
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
END CATCH

			/*----------------------------------------
			--Check for current service account
			----------------------------------------*/

SELECT @errMessage  = 'Checking service account details';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
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

	END TRY
	BEGIN CATCH
	  SELECT @errMessage  = ERROR_MESSAGE()
	  IF @Debug = 1
		BEGIN
			SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
			SET @DebugTime = GETDATE();
			IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
			RAISERROR (@errMessage,0,1) WITH NOWAIT; 
		END
	END CATCH

END TRY
BEGIN CATCH
  SELECT @errMessage  = ERROR_MESSAGE();
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
END CATCH
	
			/*----------------------------------------
			--Check for high worker thread usage
			----------------------------------------*/
SELECT @errMessage  = 'Checking for high worker thread usage';
  IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
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
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Looked at worker thread usage',0,1) WITH NOWAIT;
	END

END TRY
BEGIN CATCH
  SELECT @errMessage  = ERROR_MESSAGE();
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
END CATCH	
			 /*----------------------------------------
			--Performance counters
			----------------------------------------*/


	SELECT @ts = 
	(
		SELECT cpu_ticks/(cpu_ticks/ms_ticks)
		FROM [sys].dm_os_sys_info 
	) OPTION (RECOMPILE);
	
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
			SET @ExportDBName = '[' + DB_NAME() + ']';
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
    SELECT @errMessage  = ERROR_MESSAGE();
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
END CATCH


SELECT @errMessage  = 'Working those perfom counters';
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT;
		SELECT @errMessage  = ERROR_MESSAGE();
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
BEGIN TRY

BEGIN
IF 'Skip syscounters until I can find a use for it' = 'Yup'
BEGIN
	DECLARE @syscounters [NVARCHAR] (4000);
	SET @syscounters=STUFF((SELECT DISTINCT ''',''' +LTRIM([counter_name])
	FROM @PerformanceCounterList
	WHERE [is_captured_in] = 1 FOR XML PATH('')), 1, 2, '')+'''' ;

	DECLARE @syscountertable TABLE 
	(
		id [INT] IDENTITY(1,1)
		, [output] [VARCHAR](500)
	);
	
	DECLARE @syscountervaluestable TABLE 
	(
		id [INT] IDENTITY(1,1)
		, [value] [VARCHAR](500)
	);
	
	IF @StateOfXP_CMDSHELL = 1
	BEGIN
		SET @powershellrun = 'C:\WINDOWS\system32\WindowsPowerShell\v1.0\powershell.exe "& get-counter -counter '+ @syscounters +' -SampleInterval 5 -MaxSamples 2 | Select-Object -ExpandProperty Readings"'
		INSERT @syscountertable
		EXEC xp_cmdshell @powershellrun
	END
END
	BEGIN TRY
		/*While we are on the topic of xm_cmdshell, check the SPNs as well*/
		DECLARE @spnCheckCmd [NVARCHAR] (4000);
		SET @spnCheckCmd = 'setspn -L ' + @DBEngineLogin;
		--IF @Debug = 1
		--PRINT @spnCheckCmd
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
			RAISERROR (N'Checking SPNs',0,1) WITH NOWAIT; 
		END
		IF @StateOfXP_CMDSHELL = 1
		BEGIN
			INSERT #output_sqldba_org_sp_triage_spnCheck 
			EXEC xp_cmdshell @spnCheckCmd;
		END
		
		
		IF EXISTS
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
		IF NOT EXISTS
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
			, 'No SPNs registered';
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
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
			RAISERROR (N'Error: xp_cmdshell DISABLED',0,1) WITH NOWAIT; 
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
  SELECT @errMessage  = ERROR_MESSAGE();
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
END CATCH

/*Generate DTU calculations*/

SELECT @errMessage  = 'Checking Azure calculations';
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
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
  SELECT @errMessage  = ERROR_MESSAGE();
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
	END
END CATCH

IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Finished rough IOPS calculation',0,1) WITH NOWAIT;
	END


			/*----------------------------------------
			--Check for any pages marked suspect for corruption
			----------------------------------------*/
SELECT @errMessage  = 'Checking for suspiciouns pages';
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
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
		FROM msdb.dbo.suspect_pages P';
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
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Included Suspect Pages, if any',0,1) WITH NOWAIT;
	END

END TRY
BEGIN CATCH
  SELECT @errMessage  = ERROR_MESSAGE();
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
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

	END
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
		'[' + name + ']'
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
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
		SELECT   
			@secondsperoperator = AVG(qs.total_worker_time/qs.execution_count/1000)/0.7248/1000  
		FROM  #dadatafor_exec_query_stats qs
		CROSS APPLY [sys].dm_exec_sql_text(qs.sql_handle) qt
		WHERE qs.total_logical_reads = 0 
		AND qs.last_execution_time BETWEEN DATEADD(MINUTE,-2,@StartTest) AND GETDATE()
		AND PATINDEX('%ThisistoStandardisemyOperatorCostMate%',CAST(qt.TEXT AS [NVARCHAR] (MAX))) > 0
			
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
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	
	SET @dynamicSQL = @dynamicSQL + ' AND 1=1/*db.database_id > 4*/ AND db.user_access = 0 AND db.State = 0 ';
	
	BEGIN TRY

	IF @SQLVersion >= 11 AND @isSQLAzure = 0 
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
			WHERE /*db.database_id > 4 AND*/ db.user_access = 0 AND db.State = 0 
			AND t1.LocalReplicaRole IS NOT NULL
			';
		END
	END TRY
	BEGIN CATCH
		SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
			RAISERROR (N'Trouble with Availability Group database list',0,1) WITH NOWAIT;
		END
	END CATCH

	SET @dynamicSQL = @dynamicSQL + ' OPTION (RECOMPILE);';
	INSERT INTO @Databases 
	EXEC sp_executesql @dynamicSQL ;
	SET @Databasei_Max = (
		SELECT MAX(id) 
		FROM @Databases );

			/*----------------------------------------
			--Get uptime and cache age
			----------------------------------------*/

	SET @oldestcachequery = (SELECT ISNULL(  MIN(creation_time),0.1) 
	FROM  #dadatafor_exec_query_stats WITH (NOLOCK));

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

		INSERT @lastservericerestartTable
		EXEC sp_executesql @dynamicSQL ;

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

	SET @CachevsUpdate = @DaysOldestCachedQuery*100/@DaysUptime;
	IF @CachevsUpdate < 1
		SET @CachevsUpdate = 1
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
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Server uptime and cache age established',0,1) WITH NOWAIT;
	END

	   /*----------------------------------------
			--Internals and Memory usage
		----------------------------------------*/

	SELECT @VMType = RIGHT(@@version,CHARINDEX('(',REVERSE(@@version)));

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

	INSERT #output_sqldba_org_sp_triage 
	(
		SectionID
		, Section
		, Summary
		, HoursToResolveWithTesting 
	)
	SELECT 4, '['+REPLICATE('|', AVG(CONVERT(MONEY,SQLProcessUtilization))) + REPLICATE('.',100-(AVG(CONVERT(MONEY,SQLProcessUtilization)) )) +']'
	, (
	@cpu_ghz
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

IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Checked CPU usage for the last 5 hours',0,1) WITH NOWAIT;
	END
	



			/*----------------------------------------
			--Error Log issues on the server
			----------------------------------------*/
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
			, [Log File Size (Byte)] [INT]
		);

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
		DECLARE @logCount INT;
		SELECT @logCount = COUNT(*) 
		FROM @ErrorLogFiles;

		DECLARE @i [INT] ;
		SET @i = 0;
		DECLARE @tableName [NVARCHAR] (128);
		DECLARE @datecheck DATETIME;
		DECLARE curReadSQLErrorLogs CURSOR FAST_FORWARD READ_ONLY FOR 
			   SELECT [Archive #] 
			   FROM @ErrorLogFiles
			   WHERE 1=1 --(Date > DATEADD(WEEK, -2,GETDATE()))
			   AND Id <= 5
		OPEN curReadSQLErrorLogs
		FETCH NEXT FROM curReadSQLErrorLogs INTO @i
		WHILE @@FETCH_STATUS = 0
		BEGIN
			--SELECT @datecheck = Date FROM @ErrorLogFiles WHERE [Archive #] = @i
			/*IF @datecheck > DATEADD(MONTH,-1,GETDATE())*/
			/*Only use log files that were created in the last 3 months*/
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

 
		IF EXISTS (
			SELECT 1 FROM @SQLLogData
				)
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
			, CONVERT(NVARCHAR(500),CONVERT(NVARCHAR(50),[EventCount]) +' events in ' + CONVERT(NVARCHAR(50), DATEDIFF(HOUR,L.[From],L.[To])) + ' hours. ' + L.LogText +'. ' + m.text)
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
		END
	END
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
				SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
				SET @DebugTime = GETDATE();
				IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
			SET @dynamicSQL = 'EXEC sp_readerrorlog 1, 2, ''Error:'' ';
			INSERT @Errorlog
			EXEC sp_executesql @dynamicSQL;
		END 

		/*Already checked before
		BEGIN   
			SET @dynamicSQL = 'EXEC sp_readerrorlog 1, 1, ''Error:'' '
			INSERT @Errorlog
			EXEC sp_executesql @dynamicSQL;
		END
		*/
		IF EXISTS (SELECT * FROM @Errorlog)
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
		END  
	END TRY
	BEGIN CATCH
		SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT; 
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Error reading agent log',0,1) WITH NOWAIT;
	END
	END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
)
SET @dynamicSQL = '
	SELECT 
	DBSysJobHistory.job_id
	, DBSysJobHistory.step_id
	, MAX(DBSysJobHistory.instance_id) as instance_id
	FROM msdb.dbo.sysjobhistory DBSysJobHistory
	GROUP BY DBSysJobHistory.job_id, DBSysJobHistory.step_id';

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
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
        SET @query = 'dbcc loginfo (' + '''' + @dbname + ''') ' ; 
		
		IF @IsSQLAzure = 0
		BEGIN
			INSERT into @dbccloginfo  
			exec (@query)  ;
		END
  
        SET @vlfs = @@rowcount ; 
  
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
        SET @query = 'dbcc loginfo (' + '''' + @dbname + ''') '  ;

		IF @IsSQLAzure = 0
		BEGIN
			INSERT into @dbccloginfo2012  
			exec (@query) ; 
		END
  
        SET @vlfs = @@rowcount  ;
  
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
		ELSE ''
	END --as VLFCommentary 
	+ CASE 
		WHEN log_size_mb/2 > ls.maxsize  THEN ' .LOG too big compared to Max LOG Backup' /*'Log Size is more than twice the size of the maximum needed based on 3 month history' */
		ELSE ''
	END --as LogSizeCOmmentary2
	 [Summary]

	---
	--[vlfcount];[Size_MBs];[log_size_mb];[AvgBackupSizeMB];[MaxBackupSizeMB]
	, CONVERT([NVARCHAR] (20),v.vlfcount)
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
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
		SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
		RAISERROR (N'Server does not seem to have spt_values table ',0,1) WITH NOWAIT;
		SET @PageSize = 8 /*as in 8 KB*/;
	END CATCH

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

	DECLARE @SecondaryReadRole [NVARCHAR] (250);
	DECLARE @AGBackupPref [NVARCHAR] (250);
	DECLARE @AGCurrentLocation [NVARCHAR] (250);

	SET @Databasei_Count = 1; 
	WHILE @Databasei_Count <= @Databasei_Max 
	BEGIN 
		SELECT 
			@DatabaseName = d.databasename
			, @DatabaseState = d.state 
			, @AGBackupPref = BackupPref
			, @SecondaryReadRole = ReadSecondary
		FROM @Databases d 
		WHERE id = @Databasei_Count 
		AND d.state NOT IN (2,6);

		IF (
			@SecondaryReadRole <> 'NO' 
			AND @AGBackupPref <> 'primary'
		) 
		AND EXISTS( SELECT @DatabaseName)
		BEGIN
			SET @dynamicSQL = 'USE [' + @DatabaseName + '];
			DBCC showfilestats WITH NO_INFOMSGS;';

			INSERT @FileStats
			EXEC sp_executesql @dynamicSQL;

			SET @dynamicSQL = 'USE [' + @DatabaseName + '];
			SELECT ''' +@DatabaseName + ''', filename, size, ISNULL(FILEGROUP_NAME(groupid),''LOG''), [name] ,maxsize, growth  FROM dbo.sysfiles sf ; ';
			
			INSERT @FileSize 
			EXEC sp_executesql @dynamicSQL;

			SET @dynamicSQL = 'USE [' + @DatabaseName + '];
			DBCC loginfo WITH NO_INFOMSGS;';

			IF @IsSQLAzure = 0
			BEGIN
				SET IDENTITY_INSERT #output_sqldba_org_sp_triage_dbccloginfo ON;

				INSERT #output_sqldba_org_sp_triage_dbccloginfo
				EXEC sp_executesql @dynamicSQL;

				SET IDENTITY_INSERT #output_sqldba_org_sp_triage_dbccloginfo OFF;
			END

			SELECT @VLFcount = COUNT(*) 
			FROM #output_sqldba_org_sp_triage_dbccloginfo ;

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
		+ CASE WHEN FileGroupName = 'LOG' THEN '(' + CONVERT([VARCHAR](20),VLFCount) + 'vlfs)' ELSE '' END
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
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	CROSS APPLY [sys].dm_exec_sql_text(plan_handle) 
	WHERE cp.cacheobjtype = N'Compiled Plan' 
	AND cp.objtype IN (N'Adhoc', N'Prepared') 
	AND cp.usecounts = 1
	ORDER BY cp.size_in_bytes DESC 
	OPTION (RECOMPILE);

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Got cached plan statistics - Biggest single use plans',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Find cpu load, io and memory per DB
			----------------------------------------*/
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
		Name AS 'DatabaseName'
		, d.database_id
		, SUM(num_of_reads) AS'Number of Reads'
		, SUM(num_of_writes) AS'Number of Writes'
		, SUM(num_of_writes) +  SUM(num_of_reads) [TotalIO]
		FROM [sys].dm_io_virtual_file_stats(NULL,NULL) I
		INNER JOIN @sysdatabasesTable d ON I.database_id = d.database_id
		GROUP BY Name, d.database_id
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
			, db_buffer_pages =COUNT_BIG(*)
		FROM [sys].dm_os_buffer_descriptors
		GROUP BY database_id
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

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE();
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Filtered wait stats have been prepared',0,1) WITH NOWAIT;
	END

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
				, OBJECT_ID(n.value('(//MissingIndex/@Database)[1]', '[VARCHAR](128)') + '.' + 
				   n.value('(//MissingIndex/@Schema)[1]', '[VARCHAR](128)') + '.' + 
				   n.value('(//MissingIndex/@Table)[1]', '[VARCHAR](128)')) AS OBJECT_ID, 
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
		) AS tab 
		CROSS APPLY query_plan.nodes('//StmtSimple') AS q(n) 
		) TF
		INNER JOIN @Databases d ON d.database_id = TF.database_id
		LEFT OUTER JOIN 
		(
			SELECT  
				(( ISNULL(user_seeks,0) + ISNULL(user_scans,0 ) * avg_total_user_cost * avg_user_impact)/1) [Magic]
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
	ORDER BY  [SecondsSavedPerDay] DESC, total_elapsed_time DESC 
	OPTION (RECOMPILE);
	END TRY
	BEGIN CATCH
		SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
		IF @Debug = 1
	BEGIN
		SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
			RAISERROR	  (N'ERROR Section 17 Find most intensive query',0,1) WITH NOWAIT;
		END
	END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR	  (N'Evaluated execution plans for missing indexes',0,1) WITH NOWAIT;
	END


			/*----------------------------------------
			--Get missing index information for each database
			----------------------------------------*/

		SET @CustomErrorText = '['+@DatabaseName+'] Looking for missing indexes in DMVs'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
				RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;
			END

			SET @dynamicSQL = '
			USE [master]
			SELECT 
				LEFT([statement],(PATINDEX(''%.%'',[statement]))-1) [Database]
				,  (( user_seeks + user_scans ) * avg_total_user_cost * avg_user_impact)/' + CONVERT([NVARCHAR],@DaysOldestCachedQuery) + ' daily_magic_benefit_number
				, [Table] = [statement]
				, [CreateIndexStatement] = ''CREATE NONCLUSTERED INDEX IX_SQLDBA_'' + REPLACE(REPLACE(REVERSE(LEFT(REVERSE([statement]),(PATINDEX(''%.%'',REVERSE([statement])))-1)),'']'',''''),''['','''')
				+ REPLACE(REPLACE(REPLACE(LEFT(ISNULL(mid.equality_columns,'''')+ISNULL(mid.inequality_columns,''''),15), ''['', ''''), '']'',''''), '', '',''_'') + ''_''+ REPLACE(CONVERT([VARCHAR](20),GETDATE(),102),''.'',''_'') + ''T''  + REPLACE(CONVERT([VARCHAR](20),GETDATE(),108),'':'',''_'') + '' ON '' + [statement] 
				+  '' ( '' 
				+ ''< be clever here > ''
				+  '') ''
				+  ISNULL(''INCLUDE ('' + mid.included_columns + '')'','''')
				' /* Don't be too clever now.
				+ CHAR(13) + CHAR(10) + ''WITH (PAD_INDEX  = OFF, STATISTICS_NORECOMPUTE  = OFF, SORT_IN_TEMPDB = ON, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = '+@rebuildonline+', ALLOW_ROW_LOCKS  = ON, ALLOW_PAGE_LOCKS  = ON,FILLFACTOR = 98) ON [PRIMARY];''
				*/
				+ '
				, mid.equality_columns
				, mid.inequality_columns
				, mid.included_columns
				, ''SELECT STUFF(( SELECT '''', '''' + [Columns] 
			FROM ( SELECT TOP 100 PERCENT c1.[id], [Columns], [Count] 
			FROM (
			SELECT ROW_NUMBER() OVER(ORDER BY [RankMe]) [id], LTRIM([Columns]) [Columns] 
			FROM (VALUES('''''' + REPLACE(ISNULL(mid.equality_columns + ISNULL('',''+ mid.inequality_columns,''''),ISNULL(mid.inequality_columns,'''')) ,'','','''''',1),('''''') 
			+'''''',1))t ([Columns],[RankMe]) ) c1 '' 
			+ '' LEFT OUTER JOIN (
			SELECT ROW_NUMBER() OVER(ORDER BY [Count]) [id] ,LTRIM([Count]) [Count] 
			FROM (VALUES((SELECT COUNT (DISTINCT '' + REPLACE(ISNULL(mid.equality_columns + ISNULL('',''+ mid.inequality_columns,''''),ISNULL(mid.inequality_columns,'''')) ,'',''
			,'') FROM '' + [statement] +'' WITH(NOLOCK)),((SELECT COUNT (DISTINCT '') 
			+'') FROM '' + [statement] +'' WITH(NOLOCK))))t ([Count]) )c2 ON c2.id = c1.id 
			ORDER BY c2.[Count] * 1 DESC
			) t1 FOR XML PATH('''''''')),1,1,'''''''') AS NameValues'' [BeingClever]
			FROM [sys].dm_db_missing_index_group_stats AS migs 
			INNER JOIN [sys].dm_db_missing_index_groups AS mig ON migs.group_handle = mig.index_group_handle 
			INNER JOIN [sys].dm_db_missing_index_details AS mid ON mig.index_handle = mid.index_handle 
			ORDER BY daily_magic_benefit_number DESC, [CreateIndexStatement] DESC OPTION (RECOMPILE);'


			BEGIN TRY
				INSERT #output_sqldba_org_sp_triage_MissingIndex
				EXEC sp_executesql @dynamicSQL;
			END TRY
			BEGIN CATCH
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (@errMessage,0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
			END CATCH
			
			SET @CustomErrorText = '['+@DatabaseName+'] Index usage checks'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
				RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;
			END

			SELECT @dynamicSQL = '
			USE ['+@DatabaseName+']
			SELECT --TOP 5000 
				'''+@DatabaseName+'''
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
				/ SUM(user_seeks + user_scans + user_lookups) OVER(PARTITION BY S.object_id) * 100 [TableReadActivity%]
				,CONVERT(MONEY,(user_seeks + user_scans + user_lookups)) 
				/ SUM(user_seeks + user_scans + user_lookups) OVER() * 100 [TotalReadActivity%]
			FROM [sys].dm_db_index_usage_stats AS s WITH (NOLOCK)
			INNER JOIN [sys].indexes AS i WITH (NOLOCK)
				ON s.[object_id] = i.[object_id]
			WHERE OBJECTPROPERTY(s.[object_id],''IsUserTable'') = 1
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
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (@errMessage,0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
			END CATCH

			/*----------------------------------------
			--Loop all the user databases to run database specific commands against them
			some build in logic, if more than 10 non-core database, just do the heaviest usage databases.
			Loop logic would set the variables then delete the entry from the @databases table
			----------------------------------------*/
	
	IF (SELECT COUNT(1) FROM @Databases WHERE database_id > 4) > 10
	BEGIN
		RAISERROR (N'Lots of databases, only taking the top 5',0,1) WITH NOWAIT;
	

		DELETE FROM @Databases
		WHERE [TotalIORank] > 10 OR database_id <5

		SELECT @Databasei_Max = COUNT(1) FROM @Databases

		SELECT * from @Databases
	END

	DECLARE @SkipthisDB BIT	
	SET @SkipthisDB = 0
	SET @dynamicSQL = ''
	SET @Databasei_Count = 1; 
	WHILE @Databasei_Count <= @Databasei_Max 
	BEGIN 
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

		/*Now we have the variables, so delete the entry so the table loops become simpler*/
		DELETE FROM @Databases
		WHERE databasename = @DatabaseName

		SET @ErrorMessage = 'Looping Database ' + CONVERT([VARCHAR](4),@Databasei_Count) +' of ' + CONVERT([VARCHAR](4),@Databasei_Max ) + ': [' + @DatabaseName + '] ';
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
			RAISERROR (@ErrorMessage,0,1) WITH NOWAIT;
		END

		--IF (ISNULL(@SecondaryReadRole,'YES') <> 'NO' AND ISNULL(@AGBackupPref,'') <> 'primary') AND EXISTS( SELECT @DatabaseName)
		BEGIN  
			IF (ISNULL(@SecondaryReadRole,'YES') = 'NO' AND @AGCurrentLocation = @ThisServer) 
			SET	@SkipthisDB = 1
			IF @SecondaryReadRole IS NULL
			SET	@SkipthisDB = 0
			IF @SkipthisDB = 0
			BEGIN


		/*13. Find idle indexes*/

			/*---------------------------------------Shows Indexes that have never been used---------------------------------------*/
				SET @CustomErrorText = '['+@DatabaseName+'] Looking at unused indexes'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
				RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;
			END
			SET ANSI_WARNINGS OFF
			
			SET @dynamicSQL = '
			USE ['+@DatabaseName +']
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
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (@errMessage,0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
			END CATCH
			SET ANSI_WARNINGS ON


		/*14. Find heaps*/

			/*---------------------------------------Shows tables without primary key. Heaps---------------------------------------*/

				SET @CustomErrorText = '['+@DatabaseName+'] Looking for heap tables'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
				RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;
			END

			SET @dynamicSQL = CASE 
			WHEN @IsSQLAzure = 0 THEN 'USE ['+@DatabaseName +']'
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
			''['' + DB_NAME(DB_ID()) + '']'' DB
			, ''['' + OBJECT_SCHEMA_NAME(IDX.object_id) +'']'' [Schema] 
			, ''['' +OBJECT_NAME(IDX.object_id) + '']'' AS table_name
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

		FROM ['+@DatabaseName +'].[sys].indexes IDX  
		INNER JOIN ['+@DatabaseName +'].[sys].dm_db_index_usage_stats ius ON IDX.object_id = ius.object_id AND IDX.index_id = ius.index_id --AND IDXPS.database_id = ius.database_id
		INNER JOIN ['+@DatabaseName +'].[sys].partitions p ON IDX.object_id = p.object_id AND IDX.index_id = p.index_id

		WHERE  IDX.type = 0
		--AND rows< 500
	) T1
	CROSS APPLY
	['+@DatabaseName +'].[sys].dm_db_index_physical_stats(DB_ID(), T1.object_id, T1.index_id, partition_number, ''SAMPLED'') IDXPS '
			IF @SkipHeaps = 0
			BEGIN
				
				BEGIN TRY
					INSERT #output_sqldba_org_sp_triage_HeapTable
					EXEC sp_executesql @dynamicSQL;
				END TRY
				BEGIN CATCH
					SELECT @errMessage  = ERROR_MESSAGE()
					RAISERROR (@errMessage,0,1) WITH NOWAIT;
					SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
					RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
				END CATCH
			END
		
			
			SET @CustomErrorText = '['+@DatabaseName+'] Looking for stale statistics'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
				RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;
			END
			SET @dynamicSQL = 'SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
			USE ['+@DatabaseName+'];
			SELECT 
				'''+@DatabaseName+''' [DbName]
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
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (@errMessage,0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
			END CATCH
		
			
		SET @CustomErrorText = '['+@DatabaseName+'] Skipping bad NC Indexes tables'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
				RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;
			END

		   SET @dynamicSQL = 'SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
			USE ['+@DatabaseName+'];
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
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (@errMessage,0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
			END CATCH
			END
			/*----------------------------------------
			--Find badly behaving constraints
			----------------------------------------*/

			/* Constraints behaving badly*/
	SET @CustomErrorText = '['+@DatabaseName+'] Looking for bad constraints'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
				RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;
			END

			SET @dynamicSQL = 'SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
				USE ['+@DatabaseName+'];
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
					, ''['+@DatabaseName+'].['' + s.name + ''].['' + o.name + '']'' [tablename]
					, ''['+@DatabaseName+'].['' + s.name + ''].['' + o.name + ''].['' + i.name + '']'' AS keyname
					, ''DBCC CHECKCONSTRAINTS (['' + i.name + '']) WITH ALL_ERRORMSGS'' [DBCC]
					, ''ALTER TABLE ['+@DatabaseName+'].['' + s.name + ''].'' + ''['' + o.name + ''] WITH CHECK CHECK CONSTRAINT ['' + i.name + '']'' [Fix]
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
					,  ''['+@DatabaseName+'].['' + s.name + ''].'' + ''['' + o.name + '']'' AS TableName
					, ''['+@DatabaseName+'].['' + s.name + ''].['' + o.name + ''].['' + i.name + '']'' AS FKName
					,''DBCC CHECKCONSTRAINTS (['' + i.name + '']) WITH ALL_ERRORMSGS'' [DBCC]
					, ''ALTER TABLE ['+@DatabaseName+'].['' + s.name + ''].'' + ''['' + o.name + ''] WITH CHECK CHECK CONSTRAINT ['' + i.name + '']'' [Fix]
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
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (@errMessage,0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
			END CATCH


			SET @CustomErrorText = '['+@DatabaseName+'] Looking for indexes with possible compression benefit'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
				RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;
			END
			SET @dynamicSQL = '
			USE ['+@DatabaseName+']
			SELECT TOP 10 '''+@DatabaseName+'''
			, CASE 
				WHEN i.index_id >0 THEN ''ALTER INDEX ''+ ''['' + i.[name] + '']''  + '' ON '' 
				ELSE''ALTER TABLE '' 
			END  
			+ ''['+@DatabaseName+']'' + ''.'' + ''['' + s.[name] + '']'' + ''.'' + ''['' + o.[name] + '']'' 
			+ '' REBUILD WITH (DATA_COMPRESSION=PAGE);''
			[Just compress]
			, CASE 
				WHEN ps.lob_used_page_count > 0 THEN  ''ALTER INDEX ''+ ''['' + i.[name] + '']'' + '' ON '' + ''['+@DatabaseName+']'' + ''.'' +  ''['' + s.[name] + '']'' + ''.'' + ''['' + o.[name] + '']'' + '' reorganize WITH (LOB_COMPACTION = ON);''
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
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (@errMessage,0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
			END CATCH
		

		SET @CustomErrorText = '['+@DatabaseName+'] Intelligent compression evaluation'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
				RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;
	END

			SET @dynamicSQL=' 
			SELECT 
			TOP 10 
			'''+@DatabaseName+'''
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
				WHEN i.index_id >0 THEN ''ALTER INDEX ''+ ''['' + i.[name] + '']''  + '' ON '' 
				ELSE ''ALTER TABLE '' 
			END  + ''['+@DatabaseName+']'' 
			+ ''.'' + ''['' + sc.[name] + '']'' + ''.'' + ''['' + o.[name] + '']'' 
			+ '' REBUILD WITH (DATA_COMPRESSION=PAGE);''
			[Just compress]
			, CASE 
				WHEN SUM(s.lob_used_page_count )> 0 THEN  ''ALTER INDEX ''+ ''['' + i.[name] + '']'' + '' ON '' + ''['+@DatabaseName+']'' + ''.'' +  ''['' + sc.[name] + '']'' + ''.'' + ''['' + o.[name] + '']'' + '' reorganize WITH (LOB_COMPACTION = ON);''
				ELSE '''' 
			END [For LOB data]
			FROM ['+@DatabaseName+'].[sys].dm_db_partition_stats AS s
			INNER JOIN ['+@DatabaseName+'].[sys].indexes AS i 
				ON s.[object_id] = i.[object_id] AND s.[index_id] = i.[index_id]
			INNER JOIN ['+@DatabaseName+'].[sys].tables t 
				ON t.OBJECT_ID = i.object_id
			INNER JOIN ['+@DatabaseName+'].[sys].partitions p 
				ON i.object_id = p.OBJECT_ID AND i.index_id = p.index_id
			INNER JOIN ['+@DatabaseName+'].[sys].objects AS o WITH (NOLOCK)	ON o.[object_id] = i.[object_id]
			INNER JOIN ['+@DatabaseName+'].[sys].schemas sc WITH (NOLOCK)	ON o.[schema_id] = sc.[schema_id]
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
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (@errMessage,0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
			END CATCH


	SET @CustomErrorText = '['+@DatabaseName+'] Blocking tables'
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
				RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;
	END

			SELECT @dynamicSQL = ' USE ['+@DatabaseName+']
			SELECT 
			TOP 5 
				'''+@DatabaseName+''' DatabaseName
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
			FROM [sys].dm_db_index_operational_stats(NULL,NULL,NULL,NULL)
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
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (@errMessage,0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
			END CATCH



		SET @CustomErrorText = '['+@DatabaseName+'] FKs without Indexes'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
				RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;
			END

		SELECT @dynamicSQL = ' USE ['+@DatabaseName+']

		SELECT 
			DISTINCT 
			'''+@DatabaseName+''' DatabaseName
			, OBJECT_NAME(a.parent_object_id) AS TableName
			, b.[name]  AS Column_Name
			, N''CREATE NONCLUSTERED INDEX '' + QUOTENAME(N''IX_SQLDBA_FKI_'' + b.[name]) + N'' ON ''
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
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (@errMessage,0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
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
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Evaluated all databases',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Output results from all databases into results table
			----------------------------------------*/
			SET @CustomErrorText = '['+@DatabaseName+'] Looking for Stored Procudure Workload'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
				RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;
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
					DB_NAME(dbid)
					, OBJECT_NAME(objectid,dbid)AS [SP Name]
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
				CROSS APPLY [sys].dm_exec_sql_text(cp.plan_handle) 
				WHERE 1=1
				AND dbid IS NOT NULL
				AND DB_NAME(dbid) IS NOT NULL
				AND objectid is not null
				GROUP BY cp.plan_handle,DBID,objectid 

			END TRY
			BEGIN CATCH
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (@errMessage,0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
			END CATCH


BEGIN TRY
	IF EXISTS (SELECT 1 FROM #output_sqldba_org_sp_triage_MissingIndex ) 
	BEGIN
				SET @CustomErrorText = 'MISSING INDEXES - !Benefit > 1mm!'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
	END
				RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;

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
		SELECT sp.spid,sp.cmd,sp.hostname,DB_NAME(sp.dbid),sp.last_batch,node_id,physical_operator_name,SUM(row_count) row_count,SUM(estimate_row_count) AS estimate_row_count,
		CAST(SUM(row_count)*100 AS float)/SUM(estimate_row_count) as EST_COMPLETE_PERCENT
		FROM [sys].dm_exec_query_profiles eqp
		INNER JOIN [sys].sysprocesses sp on sp.spid=eqp.session_id AND sp.cmd like ''%INDEX%'' OR sp.cmd like ''%ALTER%''
		GROUP BY spid, node_id, physical_operator_name, sp.cmd, sp.hostname, sp.last_batch
		ORDER BY spid,sp.dbid, node_id desc;'' '

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
			, CONVERT([NVARCHAR] (40),'Benefit:'+  CONVERT([VARCHAR](20),CONVERT([BIGINT],T1.magic_benefit_number),0)
				+ '; ' + T1.[Table]
				+ '; Eq:' + ISNULL(T1.equality_columns,'')
				+ '; Ineq:' +  ISNULL(T1.inequality_columns,'')
				+ '; Incl:' +  ISNULL(T1.included_columns,''))
			, CASE 
				WHEN LOG(T1.magic_benefit_number)  < 13 THEN @Result_Warning 
				WHEN LOG(T1.magic_benefit_number) >= 13 AND LOG(T1.magic_benefit_number) < 20 THEN @Result_YourServerIsDead  
				WHEN LOG(T1.magic_benefit_number) >= 20  THEN @Result_ReallyBad
			END
			/*, T2.[SETs] + '; ' + CHAR(13) + CHAR(10)  +*/
			, 'UNION ALL SELECT '''   + REPLACE(T1.ChangeIndexStatement,'< be clever here >', ' ''+ ('+  replace(replace(replace(replace(CONVERT([NVARCHAR] (2000),BeingClever  ), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), '  ',' ') + ') + '' ') + ''' '
			, CASE 
				WHEN LOG(T1.magic_benefit_number) >= 10 AND LOG(T1.magic_benefit_number) < 12 THEN 1
				WHEN LOG(T1.magic_benefit_number) >= 12 AND LOG(T1.magic_benefit_number) < 14 THEN 2
				WHEN LOG(T1.magic_benefit_number) >= 14 AND LOG(T1.magic_benefit_number) < 16 THEN 4
				WHEN LOG(T1.magic_benefit_number) >= 16 AND LOG(T1.magic_benefit_number) < 20 THEN 6
				WHEN LOG(T1.magic_benefit_number) >= 20 AND LOG(T1.magic_benefit_number) < 25  THEN 8
				WHEN LOG(T1.magic_benefit_number) > 25  THEN 12
			END
			FROM #output_sqldba_org_sp_triage_MissingIndex T1 
			LEFT OUTER JOIN #output_sqldba_org_sp_triage_whatsets T2 ON T1.DB = T2.DBname
			WHERE T1.magic_benefit_number > 50000
			ORDER BY magic_benefit_number DESC OPTION (RECOMPILE)

			END TRY
			BEGIN CATCH
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (@errMessage,0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
			END CATCH

	END
END TRY
BEGIN CATCH
	RAISERROR (N'Error with missing index details',0,1) WITH NOWAIT;
	SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
	SELECT @errMessage  = ERROR_MESSAGE()
	RAISERROR (@errMessage,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
			RAISERROR (N'Completed missing index details',0,1) WITH NOWAIT;
		END


BEGIN TRY
		IF EXISTS (SELECT 1 FROM #output_sqldba_org_sp_triage_HeapTable ) 
	BEGIN
					SET @CustomErrorText = 'HEAP TABLES - Bad news'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
				RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;
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
				, REPLACE(REPLACE(LEFT('Rows:' + CONVERT([VARCHAR](20),T1.rows)
					+ ';'+ '['+T1.DB+'].' + '['+T1.[schema]+'].' + '['+T1.[table]+']' 
					+ '; Scan:' + CONVERT([VARCHAR](20),ISNULL(T1.last_user_scan,0) ,120)
					+ '; Seek:' + CONVERT([VARCHAR](20),ISNULL(T1.last_user_seek,0) ,120)
					+ '; Lookup:' + CONVERT([VARCHAR](20),ISNULL(T1.last_user_lookup,0) ,120),3800)
				,'[[','['),']]',']')
				, @Result_Warning
				, REPLACE(REPLACE(LEFT('/*DIRTY FIX, assuming forwarded records*/ALTER TABLE ['+T1.DB+'].' + '['+T1.[schema]+'].' + '['+T1.[table]+'] REBUILD ; RAISERROR (N''Completed heap ['+T1.DB+'].' + '['+T1.[schema]+'].' + '['+T1.[table]+']'' ,0,1) WITH NOWAIT',3800),'[[','['),']]',']')
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
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (@errMessage,0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
			END CATCH
	END
END TRY
BEGIN CATCH
	RAISERROR (N'Error with finding heaps',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
	SELECT @errMessage  = ERROR_MESSAGE()
	RAISERROR (@errMessage,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Found heap tables',0,1) WITH NOWAIT;
	END


BEGIN TRY
		IF EXISTS (SELECT 1 FROM #NeverUsedIndex ) 
	BEGIN
		SET @CustomErrorText = 'STALE INDEXES - Consider removing them at some stage'
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
				RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;
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
				SELECT @errMessage  = ERROR_MESSAGE()
				RAISERROR (@errMessage,0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
				RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
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
			, '%Change:' + CONVERT([VARCHAR](15),s.[ModPerc]) +'%; Rows:' + CONVERT([VARCHAR](15),Rows) + ';Modifications:' + CONVERT([VARCHAR](20),s.ModificationCount) +'; ['+ DBname + '].['+SchemaName+'].['+TableName+']:['+StatisticsName+']'
			, CASE WHEN DATEDIFF(DAY,s.LastUpdated,GETDATE()) < 14 THEN @Result_Warning ELSE @Result_Bad END
			, 'UPDATE STATISTICS [' + DBname + '].['+SchemaName+'].['+TableName+'] ['+StatisticsName+'] ' 
			+ CASE 
				WHEN s.Rows BETWEEN 0 AND 500000 THEN 'WITH FULLSCAN' 
				WHEN s.Rows BETWEEN 500000 AND 5000000 THEN 'WITH SAMPLE 20 PERCENT'
				WHEN s.Rows BETWEEN 5000000 AND 50000000 THEN 'WITH SAMPLE 10 PERCENT'
				WHEN s.Rows > 50000000 THEN 'WITH SAMPLE 5 PERCENT'
				ELSE 'WITH SAMPLE ' + CONVERT([VARCHAR](3),CONVERT(INT,EstPerc)*2) + 'PERCENT' 
				END +'; PRINT ''[' + DBname + '].['+SchemaName+'].['+TableName+'] ['+StatisticsName+'] Done ''' [UpdateStats]
			, 0.15
			 FROM #output_sqldba_org_sp_triage_Action_Statistics s 
			 ORDER BY s.[ModPerc] DESC OPTION (RECOMPILE);/*They are like little time capsules.. just sitting there.. waiting*/

	END
END TRY
BEGIN CATCH
	SELECT @errMessage  = ERROR_MESSAGE()
	RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with stale stats',0,1) WITH NOWAIT;
	SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR(@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	SELECT @errMessage  = ERROR_MESSAGE()
	RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with SP workload',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with server details',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	SELECT @errMessage  = ERROR_MESSAGE()
	RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with non default server settings',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	SELECT @errMessage  = ERROR_MESSAGE()
	RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with current active users',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with trust issues.. it happens',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH	
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with connected users',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
select d.name, [LastSomethingHours] = DATEDIFF(HOUR,ISNULL(
(select X1= max(bb.xx) 
FROM (
    select xx = max(last_user_seek) 
        where max(last_user_seek) is not null 
    union all 
    select xx = max(last_user_scan) 
        where max(last_user_scan) is not null 
    union all 
    select xx = max(last_user_lookup) 
        where max(last_user_lookup) is not null 
    union all 
        select xx = max(last_user_update) 
        where max(last_user_update) is not null) bb) ,@lastservericerestart),GETDATE())
FROM sysdatabases d 
LEFT OUTER JOIN [sys].dm_db_index_usage_stats s on d.dbid= s.database_id 
WHERE database_id > 4
GROUP BY d.name
END TRY
BEGIN CATCH
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Issue with @confidence',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH

BEGIN TRY	
IF @ShowMigrationRelatedOutputs = 1
BEGIN
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
	,ISNULL(CONVERT([VARCHAR](10),CASE
		WHEN pages.[TotalPages in MB] > 0 THEN 100
		WHEN con.number_of_connections > 0 THEN confidence.low 
		ELSE confidence.high  
	END),'')  + '% likely'    
	+  '; Connections: ' + ISNULL(CONVERT([VARCHAR](10),con.number_of_connections ),'')
	+ '; HoursActive: '+ ISNULL(CONVERT([VARCHAR](10),DATEDIFF(HOUR,@lastservericerestart,GETDATE())),'') 
	+ '; Pages(MB) in memory: ' + ISNULL(CONVERT([VARCHAR](10), ISNULL(pages.[TotalPages in MB],0)),'') 
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
	, MAX(o.modify_date) [ObjectModifyDate]
	, MAX(ius.last_user_seek)    [last_user_seek]
	, MAX(ius.last_user_scan)   [last_user_scan]
	, MAX(ius.last_user_lookup) [last_user_lookup]
	, MAX(ius.last_user_update) [last_user_update]
	FROM
		@sysdatabasesTable db
		LEFT OUTER JOIN [sys].dm_db_index_usage_stats ius  ON db.database_id = ius.database_id
		LEFT OUTER JOIN  [sys].all_objects o ON o.object_id = ius.object_id AND o.type = 'U'
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
	LEFT OUTER JOIN (
	SELECT DB_NAME (database_id) AS 'Database Name'
	,  COUNT(*) *8/1024 AS [TotalPages in MB]
	FROM [sys].dm_os_buffer_descriptors
	GROUP BY database_id
	) pages ON pages.[Database Name] = base.name

	LEFT OUTER JOIN (
	SELECT DBName
	  , intervals.n as [Hours]
	  , intervals.x as [TargetActiveHours]
	  , CONVERT(MONEY,(p - se * 1.96)*100) as low
	  , CONVERT(MONEY,(intervals.p * 100)) as mid
	  , CONVERT(MONEY,(p + se * 1.96)*100) as high 
	FROM (
	  SELECT 
		rates.*, 
		sqrt(p * (1 - p) / n) as se -- calculate se
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
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Database usage likelyhood measured',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
		RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
	END
			
END
END TRY
BEGIN CATCH
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
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
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Create DMA commands',0,1) WITH NOWAIT;
	END
	END
END TRY
BEGIN CATCH
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with creating DMA commands',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	

			/*----------------------------------------
			--Getting Trace data
			----------------------------------------*/

--https://www.ptr.co.uk/blog/how-improve-your-sql-server-speed
--The script below can be use to search for automatic Log File Growths, using the background profiler trace that SQL Server maintains.
DECLARE @tracepath [NVARCHAR] (260)

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

	SET @CustomErrorText = 'Default trace data'
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 32, 'Trace Data','------','------'

	IF @IsSQLAzure = 0
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
		FROM fn_trace_gettable('''+@tracepath+''', default) g
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
		FROM fn_trace_gettable('''+@tracepath+''', default) g
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
		FROM fn_trace_gettable('''+@tracepath+''', default) g
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with trace part 1 before autogrowth',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
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
	FROM fn_trace_gettable('''+@tracepath+''', default) g
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Issue with Trace data insert',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
	PRINT @dynamicSQL
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with compression candidates',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error compression validation',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
		+',[last_user_scan]:' + CONVERT([VARCHAR],ISNULL(IndexType,''),120) 
		+',[last_user_lookup]:'+CONVERT([VARCHAR],ISNULL(IndexType,''),120) 
		+',[last_user_seek]:'+ CONVERT([VARCHAR],ISNULL(IndexType,''),120)
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
		FROM #output_sqldba_org_sp_triage_indexusage
		)  S
		WHERE RankMe < 25
END TRY
BEGIN CATCH
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with index usage stats',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
		WHERE UPPER(IndexName) LIKE '%LEXEL%' 
		OR UPPER(IndexName) LIKE '%SQLDBA%'
		

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
		WHERE UPPER(IndexName) LIKE '%LEXEL%' 
		OR UPPER(IndexName) LIKE '%SQLDBA%'
		GROUP BY dbname,[ObjectName]
END TRY
BEGIN CATCH
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with custom index evaluation',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with currently blocking tables',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	FROM
		#dadatafor_exec_query_stats qs
	GROUP BY qs.plan_handle 
	--HAVING CONVERT(MONEY,(SUM(qs.total_elapsed_time) / 1000000.0)) > 1
	) s
OUTER APPLY
	[sys].dm_exec_query_plan(s.[Plan Handle]) AS qp
LEFT OUTER JOIN
	@sysdatabasesTable DBs ON qp.dbid = DBs.database_id
OUTER APPLY
	[sys].dm_exec_sql_text(s.[Plan Handle]) AS st
WHERE [RankIO] <= 35
ORDER BY [RankIO]  ASC
END TRY
BEGIN CATCH
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with selecting the top queries',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Usage top xx queries completed',0,1) WITH NOWAIT;
	END

			/*----------------------------------------
			--Disk Cluster Sizes
			----------------------------------------*/
SET @CustomErrorText = 'Checking volume cluster size'
DECLARE @svrName [VARCHAR](255)
DECLARE @output TABLE (line [VARCHAR](255))
DECLARE @volumeinfo TABLE (Info [NVARCHAR] (500))
SET @svrName = @ThisServer 
IF CHARINDEX ('\', @svrName) > 0
BEGIN
       SET @svrName = SUBSTRING(@svrName, 1, CHARINDEX('\',@svrName)-1)
END
BEGIN TRY
	IF @StateOfXP_CMDSHELL = 1
	BEGIN
		SET @powershellrun = 'powershell.exe -c "Get-WmiObject -ComputerName ' + QUOTENAME(@svrName,'''') + ' -Class Win32_Volume -Filter ''DriveType = 3'' | select Name, BlockSize | format-table"'
		INSERT INTO @volumeinfo
		EXEC xp_cmdshell @powershellrun
	END

	DECLARE @volumes TABLE 
	(
		Volume [NVARCHAR] (500)
		, [BlockSize] INT
	)
	INSERT INTO @volumes
	SELECT [Measure], T2.Value
	--, RANK ( ) OVER ( partition by T2.Measure ORDER BY Value ) RowID
	FROM (
	SELECT LEFT(Info, PATINDEX('%\%',Info)) [Measure]
	, REPLACE(RIGHT(Info, LEN(Info)-PATINDEX('%\%',Info)),' ','') [Value]
	FROM (
	SELECT REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(Info,'  ',' '),'  ',' '),'  ',' '),'  ',' '),'  ',' '),'  ',' ') Info
	FROM @volumeinfo
	) T1
	WHERE Info LIKE '[A-Z][:]\%' 
	) T2
END TRY
BEGIN CATCH
	
	RAISERROR (N'Error with volume cluster size',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
	SELECT @errMessage  = ERROR_MESSAGE()
	--IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
	RAISERROR (@errMessage,0,1) WITH NOWAIT; 
END CATCH

BEGIN TRY
	SET @CustomErrorText = 'Disk cluster size'
	INSERT #output_sqldba_org_sp_triage (
		[SectionID]
		, [Section]
		, [Summary]
		, [Details]
	) 
	SELECT 40, 'Disk Cluster size','------','------'
	INSERT #output_sqldba_org_sp_triage (
	SectionID
	, Section
	, Summary
	, Details
	)
	SELECT 40
		,'Disk Cluster size'
		, Volume
		, [BlockSize]
	FROM @volumes V1
END TRY
BEGIN CATCH
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with volume cluster size to output table',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Done with disk cluster sizes',0,1) WITH NOWAIT;
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with reading or finding CommandLog table for Ola output',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Done reading  CommandLog table for Ola output0',0,1) WITH NOWAIT;
	END



			/*----------------------------------------
			--Calculate daily IO workload
			----------------------------------------*/

SET @CustomErrorText = 'Daily workload'
--BEGIN TRY
	set @dbid = db_id(); 
	SET @cnt = 0; 
	SET @record_count = 0; 
	DECLARE @sql_handle varbinary(64); 
	DECLARE @sql_handle_string [NVARCHAR] (130); 
	SET @grand_total_worker_time = 0 ; 
	SET @grand_total_IO = 0 ; 


	--REWRITE FROM HERE

	/* SELECT @TotalIODailyWorkload = SUM(CONVERT(MONEY,CONVERT(FLOAT,t_total_IO) * 8 /*KB*/ /1024/*MB*//1024)/@DaysOldestCachedQuery)  
	FROM #LEXEL_OES_stats_output
	*/
 SELECT @TotalIODailyWorkload = CONVERT(MONEY,SUM(qs.total_physical_reads) + SUM(qs.total_logical_reads) + SUM(qs.total_logical_writes))  * 8 /1024/1024 /@DaysOldestCachedQuery
	FROM #dadatafor_exec_query_stats qs

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
	, LEFT('Oldest Cache: ' + CONVERT([VARCHAR](15), @DaysOldestCachedQuery) + '; Days Uptime: ' + CONVERT([VARCHAR](15),@DaysUptime),3500) [Section]
	, LEFT(CONVERT([VARCHAR](50),@TotalIODailyWorkload)
	+ 'GB/day; ' + CONVERT([VARCHAR](50),SUM(qs.execution_count)) +' executions/day; '
	+ CONVERT([VARCHAR](50),CONVERT(MONEY,SUM(qs.total_elapsed_time) / 1000000.0)/1000) +' (avg) *[DailyGB; DailyExecutions; AverageTime(s)]', 3500) Summary
	, 'Total' [Details]
	FROM #dadatafor_exec_query_stats qs


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
		, LEFT('/*' + ISNULL(O.type_desc COLLATE DATABASE_DEFAULT,'') + '; '+ ISNULL(O.name COLLATE DATABASE_DEFAULT,'') + ' [' + ISNULL(DBs.name COLLATE DATABASE_DEFAULT,'') + '].[' + ISNULL(SC.name COLLATE DATABASE_DEFAULT,'') + '] > */' + ISNULL(left(replace(replace(replace(replace(CONVERT([NVARCHAR] (3600),st.text COLLATE DATABASE_DEFAULT), CHAR(9), ' '),CHAR(10),' '), CHAR(13), ' '), ' ',' ') ,3800),''),3850) [Details]  
	FROM
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
		
		FROM
			#dadatafor_exec_query_stats qs
		GROUP BY qs.plan_handle 
		--HAVING CONVERT(MONEY,(SUM(qs.total_elapsed_time) / 1000000.0)) > 1
		) s
	OUTER APPLY
		[sys].dm_exec_query_plan(s.[Plan Handle]) AS qp
	LEFT OUTER JOIN
		@sysdatabasesTable DBs ON qp.dbid = DBs.database_id
	OUTER APPLY
		[sys].dm_exec_sql_text(s.[Plan Handle]) AS st
	LEFT OUTER JOIN [sys].objects O On O.object_id = qp.objectid
	LEFT OUTER JOIN [sys].schemas SC ON SC.schema_id = O.schema_id
	WHERE [RankIO] <= 35
	ORDER BY [RankIO]  ASC
	OPTION (RECOMPILE);

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Daily workload calculated',0,1) WITH NOWAIT;
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
IF @IsSQLAzure = 0
BEGIN
	INSERT INTO @sysloginsTable
	EXEC sp_executesql @dynamicSQL
END

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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error checking password issues',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with reading or finding Depreacted features',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH

	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with FKs with no indexes',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with Perfmon Counters',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with Batch averages from perfmon',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Done looking at Batch averages from perfmon',0,1) WITH NOWAIT;
	END

	/*----------------------------------------
	--TCP listener states
	----------------------------------------*/
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with TCP listener states',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with Endpoints',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with Connections ports and IPs',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Done looking at Connections ports and IPs',0,1) WITH NOWAIT;
	END

	/*----------------------------------------
	--Server registry
	----------------------------------------*/
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with Server registry',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
			'',"CertSubject]":"'' + ISNULL(subject,'''') +''"''
			+
			'',"CertIssuer]":"'' + ISNULL(issuer_name,'''') +''"''

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
			'',"pvt_key_encryption_type_desc":"'' + ISNULL(pvt_key_encryption_type_desc,'''')
			'',"KeyLength":"'
			--Does not work in SQL 2014, 12
			+ CASE WHEN CONVERT( TINYINT ,@SQLVersion) <= 12 -- 2012- 
			THEN '' + 'CONVERT(VARCHAR(10),ISNULL(key_length,'''')) '
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
		EXEC sp_executesql @dynamicSQL		

		SET @CustomErrorText = 'Done looking at '+ @CustomErrorText
END TRY
BEGIN CATCH
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	SET @CustomErrorText = 'Error with '+ @CustomErrorText
	RAISERROR (@CustomErrorText,0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@CustomErrorText,0,1) WITH NOWAIT;
	END


	/*----------------------------------------
	--Availability Group synchronization lag
	----------------------------------------*/
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	SET @CustomErrorText = 'Error with '+ @CustomErrorText
	RAISERROR (@CustomErrorText,0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (@CustomErrorText,0,1) WITH NOWAIT;
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
	SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
	RAISERROR (N'Error with the thing',0,1) WITH NOWAIT;
		SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
	RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Done looking at FKs with no indexes',0,1) WITH NOWAIT;
	END
	/*----------------------------------------
	--TEMPLATE
	----------------------------------------*/
/*ADD NEW sections before this line*/
/*ADD NEW sections before this line*/

/*----------------------------------------
--Add Latest Blitz output
----------------------------------------*/
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

/*----------------------------------------
--Add Latest Security Checklist output
----------------------------------------*/
/*

*/
IF OBJECT_ID('master.dbo.sqldba_stpSecurity_Checklist_Table') IS NULL
/*If no Blitz table, run Blitz*/
BEGIN
	IF @Debug = 1
		RAISERROR (N'Skipping sqldba_stpSecurity_Checklist results, cannot find output table',0,1) WITH NOWAIT;
	
END
IF OBJECT_ID('master.dbo.sqldba_stpSecurity_Checklist_Table') IS NOT NULL
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
	FROM master.dbo.sqldba_stpSecurity_Checklist_Table 
	WHERE [evaldate] = (SELECT max([evaldate]) 
	FROM master.dbo.sqldba_stpSecurity_Checklist_Table 
	HAVING DATEADD(DAY,-2,GETDATE()) < max([evaldate]) )
	ORDER BY [code] ASC'
	)

END



			/*----------------------------------------
			--select output
			----------------------------------------*/
IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'Cleaning up output table',0,1) WITH NOWAIT;
	END

UPDATE  #output_sqldba_org_sp_triage
SET evaldate = @evaldate
, SQLInstance = @ThisServer
, domain = @ThisDomain

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
	ORDER BY ID ASC
	OPTION (RECOMPILE)
END



IF UPPER(LEFT(@Export,1)) = 'T'
BEGIN
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
		EXEC sp_executesql @dynamicSQL;	
	END
	ELSE
	BEGIN
		SET @dynamicSQL = 'DELETE FROM ' + @ExportDBName + '.' + @ExportSchema  + '.' + @ExportTableName + '
		WHERE	evaldate < DATEADD(DAY, - ' + CONVERT([VARCHAR](5),@ExportCleanupDays) + ', GETDATE())'
		BEGIN TRY
			EXEC sp_executesql @dynamicSQL;	
		END TRY
		BEGIN CATCH
			SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
			RAISERROR (N'Error creating table',0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
			RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
		END CATCH

	END
	/*
	Okay, time to be clever. Let's assume the table was built in a previous version, let's compare columns and see what needs to be added.Error with modifying output table
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
	  INNER JOIN tempdb.[sys].tables  t ON t.object_id = c.object_id
	  WHERE t.name like ''%output_man_script%''
	  ) targetcolumns
  LEFT OUTER JOIN (
	  SELECT 
	  c.name
	  , column_id
	  , max_length
	  FROM '+@ExportDBName +'.[sys].columns c
  INNER JOIN  '+@ExportDBName +'.[sys].tables  t ON t.object_id = c.object_id
  WHERE t.name = ''' + @ExportTableName + '''
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
				ALTER TABLE '+  @ExportDBName +'.' + @ExportSchema + '.' + @ExportTableName + ' ADD QueryPlan XML NULL
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
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
			RAISERROR (N'Error with modifying output table',0,1) WITH NOWAIT;
				SET @CustomErrorText = REPLACE(@CustomErrorText,'[','Error - [')
			RAISERROR	  (@CustomErrorText,0,1) WITH NOWAIT;
		END CATCH
	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
		PRINT @dynamicSQL
	END CATCH


	IF @ShowOnScreenWhenResultsToTable = 1 
	BEGIN
		IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
			RAISERROR (N'Results to screen',0,1) WITH NOWAIT;
		END
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
		ORDER BY ID ASC
	END
END

/*Check to send out the results over email as well*/
IF @MailResults = 1 

/*
DECLARE @EmailSubject [NVARCHAR] (500)
DECLARE @EmailRecipients [NVARCHAR] (500) 
DECLARE @MaxID [NVARCHAR] (25)
SELECT @MaxID= MAX(ID)  FROM   [master].[dbo].[sqldba_sp_triage®_output]
DECLARE @evaldate [NVARCHAR] (25)
DECLARE @ThisDomain [NVARCHAR] (50)
DECLARE @ThisServer [NVARCHAR] (50)
DECLARE @CharToCheck CHAR(1)
SET @CharToCheck = '\'

SELECT @evaldate = evaldate, @ThisDomain = domain , @ThisServer = SQLInstance 
FROM   [master].[dbo].[sqldba_sp_triage®_output]
WHERE ID = @MaxID

DECLARE @EmailBody [NVARCHAR] (500) 
DECLARE @query_result_separator [NVARCHAR] (50)
DECLARE @StringToExecute [NVARCHAR] (4000)
DECLARE @EmailProfile [NVARCHAR] (500)
DECLARE @AttachfileName [NVARCHAR] (500)
SET @query_result_separator = '~';--char(9);
SET @EmailRecipients ='scriptoutput@sqldba.org'
SET @EmailSubject = 'sqldba_sp_triage®_data for ' +@ThisDomain + ' '+@ThisServer + '' + REPLACE(REPLACE(REPLACE(@evaldate,'-','_'),':',''),' ','');

SET @AttachfileName = 'sqldba_sp_triage®_data__' +REPLACE(@ThisDomain,'.','_') + '_'+ REPLACE(@ThisServer,@CharToCheck,'_') + '_' + REPLACE(REPLACE(REPLACE(@evaldate,'-','_'),':',''),' ','') +'.csv' 

	SET @StringToExecute = '
SET NOCOUNT ON;
SELECT ID,evaldate,domain,SQLInstance,SectionID,Section,Summary,Severity,Details,HoursToResolveWithTesting,QueryPlan
FROM (
SELECT 
CONVERT([NVARCHAR] (25),			''ID'') ID
, CONVERT([NVARCHAR] (50),		''evaldate'') evaldate
, CONVERT([NVARCHAR] (50),		''domain'') domain
, CONVERT([NVARCHAR] (50),		''SQLInstance'' ) SQLInstance
, CONVERT([NVARCHAR] (10),		''SectionID'') SectionID
, CONVERT([NVARCHAR] (1000),		''Section'') Section
, CONVERT([NVARCHAR] (4000),		''Summary'') Summary
, CONVERT([NVARCHAR] (15),		''Severity'') Severity
, CONVERT([NVARCHAR] (4000),		''Details'') Details
, CONVERT([NVARCHAR] (35),		''HoursToResolveWithTesting'') HoursToResolveWithTesting
, CONVERT([NVARCHAR] (4000),		''QueryPlan'') QueryPlan
, 0 Sorter
UNION ALL
SELECT 
ID,evaldate,domain,SQLInstance,SectionID,Section,Summary,Severity,Details,HoursToResolveWithTesting,QueryPlan, Sorter
FROM 
(
SELECT TOP 100 PERCENT CONVERT([NVARCHAR] (25),T1.ID) ID
	,  REPLACE(T1.evaldate,''~'',''-'') evaldate
	,  REPLACE(domain,''~'',''-'') domain
	,  REPLACE(SQLInstance ,''~'',''-'') SQLInstance
	,  REPLACE(CONVERT([NVARCHAR] (10), SectionID),''~'',''-'')  SectionID
	,  REPLACE(Section,''~'',''-'') Section
	,  REPLACE(Summary,''~'',''-'') Summary
	,  REPLACE(Severity,''~'',''-'') Severity
	,  replace(replace(replace(replace( ISNULL(REPLACE(Details,''~'',''-''),''''), CHAR(9), '' ''),CHAR(10),'' ''), CHAR(13), '' ''), ''  '','' '') [Details]
	,  REPLACE(CONVERT([NVARCHAR] (10),HoursToResolveWithTesting),''~'',''-'') HoursToResolveWithTesting
	,  REPLACE(QueryPlan,''~'',''-'')QueryPlan
	,T1.ID Sorter
	FROM   [master].[dbo].[sqldba_sp_triage®_output]
T1
INNER JOIN (
SELECT MAX(evaldate) evaldate   FROM   [master].[dbo].[sqldba_sp_triage®_output]
) T2
ON T1.evaldate = T2.evaldate
) T3
) T4
ORDER BY Sorter ASC

; SET NOCOUNT OFF;';

					
SET @EmailBody = @EmailSubject;

--EXECUTE msdb.dbo.sysmail_configure_sp 'MaxFileSize', '10000000';
IF @EmailProfile IS NULL
BEGIN
	EXEC msdb.dbo.sp_send_dbmail
	@recipients = @EmailRecipients,
	@subject = @EmailSubject,
	@body = @EmailBody,
	@query_attachment_filename =@AttachfileName,
	@attach_query_result_as_file = 1,
	@query_result_header = 0,
	@execute_query_database = 'master', 
	@query_result_width = 32767,
	@append_query_error = 1,
	@query_result_no_padding = 1,
	@query_result_separator = @query_result_separator,
	@query = @StringToExecute EXECUTE AS LOGIN = N'sa';
 END



*/
BEGIN
IF @Debug = 1
		RAISERROR (N'Results to mail',0,1) WITH NOWAIT;
DECLARE @EmailSubject [NVARCHAR] (500)
SET @EmailSubject = 'sqldba_sp_triage®_data for ' +@ThisDomain + ' '+@ThisServer + '' + REPLACE(REPLACE(REPLACE(@evaldate,'-','_'),':',''),' ','');
DECLARE @EmailBody [NVARCHAR] (500) 
DECLARE @query_result_separator [NVARCHAR] (50)
DECLARE @StringToExecute [NVARCHAR] (4000)
DECLARE @EmailProfile [NVARCHAR] (500)
DECLARE @AttachfileName [NVARCHAR] (500)
SET @query_result_separator = '~';--char(9);
SET @AttachfileName = 'sqldba_sp_triage®_data__' +REPLACE(@ThisDomain,'.','_') + '_'+ REPLACE(@ThisServer,@CharToCheck,'_') + '_' + REPLACE(REPLACE(REPLACE(@evaldate,'-','_'),':',''),' ','') +'.csv' 

/*Yes, it is a mouth full, but it works to create a nicely formed, ready to use CSV file.*/
	SET @StringToExecute = '
SET NOCOUNT ON;
SELECT ID,evaldate,domain,SQLInstance,SectionID,Section,Summary,Severity,Details,HoursToResolveWithTesting,QueryPlan
FROM (
SELECT 
CONVERT([NVARCHAR] (25),			''ID'') ID
, CONVERT([NVARCHAR] (50),		''evaldate'') evaldate
, CONVERT([NVARCHAR] (50),		''domain'') domain
, CONVERT([NVARCHAR] (50),		''SQLInstance'' ) SQLInstance
, CONVERT([NVARCHAR] (10),		''SectionID'') SectionID
, CONVERT([NVARCHAR] (1000),		''Section'') Section
, CONVERT([NVARCHAR] (4000),		''Summary'') Summary
, CONVERT([NVARCHAR] (15),		''Severity'') Severity
, CONVERT([NVARCHAR] (4000),		''Details'') Details
, CONVERT([NVARCHAR] (35),		''HoursToResolveWithTesting'') HoursToResolveWithTesting
, CONVERT([NVARCHAR] (4000),		''QueryPlan'') QueryPlan
, 0 Sorter
UNION ALL
SELECT 
ID,evaldate,domain,SQLInstance,SectionID,Section,Summary,Severity,Details,HoursToResolveWithTesting,QueryPlan, Sorter
FROM 
(
SELECT TOP 100 PERCENT CONVERT([NVARCHAR] (25),T1.ID) ID
,  REPLACE(T1.evaldate,''~'',''-'') evaldate
,  REPLACE(domain,''~'',''-'') domain
,  REPLACE(SQLInstance ,''~'',''-'') SQLInstance
,  REPLACE(CONVERT([NVARCHAR] (10), SectionID),''~'',''-'')  SectionID
,  REPLACE(Section,''~'',''-'') Section
,  REPLACE(Summary,''~'',''-'') Summary
,  REPLACE(Severity,''~'',''-'') Severity
,  replace(replace(replace(replace( ISNULL(REPLACE(Details,''~'',''-''),''''), CHAR(9), '' ''),CHAR(10),'' ''), CHAR(13), '' ''), ''  '','' '') [Details]
,  REPLACE(CONVERT([NVARCHAR] (10),HoursToResolveWithTesting),''~'',''-'') HoursToResolveWithTesting
,  REPLACE(QueryPlan,''~'',''-'')QueryPlan
,T1.ID Sorter
 FROM   ['+ @ExportDBName +'].[' + @ExportSchema +'].[' + @ExportTableName + ']
T1
INNER JOIN (
SELECT MAX(evaldate) evaldate   FROM   ['+ @ExportDBName +'].[' + @ExportSchema +'].[' + @ExportTableName + ']
) T2
ON T1.evaldate = T2.evaldate
) T3
) T4
ORDER BY Sorter ASC

; SET NOCOUNT OFF;';

					
					SET @EmailBody = @EmailSubject;
					/*Make sure there is space to send*/
					--EXECUTE msdb.dbo.sysmail_configure_sp 'MaxFileSize', '10000000';
					IF @EmailProfile IS NULL
					BEGIN
					SET @dynamicSQL='
						EXEC msdb.dbo.sp_send_dbmail
						 @recipients = @EmailRecipients,
						 @subject = @EmailSubject,
						 @body = @EmailBody,
						 @query_attachment_filename =@AttachfileName,
						 @attach_query_result_as_file = 1,
						 @query_result_header = 0,/*Set to 0 to Turn Headers off, makes for better CSV files as long as we include headers in Select statement*/
						  @execute_query_database = @ExportDBName, 
						 @query_result_width = 32767,
						 @append_query_error = 1,
						 @query_result_no_padding = 1,
						 @query_result_separator = @query_result_separator,
						 @query = @StringToExecute EXECUTE AS LOGIN = N''sa''
						 '
						 EXEC sp_executesql @dynamicSQL 
						 ;
					 END
							
END


	/*Before cleaning out tables, check if any other settings need to be turned OFF/ON*/
IF @TurnNumericRoundabortOn = 1
BEGIN
	SET NUMERIC_ROUNDABORT ON;
END
		
	/*Housekeeping*/
	IF OBJECT_ID(@ExportDBName + '.' + @ExportSchema  + '.' + @ExportTableName) IS NOT NULL AND @CleanupTime IS NOT NULL
	BEGIN TRY
		RAISERROR (N'Cleaning output table',0,1) WITH NOWAIT;
		SET @dynamicSQL = '
		DECLARE @filterdate DATETIME
		SET @filterdate = DATEADD(DAY,-' +CONVERT([VARCHAR](5),@CleanupTime)+',GETDATE())
		DELETE FROM ' + @ExportDBName + '.' + @ExportSchema  + '.' + @ExportTableName + '
		WHERE evaldate < CONVERT([VARCHAR],@filterdate,120)'
		--PRINT @dynamicSQL
		EXEC sp_executesql @dynamicSQL;	
	END TRY
	BEGIN CATCH
		SELECT @errMessage  = ERROR_MESSAGE()
		RAISERROR (@errMessage,0,1) WITH NOWAIT;
		RAISERROR (N'Failed to clean old records in output table',0,1) WITH NOWAIT;
	END CATCH


	IF @Debug = 1
	BEGIN
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
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

	IF OBJECT_ID('tempdb.dbo.#LEXEL_OES_stats_sql_handle_convert_table', 'U') IS NOT NULL
		DROP TABLE #LEXEL_OES_stats_sql_handle_convert_table;

	IF OBJECT_ID('tempdb.dbo.#LEXEL_OES_stats_objects', 'U') IS NOT NULL
		DROP TABLE #LEXEL_OES_stats_objects;

	IF OBJECT_ID('tempdb.dbo.#LEXEL_OES_stats_object_name', 'U') IS NOT NULL
		DROP TABLE #LEXEL_OES_stats_object_name;

	IF OBJECT_ID('tempdb..#sql_handle_convert_table') IS NOT NULL
		DROP TABLE #sql_handle_convert_table;

	IF OBJECT_ID('tempdb..#perf_report_objects') IS NOT NULL
		DROP TABLE #perf_report_objects;

	IF OBJECT_ID('tempdb.dbo.#LEXEL_OES_stats_output', 'U') IS NOT NULL
		DROP TABLE #LEXEL_OES_stats_output;

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
		SET @DebugTimeMSG = CONVERT([VARCHAR],GETDATE(),120) +' previous step took: ' + CONVERT([VARCHAR](5),DATEDIFF(MILLISECOND,@DebugTime,GETDATE() )) + ' milliseconds'
		SET @DebugTime = GETDATE()
		IF @ShowDebugTime = 1 RAISERROR( @DebugTimeMSG,0,1) WITH NOWAIT; 
		RAISERROR (N'All done',0,1) WITH NOWAIT;
	END
    SET NOCOUNT OFF;
	
END
