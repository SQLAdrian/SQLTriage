/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
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
    /// The harness round 8's gate tests drive real page handlers through.
    ///
    /// <para>Same shape as <c>Tests/Portal/RbacRound4PortalStatusHandlerTests</c>, and for the same
    /// reason: a gate asserted from source is a claim about a file, and this file's own history is
    /// a list of source-level claims that turned out to be false. Every service on the component is
    /// left NULL except the two the gate itself needs (<see cref="AppUserState"/> and the
    /// <see cref="ToastService"/> the refusal message goes to), so a handler that runs past its gate
    /// FAULTS. The denial assertion and the positive control are therefore the same measurement read
    /// two ways, which is what stops the denial half being vacuous.</para>
    ///
    /// <para>Roles are real: an ENFORCED <see cref="RbacService"/> plus
    /// <see cref="AppUserState.SetRole"/>, so <c>viewer</c>, <c>operator</c> and <c>admin</c> get the
    /// shipped permission matrix rather than a stub. That matters for round 8 specifically, because
    /// two different permissions were ruled onto one page: an Operator must be able to deploy an
    /// Extended Events session and must NOT be able to rewrite the alert definitions.</para>
    ///
    /// <para>It is a class rather than a static helper so the RBAC store files are per-test-class
    /// temp files, and it is public so the community-gated Alerts tests under <c>Gated\</c> can use
    /// it without duplicating any of it. It binds no type the community build removes.</para>
    /// </summary>
    public sealed class RbacRound8GateHarness : IDisposable
    {
        private readonly string _tempDir;
        private readonly RbacService _rbac;

        public RbacRound8GateHarness()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "rbac-round8-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            var configPath = Path.Combine(_tempDir, "rbac-config.json");
            var usersPath = Path.Combine(_tempDir, "rbac-users.json");

            // ENFORCED, so RbacService.IsAuthorized answers from the permission matrix alone and the
            // unconfigured-install bootstrap hatch cannot quietly grant everything.
            var config = new RbacConfig { Enabled = true };
            config.LocalPassword.Enabled = true;
            File.WriteAllText(configPath, JsonSerializer.Serialize(config));
            File.WriteAllText(usersPath, JsonSerializer.Serialize(new List<RbacUser>
            {
                new()
                {
                    Email = "admin@example.com",
                    DisplayName = "admin",
                    Provider = AuthProviders.Local,
                    Role = AppRoles.Admin,
                    Enabled = true,
                    PasswordHash = RbacService.HashPassword("correct horse battery staple"),
                },
            }));

            _rbac = new RbacService(NullLogger<RbacService>.Instance, configPath, usersPath);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        /// <summary>A browser-hosted, NON-loopback circuit carrying the given role.</summary>
        public AppUserState Circuit(string role)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.10.32");
            ctx.Request.Host = new HostString("localhost", 5170);

            var services = new ServiceCollection();
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = ctx });

            var state = new AppUserState(
                HostEnvironmentInfo.BrowserHosted, _rbac,
                services.BuildServiceProvider(), NullLogger<AppUserState>.Instance);
            state.SetRole(role);
            return state;
        }

        /// <summary>
        /// What a page carrying this role is actually told when it asks for a permission — the whole
        /// production path, not a table beside it: enforced RBAC, a NON-loopback browser circuit
        /// (so no bootstrap eligibility), and <see cref="AppUserState.IsAuthorized(string)"/> as the
        /// only entry point a shipped page has.
        ///
        /// <para><b>Added 2026-08-17 (the chokepoint lane).</b> Nine assertions across this file and
        /// <c>Gated/RbacRound8AlertsHandlerGateTests.cs</c> used to call the static
        /// <c>RbacService.HasPermission</c> to pin the matrix behind the handler gates above. That
        /// method went PRIVATE when the fail-open surface was closed structurally, so the old
        /// spelling no longer compiles from the test assembly — <c>InternalsVisibleTo</c> does not
        /// reach a private member, and that is the point. Routing them here preserves exactly what
        /// they pinned (which role gets which permission) and adds what they were missing: the
        /// answer now comes from the same call the gate under test makes.</para>
        /// </summary>
        public bool Authorizes(string role, string permission) => Circuit(role).IsAuthorized(permission);

        /// <summary>The component type, or a failed assertion naming it.</summary>
        public static Type ComponentType(string typeName)
        {
            var type = typeof(AppUserState).Assembly.GetType(typeName);
            Assert.True(type != null, "No such component type in the built assembly: " + typeName);
            return type!;
        }

        /// <summary>
        /// A component instance with ONLY the injected AppUserState and ToastService populated.
        /// Everything else stays null on purpose.
        /// </summary>
        public object NewComponent(string typeName, AppUserState state, ToastService toast)
        {
            var type = ComponentType(typeName);
            var component = Activator.CreateInstance(type, nonPublic: true)!;

            var injected = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(p => p.GetCustomAttributes(typeof(InjectAttribute), inherit: true).Any())
                .ToList();

            var userStateProps = injected.Where(p => p.PropertyType.IsAssignableFrom(typeof(AppUserState))).ToList();
            Assert.True(userStateProps.Count == 1,
                typeName + " must have exactly one injected AppUserState property; found " + userStateProps.Count
                + ". Without it the gate under test cannot be reached at all.");
            userStateProps[0].SetValue(component, state);

            foreach (var p in injected.Where(p => p.PropertyType.IsAssignableFrom(typeof(ToastService))))
                p.SetValue(component, toast);

            return component;
        }

        public static void SetField(object component, string name, object? value)
        {
            var field = component.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.True(field != null, component.GetType().Name + " has no field called " + name + ".");
            field!.SetValue(component, value);
        }

        public static object? GetField(object component, string name)
        {
            var field = component.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.True(field != null, component.GetType().Name + " has no field called " + name + ".");
            return field!.GetValue(component);
        }

        /// <summary>The exact shipped refusal string, read off the component rather than retyped.</summary>
        public static string RefusalConst(string typeName, string constName)
        {
            var field = ComponentType(typeName).GetField(constName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.True(field != null, typeName + " has no const called " + constName + ".");
            var value = field!.GetValue(null) as string;
            Assert.False(string.IsNullOrWhiteSpace(value), typeName + "." + constName + " is empty.");
            return value!;
        }

        /// <summary>Invokes the handler and reports what escaped it.</summary>
        public static async Task<Exception?> InvokeAsync(object component, string handler, object?[] args)
        {
            var method = component.GetType().GetMethod(
                handler, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            Assert.True(method != null, component.GetType().Name + " has no handler called " + handler + ".");

            try
            {
                var result = method!.Invoke(component, args);
                if (result is Task task) await task;
                return null;
            }
            catch (TargetInvocationException ex) { return ex.InnerException ?? ex; }
            catch (Exception ex) { return ex; }
        }

        /// <summary>Captures every toast the component raises while a handler runs.</summary>
        public static (ToastService Service, List<ToastNotification> Captured) NewToastCapture()
        {
            var captured = new List<ToastNotification>();
            var service = new ToastService();
            service.OnShow += t => captured.Add(t);
            return (service, captured);
        }
    }

    /// <summary>
    /// Round 8's gates on <c>Pages/QuickCheck.razor</c> and <c>Pages/ReportBundles.razor</c>, driven
    /// through the real handlers. The Alerts half lives under <c>Gated\</c>, because
    /// <c>buildprofile.targets</c> Content-Removes <c>Pages\Alerts.razor</c> when the operations
    /// module is off, which is the community default - so that type is absent from the community
    /// assembly and a test that looks it up by name would fail at RUN time where no source scan can
    /// see it (Gated\README.md, category 3).
    ///
    /// <para><b>Not exercised here, and why.</b> <c>ReportBundles.OpenInExplorer</c>'s ALLOWED branch
    /// is deliberately not driven: past the gate it reaches
    /// <c>Process.Start("explorer.exe", "/select,...")</c>, so a passing positive control would mean
    /// this suite opened a File Explorer window on whatever box ran it. The refusal is driven, and
    /// the fact that its gate is not vacuous rests on the census's per-permission pin
    /// (<c>EachRoundEightHandlerIsGatedOnItsRuledPermission</c>) plus the mutation that made that pin
    /// fail. Stated rather than papered over.</para>
    /// </summary>
    public sealed class RbacRound8QuickCheckAndReportsGateTests : IDisposable
    {
        private const string QuickCheck = "SQLTriage.Pages.QuickCheck";
        private const string ReportBundles = "SQLTriage.Pages.ReportBundles";

        private readonly RbacRound8GateHarness _h = new();

        public void Dispose() => _h.Dispose();

        /// <summary>
        /// The five report-layout handlers, all ruled onto <c>settings</c>. Args and the one field
        /// each body reads before it writes, so an ALLOWED run really reaches ReportPageCfg.
        /// </summary>
        public static TheoryData<string, object?[]> LayoutHandlers => new()
        {
            { "HandleSectionSaved", new object?[] { new ReportSection() } },
            { "HandleSectionAdded", new object?[] { new ReportSection() } },
            { "DeleteSection",      new object?[] { new ReportSection() } },
            { "ToggleSection",      new object?[] { new ReportSection() } },
            { "MoveSection",        new object?[] { new ReportSection(), 1 } },
        };

        [Theory]
        [MemberData(nameof(LayoutHandlers))]
        public async Task LayoutHandler_RefusesEveryRoleBelowAdmin(string handler, object?[] args)
        {
            string refusal = RbacRound8GateHarness.RefusalConst(QuickCheck, "LayoutRefusal");

            foreach (var role in new[] { AppRoles.Viewer, AppRoles.Operator })
            {
                var (toast, captured) = RbacRound8GateHarness.NewToastCapture();
                var component = _h.NewComponent(QuickCheck, _h.Circuit(role), toast);
                RbacRound8GateHarness.SetField(component, "_pageDef", new ReportPageDefinition());

                var thrown = await RbacRound8GateHarness.InvokeAsync(component, handler, args);

                Assert.True(thrown == null,
                    "QuickCheck." + handler + " ran past its gate as " + role + " and reached a null "
                    + "service (" + thrown?.GetType().Name + ").");
                Assert.Contains(captured, t => t.Title == refusal);
            }
        }

        [Theory]
        [MemberData(nameof(LayoutHandlers))]
        public async Task LayoutHandler_ProceedsForAnAdmin(string handler, object?[] args)
        {
            var (toast, captured) = RbacRound8GateHarness.NewToastCapture();
            var component = _h.NewComponent(QuickCheck, _h.Circuit(AppRoles.Admin), toast);
            RbacRound8GateHarness.SetField(component, "_pageDef", new ReportPageDefinition());

            var thrown = await RbacRound8GateHarness.InvokeAsync(component, handler, args);

            Assert.True(thrown != null,
                "QuickCheck." + handler + " completed quietly for an ADMIN. With ReportPageCfg null a "
                + "handler that really runs must fault, so a quiet completion means the refusal "
                + "assertion beside it proves nothing.");
            Assert.DoesNotContain(captured, t => t.Title == RbacRound8GateHarness.RefusalConst(QuickCheck, "LayoutRefusal"));
        }

        /// <summary>
        /// <c>ConfirmAcceptance</c> catches everything it throws, so the discriminator is the field
        /// the page renders rather than an exception.
        /// </summary>
        [Fact]
        public async Task ConfirmAcceptance_RefusesBelowAdminAndProceedsForAnAdmin()
        {
            string refusal = RbacRound8GateHarness.RefusalConst(QuickCheck, "AcceptanceRefusal");

            foreach (var role in new[] { AppRoles.Viewer, AppRoles.Operator })
            {
                var (toast, _) = RbacRound8GateHarness.NewToastCapture();
                var component = _h.NewComponent(QuickCheck, _h.Circuit(role), toast);
                RbacRound8GateHarness.SetField(component, "_acceptTarget", new CheckResult());
                RbacRound8GateHarness.SetField(component, "_acceptReason", "a reason");

                await RbacRound8GateHarness.InvokeAsync(component, "ConfirmAcceptance", Array.Empty<object?>());

                Assert.Equal(refusal, RbacRound8GateHarness.GetField(component, "_acceptError"));
                Assert.False((bool)RbacRound8GateHarness.GetField(component, "_acceptSaving")!,
                    "a refused acceptance must not leave the modal in its saving state");
            }

            var (adminToast, _) = RbacRound8GateHarness.NewToastCapture();
            var admin = _h.NewComponent(QuickCheck, _h.Circuit(AppRoles.Admin), adminToast);
            RbacRound8GateHarness.SetField(admin, "_acceptTarget", new CheckResult());
            RbacRound8GateHarness.SetField(admin, "_acceptReason", "a reason");

            await RbacRound8GateHarness.InvokeAsync(admin, "ConfirmAcceptance", Array.Empty<object?>());

            var adminError = RbacRound8GateHarness.GetField(admin, "_acceptError") as string;
            Assert.True(adminError != null && adminError != refusal,
                "ConfirmAcceptance did not get past its gate for an ADMIN. With AcceptedFindings null "
                + "it must reach that service and record the fault, so the refusals above are real. "
                + "Got: " + (adminError ?? "(null)"));
        }

        /// <summary>
        /// <c>RevokeAcceptance</c> also swallows its own exception, so the discriminator is which
        /// toast the page raised.
        /// </summary>
        [Fact]
        public async Task RevokeAcceptance_RefusesBelowAdminAndProceedsForAnAdmin()
        {
            string refusal = RbacRound8GateHarness.RefusalConst(QuickCheck, "AcceptanceRefusal");

            foreach (var role in new[] { AppRoles.Viewer, AppRoles.Operator })
            {
                var (toast, captured) = RbacRound8GateHarness.NewToastCapture();
                var component = _h.NewComponent(QuickCheck, _h.Circuit(role), toast);

                await RbacRound8GateHarness.InvokeAsync(component, "RevokeAcceptance", new object?[] { new CheckResult() });

                Assert.Contains(captured, t => t.Title == refusal);
                Assert.DoesNotContain(captured, t => t.Type == ToastType.Error);
            }

            var (adminToast, adminCaptured) = RbacRound8GateHarness.NewToastCapture();
            var admin = _h.NewComponent(QuickCheck, _h.Circuit(AppRoles.Admin), adminToast);

            await RbacRound8GateHarness.InvokeAsync(admin, "RevokeAcceptance", new object?[] { new CheckResult() });

            Assert.DoesNotContain(adminCaptured, t => t.Title == refusal);
            Assert.Contains(adminCaptured, t => t.Type == ToastType.Error);
        }

        /// <summary>
        /// The one that had a gate in its MARKUP and none in its handler: the owner editor is behind
        /// <c>IsAuthorized("settings")</c> in the razor, and <c>SaveOwnerRowAsync</c> itself was open.
        /// </summary>
        [Fact]
        public async Task SaveOwnerRowAsync_RefusesBelowAdminAndProceedsForAnAdmin()
        {
            string refusal = RbacRound8GateHarness.RefusalConst(ReportBundles, "OwnerRefusal");
            var rowType = RbacRound8GateHarness.ComponentType(ReportBundles)
                .GetNestedType("OwnerEditRow", BindingFlags.NonPublic);
            Assert.True(rowType != null, "ReportBundles no longer declares OwnerEditRow.");

            foreach (var role in new[] { AppRoles.Viewer, AppRoles.Operator })
            {
                var (toast, captured) = RbacRound8GateHarness.NewToastCapture();
                var component = _h.NewComponent(ReportBundles, _h.Circuit(role), toast);
                var row = Activator.CreateInstance(rowType!, nonPublic: true)!;

                var thrown = await RbacRound8GateHarness.InvokeAsync(component, "SaveOwnerRowAsync", new object?[] { row });

                Assert.True(thrown == null,
                    "ReportBundles.SaveOwnerRowAsync ran past its gate as " + role + " ("
                    + thrown?.GetType().Name + ").");
                Assert.Contains(captured, t => t.Title == refusal);
                Assert.False((bool)rowType!.GetField("Saving")!.GetValue(row)!,
                    "a refused save must not leave the row spinning");
            }

            var (adminToast, _) = RbacRound8GateHarness.NewToastCapture();
            var admin = _h.NewComponent(ReportBundles, _h.Circuit(AppRoles.Admin), adminToast);
            var adminRow = Activator.CreateInstance(rowType!, nonPublic: true)!;

            var adminThrown = await RbacRound8GateHarness.InvokeAsync(admin, "SaveOwnerRowAsync", new object?[] { adminRow });

            Assert.True(adminThrown != null,
                "SaveOwnerRowAsync completed quietly for an ADMIN. With ReportBundleSvc and Logger "
                + "null it must fault, so a quiet completion makes the refusals above vacuous.");
        }

        /// <summary>
        /// <c>OpenInExplorer</c>, refusal only. The allowed branch starts a process on the host, so it
        /// is not driven here - see the class note.
        /// </summary>
        [Fact]
        public async Task OpenInExplorer_RefusesBelowAdmin()
        {
            string refusal = RbacRound8GateHarness.RefusalConst(ReportBundles, "RevealRefusal");
            string probePath = Path.Combine(Path.GetTempPath(), "sqlt-round8-never-opened.txt");

            foreach (var role in new[] { AppRoles.Viewer, AppRoles.Operator })
            {
                var (toast, captured) = RbacRound8GateHarness.NewToastCapture();
                var component = _h.NewComponent(ReportBundles, _h.Circuit(role), toast);

                var thrown = await RbacRound8GateHarness.InvokeAsync(component, "OpenInExplorer", new object?[] { probePath });

                Assert.True(thrown == null, "OpenInExplorer faulted for " + role + ": " + thrown?.Message);
                Assert.Contains(captured, t => t.Title == refusal);
            }
        }

        /// <summary>
        /// The permission ruled onto these handlers is Admin-only, and that is asserted against the
        /// SHIPPED matrix rather than assumed from the word "settings". Without this the tests above
        /// would still pass if <c>settings</c> were quietly widened to every role.
        ///
        /// <para><b>Rerouted 2026-08-17 (the chokepoint lane), and it got stronger.</b> These three
        /// assertions used to call <c>RbacService.HasPermission</c> directly; that method is now
        /// PRIVATE and the old spelling does not compile. They now ask
        /// <see cref="RbacRound8GateHarness.Authorizes"/>, which is the production path — enforced
        /// RBAC, a non-loopback circuit with no bootstrap eligibility, and
        /// <see cref="AppUserState.IsAuthorized(string)"/> on top — so the claim is now about what
        /// the gated page is told rather than about a static table beside it.</para>
        /// </summary>
        [Fact]
        public void SettingsIsAdminOnlyInTheShippedMatrix()
        {
            Assert.True(_h.Authorizes(AppRoles.Admin, "settings"));
            Assert.False(_h.Authorizes(AppRoles.Operator, "settings"));
            Assert.False(_h.Authorizes(AppRoles.Viewer, "settings"));
        }
    }
}
