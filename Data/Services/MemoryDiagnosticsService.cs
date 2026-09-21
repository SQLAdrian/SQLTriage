/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Captures a production-safe memory diagnostic snapshot (no dev tools, no admin
    /// rights required) — managed heap by generation incl. LOH/POH, fragmentation,
    /// process working/private set, hot-tier cache size, and WebView2 process totals.
    /// Surfaced on the Perf Inspector page as a live panel + downloadable dump so a
    /// memory issue can be diagnosed on a locked-down production server.
    /// </summary>
    public sealed class MemoryDiagnosticsService
    {
        private readonly IMemoryCache? _hotTier;
        private readonly MemoryMonitorService? _monitor;

        public MemoryDiagnosticsService(IMemoryCache? hotTier = null, MemoryMonitorService? monitor = null)
        {
            _hotTier = hotTier;
            _monitor = monitor;
        }

        public MemorySnapshot Capture()
        {
            const double MB = 1024.0 * 1024.0;
            var proc = Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();

            var gens = new List<GenInfo>();
            var gi = gc.GenerationInfo.ToArray();
            for (var i = 0; i < gi.Length; i++)
            {
                gens.Add(new GenInfo(
                    Name: i switch { 0 => "Gen 0", 1 => "Gen 1", 2 => "Gen 2", 3 => "LOH", 4 => "POH", _ => "Gen " + i },
                    SizeMB: Math.Round(gi[i].SizeAfterBytes / MB, 1),
                    FragmentedMB: Math.Round(gi[i].FragmentationAfterBytes / MB, 1)));
            }

            // WebView2 attribution. PID-parent walking misses reparented children,
            // and command-line attribution needs WMI (not referenced), so we report
            // the machine-wide msedgewebview2 total with a caveat. The managed host
            // numbers below are the precise per-process truth.
            int wvCount = 0; double wvMB = 0;
            try
            {
                var wv = Process.GetProcessesByName("msedgewebview2");
                wvCount = wv.Length;
                wvMB = Math.Round(wv.Sum(p => { try { return p.WorkingSet64; } catch { return 0L; } }) / MB, 1);
                foreach (var p in wv) p.Dispose();
            }
            catch { /* best-effort */ }

            double uptimeSec = 0;
            try { uptimeSec = Math.Round((DateTime.Now - proc.StartTime).TotalSeconds, 0); } catch { }

            return new MemorySnapshot(
                TimestampUtc: DateTime.UtcNow.ToString("o"),
                MachineName: Environment.MachineName,
                ProcessId: proc.Id,
                AppVersion: typeof(MemoryDiagnosticsService).Assembly.GetName().Version?.ToString() ?? "?",
                UptimeSeconds: uptimeSec,

                WorkingSetMB: Math.Round(proc.WorkingSet64 / MB, 1),
                PrivateMB: Math.Round(proc.PrivateMemorySize64 / MB, 1),
                PagedMB: Math.Round(proc.PagedMemorySize64 / MB, 1),
                VirtualMB: Math.Round(proc.VirtualMemorySize64 / MB, 1),
                ThreadCount: proc.Threads.Count,
                HandleCount: proc.HandleCount,

                ManagedHeapMB: Math.Round(GC.GetTotalMemory(false) / MB, 1),
                TotalAllocatedMB: Math.Round(GC.GetTotalAllocatedBytes(false) / MB, 1),
                GcHeapSizeMB: Math.Round(gc.HeapSizeBytes / MB, 1),
                GcFragmentedMB: Math.Round(gc.FragmentedBytes / MB, 1),
                GcCommittedMB: Math.Round(gc.TotalCommittedBytes / MB, 1),
                MemoryLoadMB: Math.Round(gc.MemoryLoadBytes / MB, 1),
                HighMemoryLoadThresholdMB: Math.Round(gc.HighMemoryLoadThresholdBytes / MB, 1),
                TotalAvailableMB: Math.Round(gc.TotalAvailableMemoryBytes / MB, 1),
                Gen0Collections: GC.CollectionCount(0),
                Gen1Collections: GC.CollectionCount(1),
                Gen2Collections: GC.CollectionCount(2),
                Generations: gens,

                HotTierEntries: (_hotTier as MemoryCache)?.Count,
                IsUnderMemoryPressure: _monitor?.IsUnderPressure ?? false,

                WebView2ProcessCount: wvCount,
                WebView2WorkingSetMB_MachineWide: wvMB);
        }

        /// <summary>Plain-text rendering suitable for a .txt dump or clipboard.</summary>
        public string ToText(MemorySnapshot s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("SQLTriage — Memory Diagnostic Dump");
            sb.AppendLine("===================================");
            sb.AppendLine($"Timestamp (UTC) : {s.TimestampUtc}");
            sb.AppendLine($"Machine         : {s.MachineName}");
            sb.AppendLine($"Process ID      : {s.ProcessId}");
            sb.AppendLine($"App version     : {s.AppVersion}");
            sb.AppendLine($"Uptime          : {TimeSpan.FromSeconds(s.UptimeSeconds):d\\.hh\\:mm\\:ss}");
            sb.AppendLine();
            sb.AppendLine("-- Process (SQLTriage.exe) --");
            sb.AppendLine($"Working set     : {s.WorkingSetMB:N1} MB");
            sb.AppendLine($"Private bytes   : {s.PrivateMB:N1} MB");
            sb.AppendLine($"Paged           : {s.PagedMB:N1} MB");
            sb.AppendLine($"Virtual         : {s.VirtualMB:N1} MB");
            sb.AppendLine($"Threads         : {s.ThreadCount}");
            sb.AppendLine($"Handles         : {s.HandleCount}");
            sb.AppendLine();
            sb.AppendLine("-- Managed GC heap --");
            sb.AppendLine($"Managed heap    : {s.ManagedHeapMB:N1} MB");
            sb.AppendLine($"GC heap size    : {s.GcHeapSizeMB:N1} MB");
            sb.AppendLine($"GC fragmented   : {s.GcFragmentedMB:N1} MB");
            sb.AppendLine($"GC committed    : {s.GcCommittedMB:N1} MB");
            sb.AppendLine($"Total allocated : {s.TotalAllocatedMB:N1} MB (cumulative)");
            sb.AppendLine($"Collections     : Gen0={s.Gen0Collections}  Gen1={s.Gen1Collections}  Gen2={s.Gen2Collections}");
            foreach (var g in s.Generations)
                sb.AppendLine($"  {g.Name,-6}        : {g.SizeMB,8:N1} MB (frag {g.FragmentedMB:N1} MB)");
            sb.AppendLine();
            sb.AppendLine("-- System memory load --");
            sb.AppendLine($"Memory load     : {s.MemoryLoadMB:N1} MB");
            sb.AppendLine($"High threshold  : {s.HighMemoryLoadThresholdMB:N1} MB");
            sb.AppendLine($"Total available : {s.TotalAvailableMB:N1} MB");
            sb.AppendLine($"Under pressure  : {s.IsUnderMemoryPressure}");
            sb.AppendLine();
            sb.AppendLine("-- Cache + WebView2 --");
            sb.AppendLine($"Hot-tier entries: {(s.HotTierEntries.HasValue ? s.HotTierEntries.Value.ToString() : "n/a")}");
            sb.AppendLine($"WebView2 procs  : {s.WebView2ProcessCount} (machine-wide)");
            sb.AppendLine($"WebView2 WS     : {s.WebView2WorkingSetMB_MachineWide:N1} MB (machine-wide — includes other WebView2 apps)");
            return sb.ToString();
        }
    }

    public sealed record GenInfo(string Name, double SizeMB, double FragmentedMB);

    public sealed record MemorySnapshot(
        string TimestampUtc,
        string MachineName,
        int ProcessId,
        string AppVersion,
        double UptimeSeconds,

        double WorkingSetMB,
        double PrivateMB,
        double PagedMB,
        double VirtualMB,
        int ThreadCount,
        int HandleCount,

        double ManagedHeapMB,
        double TotalAllocatedMB,
        double GcHeapSizeMB,
        double GcFragmentedMB,
        double GcCommittedMB,
        double MemoryLoadMB,
        double HighMemoryLoadThresholdMB,
        double TotalAvailableMB,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        IReadOnlyList<GenInfo> Generations,

        int? HotTierEntries,
        bool IsUnderMemoryPressure,

        int WebView2ProcessCount,
        double WebView2WorkingSetMB_MachineWide);
}
