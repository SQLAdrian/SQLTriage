/* In the name of God, the Merciful, the Compassionate */
/*
 * PowerEstimateService — per-server electrical-power ESTIMATE + tuning headroom,
 * derived entirely from inside SQL (VIEW SERVER STATE; no agent, no admin).
 *
 * Companion + methodology: bpscripts/Estimate server power and tuning headroom.sql.
 * This is the maintainable C# port of that script's Model A (linear util->power,
 * Fan/Weber/Barroso ISCA 2007 linear form) + the hardware-class calibration that
 * was validated against a measured WattSeal reading.
 *
 * HONESTY (voice contract): everything here is a MODELLED low..high band, never a
 * measurement. Lead with the RELATIVE tuning delta; absolute watts are a hedged
 * secondary band that is SUPPRESSED on virtual/managed hosts (the physical host is
 * invisible there — a per-VM watt number is fiction). Tuning figures are "potential
 * avoidance IF capacity is reclaimed", never a guaranteed saving.
 */

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;

namespace SQLTriage.Data.Services
{
    public class PowerEstimateService
    {
        private readonly ILogger<PowerEstimateService> _logger;
        private readonly ServerConnectionManager _connections;
        private readonly ConcurrentDictionary<string, (DateTime At, ServerPowerEstimate Est)> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        // Re-probe at most this often per server (the ring buffer only refreshes ~1/min).
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(90);

        // Translation defaults — editable later via config; disclosed in the readout.
        // $/kWh + grid CO2 — editable per deployment via Config/power-pricing.json (these are the fallback).
        private double _costPerKwh = 0.15;   // US-ish; EU/UK ~0.30+, NZ ~0.13
        private double _co2KgPerKwh = 0.40;  // grid kgCO2e/kWh (world ~0.40)
        private bool _ratesLoaded;
        private const int HoursPerYear = 8760;
        private const double TuneCutLow = 0.10;          // illustrative SQL CPU-work reduction band
        private const double TuneCutHigh = 0.30;
        private const double PsuEffLow = 0.82, PsuEffHigh = 0.90;
        private const double RamWattPer16GbLow = 3.0, RamWattPer16GbHigh = 5.0;
        private const double DiskBaseLow = 5.0, DiskBaseHigh = 10.0;
        private const int MinUptimeDaysForAnnual = 7;

        public PowerEstimateService(ILogger<PowerEstimateService> logger, ServerConnectionManager connections)
        {
            _logger = logger;
            _connections = connections;
        }

        /// <summary>
        /// Probe a server and return its power estimate. Cached per server for a short TTL.
        /// Returns null when the server can't be reached or the DMVs are unreadable.
        /// </summary>
        public async Task<ServerPowerEstimate?> GetEstimateAsync(string serverName, bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return null;

            if (!forceRefresh
                && _cache.TryGetValue(serverName, out var hit)
                && (DateTime.UtcNow - hit.At) < CacheTtl)
            {
                return hit.Est;
            }

            try
            {
                var inputs = await ProbeAsync(serverName);
                if (inputs is null) return null;

                EnsureRates();
                var est = Model(inputs);
                _cache[serverName] = (DateTime.UtcNow, est);
                return est;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Power estimate probe failed for {Server}", serverName);
                return null;
            }
        }

        // Per-fix power-saving band: the remediation type's reduction range applied to the
        // server's SQL CPU dynamic power. Relative %-band always; watts only on a physical
        // host (suppressed on VM/cloud, where an absolute number is fiction).
        public async Task<FixPowerSaving?> EstimateFixSavingAsync(string serverName, RemediationType type)
        {
            var est = await GetEstimateAsync(serverName);
            if (est is null) return null;
            return SavingFrom(est, type);
        }

        /// <summary>
        /// Peek the per-server estimate cache WITHOUT probing. Returns false when there is
        /// no cached estimate — the caller should then show the RELATIVE band only and never
        /// block the UI on a live DMV probe (the approval-gate honesty rule). The cached
        /// value is illustrative, so a slightly stale entry (past the refresh TTL) is fine.
        /// </summary>
        public bool TryGetCached(string serverName, out ServerPowerEstimate? estimate)
        {
            estimate = null;
            if (string.IsNullOrWhiteSpace(serverName)) return false;
            if (_cache.TryGetValue(serverName, out var hit)) { estimate = hit.Est; return true; }
            return false;
        }

        /// <summary>
        /// Synchronous projection of an already-fetched estimate onto a fix class.
        /// Lets the UI render the band from a cached estimate without an async probe
        /// mid-render. Relative %-band always; watts only on a physical host.
        /// </summary>
        public static FixPowerSaving SavingFrom(ServerPowerEstimate est, RemediationType type)
        {
            var (lo, hi) = ReductionBand(type);
            int? wLo = null, wHi = null;
            if (est.AbsoluteApplicable && est.SqlCpuWattsHigh.HasValue)
            {
                wLo = (int)Math.Round(est.SqlCpuWattsLow!.Value * lo / 100.0);
                wHi = (int)Math.Round(est.SqlCpuWattsHigh.Value * hi / 100.0);
            }
            return new FixPowerSaving(lo, hi, wLo, wHi);
        }

        /// <summary>Illustrative CPU-work reduction band per fix class (matches the bpscript Model B).</summary>
        public static (int Low, int High) ReductionBand(RemediationType type) => type switch
        {
            RemediationType.IndexAddRebuild => (10, 40),
            RemediationType.MaxdopParamSniff => (5, 25),
            RemediationType.IoReduction => (10, 35),
            RemediationType.PlanQuality => (10, 30),
            _ => (3, 10),
        };

        // Load $/kWh + CO2 intensity from Config/power-pricing.json once; fall back to defaults.
        private void EnsureRates()
        {
            if (_ratesLoaded) return;
            _ratesLoaded = true;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Config", "power-pricing.json");
                if (!File.Exists(path)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var r = doc.RootElement;
                if (r.TryGetProperty("costPerKwh", out var c) && c.TryGetDouble(out var cv) && cv > 0) _costPerKwh = cv;
                if (r.TryGetProperty("co2KgPerKwh", out var e) && e.TryGetDouble(out var ev) && ev > 0) _co2KgPerKwh = ev;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "power-pricing.json load failed; using defaults"); }
        }

        // ── Raw-input probe (one round trip; no WAITFOR — storage term uses the base band) ──
        private async Task<ServerPowerInputs?> ProbeAsync(string serverName)
        {
            var conn = _connections.GetEnabledConnections()
                .FirstOrDefault(c => c.GetServerList()
                    .Any(s => string.Equals(s, serverName, StringComparison.OrdinalIgnoreCase)));
            if (conn is null) return null;

            var connStr = conn.GetConnectionString(serverName, "master") +
                          ";Connect Timeout=5;Application Name=SQLTriage-PowerProbe";
            using var sql = new SqlConnection(connStr);
            await sql.OpenAsync();

            using var cmd = sql.CreateCommand();
            cmd.CommandTimeout = 8;
            // QUOTED_IDENTIFIER is ON by default under SqlClient, so the XML .value() calls are fine.
            cmd.CommandText = @"
SET NOCOUNT ON;
DECLARE @cpu int, @ht int, @memkb bigint, @vm sysname, @start datetime;
SELECT @cpu = cpu_count,
       @ht  = NULLIF(hyperthread_ratio, 0),
       @memkb = physical_memory_kb,
       @vm  = virtual_machine_type_desc,
       @start = sqlserver_start_time
FROM sys.dm_os_sys_info;

DECLARE @hostBusy decimal(9,2), @sqlPct decimal(9,2);
;WITH rb AS (
    SELECT CONVERT(xml, record) AS rec, [timestamp]
    FROM sys.dm_os_ring_buffers
    WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR'
), parsed AS (
    SELECT rec.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]','int') AS sql_pct,
           rec.value('(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]','int')          AS idle_pct,
           [timestamp]
    FROM rb
)
SELECT @sqlPct = AVG(CONVERT(decimal(9,2), sql_pct)),
       @hostBusy = AVG(CONVERT(decimal(9,2), 100 - idle_pct))
FROM (SELECT TOP (60) sql_pct, idle_pct, [timestamp] FROM parsed ORDER BY [timestamp] DESC) r;

DECLARE @uptimeSec bigint = NULLIF(DATEDIFF(SECOND, @start, GETDATE()), 0);
DECLARE @workerPct decimal(9,2) = CONVERT(decimal(9,2),
        ISNULL((SELECT SUM(CONVERT(float, total_worker_time)) FROM sys.dm_exec_query_stats), 0)
        / (CONVERT(float, @cpu) * ISNULL(@uptimeSec, 1) * 1000000.0) * 100.0);

SELECT
    cpu_count       = @cpu,
    hyperthread     = ISNULL(@ht, 1),
    physical_mem_kb = @memkb,
    vm_type         = @vm,
    engine_edition  = CONVERT(int, SERVERPROPERTY('EngineEdition')),
    uptime_days     = DATEDIFF(DAY, @start, GETDATE()),
    host_busy_pct   = ISNULL(@hostBusy, 0),
    sql_cpu_pct     = ISNULL(@sqlPct, 0),
    sql_worker_pct  = CASE WHEN @workerPct < 0 THEN 0 WHEN @workerPct > 100 THEN 100 ELSE @workerPct END,
    cpu_history_ok  = CASE WHEN @hostBusy IS NULL THEN 0 ELSE 1 END;";

            using var rdr = await cmd.ExecuteReaderAsync();
            if (!await rdr.ReadAsync()) return null;

            int Ord(string n) => rdr.GetOrdinal(n);
            int GetInt(string n) => rdr.IsDBNull(Ord(n)) ? 0 : Convert.ToInt32(rdr[n]);
            decimal GetDec(string n) => rdr.IsDBNull(Ord(n)) ? 0m : Convert.ToDecimal(rdr[n]);

            return new ServerPowerInputs
            {
                ServerName = serverName,
                CpuCount = GetInt("cpu_count"),
                HyperthreadRatio = Math.Max(1, GetInt("hyperthread")),
                PhysicalMemoryKb = rdr.IsDBNull(Ord("physical_mem_kb")) ? 0L : Convert.ToInt64(rdr["physical_mem_kb"]),
                VmType = rdr.IsDBNull(Ord("vm_type")) ? "NONE" : rdr.GetString(Ord("vm_type")),
                EngineEdition = GetInt("engine_edition"),
                UptimeDays = GetInt("uptime_days"),
                HostBusyPct = (double)GetDec("host_busy_pct"),
                SqlCpuPct = (double)GetDec("sql_cpu_pct"),
                SqlWorkerPct = (double)GetDec("sql_worker_pct"),
                CpuHistoryOk = GetInt("cpu_history_ok") == 1,
            };
        }

        // ── Model A (+ hardware class, VM suppression, worker-time basis) in C# ──
        private ServerPowerEstimate Model(ServerPowerInputs i)
        {
            bool isManaged = i.EngineEdition is 5 or 6 or 8 or 11;        // Azure SQL DB / Synapse / MI / Edge
            bool isVirtual = !string.IsNullOrEmpty(i.VmType) && !string.Equals(i.VmType, "NONE", StringComparison.OrdinalIgnoreCase);
            int sockets = Math.Max(1, i.HyperthreadRatio > 0 ? i.CpuCount / i.HyperthreadRatio : 1);
            double ramGb = i.PhysicalMemoryKb / 1024.0 / 1024.0;

            var cls = Classify(isManaged, isVirtual, sockets, i.CpuCount, ramGb);
            var b = Bands(cls);

            // Absolute watts are only honest on a non-managed, non-virtual (physical) host.
            bool absApplicable = !isManaged && !isVirtual;
            bool annualAllowed = absApplicable && i.UptimeDays >= MinUptimeDaysForAnnual && i.CpuHistoryOk;

            double util = Math.Clamp(i.HostBusyPct / 100.0, 0, 1);
            // Stable basis for tuning/annual so an idle-at-sample-time-but-busy server isn't zeroed.
            double basisPct = Math.Max(i.SqlCpuPct, i.SqlWorkerPct);

            // CPU / RAM / disk -> whole-server band.
            double cpuLo = sockets * b.TdpLo * (b.IdleLo + (1 - b.IdleLo) * util);
            double cpuHi = sockets * b.TdpHi * (b.IdleHi + (1 - b.IdleHi) * util);
            double ramLo = (ramGb / 16.0) * RamWattPer16GbLow;
            double ramHi = (ramGb / 16.0) * RamWattPer16GbHigh;
            double srvLo = (cpuLo + ramLo + DiskBaseLow) / PsuEffHigh + b.OvhLo;
            double srvHi = (cpuHi + ramHi + DiskBaseHigh) / PsuEffLow + b.OvhHi;

            // Saving band (CPU dynamic term only) — paired consistently so it can't invert.
            double savedLo = sockets * b.TdpLo * (1 - b.IdleLo) * (basisPct * TuneCutLow / 100.0) / PsuEffHigh;
            double savedHi = sockets * b.TdpHi * (1 - b.IdleHi) * (basisPct * TuneCutHigh / 100.0) / PsuEffLow;
            savedLo = Math.Max(0, savedLo);
            savedHi = Math.Max(0, savedHi);

            double kWhLo = savedLo * HoursPerYear / 1000.0;
            double kWhHi = savedHi * HoursPerYear / 1000.0;

            return new ServerPowerEstimate
            {
                ServerName = i.ServerName,
                HardwareClass = cls,
                IsVirtual = isVirtual,
                IsManaged = isManaged,
                AbsoluteApplicable = absApplicable,
                HostBusyPct = i.HostBusyPct,
                SqlCpuPct = i.SqlCpuPct,
                TuneCutLowPct = (int)(TuneCutLow * 100),
                TuneCutHighPct = (int)(TuneCutHigh * 100),
                ServerWattsLow = absApplicable ? (int?)Math.Round(srvLo) : null,
                ServerWattsHigh = absApplicable ? (int?)Math.Round(srvHi) : null,
                TunedWattsLow = absApplicable ? (int?)Math.Round(srvLo - savedLo) : null,
                TunedWattsHigh = absApplicable ? (int?)Math.Round(srvHi - savedHi) : null,
                SqlCpuWattsLow = absApplicable ? (int?)Math.Round(sockets * b.TdpLo * (1 - b.IdleLo) * (basisPct / 100.0) / PsuEffHigh) : null,
                SqlCpuWattsHigh = absApplicable ? (int?)Math.Round(sockets * b.TdpHi * (1 - b.IdleHi) * (basisPct / 100.0) / PsuEffLow) : null,
                AnnualKWhLow = annualAllowed ? (int?)Math.Round(kWhLo) : null,
                AnnualKWhHigh = annualAllowed ? (int?)Math.Round(kWhHi) : null,
                AnnualCostLowUsd = annualAllowed ? (int?)Math.Round(kWhLo * _costPerKwh) : null,
                AnnualCostHighUsd = annualAllowed ? (int?)Math.Round(kWhHi * _costPerKwh) : null,
                AnnualCo2KgLow = annualAllowed ? (int?)Math.Round(kWhLo * _co2KgPerKwh) : null,
                AnnualCo2KgHigh = annualAllowed ? (int?)Math.Round(kWhHi * _co2KgPerKwh) : null,
            };
        }

        private static string Classify(bool managed, bool virt, int sockets, int cpu, double ramGb)
        {
            if (managed || virt) return "vm_unknown";
            if (sockets >= 2) return "server_2s";
            if (cpu >= 16 || ramGb >= 64) return "server_1s";
            if (cpu >= 6 || ramGb >= 24) return "desktop";
            return "laptop_mobile";
        }

        // TDP is whole-CPU for laptop/desktop, per-socket for servers. Bands deliberately wide.
        private static (double TdpLo, double TdpHi, double IdleLo, double IdleHi, double OvhLo, double OvhHi) Bands(string cls) => cls switch
        {
            "laptop_mobile" => (15, 45, 0.15, 0.30, 5, 15),
            "desktop"       => (35, 125, 0.20, 0.35, 20, 50),
            "server_1s"     => (105, 205, 0.40, 0.55, 30, 60),
            "server_2s"     => (105, 205, 0.40, 0.55, 50, 100),
            _               => (30, 205, 0.30, 0.55, 15, 80),  // vm_unknown — very wide
        };
    }

    // ── DTOs ──
    public class ServerPowerInputs
    {
        public string ServerName { get; set; } = "";
        public int CpuCount { get; set; }
        public int HyperthreadRatio { get; set; } = 1;
        public long PhysicalMemoryKb { get; set; }
        public string VmType { get; set; } = "NONE";
        public int EngineEdition { get; set; }
        public int UptimeDays { get; set; }
        public double HostBusyPct { get; set; }
        public double SqlCpuPct { get; set; }
        public double SqlWorkerPct { get; set; }
        public bool CpuHistoryOk { get; set; }
    }

    public class ServerPowerEstimate
    {
        public string ServerName { get; set; } = "";
        public string HardwareClass { get; set; } = "";
        public bool IsVirtual { get; set; }
        public bool IsManaged { get; set; }
        /// <summary>True only on a physical host — absolute watts are fiction on VM/cloud, so they're suppressed.</summary>
        public bool AbsoluteApplicable { get; set; }

        public double HostBusyPct { get; set; }
        public double SqlCpuPct { get; set; }
        public int TuneCutLowPct { get; set; }
        public int TuneCutHighPct { get; set; }

        // Absolute band (null when suppressed on VM/managed).
        public int? ServerWattsLow { get; set; }
        public int? ServerWattsHigh { get; set; }
        public int? TunedWattsLow { get; set; }
        public int? TunedWattsHigh { get; set; }

        /// <summary>SQL-attributable CPU dynamic watts (the per-100%-cut base). Null when absolute is suppressed.</summary>
        public int? SqlCpuWattsLow { get; set; }
        public int? SqlCpuWattsHigh { get; set; }

        // Annual avoidance (null unless physical + >= 7 days uptime). "Potential, if reclaimed."
        public int? AnnualKWhLow { get; set; }
        public int? AnnualKWhHigh { get; set; }
        public int? AnnualCostLowUsd { get; set; }
        public int? AnnualCostHighUsd { get; set; }
        public int? AnnualCo2KgLow { get; set; }
        public int? AnnualCo2KgHigh { get; set; }

        /// <summary>The honest, relative-first one-liner for the status-bar chip.</summary>
        public string RelativeHeadline =>
            $"Tuning could cut this server's compute energy ~{TuneCutLowPct}–{TuneCutHighPct}% (modelled)";

        public string AbsoluteBand =>
            AbsoluteApplicable && ServerWattsLow.HasValue
                ? $"~{ServerWattsLow}–{ServerWattsHigh} W (estimate)"
                : IsManaged ? "n/a (managed service)" : "shared host — not shown";
    }

    /// <summary>A per-fix power-saving estimate: a relative %-of-CPU-work band, plus a watt band on a physical host.</summary>
    public readonly record struct FixPowerSaving(int LowPct, int HighPct, int? WattsLow, int? WattsHigh);
}
