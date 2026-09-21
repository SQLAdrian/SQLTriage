/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationInputBoundsRenderTests — the RENDERED half of two fix-round blockers, 2026-09-01.
 *
 * Both were found by the gate READING the page and comparing it against the engine, and neither
 * could have been found by any test in this lane, because every one of them tested a producer in
 * isolation. [[render-the-ui-before-sweeping]]: pixels found what forty agents and two gates missed.
 *
 *   BLOCKER 4 — THE BOX AND THE ENGINE DISAGREED. Pages/Remediation.razor bound the target input's
 *     min/max to op.MinValue / op.MaxValue — the TEMPLATE's schema range. The executor enforces
 *     RemediationValueBounds. For MAXSERVERMEMORY the two flatly contradict each other: the box
 *     offered 128..2147483647 while the engine refuses anything below 1024 MB or above the host's
 *     installed RAM. The browser accepted the number, the operator clicked, and the app refused
 *     after the click — the worst possible order to learn a bound in. This file asserts, over the
 *     REAL shipped template set and the REAL rendered html, that every operator-input box carries
 *     the ENGINE's bounds.
 *
 *   BLOCKER 3 — THE HONEST SENTENCE NEVER REACHED THE PAGE. RemediationRollbackProse.DescribeState's
 *     Confirmed arm dropped RollbackError, and Confirmed is the state this lane's coercion sentence
 *     is written onto ("The configured value is back at 0. The engine is still using 16..."). A unit
 *     test on DescribeState proves the producer; this proves the OPERATOR SEES IT, in the html the
 *     browser receives, at the place the result panel renders.
 *
 * ⚠ SUBSTITUTIONS, stated (same set as RemediationBatchRenderTests, and for the same reasons):
 *   1. The licence SOURCE is a FakeBundleAccessor. The gate LOGIC is real; a minted signature is not.
 *   2. NO SERVER IS CONTACTED. The first render happens with no server selected, so the page never
 *      asks ServerSizingService to read a host; the RESULT planted below is composed, not applied.
 *   3. The templates are the SHIPPED store — not a fixture — so the bounds compared are the ones a
 *      real operator's browser would receive.
 *
 * ⚠ In Gated/ because it reaches SQLTriage.Pages.Remediation, which buildprofile.targets removes
 * from a COMMUNITY build.
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
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Remediation;
using SQLTriage.Tests.Licensing;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests.Gated
{
    public sealed class RemediationInputBoundsRenderTests
    {
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Server = "SQLT-BOUNDS-RENDER-FIXTURE";

        /// <summary>A sentence no producer could emit by accident, planted as RollbackError.</summary>
        private const string Sentinel =
            "The configured value is back at 0. The engine is still using 16, which it was before "
            + "this change as well (SQL Server coerces this setting or needs a restart).";

        private readonly ITestOutputHelper _out;
        public RemediationInputBoundsRenderTests(ITestOutputHelper output) => _out = output;

        // ── BLOCKER 4: the rendered input bounds ARE the engine bounds ──────────────

        [Fact]
        public async Task EveryTargetInputCarriesTheENGINESBounds_NotTheTemplatesSchemaRange()
        {
            var html = await RenderAsync(plantResult: false);

            var rows = InputRows(html);
            _out.WriteLine($"rendered target inputs: {rows.Count}");
            Assert.True(rows.Count > 0,
                "no number input rendered at all, so this test would pass over an empty page. "
                + "The fix table did not render — check the page's gate, not the bounds.");

            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var byConfigName = store.All()
                .Where(t => t.Kind == RemediationKind.Configuration
                            && t.Operation is not null
                            && t.Operation.OpKind != RemediationOpKind.DbSetOption
                            && !string.IsNullOrWhiteSpace(t.Operation.ConfigName))
                .GroupBy(t => t.Operation!.ConfigName!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var mismatches = new List<string>();
            int compared = 0;

            foreach (var (configName, min, max) in rows)
            {
                if (!byConfigName.TryGetValue(configName, out var template))
                {
                    mismatches.Add($"the page rendered an input for '{configName}', which is not a "
                                 + "shipped sp_configure template — this test cannot check its bounds.");
                    continue;
                }

                var bounds = RemediationValueBounds.ResolveStatic(template.Operation!);
                compared++;
                _out.WriteLine($"'{configName}': input [{min}, {max}] engine [{bounds.Min}, {bounds.Max}]"
                             + (bounds.HasHostCeiling ? " (+ host ceiling at apply time)" : ""));

                if (min != bounds.Min || max != bounds.Max)
                    mismatches.Add($"'{configName}': the box offers [{min}, {max}] and the engine "
                                 + $"enforces [{bounds.Min}, {bounds.Max}].");
            }

            Assert.True(mismatches.Count == 0,
                "The rendered target inputs disagree with the bounds the executor enforces. An "
                + "operator's browser accepts a number the app then refuses after the click.\n"
                + string.Join("\n", mismatches));

            Assert.True(compared >= 3,
                $"only {compared} inputs were compared, which is too few to be reading the real fix "
                + "table. The scan is probably matching the wrong markup.");
        }

        [Fact]
        public async Task TheMaxServerMemoryBox_IsTheOneThatUsedToDisagree_AndIsCheckedByName()
        {
            // The general assertion above would still pass if MAXSERVERMEMORY stopped rendering.
            // This names the case the gate actually proved, so it cannot vanish quietly.
            var html = await RenderAsync(plantResult: false);
            var rows = InputRows(html);

            var mem = rows.FirstOrDefault(r =>
                RemediationValueBounds.IsMaxServerMemory(r.ConfigName));

            Assert.False(string.IsNullOrEmpty(mem.ConfigName),
                "'max server memory (MB)' no longer renders a target input on this page. If the fix "
                + "was removed, remove this test deliberately; do not let it pass by absence.");

            _out.WriteLine($"max server memory input: min={mem.Min} max={mem.Max}");
            Assert.Equal(RemediationValueBounds.MaxServerMemoryFloorMb, mem.Min);
            Assert.NotEqual(128, mem.Min);      // the template's schema floor, which used to be rendered
        }

        // ── BLOCKER 3: the confirmed rollback's REASON reaches the operator ─────────

        [Fact]
        public async Task AConfirmedRollbacksReason_IsRenderedWhereTheOperatorReadsTheResult()
        {
            var html = await RenderAsync(plantResult: true);
            var visible = Visible(html);

            Assert.Contains("Recorded in the audit ledger", visible, StringComparison.Ordinal);
            Assert.Contains("rolled back, value restored", visible, StringComparison.Ordinal);

            // ⚠ THE ASSERTION THIS HALF EXISTS FOR. Before the fix round the page rendered
            // "(rolled back, value restored)" and stopped: the sentence explaining that the engine
            // is STILL USING THE OTHER NUMBER was produced, carried on the result, and dropped at
            // the last layer.
            Assert.Contains("The engine is still using 16", visible, StringComparison.Ordinal);
            Assert.Contains("The configured value is back at 0", visible, StringComparison.Ordinal);
        }

        // ── The harness ────────────────────────────────────────────────────────────

        /// <summary>
        /// Renders the real page with a server selected. When <paramref name="plantResult"/>, a
        /// composed CONFIRMED-rollback result carrying the sentinel is set on the first fix row
        /// before the second render.
        /// </summary>
        private async Task<string> RenderAsync(bool plantResult)
        {
            var settingsDir = Path.Combine(Path.GetTempPath(), "sqlt-bounds-render-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(settingsDir);

            var services = BuildGraph(settingsDir, out var bundle);
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var sp = scope.ServiceProvider;

            Assert.True(sp.GetRequiredService<IRemediationCapability>().IsGranted,
                "the substituted licence did not grant remediation, so the page would render its "
                + "refusal shell and every assertion here would be over the wrong markup. "
                + ServerConfigSuiteGate.DescribeRefusal(bundle));

            var context = sp.GetRequiredService<IServerContextService>();
            var connections = sp.GetRequiredService<ServerConnectionManager>();
            var loggerFactory = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
            await using var renderer = new HtmlRenderer(sp, loggerFactory);

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                ComponentBase? page = null;
                var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["OnCaptured"] = (Action<ComponentBase>)(c => page = c),
                });

                // FIRST render with no server selected: OnInitializedAsync never asks
                // ServerSizingService to read a host, so nothing on this box is contacted.
                var root = await renderer.RenderComponentAsync<CapturingHost>(parameters);
                Assert.NotNull(page);
                await root.QuiescenceTask;

                var id = connections.GetConnections().First().Id;
                var outcome = await context.SetServerAsync(id, ConnectionRetargetGrant.Establish);
                Assert.True(outcome.Applied, "the fixture server was not selected.");

                if (plantResult) PlantConfirmedRollbackResult(page!);

                typeof(ComponentBase).GetMethod("StateHasChanged", F)!.Invoke(page, null);
                await root.QuiescenceTask;

                return root.ToHtmlString();
            });
        }

        /// <summary>
        /// Sets a composed result on the page's first fix row: a verify failure whose rollback was
        /// CONFIRMED and which carries the coercion sentence. Reflection, because FixRow is the
        /// page's own private type — the alternative is a seam that exists only for this test.
        /// </summary>
        private void PlantConfirmedRollbackResult(ComponentBase page)
        {
            var rowsField = page.GetType().GetField("_rows", F);
            Assert.NotNull(rowsField);

            var rows = (IEnumerable)rowsField!.GetValue(page)!;
            var first = rows.Cast<object>().FirstOrDefault();
            Assert.NotNull(first);

            var result = RemediationResult.Applied(
                RemediationOutcome.AppliedVerifyFailed,
                message: "Post-change verify expected 8 but read '16'.",
                rollbackState: RemediationRollbackState.Confirmed,
                preChangeValue: 0,
                rollbackError: Sentinel,
                creditsCharged: 1, creditsCommitted: 0, preChangeValueInUse: 16);

            first!.GetType().GetProperty("Result")!.SetValue(first, result);
            _out.WriteLine("planted a Confirmed-rollback result on the first fix row.");
        }

        private ServiceCollection BuildGraph(string settingsDir, out FakeBundleAccessor bundle)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.None));
            var configuration = new ConfigurationBuilder().Build();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSharedServices(configuration);

            // The store seam is not optional: the default path is the BUILD OUTPUT's
            // Config\server-connections.json, shared by every test in this assembly.
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

        // ── Reading the rendered html ──────────────────────────────────────────────

        /// <summary>
        /// Every fix row that rendered a target box, as (option name, min, max). The option name and
        /// the input are in the same &lt;tr&gt;, which is what makes the pairing sound: matching by
        /// document order across the whole page would silently mis-pair the moment a row is added.
        /// </summary>
        private static List<(string ConfigName, int Min, int Max)> InputRows(string html)
        {
            var rows = new List<(string, int, int)>();
            foreach (var block in Regex.Split(html, "<tr\\b", RegexOptions.IgnoreCase).Skip(1))
            {
                var input = Regex.Match(block, "<input\\b[^>]*type\\s*=\\s*\"number\"[^>]*>", RegexOptions.IgnoreCase);
                if (!input.Success) continue;

                var min = Regex.Match(input.Value, "\\bmin\\s*=\\s*\"(?<v>-?\\d+)\"", RegexOptions.IgnoreCase);
                var max = Regex.Match(input.Value, "\\bmax\\s*=\\s*\"(?<v>-?\\d+)\"", RegexOptions.IgnoreCase);
                if (!min.Success || !max.Success) continue;

                // The option name is the muted line under the display name, before the input.
                var label = Regex.Match(block.Substring(0, input.Index),
                    "<div style=\"color:var\\(--text-muted\\);font-size:12px;\">(?<n>.*?)<",
                    RegexOptions.Singleline);
                if (!label.Success) continue;

                rows.Add((System.Net.WebUtility.HtmlDecode(label.Groups["n"].Value).Trim(),
                          int.Parse(min.Groups["v"].Value),
                          int.Parse(max.Groups["v"].Value)));
            }
            return rows;
        }

        /// <summary>Tags out, entities decoded, whitespace collapsed: the words a person reads.</summary>
        private static string Visible(string html)
            => Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();

        // ── Doubles ────────────────────────────────────────────────────────────────

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
