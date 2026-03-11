# Dashboard Description Update Script
# Updates dashboard-config.json with analyzed descriptions

$configPath = "c:\GitHub\LiveMonitor\Config\dashboard-config.json"
$config = Get-Content $configPath -Raw | ConvertFrom-Json

# Live Dashboard
$liveDash = $config.dashboards | Where-Object {$_.id -eq "live"}
$liveDash.description = "Real-time monitoring of SQL Server performance metrics including CPU, memory, transactions, and active sessions. Provides instant visibility into current server health and workload."

$liveDash.panels | Where-Object {$_.id -eq "live.cpu"} | ForEach-Object {$_.description = "Current CPU utilization percentage from ring buffer. Shows ProcessUtilization from sys.dm_os_ring_buffers RING_BUFFER_SCHEDULER_MONITOR."}
$liveDash.panels | Where-Object {$_.id -eq "live.batch_req"} | ForEach-Object {$_.description = "Batch requests per second. Measures SQL Server workload intensity from sys.dm_os_performance_counters."}
$liveDash.panels | Where-Object {$_.id -eq "live.transactions"} | ForEach-Object {$_.description = "Active transactions per second. Tracks transaction throughput from sys.dm_os_performance_counters."}
$liveDash.panels | Where-Object {$_.id -eq "live.compilations"} | ForEach-Object {$_.description = "SQL compilations and recompilations per second. High values may indicate plan cache issues or missing parameterization."}
$liveDash.panels | Where-Object {$_.id -eq "live.page_reads"} | ForEach-Object {$_.description = "Page reads per second from disk. High values indicate memory pressure or missing indexes forcing disk I/O."}
$liveDash.panels | Where-Object {$_.id -eq "live.page_writes"} | ForEach-Object {$_.description = "Page writes per second to disk. Tracks checkpoint and lazy writer activity."}
$liveDash.panels | Where-Object {$_.id -eq "live.poison_waits"} | ForEach-Object {$_.description = "Cumulative poison wait types (CXPACKET, PAGEIOLATCH, etc.) that indicate performance bottlenecks. Sourced from sys.dm_os_wait_stats."}
$liveDash.panels | Where-Object {$_.id -eq "live.serializable_locking"} | ForEach-Object {$_.description = "Lock requests per second at SERIALIZABLE isolation level. High values may indicate blocking or deadlock risks."}
$liveDash.panels | Where-Object {$_.id -eq "live.cmemthread"} | ForEach-Object {$_.description = "CMEMTHREAD wait time indicating memory grant queue waits. Suggests queries waiting for memory to execute."}
$liveDash.panels | Where-Object {$_.id -eq "live.sessions"} | ForEach-Object {$_.description = "Count of active user sessions from sys.dm_exec_sessions where is_user_process = 1."}
$liveDash.panels | Where-Object {$_.id -eq "live.blocking"} | ForEach-Object {$_.description = "Number of blocked sessions from sys.dm_exec_requests where blocking_session_id > 0."}
$liveDash.panels | Where-Object {$_.id -eq "sessions.top"} | ForEach-Object {$_.description = "Top active sessions by CPU, reads, or duration. Shows session_id, login, database, and current query text."}

# Instance Dashboard (SQLWATCH)
$instanceDash = $config.dashboards | Where-Object {$_.id -eq "instance"}
$instanceDash.description = "Comprehensive SQLWATCH-based instance monitoring showing historical trends for CPU, memory, disk I/O, wait statistics, and database health metrics over time."

$instanceDash.panels | Where-Object {$_.id -eq "instance.ks1"} | ForEach-Object {$_.description = "Key performance indicator showing overall instance health status from SQLWATCH checks."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.ks2"} | ForEach-Object {$_.description = "Secondary health indicator tracking critical performance thresholds."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.blocking"} | ForEach-Object {$_.description = "Historical blocking session count over time from SQLWATCH performance snapshots."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.disk_space"} | ForEach-Object {$_.description = "Database file space usage and growth trends. Monitors disk capacity and alerts on low space conditions."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.cpu"} | ForEach-Object {$_.description = "Historical CPU utilization percentage over time from SQLWATCH performance counters."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.activity"} | ForEach-Object {$_.description = "Batch requests and SQL compilations per second showing workload intensity trends."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.waits_category"} | ForEach-Object {$_.description = "Wait statistics grouped by category (CPU, I/O, Memory, Lock) showing performance bottleneck areas."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.memory"} | ForEach-Object {$_.description = "Buffer pool memory usage and page life expectancy trends from SQLWATCH snapshots."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.sessions"} | ForEach-Object {$_.description = "Active session count over time tracking concurrent user connections."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.requests"} | ForEach-Object {$_.description = "Active request count showing queries currently executing on the instance."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.file_stats_latency"} | ForEach-Object {$_.description = "Database file I/O latency in milliseconds. High values indicate disk performance issues."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.file_stats_throughput"} | ForEach-Object {$_.description = "Database file I/O throughput in MB/s showing read and write activity."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.schedulers"} | ForEach-Object {$_.description = "SQL Server scheduler activity and runnable task queue length. High queue indicates CPU pressure."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.lock_requests"} | ForEach-Object {$_.description = "Lock requests per second by lock type showing locking patterns and potential contention."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.page_life"} | ForEach-Object {$_.description = "Page Life Expectancy in seconds. Low values indicate memory pressure forcing frequent page evictions."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.memory_clerks"} | ForEach-Object {$_.description = "Memory allocation by clerk type showing how SQL Server distributes memory across components."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.database_stats"} | ForEach-Object {$_.description = "Per-database statistics including size, log usage, and transaction activity."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.tempdb"} | ForEach-Object {$_.description = "TempDB usage statistics including version store, user objects, and internal objects."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.checks_history"} | ForEach-Object {$_.description = "Historical SQLWATCH check results showing pass/fail trends over time."}
$instanceDash.panels | Where-Object {$_.id -eq "instance.checks_detail"} | ForEach-Object {$_.description = "Detailed SQLWATCH check results with failure reasons and recommended actions."}

# Sessions Dashboard
$sessionsDash = $config.dashboards | Where-Object {$_.id -eq "sessions"}
$sessionsDash.description = "Interactive bubble chart visualization of active SQL Server sessions. Bubble size represents activity score, position shows CPU vs I/O usage, with blocking chain analysis."

$sessionsDash.panels | Where-Object {$_.id -eq "sessions.bubble"} | ForEach-Object {$_.description = "Interactive session bubble view showing active sessions positioned by CPU time (Y-axis) and logical reads/writes (X-axis). Bubble size indicates activity score. Click bubbles for session details."}

# Query Store Dashboard
$qsDash = $config.dashboards | Where-Object {$_.id -eq "querystore"}
$qsDash.description = "Query Store analytics showing top queries by CPU, duration, and execution count. Identifies plan regressions and query performance variations across plan changes."

$qsDash.panels | Where-Object {$_.id -eq "qs.topcpu"} | ForEach-Object {$_.description = "Top queries by total CPU time from Query Store. Shows query_id, execution count, avg CPU, and query text."}
$qsDash.panels | Where-Object {$_.id -eq "qs.topduration"} | ForEach-Object {$_.description = "Top queries by total duration from Query Store. Identifies long-running queries impacting user experience."}
$qsDash.panels | Where-Object {$_.id -eq "qs.planvariation"} | ForEach-Object {$_.description = "Queries with multiple execution plans showing plan_id variations. Helps identify parameter sniffing or plan instability."}
$qsDash.panels | Where-Object {$_.id -eq "qs.regressed"} | ForEach-Object {$_.description = "Regressed queries where recent performance is worse than historical baseline. Compares recent vs overall avg duration."}

Write-Host "Updating config file..."
$config | ConvertTo-Json -Depth 100 | Set-Content $configPath -Encoding UTF8
Write-Host "Config updated successfully!"
