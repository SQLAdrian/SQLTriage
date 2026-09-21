/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationRefusalProsePageRenderTests — the RENDER guard for the three refusal-bearing pages
 * OUTSIDE Pages/Remediation.razor (lane/remediation-enum-prose-2, 2026-09-02).
 *
 * WHY IT EXISTS. Lane 1 fixed Pages/Remediation.razor and left a catalogue of the sites it had not
 * touched: RemediationLab 94/109, AgentJobGuard 394/429, AgentJobSync 367/399, tagged BELIEVED —
 * read from source, never rendered. This file is what turns that tag into PROVED for the pages, and
 * RemediationRefusalProseSourceGuardTests is what turns it into proved for the tree.
 *
 * ⚠ THE TWO DEFECTS ARE NOT THE SAME SHAPE, AND THEY NEEDED DIFFERENT PROOF.
 *   - RemediationLab wrote the enum into MARKUP: `Refused at gate: @_proposal.Refusal`. Planting a
 *     refused proposal on the page's own field and rendering is a complete proof of that site.
 *   - AgentJobGuard and AgentJobSync wrote it in C#, inside the apply/preview handlers, into a
 *     string field the markup then prints. Planting that string would prove nothing — it would
 *     assert the fixture. So those two are driven through THE PAGE'S OWN HANDLERS: the test invokes
 *     PreviewAsync and ApplySelectedAsync and reads what the page put on the row.
 *
 * ⚠ HOW A HANDLER RUNS WITHOUT A SERVER, AND WHY THAT IS SAFE. The runner these two pages resolve
 * from DI is replaced with one built on a DENIED capability, so RemediationRunner refuses at gate 1
 * or gate 2 — both decided from local state, before any connection is opened. Its executor is
 * ThrowingExecutor, which throws on every member: if a future change ever let one of these handlers
 * reach execution, this test fails loudly instead of quietly contacting a SQL Server. Nothing is
 * applied and no server is named beyond a fixture string.
 *
 * ⚠ THE SUBSTITUTIONS, so nothing here is over-read:
 *   1. THE LICENCE SOURCE IS A FAKE (FakeBundleAccessor). The gate LOGIC is real; the signature
 *      check on a minted bundle is not exercised.
 *   2. THE RUNNER'S CAPABILITY IS DENIED ON PURPOSE while the PAGE's capability is granted. That is
 *      not a state a real install reaches — it is the seam that makes a refusal reachable offline.
 *      What is proved is the RENDERING of a refusal, never that a real gate produces one; the gates'
 *      own answers stay proved by RemediationRunnerTests and the live smokes.
 *   3. THE JOB ROWS ARE SYNTHETIC. No msdb is read.
 *   4. NO SERVER IS CONTACTED and nothing is applied.
 *
 * ⚠ WHY THIS IS IN Gated\. It binds SQLTriage.Pages.RemediationLab / AgentJobGuard / AgentJobSync,
 * all three Content-Removed from a COMMUNITY build (buildprofile.targets:177-198). The Gated\**\*.cs
 * glob keeps this folder out of the community test build, so it runs only where the pages exist —
 * which means the FULL axis locally and NOT CI, exactly as lane 1 recorded for its own guard. The
 * CI-visible half of this lane is RemediationRefusalProseSourceGuardTests, which reads source and is
 * deliberately not in this folder.
 */

using System;
using System.Collections;
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
using SQLTriage.Data.Models.Jobs;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Jobs;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Remediation;
using SQLTriage.Tests.Licensing;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests.Gated
{
    public sealed class RemediationRefusalProsePageRenderTests
    {
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Server = "SQLT-REFUSAL-PROSE-PAGES-FIXTURE";
        private const string Sentinel = "SENTINEL-PAGE-REFUSAL";

        private readonly ITestOutputHelper _out;
        public RemediationRefusalProsePageRenderTests(ITestOutputHelper output) => _out = output;

        // ── 1. Pages/RemediationLab.razor — the markup sites (94, 109) ───────────

        [Fact]
        public async Task RemediationLab_PreviewAndResultRefusals_ReadAsPlainWords_AndNameTheNextStep()
        {
            var html = await RenderAsync<SQLTriage.Pages.RemediationLab>(
                arrange: (page, type) =>
                {
                    // Both sites are pure markup, so a planted refusal IS the thing under test:
                    // the page has no chance to launder the value between here and the browser.
                    Field(type, "_proposal").SetValue(page,
                        RemediationProposal.Refused(RemediationRefusal.CapabilityDenied,
                            "The gate said no. " + Sentinel + "-PREVIEW"));
                    Field(type, "_result").SetValue(page,
                        RemediationResult.Refused(RemediationRefusal.InsufficientCredits,
                            "The gate said no. " + Sentinel + "-RESULT"));
                    return Task.CompletedTask;
                });

            // ⚠ THE PRECONDITION. RemediationLab renders a "Dev tools not licensed" shell and an
            // AccessDenied shell, either of which would let every scan below pass over the wrong
            // markup and report clean.
            Assert.Contains("Remediation Lab", html, StringComparison.Ordinal);

            AssertSiteReadsAsProse(html, "RemediationLab.razor:94 / preview", Sentinel + "-PREVIEW",
                RemediationRefusal.CapabilityDenied);
            AssertSiteReadsAsProse(html, "RemediationLab.razor:109 / result", Sentinel + "-RESULT",
                RemediationRefusal.InsufficientCredits);
        }

        // ── 2. Pages/AgentJobGuard.razor — the handler sites (394, 429) ──────────

        [Fact]
        public async Task AgentJobGuard_PreviewAndApplyRefusals_ReadAsPlainWords_AndNameTheNextStep()
        {
            string? preview = null, result = null;

            var html = await RenderAsync<SQLTriage.Pages.AgentJobGuard>(
                arrange: async (page, type) =>
                {
                    Field(type, "_selectedServer").SetValue(page, Server);
                    Field(type, "_defaultDb").SetValue(page, "FixtureDb");

                    var rows = (IList)Field(type, "_rows").GetValue(page)!;
                    var rowType = type.GetNestedType("JobRow", BindingFlags.NonPublic)!;
                    var row = Activator.CreateInstance(rowType, nonPublic: true)!;
                    rowType.GetField("Job")!.SetValue(row, FixtureJob());
                    rowType.GetField("Selected")!.SetValue(row, true);
                    rows.Add(row);

                    // THE PAGE'S OWN HANDLERS compose the two strings under test. Nothing here
                    // writes PreviewText or ResultText.
                    await Invoke(page, type, "PreviewAsync", row);
                    await Invoke(page, type, "ApplySelectedAsync");

                    (preview, result) = ReadRowText(rowType, row);
                    RePlantAfterTheConfirmingReRead(page, type, row);
                });

            // ⚠ THE PRECONDITION: the page's own shell, not the licence-refusal or AccessDenied one.
            Assert.Contains("AG primary-replica guard", html, StringComparison.Ordinal);

            AssertHandlerTextIsProducerProse("AgentJobGuard.razor:394 / preview", preview, html);
            AssertHandlerTextIsProducerProse("AgentJobGuard.razor:429 / apply", result, html);
            AssertNoMemberAnywhere("Pages/AgentJobGuard.razor", html);
        }

        // ── 3. Pages/AgentJobSync.razor — the handler sites (367, 399) ───────────

        [Fact]
        public async Task AgentJobSync_PreviewAndApplyRefusals_ReadAsPlainWords_AndNameTheNextStep()
        {
            string? preview = null, result = null;

            var html = await RenderAsync<SQLTriage.Pages.AgentJobSync>(
                arrange: async (page, type) =>
                {
                    Field(type, "_source").SetValue(page, Server + "-PRIMARY");
                    Field(type, "_target").SetValue(page, Server);

                    var rows = (IList)Field(type, "_rows").GetValue(page)!;
                    var rowType = type.GetNestedType("SyncRow", BindingFlags.NonPublic)!;
                    var row = Activator.CreateInstance(rowType, nonPublic: true)!;
                    rowType.GetField("Diff")!.SetValue(row, new JobDiffRow
                    {
                        JobName = "SQLTriage fixture job",
                        Status = JobDiffStatus.Missing,
                        Details = "steps differ",
                    });
                    rowType.GetField("Selected")!.SetValue(row, true);
                    rows.Add(row);

                    await Invoke(page, type, "PreviewAsync", row);
                    await Invoke(page, type, "ApplySelectedAsync");

                    (preview, result) = ReadRowText(rowType, row);
                    RePlantAfterTheConfirmingReRead(page, type, row);
                });

            // ⚠ THE PRECONDITION: the page's own shell, not the licence-refusal or AccessDenied one.
            Assert.Contains("AG Agent-job sync", html, StringComparison.Ordinal);

            AssertHandlerTextIsProducerProse("AgentJobSync.razor:367 / preview", preview, html);
            AssertHandlerTextIsProducerProse("AgentJobSync.razor:399 / apply", result, html);
            AssertNoMemberAnywhere("Pages/AgentJobSync.razor", html);
        }

        // ── 4. RemediationLab, driven over EVERY member ──────────────────────────

        [Fact]
        public async Task RemediationLab_RendersNoEnumMember_ForAnyMemberOfTheEnum()
        {
            // Tests 1-3 each scan their whole page's raw html for every member, so the catch-all is
            // already made three times. What this adds is the OTHER axis: the two sites above are
            // planted with two members, and a switch arm that only misbehaved on a third would go
            // unseen. Here the page is re-rendered once per member, both sites carrying it.
            //
            // ⚠ Only RemediationLab can be driven this way. Its two sites take whatever refusal is
            // planted on the field. AgentJobGuard and AgentJobSync compose their string inside a
            // handler, so the member is chosen by whichever gate refuses, not by this fixture —
            // which is exactly why those two are driven through their handlers and not planted.
            foreach (RemediationRefusal member in Enum.GetValues(typeof(RemediationRefusal)))
            {
                var current = member;
                var html = await RenderAsync<SQLTriage.Pages.RemediationLab>((page, type) =>
                {
                    Field(type, "_proposal").SetValue(page,
                        RemediationProposal.Refused(current, "The gate said no."));
                    Field(type, "_result").SetValue(page,
                        RemediationResult.Refused(current, "The gate said no."));
                    return Task.CompletedTask;
                });

                Assert.Contains("Remediation Lab", html, StringComparison.Ordinal);
                Assert.Contains(RemediationRefusalProse.Describe(current), html, StringComparison.Ordinal);

                foreach (var name in Enum.GetNames(typeof(RemediationRefusal)))
                {
                    var hit = Regex.Match(html, @"\b" + Regex.Escape(name) + @"\b");
                    Assert.False(hit.Success,
                        $"Pages/RemediationLab.razor rendered the RemediationRefusal member '{name}' "
                        + $"while showing a {current} refusal. Context: "
                        + html.Substring(Math.Max(0, hit.Index - 120),
                            Math.Min(300, html.Length - Math.Max(0, hit.Index - 120))));
                }
            }
        }

        // ── Assertions ───────────────────────────────────────────────────────────

        /// <summary>
        /// One named site, found by its planted sentinel: plain words for its own member, that
        /// member's own next step, and no enum member anywhere in the block.
        /// </summary>
        private void AssertSiteReadsAsProse(string html, string site, string sentinel, RemediationRefusal refusal)
        {
            var block = BlockContaining(html, sentinel);
            Assert.False(string.IsNullOrEmpty(block),
                $"[{site}] did not render: the planted gate message '{sentinel}' is absent from the "
                + "page html, so this site was never scanned and its wording is UNPROVED by this run.");

            var visible = Visible(block);
            _out.WriteLine($"{site}: {visible}");

            Assert.Contains(RemediationRefusalProse.Describe(refusal), visible, StringComparison.Ordinal);
            Assert.Contains(RemediationRefusalProse.DescribeNextStep(refusal), visible, StringComparison.Ordinal);

            foreach (var member in Enum.GetNames(typeof(RemediationRefusal)))
                Assert.False(Regex.IsMatch(block, @"\b" + Regex.Escape(member) + @"\b"),
                    $"[{site}] carries the RemediationRefusal member '{member}'. An operator reads "
                    + "words, not enum members: route it through RemediationRefusalProse. "
                    + "Rendered: " + visible);
        }

        /// <summary>
        /// ONE handler-composed site, asserted on the exact string THAT SITE wrote — read back off
        /// the row rather than found in the page, so the preview site and the apply site are two
        /// separate measurements rather than one shared block. The member is chosen by whichever
        /// gate refused, not by this fixture, so the assertion is: it is SOME member's sentence, and
        /// it carries THAT member's next step. Half a producer line fails here.
        ///
        /// <para>Then, separately, the string is required to have reached the rendered page. A
        /// handler that composed it perfectly into a field the markup never prints is not a fix.</para>
        /// </summary>
        private void AssertHandlerTextIsProducerProse(string site, string? text, string html)
        {
            Assert.False(string.IsNullOrWhiteSpace(text),
                $"[{site}] wrote no text at all. The handler either did not run or did not refuse, "
                + "so this site is UNPROVED by this run.");

            _out.WriteLine($"{site}: {text}");

            var matched = Enum.GetValues(typeof(RemediationRefusal))
                .Cast<RemediationRefusal>()
                .Where(r => text!.Contains(RemediationRefusalProse.Describe(r), StringComparison.Ordinal))
                .ToList();

            Assert.True(matched.Count > 0,
                $"[{site}] refused, and the words are not any member's sentence from "
                + $"RemediationRefusalProse. Wrote: {text}");

            foreach (var r in matched)
                Assert.True(text!.Contains(RemediationRefusalProse.DescribeNextStep(r), StringComparison.Ordinal),
                    $"[{site}] tells the operator what happened and not what to do. Expected the next "
                    + $"step for {r}: '{RemediationRefusalProse.DescribeNextStep(r)}'. Wrote: {text}");

            Assert.Contains("Nothing was sent to the server.", text!, StringComparison.Ordinal);

            foreach (var member in Enum.GetNames(typeof(RemediationRefusal)))
                Assert.False(Regex.IsMatch(text!, @"\b" + Regex.Escape(member) + @"\b"),
                    $"[{site}] carries the RemediationRefusal member '{member}'. An operator reads "
                    + $"words, not enum members. Wrote: {text}");

            // ⚠ AND IT REACHED THE BROWSER. Compared over the page's VISIBLE text (tags out,
            // entities decoded, whitespace collapsed), so markup and encoding cannot fake a match.
            Assert.Contains(Collapse(text!), Visible(html), StringComparison.Ordinal);
        }

        /// <summary>The catch-all over one whole page's raw html, members enumerated off the type.</summary>
        private static void AssertNoMemberAnywhere(string page, string html)
        {
            foreach (var member in Enum.GetNames(typeof(RemediationRefusal)))
            {
                var hit = Regex.Match(html, @"\b" + Regex.Escape(member) + @"\b");
                Assert.False(hit.Success,
                    $"{page} rendered the RemediationRefusal member '{member}' somewhere outside the "
                    + "refusal line itself — an attribute, a css class, or an uncatalogued surface. "
                    + "Context: " + html.Substring(Math.Max(0, hit.Index - 120),
                        Math.Min(300, html.Length - Math.Max(0, hit.Index - 120))));
            }
        }

        /// <summary>
        /// Both strings the handlers wrote. Read as a pair so a page whose preview silently did
        /// nothing cannot pass on the apply half alone.
        /// </summary>
        private static (string? Preview, string? Result) ReadRowText(Type rowType, object row) =>
            (rowType.GetField("PreviewText")!.GetValue(row) as string,
             rowType.GetField("ResultText")!.GetValue(row) as string);

        /// <summary>
        /// Puts the row back on the page after ApplySelectedAsync's confirming re-read has replaced
        /// the whole collection.
        ///
        /// <para>⚠ THIS IS NOT A FIXTURE WORKAROUND — IT IS PRODUCT BEHAVIOUR, FOUND BY THIS TEST
        /// GOING RED (2026-09-02). Both apply handlers end by re-reading the estate to prove it
        /// converged: AgentJobGuard awaits LoadJobsAsync, AgentJobSync awaits ComputeDiffAsync, and
        /// each ASSIGNS A NEW LIST to _rows. Every row's ResultText goes with the old list. In a live
        /// circuit the operator sees each result as the loop's StateHasChanged paints it and then
        /// watches the re-read replace it; in a static render there is only the final state, so the
        /// text the handler wrote is simply not on the page.</para>
        ///
        /// <para>WHY RE-PLANTING IS STILL AN HONEST MEASUREMENT. What goes back on the page is the
        /// string THE HANDLER COMPOSED, read off the row above, never text this fixture typed. So
        /// the assertion it enables is exactly the one that is left to make: the markup prints
        /// PreviewText and ResultText verbatim, so a producer line reaches a browser. The composing
        /// is proved separately, on the field value, by AssertHandlerTextIsProducerProse.</para>
        /// </summary>
        private static void RePlantAfterTheConfirmingReRead(ComponentBase page, Type type, object row)
        {
            // Re-read the field: the handler assigned a NEW list, so the one captured earlier is a
            // stale reference and adding to it would render nothing.
            var rows = (IList)Field(type, "_rows").GetValue(page)!;
            if (rows.Contains(row)) return;
            rows.Clear();
            rows.Add(row);
        }

        // ── The harness ──────────────────────────────────────────────────────────

        private async Task<string> RenderAsync<TPage>(Func<ComponentBase, Type, Task> arrange)
            where TPage : IComponent
        {
            var settingsDir = Path.Combine(Path.GetTempPath(), "sqlt-refusal-pages-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(settingsDir);

            var services = BuildGraph(settingsDir, out var bundle);
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var sp = scope.ServiceProvider;

            Assert.True(sp.GetRequiredService<IRemediationCapability>().IsGranted,
                "the substituted licence did not grant remediation, so these pages would render "
                + "their refusal shell and every assertion here would be over the wrong markup. "
                + ServerConfigSuiteGate.DescribeRefusal(bundle));

            var connections = sp.GetRequiredService<ServerConnectionManager>();
            Assert.True(connections.GetConnections().Any(), "the fixture connection was not registered");

            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            await using var renderer = new HtmlRenderer(sp, loggerFactory);

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                ComponentBase? page = null;
                var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["OnCaptured"] = (Action<ComponentBase>)(c => page = c),
                });

                var root = await renderer.RenderComponentAsync<CapturingHost<TPage>>(parameters);
                Assert.NotNull(page);
                await root.QuiescenceTask;

                var type = page!.GetType();
                await arrange(page!, type);

                typeof(ComponentBase).GetMethod("StateHasChanged", F)!.Invoke(page, null);
                await root.QuiescenceTask;

                return root.ToHtmlString();
            });
        }

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

            // DevToolsCapability unlocks Pages/RemediationLab (FeatureRegistrar's dev-tools hard
            // gate reads exactly this claim); Remediation unlocks the other two pages' shells.
            bundle = new FakeBundleAccessor
            {
                IsUnlocked = true,
                Tier = Tier.Full,
                Features = new BundleFeatures(
                    RagEnabled: false, SpBlitzImport: true, FullCorpus: true,
                    PermittedCheckIds: Array.Empty<int>(),
                    DevToolsCapability: true,
                    Remediation: true, RemediationCreditsPerServer: 10),
            };
            var accessor = bundle;
            Replace(services, ServiceDescriptor.Singleton<IBundleAccessor>(_ => accessor));

            var settings = new UserSettingsService(Path.Combine(settingsDir, "user-settings.json"));
            settings.SetNoPantsMode(true);
            Replace(services, ServiceDescriptor.Singleton(settings));
            Replace(services, ServiceDescriptor.Singleton<IUserSettingsService>(_ => settings));

            Replace(services, ServiceDescriptor.Singleton<IJSRuntime>(_ => new NoJsRuntime()));

            // ⚠ THE SEAM THAT MAKES A REFUSAL REACHABLE OFFLINE. The PAGE's IRemediationCapability
            // stays granted (or its shell would not render); the RUNNER's is denied, so gate 2
            // refuses from local state. See the header for why this is not a real install's state.
            Replace(services, ServiceDescriptor.Singleton(sp => new RemediationRunner(
                sp.GetRequiredService<RemediationTemplateStore>(),
                new DeniedCapability(),
                new InMemoryRemediationCreditLedger(initialCreditsPerServer: 5),
                new ThrowingExecutor(),
                sp.GetRequiredService<SQLTriage.Data.AuditLogService>(),
                NullLogger<RemediationRunner>.Instance)));

            return services;
        }

        private static void Replace(IServiceCollection services, ServiceDescriptor descriptor)
        {
            for (int i = services.Count - 1; i >= 0; i--)
                if (services[i].ServiceType == descriptor.ServiceType) services.RemoveAt(i);
            services.Add(descriptor);
        }

        private static AgentJobDefinition FixtureJob() => new()
        {
            JobId = Guid.NewGuid(),
            Name = "SQLTriage fixture job",
            Enabled = true,
            StartStepId = 1,
            Steps = new List<AgentJobStep>
            {
                new() { StepId = 1, StepName = "Step one", Command = "SELECT 1;", OnSuccessAction = 1, OnFailAction = 2 },
            },
        };

        // ── Reflection helpers ───────────────────────────────────────────────────

        private static FieldInfo Field(Type type, string name) =>
            type.GetField(name, F)
            ?? throw new InvalidOperationException(
                $"{type.Name} no longer carries the field '{name}'. This guard plants state on that "
                + "field, so a rename silently narrows what it scans: update the harness rather than "
                + "deleting the site.");

        private static async Task Invoke(ComponentBase page, Type type, string method, params object?[] args)
        {
            var m = type.GetMethod(method, F)
                ?? throw new InvalidOperationException(
                    $"{type.Name} no longer carries the handler '{method}'. This guard drives the "
                    + "PAGE'S OWN handler rather than planting its output, so a rename turns a "
                    + "measurement into a formality: update the harness.");

            var returned = m.Invoke(page, args);
            if (returned is Task t) await t;
        }

        // ── Reading the rendered html ────────────────────────────────────────────

        /// <summary>
        /// The SMALLEST &lt;pre&gt;/&lt;p&gt;/&lt;div&gt; carrying a needle, so a per-site assertion
        /// is made over that site's own block. Smallest, not first: an outer container also contains
        /// the needle, and asserting over the whole page dressed up as one site is the shape that
        /// makes a per-site failure message a lie. "" when the needle never rendered.
        /// </summary>
        private static string BlockContaining(string html, string needle)
        {
            var best = string.Empty;
            foreach (Match m in Regex.Matches(html, @"<(pre|p|div)\b[^>]*>.*?</\1>",
                                              RegexOptions.Singleline | RegexOptions.IgnoreCase))
                if (m.Value.Contains(needle, StringComparison.Ordinal)
                    && (best.Length == 0 || m.Value.Length < best.Length))
                    best = m.Value;
            return best;
        }

        private static string Visible(string html)
            => Collapse(System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")));

        private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();

        // ── Doubles ──────────────────────────────────────────────────────────────

        private sealed class CapturingHost<TPage> : ComponentBase where TPage : IComponent
        {
            [Parameter] public Action<ComponentBase>? OnCaptured { get; set; }

            protected override void BuildRenderTree(RenderTreeBuilder builder)
            {
                builder.OpenComponent<TPage>(0);
                builder.AddComponentReferenceCapture(1, o => OnCaptured?.Invoke((ComponentBase)o));
                builder.CloseComponent();
            }
        }

        private sealed class DeniedCapability : IRemediationCapability { public bool IsGranted => false; }

        /// <summary>
        /// Every member throws. A refusal decided at a gate never reaches this class; anything that
        /// DOES reach it means a handler got past the gates, and this test must say so loudly rather
        /// than let a fixture quietly open a connection to a real server.
        /// </summary>
        private sealed class ThrowingExecutor : IRemediationExecutor
        {
            public Task<RemediationPreview> PreviewAsync(RemediationRequest request, CancellationToken ct = default) =>
                throw new InvalidOperationException(
                    "the refusal-prose fixture reached the EXECUTOR: a gate that should have refused "
                    + "from local state did not, and this test must never contact a server");

            public Task<RemediationExecution> ExecuteAsync(RemediationRequest request, CancellationToken ct = default) =>
                throw new InvalidOperationException(
                    "the refusal-prose fixture reached the EXECUTOR: a gate that should have refused "
                    + "from local state did not, and this test must never apply anything");

            public bool CanWriteAudit() => true;
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
