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
            var probe = ProbeWindowsService("SQLTriage");
            var (h, state) = DescribeWindowsService(probe);
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
            else (h, state) = DescribeUpdateCheck(_updater.LastCheckState, _updater.LastCheckError);

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

        // ── Third-party data collectors SQLTriage READS (target SQL instances) ──
        // Unbundled 2026-07-21: SQLTriage no longer installs either of these. The rows stay
        // because this catalog claims to be the single source of truth for every service the
        // app touches, and the app does still read both databases when a DBA has installed
        // them independently — dropping the rows would make the catalog lie. ManageRoute is
        // null so no "Manage →" button renders (Services.razor guards on it being non-empty).
        list.Add(new ServiceCatalogEntry(
            "sqlwatch", "SQLWATCH Collector",
            "Open-source monitoring framework. SQLTriage reads a SQLWATCH database if one is present on the instance; it does not install or deploy one.",
            ServiceLocus.TargetInstance, ServiceHealth.Available,
            "Read-only. Installed and maintained by your DBA, not by SQLTriage.", null, "fa-database"));
        list.Add(new ServiceCatalogEntry(
            "perfmon", "Darling Performance Monitor",
            "Third-party perf collector. SQLTriage reads a PerformanceMonitor database if one is present on the instance; it does not install or deploy one.",
            ServiceLocus.TargetInstance, ServiceHealth.Available,
            "Read-only. Not recommended on production: in our own field use it caused two client outages in two months (memory >2GB, high CPU, a ~600GB database blowout).", null, "fa-gauge-high"));

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

    /// <summary>
    /// pages-r1-06: what the SCM probe actually established. "Not installed" and "we could not
    /// ask" are opposite facts and used to return the same tuple, rendered as the same red
    /// "Not installed (sc create via Service &amp; Updates)." card - so an access-denied or
    /// transient SCM failure told the operator to create a service that already exists.
    /// </summary>
    internal enum WindowsServiceProbeState { Installed, NotInstalled, ProbeFailed }

    internal readonly record struct WindowsServiceProbe(
        WindowsServiceProbeState State, bool Running, string? Account, string? FailureDetail);

    /// <summary>The card's health and words, from the probe alone. Pure, so it is testable.</summary>
    internal static (ServiceHealth Health, string State) DescribeWindowsService(WindowsServiceProbe probe) =>
        probe.State switch
        {
            WindowsServiceProbeState.NotInstalled =>
                (ServiceHealth.NotInstalled, "Not installed (sc create via Service & Updates)."),

            WindowsServiceProbeState.ProbeFailed =>
                (ServiceHealth.Unknown,
                 $"Could not check the service, so whether it is installed is unknown: {probe.FailureDetail}"),

            _ => probe.Running
                ? (ServiceHealth.Running, $"Running{(string.IsNullOrEmpty(probe.Account) ? "" : $" as {probe.Account}")}")
                : (ServiceHealth.Stopped, "Installed but stopped.")
        };

    /// <summary>
    /// pages-r1-02: the update card used to fall through to a green "Up to date (signed)." for
    /// every state that was not a staged or available update - including Updates:Enabled = false,
    /// which is the SHIPPED DEFAULT, and including a check that had not run yet. So a fresh
    /// install asserted a signature verification that never occurred about a release it had never
    /// contacted. AutoUpdateService already records which of the five states it is in; this reads
    /// it, and the words match /service-management's sibling text for the same state.
    /// </summary>
    internal static (ServiceHealth Health, string State) DescribeUpdateCheck(
        UpdateCheckState state, string? lastCheckError) => state switch
    {
        UpdateCheckState.UpToDate =>
            (ServiceHealth.Healthy, "Up to date - this build matched the latest release when it was last checked."),

        UpdateCheckState.Disabled =>
            (ServiceHealth.Unknown,
             "Update checks are turned off in this build's configuration (Updates:Enabled = false). "
             + "Nothing was contacted, so this build's version was not compared against any release."),

        UpdateCheckState.NotChecked =>
            (ServiceHealth.Unknown,
             "No update check has run yet, so this build's version has not been compared against any release."),

        UpdateCheckState.Failed =>
            (ServiceHealth.Unknown,
             $"Update check failed, so this build's version is unknown, not confirmed current: {lastCheckError}"),

        _ => (ServiceHealth.Unknown, "The update check's state was not recorded, so nothing about this build's version is known."),
    };

    /// <summary>
    /// True when the exception proves the service is genuinely absent. ERROR_SERVICE_DOES_NOT_EXIST
    /// (1060) on the inner Win32Exception is the only thing that proves it: ServiceController raises
    /// InvalidOperationException for an SCM-open failure and for an access-denied probe too, so the
    /// exception TYPE cannot carry the distinction. Anything unclassifiable is a failed probe, not
    /// an absent service - fail to "unknown", never to a fact.
    /// </summary>
    internal static bool ProvesServiceAbsent(Exception ex) =>
        ex is InvalidOperationException
        && ex.InnerException is System.ComponentModel.Win32Exception w
        && w.NativeErrorCode == 1060;

    private WindowsServiceProbe ProbeWindowsService(string name)
    {
        if (!OperatingSystem.IsWindows())
            return new WindowsServiceProbe(WindowsServiceProbeState.ProbeFailed, false, null,
                "this platform has no Windows service control manager.");
        try
        {
#pragma warning disable CA1416
            using var sc = new System.ServiceProcess.ServiceController(name);
            var status = sc.Status; // throws InvalidOperationException if the service is not installed
            return new WindowsServiceProbe(
                WindowsServiceProbeState.Installed,
                status == System.ServiceProcess.ServiceControllerStatus.Running, null, null);
#pragma warning restore CA1416
        }
        catch (Exception ex) when (ProvesServiceAbsent(ex))
        {
            _logger.LogDebug(ex, "[ServiceCatalog] service {Name} is not installed", name);
            return new WindowsServiceProbe(WindowsServiceProbeState.NotInstalled, false, null, null);
        }
        catch (Exception ex)
        {
            // Warning, not Debug: this branch used to be the one with no trace at all, and it is
            // the branch where the card cannot say what is true.
            _logger.LogWarning(ex, "[ServiceCatalog] service probe failed for {Name}", name);
            return new WindowsServiceProbe(WindowsServiceProbeState.ProbeFailed, false, null, ex.Message);
        }
    }
}
