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
    /// A census of every ROUTABLE page and whether it has an authorization gate at all.
    ///
    /// <para><b>Why this exists.</b> The original <c>RbacService.HasPermission(</c> scan in
    /// <c>RbacServerModeLockoutTests</c> (retired 2026-08-17 with the method it policed, which is now
    /// <c>private</c>) asked one question — "does this file call the static
    /// <c>RbacService.HasPermission</c>?" — and a file with NO gate whatsoever answers no. It was therefore structurally blind to
    /// <c>Pages/Onboarding.razor</c>, the one page that can MINT THE FIRST ADMIN, which shipped
    /// with zero authorization checks and was exercised from a LAN address on 2026-08-01. So the
    /// scan was widened: enumerate the routes, and require each one to have a gate or be named on
    /// a reviewed list.</para>
    ///
    /// <para><b>What the round-2 version of this file got wrong, and why it mattered.</b> It
    /// described <see cref="ReadOnlySurfaces"/> as pages "whose content is covered by the view
    /// permissions every role has" and claimed the list had been "Reviewed 2026-08-01 … by the
    /// mutating calls each file makes". Neither statement was true. Re-deriving the list on
    /// 2026-08-02 — mechanically, by extracting every <c>Receiver.MutatingVerb(</c> call out of
    /// each listed file and reading the hits — found FIVE pages on the read-only list that write
    /// to a connected server, to this install's server catalogue, or to a stored credential:</para>
    /// <list type="bullet">
    /// <item><c>Pages/AgentJobGuard.razor</c> — <c>RemediationRunner.ApplyAsync</c>, the same call
    ///   that put <c>AgentJobSync</c> on the DEBT REGISTER two entries away. Named by the gate.</item>
    /// <item><c>Pages/AgentJobTimeline.razor</c> — <c>JobControl.Start/Stop/Enable/DisableJobAsync</c>,
    ///   i.e. starting and stopping SQL Agent jobs on a client's production server.</item>
    /// <item><c>Pages/XEvents.razor</c> — <c>XEventSvc.Create/Start/Stop/DropSessionAsync</c>,
    ///   Extended Events DDL on a connected server.</item>
    /// <item><c>Pages/EnvironmentView.razor</c> — <c>ConnectionManager.AddConnection</c>, writing
    ///   discovered instances (with credentials carried over) into this install's catalogue.</item>
    /// <item><c>Pages/Portal/PortalStatus.razor</c> — <c>PortalPublishRunner.SaveIntakeSas</c>,
    ///   writing the portal INTAKE CREDENTIAL.</item>
    /// </list>
    /// <para>A test whose allow-list carries a false justification is worse than no test: it
    /// launders the gap as reviewed. All five now carry gates (the first at page level, the rest at
    /// handler level, because their read surfaces are legitimately open to every role) and the
    /// wording below says only what was actually checked.</para>
    ///
    /// <para><b>What the lists mean, precisely.</b> Being on a list is A STATEMENT THAT SOMEONE
    /// LOOKED, and nothing more. <see cref="ReadOnlySurfaces"/> holds pages for which the
    /// mechanical mutating-call scan described above returned no call that changes state OUTSIDE
    /// this process — no write to a connected SQL Server, no write to persisted app configuration
    /// or credentials, no upload. It does NOT mean the page was read line by line, and it does not
    /// mean the page discloses nothing. <see cref="UngatedWriteSurfaces"/> is a DEBT REGISTER, not
    /// an approval: those pages can change something and still have no gate. Reviewed 2026-08-02.</para>
    ///
    /// <para><b>What round 4 changed here, 2026-08-02.</b> Round 3's scan could not see the verb
    /// <c>Run</c>. <c>Pages/Governance.razor</c> therefore sat on the READ-ONLY list while calling
    /// <c>QuickCheckRunner.RunAsync</c>, and a round-4 gate drove that call from a LAN origin with
    /// no credentials — the host logged "QuickCheck starting on tcp:localhost,56510". Adding
    /// Run|Start|Stop to the net cost three entries in total; all three (Governance, Benchmark,
    /// PerformanceTrends) were real, and all three are gated now. The <c>Process</c> receiver was
    /// also removed from the benign list, because round 4's B1 showed that
    /// <c>Process.Start(UseShellExecute)</c> on a local file is process creation on the host and an
    /// unauthenticated LAN caller reached it. THE LESSON, THIRD TIME OF ASKING: this scan's false
    /// negatives have cost five ungated write surfaces, then three, and every one of them was
    /// hiding behind a NARROWING — a verb left out, a receiver excused. Widen the net and handle
    /// the noise by name.</para>
    ///
    /// <para><b>What round 6 changed here, 2026-08-10.</b> The known weakness this file had been
    /// STATING and then leaning on — <see cref="RoutablePage.HasGate"/> is file-level, so one gated
    /// handler hides N ungated ones — is closed by a PER-HANDLER census beside the file-level one
    /// (see <see cref="EveryMutatingHandlerIsGatedOrRegistered"/>). It found six on
    /// <c>Pages/Alerts.razor</c> (seven after round 7 widened the verb net, below), a page that
    /// passes the file-level census because two OTHER
    /// handlers call <c>IsAuthorized("acknowledge_alerts")</c>: three of the six create and start
    /// Extended Events sessions on a connected server, the same call class that put
    /// <c>Pages/XEvents.razor</c> behind a gate in round 3. They are REGISTERED, not gated, and
    /// <see cref="HandlersRegisteredUngated"/> says why each one is still open. FOURTH TIME OF
    /// ASKING, and the same lesson in a new dimension: the previous three rounds widened the net
    /// horizontally (more verbs, fewer excused receivers) while the GRANULARITY stayed wrong.</para>
    ///
    /// <para><b>What round 7 changed here, the same day.</b> Round 6's per-handler census was
    /// verified by exercise and failed on three counts, every one of them this file's own recurring
    /// defect: a narrowing that makes the instrument report LESS.</para>
    /// <list type="number">
    /// <item>BINDING SYNTAX. <see cref="EventBinding"/> named only the nine <c>@on*</c> attributes,
    ///   so real ungated mutating handlers reached through component EventCallback parameters on
    ///   <c>Pages/QuickCheck.razor</c> were never enumerated. They write this install's persisted
    ///   report-section definitions for /audit, and they are on the register below.</item>
    /// <item>DECLARATION RESOLUTION. <see cref="MemberBody"/> took the first brace-matching
    ///   occurrence of a name, which for a lambda binding is the CALL SITE in the markup, so the
    ///   handler's "body" was markup and its mutating calls vanished. It now requires a declaration
    ///   site.</item>
    /// <item>VOCABULARY, again. The net had no "Evaluate", so <c>Engine.EvaluateAllAsync</c> behind
    ///   Alerts' ungated Run Now button was invisible: a SEVENTH handler on the page round 6 both
    ///   scanned AND audited by hand, because a hand audit that trusts the instrument inherits the
    ///   instrument's blind spot.</item>
    /// </list>
    /// <para>Round 6's stated limits claimed "Both directions of error therefore point at MORE
    /// review, not less." All three of those pointed at less. FIFTH TIME OF ASKING.</para>
    ///
    /// <para><b>What round 8 changed here, 2026-08-10.</b> Two things, and only one of them was the
    /// instrument.</para>
    /// <list type="number">
    /// <item>THE RULING. Adrian ruled on what rounds 6 and 7 both wrote down as "needs a permission
    ///   ruling" and then left open: gate them. Nineteen of the twenty handlers discharged carry a
    ///   permission this tree already used for the same call class, and the choice is pinned per
    ///   handler in <see cref="RoundEightGatedHandlers"/> so a later re-gate onto a weaker
    ///   permission fails rather than reads as still-discharged. ONE entry stays on
    ///   <see cref="HandlersRegisteredUngated"/>, and the reason is written there rather than
    ///   implied by its absence.</item>
    /// <item>THE NET, AGAIN, ON BOTH AXES ROUND 7 HAD ALREADY WIDENED. Round 7 disclosed two verbs
    ///   it could not see (Move, Accept) and registered around them. Adding those plus Update, Set
    ///   and Assign surfaced SEVEN more ungated mutating handlers, and adding <c>@bind:after</c> to
    ///   <see cref="EventBinding"/> surfaced an eighth - <c>Pages/Alerts.razor::SaveGlobalDefaults</c>,
    ///   which persists the retention policy that decides when alert history is deleted, on a page
    ///   this census had already produced seven findings for. SIXTH TIME OF ASKING, and this time
    ///   both blind spots had been DISCLOSED IN WRITING by the previous round: a stated limit is
    ///   not a closed one.</item>
    /// </list>
    ///
    /// <para><b>What round 9 changed here, 2026-08-10, and what it did NOT.</b> Verification of
    /// round 8 found two things this file was blind to, and only one of them is closed.</para>
    /// <list type="number">
    /// <item>BINDING MECHANISM, not binding syntax. Every previous round widened
    ///   <see cref="EventBinding"/> and stayed inside markup. A handler subscribed in C#
    ///   (<c>ShortcutSvc.OnExportCsvRequested += Handler;</c>) has no attribute form at all, so it
    ///   was never ENUMERATED and therefore never asked whether it was gated. Proved by planting an
    ///   ungated handler calling the same <c>ReportPageCfg.DeleteSection</c> as a markup-bound
    ///   control: the markup twin was named, the subscribed one went 57/57 green. That is upstream
    ///   of the verb net, so no widening of <see cref="ActingVerbs"/> could ever have reached it.
    ///   CLOSED by <see cref="HandlerSubscription"/>, which enumerates the four real shortcut
    ///   handlers this tree has. SEVENTH TIME OF ASKING.</item>
    /// <item>PROPERTY ASSIGNMENT, and this one is STILL OPEN. <see cref="MutatingCalls"/> is
    ///   call-shaped: <c>Engine.DryRun = _dryRun;</c> on an injected singleton is a write with
    ///   process-wide effect and matches no verb, because it is not a call. It was found in a
    ///   RENDER, not by this instrument. The one live instance is gated now and pinned in
    ///   <see cref="RoundEightGatedHandlers"/>, and a hand sweep of all 59 injected-service property
    ///   assignments under <c>Pages/**</c> and <c>Components/**</c> found no second one with a
    ///   cross-user effect. That sweep is a MEASUREMENT TAKEN ONCE, not a standing instrument, and
    ///   nothing in this file will notice the sixtieth. Teaching the census to read property
    ///   assignments is the follow-up lane.</item>
    /// </list>
    /// </summary>
    public class RbacPageGateCensusTests
    {
        /// <summary>
        /// Read-only surfaces: dashboards, reports and diagnostic views. The mutating-call scan
        /// (see the class note) found nothing here that writes to a server, to persisted app
        /// configuration or to a credential. Local export to disk — <c>File.WriteAllBytesAsync</c>
        /// of a PDF or CSV into the user's output folder — is treated as part of a read surface and
        /// is NOT a mutation for this list's purpose; that is a judgement, and it is written down
        /// here so the next person can disagree with it knowingly rather than assume it was never
        /// considered.
        ///
        /// <para>The <c>Process.Start</c> that opens the exported file, or its folder, USED TO BE
        /// covered by that same sentence. It is not any more: round 4 drove exactly that shape on
        /// Pages/Portal/PortalStatus from an unauthenticated LAN origin and got a real process on
        /// the host. Process creation is now scanned like any other call, and the two read-only
        /// pages that still do it are named, with their exact call, in
        /// <see cref="ReadOnlyProcessStartExceptions"/>.</para>
        /// </summary>
        private static readonly string[] ReadOnlySurfaces =
        {
            "Components/Dashboards/LiveDashboard.razor",
            "Pages/About.razor",
            "Pages/AccessSurface.razor",
            "Pages/AdvancedReporting.razor",
            "Pages/AppMetrics.razor",
            "Pages/BlitzDashboard.razor",
            "Pages/BlockingForensics.razor",
            "Pages/Bpcheck.razor",
            "Pages/CapacityPlanning.razor",
            // Pages/ChangeLedger.razor left this list 2026-08-10, moved to the debt register below:
            // the Evaluate verb made ChangeItems.EvaluateFollowUpsAsync visible, and it writes.
            "Pages/ChangedObjects.razor",
            "Pages/CheckTrend.razor",
            "Pages/Checks.razor",
            "Pages/CioDashboard.razor",
            "Pages/CodeHotspots.razor",
            "Pages/ComplianceBoard.razor",
            "Pages/ComplianceMap.razor",
            "Pages/ComplianceTree.razor",
            "Pages/Dashboard.razor",
            "Pages/DbaDashboard.razor",
            "Pages/DiskIo.razor",
            "Pages/Documentation.razor",
            "Pages/Guide.razor",
            "Pages/Health.razor",
            "Pages/Index.razor",
            "Pages/IndexAnalysis.razor",
            "Pages/InstanceOverview.razor",
            "Pages/Login.razor",
            "Pages/LongQueries.razor",
            "Pages/OffboardingTrace.razor",
            "Pages/PerformanceReport.razor",
            "Pages/Pevents.razor",
            "Pages/Playbooks.razor",
            "Pages/Pmemory.razor",
            "Pages/PmemoryAnalysis.razor",
            "Pages/Pquery.razor",
            "Pages/Premium.razor",
            "Pages/QueryStore.razor",
            // Pages/ReplicationMap.razor left this list on 2026-08-05: the map now offers
            // discovered replication participants for adding, and that button is gated on
            // manage_servers (handler level, the same permission /servers uses). The page
            // itself still writes nothing — the add goes to the /servers dialog — but a page
            // with a gate does not belong on the list of ungated read-only surfaces.
            "Pages/RiskReport.razor",
            "Pages/SchedulerHealth.razor",
            "Pages/ServerComparison.razor",
            "Pages/ServerConfigDiff.razor",
            "Pages/Services.razor",
            "Pages/TestPlan.razor",
            "Pages/WaitEvents.razor",
        };

        /// <summary>
        /// DEBT REGISTER — pages that can WRITE something and still have no gate. Not approved;
        /// recorded, with the exact call named so the next triage does not have to re-derive it.
        ///
        /// <para>Round 2 registered ten. Round 3 (2026-08-02) discharged all ten — see
        /// <see cref="TheRoundTwoDebtRegisterIsDischarged"/>, which pins that by name so the list
        /// cannot quietly refill with the same entries. Round 3 then registered three more; round 4
        /// discharged one of them (VulnerabilityAssessment, gated on run_scripts after a LAN caller
        /// was shown clicking Run Assessment into SqlConnection.InternalOpenAsync). The two that
        /// remain write LOCAL preferences only — a roadmap schedule, an SoD baseline path — neither
        /// reaches a connected server or a credential, and each is left registered rather than
        /// gated because choosing the right permission for them needs a decision no round so far
        /// has been asked to make.</para>
        /// </summary>
        private static readonly string[] UngatedWriteSurfaces =
        {
            "Pages/DiagnosticsRoadmap.razor",       // UserSettings.SaveRoadmapSchedule / UpdateRoadmapScheduleLastRun — authors an unattended schedule
            "Pages/SodMatrix.razor",                // SodBaselineStore.Write / UserSettings.SetSodPreferredBaselinePath — writes the SoD baseline
            // Registered 2026-08-10 by round 7's Evaluate verb, and it had been on the READ-ONLY
            // list: OnInitializedAsync calls ChangeItems.EvaluateFollowUpsAsync, which reaches
            // ChangeItemService.MarkStillFailing and persists a status transition on this install's
            // change ledger. Registered rather than gated because the write is a best-effort sweep
            // on PAGE LOAD, not a control anyone clicks, and gating the load would hide the ledger
            // itself from readers who are meant to see it. Choosing what the sweep needs is a
            // ruling; naming it here is not.
            "Pages/ChangeLedger.razor",             // ChangeItems.EvaluateFollowUpsAsync — auto-flags overdue handed-off items StillFailing
        };

        /// <summary>
        /// The ten entries round 2 recorded as ungated write surfaces. Every one of them is gated
        /// now. Pinned by name because "the debt register shrank" is only meaningful if the
        /// specific debts are the ones that went away.
        /// </summary>
        private static readonly string[] RoundTwoDebtRegister =
        {
            "Pages/AgentJobSync.razor",
            "Pages/BuildProfile.razor",
            "Pages/EditAuditScripts.razor",
            "Pages/ImportResults.razor",
            "Pages/InstallationHelper.razor",
            "Pages/Portal/PublishToPortal.razor",
            "Pages/Remediation.razor",
            "Pages/RemediationLab.razor",
            "Pages/RemediationTuner.razor",
            "Pages/ScheduledTasks.razor",
        };

        /// <summary>
        /// The twelve routes the cold gate proved were serving mutating UI to an unauthenticated
        /// LAN caller in both dormant and enforced mode, plus /portal-publish — which was on round
        /// 2's debt register, was NOT among the gate's twelve, and IS routable, so it could have
        /// been closed as out of scope. The union is thirteen. Pinned by ROUTE rather than by path
        /// so a file rename cannot silently drop one.
        /// </summary>
        private static readonly string[] RoundThreeGatedRoutes =
        {
            "/agent-job-guard",
            "/agent-job-sync",
            "/build-profile",
            "/editauditscripts",
            "/fullaudit",
            "/import-results",
            "/installation-helper",
            "/portal-publish",
            "/remediation",
            "/remediation-lab",
            "/remediation-tuner",
            "/scheduled-tasks",
            "/servers/baseline",
        };

        /// <summary>
        /// The routes round 4 gated, pinned by ROUTE so a file rename cannot silently drop one.
        /// Two came from the round-4 brief (/portal-status, /vulnerabilityassessment, /governance);
        /// /benchmark and /performance-trends came from the WIDENED SCAN in this file and were
        /// sitting on the read-only list above at db5ec28.
        /// </summary>
        private static readonly string[] RoundFourGatedRoutes =
        {
            "/benchmark",
            "/governance",
            "/trends",             // Pages/PerformanceTrends.razor
            "/portal-status",
            "/vulnerabilityassessment",
        };

        private static IEnumerable<string> AllowListed => ReadOnlySurfaces.Concat(UngatedWriteSurfaces);

        [Fact]
        public void EveryRoutablePageHasAGateOrIsOnTheReviewedList()
        {
            var ungated = EnumerateRoutablePages()
                .Where(p => !p.HasGate)
                .Where(p => !AllowListed.Contains(p.Path, StringComparer.OrdinalIgnoreCase))
                .ToList();

            Assert.True(
                ungated.Count == 0,
                "These routable pages have NO authorization check of any kind and are not on the reviewed list. "
                + "Add a gate (AppUserState.IsAuthorized / IsAuthorizedWithBreakGlass), or add the page to "
                + "ReadOnlySurfaces / UngatedWriteSurfaces with a note saying why:\n  "
                + string.Join("\n  ", ungated.Select(p => p.Path + "  (route " + p.Route + ")")));
        }

        [Fact]
        public void TheReviewedListHasNoStaleEntries()
        {
            var pages = EnumerateRoutablePages().ToDictionary(p => p.Path, p => p, StringComparer.OrdinalIgnoreCase);

            var stale = new List<string>();
            foreach (var entry in AllowListed)
            {
                if (!pages.TryGetValue(entry, out var page))
                {
                    stale.Add(entry + " — no such routable page any more; remove the entry.");
                    continue;
                }
                if (page.HasGate)
                    stale.Add(entry + " — now HAS a gate; remove it from the ungated list.");
            }

            // An allow-list nobody prunes is an allow-list that quietly becomes the whole app.
            Assert.True(stale.Count == 0, "Stale entries:\n  " + string.Join("\n  ", stale));
        }

        /// <summary>
        /// The claim this file used to make falsely, now made as an assertion instead of a comment.
        ///
        /// <para>Every page on <see cref="ReadOnlySurfaces"/> is re-scanned for calls that change
        /// state outside this process. If one appears, this fails and names it — so the next time
        /// somebody adds an Apply button to a dashboard, the list stops being true out loud
        /// instead of quietly. This is the mechanism that would have caught AgentJobGuard,
        /// AgentJobTimeline, XEvents, EnvironmentView and PortalStatus on 2026-08-01.</para>
        ///
        /// <para>⚠ KNOWN NARROWING (2026-08-13, no current offender). ReadPageAndCodeBehind scans
        /// the .razor and its .razor.cs only. It does not follow a page into an injected service,
        /// so SQL a page reaches through one is invisible here. Pages/IndexAnalysis.razor is the
        /// first entry in that shape: its four commands moved to Data/Services/
        /// IndexAnalysisService.cs. Nothing is wrong today — the four queries were byte-compared
        /// against the page at the base commit and are identical, all SELECTs — but a write added
        /// inside that service would not be seen by the census that vouches for the page. Same
        /// shape as the house lesson "authorization audits drive controls, not routes": the
        /// instrument is blind to a category, not to a route.</para>
        /// </summary>
        [Fact]
        public void TheReadOnlyListIsActuallyReadOnly()
        {
            var offenders = new List<string>();
            var root = RawPassedScan.RepoRoot();

            foreach (var entry in ReadOnlySurfaces)
            {
                var text = ReadPageAndCodeBehind(root.FullName, entry);
                if (text == null) continue; // covered by TheReviewedListHasNoStaleEntries

                var hits = MutatingCalls(text).ToList();

                // A pinned exception excuses EXACTLY the calls it records, and nothing else.
                if (ReadOnlyProcessStartExceptions.TryGetValue(entry, out var allowed))
                {
                    var unexpected = hits.Except(allowed, StringComparer.Ordinal).ToList();
                    if (unexpected.Count > 0)
                        offenders.Add(entry + " → " + string.Join(", ", unexpected)
                                      + "  (this page has a PINNED exception for "
                                      + string.Join(", ", allowed) + " and nothing else)");
                    continue;
                }

                if (hits.Count > 0)
                    offenders.Add(entry + " → " + string.Join(", ", hits));
            }

            Assert.True(offenders.Count == 0,
                "These pages are on the READ-ONLY list but call something that changes state outside this "
                + "process. Either gate them (page-level, or handler-level if the read surface should stay "
                + "open to every role) and remove them from the list, or — if the call really is benign — "
                + "add the receiver to KnownBenignReceivers WITH A REASON. Do not simply move them to the "
                + "debt register: that list is for pages someone decided to leave open, not for pages "
                + "nobody looked at.\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// The thirteen routes this round gated. Asserts the gate is present AND that the page
        /// names a permission — a gate that renders AccessDenied but calls nothing is not a gate.
        /// </summary>
        [Theory]
        [MemberData(nameof(RoundThreeRoutes))]
        public void EachRoundThreeRouteIsGated(string route)
        {
            var page = EnumerateRoutablePages().SingleOrDefault(p =>
                p.Route.Equals(route, StringComparison.OrdinalIgnoreCase));

            Assert.True(page != null, "No routable page answers " + route + " any more. If it was removed, "
                                    + "remove it from RoundThreeGatedRoutes; if it was renamed, the gate must move with it.");
            Assert.True(page!.HasGate, route + " (" + page.Path + ") lost its authorization gate.");
            Assert.True(page.Permission != null,
                route + " (" + page.Path + ") has a gate that names no permission — "
                + "IsAuthorized/IsAuthorizedWithBreakGlass must be called with the permission the page needs.");
        }

        public static IEnumerable<object[]> RoundThreeRoutes => RoundThreeGatedRoutes.Select(r => new object[] { r });

        /// <summary>Round 4's five routes: the gate is present and names a permission.</summary>
        [Theory]
        [MemberData(nameof(RoundFourRoutes))]
        public void EachRoundFourRouteIsGated(string route)
        {
            var page = EnumerateRoutablePages().SingleOrDefault(p =>
                p.Route.Equals(route, StringComparison.OrdinalIgnoreCase));

            Assert.True(page != null, "No routable page answers " + route + " any more. If it was removed, "
                                    + "remove it from RoundFourGatedRoutes; if it was renamed, the gate must move with it.");
            Assert.True(page!.HasGate, route + " (" + page.Path + ") lost its authorization gate.");
            Assert.True(page.Permission != null,
                route + " (" + page.Path + ") has a gate that names no permission.");
        }

        public static IEnumerable<object[]> RoundFourRoutes => RoundFourGatedRoutes.Select(r => new object[] { r });

        /// <summary>
        /// Routes gated when they SHIPPED, not after a probe found them open. /export-pack (Export
        /// Pack, 2026-08-09) is the first entry: it renders this machine's key directory, its pack
        /// directory and whether this install holds an export key, and its two actions write a
        /// tokenised copy of the estate's audit artefacts to disk and retire the key every prior pack
        /// is joinable under. It carries `settings`, the same permission /portal-status and
        /// /portal-publish carry, for the same reasons.
        ///
        /// <para>It is deliberately NOT on <see cref="ReadOnlySurfaces"/>. Adding it there would have
        /// been the exact move that put PortalStatus on that list for a year while it wrote the
        /// portal intake credential.</para>
        ///
        /// <para>⚠ THE KNOWN WEAKNESS THIS NOTE USED TO RESTATE — the scan is file-level, so a page
        /// with one gated handler and five ungated ones passes — is CLOSED as of 2026-08-10 by the
        /// per-handler census in this file (<see cref="EveryMutatingHandlerIsGatedOrRegistered"/>).
        /// The reason for this page's belt and braces stands regardless: its gate is at PAGE level
        /// (an unauthorized circuit renders nothing) AND at handler level (<c>CanAct</c> opens both
        /// handlers), and <see cref="Portal.ExportPackCompositionTests"/> asserts those two facts
        /// about the real source rather than inferring them from this test passing.</para>
        /// </summary>
        private static readonly string[] GatedOnArrivalRoutes =
        {
            "/export-pack",
        };

        [Theory]
        [MemberData(nameof(ArrivalRoutes))]
        public void EachRouteGatedOnArrivalIsGated(string route)
        {
            var page = EnumerateRoutablePages().SingleOrDefault(p =>
                p.Route.Equals(route, StringComparison.OrdinalIgnoreCase));

            Assert.True(page != null, "No routable page answers " + route + " any more. If it was removed, "
                                    + "remove it from GatedOnArrivalRoutes; if it was renamed, the gate must move with it.");
            Assert.True(page!.HasGate, route + " (" + page.Path + ") lost its authorization gate.");
            Assert.True(page.Permission != null,
                route + " (" + page.Path + ") has a gate that names no permission.");
        }

        public static IEnumerable<object[]> ArrivalRoutes => GatedOnArrivalRoutes.Select(r => new object[] { r });

        /// <summary>
        /// Routes REALIGNED after shipping with a gate of the wrong kind — present, so every census
        /// above was satisfied, but keyed off the wrong authority. /audit-log (2026-08-15) is the
        /// first: it gated on the static <c>RbacService.HasPermission(UserState.Role, "settings")</c>,
        /// the permission matrix alone, and so was the one settings surface that could not see this
        /// circuit's bootstrap hatch. Adrian ruled ALIGN; it now reads
        /// <c>UserState.IsAuthorized("settings")</c> like its peers.
        ///
        /// <para>Added because this file named /audit-log NOWHERE before today — the route was
        /// covered only by the generic <see cref="EveryRoutablePageHasAGateOrIsOnTheReviewedList"/>,
        /// which asks whether A gate exists and not which one. That generic test was green
        /// throughout, which is precisely how the wrong gate survived a fortnight of census runs.
        /// The gate's BEHAVIOUR is proved by AuditLogViewerGateTests over the real component;
        /// RbacServerModeLockoutTests.MutationTwo goes red if the static call is restored. This pin
        /// is the cheap third leg: the route still exists and still carries a permission-naming
        /// gate.</para>
        /// </summary>
        private static readonly string[] RealignedGateRoutes =
        {
            "/audit-log",
        };

        [Theory]
        [MemberData(nameof(RealignedRoutes))]
        public void EachRealignedRouteStillCarriesAPermissionNamingGate(string route)
        {
            var page = EnumerateRoutablePages().SingleOrDefault(p =>
                p.Route.Equals(route, StringComparison.OrdinalIgnoreCase));

            Assert.True(page != null, "No routable page answers " + route + " any more. If it was removed, "
                                    + "remove it from RealignedGateRoutes; if it was renamed, the gate must move with it.");
            Assert.True(page!.HasGate, route + " (" + page.Path + ") lost its authorization gate.");
            Assert.True(page.Permission != null,
                route + " (" + page.Path + ") has a gate that names no permission — a realigned gate "
                + "that stops naming its permission is the same defect wearing a different face.");
        }

        public static IEnumerable<object[]> RealignedRoutes => RealignedGateRoutes.Select(r => new object[] { r });

        /// <summary>Round 2's ten registered debts are gated, by name.</summary>
        [Fact]
        public void TheRoundTwoDebtRegisterIsDischarged()
        {
            var pages = EnumerateRoutablePages().ToDictionary(p => p.Path, p => p, StringComparer.OrdinalIgnoreCase);

            var outstanding = RoundTwoDebtRegister
                .Where(entry => !pages.TryGetValue(entry, out var page) || !page.HasGate)
                .ToList();

            Assert.True(outstanding.Count == 0,
                "Round 2 recorded these as ungated write surfaces and round 3 claimed to discharge them. "
                + "They are ungated again (or gone):\n  " + string.Join("\n  ", outstanding));
        }

        [Fact]
        public void OnboardingIsGated_ItCanMintTheFirstAdmin()
        {
            var onboarding = EnumerateRoutablePages()
                .SingleOrDefault(p => p.Path.Equals("Pages/Onboarding.razor", StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(onboarding);
            Assert.True(onboarding!.HasGate, "Pages/Onboarding.razor must gate every step — it mints the first admin.");

            // The gate has to be ABOVE the step switch, or the page still opens at step 1 (where
            // OnInitialized lands it once any user exists) with Detect / Add Server live. Assert on
            // the shipped markup: the gate block must precede the first `@if (_step`.
            var text = File.ReadAllText(Path.Combine(RawPassedScan.RepoRoot().FullName, "Pages", "Onboarding.razor"));
            var gateAt = text.IndexOf("IsAuthorizedWithBreakGlass", StringComparison.Ordinal);
            var firstStepAt = text.IndexOf("_step == 0", StringComparison.Ordinal);

            Assert.True(gateAt >= 0 && firstStepAt > gateAt,
                "The Onboarding gate must be rendered BEFORE the step switch, so its `return` covers every step.");
        }

        /// <summary>
        /// THE GUARD ON THE ONE NARROWING IN THIS FILE.
        ///
        /// <para>De-anchoring the verb match (round 5) forced a reader-prefix filter, and a
        /// reader-prefix filter is the obvious way to quietly re-anchor the net: add "Tri" and
        /// TriggerRun vanishes again. Two assertions stop that. No reader prefix may be a prefix
        /// of an acting verb, and the four calls that round 5 was blind to must still survive the
        /// whole pipeline — the ones that were four lines from the pinned set and invisible.</para>
        /// </summary>
        [Fact]
        public void ReaderPrefixesNeverHideAnActingVerbPrefix()
        {
            // THE array the net is built from, not a hand-kept copy of it. Until round 7 this test
            // held its own transcription of the verb list, so a verb added to the regex and not to
            // the copy would leave the guard measuring a vocabulary the scanner no longer uses. The
            // two agreed on 2026-08-10; they now cannot disagree.
            var verbs = ActingVerbs;

            var swallowed = (from p in ReaderPrefixes
                             from v in verbs
                             where v.StartsWith(p, StringComparison.Ordinal)
                             select p + " swallows " + v + "*").ToList();

            Assert.True(swallowed.Count == 0,
                "A reader prefix that is also the start of an acting verb re-anchors the net by the "
                + "back door — which is exactly the defect round 5 fixed:\n  "
                + string.Join("\n  ", swallowed));

            // End-to-end, through NoisyMethods and ReaderPrefixes as well as the regex.
            var probe = @"
                ShortcutSvc.TriggerRun();
                ShortcutSvc.TriggerExportPdf();
                ShortcutSvc.TriggerExportCsv();
                ShortcutSvc.TriggerCommandPalette();
                Bus.RaiseSomething();
                Hub.BroadcastToAll();
                Svc.DispatchWork();
                Svc.NotifySubscribers();
                Conn.GetEnabledConnections();
                Flags.IsSoftEnabled();
                s.TrimStart();
            ";
            var found = MutatingCalls(probe).ToList();

            foreach (var expected in new[]
                     {
                         "ShortcutSvc.TriggerRun", "ShortcutSvc.TriggerExportPdf",
                         "ShortcutSvc.TriggerExportCsv", "ShortcutSvc.TriggerCommandPalette",
                         "Bus.RaiseSomething", "Hub.BroadcastToAll",
                         "Svc.DispatchWork", "Svc.NotifySubscribers",
                     })
            {
                Assert.True(found.Contains(expected),
                    expected + " must be visible to the census. It was not before 2026-08-02, and a "
                    + "Ctrl+R on an Access Denied page ran checks in somebody else's circuit. "
                    + "Found: " + string.Join(", ", found));
            }

            // …and the reader/plumbing noise still does not land on the register.
            Assert.DoesNotContain("Conn.GetEnabledConnections", found);
            Assert.DoesNotContain("Flags.IsSoftEnabled", found);
            Assert.DoesNotContain("s.TrimStart", found);
        }

        // ── Round 6: the PER-HANDLER census (2026-08-10) ─────────────────

        /// <summary>
        /// THE DEFECT THIS ROUND FIXES, which the class note above already stated as a known
        /// weakness and then leaned on anyway: <see cref="RoutablePage.HasGate"/> is FILE-LEVEL. It
        /// asks whether the page or its code-behind contains ANY recognised authorization call, so a
        /// page with one gated handler and five ungated ones passes. That was proven by mutation in
        /// the E3 wave, and it is not hypothetical here: <c>Pages/Alerts.razor</c> carries two
        /// <c>acknowledge_alerts</c> gates and six other handlers with none, three of which run
        /// Extended Events DDL on a connected server (see <see cref="HandlersRegisteredUngated"/>).
        ///
        /// <para><b>What this census can see, and it is designed from that.</b> Source analysis, no
        /// runtime. It reads the event bindings out of the markup, finds each handler's body, runs
        /// the SAME <see cref="MutatingCalls"/> net the read-only-list test uses over that body, and
        /// asks whether that ONE handler is covered. Coverage is any of:</para>
        /// <list type="number">
        /// <item>a PAGE-LEVEL gate, meaning a gate whose refusal covers the whole body: an
        ///   <c>@if</c> on a gate that <c>return;</c>s out of the render, or one that holds the
        ///   entire body in its <c>else</c> branch, or <c>@attribute [Authorize]</c>;</item>
        /// <item>a gate call inside the handler's own body;</item>
        /// <item>a reference from the handler's body to a GATED MEMBER, i.e. a member whose body
        ///   calls a gate (<c>CanAct</c>, <c>MayExport</c>) or a field assigned from one
        ///   (<c>_canControl = UserState.IsAuthorized("run_scripts")</c>). One level of
        ///   indirection, because that is the shape the gated pages in this tree actually use.</item>
        /// </list>
        ///
        /// <para><b>Honest limits, stated rather than discovered later.</b> The condition resolver
        /// is one level deep, so a gate reached through two hops reads as ungated and lands on the
        /// register with a reason saying so. Markup regions that hide a control behind
        /// <c>@if (_canX)</c> are NOT read as gating the handler: a hidden control is not an
        /// unreachable one, and this file's own history is a list of narrowings that cost ungated
        /// write surfaces. Both directions of error therefore point at MORE review, not less.</para>
        /// </summary>
        internal sealed record HandlerFinding(string Page, string Handler, IReadOnlyList<string> Calls)
        {
            public string Key => Page + "::" + Handler;
            public override string ToString() => Key + " -> " + string.Join(", ", Calls);
        }

        /// <summary>
        /// PER-HANDLER DEBT REGISTER. Same meaning as <see cref="UngatedWriteSurfaces"/> one level
        /// down: recorded, with the exact calls named, NOT approved. An entry here is a statement
        /// that somebody looked and did not gate it, and the value says why.
        ///
        /// <para><b>Round 8, 2026-08-10. This register held THIRTEEN entries and now holds one.</b>
        /// Adrian ruled on the thing rounds 6 and 7 said they could not decide: gate them all. The
        /// permissions used are the ones this tree already has, and each was chosen from a measured
        /// precedent rather than invented - <c>run_scripts</c> for the three Extended Events deploys
        /// and Test Query on <c>Pages/Alerts.razor</c>, because <c>Pages/XEvents.razor</c> gates the
        /// identical <c>XEventService</c> calls with it; <c>manage_alerts</c> for everything on that
        /// page that writes an alert definition or drives the evaluation engine, because
        /// <c>Pages/AlertingConfig.razor</c> gates its whole page on it and four alert-configuration
        /// API endpoints carry it; <c>settings</c> for the rest. The discharge is pinned by name in
        /// <see cref="TheRoundEightDischargeHolds"/>, so this list cannot quietly refill with the
        /// same entries the way round 2's did.</para>
        ///
        /// <para><b>The one that stayed open, and why it is not a narrowing.</b> Every other entry
        /// was a write to a connected server, to an alert definition, to this install's report layout
        /// or report metadata, or a process on the host. This one is a display preference, and the
        /// measurement is in <c>Data/UserSettingsService.cs:568</c>: it sets one bool and calls
        /// SaveSettings, so it DOES write this install's shared settings file - it is not excused for
        /// being in-process. It is left open because gating it takes the diagnostics pane away from
        /// the Viewer and Operator roles that are meant to read /audit, and the pane's CONTENT is
        /// already rendered to them; the only thing the write can do to another user is flip a panel
        /// open or shut. Adrian's ruling was "gate the ungated mutating handlers", and this is the
        /// one where following it literally would cost a real workflow for no boundary, so it is
        /// registered loudly instead of gated quietly. Overturning it is one line here plus a
        /// <c>MayEditReportLayout</c> reference in the handler.</para>
        /// </summary>
        private static readonly Dictionary<string, string> HandlersRegisteredUngated =
            new(StringComparer.Ordinal)
            {
                ["Pages/QuickCheck.razor::ToggleDiagnosticPane"] =
                    "UserSettings.SetShowDiagnosticPane - newly visible 2026-08-10 with the Set verb. "
                    + "Writes this install's shared settings file (UserSettingsService.cs:568 sets the "
                    + "bool and calls SaveSettings), so the effect on another user is that the "
                    + "diagnostics panel on /audit is open or shut. Left open deliberately: the pane's "
                    + "contents are already rendered to every role that can reach the page, and gating "
                    + "the toggle would take a reader's own view control away to protect nothing. This "
                    + "is the ONLY entry on this register, and it is a judgement rather than a debt.",
            };

        /// <summary>
        /// Every mutating handler on every routable page is covered by a gate, or is on the
        /// per-handler register with a reason.
        /// </summary>
        [Fact]
        public void EveryMutatingHandlerIsGatedOrRegistered()
        {
            var offenders = CensusMutatingHandlers()
                .Where(f => !HandlersRegisteredUngated.ContainsKey(f.Key))
                .ToList();

            Assert.True(offenders.Count == 0,
                "These HANDLERS mutate and are covered by no gate. The page they are on may well have "
                + "a gate elsewhere - that is the whole point of this census, and why the file-level "
                + "one passes. Gate the handler (or the page), or add it to HandlersRegisteredUngated "
                + "with the reason:\n  " + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// The register cannot rot. An entry that is now gated, or whose handler no longer exists,
        /// fails here - the same discipline <see cref="TheReviewedListHasNoStaleEntries"/> applies
        /// to the page-level lists.
        /// </summary>
        [Fact]
        public void ThePerHandlerRegisterHasNoStaleEntries()
        {
            var live = CensusMutatingHandlers().Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

            var stale = HandlersRegisteredUngated.Keys
                .Where(k => !live.Contains(k))
                .Select(k => k + " - no longer an ungated mutating handler (gated, renamed or gone); remove the entry.")
                .ToList();

            Assert.True(stale.Count == 0, "Stale per-handler register entries:\n  " + string.Join("\n  ", stale));
        }

        // ── Round 8: the discharge, pinned by name AND by permission ─────

        /// <summary>
        /// The twenty handlers round 8 gated on Adrian's ruling, each with the permission it was
        /// gated on. Pinned by name because "the register emptied" is only meaningful if the
        /// specific debts are the ones that went away - the lesson
        /// <see cref="RoundTwoDebtRegister"/> exists for - and pinned by PERMISSION because a
        /// handler re-gated on a weaker one would otherwise read as still discharged.
        ///
        /// <para>Thirteen came off <see cref="HandlersRegisteredUngated"/>. The other seven were
        /// never on it: they became visible the same day, when the verb net gained Move/Accept/
        /// Update/Set/Assign and <see cref="EventBinding"/> gained <c>@bind:after</c>.</para>
        ///
        /// <para>A twenty-first entry joined in round 9, from a source that is not this census at
        /// all: a render. Its provenance is written beside it.</para>
        /// </summary>
        private static readonly (string Key, string Permission)[] RoundEightGatedHandlers =
        {
            // Extended Events DDL and operator-supplied SQL on a CONNECTED SERVER. Same permission
            // Pages/XEvents.razor gates the identical XEventService calls with.
            ("Pages/Alerts.razor::DeployComprehensiveSession", "run_scripts"),
            ("Pages/Alerts.razor::DeployDeadlockSession",      "run_scripts"),
            ("Pages/Alerts.razor::DeployErrorSession",         "run_scripts"),
            ("Pages/Alerts.razor::TestQuery",                  "run_scripts"),

            // The alert definitions and the evaluation engine. Same permission /alerting-config
            // gates its whole page on, and the four alert-config API endpoints require.
            ("Pages/Alerts.razor::ToggleEngine",        "manage_alerts"),
            ("Pages/Alerts.razor::RunNow",              "manage_alerts"),
            ("Pages/Alerts.razor::ToggleAlert",         "manage_alerts"),
            ("Pages/Alerts.razor::OnSeverityChanged",   "manage_alerts"),
            ("Pages/Alerts.razor::SaveEdit",            "manage_alerts"),
            ("Pages/Alerts.razor::SaveTemplate",        "manage_alerts"),
            ("Pages/Alerts.razor::SaveGlobalDefaults",  "manage_alerts"),

            // This install's persisted /audit report layout, and the acceptance decisions that
            // change what every later reader of a report sees.
            ("Pages/QuickCheck.razor::HandleSectionSaved", "settings"),
            ("Pages/QuickCheck.razor::HandleSectionAdded", "settings"),
            ("Pages/QuickCheck.razor::DeleteSection",      "settings"),
            ("Pages/QuickCheck.razor::ToggleSection",      "settings"),
            ("Pages/QuickCheck.razor::MoveSection",        "settings"),
            ("Pages/QuickCheck.razor::ConfirmAcceptance",  "settings"),
            ("Pages/QuickCheck.razor::RevokeAcceptance",   "settings"),

            // Risk-owner assignment (whose MARKUP already tested this permission while the handler
            // did not) and process creation on the host.
            ("Pages/ReportBundles.razor::SaveOwnerRowAsync", "settings"),
            ("Pages/ReportBundles.razor::OpenInExplorer",    "settings"),

            // ROUND 9, 2026-08-10. Not found by this census: found in a RENDER, by the verifier,
            // sitting live in the same flex row as a disabled Stop and a disabled Run Now. It is a
            // property assignment (Engine.DryRun = _dryRun) on a singleton service, and this file's
            // net is call-shaped, so neither census could see it. Ruled onto manage_alerts because
            // the blast radius is identical to ToggleEngine's: while it is on, ten sites in
            // AlertEvaluationService suppress every notification and every history write, for every
            // user. Teaching the census to read property assignments is a separate lane.
            ("Pages/Alerts.razor::ToggleDryRun", "manage_alerts"),
        };

        [Fact]
        public void TheRoundEightDischargeHolds()
        {
            var live = CensusMutatingHandlers().Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

            var regressed = RoundEightGatedHandlers
                .Where(h => live.Contains(h.Key))
                .Select(h => h.Key + " (was gated on " + h.Permission + ")")
                .ToList();

            Assert.True(regressed.Count == 0,
                "Round 8 gated these on Adrian's ruling and the census can see them ungated again:\n  "
                + string.Join("\n  ", regressed));
        }

        /// <summary>
        /// Not just "has A gate" but "has THE gate it was ruled onto". A handler re-gated on a
        /// permission every role holds would pass <see cref="TheRoundEightDischargeHolds"/> while
        /// being open to everyone, which is the shape of every mistake this file records.
        /// </summary>
        [Theory]
        [MemberData(nameof(RoundEightHandlers))]
        public void EachRoundEightHandlerIsGatedOnItsRuledPermission(string key, string permission)
        {
            var parts = key.Split("::", StringSplitOptions.None);
            var (markup, code) = ReadSources(parts[0]);
            Assert.True(markup != null, parts[0] + " is gone; move the entry with the file or drop it.");

            string combined = Strip(markup!) + "\n" + Strip(code!);
            var body = MemberBody(combined, parts[1]);
            Assert.True(body != null, key + " no longer declares that handler.");

            var direct = new Regex(@"IsAuthorized(?:WithBreakGlass)?\s*\(\s*""" + Regex.Escape(permission) + @"""");
            bool covered = direct.IsMatch(body!)
                || MembersGatedOn(combined, permission)
                       .Any(m => Regex.IsMatch(body!, @"\b" + Regex.Escape(m) + @"\b"));

            Assert.True(covered,
                key + " must be covered by a gate naming \"" + permission + "\", either in its own "
                + "body or through a member whose body names it. Body was:\n" + body);
        }

        public static IEnumerable<object[]> RoundEightHandlers =>
            RoundEightGatedHandlers.Select(h => new object[] { h.Key, h.Permission });

        /// <summary>Members whose own body calls a gate naming exactly this permission.</summary>
        private static ISet<string> MembersGatedOn(string code, string permission)
        {
            var gate = new Regex(@"IsAuthorized(?:WithBreakGlass)?\s*\(\s*""" + Regex.Escape(permission) + @"""");
            var found = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match m in Regex.Matches(code, @"\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:\([^()]*\))?\s*(?:=>|\{)"))
            {
                string name = m.Groups[1].Value;
                if (Keywords.Contains(name) || found.Contains(name)) continue;
                string? body = MemberBody(code, name);
                if (body != null && gate.IsMatch(body)) found.Add(name);
            }
            return found;
        }

        /// <summary>
        /// An empty scan proves nothing. This pins that the census is actually reading the tree:
        /// it finds handlers, it classifies pages into the three gate shapes, and the numbers are
        /// not zero.
        /// </summary>
        [Fact]
        public void ThePerHandlerCensusActuallyScansTheTree()
        {
            int pageLevel = 0, handlerLevel = 0, ungated = 0, handlersSeen = 0;

            foreach (var page in EnumerateRoutablePages())
            {
                var (markup, code) = ReadSources(page.Path);
                if (markup == null) continue;

                // The SAME classifier the analyser uses. See PageGateShapeFor for why that matters.
                if (PageGateShapeFor(markup, code!) != null) { pageLevel++; continue; }
                if (!HasAnyGate(Strip(markup) + Strip(code!))) { ungated++; continue; }
                handlerLevel++;
                handlersSeen += ActionHandlers(Strip(markup)).Count;
            }

            // Measured 2026-08-10 on this tree at round 7: 30 / 12 / 46, with 113 handlers examined
            // on the handler-gated pages. (Round 6 recorded 101 for that last number; round 7's
            // widened binding net is the whole of the difference, and the three shape counts are
            // unchanged by it.) Round 8's @bind:after binding raises the handler count again and
            // leaves the three shape counts alone: gating a handler does not change which of the
            // three shapes its PAGE has. The floors are well under those so ordinary page work does
            // not trip them, and well over zero so an empty scan cannot pass.
            string seen = $"page-level={pageLevel} handler-gated={handlerLevel} ungated={ungated} "
                        + $"handlers-on-handler-gated-pages={handlersSeen}";

            Assert.True(pageLevel >= 20, seen);
            Assert.True(handlerLevel >= 5,
                "Fewer handler-gated pages than this tree has. Either a lot of pages just gained a "
                + "page-level gate, or the classifier widened and the per-handler census now has "
                + "almost nothing to look at - which is exactly how this round's own first cut "
                + "failed. " + seen);
            Assert.True(ungated >= 20, seen);
            Assert.True(handlersSeen >= 20,
                "A census that reads no handler passes every assertion for the wrong reason. " + seen);
        }

        // ── Mutation proofs, run every time rather than done once by hand ────

        /// <summary>
        /// MUTATION (i): a page with ONE GATED and ONE UNGATED handler. The file-level census passes
        /// it - that is the defect - and the per-handler census names the ungated one and only that
        /// one. Driven over synthetic source so the proof runs on every build instead of being a
        /// note about something somebody did once.
        /// </summary>
        [Fact]
        public void MutationOne_OneGatedHandlerDoesNotCoverAnUngatedOneOnTheSamePage()
        {
            const string markup = @"
@page ""/probe""
<button @onclick=""GatedSave"">Save</button>
<button @onclick=""UngatedDeploy"">Deploy</button>
";
            const string code = @"
@code {
    private void GatedSave()
    {
        if (!UserState.IsAuthorized(""manage_servers"")) return;
        Store.SaveThing();
    }

    private void UngatedDeploy()
    {
        XEventService.CreateSessionAsync(cs, ""x"", ""y"");
    }
}
";
            // The OLD instrument: any gate anywhere in the file. It passes, which is the defect.
            Assert.True(HasAnyGate(Strip(markup) + Strip(code)),
                "premise: the file-level census sees the gate on the other handler and passes");

            var findings = AnalysePage("Pages/Probe.razor", markup, code);

            Assert.True(findings.Count == 1,
                "expected exactly the ungated handler, got: " + string.Join(" | ", findings));
            Assert.Equal("UngatedDeploy", findings[0].Handler);
            Assert.Contains("XEventService.CreateSessionAsync", findings[0].Calls);
        }

        /// <summary>
        /// MUTATION (ii): REMOVING a page-level gate. With the gate the page's mutating handlers are
        /// all covered and the census is silent; with the same source minus the gate block, every
        /// one of them is named. The two runs differ ONLY in the gate, so the census is measuring
        /// the gate and not something correlated with it.
        /// </summary>
        [Fact]
        public void MutationTwo_RemovingThePageLevelGateExposesEveryHandlerOnIt()
        {
            const string gateBlock = @"
@if (!MayAct)
{
    <AccessDenied RequiredRole=""Admin"" Message=""no"" />
    return;
}
";
            const string body = @"
<button @onclick=""RunOne"">One</button>
<button @onclick=""RunTwo"">Two</button>
";
            const string code = @"
@code {
    private bool MayAct => UserState.IsAuthorized(""run_scripts"");

    private void RunOne() { Runner.RunAsync(); }
    private void RunTwo() { Store.SaveThing(); }
}
";
            string withGate = "@page \"/probe\"\n" + gateBlock + body;
            string withoutGate = "@page \"/probe\"\n" + body;

            Assert.Equal("early-return", PageLevelGateShape(Strip(withGate), GatedMembers(Strip(withGate) + Strip(code))));
            Assert.Empty(AnalysePage("Pages/Probe.razor", withGate, code));

            var exposed = AnalysePage("Pages/Probe.razor", withoutGate, code);
            Assert.True(exposed.Count == 2,
                "removing the page gate must expose BOTH handlers, got: " + string.Join(" | ", exposed));
            Assert.Contains(exposed, f => f.Handler == "RunOne");
            Assert.Contains(exposed, f => f.Handler == "RunTwo");
        }

        /// <summary>
        /// The other page-level shape in this tree, pinned so a refactor from one to the other does
        /// not read as a lost gate: <c>@if (!gate) { AccessDenied } else { the whole page }</c>,
        /// which Pages/Servers.razor uses and which has no <c>return;</c> in it at all.
        /// </summary>
        [Fact]
        public void TheIfElsePageGateShapeIsRecognised()
        {
            const string markup = @"
@page ""/probe""
@if (!UserState.IsAuthorized(""manage_servers""))
{
    <AccessDenied RequiredRole=""Admin"" Message=""no"" />
}
else
{
    <button @onclick=""SaveIt"">Save</button>
}
";
            const string code = @"@code { private void SaveIt() { Store.SaveThing(); } }";

            Assert.Equal("if-else", PageLevelGateShape(Strip(markup), GatedMembers(Strip(markup) + Strip(code))));
            Assert.Empty(AnalysePage("Pages/Probe.razor", markup, code));
        }

        /// <summary>
        /// A gate that sits BELOW the controls it is supposed to protect is not a page gate. This is
        /// the Onboarding lesson (the gate has to be above the step switch) expressed as a property
        /// of the census rather than of one page.
        /// </summary>
        [Fact]
        public void AGateBelowTheControlsIsNotAPageGate()
        {
            const string markup = @"
@page ""/probe""
<button @onclick=""SaveIt"">Save</button>
@if (!UserState.IsAuthorized(""manage_servers""))
{
    <AccessDenied RequiredRole=""Admin"" Message=""no"" />
    return;
}
";
            const string code = @"@code { private void SaveIt() { Store.SaveThing(); } }";

            Assert.Null(PageLevelGateShape(Strip(markup), GatedMembers(Strip(markup) + Strip(code))));
            Assert.Contains(AnalysePage("Pages/Probe.razor", markup, code), f => f.Handler == "SaveIt");
        }

        /// <summary>Indirection through a gated member, and through a field assigned from a gate,
        /// both count as covering a handler. Pins the two shapes this tree uses.</summary>
        [Fact]
        public void AHandlerCoveredByAGatedMemberOrAGatedFieldIsNotAnOffender()
        {
            const string markup = @"
@page ""/probe""
<button @onclick=""ViaProperty"">A</button>
<button @onclick=""ViaField"">B</button>
<button @onclick=""ViaNothing"">C</button>
";
            const string code = @"
@code {
    private bool CanAct => UserState.IsAuthorized(""settings"");
    private bool _canControl;
    protected override void OnInitialized() { _canControl = UserState.IsAuthorized(""run_scripts""); }

    private void ViaProperty() { if (!CanAct) return; Runner.RunAsync(); }
    private void ViaField()    { if (!_canControl) return; Collector.StartAll(); }
    private void ViaNothing()  { Collector.StartAll(); }
}
";
            var findings = AnalysePage("Pages/Probe.razor", markup, code);
            Assert.True(findings.Count == 1, "got: " + string.Join(" | ", findings));
            Assert.Equal("ViaNothing", findings[0].Handler);
        }

        /// <summary>
        /// MUTATION (iii), round 7: a handler reached through a child component's EventCallback
        /// parameter is enumerated. Round 6's net named the nine <c>@on*</c> attributes only, so the
        /// SAME handler with the SAME body and the SAME (absent) gate was a finding when bound
        /// <c>@onclick=</c> and invisible when bound <c>OnSave=</c>. This pins the difference away.
        /// </summary>
        [Fact]
        public void MutationThree_AHandlerBoundToAComponentEventCallbackIsEnumerated()
        {
            const string markup = @"
@page ""/probe""
<button @onclick=""GatedSave"">Save</button>
<SectionEditorModal IsVisible=""true"" OnSave=""UngatedUpsert"" OnCancel=""() => _open = false"" />
";
            const string code = @"
@code {
    private bool _open;

    private void GatedSave()
    {
        if (!UserState.IsAuthorized(""settings"")) return;
        Store.SaveThing();
    }

    private void UngatedUpsert(ReportSection s)
    {
        ReportPageCfg.UpsertSection(_pageDef.Id, s);
    }
}
";
            Assert.Contains("UngatedUpsert", ActionHandlers(Strip(markup)));

            var findings = AnalysePage("Pages/Probe.razor", markup, code);
            Assert.True(findings.Count == 1, "got: " + string.Join(" | ", findings));
            Assert.Equal("UngatedUpsert", findings[0].Handler);
            Assert.Contains("ReportPageCfg.UpsertSection", findings[0].Calls);
        }

        /// <summary>
        /// MUTATION (iv), round 7: a lambda binding resolves the handler's DECLARATION, not the
        /// markup that follows the call site. Round 6's <see cref="MemberBody"/> took the first
        /// brace-matching occurrence of the name in <c>markup + codebehind</c>, and for a lambda
        /// binding that is the call site, so the "body" it analysed was markup and the handler was
        /// dropped for having no mutating call. The markup below carries a brace after the binding
        /// on purpose: that is the data dependence which made the old miss silent.
        /// </summary>
        [Fact]
        public void MutationFour_ALambdaBindingResolvesTheDeclarationAndNotTheMarkup()
        {
            const string markup = @"
@page ""/probe""
<SectionList OnDelete=""s => DeleteThing(s)"" OnToggle=""s => ToggleThing(s)"">
    <div class=""execution-info"">
        @if (_open) { <span>open</span> }
    </div>
</SectionList>
";
            const string code = @"
@code {
    private bool _open;
    private bool CanAct => UserState.IsAuthorized(""settings"");

    private void DeleteThing(ReportSection s)
    {
        ReportPageCfg.DeleteSection(_pageDef.Id, s.Id);
    }

    private void ToggleThing(ReportSection s)
    {
        if (!CanAct) return;
        ReportPageCfg.UpsertSection(_pageDef.Id, s);
    }
}
";
            string combined = Strip(markup) + "\n" + Strip(code);

            var body = MemberBody(combined, "DeleteThing");
            Assert.NotNull(body);
            Assert.Contains("ReportPageCfg.DeleteSection", body!);
            Assert.DoesNotContain("execution-info", body);

            var findings = AnalysePage("Pages/Probe.razor", markup, code);
            Assert.True(findings.Count == 1,
                "the gated sibling must stay covered and the ungated one must be named; got: "
                + string.Join(" | ", findings));
            Assert.Equal("DeleteThing", findings[0].Handler);
        }

        /// <summary>
        /// MUTATION (v), round 9: the verifier's probe, kept. Two handlers, one call, one
        /// difference: <c>MarkupBound</c> is bound by attribute and <c>SubscribedBound</c> by
        /// <c>+=</c> in <c>OnInitialized</c>. Before <see cref="HandlerSubscription"/> the census
        /// named the first and not the second while passing green. The gated third handler is here
        /// so a fix that simply names every subscribed handler regardless of its gate fails too.
        /// </summary>
        [Fact]
        public void MutationFive_AHandlerBoundByEventSubscriptionIsEnumerated()
        {
            const string markup = @"
@page ""/probe""
<button @onclick=""MarkupBound"">Delete</button>
";
            const string code = @"
@code {
    protected override void OnInitialized()
    {
        ShortcutSvc.OnExportCsvRequested += SubscribedBound;
        ShortcutSvc.OnRunRequested += SubscribedGated;
        _total += _count;
    }

    private void MarkupBound()
    {
        ReportPageCfg.DeleteSection(_pageDef.Id, ""markup"");
    }

    private void SubscribedBound()
    {
        ReportPageCfg.DeleteSection(_pageDef.Id, ""kbd"");
    }

    private void SubscribedGated()
    {
        if (!UserState.IsAuthorized(""settings"")) return;
        ReportPageCfg.DeleteSection(_pageDef.Id, ""gated"");
    }
}
";
            var subscribed = SubscribedHandlers(Strip(markup) + "\n" + Strip(code));
            Assert.Contains("SubscribedBound", subscribed);
            Assert.Contains("SubscribedGated", subscribed);
            Assert.DoesNotContain("_count", subscribed);

            var named = AnalysePage("Pages/Probe.razor", markup, code)
                .Select(f => f.Handler).ToList();

            Assert.Contains("MarkupBound", named);
            Assert.Contains("SubscribedBound", named);
            Assert.DoesNotContain("SubscribedGated", named);
        }

        /// <summary>
        /// The verb the seventh Alerts handler needed. Driven end to end through
        /// <see cref="MutatingCalls"/> so a later narrowing of the vocabulary fails here rather than
        /// silently un-finding <c>Engine.EvaluateAllAsync</c>.
        /// </summary>
        [Fact]
        public void TheNetSeesAnEvaluateCall()
        {
            var found = MutatingCalls("await Engine.EvaluateAllAsync();").ToList();
            Assert.Contains("Engine.EvaluateAllAsync", found);
        }

        // ── The per-handler analyser ─────────────────────────────────────

        /// <summary>
        /// Every way this tree hands a click to a method: a DOM event attribute, and a child
        /// component's EventCallback parameter.
        ///
        /// <para><b>Round 7, 2026-08-10.</b> This regex used to name the nine <c>@on*</c> attributes
        /// and nothing else, so a handler reached through a component parameter
        /// (<c>OnSave="X"</c>, <c>OnDelete="s =&gt; X(s)"</c>) was never enumerated and
        /// <see cref="AnalysePage"/> never looked at it. That was not a stated limit and it points
        /// the wrong way: LESS review, not more. Measured cost on this tree, 2026-08-10: four real
        /// ungated mutating handlers on <c>Pages/QuickCheck.razor</c> - HandleSectionSaved and
        /// HandleSectionAdded (bound <c>OnSave=</c> / <c>OnAdd=</c>), DeleteSection and ToggleSection
        /// (bound <c>OnDelete="s =&gt; DeleteSection(s)"</c>, so blind to this regex AND to
        /// <see cref="MemberBody"/>), every one of them writing this install's persisted
        /// report-section definitions through <c>ReportPageCfg.UpsertSection</c> /
        /// <c>DeleteSection</c> - on a page that PASSES the file-level census because it has an
        /// IsAuthorized elsewhere. Exactly the shape round 6 exists to catch, invisible on a syntax
        /// difference. The axis this time is the BINDING SYNTAX rather than the verb or the
        /// granularity.</para>
        ///
        /// <para><c>On[A-Z]</c> is the convention every EventCallback parameter in this tree follows.
        /// A PascalCase attribute that is not a callback contributes a name that is never declared as
        /// a member, and <see cref="MemberBody"/> returns null for it, so the noise costs nothing.</para>
        ///
        /// <para><b>Round 8, 2026-08-10: <c>@bind:after</c>.</b> Round 7 widened this regex on the
        /// binding-syntax axis and STILL left a syntax out. <c>Pages/Alerts.razor</c> binds four
        /// Global Settings inputs with <c>@bind:after="SaveGlobalDefaults"</c>, and that handler
        /// calls <c>Definitions.UpdateGlobalDefaults</c> - which persists this install's global alert
        /// configuration, including the RetentionDays that decides when alert history is DELETED.
        /// It was ungated, on a page this census had already produced seven entries for, and no
        /// round had ever enumerated it. Measured cost of adding <c>@bind:after</c> and
        /// <c>@bind:set</c> to this regex: exactly that one handler, no noise. Same lesson, same
        /// axis, one round later.</para>
        /// </summary>
        private static readonly Regex EventBinding = new(
            @"(?:@on(?:click|change|submit|input|dblclick|keyup|keydown|blur|focus)|@bind:(?:after|set)|\bOn[A-Z][A-Za-z0-9_]*)\s*=\s*""([^""]*)""",
            RegexOptions.Compiled);

        /// <summary>
        /// A handler bound by C# EVENT SUBSCRIPTION rather than by markup attribute, which is how
        /// every keyboard shortcut in this tree is wired: <c>ShortcutSvc.OnRunRequested +=
        /// OnShortcutRun;</c> in <c>OnInitialized</c>.
        ///
        /// <para><b>Why this exists, round 9, 2026-08-10.</b> Rounds 6, 7 and 8 each widened
        /// <see cref="EventBinding"/> on the binding-SYNTAX axis and each disclosed the next syntax
        /// it could not see. All three stayed inside markup. Verification of round 8 planted an
        /// ungated mutating handler on <c>Pages/QuickCheck.razor</c> calling the same
        /// <c>ReportPageCfg.DeleteSection</c> as a markup-bound control probe, changed NOTHING but
        /// the binding, and the census went 57/57 green while naming the markup-bound twin. The
        /// handler was never ENUMERATED, so it was never asked whether it was gated: a miss upstream
        /// of the verb net, which no amount of widening <see cref="ActingVerbs"/> could reach. This
        /// file had documented its binding limits three times and never that a whole binding
        /// MECHANISM sat outside the enumeration.</para>
        ///
        /// <para><b>Why the receiver must be dotted.</b> <c>Svc.Event += Handler;</c> is the shape;
        /// requiring at least one <c>.</c> keeps numeric and string accumulation
        /// (<c>_total += count;</c>) out. Measured cost across <c>Pages/**</c> at the time of
        /// writing: twenty matches, all twenty real event subscriptions, no noise. A name that is
        /// not a declared member costs nothing anyway, because <see cref="MemberBody"/> returns null
        /// for it.</para>
        ///
        /// <para><b>Still outside the enumeration, stated rather than discovered later.</b> A
        /// subscription whose right side is a lambda (<c>Svc.Event += () =&gt; Foo();</c>), one
        /// wired through <c>new EventHandler(Foo)</c>, and one built anywhere other than a
        /// <c>;</c>-terminated statement. Also <c>-=</c>, deliberately: unsubscription is not a
        /// binding. And this reads the page's own sources only, so a handler passed to a service
        /// that subscribes it elsewhere is invisible here.</para>
        /// </summary>
        private static readonly Regex HandlerSubscription = new(
            @"\b[A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)+\s*\+=\s*([A-Za-z_][A-Za-z0-9_]*)\s*;",
            RegexOptions.Compiled);

        private static readonly Regex AnyGate = new(
            @"UserState\s*\.\s*IsAuthorized|IsAuthorizedWithBreakGlass|RbacService\.HasPermission",
            RegexOptions.Compiled);

        private static readonly Regex GateAssignment = new(
            @"\b([A-Za-z_][A-Za-z0-9_]*)\s*=\s*[^;\r\n]*(?:IsAuthorized|IsAuthorizedWithBreakGlass|HasPermission)\s*\(",
            RegexOptions.Compiled);

        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "try", "else",
            "do", "return", "new", "get", "set", "await",
        };

        internal static bool HasAnyGate(string text) => AnyGate.IsMatch(text);

        /// <summary>
        /// Removes comment spans so the brace matching and the gate search read code. Same honest
        /// limit as everywhere else in this file: an unquoted // inside a string swallows the rest
        /// of that line, which can only make this MISS a gate and never invent one - and a missed
        /// gate lands a handler on the register, where a human reads it.
        /// </summary>
        internal static string Strip(string text)
        {
            text = Regex.Replace(text, @"@\*[\s\S]*?\*@", " ");
            text = Regex.Replace(text, @"<!--[\s\S]*?-->", " ");
            text = Regex.Replace(text, @"/\*[\s\S]*?\*/", " ");
            return Regex.Replace(text, @"//[^\r\n]*", "");
        }

        private static int MatchBlock(string text, int openBrace)
        {
            int depth = 0;
            for (int i = openBrace; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>
        /// The shape of a gate that covers the WHOLE page body, or null when there is none.
        /// "early-return", "if-else" or "attribute".
        /// </summary>
        internal static string? PageLevelGateShape(string markup, ISet<string> gatedMembers)
        {
            if (markup.Contains("@attribute [Authorize", StringComparison.Ordinal)) return "attribute";

            var firstBinding = EventBinding.Match(markup);
            int limit = firstBinding.Success ? firstBinding.Index : markup.Length;

            foreach (Match m in Regex.Matches(markup, @"@if\s*\("))
            {
                // A gate below the controls it claims to protect is not a page gate.
                if (m.Index > limit) break;

                int condEnd = markup.IndexOf(')', m.Index + m.Length - 1);
                if (condEnd < 0) continue;
                string head = markup.Substring(m.Index, condEnd - m.Index + 1);

                bool namesAGate = AnyGate.IsMatch(head)
                    || gatedMembers.Any(g => Regex.IsMatch(head, @"\b" + Regex.Escape(g) + @"\b"));
                if (!namesAGate) continue;

                int brace = markup.IndexOf('{', condEnd);
                if (brace < 0) continue;
                int close = MatchBlock(markup, brace);
                if (close < 0) continue;

                if (Regex.IsMatch(markup.Substring(brace, close - brace), @"^\s*return;\s*$", RegexOptions.Multiline))
                    return "early-return";
                if (Regex.IsMatch(markup.Substring(close + 1), @"^\s*else\b"))
                    return "if-else";
            }
            return null;
        }

        /// <summary>
        /// Members whose body calls a gate (<c>CanAct</c>, <c>MayExport</c>) plus fields assigned
        /// from one. A handler that references any of these is covered.
        /// </summary>
        internal static ISet<string> GatedMembers(string code)
        {
            var gated = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match m in Regex.Matches(code, @"\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:\([^()]*\))?\s*(?:=>|\{)"))
            {
                string name = m.Groups[1].Value;
                if (Keywords.Contains(name) || gated.Contains(name)) continue;
                string? body = MemberBody(code, name);
                if (body != null && AnyGate.IsMatch(body)) gated.Add(name);
            }

            foreach (Match m in GateAssignment.Matches(code))
                gated.Add(m.Groups[1].Value);

            return gated;
        }

        /// <summary>The body of the first member declared with this name, brace-matched or
        /// expression-bodied. Null when the name is only ever called, never declared.</summary>
        internal static string? MemberBody(string code, string name)
        {
            foreach (Match m in Regex.Matches(code, @"\b" + Regex.Escape(name) + @"\s*(?:\(|=>|\{)"))
            {
                // ROUND 7, 2026-08-10: this used to take the FIRST brace-matching occurrence of the
                // name anywhere in `markup + codebehind`, and for a lambda binding the CALL SITE in
                // the markup comes first. `OnDelete="s => DeleteSection(s)"` therefore resolved
                // DeleteSection's "body" to the markup that follows it, which contains no mutating
                // call, so the handler was dropped with calls.Count == 0 while looking analysed. The
                // miss was data-dependent (whether a { or a ; came first after the call site), i.e.
                // silent and unpredictable rather than a stated limit. A declaration always carries
                // its return type immediately before the name; a call site never does.
                if (!IsDeclarationSite(code, m.Index)) continue;

                string tail = code.Substring(m.Index);
                var expr = Regex.Match(tail, @"^" + Regex.Escape(name) + @"\s*(?:\([^()]*\))?\s*=>");
                if (expr.Success)
                {
                    int semi = tail.IndexOf(';', expr.Length);
                    if (semi > 0) return tail.Substring(0, semi);
                }

                int brace = code.IndexOf('{', m.Index + m.Length - 1);
                if (brace < 0) continue;
                int semicolon = code.IndexOf(';', m.Index + m.Length - 1);
                if (semicolon >= 0 && semicolon < brace) continue;   // a call, not a declaration
                int close = MatchBlock(code, brace);
                if (close > 0) return code.Substring(m.Index, close - m.Index + 1);
            }
            return null;
        }

        /// <summary>
        /// Tokens that can sit immediately before a name at a CALL site and look like a return type
        /// because they are ordinary identifiers. <c>await Foo()</c> is the one that matters here.
        /// </summary>
        private static readonly HashSet<string> NotATypeToken = new(StringComparer.Ordinal)
        {
            "await", "return", "new", "throw", "yield", "case", "is", "as", "in", "out", "ref",
            "when", "else", "do", "while", "if", "for", "foreach", "switch", "lock", "using",
            "try", "catch", "finally", "and", "or", "not", "from", "select", "where", "let",
            "into", "orderby", "group", "by", "on", "equals", "checked", "unchecked", "default",
            "typeof", "nameof", "sizeof", "stackalloc",
        };

        /// <summary>
        /// Whether the name starting at <paramref name="index"/> is a DECLARATION rather than a call
        /// or a markup binding, judged by what immediately precedes it.
        ///
        /// <para>A member declaration always carries its return type right before the name
        /// (<c>void Foo()</c>, <c>Task&lt;bool&gt; Foo()</c>, <c>bool CanAct =&gt;</c>). A call site
        /// never does: it is preceded by <c>.</c>, <c>(</c>, <c>=</c>, <c>=&gt;</c>, a statement
        /// terminator, a quote, or a keyword such as <c>await</c>. Both directions of error here
        /// point at MORE review, which is the property this file has repeatedly failed to hold:
        /// treating a declaration as a call drops it out of <see cref="GatedMembers"/>, so the
        /// handlers it covered land on the register where a human reads them.</para>
        /// </summary>
        private static bool IsDeclarationSite(string code, int index)
        {
            string before = code.Substring(0, index).TrimEnd();
            if (before.Length == 0) return false;                       // no return type = not a declaration
            if (before.EndsWith("=>", StringComparison.Ordinal)) return false;   // a lambda body, not a member

            char last = before[before.Length - 1];
            if (last == '>' || last == ']') return true;                // Task<T>, string[], List<T>[]
            if (!char.IsLetterOrDigit(last) && last != '_') return false;

            var token = Regex.Match(before, @"[A-Za-z_][A-Za-z0-9_]*$").Value;
            return !NotATypeToken.Contains(token);
        }

        /// <summary>Handler names bound to events in the markup. A lambda contributes every method
        /// it invokes, so <c>@onchange="e =&gt; ToggleAlert(a, v)"</c> names ToggleAlert.</summary>
        internal static IReadOnlyList<string> ActionHandlers(string markup)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match b in EventBinding.Matches(markup))
            {
                string value = b.Groups[1].Value.Trim();
                var bare = Regex.Match(value, @"^@?([A-Za-z_][A-Za-z0-9_]*)$");
                if (bare.Success) { names.Add(bare.Groups[1].Value); continue; }
                foreach (Match c in Regex.Matches(value, @"\b([A-Za-z_][A-Za-z0-9_]*)\s*\("))
                    if (c.Groups[1].Value != "nameof") names.Add(c.Groups[1].Value);
            }
            return names.ToList();
        }

        /// <summary>Handler names bound by C# event subscription. See
        /// <see cref="HandlerSubscription"/> for why this is a separate enumeration and what it
        /// still cannot see.</summary>
        internal static IReadOnlyList<string> SubscribedHandlers(string code)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match m in HandlerSubscription.Matches(code))
                names.Add(m.Groups[1].Value);
            return names.ToList();
        }

        /// <summary>
        /// The page-gate shape for one page's RAW sources, or null. THE SINGLE CLASSIFIER: this
        /// round's own first cut had the analyser stripping comments and the sanity check not, and
        /// the two disagreed about 29 pages - a divergence that made the census toothless while
        /// every assertion still passed. Both callers go through here now.
        /// </summary>
        internal static string? PageGateShapeFor(string rawMarkup, string rawCodeBehind)
        {
            string markup = Strip(rawMarkup);
            return PageLevelGateShape(markup, GatedMembers(markup + "\n" + Strip(rawCodeBehind)));
        }

        /// <summary>The census for ONE page, from its raw sources. Internal to the tests so the
        /// mutation proofs drive the same code path the tree scan does.</summary>
        internal static List<HandlerFinding> AnalysePage(string path, string rawMarkup, string rawCodeBehind)
        {
            string markup = Strip(rawMarkup);
            string code = markup + "\n" + Strip(rawCodeBehind);
            var gated = GatedMembers(code);

            var findings = new List<HandlerFinding>();
            if (PageGateShapeFor(rawMarkup, rawCodeBehind) != null) return findings;

            var handlers = new SortedSet<string>(ActionHandlers(markup), StringComparer.Ordinal);
            foreach (var subscribed in SubscribedHandlers(code)) handlers.Add(subscribed);

            foreach (var handler in handlers)
            {
                string? body = MemberBody(code, handler);
                if (body == null) continue;

                var calls = MutatingCalls(body).ToList();
                if (calls.Count == 0) continue;
                if (AnyGate.IsMatch(body)) continue;
                if (gated.Any(g => Regex.IsMatch(body, @"\b" + Regex.Escape(g) + @"\b"))) continue;

                findings.Add(new HandlerFinding(path, handler, calls));
            }
            return findings;
        }

        /// <summary>Every ungated mutating handler in the tree. Pages with no gate at all are left
        /// to the FILE-level census and its reviewed lists: this round is about the pages that pass
        /// that census while hiding an ungated handler behind a gated one.</summary>
        internal static List<HandlerFinding> CensusMutatingHandlers()
        {
            var findings = new List<HandlerFinding>();
            foreach (var page in EnumerateRoutablePages())
            {
                var (markup, code) = ReadSources(page.Path);
                if (markup == null) continue;
                if (!HasAnyGate(Strip(markup) + Strip(code!))) continue;
                findings.AddRange(AnalysePage(page.Path, markup, code!));
            }
            return findings;
        }

        private static (string? Markup, string? CodeBehind) ReadSources(string relativePath)
        {
            var full = Path.Combine(RawPassedScan.RepoRoot().FullName,
                                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return (null, null);
            return (File.ReadAllText(full), File.Exists(full + ".cs") ? File.ReadAllText(full + ".cs") : "");
        }

        // ── Scanner ──────────────────────────────────────────────────────

        internal sealed record RoutablePage(string Path, string Route, bool HasGate, string? Permission);

        /// <summary>
        /// Every .razor file carrying an <c>@page</c> directive, with whether it (or its
        /// code-behind) contains a recognised authorization gate, and which permission that gate
        /// names.
        /// </summary>
        internal static List<RoutablePage> EnumerateRoutablePages()
        {
            var root = RawPassedScan.RepoRoot();
            var pageDirective = new Regex(@"^\s*@page\s+""([^""]+)""", RegexOptions.Multiline | RegexOptions.Compiled);

            // The gates this codebase recognises. AppUserState.IsAuthorized / IsAuthorizedWithBreakGlass
            // are the sanctioned ones; the static RbacService.HasPermission counts as A gate here
            // (it denies) even though RbacServerModeLockoutTests separately forbids new ones.
            var gate = new Regex(
                @"UserState\s*\.\s*IsAuthorized|IsAuthorizedWithBreakGlass|RbacService\.HasPermission|<AuthorizeView|@attribute\s*\[Authorize",
                RegexOptions.Compiled);
            var permission = new Regex(
                @"IsAuthorized(?:WithBreakGlass)?\s*\(\s*""([a-z_]+)""", RegexOptions.Compiled);

            var pages = new List<RoutablePage>();
            foreach (var scanRoot in new[] { "Pages", "Components" })
            {
                var dir = new DirectoryInfo(Path.Combine(root.FullName, scanRoot));
                if (!dir.Exists) continue;

                foreach (var file in dir.EnumerateFiles("*.razor", SearchOption.AllDirectories))
                {
                    var text = File.ReadAllText(file.FullName);
                    var match = pageDirective.Match(text);
                    if (!match.Success) continue;

                    // A page's gate may live in its .razor.cs — read both before deciding.
                    var codeBehind = file.FullName + ".cs";
                    if (File.Exists(codeBehind)) text += File.ReadAllText(codeBehind);

                    var perm = permission.Match(text);
                    pages.Add(new RoutablePage(
                        Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/'),
                        match.Groups[1].Value,
                        gate.IsMatch(text),
                        perm.Success ? perm.Groups[1].Value : null));
                }
            }
            return pages;
        }

        private static string? ReadPageAndCodeBehind(string root, string relativePath)
        {
            var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return null;
            var text = File.ReadAllText(full);
            if (File.Exists(full + ".cs")) text += File.ReadAllText(full + ".cs");
            return text;
        }

        /// <summary>
        /// Receivers whose "mutating-looking" calls do not change state outside this process.
        /// Every entry needs a reason; an unexplained entry is how a scan gets neutered.
        /// </summary>
        private static readonly Dictionary<string, string> KnownBenignReceivers = new(StringComparer.Ordinal)
        {
            ["File"] = "local export of a report the caller is already allowed to see",
            ["Directory"] = "creating the local output folder for that export",
            // ["Process"] WAS here, excused as "opening the local output folder in Explorer".
            // Removed 2026-08-02. B1 of round 4 falsified the excuse: Pages/Portal/PortalStatus's
            // OpenLastPayload is the same Process.Start(UseShellExecute) shape, and an
            // unauthenticated LAN viewer clicked it into a real process on the host (OpenWith,
            // PID 27908). Process creation is not a read, whatever the path is. The two read-only
            // pages that still do it are named in ReadOnlyProcessStartExceptions with their exact
            // call, so they are an exception somebody wrote down rather than a whole receiver
            // nobody looks at again.
            ["builder"] = "RenderTreeBuilder.AddAttribute/AddContent — building this circuit's own render tree",
            ["Debug"] = "System.Diagnostics.Debug.WriteLine — a no-op in release",
            ["Stopwatch"] = "timing",
            ["SHA256"] = "SHA256.Create() is a factory, not a write",
            ["MD5"] = "hash factory",
            ["DotNetObjectReference"] = "Create() is a JS-interop handle, not a write",
            ["Convert"] = "format conversion",
            ["Encoding"] = "format conversion",
            ["Task"] = "task combinators",
            ["JS"] = "JS interop into the caller's own browser",
            ["JSRuntime"] = "JS interop into the caller's own browser",
            ["Activator"] = "reflection factory",
            ["CultureInfo"] = "format conversion",
            // Added 2026-08-02, newly visible once the verb match stopped being prefix-anchored.
            // AssessmentRehydrationService.EnsureLastRunLoaded is an idempotent COLD-START RESTORE:
            // it returns immediately unless QuickCheckStateService is empty, and when it does run it
            // reads GovernanceHistoryService (this install's own local history) and populates
            // in-memory state. It reaches no server and writes no file. /compliance-map and
            // /compliance-tree call it because they read QuickCheckStateService.Results directly and
            // otherwise render their empty state until somebody visits /audit first.
            ["Rehydration"] = "AssessmentRehydrationService cold-start restore — reads local history into in-memory state; touches no server and no file",
            // Added 2026-08-10 with the Evaluate verb. FullTierGate is a static classifier over the
            // unlocked bundle: Evaluate(IBundleAccessor?) returns a FullTierState enum and writes
            // nothing anywhere. It is excused by RECEIVER rather than by method name because every
            // member of that class is the same pure question asked three ways.
            ["FullTierGate"] = "Licensing.FullTierGate.Evaluate — a pure classifier returning FullTierState; reads the unlocked bundle and writes nothing",
        };

        /// <summary>
        /// Method names that match a mutating verb but operate on a local collection, a DateTime,
        /// or the DI container. Filtered by METHOD, never by receiver: filtering <c>list.Add</c> by
        /// naming every local variable would be endless, and would silently swallow a real call the
        /// day somebody names a service <c>list</c>.
        /// </summary>
        private static readonly HashSet<string> NoisyMethods = new(StringComparer.Ordinal)
        {
            "Add", "AddRange", "AddFirst", "AddLast", "AddOrUpdate",
            "AddDays", "AddHours", "AddMinutes", "AddSeconds", "AddMilliseconds",
            "AddYears", "AddMonths", "AddTicks",
            "AddSingleton", "AddScoped", "AddTransient", "AddLogging",
            "Remove", "RemoveAt", "RemoveAll", "RemoveRange",
            "Create",   // Foo.Create() factories; a real write reads Create<Noun>
            // Added with the Run/Start/Stop verbs below.
            "StartsWith", "StartNew", "AddWithValue",
            // Added 2026-08-02 with the de-anchored verb match: string/Task/ComponentBase
            // plumbing whose names now match because the verb sits mid-name.
            "TrimStart", "TrimEnd", "PadStart", "PadEnd",
            "InvokeAsync",        // ComponentBase.InvokeAsync — marshals onto this circuit's own renderer
            "InvokeVoidAsync",    // JS interop; the JS/JSRuntime receivers are already excused, this covers the other injection names
            "SetParametersAsync",
            // Added 2026-08-10 with the Set verb: ToHashSet is a LINQ operator that materialises a
            // NEW collection from a sequence and writes nothing anywhere. It matched on three pages
            // (QuickCheck's two Export handlers, ComplianceMap, VulnerabilityAssessment) and one of
            // those is on ReadOnlySurfaces, so without this the read-only test fails on a copy.
            "ToHashSet",
            // Same round, same verb: ComponentBase's own lifecycle hook, reached as
            // base.OnParametersSet() by Components/Shared/DynamicPanel.razor. The sibling
            // OnParametersSetAsync is covered by the Async form being a different name, so both are
            // listed rather than matched by prefix.
            "OnParametersSet", "OnParametersSetAsync",
        };

        /// <summary>
        /// Method-name prefixes that make a call a READ by this codebase's own naming convention.
        ///
        /// <para>Needed only because the verb match is no longer prefix-anchored (see
        /// <see cref="MutatingCalls"/>): once a verb may sit anywhere in the name,
        /// <c>GetEnabledConnections</c> and <c>IsSoftEnabled</c> match on "Enable".</para>
        ///
        /// <para>Bounded deliberately. <see cref="ReaderPrefixesNeverHideAnActingVerbPrefix"/>
        /// fails if any entry here would swallow a name that begins with an acting verb, so this
        /// list cannot grow into a way of re-anchoring the net by the back door. <c>Try</c> and
        /// <c>Ensure</c> are NOT here: TryDeleteX and EnsureLastRunLoaded both act. Nor is
        /// <c>Can</c>, which would also swallow <c>Cancel…</c>.</para>
        /// </summary>
        /// <summary>
        /// The acting-verb vocabulary. ONE array, read by <see cref="MutatingCalls"/> to build its
        /// regex and by <see cref="ReaderPrefixesNeverHideAnActingVerbPrefix"/> to guard the reader
        /// prefixes against it, so the net and its guard cannot describe different vocabularies.
        ///
        /// <para><b>Round 8, 2026-08-10.</b> Round 7 disclosed its own residual in writing —
        /// <c>ReportPageCfg.MoveSection</c> and <c>AcceptedFindings.Accept</c> were invisible
        /// because Move and Accept were not verbs here — and then registered around it. This round
        /// adds them, plus the siblings the same reading gives: <c>Update</c> (the twin of Save and
        /// Upsert), <c>Set</c>, <c>Assign</c>, and <c>Reject</c> for the Accept pair. MEASURED cost
        /// on this tree, before any gating: EIGHT more ungated mutating handlers, six of which write
        /// persisted state on pages this census already had entries for.</para>
        /// <list type="bullet">
        /// <item>Move → <c>Pages/QuickCheck.razor::MoveSection</c> (the disclosed one).</item>
        /// <item>Accept → <c>Pages/QuickCheck.razor::ConfirmAcceptance</c> (the other disclosed one).</item>
        /// <item>Update → <c>Pages/Alerts.razor::OnSeverityChanged</c>, <c>::SaveEdit</c> (both
        ///   <c>Definitions.UpdateAlert</c>) and <c>::SaveTemplate</c> (<c>Templates.Update</c>),
        ///   i.e. THREE more on the very page round 6 audited by hand and round 7 re-audited.</item>
        /// <item>Set → <c>Pages/QuickCheck.razor::ToggleDiagnosticPane</c>. The one entry left on
        ///   <see cref="HandlersRegisteredUngated"/>, with its reason.</item>
        /// <item>Assign → <c>Pages/ReportBundles.razor::SaveOwnerRowAsync</c>
        ///   (<c>ReportBundleSvc.AssignOwner</c>), whose markup was ALREADY behind
        ///   <c>IsAuthorized("settings")</c> while the handler itself had no gate: the exact shape
        ///   this census exists for, hiding behind a verb.</item>
        /// <item>Reject → nothing on this tree. Added anyway as Accept's pair, so the day somebody
        ///   writes the other half of an approve/reject control the net already names it.</item>
        /// </list>
        /// <para>The eighth was not a verb at all: <c>Pages/Alerts.razor::SaveGlobalDefaults</c>,
        /// bound <c>@bind:after=</c> and therefore invisible to <see cref="EventBinding"/>. See that
        /// field. SIXTH TIME OF ASKING, and the axis was the same one round 7 named.</para>
        ///
        /// <para><b>Measured and NOT added, so the next round does not re-derive it.</b> A superset
        /// of 43 candidate verbs was run over this tree on 2026-08-10. Everything the extra 37
        /// produced was in-process noise: <c>Clear</c> (eight sites, every one a local collection or
        /// a view-state reset), <c>Insert</c> (<c>List.Insert</c>), <c>Replace</c>
        /// (<c>string.Replace</c>), and <c>Send</c>/<c>Upload</c>/<c>Post</c>, which produced zero
        /// real sites and one reader (<c>Uploader.LastUploadStatus</c>). They are left out because
        /// they were measured, not because the net was kept small.</para>
        /// </summary>
        internal static readonly string[] ActingVerbs =
        {
            "Save","Write","Delete","Apply","Upsert","Create","Import","Publish","Deploy",
            "Install","Execute","Enable","Disable","Drop","Revoke","Purge","Add","Remove",
            "Run","Start","Stop","Trigger","Invoke","Request","Fire","Dispatch","Raise",
            "Notify","Broadcast","Evaluate",
            // Round 8, 2026-08-10. See the note above for what each one cost, by name.
            "Move","Accept","Reject","Update","Set","Assign",
        };

        private static readonly string[] ReaderPrefixes =
        {
            "Get", "Is", "Has", "Read", "Format",
            // "Load" earns its place on a false positive the new Fire verb produced:
            // IAuditOutputScanner.LoadFiredChecksAsync parses sp_Blitz CSVs already on disk, and
            // "Fired" is a noun in it. Every Load* in this tree hydrates memory from local state.
            "Load",
            // "Find" earns its place on the per-handler census (2026-08-10): ReplicationMap's
            // ScanAsync and LoadSample call ReplicationTopology.FindAddable, which RETURNS the
            // participants that could be added and adds none. "Add" is a noun in it, the same shape
            // as "Fired" above. Bounded by the same guard as every other entry here: no acting verb
            // in this file's vocabulary starts with "Find", and
            // ReaderPrefixesNeverHideAnActingVerbPrefix fails if one ever does.
            "Find",
        };

        /// <summary>
        /// READ-ONLY LIST EXCEPTIONS, pinned by their exact call set.
        ///
        /// <para>These two pages stay on <see cref="ReadOnlySurfaces"/> even though the scan finds
        /// a call on them, and the exception is recorded here rather than hidden by excusing a
        /// receiver globally. Both are the "export a report, then show the operator where it went"
        /// shape. They are the SAME SHAPE as round 4's B1 — <c>Process.Start</c> with
        /// <c>UseShellExecute</c>, i.e. process creation on the host, reachable by whoever can
        /// reach the page — and the difference is that the path is a file the app has just written
        /// with an extension it chose, not a payload lying on disk. That is a weaker exposure, not
        /// no exposure, and NO ROUND HAS YET BEEN ASKED TO DECIDE IT. It is left open deliberately
        /// and named here so the next reviewer overturns it knowingly.</para>
        ///
        /// <para>The value is the exact call set. A new call on either page fails the test, so the
        /// exception cannot quietly widen into "this page is excused".</para>
        /// </summary>
        private static readonly Dictionary<string, string[]> ReadOnlyProcessStartExceptions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // opens the CIO executive PDF it just wrote to the output folder
                ["Pages/CioDashboard.razor"] = new[] { "Process.Start" },
                // explorer.exe /select,"<path>" — reveals the exported report in its folder
                ["Pages/PerformanceReport.razor"] = new[] { "Process.Start" },
                // opens the offboarding trace PDF it just wrote to the output folder
                ["Pages/OffboardingTrace.razor"] = new[] { "Process.Start" },
            };

        /// <summary>
        /// Extracts <c>Receiver.MutatingVerb(</c> calls, minus the local-collection and
        /// format-conversion noise. Deliberately a coarse net: this test exists to make somebody
        /// LOOK, and a false positive costs one line of explanation while a false negative cost
        /// five ungated write surfaces.
        /// </summary>
        internal static IEnumerable<string> MutatingCalls(string text)
        {
            // `Add` is in the list because ConnectionManager.AddConnection — writing a discovered
            // instance, credentials and all, into this install's server catalogue — is exactly the
            // kind of call a narrower net misses. It WAS missed: an earlier draft of this regex
            // omitted Add to keep the noise down, and EnvironmentView.razor passed the census while
            // making that call. Noise is handled by NoisyMethods below, not by narrowing the net.
            // Run|Start|Stop were added 2026-08-02. Without them the net could not see
            // Pages/Governance.razor's QuickCheckRunner.RunAsync — the call a round-4 gate DROVE
            // from a LAN origin into "QuickCheck starting on tcp:localhost,56510" — while
            // Governance sat on the READ-ONLY list. Widening them cost three entries in total
            // (Governance, Benchmark's BenchmarkService.RunBenchmarkAsync and PerformanceTrends'
            // HistoricalPerf.RunRollupAsync), all three of which were real and are now gated. That
            // is the same lesson as the Add verb one revision earlier: handle noise by method name,
            // never by narrowing the net.
            //
            // THE VERB MAY SIT ANYWHERE IN THE METHOD NAME (fixed 2026-08-02, round 5).
            //
            // Until this revision the alternation sat immediately after `receiver.`, so the net
            // only ever matched methods whose name BEGAN with a verb. Run over
            // Components/Layout/MainLayout.razor it therefore returned exactly
            // {AutoUpdate.StartBackgroundCheck, Tour.Start, _maintenanceTimer.Start} — precisely
            // the three calls pinned in ReviewedActingComponents — and was structurally unable to
            // see ShortcutSvc.TriggerRun / TriggerExportPdf / TriggerExportCsv sitting four lines
            // away, which fired a privileged run in OTHER circuits. Because the pinned set matched
            // what the blind scan found, the census PASSED with the defect live.
            //
            // Round 4 had already widened the verb VOCABULARY (Run|Start|Stop) and still missed
            // it: the hole was the ANCHORING, not the words. Both were wrong, and only one of them
            // was being fixed each round.
            //
            // Trigger|Invoke|Request|Fire|Dispatch|Raise|Notify|Broadcast were added at the same
            // time. They are the vocabulary of "hand this off to somebody else's handler" — the
            // shape that turns a control into a confused deputy.
            // Evaluate was added 2026-08-10, round 7. Without it the net could not see
            // Pages/Alerts.razor's `Engine.EvaluateAllAsync` behind the ungated Run Now button, and
            // AlertEvaluationService.EvaluateAllAsync calls _history.AutoAcknowledge - the same
            // effect the two GATED handlers on that page are gated for. The page was audited by hand
            // in round 6 and the seventh handler was still missed, because a hand audit that trusts
            // the instrument inherits the instrument's blind spot. Same lesson, VOCABULARY axis.
            var call = new Regex(
                @"\b([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*([A-Za-z0-9_]*(?:"
                + string.Join("|", ActingVerbs) + @")[A-Za-z0-9_]*)\s*\(",
                RegexOptions.Compiled);

            // Drop comment lines before scanning — a mention of ExecuteChecksAsync in a comment is
            // not a call, and treating it as one is exactly the kind of noise that gets a guard
            // switched off.
            var code = string.Join("\n", text.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith("*", StringComparison.Ordinal)));

            var seen = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match m in call.Matches(code))
            {
                var receiver = m.Groups[1].Value;
                var method = m.Groups[2].Value;
                if (KnownBenignReceivers.ContainsKey(receiver)) continue;

                // ExecuteReader returns a result set: it is how this app READS from SQL Server.
                // ExecuteNonQuery / ExecuteChecks / ExecuteAsync are NOT excused — those are the
                // shapes that write, and ScheduledTasks.DeployOlaMaintenanceAsync uses the first
                // of them.
                if (method.StartsWith("ExecuteReader", StringComparison.Ordinal)) continue;
                if (NoisyMethods.Contains(method)) continue;

                // De-anchoring the verb (above) means a READER whose name merely CONTAINS one now
                // matches — GetEnabledConnections, IsSoftEnabled, ReadLatestRun, CanPublish. These
                // are filtered by an explicit prefix list on the METHOD name, the same mechanism as
                // NoisyMethods and for the same reason: never by receiver.
                //
                // This is the one narrowing in the file, so it is bounded on purpose:
                // ReaderPrefixesNeverHideAnActingVerbPrefix asserts that no prefix here can swallow
                // a name that STARTS with an acting verb, so the de-anchoring cannot be quietly
                // undone by growing this list.
                if (ReaderPrefixes.Any(p => method.StartsWith(p, StringComparison.Ordinal))) continue;

                seen.Add(receiver + "." + method);
            }
            return seen;
        }
    }
}
