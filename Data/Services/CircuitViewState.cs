/* In the name of God, the Merciful, the Compassionate */

/*
 * PER-CIRCUIT view state for the three run pages.
 *
 * WHY THIS FILE EXISTS (round 5, 2026-08-02)
 * ------------------------------------------
 * QuickCheckStateService, VulnerabilityAssessmentStateService and FullAuditStateService are
 * SINGLETONS, and each of them used to carry two different kinds of state under one roof:
 *
 *   (a) what the estate's last scan FOUND — Results, summaries, assessed servers, the
 *       IsRunning / progress of the run itself; and
 *   (b) what THIS OPERATOR is looking at — which connection and which servers are selected,
 *       and which filters are applied.
 *
 * (a) is deliberately process-wide and stays where it is. It is the estate's scan, not the
 * viewer's: the cold-start rehydrate (AssessmentRehydrationService) restores it for everybody,
 * the report composers (ExecutiveHealthService, ReportBundleService) read it as singletons,
 * /compliance-map, /compliance-tree and /compliance-board render it, NavMenu shows IsRunning as
 * "the estate is being scanned", and ServerModeService.RegisterSharedSingletons forwards these
 * instances from the WPF container ON PURPOSE so the operator's desktop window and their browser
 * are one session. IsRunning in particular is a mutual-exclusion flag protecting production
 * servers from two simultaneous estate scans — per-circuit copies of it would remove that.
 *
 * (b) is NOT safe process-wide once the host is network-reachable, and it is not a cosmetic
 * problem: SelectedConnectionId / SelectedServer / SelectedServerNames are the TARGET of a
 * privileged action. Sharing them lets one caller retarget another caller's run — a caller who
 * cannot run anything themselves can still choose which production instance the operator's next
 * Run hits. Scoping them means the target of a run is chosen by the circuit that starts it.
 *
 * The split line is the one the code already drew for itself: every service carried a
 * "UI preference state (survives navigation)" block, and that block is what moved here.
 *
 * DiLifetimeCensusTests holds the line — the singletons that remain are on a declared register
 * with these reasons, and a NEW singleton carrying this shape fails the build.
 */

#nullable enable

using System;
using System.Collections.Generic;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    // BM:QuickCheckViewState.Class — per-circuit selection + filters for /audit
    /// <summary>
    /// Scoped. The /audit page's own selection and filters. Registered AddScoped; see the file
    /// header for why this is a boundary rather than a preference.
    /// </summary>
    public class QuickCheckViewState
    {
        /// <summary>Run against every enabled connection rather than <see cref="SelectedConnectionId"/>.</summary>
        public bool RunAllServers { get; set; } = true;

        /// <summary>The connection this circuit's next run targets.</summary>
        public string SelectedConnectionId { get; set; } = string.Empty;

        public string SelectedFilter { get; set; } = "all";
        public string SelectedCategory { get; set; } = string.Empty;
        public string SelectedServer { get; set; } = string.Empty;
        public string SelectedSeverity { get; set; } = string.Empty;
    }

    // BM:VulnerabilityAssessmentViewState.Class — per-circuit selection + filters for /vulnerabilityassessment
    /// <summary>
    /// Scoped. The Vulnerability Assessment page's own selection, scope and filters, plus the
    /// filtered projection of the shared results (a view, so it belongs to the viewer).
    /// </summary>
    public class VulnerabilityAssessmentViewState
    {
        public bool RunAllServers { get; set; }

        /// <summary>The connection this circuit's next assessment targets.</summary>
        public string? SelectedConnectionId { get; set; }

        /// <summary>The server names this circuit's next assessment targets.</summary>
        public List<string> SelectedServerNames { get; set; } = new();

        public string SearchFilter { get; set; } = string.Empty;
        public string SeverityFilter { get; set; } = "All";
        public string StatusFilter { get; set; } = "All";
        public string CategoryFilter { get; set; } = "All";

        /// <summary>Multiselect category filter — empty means "all categories".</summary>
        public HashSet<string> SelectedCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Pre-run scope: only include results whose Category matches one of these tags.
        /// Empty = run all checks (default).
        /// </summary>
        public HashSet<string> ScopeTagFilter { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>This circuit's filtered projection of the shared result set.</summary>
        public List<AssessmentResult> FilteredResults { get; set; } = new();
    }

    // BM:FullAuditViewState.Class — per-circuit selection for /fullaudit
    /// <summary>
    /// Scoped. The Raw Diagnostics page's own target selection.
    /// </summary>
    public class FullAuditViewState
    {
        /// <summary>The connection this circuit's next diagnostic run targets.</summary>
        public string? SelectedConnectionId { get; set; }

        /// <summary>The server this circuit's next diagnostic run targets.</summary>
        public string? SelectedServer { get; set; }
    }
}
