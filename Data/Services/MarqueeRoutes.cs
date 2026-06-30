/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;

namespace SQLTriage.Data.Services;

/// <summary>
/// The canonical 7-stop marquee tour — the same set used by:
/// <list type="bullet">
///   <item><see cref="DevBridgeService"/>'s /demo/tour endpoint (for marketing GIF capture)</item>
///   <item>The in-app first-launch <c>WelcomeTourService</c> (for new users)</item>
/// </list>
/// Ordered for narrative flow: hero → audit-first 2-up → monitoring 2-up
/// → benchmark deep-dive → guide closer.
/// </summary>
public sealed record MarqueeStop(string Route, string Title, string Narration);

public static class MarqueeRoutes
{
    public static readonly IReadOnlyList<MarqueeStop> Default = new[]
    {
        new MarqueeStop(
            "/cio",
            "CIO Dashboard",
            "Hero view — compliance score, risk posture, and remediation cost across every monitored server. The shortest path from 'how are we doing?' to a board-ready answer."),

        new MarqueeStop(
            "/diagnostics-roadmap",
            "Compliance Roadmap",
            "5-level maturity bands with per-check status. Shows the next concrete step at each level, not just a score."),

        new MarqueeStop(
            "/audit",
            "Audit Assessment",
            "Run 700+ checks across all servers in parallel. Findings merge live, framework-mapped to CIS / STIG / NIST."),

        new MarqueeStop(
            "/code-hotspots",
            "Code Hotspots",
            "Drive cost analysis by query — encrypted SQLite snapshots every 5 minutes, then toggle Delta vs Cumulative to see what's changing now vs all-time."),

        new MarqueeStop(
            "/disk-io",
            "Disk I/O",
            "Per-drive treemap with file nodes sized by file size, coloured by database, heat-tinted by latency. Tempdb files dashed; log files striped."),

        new MarqueeStop(
            "/benchmark",
            "CPU & Latency Benchmark",
            "Hardware / hypervisor diagnostics across all servers. Scheduler-delay and signal-wait reveal vCPU steal that pure DMV stats miss."),

        new MarqueeStop(
            "/guide",
            "Navigation Map",
            "Quick reference — how the surfaces connect, and what to look at next.")
    };
}
