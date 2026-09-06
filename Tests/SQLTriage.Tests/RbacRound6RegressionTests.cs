/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Round 6 of the server-mode RBAC lane: the ALWAYS-RENDERED SHELL.
    ///
    /// <para>Five rounds each closed a category and were each defeated through the next one: call
    /// sites, an ungated page, an HTTP API, a shared component on the denial pages, cross-circuit
    /// state. Round 6's category is the shell itself — <c>MainLayout</c>, <c>NavMenu</c>,
    /// <c>StatusBar</c>, <c>DashboardToolbar</c>, <c>OnboardingWizard</c>,
    /// <c>GlobalServerSelector</c> — markup that renders on EVERY route, including the AccessDenied
    /// pages the earlier rounds put in front of the gated routes.</para>
    ///
    /// <para><b>Measured on the shipped build at 5ffc274, no edits.</b> From
    /// <c>http://192.10.10.32:5182/scheduled-tasks</c>, a page reading "Scheduled tasks are
    /// restricted to Admin users", one click on the NavMenu Experimental toggle wrote
    /// <c>%APPDATA%\SQLTriage\user-settings.json</c>: <c>ExperimentalMode</c> True→False, 2680→2681
    /// bytes, md5 changed. The gate's own scan found 44 layout→singleton call edges of which 4 were
    /// on any register.</para>
    ///
    /// <para><b>The fix is at the RENDER, and it is not another gate on another call.</b> A denied
    /// caller sees the shell, not its switches: the four NavMenu preference toggles, the StatusBar
    /// animations toggle, the DashboardToolbar refresh/time-range selects, its PDF button, both
    /// instance pickers and the three first-run overlays are absent from the markup that caller
    /// receives. The handlers refuse as well — round 4 proved that a control which is "not
    /// rendered" is still reachable if the method has no guard.</para>
    ///
    /// <para>The instrument change lives in <see cref="RbacShellSurfaceCensusTests"/>; this file is
    /// the behavioural half: the shipped markup carries the gate, and the method itself refuses on
    /// a real denied circuit, with a positive control on every case.</para>
    /// </summary>
    public class RbacRound6RegressionTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _configPath;
        private readonly string _usersPath;

        public RbacRound6RegressionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "rbac-round6-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "rbac-config.json");
            _usersPath = Path.Combine(_tempDir, "rbac-users.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
        }

        // ══ 1 — the control is ABSENT from the markup a denied caller receives ═════════════

        /// <summary>
        /// file → the PERMISSION its boundary must name → a fragment of the control's own markup
        /// that must sit inside that boundary. Asserting the fragment (rather than only that a
        /// gate exists somewhere) is what makes this about the control and not about the file.
        ///
        /// <para>ROUND 7 changed the middle column from a <c>May…</c> property name to a
        /// permission. The controls and the fragments are unchanged: what moved is the MECHANISM
        /// they must be behind. An <c>@if (MayChangeInstallSettings)</c> was a code fact, and the
        /// census that read code facts was defeated on 2026-08-02 by one line —
        /// <c>var s = UserSettings;</c> — with a green suite and a working exploit. A
        /// <c>&lt;ShellGate&gt;</c> region is a markup fact, and the handler cannot argue with
        /// where it is written.</para>
        /// </summary>
        public static TheoryData<string, string, string> GatedShellControls => new()
        {
            // NavMenu's four install-wide preference switches — the Experimental one is the
            // control the cold gate drove from the LAN denial page.
            { "Components/Layout/NavMenu.razor", "settings", "ValueChanged=\"ToggleExperimentalMode\"" },
            { "Components/Layout/NavMenu.razor", "settings", "ValueChanged=\"ToggleNotifications\"" },
            { "Components/Layout/NavMenu.razor", "settings", "ValueChanged=\"ToggleColorBlindMode\"" },
            { "Components/Layout/NavMenu.razor", "settings", "ValueChanged=\"ToggleNarrationMode\"" },

            // StatusBar's animations switch.
            { "Components/Layout/StatusBar.razor", "settings", "@onclick=\"ToggleAnimationsAsync\"" },

            // DashboardToolbar: two install-setting selects, the export button, the instance picker.
            { "Components/Layout/DashboardToolbar.razor", "settings", "@onchange=\"OnRefreshIntervalChanged\"" },
            { "Components/Layout/DashboardToolbar.razor", "settings", "@onchange=\"OnTimeRangeChanged\"" },
            { "Components/Layout/DashboardToolbar.razor", "export_data", "@onclick=\"OpenPdfModal\"" },
            { "Components/Layout/DashboardToolbar.razor", "execute_checks", "@onchange=\"OnServerConnectionChanged\"" },

            // The top-bar server picker MainLayout renders on every route.
            { "Components/Shared/GlobalServerSelector.razor", "execute_checks", "@onchange=\"OnServerChanged\"" },

            // MainLayout's three first-run overlays. Each dismisses itself by writing an
            // install-wide persisted flag, so a handler-only gate would have left a modal a denied
            // caller can see and can never close.
            { "Components/Layout/MainLayout.razor", "settings", "<OnboardingWizard" },
            { "Components/Layout/MainLayout.razor", "settings", "<ReleaseNotesModal" },
            { "Components/Layout/MainLayout.razor", "settings", "@onclick=\"StartWelcomeTour\"" },
        };

        [Theory]
        [MemberData(nameof(GatedShellControls))]
        public void EachGatedShellControlRendersOnlyInsideItsGate(string file, string permission, string controlMarkup)
        {
            var markup = ReadMarkup(file);

            var controlAt = markup.IndexOf(controlMarkup, StringComparison.Ordinal);
            Assert.True(controlAt > 0,
                file + " no longer contains " + controlMarkup + ". If the control was renamed the "
                + "gate moves with it; if it was removed, remove this case.");

            // Which <ShellGate> region — if any — the control's markup falls inside. Computed from
            // the boundary spans rather than from a regex over the condition, because "is this
            // offset inside that region" is the whole question and it is answerable exactly.
            var regions = ShellBoundaryScan.BoundaryRegions(ShellBoundaryScan.Blank(markup));
            var enclosing = regions.Where(r => controlAt >= r.Start && controlAt < r.End).ToList();

            Assert.True(enclosing.Count > 0,
                file + " must render " + controlMarkup + " inside a <ShellGate Permission=\""
                + permission + "\">. A denied caller should see the shell, not its switches — and "
                + "a gate that lives only in the handler still hands them the control.");

            Assert.True(enclosing.Any(r => r.Permission == permission),
                file + " renders " + controlMarkup + " inside a boundary, but one that names \""
                + string.Join("\"/\"", enclosing.Select(r => r.Permission)) + "\" rather than \""
                + permission + "\". The permission is chosen by WHAT THE CONTROL WRITES; changing "
                + "it is a re-ruling, not a refactor.");
        }

        /// <summary>
        /// The wizard is suppressed on /servers as well — the second UI defect the operator drove
        /// on 2026-08-02: the Add Server dialog that page opens with no server configured, and this
        /// wizard, rendered at once, text overlapping and unreadable.
        /// </summary>
        [Fact]
        public void TheOnboardingWizardIsSuppressedOnTheServerManagementPage()
        {
            var markup = ReadMarkup("Components/Layout/MainLayout.razor");

            Assert.Contains("!OnServerManagementPage", markup, StringComparison.Ordinal);
            Assert.Matches(@"OnServerManagementPage\s*=>[^;]*StartsWith\(""servers""", markup);
            Assert.Contains("Navigation.LocationChanged += OnLocationChanged", markup, StringComparison.Ordinal);
            Assert.Contains("Navigation.LocationChanged -= OnLocationChanged", markup, StringComparison.Ordinal);
        }

        /// <summary>
        /// <c>Components/Shared/OnboardingWizard.razor</c> had ZERO occurrences of
        /// <c>IsAuthorized</c>, <c>UserState</c> or <c>RbacGuard</c> and was rendered from
        /// <c>MainLayout.razor</c>, so it appeared over every page in the app. Round 2 gated
        /// <c>Pages/Onboarding.razor</c>; nobody gated the layout component of the same wizard.
        /// </summary>
        [Fact]
        public void TheLayoutRenderedOnboardingWizardIsGatedAtItsOwnRoot()
        {
            var markup = ReadMarkup("Components/Shared/OnboardingWizard.razor");

            Assert.Contains("UserState.IsAuthorized(\"settings\")", markup, StringComparison.Ordinal);

            // The boundary has to wrap the wizard's OUTERMOST markup — its backdrop included — or
            // the wizard still paints a full-screen modal-backdrop over the app for a denied
            // caller, with nothing inside it to close.
            var backdropAt = markup.IndexOf("<div class=\"modal-backdrop\"", StringComparison.Ordinal);
            Assert.True(backdropAt > 0, "The wizard no longer renders its own backdrop.");

            var enclosing = ShellBoundaryScan
                .BoundaryRegions(ShellBoundaryScan.Blank(markup))
                .Where(r => backdropAt >= r.Start && backdropAt < r.End)
                .ToList();

            Assert.True(enclosing.Any(r => r.Permission == "settings"),
                "The boundary must wrap the wizard's outermost markup; wrapping an inner branch "
                + "leaves the backdrop and the header on screen.");

            // Round 1 scoped break-glass to Settings and Onboarding, and this component renders on
            // every route — which is not that scope.
            Assert.DoesNotContain("IsAuthorizedWithBreakGlass", markup, StringComparison.Ordinal);
        }

        // ══ 2 — the gate, on a real circuit ═══════════════════════════════════════════════

        public static TheoryData<string> RoundSixPermissions => new()
        {
            "settings", "execute_checks", "export_data",
        };

        [Theory]
        [MemberData(nameof(RoundSixPermissions))]
        public async Task DormantRbac_LanCircuitIsDenied(string permission)
        {
            var state = LanCircuit(NewRbac(new RbacConfig()));
            await state.InitAsync();

            Assert.False(state.IsLoopback);
            Assert.False(state.IsBootstrapEligible);
            Assert.False(state.IsAuthorized(permission));
        }

        [Theory]
        [MemberData(nameof(RoundSixPermissions))]
        public async Task EnforcedRbac_LanCircuitIsDenied(string permission)
        {
            var rbac = EnforcedRbac();
            Assert.True(rbac.IsRbacEnforced(), "The fixture must actually enforce, or this proves nothing.");

            var state = LanCircuit(rbac);
            await state.InitAsync();

            Assert.False(state.IsAuthorized(permission));
        }

        /// <summary>
        /// The half that keeps the round honest: an unconfigured install reached over LOOPBACK
        /// still opens, so the first-run wizard a real operator needs is unaffected.
        /// </summary>
        [Theory]
        [MemberData(nameof(RoundSixPermissions))]
        public async Task DormantRbac_LoopbackStillOpens(string permission)
        {
            var state = await LoopbackCircuitAsync(NewRbac(new RbacConfig()));
            Assert.True(state.IsAuthorized(permission));
        }

        // ══ 3 — DRIVING THE SHELL HANDLERS ════════════════════════════════════════════════

        /// <summary>
        /// component type → private handler → arguments. These are the methods the shell's controls
        /// are bound to. Nothing here reads markup: each one is INVOKED.
        /// </summary>
        public static TheoryData<string, string, object?[]> DrivableShellHandlers => new()
        {
            { "SQLTriage.Components.Layout.MainLayout", "OnOnboardingComplete",  Array.Empty<object?>() },
            { "SQLTriage.Components.Layout.MainLayout", "StartWelcomeTour",      Array.Empty<object?>() },
            { "SQLTriage.Components.Layout.MainLayout", "DismissWelcomeTourCta", Array.Empty<object?>() },
            { "SQLTriage.Components.Layout.MainLayout", "OnReleaseNotesClosed",  new object?[] { true } },

            { "SQLTriage.Components.Layout.NavMenu", "ToggleExperimentalMode", new object?[] { true } },
            { "SQLTriage.Components.Layout.NavMenu", "ToggleColorBlindMode",   new object?[] { true } },
            { "SQLTriage.Components.Layout.NavMenu", "ToggleNarrationMode",    new object?[] { true } },
            { "SQLTriage.Components.Layout.NavMenu", "ToggleNotifications",    new object?[] { true } },

            { "SQLTriage.Components.Layout.StatusBar", "ToggleAnimationsAsync", Array.Empty<object?>() },

            { "SQLTriage.Components.Layout.DashboardToolbar", "OnRefreshIntervalChanged", new object?[] { new ChangeEventArgs { Value = "5" } } },
            { "SQLTriage.Components.Layout.DashboardToolbar", "OnTimeRangeChanged",       new object?[] { new ChangeEventArgs { Value = "1440" } } },
            { "SQLTriage.Components.Layout.DashboardToolbar", "OpenPdfModal",             Array.Empty<object?>() },
            { "SQLTriage.Components.Layout.DashboardToolbar", "OnPdfConfirmed",           new object?[] { new PdfExportSettings { FileName = "x" } } },
            { "SQLTriage.Components.Layout.DashboardToolbar", "OnServerConnectionChanged", new object?[] { new ChangeEventArgs { Value = "srv-1" } } },

            { "SQLTriage.Components.Shared.GlobalServerSelector", "OnServerChanged", new object?[] { new ChangeEventArgs { Value = "srv-1" } } },

            { "SQLTriage.Components.Shared.OnboardingWizard", "Next",         Array.Empty<object?>() },
            { "SQLTriage.Components.Shared.OnboardingWizard", "GoToServers",  Array.Empty<object?>() },
            { "SQLTriage.Components.Shared.OnboardingWizard", "GoToQuickCheck", Array.Empty<object?>() },
            { "SQLTriage.Components.Shared.OnboardingWizard", "GoToRoadmap",  Array.Empty<object?>() },
        };

        /// <summary>
        /// THE ASSERTION THIS ROUND EXISTS FOR: the METHOD refuses an unauthenticated LAN circuit,
        /// in both dormant and enforced mode. Every other service on the component is null, so a
        /// handler that runs past its gate faults visibly instead of doing nothing.
        /// </summary>
        [Theory]
        [MemberData(nameof(DrivableShellHandlers))]
        public async Task DeniedCircuit_TheShellHandlerItselfRefuses(string typeName, string handler, object?[] args)
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
                    typeName + "." + handler + " did NOT refuse an unauthenticated LAN circuit — it "
                    + "ran past the gate and reached a service (" + thrown?.GetType().Name + ": "
                    + thrown?.Message + ").");

                Assert.True(moved.Count == 0,
                    typeName + "." + handler + " changed the component's own state for a denied "
                    + "caller: " + string.Join(", ", moved) + ". The gate must be the FIRST thing "
                    + "in the handler.");
            }
        }

        /// <summary>
        /// THE POSITIVE CONTROL. Without it the test above would pass against an emptied method, a
        /// deleted handler or a typo in the name.
        /// </summary>
        [Theory]
        [MemberData(nameof(DrivableShellHandlers))]
        public async Task AllowedCircuit_TheShellHandlerProceedsPastTheGate(string typeName, string handler, object?[] args)
        {
            var state = await LoopbackCircuitAsync(NewRbac(new RbacConfig()));
            Assert.True(state.IsBootstrapEligible);

            var component = NewComponentWith(typeName, state);
            var before = Snapshot(component);
            var thrown = await InvokeAsync(component, handler, args);
            var moved = Changed(before, Snapshot(component));

            Assert.True(thrown != null || moved.Count > 0,
                typeName + "." + handler + " did nothing at all on an ALLOWED circuit — it neither "
                + "faulted on a null service nor changed any of the component's own state, so the "
                + "denial assertion beside it proves nothing.");
        }

        /// <summary>
        /// The handlers that are guarded but cannot be driven to a visible effect with null
        /// services (they only raise an empty EventCallback, or move a bounded counter that is
        /// already at its bound). Asserted structurally instead of left out — leaving them out is
        /// how a guard goes missing quietly.
        /// </summary>
        [Theory]
        [InlineData("Components/Shared/OnboardingWizard.razor", "MayRunOnboarding", "Dismiss")]
        [InlineData("Components/Shared/OnboardingWizard.razor", "MayRunOnboarding", "Finish")]
        [InlineData("Components/Shared/OnboardingWizard.razor", "MayRunOnboarding", "Back")]
        public void EachRemainingShellHandlerCarriesItsGuard(string file, string gate, string handler)
        {
            var text = ReadMarkup(file);
            var at = text.IndexOf(handler + "()", StringComparison.Ordinal);
            Assert.True(at > 0, handler + " has gone from " + file + " — the guard moves with it.");

            var body = text[at..Math.Min(text.Length, at + 300)];
            Assert.Contains("if (!" + gate + ") return;", body, StringComparison.Ordinal);
        }

        // ══ 4 — the dead write that was hiding on the shell ═══════════════════════════════

        /// <summary>
        /// <c>DashboardToolbar</c>'s "Source:" select had been commented out of the markup while
        /// <c>OnDataSourceChanged</c> — which calls <c>UserSettings.SetDataSource</c>, an
        /// install-wide persisted write — stayed live in the code block with no call site. A dead
        /// handler is not a safe handler: it is a gate nobody will think to add.
        /// </summary>
        [Fact]
        public void TheDeadDataSourceWriteIsGoneFromTheToolbar()
        {
            var text = ReadMarkup("Components/Layout/DashboardToolbar.razor");

            // The CALL, not the word — the comment left in its place names it deliberately.
            Assert.DoesNotContain("UserSettings.SetDataSource(", text, StringComparison.Ordinal);
            Assert.DoesNotContain("private async Task OnDataSourceChanged", text, StringComparison.Ordinal);
            Assert.DoesNotContain("private void OpenSettings()", text, StringComparison.Ordinal);
        }

        // ── Fixtures ─────────────────────────────────────────────────────

        private RbacService NewRbac(RbacConfig? config = null, params RbacUser[] users)
        {
            if (config != null) File.WriteAllText(_configPath, JsonSerializer.Serialize(config));
            if (users.Length > 0) File.WriteAllText(_usersPath, JsonSerializer.Serialize(users.ToList()));
            return new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);
        }

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

        /// <summary>Unauthenticated, from 192.10.10.32 — the origin the cold gate used.</summary>
        private static AppUserState LanCircuit(RbacService rbac)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.10.10.32");
            // The Host header is attacker-controlled and must not move the answer.
            ctx.Request.Host = new HostString("localhost", 5187);

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

        private static string ReadMarkup(string relativePath)
        {
            var full = Path.Combine(RawPassedScan.RepoRoot().FullName,
                                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), relativePath + " is gone; the test moves with the file.");
            return File.ReadAllText(full);
        }

        /// <summary>
        /// Builds the component and injects ONLY <see cref="AppUserState"/>. Every other service
        /// stays null on purpose — that is what makes a handler running past its gate fault
        /// visibly instead of silently doing nothing.
        /// </summary>
        private static object NewComponentWith(string typeName, AppUserState state)
        {
            var type = typeof(AppUserState).Assembly.GetType(typeName);
            Assert.True(type != null, "No such component type: " + typeName);

            var component = Activator.CreateInstance(type!, nonPublic: true)!;

            var injected = type!.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(p => p.GetCustomAttributes(typeof(InjectAttribute), inherit: true).Any())
                .Where(p => p.PropertyType.IsAssignableFrom(typeof(AppUserState)))
                .ToList();

            Assert.True(injected.Count == 1,
                typeName + " must have exactly one injected AppUserState property for the gate to "
                + "read; found " + injected.Count + ".");

            injected[0].SetValue(component, state);
            return component;
        }

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
    }
}
