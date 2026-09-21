/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SQLTriage.Tests
{
    /// <summary>
    /// THE ONE REGISTER for the always-rendered app shell, and the scanner that derives what has
    /// to be on it.
    ///
    /// <para>⚠ <b>SECOND LAYER, NOT THE BOUNDARY.</b> Since round 8 (2026-08-03) the thing that
    /// keeps an unauthenticated stranger off this application is
    /// <c>Data/Services/InteractiveAppAdmission.cs</c>: a caller from a non-loopback origin with
    /// no session receives no document, no static asset and no <c>/_blazor</c> circuit, so there
    /// is no control for them to reach and no register entry that can be wrong in a way they can
    /// exploit. This register still earns its keep — it says what an already-permitted control may
    /// REACH, which is a question about authenticated callers and about blast radius — but a green
    /// run of it is not evidence that a control is unreachable. Four instruments in four rounds
    /// each read that way and each was wrong.</para>
    ///
    /// <para><b>Why this replaces three lists with one.</b> Round 5 left the shell described by
    /// three registers that could contradict each other and did: the SAME COMMIT put
    /// <c>DashboardToolbar</c> on <c>InertInteractiveComponents</c> — the "makes no acting call"
    /// list, justified as "raises EventCallbacks to the host dashboard" — while putting
    /// <c>DashboardToolbar → ServerConnectionManager.SetCurrentServer</c> on
    /// <c>ReviewedLayoutEdges</c>. Both statements were about the same file and only one could be
    /// true. Round 5 had corrected exactly that defect one entry above and left it standing on its
    /// neighbour. Two lists a human keeps in step is not a source of truth, so there is now one:
    /// <see cref="Edges"/>. The component census and the DI-lifetime census both read it, and a
    /// shell component is FORBIDDEN from appearing on the old lists at all
    /// (<c>RbacShellSurfaceCensusTests.NoShellComponentSitsOnTheComponentCensusLists</c>), which
    /// is what makes the contradiction structurally impossible rather than merely fixed.</para>
    ///
    /// <para><b>Why the membership test is not a verb lexicon.</b> The cold gate defeated round 5
    /// by adding ONE real control to <c>MainLayout</c> —
    /// <c>UserSettings.SetAnonymiseServerNames(false)</c>, which disables install-wide
    /// anonymisation of client server names in logs and resets the alias map — and the census
    /// answered <c>Failed: 0, Passed: 42</c> while the control worked from the LAN denial page.
    /// It was invisible because <c>MutatingCalls</c> matched a 29-verb lexicon with no bare
    /// <c>Set</c>, and the DI guard only flagged methods that raise an event.
    /// <c>SetAnonymiseServerNames</c> is neither. A lexicon is a match-known instrument: it is
    /// blind by construction to every verb nobody thought of, and five rounds have now each been
    /// defeated through the category the instrument could not see.</para>
    ///
    /// <para>So <see cref="EdgesOf"/> asks no question about the METHOD at all. It enumerates
    /// EVERY call and EVERY property assignment the shell makes on an injected service, whatever
    /// it is called, and an edge that is not on <see cref="Edges"/> FAILS. Unknown is red, not
    /// green. The verb lexicon still exists in <c>RbacPageGateCensusTests.MutatingCalls</c> and
    /// still adds signal on the routable pages; it decides nothing here.</para>
    ///
    /// <para><b>Why the shell set is derived rather than listed.</b> A hand-written list of
    /// "always-rendered components" is the same instrument class — it is blind to the component
    /// somebody adds to <c>MainLayout</c> next week. <see cref="ShellComponents"/> is the
    /// transitive closure of the component tags <c>MainLayout</c> renders, unioned with everything
    /// under <c>Components/Layout/</c>. Add a component to the shell and its edges land on this
    /// register's doorstep the same day.</para>
    /// </summary>
    internal static class ShellSurfaceRegistry
    {
        /// <summary>
        /// A reviewed edge. <paramref name="Permission"/> non-null means the control is GATED and
        /// the file must prove it: declare <c>private bool MayXxx =&gt; …
        /// UserState.IsAuthorized("&lt;permission&gt;")</c> and guard a handler on it. Null means
        /// the edge is deliberately ungated, and <paramref name="Reason"/> has to say what the
        /// call actually is — the false justification on an allow-list is the failure mode this
        /// lane has died of in five successive rounds.
        /// </summary>
        internal sealed record ReviewedEdge(string? Permission, string Reason);

        // ── THE REGISTER ─────────────────────────────────────────────────
        //
        // Key format: "<relative path> → <ServiceType>.<Member>", and "<Member>=" for a property
        // assignment. Re-derived mechanically on 2026-08-02 from what each file actually calls —
        // not copied forward from the lists it replaces.

        internal static readonly Dictionary<string, ReviewedEdge> Edges = new(StringComparer.Ordinal)
        {
            // ══ Components/Layout/MainLayout.razor — wraps the <Router>, so EVERY route ═══════

            ["Components/Layout/MainLayout.razor → UserSettingsService.SetOnboardingComplete"] =
                new("settings",
                    "Written when the first-run wizard closes. UserSettingsService is a SINGLETON "
                    + "holding the INSTALL's settings, persisted to %APPDATA%\\SQLTriage and "
                    + "forwarded from the WPF container — not a per-user profile."),
            ["Components/Layout/MainLayout.razor → UserSettingsService.SetHasSeenWelcomeTour"] =
                new("settings", "Same install-wide persisted flag, written by both welcome-tour CTA buttons."),
            ["Components/Layout/MainLayout.razor → UserSettingsService.SetLastSeenVersion"] =
                new("settings", "Install-wide persisted flag written when the release-notes modal closes."),
            ["Components/Layout/MainLayout.razor → UserSettingsService.SetShowReleaseNotesOnUpdate"] =
                new("settings", "Install-wide persisted preference written by the modal's \"don't show again\"."),

            ["Components/Layout/MainLayout.razor → AutoUpdateService.StartBackgroundCheck"] =
                new(null,
                    "The app's own update check, fired once from OnInitializedAsync on the app's "
                    + "schedule rather than by a caller — no control renders it. OnUpdateAvailable "
                    + "then raises the banner in every circuit, which is correct: \"a new build "
                    + "exists\" is a fact about the install, identical for every viewer, and the "
                    + "banner only links to /settings."),
            ["Components/Layout/MainLayout.razor → WelcomeTourService.Start"] =
                new(null,
                    "WelcomeTourService is AddScoped, so the tour it starts is this circuit's own. "
                    + "The button that reaches it is behind MayChangeInstallSettings anyway, "
                    + "because the same handler writes HasSeenWelcomeTour."),
            ["Components/Layout/MainLayout.razor → KeyboardShortcutService.TriggerRun"] =
                new(null,
                    "Ctrl+R on the app container. KeyboardShortcutService is AddScoped since round "
                    + "5 and is deliberately NOT forwarded by RegisterSharedSingletons, so a "
                    + "trigger cannot leave this circuit; each subscriber carries its own "
                    + "permission check. DiLifetimeCensusTests is what keeps that true — if the "
                    + "service goes back to AddSingleton this excuse is void and that test fails."),
            ["Components/Layout/MainLayout.razor → KeyboardShortcutService.TriggerExportPdf"] =
                new(null, "Ctrl+P. Same scoped bus and same reasoning as TriggerRun above."),
            ["Components/Layout/MainLayout.razor → KeyboardShortcutService.TriggerExportCsv"] =
                new(null, "Ctrl+E. Same scoped bus and same reasoning as TriggerRun above."),
            ["Components/Layout/MainLayout.razor → KeyboardShortcutService.TriggerCommandPalette"] =
                new(null,
                    "Ctrl+K. Same scoped bus; it replaced CommandPalette's public static Action, "
                    + "which was one process-wide slot owned by whichever circuit initialised last."),
            ["Components/Layout/MainLayout.razor → CorrelationIdAccessor.PushToLogContext"] =
                new(null, "AddScoped — pushes THIS circuit's correlation id into Serilog's LogContext."),
            ["Components/Layout/MainLayout.razor → NotificationChannelService.GetAlertWindows"] =
                new(null, "Read: the maintenance-window banner polls it every 30s. No write, no server."),
            ["Components/Layout/MainLayout.razor → ServerConnectionManager.GetConnections"] =
                new(null, "Read: counts configured servers to decide whether first-run setup is due."),
            ["Components/Layout/MainLayout.razor → UserSettingsService.GetEnableAnimations"] =
                new(null, "Read, to set this browser's no-animations body class."),
            ["Components/Layout/MainLayout.razor → UserSettingsService.GetHasSeenWelcomeTour"] =
                new(null, "Read, to decide whether the tour CTA is due."),
            ["Components/Layout/MainLayout.razor → UserSettingsService.GetLastSeenVersion"] =
                new(null, "Read: whether the operator has opted out of the what-is-new modal on update."),
            ["Components/Layout/MainLayout.razor → UserSettingsService.GetShowReleaseNotesOnUpdate"] =
                new(null, "Read: whether the operator has opted out of the what-is-new modal on update."),
            ["Components/Layout/MainLayout.razor → AppUserState.InitAsync"] =
                new(null, "Resolves THIS circuit's principal and role. Scoped; it is the gate's own setup."),
            ["Components/Layout/MainLayout.razor → AppUserState.IsAuthorized"] =
                new(null, "The gate itself (MayChangeInstallSettings), not a surface behind one."),
            ["Components/Layout/MainLayout.razor → IRbacEnforcementPostureAccessor.DescribeEnforcementPosture"] =
                new(null,
                    "Read: the global RBAC admission banner (security-8, 2026-08-31). The shell "
                    + "injects IRbacEnforcementPostureAccessor — a narrow read-only handle backed by "
                    + "the RbacService singleton — NOT RbacService itself, so the holder-pin's three "
                    + "administration pages (Login/Onboarding/Settings) stay exact and the banner "
                    + "cannot reach a fail-open gate call. Renders the COMPUTED posture — "
                    + "Headline/Detail from the same object the log and the Settings banner use — on "
                    + "every routed page when the posture is a PROBLEM, so a silent "
                    + "enforced→unenforced downgrade after a store is damaged is named to the "
                    + "operator rather than only logged. AMENDED 2026-09-07 (fresh-eyes ruling of "
                    + "2026-09-06): PostureKind.Off now gets a second, amber arm on the same read — "
                    + "an unconfigured install serves every caller from this machine as a full "
                    + "administrator, and that was visible only on a Settings page nobody opens. "
                    + "Enforcing still renders nothing. It reads the posture and navigates to "
                    + "Settings via a plain link; it changes nothing and gates nothing."),
            ["Components/Layout/MainLayout.razor → NavigationManager.NavigateTo"] =
                new(null, "Navigation only; every destination enforces its own gate."),
            ["Components/Layout/MainLayout.razor → NavigationManager.ToBaseRelativePath"] =
                new(null, "Read: decides whether the caller is on /servers, where the wizard is suppressed."),
            ["Components/Layout/MainLayout.razor → IJSRuntime.InvokeVoidAsync"] =
                new(null, "history.back/forward and a body class — this caller's own browser."),
            ["Components/Layout/MainLayout.razor → ILogger<MainLayout>.LogError"] =
                new(null, "Error-boundary record for this circuit's unhandled UI error."),
            ["Components/Layout/MainLayout.razor → ILogger<MainLayout>.LogWarning"] =
                new(null, "Same, on the path where the browser console mirror is unavailable."),

            // ══ Components/Layout/NavMenu.razor — rendered by MainLayout on every route ═══════

            ["Components/Layout/NavMenu.razor → UserSettingsService.SetExperimentalMode"] =
                new("settings",
                    "The Experimental switch. Install-wide persisted setting; THE CONTROL THE COLD "
                    + "GATE DROVE on 2026-08-01 from http://192.10.10.32:5182/scheduled-tasks — a "
                    + "page reading \"Scheduled tasks are restricted to Admin users\" — writing "
                    + "%APPDATA%\\SQLTriage\\user-settings.json, 2680→2681 B, md5 changed."),
            ["Components/Layout/NavMenu.razor → UserSettingsService.SetNotificationsEnabled"] =
                new("settings",
                    "The Notifications switch. Install-wide persisted setting, and "
                    + "OnNotificationsEnabledChanged redraws it in every live circuit. Silencing "
                    + "toasts also silences alert toasts."),
            ["Components/Layout/NavMenu.razor → UserSettingsService.SetColorBlindMode"] =
                new("settings", "The Color-Blind switch: install-wide persisted palette, broadcast to open circuits."),
            ["Components/Layout/NavMenu.razor → UserSettingsService.SetNarrationMode"] =
                new("settings", "The Narration switch: install-wide persisted copy mode, broadcast to open circuits."),
            ["Components/Layout/NavMenu.razor → ToastService.Enabled="] =
                new("settings",
                    "A property assignment, which no call-shaped scan would have seen. ToastService "
                    + "is a SINGLETON, so this mutes or unmutes toasts for the whole process. "
                    + "Written from the gated Notifications handler, and once more in "
                    + "OnInitializedAsync as a startup sync to the persisted value."),

            ["Components/Layout/NavMenu.razor → DatabaseAvailabilityService.InvalidateCache"] =
                new(null,
                    "Drops the singleton's cached \"does SQLWATCH/PerformanceMonitor exist\" answer "
                    + "when the current server changes. Reached only from OnServerChanged / "
                    + "OnInstanceChanged — subscription handlers, not a control — and its whole "
                    + "effect is that the next read re-queries. It cannot make a stale answer."),
            ["Components/Layout/NavMenu.razor → DatabaseAvailabilityService.DatabaseExistsAsync"] =
                new(null, "Read: decides whether the SQLWATCH / Performance Monitor nav groups render."),
            ["Components/Layout/NavMenu.razor → PanelMetricsService.GetMetric"] =
                new(null, "Read: the per-panel metric badge in the nav."),
            ["Components/Layout/NavMenu.razor → IFeatureGate.IsEnabled"] =
                new(null, "Read: feature-flag lookup deciding which nav entries render."),
            ["Components/Layout/NavMenu.razor → IFeatureGate.IsSoftEnabled"] =
                new(null, "Read: same, for the soft-enabled (preview) tier."),
            ["Components/Layout/NavMenu.razor → UserSettingsService.GetNoPantsMode"] =
                new(null, "Read: mirrors the persisted flag so the nav can gate the Apply category."),
            ["Components/Layout/NavMenu.razor → UserSettingsService.GetExperimentalMode"] =
                new(null, "Read: initial state of the Experimental switch."),
            ["Components/Layout/NavMenu.razor → UserSettingsService.GetShowMaturityRoadmap"] =
                new(null, "Read: decides whether the roadmap nav entry renders."),
            ["Components/Layout/NavMenu.razor → UserSettingsService.GetColorBlindMode"] =
                new(null, "Read: initial state of the Color-Blind switch."),
            ["Components/Layout/NavMenu.razor → UserSettingsService.GetNarrationMode"] =
                new(null, "Read: initial state of the Narration switch."),
            ["Components/Layout/NavMenu.razor → UserSettingsService.GetNotificationsEnabled"] =
                new(null, "Read: initial state of the Notifications switch."),
            ["Components/Layout/NavMenu.razor → UserSettingsService.OnNoPantsModeChanged"] =
                new(null, "An event SUBSCRIPTION (+=/-=), so the nav re-gates when the mode changes elsewhere."),
            ["Components/Layout/NavMenu.razor → AppUserState.InitAsync"] =
                new(null, "Resolves THIS circuit's role before the nav decides what to render."),
            ["Components/Layout/NavMenu.razor → AppUserState.IsAuthorized"] =
                new(null, "The gate itself (MayChangeInstallSettings)."),
            ["Components/Layout/NavMenu.razor → NavigationManager.NavigateTo"] =
                new(null, "Navigation only; every destination enforces its own gate."),
            ["Components/Layout/NavMenu.razor → IJSRuntime.InvokeVoidAsync"] =
                new(null, "Toggles the colourblind body class in this caller's own browser."),

            // ══ Components/Layout/StatusBar.razor — rendered by MainLayout on every route ═════

            ["Components/Layout/StatusBar.razor → UserSettingsService.SetEnableAnimations"] =
                new("settings", "The animations switch: an install-wide persisted setting on the status bar."),
            ["Components/Layout/StatusBar.razor → UserSettingsService.GetEnableAnimations"] =
                new(null, "Read: the persisted animations setting, for this switch's initial state."),
            ["Components/Layout/StatusBar.razor → AppUserState.IsAuthorized"] =
                new(null, "The gate itself (MayChangeInstallSettings)."),
            ["Components/Layout/StatusBar.razor → IJSRuntime.InvokeVoidAsync"] =
                new(null, "Applies the no-animations body class in this caller's own browser."),

            // ══ Components/Layout/DashboardToolbar.razor ═════════════════════════════════════

            ["Components/Layout/DashboardToolbar.razor → UserSettingsService.SetRefreshInterval"] =
                new("settings", "Writes the install's persisted dashboard refresh interval."),
            ["Components/Layout/DashboardToolbar.razor → AutoRefreshService.SetInterval"] =
                new("settings",
                    "AutoRefreshService is a SINGLETON: this retimes the refresh timer for the "
                    + "whole process, so one caller's dropdown changes every other circuit's "
                    + "dashboard cadence. Also called once in OnInitialized as a startup sync to "
                    + "the persisted value, which is the same number for every caller."),
            ["Components/Layout/DashboardToolbar.razor → UserSettingsService.SetDefaultTimeRange"] =
                new("settings", "Writes the install's persisted DEFAULT dashboard time range."),

            ["Components/Layout/DashboardToolbar.razor → PrintService.PrintToPdfAsync"] =
                new("export_data",
                    "Renders the dashboard and WRITES A PDF ON THE HOST. That is an export of data "
                    + "to a file, so it sits on export_data rather than on settings."),
            ["Components/Layout/DashboardToolbar.razor → PrintService.PrintViaBrowserAsync"] =
                new("export_data", "The fallback path of the same button: opens the browser print dialog."),

            ["Components/Layout/DashboardToolbar.razor → IServerContextService.SetServerAsync"] =
                new("execute_checks",
                    "The Instance dropdown. ServerContextService is scoped, but it writes "
                    + "ServerConnectionManager.CurrentServer, which is a SINGLETON — so this "
                    + "retargets the instance the NEXT operator's run will hit. RULED 2026-08-02: "
                    + "a caller who may run nothing may not retarget what somebody else runs."),
            ["Components/Layout/DashboardToolbar.razor → ServerConnectionManager.SetCurrentServer"] =
                new(null,
                    "The BOOTSTRAP call in OnInitialized only — it fires when nothing has been "
                    + "selected yet and picks ServerConnections[0], the same value for every "
                    + "caller. No caller input reaches it. This entry used to go on to say it "
                    + "\"can only move the target from none to the first enabled connection\", "
                    + "which was a claim about code somewhere else that nothing checked — and it "
                    + "was NOT true in a second tab, where this component's own state is empty "
                    + "while the process is already connected. The call now passes "
                    + "ConnectionRetargetGrant.Establish and ServerConnectionManager ENFORCES the "
                    + "claim: with a server already selected the write is refused, and the "
                    + "dropdown is set from what actually holds rather than from what was asked "
                    + "for. The caller-directed path is IServerContextService.SetServerAsync "
                    + "above, which is gated."),
            ["Components/Layout/DashboardToolbar.razor → ServerConnectionManager.GetEnabledConnections"] =
                new(null, "Read: the enabled connections that populate the instance dropdown."),
            ["Components/Layout/DashboardToolbar.razor → UserSettingsService.GetRefreshInterval"] =
                new(null, "Read: initial value of the refresh select."),
            ["Components/Layout/DashboardToolbar.razor → UserSettingsService.GetDataSource"] =
                new(null,
                    "Read. The matching WRITE (SetDataSource) was removed 2026-08-02: its select "
                    + "had been commented out of the markup while the handler stayed live in the "
                    + "code block with no call site — an install-wide write hiding on the shell "
                    + "with nothing rendering it."),
            ["Components/Layout/DashboardToolbar.razor → ToastService.ShowSuccess"] =
                new(null,
                    "Reports the result of THIS caller's export. ToastService is a singleton so "
                    + "the toast is raised in every open circuit — cosmetic, carries a file name, "
                    + "and only reachable behind MayExportData."),
            ["Components/Layout/DashboardToolbar.razor → ToastService.ShowError"] =
                new(null, "Same shape: the failure message for that same gated export."),
            ["Components/Layout/DashboardToolbar.razor → ToastService.ShowInfo"] =
                new(null, "Same shape: the \"browser print dialog opened\" notice for that same gated export."),
            ["Components/Layout/DashboardToolbar.razor → AppUserState.IsAuthorized"] =
                new(null, "The gates themselves (MayChangeInstallSettings / MayExportData / MaySelectInstance)."),

            // ══ Components/Layout/PowerChip.razor ════════════════════════════════════════════

            ["Components/Layout/PowerChip.razor → PowerEstimateService.GetEstimateAsync"] =
                new(null, "Read: the licensing/power estimate shown on the status-bar chip."),
            ["Components/Layout/PowerChip.razor → ServerConnectionManager.GetConnections"] =
                new(null, "Read: resolves the current connection's display name for that chip."),

            // ══ Components/Shared/GlobalServerSelector.razor — MainLayout renders it ═════════

            ["Components/Shared/GlobalServerSelector.razor → IServerContextService.SetServerAsync"] =
                new("execute_checks",
                    "The top-bar server picker, rendered on EVERY route including the AccessDenied "
                    + "pages. Writes the process-wide current server. Same ruling and same "
                    + "permission as the DashboardToolbar instance dropdown, and the grant it "
                    + "hands SetServerAsync names that permission. The bootstrap call in "
                    + "OnInitialized — first configured server when nothing is selected — is "
                    + "outside the gate and passes ConnectionRetargetGrant.Establish instead, "
                    + "which the MANAGER checks: its \"nothing is selected\" test used to read "
                    + "this scoped service's own CurrentServerId, empty in every new tab, so the "
                    + "bootstrap moved everybody's connection onto this circuit's first server."),
            ["Components/Shared/GlobalServerSelector.razor → ServerConnectionManager.GetConnections"] =
                new(null, "Read: populates the picker and resolves the read-only name a denied caller sees."),
            ["Components/Shared/GlobalServerSelector.razor → AppUserState.IsAuthorized"] =
                new(null, "The gate itself (MaySelectInstance) — the decision, not a surface behind it."),
            ["Components/Shared/GlobalServerSelector.razor → NavigationManager.NavigateTo"] =
                new(null, "The \"Manage Servers\" button; /servers enforces manage_servers itself (round 3)."),

            // ══ Components/Shared/OnboardingWizard.razor — MainLayout renders it ═════════════

            ["Components/Shared/OnboardingWizard.razor → AppUserState.IsAuthorized"] =
                new(null, "The gate itself (MayRunOnboarding), added 2026-08-02 — it had none."),
            ["Components/Shared/OnboardingWizard.razor → NavigationManager.NavigateTo"] =
                new(null,
                    "Sends a fresh install to /servers, /audit or /roadmap. Navigation only, and "
                    + "each destination enforces its own gate; the wizard itself is now behind "
                    + "MayRunOnboarding as well."),

            // ══ Components/Shared/ServerModeToggle.razor — StatusBar renders it ══════════════

            ["Components/Shared/ServerModeToggle.razor → ServerModeService.StartAsync"] =
                new("settings",
                    "Starts the LAN listener. Round 4's finding: rendered on every AccessDenied "
                    + "page in the app."),
            ["Components/Shared/ServerModeToggle.razor → ServerModeService.StopAsync"] =
                new("settings", "Stops the LAN listener — the other half of the same control."),
            ["Components/Shared/ServerModeToggle.razor → AppUserState.IsAuthorized"] =
                new(null, "The gate itself (MayControlServerMode) — the decision, not a surface behind it."),
            ["Components/Shared/ServerModeToggle.razor → IJSRuntime.InvokeVoidAsync"] =
                new(null, "Copies the server URL to this caller's own clipboard."),

            // ══ Components/Shared/WelcomeTourOverlay.razor ═══════════════════════════════════

            ["Components/Shared/WelcomeTourOverlay.razor → WelcomeTourService.Next"] =
                new(null, "WelcomeTourService is AddScoped — the tour is this circuit's own UI state."),
            ["Components/Shared/WelcomeTourOverlay.razor → WelcomeTourService.Previous"] =
                new(null, "Same scoped service: steps this circuit's own tour backwards."),
            ["Components/Shared/WelcomeTourOverlay.razor → WelcomeTourService.Stop"] =
                new(null, "Same scoped service, same reason: dismissing this circuit's tour."),
            ["Components/Shared/WelcomeTourOverlay.razor → IJSRuntime.InvokeVoidAsync"] =
                new(null, "Registers/clears this component's own DotNetObjectReference for arrow keys."),

            // ══ Newly VISIBLE 2026-08-06: the method-group / value-position shape ═════════════
            //
            // EdgesIn learned to see `alias.Member` handed somewhere as a VALUE — no parentheses
            // of its own. WelcomeTourOverlay's `@onclick="Tour.ToggleAutoAdvance"` is the shape
            // that mattered: a SERVICE METHOD wired straight to a DOM event, which every earlier
            // pattern in the scanner missed because each of them needs a '(' , '=' or '+=' right
            // after the member. The rest below are property READS in the same positions, and they
            // land here for the register's stated reason: the instrument asks nothing about the
            // member's name, so a read costs one line and a false negative has cost five rounds.

            ["Components/Shared/WelcomeTourOverlay.razor → WelcomeTourService.ToggleAutoAdvance"] =
                new(null,
                    "A METHOD GROUP on the pause/play button (@onclick=\"Tour.ToggleAutoAdvance\", "
                    + "line 40). Same AddScoped service as Next/Previous/Stop above, so it flips "
                    + "this circuit's own tour; recorded because the SHAPE was invisible, not "
                    + "because this particular call is dangerous."),
            ["Components/Shared/WelcomeTourOverlay.razor → WelcomeTourService.IsActive"] =
                new(null, "Read: whether this circuit's tour is running, in the render condition."),
            ["Components/Shared/WelcomeTourOverlay.razor → WelcomeTourService.CurrentIndex"] =
                new(null, "Read: which stop is showing, for the \"Stop n of N\" caption and the progress bar."),
            ["Components/Shared/WelcomeTourOverlay.razor → WelcomeTourService.TotalStops"] =
                new(null, "Read: how many stops the tour has, the N in that caption and the divisor of the progress percentage."),
            ["Components/Shared/WelcomeTourOverlay.razor → WelcomeTourService.AutoAdvance"] =
                new(null, "Read: the pause/play button's own icon, title and label."),

            ["Components/Layout/MainLayout.razor → NavigationManager.Uri"] =
                new(null,
                    "Read: the current URL, used as the @key on the page-fade wrapper so a route "
                    + "change re-runs the animation, and by ToBaseRelativePath for the active-nav "
                    + "comparison. It navigates nothing."),

            ["Components/Layout/NavMenu.razor → ConnectionHealthService.AllStatuses"] =
                new(null, "Read: the per-connection health map behind the nav's online/offline chip."),
            ["Components/Layout/NavMenu.razor → ConnectionHealthService.OnlineCount"] =
                new(null, "Read: how many connections answered, the online half of that chip."),
            ["Components/Layout/NavMenu.razor → ConnectionHealthService.OfflineCount"] =
                new(null, "Read: the offline half of the same chip."),
            ["Components/Layout/NavMenu.razor → QuickCheckStateService.IsRunning"] =
                new(null, "Read: renders the spinner beside the Quick Check nav item while one runs."),
            ["Components/Layout/NavMenu.razor → FullAuditStateService.IsRunning"] =
                new(null, "Read: the same spinner for a full audit."),
            ["Components/Layout/NavMenu.razor → VulnerabilityAssessmentStateService.IsRunning"] =
                new(null, "Read: the same spinner for a vulnerability assessment."),
            ["Components/Layout/NavMenu.razor → IBundleAccessor.IsUnlocked"] =
                new(null, "Read: whether the corpus bundle is unlocked, cached into _bundleUnlocked for the nav."),
            ["Components/Layout/NavMenu.razor → IBundleAccessor.Tier"] =
                new(null,
                    "Read: the licensed tier, cached into _bundleTier and compared when deciding "
                    + "whether a Full-tier-only nav entry is shown. A read of the licence state, "
                    + "not a change to it — the licensing files belong to the unmerged licence "
                    + "wave and are untouched here."),
            ["Components/Layout/NavMenu.razor → IConsolidationModelProvider.IsUnlocked"] =
                new(null, "Read: whether the consolidation model is available, for the same nav filtering."),

            ["Components/Layout/PowerChip.razor → IServerContextService.CurrentServerId"] =
                new(null, "Read: which server the chip should estimate for."),

            ["Components/Layout/DashboardToolbar.razor → ServerConnectionManager.CurrentServer"] =
                new(null, "Read: the connection the toolbar labels and passes to its own handlers."),

            ["Components/Shared/GlobalServerSelector.razor → IServerContextService.CurrentServerId"] =
                new(null, "Read: which option the picker shows as selected, and the bootstrap test in OnInitialized."),
            ["Components/Shared/GlobalServerSelector.razor → IServerContextService.CurrentDatabase"] =
                new(null, "Read: the database name rendered beside the server name."),

            ["Components/Shared/ServerModeToggle.razor → ServerModeService.IsRunning"] =
                new(null,
                    "Read: which arm of the toggle to render. It is INSIDE the settings boundary "
                    + "with the rest of the component's markup, which is what "
                    + "ServerModeToggleIsGatedAndStillRenderedByTheLayout asserts."),
            ["Components/Shared/ServerModeToggle.razor → ServerModeService.Url"] =
                new(null,
                    "Read: the LOOPBACK url shown and opened by the button beside it, behind the same "
                    + "boundary. It said \"the LAN URL\" until 2026-08-25, and that was accurate then and "
                    + "was the defect: the toggle opened http://{machine}:{port}, a non-loopback origin "
                    + "InteractiveAppAdmission refuses to an unauthenticated caller, so the control "
                    + "locked the operator out of a fresh install. The LAN form is now LanUrl, below."),
            ["Components/Shared/ServerModeToggle.razor → ServerModeService.LanUrl"] =
                new(null,
                    "Read: the address for OTHER machines, rendered as text beside the local one so the "
                    + "two are labelled rather than conflated. Discloses this box's hostname to whoever "
                    + "can already see this component, which is a caller who holds \"settings\" (the "
                    + "whole component is inside that boundary) and who is therefore already reading the "
                    + "server inventory two panels away. No control, no call, no mutation."),

            // ══ Components/Shared/ToastContainer.razor ═══════════════════════════════════════

            ["Components/Shared/ToastContainer.razor → ToastService.MuteFor"] =
                new(null,
                    "Flood control, not a control: reached only from ShowToast when five alert "
                    + "toasts arrive within five seconds, and it mutes for five minutes. No control "
                    + "renders it - the disclosure badge added 2026-08-28 is a status line with no "
                    + "button on it, precisely so nothing on every route can drive this. It IS "
                    + "process-wide (ToastService is a singleton), so it is on the record rather "
                    + "than excused."),
            ["Components/Shared/ToastContainer.razor → ToastService.IsMuted"] =
                new(null,
                    "Read: whether to paint the mute disclosure badge at all, and (in ShowToast) "
                    + "whether the flood threshold still needs to engage the mute. Reads a "
                    + "singleton's state; writes nothing."),
            ["Components/Shared/ToastContainer.razor → ToastService.MutedUntil"] =
                new(null,
                    "Read: how many minutes of the mute are left, for the badge's sentence and for "
                    + "the Task.Delay that repaints the badge away when the mute lapses."),
            ["Components/Shared/ToastContainer.razor → ToastService.SuppressedAlertCount"] =
                new(null,
                    "Read: how many alerts the mute has swallowed, which is the number the badge "
                    + "exists to state. A suppression an operator cannot size is indistinguishable "
                    + "from silence."),
            ["Components/Shared/ToastContainer.razor → ToastService.ShowSuppressionNotice"] =
                new(null,
                    "Raises ONE non-alert warning toast saying that alert toasts are now muted, "
                    + "reached only from the flood branch beside MuteFor above. It deliberately "
                    + "bypasses both suppression gates - a notice about suppression cannot travel "
                    + "through the mechanism doing the suppressing - and it is process-wide for the "
                    + "same reason every toast is: it paints text, and no handler runs from it."),
            ["Components/Shared/ToastContainer.razor → ToastService.MuteStateChanged"] =
                new(null,
                    "Subscription on a SINGLETON, same disclosure-not-authorization shape as the "
                    + "OnShow entry below: the service announces that the withheld count moved, and "
                    + "this circuit repaints its own badge. Without it the badge froze at \"No "
                    + "alerts have been withheld yet.\" for the whole five-minute flood, because a "
                    + "dropped alert returns before OnShow is raised."),

            // ══ Components/Shared/FreeStateBanner.razor ══════════════════════════════════════

            ["Components/Shared/FreeStateBanner.razor → CheckRepositoryService.LoadChecksAsync"] =
                new(null,
                    "Cold-start hydrate from OnInitializedAsync: loads this install's own check "
                    + "corpus into the singleton repository so the banner can count enabled checks. "
                    + "Reaches no SQL Server and writes no file; no control renders it."),
            ["Components/Shared/FreeStateBanner.razor → CheckRepositoryService.GetEnabledChecks"] =
                new(null, "Read: the count shown in the free-tier banner."),
            ["Components/Shared/FreeStateBanner.razor → UserSettingsService.GetFastAppLoad"] =
                new(null, "Read: whether to yield before the hydrate above."),
            ["Components/Shared/FreeStateBanner.razor → NavigationManager.NavigateTo"] =
                new(null, "The banner's Settings link; /settings enforces its own gate."),
            ["Components/Shared/FreeStateBanner.razor → IJSRuntime.InvokeVoidAsync"] =
                new(null, "Dismiss animation in this caller's own browser."),

            // ══ Components/Shared/FullAuditUpsellPill.razor ══════════════════════════════════

            ["Components/Shared/FullAuditUpsellPill.razor → CheckRepositoryService.GetEnabledChecks"] =
                new(null, "Read: the check count shown on the pill."),
            ["Components/Shared/FullAuditUpsellPill.razor → NavigationManager.NavigateTo"] =
                new(null, "Navigation only; the destination enforces its own gate."),

            // ══ Components/Shared/CommandPalette.razor ═══════════════════════════════════════

            ["Components/Shared/CommandPalette.razor → IFeatureGate.IsEnabled"] =
                new(null, "Read: filters the palette's entries by feature flag."),
            ["Components/Shared/CommandPalette.razor → NavigationManager.NavigateTo"] =
                new(null, "The palette navigates; every destination enforces its own gate."),
            ["Components/Shared/CommandPalette.razor → IJSRuntime.InvokeVoidAsync"] =
                new(null,
                    "Registers THIS component's DotNetObjectReference with the per-page "
                    + "window.sqltriageCommandPalette registry. Round 5 replaced a static "
                    + "[JSInvokable] with exactly this, so one browser's Ctrl+K can no longer open "
                    + "another's palette."),

            // ══ Components/Shared/ShellGate.razor — THE BOUNDARY (round 7) ═══════════════════
            //
            // Both edges moved HERE from MainLayout and NavMenu, which is the point of a single
            // boundary: the permission decision is made in one file instead of once per control,
            // so there is one place to read and one place to get wrong.

            ["Components/Shared/ShellGate.razor → AppUserState.IsAuthorized"] =
                new(null,
                    "The boundary's own decision. Every gated control in the shell now renders "
                    + "inside this component, so this call IS the gate rather than a surface "
                    + "behind one."),
            ["Components/Shared/ShellGate.razor → AppUserState.IsAuthorizedWithBreakGlass"] =
                new(null,
                    "The same decision with round 1's hatch, taken only when a boundary sets "
                    + "BreakGlass. Scoped by ruling to Settings and Onboarding, and every use is "
                    + "named on BreakGlassBoundaries with the reason it is one of those two — "
                    + "today the top-bar gear and the Configuration nav category, both of which "
                    + "are the console route into the page that can undo a bad RBAC config."),

            // ══ EVENT SUBSCRIPTIONS (+= / -=) ═══════════════════════════════════════════════
            //
            // A subscription is an edge and is enumerated like any other, because "it only
            // subscribes" is precisely the kind of claim that has been wrong before. What makes
            // these safe is the DIRECTION: the shell is the LISTENER, so the handler that runs is
            // this circuit's own re-render. The dangerous direction — this circuit RAISING an
            // event whose subscribers live in other circuits — is the separate question asked by
            // DiLifetimeCensusTests.NothingTheAlwaysRenderedLayoutCallsRaisesEventsInOtherCircuits,
            // which reads THIS register too.

            ["Components/Layout/MainLayout.razor → AutoUpdateService.OnUpdateAvailable"] =
                new(null, "Subscription: shows this circuit's update banner when a new build is found."),
            ["Components/Layout/MainLayout.razor → NavigationManager.LocationChanged"] =
                new(null,
                    "Subscription (scoped NavigationManager): re-renders when the route moves, so "
                    + "the first-run wizard stops rendering once the caller reaches /servers."),

            ["Components/Layout/NavMenu.razor → ServerConnectionManager.OnConnectionChanged"] =
                new(null, "Subscription: re-reads the SQLWATCH/PM nav groups when the current server changes."),
            ["Components/Layout/NavMenu.razor → ConnectionHealthService.OnStatusChanged"] =
                new(null, "Subscription: redraws the server-health strip."),
            ["Components/Layout/NavMenu.razor → GlobalInstanceSelector.OnInstanceChanged"] =
                new(null, "Subscription: same re-read as OnConnectionChanged, for the instance selector."),
            ["Components/Layout/NavMenu.razor → QuickCheckStateService.StateChanged"] =
                new(null, "Subscription: the nav spinner follows the ESTATE's audit run, by design."),
            ["Components/Layout/NavMenu.razor → FullAuditStateService.StateChanged"] =
                new(null, "Subscription: same spinner, raw-diagnostics run."),
            ["Components/Layout/NavMenu.razor → VulnerabilityAssessmentStateService.StateChanged"] =
                new(null, "Subscription: same spinner, vulnerability assessment."),
            ["Components/Layout/NavMenu.razor → IBundleAccessor.BundleStateChanged"] =
                new(null, "Subscription: redraws the tier badge when the licence bundle state changes."),
            ["Components/Layout/NavMenu.razor → IFeatureGate.Changed"] =
                new(null, "Subscription: re-evaluates which nav entries render."),
            ["Components/Layout/NavMenu.razor → UserSettingsService.OnShowMaturityRoadmapChanged"] =
                new(null, "Subscription: shows/hides the roadmap nav entry when Settings changes it."),

            ["Components/Layout/PowerChip.razor → IServerContextService.OnServerChanged"] =
                new(null, "Subscription (scoped service): recomputes this circuit's power chip."),

            ["Components/Shared/GlobalServerSelector.razor → IServerContextService.OnServerChanged"] =
                new(null, "Subscription (scoped service): redraws the picker's current value."),
            ["Components/Shared/GlobalServerSelector.razor → IServerContextService.OnDatabaseChanged"] =
                new(null, "Subscription: redraws the \"(database)\" suffix beside the picker."),

            ["Components/Shared/ServerModeToggle.razor → ServerModeService.OnStateChanged"] =
                new(null, "Subscription: the running/stopped state this toggle displays."),

            ["Components/Shared/CommandPalette.razor → KeyboardShortcutService.OnCommandPaletteRequested"] =
                new(null,
                    "Subscription on the SCOPED shortcut bus — the round-5 replacement for the "
                    + "process-wide static Action. Ctrl+K reaches only this circuit's palette."),

            ["Components/Shared/FreeStateBanner.razor → IBundleAccessor.BundleStateChanged"] =
                new(null, "Subscription: re-renders the banner when the licence bundle state changes."),

            ["Components/Shared/ToastContainer.razor → ToastService.OnShow"] =
                new(null,
                    "Subscription on a SINGLETON: a toast raised anywhere is painted in every open "
                    + "circuit. Recorded rather than excused — it is a disclosure surface, not an "
                    + "authorization one: it paints text somebody else's action produced, and no "
                    + "handler of this circuit's runs as a result."),

            ["Components/Shared/WelcomeTourOverlay.razor → WelcomeTourService.StateChanged"] =
                new(null, "Subscription on a SCOPED service: this circuit's own tour position."),
        };

        // ══ ROUND 7 ═════════════════════════════════════════════════════════════════════════
        //
        // The three registers below belong to the BOUNDARY census
        // (RbacShellBoundaryCensusTests), which is now the decider. Edges above stays, and still
        // fails on an unregistered service edge, but it is defence-in-depth: it was defeated on
        // 2026-08-02 by `var s = UserSettings;` and no amount of widening fixes the class of
        // defect that one line belongs to.

        /// <summary>
        /// INTERACTIVE CONTROLS IN THE SHELL THAT DELIBERATELY RENDER TO EVERYBODY.
        ///
        /// <para>An entry is a written claim that the control is INERT: that it moves this
        /// circuit's own view and touches no service, no install setting and no other caller.
        /// The key carries the exact expression, so a control whose handler text differs by one
        /// character is a different key and is therefore unreviewed — an exemption cannot be
        /// inherited by the next thing somebody drops beside it.</para>
        ///
        /// <para>Nothing here inspects the expression; it is an identity, not a judgement. What
        /// keeps these honest is the second instrument: if an "inert" handler ever starts calling
        /// an injected service, <see cref="Edges"/> sees a new edge and
        /// <c>EveryShellEdgeIsOnTheRegister</c> fails. Boundary census for the render, edge census
        /// for the reach.</para>
        /// </summary>
        internal static readonly Dictionary<string, string> InertControls = new(StringComparer.Ordinal)
        {
            // ══ Components/Layout/MainLayout.razor ═══════════════════════════════════════════

            [@"Components/Layout/MainLayout.razor → @onkeydown=""HandleKeyDown"""] =
                "The keyboard-shortcut dispatcher on the app container. It cannot be wrapped: the "
                + "element it sits on CONTAINS the whole application, so a boundary here would gate "
                + "the app rather than a control. What makes it safe is round 5's lifetime fix — "
                + "KeyboardShortcutService is AddScoped and deliberately NOT forwarded by "
                + "RegisterSharedSingletons, so a keystroke reaches only subscribers in the circuit "
                + "that pressed it, and each subscriber evaluates its own permission against the "
                + "identity that acted. DiLifetimeCensusTests is what keeps that true; if the "
                + "service goes back to AddSingleton this exemption is void and that test fails.",

            [@"Components/Layout/MainLayout.razor → ShortcutsDialog.OnClose=""@(() => showShortcuts = false)"""] =
                "Sets a private bool on this circuit's MainLayout so the keyboard-shortcuts "
                + "reference card stops rendering. No service, no persistence, no other circuit.",

            [@"Components/Layout/MainLayout.razor → @onclick=""() => _updateAvailable = false"""] =
                "Dismisses the \"update available\" banner for this circuit only — a private bool, "
                + "re-raised next time AutoUpdateService reports a new build. Two controls share "
                + "this expression (the link and the X) and therefore this one entry.",

            [@"Components/Layout/MainLayout.razor → @onclick=""GoBack"""] =
                "history.back in THIS caller's browser, via IJSRuntime. It moves nobody else and "
                + "every route it can land on enforces its own gate.",
            [@"Components/Layout/MainLayout.razor → @onclick=""GoForward"""] =
                "history.forward in THIS caller's browser. Same reasoning as GoBack above.",

            [@"Components/Layout/MainLayout.razor → @onclick=""() => Navigation.NavigateTo(""/settings"")"""] =
                "The user/role chip in the top bar. Navigation only: it renders /settings, which "
                + "carries its own Admin gate and shows AccessDenied to a caller who may not be "
                + "there. Hiding it would hide the ROLE BADGE beside it, which is information a "
                + "denied caller is entitled to. NOTE the gear icon two lines above is a separate "
                + "control with the same destination and is NOT exempt — it is inside a "
                + "<ShellGate BreakGlass> because it is the console route into Settings.",

            [@"Components/Layout/MainLayout.razor → @onclick=""() => _pageErrorBoundary?.Recover()"""] =
                "Re-renders the page this circuit's ErrorBoundary caught an exception from. The "
                + "page it recovers is the page the caller was already permitted to reach.",
            [@"Components/Layout/MainLayout.razor → @onclick=""() => Navigation.NavigateTo(RouteConstants.Guide)"""] =
                "The error boundary's \"Go to Dashboard\" escape. Navigation only; the destination "
                + "enforces its own gate.",

            // ══ Components/Layout/NavMenu.razor ══════════════════════════════════════════════
            //
            // The four category rails set a private string that chooses which nav list this
            // circuit's sidebar shows. Every entry INSIDE those lists is a NavLink to a route that
            // enforces its own gate, and the two categories that are themselves privileged
            // (Config, Apply) are the ones inside the boundary.

            [@"Components/Layout/NavMenu.razor → @onclick=""@(() => SetCategory(""Governance""))"""] =
                "Selects the Governance nav list for this circuit — a private string. The entries "
                + "it reveals are NavLinks whose routes each enforce their own gate.",
            [@"Components/Layout/NavMenu.razor → @onclick=""@(() => SetCategory(""Operations""))"""] =
                "Selects the Operations nav list for this circuit. Same private string, same "
                + "per-route gates on everything it reveals.",
            [@"Components/Layout/NavMenu.razor → @onclick=""@(() => SetCategory(""Diagnostics""))"""] =
                "Selects the Diagnostics nav list for this circuit. Same private string, same "
                + "per-route gates on everything it reveals.",
            [@"Components/Layout/NavMenu.razor → @onclick=""@(() => SetCategory(""Intelligence""))"""] =
                "Selects the Intelligence nav list for this circuit. Same private string, same "
                + "per-route gates on everything it reveals.",

            [@"Components/Layout/NavMenu.razor → @onclick=""ToggleCollapse"""] =
                "Collapses the sidebar to its icon rail. A private bool on this circuit's NavMenu; "
                + "nothing is persisted and no other caller's sidebar moves.",
            [@"Components/Layout/NavMenu.razor → @onclick=""GoToTierSettings"""] =
                "The licence-tier badge. Navigation to /settings, which enforces its own gate.",
            [@"Components/Layout/NavMenu.razor → @onclick=""() => _healthExpanded = !_healthExpanded"""] =
                "Expands the server-health strip from problem-servers-only to all servers. A "
                + "private bool; the health data itself is already rendered to this caller either "
                + "way, so this changes how many rows are shown and nothing else.",
            [@"Components/Layout/NavMenu.razor → @onclick=""() => _healthExpanded = true"""] =
                "The \"+N more offline\" line, the same private bool as the header row above.",

            // ══ Components/Layout/StatusBar.razor ════════════════════════════════════════════
            //
            // THE FILE THE ROUND-6 EXPLOIT WAS PLANTED IN. Its one acting control (animations) is
            // inside a boundary; the three below open and close a popover holding two external
            // links, and they are named individually rather than by "the feedback area" so that a
            // fourth control appearing in that popover would still be unreviewed and still fail.

            [@"Components/Layout/StatusBar.razor → @onclick=""() => _showFeedback = !_showFeedback"""] =
                "Opens/closes the feedback popover — a private bool on this circuit's StatusBar. "
                + "The popover contains two anchors to github.com and nothing else.",
            [@"Components/Layout/StatusBar.razor → @onclick=""() => _showFeedback = false"""] =
                "Closes the feedback popover when one of its two GitHub links is followed. Same "
                + "private bool; both links share this expression and therefore this entry.",

            // ══ Components/Shared/CommandPalette.razor ═══════════════════════════════════════

            [@"Components/Shared/CommandPalette.razor → @onclick=""Close"""] =
                "Closes this circuit's palette (a private bool) when the overlay is clicked.",
            [@"Components/Shared/CommandPalette.razor → @onclick=""(() => {})"""] =
                "An empty handler on the palette body. It exists only so a click inside the panel "
                + "is not the overlay's click; it runs no code at all.",
            [@"Components/Shared/CommandPalette.razor → @onkeydown=""HandleKeyDown"""] =
                "Arrow keys and Enter move _selectedIndex and then navigate. Navigation only — "
                + "every route the palette lists enforces its own gate — and the palette's entries "
                + "are already filtered by build-profile module presence.",
            [@"Components/Shared/CommandPalette.razor → @bind=""_query"""] =
                "The palette's search box. Filters an in-memory list of page names in this "
                + "circuit; it is not sent anywhere and reaches no service.",
            [@"Components/Shared/CommandPalette.razor → @onclick=""() => NavigateTo(item.Route)"""] =
                "Navigates to the chosen page. Navigation only; the destination enforces its gate.",
            [@"Components/Shared/CommandPalette.razor → @onmouseenter=""() => _selectedIndex = index"""] =
                "Highlights the row under the cursor — a private int on this circuit's palette.",

            // ══ Banners, dialogs and the toast stack ═════════════════════════════════════════

            [@"Components/Shared/FreeStateBanner.razor → @onclick=""GoToSettings"""] =
                "The bundle-not-loaded banner's Settings link. Navigation only; /settings enforces "
                + "its own gate and the banner states a fact about the INSTALL, identical for "
                + "every viewer.",
            [@"Components/Shared/FullAuditUpsellPill.razor → @onclick=""GoToSettings"""] =
                "The free-tier pill's Settings link. Navigation only, same as the banner that "
                + "renders it. This component never ships in community builds.",

            [@"Components/Shared/GlobalServerSelector.razor → @onclick=""() => Navigation.NavigateTo(""/servers"")"""] =
                "The \"Manage Servers\" icon beside the picker. Navigation only: /servers is gated "
                + "on manage_servers and shows AccessDenied to a caller who may not be there. The "
                + "picker itself — which RETARGETS the process-wide current instance — is the "
                + "control beside it, and that one is inside a boundary.",

            [@"Components/Shared/ShortcutsDialog.razor → @onclick=""Close"""] =
                "Closes the keyboard-shortcuts reference card by raising OnClose to its host. The "
                + "dialog is a static table of key bindings; it reads nothing and writes nothing. "
                + "Both the backdrop and the X share this expression and this entry.",

            [@"Components/Shared/ToastContainer.razor → @onclick=""() => RemoveToast(toast.Id)"""] =
                "Dismisses one toast from THIS circuit's list. ToastService is a singleton and a "
                + "toast raised anywhere is painted everywhere — that disclosure is recorded on "
                + "the edge register — but removing it from this list removes it from this browser "
                + "only.",

            [@"Components/Shared/ToggleSwitch.razor → @onclick=""Toggle"""] =
                "THE GENERIC SWITCH, and the reason component-callback bindings are censused at "
                + "the USE SITE. Toggle flips a bool and raises ValueChanged; it has no idea what "
                + "it is switching, so a permission here would be a guess. The authority lives "
                + "where the callback is bound — NavMenu's four install-wide preference switches "
                + "carry no @onclick of their own and are gated as ToggleSwitch.ValueChanged "
                + "inside a <ShellGate Permission=\"settings\">.",

            [@"Components/Shared/WelcomeTourOverlay.razor → @onclick=""Tour.Stop"""] =
                "Stops the guided tour. WelcomeTourService is AddScoped, so the tour these four "
                + "controls drive is this circuit's own and no other caller's card moves.",
            [@"Components/Shared/WelcomeTourOverlay.razor → @onclick=""Tour.Previous"""] =
                "Previous stop of this circuit's own tour — AddScoped, as above.",
            [@"Components/Shared/WelcomeTourOverlay.razor → @onclick=""Tour.ToggleAutoAdvance"""] =
                "Pauses/resumes auto-advance on this circuit's own tour — AddScoped, as above.",
            [@"Components/Shared/WelcomeTourOverlay.razor → @onclick=""Tour.Next"""] =
                "Next stop of this circuit's own tour — AddScoped, as above.",

            // ══ Components/Shared/ReleaseNotesModal.razor ════════════════════════════════════
            //
            // The modal is FIELDS PLUS ONE CALLBACK. Its install-wide write (SetLastSeenVersion,
            // SetShowReleaseNotesOnUpdate) happens in MainLayout.OnReleaseNotesClosed, which is
            // guarded on MayChangeInstallSettings AND is only reachable because MainLayout renders
            // this modal inside <ShellGate Permission="settings">.

            [@"Components/Shared/ReleaseNotesModal.razor → @onclick=""Close"""] =
                "Raises OnClose(_dontShowAgain) to whichever host rendered the modal. In the shell "
                + "that host is MainLayout, behind a settings boundary, and its handler carries "
                + "the same gate. The X and the \"Got it\" button share this entry.",
            [@"Components/Shared/ReleaseNotesModal.razor → @bind=""_dontShowAgain"""] =
                "A private bool inside the modal, read once when Close raises OnClose. Nothing is "
                + "persisted here; the host decides whether it may write the install flag.",

            // ══ Components/Shared/PdfExportModal.razor ═══════════════════════════════════════
            //
            // A settings sheet for one export. Every control below writes a private field of the
            // modal; the only way out is Confirm, which raises OnConfirm to the host. In the shell
            // that host is DashboardToolbar, which renders the modal inside
            // <ShellGate Permission="export_data"> and guards OnPdfConfirmed on MayExportData.

            [@"Components/Shared/PdfExportModal.razor → @onclick=""Cancel"""] =
                "Closes the sheet without exporting. Three controls share the expression — the "
                + "overlay, the X and the Cancel button — and therefore this entry.",
            [@"Components/Shared/PdfExportModal.razor → @bind=""_stem"""] =
                "The output file name, a private string. It becomes a filename only if the host's "
                + "gated OnConfirm handler runs.",
            [@"Components/Shared/PdfExportModal.razor → @onclick=""() => _landscape = true"""] =
                "Orientation choice — a private bool on the sheet, meaningful only on Confirm.",
            [@"Components/Shared/PdfExportModal.razor → @onclick=""() => _landscape = false"""] =
                "Orientation choice — a private bool on the sheet, meaningful only on Confirm.",
            [@"Components/Shared/PdfExportModal.razor → @onclick=""() => _printBackgrounds = true"""] =
                "Print-backgrounds choice — a private bool on the sheet, applied only on Confirm.",
            [@"Components/Shared/PdfExportModal.razor → @onclick=""() => _printBackgrounds = false"""] =
                "Print-backgrounds choice — a private bool on the sheet, applied only on Confirm.",
            [@"Components/Shared/PdfExportModal.razor → @onclick=""() => _suppressWatermark = false"""] =
                "Draft/Release watermark choice — a private bool, applied only on Confirm.",
            [@"Components/Shared/PdfExportModal.razor → @onclick=""() => _suppressWatermark = true"""] =
                "Draft/Release watermark choice — a private bool, applied only on Confirm.",
            [@"Components/Shared/PdfExportModal.razor → @onclick=""Confirm"""] =
                "Raises OnConfirm(settings) to the host. It writes no file itself: in the shell the "
                + "host is DashboardToolbar, which renders this modal inside "
                + "<ShellGate Permission=\"export_data\"> and re-checks MayExportData before it "
                + "asks PrintService for anything.",

            // ══ Components/Layout/DashboardToolbar.razor ═════════════════════════════════════
            //
            // The three view controls. Each raises an EventCallback to the host dashboard and
            // touches no injected service; the toolbar's four ACTING controls (refresh interval,
            // default time range, PDF, instance picker) are all inside boundaries.

            [@"Components/Layout/DashboardToolbar.razor → @onclick=""ToggleAutoRefresh"""] =
                "Pauses/resumes the host dashboard's own refresh loop by raising AutoRefreshChanged. "
                + "It does NOT touch AutoRefreshService — that is the process-wide timer the "
                + "refresh-interval picker writes, and that picker is inside a settings boundary.",
            [@"Components/Layout/DashboardToolbar.razor → @onclick=""() => OnRefreshRequested.InvokeAsync()"""] =
                "Asks the host dashboard to re-read the panels this caller is already looking at. "
                + "Same queries, same connection, same data the page rendered a moment ago; it "
                + "changes nothing on the server and nothing for any other circuit.",
            [@"Components/Layout/DashboardToolbar.razor → @onclick=""() => OnBaselineToggled.InvokeAsync()"""] =
                "Overlays the 7-day baseline on this circuit's charts. A view option raised to the "
                + "host dashboard; it reads history the page is already permitted to show.",
        };

        /// <summary>
        /// Every <c>&lt;ShellGate BreakGlass&gt;</c> in the shell, with the reason it is inside
        /// round 1's ruling: break-glass is scoped to SETTINGS and ONBOARDING — the two surfaces
        /// that can undo a bad RBAC configuration — and nowhere else.
        /// </summary>
        internal static readonly Dictionary<string, string> BreakGlassBoundaries = new(StringComparer.Ordinal)
        {
            ["Components/Layout/MainLayout.razor"] =
                "The gear icon in the top bar — the console route into /settings, the one page that "
                + "can undo a bad RBAC configuration. This is SETTINGS itself, which is exactly "
                + "what round 1 scoped the hatch to. Adrian was one click from a lockout on "
                + "2026-08-01 that would have been recoverable only by deleting "
                + "Config/rbac-users.json on disk.",

            ["Components/Layout/NavMenu.razor"] =
                "The Configuration category rail. Same reason and same scope as MainLayout's gear: "
                + "it is how a locked-out operator on the console reaches Settings at all. Asking "
                + "the gate rather than the role also matters here — on a DORMANT loopback install "
                + "every circuit resolves to viewer until sign-in, and a raw role check made the "
                + "whole category vanish on an install with no RBAC to enforce.",
        };

        /// <summary>
        /// <c>[JSInvokable]</c> methods on shell components. A C# method cannot sit inside a
        /// markup boundary, so the boundary rule cannot answer for these and they are answered
        /// here instead, one entry each.
        /// </summary>
        internal static readonly Dictionary<string, string> ShellJsInvokables = new(StringComparer.Ordinal)
        {
            ["Components/Shared/CommandPalette.razor → JSOpenCommandPalette"] =
                "Ctrl+K from the window-level handler in wwwroot/index.html. Opens THIS circuit's "
                + "palette — a private bool — through a per-page DotNetObjectReference registered "
                + "on the caller's own window. It used to be a STATIC [JSInvokable] backed by a "
                + "public static Action, i.e. one process-wide slot the last circuit to initialise "
                + "won, so a keystroke in one browser re-rendered a different user's palette. "
                + "Round 5 fixed the reference; the palette itself only navigates.",

            ["Components/Shared/WelcomeTourOverlay.razor → OnKeyPrevious"] =
                "Left-arrow while the guided tour is running. Calls WelcomeTourService.Previous, "
                + "and that service is AddScoped — the tour it moves is the calling circuit's own. "
                + "The ref is cleared as soon as the tour stops.",
            ["Components/Shared/WelcomeTourOverlay.razor → OnKeyNext"] =
                "Right-arrow while the tour is running. Same scoped service, same circuit, same "
                + "lifetime on the registered reference.",
            ["Components/Shared/WelcomeTourOverlay.razor → OnKeyStop"] =
                "Escape while the tour is running. Same scoped service and same circuit; the only "
                + "effect is that this browser's narration card stops rendering.",
        };

        // ── The shell set, DERIVED ───────────────────────────────────────

        private static readonly Lazy<IReadOnlyList<string>> _shell = new(ComputeShellClosure);

        /// <summary>
        /// The always-rendered shell: the transitive closure of component tags reachable from
        /// <c>MainLayout.razor</c>, unioned with every <c>.razor</c> under
        /// <c>Components/Layout/</c>.
        ///
        /// <para>Both halves are needed. The closure catches whatever MainLayout renders —
        /// <c>NavMenu</c>, <c>StatusBar</c>, <c>OnboardingWizard</c>, <c>GlobalServerSelector</c>,
        /// and through StatusBar the <c>ServerModeToggle</c> that round 4 found on every
        /// AccessDenied page. <c>Components/Layout/</c> catches <c>DashboardToolbar</c>, which is
        /// rendered by dashboards rather than by MainLayout but is shell chrome by design and by
        /// location.</para>
        /// </summary>
        internal static IReadOnlyList<string> ShellComponents => _shell.Value;

        internal static bool IsShell(string relativePath) =>
            ShellComponents.Contains(relativePath, StringComparer.OrdinalIgnoreCase);

        private static IReadOnlyList<string> ComputeShellClosure()
        {
            var root = RawPassedScan.RepoRoot();

            // Component tag name → file. First writer wins; Razor resolves by type name too.
            var byName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var dirName in new[] { "Components", "Pages" })
            {
                var dir = new DirectoryInfo(Path.Combine(root.FullName, dirName));
                if (!dir.Exists) continue;
                foreach (var file in dir.EnumerateFiles("*.razor", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');
                    var name = Path.GetFileNameWithoutExtension(file.Name);
                    if (!byName.ContainsKey(name)) byName[name] = relative;
                }
            }

            var shell = new SortedSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();

            void Seed(string relative)
            {
                if (shell.Add(relative)) queue.Enqueue(relative);
            }

            Seed("Components/Layout/MainLayout.razor");
            var layoutDir = new DirectoryInfo(Path.Combine(root.FullName, "Components", "Layout"));
            if (layoutDir.Exists)
                foreach (var file in layoutDir.EnumerateFiles("*.razor", SearchOption.AllDirectories))
                    Seed(Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/'));

            var tag = new Regex(@"<([A-Z][A-Za-z0-9_]*)[\s/>]", RegexOptions.Compiled);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var full = Path.Combine(root.FullName, current.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;

                foreach (Match m in tag.Matches(File.ReadAllText(full)))
                    if (byName.TryGetValue(m.Groups[1].Value, out var target))
                        Seed(target);
            }

            return shell.ToList();
        }

        // ── The edge scanner: NO verb filter, NO lifetime filter ─────────

        /// <summary>
        /// Every call and every property assignment <paramref name="relativePath"/> makes on a
        /// service it has injected, as <c>Type.Member</c> (with a trailing <c>=</c> for a
        /// property write).
        ///
        /// <para>Deliberately asks NOTHING about the member's name. That question — "does this
        /// look like a mutation?" — is what a 29-verb lexicon answered wrongly for
        /// <c>SetAnonymiseServerNames</c>, and what an event-raising check answered wrongly for
        /// the same call. The lifetime is not asked either: <c>IServerContextService</c> is
        /// AddScoped and writes a singleton, so filtering on "is the receiver a singleton" would
        /// have hidden the instance-retarget edges too.</para>
        ///
        /// <para>Reads therefore land on the register alongside writes. That is the cost of the
        /// inversion and it is the point: an entry costs one line, and a false negative has cost
        /// this lane five rounds.</para>
        /// </summary>
        internal static List<string> EdgesOf(string relativePath)
        {
            var root = RawPassedScan.RepoRoot();
            var full = Path.Combine(root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return new List<string>();

            var text = File.ReadAllText(full);
            if (File.Exists(full + ".cs")) text += File.ReadAllText(full + ".cs");

            return EdgesIn(text);
        }

        /// <summary>
        /// <see cref="EdgesOf"/> over text rather than a file — so the canary test can hand it the
        /// exact control the cold gate planted and prove the scanner sees it.
        /// </summary>
        /// <summary>
        /// Injected-service alias → declared type, from <c>@inject</c> and <c>[Inject]</c>.
        ///
        /// <para>Exposed so a caller can tell which receivers the edge scanner CAN see. The
        /// component census needs that: the edge scanner is blind to a call on a LOCAL variable
        /// (<c>cmd.ExecuteNonQueryAsync</c> on a SqlCommand), which is exactly what the verb
        /// lexicon is still good for, so the two instruments divide by receiver rather than
        /// overlapping and double-registering the same call under two names.</para>
        /// </summary>
        internal static Dictionary<string, string> InjectedAliases(string text)
        {
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(text, @"@inject\s+([A-Za-z0-9_.<>]+)\s+([A-Za-z_][A-Za-z0-9_]*)"))
                aliases[m.Groups[2].Value] = ShortName(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"\[Inject\][^\n]*?\s([A-Za-z0-9_.<>]+)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{"))
                aliases[m.Groups[2].Value] = ShortName(m.Groups[1].Value);
            return aliases;
        }

        internal static List<string> EdgesIn(string text)
        {
            var aliases = InjectedAliases(text);

            // A mention in a comment is not a call. Same filter as the page census.
            var code = string.Join("\n", text.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith("*", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith("@*", StringComparison.Ordinal)));

            var edges = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (alias, type) in aliases)
            {
                var escaped = Regex.Escape(alias);

                // Calls, with an OPTIONAL generic argument list between the member and the '('.
                //
                // ⚠ The `(?:<…>)?` group was added 2026-09-01 and it closed a live blind spot. The
                // pattern required the '(' to follow the member name immediately, so EVERY generic
                // call on an injected service was invisible — `JS.InvokeAsync<bool>("confirm", …)`
                // among them, which is the shape a confirmation dialog has. MEASURED at the time it
                // was added: the DataGrid confirmation gate (ruling R2(a)) was on no register and
                // the census stayed green, while the component sat on the INERT list describing
                // itself as making no acting call. Same class as the bare-Set gap in round 5 — an
                // instrument that cannot see a shape reports the absence of that shape as clean.
                //
                // The inner class excludes '(', ';', '{' and '}' so a less-than comparison cannot
                // be read as a type argument list: `x.Count < (y + 1)` finds no '>' before the '('
                // and falls through to no match, which is the honest answer for a comparison.
                foreach (Match m in Regex.Matches(
                             code, @"\b" + escaped + @"\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^;{}()]*>)?\s*\("))
                    edges.Add(type + "." + m.Groups[1].Value);

                // Property WRITES. `x.P = v` only — never `==`, never `=>`. Round 5's scan was
                // call-shaped, so NavMenu's `ToastService.Enabled = …` (a singleton, process-wide)
                // was invisible to every instrument in the tree.
                foreach (Match m in Regex.Matches(code, @"\b" + escaped + @"\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(?![=>])"))
                    edges.Add(type + "." + m.Groups[1].Value + "=");

                // Property writes THROUGH a returned object: `x.A.B = v`. Found 2026-08-06 while
                // upgrading the component census. DynamicDashboard retargets the process-wide
                // connection with `ConnectionManager.CurrentServer.Database = dbName`, and every
                // shape above is blind to it: the receiver of the assignment is CurrentServer, not
                // the injected alias, so the call-shaped regex sees nothing and the single-hop
                // write regex sees `x.A` followed by `.`, not `=`.
                //
                // One hop only, deliberately. A deeper chain is a different question (what the
                // intermediate object IS), and a regex that guesses at it would be the same
                // match-known mistake in a new costume; a two-hop write shows up as its own gap
                // and should be answered by looking, not by another pattern.
                foreach (Match m in Regex.Matches(
                             code, @"\b" + escaped + @"\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(?![=>])"))
                    edges.Add(type + "." + m.Groups[1].Value + "." + m.Groups[2].Value + "=");

                // Event subscription/unsubscription: `x.E += h`. Recorded as a plain member so a
                // subscription and a write cannot be confused for each other.
                foreach (Match m in Regex.Matches(code, @"\b" + escaped + @"\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*(\+=|-=)"))
                    edges.Add(type + "." + m.Groups[1].Value);

                // METHOD GROUP: `x.M` handed somewhere as a VALUE, with no parentheses of its own —
                // `@onclick="Svc.Reset"`, `Action a = Svc.Reset;`, `Register(Svc.Reset)`. Added
                // 2026-08-06 because the gate defeated the scanner with exactly this shape: every
                // pattern above needs a `(`, a `=` or a `+=` immediately after the member, and a
                // method group has none of them. The call still happens; it happens later, through
                // a name this file cannot see.
                //
                // Positioned, not bare. Requiring the reference to sit after '=', '(' or ',' (or
                // the opening quote of a Razor attribute) is what keeps this from matching every
                // property read in the tree; a read in an argument list does land here, and that is
                // the register's stated cost — reads sit beside writes because asking about the
                // member's name is the thing that failed five times.
                foreach (Match m in Regex.Matches(
                             code,
                             @"(?<=[=(,]\s*""?@?)\b" + escaped
                             + @"\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\b(?!\s*[(.=\[<])"))
                    edges.Add(type + "." + m.Groups[1].Value);
            }
            return edges.ToList();
        }

        internal static string Key(string relativePath, string edge) => relativePath + " → " + edge;

        private static string ShortName(string declaredType)
        {
            // ILogger<MainLayout> and SQLTriage.Data.UserSettingsService both reduce to the name
            // the register uses. Generic arguments are kept: two ILogger<T> edges are two edges.
            var generic = declaredType.IndexOf('<');
            if (generic < 0) return declaredType.Split('.').Last();

            var head = declaredType[..generic].Split('.').Last();
            var arg = declaredType[(generic + 1)..].TrimEnd('>').Split('.').Last();
            return head + "<" + arg + ">";
        }
    }
}
