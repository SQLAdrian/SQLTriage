/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// A census of every NON-ROUTABLE component — the class of surface the page census is
    /// structurally blind to.
    ///
    /// <para><b>Why this exists.</b> <see cref="RbacPageGateCensusTests"/> enumerates
    /// <c>@page</c> directives. A shared component has no route, so it has no <c>@page</c>, so the
    /// page census cannot see it however carefully it is written. On 2026-08-02 a round-4 gate
    /// walked in through exactly that gap: <c>Components/Shared/ServerModeToggle.razor</c> is
    /// rendered by <c>StatusBar</c>, <c>StatusBar</c> is part of <c>MainLayout</c>, and so a
    /// control that STARTS AND STOPS SERVER MODE appeared on every route in the app — including
    /// the AccessDenied pages rounds 1–3 had just put in front of the gated routes. A caller who
    /// was correctly denied /scheduled-tasks was handed, on the denial page itself, a button that
    /// could stop the server.</para>
    ///
    /// <para>Enumerating the class rather than fixing the one component is the point. The scan
    /// below found four more writers hiding in <c>Components/</c> with no gate of any kind:
    /// <c>DynamicDashboard</c> (<c>SqlCommand.ExecuteNonQueryAsync</c> of an action cell's DDL,
    /// with a "query" double-hop mode where the cell VALUE is a SELECT returning the DDL, plus
    /// <c>EXEC msdb.dbo.sp_start_job</c>), <c>QueryPlanModal</c> (CREATE INDEX against the instance
    /// the plan came from), <c>PanelEditorModal</c> (runs the T-SQL the caller typed, and a
    /// <c>SELECT TOP 1 * FROM (&lt;caller sql&gt;)</c> fallback), and <c>ConnectionDialog</c>
    /// (writes a server, credentials and all, into this install's catalogue). None of those five is
    /// reachable by URL; all five were reachable by a caller looking at a page.</para>
    ///
    /// <para><b>How the allow-lists are kept honest.</b> Round 2's page census carried a list
    /// described as "reviewed … by the mutating calls each file makes" when nobody had done that,
    /// and round 3 found nine writers on it. So no list here is trusted on its word:</para>
    /// <list type="bullet">
    /// <item><see cref="Edges"/> — one entry PER EDGE, not per component, so an excuse cannot
    ///   spread from the call it was written for to the next call somebody adds. An entry naming a
    ///   permission is asserted against the shipped markup: the file must declare
    ///   <c>UserState.IsAuthorized("&lt;permission&gt;")</c> behind a named <c>May…</c> property
    ///   and READ it. An entry with a null permission has to say what the call actually is.</item>
    /// <item><see cref="InertInteractiveComponents"/> — components with interactive controls and no
    ///   edge at all. Re-derived mechanically on every run by
    ///   <see cref="TheInertListIsActuallyInert"/>; the prose justification says what the component
    ///   IS, and the scan is what makes the claim true.</item>
    /// </list>
    ///
    /// <para>The three-list shape (gated / reviewed-acting / inert) collapsed into these two on
    /// 2026-08-06. "Gated" is no longer a list a human maintains: it is derived from the register
    /// entries that name a permission, so a component cannot be described as gated in one place
    /// while an unregistered call sits in it.</para>
    ///
    /// <para><b>THE INSTRUMENT, upgraded 2026-08-06.</b> This census used to ask
    /// <see cref="RbacPageGateCensusTests.MutatingCalls"/> — a 29-verb lexicon — whether a call
    /// looked like a mutation. That question is unanswerable by a word list, and this lane proved
    /// it five times: the lexicon has no bare <c>Set</c>, no <c>Toggle</c>, no <c>Update</c>, and
    /// a call-shaped regex cannot match a property write at all. It now asks
    /// <see cref="ShellSurfaceRegistry.EdgesIn"/> instead — every call and every property
    /// assignment on an injected service, whatever it is named — and an edge that is not on
    /// <see cref="Edges"/> FAILS. Unknown is red.</para>
    ///
    /// <para>The lexicon was not thrown away; it was narrowed to where the edge scanner is blind.
    /// The edge scanner only sees receivers it can resolve to an injected type, so a call on a
    /// local <c>SqlCommand</c> / <c>SqlConnection</c> / <c>Process</c> is invisible to it — and
    /// those are exactly the calls that gated DynamicDashboard, QueryPlanModal and PanelEditorModal
    /// in the first place. See <see cref="ActingCallsIn"/>.</para>
    ///
    /// <para><b>What the upgrade caught on its first run</b>, none of it visible to the lexicon:
    /// the two ungated selectors in <c>DynamicDashboard</c> that retarget the process-wide
    /// connection (<c>SetCurrentServer</c>, and <c>CurrentServer.Database=</c> — a write THROUGH a
    /// returned object that no shape in the scanner could see until this round added one);
    /// <c>DashboardConfigService.UpdateConfig</c>, an install-wide layout write interlocked only by
    /// a local UI preference this file's own comment calls "not an authorization check";
    /// <c>ActivateFullAuditCard</c> and <c>AdminGuard</c>, both recorded as INERT — "makes no
    /// acting call" — while calling <c>LicenseService.TryActivate/Deactivate</c> and
    /// <c>AdminAuthService.SetInitialPassword/Unlock</c> respectively. The same false-justification
    /// shape as the DashboardToolbar contradiction that created ShellSurfaceRegistry.</para>
    ///
    /// <para><b>What this file no longer owns, since 2026-08-02.</b> The ALWAYS-RENDERED SHELL —
    /// MainLayout, NavMenu, StatusBar, DashboardToolbar, and everything MainLayout renders
    /// transitively — moved to <see cref="ShellSurfaceRegistry"/>. Two reasons, both structural.
    /// First, the INSTRUMENT: this census asks whether a call matches an acting verb, and a cold
    /// gate defeated that by adding <c>UserSettings.SetAnonymiseServerNames(false)</c> to
    /// MainLayout — a bare <c>Set</c>, invisible to the lexicon, live from the LAN denial page,
    /// census green at 42 passed. The shell census asks instead whether EVERY edge is on the
    /// record, so an unrecognised call fails rather than passes. Second, the CONTRADICTION: round
    /// 5's commit put DashboardToolbar on the INERT list ("makes no acting call") while recording
    /// a DashboardToolbar acting edge on <c>DiLifetimeCensusTests.ReviewedLayoutEdges</c>. One
    /// register now serves both censuses, and a shell component may not appear on these lists at
    /// all.</para>
    /// </summary>
    public class RbacComponentGateCensusTests
    {
        /// <summary>
        /// A reviewed component edge. Same contract as
        /// <see cref="ShellSurfaceRegistry.ReviewedEdge"/>, deliberately: one shape for both
        /// censuses so a reader does not have to hold two conventions.
        /// <paramref name="Permission"/> non-null means the component must PROVE the gate —
        /// declare <c>private bool MayXxx =&gt; UserState.IsAuthorized("&lt;permission&gt;")</c>
        /// and read it. Null means the edge is deliberately ungated and
        /// <paramref name="Reason"/> has to say what the call actually is.
        /// </summary>
        internal sealed record ReviewedComponentEdge(string? Permission, string Reason);

        /// <summary>
        /// THE REGISTER. Key format <c>"&lt;relative path&gt; → &lt;Type&gt;.&lt;Member&gt;"</c>,
        /// with a trailing <c>=</c> for a property assignment, matching
        /// <see cref="ShellSurfaceRegistry.Edges"/>. Derived mechanically on 2026-08-06 from what
        /// each file actually calls — every entry below was read at its call site, not inferred
        /// from the member's name.
        ///
        /// <para>Reads are on the register beside writes. That is the cost of asking nothing about
        /// the member's name, and it is the point: an entry costs one line, and a false negative
        /// has cost this lane five rounds.</para>
        /// </summary>
        private static readonly Dictionary<string, ReviewedComponentEdge> Edges =
            new(StringComparer.Ordinal)
            {
                // ══ Components/DevTools/PerfLoadWaterfall.razor ═══════════════════════════════

                ["Components/DevTools/PerfLoadWaterfall.razor → PerformanceInspectorService.GetRecentTraces"] =
                    new(null, "Read: the in-memory panel-timing ring buffer, rendered as a waterfall."),
                ["Components/DevTools/PerfLoadWaterfall.razor → PerformanceInspectorService.GetLastLoadTraces"] =
                    new(null, "Read: the same buffer, filtered to the most recent dashboard load."),

                // ══ Components/Shared/ActivateFullAuditCard.razor ═════════════════════════════
                //
                // ⚠ IT WAS ON THE INERT LIST as "upsell card; navigates" — a false claim the verb
                // lexicon could not contradict, because neither "Activate" nor "Deactivate" is one
                // of its 29 verbs. It activates and deactivates the LICENCE.

                ["Components/Shared/ActivateFullAuditCard.razor → LicenseService.TryActivate"] =
                    new(null,
                        "Activates a Full-tier licence from a customer name + 24-word key. UNGATED at "
                        + "the component, and reached from exactly one place: Pages/Settings.razor, "
                        + "which wraps its whole body in IsAuthorizedWithBreakGlass(\"settings\"). "
                        + "ActivateFullAuditCardIsReachedOnlyFromTheGatedSettingsPage pins both halves "
                        + "of that claim so it cannot rot into prose. ⚠ OPEN, NOT DECIDED HERE: "
                        + "break-glass means this control stays reachable when the RBAC store is "
                        + "unusable, which is right for Settings and is a distinct question for a "
                        + "LICENCE surface. That question belongs to the unmerged licence-binding "
                        + "wave, which owns Data/Services/Licensing/**, and is Adrian's to rule."),
                ["Components/Shared/ActivateFullAuditCard.razor → LicenseService.Deactivate"] =
                    new(null, "Reverts this install to the Free tier. Same surface, same host, same open question."),
                ["Components/Shared/ActivateFullAuditCard.razor → LicenseService.InstallBundleFile"] =
                    new("settings",
                        "Copies a .aesgcm the caller names into the install folder, after checking the "
                        + "bytes are a bundle. It WRITES, so it is on the record as a write: the caller "
                        + "chooses a source path on the server's filesystem and a file appears next to "
                        + "the executable. GATED at the component since SEC-3 (DECISIONS 2026-08-26 "
                        + "00:20) behind MayInstallBundle, which reads UserState.IsAuthorized(\"settings\") "
                        + "&& UserState.IsAdmin — an AUTHENTICATED admin. The && IsAdmin is the whole SEC-3 "
                        + "change: plain IsAuthorized(\"settings\") still admits the unconfigured-install "
                        + "bootstrap caller (an anonymous loopback circuit, Role=Viewer, granted settings "
                        + "by the hatch), and IsAdmin is the one term that closes that cell without a "
                        + "second permission call. The click handler refuses on the same property before "
                        + "any path reaches the service, so the gate is the DECISION, not the render. The "
                        + "destination file name comes from the source path's own file name and the "
                        + "destination folder is AppContext.BaseDirectory, so no caller-supplied string "
                        + "chooses WHERE. An existing file is never replaced (overwrite: false, and a "
                        + "name already in the install folder is refused), so this cannot drop a working "
                        + "install back to Free — that arm was live until 2026-08-25. ⚠ THE READ IS A "
                        + "PROBE: three distinct replies (no file at that path / could not read it, "
                        + "carrying the OS message / refused on shape) answer 'does this file exist and "
                        + "can the service account read it' for any path on the box — which is why SEC-3 "
                        + "put it behind an authenticated admin rather than the break-glass hatch. The "
                        + "read is also bounded: capped at LicenseService.MaxBundleFileBytes, header only. "
                        + "⚠ SEC-2 (same review): a non-local path is refused before any File.* touches "
                        + "it, so a UNC source cannot make the service account authenticate outbound to a "
                        + "named SMB host. Accepted cost of the SEC-3 gate: an unconfigured install cannot "
                        + "load a licence from the UI until first-admin is created via the admin-access flow."),
                ["Components/Shared/ActivateFullAuditCard.razor → AppUserState.IsAuthorized"] =
                    new(null, "The gate itself (MayInstallBundle) — the SEC-3 authorization decision, "
                        + "not a surface behind one."),
                ["Components/Shared/ActivateFullAuditCard.razor → IBundleAccessor.BundleStateChanged"] =
                    new(null, "Event subscribe/unsubscribe so the card re-renders when the bundle unlocks."),
                ["Components/Shared/ActivateFullAuditCard.razor → ToastService.ShowSuccess"] =
                    new(null,
                        "\"Full Audit activated.\" ToastService is a singleton, so the toast is raised in "
                        + "every open circuit — cosmetic, and it carries no key material."),
                ["Components/Shared/ActivateFullAuditCard.razor → ToastService.ShowError"] =
                    new(null, "The activation-failure message, carrying LicenseService's own ErrorMessage."),
                ["Components/Shared/ActivateFullAuditCard.razor → ToastService.ShowInfo"] =
                    new(null, "\"Deactivated — reverted to Free tier.\""),

                // ══ Components/Shared/AdminGuard.razor ════════════════════════════════════════
                //
                // ⚠ ALSO ON THE INERT LIST, and its prose already described these two calls
                // correctly while the structural claim ("makes no acting call") was false. The
                // lexicon could not see SetInitialPassword or Unlock either.

                ["Components/Shared/AdminGuard.razor → AdminAuthService.Unlock"] =
                    new(null,
                        "This component IS the legacy admin-password gate; Unlock is the act of "
                        + "passing it. A gate cannot be a surface behind itself. AdminAuthService "
                        + "carries its own attempt counter and lockout."),
                ["Components/Shared/AdminGuard.razor → AdminAuthService.SetInitialPassword"] =
                    new(null,
                        "First-run enrolment on the same gate, reachable only while no password is set. "
                        + "Putting an RBAC permission in front of it would make the fallback gate depend "
                        + "on the store it exists to survive."),

                // ══ Components/Shared/BaselineSeedingProgress.razor ═══════════════════════════

                ["Components/Shared/BaselineSeedingProgress.razor → AlertBaselineService.OnProgressChanged"] =
                    new(null, "Event subscribe/unsubscribe to render seeding progress. No write."),

                // ══ Components/Shared/ConnectionDialog.razor ══════════════════════════════════

                ["Components/Shared/ConnectionDialog.razor → ServerConnectionManager.AddConnection"] =
                    new("manage_servers",
                        "Writes a server into this install's catalogue, credentials and all — the same "
                        + "call round 3 gated on Pages/EnvironmentView.razor, same permission."),
                ["Components/Shared/ConnectionDialog.razor → ServerConnectionManager.UpdateConnection"] =
                    new("manage_servers",
                        "The edit half of the same Save(), inside the same `if (!MayManageServers) return;`. "
                        + "The old lexicon had no Update in it, so this one call was invisible while its "
                        + "twin was the reason the file was gated."),
                ["Components/Shared/ConnectionDialog.razor → AppUserState.IsAuthorized"] =
                    new(null, "The gate itself (MayManageServers)."),
                ["Components/Shared/ConnectionDialog.razor → ILogger<ConnectionDialog>.LogError"] =
                    new(null, "Failure lines for the connection test and the save."),
                ["Components/Shared/ConnectionDialog.razor → connection.CreateCommand"] =
                    new(null,
                        "A local SqlConnection in the TEST button's probe, not an injected service — "
                        + "seen by the verb lexicon, invisible to the edge scanner. Creates the command "
                        + "for the SELECT @@VERSION below."),
                ["Components/Shared/ConnectionDialog.razor → command.ExecuteScalar"] =
                    new(null, "That same probe's SELECT @@VERSION. A read, against a server the caller typed."),
                // Newly visible 2026-08-10, when round 8 of the page census added "Set" to the verb
                // lexicon. Connection is a [Parameter] rather than an injected service, so the edge
                // scanner never saw it and the lexicon had no verb for it: the one shape BOTH
                // instruments were blind to at once.
                ["Components/Shared/ConnectionDialog.razor → Connection.SetPassword"] =
                    new(null,
                        "Writes the typed password onto the ServerConnection MODEL this dialog is "
                        + "editing, from the password field's own onchange. It persists nothing by "
                        + "itself: the dialog's Save is what calls ServerConnectionManager.Add/Update"
                        + "Connection, and those two are the entries above. The dialog is opened from "
                        + "Pages/Servers.razor, which wraps its whole body in "
                        + "IsAuthorized(\"manage_servers\"). Ungated at the component for the same "
                        + "reason the two persisting calls are."),

                // ══ Components/Shared/DbMissingBanner.razor ═══════════════════════════════════

                ["Components/Shared/DbMissingBanner.razor → DatabaseAvailabilityService.DatabaseExistsAsync"] =
                    new(null, "Read: does the named database exist on the current connection."),
                ["Components/Shared/DbMissingBanner.razor → DatabaseAvailabilityService.DatabaseExistsOnServerAsync"] =
                    new(null, "Read: the same question against an explicitly named server."),
                ["Components/Shared/DbMissingBanner.razor → UserSettingsService.GetFastAppLoad"] =
                    new(null, "Read: the fast-load preference, which only decides whether to yield before probing."),

                // ══ Chart components — one shared theme event, three subscribers ══════════════

                ["Components/Shared/DonutChart.razor → IChartThemeService.OnChartThemeChanged"] =
                    new(null, "Event subscribe/unsubscribe so the chart repaints on a theme change."),
                ["Components/Shared/HorizontalBarChart.razor → IChartThemeService.OnChartThemeChanged"] =
                    new(null, "Same subscription, same purpose."),
                ["Components/Shared/TimeSeriesChart.razor → IChartThemeService.OnChartThemeChanged"] =
                    new(null, "Same subscription, same purpose."),

                // ══ Components/Shared/DynamicDashboard.razor ══════════════════════════════════
                //
                // The largest surface in this census and the one the upgrade was aimed at. Three
                // separate gaps here, all invisible to the verb lexicon.

                ["Components/Shared/DynamicDashboard.razor → cmd.ExecuteNonQueryAsync"] =
                    new("run_scripts",
                        "ExecuteActionSqlAsync — a panel action cell's DDL, with a \"query\" double-hop "
                        + "mode where the cell VALUE is a SELECT that returns the DDL to run. A local "
                        + "SqlCommand, so only the verb lexicon can see it; this is why the lexicon "
                        + "was kept rather than replaced."),
                ["Components/Shared/DynamicDashboard.razor → conn.CreateCommand"] =
                    new("run_scripts", "The command behind that same gated action path."),
                ["Components/Shared/DynamicDashboard.razor → queryCmd.ExecuteScalarAsync"] =
                    new("run_scripts", "The double-hop read that produces the DDL the line above then executes."),
                ["Components/Shared/DynamicDashboard.razor → ServerConnectionManager.SetCurrentServer"] =
                    new("run_scripts",
                        "Retargets the server this whole process is connected to; "
                        + "ServerConnectionManager is a SINGLETON, so the retarget is not scoped to "
                        + "the caller's view. THREE ROUNDS, and the first two are why this entry no "
                        + "longer names a method as the chokepoint. Round 1 gated the two selector "
                        + "HANDLERS; round 2 found the INIT PATH open and moved the guard into "
                        + "SetServerContext, and THIS ENTRY then claimed that method was \"the single "
                        + "point all five call sites pass through\" — which round 3 falsified with a "
                        + "sixth call site sixty lines below, inside the discovery loop, ungated, on "
                        + "the path every dashboard load takes. It survived because a ONE-CONNECTION "
                        + "install cannot show it: the loop writes the same id back and the defect is "
                        + "a no-op until a second connection exists. The chokepoint is now the "
                        + "SIGNATURE — SetCurrentServer takes a ConnectionRetargetGrant and "
                        + "default(grant) is a refusal — so a call site added tomorrow does not "
                        + "compile until it says who is asking. The discovery write is gone entirely; "
                        + "the two remaining calls are in SetServerContext, behind this permission, "
                        + "and the caller is told their view is following the process's connection "
                        + "rather than setting it. Exercised, not just read: "
                        + "ConnectionRetargetChokepointTests."),
                ["Components/Shared/DynamicDashboard.razor → SqlWatchInstanceDiscovery.DiscoverAsync"] =
                    new(null,
                        "Read: the instance dropdown's SQLWATCH probe over every enabled connection. "
                        + "It replaced an in-component loop whose per-connection SetCurrentServer "
                        + "write was the fifth ungated write of the connection edge; the class is "
                        + "handed a probe that takes the connection as an ARGUMENT and touches no "
                        + "process-wide state, and it lives outside the .razor file precisely so a "
                        + "test can RUN it against two connections and ask what it moved."),
                ["Components/Shared/DynamicDashboard.razor → GlobalInstanceSelector.SetSelectedInstance"] =
                    new("run_scripts",
                        "The other write in the same handler, also a singleton, also undiscussed before "
                        + "this round. Behind the same refusal — and since 2026-08-07 behind the same "
                        + "COMPILE-ENFORCED grant as SetCurrentServer, because this is the STRONGER of "
                        + "the two process-wide connection edges: GetCurrentConnectionString reads it "
                        + "before it reads CurrentServer. Two ungated writers of it were sitting on "
                        + "Pages/Sessions.razor, which no census in this tree opens. Exercised in "
                        + "ConnectionRetargetChokepointTests section 5."),
                ["Components/Shared/DynamicDashboard.razor → ServerConnectionManager.CurrentServer.Database="] =
                    new("run_scripts",
                        "GAP CLOSED 2026-08-06. The Query-Store database selector writes the database "
                        + "through the singleton's CurrentServer object. Invisible to a call-shaped scan "
                        + "AND to a single-hop property-write scan; the chained-write shape was added to "
                        + "ShellSurfaceRegistry.EdgesIn in this round because of this exact line. "
                        + "THREE sites, not one: the selector's handler, and two on the LOAD path "
                        + "(InitializeDashboard's DefaultDatabase write and LoadQueryStoreDatabases' "
                        + "first-database write) which merely opening the dashboard reached. All three "
                        + "are now behind MayExecuteActions."),
                ["Components/Shared/DynamicDashboard.razor → DashboardConfigService.UpdateConfig"] =
                    new("settings",
                        "GAP CLOSED 2026-08-06. Persists the dashboard layout to "
                        + "Config/dashboard-config.json through a singleton — an INSTALL-WIDE write "
                        + "reached from six edit handlers, previously interlocked only by _noPantsMode, "
                        + "which this file's own comment calls a safety interlock and not an "
                        + "authorization check. \"settings\" follows round 6's ruling on the shell's "
                        + "install-wide preference writes."),
                ["Components/Shared/DynamicDashboard.razor → DashboardConfigService.OnConfigChanged"] =
                    new(null, "Event subscribe/unsubscribe so the dashboard rebuilds when the config changes."),
                ["Components/Shared/DynamicDashboard.razor → ServerConnectionManager.CacheDiscoveryResults"] =
                    new(null,
                        "Stores the SQLWATCH instance-name → connection-id map this dashboard just "
                        + "discovered, on the singleton, so the next dashboard does not re-query for it. "
                        + "A cache of derived facts; it adds no connection and writes no file."),
                ["Components/Shared/DynamicDashboard.razor → ServerConnectionManager.GetDiscoveryCache"] =
                    new(null, "The read half of that cache."),
                ["Components/Shared/DynamicDashboard.razor → ServerConnectionManager.GetConnections"] =
                    new(null, "Read: the catalogue, to resolve an instance name to a connection."),
                ["Components/Shared/DynamicDashboard.razor → ServerConnectionManager.GetEnabledConnections"] =
                    new(null, "Read: the enabled subset, to build the instance list."),
                ["Components/Shared/DynamicDashboard.razor → AutoRefreshService.Start"] =
                    new(null,
                        "Starts this dashboard's auto-refresh timer. AutoRefreshService is a singleton "
                        + "and OnRefresh fires in every circuit, so the effect is that dashboards refresh "
                        + "— it issues the same reads the page already issues, on a clock."),
                ["Components/Shared/DynamicDashboard.razor → AutoRefreshService.OnRefresh"] =
                    new(null, "Event subscribe/unsubscribe for that timer."),
                ["Components/Shared/DynamicDashboard.razor → GlobalInstanceSelector.OnInstanceChanged"] =
                    new(null, "Event subscribe/unsubscribe: follow an instance change made elsewhere."),
                ["Components/Shared/DynamicDashboard.razor → CachingQueryExecutor.ExecuteQueryAsync"] =
                    new(null,
                        "Runs a PANEL's query — the SQL that ships in dashboard-config.json, never the "
                        + "caller's. Reading a dashboard is what this component is for; the acting paths "
                        + "are the three gated on run_scripts above."),
                ["Components/Shared/DynamicDashboard.razor → CachingQueryExecutor.PreloadFromCacheAsync"] =
                    new(null, "Fills the panel result dictionaries from cache before the first query goes out."),
                ["Components/Shared/DynamicDashboard.razor → CachingQueryExecutor.PrepareRefreshCycle"] =
                    new(null, "Marks the start of a refresh so the executor can tier its cache decisions."),
                ["Components/Shared/DynamicDashboard.razor → CachingQueryExecutor.ResetMetrics"] =
                    new(null, "Zeroes that executor's per-cycle hit/miss counters. Instrumentation, process-local."),
                ["Components/Shared/DynamicDashboard.razor → CachingQueryExecutor.GetLastTier"] =
                    new(null, "Read: which cache tier answered a panel, for the timing trace."),
                ["Components/Shared/DynamicDashboard.razor → CacheMetricsService.RecordSnapshotAsync"] =
                    new(null, "Records this load's cache hit/miss snapshot for the cache dashboard. Metrics only."),
                ["Components/Shared/DynamicDashboard.razor → IQueryOrchestrator.EnqueueAsync"] =
                    new(null, "Queues a panel query on the shared orchestrator — the throttle in front of the same reads."),
                ["Components/Shared/DynamicDashboard.razor → QueryExecutor.GetSqlWatchInstanceNamesAsync"] =
                    new(null, "Read: SQLWATCH instance names, for the instance dropdown."),
                ["Components/Shared/DynamicDashboard.razor → liveQueriesCacheStore.GetTimeSeriesAsync"] =
                    new(null, "Read: cached baseline series for a panel."),
                ["Components/Shared/DynamicDashboard.razor → liveQueriesCacheStore.GetMetricHistoryAsync"] =
                    new(null,
                        "Read: the retained baseline series for a panel, from metric_history. Same read, "
                        + "same shape and same panel as the cached one directly above it — retention only "
                        + "changes which store answers, never who may ask."),
                ["Components/Shared/DynamicDashboard.razor → CachingQueryExecutor.RetainsHistory"] =
                    new(null,
                        "Read: whether this PANEL opted into retention in dashboard-config.json. A config "
                        + "property of the panel, not a property of the caller."),
                ["Components/Shared/DynamicDashboard.razor → CachingQueryExecutor.GetRetentionCoverageAsync"] =
                    new(null,
                        "Read: how much retained history backs the selected range, so the honesty notice can "
                        + "say a trend is short. Counts rows and reads the oldest bucket; writes nothing."),
                ["Components/Shared/DynamicDashboard.razor → DatabaseAvailabilityService.DatabaseExistsOnServerAsync"] =
                    new(null, "Read: whether the dashboard's RequiresDatabase is present, to render the missing-db banner."),
                ["Components/Shared/DynamicDashboard.razor → PerformanceInspectorService.AddTrace"] =
                    new(null, "Appends one panel-timing record to the in-memory ring buffer PerfLoadWaterfall reads."),
                ["Components/Shared/DynamicDashboard.razor → UserSettingsService.GetNoPantsMode"] =
                    new(null, "Read: the dev-mode preference. It is an interlock in front of gated paths, never a gate."),
                ["Components/Shared/DynamicDashboard.razor → UserSettingsService.OnNoPantsModeChanged"] =
                    new(null, "Event subscribe/unsubscribe for that preference."),
                ["Components/Shared/DynamicDashboard.razor → UserSettingsService.GetDataSource"] =
                    new(null, "Read: the configured data source (native vs SQLWATCH)."),
                ["Components/Shared/DynamicDashboard.razor → UserSettingsService.GetDefaultTimeRange"] =
                    new(null, "Read: the default time window for the panels."),
                ["Components/Shared/DynamicDashboard.razor → UserSettingsService.GetFastAppLoad"] =
                    new(null, "Read: the fast-load preference."),
                ["Components/Shared/DynamicDashboard.razor → AppUserState.IsAuthorized"] =
                    new(null, "The gates themselves (MayExecuteActions / MayEditDashboardLayout)."),
                ["Components/Shared/DynamicDashboard.razor → ILogger<DynamicDashboard>.LogInformation"] =
                    new(null, "Structured log lines, including the two refusal records added this round."),
                ["Components/Shared/DynamicDashboard.razor → ILogger<DynamicDashboard>.LogDebug"] =
                    new(null, "Load/refresh tracing."),
                ["Components/Shared/DynamicDashboard.razor → ILogger<DynamicDashboard>.LogWarning"] =
                    new(null, "Non-fatal panel problems."),
                ["Components/Shared/DynamicDashboard.razor → ILogger<DynamicDashboard>.LogError"] =
                    new(null, "Panel and instance-change failures."),
                ["Components/Shared/DynamicDashboard.razor → LocalLogService.LogInfo"] =
                    new(null, "The same events on the in-app log surface."),
                ["Components/Shared/DynamicDashboard.razor → LocalLogService.LogWarning"] =
                    new(null, "Same surface, warning level."),
                ["Components/Shared/DynamicDashboard.razor → LocalLogService.LogError"] =
                    new(null, "Same surface, error level."),
                ["Components/Shared/DynamicDashboard.razor → _cachedPanelIds.TryRemove"] =
                    new(null,
                        "A local ConcurrentDictionary of panel ids, not an injected service — the verb "
                        + "lexicon matches Remove and the edge scanner correctly does not see it."),

                // ══ Components/Shared/DynamicPanel.razor ══════════════════════════════════════

                ["Components/Shared/DynamicPanel.razor → ILogger<DynamicPanel>.LogDebug"] =
                    new(null, "Per-panel render tracing."),
                ["Components/Shared/DynamicPanel.razor → ILogger<DynamicPanel>.LogWarning"] =
                    new(null, "\"QueryPlanModal is null, cannot show plan\" and siblings."),
                ["Components/Shared/DynamicPanel.razor → ILogger<DynamicPanel>.LogError"] =
                    new(null, "Failure opening the plan modal."),
                ["Components/Shared/DynamicPanel.razor → LocalLogService.LogInfo"] =
                    new(null, "Panel-initialised line on the in-app log surface."),
                ["Components/Shared/DynamicPanel.razor → LocalLogService.LogError"] =
                    new(null, "Plan-modal failure on the same surface."),

                // ══ Components/Shared/HealthBadge.razor ═══════════════════════════════════════

                ["Components/Shared/HealthBadge.razor → ExecutiveHealthService.GetAllHealthScoresAsync"] =
                    new(null, "Read: the estate health scores the badge renders. It is in MainLayout's chrome by rendering, not by location."),
                ["Components/Shared/HealthBadge.razor → UserSettingsService.GetFastAppLoad"] =
                    new(null, "Read: the fast-load preference, which only decides whether to yield first."),

                // ══ Components/Shared/PanelEditorModal.razor ══════════════════════════════════

                ["Components/Shared/PanelEditorModal.razor → sw.Stop"] =
                    new(null, "A local Stopwatch. The verb lexicon matches Stop; there is no service here."),
                ["Components/Shared/PanelEditorModal.razor → trimmed.ImportRow"] =
                    new(null, "A local DataTable, trimming the preview grid to its top rows."),
                ["Components/Shared/PanelEditorModal.razor → AppUserState.IsAuthorized"] =
                    new(null, "The gate itself (MayRunQueries)."),
                ["Components/Shared/PanelEditorModal.razor → ToastService.ShowSuccess"] =
                    new(null, "\"Detected N columns\" after a gated column detection."),
                ["Components/Shared/PanelEditorModal.razor → ToastService.ShowWarning"] =
                    new(null, "\"No server selected\" and \"no columns detected\" for that same path."),

                // ══ Components/Shared/QueryPlanModal.razor ════════════════════════════════════

                ["Components/Shared/QueryPlanModal.razor → cmd.ExecuteNonQueryAsync"] =
                    new("run_scripts",
                        "ExecuteSingleIndex — CREATE NONCLUSTERED INDEX on the instance the plan came "
                        + "from. A local SqlCommand, so the verb lexicon is the instrument that sees it."),
                ["Components/Shared/QueryPlanModal.razor → Process.Start"] =
                    new(null,
                        "Writes the plan XML to %TEMP% and shell-executes the .sqlplan so the OS opens "
                        + "it in Plan Explorer / SSMS. Launches a program on the OPERATOR's own desktop "
                        + "with a file this app just wrote; it touches no SQL Server. ⚠ Not gated, and "
                        + "recorded here rather than assumed away: it is a local-desktop act, and the "
                        + "app is a desktop app whose server mode is a different admission boundary."),
                ["Components/Shared/QueryPlanModal.razor → AppUserState.IsAuthorized"] =
                    new(null, "The gate itself (the run_scripts property guarding ExecuteSingleIndex)."),
                ["Components/Shared/QueryPlanModal.razor → ServerConnectionManager.GetConnection"] =
                    new(null, "Read: the connection the plan came from, to run the gated index creation against it."),
                ["Components/Shared/QueryPlanModal.razor → ServerConnectionManager.GetEnabledConnections"] =
                    new(null, "Read: the enabled catalogue, to resolve that connection."),
                ["Components/Shared/QueryPlanModal.razor → ConnectionHealthService.GetCapabilities"] =
                    new(null, "Read: cached capability flags for the target instance."),
                ["Components/Shared/QueryPlanModal.razor → IJSRuntime.InvokeVoidAsync"] =
                    new(null,
                        "The plan renderer's JS interop (showPlan / setSearchTerm / setCompactView / "
                        + "setDotNetRef). Draws in THIS circuit's DOM; no server is touched."),
                ["Components/Shared/QueryPlanModal.razor → UserSettingsService.GetNoPantsMode"] =
                    new(null, "Read: the dev-mode preference, an interlock in front of the gated index path."),
                ["Components/Shared/QueryPlanModal.razor → UserSettingsService.GetUseV2PlanIcons"] =
                    new(null, "Read: which icon set the plan renderer should use."),
                ["Components/Shared/QueryPlanModal.razor → ToastService.ShowSuccess"] =
                    new(null, "\"Plan opened\" and the index-created message."),
                ["Components/Shared/QueryPlanModal.razor → ToastService.ShowError"] =
                    new(null, "The rejection messages for a statement that is not a CREATE NONCLUSTERED INDEX, and parse failures."),
                ["Components/Shared/QueryPlanModal.razor → ToastService.ShowWarning"] =
                    new(null, "\"No plan available\"."),
                ["Components/Shared/QueryPlanModal.razor → ToastService.ShowInfo"] =
                    new(null, "The optimisation-tips panel."),

                // ══ Components/Shared/RateLimitBadge.razor ════════════════════════════════════

                ["Components/Shared/RateLimitBadge.razor → RateLimiter.GetTimeToReset"] =
                    new(null, "Read: seconds until the query-rate window resets."),
                ["Components/Shared/RateLimitBadge.razor → RateLimiter.GetConnectionTimeToReset"] =
                    new(null, "Read: the same for the connection-attempt window."),

                // ══ Components/Shared/RbacGuard.razor ═════════════════════════════════════════

                ["Components/Shared/RbacGuard.razor → AppUserState.IsAuthorized"] =
                    new(null,
                        "The permission wrapper itself. It calls IsAuthorized to DECIDE, which is the "
                        + "gate rather than a surface behind one."),

                // ══ Components/Shared/SessionBubbleView.razor ═════════════════════════════════

                ["Components/Shared/SessionBubbleView.razor → IJSRuntime.InvokeVoidAsync"] =
                    new(null, "bubbleDragInterop init/redraw/dispose. Drag geometry in this circuit's DOM."),

                // ══ Newly VISIBLE 2026-08-06: the method-group / value-position shape ══════════
                //
                // ShellSurfaceRegistry.EdgesIn learned to see `alias.Member` handed somewhere as a
                // VALUE — with no parentheses of its own. Every shape before it needed a '(', an
                // '=' or a '+=' immediately after the member, so a service method handed to an
                // event, or read as a property, was invisible to the whole instrument. In THIS
                // file's components all of the newly-visible edges are reads; the method-group
                // case that proves the shape matters is in the shell
                // (WelcomeTourOverlay's @onclick="Tour.ToggleAutoAdvance").

                ["Components/Shared/ActivateFullAuditCard.razor → IBundleAccessor.IsUnlocked"] =
                    new(null,
                        "Read: whether the bundle is unlocked, which decides the card's label and "
                        + "whether the Deactivate button is offered. The acts are TryActivate / "
                        + "Deactivate, registered above with the open question about their host."),

                ["Components/Shared/AdminGuard.razor → AdminAuthService.RequiresSetup"] =
                    new(null, "Read: is there no admin password yet, which renders the enrolment form instead of the prompt."),
                ["Components/Shared/AdminGuard.razor → AdminAuthService.IsOpenUnprotected"] =
                    new(null, "Read: is this install running with the legacy gate switched off, which renders the warning."),

                ["Components/Shared/BaselineSeedingProgress.razor → AlertBaselineService.SeededCount"] =
                    new(null, "Read: how many baseline pairs are seeded, for the progress bar."),
                ["Components/Shared/BaselineSeedingProgress.razor → AlertBaselineService.TotalPairCount"] =
                    new(null, "Read: the denominator of that bar."),
                ["Components/Shared/BaselineSeedingProgress.razor → AlertBaselineService.SeedingComplete"] =
                    new(null, "Read: whether to render the bar at all."),
                ["Components/Shared/BaselineSeedingProgress.razor → AlertBaselineService.IsBaselineEnabled"] =
                    new(null, "Read: whether the learned-baseline feature is switched on at all "
                        + "(alerts-r1-11). With it off, seeding never starts and SeedingComplete never "
                        + "flips, so the banner is gated on this to stop rendering a forever '0 of 0' "
                        + "progress bar for a feature nobody turned on. No control, no gate."),

                ["Components/Shared/DynamicDashboard.razor → DashboardConfigService.Config"] =
                    new(null,
                        "Read: the in-memory config object this dashboard's definition comes from. "
                        + "⚠ It is the SAME object the edit handlers mutate — re-reading it is not "
                        + "a reload from disk, which is why the layout refusal no longer claims to "
                        + "be one."),
                ["Components/Shared/DynamicDashboard.razor → ServerConnectionManager.CurrentServer"] =
                    new(null,
                        "Read: the connection this dashboard is pointed at, for the header, the "
                        + "SQLWATCH check and the Query-Store database list. The WRITE through it "
                        + "is CurrentServer.Database=, registered above on run_scripts."),
                ["Components/Shared/DynamicDashboard.razor → ServerConnectionManager.DiscoveryCompleted"] =
                    new(null, "Read: has instance discovery already run, so a dashboard switch can reuse the cache."),
                ["Components/Shared/DynamicDashboard.razor → GlobalInstanceSelector.SelectedInstance"] =
                    new(null,
                        "Read: the instance selected process-wide, used as this dashboard's initial "
                        + "selection. The WRITE is SetSelectedInstance, registered above on run_scripts."),
                ["Components/Shared/DynamicDashboard.razor → CachingQueryExecutor.IsServingStaleData"] =
                    new(null, "Read: renders the \"serving cached data\" banner."),
                ["Components/Shared/DynamicDashboard.razor → CachingQueryExecutor.TotalQueries"] =
                    new(null, "Read: one of three cache counters written to the debug log line after a load."),
                ["Components/Shared/DynamicDashboard.razor → CachingQueryExecutor.FreshHits"] =
                    new(null, "Read: the second of those three counters."),
                ["Components/Shared/DynamicDashboard.razor → CachingQueryExecutor.CacheHits"] =
                    new(null, "Read: the third of those three counters, logged beside them."),

                ["Components/Shared/PanelEditorModal.razor → ServerConnectionManager.CurrentServer"] =
                    new(null,
                        "Read: which instance the gated Test-query and Detect-columns paths will run "
                        + "against, and the source of the \"No server selected\" warning. The ACT "
                        + "those two paths perform is pinned on ComponentActPins, not here, because "
                        + "no instrument in this file can see it."),

                ["Components/Shared/RateLimitBadge.razor → RateLimiter.IsRateLimited"] =
                    new(null, "Read: is the query-rate window exhausted, for the badge's colour."),
                ["Components/Shared/RateLimitBadge.razor → RateLimiter.IsConnectionRateLimited"] =
                    new(null, "Read: the same for the connection-attempt window."),
            };

        /// <summary>
        /// A component-level pin: an ACT this file's instruments cannot see, and the permission it
        /// must stay behind. <paramref name="Markers"/> is the exact code shape the act is made
        /// of, so a pin cannot outlive the call it was written for.
        /// </summary>
        internal sealed record PinnedComponentAct(string Permission, string[] Markers, string Reason);

        /// <summary>
        /// COMPONENT → PERMISSION PINS. A DETECTION THAT WAS LOST, restored 2026-08-06.
        ///
        /// <para>The register above is per EDGE, and an edge is what an instrument can see. Both
        /// instruments are blind to what PanelEditorModal does: <c>cmd</c> is a local
        /// <c>SqlCommand</c>, so the edge scanner cannot resolve the receiver, and the retained
        /// verb lexicon explicitly excuses <c>ExecuteReader*</c> as a READ — which it is, of
        /// somebody's production server, running the T-SQL the caller typed into the panel editor.
        /// So the whole act is invisible, and the per-edge register has nowhere to hang a
        /// permission for it.</para>
        ///
        /// <para>The census AT 3f0dcf6 asserted this, through a gated-components list keyed by
        /// FILE. That list was replaced by the per-edge register on 2026-08-06 and the assertion
        /// went with it: changing PanelEditorModal's gate to any other permission — or to none —
        /// passed the new census in silence. An upgrade that removes a detection is not an
        /// upgrade, and this is the second time this lane has proved that sentence.</para>
        ///
        /// <para>Deliberately SMALL and deliberately not a fallback for laziness: a call the
        /// scanner CAN see belongs on <see cref="Edges"/>, where the entry is checked against the
        /// live scan. An entry here has to name an act no instrument reports, and
        /// <see cref="EachPinnedActIsStillInTheFileItIsPinnedTo"/> fails if the act leaves.</para>
        /// </summary>
        private static readonly Dictionary<string, PinnedComponentAct> ComponentActPins =
            new(StringComparer.Ordinal)
            {
                ["Components/Shared/PanelEditorModal.razor"] = new(
                    "run_scripts",
                    new[] { "new SqlCommand(", "ExecuteReaderAsync" },
                    "TestQuery and DetectColumns run the T-SQL the caller has typed into the panel "
                    + "editor against a connected instance (SqlConnection.OpenAsync + "
                    + "SqlCommand.ExecuteReaderAsync, plus a SELECT TOP 1 * FROM (<caller sql>) "
                    + "fallback). That is arbitrary T-SQL execution, the same act as /query, from a "
                    + "component with no route. Invisible to BOTH instruments: a local SqlCommand "
                    + "receiver the edge scanner cannot resolve, and an ExecuteReader* name the verb "
                    + "lexicon excuses as a read."),
            };

        /// <summary>
        /// Components that render interactive controls and reach NO service edge at all. Being
        /// here is a statement that somebody looked, plus a machine-checked claim that there is
        /// nothing to find: <see cref="TheInertListIsActuallyInert"/> re-derives it on every run.
        ///
        /// <para>Since 2026-08-06 "inert" is a much stronger claim than it used to be. It no longer
        /// means "makes no call matching an acting verb" — it means the edge scanner finds no call,
        /// no property write and no event subscription on any injected service, plus no local
        /// SqlCommand-shaped call the lexicon can see. A component that so much as logs is not on
        /// this list; it is on the register.</para>
        ///
        /// <para>Several are MODALS AND EDITORS that look like they should write. They do not: they
        /// raise an <c>EventCallback</c> and the host page performs the write, and every host page
        /// is covered by the page census. That is the whole reason the claim is re-derived rather
        /// than asserted — "the host does the writing" stops being true the day somebody adds a
        /// service call here, and this test is what notices.</para>
        /// </summary>
        private static readonly Dictionary<string, string> InertInteractiveComponents =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // ⚠ DashboardToolbar SAT HERE, described as "dashboard chrome — filters, time
                // range, refresh; raises EventCallbacks to the host dashboard". That was false for
                // six calls (SetDataSource, SetRefreshInterval, SetDefaultTimeRange,
                // AutoRefreshService.SetInterval, PrintToPdfAsync, PrintViaBrowserAsync), and the
                // SAME COMMIT recorded DashboardToolbar → ServerConnectionManager.SetCurrentServer
                // on a second register. Two lists, one file, contradicting each other. It is now
                // shell, owned by ShellSurfaceRegistry, and
                // RbacShellSurfaceCensusTests.NoShellComponentSitsOnTheComponentCensusLists makes
                // that contradiction impossible rather than merely corrected.
                // ⚠ ActivateFullAuditCard and AdminGuard SAT HERE until 2026-08-06, the first as
                // "upsell card; navigates" and the second claiming no acting call while its own
                // prose described two. Both are false and both survived because the verb lexicon
                // has no Activate, no Deactivate, no Unlock and no Set. They are on the edge
                // register above now, with what they really call.
                ["Components/Shared/AddSectionModal.razor"] = "names a new report section and raises OnAdd; the host page persists it",
                ["Components/Shared/BlockingTreeViewer.razor"] = "renders a blocking chain as a tree; expand/collapse only",
                ["Components/Shared/CheckValidatorTable.razor"] = "displays check-validation results; row selection only",
                ["Components/Shared/CollapsibleSection.razor"] = "expand/collapse wrapper",
                ["Components/Shared/DashboardPanelWrapper.razor"] = "drag/resize chrome around a panel; raises EventCallbacks to DynamicDashboard",
                ["Components/Shared/DataGrid.razor"] = "sort/filter/top-N grid. It RENDERS the action and run-job buttons, but the delegates behind them belong to DynamicDashboard and are gated there",
                ["Components/Shared/DeadlockViewer.razor"] = "renders deadlock XML; selection only",
                ["Components/Shared/DeltaStatCard.razor"] = "a stat card with a delta arrow",
                ["Components/Shared/EditPageToolbar.razor"] = "edit/save/cancel toolbar; raises EventCallbacks to the host page",
                ["Components/Shared/FileSelectModal.razor"] = "picks a file path and raises OnSelect; the host page opens it",
                ["Components/Shared/PremiumLockCard.razor"] = "premium-lock card; navigates",
                ["Components/Shared/ReportSectionWrapper.razor"] = "section chrome in report edit mode; raises EventCallbacks to the host page",
                ["Components/Shared/SectionEditorModal.razor"] = "edits a section's title/layout in memory and raises OnSave; the host page persists it",
                ["Components/Shared/ServerDocSection.razor"] = "renders one documentation section; expand/collapse",
                ["Components/Shared/ServerSelector.razor"] = "server-picker dropdown; in-memory selection",
                ["Components/Shared/SessionDetailPanel.razor"] = "read-only session detail slide-out",
                ["Components/Shared/SessionLegend.razor"] = "colour legend; toggles a filter",
                ["Components/Shared/StatCard.razor"] = "single-value card; optional click raises an EventCallback",
            };

        // ── The tests ────────────────────────────────────────────────────

        /// <summary>
        /// THE ONE THAT FAILS WHEN A COMPONENT REACHES SOMETHING NOBODY HAS LOOKED AT.
        ///
        /// <para>Every edge every non-shell component makes must be on <see cref="Edges"/> by name.
        /// This is the inversion the round is for: the question is no longer "does this call look
        /// like a mutation" — which a word list answers wrongly for every word nobody thought of —
        /// but "is this edge on the record". An unrecognised call fails.</para>
        /// </summary>
        [Fact]
        public void EveryComponentEdgeIsOnTheRecord()
        {
            var unregistered = new List<string>();

            foreach (var component in EnumerateComponents())
            {
                // The always-rendered shell is owned by ShellSurfaceRegistry, which asks this exact
                // question of it already. Excluded so there is one place that speaks about a shell
                // file — the two-registers-disagreeing defect that created that class.
                if (ShellSurfaceRegistry.IsShell(component.Path)) continue;

                foreach (var edge in component.ActingCalls)
                {
                    var key = component.Path + " → " + edge;
                    if (!Edges.ContainsKey(key)) unregistered.Add(key);
                }
            }

            Assert.True(unregistered.Count == 0,
                "These component edges are on no register. A component has no route, so the PAGE census "
                + "cannot see it however carefully it is written — that is how ServerModeToggle came to "
                + "render a Start/Stop control on every AccessDenied page. Read the call, then add an "
                + "entry to RbacComponentGateCensusTests.Edges: a permission if it must be gated, or null "
                + "with a reason that says what the call actually is. Do not guess from the member's "
                + "name; that is the instrument this census replaced:\n  "
                + string.Join("\n  ", unregistered));
        }

        /// <summary>
        /// The other half of the accounting: a component that renders a control and reaches NO
        /// edge is still something somebody has to have looked at, because "it reaches nothing"
        /// is a claim about today's code and a service call is one line away.
        /// </summary>
        [Fact]
        public void EveryInteractiveComponentIsAccountedFor()
        {
            var unaccounted = EnumerateComponents()
                .Where(c => c.IsInteractive)
                .Where(c => !ShellSurfaceRegistry.IsShell(c.Path))
                .Where(c => c.ActingCalls.Count == 0)
                .Where(c => !InertInteractiveComponents.ContainsKey(c.Path))
                .Select(c => c.Path)
                .ToList();

            Assert.True(unaccounted.Count == 0,
                "These components render an interactive control, reach no service edge, and are on no "
                + "list. Add them to InertInteractiveComponents with a justification saying what they "
                + "are — the entry is what records that somebody looked:\n  "
                + string.Join("\n  ", unaccounted));
        }

        /// <summary>
        /// A register entry that names a permission is a claim about the shipped markup, so the
        /// file has to carry the gate and READ it. A <c>May…</c> property nothing consults is not
        /// a gate.
        /// </summary>
        [Theory]
        [MemberData(nameof(PermissionedComponentCases))]
        public void EachPermissionedEdgeIsGatedInTheComponentThatMakesIt(string path, string permission)
        {
            var text = ReadComponent(path);
            Assert.NotNull(text);

            var declaration = Regex.Match(text!,
                @"private bool (May[A-Za-z]+)\s*=>\s*UserState\.IsAuthorized\(""" + permission + @"""\)");
            Assert.True(declaration.Success,
                path + " is recorded as requiring \"" + permission + "\" — on Edges, or on "
                + "ComponentActPins for an act no instrument can see — but does not declare "
                + "`private bool MayXxx => UserState.IsAuthorized(\"" + permission + "\")`.");

            var gateName = declaration.Groups[1].Value;
            var uses = Regex.Matches(text!, @"\b" + gateName + @"\b").Count;
            Assert.True(uses >= 2,
                path + " declares " + gateName + " but nothing reads it (" + uses + " occurrence). "
                + "A gate that is declared and never consulted is not a gate.");

            Assert.False(text!.Contains("IsAuthorizedWithBreakGlass", StringComparison.Ordinal),
                path + " must not use IsAuthorizedWithBreakGlass — round 1 scoped break-glass to "
                + "Settings and Onboarding, the two surfaces that can undo a bad RBAC configuration. "
                + "None of these components can.");
        }

        /// <summary>
        /// One case per (component, permission) named anywhere on the register OR on
        /// <see cref="ComponentActPins"/>. The two sources feed ONE test on purpose: a pin has to
        /// be checked exactly as hard as an edge, or it becomes a comment with a dictionary around
        /// it.
        /// </summary>
        public static IEnumerable<object[]> PermissionedComponentCases =>
            Edges.Where(kv => kv.Value.Permission is not null)
                 .Select(kv => new
                 {
                     Path = kv.Key[..kv.Key.IndexOf(" → ", StringComparison.Ordinal)],
                     Permission = kv.Value.Permission!,
                 })
                 .Concat(ComponentActPins.Select(kv => new
                 {
                     Path = kv.Key,
                     Permission = kv.Value.Permission,
                 }))
                 .Distinct()
                 .Select(x => new object[] { x.Path, x.Permission });

        /// <summary>
        /// A pin names an ACT. If the act leaves the file, the pin is a reader believing somebody
        /// checked something that is not there — the same rot <see cref="TheListsHaveNoStaleEntries"/>
        /// catches for the register, which cannot see these acts at all.
        /// </summary>
        [Fact]
        public void EachPinnedActIsStillInTheFileItIsPinnedTo()
        {
            var stale = new List<string>();

            foreach (var (path, pin) in ComponentActPins)
            {
                var text = ReadComponent(path);
                if (text is null)
                {
                    stale.Add(path + " — no such component any more; remove or move the pin.");
                    continue;
                }

                foreach (var marker in pin.Markers)
                    if (!text.Contains(marker, StringComparison.Ordinal))
                        stale.Add(path + " — no longer contains \"" + marker
                                  + "\", which the pin describes as the act being gated.");
            }

            Assert.True(stale.Count == 0,
                "These component pins describe an act the file no longer performs:\n  "
                + string.Join("\n  ", stale));
        }

        /// <summary>
        /// The two selectors item 3b named, pinned at the handler.
        ///
        /// <para>Gating the MARKUP would not hold: the change event is bound directly to the
        /// handler, and hiding the control leaves the handler reachable. The refusal is also
        /// asserted to be RENDERED — a refusal a caller cannot see is indistinguishable from a
        /// change that silently did not take, and the dropdown is rebuilt from the selection that
        /// actually holds so the control does not assert something false either.</para>
        /// </summary>
        [Fact]
        public void BothDynamicDashboardSelectorsRefuseAnUnauthorisedRetarget()
        {
            var text = ReadComponent("Components/Shared/DynamicDashboard.razor");
            Assert.NotNull(text);

            foreach (var (handler, mutation) in new[]
                     {
                         ("OnInstanceChanged(ChangeEventArgs e)", "InstanceSelector.SetSelectedInstance"),
                         ("OnQueryStoreDatabaseChanged(ChangeEventArgs e)", "ConnectionManager.CurrentServer.Database ="),
                     })
            {
                var body = MemberBody(text!, handler);

                var guard = body.IndexOf("if (!MayExecuteActions)", StringComparison.Ordinal);
                var refuse = body.IndexOf("RefuseRetarget(", StringComparison.Ordinal);
                var early = body.IndexOf("return;", StringComparison.Ordinal);
                var write = body.IndexOf(mutation, StringComparison.Ordinal);

                Assert.True(guard >= 0, handler + " does not consult MayExecuteActions.");
                Assert.True(refuse > guard, handler + " must announce the refusal, not swallow it.");
                Assert.True(early > refuse, handler + " must leave without performing the retarget.");
                Assert.True(write > early,
                    handler + " performs " + mutation + " before or without the refusal path. A guard "
                    + "that runs after the write is not a guard.");
            }

            // The refusal reaches the screen, and the control is rebuilt from the truth.
            Assert.Contains("@_retargetRefusal", text!, StringComparison.Ordinal);
            Assert.Contains("_selectorGeneration++", text!, StringComparison.Ordinal);
            Assert.Contains("@key=\"@(\"instance-select-\" + _selectorGeneration)\"", text!, StringComparison.Ordinal);
            Assert.Contains("@key=\"@(\"qs-db-select-\" + _selectorGeneration)\"", text!, StringComparison.Ordinal);
        }

        /// <summary>
        /// The dashboard's half of the process-wide connection edge.
        ///
        /// <para><b>This test used to be the defect.</b> It read the body of
        /// <c>SetServerContext</c> and nothing else, and it certified a claim — "the single point
        /// all five call sites pass through" — that a SIXTH call site sixty lines further down the
        /// same file defeated. Two rounds of gating shipped with this test green. An instrument
        /// that inspects the method it has been told is the chokepoint cannot find the write that
        /// is not in it.</para>
        ///
        /// <para>So it now reads the WHOLE FILE for the edge, and the file-independent half of the
        /// question — can a call site anywhere reach the write without deciding? — moved to
        /// <see cref="ConnectionRetargetChokepointTests"/>, which runs the manager rather than
        /// reading it.</para>
        /// </summary>
        [Fact]
        public void TheServerContextChokepointRefusesAnUnauthorisedCaller()
        {
            var text = ReadComponent("Components/Shared/DynamicDashboard.razor");
            Assert.NotNull(text);

            var body = MemberBody(text!, "private void SetServerContext(string instanceName)");

            var guard = body.IndexOf("if (!MayExecuteActions)", StringComparison.Ordinal);
            var note = body.IndexOf("NoteContextFollowsTheProcess(", StringComparison.Ordinal);
            var early = body.IndexOf("return;", StringComparison.Ordinal);
            var write = body.IndexOf("ConnectionManager.SetCurrentServer(", StringComparison.Ordinal);

            Assert.True(guard >= 0, "SetServerContext does not consult MayExecuteActions.");
            Assert.True(note > guard, "The refusal must be announced, not swallowed.");
            Assert.True(early > note, "SetServerContext must leave without writing the singleton.");
            Assert.True(write > early,
                "SetServerContext writes the process-wide connection before or without the guard.");

            // ── THE WHOLE FILE, not the one method ────────────────────────────────────────────
            //
            // Every write of the edge in this component, wherever it is, must hand over a grant.
            // The compiler already refuses a call without one; this says so where the next reader
            // of this file will look, and it fails loudly if somebody reintroduces the discovery
            // loop's version of the call.
            var callSites = Regex.Matches(text!, @"ConnectionManager\.SetCurrentServer\(([^;]*)\)\s*;");
            Assert.True(callSites.Count >= 2,
                "The SetCurrentServer call sites in DynamicDashboard have moved — re-point this pin.");
            foreach (Match call in callSites)
                Assert.True(
                    call.Groups[1].Value.Contains("grant", StringComparison.Ordinal),
                    "A write of the process-wide connection in DynamicDashboard does not pass a "
                    + "ConnectionRetargetGrant: " + call.Value);

            // The fifth write, by name. It was inside the discovery loop, ungated, on the path
            // every dashboard load takes — and it is now not a write at all: discovery is handed
            // the connection it is to probe.
            var discovery = MemberBody(text!, "private async Task DiscoverAndUpdateInstancesAsync()");
            Assert.DoesNotContain("SetCurrentServer", discovery, StringComparison.Ordinal);
            Assert.Contains("Discovery.DiscoverAsync(", discovery, StringComparison.Ordinal);

            // ── The notice, and the thing it must not go back to saying ───────────────────────
            //
            // It reaches the screen, in a header that renders even when there is no current server
            // to describe; and it is a PROPERTY composed at render, not a string frozen at the
            // moment of refusal. The frozen one said "so nothing was changed" for the rest of the
            // load however the connection moved afterwards.
            Assert.Contains("@ContextFollowNotice", text!, StringComparison.Ordinal);
            Assert.Contains("|| ContextFollowNotice is not null", text!, StringComparison.Ordinal);
            Assert.Contains("private string? ContextFollowNotice", text!, StringComparison.Ordinal);
            Assert.DoesNotContain("so nothing was changed", CodeOnly(text!), StringComparison.Ordinal);

            // …and it is conditioned on a measurement rather than on an assumption: the current
            // server observed before this load could have written anything, compared with the one
            // in force at render.
            var notice = MemberBody(text!, "private string? ContextFollowNotice");
            Assert.Contains("_contextAtInitObserved", notice, StringComparison.Ordinal);
            Assert.Contains("_contextAtInit,", notice, StringComparison.Ordinal);
            Assert.Contains("ConnectionManager.CurrentServer", notice, StringComparison.Ordinal);

            var init = MemberBody(text!, "private async Task InitializeDashboard()");
            Assert.Contains("_contextAtInit = ConnectionManager.CurrentServer?.Id;", init,
                StringComparison.Ordinal);

            // …and it is taken ONCE PER LOAD, which is the half this pin used to leave out.
            //
            // Asserting only that the measurement EXISTS certifies its presence and never that it
            // measures what the sentence beside it claims — the instrument mistake this file's own
            // header is about. InitializeDashboard runs TWICE on one page load (OnInitializedAsync,
            // then OnParametersSetAsync's first pass), and the second capture overwrote the
            // observation with the state the load had already produced. A build carrying the
            // discovery loop's fifth write then rendered "which is the server this page began
            // loading with" underneath GATEPROBE-NOSUCHHOST, on the load that moved it there.
            var onceGuard = init.IndexOf("if (!_contextAtInitObserved", StringComparison.Ordinal);
            var capture = init.IndexOf("_contextAtInit = ConnectionManager.CurrentServer?.Id;",
                StringComparison.Ordinal);
            Assert.True(onceGuard > 0 && onceGuard < capture,
                "The context measurement is taken unconditionally, before the assignment is "
                + "guarded. InitializeDashboard runs twice per page load and the second pass then "
                + "erases the load's own movement from the evidence the notice is composed from.");
            Assert.Contains("_contextAtInitFor = DashboardId;", init, StringComparison.Ordinal);

            // The two writes that do NOT pass through the chokepoint, both on the load path.
            // Matched on one line each so the assertion does not depend on this file's line endings.
            foreach (var conditioned in new[]
                     {
                         "if (MayExecuteActions && !string.IsNullOrEmpty(Dashboard.DefaultDatabase)",
                         "if (MayExecuteActions && _queryStoreDatabases.Any()",
                     })
                Assert.Contains(conditioned, text!, StringComparison.Ordinal);
        }

        /// <summary>
        /// The install-wide layout write, pinned at its single chokepoint, plus the render
        /// condition so a denied caller is not offered the mode.
        /// </summary>
        [Fact]
        public void TheDashboardLayoutSaveIsGatedAtItsChokepoint()
        {
            var text = ReadComponent("Components/Shared/DynamicDashboard.razor");
            Assert.NotNull(text);

            var at = text!.IndexOf("private void DashSaveAndRebuild()", StringComparison.Ordinal);
            Assert.True(at > 0, "DashSaveAndRebuild no longer exists — re-point this pin.");

            var body = text[at..Math.Min(text.Length, at + 1400)];
            Assert.Contains("if (LayoutEditRefused()) return;", body, StringComparison.Ordinal);

            // The guard sits BEFORE the write, not beside it.
            Assert.True(
                body.IndexOf("if (LayoutEditRefused()) return;", StringComparison.Ordinal)
                    < body.IndexOf("ConfigService.UpdateConfig", StringComparison.Ordinal),
                "The permission check must precede the write it is guarding.");

            Assert.Contains("_noPantsMode && MayEditDashboardLayout", text, StringComparison.Ordinal);
            Assert.Contains("@LayoutRefusalNotice", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE REFUSAL HAS TO BE TRUE, and it was not.
        ///
        /// <para>The guard used to sit only at the save, so all six handlers mutated
        /// <c>Dashboard.Panels</c> first and the refusal then "re-read from the service so the
        /// panels on screen are the ones on disk". <c>ConfigService.Config</c> returns the SAME
        /// in-memory object those handlers had just edited, so nothing was undone and the sentence
        /// under the edited layout said the layout was the saved one.</para>
        ///
        /// <para>Two assertions, because either one alone can be satisfied while the defect
        /// stands: every handler refuses BEFORE its first mutation, and no refusal path claims a
        /// reload it does not perform.</para>
        /// </summary>
        [Fact]
        public void EveryLayoutEditHandlerRefusesBeforeItMutates()
        {
            var text = ReadComponent("Components/Shared/DynamicDashboard.razor");
            Assert.NotNull(text);

            foreach (var (signature, mutation) in new[]
                     {
                         ("private void DashMoveUp(PanelDefinition panel)", "panels.RemoveAt(idx)"),
                         ("private void DashMoveDown(PanelDefinition panel)", "panels.RemoveAt(idx)"),
                         ("private void DashDeletePanel(PanelDefinition panel)", "Dashboard.Panels.Remove(panel)"),
                         ("private void DashTogglePanel(PanelDefinition panel)", "panel.Enabled = !panel.Enabled"),
                         ("private async Task DashSavePanel(PanelDefinition updated)", "Dashboard.Panels[idx] = updated"),
                         ("private async Task DashHandlePanelAdded(ReportSection section)", "Dashboard.Panels.Add("),
                     })
            {
                var body = MemberBody(text!, signature);

                var guard = body.IndexOf("LayoutEditRefused()", StringComparison.Ordinal);
                var write = body.IndexOf(mutation, StringComparison.Ordinal);

                Assert.True(guard >= 0, signature + " does not consult LayoutEditRefused().");
                Assert.True(write > guard,
                    signature + " performs " + mutation + " before it is refused. A guard that runs "
                    + "after the mutation has to undo it, and the undo this file used to attempt "
                    + "re-read the very object the handler had just changed.");
            }

            // The claim that was false, checked STRUCTURALLY rather than by banning a phrase: the
            // refusal path must not re-read ConfigService.Config at all. That re-read was the
            // "undo", it returns the object the handler just mutated, and its presence is what the
            // false sentence was describing. (The prose ABOVE the method quotes the old claim on
            // purpose; MemberBody starts at the signature, so the history is kept and the code is
            // what is measured.)
            var refusal = MemberBody(text!, "private bool LayoutEditRefused()");
            Assert.DoesNotContain("ConfigService.Config", refusal, StringComparison.Ordinal);
            Assert.DoesNotContain("panels on screen are the ones on disk", refusal, StringComparison.Ordinal);

            // And the refusal says what is true — of the cause it is describing.
            //
            // The sentence used to live in LayoutEditRefused and to end "nothing on this page was
            // altered". Two things were wrong with it once the notice became reachable a second
            // way. It is not true of the OTHER cause: when authorisation drops while edit mode is
            // open, no change was submitted, so "the change was refused" describes nothing. And it
            // was not quite true of its own cause either, because the refusal closes edit mode,
            // which is a change to this page. Two causes, two sentences, and both say what did NOT
            // happen in terms of the thing at stake — the panels.
            var notice = MemberBody(text!, "private string? LayoutRefusalNotice =>");
            Assert.Contains("so the change was refused: no panel was", notice, StringComparison.Ordinal);
            Assert.Contains("no longer authorised for settings, so editing has been closed", notice,
                StringComparison.Ordinal);
            Assert.DoesNotContain("nothing on this page was altered", CodeOnly(text!), StringComparison.Ordinal);

            // The second cause has to be REACHED by something, or it is a sentence with no state.
            // OnAfterRender closes an armed edit toolbar the moment the permission behind it goes
            // away. In the shipped UI a caller who may not edit is never offered edit mode — the
            // Edit Page button carries the same permission as the guard — so every route to EITHER
            // notice runs through a mid-session authorisation drop, and this is the one that does
            // not also require the caller to click a control they can no longer be shown.
            // OBSERVED 2026-08-07 on :5199: RBAC off ⇒ loopback bootstrap admin ⇒ Edit Page clicked;
            // RBAC then enabled from a second tab (in-process, "switched on and ENFORCED"); the next
            // render printed the AuthorisationDropped sentence and withdrew the button, and a click
            // on Move up in the same state printed the ChangeRefused one with dashboard-config.json
            // untouched on disk.
            var after = MemberBody(text!, "protected override void OnAfterRender(bool firstRender)");
            Assert.Contains("if (!_dashEditMode || MayEditDashboardLayout) return;", after,
                StringComparison.Ordinal);
            Assert.Contains("LayoutRefusalCause.AuthorisationDropped", after, StringComparison.Ordinal);
            Assert.Contains("_dashEditMode = false;", after, StringComparison.Ordinal);
            Assert.DoesNotContain("LayoutEditRefused", after, StringComparison.Ordinal);

            // The inherited comment at DashResetPanels, which claimed the same reload.
            Assert.DoesNotContain("Reload config from disk", text!, StringComparison.Ordinal);
        }

        /// <summary>
        /// ActivateFullAuditCard is hosted only by the Settings page, which gates on break-glass.
        /// Since SEC-3 (2026-08-26) its licence-install action carries a stricter authenticated-admin
        /// gate of its own (MayInstallBundle), pinned as a permissioned edge above; this test pins
        /// the weaker outer relationship — WHERE the card renders and that its host is gated — which
        /// is prose until something checks it, and prose is what this lane has died of.
        /// </summary>
        [Fact]
        public void ActivateFullAuditCardIsReachedOnlyFromTheGatedSettingsPage()
        {
            var root = RawPassedScan.RepoRoot();
            var hosts = new List<string>();

            foreach (var scanRoot in new[] { "Pages", "Components" })
            {
                var dir = new DirectoryInfo(Path.Combine(root.FullName, scanRoot));
                if (!dir.Exists) continue;
                foreach (var file in dir.EnumerateFiles("*.razor", SearchOption.AllDirectories))
                {
                    if (!File.ReadAllText(file.FullName).Contains("<ActivateFullAuditCard", StringComparison.Ordinal)) continue;
                    hosts.Add(Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/'));
                }
            }

            Assert.Equal(new[] { "Pages/Settings.razor" }, hosts.OrderBy(h => h, StringComparer.Ordinal).ToArray());

            var settings = ReadComponent("Pages/Settings.razor");
            Assert.NotNull(settings);
            Assert.Contains("UserState.IsAuthorizedWithBreakGlass(\"settings\")", settings!, StringComparison.Ordinal);
        }

        /// <summary>
        /// The inert list, re-derived. If somebody adds a service call to one of these, the list
        /// stops being true OUT LOUD instead of quietly — which is the failure mode round 2's page
        /// census had and round 3 paid for.
        /// </summary>
        [Fact]
        public void TheInertListIsActuallyInert()
        {
            var byPath = EnumerateComponents().ToDictionary(c => c.Path, c => c, StringComparer.OrdinalIgnoreCase);

            var offenders = new List<string>();
            foreach (var entry in InertInteractiveComponents)
            {
                if (!byPath.TryGetValue(entry.Key, out var component)) continue; // TheListsHaveNoStaleEntries
                if (component.ActingCalls.Count > 0)
                    offenders.Add(entry.Key + " → " + string.Join(", ", component.ActingCalls)
                                  + "\n      justification on file: \"" + entry.Value + "\"");
            }

            Assert.True(offenders.Count == 0,
                "These components are on the INERT list but call something that changes state outside "
                + "this process. Gate them and move them to GatedComponents, or register the call in "
                + "ReviewedActingComponents with the reason:\n  " + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// Every justification says something. An empty or placeholder entry is how a list becomes
        /// decoration.
        /// </summary>
        [Fact]
        public void EveryReviewedEntryCarriesAJustification()
        {
            static bool Thin(string reason) =>
                reason.Trim().Length < 20
                || reason.Contains("TODO", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("n/a", StringComparison.OrdinalIgnoreCase);

            var thin = InertInteractiveComponents.Where(kv => Thin(kv.Value)).Select(kv => kv.Key)
                .Concat(Edges.Where(kv => Thin(kv.Value.Reason)).Select(kv => kv.Key))
                .ToList();

            Assert.True(thin.Count == 0,
                "These entries carry no real justification:\n  " + string.Join("\n  ", thin));
        }

        /// <summary>
        /// An allow-list nobody prunes is an allow-list that becomes the whole app. A register
        /// entry for an edge the component no longer makes is worse than useless: it is a reader
        /// believing somebody checked something that is not there.
        /// </summary>
        [Fact]
        public void TheListsHaveNoStaleEntries()
        {
            var byPath = EnumerateComponents().ToDictionary(c => c.Path, c => c, StringComparer.OrdinalIgnoreCase);
            var stale = new List<string>();

            foreach (var path in InertInteractiveComponents.Keys)
                if (!byPath.ContainsKey(path))
                    stale.Add(path + " — no such non-routable component any more; remove or move the entry.");

            foreach (var key in Edges.Keys)
            {
                var split = key.IndexOf(" → ", StringComparison.Ordinal);
                var path = key[..split];
                var edge = key[(split + 3)..];

                if (!byPath.TryGetValue(path, out var component))
                {
                    stale.Add(key + " — no such non-routable component any more.");
                    continue;
                }
                if (!component.ActingCalls.Contains(edge, StringComparer.Ordinal))
                    stale.Add(key + " — registered but the component no longer makes this call; prune it.");
            }

            // A component on the inert list must not also be on the register — "reviewed" would
            // then mean whichever list the reader happened to open. That contradiction, held in one
            // commit, is what created ShellSurfaceRegistry.
            foreach (var key in Edges.Keys)
            {
                var path = key[..key.IndexOf(" → ", StringComparison.Ordinal)];
                if (InertInteractiveComponents.ContainsKey(path))
                    stale.Add(path + " — is on the INERT list AND has register edges. Only one can be true.");
            }

            Assert.True(stale.Count == 0, "Stale entries:\n  " + string.Join("\n  ", stale.Distinct()));
        }

        /// <summary>
        /// ServerModeToggle specifically, pinned. It is the reason this file exists, and the shape
        /// that made it dangerous — rendered by StatusBar, which MainLayout renders on every route,
        /// including the AccessDenied pages — is a shape a refactor could restore without touching
        /// the toggle itself.
        /// </summary>
        [Fact]
        public void ServerModeToggleIsGatedAndStillRenderedByTheLayout()
        {
            var toggle = ReadComponent("Components/Shared/ServerModeToggle.razor");
            Assert.NotNull(toggle);

            Assert.Contains("UserState.IsAuthorized(\"settings\")", toggle!, StringComparison.Ordinal);

            // Denied renders NOTHING — the boundary must WRAP the whole component's markup, not
            // one branch of it, or the Start button (the `else` arm, shown when the server is NOT
            // running) stays live for a denied caller.
            //
            // ROUND 7: this was an early `return` in the render body until 2026-08-02. Identical
            // effect, but a bare `return` is invisible to a census that asks "is this control
            // inside a boundary?", and that question is now the decider because the question it
            // replaced — "what does this handler call?" — was answered green for a live exploit by
            // one line of C#.
            var markupAt = toggle.IndexOf("<div class=\"server-mode-toggle", StringComparison.Ordinal);
            Assert.True(markupAt > 0, "ServerModeToggle no longer renders its own root div.");

            var enclosing = ShellBoundaryScan
                .BoundaryRegions(ShellBoundaryScan.Blank(toggle))
                .Where(r => markupAt >= r.Start && markupAt < r.End)
                .ToList();

            Assert.True(enclosing.Any(r => r.Permission == "settings"),
                "The boundary must wrap the whole component's markup: gating only the running "
                + "branch would leave the Start button live for a denied caller.");

            Assert.DoesNotContain(enclosing, r => r.BreakGlass);

            // Both handlers, not just the render.
            Assert.Contains("private async Task StartServer()", toggle, StringComparison.Ordinal);
            var startBody = toggle[toggle.IndexOf("private async Task StartServer()", StringComparison.Ordinal)..];
            Assert.Contains("if (!MayControlServerMode) return;", startBody[..200], StringComparison.Ordinal);

            var stopBody = toggle[toggle.IndexOf("private async Task StopServer()", StringComparison.Ordinal)..];
            Assert.Contains("if (!MayControlServerMode) return;", stopBody[..200], StringComparison.Ordinal);

            // The rendering relationship that made it reach every page. If this ever stops being
            // true the component is no longer app-wide, and the note above should be corrected
            // rather than left to mislead.
            var statusBar = ReadComponent("Components/Layout/StatusBar.razor");
            Assert.NotNull(statusBar);
            Assert.Contains("<ServerModeToggle", statusBar!, StringComparison.Ordinal);
        }

        // ── Scanner ──────────────────────────────────────────────────────

        /// <summary>
        /// Every path named on any of the three lists in this file. Read by
        /// <c>RbacShellSurfaceCensusTests.NoShellComponentSitsOnTheComponentCensusLists</c>, which
        /// is what stops the shell from being described in two places that can disagree.
        /// </summary>
        /// <summary>
        /// Is this <c>"path → edge"</c> key on the register. Exposed so
        /// <see cref="RbacComponentCensusInstrumentTests"/> can drive the census's own predicate
        /// rather than restate it — a negative control that reimplements the thing it is testing
        /// proves only that the reimplementation agrees with itself.
        /// </summary>
        internal static bool IsRegistered(string key) => Edges.ContainsKey(key);

        internal static IEnumerable<string> AllListedComponentPaths() =>
            Edges.Keys
                .Select(k => k[..k.IndexOf(" → ", StringComparison.Ordinal)])
                .Concat(InertInteractiveComponents.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase);

        internal sealed record ComponentFile(string Path, bool IsInteractive, IReadOnlyList<string> ActingCalls);

        /// <summary>
        /// Every <c>.razor</c> under <c>Pages/</c> or <c>Components/</c> that carries NO
        /// <c>@page</c> directive — i.e. everything the page census cannot see — with whether it
        /// binds a DOM event and what acting calls it makes.
        /// </summary>
        internal static List<ComponentFile> EnumerateComponents()
        {
            var root = RawPassedScan.RepoRoot();
            var pageDirective = new Regex(@"^\s*@page\s+""", RegexOptions.Multiline);

            // @on* covers click/change/input/submit/keydown/…; @bind covers two-way inputs, which
            // are how a modal's fields are edited. Either makes a component something a caller can
            // OPERATE, which is the property that matters.
            var interactive = new Regex(@"@on[a-z]+\s*=|@bind\b", RegexOptions.Compiled);

            var components = new List<ComponentFile>();
            foreach (var scanRoot in new[] { "Pages", "Components" })
            {
                var dir = new DirectoryInfo(Path.Combine(root.FullName, scanRoot));
                if (!dir.Exists) continue;

                foreach (var file in dir.EnumerateFiles("*.razor", SearchOption.AllDirectories))
                {
                    var text = File.ReadAllText(file.FullName);
                    if (pageDirective.IsMatch(text)) continue;   // routable — the page census owns it

                    var codeBehind = file.FullName + ".cs";
                    if (File.Exists(codeBehind)) text += File.ReadAllText(codeBehind);

                    components.Add(new ComponentFile(
                        Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/'),
                        interactive.IsMatch(text),
                        ActingCallsIn(text)));
                }
            }
            return components;
        }

        /// <summary>
        /// What this census now asks of a component, and the reason it asks it this way.
        ///
        /// <para><b>The edge scanner is the primary instrument.</b>
        /// <see cref="ShellSurfaceRegistry.EdgesIn"/> enumerates EVERY call and EVERY property
        /// assignment on an injected service, asking nothing at all about the member's name. An
        /// edge that is not on the register FAILS. That inversion is the whole upgrade: the verb
        /// lexicon this census used to run on is a match-known instrument, blind by construction to
        /// every verb nobody thought of, and it was defeated five times in five rounds — most
        /// plainly by a bare <c>Set</c> (<c>UserSettings.SetAnonymiseServerNames</c>, live from a
        /// LAN denial page, census green) and by a property write, which a call-shaped regex cannot
        /// match at all.</para>
        ///
        /// <para><b>The verb lexicon is kept, narrowed to where the edge scanner is blind.</b>
        /// <see cref="RbacPageGateCensusTests.MutatingCalls"/> is retained for calls on receivers
        /// that are NOT injected services — a local <c>SqlCommand</c>, <c>SqlConnection</c>,
        /// <c>Process</c>. Those are precisely the calls the edge scanner cannot see, and they are
        /// the ones that gated DynamicDashboard, QueryPlanModal and PanelEditorModal in the first
        /// place. Dropping the lexicon here would have LOST <c>cmd.ExecuteNonQueryAsync</c> —
        /// an upgrade that removes a detection is not an upgrade.</para>
        ///
        /// <para>Splitting the two instruments by RECEIVER, rather than unioning them raw, also
        /// stops one call arriving on the register twice under two names: the edge scanner records
        /// <c>ServerConnectionManager.AddConnection</c> (the declared TYPE) where the lexicon
        /// records <c>ConnectionManager.AddConnection</c> (the alias). Two keys for one call is how
        /// a register starts to disagree with itself.</para>
        /// </summary>
        internal static List<string> ActingCallsIn(string text)
        {
            var edges = ShellSurfaceRegistry.EdgesIn(text);
            var injected = ShellSurfaceRegistry.InjectedAliases(text);

            var localCalls = RbacPageGateCensusTests.MutatingCalls(text)
                .Where(c =>
                {
                    var dot = c.IndexOf('.');
                    return dot > 0 && !injected.ContainsKey(c[..dot]);
                });

            return edges.Concat(localCalls)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// The file with its comment lines removed.
        ///
        /// <para>Needed by the two "this sentence must never be printed again" assertions below. A
        /// struck claim is QUOTED in this tree on purpose — the comment above a fix says what the
        /// fix is a fix for, and deleting that history is how the same sentence comes back in a new
        /// costume three months later. So the ban has to be on what can REACH A SCREEN, which is
        /// the string literals, not on the file's memory of its own mistakes. Same line filter the
        /// edge scanner uses.</para>
        /// </summary>
        private static string CodeOnly(string text) =>
            string.Join("\n", text.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith("*", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith("@*", StringComparison.Ordinal)));

        /// <summary>
        /// The text of one member, from its signature to the start of the next member at the same
        /// indent. A fixed character window was tried first and was wrong: a doc comment in front
        /// of the guard pushed the guard out of it, and the pin failed on correct code. A window
        /// that can silently exclude the thing it is looking for is the same instrument mistake
        /// this whole census is about.
        /// </summary>
        private static string MemberBody(string text, string signature)
        {
            var at = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at > 0, signature + " no longer exists — re-point this pin at whatever replaced it.");

            var next = text.IndexOf("\n    private ", at + signature.Length, StringComparison.Ordinal);
            return next > 0 ? text[at..next] : text[at..];
        }

        private static string? ReadComponent(string relativePath)
        {
            var full = Path.Combine(RawPassedScan.RepoRoot().FullName,
                                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return null;
            var text = File.ReadAllText(full);
            if (File.Exists(full + ".cs")) text += File.ReadAllText(full + ".cs");
            return text;
        }
    }
}
