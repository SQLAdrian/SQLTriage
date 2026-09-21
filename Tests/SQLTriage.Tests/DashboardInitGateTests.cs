/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    // BM:DashboardInitGateTests - one dashboard request, one initialisation, none on the prerender
    /// <summary>
    /// <see cref="DashboardInitGate"/> exercised as the object the component actually asks, by
    /// driving it through the Blazor lifecycle in the order Blazor calls it.
    ///
    /// <para><b>What was wrong, measured rather than reasoned.</b> On a throwaway <c>--server</c>
    /// at 2026-09-09 07:24, ONE <c>GET /dashboard/instance</c> against dev main <c>4108cda</c>
    /// logged two "Starting initialization" lines and FOUR <c>LoadData START</c> lines, and took
    /// 48.6 s to first byte (client timeout 300 s; the server's own log shows it completed and
    /// disposed, so this was slow, never stuck). Two of those four loads came from the
    /// process-wide 15-second refresh timer, which the prerender itself armed at 07:24:20 and
    /// which then fired into the component at +15 s and +30 s while it was still prerendering.</para>
    ///
    /// <para><b>Why the gate is tested and not the render.</b> This repo has no bUnit; the
    /// component's seam is this class, and the wiring that makes the component ask it is pinned
    /// separately in <see cref="DashboardPrerenderWiringTests"/>. Neither half is sufficient
    /// alone, which is why both are here.</para>
    /// </summary>
    public class DashboardInitGateTests
    {
        private const string Dash = "instance";

        // The framework's renderer names. Only the first is a prerender pass.
        private const string StaticRenderer = "Static";
        private const string ServerRenderer = "Server";
        private const string WebViewRenderer = "WebView";
        private const string WasmRenderer = "WebAssembly";

        /// <summary>
        /// The static prerender pass does NO initialisation - so no discovery, no panel load, and
        /// no arming of the refresh timer, all of which live behind this one answer.
        ///
        /// <para>The same gate is then asked as an interactive pass and still says yes: a prerender
        /// must not consume the load the circuit is going to need. (In Blazor the circuit gets a
        /// fresh component and therefore a fresh gate; asking the SAME gate is the harder case and
        /// it is the one asserted here.)</para>
        /// </summary>
        [Fact]
        public void A_prerender_pass_initialises_nothing_and_does_not_consume_the_interactive_load()
        {
            var gate = new DashboardInitGate();

            gate.ShouldInitialize(Dash, rendererIsInteractive: false, rendererName: StaticRenderer)
                .Should().BeFalse("the prerender emits the shell; the panel loads and the refresh timer belong to the circuit");
            gate.ShouldInitialize(Dash, rendererIsInteractive: false, rendererName: StaticRenderer)
                .Should().BeFalse("OnParametersSetAsync asks the same question on the same pass");
            gate.HasInitialised.Should().BeFalse("nothing was initialised, so nothing should have been recorded");

            gate.ShouldInitialize(Dash, rendererIsInteractive: true, rendererName: ServerRenderer)
                .Should().BeTrue("the interactive pass must still do the work exactly once");
        }

        /// <summary>
        /// An interactive load initialises EXACTLY ONCE across the whole lifecycle - the defect
        /// this lane fixes. <c>OnInitializedAsync</c> runs first, then <c>OnParametersSetAsync</c>,
        /// then <c>OnParametersSetAsync</c> again on every later parameter set of the parent.
        /// </summary>
        [Theory]
        [InlineData(ServerRenderer)]
        [InlineData(WebViewRenderer)]
        [InlineData(WasmRenderer)]
        public void An_interactive_load_initialises_exactly_once_across_the_whole_lifecycle(string renderer)
        {
            var gate = new DashboardInitGate();
            var inits = 0;

            if (gate.ShouldInitialize(Dash, true, renderer)) inits++;       // OnInitializedAsync
            if (gate.ShouldInitialize(Dash, true, renderer)) inits++;       // OnParametersSetAsync, first pass
            for (var i = 0; i < 5; i++)
                if (gate.ShouldInitialize(Dash, true, renderer)) inits++;   // later parameter sets

            inits.Should().Be(1,
                "before the gate, OnInitializedAsync and the first OnParametersSetAsync each ran a full " +
                "initialisation of the same dashboard - two discoveries, two preloads, two LoadData passes over every panel");
            gate.InitialisedFor.Should().Be(Dash);
        }

        /// <summary>
        /// An in-app navigation to a DIFFERENT dashboard reuses the component instance, and that
        /// genuinely is a new page beginning to load: it re-initialises, once.
        /// </summary>
        [Fact]
        public void Navigating_to_another_dashboard_reinitialises_exactly_once()
        {
            var gate = new DashboardInitGate();
            var inits = 0;

            if (gate.ShouldInitialize("instance", true, ServerRenderer)) inits++;
            if (gate.ShouldInitialize("instance", true, ServerRenderer)) inits++;
            if (gate.ShouldInitialize("repo.checks", true, ServerRenderer)) inits++;   // navigation
            if (gate.ShouldInitialize("repo.checks", true, ServerRenderer)) inits++;
            if (gate.ShouldInitialize("instance", true, ServerRenderer)) inits++;      // and back

            inits.Should().Be(3, "one per dashboard the component is handed, and no more");
            gate.InitialisedFor.Should().Be("instance");
        }

        /// <summary>
        /// THE FAIL-SAFE DIRECTION, and the reason the skip asks for two facts rather than one.
        /// The desktop app renders this same component inside a WPF <c>BlazorWebView</c>. If the
        /// skip triggered on "not interactive" alone and any host ever reported that, the flagship
        /// product would render a permanently empty dashboard. Every row here must LOAD.
        /// </summary>
        [Theory]
        [InlineData(true, ServerRenderer)]     // Blazor Server circuit
        [InlineData(true, WebViewRenderer)]    // WPF BlazorWebView - the desktop app
        [InlineData(true, WasmRenderer)]
        [InlineData(true, StaticRenderer)]     // interactive but oddly named: still loads
        [InlineData(false, WebViewRenderer)]   // non-interactive but not the static renderer: still loads
        [InlineData(false, ServerRenderer)]
        [InlineData(false, null)]              // unknown host: keeps today's behaviour
        [InlineData(false, "")]
        [InlineData(false, "static")]          // case matters: only the exact framework name skips
        public void Any_host_that_is_not_the_static_prerender_pass_still_loads(bool isInteractive, string? renderer)
        {
            DashboardInitGate.IsStaticPrerenderPass(isInteractive, renderer)
                .Should().BeFalse("only a non-interactive renderer NAMED 'Static' is the prerender pass");

            new DashboardInitGate().ShouldInitialize(Dash, isInteractive, renderer)
                .Should().BeTrue("an unrecognised host must keep today's behaviour, not render an empty dashboard");
        }

        /// <summary>
        /// A HOST THAT CANNOT REPORT ITS RENDERER AT ALL STILL LOADS, and the read does not blow up
        /// the component. This is the defect the cold gate found on 2026-09-09: the WPF desktop app
        /// ships Microsoft.AspNetCore.Components.WebView 8.0.10, whose WebViewRenderer predates the
        /// .NET 9 RendererInfo API and never overrides it, so ComponentBase.RendererInfo THROWS
        /// InvalidOperationException("No renderer has been initialized.") - on the FIRST lifecycle
        /// pass, on all ten pages that embed DynamicDashboard. The stub here throws exactly what
        /// the framework throws; the gate must swallow it and answer "initialise".
        /// </summary>
        [Fact]
        public void A_host_whose_RendererInfo_throws_still_initialises_and_nothing_propagates()
        {
            var reads = 0;
            DashboardInitGate.RendererFacts? facts = null;

            Action read = () => facts = DashboardInitGate.TryReadRendererFacts(() =>
            {
                reads++;
                throw new InvalidOperationException("No renderer has been initialized.");
            });

            read.Should().NotThrow(
                "the WPF BlazorWebView's renderer cannot answer this question, and a dashboard page " +
                "that throws on its first lifecycle pass is worse than one that loads twice");

            reads.Should().Be(1, "the facts are read once per pass, through the one guarded accessor");
            facts.Should().BeNull("an unreadable renderer reports nothing, not an invented pair of defaults");

            DashboardInitGate.IsStaticPrerenderPass(facts).Should().BeFalse(
                "'I cannot tell you' is not 'this is the static prerender pass'");
            new DashboardInitGate().ShouldInitialize(Dash, facts).Should().BeTrue(
                "the desktop app must load exactly as it did before this gate existed");
        }

        /// <summary>
        /// ONLY the framework's "no renderer" exception is swallowed. A catch-all here would hide a
        /// real bug in the component's own lambda behind a silent extra initialisation.
        /// </summary>
        [Fact]
        public void Only_the_no_renderer_exception_is_swallowed()
        {
            Action read = () => DashboardInitGate.TryReadRendererFacts(
                () => throw new NotSupportedException("a real bug, not a missing renderer"));

            read.Should().Throw<NotSupportedException>(
                "InvalidOperationException is the framework's 'No renderer has been initialized.'; " +
                "everything else is a defect and must reach the log");
        }

        /// <summary>
        /// NULL FACTS INITIALISE. Stated on its own, without the throwing stub, because this is the
        /// decision the fail-safe rests on: the null is produced by the guard above, and every
        /// consumer of it must read it the same way.
        /// </summary>
        [Fact]
        public void Renderer_facts_that_could_not_be_read_are_not_a_prerender_pass()
        {
            DashboardInitGate.RendererFacts? unknown = null;

            DashboardInitGate.IsStaticPrerenderPass(unknown).Should().BeFalse(
                "only a non-interactive renderer NAMED 'Static' is the prerender pass; an unreadable " +
                "one is an unrecognised host, which keeps today's behaviour");

            var gate = new DashboardInitGate();
            gate.ShouldInitialize(Dash, unknown).Should().BeTrue("first pass on an unknown host: load");
            gate.ShouldInitialize(Dash, unknown).Should().BeFalse("and still exactly once per dashboard");
            gate.HasInitialised.Should().BeTrue();
        }

        /// <summary>Facts that WERE readable behave exactly as the three-argument overload does.</summary>
        [Theory]
        [InlineData(false, StaticRenderer, false)]
        [InlineData(true, ServerRenderer, true)]
        [InlineData(true, WebViewRenderer, true)]
        [InlineData(false, WebViewRenderer, true)]
        public void Readable_facts_answer_exactly_as_the_two_fact_overload_does(
            bool isInteractive, string? renderer, bool expected)
        {
            new DashboardInitGate()
                .ShouldInitialize(Dash, new DashboardInitGate.RendererFacts(isInteractive, renderer))
                .Should().Be(expected);
            new DashboardInitGate()
                .ShouldInitialize(Dash, isInteractive, renderer)
                .Should().Be(expected, "the overload must not be a second, divergent answer");
        }

        /// <summary>The one combination that IS the prerender pass.</summary>
        [Fact]
        public void The_static_non_interactive_pass_is_the_only_prerender_pass()
        {
            DashboardInitGate.IsStaticPrerenderPass(false, DashboardInitGate.StaticRendererName)
                .Should().BeTrue();
            DashboardInitGate.StaticRendererName.Should().Be("Static",
                "this is the framework's own name for the static renderer, not ours to choose");
        }
    }

    // BM:DashboardPrerenderWiringTests - the component asks the gate, and asks it at every door
    /// <summary>
    /// The other half of the proof: that <c>DynamicDashboard.razor</c> actually routes its
    /// initialisation through <see cref="DashboardInitGate"/>, at every call site, and that the
    /// refresh timer and the unsubscribe are where the fix assumes they are.
    ///
    /// <para><b>This file knows it is a source scanner.</b> The class above exercises the decision;
    /// this one only pins the wiring, because a perfect gate the component never consults fixes
    /// nothing. Read the pair together.</para>
    ///
    /// <para><b>And it scans CODE, not prose.</b> The first draft of
    /// <see cref="The_shared_refresh_timer_is_armed_only_from_inside_the_gated_initialisation"/>
    /// counted two arming sites and failed on both axes, because the second "site" was the phrase
    /// naming that call inside a documentation comment added by the same change. A source scanner
    /// that cannot tell a call from a sentence about a call reports defects that do not exist - so
    /// every assertion here runs over <see cref="CodeOnly"/>.</para>
    /// </summary>
    public class DashboardPrerenderWiringTests
    {
        private static string RazorPath() => Path.Combine(
            RawPassedScan.RepoRoot().FullName, "Components", "Shared", "DynamicDashboard.razor");

        private static bool IsComment(string line) =>
            line.TrimStart().StartsWith("//", StringComparison.Ordinal);

        /// <summary>The component's source with every <c>//</c> and <c>///</c> line removed.</summary>
        private static string CodeOnly() =>
            string.Join("\n", File.ReadAllLines(RazorPath()).Where(l => !IsComment(l)));

        /// <summary>
        /// Every <c>await InitializeDashboard()</c> in the component is guarded by the gate. Before
        /// the fix there were two call sites and neither could see the other's answer.
        /// </summary>
        [Fact]
        public void Every_initialisation_call_site_is_behind_the_gate()
        {
            var callSites = File.ReadAllLines(RazorPath())
                .Select((text, i) => (text, line: i + 1))
                .Where(x => !IsComment(x.text))
                .Where(x => Regex.IsMatch(x.text, @"await\s+InitializeDashboard\s*\(\s*\)"))
                .ToList();

            callSites.Should().NotBeEmpty("the component still has to initialise somewhere");

            foreach (var (text, line) in callSites)
            {
                text.Should().Contain("ShouldInitializeThisPass(",
                    $"DynamicDashboard.razor:{line} runs a full dashboard initialisation - discovery, " +
                    "preload, LoadData over every panel, and the arming of the shared refresh timer - " +
                    "so it must ask the gate first");
            }
        }

        /// <summary>
        /// The gate is asked with the framework's own renderer facts, both of them, AND THE READ IS
        /// GUARDED. <c>ComponentBase.RendererInfo</c> throws inside the WPF <c>BlazorWebView</c> on
        /// the 8.0.10 WebView package this app ships (cold gate, 2026-09-09), so an unguarded read
        /// anywhere in this component throws on the desktop app's first lifecycle pass. This is the
        /// assertion that keeps the second read from creeping back in.
        /// </summary>
        [Fact]
        public void The_gate_is_asked_with_the_frameworks_own_renderer_facts_through_the_guard()
        {
            var code = CodeOnly();

            Regex.IsMatch(code,
                @"DashboardInitGate\.TryReadRendererFacts\(\s*\(\)\s*=>\s*new DashboardInitGate\.RendererFacts\("
                + @"RendererInfo\.IsInteractive,\s*RendererInfo\.Name\s*\)\s*\)")
                .Should().BeTrue(
                    "the facts must be read through the one guarded accessor, and with the framework's " +
                    "real name and interactivity - a constant would silently disable the fix or the fail-safe");

            code.Should().Contain("_initGate.ShouldInitialize(DashboardId, facts)",
                "the gate decides from the guarded facts, not from a second read");

            var readingLines = code.Split('\n')
                .Where(l => Regex.IsMatch(l, @"RendererInfo\s*\."))
                .ToList();

            readingLines.Should().HaveCount(1,
                "RendererInfo is read from EXACTLY ONE line in this component - the lambda handed to " +
                "TryReadRendererFacts. Every other read is unguarded and throws in the desktop app; " +
                "there were three such reads before the fix. Lines found: " +
                string.Join(" | ", readingLines.Select(l => l.Trim())));

            readingLines[0].Should().Contain("new DashboardInitGate.RendererFacts(",
                "and that one line is inside the guard");

            code.Should().NotContain("_currentDashboardId",
                "the flag whose initial empty value produced the second full initialisation per request is gone; " +
                "reintroducing it reintroduces the defect (comments still narrate it by name, deliberately - " +
                "the history is the reason the gate exists, which is why this assertion runs over CodeOnly)");
        }

        /// <summary>
        /// The arming of the process-wide 15-second timer shared by every dashboard on this server
        /// is reached only from inside <c>InitializeDashboard</c>, which is now behind the gate.
        /// That is what stops a prerender arming it.
        /// </summary>
        [Fact]
        public void The_shared_refresh_timer_is_armed_only_from_inside_the_gated_initialisation()
        {
            var code = CodeOnly();

            Regex.Matches(code, @"RefreshService\.Start\s*\(\s*\)").Count.Should().Be(1,
                "one arming site keeps the gate sufficient; a second one would need its own guard");

            var initDecl = code.IndexOf("private async Task InitializeDashboard()", StringComparison.Ordinal);
            var start = code.IndexOf("RefreshService.Start()", StringComparison.Ordinal);
            var initEnd = code.IndexOf("Initialization completed successfully", StringComparison.Ordinal);

            initDecl.Should().BeGreaterThan(-1);
            start.Should().BeGreaterThan(initDecl, "the timer must be armed inside InitializeDashboard, not before it");
            start.Should().BeLessThan(initEnd, "and before that method's own completion log line");
        }

        /// <summary>
        /// Item 3(c) of the lane brief: the subscribe/unsubscribe pair was already correct and is
        /// NOT part of the fix. Pinned so a later reading of the old "leak" story cannot quietly
        /// remove it. A stop-the-timer call on disposal is deliberately absent: the timer is
        /// shared, so one dashboard closing must not stop it for the others.
        /// </summary>
        [Fact]
        public void Disposal_still_unsubscribes_from_the_shared_refresh_service()
        {
            var code = CodeOnly();

            Regex.Matches(code, @"RefreshService\.OnRefresh\s*\+=\s*OnAutoRefresh").Count.Should().Be(1);
            Regex.Matches(code, @"RefreshService\.OnRefresh\s*-=\s*OnAutoRefresh").Count.Should().Be(1);

            var dispose = code.IndexOf("public void Dispose()", StringComparison.Ordinal);
            var unsubscribe = code.IndexOf("RefreshService.OnRefresh -= OnAutoRefresh", StringComparison.Ordinal);
            dispose.Should().BeGreaterThan(-1);
            unsubscribe.Should().BeGreaterThan(dispose, "the unsubscribe belongs inside Dispose()");
        }
    }

    // BM:WebViewRendererInfoPinTests - the guard is load-bearing only while the shipped renderer is silent
    /// <summary>
    /// WHY THE GUARD EXISTS, pinned against the assembly the app actually ships rather than against
    /// a sentence about it.
    ///
    /// <para>Reflection over the shipped <c>Microsoft.AspNetCore.Components.WebView.dll</c> (the
    /// copy beside this test binary, put there by the app's own project reference): its
    /// <c>WebViewRenderer</c> does not DECLARE <c>RendererInfo</c>, so the property resolves to
    /// <c>Renderer</c>'s, whose backing field is never written, and
    /// <c>ComponentBase.RendererInfo</c> throws
    /// <c>InvalidOperationException("No renderer has been initialized.")</c> in the WPF desktop
    /// app. Measured by the cold gate on 2026-09-09 with a live two-armed probe (a non-overriding
    /// renderer threw; a renderer that DOES override answered <c>Name='WebView'</c>) - see
    /// <c>evidence/dashboard-reload-leak-2026-09-09/gate/rendererinfo-runtime-probe.log</c>.</para>
    ///
    /// <para><b>When this test fails, nothing is broken.</b> It means
    /// <c>Microsoft.AspNetCore.Components.WebView.Wpf</c> (floated at <c>8.0.*</c> in
    /// <c>SQLTriage.csproj</c>) has moved to a version whose renderer reports itself properly. The
    /// guard in <see cref="DashboardInitGate.TryReadRendererFacts"/> is then harmless but no longer
    /// load-bearing, and the WebView row of the fail-safe table can be proved live instead of
    /// assumed. Read the failure as a prompt, not a regression.</para>
    /// </summary>
    public class WebViewRendererInfoPinTests
    {
        private const string WebViewAssemblyFile = "Microsoft.AspNetCore.Components.WebView.dll";
        private const string RendererTypeName = "Microsoft.AspNetCore.Components.WebView.Services.WebViewRenderer";
        private const string PropertyName = "RendererInfo";

        [Fact]
        public void The_shipped_WebView_renderer_still_does_not_report_its_RendererInfo()
        {
            var path = Path.Combine(AppContext.BaseDirectory, WebViewAssemblyFile);
            File.Exists(path).Should().BeTrue(
                $"the app's project reference copies {WebViewAssemblyFile} beside this test binary; " +
                $"without it this test would assert nothing. Looked in {AppContext.BaseDirectory}");

            var assembly = Assembly.LoadFrom(path);
            var renderer = assembly.GetType(RendererTypeName, throwOnError: false);
            renderer.Should().NotBeNull(
                $"{RendererTypeName} is the renderer the WPF BlazorWebView runs this app's components in");

            const BindingFlags Members =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            Type? declaredBy = null;
            for (var t = renderer; t != null; t = t.BaseType)
            {
                if (t.GetProperty(PropertyName, Members) != null) { declaredBy = t; break; }
            }

            declaredBy.Should().NotBeNull(
                $"{PropertyName} must exist somewhere in the renderer's base chain, or this test is " +
                "reading the wrong member and asserting nothing");

            declaredBy.Should().NotBe(renderer,
                $"THE GUARD EXISTS BECAUSE OF THIS: {RendererTypeName} in " +
                $"{assembly.GetName().Name} {assembly.GetName().Version} does NOT override {PropertyName}, " +
                $"it inherits {declaredBy!.FullName}'s - whose backing field is never written - so " +
                "ComponentBase.RendererInfo THROWS 'No renderer has been initialized.' inside the WPF " +
                "desktop app. DynamicDashboard therefore reads it through " +
                "DashboardInitGate.TryReadRendererFacts and treats an unreadable renderer as 'not a " +
                "prerender pass: initialise'. IF THIS ASSERTION FAILS the package was bumped to a " +
                "version that DOES report RendererInfo: the guard becomes belt-and-braces rather than " +
                "load-bearing, and the WebView row of the fail-safe table can be re-proved live.");
        }
    }
}
