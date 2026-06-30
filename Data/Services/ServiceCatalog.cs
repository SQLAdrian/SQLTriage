/* In the name of God, the Merciful, the Compassionate */
/*
 * ServiceCatalog — a single registry of every "service / scenario" the platform can run:
 * where each one lives (in-app process, headless Windows service, on a target SQL instance,
 * or a portable agent) and its current state/health. Powers the Services page.
 *
 * Read-only and cheap: it introspects already-running singletons and probes the Windows
 * service via ServiceController. No new background work.
 */

#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services.Capacity;

namespace SQLTriage.Data.Services;

public enum ServiceLocus { InAppProcess, HeadlessService, TargetInstance, PortableAgent, External }
public enum ServiceHealth { Healthy, Running, Stopped, Degraded, NotInstalled, Available, Planned, Unknown }

public sealed record ServiceCatalogEntry(
    string Id,
    string Name,
    string Description,
    ServiceLocus Locus,
    ServiceHealth Health,
    string StateText,
    string? ManageRoute,
    string Icon);

public sealed class ServiceCatalog
{
    private readonly ILogger<ServiceCatalog> _logger;
    private readonly ConsolidationCollector _collector;
    private readonly AutoUpdateService _updater;
    private readonly ConnectionHealthService _health;

    public ServiceCatalog(
        ILogger<ServiceCatalog> logger,
        ConsolidationCollector collector,
        AutoUpdateService updater,
        ConnectionHealthService health)
    {
        _logger = logger;
        _collector = collector;
        _updater = updater;
        _health = health;
    }

    public List<ServiceCatalogEntry> GetEntries()
    {
        var list = new List<ServiceCatalogEntry>();

        // ── Consolidation telemetry collector (app + headless) ──
        // Premium module: the /consolidation page is compiled out of community builds,
        // so the catalog entry (and its route link) must not exist there either.
#if !SQLT_NO_PREMIUM
        {
            ServiceHealth h;
            string state;
            if (!_collector.IsLicensed) { h = ServiceHealth.Planned; state = "Premium licence required to run."; }
            else if (_collector.IsEnabled)
            {
                h = ServiceHealth.Running;
                state = _collector.LastRunUtc is { } lr
                    ? $"Every {_collector.CadenceMinutes} min · last sample {lr.ToLocalTime():dd MMM HH:mm} ({_collector.LastSampleCount} server(s))"
                    : $"Every {_collector.CadenceMinutes} min · awaiting first sample";
            }
            else { h = ServiceHealth.Stopped; state = "Opt-in — not started."; }

            list.Add(new ServiceCatalogEntry(
                "consolidation-collector", "Consolidation Telemetry Collector",
                "Samples QS + plan-cache + ring-buffer (metadata-only) into the encrypted store on a timer.",
                ServiceLocus.InAppProcess, h, state, RouteConstants.Consolidation, "fa-satellite-dish"));
        }
#endif

        // ── Headless Windows service ──
        {
            var (installed, running, account) = ProbeWindowsService("SQLTriage");
            var h = !installed ? ServiceHealth.NotInstalled : running ? ServiceHealth.Running : ServiceHealth.Stopped;
            var state = !installed ? "Not installed (sc create via Service & Updates)."
                : running ? $"Running{(string.IsNullOrEmpty(account) ? "" : $" as {account}")}"
                : "Installed but stopped.";
            list.Add(new ServiceCatalogEntry(
                "windows-service", "Headless Windows Service",
                "Runs the full app + background collectors 24/7 without the desktop UI.",
                ServiceLocus.HeadlessService, h, state, RouteConstants.ServiceManagement, "fa-server"));
        }

        // ── Update service ──
        {
            ServiceHealth h; string state;
            if (_updater.HasStagedUpdate) { h = ServiceHealth.Degraded; state = "Update downloaded — applies on next restart."; }
            else if (_updater.IsUpdateAvailable) { h = ServiceHealth.Degraded; state = $"Update available: {_updater.LastCheckResult?.Info?.Version}"; }
            else if (_updater.LastCheckError is { } err) { h = ServiceHealth.Unknown; state = $"Last check failed: {err}"; }
            else { h = ServiceHealth.Healthy; state = "Up to date (signed)."; }

            var externalApplier = System.IO.File.Exists(
                System.IO.Path.Combine(AppContext.BaseDirectory, "updater", "SQLTriageUpdater.exe"));
            state += externalApplier ? " · external applier ready (rollback-safe)" : " · legacy script applier";
            list.Add(new ServiceCatalogEntry(
                "updater", "Update Service",
                "Checks signed GitHub releases, verifies the signature, then applies out-of-process with rollback.",
                externalApplier ? ServiceLocus.External : ServiceLocus.InAppProcess,
                h, state, RouteConstants.ServiceManagement, "fa-cloud-arrow-down"));
        }

        // ── Connection health monitor ──
        list.Add(new ServiceCatalogEntry(
            "connection-health", "Connection Health Monitor",
            "Polls every enabled instance for reachability on a timer.",
            ServiceLocus.InAppProcess, ServiceHealth.Running,
            $"{_health.OnlineCount} online · {_health.OfflineCount} offline", RouteConstants.Servers, "fa-heart-pulse"));

        // ── Deployable data collectors (target SQL instances) ──
        list.Add(new ServiceCatalogEntry(
            "sqlwatch", "SQLWATCH Collector",
            "Open-source monitoring framework deployed into a SQLWATCH database on a target instance.",
            ServiceLocus.TargetInstance, ServiceHealth.Available,
            "Deploy per instance.", RouteConstants.DeploySqlWatch, "fa-database"));
        list.Add(new ServiceCatalogEntry(
            "perfmon", "Darling Performance Monitor",
            "Lightweight perf collector deployed into a PerformanceMonitor database on a target instance.",
            ServiceLocus.TargetInstance, ServiceHealth.Available,
            "Deploy per instance.", RouteConstants.DeployDarlingPm, "fa-gauge-high"));

        // ── Planned / roadmap scenarios (surfaced so the catalog is the single source of truth) ──
        list.Add(new ServiceCatalogEntry(
            "portable-collector", "Portable Collector Agent",
            "Packaged from the app, dropped on a server, collects autonomously into an encrypted bundle only the spawning app can open.",
            ServiceLocus.PortableAgent, ServiceHealth.Planned, "Design — not yet built.", null, "fa-box"));
        list.Add(new ServiceCatalogEntry(
            "collector-service", "Dedicated Collector Service",
            "Lean 'run as collector service on this machine' install (no web UI) with a guided setup wizard.",
            ServiceLocus.HeadlessService, ServiceHealth.Planned, "Design — not yet built.", null, "fa-gears"));

        return list;
    }

    private (bool installed, bool running, string? account) ProbeWindowsService(string name)
    {
        if (!OperatingSystem.IsWindows()) return (false, false, null);
        try
        {
#pragma warning disable CA1416
            using var sc = new System.ServiceProcess.ServiceController(name);
            var status = sc.Status; // throws InvalidOperationException if the service is not installed
            return (true, status == System.ServiceProcess.ServiceControllerStatus.Running, null);
#pragma warning restore CA1416
        }
        catch (InvalidOperationException) { return (false, false, null); }
        catch (Exception ex) { _logger.LogDebug(ex, "[ServiceCatalog] service probe failed for {Name}", name); return (false, false, null); }
    }
}
