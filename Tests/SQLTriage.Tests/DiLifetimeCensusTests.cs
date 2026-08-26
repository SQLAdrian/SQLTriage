/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// A census of DI LIFETIMES against cross-circuit state — the class of defect the page census
    /// and the component census are both structurally blind to.
    ///
    /// <para>⚠ <b>STAYS, and stays load-bearing, after round 8 moved the boundary into the HTTP
    /// pipeline (2026-08-03).</b> <see cref="InteractiveAppAdmission"/> stops an ANONYMOUS caller
    /// from getting a circuit at all, which removes the 2026-08-02 attack in which an
    /// unauthenticated LAN caller pressed Ctrl+R on an Access Denied page. It does NOT remove this
    /// census's subject: a circuit that shares mutable state with another circuit is a defect
    /// between two SIGNED-IN callers as much as between a stranger and an operator, and a viewer
    /// who can raise an event in an admin's circuit is a privilege escalation whether or not they
    /// authenticated to get there. Round 5's scoped lifetimes are kept for that reason, not out of
    /// caution.</para>
    ///
    /// <para><b>Why this exists.</b> Four rounds of RBAC work each closed a set of authorization
    /// holes and were each defeated through a category the previous instrument could not see:
    /// call sites, then an ungated page, then an HTTP API, then a shared component rendered on the
    /// denial pages. Round 5 was defeated through the fifth: <b>cross-circuit state</b>.</para>
    ///
    /// <para><c>KeyboardShortcutService</c> was <c>AddSingleton</c>. Its <c>OnRunRequested</c> event
    /// therefore held subscriber delegates from EVERY live circuit, so <c>TriggerRun()</c> raised
    /// from one circuit invoked the Run handler of every other. Each of those handlers then checked
    /// <c>IsAuthorized("execute_checks")</c> — on the VICTIM's <c>AppUserState</c> — and passed.
    /// An unauthenticated LAN caller sitting on an Access Denied page pressed Ctrl+R and a
    /// privileged circuit executed its checks. Every gate rounds 1–4 added was correct, and every
    /// one of them was asked the wrong question. A confused deputy is not fixed by another gate.</para>
    ///
    /// <para><b>What the other two censuses could see, and could not.</b> The page census asks
    /// "does this file contain a gate" — MainLayout has no <c>@page</c>, so it is not even
    /// enumerated. The component census asks "does this component call an acting verb" — and its
    /// scanner was prefix-anchored, so <c>ShortcutSvc.TriggerRun</c> was invisible and the pinned
    /// call set matched the blind scan exactly. Both censuses were green with the defect live.
    /// Neither of them asks the question that actually mattered, which is a question about the
    /// SERVICE, not about the markup: <i>can this reach another circuit at all?</i></para>
    ///
    /// <para>That is the question below. It is asked of the registration, so it cannot be answered
    /// wrongly by a component that is written carefully — and it stays answered when somebody adds
    /// the next shared service.</para>
    /// </summary>
    public class DiLifetimeCensusTests
    {
        // ── Where lifetimes are declared ─────────────────────────────────

        /// <summary>
        /// Every file that registers services. Both hosts, because they are two containers in one
        /// process and drift between them is its own defect class (see
        /// <c>ServerModeService.RegisterSharedSingletons</c>' own comment).
        /// </summary>
        private static readonly string[] RegistrationFiles =
        {
            "Data/ServiceCollectionExtensions.cs",
            "Data/Services/WindowsServiceHost.cs",
            "Data/Services/ServerModeService.cs",
            "App.xaml.cs",
        };

        /// <summary>
        /// SINGLETONS THAT HOLD PROCESS-WIDE STATE ON PURPOSE, each with the reason.
        ///
        /// <para>This is a register, not an approval. Every entry is a service that
        /// <see cref="CrossCircuitStateMarkers"/> flags and that is nevertheless correct as a
        /// singleton, because the state it holds belongs to the ESTATE (one process manages one
        /// set of SQL Servers) rather than to a viewer. A service that is not here and carries the
        /// shape fails the build, which is the whole point: round 5's defect was invisible, not
        /// disputed.</para>
        ///
        /// <para>Judge an entry by what would break if it were scoped, not by whether sharing feels
        /// tidy. "Two operators would each see their own copy" is a REASON when the thing is a
        /// filter and a DEFECT when the thing is the estate's last scan.</para>
        /// </summary>
        private static readonly Dictionary<string, string> DeclaredProcessWideState =
            new(StringComparer.Ordinal)
            {
                ["QuickCheckStateService"] =
                    "The estate's last audit run: Results/ServerSummaries/IsRunning/progress. Read as "
                    + "the estate's by /compliance-map, /compliance-tree, /compliance-board and the "
                    + "NavMenu spinner, restored for everybody at cold start by "
                    + "AssessmentRehydrationService, and forwarded from the WPF container by "
                    + "RegisterSharedSingletons so the desktop window and the browser are one session. "
                    + "IsRunning is also the mutual-exclusion flag that stops two simultaneous estate "
                    + "scans against the same production servers. Its per-USER half (selection and "
                    + "filters) was split out to the scoped QuickCheckViewState on 2026-08-02.",

                ["VulnerabilityAssessmentStateService"] =
                    "The estate's last vulnerability assessment: Results/AssessmentSummary/"
                    + "AssessedServers/IsRunning. Read as the estate's by the singleton report "
                    + "composers ExecutiveHealthService and ReportBundleService. Its per-USER half "
                    + "(selection, filters, FilteredResults) was split out to the scoped "
                    + "VulnerabilityAssessmentViewState on 2026-08-02.",

                ["FullAuditStateService"] =
                    "The estate's last raw-diagnostics run: execution results and progress, shown by "
                    + "the NavMenu spinner and forwarded from the WPF container so the desktop and "
                    + "browser views agree. Its per-USER half (SelectedConnectionId/SelectedServer) "
                    + "was split out to the scoped FullAuditViewState on 2026-08-02.",

                ["GlobalInstanceSelector"] =
                    "RULED 2026-08-02 (round 6) — the LIFETIME stays, the CONTROLS are gated. "
                    + "SelectedInstance is the process-wide 'current SQL instance' and it is the "
                    + "target every dashboard queries, so one circuit changing it re-points the "
                    + "others. It stays a singleton because SqlServerConnectionFactory — itself a "
                    + "SINGLETON — takes it in its constructor: scoping it makes the factory a "
                    + "captive dependency and the container throws at runtime. Closing it by "
                    + "lifetime means threading the instance through the call instead of holding it "
                    + "in the container, which is a design change, not a registration change. What "
                    + "round 6 DID close is the reachable end: the two controls that set the current "
                    + "instance — DashboardToolbar's Instance dropdown and GlobalServerSelector, "
                    + "which MainLayout renders on every route — are gated on execute_checks. "
                    + "Whoever may run a check against the estate may choose the instance it runs "
                    + "against; whoever may run nothing may not retarget what somebody else runs.",

                ["ServerConnectionManager"] =
                    "RULED 2026-08-02 (round 6) — same shape, same constraint, same resolution as "
                    + "GlobalInstanceSelector above. _currentServerId is process-wide and "
                    + "SetCurrentServer raises OnConnectionChanged in every circuit; the manager is "
                    + "also the seat-guard choke point and is injected into SqlServerConnectionFactory "
                    + "and a dozen other singletons, so its lifetime is not movable on its own. The "
                    + "connection CATALOGUE genuinely is install-wide (it is the estate); the 'which "
                    + "one am I looking at' half is per-viewer and is now gated at the controls that "
                    + "write it, on execute_checks. The one remaining ungated write is the "
                    + "deterministic bootstrap in DashboardToolbar.OnInitialized — first enabled "
                    + "connection when nothing is selected, no caller input, same value for every "
                    + "caller — recorded as such in ShellSurfaceRegistry.",
            };

        /// <summary>
        /// Members whose presence means a type carries state that must not straddle circuits.
        ///
        /// <para><b>1. A COMMAND event — <c>event Func&lt;…&gt;</c>.</b> This is the exact shape of
        /// the defect. A handler behind an <c>Action</c> is NOTIFIED that something changed; a
        /// handler behind a <c>Func</c> is invoked and AWAITED because the raiser wants its work
        /// done. On a singleton, "its work" is another circuit's work, performed under another
        /// circuit's permissions. <c>KeyboardShortcutService.OnRunRequested</c> was
        /// <c>event Func&lt;Task&gt;</c> and is the only such event in the tree.</para>
        ///
        /// <para><b>2. A public delegate FIELD.</b> A slot rather than a subscription list: the
        /// last writer wins process-wide. <c>CommandPalette.RequestOpen</c> was this.</para>
        ///
        /// <para><b>3. <c>Selected…</c> state.</b> The TARGET of a privileged action. Sharing it
        /// lets one caller retarget another caller's run even when that caller can run nothing at
        /// all themselves.</para>
        ///
        /// <para><b>Why a plain <c>public event Action</c> is NOT on this list.</b> Twenty-six
        /// singletons declare one, and they are change notifications: NavMenu subscribes to
        /// <c>QuickCheckState.StateChanged</c> so its spinner redraws when the estate's scan
        /// progresses, which is the shared-estate model working as designed. Flagging all of them
        /// would bury the three shapes above in twenty-six entries nobody reads, which is how a
        /// guard gets switched off. The cross-circuit reach of those notifications is real and is
        /// not being hidden — it is the subject of
        /// <see cref="NothingTheAlwaysRenderedLayoutCallsRaisesEventsInOtherCircuits"/>, which asks
        /// the sharper question: is the call reachable from markup that renders on EVERY route,
        /// AccessDenied included.</para>
        /// </summary>
        private static readonly (string Name, Regex Pattern)[] CrossCircuitStateMarkers =
        {
            ("a COMMAND event (event Func<…>) — subscribers are invoked to do work, not notified",
             new Regex(@"^\s*public\s+event\s+Func\s*<", RegexOptions.Multiline | RegexOptions.Compiled)),

            ("a public delegate FIELD (Action/Func/EventHandler) — one slot, last writer wins process-wide",
             new Regex(@"^\s*public\s+(static\s+)?(Action|Func\s*<|EventHandler)[^\n]*\s(?!.*=>)[A-Za-z_][A-Za-z0-9_]*\s*(=|;)",
                       RegexOptions.Multiline | RegexOptions.Compiled)),

            ("per-user selection state (Selected…), which is the TARGET of a privileged action",
             new Regex(@"^\s*public\s+[^\s]+\??\s+Selected[A-Za-z]*\s*\{\s*get", RegexOptions.Multiline | RegexOptions.Compiled)),
        };

        // ── 1. No undeclared singleton holds cross-circuit state ─────────

        /// <summary>
        /// THE ONE THAT FAILS WHEN A NEW SHARED-STATE SERVICE IS REGISTERED AS A SINGLETON.
        ///
        /// <para>Proven able to fail: reverting <c>AddScoped&lt;KeyboardShortcutService&gt;()</c> to
        /// <c>AddSingleton</c> — the exact defect this round fixed — turns this red and names the
        /// service and the marker it carries.</para>
        /// </summary>
        [Fact]
        public void NoUndeclaredSingletonHoldsCrossCircuitState()
        {
            var root = RawPassedScan.RepoRoot();
            var offenders = new List<string>();

            foreach (var type in SingletonRegisteredTypes(root))
            {
                if (DeclaredProcessWideState.ContainsKey(type)) continue;

                var source = FindTypeSource(root, type);
                if (source == null) continue;   // third-party or generic type; nothing to read

                foreach (var (what, pattern) in CrossCircuitStateMarkers)
                {
                    if (!pattern.IsMatch(source.Text)) continue;
                    offenders.Add(type + "  (" + source.RelativePath + ")\n      carries " + what);
                    break;
                }
            }

            Assert.True(offenders.Count == 0,
                "These types are registered as SINGLETONS and carry state that reaches across "
                + "circuits. In a process serving more than one browser that is not a lifetime "
                + "choice, it is an authorization boundary: KeyboardShortcutService held every "
                + "circuit's Run handler on one event, so one caller's Ctrl+R ran checks under "
                + "another caller's permissions and every gate passed.\n\n"
                + "Register the service AddScoped, or split the per-circuit half out of it (see "
                + "Data/Services/CircuitViewState.cs), or — if the state genuinely belongs to the "
                + "ESTATE rather than to a viewer — add it to DeclaredProcessWideState with the "
                + "reason it is correct:\n  "
                + string.Join("\n  ", offenders));
        }

        // ── 2. The declared register stays honest ────────────────────────

        /// <summary>An allow-list nobody prunes becomes the whole app.</summary>
        [Fact]
        public void TheProcessWideRegisterHasNoStaleOrThinEntries()
        {
            var root = RawPassedScan.RepoRoot();
            var singletons = new HashSet<string>(SingletonRegisteredTypes(root), StringComparer.Ordinal);

            var stale = DeclaredProcessWideState.Keys.Where(k => !singletons.Contains(k)).ToList();
            Assert.True(stale.Count == 0,
                "These are on the process-wide register but are no longer registered as singletons. "
                + "Prune them, or the register stops describing the app:\n  " + string.Join("\n  ", stale));

            var thin = DeclaredProcessWideState
                .Where(kv => kv.Value.Trim().Length < 60
                             || kv.Value.Contains("TODO", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();
            Assert.True(thin.Count == 0,
                "These entries carry no real reason. A register whose reasons are decoration is the "
                + "failure mode round 2's page census had:\n  " + string.Join("\n  ", thin));
        }

        // ── 3. THE MISSING CATEGORY: the always-rendered layout ──────────

        /// <summary>
        /// THE CATEGORY NEITHER OTHER CENSUS CAN SEE.
        ///
        /// <para>Components under <c>Components/Layout/</c> wrap the <c>&lt;Router&gt;</c>. They
        /// render on every route in the app, INCLUDING the AccessDenied pages rounds 1–4 put in
        /// front of the gated routes — so a caller who was correctly denied a route is still
        /// executing this markup and its handlers. The page census cannot see them (no
        /// <c>@page</c>). The component census asks only whether they call an acting verb, and
        /// answers "these are the app's own UI chrome", which is true and beside the point.</para>
        ///
        /// <para>The question that matters is where the call LANDS. If an always-rendered layout
        /// invokes a service, and that service is process-wide and raises events, then a control on
        /// the denial page reaches into other people's circuits. That is not a markup property and
        /// no amount of reading the .razor file will reveal it: it is a property of the
        /// registration.</para>
        ///
        /// <para>The scan is an EDGE scan, not a file scan: for every
        /// <c>alias.Method(</c> the layout makes on an injected SINGLETON, it opens that service
        /// and asks whether <c>Method</c>'s body raises one of the service's events. If it does,
        /// the call lands in every other circuit. Neither reading the .razor nor reading the
        /// service alone answers that — which is why four rounds of reading .razor files did not
        /// find it.</para>
        ///
        /// <para>Proven able to fail: reverting KeyboardShortcutService to <c>AddSingleton</c>
        /// turns this red and names MainLayout → ShortcutSvc.TriggerRun.</para>
        /// </summary>
        [Fact]
        public void NothingTheAlwaysRenderedLayoutCallsRaisesEventsInOtherCircuits()
        {
            var root = RawPassedScan.RepoRoot();
            var layoutDir = new DirectoryInfo(Path.Combine(root.FullName, "Components", "Layout"));
            Assert.True(layoutDir.Exists,
                "Components/Layout must exist — the guard cannot scan and must fail rather than pass over nothing.");

            var singletons = new HashSet<string>(SingletonRegisteredTypes(root), StringComparer.Ordinal);
            var inject = new Regex(@"@inject\s+([A-Za-z0-9_.<>]+)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
            var edges = new List<string>();

            foreach (var file in layoutDir.EnumerateFiles("*.razor", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file.FullName);
                var codeBehind = file.FullName + ".cs";
                if (File.Exists(codeBehind)) text += File.ReadAllText(codeBehind);

                var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');

                foreach (Match inj in inject.Matches(text))
                {
                    var typeName = inj.Groups[1].Value.Split('.').Last();
                    var alias = inj.Groups[2].Value;

                    if (!singletons.Contains(typeName)) continue;

                    var source = FindTypeSource(root, typeName);
                    if (source == null) continue;

                    var events = EventNames(source.Text);
                    if (events.Count == 0) continue;

                    // Every method this layout CALLS on that singleton. A property read
                    // (NavMenu's QuickCheckState.IsRunning spinner) is not a call and not here.
                    var called = Regex.Matches(text, @"\b" + Regex.Escape(alias) + @"\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(")
                        .Select(m => m.Groups[1].Value)
                        .Distinct(StringComparer.Ordinal);

                    foreach (var method in called)
                    {
                        if (!MethodRaisesAnEvent(source.Text, method, events)) continue;

                        var edge = ShellSurfaceRegistry.Key(relative, typeName + "." + method);
                        if (ShellSurfaceRegistry.Edges.ContainsKey(edge)) continue;
                        edges.Add(edge + "   (" + source.RelativePath + "; raises one of: "
                                  + string.Join(", ", events) + ")");
                    }
                }
            }

            Assert.True(edges.Count == 0,
                "An always-rendered layout renders on EVERY route, AccessDenied included. These "
                + "calls it makes land in a SINGLETON method that raises that singleton's events — "
                + "so the handler that runs belongs to every other live circuit, and the permission "
                + "check that runs there is THEIRS. That is exactly how Ctrl+R on an Access Denied "
                + "page executed checks on 2026-08-02: every gate was correct and every gate was "
                + "asked the wrong question.\n\n"
                + "Register the service AddScoped so the call cannot leave this circuit, move the "
                + "call out of the always-rendered layout, or add the edge to "
                + "ShellSurfaceRegistry.Edges with what it actually does to the other circuits:\n  "
                + string.Join("\n  ", edges));
        }

        /// <summary>
        /// THE EDGE REGISTER THIS TEST READS IS NOT ITS OWN ANY MORE.
        ///
        /// <para>Until 2026-08-02 this file carried a private <c>ReviewedLayoutEdges</c> dictionary
        /// while <see cref="RbacComponentGateCensusTests"/> carried three more lists about the same
        /// components — and they contradicted each other inside one commit: DashboardToolbar was on
        /// the "makes no acting call" list at the same moment as
        /// <c>DashboardToolbar → ServerConnectionManager.SetCurrentServer</c> was on this one. Both
        /// censuses now read <see cref="ShellSurfaceRegistry.Edges"/>, which is a superset of what
        /// this test needs (it holds EVERY shell edge, not only the event-raising ones), so an edge
        /// this guard flags is either already reviewed there or it is new — and if it is new, the
        /// shell census has already failed on it too.</para>
        ///
        /// <para>This test pins that the coupling is real, so that deleting the shared register and
        /// quietly reintroducing a local one fails here rather than passing.</para>
        /// </summary>
        [Fact]
        public void ThisGuardReadsTheSharedShellRegister()
        {
            Assert.True(ShellSurfaceRegistry.Edges.Count > 50,
                "The shared shell register holds only " + ShellSurfaceRegistry.Edges.Count
                + " edges. This guard consults it to decide which layout→singleton edges are "
                + "reviewed; a register that small means it has been gutted and this test is "
                + "passing because it is comparing against nothing.");

            // The two edges that made the case for a shared register: one this guard would flag
            // (SetNotificationsEnabled raises an event), one it would not (SetCurrentServer does
            // not) — described in ONE place, so they cannot disagree.
            Assert.Contains(
                "Components/Layout/NavMenu.razor → UserSettingsService.SetNotificationsEnabled",
                ShellSurfaceRegistry.Edges.Keys);
            Assert.Contains(
                "Components/Layout/DashboardToolbar.razor → ServerConnectionManager.SetCurrentServer",
                ShellSurfaceRegistry.Edges.Keys);
        }

        // ── 4. A scoped service must not be resurrected as a singleton ───

        /// <summary>
        /// The way this fix gets silently undone.
        ///
        /// <para><c>ServerModeService.RegisterSharedSingletons</c> forwards services from the
        /// running WPF container into the browser container with
        /// <c>TryAdd&lt;T&gt;</c> — which resolves an INSTANCE and calls
        /// <c>services.AddSingleton(instance)</c>, inside a <c>try/catch</c> that only logs. So a
        /// scoped service left on that list comes back as a process-wide singleton in the container
        /// that actually serves browsers, with nothing louder than a debug line to say so.
        /// <c>KeyboardShortcutService</c> was on that list.</para>
        /// </summary>
        [Fact]
        public void NoScopedServiceIsForwardedAsASharedSingletonInstance()
        {
            var root = RawPassedScan.RepoRoot();
            var scoped = new HashSet<string>(
                RegisteredTypes(root, "Data/ServiceCollectionExtensions.cs", "AddScoped"),
                StringComparer.Ordinal);

            var serverMode = File.ReadAllText(Path.Combine(root.FullName, "Data", "Services", "ServerModeService.cs"));
            var code = string.Join("\n", serverMode.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            var forwarded = Regex.Matches(code, @"TryAdd<\s*([A-Za-z0-9_.]+)")
                .Select(m => m.Groups[1].Value.Split('.').Last())
                .ToList();

            var resurrected = forwarded.Where(scoped.Contains).Distinct(StringComparer.Ordinal).ToList();

            Assert.True(resurrected.Count == 0,
                "These services are registered AddScoped, and RegisterSharedSingletons forwards a "
                + "RESOLVED INSTANCE of them into the browser container as a singleton — which "
                + "re-creates the cross-circuit sharing the scoped lifetime exists to remove, in the "
                + "one container that serves browsers, and does it inside a try/catch that only "
                + "logs. Remove the TryAdd line; AddSharedServices already registers these:\n  "
                + string.Join("\n  ", resurrected));
        }

        // ── 5. No Blazor component holds process-wide delegate state ─────

        /// <summary>
        /// <c>public static Action? RequestOpen</c> on <c>CommandPalette</c> was process-wide by
        /// construction: one slot, owned by whichever circuit initialised last. A static
        /// <c>[JSInvokable]</c> is the same thing reached from the browser. Neither is a lifetime
        /// the container can fix, so this is a hard zero rather than a register.
        /// </summary>
        [Fact]
        public void NoComponentHoldsStaticDelegateOrStaticJsInvokableState()
        {
            var root = RawPassedScan.RepoRoot();
            var offenders = new List<string>();

            foreach (var dirName in new[] { "Components", "Pages" })
            {
                var dir = new DirectoryInfo(Path.Combine(root.FullName, dirName));
                if (!dir.Exists) continue;

                foreach (var file in dir.EnumerateFiles("*.razor", SearchOption.AllDirectories))
                {
                    var text = File.ReadAllText(file.FullName);
                    var codeBehind = file.FullName + ".cs";
                    if (File.Exists(codeBehind)) text += File.ReadAllText(codeBehind);

                    var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');

                    var code = string.Join("\n", text.Split('\n')
                        .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                        .Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));

                    foreach (Match m in Regex.Matches(code,
                                 @"^\s*(?:public|internal)\s+static\s+(?:Action|Func<|EventHandler)[^\n]*",
                                 RegexOptions.Multiline))
                    {
                        offenders.Add(relative + " → " + m.Value.Trim());
                    }

                    foreach (Match m in Regex.Matches(code,
                                 @"\[(?:Microsoft\.JSInterop\.)?JSInvokable[^\]]*\]\s*\n\s*public\s+static\s+[^\n]*",
                                 RegexOptions.Multiline))
                    {
                        offenders.Add(relative + " → static JSInvokable: " + m.Value.Replace("\n", " ").Trim());
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                "A static delegate or a static [JSInvokable] on a Blazor component is one slot for "
                + "the whole process: the last circuit to initialise owns it, and every other "
                + "circuit's keystroke lands there. CommandPalette.RequestOpen was exactly this. "
                + "Hold the delegate on a SCOPED service, and reach a component from JS through a "
                + "per-page DotNetObjectReference:\n  "
                + string.Join("\n  ", offenders));
        }

        // ── 6. The lifetimes, resolved for real ──────────────────────────

        /// <summary>
        /// The source scans above read text. This one builds the REAL container from
        /// <see cref="ServiceCollectionExtensions.AddSharedServices"/> and resolves through it,
        /// because a lifetime change is a runtime property: a singleton that captures a scoped
        /// service is a captive dependency that throws when the app runs, not when it compiles.
        ///
        /// <para>Three claims, each the thing that would actually break:</para>
        /// <list type="number">
        /// <item><c>ValidateScopes = true</c> + <c>ValidateOnBuild = true</c> — no singleton in the
        ///   whole shared graph captures any of the newly scoped services.</item>
        /// <item>Two scopes get two <c>KeyboardShortcutService</c> instances, so a Ctrl+R raised in
        ///   one circuit CANNOT reach a handler subscribed in another. This is the fix, asserted
        ///   directly rather than inferred from the registration line.</item>
        /// <item>One scope keeps getting the same instance, so MainLayout's trigger and the page's
        ///   subscription still meet — a "fix" that broke that would break the shortcut.</item>
        /// </list>
        /// </summary>
        [Fact]
        public void SharedContainerBuildsWithScopeValidation_AndTheShortcutBusIsPerScope()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();
            services.AddSingleton<IConfiguration>(configuration);

            // Supplied by the Blazor host in production (Radzen.DialogService and
            // WelcomeTourService both take it). Stubbed here so ValidateOnBuild can walk the whole
            // graph instead of stopping at the first framework service this container lacks.
            services.AddScoped<Microsoft.AspNetCore.Components.NavigationManager, StubNavigationManager>();
            services.AddScoped<Microsoft.JSInterop.IJSRuntime, StubJsRuntime>();

            services.AddSharedServices(configuration);

            // ── Keep the REAL user profile out of this graph (2026-08-04) ──────────────────
            //
            // AddSharedServices registers UserSettingsService and InstallProvenanceService, and
            // BOTH default to the operator's real %APPDATA%\SQLTriage. Resolving the graph below
            // therefore constructed a UserSettingsService bound to the developer's actual
            // user-settings.json — its licence and all — on every full-suite run.
            //
            // This is the same defect as the fourteen `new UserSettingsService()` call sites fixed
            // that day, but in a shape no grep for that expression can find: nothing here names the
            // type at all, the container builds it transitively. It was found only because
            // UserSettingsService's constructor now REFUSES the default path under a test host
            // (RealUserProfileGuard) — which is the argument for putting the boundary at a runtime
            // chokepoint rather than in a source scan.
            //
            // Re-registered AFTER AddSharedServices because the last registration wins in MS.DI.
            // Overriding the concrete UserSettingsService covers IUserSettingsService too: that
            // registration is a factory delegating to this one.
            var profileDir = Path.Combine(Path.GetTempPath(), "sqlt-di-census-" + Guid.NewGuid().ToString("N"));
            services.AddSingleton(new UserSettingsService(Path.Combine(profileDir, "user-settings.json")));
            services.AddSingleton(new InstallProvenanceService(profileDir));

            try
            {

            // ValidateScopes is the assertion: it makes resolving a scoped service from the ROOT
            // provider throw, which is exactly how a captive dependency announces itself.
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            using var circuitA = provider.CreateScope();
            using var circuitB = provider.CreateScope();

            var busA = circuitA.ServiceProvider.GetRequiredService<KeyboardShortcutService>();
            var busB = circuitB.ServiceProvider.GetRequiredService<KeyboardShortcutService>();

            Assert.NotSame(busA, busB);

            // The handler-reach proof: subscribe in circuit B, raise in circuit A, and B must not
            // have run. As a singleton this assertion failed — and that failure was an
            // unauthenticated caller executing a privileged circuit's Run handler.
            var circuitBRan = false;
            busB.OnRunRequested += () => { circuitBRan = true; return Task.CompletedTask; };
            busA.TriggerRun().GetAwaiter().GetResult();
            Assert.False(circuitBRan,
                "A Ctrl+R raised in one circuit reached a handler subscribed in another. That is the "
                + "confused deputy round 5 closed: the handler evaluates ITS OWN user's permissions "
                + "and passes while somebody else pressed the key.");

            // …and within one circuit the trigger still reaches its own subscriber.
            var sameCircuitRan = false;
            busA.OnRunRequested += () => { sameCircuitRan = true; return Task.CompletedTask; };
            busA.TriggerRun().GetAwaiter().GetResult();
            Assert.True(sameCircuitRan, "The shortcut must still work inside its own circuit.");

            // Same for the page view states split out of the three singleton state services.
            Assert.NotSame(circuitA.ServiceProvider.GetRequiredService<QuickCheckViewState>(),
                           circuitB.ServiceProvider.GetRequiredService<QuickCheckViewState>());
            Assert.NotSame(circuitA.ServiceProvider.GetRequiredService<VulnerabilityAssessmentViewState>(),
                           circuitB.ServiceProvider.GetRequiredService<VulnerabilityAssessmentViewState>());
            Assert.NotSame(circuitA.ServiceProvider.GetRequiredService<FullAuditViewState>(),
                           circuitB.ServiceProvider.GetRequiredService<FullAuditViewState>());

            // …while the ESTATE state is deliberately still one object for the whole process.
            Assert.Same(circuitA.ServiceProvider.GetRequiredService<QuickCheckStateService>(),
                        circuitB.ServiceProvider.GetRequiredService<QuickCheckStateService>());

            }
            finally
            {
                try { if (Directory.Exists(profileDir)) Directory.Delete(profileDir, recursive: true); }
                catch { /* best-effort */ }
            }
        }

        /// <summary>Stands in for the host's NavigationManager; navigates nowhere.</summary>
        private sealed class StubNavigationManager : Microsoft.AspNetCore.Components.NavigationManager
        {
            public StubNavigationManager() => Initialize("http://localhost/", "http://localhost/");
            protected override void NavigateToCore(string uri, bool forceLoad) { }
        }

        /// <summary>Stands in for the host's IJSRuntime; calls nothing.</summary>
        private sealed class StubJsRuntime : Microsoft.JSInterop.IJSRuntime
        {
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, System.Threading.CancellationToken cancellationToken, object?[]? args) => default;
        }

        // ── Scanner ──────────────────────────────────────────────────────

        private sealed record TypeSource(string RelativePath, string Text);

        /// <summary>The names of every event a type declares.</summary>
        private static List<string> EventNames(string source) =>
            Regex.Matches(source, @"^\s*public\s+event\s+[A-Za-z0-9_.<>,\s\?]+?\s+([A-Za-z_][A-Za-z0-9_]*)\s*;",
                          RegexOptions.Multiline)
                 .Select(m => m.Groups[1].Value)
                 .Distinct(StringComparer.Ordinal)
                 .ToList();

        /// <summary>
        /// Does <paramref name="method"/>'s body raise one of <paramref name="events"/>?
        ///
        /// <para>Brace-matched from the signature so the answer is about THAT method and not about
        /// the file. A method that raises an event is a method whose effect leaves this circuit;
        /// one that does not (UserSettings.SetEnableAnimations writes a field and saves) stays
        /// here, and the difference is the whole question.</para>
        /// </summary>
        private static bool MethodRaisesAnEvent(string source, string method, List<string> events)
        {
            foreach (Match sig in Regex.Matches(source,
                         @"^\s*(?:public|internal|private|protected)[^\n;=]*\b" + Regex.Escape(method) + @"\s*\([^)]*\)\s*",
                         RegexOptions.Multiline))
            {
                var open = source.IndexOf('{', sig.Index + sig.Length - 1);
                if (open < 0) continue;

                // An expression-bodied member (`=> Foo?.Invoke()`) has no brace: take the line.
                var arrow = source.IndexOf("=>", sig.Index, StringComparison.Ordinal);
                if (arrow >= 0 && arrow < open)
                {
                    var eol = source.IndexOf('\n', arrow);
                    var line = source.Substring(arrow, (eol < 0 ? source.Length : eol) - arrow);
                    if (events.Any(e => line.Contains(e, StringComparison.Ordinal))) return true;
                    continue;
                }

                var depth = 0;
                var i = open;
                for (; i < source.Length; i++)
                {
                    if (source[i] == '{') depth++;
                    else if (source[i] == '}' && --depth == 0) { i++; break; }
                }

                var body = source.Substring(open, Math.Min(i, source.Length) - open);
                if (events.Any(e => Regex.IsMatch(body, @"\b" + Regex.Escape(e) + @"\s*(\?\s*)?\.\s*Invoke\b")))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Type names registered with <c>AddSingleton</c> anywhere in <see cref="RegistrationFiles"/>.
        /// Takes the FIRST generic argument, which is the service type for both
        /// <c>AddSingleton&lt;T&gt;()</c> and <c>AddSingleton&lt;TService, TImpl&gt;()</c>.
        /// </summary>
        private static IEnumerable<string> SingletonRegisteredTypes(DirectoryInfo root) =>
            RegistrationFiles
                .SelectMany(f => RegisteredTypes(root, f, "AddSingleton"))
                .Distinct(StringComparer.Ordinal);

        private static List<string> RegisteredTypes(DirectoryInfo root, string relativeFile, string method)
        {
            var path = Path.Combine(root.FullName, relativeFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return new List<string>();

            var code = string.Join("\n", File.ReadAllLines(path)
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            return Regex.Matches(code, Regex.Escape(method) + @"<\s*([A-Za-z0-9_.]+)")
                .Select(m => m.Groups[1].Value.Split('.').Last())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        // One pass over the source tree, shared by every test in this class.
        private static readonly Lazy<Dictionary<string, TypeSource>> TypeIndex = new(() =>
        {
            var root = RawPassedScan.RepoRoot();
            var index = new Dictionary<string, TypeSource>(StringComparer.Ordinal);

            foreach (var dirName in new[] { "Data", "Components", "Services" })
            {
                var dir = new DirectoryInfo(Path.Combine(root.FullName, dirName));
                if (!dir.Exists) continue;

                foreach (var file in dir.EnumerateFiles("*.cs", SearchOption.AllDirectories))
                {
                    if (file.FullName.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                    if (file.FullName.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;

                    var text = File.ReadAllText(file.FullName);
                    var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');

                    foreach (Match m in Regex.Matches(text,
                                 @"^\s*(?:public|internal)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*class\s+([A-Za-z0-9_]+)",
                                 RegexOptions.Multiline))
                    {
                        var name = m.Groups[1].Value;
                        if (!index.ContainsKey(name)) index[name] = new TypeSource(relative, text);
                    }
                }
            }
            return index;
        });

        private static TypeSource? FindTypeSource(DirectoryInfo root, string typeName) =>
            TypeIndex.Value.TryGetValue(typeName, out var s) ? s : null;
    }
}
