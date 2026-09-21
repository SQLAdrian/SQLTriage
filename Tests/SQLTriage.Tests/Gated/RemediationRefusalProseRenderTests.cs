/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationRefusalProseRenderTests — the PAGE-WIDE guard that no RemediationRefusal enum member
 * reaches an operator's browser from Pages/Remediation.razor (lane/remediation-enum-prose, 2026-09-02).
 *
 * WHY IT EXISTS, AND WHY THE P3 GUARD WAS NOT ENOUGH. Gated/RemediationBatchApplyRenderTests carries
 * AssertNoRefusalEnumTokens, and it is a good guard — over the BATCH SECTION. It is handed
 * SectionHtml(html), the .rbatch-section div and nothing else. Everything on the page outside that
 * div was never scanned, and the P3 gate's own catalogue recorded seven surviving raw-enum render
 * sites out there. A scan scoped to one div reports the other twelve hundred lines clean.
 *
 * ⚠ AND THE CATALOGUE UNDERCOUNTED. Enumerated at HEAD rather than trusted, the page carried
 * FOURTEEN raw `@x.Refusal` render sites, not seven: every one of the seven catalogued PREVIEW sites
 * has a RESULT twin twenty to fifty lines below it, rendering the same `Refused at gate: @x.Refusal`
 * for the apply half. The catalogue had recorded one of each pair. The house lesson
 * [[verify-cardinality-not-just-presence]] is exactly this shape, so this file asserts each site
 * SEPARATELY and by name: a failure says which surface regressed, never just "the page".
 *
 * ⚠ A FIFTEENTH SITE IS NOT A RENDER SITE AT ALL. BatchRemediationDriver.DescribeStop baked
 * `was refused at a gate ({r.Refusal})` into BatchApplyResult.StopReason, which Remediation.razor
 * renders verbatim INSIDE the batch section — the section the P3 guard did scan. It stayed green
 * because the P3 fixture plants a hand-written StopReason naming a CouldNotRun stop, so the refusal
 * arm of that producer was never executed by any test. A guard is only as wide as its fixture, which
 * is why TheStopReasonProducer_NeverBakesAnEnumMemberIntoItsSentence below drives the producer
 * itself over every member of the enum rather than trusting one planted string.
 *
 * ⚠ THE SUBSTITUTIONS, STATED PLAINLY, SO NO CLAIM HERE IS OVER-READ. They are the same four
 * RemediationBatchApplyRenderTests makes, repeated rather than cross-referenced because a reader of
 * an assertion needs them beside it:
 *
 *   1. THE LICENCE SOURCE IS A FAKE. FakeBundleAccessor stands in for a signed bundle file. The gate
 *      LOGIC is real; the SIGNATURE check on a minted bundle is not exercised here.
 *   2. THE FINDINGS ARE SYNTHETIC. CheckResult objects this test composes, not a captured audit run.
 *   3. THE PROPOSALS AND RESULTS ARE PLANTED. A real refusal needs a real gate and a real server.
 *      Every RemediationProposal / RemediationResult here is built by this test and set on the page's
 *      own fields. What this file proves is the RENDERING of a given refusal, never that a gate
 *      produces it. The gates' own answers are proved by RemediationRunnerTests and the live smokes.
 *   4. NO SERVER IS CONTACTED and nothing is applied. No event is dispatched, so no handler runs.
 *
 * ⚠ WHY THIS IS IN Gated/. It reaches SQLTriage.Pages.Remediation, which buildprofile.targets
 * Content-Removes from a COMMUNITY build. The Gated\**\*.cs glob removes this folder from the
 * community test build, so this runs only where the page exists.
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
    public sealed class RemediationRefusalProseRenderTests
    {
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Server = "SQLT-REFUSAL-PROSE-FIXTURE";

        private readonly ITestOutputHelper _out;
        public RemediationRefusalProseRenderTests(ITestOutputHelper output) => _out = output;

        /// <summary>
        /// Copy from the page's own shell, asserted before anything else. Without it every scan
        /// below could run over the LICENCE-REFUSAL shell (or an initialiser that threw) and report
        /// a clean page, which is the failure mode that makes a render guard worthless. Deliberately
        /// NOT "Batch preview:", which needs a planted reading this fixture does not set.
        /// </summary>
        private const string ShellAnchor = "Preview several fixes at once";

        /// <summary>
        /// One refusal surface on the page: the name a failure should print, the sentinel planted in
        /// its gate message so the rendered block can be found, and which member of the enum it was
        /// refused with. The members are cycled deliberately, so no single member's prose can carry
        /// every site.
        /// </summary>
        private sealed record Site(string Name, string Sentinel, RemediationRefusal Refusal);

        private static readonly Site[] Sites =
        {
            new("fix row / preview",            "SENTINEL-FIXROW-PREVIEW",     RemediationRefusal.NotARegisteredTemplate),
            new("fix row / result",             "SENTINEL-FIXROW-RESULT",      RemediationRefusal.CapabilityDenied),
            new("alert pack / preview",         "SENTINEL-ALERTPACK-PREVIEW",  RemediationRefusal.InsufficientCredits),
            new("alert pack / result",          "SENTINEL-ALERTPACK-RESULT",   RemediationRefusal.NotApproved),
            new("maintenance install / preview","SENTINEL-MAINTINST-PREVIEW",  RemediationRefusal.AuditNotWritable),
            new("maintenance install / result", "SENTINEL-MAINTINST-RESULT",   RemediationRefusal.NotARegisteredTemplate),
            new("maintenance lane / preview",   "SENTINEL-MAINTLANE-PREVIEW",  RemediationRefusal.CapabilityDenied),
            new("maintenance lane / result",    "SENTINEL-MAINTLANE-RESULT",   RemediationRefusal.InsufficientCredits),
            new("backup now / preview",         "SENTINEL-BACKUPNOW-PREVIEW",  RemediationRefusal.NotApproved),
            new("backup now / result",          "SENTINEL-BACKUPNOW-RESULT",   RemediationRefusal.AuditNotWritable),
            new("checkdb now / preview",        "SENTINEL-CHECKDBNOW-PREVIEW", RemediationRefusal.NotARegisteredTemplate),
            new("checkdb now / result",         "SENTINEL-CHECKDBNOW-RESULT",  RemediationRefusal.CapabilityDenied),
            new("index row / preview",          "SENTINEL-INDEXROW-PREVIEW",   RemediationRefusal.InsufficientCredits),
            new("index row / result",           "SENTINEL-INDEXROW-RESULT",    RemediationRefusal.NotApproved),
            new("batch stop reason",            "SENTINEL-BATCHSTOP",          RemediationRefusal.AuditNotWritable),
        };

        // ── 1. EVERY SITE, BY NAME ───────────────────────────────────────────────

        [Fact]
        public async Task EveryRefusalSurfaceOnThePage_ReadsAsPlainWords_AndNamesTheNextStep()
        {
            var html = await RenderWithEveryRefusalSurfaceRefusedAsync();

            // ⚠ THE PRECONDITION. Without it every assertion below could pass over a page that
            // rendered its licence-refusal shell, or threw in an initialiser, and reported clean.
            Assert.False(string.IsNullOrWhiteSpace(html), "the page did not render at all");
            Assert.Contains(ShellAnchor, html, StringComparison.Ordinal);

            var failures = new List<string>();
            foreach (var site in Sites)
            {
                var block = BlockContaining(html, site.Sentinel);
                if (string.IsNullOrEmpty(block))
                {
                    failures.Add($"[{site.Name}] did not render: the planted gate message "
                        + $"'{site.Sentinel}' is absent from the page html, so this site was never "
                        + "scanned and its refusal wording is UNPROVED by this run.");
                    continue;
                }

                var visible = Visible(block);
                _out.WriteLine($"{site.Name}: {visible}");

                var words = RemediationRefusalProse.Describe(site.Refusal);
                if (!visible.Contains(words, StringComparison.Ordinal))
                    failures.Add($"[{site.Name}] does not carry the plain-word sentence for "
                        + $"{site.Refusal}. Expected: '{words}'. Rendered: {visible}");

                var next = RemediationRefusalProse.DescribeNextStep(site.Refusal);
                if (!visible.Contains(next, StringComparison.Ordinal))
                    failures.Add($"[{site.Name}] tells the operator what happened and not what to do. "
                        + $"Expected the next step: '{next}'. Rendered: {visible}");

                foreach (var name in Enum.GetNames(typeof(RemediationRefusal)))
                    if (Regex.IsMatch(block, @"\b" + Regex.Escape(name) + @"\b"))
                        failures.Add($"[{site.Name}] carries the RemediationRefusal member '{name}'. "
                            + "An operator reads words, not enum members: route it through "
                            + "RemediationRefusalProse. Rendered: " + visible);
            }

            Assert.True(failures.Count == 0,
                $"{failures.Count} refusal surface(s) failed:{Environment.NewLine}"
                + string.Join(Environment.NewLine, failures));
        }

        // ── 2. THE WHOLE PAGE, NOT ONE SECTION ───────────────────────────────────

        [Fact]
        public async Task NoRefusalEnumMemberAppearsAnywhereInTheRenderedPage()
        {
            var html = await RenderWithEveryRefusalSurfaceRefusedAsync();
            Assert.Contains(ShellAnchor, html, StringComparison.Ordinal);

            // The catch-all behind the per-site list above. It scans the RAW html of the WHOLE page,
            // so a member hiding in a title= attribute, a css class or a surface nobody catalogued is
            // caught too. The members are enumerated off the type, so one added later is covered
            // without anyone editing this line.
            foreach (var name in Enum.GetNames(typeof(RemediationRefusal)))
            {
                var hit = Regex.Match(html, @"\b" + Regex.Escape(name) + @"\b");
                Assert.False(hit.Success,
                    $"The rendered page carries the RemediationRefusal member '{name}'. Context: "
                    + html.Substring(Math.Max(0, hit.Index - 120),
                        Math.Min(300, html.Length - Math.Max(0, hit.Index - 120))));
            }
        }

        // ── 3. THE PRODUCER BEHIND THE FIFTEENTH SITE ────────────────────────────

        [Fact]
        public void TheStopReasonProducer_NeverBakesAnEnumMemberIntoItsSentence()
        {
            // Driven over EVERY member rather than the one a fixture happened to plant. The defect
            // this closes was invisible to the P3 render guard for exactly that reason: its fixture
            // stopped the batch on a CouldNotRun, so the refusal arm of this producer never ran.
            foreach (RemediationRefusal refusal in Enum.GetValues(typeof(RemediationRefusal)))
            {
                var sentence = BatchRemediationDriver.DescribeStop(
                    "MAXDOP", RemediationResult.Refused(refusal, "The gate said no."));
                _out.WriteLine($"{refusal}: {sentence}");

                foreach (var name in Enum.GetNames(typeof(RemediationRefusal)))
                    Assert.False(Regex.IsMatch(sentence, @"\b" + Regex.Escape(name) + @"\b"),
                        $"BatchRemediationDriver.DescribeStop baked the enum member '{name}' into the "
                        + "stop reason, and Pages/Remediation.razor renders that string verbatim. "
                        + "Sentence: " + sentence);

                Assert.Contains(RemediationRefusalProse.Describe(refusal), sentence, StringComparison.Ordinal);
                Assert.Contains("MAXDOP", sentence, StringComparison.Ordinal);
            }
        }

        // ── 4. THE PROSE ITSELF: every member answered, nothing falling to a default

        [Fact]
        public void EveryRefusalMember_HasItsOwnSentenceAndItsOwnNextStep()
        {
            // A switch whose members all fall through to `_ => "the app refused it"` would pass every
            // render assertion above while telling five different operators the same nothing. Both
            // producers are asserted DISTINCT across the enum, and distinct from the fallbacks.
            var fallbackWords = RemediationRefusalProse.Describe(null);
            var fallbackNext = RemediationRefusalProse.DescribeNextStep(null);

            var words = new List<string>();
            var steps = new List<string>();
            foreach (RemediationRefusal refusal in Enum.GetValues(typeof(RemediationRefusal)))
            {
                var w = RemediationRefusalProse.Describe(refusal);
                var s = RemediationRefusalProse.DescribeNextStep(refusal);

                Assert.False(string.IsNullOrWhiteSpace(w), $"{refusal} has no sentence");
                Assert.False(string.IsNullOrWhiteSpace(s), $"{refusal} has no next step");
                Assert.NotEqual(fallbackWords, w);
                Assert.NotEqual(fallbackNext, s);

                // Never the member's own name, however the sentence is written.
                var ownName = @"\b" + Regex.Escape(refusal.ToString()) + @"\b";
                Assert.DoesNotMatch(ownName, w);
                Assert.DoesNotMatch(ownName, s);

                words.Add(w);
                steps.Add(s);
            }

            Assert.Equal(words.Count, words.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(steps.Count, steps.Distinct(StringComparer.Ordinal).Count());
        }

        // ── The harness ──────────────────────────────────────────────────────────

        /// <summary>
        /// Renders the real page with EVERY refusal-bearing surface open and refused. No event is
        /// dispatched: the fields are set directly, so no handler runs and nothing is applied.
        /// </summary>
        private async Task<string> RenderWithEveryRefusalSurfaceRefusedAsync()
        {
            var settingsDir = Path.Combine(Path.GetTempPath(), "sqlt-refusal-prose-" + Guid.NewGuid().ToString("N"));
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
                Assert.True(outcome.Applied, "the fixture server was not selected");
                quick.Results = Findings();
                quick.HasRun = true;

                var type = page!.GetType();
                typeof(ComponentBase).GetMethod("StateHasChanged", F)!.Invoke(page, null);
                await root.QuiescenceTask;

                PlantEveryRefusal(page!, type);

                typeof(ComponentBase).GetMethod("StateHasChanged", F)!.Invoke(page, null);
                await root.QuiescenceTask;

                return root.ToHtmlString();
            });
        }

        /// <summary>
        /// Opens every collapsible section that can show a refusal and plants one refusal on each of
        /// the fifteen surfaces. Reflection throughout, because the page's row types are private
        /// nested classes and its state fields are private: the alternative is widening production
        /// visibility for a test, which would change the thing under test.
        /// </summary>
        private static void PlantEveryRefusal(ComponentBase page, Type type)
        {
            foreach (var flag in new[] { "_alertPackOpen", "_maintOpen", "_backupNowOpen", "_checkDbNowOpen", "_indexOpen", "_batchOpen" })
                Field(type, flag).SetValue(page, true);

            // ── The four proposal/result pairs held in plain fields.
            Set(page, type, "_alertPackProposal", Proposal("alert pack / preview"));
            Set(page, type, "_alertPackResult", Result("alert pack / result"));
            Set(page, type, "_maintInstallProposal", Proposal("maintenance install / preview"));
            Set(page, type, "_maintInstallResult", Result("maintenance install / result"));
            Set(page, type, "_backupNowProposal", Proposal("backup now / preview"));
            Set(page, type, "_backupNowResult", Result("backup now / result"));
            Set(page, type, "_checkDbNowProposal", Proposal("checkdb now / preview"));
            Set(page, type, "_checkDbNowResult", Result("checkdb now / result"));

            // ── The three row collections. The first two are populated by the page itself.
            var rows = (IList)Field(type, "_rows").GetValue(page)!;
            Assert.True(rows.Count > 0,
                "the page seeded no fix rows, so the fix-row refusal sites could not be planted and "
                + "would report clean without being scanned");
            SetOnRow(rows[0]!, Proposal("fix row / preview"), Result("fix row / result"));

            var lanes = (IList)Field(type, "_maintLaneRows").GetValue(page)!;
            Assert.True(lanes.Count > 0, "the page seeded no maintenance lane rows");
            SetOnRow(lanes[0]!, Proposal("maintenance lane / preview"), Result("maintenance lane / result"));

            // ── The index rows are loaded from a live DMV read, so one is built here through the
            //    page's own private row type rather than by contacting a server.
            var indexRows = (IList)Field(type, "_indexRows").GetValue(page)!;
            var rowType = type.GetNestedType("IndexFixRow", BindingFlags.NonPublic)!;
            var indexRow = Activator.CreateInstance(rowType, nonPublic: true)!;
            rowType.GetProperty("Candidate")!.SetValue(indexRow, new MissingIndexCandidate
            {
                Database = "FixtureDb",
                Schema = "dbo",
                Table = "FixtureTable",
                SuggestedName = "IX_FixtureTable_Fixture",
                KeyColumns = new List<string> { "ColumnA" },
                IncludedColumns = new List<string> { "ColumnB" },
                UserSeeks = 10,
                AvgImpact = 50,
                EstimatedBenefit = 500,
            });
            SetOnRow(indexRow, Proposal("index row / preview"), Result("index row / result"));
            indexRows.Add(indexRow);

            // ── The fifteenth site: the batch stop reason, built by the SHIPPED producer over a real
            //    refusal rather than typed out, so this test measures that producer and not itself.
            Set(page, type, "_batchApply", new BatchApplyResult
            {
                RunId = "refusal-prose-fixture-run-id",
                ServerName = Server,
                Message = "The batch stopped after an item was refused.",
                TotalReserved = 1,
                TotalCommitted = 0,
                Stopped = true,
                StopReason = BatchRemediationDriver.DescribeStop("MAXDOP", Result("batch stop reason")),
                PerItem = Array.Empty<BatchApplyItemResult>(),
            });
        }

        private static Site Find(string name) =>
            Sites.Single(s => string.Equals(s.Name, name, StringComparison.Ordinal));

        /// <summary>A refused proposal for one named site, carrying that site's sentinel.</summary>
        private static RemediationProposal Proposal(string siteName)
        {
            var site = Find(siteName);
            return RemediationProposal.Refused(site.Refusal, "The gate said no. " + site.Sentinel);
        }

        /// <summary>A refused result for one named site, carrying that site's sentinel.</summary>
        private static RemediationResult Result(string siteName)
        {
            var site = Find(siteName);
            return RemediationResult.Refused(site.Refusal, "The gate said no. " + site.Sentinel);
        }

        private static FieldInfo Field(Type type, string name) =>
            type.GetField(name, F)
            ?? throw new InvalidOperationException(
                $"Pages/Remediation.razor no longer carries the field '{name}'. This guard plants "
                + "state on that field, so a rename silently narrows what it scans: update the "
                + "harness rather than deleting the site.");

        private static void Set(ComponentBase page, Type type, string name, object value) =>
            Field(type, name).SetValue(page, value);

        private static void SetOnRow(object row, RemediationProposal proposal, RemediationResult result)
        {
            row.GetType().GetProperty("Proposal")!.SetValue(row, proposal);
            row.GetType().GetProperty("Result")!.SetValue(row, result);
        }

        // ── Reading the rendered html ────────────────────────────────────────────

        /// <summary>
        /// The innermost &lt;pre&gt; or &lt;p&gt; carrying a needle, so a per-site assertion is made
        /// over that site's own block and a failure names it. "" when the needle never rendered.
        /// </summary>
        private static string BlockContaining(string html, string needle)
        {
            foreach (Match m in Regex.Matches(html, @"<(pre|p)\b[^>]*>.*?</\1>",
                                              RegexOptions.Singleline | RegexOptions.IgnoreCase))
                if (m.Value.Contains(needle, StringComparison.Ordinal)) return m.Value;
            return string.Empty;
        }

        /// <summary>Tags out, entities decoded, whitespace collapsed: the words a person reads.</summary>
        private static string Visible(string html)
            => Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();

        // ── The graph ────────────────────────────────────────────────────────────

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
