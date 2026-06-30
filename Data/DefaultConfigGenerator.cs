/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using SQLTriage.Data.Models;

namespace SQLTriage.Data
{
    /// <summary>
    /// Generates the default dashboard configuration that matches the current hardcoded behavior exactly.
    /// This is used to produce the initial dashboard-config.json content.
    /// </summary>
    public static class DefaultConfigGenerator
    {
        public static DashboardConfigRoot Generate()
        {
            return new DashboardConfigRoot
            {
                Version = 1,
                Dashboards = new List<DashboardDefinition>
                {
                    BuildRepositoryDashboard(),
                    BuildInstanceDashboard(),
                    BuildBpCheckDashboard()
                },
                SupportQueries = BuildSupportQueries()
            };
        }

        // ================================================================
        // Helper methods for panel creation
        // ================================================================

        private static PanelDefinition TimeSeriesPanel(string id, string title, int column, int order, string sqlServer, string chartType = "Line", bool span = false)
        {
            return new PanelDefinition
            {
                Id = id,
                Title = title,
                PanelType = "TimeSeries",
                ChartType = chartType,
                Layout = new PanelLayout { Column = column, Order = order, SpanColumns = span },
                Query = new QueryPair { SqlServer = sqlServer }
            };
        }

        private static PanelDefinition StatCardPanel(string id, string title, string unit, int order, string sqlServer, string? statThresholdKey = null, List<ColorThresholdRule>? colorThresholds = null)
        {
            return new PanelDefinition
            {
                Id = id,
                Title = title,
                PanelType = "StatCard",
                StatUnit = unit,
                StatThresholdKey = statThresholdKey,
                Layout = new PanelLayout { Column = 0, Order = order },
                Query = new QueryPair { SqlServer = sqlServer },
                ColorThresholds = colorThresholds ?? new List<ColorThresholdRule>()
            };
        }

        private static PanelDefinition CheckStatusPanel(string id, string title, int order, string sqlServer)
        {
            return new PanelDefinition
            {
                Id = id,
                Title = title,
                PanelType = "CheckStatus",
                Layout = new PanelLayout { Column = 0, Order = order },
                Query = new QueryPair { SqlServer = sqlServer }
            };
        }

        private static PanelDefinition BarGaugePanel(string id, string title, string thresholdKey, string unitSuffix, int column, int order, string sqlServer, bool span = false, List<ColorThresholdRule>? colorThresholds = null)
        {
            return new PanelDefinition
            {
                Id = id,
                Title = title,
                PanelType = "BarGauge",
                BarGaugeThresholdKey = thresholdKey,
                BarGaugeUnitSuffix = unitSuffix,
                Layout = new PanelLayout { Column = column, Order = order, SpanColumns = span },
                Query = new QueryPair { SqlServer = sqlServer },
                ColorThresholds = colorThresholds ?? new List<ColorThresholdRule>()
            };
        }

        private static PanelDefinition DataGridPanel(string id, string title, int order, string sqlServer, bool span = false)
        {
            return new PanelDefinition
            {
                Id = id,
                Title = title,
                PanelType = "DataGrid",
                Layout = new PanelLayout { Column = 1, Order = order, SpanColumns = span },
                Query = new QueryPair { SqlServer = sqlServer }
            };
        }

        // ================================================================
        // REPOSITORY DASHBOARD
        // ================================================================

        private static DashboardDefinition BuildRepositoryDashboard()
        {
            return new DashboardDefinition
            {
                Id = "repository",
                Title = "Repository",
                Route = "/",
                ShowAllOption = true,
                Panels = new List<PanelDefinition>
                {
                    // --- StatCard panels (column=0) ---

                    StatCardPanel(
                        id: "repo.instance_count",
                        title: "Instances",
                        unit: "",
                        order: 1,
                        sqlServer: @"
SELECT COUNT(DISTINCT sql_instance) AS [Value]
FROM dbo.sqlwatch_config_sql_instance WITH (NOLOCK)
WHERE repo_collector_is_active = 1"
                    ),

                    StatCardPanel(
                        id: "repo.process_memory",
                        title: "SQL Memory",
                        unit: "%",
                        order: 2,
                        statThresholdKey: "SQL Memory",
                        colorThresholds: new List<ColorThresholdRule>
                        {
                            new() { Operator = ">=", Value = 0,  Color = "#1b5e20", Label = "Normal" },
                            new() { Operator = ">=", Value = 85, Color = "#ff9800", Label = "Warning" },
                            new() { Operator = ">=", Value = 95, Color = "#f44336", Label = "Critical" }
                        },
                        sqlServer: @"
SELECT TOP 1
    h.report_time AS [Time],
    'Memory %' AS [Series],
    d.memory_utilization_percentage AS [Value]
FROM dbo.sqlwatch_logger_perf_os_process_memory d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
ORDER BY h.report_time DESC"
                    ),

                    StatCardPanel(
                        id: "repo.schedulers",
                        title: "Pending Queue",
                        unit: "",
                        order: 3,
                        statThresholdKey: "Pending",
                        colorThresholds: new List<ColorThresholdRule>
                        {
                            new() { Operator = ">=", Value = 0, Color = "#1b5e20", Label = "Normal" },
                            new() { Operator = ">=", Value = 1, Color = "#f44336", Label = "Critical" }
                        },
                        sqlServer: @"
SELECT TOP 1
    h.report_time AS [Time],
    'Pending' AS [Series],
    d.work_queue_count AS [Value]
FROM dbo.sqlwatch_logger_perf_os_schedulers d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
ORDER BY h.report_time DESC"
                    ),

                    // --- CheckStatus panel (column=0) ---

                    CheckStatusPanel(
                        id: "repo.checks",
                        title: "Health Checks",
                        order: 4,
                        sqlServer: @"
SELECT
    d.check_status AS [Status],
    COUNT(*) AS [Count]
FROM dbo.sqlwatch_logger_check d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
GROUP BY d.check_status"
                    ),

                    // --- TimeSeries charts (columns 1 and 2, alternating) ---

                    TimeSeriesPanel(
                        id: "repo.cpu",
                        title: "CPU Utilization",
                        column: 1,
                        order: 1,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    m.counter_name AS [Series],
    d.cntr_value_calculated AS [Value]
FROM dbo.sqlwatch_logger_perf_os_performance_counters d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
INNER JOIN dbo.sqlwatch_meta_performance_counter m WITH (NOLOCK)
    ON m.sql_instance = d.sql_instance
    AND m.performance_counter_id = d.performance_counter_id
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND m.counter_name IN ('Processor Time %','Privileged Time %','User Time %')
    AND m.cntr_type <> 1073939712
ORDER BY h.report_time"
                    ),

                    TimeSeriesPanel(
                        id: "repo.perf_counters",
                        title: "Performance Counters",
                        column: 2,
                        order: 2,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    m.counter_name AS [Series],
    d.cntr_value_calculated AS [Value]
FROM dbo.sqlwatch_logger_perf_os_performance_counters d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
INNER JOIN dbo.sqlwatch_meta_performance_counter m WITH (NOLOCK)
    ON m.sql_instance = d.sql_instance
    AND m.performance_counter_id = d.performance_counter_id
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND m.counter_name IN ('Batch Requests/Sec','Readahead pages/sec','Transactions/sec','Processes blocked')
    AND m.cntr_type <> 1073939712
ORDER BY h.report_time"
                    ),

                    TimeSeriesPanel(
                        id: "repo.wait_stats",
                        title: "Wait Stats",
                        column: 1,
                        order: 3,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    'Wait Time (ms)' AS [Series],
    SUM(d.wait_time_ms_delta) AS [Value]
FROM dbo.sqlwatch_logger_perf_os_wait_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
GROUP BY h.report_time
ORDER BY h.report_time"
                    ),

                    TimeSeriesPanel(
                        id: "repo.file_stats",
                        title: "File IO Throughput",
                        column: 2,
                        order: 4,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    'Bytes Read/s' AS [Series],
    SUM(CASE WHEN ISNULL(d.delta_seconds,0) > 0 THEN d.num_of_bytes_read_delta / d.delta_seconds ELSE 0 END) AS [Value]
FROM dbo.sqlwatch_logger_perf_file_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
GROUP BY h.report_time

UNION ALL

SELECT
    h.report_time AS [Time],
    'Bytes Written/s' AS [Series],
    SUM(CASE WHEN ISNULL(d.delta_seconds,0) > 0 THEN d.num_of_bytes_written_delta / d.delta_seconds ELSE 0 END) AS [Value]
FROM dbo.sqlwatch_logger_perf_file_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
GROUP BY h.report_time
ORDER BY [Time]"
                    ),

                    TimeSeriesPanel(
                        id: "repo.long_queries",
                        title: "Long Running Queries",
                        column: 1,
                        order: 5,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    'Long Queries' AS [Series],
    COUNT(*) AS [Value]
FROM dbo.sqlwatch_logger_xes_long_queries d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
GROUP BY h.report_time
ORDER BY h.report_time"
                    )
                }
            };
        }

        // ================================================================
        // INSTANCE DASHBOARD
        // ================================================================

        private static DashboardDefinition BuildInstanceDashboard()
        {
            return new DashboardDefinition
            {
                Id = "instance",
                Title = "Instance Overview",
                Route = "/instance",
                ShowAllOption = false,
                Panels = new List<PanelDefinition>
                {
                    // --- StatCard panels (column=0) ---

                    StatCardPanel(
                        id: "repo.instance_count",
                        title: "Instances",
                        unit: "",
                        order: 1,
                        sqlServer: @"
SELECT COUNT(DISTINCT sql_instance) AS [Value]
FROM dbo.sqlwatch_config_sql_instance WITH (NOLOCK)
WHERE repo_collector_is_active = 1"
                    ),

                    StatCardPanel(
                        id: "repo.process_memory",
                        title: "SQL Memory",
                        unit: "%",
                        order: 2,
                        statThresholdKey: "SQL Memory",
                        colorThresholds: new List<ColorThresholdRule>
                        {
                            new() { Operator = ">=", Value = 0,  Color = "#1b5e20", Label = "Normal" },
                            new() { Operator = ">=", Value = 85, Color = "#ff9800", Label = "Warning" },
                            new() { Operator = ">=", Value = 95, Color = "#f44336", Label = "Critical" }
                        },
                        sqlServer: @"
SELECT TOP 1
    h.report_time AS [Time],
    'Memory %' AS [Series],
    d.memory_utilization_percentage AS [Value]
FROM dbo.sqlwatch_logger_perf_os_process_memory d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
ORDER BY h.report_time DESC"
                    ),

                    StatCardPanel(
                        id: "repo.schedulers",
                        title: "Pending Queue",
                        unit: "",
                        order: 3,
                        statThresholdKey: "Pending",
                        colorThresholds: new List<ColorThresholdRule>
                        {
                            new() { Operator = ">=", Value = 0, Color = "#1b5e20", Label = "Normal" },
                            new() { Operator = ">=", Value = 1, Color = "#f44336", Label = "Critical" }
                        },
                        sqlServer: @"
SELECT TOP 1
    h.report_time AS [Time],
    'Pending' AS [Series],
    d.work_queue_count AS [Value]
FROM dbo.sqlwatch_logger_perf_os_schedulers d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
ORDER BY h.report_time DESC"
                    ),

                    // --- BarGauge panel (column=0, spanColumns) ---

                    BarGaugePanel(
                        id: "instance.disk_space",
                        title: "Disk Space",
                        thresholdKey: "Disk Space Used %",
                        unitSuffix: "%",
                        column: 0,
                        order: 10,
                        span: true,
                        colorThresholds: new List<ColorThresholdRule>
                        {
                            new() { Operator = ">=", Value = 0,  Color = "#1b5e20", Label = "Normal" },
                            new() { Operator = ">=", Value = 80, Color = "#ff9800", Label = "Warning" },
                            new() { Operator = ">=", Value = 90, Color = "#f44336", Label = "Critical" }
                        },
                        sqlServer: @"
SELECT
    v.volume_name AS [Label],
    CASE WHEN d.volume_total_space_bytes > 0
        THEN CAST((1.0 - (1.0 * d.volume_free_space_bytes / d.volume_total_space_bytes)) * 100.0 AS decimal(5,1))
        ELSE 0 END AS [Value],
    '%' AS [Unit],
    d.sql_instance AS [Instance]
FROM dbo.sqlwatch_logger_disk_utilisation_volume d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
INNER JOIN dbo.sqlwatch_meta_os_volume v WITH (NOLOCK)
    ON v.sql_instance = d.sql_instance
    AND v.sqlwatch_volume_id = d.sqlwatch_volume_id
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND h.snapshot_time = (
        SELECT MAX(h2.snapshot_time)
        FROM dbo.sqlwatch_logger_snapshot_header h2 WITH (NOLOCK)
        WHERE h2.report_time BETWEEN @TimeFrom AND @TimeTo
            AND h2.sql_instance = d.sql_instance
            AND h2.snapshot_type_id = d.snapshot_type_id
    )"
                    ),

                    // --- TimeSeries charts (alternating columns 1 and 2) ---

                    TimeSeriesPanel(
                        id: "instance.cpu",
                        title: "CPU Utilization",
                        column: 1,
                        order: 1,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    m.counter_name AS [Series],
    d.cntr_value_calculated AS [Value]
FROM dbo.sqlwatch_logger_perf_os_performance_counters d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
INNER JOIN dbo.sqlwatch_meta_performance_counter m WITH (NOLOCK)
    ON m.sql_instance = d.sql_instance
    AND m.performance_counter_id = d.performance_counter_id
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND m.counter_name IN ('Processor Time %','Privileged Time %','User Time %')
    AND m.cntr_type <> 1073939712
ORDER BY h.report_time"
                    ),

                    TimeSeriesPanel(
                        id: "instance.activity",
                        title: "Activity (Batch Requests, Logins, etc.)",
                        column: 2,
                        order: 2,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    m.counter_name AS [Series],
    d.cntr_value_calculated AS [Value]
FROM dbo.sqlwatch_logger_perf_os_performance_counters d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
INNER JOIN dbo.sqlwatch_meta_performance_counter m WITH (NOLOCK)
    ON m.sql_instance = d.sql_instance
    AND m.performance_counter_id = d.performance_counter_id
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND m.counter_name IN ('Batch Requests/Sec','Logins/sec','Transactions/sec','SQL Compilations/sec','User Connections')
    AND m.cntr_type <> 1073939712
ORDER BY h.report_time"
                    ),

                    TimeSeriesPanel(
                        id: "instance.waits_category",
                        title: "Wait Events by Category",
                        column: 1,
                        order: 3,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    ISNULL(ws.wait_category, 'Other') AS [Series],
    SUM(d.wait_time_ms_delta) AS [Value]
FROM dbo.sqlwatch_logger_perf_os_wait_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
LEFT JOIN dbo.vw_sqlwatch_meta_wait_stats_category ws WITH (NOLOCK)
    ON ws.sql_instance = d.sql_instance
    AND ws.wait_type_id = d.wait_type_id
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
GROUP BY h.report_time, ISNULL(ws.wait_category, 'Other')
HAVING SUM(d.wait_time_ms_delta) > 0
ORDER BY h.report_time"
                    ),

                    TimeSeriesPanel(
                        id: "instance.memory",
                        title: "Memory",
                        column: 2,
                        order: 4,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    'Physical Memory In Use (KB)' AS [Series],
    d.physical_memory_in_use_kb AS [Value]
FROM dbo.sqlwatch_logger_perf_os_process_memory d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))

UNION ALL

SELECT
    h.report_time,
    'Memory Utilization %',
    d.memory_utilization_percentage
FROM dbo.sqlwatch_logger_perf_os_process_memory d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
ORDER BY [Time]"
                    ),

                    TimeSeriesPanel(
                        id: "instance.sessions",
                        title: "Sessions",
                        column: 1,
                        order: 5,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    'Running' AS [Series], d.running AS [Value]
FROM dbo.sqlwatch_logger_dm_exec_sessions_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND d.type = 1

UNION ALL

SELECT h.report_time, 'Sleeping', d.sleeping
FROM dbo.sqlwatch_logger_dm_exec_sessions_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND d.type = 1

UNION ALL

SELECT h.report_time, 'Dormant', d.dormant
FROM dbo.sqlwatch_logger_dm_exec_sessions_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND d.type = 1
ORDER BY [Time]"
                    ),

                    TimeSeriesPanel(
                        id: "instance.requests",
                        title: "Requests",
                        column: 2,
                        order: 6,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    'Background' AS [Series], d.background AS [Value]
FROM dbo.sqlwatch_logger_dm_exec_requests_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND d.type = 1

UNION ALL

SELECT h.report_time, 'Running', d.running
FROM dbo.sqlwatch_logger_dm_exec_requests_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time AND h.snapshot_type_id = d.snapshot_type_id AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND d.type = 1

UNION ALL

SELECT h.report_time, 'Runnable', d.runnable
FROM dbo.sqlwatch_logger_dm_exec_requests_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time AND h.snapshot_type_id = d.snapshot_type_id AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND d.type = 1

UNION ALL

SELECT h.report_time, 'Sleeping', d.sleeping
FROM dbo.sqlwatch_logger_dm_exec_requests_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time AND h.snapshot_type_id = d.snapshot_type_id AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND d.type = 1

UNION ALL

SELECT h.report_time, 'Suspended', d.suspended
FROM dbo.sqlwatch_logger_dm_exec_requests_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time AND h.snapshot_type_id = d.snapshot_type_id AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND d.type = 1
ORDER BY [Time]"
                    ),

                    TimeSeriesPanel(
                        id: "instance.file_stats_latency",
                        title: "File Stats \u2014 Latency (ms)",
                        column: 1,
                        order: 7,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    ISNULL(f.database_name,'?') + ' - ' + ISNULL(mf.file_name,'?') AS [Series],
    CASE WHEN d.num_of_reads_delta > 0 THEN d.io_stall_read_ms_delta / d.num_of_reads_delta ELSE 0 END
    + CASE WHEN d.num_of_writes_delta > 0 THEN d.io_stall_write_ms_delta / d.num_of_writes_delta ELSE 0 END AS [Value]
FROM dbo.sqlwatch_logger_perf_file_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
LEFT JOIN dbo.sqlwatch_meta_master_file mf WITH (NOLOCK)
    ON mf.sql_instance = d.sql_instance
    AND mf.sqlwatch_database_id = d.sqlwatch_database_id
    AND mf.sqlwatch_master_file_id = d.sqlwatch_master_file_id
LEFT JOIN dbo.sqlwatch_meta_database f WITH (NOLOCK)
    ON f.sql_instance = mf.sql_instance
    AND f.sqlwatch_database_id = mf.sqlwatch_database_id
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
ORDER BY h.report_time"
                    ),

                    TimeSeriesPanel(
                        id: "instance.file_stats_throughput",
                        title: "File Stats \u2014 Throughput",
                        column: 2,
                        order: 8,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    ISNULL(f.database_name,'?') + ' Read B/s' AS [Series],
    CASE WHEN ISNULL(d.delta_seconds,0) > 0 THEN d.num_of_bytes_read_delta / d.delta_seconds ELSE 0 END AS [Value]
FROM dbo.sqlwatch_logger_perf_file_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
LEFT JOIN dbo.sqlwatch_meta_master_file mf WITH (NOLOCK)
    ON mf.sql_instance = d.sql_instance
    AND mf.sqlwatch_database_id = d.sqlwatch_database_id
    AND mf.sqlwatch_master_file_id = d.sqlwatch_master_file_id
LEFT JOIN dbo.sqlwatch_meta_database f WITH (NOLOCK)
    ON f.sql_instance = mf.sql_instance
    AND f.sqlwatch_database_id = mf.sqlwatch_database_id
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))

UNION ALL

SELECT
    h.report_time,
    ISNULL(f.database_name,'?') + ' Write B/s',
    CASE WHEN ISNULL(d.delta_seconds,0) > 0 THEN d.num_of_bytes_written_delta / d.delta_seconds ELSE 0 END
FROM dbo.sqlwatch_logger_perf_file_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
LEFT JOIN dbo.sqlwatch_meta_master_file mf WITH (NOLOCK)
    ON mf.sql_instance = d.sql_instance
    AND mf.sqlwatch_database_id = d.sqlwatch_database_id
    AND mf.sqlwatch_master_file_id = d.sqlwatch_master_file_id
LEFT JOIN dbo.sqlwatch_meta_database f WITH (NOLOCK)
    ON f.sql_instance = mf.sql_instance
    AND f.sqlwatch_database_id = mf.sqlwatch_database_id
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
ORDER BY [Time]"
                    ),

                    TimeSeriesPanel(
                        id: "instance.schedulers",
                        title: "Scheduler Stats",
                        column: 1,
                        order: 9,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    'Current Tasks' AS [Series], d.current_tasks_count AS [Value]
FROM dbo.sqlwatch_logger_perf_os_schedulers d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))

UNION ALL

SELECT h.report_time, 'Runnable Tasks', d.runnable_tasks_count
FROM dbo.sqlwatch_logger_perf_os_schedulers d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time AND h.snapshot_type_id = d.snapshot_type_id AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))

UNION ALL

SELECT h.report_time, 'Work Queue', d.work_queue_count
FROM dbo.sqlwatch_logger_perf_os_schedulers d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time AND h.snapshot_type_id = d.snapshot_type_id AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))

UNION ALL

SELECT h.report_time, 'Pending Disk IO', d.pending_disk_io_count
FROM dbo.sqlwatch_logger_perf_os_schedulers d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time AND h.snapshot_type_id = d.snapshot_type_id AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
ORDER BY [Time]"
                    ),

                    TimeSeriesPanel(
                        id: "instance.blocking",
                        title: "Blocking",
                        column: 2,
                        order: 10,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    'Blocked Sessions' AS [Series],
    COUNT(DISTINCT d.blocked_spid) AS [Value]
FROM dbo.sqlwatch_logger_xes_blockers d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
GROUP BY h.report_time
ORDER BY h.report_time"
                    ),

                    TimeSeriesPanel(
                        id: "instance.lock_requests",
                        title: "Lock Requests",
                        column: 1,
                        order: 11,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    m.counter_name AS [Series],
    d.cntr_value_calculated AS [Value]
FROM dbo.sqlwatch_logger_perf_os_performance_counters d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
INNER JOIN dbo.sqlwatch_meta_performance_counter m WITH (NOLOCK)
    ON m.sql_instance = d.sql_instance
    AND m.performance_counter_id = d.performance_counter_id
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND m.counter_name IN ('Lock Requests/sec','Lock Waits/sec','Lock Timeouts/sec','Number of Deadlocks/sec')
    AND m.cntr_type <> 1073939712
ORDER BY h.report_time"
                    ),

                    TimeSeriesPanel(
                        id: "instance.page_life",
                        title: "Page Life Expectancy",
                        column: 2,
                        order: 12,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    'Page Life Expectancy' AS [Series],
    d.cntr_value_calculated AS [Value]
FROM dbo.sqlwatch_logger_perf_os_performance_counters d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
INNER JOIN dbo.sqlwatch_meta_performance_counter m WITH (NOLOCK)
    ON m.sql_instance = d.sql_instance
    AND m.performance_counter_id = d.performance_counter_id
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND m.counter_name = 'Page life expectancy'
    AND m.cntr_type <> 1073939712
ORDER BY h.report_time"
                    ),

                    TimeSeriesPanel(
                        id: "instance.memory_clerks",
                        title: "Memory Clerks",
                        column: 1,
                        order: 13,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    mc.clerk_name AS [Series],
    omc.allocated_kb AS [Value]
FROM dbo.sqlwatch_logger_perf_os_memory_clerks omc WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = omc.snapshot_time
    AND h.snapshot_type_id = omc.snapshot_type_id
    AND h.sql_instance = omc.sql_instance
INNER JOIN dbo.sqlwatch_meta_memory_clerk mc WITH (NOLOCK)
    ON mc.sql_instance = omc.sql_instance
    AND mc.sqlwatch_mem_clerk_id = omc.sqlwatch_mem_clerk_id
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND omc.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
ORDER BY h.report_time"
                    ),

                    TimeSeriesPanel(
                        id: "instance.database_stats",
                        title: "Database Stats",
                        column: 2,
                        order: 14,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    'Database Count' AS [Series],
    COUNT(DISTINCT d.sqlwatch_database_id) AS [Value]
FROM dbo.sqlwatch_logger_disk_utilisation_database d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
GROUP BY h.report_time

UNION ALL

SELECT
    h.report_time,
    'Total Size (MB)',
    SUM(ISNULL(d.database_size_bytes,0)) / 1048576.0
FROM dbo.sqlwatch_logger_disk_utilisation_database d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
GROUP BY h.report_time
ORDER BY [Time]"
                    ),

                    TimeSeriesPanel(
                        id: "instance.tempdb",
                        title: "TempDB Size",
                        column: 1,
                        order: 15,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    'TempDB Size (MB)' AS [Series],
    SUM(d.size_on_disk_bytes) / 1048576.0 AS [Value]
FROM dbo.sqlwatch_logger_perf_file_stats d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
INNER JOIN dbo.sqlwatch_meta_master_file mf WITH (NOLOCK)
    ON mf.sql_instance = d.sql_instance
    AND mf.sqlwatch_database_id = d.sqlwatch_database_id
    AND mf.sqlwatch_master_file_id = d.sqlwatch_master_file_id
INNER JOIN dbo.sqlwatch_meta_database db WITH (NOLOCK)
    ON db.sql_instance = mf.sql_instance
    AND db.sqlwatch_database_id = mf.sqlwatch_database_id
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND db.database_name = 'tempdb'
GROUP BY h.report_time
ORDER BY h.report_time"
                    ),

                    // --- Bar chart (spanColumns) ---

                    TimeSeriesPanel(
                        id: "instance.checks_history",
                        title: "Check Failures Over Time",
                        column: 1,
                        order: 20,
                        chartType: "Bar",
                        span: true,
                        sqlServer: @"
SELECT
    h.report_time AS [Time],
    d.check_status AS [Series],
    COUNT(*) AS [Value]
FROM dbo.sqlwatch_logger_check d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND d.check_status <> 'OK'
GROUP BY h.report_time, d.check_status
ORDER BY h.report_time"
                    ),

                    // --- DataGrid (spanColumns) ---

                    DataGridPanel(
                        id: "instance.checks_detail",
                        title: "Check Details",
                        order: 21,
                        span: true,
                        sqlServer: @"
SELECT
    d.snapshot_time,
    c.check_name,
    c.check_description,
    d.check_value,
    d.check_status,
    d.check_exec_time_ms,
    d.sql_instance
FROM dbo.sqlwatch_logger_check d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.snapshot_type_id = d.snapshot_type_id
    AND h.sql_instance = d.sql_instance
INNER JOIN dbo.sqlwatch_meta_check c WITH (NOLOCK)
    ON c.sql_instance = d.sql_instance
    AND c.check_id = d.check_id
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
ORDER BY d.snapshot_time DESC"
                    )
                }
            };
        }

        // ================================================================
        // BPCHECK DASHBOARD  (live DMV-based best-practice checks)
        // ================================================================

        private static DashboardDefinition BuildBpCheckDashboard()
        {
            return new DashboardDefinition
            {
                Id = "bpcheck",
                Title = "Best Practices Check",
                NavTitle = "BPCheck",
                NavIcon = "\u2705",
                NavCategory = "Audits",
                NavOrder = 10,
                Route = "/bpcheck",
                Description = "Live DMV-based best-practice health checks, adapted from tigertoolbox BPCheck.",
                Source = "sqlwatch",
                DefaultDatabase = "master",
                ShowAllOption = false,
                Panels = new List<PanelDefinition>
                {
                    // ── StatCard summary row ──────────────────────────────

                    StatCardPanel(
                        id:    "bp.maxdop",
                        title: "MaxDOP",
                        unit:  "",
                        order: 1,
                        sqlServer: @"
SELECT CAST([value] AS INT) AS [Value]
FROM sys.configurations WITH (NOLOCK)
WHERE name = 'max degree of parallelism'"
                    ),

                    StatCardPanel(
                        id:    "bp.work_queue",
                        title: "Work Queue",
                        unit:  "",
                        order: 2,
                        colorThresholds: new List<ColorThresholdRule>
                        {
                            new() { Operator = ">=", Value = 0, Color = "#1b5e20", Label = "Normal"   },
                            new() { Operator = ">=", Value = 1, Color = "#f44336", Label = "Critical" }
                        },
                        sqlServer: @"
SELECT SUM(work_queue_count) AS [Value]
FROM sys.dm_os_schedulers WITH (NOLOCK)
WHERE parent_node_id < 64 AND scheduler_id < 255"
                    ),

                    StatCardPanel(
                        id:    "bp.lpim",
                        title: "LPIM Active",
                        unit:  "",
                        order: 3,
                        sqlServer: @"
SELECT CASE WHEN sql_memory_model = 2 THEN 1 ELSE 0 END AS [Value]
FROM sys.dm_os_sys_info WITH (NOLOCK)"
                    ),

                    StatCardPanel(
                        id:    "bp.suspect_pages",
                        title: "Suspect Pages",
                        unit:  "",
                        order: 4,
                        colorThresholds: new List<ColorThresholdRule>
                        {
                            new() { Operator = ">=", Value = 0, Color = "#1b5e20", Label = "Clean"    },
                            new() { Operator = ">=", Value = 1, Color = "#f44336", Label = "Critical" }
                        },
                        sqlServer: @"
SELECT COUNT(*) AS [Value]
FROM msdb.dbo.suspect_pages WITH (NOLOCK)
WHERE event_type IN (1, 2, 3)"
                    ),

                    StatCardPanel(
                        id:    "bp.policy_violations",
                        title: "Policy Violations",
                        unit:  "",
                        order: 5,
                        colorThresholds: new List<ColorThresholdRule>
                        {
                            new() { Operator = ">=", Value = 0, Color = "#1b5e20", Label = "Clean"    },
                            new() { Operator = ">=", Value = 1, Color = "#ff9800", Label = "Warning"  }
                        },
                        sqlServer: @"
SELECT COUNT(*) AS [Value]
FROM sys.sql_logins WITH (NOLOCK)
WHERE is_policy_checked = 0 OR is_expiration_checked = 0"
                    ),

                    // ── Max Server Memory ─────────────────────────────────

                    DataGridPanel(
                        id:    "bp.memory_config",
                        title: "Max Server Memory vs System RAM",
                        order: 10,
                        span:  true,
                        sqlServer: @"
SELECT
    sc.[value]                                      AS [configured_max_mem_MB],
    dosi.physical_memory_kb / 1024                  AS [total_system_mem_MB],
    CAST(sc.[value] * 100.0
         / NULLIF(dosi.physical_memory_kb / 1024, 0)
         AS DECIMAL(5,2))                           AS [pct_of_system_mem],
    CASE
        WHEN sc.[value] = 2147483647
            THEN '[WARNING] MaxServerMemory is default — revise before memory pressure occurs'
        WHEN sc.[value] > dosi.physical_memory_kb / 1024
            THEN '[WARNING] MaxServerMemory exceeds total physical RAM'
        WHEN sc.[value] * 100.0 / NULLIF(dosi.physical_memory_kb / 1024, 0) > 90
            THEN '[CAUTION] MaxServerMemory leaves <10 % for OS — consider reducing'
        ELSE '[OK]'
    END                                             AS [recommendation]
FROM sys.configurations sc WITH (NOLOCK)
CROSS JOIN sys.dm_os_sys_info dosi WITH (NOLOCK)
WHERE sc.name = 'max server memory (MB)'"
                    ),

                    // ── I/O Stall Analysis ────────────────────────────────

                    DataGridPanel(
                        id:    "bp.io_stall",
                        title: "I/O Stall Analysis (cumulative since restart)",
                        order: 11,
                        span:  true,
                        sqlServer: @"
SELECT
    DB_NAME(f.database_id)                                          AS [database_name],
    f.name                                                          AS [logical_file],
    f.type_desc                                                     AS [file_type],
    fs.num_of_reads                                                 AS [read_count],
    fs.num_of_writes                                                AS [write_count],
    CAST(fs.io_stall_read_ms  / (1.0 + fs.num_of_reads)  AS NUMERIC(10,2)) AS [avg_read_latency_ms],
    CAST(fs.io_stall_write_ms / (1.0 + fs.num_of_writes) AS NUMERIC(10,2)) AS [avg_write_latency_ms],
    fs.io_stall                                                     AS [total_stall_ms],
    CASE
        WHEN fs.io_stall_read_ms  / (1.0 + fs.num_of_reads)  >= 50
          OR fs.io_stall_write_ms / (1.0 + fs.num_of_writes) >= 50
            THEN '[WARNING] High latency — review storage configuration'
        WHEN fs.io_stall_read_ms  / (1.0 + fs.num_of_reads)  >= 20
          OR fs.io_stall_write_ms / (1.0 + fs.num_of_writes) >= 20
            THEN '[CAUTION] Elevated latency'
        ELSE '[OK]'
    END                                                             AS [status]
FROM sys.dm_io_virtual_file_stats(NULL, NULL) AS fs
INNER JOIN sys.master_files AS f WITH (NOLOCK)
    ON fs.database_id = f.database_id AND fs.file_id = f.file_id
ORDER BY fs.io_stall DESC"
                    ),

                    // ── Top Wait Stats with BPCheck categories ────────────

                    DataGridPanel(
                        id:    "bp.wait_stats",
                        title: "Top Wait Stats by Category (cumulative since restart)",
                        order: 12,
                        span:  true,
                        sqlServer: @"
;WITH Waits AS (
    SELECT
        wait_type,
        wait_time_ms / 1000.0                                                           AS wait_time_s,
        signal_wait_time_ms / 1000.0                                                    AS signal_wait_time_s,
        (wait_time_ms - signal_wait_time_ms) / 1000.0                                  AS resource_wait_time_s,
        100.0 * wait_time_ms / NULLIF(SUM(wait_time_ms) OVER (), 0)                   AS pct,
        ROW_NUMBER() OVER (ORDER BY wait_time_ms DESC)                                  AS rn
    FROM sys.dm_os_wait_stats WITH (NOLOCK)
    WHERE wait_type NOT IN (
        'RESOURCE_QUEUE','SQLTRACE_INCREMENTAL_FLUSH_SLEEP','SP_SERVER_DIAGNOSTICS_SLEEP',
        'SOSHOST_SLEEP','QDS_PERSIST_TASK_MAIN_LOOP_SLEEP','QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP',
        'LOGMGR_QUEUE','CHECKPOINT_QUEUE','REQUEST_FOR_DEADLOCK_SEARCH','XE_TIMER_EVENT',
        'BROKER_TASK_STOP','CLR_MANUAL_EVENT','CLR_AUTO_EVENT','DISPATCHER_QUEUE_SEMAPHORE',
        'FT_IFTS_SCHEDULER_IDLE_WAIT','BROKER_TO_FLUSH','XE_DISPATCHER_WAIT','XE_DISPATCHER_JOIN',
        'MSQL_XP','WAIT_FOR_RESULTS','CLR_SEMAPHORE','LAZYWRITER_SLEEP','SLEEP_TASK',
        'SLEEP_SYSTEMTASK','SQLTRACE_BUFFER_FLUSH','WAITFOR','BROKER_EVENTHANDLER','TRACEWRITE',
        'FT_IFTSHC_MUTEX','BROKER_RECEIVE_WAITFOR','ONDEMAND_TASK_QUEUE','DBMIRROR_EVENTS_QUEUE',
        'DBMIRRORING_CMD','BROKER_TRANSMITTER','SQLTRACE_WAIT_ENTRIES','SLEEP_BPOOL_FLUSH',
        'SQLTRACE_LOCK','DIRTY_PAGE_POLL','HADR_FILESTREAM_IOMGR_IOCOMPLETION',
        'WAIT_XTP_OFFLINE_CKPT_NEW_LOG'
    )
    AND wait_type NOT LIKE 'SLEEP_%'
)
SELECT
    W1.wait_type,
    CAST(W1.wait_time_s        AS DECIMAL(14,2)) AS wait_time_s,
    CAST(W1.signal_wait_time_s AS DECIMAL(14,2)) AS signal_wait_time_s,
    CAST(W1.pct                AS DECIMAL(14,2)) AS pct,
    CASE
        WHEN W1.wait_type = 'SOS_SCHEDULER_YIELD'                              THEN 'CPU'
        WHEN W1.wait_type = 'THREADPOOL'                                       THEN 'CPU - Worker Threads'
        WHEN W1.wait_type IN ('CXPACKET','EXCHANGE','CXCONSUMER',
             'HTBUILD','HTDELETE','HTMEMO','HTREINIT','HTREPARTITION')         THEN 'CPU - Parallelism'
        WHEN W1.wait_type LIKE 'LCK_%' OR W1.wait_type = 'LOCK'               THEN 'Lock'
        WHEN W1.wait_type LIKE 'LATCH_%'                                       THEN 'Latch'
        WHEN W1.wait_type LIKE 'PAGELATCH_%'                                   THEN 'Buffer Latch'
        WHEN W1.wait_type LIKE 'PAGEIOLATCH_%'                                 THEN 'Buffer IO'
        WHEN W1.wait_type IN ('LOGMGR','LOGBUFFER','LOGMGR_FLUSH','WRITELOG') THEN 'Tran Log IO'
        WHEN W1.wait_type IN ('ASYNC_NETWORK_IO','NET_WAITFOR_PACKET')         THEN 'Network IO'
        WHEN W1.wait_type LIKE 'RESOURCE_SEMAPHORE_%'                          THEN 'Memory - Compilation'
        WHEN W1.wait_type IN ('UTIL_PAGE_ALLOC','SOS_VIRTUALMEMORY_LOW',
             'SOS_RESERVEDMEMBLOCKLIST','RESOURCE_SEMAPHORE','CMEMTHREAD')     THEN 'Memory'
        ELSE 'Other'
    END AS wait_category
FROM Waits W1
WHERE W1.wait_time_s >= 0.01
ORDER BY W1.rn"
                    ),

                    // ── Worker Thread Exhaustion ──────────────────────────

                    DataGridPanel(
                        id:    "bp.worker_threads",
                        title: "Worker Thread Health",
                        order: 13,
                        span:  false,
                        sqlServer: @"
SELECT
    SUM(current_tasks_count)   AS [current_tasks],
    SUM(runnable_tasks_count)  AS [runnable_tasks],
    SUM(work_queue_count)      AS [work_queue_count],
    SUM(pending_disk_io_count) AS [pending_disk_io],
    CASE
        WHEN SUM(work_queue_count) > 1
            THEN '[WARNING] Work queue > 1 — possible worker thread exhaustion'
        WHEN AVG(runnable_tasks_count) > 2
            THEN '[CAUTION] High runnable task count — CPU pressure'
        ELSE '[OK]'
    END AS [status]
FROM sys.dm_os_schedulers WITH (NOLOCK)
WHERE parent_node_id < 64 AND scheduler_id < 255"
                    ),

                    // ── Missing Indexes ───────────────────────────────────

                    DataGridPanel(
                        id:    "bp.missing_indexes",
                        title: "High-Impact Missing Indexes (Score \u2265 100,000)",
                        order: 14,
                        span:  true,
                        sqlServer: @"
SELECT
    DB_NAME(i.database_id)                                                          AS [database_name],
    OBJECT_NAME(i.object_id, i.database_id)                                         AS [table_name],
    i.equality_columns                                                              AS [equality_key_cols],
    i.inequality_columns                                                            AS [inequality_key_cols],
    i.included_columns                                                              AS [included_cols],
    s.user_seeks + s.user_scans                                                     AS [user_hits],
    CAST(s.avg_user_impact       AS DECIMAL(6,2))                                   AS [avg_impact_pct],
    CAST((s.user_seeks + s.user_scans)
         * s.avg_total_user_cost * s.avg_user_impact AS DECIMAL(18,0))              AS [score]
FROM sys.dm_db_missing_index_details i WITH (NOLOCK)
INNER JOIN sys.dm_db_missing_index_groups g WITH (NOLOCK)
    ON i.index_handle = g.index_handle
INNER JOIN sys.dm_db_missing_index_group_stats s WITH (NOLOCK)
    ON s.group_handle = g.index_group_handle
WHERE i.database_id > 4
  AND (s.user_seeks + s.user_scans) * s.avg_total_user_cost * s.avg_user_impact >= 100000
ORDER BY score DESC"
                    ),

                    // ── Password Policy ───────────────────────────────────

                    DataGridPanel(
                        id:    "bp.password_policy",
                        title: "Password Policy Violations",
                        order: 15,
                        span:  false,
                        sqlServer: @"
SELECT
    name                    AS [login_name],
    is_policy_checked       AS [policy_checked],
    is_expiration_checked   AS [expiration_checked],
    LOGINPROPERTY(name, 'IsLocked')   AS [is_locked],
    LOGINPROPERTY(name, 'IsExpired')  AS [is_expired]
FROM sys.sql_logins WITH (NOLOCK)
WHERE is_policy_checked = 0 OR is_expiration_checked = 0
ORDER BY name"
                    ),

                    // ── Suspect Pages ─────────────────────────────────────

                    DataGridPanel(
                        id:    "bp.suspect_pages_detail",
                        title: "Suspect Pages Detail",
                        order: 16,
                        span:  false,
                        sqlServer: @"
SELECT
    DB_NAME(database_id) AS [database_name],
    file_id              AS [file_id],
    page_id              AS [page_id],
    CASE event_type
        WHEN 1 THEN 'Error 823 / unspecified 824'
        WHEN 2 THEN 'Bad Checksum'
        WHEN 3 THEN 'Torn Page'
        ELSE CAST(event_type AS VARCHAR(10))
    END                  AS [event_type],
    error_count          AS [error_count],
    last_update_date     AS [last_update_date]
FROM msdb.dbo.suspect_pages WITH (NOLOCK)
WHERE event_type IN (1, 2, 3)
ORDER BY DB_NAME(database_id), last_update_date DESC, file_id, page_id"
                    )
                }
            };
        }

        // ================================================================
        // SUPPORT QUERIES
        // ================================================================

        private static Dictionary<string, QueryPair> BuildSupportQueries()
        {
            return new Dictionary<string, QueryPair>
            {
                ["instances.list"] = new QueryPair
                {
                    SqlServer = @"
SELECT sql_instance
FROM dbo.sqlwatch_config_sql_instance WITH (NOLOCK)
ORDER BY repo_collector_is_active DESC, sql_instance"
                },

                ["longquery.sqltext"] = new QueryPair
                {
                    SqlServer = @"
SELECT
    COALESCE(qph.statement_for_query_plan_hash, '') AS sql_text,
    qph.query_plan_for_query_plan_hash AS query_plan
FROM dbo.sqlwatch_logger_xes_long_queries d WITH (NOLOCK)
LEFT JOIN dbo.sqlwatch_meta_query_plan qp WITH (NOLOCK)
    ON qp.sql_instance = d.sql_instance
    AND qp.plan_handle = d.plan_handle
    AND qp.statement_start_offset = d.statement_start_offset
    AND qp.statement_end_offset = d.statement_end_offset
LEFT JOIN dbo.sqlwatch_meta_query_plan_hash qph WITH (NOLOCK)
    ON qph.sql_instance = qp.sql_instance
    AND qph.query_plan_hash = qp.query_plan_hash
WHERE d.long_query_id = @QueryId
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))"
                },

                ["waits.sqltext"] = new QueryPair
                {
                    SqlServer = @"
SELECT
    COALESCE(qph.statement_for_query_plan_hash, '') AS sql_text,
    qph.query_plan_for_query_plan_hash AS query_plan
FROM dbo.sqlwatch_meta_query_plan_hash qph WITH (NOLOCK)
WHERE qph.query_plan_hash = @PlanHash
    AND qph.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))"
                }
            };
        }
    }
}
