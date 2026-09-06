/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Round 4 of the server-mode RBAC lane: four doors, three of them a class the earlier rounds
    /// could not see, all driven live from a non-loopback origin against db5ec28 — unauthenticated,
    /// badge <c>viewer</c>, RBAC enforced.
    ///
    /// <list type="number">
    /// <item><b>/portal-status</b> served ~31 KB with no denial marker in both modes, and two of its
    ///   six handlers had no gate. <c>OpenLastPayload</c> was CLICKED from the LAN with a payload
    ///   planted on disk and created a process on the host (OpenWith, PID 27908). The lead confirmed
    ///   the same page on the LIVE service: <c>curl 127.0.0.1:5155/portal-status</c> → 32,410 bytes,
    ///   "View last payload", the outbox path and the intake/SAS state.</item>
    /// <item><b>/vulnerabilityassessment</b> had <c>IsAuthorized</c> count 0. "Run Assessment"
    ///   rendered ENABLED to a LAN viewer; clicking it produced a host stack trace through
    ///   <c>SqlAssessmentService.RunServerAssessmentAsync</c> → <c>SqlConnection.InternalOpenAsync</c>
    ///   with TCP established to <c>tcp:localhost,56510</c>. It died at the TLS pre-login handshake —
    ///   a fault on that box that day, NOT an authorization check.</item>
    /// <item><b>/governance</b> had <c>IsAuthorized</c> count 0. Clicked as a LAN viewer, the host
    ///   logged <c>QuickCheck starting on tcp:localhost,56510</c> and <c>QuickCheck finished</c>;
    ///   0 checks only because the sandbox had no licence bundle.</item>
    /// <item><b>ServerModeToggle</b> is rendered by the LAYOUT, so it appeared on every route —
    ///   including the AccessDenied pages themselves — and it starts and stops server mode.</item>
    /// </list>
    ///
    /// <para><b>The methodological point, and what these tests do differently.</b> Round 3's
    /// verifier probed all 89 routes with GETs and found one door; the gate found three more BY
    /// CLICKING THE BUTTONS. A GET tells you whether the page renders. Only driving the control
    /// tells you whether the mutation is reachable. So round 3's structural assertions are kept —
    /// the shipped markup must call a named gate with a named permission — and on top of them every
    /// newly gated surface has its HANDLER INVOKED, on a real denied circuit, and asserted to
    /// refuse.</para>
    ///
    /// <para><b>How the handler tests avoid being vacuous.</b> Each handler is invoked on a
    /// component whose OTHER injected services are deliberately left null. On a DENIED circuit the
    /// gate returns first, so nothing is dereferenced and the call completes quietly. On an ALLOWED
    /// circuit the very next statement reaches a null service and throws. Both halves are asserted:
    /// the allowed half is the POSITIVE CONTROL that proves the quiet return was the gate refusing
    /// and not an empty method body. A test that only asserted "denied does not throw" would pass
    /// against a handler someone had deleted.</para>
    /// </summary>
    public class RbacRound4RegressionTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _configPath;
        private readonly string _usersPath;

        public RbacRound4RegressionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "rbac-round4-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "rbac-config.json");
            _usersPath = Path.Combine(_tempDir, "rbac-users.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        // ══ 1 — the shipped markup declares the gate ═══════════════════════════════════════

        /// <summary>
        /// file → the permission its gate must name. Chosen from what the surface DOES, per round
        /// 3's rule. The two entries that are NOT from the round-4 brief are marked: they came out
        /// of the widened census scan in this round and were on the census's READ-ONLY list.
        /// </summary>
        public static TheoryData<string, string> GatedSurfaces => new()
        {
            // ── from the brief ──
            { "Pages/VulnerabilityAssessment.razor",        "run_scripts" },    // runs the assessment against connected instances
            { "Pages/Governance.razor",                     "execute_checks" }, // QuickCheckRunner.RunAsync against a connected instance
            { "Components/Shared/ServerModeToggle.razor",   "settings" },       // starts and stops the LAN listener
            // ── found by the widened scan in this round ──
            { "Pages/Benchmark.razor",                      "run_scripts" },    // BenchmarkService.RunBenchmarkAsync drives a workload at an instance
            { "Pages/PerformanceTrends.razor",              "execute_checks" }, // HistoricalPerf.RunRollupAsync recomputes the perf rollup
            { "Components/Shared/DynamicDashboard.razor",   "run_scripts" },    // action DDL + EXEC msdb.dbo.sp_start_job
            { "Components/Shared/QueryPlanModal.razor",     "run_scripts" },    // CREATE INDEX on the plan's instance
            { "Components/Shared/PanelEditorModal.razor",   "run_scripts" },    // runs the caller's own T-SQL
            { "Components/Shared/ConnectionDialog.razor",   "manage_servers" }, // writes a server into this install's catalogue
        };

        [Theory]
        [MemberData(nameof(GatedSurfaces))]
        public void EachSurfaceDeclaresItsGate(string file, string permission)
        {
            var text = ReadWithCodeBehind(file);

            Assert.True(
                text.Contains("UserState.IsAuthorized(\"" + permission + "\")", StringComparison.Ordinal),
                file + " must gate on UserState.IsAuthorized(\"" + permission + "\"). A comment "
                + "claiming a gate is not a gate; this asserts the shipped file.");

            Assert.False(
                text.Contains("IsAuthorizedWithBreakGlass(\"", StringComparison.Ordinal),
                file + " must NOT use IsAuthorizedWithBreakGlass. Plain IsAuthorized already grants "
                + "the loopback BOOTSTRAP hatch on an unconfigured install; break-glass additionally "
                + "opens the surface on loopback when RBAC is ENFORCED, and round 1 scoped that, in "
                + "writing, to Settings and Onboarding only.");
        }

        /// <summary>
        /// Every gated surface guards a HANDLER, not only the render. This is the structural half;
        /// <see cref="DeniedCircuit_TheHandlerItselfRefuses"/> below is the half that drives it.
        /// </summary>
        [Theory]
        [MemberData(nameof(GatedSurfaces))]
        public void EachSurfaceGuardsItsHandlersToo(string file, string permission)
        {
            var text = ReadWithCodeBehind(file);
            _ = permission;

            var declarations = System.Text.RegularExpressions.Regex.Matches(
                text, @"private bool (May[A-Za-z]+)\s*=>\s*UserState\.IsAuthorized\(");
            Assert.True(declarations.Count >= 1,
                file + " must declare at least one `private bool MayXxx => UserState.IsAuthorized(...)` "
                + "gate property so the handlers have something to guard on.");

            var guarded = declarations
                .Select(d => d.Groups[1].Value)
                .Sum(name => System.Text.RegularExpressions.Regex.Matches(
                    text, @"if \(!" + name + @"\)").Count);

            Assert.True(guarded >= 1,
                file + " declares a gate property but no handler guards on it. The markup `return` is "
                + "not enough — an unauthorized circuit renders no button and so registers no handler, "
                + "but a gate that lives only in markup is one refactor from none.");
        }

        /// <summary>
        /// /portal-status is now gated at the RENDER as well as the handlers, and the gate is above
        /// the page body — the same placement rule round 2 pinned for Onboarding, for the same
        /// reason: a gate inside the body leaves whatever the body already emitted.
        /// </summary>
        [Fact]
        public void PortalStatusIsGatedAtTheRenderNotOnlyAtTheHandlers()
        {
            var markup = ReadWithCodeBehind("Pages/Portal/PortalStatus.razor", codeBehind: false);

            var gateAt = markup.IndexOf("if (!MayConfigurePortal)", StringComparison.Ordinal);
            var bodyAt = markup.IndexOf("<div class=\"portal-status-page\"", StringComparison.Ordinal);

            Assert.True(gateAt >= 0,
                "Pages/Portal/PortalStatus.razor must carry a PAGE-level gate. Round 3 gated four of "
                + "its six handlers and left the render open as \"a status read\"; the render "
                + "discloses the outbox path, the client id, whether an intake credential exists and "
                + "the last-upload result, and it served ~31 KB to an unauthenticated LAN caller in "
                + "both dormant and enforced mode.");
            Assert.True(bodyAt > gateAt, "The gate must precede the page body.");
        }

        /// <summary>
        /// The two handlers on /portal-status that round 3 left ungated. Named individually because
        /// "the page is gated" is a different claim from "this call cannot be reached".
        /// </summary>
        [Theory]
        [InlineData("OpenLastPayload")]
        [InlineData("RetryEnrolment")]
        public void TheTwoUngatedPortalStatusHandlersAreGuarded(string handler)
        {
            var text = ReadWithCodeBehind("Pages/Portal/PortalStatus.razor");
            var at = text.IndexOf(handler + "()", StringComparison.Ordinal);
            Assert.True(at > 0, handler + " has gone from PortalStatus — if it was renamed, the guard moves with it.");

            var body = text[at..Math.Min(text.Length, at + 400)];
            Assert.Contains("if (!MayConfigurePortal) return;", body, StringComparison.Ordinal);
        }

        // ══ 2 — the gate, exercised on a real circuit ══════════════════════════════════════

        public static TheoryData<string> RoundFourPermissions => new()
        {
            "run_scripts", "settings", "execute_checks", "manage_servers", "export_data",
        };

        /// <summary>Unauthenticated, from 192.168.10.32, RBAC DORMANT — the bootstrap-hatch mode.</summary>
        [Theory]
        [MemberData(nameof(RoundFourPermissions))]
        public async Task DormantRbac_LanCircuitIsDenied(string permission)
        {
            var state = LanCircuit(NewRbac(new RbacConfig()));
            await state.InitAsync();

            Assert.False(state.IsLoopback);
            Assert.False(state.IsBootstrapEligible);
            Assert.False(state.IsAuthorized(permission));
        }

        /// <summary>Same circuit, RBAC ENFORCED.</summary>
        [Theory]
        [MemberData(nameof(RoundFourPermissions))]
        public async Task EnforcedRbac_LanCircuitIsDenied(string permission)
        {
            var rbac = EnforcedRbac();
            Assert.True(rbac.IsRbacEnforced(), "The fixture must actually enforce, or this proves nothing.");

            var state = LanCircuit(rbac);
            await state.InitAsync();

            Assert.False(state.IsAuthorized(permission));
        }

        /// <summary>
        /// The half that keeps the round honest: an unconfigured install reached over LOOPBACK still
        /// opens. A round that only proved denial could be passed by nailing every door shut.
        /// </summary>
        [Theory]
        [MemberData(nameof(RoundFourPermissions))]
        public async Task DormantRbac_LoopbackStillOpens(string permission)
        {
            var state = await LoopbackCircuitAsync(NewRbac(new RbacConfig()));
            Assert.True(state.IsAuthorized(permission));
        }

        // ══ 3 — DRIVING THE HANDLER, which is what a GET could never tell us ═══════════════

        /// <summary>
        /// component type → the private handler to invoke → the arguments it takes.
        ///
        /// <para>These are the actual methods the buttons are bound to. Nothing here asserts against
        /// markup: each one is INVOKED.</para>
        /// </summary>
        public static TheoryData<string, string, object?[]> DrivableHandlers
        {
            get
            {
                var data = new TheoryData<string, string, object?[]>
                {
                    { "SQLTriage.Pages.VulnerabilityAssessment", "RunAssessment",    Array.Empty<object?>() },
                    { "SQLTriage.Pages.VulnerabilityAssessment", "SaveVaSchedule",   Array.Empty<object?>() },
                    { "SQLTriage.Pages.Governance",              "RunQuickCheck",    Array.Empty<object?>() },
                    { "SQLTriage.Pages.Governance",              "RunFullAssessment", Array.Empty<object?>() },
                    { "SQLTriage.Pages.Governance",              "ExportPdf",        Array.Empty<object?>() },
                    { "SQLTriage.Pages.Benchmark",               "RunBenchmarkForServer", new object?[] { new ServerConnection { ServerNames = "tcp:localhost,56510" } } },
                    { "SQLTriage.Components.Shared.ServerModeToggle", "StartServer", Array.Empty<object?>() },
                    { "SQLTriage.Components.Shared.ServerModeToggle", "StopServer",  Array.Empty<object?>() },
                    { "SQLTriage.Components.Shared.ConnectionDialog", "Save",        Array.Empty<object?>() },
                    { "SQLTriage.Components.Shared.ConnectionDialog", "TestConnection", Array.Empty<object?>() },
                    { "SQLTriage.Components.Shared.PanelEditorModal", "TestQuery",   Array.Empty<object?>() },
                    { "SQLTriage.Components.Shared.QueryPlanModal",   "ExecuteSingleIndex", new object?[] { "CREATE INDEX ix_x ON dbo.t(c)" } },
                };

                // Pages\PerformanceTrends.razor is Content-Removed when the live-monitoring module
                // is off (buildprofile.targets), which is the shipped community state — so in a
                // community build the component TYPE does not exist and this case asserted about a
                // page that is not in the edition. 2026-08-04: it was two of the seven failures the
                // community suite produced the first time CI ever got far enough to run it.
                //
                // Gated on the app's OWN compile-time statement of what it contains, not on a copy
                // of the rule kept here: BuildModules.LiveMonitoring is the same const the nav uses
                // to decide whether to render the link. NewComponentWith still hard-fails on a
                // missing type, so a page that MOVED is as loud as it ever was — only a page this
                // edition genuinely does not ship is skipped.
                if (BuildModules.LiveMonitoring)
                    data.Add("SQLTriage.Pages.PerformanceTrends", "ForceRollupNow", Array.Empty<object?>());

                return data;
            }
        }

        /// <summary>
        /// THE ASSERTION THIS ROUND EXISTS FOR. The handler itself refuses on an unauthenticated
        /// LAN circuit, in BOTH dormant and enforced mode — not the markup, the method.
        /// </summary>
        [Theory]
        [MemberData(nameof(DrivableHandlers))]
        public async Task DeniedCircuit_TheHandlerItselfRefuses(string typeName, string handler, object?[] args)
        {
            foreach (var rbac in new[] { NewRbac(new RbacConfig()), EnforcedRbac() })
            {
                var state = LanCircuit(rbac);
                await state.InitAsync();
                Assert.False(state.IsLoopback);

                var component = NewComponentWith(typeName, state);
                var before = Snapshot(component);
                var thrown = await InvokeAsync(component, handler, args);
                var moved = Changed(before, Snapshot(component));

                Assert.True(thrown == null,
                    typeName + "." + handler + " did NOT refuse an unauthenticated LAN circuit — it ran on "
                    + "past the gate and reached a service (" + thrown?.GetType().Name + ": "
                    + thrown?.Message + "). Every other service on this component is null, so getting "
                    + "this far means the gate is not there or does not hold.");

                Assert.True(moved.Count == 0,
                    typeName + "." + handler + " changed the component's own state for a denied caller: "
                    + string.Join(", ", moved) + ". The gate must be the FIRST thing in the handler.");
            }
        }

        /// <summary>
        /// THE POSITIVE CONTROL. The same handlers, on a circuit that IS allowed, run past the gate
        /// and blow up on a null service. Without this the test above would pass against an empty
        /// method, a deleted handler or a typo in the name.
        /// </summary>
        [Theory]
        [MemberData(nameof(DrivableHandlers))]
        public async Task AllowedCircuit_TheHandlerProceedsPastTheGate(string typeName, string handler, object?[] args)
        {
            var state = await LoopbackCircuitAsync(NewRbac(new RbacConfig()));
            Assert.True(state.IsBootstrapEligible);

            var component = NewComponentWith(typeName, state);
            Prime(component);
            var before = Snapshot(component);
            var thrown = await InvokeAsync(component, handler, args);
            var moved = Changed(before, Snapshot(component));

            Assert.True(thrown != null || moved.Count > 0,
                typeName + "." + handler + " did nothing at all on an ALLOWED circuit — it neither "
                + "faulted on a null service nor changed any of the component's own state. That means "
                + "this test is not exercising the body: the handler may have been renamed, emptied, "
                + "or short-circuited by a precondition rather than by the gate, and the denial "
                + "assertion beside it would then be vacuous.");
        }

        /// <summary>
        /// DynamicDashboard's two acts return a result rather than void, so they are driven
        /// separately: the refusal is asserted on the VALUE the caller gets back, which is what the
        /// DataGrid action button actually shows.
        /// </summary>
        [Theory]
        [InlineData("ExecuteActionSqlAsync")]
        [InlineData("RunAgentJobAsync")]
        public async Task DeniedCircuit_DynamicDashboardActionsReturnRefusal(string handler)
        {
            var state = LanCircuit(EnforcedRbac());
            await state.InitAsync();

            var component = NewComponentWith("SQLTriage.Components.Shared.DynamicDashboard", state);
            var method = component.GetType().GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.True(method != null, "DynamicDashboard." + handler + " has gone — the gate must move with it.");

            var args = method!.GetParameters().Length == 3
                ? new object?[] { "CREATE INDEX ix ON dbo.t(c)", CancellationToken.None, null }
                : new object?[] { "SomeJob", CancellationToken.None };

            var task = (Task)method.Invoke(component, args)!;
            await task;
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            var success = (bool)result.GetType().GetField("Item1")!.GetValue(result)!;
            var message = (string)result.GetType().GetField("Item2")!.GetValue(result)!;

            Assert.False(success, handler + " reported success to an unauthenticated LAN caller.");
            Assert.Contains("Not permitted", message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Same two, allowed: they proceed past the gate and fault on the null ConnectionManager.
        /// The positive control for the pair above.
        /// </summary>
        [Theory]
        [InlineData("ExecuteActionSqlAsync")]
        [InlineData("RunAgentJobAsync")]
        public async Task AllowedCircuit_DynamicDashboardActionsProceed(string handler)
        {
            var state = await LoopbackCircuitAsync(NewRbac(new RbacConfig()));

            var component = NewComponentWith("SQLTriage.Components.Shared.DynamicDashboard", state);
            var method = component.GetType().GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!;
            var args = method.GetParameters().Length == 3
                ? new object?[] { "CREATE INDEX ix ON dbo.t(c)", CancellationToken.None, null }
                : new object?[] { "SomeJob", CancellationToken.None };

            Exception? thrown = null;
            try
            {
                var task = (Task)method.Invoke(component, args)!;
                await task;
            }
            catch (Exception ex) { thrown = ex; }

            Assert.True(thrown != null,
                handler + " completed quietly on an allowed circuit; the denial test beside it would "
                + "then prove nothing.");
        }

        // ══ 4 — the class the page census cannot see ═══════════════════════════════════════

        /// <summary>
        /// The component census exists and enumerates the class, rather than the one component that
        /// was found. If somebody deletes it, this fails loudly instead of the coverage quietly
        /// going away.
        /// </summary>
        [Fact]
        public void TheComponentCensusCoversTheWholeComponentTree()
        {
            var components = RbacComponentGateCensusTests.EnumerateComponents();

            Assert.True(components.Count > 30,
                "The component census enumerated only " + components.Count + " non-routable components. "
                + "It is meant to walk the whole of Pages/ and Components/; a number this small means "
                + "the walk is broken and the census is passing because it is looking at nothing.");

            Assert.Contains(components, c =>
                c.Path.Equals("Components/Shared/ServerModeToggle.razor", StringComparison.OrdinalIgnoreCase));

            // A routable page must NOT appear here — the two censuses partition the tree between
            // them, and an overlap would let a surface be "covered" by whichever one is looser.
            Assert.DoesNotContain(components, c => c.Path.Equals("Pages/Governance.razor", StringComparison.OrdinalIgnoreCase));
        }

        // ── Fixtures ─────────────────────────────────────────────────────

        private RbacService NewRbac(RbacConfig? config = null, params RbacUser[] users)
        {
            if (config != null) File.WriteAllText(_configPath, JsonSerializer.Serialize(config));
            if (users.Length > 0) File.WriteAllText(_usersPath, JsonSerializer.Serialize(users.ToList()));
            return new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);
        }

        /// <summary>Enforced RBAC with a usable local-password admin, so the lockout guard holds.</summary>
        private RbacService EnforcedRbac()
        {
            var config = new RbacConfig { Enabled = true };
            config.LocalPassword.Enabled = true;
            var admin = new RbacUser
            {
                Email = "admin@example.com",
                DisplayName = "admin",
                Provider = AuthProviders.Local,
                Role = AppRoles.Admin,
                Enabled = true,
                PasswordHash = RbacService.HashPassword("correct horse battery staple"),
            };
            return NewRbac(config, admin);
        }

        /// <summary>The circuit the gate used: unauthenticated, from 192.168.10.32.</summary>
        private static AppUserState LanCircuit(RbacService rbac)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.10.32");
            // The Host header is attacker-controlled and must not move the answer.
            ctx.Request.Host = new HostString("localhost", 5170);

            var services = new ServiceCollection();
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = ctx });

            return new AppUserState(
                HostEnvironmentInfo.BrowserHosted, rbac,
                services.BuildServiceProvider(), NullLogger<AppUserState>.Instance);
        }

        private static async Task<AppUserState> LoopbackCircuitAsync(RbacService rbac)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:127.0.0.1");

            var services = new ServiceCollection();
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = ctx });

            var state = new AppUserState(
                HostEnvironmentInfo.BrowserHosted, rbac,
                services.BuildServiceProvider(), NullLogger<AppUserState>.Instance);
            await state.InitAsync();
            return state;
        }

        // ── Driving a Blazor component without a renderer ────────────────

        /// <summary>
        /// Builds the component and injects ONLY <see cref="AppUserState"/>. Every other service
        /// stays null on purpose — that is what makes a handler that runs past its gate fault
        /// visibly instead of silently doing nothing.
        /// </summary>
        private static object NewComponentWith(string typeName, AppUserState state)
        {
            var type = typeof(AppUserState).Assembly.GetType(typeName);
            Assert.True(type != null,
                "No such component type: " + typeName + ". If the file moved, the test moves with it — "
                + "a handler test that silently stops finding its component is worse than none.");

            var component = Activator.CreateInstance(type!, nonPublic: true)!;

            var injected = type!.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(p => p.GetCustomAttributes(typeof(InjectAttribute), inherit: true).Any())
                .Where(p => p.PropertyType.IsAssignableFrom(typeof(AppUserState)))
                .ToList();

            Assert.True(injected.Count == 1,
                typeName + " must have exactly one injected AppUserState property for the gate to read; "
                + "found " + injected.Count + ".");

            injected[0].SetValue(component, state);
            return component;
        }

        /// <summary>
        /// Some handlers carry their own preconditions AFTER the gate — PanelEditorModal.TestQuery
        /// returns early when no panel is loaded, for instance. The positive control has to get past
        /// those to prove anything, so the component is primed to the state a real caller would have
        /// it in before the button is even visible. Priming is applied ONLY to the allowed half:
        /// the denial half must refuse a component in any state at all.
        /// </summary>
        private static void Prime(object component)
        {
            if (component.GetType().FullName == "SQLTriage.Components.Shared.PanelEditorModal")
            {
                var working = component.GetType().GetField("_working", BindingFlags.NonPublic | BindingFlags.Instance);
                working?.SetValue(component, new PanelDefinition
                {
                    Id = "test.panel",
                    Title = "test",
                    Query = { SqlServer = "SELECT 1" },
                });
            }
        }

        /// <summary>
        /// Every instance field DECLARED ON THE COMPONENT ITSELF — its own UI state. Inherited
        /// ComponentBase plumbing is excluded: it moves for reasons that have nothing to do with the
        /// handler.
        /// </summary>
        private static Dictionary<string, object?> Snapshot(object component)
        {
            var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in component.GetType().GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                try { snapshot[field.Name] = field.GetValue(component); }
                catch { /* a field we cannot read cannot be evidence either way */ }
            }
            return snapshot;
        }

        private static List<string> Changed(Dictionary<string, object?> before, Dictionary<string, object?> after)
        {
            var moved = new List<string>();
            foreach (var (name, oldValue) in before)
            {
                if (!after.TryGetValue(name, out var newValue)) continue;
                if (!Equals(oldValue, newValue)) moved.Add(name);
            }
            return moved;
        }

        /// <summary>
        /// Invokes a private handler and returns whatever it threw (null when it returned quietly).
        /// Handles both shapes: a sync method throws through <see cref="TargetInvocationException"/>,
        /// an async method faults its Task instead.
        /// </summary>
        private static async Task<Exception?> InvokeAsync(object component, string handler, object?[] args)
        {
            var method = component.GetType().GetMethod(
                handler, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);

            Assert.True(method != null,
                component.GetType().FullName + " has no handler called " + handler
                + ". If it was renamed, the gate and this test move with it.");

            try
            {
                var result = method!.Invoke(component, args);
                if (result is Task task) await task;
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static string ReadWithCodeBehind(string relativePath, bool codeBehind = true)
        {
            var full = Path.Combine(RawPassedScan.RepoRoot().FullName,
                                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), "Missing file: " + relativePath);

            var text = File.ReadAllText(full);
            if (codeBehind && File.Exists(full + ".cs")) text += File.ReadAllText(full + ".cs");
            return text;
        }
    }
}
