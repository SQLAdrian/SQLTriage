/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationBatchApplyRenderTests — the RENDERED proof that the batch apply is ARMED
 * (plan item 3.1 UI, Phase-3 concentrate lane, 2026-09-01).
 *
 * WHY IT EXISTS, AND WHAT ONLY IT CAN SEE. RemediationBatchPreviewUiTests proves, by reading source
 * text, that every apply reach on this surface sits inside a .rbatch-armed / .rbatch-rollback-armed
 * dialog. That is a claim about WHERE the markup is. It is not a claim about WHEN it renders: a
 * dialog whose `@if (_batchArmOpen ...)` condition was deleted would keep its class, keep its
 * position, and pass every source scan in that file — while shipping an apply button to every
 * operator who opened the section. Only a render can tell those two apart, and the house lesson
 * [[render-the-ui-before-sweeping]] is that pixels found what forty agents and two gates missed.
 *
 * So this file renders the REAL page through the REAL Blazor renderer TWICE:
 *
 *   1. DIALOG SHUT — a populated batch reading, nothing armed. Assertion: ZERO apply-shaped
 *      controls in the html an operator's browser receives.
 *   2. DIALOG OPEN — the same page with the approval dialog armed. Assertion: the apply control is
 *      there, the two credit numbers are there APART, and every item's reversibility answer is
 *      visible BEFORE the tick.
 *
 * The pair is the point. (1) alone would pass on a page that had no apply at all; (2) alone would
 * pass on a page that showed the apply always. Together they say the control appears only in the
 * armed state, which is what "armed" means.
 *
 * ⚠ THE SUBSTITUTIONS, STATED PLAINLY, SO NO CLAIM HERE IS OVER-READ. They are the same four
 * Gated/RemediationBatchRenderTests makes, for the same reasons, and they are repeated rather than
 * cross-referenced because a reader of an assertion needs them beside it:
 *
 *   1. THE LICENCE SOURCE IS A FAKE. FakeBundleAccessor (Tier.Full, Features.Remediation = true)
 *      stands in for a signed bundle file. The GATE LOGIC is the real
 *      BundleBackedRemediationCapability over the real ServerConfigSuiteGate; the SIGNATURE check on
 *      a minted bundle is not exercised here and stays untested by this file.
 *
 *   2. THE FINDINGS ARE SYNTHETIC. CheckResult objects this test composes, not a captured audit
 *      run. The selector half is still driven by the SHIPPED CheckResolutionLookup over the real
 *      template store, so the join under the markup is real.
 *
 *   3. THE READINGS AND RESULTS ARE PLANTED. A real batch apply needs a live SQL Server. The
 *      BatchPreviewResult and BatchApplyResult here are objects this test builds and sets on the
 *      page's own fields. Everything DOWNSTREAM of those fields — every branch, every sentence,
 *      every control — is the page's real markup. What this file therefore proves is the RENDERING
 *      of a given engine answer, never that the engine produces that answer. The engine's own
 *      answers are proved by the driver tests and, live, by the lane's end-to-end proof.
 *
 *   4. NO SERVER IS CONTACTED, and no apply is executed. The page renders once with no server
 *      selected (so OnInitializedAsync never reaches ServerSizingService), then the server, the
 *      findings and the planted state are set and the page is re-rendered. No event is dispatched,
 *      so no handler runs.
 *
 * ⚠ WHY THIS IS IN Gated/. It reaches SQLTriage.Pages.Remediation, which buildprofile.targets
 * Content-Removes from a COMMUNITY build. The Gated\**\*.cs glob removes this folder from the
 * community test build, so this runs only where the page exists.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    public sealed class RemediationBatchApplyRenderTests
    {
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Server = "SQLT-APPLY-RENDER-FIXTURE";

        private readonly ITestOutputHelper _out;
        public RemediationBatchApplyRenderTests(ITestOutputHelper output) => _out = output;

        // ── 1. DISARMED: nothing that applies reaches the browser ────────────────

        [Fact]
        public async Task WithTheApprovalDialogSHUT_TheRenderedSurfaceCarriesNoApplyControl()
        {
            var section = SectionHtml(await RenderAsync(armed: false));
            var visible = Visible(section);
            _out.WriteLine(visible);

            // It rendered at all. Without this the assertions below could pass over an empty page —
            // a refused gate, a thrown initialiser — which is the failure mode that makes a render
            // test worthless.
            Assert.False(string.IsNullOrWhiteSpace(section), "the batch section did not render");
            Assert.Contains("Batch preview:", section, StringComparison.Ordinal);

            // The scan has something to scan: the Preview button and the Continue button exist.
            var controls = RenderedControls(section);
            Assert.True(controls.Count >= 2,
                "Expected the disarmed surface to carry its preview and continue controls; found "
                + controls.Count + ". A control scan that finds none reports every page clean.");

            var applyShaped = controls.Where(LooksLikeApply).ToList();
            Assert.True(applyShaped.Count == 0,
                "With the approval dialog shut, the rendered batch section must carry no apply-shaped "
                + "control. Found: " + string.Join(" | ", applyShaped));

            // And the copy that promises it, rendered rather than read from source.
            Assert.Contains("Nothing is applied from this list", visible, StringComparison.Ordinal);
        }

        // ── 2. ARMED: the dialog is where the apply lives, and it shows the facts ─

        [Fact]
        public async Task WithTheApprovalDialogOPEN_TheApplyAppears_WithBothTotalsAndEveryReversibilityAnswer()
        {
            var html = await RenderAsync(armed: true);
            var section = SectionHtml(html);
            var visible = Visible(section);
            _out.WriteLine(visible);

            // ── The armed dialog rendered, and the apply control is in it.
            Assert.Contains("rbatch-armed", section, StringComparison.Ordinal);
            var dialog = ElementHtml(section, "rbatch-armed");
            Assert.False(string.IsNullOrWhiteSpace(dialog), "the armed approval dialog did not render");

            var applyShapedInDialog = RenderedControls(dialog).Where(LooksLikeApply).ToList();
            Assert.True(applyShapedInDialog.Count > 0,
                "The armed dialog must carry the apply control. It rendered none, so the pair of "
                + "assertions in this file would both pass on a page with no apply at all.");

            // ── EVERY apply-shaped control on the whole surface is inside that dialog. This is the
            //    rendered form of the source-level confinement, and it is the assertion that makes
            //    "armed" mean something: the control exists only where the tick is.
            var loose = RenderedControls(WithoutElement(section, "rbatch-armed")).Where(LooksLikeApply).ToList();
            Assert.True(loose.Count == 0,
                "Every apply-shaped control must sit inside the armed dialog. Found outside it: "
                + string.Join(" | ", loose));

            // ── The arming gesture itself reached the browser. A dialog with a button and no tick
            //    is a one-click apply wearing a warning.
            Assert.Contains("type=\"checkbox\"", dialog, StringComparison.Ordinal);
            Assert.Contains("I approve these", Visible(dialog), StringComparison.Ordinal);

            // ── TWO NUMBERS, APART, ON THE APPROVAL SCREEN. The planted reading prices three items
            //    at 1 each with one already at target, so these must be 3 and 2 — different
            //    numbers, which is what makes "shown apart" an assertion rather than a coincidence.
            var dialogVisible = Visible(dialog);
            Assert.Matches(new Regex(@"Would reserve\s*3\b"), dialogVisible);
            Assert.Matches(new Regex(@"Projected commit\s*2\b"), dialogVisible);

            // ── REVERSIBILITY, PER ITEM, BEFORE THE TICK. All three answers, because bool? has
            //    three and the planted reading carries one of each. An "unknown" rendered as either
            //    of the other two is the defect this asserts against.
            Assert.Contains("Can be undone", dialogVisible, StringComparison.Ordinal);
            Assert.Contains("Cannot be undone", dialogVisible, StringComparison.Ordinal);
            Assert.Contains("Undo unknown", dialogVisible, StringComparison.Ordinal);

            // The REASON travels with the answer, not just the word. This is the P2 fix-round
            // blocker-3 shape: a state word on its own tells a person nothing they can act on. The
            // marker is the producer's own constant, so a re-wording of the sentence cannot leave
            // this asserting text the app no longer emits.
            Assert.Contains(RemediationRollbackProse.NoRollbackMarker, dialogVisible, StringComparison.Ordinal);
            Assert.Contains(RemediationRollbackProse.ReversibleMarker, dialogVisible, StringComparison.Ordinal);

            // ── The no-change flag is on the approval screen too, so the operator sees which items
            //    are about to cost nothing at the moment they commit rather than only in the reading.
            Assert.Contains("already at the target", dialogVisible, StringComparison.Ordinal);
        }

        // ── 3. THE RESULT SURFACE: honest totals, plain-word rollback, stop reason ─

        [Fact]
        public async Task TheResultSurface_NamesTheCommittedTotal_TheStopReason_AndEveryRollbackInWords()
        {
            var section = SectionHtml(await RenderAsync(armed: false, result: PlantedApplyResult()));
            var visible = Visible(section);
            _out.WriteLine(visible);

            Assert.Contains("Batch result", visible, StringComparison.Ordinal);

            // ── THE HONEST TOTAL. The planted result reserves 3 and commits 1: one applied item,
            //    one no-op that refunded, one never attempted. A surface that summed the
            //    reservations would print 3 as the charge — the exact over-report spike S1 §3.4
            //    proved live, where a 3-credit batch billed 2.
            Assert.Matches(new Regex(@"Charged\s*1\b"), visible);
            Assert.Matches(new Regex(@"Reserved\s*3\b"), visible);
            Assert.Contains("2 refunded", visible, StringComparison.Ordinal);

            // ── THE STOP REASON, not just the fact of stopping.
            Assert.Contains("The batch stopped early", visible, StringComparison.Ordinal);
            Assert.Contains("CTFP could not run", visible, StringComparison.Ordinal);

            // ── PER ITEM, IN WORDS. Never a bare enum member.
            Assert.Contains("applied and verified", visible, StringComparison.Ordinal);
            Assert.Contains("nothing to do - already at the target", visible, StringComparison.Ordinal);
            Assert.Contains("not attempted - the batch stopped at OPTIMIZEFORADHOC", visible, StringComparison.Ordinal);
            Assert.DoesNotContain("AppliedVerified", visible, StringComparison.Ordinal);

            // ── AND THE REFUSED ITEM READS AS WORDS. The fixture carries one item refused at the
            //    credit gate specifically so the guard below has something to catch.
            Assert.Contains("refused before anything ran - there are not enough change credits",
                visible, StringComparison.Ordinal);

            // ── THE WIDENED GUARD (fix round 1, gate blocker 3). NOT "does the one string I
            //    remembered appear" — every member of the RemediationRefusal enum is enumerated from
            //    the type itself, so a member added later is covered without anyone editing this
            //    line. The old render printed `refused at gate (NotApproved)`; a guard naming only
            //    that one string would have gone green the day the wording moved to a different
            //    member.
            AssertNoRefusalEnumTokens(section);

            // ── THE ROLLBACK SENTENCE SURVIVES TO THE HTML. This is the render-level half of the
            //    P2 fix-round blocker 3: the executor writes a real reason, and the page used to
            //    drop it. A sentinel is planted in RollbackReason and must appear verbatim.
            Assert.Contains(RollbackSentinel, visible, StringComparison.Ordinal);

            // ── THE UNDO OFFER exists, because one item is still changed on the server — and it is
            //    an offer to open a second confirmation, not an apply.
            var controls = RenderedControls(section);
            Assert.Contains(controls, c => c.Contains("Put these changes back", StringComparison.Ordinal));
            Assert.True(controls.Where(LooksLikeApply).ToList().Count == 0,
                "The result surface is not an armed dialog, so nothing on it may read as an apply. "
                + "Found: " + string.Join(" | ", controls.Where(LooksLikeApply)));
        }

        [Fact]
        public async Task AResultThatLeftNothingChanged_OffersNoUndo_AndSaysWhy()
        {
            // The negative control for the offer. A "put it back" button over a batch that changed
            // nothing would send inverse statements to a server for no reason, and would tell the
            // operator something had been changed.
            var visible = Visible(SectionHtml(await RenderAsync(armed: false, result: PlantedNoOpOnlyResult())));

            Assert.Contains("Nothing was left changed on this server", visible, StringComparison.Ordinal);
            Assert.DoesNotContain("Put these changes back", visible, StringComparison.Ordinal);
        }

        // ── 4. THE REVERSAL RESULT: five states, five distinct sentences ──────────

        [Fact]
        public async Task TheReversalResult_RendersFiveHonestStates_AndNeverReadsAsACleanUndo()
        {
            var reversalSection = SectionHtml(
                await RenderAsync(armed: false, result: PlantedApplyResult(), rollback: PlantedRollbackResult()));
            var visible = Visible(reversalSection);
            _out.WriteLine(visible);

            Assert.Contains("Reversal result", visible, StringComparison.Ordinal);

            // The same widened guard over the reversal surface, which renders the result rows again
            // beneath it (fix round 1, gate blocker 3).
            AssertNoRefusalEnumTokens(reversalSection);

            // Each of the five states gets its OWN words. Confirmed is the only one that may read
            // as a success; the other four each mean the server may not be back.
            Assert.Contains("put back, confirmed", visible, StringComparison.Ordinal);
            Assert.Contains("reversal ran, not confirmed", visible, StringComparison.Ordinal);
            Assert.Contains("reversal failed", visible, StringComparison.Ordinal);
            Assert.Contains("no reversal available", visible, StringComparison.Ordinal);

            // ⚠ THE ASSERTION THAT MATTERS. AllConfirmed is false on this fixture, so the surface
            // must NOT print the clean-undo sentence. A batch containing one irreversible fix was
            // not fully undone, and an operator reading "everything is back" over that would stop
            // checking the server.
            Assert.DoesNotContain("Every change was put back and confirmed", visible, StringComparison.Ordinal);
            Assert.Contains("Not everything came back", visible, StringComparison.Ordinal);

            // ⚠ AND THE SHORTFALL IS COUNTED WHOLE. The fixture has four items: one Confirmed, one
            // Unconfirmed, one Failed, one NotAvailable. The headline must say THREE did not come
            // back confirmed, not the one that had no reversal available — an operator told "1 of 4"
            // would go looking for one problem instead of three. The no-reversal subset is named
            // after it, because those two facts need different actions.
            Assert.Matches(new Regex(@"\b3 of 4\s*did not come back confirmed"), visible);
            Assert.Contains("1 of those had no reversal available at all", visible, StringComparison.Ordinal);

            // The NO ROLLBACK reason is REPORTED, never silently skipped — the contract's own rule.
            Assert.Contains(RemediationRollbackProse.NoRollbackMarker, visible, StringComparison.Ordinal);
            Assert.Contains(NoRollbackSentinel, visible, StringComparison.Ordinal);
        }

        // ── 5. THE SENSITIVE EXCLUSION, BEHAVIOURALLY ────────────────────────────

        /// <summary>
        /// The name of a Sensitive Configuration fix promoted through the REAL corpus path.
        ///
        /// <para>⚠ WHY THIS FIXTURE HAS TO BE BUILT AT ALL, stated so the test is not over-read.
        /// All four SHIPPED Sensitive templates (ADDMISSINGINDEX, BACKUPDATABASENOW, CHECKDBNOW,
        /// DELETEEXTRAJOB) are <c>Kind = Transactable</c>, and BatchCandidateSelector already
        /// excludes everything that is not <c>Configuration</c> — so against today's shipped set the
        /// page's Sensitive clause has nothing to do, and a test over shipped templates alone would
        /// pass without exercising it. The reachable path is a CORPUS-fed fix: RemediationTemplateStore
        /// parses <c>risk_class: sensitive</c> out of corpus yaml, and the plan's own §4 names two
        /// corpus checks (GAPFIL-00930, VA-TRUSTWORTHY-DB) ruled one-at-a-time for exactly that
        /// reason. So the fixture promotes one the way the app does, through LoadCorpusTemplates,
        /// with zero app-code change.</para>
        /// </summary>
        private const string SensitiveKey = "SQLT-TEST-SENSITIVE-CONFIG-FIX";

        [Fact]
        public async Task ASensitiveFix_IsNotTickable_AndIsDroppedFromTheBatchEvenIfItIsTicked()
        {
            var (html, page) = await RenderWithSensitiveCandidateAsync();
            var section = SectionHtml(html);
            var visible = Visible(section);
            _out.WriteLine(visible);

            // ── The exclusion has something to exclude. Without this the assertions below would
            //    pass over a page that simply never offered the fix, which proves nothing about the
            //    clause under test.
            Assert.Contains(SensitiveKey, section, StringComparison.Ordinal);

            // ── Its tickbox is disabled, and the row says WHY in the operator's words — naming the
            //    ruling (one at a time) and where to go instead, not just refusing.
            var row = RowHtmlContaining(section, SensitiveKey);
            Assert.False(string.IsNullOrWhiteSpace(row), "the sensitive candidate did not render a row");
            Assert.Contains("disabled", row, StringComparison.Ordinal);
            Assert.Contains("Marked sensitive, so it is applied on its own", Visible(row), StringComparison.Ordinal);

            // ── BELT AND BRACES. Force the tick past the disabled attribute — which is a rendering,
            //    not a control, and a crafted request or a stale selection can carry one — and the
            //    page's own selection method must still drop it. This is the assertion that would
            //    catch a Sensitive fix reaching ApplyBatchAsync.
            var type = page.GetType();

            // ⚠ THE PRECONDITION, ASSERTED RATHER THAN ASSUMED. SelectedBatchItems drops an item for
            // three reasons, and only one of them is under test. If this row needed an
            // operator-typed target it would be dropped for THAT reason and the assertion below
            // would pass with the Sensitive clause deleted — which is exactly what happened to the
            // first version of this fixture, and was caught by mutation. Fail loudly here instead of
            // green-lighting an unreachable branch.
            var fixRow = type.GetMethod("RowFor", F)!.Invoke(page, new object[] { SensitiveKey });
            Assert.NotNull(fixRow);
            Assert.False((bool)type.GetMethod("NeedsATarget", BindingFlags.Static | BindingFlags.NonPublic)!
                                   .Invoke(null, new[] { fixRow })!,
                "the fixture needs an operator-typed target, so it would be dropped for that reason "
                + "and this test would not exercise the Sensitive clause at all.");

            var selected = (HashSet<string>)type.GetField("_batchSelected", F)!.GetValue(page)!;
            selected.Add(SensitiveKey);

            var candidates = type.GetMethod("BatchCandidates", F)!.Invoke(page, null)!;
            var items = (System.Collections.IList)type
                .GetMethod("SelectedBatchItems", F)!.Invoke(page, new[] { candidates })!;

            var keys = items.Cast<BatchRemediationItem>().Select(i => i.TemplateKey).ToList();
            _out.WriteLine("selected keys after force-ticking the sensitive fix: "
                + (keys.Count == 0 ? "(none)" : string.Join(", ", keys)));
            Assert.DoesNotContain(SensitiveKey, keys);

            // ── And the driver agrees, from its own predicate rather than this test's opinion. If
            //    these two ever disagreed, the visible one would be the tickbox.
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            Assert.False(BatchRemediationDriver.IsBatchable(SensitiveTemplateAsRegistered(page)),
                "the page dropped the item for some reason OTHER than the driver's own IsBatchable, "
                + "which means the two walls are not the same rule.");
            Assert.NotNull(store);   // the shipped store still loads; the promotion did not corrupt it
        }

        /// <summary>The promoted template as the page's own store holds it.</summary>
        private static RemediationTemplate? SensitiveTemplateAsRegistered(ComponentBase page)
        {
            var storeField = page.GetType().GetProperty("Templates",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var store = (RemediationTemplateStore)storeField!.GetValue(page)!;
            return store.TryGet(SensitiveKey);
        }

        /// <summary>The &lt;tr&gt; containing a given string. "" when absent.</summary>
        private static string RowHtmlContaining(string html, string needle)
        {
            foreach (Match tr in Regex.Matches(html, @"<tr\b[^>]*>.*?</tr>",
                                               RegexOptions.Singleline | RegexOptions.IgnoreCase))
                if (tr.Value.Contains(needle, StringComparison.Ordinal)) return tr.Value;
            return string.Empty;
        }

        // ── The harness ──────────────────────────────────────────────────────────

        /// <summary>
        /// Renders the real page with the batch section open, a populated reading, and whichever
        /// Phase-3 state the caller asks for. No event is dispatched: the fields are set directly,
        /// so no handler runs and nothing is applied.
        /// </summary>
        private async Task<string> RenderAsync(
            bool armed, BatchApplyResult? result = null, BatchRollbackResult? rollback = null)
        {
            var settingsDir = Path.Combine(Path.GetTempPath(), "sqlt-rbatch-apply-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(settingsDir);

            var services = BuildGraph(settingsDir, out var bundle);
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var sp = scope.ServiceProvider;

            Assert.True(sp.GetRequiredService<IRemediationCapability>().IsGranted,
                "the substituted licence did not grant remediation, so the page would render its "
                + "refusal shell and every assertion here would be over the wrong markup. "
                + ServerConfigSuiteGate.DescribeRefusal(bundle));

            var quick = sp.GetRequiredService<QuickCheckStateService>();
            var context = sp.GetRequiredService<IServerContextService>();
            var connections = sp.GetRequiredService<ServerConnectionManager>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            await using var renderer = new HtmlRenderer(sp, loggerFactory);

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                ComponentBase? page = null;
                var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["OnCaptured"] = (Action<ComponentBase>)(c => page = c),
                });

                var root = await renderer.RenderComponentAsync<CapturingHost>(parameters);
                Assert.NotNull(page);
                await root.QuiescenceTask;

                var id = connections.GetConnections().First().Id;
                var outcome = await context.SetServerAsync(id, ConnectionRetargetGrant.Establish);
                Assert.True(outcome.Applied,
                    "the fixture server was not selected, so the batch section would render its "
                    + "'select a server' shell");
                quick.Results = Findings();
                quick.HasRun = true;

                var type = page!.GetType();
                type.GetField("_batchOpen", F)!.SetValue(page, true);
                type.GetField("_batchPreview", F)!.SetValue(page, PlantedReading());

                // The three keys in the planted reading are ticked, so SelectedBatchItems returns a
                // non-empty set and the approval dialog has something to price. Only MAXDOP has a
                // page row it can build parameters from — the count in the heading is that set's,
                // not the reading's, and the assertions above deliberately never depend on it.
                var selected = (HashSet<string>)type.GetField("_batchSelected", F)!.GetValue(page)!;
                foreach (var key in new[] { "MAXDOP", "OPTIMIZEFORADHOC", "CTFP" }) selected.Add(key);

                type.GetField("_batchArmOpen", F)!.SetValue(page, armed);
                if (result is not null) type.GetField("_batchApply", F)!.SetValue(page, result);
                if (rollback is not null) type.GetField("_batchRollback", F)!.SetValue(page, rollback);

                typeof(ComponentBase).GetMethod("StateHasChanged", F)!.Invoke(page, null);
                await root.QuiescenceTask;

                return root.ToHtmlString();
            });
        }

        /// <summary>
        /// Renders the page with ONE extra candidate: a Sensitive Configuration fix promoted through
        /// the app's own corpus path, so the page's Sensitive clause has something to act on.
        /// Returns the html and the page instance, because this test asserts on both.
        /// </summary>
        private async Task<(string Html, ComponentBase Page)> RenderWithSensitiveCandidateAsync()
        {
            var settingsDir = Path.Combine(Path.GetTempPath(), "sqlt-rbatch-sensitive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(settingsDir);

            var services = BuildGraph(settingsDir, out var bundle);
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var sp = scope.ServiceProvider;

            Assert.True(sp.GetRequiredService<IRemediationCapability>().IsGranted,
                ServerConfigSuiteGate.DescribeRefusal(bundle));

            // Seed the corpus BEFORE the first render. OnInitializedAsync calls
            // LoadCorpusTemplates(CheckRepo.Checks) itself, and that method drops its own prior
            // additions first — so promoting the template directly on the store here would be
            // undone by the page's own initialisation. Going through the repository is the route
            // the app actually takes.
            sp.GetRequiredService<CheckRepositoryService>().GetAllChecks().Add(SensitiveCorpusCheck());

            var quick = sp.GetRequiredService<QuickCheckStateService>();
            var context = sp.GetRequiredService<IServerContextService>();
            var connections = sp.GetRequiredService<ServerConnectionManager>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            await using var renderer = new HtmlRenderer(sp, loggerFactory);

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                ComponentBase? page = null;
                var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["OnCaptured"] = (Action<ComponentBase>)(c => page = c),
                });

                var root = await renderer.RenderComponentAsync<CapturingHost>(parameters);
                Assert.NotNull(page);
                await root.QuiescenceTask;

                var templates = sp.GetRequiredService<RemediationTemplateStore>();
                Assert.True(templates.IsRegistered(SensitiveKey),
                    "the corpus promotion did not register, so this test would prove nothing about "
                    + "the Sensitive clause — it would prove the fix was never offered.");
                Assert.Equal(RemediationRiskClass.Sensitive, templates.TryGet(SensitiveKey)!.RiskClass);

                var id = connections.GetConnections().First().Id;
                var outcome = await context.SetServerAsync(id, ConnectionRetargetGrant.Establish);
                Assert.True(outcome.Applied, "the fixture server was not selected");
                quick.Results = new List<CheckResult> { Finding(SensitiveKey) };
                quick.HasRun = true;

                page!.GetType().GetField("_batchOpen", F)!.SetValue(page, true);
                typeof(ComponentBase).GetMethod("StateHasChanged", F)!.Invoke(page, null);
                await root.QuiescenceTask;

                return (root.ToHtmlString(), page!);
            });
        }

        /// <summary>
        /// A corpus check declaring a one-click sp_configure fix at risk class SENSITIVE. Shaped
        /// exactly like the Phase-2 live harness's promotion, so it travels the same parser and the
        /// same registration path a real corpus entry does.
        /// </summary>
        private static SqlCheck SensitiveCorpusCheck() => new()
        {
            Id = SensitiveKey,
            Name = "A sensitive one-click configuration fix (batch-exclusion fixture)",
            Remediation = new CheckRemediation
            {
                AutoFixable = true,
                RiskClass = "sensitive",
                Reversible = true,
                CmdletOrTemplate = "sp_configure 'min server memory (MB)'",
                Operation = new CheckRemediationOperation
                {
                    OpKind = "sp_configure",
                    ConfigName = "min server memory (MB)",
                    AdvancedOption = true,
                    // ⚠ value_FIXED, NOT value_param, AND THAT IS THE WHOLE POINT OF THE FIXTURE.
                    //
                    // The first version of this check used ValueParam, and the test passed — while
                    // proving nothing. SelectedBatchItems drops an item for THREE reasons: it is
                    // Sensitive, its page row is missing, or the row needs an operator-typed target
                    // it does not have. A ValueParam template with no recommendation to pre-fill
                    // trips the THIRD one, so the item was dropped for the wrong reason and the
                    // Sensitive clause was never reached. Measured, not reasoned: with the Sensitive
                    // clause disabled (`if (false && ...)`) the test stayed GREEN.
                    //
                    // A fixed target makes NeedsATarget return false at its ValueFixed check, so the
                    // ONLY remaining reason this item can be dropped is the one under test. The
                    // mutation now turns it red, which is what makes the assertion a measurement.
                    ValueFixed = 1024,
                },
            },
        };

        private ServiceCollection BuildGraph(string settingsDir, out FakeBundleAccessor bundle)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.None));
            var configuration = new ConfigurationBuilder().Build();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSharedServices(configuration);

            // ⚠ THE STORE SEAM IS NOT OPTIONAL. ServerConnectionManager's default path is the BUILD
            // OUTPUT's Config\server-connections.json, shared by every test in the assembly.
            var manager = new ServerConnectionManager(
                NullLogger<ServerConnectionManager>.Instance,
                seats: null,
                connectionsFilePath: Path.Combine(settingsDir, "server-connections.json"));
            manager.AddConnection(new ServerConnection
            {
                ServerNames = Server,
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

            var settings = new UserSettingsService(Path.Combine(settingsDir, "user-settings.json"));
            settings.SetNoPantsMode(true);
            Replace(services, ServiceDescriptor.Singleton(settings));
            Replace(services, ServiceDescriptor.Singleton<IUserSettingsService>(_ => settings));

            Replace(services, ServiceDescriptor.Singleton<IJSRuntime>(_ => new NoJsRuntime()));

            return services;
        }

        private static void Replace(IServiceCollection services, ServiceDescriptor descriptor)
        {
            for (int i = services.Count - 1; i >= 0; i--)
                if (services[i].ServiceType == descriptor.ServiceType) services.RemoveAt(i);
            services.Add(descriptor);
        }

        private static List<CheckResult> Findings() => new()
        {
            Finding("SQLT-BPCHK-00220-PARALLELISM-MAXDOP"),
            Finding("SQLT-CORE-TUNE-COST-THRESHOLD-FOR-PARALLELISM"),
            Finding("SQLT-VA-AD-HOC-QUERIES-OFF"),
        };

        private static CheckResult Finding(string checkId) => new()
        {
            CheckId = checkId,
            CheckName = checkId + " name",
            Category = "Configuration",
            Severity = "High",
            Passed = false,
            Message = "The setting is not at the recommended value.",
            InstanceName = Server,
        };

        /// <summary>
        /// One batch reading covering all three no-change states AND all three reversibility
        /// answers. Three items priced at 1 each with one already at target, so the two totals must
        /// render 3 and 2 — different numbers, which is what makes "shown apart" an assertion.
        /// </summary>
        private static BatchPreviewResult PlantedReading()
        {
            // The SHIPPED template store, so the keys are real templates and the option names in
            // the rendered sentence are the ones the executor would actually write.
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);

            BatchRemediationPreviewItem Item(string key, string? current, int target, bool? noChange,
                                             bool? canRollBack, string note)
            {
                var template = store.TryGet(key);
                Assert.NotNull(template);
                var configName = template!.Operation?.ConfigName;
                Assert.False(string.IsNullOrWhiteSpace(configName),
                    $"'{key}' carries no sp_configure option name, so this fixture cannot build the "
                    + "sentence the executor would emit for it.");

                return new BatchRemediationPreviewItem
                {
                    TemplateKey = key,
                    Price = 1,
                    IsNoChange = noChange,
                    Proposal = RemediationProposal.Previewed(new RemediationPreview
                    {
                        Succeeded = true,
                        CanRollBack = canRollBack,
                        ReversibilityNote = note,
                        // Built by the production formatter, not typed out here, so the panel shows
                        // the sentence the executor really emits.
                        WhatIfText = RemediationPreviewSentence.ConfigurationPreview(
                            Server, $"EXEC sp_configure '{configName}', {target}; RECONFIGURE;",
                            configName, current, target),
                    }),
                };
            }

            return new BatchPreviewResult
            {
                RunId = "apply-render-fixture-run-id",
                Items = new[]
                {
                    // The sentences are the PRODUCTION producer's, not typed out here, so a wording
                    // change in RemediationRollbackProse cannot leave this fixture asserting text
                    // the app no longer emits.
                    Item("MAXDOP", "0", 4, false, true,
                        RemediationRollbackProse.ForConfiguration(store.TryGet("MAXDOP"), "max degree of parallelism", 0).Sentence),
                    Item("OPTIMIZEFORADHOC", "1", 1, true, false,
                        RemediationRollbackProse.ForConfiguration(store.TryGet("OPTIMIZEFORADHOC"), "optimize for ad hoc workloads", null).Sentence),
                    Item("CTFP", null, 50, null, null, string.Empty),
                },
            };
        }

        /// <summary>A sentinel in the rollback reason, so its survival to the html is a measurement.</summary>
        private const string RollbackSentinel = "SENTINEL-ROLLBACK-REASON-REACHED-THE-BROWSER";

        private const string NoRollbackSentinel = "SENTINEL-NO-ROLLBACK-REASON";

        /// <summary>
        /// A batch that applied one item, no-opped one, and could not run the third — so it stopped.
        /// Reserved 3, committed 1: the numbers must differ, or "charged" and "reserved" could be
        /// the same field rendered twice and nobody would know.
        /// </summary>
        private static BatchApplyResult PlantedApplyResult() => new()
        {
            RunId = "apply-render-fixture-run-id",
            ServerName = Server,
            ApprovedBy = "render-fixture",
            Message = "The batch stopped after an item failed; later items were not attempted.",
            PricedTotal = 3,
            AvailableAtPreCheck = 10,
            TotalReserved = 3,
            TotalCommitted = 1,
            Stopped = true,
            StopReason = "CTFP could not run, so the remaining items were not attempted.",
            PerItem = new[]
            {
                new BatchApplyItemResult
                {
                    TemplateKey = "MAXDOP",
                    Attempted = true,
                    RollbackReason = RollbackSentinel,
                    Result = RemediationResult.Applied(RemediationOutcome.AppliedVerified,
                        "max degree of parallelism is now 4.", creditsCharged: 1, creditsCommitted: 1,
                        preChangeValue: 0),
                },
                new BatchApplyItemResult
                {
                    TemplateKey = "OPTIMIZEFORADHOC",
                    Attempted = true,
                    Result = RemediationResult.Applied(RemediationOutcome.NoOp,
                        "optimize for ad hoc workloads was already 1.", creditsCharged: 1, creditsCommitted: 0),
                },
                // A REAL GATE REFUSAL, attempted. It is here so the enum-token guard has bait: a
                // fixture with no refused item would let the guard pass over a page that still
                // printed the enum, which is the shape mutation 1 of the original lane caught.
                new BatchApplyItemResult
                {
                    TemplateKey = "BACKUPCOMPRESSION",
                    Attempted = true,
                    Result = RemediationResult.Refused(RemediationRefusal.InsufficientCredits,
                        "Insufficient change credits for this server."),
                },
                // NEVER ATTEMPTED, and therefore carrying NO refusal (fix round 1, gate blocker 3).
                // The driver produces this shape; planting the old Refused(NotApproved, ...) here
                // would have this file asserting a model the driver no longer emits.
                new BatchApplyItemResult
                {
                    TemplateKey = "CTFP",
                    Attempted = false,
                    Result = RemediationResult.NotAttempted(
                        RemediationRefusalProse.DescribeNotAttempted("OPTIMIZEFORADHOC")),
                },
            },
        };

        /// <summary>A batch where every item was already compliant: nothing to put back.</summary>
        private static BatchApplyResult PlantedNoOpOnlyResult() => new()
        {
            RunId = "apply-render-fixture-noop",
            ServerName = Server,
            Message = "Every item in the batch was attempted.",
            TotalReserved = 1,
            TotalCommitted = 0,
            PerItem = new[]
            {
                new BatchApplyItemResult
                {
                    TemplateKey = "MAXDOP",
                    Attempted = true,
                    Result = RemediationResult.Applied(RemediationOutcome.NoOp,
                        "max degree of parallelism was already 4.", creditsCharged: 1, creditsCommitted: 0),
                },
            },
        };

        /// <summary>
        /// A reversal carrying FOUR of the five states, including one that could not be undone at
        /// all — so AllConfirmed is false and the clean-undo sentence must not appear.
        /// </summary>
        private static BatchRollbackResult PlantedRollbackResult() => new()
        {
            RunId = "apply-render-fixture-run-id",
            ServerName = Server,
            PerItem = new[]
            {
                new BatchRollbackItemResult
                {
                    TemplateKey = "MAXDOP",
                    RollbackState = RemediationRollbackState.Confirmed,
                    RestoredToValue = 0,
                    CreditsRefunded = 1,
                    Message = "The configured value is back at 0, confirmed by reading the server.",
                },
                new BatchRollbackItemResult
                {
                    TemplateKey = "CTFP",
                    RollbackState = RemediationRollbackState.Unconfirmed,
                    Message = "The reversal ran. The confirming read then failed, so this server's state is unknown.",
                },
                new BatchRollbackItemResult
                {
                    TemplateKey = "OPTIMIZEFORADHOC",
                    RollbackState = RemediationRollbackState.Failed,
                    Message = "The reversal ran and the confirming read observed the wrong value.",
                },
                new BatchRollbackItemResult
                {
                    TemplateKey = "ADHOCDISTRIBUTEDQUERIES",
                    RollbackState = RemediationRollbackState.NotAvailable,
                    Message = RemediationRollbackProse.NoRollbackMarker
                        + " this fix is not reversible. " + NoRollbackSentinel,
                },
            },
        };

        // ── Reading the rendered html ────────────────────────────────────────────

        /// <summary>
        /// NO <see cref="RemediationRefusal"/> MEMBER MAY APPEAR IN RENDERED BATCH HTML — any of
        /// them, enumerated off the type rather than listed by hand (fix round 1, gate blocker 3).
        ///
        /// <para>It scans the RAW section html, not the visible text, so a member hiding in a title=
        /// attribute or a css class is caught too. The match is whole-token and case-sensitive: these
        /// are PascalCase identifiers, and a substring match would fire on ordinary prose.</para>
        /// </summary>
        private static void AssertNoRefusalEnumTokens(string sectionHtml)
        {
            foreach (var name in Enum.GetNames(typeof(RemediationRefusal)))
            {
                var hit = Regex.Match(sectionHtml, @"\b" + Regex.Escape(name) + @"\b");
                Assert.False(hit.Success,
                    $"The rendered batch section carries the RemediationRefusal member '{name}'. "
                    + "An operator reads words, not enum members: route it through "
                    + "RemediationRefusalProse. Context: "
                    + sectionHtml.Substring(Math.Max(0, hit.Index - 90),
                        Math.Min(220, sectionHtml.Length - Math.Max(0, hit.Index - 90))));
            }
        }

        private static string SectionHtml(string html) => ElementHtml(html, "rbatch-section");

        /// <summary>The html of the &lt;div&gt; carrying a class, by DEPTH match. "" when absent.</summary>
        private static string ElementHtml(string html, string cssClass)
        {
            var anchor = Regex.Match(html,
                "class\\s*=\\s*\"[^\"]*\\b" + Regex.Escape(cssClass) + "\\b[^\"]*\"", RegexOptions.IgnoreCase);
            if (!anchor.Success) return string.Empty;

            int open = html.LastIndexOf('<', anchor.Index);
            if (open < 0) return string.Empty;

            int depth = 0;
            foreach (Match tag in Regex.Matches(html.Substring(open), @"</?div\b", RegexOptions.IgnoreCase))
            {
                depth += tag.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
                if (depth != 0) continue;
                int end = html.Substring(open).IndexOf('>', tag.Index);
                return end < 0 ? string.Empty : html.Substring(open, end + 1);
            }
            return string.Empty;
        }

        /// <summary>The markup with one element removed, element and contents.</summary>
        private static string WithoutElement(string html, string cssClass)
        {
            var element = ElementHtml(html, cssClass);
            if (string.IsNullOrEmpty(element)) return html;
            int at = html.IndexOf(element, StringComparison.Ordinal);
            return at < 0 ? html : html.Remove(at, element.Length);
        }

        /// <summary>Every rendered button and submit/button input, by its visible label.</summary>
        private static List<string> RenderedControls(string sectionHtml)
        {
            var controls = new List<string>();
            foreach (Match b in Regex.Matches(sectionHtml, @"<button\b[^>]*>(?<inner>.*?)</button>",
                                              RegexOptions.Singleline | RegexOptions.IgnoreCase))
                controls.Add(Visible(b.Groups["inner"].Value));

            foreach (Match i in Regex.Matches(sectionHtml, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
            {
                var type = Regex.Match(i.Value, "type\\s*=\\s*\"(?<t>[^\"]*)\"", RegexOptions.IgnoreCase);
                if (!type.Success) continue;
                if (!type.Groups["t"].Value.Equals("submit", StringComparison.OrdinalIgnoreCase)
                    && !type.Groups["t"].Value.Equals("button", StringComparison.OrdinalIgnoreCase)) continue;
                var value = Regex.Match(i.Value, "value\\s*=\\s*\"(?<v>[^\"]*)\"", RegexOptions.IgnoreCase);
                controls.Add(value.Success ? value.Groups["v"].Value : "(unlabelled input)");
            }
            return controls;
        }

        private static bool LooksLikeApply(string label) =>
            Regex.IsMatch(label, @"\b(apply|applies|applying|execute|run|runs|approve|commit|remediate|fix)\b",
                          RegexOptions.IgnoreCase);

        /// <summary>Tags out, entities decoded, whitespace collapsed: the words a person reads.</summary>
        private static string Visible(string html)
            => Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();

        // ── Doubles ──────────────────────────────────────────────────────────────

        private sealed class CapturingHost : ComponentBase
        {
            [Parameter] public Action<ComponentBase>? OnCaptured { get; set; }

            protected override void BuildRenderTree(RenderTreeBuilder builder)
            {
                builder.OpenComponent<SQLTriage.Pages.Remediation>(0);
                builder.AddComponentReferenceCapture(1, o => OnCaptured?.Invoke((ComponentBase)o));
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
