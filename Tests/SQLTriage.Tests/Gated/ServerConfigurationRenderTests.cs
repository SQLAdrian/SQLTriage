/* In the name of God, the Merciful, the Compassionate */
/*
 * ServerConfigurationRenderTests — the RENDERED proof of the three multi-instance affordances the
 * mc-preview-usability lane added to Pages/ServerConfiguration.razor (2026-09-08).
 *
 * WHY IT EXISTS. The lane shipped these three things held only by a TEXT LINT, on the stated reason
 * "there is no bUnit in this tree". The cold gate's finding F2: the premise is true and the
 * conclusion is false. This tree renders real pages through the framework's own HtmlRenderer with no
 * bUnit anywhere — twelve *RenderTests do it, and Gated/RemediationBatchApplyRenderTests renders a
 * page that is Content-Removed from community exactly like this one, with a faked bundle for the
 * licence. So the two affordances Adrian asked for were provable by render and were proved only as
 * text. A lint says the call is WRITTEN. Only a render says the branch REACHES A BROWSER: a
 * @if whose condition was inverted keeps its class, keeps its position, and passes every source
 * scan in the file while shipping nothing.
 *
 * THE PAIR IS THE POINT, as in the sibling file. Fact 1 renders with NOTHING run and asserts zero
 * download controls, no tally and no consolidated toggle. Facts 2 and 3 render with a seeded run and
 * assert they are all there, on the right row. Fact 1 alone would pass on a page that had no
 * download at all; facts 2 and 3 alone would pass on a page that showed one always.
 *
 * ⚠ THE SUBSTITUTIONS, STATED PLAINLY, SO NO CLAIM HERE IS OVER-READ:
 *
 *   1. THE LICENCE SOURCE IS A FAKE. FakeBundleAccessor (Tier.Full, Features.Remediation = true)
 *      stands in for a signed bundle file, the same substitution the sibling file makes. The GATE
 *      LOGIC is the real IRemediationCapability over the real ServerConfigSuiteGate; the SIGNATURE
 *      check on a minted bundle is not exercised here and stays untested by this file. This is the
 *      lane's residual R2 in miniature: a throwaway server has no Full bundle, so the page renders
 *      its licence refusal there and the markup below can only be seen through this seam.
 *
 *   2. THE HOST ANSWER IS THE DESKTOP ONE. AddSharedServices registers HostEnvironmentInfo.Desktop,
 *      so AppUserState.IsAuthorized returns true and the page's RBAC gate admits this render. That
 *      gate is not what this file measures — RbacPageGateCensusTests owns it, and the gate's live
 *      loopback probe exercised it — but the render is asserted to have passed it (below) rather
 *      than assumed, because every assertion here would vacuously pass over an AccessDenied shell.
 *
 *   3. THE RUN IS PLANTED, NOT EXECUTED. A real multi-instance preview needs SQL Server. The
 *      InstanceRunStatus rows are seeded straight into the scoped ServerConfigRunState the page
 *      resolves — the SAME object, taken from the same scope — with a REAL file on disk behind the
 *      Done row's ExportedPath. Everything downstream of those dictionaries is the page's real
 *      markup. What this proves is the RENDERING of a given run state, never that the run produces
 *      it; ServerConfigRunStateTests owns the loop.
 *
 *   4. NO EVENT IS DISPATCHED, so no handler runs, no JS interop is called and nothing is
 *      downloaded. That a browser really receives the bytes when the button is clicked is a
 *      separate proof and is not claimed here.
 *
 * ⚠ WHY THIS IS IN Gated/. It binds SQLTriage.Pages.ServerConfiguration, which buildprofile.targets
 * Content-Removes from a COMMUNITY build (buildprofile.targets:165). The Compile Remove of
 * Gated\**\*.cs takes this folder out of the community test build, so this runs only where the page
 * exists — which is why its per-class count on the community axis is 0, with that reason.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Remediation;
using SQLTriage.Tests.Licensing;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests.Gated
{
    public sealed class ServerConfigurationRenderTests
    {
        private const string DoneInstance = "SQLT-RENDER-DONE";
        private const string RefusedInstance = "SQLT-RENDER-REFUSED";
        private const string RefusalMessage = "Not licensed on this install.";

        private readonly ITestOutputHelper _out;
        public ServerConfigurationRenderTests(ITestOutputHelper output) => _out = output;

        // ── 1. The negative control: nothing has run, so none of the three is on the page ──

        /// <summary>
        /// With both status dictionaries empty, an operator's browser receives no download control,
        /// no tally and no consolidated toggle. Without this the two facts below could pass over a
        /// page that rendered all three unconditionally, which would be its own defect: a Download
        /// button over a null ExportedPath.
        /// </summary>
        [Fact]
        public async Task WithNoRun_TheBrowserReceivesNoDownload_NoTally_AndNoConsolidatedPanel()
        {
            var html = await RenderAsync(_ => { });

            Assert.DoesNotContain("svcfg-dl", html, StringComparison.Ordinal);
            Assert.DoesNotContain("svcfg-summary", html, StringComparison.Ordinal);
            Assert.DoesNotContain("svcfg-consolidated", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Download consolidated", html, StringComparison.Ordinal);

            // …and it is the real page that rendered, not a refusal shell that would make the four
            // assertions above true for the wrong reason.
            Assert.Contains("Multi-Instance", html, StringComparison.Ordinal);
        }

        // ── 2. Item 1 + item 3, rendered: the PREVIEW table ───────────────────────────────

        /// <summary>
        /// The preview lane with one Done instance (a real exported file, 35 rows) and one Refused.
        /// Three assertions, one per thing Adrian asked for:
        /// <list type="number">
        /// <item>the Done row carries a Download control and the Refused row does NOT — the whole
        ///   point of the affordance is that it appears where there is a file to fetch;</item>
        /// <item>the tally is PERSISTENT MARKUP reading "Done 1 · Refused 1 · Failed 0". Before this
        ///   lane those three numbers reached a Toast and nothing else, so they faded a few seconds
        ///   after a run even if the operator never left the page;</item>
        /// <item>the consolidated toggle and its download are both on the page.</item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task AFinishedPreviewRun_OffersTheDownloadOnTheDoneRowOnly_WithAPersistentTallyAndTheConsolidatedControls()
        {
            var export = NewExportFile("preview");

            try
            {
                var html = await RenderAsync(state =>
                {
                    state.PreviewStatuses[DoneInstance] = new InstanceRunStatus
                    {
                        ServerName = DoneInstance,
                        State = ServerConfigRunState.StateDone,
                        ExportedPath = export,
                        RowCount = 35,
                    };
                    state.PreviewStatuses[RefusedInstance] = new InstanceRunStatus
                    {
                        ServerName = RefusedInstance,
                        State = ServerConfigRunState.StateRefused,
                        Message = RefusalMessage,
                    };
                });

                AssertTheThreeAffordances(html, export);
            }
            finally
            {
                TryDelete(export);
            }
        }

        // ── 3. The same, on the APPLY table ───────────────────────────────────────────────

        /// <summary>
        /// §0's ruling was "a download in BOTH Detail cells". The apply table is a separate block of
        /// markup bound to a separate dictionary, so proving the preview one proves nothing about
        /// it — and the apply lane is the one whose export an operator most needs to keep, because
        /// it is the record of a change that was actually made.
        /// </summary>
        [Fact]
        public async Task AFinishedApplyRun_CarriesTheSameThreeAffordances_OnItsOwnTable()
        {
            var export = NewExportFile("apply");

            try
            {
                var html = await RenderAsync(state =>
                {
                    state.ApplyStatuses[DoneInstance] = new InstanceRunStatus
                    {
                        ServerName = DoneInstance,
                        State = ServerConfigRunState.StateDone,
                        ExportedPath = export,
                        RowCount = 35,
                    };
                    state.ApplyStatuses[RefusedInstance] = new InstanceRunStatus
                    {
                        ServerName = RefusedInstance,
                        State = ServerConfigRunState.StateRefused,
                        Message = RefusalMessage,
                    };
                });

                AssertTheThreeAffordances(html, export);
            }
            finally
            {
                TryDelete(export);
            }
        }

        // ── The assertions, shared by both tables ─────────────────────────────────────────

        private void AssertTheThreeAffordances(string html, string export)
        {
            var doneRow = RowHtmlContaining(html, DoneInstance);
            var refusedRow = RowHtmlContaining(html, RefusedInstance);

            Assert.False(string.IsNullOrEmpty(doneRow), "the Done instance rendered no table row at all");
            Assert.False(string.IsNullOrEmpty(refusedRow), "the Refused instance rendered no table row at all");

            // ── Item 1: the file is reachable, and only where there is one ──
            Assert.Contains("svcfg-dl", doneRow, StringComparison.Ordinal);
            Assert.Contains("Download", Visible(doneRow), StringComparison.Ordinal);
            Assert.DoesNotContain("svcfg-dl", refusedRow, StringComparison.Ordinal);

            // Exactly one, so a future edit that hoists the button out of the @if is caught here and
            // not by an operator clicking Download on a row that exported nothing.
            Assert.Equal(1, Regex.Matches(html, "svcfg-dl").Count);

            // The control names the file it would fetch — the row text carries the file name, the
            // button's title carries the full path the handler reads.
            Assert.Contains(Path.GetFileName(export), Visible(doneRow), StringComparison.Ordinal);
            Assert.Contains(export, doneRow, StringComparison.Ordinal);

            // The refused row still says WHY, which is what it has instead of a file.
            Assert.Contains(RefusalMessage, Visible(refusedRow), StringComparison.Ordinal);

            // ── Item 3a: the tally is markup, not a toast ──
            var counts = Regex.Match(html, "<span class=\"svcfg-summary-counts\">(.*?)</span>",
                                     RegexOptions.Singleline);
            Assert.True(counts.Success,
                "the persistent Done/Refused/Failed line is not in the rendered html. Those three "
                + "numbers used to reach a Toast and nothing else, so they disappeared on their own "
                + "a few seconds after a run — the whole of Adrian's item 3.");

            var tally = Visible(counts.Groups[1].Value);
            _out.WriteLine("tally: " + tally);
            Assert.Contains("Done 1", tally, StringComparison.Ordinal);
            Assert.Contains("Refused 1", tally, StringComparison.Ordinal);
            Assert.Contains("Failed 0", tally, StringComparison.Ordinal);

            // ── Item 3b: the consolidated view and its download ──
            var summaryBlock = Regex.Match(html, "<div class=\"svcfg-summary\">.*?</div>",
                                           RegexOptions.Singleline);
            Assert.True(summaryBlock.Success, "the svcfg-summary block did not render");
            var summaryText = Visible(summaryBlock.Value);
            Assert.Contains("Consolidated", summaryText, StringComparison.Ordinal);
            Assert.Contains("Download consolidated", summaryText, StringComparison.Ordinal);

            // The panel itself is shut until the toggle is pressed: it holds every instance's file
            // contents, so rendering it unasked would read files nobody opened.
            Assert.DoesNotContain("svcfg-consolidated", html, StringComparison.Ordinal);
        }

        // ══ The per-instance operator picker (operator-picker-mailchain, 2026-09-09) ══════════
        //
        // Adrian: "is it possible to pull existing operators from the SQL servers to select per
        // instance? … and could we check these operators and the mail profiles and whether those
        // mail profiles work". These render the page over a seeded inventory and read what a browser
        // would actually receive.
        //
        // ⚠ THE FIFTH SUBSTITUTION, on top of the four in the file header: the INVENTORY IS SEEDED.
        // A real read needs SQL Server. The rows are the ones captured off .\new2022 and .\old2017 on
        // 2026-09-09, and the SELECTION is made by the shipped rule (ServerConfigRunState.
        // LoadOperatorsAsync calling AgentMailChainProbe.DefaultOperator), not by the test - a test
        // that set SelectedOperator itself would prove only that the page renders a string it was
        // handed.

        /// <summary>
        /// The whole ask, on one row: a dropdown of the instance's real operators, the DEFAULT already
        /// chosen by the shipped rule, and a verdict badge that says the mail chain is broken with the
        /// server's own failure date behind it.
        /// </summary>
        [Fact]
        public async Task AnInstanceWithOperators_RendersThePicker_WithTheDefaultPreselectedAndTheVerdictBadge()
        {
            var html = await RenderAsync(state => SeedPicker(state, DoneInstance, New2022Inventory(), New2022Rows()));

            var row = PickerRowContaining(html, DoneInstance);
            Assert.False(string.IsNullOrEmpty(row), "the instance rendered no picker row at all");

            // A real choice, not a text box.
            Assert.Contains("<select", row, StringComparison.Ordinal);
            Assert.Contains("svcfg-picker-field", row, StringComparison.Ordinal);

            var visible = Visible(row);
            Assert.Contains("DBA", visible, StringComparison.Ordinal);
            Assert.Contains("SQLDBA", visible, StringComparison.Ordinal);

            // The label carries what a human needs to choose ON: enabled, the address, how many alerts
            // already point at them, and when Agent last emailed them.
            Assert.Contains("enabled", visible, StringComparison.Ordinal);
            Assert.Contains("sqlalerts@sqldba.org", visible, StringComparison.Ordinal);
            Assert.Contains("41 alert(s)", visible, StringComparison.Ordinal);
            Assert.Contains("2026-09-05 04:45:04", visible, StringComparison.Ordinal);

            // THE DEFAULT IS PRESELECTED, and it is the operator the enabled alerts already notify
            // (41 against SQLDBA's 7). Asserted on the option that actually carries selected.
            var selected = Regex.Match(row, "<option[^>]*value=\"([^\"]*)\"[^>]*\\bselected\\b");
            Assert.True(selected.Success, "no option is preselected, so the operator has to choose one by hand");
            Assert.Equal("DBA", selected.Groups[1].Value);

            // THE VERDICT. .\new2022 has a wired profile and has never delivered a single item.
            Assert.Contains("svcfg-verdict-broken", row, StringComparison.Ordinal);
            Assert.Contains("Broken", visible, StringComparison.Ordinal);
            Assert.Contains("2026-07-27", visible, StringComparison.Ordinal);
            Assert.Contains("mail server failure", visible, StringComparison.Ordinal);

            // Links 1-4 hold, so a test send would tell you something and is offered.
            Assert.Contains("Send test notification", visible, StringComparison.Ordinal);
        }

        /// <summary>
        /// An instance with NO operators keeps the free-text box and the baseline script's create
        /// path - unchanged behaviour, deliberately: a picker with nothing in it would be a dead end.
        /// </summary>
        [Fact]
        public async Task AnInstanceWithNoOperators_RendersTheFreeTextInput_AndNoDropdown()
        {
            var html = await RenderAsync(state =>
                SeedPicker(state, DoneInstance, new OperatorInventory { ServerName = DoneInstance }, rows: null));

            var row = PickerRowContaining(html, DoneInstance);
            Assert.False(string.IsNullOrEmpty(row), "the instance rendered no picker row at all");

            Assert.Contains("<input", row, StringComparison.Ordinal);
            Assert.Contains("operator to create", row, StringComparison.Ordinal);
            Assert.DoesNotContain("<select", row, StringComparison.Ordinal);

            // Nothing was read, so nothing is claimed.
            Assert.Contains("Not checked", Visible(row), StringComparison.Ordinal);
            Assert.DoesNotContain("Send test notification", Visible(row), StringComparison.Ordinal);
        }

        /// <summary>
        /// THE PAIR. Over a NOT CONFIGURED chain (.\old2017: Database Mail on, no profile at all) the
        /// send button is NOT offered and the badge names the failing link. Without this, the fact
        /// above would pass on a page that showed the button unconditionally.
        /// </summary>
        [Fact]
        public async Task AnInstanceWhoseChainIsNotConfigured_NamesTheLink_AndOffersNoTestSend()
        {
            var html = await RenderAsync(state => SeedPicker(state, DoneInstance, Old2017Inventory(), Old2017Rows()));

            var row = PickerRowContaining(html, DoneInstance);
            var visible = Visible(row);

            Assert.Contains("svcfg-verdict-notconfigured", row, StringComparison.Ordinal);
            Assert.Contains("Not configured", visible, StringComparison.Ordinal);
            Assert.Contains("Link 2", visible, StringComparison.Ordinal);
            Assert.DoesNotContain("Send test notification", visible, StringComparison.Ordinal);
        }

        /// <summary>
        /// Ruling 2's third branch, rendered: two equally plausible operators, so no default, so the
        /// row says what it needs and Apply is disabled until it gets it.
        /// </summary>
        [Fact]
        public async Task AnInstanceWithNoDefensibleDefault_AsksForAChoice()
        {
            var html = await RenderAsync(state =>
            {
                var inv = new OperatorInventory { ServerName = DoneInstance };
                inv.Operators.Add(OperatorRow("DBA", 0, "sqlalerts@sqldba.org"));
                inv.Operators.Add(OperatorRow("SQLDBA", 0, "alerts@sqldba.org"));
                SeedPicker(state, DoneInstance, inv, rows: null);
            });

            var row = PickerRowContaining(html, DoneInstance);
            var visible = Visible(row);

            Assert.Contains("Pick an operator", visible, StringComparison.Ordinal);
            Assert.Contains("cannot be applied until you do", visible, StringComparison.Ordinal);
            Assert.DoesNotContain(" selected", row, StringComparison.Ordinal);
        }

        /// <summary>
        /// GATE FINDING F2, rendered. A DENIED operator read used to reach the browser as an empty
        /// free-text box labelled "operator to create" beside a "Not checked" badge - a server whose
        /// operators nobody was allowed to see, drawn exactly like a server that has none. The realistic
        /// trigger is a least-privilege login: Msg 229 on msdb.dbo.sysoperators, proved on both rigs.
        /// </summary>
        [Fact]
        public async Task AnInstanceWhoseOperatorReadWasDenied_RendersTheCouldNotReadBadge_AndNoInputAtAll()
        {
            var html = await RenderAsync(state =>
            {
                var inv = new OperatorInventory { ServerName = DoneInstance };
                inv.Gaps.Add(new MailChainGap(4, "Agent operators (msdb.dbo.sysoperators)",
                    "The SELECT permission was denied on the object 'sysoperators', database 'msdb'."));
                SeedPicker(state, DoneInstance, inv, rows: null);
            });

            var row = PickerRowContaining(html, DoneInstance);
            Assert.False(string.IsNullOrEmpty(row), "the instance rendered no picker row at all");

            var visible = Visible(row);
            Assert.Contains("svcfg-verdict-couldnotread", row, StringComparison.Ordinal);
            Assert.Contains("Could not read operators", visible, StringComparison.Ordinal);
            Assert.Contains("sysoperators", visible, StringComparison.Ordinal);
            Assert.Contains("SELECT permission was denied", visible, StringComparison.Ordinal);

            // NOTHING is offered to type or pick, and the row says it cannot be applied.
            Assert.DoesNotContain("<input", row, StringComparison.Ordinal);
            Assert.DoesNotContain("<select", row, StringComparison.Ordinal);
            Assert.DoesNotContain("operator to create", row, StringComparison.Ordinal);
            Assert.DoesNotContain("Not checked", visible, StringComparison.Ordinal);
            Assert.Contains("cannot be applied", visible, StringComparison.Ordinal);
        }

        /// <summary>
        /// The gate's live case, offline: `.\nosuchrig` - a registered, unreachable server - rendered
        /// the zero-operators branch with no explanation at all. A connection that never opened is the
        /// same state as a denied read: nobody knows what operators this instance has.
        /// </summary>
        [Fact]
        public async Task AnUnreachableInstance_RendersTheCouldNotReadBadge_NamingTheConnection()
        {
            var html = await RenderAsync(state =>
            {
                var inv = new OperatorInventory { ServerName = DoneInstance };
                inv.Gaps.Add(new MailChainGap(0, "connect to " + DoneInstance,
                    "A network-related or instance-specific error occurred while establishing a connection."));
                SeedPicker(state, DoneInstance, inv, rows: null);
            });

            var row = PickerRowContaining(html, DoneInstance);
            var visible = Visible(row);

            Assert.Contains("svcfg-verdict-couldnotread", row, StringComparison.Ordinal);
            Assert.Contains("Could not read operators", visible, StringComparison.Ordinal);
            Assert.Contains("network-related", visible, StringComparison.Ordinal);
            Assert.DoesNotContain("<input", row, StringComparison.Ordinal);
            Assert.DoesNotContain("operator to create", row, StringComparison.Ordinal);
        }

        /// <summary>
        /// GATE FINDING F6: the fail-safe operator cost a shipped statement and a round trip per
        /// instance and nothing rendered it. It is SHOWN as a candidate and never preselected - here it
        /// is an operator that is not even in the dropdown, so "shown" and "picked" cannot be confused.
        /// </summary>
        [Fact]
        public async Task TheFailSafeOperatorIsShown_AndIsNeverThePreselection()
        {
            var html = await RenderAsync(state =>
            {
                var inv = new OperatorInventory { ServerName = DoneInstance, FailSafeOperator = "OpsOnCall" };
                inv.Operators.Add(OperatorRow("DBA", 41, "sqlalerts@sqldba.org", id: 4));
                inv.Operators.Add(OperatorRow("SQLDBA", 7, "alerts@sqldba.org", id: 1));
                SeedPicker(state, DoneInstance, inv, rows: null);
            });

            var row = PickerRowContaining(html, DoneInstance);
            var visible = Visible(row);

            Assert.Contains("svcfg-failsafe", row, StringComparison.Ordinal);
            Assert.Contains("Fail-safe operator: OpsOnCall", visible, StringComparison.Ordinal);

            // The preselection is still the shipped default rule's answer, not the fail-safe name.
            var selected = Regex.Match(row, "<option[^>]*value=\"([^\"]*)\"[^>]*\\bselected\\b");
            Assert.True(selected.Success, "no option is preselected");
            Assert.Equal("DBA", selected.Groups[1].Value);
            Assert.DoesNotContain("<option value=\"OpsOnCall\"", row, StringComparison.Ordinal);
        }

        /// <summary>
        /// GATE FINDING F7: a REFUSED send left nothing on screen once the 5,000 ms toast faded, and
        /// the badge does not move on a refusal. The outcome now renders under the badge, verbatim.
        /// </summary>
        [Fact]
        public async Task TheLastTestSendOutcomeStaysOnScreen_WithItsUtcTimestamp()
        {
            var html = await RenderAsync(state =>
            {
                SeedPicker(state, DoneInstance, New2022Inventory(), New2022Rows());
                state.RecordTestSendResult(DoneInstance, accepted: false,
                    "Mail not queued. Database Mail is stopped. Use sysmail_start_sp to start Database Mail.",
                    new DateTime(2026, 9, 9, 3, 20, 0, DateTimeKind.Utc));
            });

            var row = PickerRowContaining(html, DoneInstance);
            var visible = Visible(row);

            Assert.Contains("svcfg-send-result", row, StringComparison.Ordinal);
            Assert.Contains("Test send refused", visible, StringComparison.Ordinal);
            Assert.Contains("2026-09-09 03:20 UTC", visible, StringComparison.Ordinal);
            Assert.Contains("Mail not queued. Database Mail is stopped.", visible, StringComparison.Ordinal);
        }

        /// <summary>
        /// The negative control for the whole feature: with nothing read, the page still renders and
        /// promises nothing. "Not checked" is not "fine".
        /// </summary>
        [Fact]
        public async Task WithNothingRead_ThePickerOffersTheReadButtonAndClaimsNothing()
        {
            var html = await RenderAsync(_ => { });

            Assert.Contains("Read operators and check mail", html, StringComparison.Ordinal);
            Assert.Contains("Not checked", Visible(html), StringComparison.Ordinal);
            Assert.DoesNotContain("svcfg-verdict-works", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Send test notification", html, StringComparison.Ordinal);

            // The warning about what Apply does to the operator it records is not conditional: it is
            // true of every apply, and it is the reason picking a client's own operator is a decision.
            Assert.Contains("overwrites that operator's address on Apply", Visible(html), StringComparison.Ordinal);
        }

        /// <summary>
        /// THE RBAC HALF OF THE SEND-BUTTON GATE, rendered rather than linted. Under a browser-hosted
        /// host AppUserState goes through the real RBAC service instead of returning true for the
        /// desktop, the page's first gate refuses, and the whole picker - dropdown, badge and send
        /// button - is absent. The gate is asserted CLOSED first, so this cannot pass because the
        /// substitution silently failed to change anything.
        /// </summary>
        [Fact]
        public async Task WithoutRunScripts_ThePageRefuses_AndNoPickerOrSendButtonReachesTheBrowser()
        {
            var html = await RenderAsync(
                state => SeedPicker(state, DoneInstance, New2022Inventory(), New2022Rows()),
                browserHosted: true,
                assertAuthorized: false);

            Assert.DoesNotContain("svcfg-picker", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Send test notification", html, StringComparison.Ordinal);
            Assert.DoesNotContain("svcfg-verdict", html, StringComparison.Ordinal);

            // …and it is the refusal shell that rendered, not an empty page.
            Assert.DoesNotContain("Multi-Instance", html, StringComparison.Ordinal);
        }

        // ── Picker fixtures: the two rigs, as read on 2026-09-09 ──────────────────────────

        /// <summary>
        /// Seeds one instance through the SHIPPED load path, so the default operator is chosen by
        /// AgentMailChainProbe.DefaultOperator and the verdict by AgentMailChainProbe.Evaluate. The
        /// delegates complete synchronously, which is why blocking on the task here is safe.
        /// </summary>
        private static void SeedPicker(
            ServerConfigRunState state, string instance, OperatorInventory inventory, MailChainRows? rows)
        {
            state.LoadOperatorsAsync(
                new[] { instance },
                (_, _) => Task.FromResult(inventory),
                (_, op, _) =>
                {
                    var r = rows ?? new MailChainRows { ServerName = instance, OperatorName = op };
                    r.OperatorName = op;
                    return Task.FromResult(new MailChainReport(
                        r, AgentMailChainProbe.Evaluate(r, DateTime.Now), DateTime.Now));
                }).GetAwaiter().GetResult();
        }

        private static AgentOperatorRow OperatorRow(
            string name, int notifications, string address,
            int id = 1, int lastEmailDate = 0, int lastEmailTime = 0) => new()
        {
            Id = id,
            Name = name,
            Enabled = true,
            EmailAddress = address,
            EnabledAlertNotifications = notifications,
            LastEmailDate = lastEmailDate,
            LastEmailTime = lastEmailTime,
        };

        private static OperatorInventory New2022Inventory()
        {
            var inv = new OperatorInventory { ServerName = DoneInstance, FailSafeOperator = "DBA" };
            inv.Operators.Add(OperatorRow("DBA", 41, "sqlalerts@sqldba.org", id: 4,
                                          lastEmailDate: 20260905, lastEmailTime: 44504));
            inv.Operators.Add(OperatorRow("SQLDBA", 7, "alerts@sqldba.org", id: 1,
                                          lastEmailDate: 20260824, lastEmailTime: 82317));
            return inv;
        }

        private static MailChainRows New2022Rows()
        {
            var rows = new MailChainRows
            {
                ServerName = DoneInstance,
                OperatorName = "DBA",
                ServerNow = new DateTime(2026, 9, 9, 1, 45, 0),
                DatabaseMailXpsValue = 1,
                DatabaseMailXpsValueInUse = 1,
                MailQueueReceiveEnabled = false,
                AgentMailProfile = "DBA Mail Profile",
                AgentUseDatabaseMail = 1,
                IsSysadmin = true,
                LastFailed = new DateTime(2026, 7, 27, 18, 28, 30, 303),
                FailedCount = 20797,
                MatchedCount = 20821,
                LastEventLogError = new DateTime(2026, 7, 27, 18, 33, 22, 403),
                LastEventLogMessage =
                    "The mail could not be sent to the recipients because of the mail server failure.",
                Operator = OperatorRow("DBA", 41, "sqlalerts@sqldba.org", id: 4,
                                       lastEmailDate: 20260905, lastEmailTime: 44504),
            };

            rows.Profiles.Add(new MailProfileRow(
                1, "DBA Mail Profile", "DBA_Email_Account", "smtp.office365.com", 587, "sqlalerts@sqldba.org"));

            return rows;
        }

        private static OperatorInventory Old2017Inventory()
        {
            var inv = new OperatorInventory { ServerName = DoneInstance, FailSafeOperator = "DBA" };
            inv.Operators.Add(OperatorRow("DBA", 99, "sqlalerts@sqldba.org", id: 3));
            inv.Operators.Add(OperatorRow("SQLDBA", 7, "alerts@sqldba.org", id: 1));
            return inv;
        }

        private static MailChainRows Old2017Rows() => new()
        {
            ServerName = DoneInstance,
            OperatorName = "DBA",
            ServerNow = new DateTime(2026, 9, 9, 1, 45, 0),
            DatabaseMailXpsValue = 1,
            DatabaseMailXpsValueInUse = 1,
            MailQueueReceiveEnabled = true,
            AgentUseDatabaseMail = 0,
            IsSysadmin = true,
            Operator = OperatorRow("DBA", 99, "sqlalerts@sqldba.org", id: 3),
        };

        /// <summary>The &lt;div class="svcfg-picker-row"&gt; containing a given string. "" when absent.
        /// A picker row holds no nested div, so the non-greedy match is the whole row.</summary>
        private static string PickerRowContaining(string html, string needle)
        {
            foreach (Match div in Regex.Matches(html, "<div class=\"svcfg-picker-row\">.*?</div>",
                                                RegexOptions.Singleline))
                if (div.Value.Contains(needle, StringComparison.Ordinal)) return div.Value;
            return string.Empty;
        }

        // ── The harness ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Renders the real page against a ServerConfigRunState seeded by <paramref name="seed"/>.
        /// The state is resolved from the SAME scope the renderer uses, so it is the object the
        /// page's @inject receives — a second instance would render an empty table and every
        /// assertion above would be vacuous.
        /// </summary>
        private async Task<string> RenderAsync(
            Action<ServerConfigRunState> seed, bool browserHosted = false, bool assertAuthorized = true)
        {
            var settingsDir = Path.Combine(Path.GetTempPath(), "sqlt-svcfg-render-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(settingsDir);

            try
            {
                var services = BuildGraph(settingsDir, out var bundle);

                // The one substitution a caller can ask for: a BROWSER-hosted answer, which sends
                // AppUserState.IsAuthorized through the real RBAC service instead of returning true
                // for the single-user desktop. It is how the run_scripts half of a gate becomes
                // measurable by render rather than only by lint.
                if (browserHosted)
                    Replace(services, ServiceDescriptor.Singleton(HostEnvironmentInfo.BrowserHosted));

                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var sp = scope.ServiceProvider;

                // Every gate the page puts ABOVE its markup, asserted rather than assumed. Each of
                // these failing would render a refusal shell over which the assertions in the facts
                // would pass for the wrong reason.
                Assert.Equal(assertAuthorized, sp.GetRequiredService<AppUserState>().IsAuthorized("run_scripts"));

                if (!assertAuthorized)
                {
                    // The page returns its AccessDenied shell before any of the markup below exists,
                    // so the remaining gates are not what this render is measuring.
                    seed(sp.GetRequiredService<ServerConfigRunState>());

                    var refusalLoggerFactory = sp.GetRequiredService<ILoggerFactory>();
                    await using var refusalRenderer = new HtmlRenderer(sp, refusalLoggerFactory);

                    return await refusalRenderer.Dispatcher.InvokeAsync(async () =>
                    {
                        var refused = await refusalRenderer.RenderComponentAsync<CapturingHost>(ParameterView.Empty);
                        await refused.QuiescenceTask;
                        return refused.ToHtmlString();
                    });
                }

                Assert.True(sp.GetRequiredService<IRemediationCapability>().IsGranted,
                    "the substituted licence did not grant remediation, so the page would render its "
                    + "'is not licensed on this install' shell. "
                    + ServerConfigSuiteGate.DescribeRefusal(bundle));

                Assert.True(sp.GetRequiredService<IUserSettingsService>().GetNoPantsMode(),
                    "No-Pants Mode is off, so the page would render the 'No-Pants Mode required' shell");

                Assert.True(sp.GetRequiredService<ServerConfigScriptService>().ScriptExists,
                    "ConfigScripts\\Server Configuration and Hardening.sql is not in the test output, "
                    + "so the page renders 'The configuration script is not present in this build' "
                    + "and the WHOLE Multi-Instance section — every subject of this file — is absent");

                seed(sp.GetRequiredService<ServerConfigRunState>());

                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                await using var renderer = new HtmlRenderer(sp, loggerFactory);

                return await renderer.Dispatcher.InvokeAsync(async () =>
                {
                    var root = await renderer.RenderComponentAsync<CapturingHost>(ParameterView.Empty);
                    await root.QuiescenceTask;
                    return root.ToHtmlString();
                });
            }
            finally
            {
                try { Directory.Delete(settingsDir, recursive: true); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// The shared DI graph, built the same way Gated/RemediationBatchApplyRenderTests builds
        /// its own: the REAL AddSharedServices composition with four substitutions, each named in
        /// the file header.
        /// </summary>
        private static ServiceCollection BuildGraph(string settingsDir, out FakeBundleAccessor bundle)
        {
            var services = new ServiceCollection();
            // Fully qualified: SQLTriage.Data declares its own LogLevel, so the bare name is
            // ambiguous here (the sibling render test qualifies it for the same reason).
            services.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.None));
            var configuration = new ConfigurationBuilder().Build();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSharedServices(configuration);

            // ⚠ THE STORE SEAM IS NOT OPTIONAL. ServerConnectionManager's default path is the BUILD
            // OUTPUT's Config\server-connections.json, shared by every test in the assembly — and
            // this page's OnInitialized enumerates it to build the instance picker.
            var manager = new ServerConnectionManager(
                NullLogger<ServerConnectionManager>.Instance,
                seats: null,
                connectionsFilePath: Path.Combine(settingsDir, "server-connections.json"));
            manager.AddConnection(new ServerConnection
            {
                ServerNames = DoneInstance,
                UseWindowsAuthentication = true,
            });
            Replace(services, ServiceDescriptor.Singleton(manager));
            Replace(services, ServiceDescriptor.Singleton<IServerConnectionManager>(_ => manager));

            bundle = new FakeBundleAccessor
            {
                IsUnlocked = true,
                Tier = Tier.Full,
                Features = new BundleFeatures(
                    RagEnabled: false, SpBlitzImport: true, FullCorpus: true,
                    PermittedCheckIds: Array.Empty<int>(),
                    Remediation: true, RemediationCreditsPerServer: 10),
            };
            var accessor = bundle;
            Replace(services, ServiceDescriptor.Singleton<IBundleAccessor>(_ => accessor));

            // Keeps the operator's real %APPDATA%\SQLTriage out of the graph (2026-08-04 lesson):
            // UserSettingsService's constructor REFUSES the default path under a test host.
            var settings = new UserSettingsService(Path.Combine(settingsDir, "user-settings.json"));
            settings.SetNoPantsMode(true);
            Replace(services, ServiceDescriptor.Singleton(settings));
            Replace(services, ServiceDescriptor.Singleton<IUserSettingsService>(_ => settings));
            Replace(services, ServiceDescriptor.Singleton(new InstallProvenanceService(settingsDir)));

            Replace(services, ServiceDescriptor.Singleton<IJSRuntime>(_ => new NoJsRuntime()));

            return services;
        }

        private static void Replace(IServiceCollection services, ServiceDescriptor descriptor)
        {
            for (int i = services.Count - 1; i >= 0; i--)
                if (services[i].ServiceType == descriptor.ServiceType) services.RemoveAt(i);
            services.Add(descriptor);
        }

        private static string NewExportFile(string lane)
        {
            var dir = Path.Combine(Path.GetTempPath(), "sqlt-svcfg-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{DoneInstance}_20260908-104500_changecontrol{(lane == "apply" ? "-applied" : "")}.txt");
            File.WriteAllText(path, "-- change control plan, 35 row(s)\r\nEXEC sp_configure 'max degree of parallelism', 8;\r\n");
            return path;
        }

        private static void TryDelete(string path)
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>The &lt;tr&gt; containing a given string. "" when absent.</summary>
        private static string RowHtmlContaining(string html, string needle)
        {
            foreach (Match tr in Regex.Matches(html, @"<tr\b[^>]*>.*?</tr>",
                                               RegexOptions.Singleline | RegexOptions.IgnoreCase))
                if (tr.Value.Contains(needle, StringComparison.Ordinal)) return tr.Value;
            return string.Empty;
        }

        /// <summary>Tags out, entities decoded, whitespace collapsed: the words a person reads.</summary>
        private static string Visible(string html)
            => Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();

        // ── Doubles ───────────────────────────────────────────────────────────────────────

        private sealed class CapturingHost : ComponentBase
        {
            protected override void BuildRenderTree(RenderTreeBuilder builder)
            {
                builder.OpenComponent<SQLTriage.Pages.ServerConfiguration>(0);
                builder.CloseComponent();
            }
        }

        private sealed class NoJsRuntime : IJSRuntime
        {
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
                throw new NotSupportedException($"JS interop is not available in a static render ({identifier}).");

            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
                throw new NotSupportedException($"JS interop is not available in a static render ({identifier}).");
        }
    }
}
