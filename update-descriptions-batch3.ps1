# Dashboard Description Update Script - Batch 3 (Final)

$configPath = "c:\GitHub\LiveMonitor\Config\dashboard-config.json"
$config = Get-Content $configPath -Raw | ConvertFrom-Json

# Performance Events Dashboard
$pevDash = $config.dashboards | Where-Object {$_.id -eq "pevents"}
$pevDash.description = "Extended events and trace analysis from Darling Performance Monitor showing default trace events, system health session data, severe errors, and configuration changes."

$pevDash.panels | Where-Object {$_.id -eq "pevents.default_trace"} | ForEach-Object {$_.description = "Events from default trace including auto-grow, auto-shrink, and configuration changes."}
$pevDash.panels | Where-Object {$_.id -eq "pevents.system_health"} | ForEach-Object {$_.description = "System health extended event session data showing deadlocks, memory issues, and scheduler problems."}
$pevDash.panels | Where-Object {$_.id -eq "pevents.severe_errors"} | ForEach-Object {$_.description = "Severe SQL Server errors (severity 17+) from system health session and error log."}
$pevDash.panels | Where-Object {$_.id -eq "pevents.config_changes"} | ForEach-Object {$_.description = "SQL Server configuration changes tracked from default trace and system health events."}

# Query Performance Dashboard
$pqDash = $config.dashboards | Where-Object {$_.id -eq "pquery"}
$pqDash.description = "Query performance analysis from Darling Performance Monitor showing expensive queries, execution statistics, blocking events, and execution trends over time."

$pqDash.panels | Where-Object {$_.id -eq "pquery.expensive_queries"} | ForEach-Object {$_.description = "Most expensive queries by CPU, duration, reads, or writes from plan cache and extended events."}
$pqDash.panels | Where-Object {$_.id -eq "pquery.query_stats"} | ForEach-Object {$_.description = "Detailed query execution statistics including avg/min/max duration, CPU, and I/O metrics."}
$pqDash.panels | Where-Object {$_.id -eq "pquery.blocking_events"} | ForEach-Object {$_.description = "Blocking events captured from extended events showing blocker/blocked session pairs and wait times."}
$pqDash.panels | Where-Object {$_.id -eq "pquery.execution_trends"} | ForEach-Object {$_.description = "Query execution trends over time showing execution count and performance metric changes."}

# Memory Analysis Dashboard
$pmemAnalysisDash = $config.dashboards | Where-Object {$_.id -eq "pmemory_analysis"}
$pmemAnalysisDash.description = "Deep-dive memory analysis from Darling Performance Monitor with detailed memory clerk breakdowns, grant analysis, and plan cache memory usage."

$pmemAnalysisDash.panels | Where-Object {$_.id -eq "pmemory.memory_stats"} | ForEach-Object {$_.description = "Comprehensive memory statistics including total, available, and committed memory from sys.dm_os_sys_memory."}
$pmemAnalysisDash.panels | Where-Object {$_.id -eq "pmemory.memory_clerks"} | ForEach-Object {$_.description = "Detailed memory clerk allocation showing top memory consumers by clerk type."}
$pmemAnalysisDash.panels | Where-Object {$_.id -eq "pmemory.memory_grants"} | ForEach-Object {$_.description = "Active and pending memory grants with grant sizes, wait times, and requesting queries."}
$pmemAnalysisDash.panels | Where-Object {$_.id -eq "pmemory.plan_cache"} | ForEach-Object {$_.description = "Plan cache memory usage by cache type showing size, entry count, and memory consumption."}

Write-Host "Updating config file (Batch 3 - Final)..."
$config | ConvertTo-Json -Depth 100 | Set-Content $configPath -Encoding UTF8
Write-Host "All dashboard descriptions updated successfully!"
Write-Host ""
Write-Host "Summary:"
Write-Host "- 12 dashboards analyzed and updated"
Write-Host "- 70+ panel descriptions added"
Write-Host "- Config file: c:\GitHub\LiveMonitor\Config\dashboard-config.json"
