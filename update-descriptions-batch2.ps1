# Dashboard Description Update Script - Batch 2

$configPath = "c:\GitHub\LiveMonitor\Config\dashboard-config.json"
$config = Get-Content $configPath -Raw | ConvertFrom-Json

# Long Queries Dashboard
$longDash = $config.dashboards | Where-Object {$_.id -eq "longqueries"}
$longDash.description = "SQLWATCH-based tracking of long-running queries with historical trends. Shows query text, execution plans, and performance metrics for queries exceeding duration thresholds."

$longDash.panels | Where-Object {$_.id -eq "longqueries.querydetails"} | ForEach-Object {$_.description = "Detailed metrics for long-running queries including duration, CPU, reads, writes, and execution count from SQLWATCH query snapshots."}
$longDash.panels | Where-Object {$_.id -eq "longqueries.querytext"} | ForEach-Object {$_.description = "Full query text and execution plan XML for selected long-running queries. Enables deep-dive analysis of problematic queries."}

# Wait Events Dashboard
$waitDash = $config.dashboards | Where-Object {$_.id -eq "waitevents"}
$waitDash.description = "Historical wait statistics analysis from SQLWATCH showing wait type trends, categories, and bottleneck identification over time."

$waitDash.panels | Where-Object {$_.id -eq "waits.timeseries_category"} | ForEach-Object {$_.description = "Wait time trends by category (CPU, I/O, Memory, Lock, Network) over time. Identifies shifting performance bottlenecks."}
$waitDash.panels | Where-Object {$_.id -eq "waits.details"} | ForEach-Object {$_.description = "Detailed wait statistics by wait type showing wait time, wait count, and percentage of total waits from SQLWATCH snapshots."}

# Repository Dashboard
$repoDash = $config.dashboards | Where-Object {$_.id -eq "repository"}
$repoDash.description = "Multi-instance monitoring dashboard showing aggregated health metrics across all SQLWATCH-monitored SQL Server instances in the repository."

$repoDash.panels | Where-Object {$_.id -eq "repo.instance_count"} | ForEach-Object {$_.description = "Total count of SQL Server instances being monitored by SQLWATCH."}
$repoDash.panels | Where-Object {$_.id -eq "repo.process_memory"} | ForEach-Object {$_.description = "SQL Server process memory usage across all monitored instances."}
$repoDash.panels | Where-Object {$_.id -eq "repo.schedulers"} | ForEach-Object {$_.description = "Scheduler activity and runnable task counts aggregated across instances."}
$repoDash.panels | Where-Object {$_.id -eq "repo.checks"} | ForEach-Object {$_.description = "SQLWATCH health check status across all instances showing pass/fail/warning counts."}
$repoDash.panels | Where-Object {$_.id -eq "repo.cpu"} | ForEach-Object {$_.description = "CPU utilization trends across all monitored instances."}
$repoDash.panels | Where-Object {$_.id -eq "repo.perf_counters"} | ForEach-Object {$_.description = "Key performance counter metrics aggregated from all instances."}
$repoDash.panels | Where-Object {$_.id -eq "repo.wait_stats"} | ForEach-Object {$_.description = "Wait statistics summary across all monitored instances."}
$repoDash.panels | Where-Object {$_.id -eq "repo.file_stats"} | ForEach-Object {$_.description = "Database file I/O statistics aggregated across instances."}
$repoDash.panels | Where-Object {$_.id -eq "repo.long_queries"} | ForEach-Object {$_.description = "Long-running queries detected across all monitored instances."}

# Performance Monitor Dashboard
$pmDash = $config.dashboards | Where-Object {$_.id -eq "pm"}
$pmDash.description = "Darling Performance Monitor dashboard showing advanced diagnostics including latches, spinlocks, memory grants, plan cache, and tempdb usage patterns."

$pmDash.panels | Where-Object {$_.id -eq "pm.latches"} | ForEach-Object {$_.description = "Latch wait statistics showing contention on internal SQL Server data structures. High latch waits indicate internal bottlenecks."}
$pmDash.panels | Where-Object {$_.id -eq "pm.spinlocks"} | ForEach-Object {$_.description = "Spinlock statistics showing lightweight synchronization contention. Useful for diagnosing high CPU with low query activity."}
$pmDash.panels | Where-Object {$_.id -eq "pm.memory_grants"} | ForEach-Object {$_.description = "Memory grant requests and waits. Shows queries waiting for memory to execute and grant sizes."}
$pmDash.panels | Where-Object {$_.id -eq "pm.plan_cache"} | ForEach-Object {$_.description = "Plan cache statistics including cache size, hit ratio, and plan reuse metrics."}
$pmDash.panels | Where-Object {$_.id -eq "pm.tempdb_usage"} | ForEach-Object {$_.description = "TempDB usage by session and object type. Identifies queries causing tempdb pressure."}
$pmDash.panels | Where-Object {$_.id -eq "pm.config_changes"} | ForEach-Object {$_.description = "SQL Server configuration changes tracked over time from default trace."}

# Memory Dashboard (Performance Monitor)
$pmemDash = $config.dashboards | Where-Object {$_.id -eq "pmemory"}
$pmemDash.description = "Detailed memory analysis from Darling Performance Monitor showing buffer pool usage, memory clerks, and memory pressure indicators."

$pmemDash.panels | Where-Object {$_.id -eq "pmemory.total"} | ForEach-Object {$_.description = "Total SQL Server memory usage including committed and target memory from sys.dm_os_sys_info."}
$pmemDash.panels | Where-Object {$_.id -eq "pmemory.buffer_pool"} | ForEach-Object {$_.description = "Buffer pool memory allocation and page life expectancy trends."}
$pmemDash.panels | Where-Object {$_.id -eq "pmemory.clerks"} | ForEach-Object {$_.description = "Memory distribution by clerk type showing how SQL Server allocates memory across components."}

Write-Host "Updating config file (Batch 2)..."
$config | ConvertTo-Json -Depth 100 | Set-Content $configPath -Encoding UTF8
Write-Host "Batch 2 updated successfully!"
