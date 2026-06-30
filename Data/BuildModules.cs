/* In the name of God, the Merciful, the Compassionate */
/*
 * BuildModules — compile-time module-presence flags for the Community Edition build profile.
 *
 * The constants are driven by DefineConstants set in buildprofile.targets (which reads
 * buildprofile.json when building with -p:SQLTriageProfile=community). In the full/dev build
 * everything is true. Because these are consts, @if (BuildModules.X) branches in markup are
 * pruned by the compiler when X is false — gated routes and labels never reach the community
 * assembly. Gate every rendered link to a gated module's page on the matching const.
 *
 * See .handoff/ADR-2026-06-11-community-build-gating.md.
 */

namespace SQLTriage.Data;

public static class BuildModules
{
    /// <summary>True when this binary was produced with -p:SQLTriageProfile=community.</summary>
#if SQLT_COMMUNITY
    public const bool Community = true;
#else
    public const bool Community = false;
#endif

    /// <summary>Premium surfaces (Premium, CapacityConsolidation, AdvancedReporting). Absent from community.</summary>
#if SQLT_NO_PREMIUM
    public const bool Premium = false;
#else
    public const bool Premium = true;
#endif

    /// <summary>Dev tools (CorpusEditor, CorpusCsvEditor, CheckValidator, BuildProfile, RemediationTuner, TestPlan, PerfInspector). Never ship.</summary>
#if SQLT_NO_DEVTOOLS
    public const bool DevTools = false;
#else
    public const bool DevTools = true;
#endif

    /// <summary>Operations module (ops hub, alerting, multi-server execution, deploys). Community toggle, default on.</summary>
#if SQLT_NO_OPERATIONS
    public const bool Operations = false;
#else
    public const bool Operations = true;
#endif

    /// <summary>Live-monitoring module (live dashboards, sessions/waits/query monitoring). Community toggle, default off.</summary>
#if SQLT_NO_LIVEMONITORING
    public const bool LiveMonitoring = false;
#else
    public const bool LiveMonitoring = true;
#endif
}

/// <summary>
/// Build-mode visual identity — a subtle at-a-glance tell of which edition is running:
/// grey = community, blue = full, red = DevBridge (full + --devbridge). Used for the
/// window title suffix and the window accent border. DevBridge is a runtime arg, set
/// once at startup via <see cref="MarkDevBridgeActive"/>.
/// </summary>
public static class BuildMode
{
    /// <summary>Set true at startup when --devbridge is on the command line.</summary>
    public static bool DevBridgeActive { get; private set; }
    public static void MarkDevBridgeActive() => DevBridgeActive = true;

    public enum Kind { Community, Full, DevBridge }

    public static Kind Current =>
        DevBridgeActive            ? Kind.DevBridge :
        BuildModules.Community     ? Kind.Community :
                                     Kind.Full;

    /// <summary>Short label appended to the title after the version/build (e.g. "· DEV", "· DEVBRIDGE").
    /// Full build with no DevBridge gets no suffix (the common dev case stays clean); community is tagged.</summary>
    public static string TitleSuffix => Current switch
    {
        Kind.DevBridge => " · DEVBRIDGE",
        Kind.Community  => " · Community",
        _               => " · Full",
    };

    /// <summary>Accent hex for the window border. grey / blue / red.</summary>
    public static string AccentHex => Current switch
    {
        Kind.DevBridge => "#d64550",  // red
        Kind.Community  => "#888888",  // grey
        _               => "#3b82f6",  // blue (full/dev)
    };
}
