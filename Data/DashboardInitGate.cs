/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data
{
    /// <summary>
    /// Decides, for ONE dashboard component instance, whether a given Blazor lifecycle pass should
    /// run the dashboard's initialisation: discovery, the cache preload, <c>LoadData</c> over every
    /// panel, and arming the shared auto-refresh timer.
    ///
    /// <para><b>Why it is a class and not two lines in the component.</b> Two separate lifecycle
    /// callbacks used to answer this question independently and both said yes on the same load, so
    /// every dashboard request paid for two complete initialisations. Measured on a throwaway
    /// <c>--server</c> on 2026-09-09 07:24, before this gate existed: one
    /// <c>GET /dashboard/instance</c> logged two "Starting initialization" lines and four
    /// <c>LoadData START</c> lines and took 48.6 s. With one object holding the answer, no ordering
    /// of the callbacks can produce the second load again.</para>
    ///
    /// <para><b>The static prerender pass.</b> Blazor Server renders the component once statically
    /// to produce the HTTP response, before any interactive circuit exists, and it holds that
    /// response until every lifecycle task has finished. Panel loads on that pass are therefore
    /// paid for inside the response, and the singleton refresh timer armed on that pass fires
    /// further loads into components that are still prerendering. This gate answers <c>false</c>
    /// there: the prerender emits the shell, and the circuit does the work once.</para>
    ///
    /// <para><b>⚠ The skip is deliberately hard to trigger.</b> It requires BOTH facts the
    /// framework reports - a non-interactive renderer AND the name of the static renderer. Any host
    /// that fails either test falls through and initialises exactly as it did before this class
    /// existed: the Blazor Server circuit (<c>Server</c>) and any render mode added in future. An
    /// unrecognised host keeping today's behaviour is the safe failure; an unrecognised host
    /// rendering a permanently empty dashboard is not.</para>
    ///
    /// <para><b>⚠⚠ AND A HOST CAN REPORT NOTHING AT ALL - which is what the WPF desktop
    /// app does.</b> The first version of this class was read straight from
    /// <c>ComponentBase.RendererInfo</c>, and its own documentation claimed the desktop
    /// <c>BlazorWebView</c> "falls through and loads exactly as before". That claim was REFUTED by
    /// the cold gate on 2026-09-09. This app ships
    /// <c>Microsoft.AspNetCore.Components.WebView</c> <b>8.0.10</b> (<c>SQLTriage.csproj:306</c>
    /// floats <c>...WebView.Wpf</c> at <c>8.0.*</c>), which predates the .NET 9 <c>RendererInfo</c>
    /// API: its <c>WebViewRenderer</c> does not override <c>Renderer.RendererInfo</c>, the inherited
    /// backing field is never written (0 <c>stfld</c> in <c>Components.dll</c> 10.0.11), and the read
    /// throws <c>InvalidOperationException: "No renderer has been initialized."</c>. Reading it
    /// directly would therefore have thrown on the desktop app's FIRST lifecycle pass, on all ten
    /// pages that embed <c>DynamicDashboard</c>. So the facts are read through
    /// <see cref="TryReadRendererFacts"/> and <c>null</c> - "this host cannot tell me" - is treated
    /// as NOT a prerender pass: initialise, exactly as the component did before this class existed.
    /// Evidence: <c>evidence/dashboard-reload-leak-2026-09-09/gate/rendererinfo-runtime-probe.log</c>
    /// and <c>rendererinfo-il-evidence.log</c>. The package version is pinned by
    /// <c>WebViewRendererInfoPinTests</c>, so a bump that makes the renderer report itself properly
    /// is not silent.</para>
    ///
    /// <para>Not registered in DI and deliberately not shared: one instance per component instance,
    /// holding only that component's own "have I initialised, and for which dashboard" state.</para>
    /// </summary>
    public sealed class DashboardInitGate
    {
        /// <summary>
        /// The framework's name for the static (prerender / static SSR) renderer, as reported by
        /// <c>ComponentBase.RendererInfo.Name</c>. The other names are <c>Server</c>,
        /// <c>WebAssembly</c> and <c>WebView</c>, and every one of them initialises.
        /// </summary>
        public const string StaticRendererName = "Static";

        private bool _initialised;
        private string? _initialisedFor;

        /// <summary>
        /// Whether the renderer running this pass is the static prerender pass - non-interactive
        /// AND named <see cref="StaticRendererName"/>. Both facts are required so that an
        /// unrecognised renderer falls through to loading rather than to an empty page.
        /// </summary>
        public static bool IsStaticPrerenderPass(bool rendererIsInteractive, string? rendererName)
            => !rendererIsInteractive
               && string.Equals(rendererName, StaticRendererName, StringComparison.Ordinal);

        /// <summary>
        /// The two facts the framework reports about the renderer running this pass, carried as one
        /// value so that "I could not read them at all" can be expressed as <c>null</c> rather than
        /// as an invented pair of defaults.
        /// </summary>
        /// <param name="IsInteractive"><c>ComponentBase.RendererInfo.IsInteractive</c>.</param>
        /// <param name="Name"><c>ComponentBase.RendererInfo.Name</c>.</param>
        public readonly record struct RendererFacts(bool IsInteractive, string? Name);

        /// <summary>
        /// THE ONE PLACE <c>ComponentBase.RendererInfo</c> IS READ. Returns <c>null</c> when the
        /// host cannot report the renderer at all, which is NOT a prerender pass.
        ///
        /// <para>Only <see cref="InvalidOperationException"/> is caught, and only that: it is the
        /// exception the framework throws from <c>RenderHandle</c> when the renderer never set its
        /// info ("No renderer has been initialized."), which is exactly the WPF
        /// <c>BlazorWebView</c> case on the 8.0.10 WebView package this app ships. Any other
        /// exception is a bug and propagates.</para>
        /// </summary>
        /// <param name="readRendererInfo">
        /// Reads the facts from the component. In production this is
        /// <c>() =&gt; new RendererFacts(RendererInfo.IsInteractive, RendererInfo.Name)</c>; in a
        /// test it is a stub that throws or answers, so the guard can be exercised without a
        /// renderer.
        /// </param>
        public static RendererFacts? TryReadRendererFacts(Func<RendererFacts> readRendererInfo)
        {
            if (readRendererInfo is null) throw new ArgumentNullException(nameof(readRendererInfo));

            try
            {
                return readRendererInfo();
            }
            catch (InvalidOperationException)
            {
                // "No renderer has been initialized." The host is not telling us anything, so it is
                // not the static prerender pass we skip; initialise as this component always did.
                return null;
            }
        }

        /// <summary>
        /// Whether these facts describe the static prerender pass. Facts that could not be read at
        /// all (<c>null</c>) are NOT that pass - the fail-safe direction, and the whole reason the
        /// desktop app still loads.
        /// </summary>
        public static bool IsStaticPrerenderPass(RendererFacts? facts)
            => facts.HasValue && IsStaticPrerenderPass(facts.Value.IsInteractive, facts.Value.Name);

        /// <summary>
        /// Answers the question once per pass, and records the answer. Returns <c>true</c> exactly
        /// once per dashboard id on an interactive host, and never on a static prerender pass.
        /// </summary>
        /// <param name="dashboardId">The component's current <c>DashboardId</c> parameter.</param>
        /// <param name="facts">
        /// The renderer facts from <see cref="TryReadRendererFacts"/>, or <c>null</c> when the host
        /// could not report them - which initialises, it does not skip.
        /// </param>
        public bool ShouldInitialize(string? dashboardId, RendererFacts? facts)
        {
            if (IsStaticPrerenderPass(facts))
                return false;

            // Ordinal, matching the `_currentDashboardId != DashboardId` comparison this replaced,
            // so a dashboard id differing only in case still re-initialises exactly as before.
            if (_initialised && string.Equals(_initialisedFor, dashboardId, StringComparison.Ordinal))
                return false;

            _initialised = true;
            _initialisedFor = dashboardId;
            return true;
        }

        /// <summary>
        /// The overload for a host whose renderer facts WERE readable. Kept because it is the shape
        /// every caller and test used before the guarded read existed, and because two facts read
        /// together is still the normal case.
        /// </summary>
        public bool ShouldInitialize(string? dashboardId, bool rendererIsInteractive, string? rendererName)
            => ShouldInitialize(dashboardId, new RendererFacts(rendererIsInteractive, rendererName));

        /// <summary>Whether this instance has answered <c>true</c> at least once.</summary>
        public bool HasInitialised => _initialised;

        /// <summary>The dashboard id this instance last answered <c>true</c> for.</summary>
        public string? InitialisedFor => _initialisedFor;
    }
}
