/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationBatchRenderTests — the RENDERED half of the preview-only batch surface
 * (plan item 1.1, Phase 1 build lane; added at the Phase-1 gate, 2026-09-01).
 *
 * WHY IT EXISTS. Before this file, NOBODY had ever seen the populated batch surface. The build had
 * three source scans, a reflection-driven page test and 36 unit tests, and every one of them read
 * text or drove a method. The house lesson [[render-the-ui-before-sweeping]] is that pixels found
 * what forty agents and two gates missed, three times. The licence gate correctly refuses first on
 * a real boot, so the surface cannot be reached by simply starting the app either.
 *
 * ⚠ THE SUBSTITUTIONS, STATED PLAINLY, SO NO CLAIM HERE IS OVER-READ:
 *
 *   1. THE LICENCE SOURCE IS A FAKE. FakeBundleAccessor (Tier.Full, Features.Remediation = true)
 *      stands in for a signed bundle file. The GATE LOGIC is the real
 *      BundleBackedRemediationCapability over the real ServerConfigSuiteGate; the SIGNATURE check on
 *      a minted bundle is not exercised here and stays untested by this file. Shipped convention —
 *      the same substitution ConfigStoreWriteGuardTests and the live remediation harnesses make.
 *
 *   2. THE FINDINGS ARE SYNTHETIC. They are CheckResult objects this test composes, not a captured
 *      audit run. What that limits: this proves the RENDERER over a populated model, never that
 *      CheckExecutionService produces that model. [[drive-renderer-tests-from-captured-responses]]
 *      is the standing warning about exactly this, and the honest answer is that the selector half
 *      IS driven from the shipped CheckResolutionLookup (a real index over the real template store),
 *      so the join under the markup is real even though the findings feeding it are composed.
 *
 *   3. THE BATCH PREVIEW READING IS PLANTED. A real reading needs a live SQL Server; the per-item
 *      no-change flag and the two totals are therefore a BatchPreviewResult this test builds, set
 *      on the page's own field. Everything downstream of that field — the three-state branch, the
 *      two named totals, the per-item panel — is the page's real markup.
 *
 *   4. NO SERVER IS CONTACTED. The page renders once with no server selected (so OnInitializedAsync
 *      does not reach ServerSizingService), then the server, the findings and the reading are set
 *      and the page is re-rendered.
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
    public sealed class RemediationBatchRenderTests
    {
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Server = "SQLT-RENDER-FIXTURE";

        private readonly ITestOutputHelper _out;
        public RemediationBatchRenderTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public async Task ThePopulatedBatchSection_ShowsTheThreeStates_TheTwoTotalsApart_AndNoApplyControl()
        {
            var html = await RenderPopulatedBatchAsync();
            var section = SectionHtml(html);
            _out.WriteLine(Visible(section));

            // ── It rendered at all. Without this the assertions below could pass over an empty
            //    page — a refused gate, a thrown initialiser — which is the failure mode that makes
            //    a render test worthless.
            Assert.Contains("rbatch-section", html, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(section), "the batch section did not render");
            Assert.Contains("Batch preview:", section, StringComparison.Ordinal);

            var visible = Visible(section);

            // ── 1. The per-item no-change flag, in all THREE states, in the rendered words.
            Assert.Contains("already at target", visible, StringComparison.Ordinal);
            Assert.Contains("would change this setting", visible, StringComparison.Ordinal);
            Assert.Contains("(unread)", visible, StringComparison.Ordinal);
            Assert.Contains("whether it would change anything is unknown", visible, StringComparison.Ordinal);

            // ── 2. Reserved and committed shown APART, each with its own number. The planted
            //    reading prices three items at 1 each, one of them already at target: 3 and 2.
            Assert.Matches(new Regex(@"Would reserve\s*3\b"), visible);
            Assert.Matches(new Regex(@"Projected commit\s*2\b"), visible);
            Assert.Contains("1 already at target", visible, StringComparison.Ordinal);
            Assert.Contains("1 unread", visible, StringComparison.Ordinal);

            // ── 3. ZERO apply-shaped controls in the RENDERED output. The source scans in
            //    RemediationBatchPreviewUiTests prove where the call sites and the apply-shaped
            //    labels are in the file; this proves what reaches the html an operator's browser
            //    receives, which is a different measurement: a control could be composed from an
            //    expression.
            //
            //    ⚠ WHAT THIS ASSERTION MEANS IN PHASE 3. This harness never arms the approval
            //    dialog — it sets _batchOpen and a reading, and nothing else — so what it measures
            //    is the DISARMED surface. That is still worth pinning here: a batch reading is not
            //    a place anything applies from. The armed half, and the pairing that makes "armed"
            //    mean something, is Gated/RemediationBatchApplyRenderTests, which renders both
            //    states. Before Phase 3 this line meant "there is no apply at all"; it now means
            //    "not from here", and the difference is worth a reader knowing.
            var controls = RenderedControls(section);
            Assert.NotEmpty(controls);   // the scan has something to scan: the Preview button exists
            var applyShaped = controls.Where(LooksLikeApply).ToList();
            Assert.True(applyShaped.Count == 0,
                "The rendered batch section must carry no apply-shaped control while the approval "
                + "dialog is shut. Found: " + string.Join(" | ", applyShaped));

            // And the copy that promises it, rendered rather than read from source.
            Assert.Contains("Nothing is applied from this list", visible, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TheRenderedDenominator_NamesEveryBucket_AndTheNumbersAddUp()
        {
            var visible = Visible(SectionHtml(await RenderPopulatedBatchAsync()));

            // The fixture plants 4 open findings on this server (3 resolving to one MAXDOP template,
            // 1 with no registered fix) and 2 on another server, one of them a duplicate.
            //
            // ⚠ THIS IS THE ASSERTION THE RENDER FOUND A DEFECT WITH. The sentence used to read
            // "4 open findings ... 1 can be previewed together here": a FIX count inside a sentence
            // whose denominator is FINDINGS, so three of the four went unaccounted for. Every number
            // below is a finding count now, the fix count is named as a fix, and 3 + 1 = 4.
            Assert.Matches(new Regex(@"\b4\s*open findings on"), visible);
            Assert.Matches(new Regex(@"\b3\s*clear through 1 fix you can preview together here"), visible);
            Assert.Matches(new Regex(@"\b1\s*has no fix registered in this build"), visible);

            // The excluded-elsewhere count is the deduped one (gate fix, 2026-09-01): two raw
            // findings on the other server, one duplicate, so ONE is reported.
            // Singular, both verbs. The render is also what caught this reading "1 more were
            // measured ... and are excluded": the count is interpolated, so the agreement has to be
            // interpolated with it.
            Assert.Contains("(1 more was measured on other servers and is excluded.)", visible, StringComparison.Ordinal);
        }

        // ── The harness ──────────────────────────────────────────────────────────

        /// <summary>
        /// Renders the real page, then plants the server, the findings and one batch reading and
        /// re-renders. Returns the html of the second render.
        /// </summary>
        private async Task<string> RenderPopulatedBatchAsync()
        {
            var settingsDir = Path.Combine(Path.GetTempPath(), "sqlt-rbatch-render-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(settingsDir);

            var services = BuildGraph(settingsDir, out var bundle);
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var sp = scope.ServiceProvider;

            // Gate 2 reads the bundle; assert the substitution actually granted, so a refused page
            // cannot be mistaken for a rendered one further down.
            Assert.True(sp.GetRequiredService<IRemediationCapability>().IsGranted,
                "the substituted licence did not grant remediation, so the page would render its "
                + "refusal shell and every assertion below would be over the wrong markup. "
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

                // FIRST render: no server selected, so OnInitializedAsync never asks
                // ServerSizingService to read a host — nothing on this box is contacted.
                var root = await renderer.RenderComponentAsync<CapturingHost>(parameters);
                Assert.NotNull(page);
                await root.QuiescenceTask;

                // Now populate: a server, that server's findings, and one batch reading.
                // Establish is the bootstrap grant — legitimate here precisely because nothing was
                // selected for the first render, which the manager verifies for itself.
                var id = connections.GetConnections().First().Id;
                var outcome = await context.SetServerAsync(id, ConnectionRetargetGrant.Establish);
                Assert.True(outcome.Applied, "the fixture server was not selected, so the batch section would render its 'select a server' shell");
                quick.Results = Findings();
                quick.HasRun = true;

                var type = page!.GetType();
                type.GetField("_batchOpen", F)!.SetValue(page, true);
                type.GetField("_batchPreview", F)!.SetValue(page, PlantedReading());

                typeof(ComponentBase).GetMethod("StateHasChanged", F)!.Invoke(page, null);
                await root.QuiescenceTask;

                return root.ToHtmlString();
            });
        }

        private ServiceCollection BuildGraph(string settingsDir, out FakeBundleAccessor bundle)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.None));
            // The app's own registration graph, so this test cannot drift from what ships. An
            // empty configuration is enough: every value AddSharedServices reads has a default.
            var configuration = new ConfigurationBuilder().Build();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSharedServices(configuration);

            // ⚠ THE STORE SEAM IS NOT OPTIONAL — same reasoning as AccessCoverageProseTests'
            // harness. ServerConnectionManager's default path is the BUILD OUTPUT's
            // Config\server-connections.json, shared by every test in the assembly. Without this
            // override the harness writes its fixture connection into the build output and resolves
            // whichever pre-existing entry it matches first.
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

            // The licence SOURCE. Everything above it — the gate, the capability, the ledger's
            // allocation read — is the real component.
            bundle = new FakeBundleAccessor
            {
                IsUnlocked = true,
                Tier = Tier.Full,
                // Remediation is the claim gate 2 reads, and it is fail-CLOSED by default; the
                // credit allocation is what the persisted ledger seeds from.
                Features = new BundleFeatures(
                    RagEnabled: false, SpBlitzImport: true, FullCorpus: true,
                    PermittedCheckIds: Array.Empty<int>(),
                    Remediation: true, RemediationCreditsPerServer: 10),
            };
            var accessor = bundle;
            Replace(services, ServiceDescriptor.Singleton<IBundleAccessor>(_ => accessor));

            // No-Pants Mode is an operator opt-in and the page is inert without it.
            var settings = new UserSettingsService(Path.Combine(settingsDir, "user-settings.json"));
            settings.SetNoPantsMode(true);
            Replace(services, ServiceDescriptor.Singleton(settings));
            Replace(services, ServiceDescriptor.Singleton<IUserSettingsService>(_ => settings));

            // Static rendering has no JS interop. The page injects IJSRuntime and does not call it
            // on the batch path; a stub keeps DI resolvable and throws if anything does call it,
            // rather than silently returning a default.
            Replace(services, ServiceDescriptor.Singleton<IJSRuntime>(_ => new NoJsRuntime()));

            return services;
        }

        private static void Replace(IServiceCollection services, ServiceDescriptor descriptor)
        {
            for (int i = services.Count - 1; i >= 0; i--)
                if (services[i].ServiceType == descriptor.ServiceType) services.RemoveAt(i);
            services.Add(descriptor);
        }

        /// <summary>
        /// Four open findings on the fixture server and two elsewhere, one of them a duplicate.
        /// The three MAXDOP ids are three of the nine dedupe links this lane wired, so they resolve
        /// through the SHIPPED CheckResolutionLookup onto one template.
        /// </summary>
        private static List<CheckResult> Findings() => new()
        {
            Finding("SQLT-BPCHK-00220-PARALLELISM-MAXDOP", Server),
            Finding("SQLT-CUSTOM-MAXDOP", Server),
            Finding("SQLT-FRONTIER-MAXDOP-CXPACKET", Server),
            Finding("SQLT-ZZ-NO-SUCH-CHECK-ID", Server),
            Finding("SQLT-CUSTOM-MAXDOP", "SOME-OTHER-SERVER"),
            Finding("SQLT-CUSTOM-MAXDOP", "SOME-OTHER-SERVER"),   // the duplicate
        };

        private static CheckResult Finding(string checkId, string server) => new()
        {
            CheckId = checkId,
            CheckName = checkId + " name",
            Category = "Configuration",
            Severity = "High",
            Passed = false,
            Message = "The setting is not at the recommended value.",
            InstanceName = server,
        };

        /// <summary>
        /// One batch reading, covering all three no-change states: one item already at target, one
        /// that would change, one whose current value could not be read. Three items priced at 1
        /// each, so the two totals must render 3 and 2 — different numbers, which is what makes
        /// "shown apart" an assertion rather than a coincidence.
        /// </summary>
        private static BatchPreviewResult PlantedReading()
        {
            // The SHIPPED template store, so the three keys below are real templates and the option
            // names in the rendered sentence are the ones the executor would actually write. A
            // fixture naming a template that does not exist would render a panel the app cannot
            // produce, and the render would prove nothing about the app.
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);

            BatchRemediationPreviewItem Item(string key, string? current, int target, bool? noChange)
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
                RunId = "render-fixture-run-id",
                Items = new[]
                {
                    Item("MAXDOP", "0", 4, false),
                    Item("OPTIMIZEFORADHOC", "1", 1, true),
                    Item("CTFP", null, 50, null),
                },
            };
        }

        // ── Reading the rendered html ────────────────────────────────────────────

        /// <summary>The batch section's html, by depth-matched div — the same anchor the source
        /// scan uses, applied to the OUTPUT.</summary>
        private static string SectionHtml(string html)
        {
            int open = html.IndexOf("<div class=\"rbatch-section\"", StringComparison.Ordinal);
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

        /// <summary>Hosts the page and hands back the instance the renderer created.</summary>
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

        /// <summary>
        /// Static rendering has no JS. This throws rather than returning a default, so a future
        /// change that calls into JS on this path fails loudly instead of rendering something the
        /// browser would not produce.
        /// </summary>
        private sealed class NoJsRuntime : IJSRuntime
        {
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
                throw new NotSupportedException($"JS interop is not available in a static render ({identifier}).");

            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
                throw new NotSupportedException($"JS interop is not available in a static render ({identifier}).");
        }
    }
}
