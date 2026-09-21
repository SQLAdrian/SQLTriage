/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Estate consolidation analyzer. Queries each connected instance for its core
    /// count, sustained CPU utilisation, memory and I/O workload, then models how many
    /// idle-CPU servers could be stacked onto fewer instances — and the SQL Server
    /// per-core LICENSING saving that yields. Core licensing dwarfs RAM/disk cost, so
    /// reclaiming licensed cores is where the consolidation money is.
    /// </summary>
    public sealed class ConsolidationAnalysisService
    {
        private readonly ServerConnectionManager _connections;
        private readonly ILogger<ConsolidationAnalysisService> _logger;

        public ConsolidationAnalysisService(ServerConnectionManager connections, ILogger<ConsolidationAnalysisService> logger)
        {
            _connections = connections;
            _logger = logger;
        }

        // SQL Server licensing minimum is 4 cores per instance, sold in 2-core packs.
        private const int MinLicensedCores = 4;

        /// <summary>
        /// Probes every enabled connection's instances for the raw resource facts
        /// (cores, sustained CPU%, memory, daily I/O, uptime, edition). Shared by the
        /// legacy <see cref="AnalyzeAsync"/> and the premium consolidation engine so both
        /// see the identical estate snapshot.
        /// </summary>
        public async Task<List<ServerResource>> ProbeEstateAsync(CancellationToken ct = default)
        {
            var servers = new List<ServerResource>();
            var tasks = new List<Task<ServerResource?>>();
            foreach (var conn in _connections.GetEnabledConnections())
                foreach (var srv in conn.GetServerList())
                    tasks.Add(ProbeServerAsync(conn, srv, ct));

            foreach (var r in await Task.WhenAll(tasks))
                if (r != null) servers.Add(r);

            // Effective licensed cores today (each instance pays for max(4, cores)).
            foreach (var s in servers)
                s.LicensedCores = Math.Max(MinLicensedCores, s.Cores);

            return servers;
        }

        public async Task<ConsolidationReport> AnalyzeAsync(
            double targetUtilization, double entCorePrice, double stdCorePrice, CancellationToken ct = default)
        {
            var servers = await ProbeEstateAsync(ct);

            // Per-core licensing is paid on the HOST's cores, once, no matter how many
            // instances are stacked on it (MS SQL licensing guide: you license the
            // OSE/host's cores; 10 instances never cost more than the host's CPUs).
            // So group instances by physical host first: the host bills its maximum
            // observed core count, at the highest edition present on it.
            var hosts = servers
                .GroupBy(s => string.IsNullOrEmpty(s.HostName) ? s.ServerName : s.HostName,
                         StringComparer.OrdinalIgnoreCase)
                .Select(h => new
                {
                    Instances = h.ToList(),
                    HostCores = h.Max(s => s.Cores),
                    LicenseClass = h.Any(s => s.LicenseClass == "Enterprise") ? "Enterprise"
                                 : h.Any(s => s.LicenseClass == "Standard") ? "Standard" : "Other",
                    // ProcessUtilization is each instance's share of host CPU, so
                    // per-instance demands on one host sum without double counting.
                    DemandCores = h.Sum(s => s.Cores * (s.AvgCpuPercent / 100.0)),
                })
                .ToList();

            // Consolidate WITHIN a licensing class (you can't mix Enterprise + Standard
            // cores on one host's licensing). Effective demand = cores × utilisation;
            // required consolidated cores = demand / target, rounded up to an even
            // 2-core pack, never below the 4-core minimum.
            var groups = new List<ConsolidationGroup>();
            foreach (var classGroup in hosts.GroupBy(x => x.LicenseClass))
            {
                var list = classGroup.ToList();
                var currentCores = list.Sum(x => Math.Max(MinLicensedCores, x.HostCores));
                var demandCores = list.Sum(x => x.DemandCores);
                var rawRequired = targetUtilization > 0 ? demandCores / targetUtilization : demandCores;
                var requiredCores = Math.Max(MinLicensedCores, RoundUpToEven((int)Math.Ceiling(rawRequired)));
                if (requiredCores > currentCores) requiredCores = currentCores; // never "negative" saving

                var price = classGroup.Key == "Enterprise" ? entCorePrice : stdCorePrice;
                var savedCores = Math.Max(0, currentCores - requiredCores);

                groups.Add(new ConsolidationGroup(
                    LicenseClass: classGroup.Key,
                    ServerCount: list.Sum(x => x.Instances.Count),
                    HostCount: list.Count,
                    CurrentCores: currentCores,
                    RequiredCores: requiredCores,
                    SavedCores: savedCores,
                    PerCorePrice: price,
                    AnnualSaving: savedCores * price,
                    Servers: list.SelectMany(x => x.Instances).OrderBy(s => s.AvgCpuPercent).ToList()));
            }

            return new ConsolidationReport(
                GeneratedUtc: DateTime.UtcNow,
                TargetUtilization: targetUtilization,
                Servers: servers,
                Groups: groups,
                TotalAnnualSaving: groups.Sum(g => g.AnnualSaving),
                TotalSavedCores: groups.Sum(g => g.SavedCores));
        }

        private static int RoundUpToEven(int n) => n % 2 == 0 ? n : n + 1;

        /// <summary>
        /// Lightweight, metadata-ONLY long-horizon workload signals on an already-open connection:
        ///   (1) Plan cache — <c>sys.dm_exec_query_stats</c>: cumulative worker-time (the CPU bridge),
        ///       logical reads (the direct CPU partner) and physical reads, divided by the plan-cache age.
        ///   (2) Query Store — per QS-enabled user DB, hourly-bucketed Σ(avg_cpu_time × count_executions),
        ///       summed across DBs into an instance CPU-cores chain → mean + P95.
        /// NEVER reads query_sql_text or plan XML. All queries are bounded with short timeouts; any
        /// failure (permissions, QS off) is non-fatal and just leaves the signal unpopulated.
        /// </summary>
        private async Task GatherWorkloadTelemetryAsync(SqlConnection c, ServerResource r, CancellationToken ct)
        {
            // ── (1) Plan-cache CPU + read signature (instance-wide, single cheap query) ──
            try
            {
                const string planSql = @"
SET NOCOUNT ON;
SELECT
    SUM(qs.total_worker_time)                          AS worker_us,
    SUM(qs.total_logical_reads)                        AS logical_reads,
    SUM(qs.total_physical_reads)                       AS physical_reads,
    DATEDIFF(SECOND, MIN(qs.creation_time), GETDATE()) AS window_sec
FROM sys.dm_exec_query_stats qs;";
                using var cmd = new SqlCommand(planSql, c) { CommandTimeout = 8 };
                using var rd = await cmd.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct) && rd["worker_us"] != DBNull.Value)
                {
                    double D(string col) => rd[col] == DBNull.Value ? 0 : Convert.ToDouble(rd[col]);
                    var winSec = D("window_sec");
                    if (winSec > 0)
                    {
                        r.PlanCacheWindowHours = Math.Round(winSec / 3600.0, 1);
                        r.PlanCacheWorkerCores = Math.Round(D("worker_us") / 1e6 / winSec, 3); // avg cores of CPU
                        r.LogicalReadsPerSec = Math.Round(D("logical_reads") / winSec, 1);
                        r.PhysicalReadsPerSec = Math.Round(D("physical_reads") / winSec, 1);
                    }
                }
                rd.Close();
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Plan-cache telemetry failed for {Server}", r.ServerName); }

            // ── (2) Query Store CPU chain (per QS-enabled DB, hourly-bucketed, metadata only) ──
            try
            {
                var dbs = new List<string>();
                using (var dbCmd = new SqlCommand(
                    "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0;", c) { CommandTimeout = 5 })
                using (var dr = await dbCmd.ExecuteReaderAsync(ct))
                    while (await dr.ReadAsync(ct)) dbs.Add(dr.GetString(0));

                const string qsSql = @"
SET NOCOUNT ON;
IF (SELECT actual_state FROM sys.database_query_store_options) IN (1, 2)
    SELECT DATEADD(HOUR, DATEDIFF(HOUR, 0, rsi.start_time), 0)         AS hr,
           SUM(rs.avg_cpu_time * rs.count_executions) / 1000000.0      AS cpu_sec
    FROM sys.query_store_runtime_stats rs
    JOIN sys.query_store_runtime_stats_interval rsi
         ON rsi.runtime_stats_interval_id = rs.runtime_stats_interval_id
    WHERE rsi.start_time >= DATEADD(DAY, -60, SYSUTCDATETIME())
    GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, rsi.start_time), 0);";

                var hourly = new Dictionary<DateTime, double>();
                DateTime? first = null, last = null;
                int dbsWithQs = 0;

                foreach (var db in dbs)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        c.ChangeDatabase(db);
                        using var cmd = new SqlCommand(qsSql, c) { CommandTimeout = 8 };
                        using var rd = await cmd.ExecuteReaderAsync(ct);
                        bool any = false;
                        while (await rd.ReadAsync(ct))
                        {
                            any = true;
                            var hr = (DateTime)rd["hr"];
                            var sec = rd["cpu_sec"] == DBNull.Value ? 0 : Convert.ToDouble(rd["cpu_sec"]);
                            hourly[hr] = hourly.TryGetValue(hr, out var v) ? v + sec : sec;
                            if (first == null || hr < first) first = hr;
                            if (last == null || hr > last) last = hr;
                        }
                        rd.Close();
                        if (any) dbsWithQs++;
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "QS read failed for {Server}/{Db}", r.ServerName, db); }
                }

                try { c.ChangeDatabase("master"); } catch { /* best-effort reset */ }

                if (hourly.Count > 0)
                {
                    var cores = hourly.Values.Select(s => s / 3600.0).OrderBy(x => x).ToList();
                    r.QsAvailable = true;
                    r.QsDbCount = dbsWithQs;
                    r.QsHourBuckets = cores.Count;
                    r.QsWindowHours = first != null && last != null
                        ? Math.Round((last.Value - first.Value).TotalHours + 1, 1) : cores.Count;
                    r.QsCpuCoresMean = Math.Round(cores.Average(), 3);
                    r.QsCpuCoresP95 = Math.Round(Percentile(cores, 0.95), 3);
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "QS telemetry failed for {Server}", r.ServerName); }
        }

        /// <summary>Linear-interpolated percentile of an ascending-sorted list.</summary>
        private static double Percentile(List<double> sortedAsc, double q)
        {
            if (sortedAsc.Count == 0) return 0;
            if (sortedAsc.Count == 1) return sortedAsc[0];
            var rank = q * (sortedAsc.Count - 1);
            int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
            return lo == hi ? sortedAsc[lo] : sortedAsc[lo] + (rank - lo) * (sortedAsc[hi] - sortedAsc[lo]);
        }

        private async Task<ServerResource?> ProbeServerAsync(ServerConnection conn, string serverName, CancellationToken ct)
        {
            const string sql = @"
SET NOCOUNT ON;
DECLARE @cpu INT;
SELECT @cpu = AVG(cpu) FROM (
    SELECT r.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]','int') AS cpu
    FROM (SELECT CONVERT(xml, record) AS r
          FROM sys.dm_os_ring_buffers
          WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR') t
) z;
DECLARE @uptime_days FLOAT =
    NULLIF(DATEDIFF(SECOND, (SELECT sqlserver_start_time FROM sys.dm_os_sys_info), GETDATE()), 0) / 86400.0;
SELECT
    CONVERT(VARCHAR(128), SERVERPROPERTY('MachineName')) AS HostName,
    si.cpu_count                                   AS Cores,
    si.hyperthread_ratio                           AS HyperthreadRatio,
    si.physical_memory_kb / 1024                    AS PhysMemMb,
    CONVERT(INT, ISNULL((SELECT value_in_use FROM sys.configurations WHERE name = 'max server memory (MB)'), 0)) AS MaxMemMb,
    CONVERT(VARCHAR(128), SERVERPROPERTY('Edition')) AS Edition,
    CONVERT(VARCHAR(64),  SERVERPROPERTY('ProductVersion')) AS Version,
    ISNULL(@cpu, 0)                                 AS AvgCpuPct,
    ISNULL(@uptime_days, 0)                         AS UptimeDays,
    io.total_reads, io.total_writes, io.total_bytes
FROM sys.dm_os_sys_info si
CROSS APPLY (
    SELECT SUM(num_of_reads) AS total_reads, SUM(num_of_writes) AS total_writes,
           SUM(num_of_bytes_read + num_of_bytes_written) AS total_bytes
    FROM sys.dm_io_virtual_file_stats(NULL, NULL)
) io;";
            try
            {
                using var c = new SqlConnection(conn.GetConnectionString(serverName, "master"));
                await c.OpenAsync(ct);
                using var cmd = new SqlCommand(sql, c) { CommandTimeout = 20 };
                using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return null;

                long G(string col) { var o = rd[col]; return o == DBNull.Value ? 0 : Convert.ToInt64(o); }
                int I(string col) { var o = rd[col]; return o == DBNull.Value ? 0 : Convert.ToInt32(o); }

                var uptimeDays = Convert.ToDouble(rd["UptimeDays"] is DBNull ? 0 : rd["UptimeDays"]);
                var totalReads = G("total_reads");
                var totalWrites = G("total_writes");
                var edition = rd["Edition"] as string ?? "";

                // Cumulative file-stats since restart → estimated daily IOPS.
                double dailyDiv = uptimeDays > 0 ? uptimeDays : 1;
                var result = new ServerResource
                {
                    ServerName = serverName,
                    HostName = rd["HostName"] as string ?? serverName,
                    Cores = I("Cores"),
                    PhysicalMemoryMb = I("PhysMemMb"),
                    MaxServerMemoryMb = I("MaxMemMb"),
                    Edition = edition,
                    Version = rd["Version"] as string ?? "",
                    AvgCpuPercent = I("AvgCpuPct"),
                    UptimeDays = Math.Round(uptimeDays, 1),
                    DailyReadIops = (long)(totalReads / dailyDiv),
                    DailyWriteIops = (long)(totalWrites / dailyDiv),
                    DailyIoGb = Math.Round(G("total_bytes") / dailyDiv / (1024.0 * 1024 * 1024), 1),
                };

                // Close the snapshot reader, then gather the long-horizon signals on the same connection.
                rd.Close();
                await GatherWorkloadTelemetryAsync(c, result, ct);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Consolidation probe failed for {Server}", serverName);
                return null;
            }
        }
    }

    public sealed class ServerResource
    {
        public string ServerName { get; set; } = "";
        /// <summary>Physical host (SERVERPROPERTY MachineName) — instances stacked on one
        /// host share its core licensing, so costing must group on this, not ServerName.</summary>
        public string HostName { get; set; } = "";
        public int Cores { get; set; }
        public int PhysicalMemoryMb { get; set; }
        public int MaxServerMemoryMb { get; set; }
        public string Edition { get; set; } = "";
        public string Version { get; set; } = "";
        public int AvgCpuPercent { get; set; }
        public double UptimeDays { get; set; }
        public long DailyReadIops { get; set; }
        public long DailyWriteIops { get; set; }
        public double DailyIoGb { get; set; }
        public int LicensedCores { get; set; }

        // ── Long-horizon workload telemetry (metadata-only) ──
        // Query Store CPU chain: cores of query-CPU per clock hour, summed across QS-enabled DBs.
        public bool QsAvailable { get; set; }
        public int QsDbCount { get; set; }
        public int QsHourBuckets { get; set; }
        public double QsWindowHours { get; set; }
        public double QsCpuCoresMean { get; set; }
        public double QsCpuCoresP95 { get; set; }
        // Plan-cache (sys.dm_exec_query_stats): worker-time bridge + the direct CPU partner (logical reads).
        public double PlanCacheWorkerCores { get; set; }
        public double PlanCacheWindowHours { get; set; }
        public double LogicalReadsPerSec { get; set; }
        public double PhysicalReadsPerSec { get; set; }

        /// <summary>Coarse licensing class for grouping: Enterprise | Standard | Other.</summary>
        public string LicenseClass =>
            Edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) ? "Enterprise"
            : Edition.Contains("Standard", StringComparison.OrdinalIgnoreCase) ? "Standard"
            : "Other";

        public long DailyTotalIops => DailyReadIops + DailyWriteIops;
    }

    public sealed record ConsolidationGroup(
        string LicenseClass,
        int ServerCount,
        int HostCount,
        int CurrentCores,
        int RequiredCores,
        int SavedCores,
        double PerCorePrice,
        double AnnualSaving,
        IReadOnlyList<ServerResource> Servers);

    public sealed record ConsolidationReport(
        DateTime GeneratedUtc,
        double TargetUtilization,
        IReadOnlyList<ServerResource> Servers,
        IReadOnlyList<ConsolidationGroup> Groups,
        double TotalAnnualSaving,
        int TotalSavedCores);
}
