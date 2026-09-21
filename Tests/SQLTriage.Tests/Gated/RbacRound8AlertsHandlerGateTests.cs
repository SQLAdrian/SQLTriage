/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests.Gated
{
    /// <summary>
    /// Round 8's eleven gates on <c>Pages/Alerts.razor</c>, driven through the real handlers.
    ///
    /// <para><b>Why this file is under <c>Gated\</c>.</b> <c>buildprofile.targets</c>
    /// Content-Removes <c>Pages\Alerts.razor</c> when the operations module is off, and it IS off in
    /// <c>buildprofile.json</c>, so <c>SQLTriage.Pages.Alerts</c> does not exist in the community
    /// assembly. This file reaches that type by NAME, so it would compile under community and fail
    /// at RUN time - Gated\README.md category 3, the one no source scan can see. The folder glob in
    /// the test csproj is what keeps it out of the community suite.</para>
    ///
    /// <para><b>What this proves that the census cannot.</b> The census reads source. These drive
    /// the shipped method bodies with every service null except AppUserState and ToastService, so a
    /// handler that gets past its gate FAULTS - the refusal and the positive control are one
    /// measurement read two ways. And because the roles are real (enforced RbacService plus
    /// SetRole), the two DIFFERENT permissions ruled onto this one page are separated by exercise:
    /// an Operator deploys an Extended Events session and is refused the alert definitions.</para>
    /// </summary>
    public sealed class RbacRound8AlertsHandlerGateTests : IDisposable
    {
        private const string Alerts = "SQLTriage.Pages.Alerts";

        private readonly RbacRound8GateHarness _h = new();

        public void Dispose() => _h.Dispose();

        private static string ManageRefusal => RbacRound8GateHarness.RefusalConst(Alerts, "ManageAlertsRefusal");
        private static string ScriptsRefusal => RbacRound8GateHarness.RefusalConst(Alerts, "RunScriptsRefusal");

        /// <summary>
        /// The eight handlers ruled onto <c>manage_alerts</c>, with the args and the one field each
        /// body reads before it writes, so an ALLOWED run really reaches the null service.
        ///
        /// <para><c>ToggleDryRun</c> joined in round 9. It is the one entry here that no census
        /// found: it writes a PROPERTY on the singleton engine rather than calling anything, so the
        /// call-shaped net was structurally blind to it and it was spotted in a render, live in the
        /// same row as a disabled Stop. Its allowed run faults on the null <c>Engine</c> exactly as
        /// the others do, which is what makes the refusal beside it mean something.</para>
        /// </summary>
        public static TheoryData<string, object?[], string?> ManageAlertsHandlers => new()
        {
            { "ToggleEngine",        Array.Empty<object?>(), null },
            { "ToggleDryRun",        new object?[] { new ChangeEventArgs { Value = true } }, null },
            { "RunNow",              Array.Empty<object?>(), null },
            { "SaveGlobalDefaults",  Array.Empty<object?>(), null },
            { "ToggleAlert",         new object?[] { new AlertDefinition(), false }, null },
            { "OnSeverityChanged",   new object?[] { new AlertDefinition(), new ChangeEventArgs { Value = "High" } }, null },
            // alerts-r2-10, added 2026-08-26. The five Definitions-tab inline @onchange lambdas that
            // wrote alert-definitions.json with no gate are now named handlers; each must refuse a
            // Viewer/Operator and reach the null Definitions service (fault) for an Admin, exactly
            // like OnSeverityChanged beside them. Before this they were inline lambdas with no name,
            // invisible to a by-name census.
            { "OnWarningThresholdChanged",  new object?[] { new AlertDefinition(), new ChangeEventArgs { Value = "10" } }, null },
            { "OnCriticalThresholdChanged", new object?[] { new AlertDefinition(), new ChangeEventArgs { Value = "20" } }, null },
            { "OnFrequencySecondsChanged",  new object?[] { new AlertDefinition(), new ChangeEventArgs { Value = "60" } }, null },
            { "OnNextAlertDelayChanged",    new object?[] { new AlertDefinition(), new ChangeEventArgs { Value = "5" } }, null },
            { "OnSendEmailChanged",         new object?[] { new AlertDefinition(), new ChangeEventArgs { Value = true } }, null },
            { "SaveEdit",            Array.Empty<object?>(), "_editingAlert" },
            { "SaveTemplate",        Array.Empty<object?>(), "_editingTemplate" },
        };

        /// <summary>The four ruled onto <c>run_scripts</c>.</summary>
        public static TheoryData<string, object?[], string?> RunScriptsHandlers => new()
        {
            { "DeployComprehensiveSession", Array.Empty<object?>(), null },
            { "DeployDeadlockSession",      Array.Empty<object?>(), null },
            { "DeployErrorSession",         Array.Empty<object?>(), null },
            { "TestQuery",                  Array.Empty<object?>(), "_editingAlert" },
        };

        private object Component(string role, out System.Collections.Generic.List<SQLTriage.Data.ToastNotification> captured)
        {
            var (toast, list) = RbacRound8GateHarness.NewToastCapture();
            captured = list;
            return _h.NewComponent(Alerts, _h.Circuit(role), toast);
        }

        private static void Prime(object component, string? field)
        {
            if (field == "_editingAlert")
                RbacRound8GateHarness.SetField(component, field, new AlertDefinition { Query = "SELECT 1" });
            else if (field == "_editingTemplate")
                RbacRound8GateHarness.SetField(component, field, new ChannelTemplate());
        }

        // ── manage_alerts: Admin only ────────────────────────────────────

        [Theory]
        [MemberData(nameof(ManageAlertsHandlers))]
        public async Task ManageAlertsHandler_RefusesViewerAndOperator(string handler, object?[] args, string? field)
        {
            foreach (var role in new[] { AppRoles.Viewer, AppRoles.Operator })
            {
                var component = Component(role, out var captured);
                Prime(component, field);

                var thrown = await RbacRound8GateHarness.InvokeAsync(component, handler, args);

                Assert.True(thrown == null,
                    "Alerts." + handler + " ran past its gate as " + role + " and reached a null "
                    + "service (" + thrown?.GetType().Name + ": " + thrown?.Message + ").");
                Assert.Contains(captured, t => t.Title == ManageRefusal);
            }
        }

        [Theory]
        [MemberData(nameof(ManageAlertsHandlers))]
        public async Task ManageAlertsHandler_ProceedsForAnAdmin(string handler, object?[] args, string? field)
        {
            var component = Component(AppRoles.Admin, out var captured);
            Prime(component, field);

            var thrown = await RbacRound8GateHarness.InvokeAsync(component, handler, args);

            Assert.True(thrown != null,
                "Alerts." + handler + " completed quietly for an ADMIN. Every service on this "
                + "component is null, so a handler that really runs must fault; a quiet completion "
                + "means the refusal assertion beside it proves nothing.");
            Assert.DoesNotContain(captured, t => t.Title == ManageRefusal);
        }

        // ── run_scripts: Admin AND Operator ──────────────────────────────

        [Theory]
        [MemberData(nameof(RunScriptsHandlers))]
        public async Task RunScriptsHandler_RefusesAViewer(string handler, object?[] args, string? field)
        {
            var component = Component(AppRoles.Viewer, out var captured);
            Prime(component, field);

            var thrown = await RbacRound8GateHarness.InvokeAsync(component, handler, args);

            Assert.True(thrown == null,
                "Alerts." + handler + " ran past its gate as a viewer (" + thrown?.GetType().Name + ").");
            Assert.Contains(captured, t => t.Title == ScriptsRefusal);
        }

        /// <summary>
        /// THE POINT OF USING TWO PERMISSIONS ON ONE PAGE, exercised rather than described: the same
        /// role that is refused every alert-definition write above gets through here.
        /// </summary>
        [Theory]
        [MemberData(nameof(RunScriptsHandlers))]
        public async Task RunScriptsHandler_ProceedsForAnOperatorAndForAnAdmin(string handler, object?[] args, string? field)
        {
            foreach (var role in new[] { AppRoles.Operator, AppRoles.Admin })
            {
                var component = Component(role, out var captured);
                Prime(component, field);

                var thrown = await RbacRound8GateHarness.InvokeAsync(component, handler, args);

                Assert.True(thrown != null,
                    "Alerts." + handler + " completed quietly for " + role + ". With ConnectionManager "
                    + "and XEventService null it must fault, so a quiet completion makes the viewer "
                    + "refusal above vacuous.");
                Assert.DoesNotContain(captured, t => t.Title == ScriptsRefusal);
            }
        }

        /// <summary>
        /// The two permissions this page's gates name, held to the SHIPPED matrix. Without this the
        /// tests above would still pass if manage_alerts were quietly widened to every role.
        ///
        /// <para><b>Rerouted 2026-08-17 (the chokepoint lane), and it got stronger.</b> These six
        /// assertions used to call <c>RbacService.HasPermission</c> directly. That method is now
        /// PRIVATE — the structural close of the census arms race — so the old spelling does not
        /// compile from this assembly at all. They now ask the harness circuit, which is the
        /// production path a page actually travels: an ENFORCED <c>RbacService</c>, a non-loopback
        /// browser circuit with no bootstrap eligibility, and
        /// <see cref="AppUserState.IsAuthorized(string)"/> on top. So this no longer asserts what a
        /// static table says; it asserts what the page in the tests above is actually told, which is
        /// the fact those tests depend on.</para>
        /// </summary>
        [Fact]
        public void TheTwoPermissionsMeanWhatThisRoundRuledTheyMean()
        {
            Assert.True(_h.Authorizes(AppRoles.Admin, "manage_alerts"));
            Assert.False(_h.Authorizes(AppRoles.Operator, "manage_alerts"));
            Assert.False(_h.Authorizes(AppRoles.Viewer, "manage_alerts"));

            Assert.True(_h.Authorizes(AppRoles.Admin, "run_scripts"));
            Assert.True(_h.Authorizes(AppRoles.Operator, "run_scripts"));
            Assert.False(_h.Authorizes(AppRoles.Viewer, "run_scripts"));
        }
    }
}
