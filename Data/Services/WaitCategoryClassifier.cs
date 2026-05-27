/* In the name of God, the Merciful, the Compassionate */
/*
 * WaitCategoryClassifier — stateless mapping of wait_type → WaitCategory.
 * Single source of truth for wait classification; replaces the duplicated
 * CASE WHEN ... blocks scattered through dashboard-config.json's livewaits
 * panels. v1 of the WAIT_STATS_DESIGN.md P1 ship.
 *
 * No DI, no DB, no I/O. Pure function plus two readonly sets.
 */

using System;
using System.Collections.Generic;

namespace SQLTriage.Data.Services;

public enum WaitCategory
{
    Cpu,
    Io,
    Memory,
    Lock,
    Latch,
    Buffer,
    Network,
    Replication,
    Tempdb,
    Other,
    IdleBenign,
}

public static class WaitCategoryClassifier
{
    /// <summary>
    /// Idle / benign wait types that should be excluded from "top waits" lists.
    /// Sourced from Paul Randal's filtered wait stats query + Microsoft guidance.
    /// Lift this into a SQL fragment (see design doc §11 P6) when the user
    /// agrees to refactor the inline CASE blocks in dashboard-config.json.
    /// </summary>
    public static readonly IReadOnlySet<string> BenignWaitTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "BROKER_EVENTHANDLER", "BROKER_RECEIVE_WAITFOR", "BROKER_TASK_STOP",
        "BROKER_TO_FLUSH", "BROKER_TRANSMITTER", "CHECKPOINT_QUEUE",
        "CHKPT", "CLR_AUTO_EVENT", "CLR_MANUAL_EVENT", "CLR_SEMAPHORE",
        "DBMIRROR_DBM_EVENT", "DBMIRROR_EVENTS_QUEUE", "DBMIRROR_WORKER_QUEUE",
        "DBMIRRORING_CMD", "DIRTY_PAGE_POLL", "DISPATCHER_QUEUE_SEMAPHORE",
        "EXECSYNC", "FSAGENT", "FT_IFTS_SCHEDULER_IDLE_WAIT", "FT_IFTSHC_MUTEX",
        "HADR_CLUSAPI_CALL", "HADR_FILESTREAM_IOMGR_IOCOMPLETION",
        "HADR_LOGCAPTURE_WAIT", "HADR_NOTIFICATION_DEQUEUE", "HADR_TIMER_TASK",
        "HADR_WORK_QUEUE", "KSOURCE_WAKEUP", "LAZYWRITER_SLEEP",
        "LOGMGR_QUEUE", "MEMORY_ALLOCATION_EXT", "ONDEMAND_TASK_QUEUE",
        "PARALLEL_REDO_DRAIN_WORKER", "PARALLEL_REDO_LOG_CAPTURE",
        "PARALLEL_REDO_TRAN_LIST", "PARALLEL_REDO_WORKER_SYNC",
        "PARALLEL_REDO_WORKER_WAIT_WORK", "PREEMPTIVE_OS_FLUSHFILEBUFFERS",
        "PREEMPTIVE_XE_GETTARGETSTATE", "PWAIT_ALL_COMPONENTS_INITIALIZED",
        "PWAIT_DIRECTLOGCONSUMER_GETNEXT", "QDS_PERSIST_TASK_MAIN_LOOP_SLEEP",
        "QDS_ASYNC_QUEUE", "QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP",
        "QDS_SHUTDOWN_QUEUE", "REDO_THREAD_PENDING_WORK", "REQUEST_FOR_DEADLOCK_SEARCH",
        "RESOURCE_QUEUE", "SERVER_IDLE_CHECK", "SLEEP_BPOOL_FLUSH",
        "SLEEP_DBSTARTUP", "SLEEP_DCOMSTARTUP", "SLEEP_MASTERDBREADY",
        "SLEEP_MASTERMDREADY", "SLEEP_MASTERUPGRADED", "SLEEP_MSDBSTARTUP",
        "SLEEP_SYSTEMTASK", "SLEEP_TASK", "SLEEP_TEMPDBSTARTUP",
        "SNI_HTTP_ACCEPT", "SOS_WORK_DISPATCHER", "SP_SERVER_DIAGNOSTICS_SLEEP",
        "SQLTRACE_BUFFER_FLUSH", "SQLTRACE_INCREMENTAL_FLUSH_SLEEP",
        "SQLTRACE_WAIT_ENTRIES", "WAIT_FOR_RESULTS", "WAITFOR",
        "WAITFOR_TASKSHUTDOWN", "WAIT_XTP_RECOVERY", "WAIT_XTP_HOST_WAIT",
        "WAIT_XTP_OFFLINE_CKPT_NEW_LOG", "WAIT_XTP_CKPT_CLOSE",
        "XE_DISPATCHER_JOIN", "XE_DISPATCHER_WAIT", "XE_TIMER_EVENT",
    };

    public static bool IsIdleBenign(string waitType) =>
        !string.IsNullOrEmpty(waitType) && BenignWaitTypes.Contains(waitType);

    public static WaitCategory Classify(string waitType)
    {
        if (string.IsNullOrEmpty(waitType)) return WaitCategory.Other;
        if (BenignWaitTypes.Contains(waitType)) return WaitCategory.IdleBenign;

        // Prefix matches first (most common), then exact matches.
        if (waitType.StartsWith("PAGEIOLATCH_", StringComparison.Ordinal)) return WaitCategory.Io;
        if (waitType.StartsWith("PAGELATCH_", StringComparison.Ordinal)) return WaitCategory.Buffer;
        if (waitType.StartsWith("LATCH_", StringComparison.Ordinal)) return WaitCategory.Latch;
        if (waitType.StartsWith("LCK_", StringComparison.Ordinal)) return WaitCategory.Lock;
        if (waitType.StartsWith("WRITELOG", StringComparison.Ordinal)) return WaitCategory.Io;
        if (waitType.StartsWith("BACKUP", StringComparison.Ordinal)) return WaitCategory.Io;
        if (waitType.StartsWith("HADR_", StringComparison.Ordinal)) return WaitCategory.Replication;
        if (waitType.StartsWith("PREEMPTIVE_HADR_", StringComparison.Ordinal)) return WaitCategory.Replication;
        if (waitType.StartsWith("DBMIRROR_", StringComparison.Ordinal)) return WaitCategory.Replication;

        return waitType switch
        {
            "SOS_SCHEDULER_YIELD" or "CXPACKET" or "CXCONSUMER" or "THREADPOOL"
                or "WORKER_BOUND" or "EXECSYNC" => WaitCategory.Cpu,
            "ASYNC_IO_COMPLETION" or "IO_COMPLETION" or "IO_QUEUE_LIMIT" => WaitCategory.Io,
            "RESOURCE_SEMAPHORE" or "RESOURCE_SEMAPHORE_QUERY_COMPILE"
                or "CMEMPARTITIONED" or "EE_PMOLIST"
                or "MEMORYBROKER_FOR_CACHE" or "CMEMTHREAD" => WaitCategory.Memory,
            "ASYNC_NETWORK_IO" or "NET_WAITFOR_PACKET"
                or "EXTERNAL_SCRIPT_NETWORK_IOF" => WaitCategory.Network,
            _ => WaitCategory.Other,
        };
    }
}
