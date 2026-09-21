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

    /// <summary>Dev tools (CorpusEditor, CorpusCsvEditor, CheckValidator, BuildProfile, RemediationTuner, TestPlan, PerfLoadWaterfall). Never ship.</summary>
#if SQLT_NO_DEVTOOLS
    public const bool DevTools = false;
#else
    public const bool DevTools = true;
#endif

    /// <summary>Portal publish surface (hosted/commercial publish-to-portal feature). Absent from community.</summary>
#if SQLT_NO_PORTAL
    public const bool Portal = false;
#else
    public const bool Portal = true;
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

    /// <summary>Performance Analysis Report — Adrian's full-PRIVATE-build-only consulting deliverable
    /// (SPEC 2026-07-16). Unlike every other flag here it is fail-closed OPT-IN: false in EVERY build
    /// (community, paid/client, and a plain full build) UNLESS the binary was produced with
    /// -p:SQLTriagePrivate=true. Gate every rendered link + route to it on this const.</summary>
#if SQLT_PERFREPORT
    public const bool PerfReport = true;
#else
    public const bool PerfReport = false;
#endif

    /// <summary>
    /// Per-report presence flags for the Community Edition (Adrian's ruling 2026-07-21). Each
    /// generated report/PDF deliverable is one const, driven by an SQLT_NO_REPORT_&lt;X&gt; define
    /// set in buildprofile.targets from the "reports" section of buildprofile.json (fail-closed:
    /// a report is present ONLY when its key is exactly "on"; anything else is compiled out).
    ///
    /// Because these are compile-time consts, an @if (BuildModules.Reports.X) branch in markup is
    /// pruned by the compiler when X is false — the trigger button, its handler, and the builder
    /// method's call site never reach the community assembly. Gate every report-export button on
    /// the matching const, and #if-fence the builder method + call site on the matching define.
    ///
    /// Community KEEPS exactly Audit Evidence, Risk Register, Risk Acknowledgement (the three
    /// compliance instruments Adrian named) plus the compliance-map "Export Evidence" report —
    /// those are never gated, so they carry no define and are simply true.
    /// </summary>
    public static class Reports
    {
        // ── Community KEEPERS — never gated, always compiled. ──
        public const bool AuditEvidence = true;
        public const bool RiskRegister = true;
        public const bool RiskAcknowledgement = true;

        // Was the fourth keeper; Adrian ruled it GATED 2026-07-21 ("three keepers only").
#if SQLT_NO_REPORT_COMPLIANCE_REPORT
        public const bool ComplianceReport = false;
#else
        public const bool ComplianceReport = true;
#endif

        // ── Gated deliverables — false in a community build that did not opt them back in. ──
#if SQLT_NO_REPORT_EXEC_SUMMARY
        public const bool ExecutiveSummary = false;
#else
        public const bool ExecutiveSummary = true;
#endif

#if SQLT_NO_REPORT_DBA_HANDOFF
        public const bool DbaHandoff = false;
#else
        public const bool DbaHandoff = true;
#endif

#if SQLT_NO_REPORT_HADR_POSTURE
        public const bool HaDrPosture = false;
#else
        public const bool HaDrPosture = true;
#endif

#if SQLT_NO_REPORT_FINDINGS_PDF
        public const bool FindingsPdf = false;
#else
        public const bool FindingsPdf = true;
#endif

#if SQLT_NO_REPORT_EXEC_BRIEFING
        public const bool ExecutiveBriefing = false;
#else
        public const bool ExecutiveBriefing = true;
#endif

#if SQLT_NO_REPORT_CIO_EXEC
        public const bool CioExecutive = false;
#else
        public const bool CioExecutive = true;
#endif

#if SQLT_NO_REPORT_ROADMAP
        public const bool DiagnosticsRoadmap = false;
#else
        public const bool DiagnosticsRoadmap = true;
#endif
    }
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
