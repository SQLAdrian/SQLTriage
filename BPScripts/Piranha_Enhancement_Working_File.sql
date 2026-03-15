/* بسم الله الرحمن الرحيم */
/* In the name of God, the Merciful, the Compassionate */

/* ============================================================================
   PIRANHA INTERCEPTOR DESTROYER - ENHANCEMENT WORKING FILE
   ============================================================================
   
   PURPOSE:
   This is a resumable working file that identifies improvements and best 
   practices that can be extracted and integrated into the main Piranha script.
   
   ANALYSIS DATE: 2026-03-12
   
   SOURCE FILES ANALYZED:
   - BPScripts/02. Post implementation configuration script - The Piranha Interceptor Destroyer.sql
   - BPScripts/AddTraceflags.ps1
   - BPScripts/doSPNs.sql
   - BPScripts/03. dba quick view.sql
   
   _OtherBPs FOLDER STATUS: Empty at time of analysis
   
   ============================================================================ */

USE [master]
GO

/* ============================================================================
   SECTION 0: ANALYSIS SUMMARY
   ============================================================================

CURRENT PIRANHA SCRIPT COVERAGE:
--------------------------------
✅ Configuration logging (DBA_Configuration_Log table + sp_LogConfigurationChange)
✅ Permission checks before execution
✅ Configuration snapshots (sys.databases, database_files, traceflags)
✅ Optional mapped drive / emergency file creation
✅ Ola Hallengren job schedule integration
✅ Deadlock XEvent capture session
✅ Blocked process threshold configuration
✅ Performance best practices:
   - Lock Pages in Memory (Enterprise Edition)
   - Accelerated Database Recovery (SQL 2019+)
   - tempdb data file recommendations
   - Query Store optimization
   - Power Plan validation
✅ Database Mail configuration
✅ Comprehensive SQL Agent alerts:
   - Severity levels (19-25)
   - Critical error numbers (823, 824, 825, 1205, 9002, etc.)
   - Always On/AG specific alerts
   - Memory and I/O errors
   - Security monitoring (18456)
✅ Maintenance jobs (error log cycling)
✅ sp_configure settings (remote admin, recovery interval)
✅ Default trace validation
✅ SQL Agent properties (job history, CPU polling)
✅ Backup settings (checksum, compression, contained DB)
✅ MAXDOP and Cost Threshold for Parallelism

IDENTIFIED ENHANCEMENTS (From Other Scripts):
---------------------------------------------

1. TRACE FLAGS (from AddTraceflags.ps1)
   - Missing startup trace flags that should be evaluated
   - T3226: Suppress successful backup messages to error log
   - T1117/T1118: tempdb allocation (SQL 2014 and earlier only)
   - T2371: Auto statistics update threshold
   - T4199: Enable plan guide changes
   - T2453: Enable table variable row count estimate
   - T1800: Optimize for SSD/flash storage
   - T9488: Legacy cardinality estimation
   
   RECOMMENDATION: Add trace flag evaluation and recommendation section

2. SPN REGISTRATION (from doSPNs.sql)
   - Kerberos authentication requires proper SPN registration
   - Missing validation of SPN configuration
   - Critical for environments using Windows authentication
   
   RECOMMENDATION: Add SPN validation and script generation

3. EXTENDED EVENTS SESSIONS (Missing)
   - Failed login tracking (currently only via alert on 18456)
   - Security audit events
   - Performance session for query profiling
   
   RECOMMENDATION: Add additional XEvent sessions

4. ADDITIONAL SECURITY HARDENING
   - Server-level password policies
   - Certificate validation
   - Endpoint security
   
5. PERFORMANCE COLLECTIONS
   - More comprehensive baseline collection
   - Wait statistics snapshot
   - Performance counter collection

6. DATABASE-LEVEL SETTINGS
   - Auto-shrink disabled verification
   - Page verify settings (CHECKSUM)
   - Compatibility level recommendations

   ============================================================================ */

/* ============================================================================
   SECTION 1: PROPOSED ADDITIONS - TRACE FLAGS EVALUATION
   ============================================================================

DECLARE @SQLVersion INT;
SELECT @SQLVersion = @@MicrosoftVersion / 0x01000000;

PRINT '=== TRACE FLAG RECOMMENDATIONS ===';
PRINT 'SQL Server Version: ' + CAST(@SQLVersion AS VARCHAR(10));

-- Note: These should be added as STARTUP trace flags via SQL Server Configuration Manager
-- or via registry for automatic startup

-- Trace flags recommended for all versions
-- T3226: Suppress successful backup messages in error log (reduces noise)
-- T1204, T1222: Deadlock capture (already covered by XEvent in Piranha)
-- T4199: Enable plan guide changes (query optimizer fixes)

-- Trace flags for SQL 2012+ (version 11+)
IF @SQLVersion >= 11
BEGIN
    PRINT 'For SQL 2012+: Consider T2453 (table variable row estimation)';
    PRINT 'For SQL 2012+: Consider T9488 (legacy cardinality estimation)';
END

-- Trace flags for SQL 2016+ (version 13+)
IF @SQLVersion >= 13
BEGIN
    PRINT 'For SQL 2016+: Consider T9567 (enable stretch database)';
END

-- Trace flags for older SQL (SQL 2014 and earlier - version 12 and below)
IF @SQLVersion <= 12
BEGIN
    PRINT 'For SQL 2014-: Consider T1117 (equal tempdb file growth)';
    PRINT 'For SQL 2014-: Consider T1118 (uniform extent allocation)';
END

-- T2371: Auto stats update threshold - relevant for all versions
-- This is now enabled by default in SQL 2016+, but helpful for earlier versions
PRINT 'Consider T2371 for automatic statistics update threshold';

-- T1800: Optimize for SSD - useful for modern storage
PRINT 'Consider T1800 for SSD/flash storage optimization';


-- TO DO: Integrate this into main Piranha script as a new section


/* ============================================================================
   SECTION 2: PROPOSED ADDITIONS - SPN VALIDATION
   ============================================================================

-- This would generate commands to verify SPN registration for Kerberos
-- Integration point: Add after Section 2 (Server Configuration)

PRINT '=== SPN VALIDATION COMMANDS ===';
PRINT 'To check SPN registration, execute:';
PRINT 'SETSPN -L <YourServiceAccount>';
PRINT '';
PRINT 'Expected SPNs for default instance:';
PRINT 'MSSQLSvc/<ServerName>.<Domain>:<Port>';
PRINT 'MSSQLSvc/<ServerName>:<Port>';
PRINT '';
PRINT 'For named instance:';
PRINT 'MSSQLSvc/<ServerName>.<Domain>:<InstanceName>';
PRINT 'MSSQLSvc/<ServerName>:<InstanceName>';


-- TO DO: Add full SPN validation and generation script


/* ============================================================================
   SECTION 3: PROPOSED ADDITIONS - ADDITIONAL XEVENT SESSIONS
   ============================================================================

-- Failed Login Tracking (more detailed than 18456 alert)
-- This XEvent session captures all failed login attempts with detailed info

/*
-- Uncomment to create failed login XEvent session
IF NOT EXISTS (SELECT * FROM sys.server_event_sessions WHERE name = N'FailedLogins')
BEGIN
    CREATE EVENT SESSION [FailedLogins] ON SERVER
    ADD EVENT sqlserver.errorlog_reported
    (WHERE ([error_number]=(18456)))
    ADD TARGET package0.event_file
    (SET filename = N'FailedLogins', max_file_size = (100))
    WITH (MAX_MEMORY = 4096 KB, EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS,
        MAX_DISPATCH_LATENCY = 30 SECONDS, MAX_EVENT_SIZE = 0 KB,
        MEMORY_PARTITION_MODE = NONE, TRACK_CAUSALITY = OFF, STARTUP_STATE = ON);
    
    PRINT 'Created FailedLogins XEvent session';
END
*/


-- TO DO: Integrate into main Piranha script as optional section


/* ============================================================================
   SECTION 4: PROPOSED ADDITIONS - DATABASE SETTINGS AUDIT
   ============================================================================

-- Verify critical database settings across all user databases

PRINT '=== DATABASE SETTINGS AUDIT ===';

SELECT 
    name AS DatabaseName,
    compatibility_level AS CompatibilityLevel,
    state_desc AS State,
    is_read_only AS IsReadOnly,
    is_auto_close_on AS IsAutoCloseOn,
    is_auto_shrink_on AS IsAutoShrinkOn,
    page_verify_option_desc AS PageVerify,
    recovery_model_desc AS RecoveryModel,
    is_encrypted AS IsEncrypted
FROM sys.databases
WHERE database_id > 4
ORDER BY name;


-- TO DO: Add automated remediation for auto-shrink, page verify, etc.


/* ============================================================================
   SECTION 5: PROPOSED ADDITIONS - SECURITY HARDENING CHECKLIST
   ============================================================================

PRINT '=== SECURITY HARDENING CHECKLIST ===';

-- Check for unnecessary protocols
SELECT 
    [name] AS ProtocolName,
    [state_desc] AS State,
    [protocol_type] AS ProtocolType
FROM sys.dm_server_services
WHERE [servicename] LIKE '%SQL Server%';

-- Check for SQL-authenticated logins (security consideration)
SELECT 
    name AS LoginName,
    type_desc AS LoginType,
    is_disabled AS IsDisabled,
    create_date AS CreateDate
FROM sys.server_principals
WHERE type IN ('S', 'U')  -- SQL and Windows logins
    AND name NOT IN ('##MS_PolicyEventProcessingLogin##', '##MS_PolicyTsqlExecutionLogin##')
ORDER BY create_date;


-- TO DO: Add automated security hardening recommendations


/* ============================================================================
   SECTION 6: PROPOSED ADDITIONS - WAIT STATISTICS BASELINE
   ============================================================================

-- Capture wait statistics baseline for performance analysis

IF OBJECT_ID('tempdb..#WaitStats') IS NOT NULL
    DROP TABLE #WaitStats;

SELECT 
    wait_type,
    waiting_tasks_count,
    wait_time_ms,
    max_wait_time_ms,
    signal_wait_time_ms
INTO #WaitStats
FROM sys.dm_os_wait_stats
WHERE wait_type NOT IN (
    -- Common idle waits to exclude
    'CHECKPOINT_QUEUE_WAIT',
    'FT_IFTS_SCHEDULER_IDLE_WAIT',
    'FT_IFTSHC_MUTEX',
    'LOGMGR_QUEUE',
    'REQUEST_FOR_DEADLOCK_SEARCH',
    'XE_DISPATCHER_WAIT',
    'XE_TIMER_EVENT',
    'BROKER_TASK_STOP',
    'BROKER_TO_FLUSH',
    'BROKER_TRANSMITTER',
    'REPLICA_WRITER',
    'SQLTRACE_INCREMENTAL_FLUSH_SLEEP',
    'DIRTY_PAGE_POLL',
    'HADR_FILESTREAM_IOMGR',
    'SP_SERVER_DIAGNOSTICS_SLEEP',
    'QDS_PERSIST_TASK_MAIN_LOOP_SLEEP'
)
AND wait_time_ms > 0;

-- Top waits by wait_time_ms
SELECT TOP(20)
    wait_type,
    waiting_tasks_count,
    wait_time_ms,
    (wait_time_ms * 1.0 / 1000) AS WaitTimeSeconds,
    (wait_time_ms * 1.0 / 1000 / 60) AS WaitTimeMinutes,
    max_wait_time_ms,
    signal_wait_time_ms,
    (signal_wait_time_ms * 1.0 / wait_time_ms * 100) AS SignalPct
FROM #WaitStats
ORDER BY wait_time_ms DESC;

DROP TABLE #WaitStats;


-- TO DO: Add to baseline collection and tracking


/* ============================================================================
   SECTION 7: PROPOSED ADDITIONS - PERFORMANCE COUNTER VALIDATION
   ============================================================================

-- Capture key performance metrics

PRINT '=== PERFORMANCE COUNTER SNAPSHOT ===';

SELECT 
    object_name,
    counter_name,
    cntr_value,
    cntr_type
FROM sys.dm_os_performance_counters
WHERE counter_name IN 
(
    'Batch Requests/sec',
    'SQL Compilations/sec',
    'SQL Recompilations/sec',
    'Page life expectancy',
    'Buffer cache hit ratio',
    'Buffer cache hit ratio base',
    'Total Server Memory (KB)',
    'Target Server Memory (KB)',
    'Lazy writes/sec',
    'Page splits/sec',
    'Lock Waits/sec',
    'Number of Deadlocks/sec'
)
ORDER BY object_name, counter_name;


-- TO DO: Add to ongoing monitoring and alerting


/* ============================================================================
   SECTION 8: TASK LIST - RESUMABLE ITEMS
   ============================================================================

/*
TASK LIST FOR ENHANCING THE PIRANHA SCRIPT:
============================================

[ ] PRIORITY 1: Trace Flags Section
    - Add startup trace flag recommendations based on SQL version
    - Include script to check current trace flags
    - Document each trace flag purpose
    
[ ] PRIORITY 2: SPN Validation  
    - Add SPN check/validation section
    - Generate SETSPN commands for remediation
    
[ ] PRIORITY 3: Database Settings Audit
    - Add automated check for:
      * Auto-shrink should be OFF
      * Page verify should be CHECKSUM
      * Compatibility level recommendations
    
[ ] PRIORITY 4: Additional XEvent Sessions
    - Add FailedLogins XEvent session
    - Add security audit XEvent session
    
[ ] PRIORITY 5: Security Hardening
    - Add login audit report
    - Add endpoint validation
    
[ ] PRIORITY 6: Performance Baselines
    - Add wait statistics capture
    - Add performance counter snapshot
    
[ ] PRIORITY 7: Integration with Existing Scripts
    - Consider including doSPNs.sql functionality
    - Consider including index maintenance checks
    - Consider including stats maintenance verification

*/


/* ============================================================================
   SECTION 9: SAMPLE INTEGRATION - ADD TO MAIN SCRIPT
   ============================================================================

-- SAMPLE: How to integrate wait stats baseline into main Piranha script
-- Add after Section 2 (Performance Best Practices)

/*
-- Wait Statistics Baseline
PRINT 'Collecting wait statistics baseline...';

IF OBJECT_ID('master.dbo.DBA_WaitStats_Baseline') IS NULL
BEGIN
    SELECT GETDATE() AS CollectionTime, *
    INTO master.dbo.DBA_WaitStats_Baseline
    FROM sys.dm_os_wait_stats
    WHERE wait_time_ms > 0;
    
    PRINT 'Created DBA_WaitStats_Baseline table';
END
ELSE
BEGIN
    INSERT INTO master.dbo.DBA_WaitStats_Baseline
    SELECT GETDATE() AS CollectionTime, *
    FROM sys.dm_os_wait_stats
    WHERE wait_time_ms > 0;
    
    PRINT 'Updated DBA_WaitStats_Baseline table';
END
*/


/* ============================================================================
   END OF ENHANCEMENT WORKING FILE
   ============================================================================

NOTES:
- This file is for analysis and planning purposes
- Each section can be integrated into the main Piranha script incrementally
- Consider adding configuration flags to enable/disable each enhancement
- Test thoroughly before production deployment

LAST UPDATED: 2026-03-12
AUTHOR: Analysis based on BPScripts folder best practices

*/

GO
