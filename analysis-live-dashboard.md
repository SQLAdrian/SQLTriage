# Live Dashboard Analysis Results

## Dashboard Description
"Real-time monitoring of SQL Server performance metrics including CPU, memory, transactions, and active sessions. Provides instant visibility into current server health and workload."

## Panel Descriptions

### live.cpu
**Current**: null
**New**: "Current CPU utilization percentage from ring buffer. Shows ProcessUtilization from sys.dm_os_ring_buffers RING_BUFFER_SCHEDULER_MONITOR."

### live.batch_req
**Current**: null  
**New**: "Batch requests per second. Measures SQL Server workload intensity from sys.dm_os_performance_counters."

### live.transactions
**Current**: null
**New**: "Active transactions per second. Tracks transaction throughput from sys.dm_os_performance_counters."

### live.compilations
**Current**: null
**New**: "SQL compilations and recompilations per second. High values may indicate plan cache issues or missing parameterization."

### live.page_reads
**Current**: null
**New**: "Page reads per second from disk. High values indicate memory pressure or missing indexes forcing disk I/O."

### live.page_writes  
**Current**: null
**New**: "Page writes per second to disk. Tracks checkpoint and lazy writer activity."

### live.poison_waits
**Current**: null
**New**: "Cumulative poison wait types (CXPACKET, PAGEIOLATCH, etc.) that indicate performance bottlenecks. Sourced from sys.dm_os_wait_stats."

### live.serializable_locking
**Current**: null
**New**: "Lock requests per second at SERIALIZABLE isolation level. High values may indicate blocking or deadlock risks."

### live.cmemthread
**Current**: null
**New**: "CMEMTHREAD wait time indicating memory grant queue waits. Suggests queries waiting for memory to execute."

### live.sessions
**Current**: null
**New**: "Count of active user sessions from sys.dm_exec_sessions where is_user_process = 1."

### live.blocking
**Current**: null
**New**: "Number of blocked sessions from sys.dm_exec_requests where blocking_session_id > 0."

### sessions.top
**Current**: null
**New**: "Top active sessions by CPU, reads, or duration. Shows session_id, login, database, and current query text."

## Implementation Notes
- All panels use master database context
- Queries pull from DMVs (sys.dm_os_*, sys.dm_exec_*)
- Most are StatCard type showing single metric values
- Poison waits and sessions.top are DataGrid type showing detailed rows
