/* In the name of God, the Merciful, the Compassionate */

// ── The BOOTSTRAP-ADMIN banner, RENDERED, 2026-09-07 ─────────────────────────────────────────
//
// ⚠⚠ WHY THIS FILE EXISTS. Adrian's fresh-eyes ruling of 2026-09-06 ("accept and be loud") says an
// unconfigured install must SAY that it is serving every caller from this machine as a full
// administrator, on every page, by the same mechanism the licence banner uses. RbacRound15
// RegressionTests proves the POSTURE OBJECT — kind, headline, detail, log level. It cannot prove
// that any pixel reaches an operator: the shell's admission banner rendered on IsProblem, and
// PostureKind.Off is deliberately NOT a problem, so every C# assertion about the Off posture was
// green on a build where the shell said nothing at all.
//
// That gap is the whole lane. What has to be right lives in Components/Layout/MainLayout.razor and
// nowhere else: which arm of the if/else-if fires, that the words come from the posture object
// rather than from a literal beside it, and that the Enforcing arm still renders NOTHING — a
// banner that is always on is not a banner.
//
// WHAT IS REAL HERE. The service is the REAL RbacService over REAL rbac-config.json /
// rbac-users.json files on disk, loaded exactly the way an install loads them. The component is
// the REAL MainLayout — which hosts the Router, so the route is genuinely matched and the page
// genuinely rendered beneath the banner — through the real DI graph built by AddSharedServices.
//
// WHAT IS NOT. HtmlRenderer is a STATIC renderer: OnAfterRenderAsync never runs, so nothing here
// exercises interactivity, and "not dismissable" is asserted as the ABSENCE of any control in the
// rendered element rather than by clicking one that is not there. The live probe on a running
// --server instance is what proves the banner over HTTP on / and /query; this file is the
// regression net that keeps it there.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public sealed class BootstrapPostureBannerRenderTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly List<string> _dirs = new();

        public BootstrapPostureBannerRenderTests(ITestOutputHelper output) => _out = output;

        /// <summary>The class the Off arm renders, and the only place in the app that carries it.</summary>
        private const string BannerClass = "rbac-bootstrap-banner";

        /// <summary>The class the PROBLEM arm renders. Asserted absent, so the two arms cannot be confused.</summary>
        private const string AdmissionBannerClass = "rbac-admission-banner";

        /// <summary>
        /// The landing page, and a second routed page, so "on every routed page" is measured on more
        /// than one route.
        ///
        /// <para>⚠ THE SECOND ROUTE IS EDITION-DEPENDENT and that is not a fudge. The ruling names
        /// <c>/query</c>, but <c>Pages\QueryExecutor.razor</c> is Content-Removed from a community
        /// build (buildprofile.targets, the live-monitoring module), so on that axis the route does
        /// not exist and the Router would render NotFound — the banner lives inside the Found
        /// branch, so the assertion would go red for a reason that has nothing to do with this
        /// lane. <c>/about</c> ships in both editions and is cheap to render.</para>
        /// </summary>
        private static string SecondRoute => BuildModules.Community ? "/about" : "/query";

        private string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "_sqlt_bootstrap_banner_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _dirs.Add(dir);
            return dir;
        }

        public void Dispose()
        {
            foreach (var dir in _dirs)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch { /* a locked temp file is not a test failure */ }
            }
        }

        // ── Rig ──────────────────────────────────────────────────────────────────────────────

        private static RbacUser TheAdmin() => new()
        {
            Email = @"BOOTSTRAP\admin",
            DisplayName = "Configured Admin",
            Provider = AuthProviders.Windows,
            Role = AppRoles.Admin,
            Enabled = true,
        };

        /// <summary>
        /// A REAL RbacService over REAL files, so the posture under test is loaded rather than
        /// poked onto the object. <paramref name="configJson"/> null means "no config file at all" —
        /// a never-configured install, which is the state the ruling is about.
        /// </summary>
        private RbacService RealServiceIn(string dir, string? configJson, string? usersJson)
        {
            var configPath = Path.Combine(dir, "rbac-config.json");
            var usersPath = Path.Combine(dir, "rbac-users.json");
            if (configJson != null) File.WriteAllText(configPath, configJson);
            if (usersJson != null) File.WriteAllText(usersPath, usersJson);
            return new RbacService(NullLogger<RbacService>.Instance, configPath, usersPath);
        }

        /// <summary>
        /// Renders the shipped <c>MainLayout</c> — which hosts the Router — at
        /// <paramref name="route"/>, over the real service graph, with
        /// <paramref name="rbac"/> standing behind the narrow posture handle the shell injects.
        /// </summary>
        private async Task<string> RenderShellAsync(RbacService rbac, string route)
        {
            var services = new ServiceCollection();
            // Fully qualified: SQLTriage.Data carries a LogLevel of its own, so the bare name is
            // ambiguous in this file.
            services.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.None));

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();
            services.AddSingleton<IConfiguration>(configuration);

            // Supplied by the Blazor host in production. Navigation carries the ROUTE, which is what
            // makes this a test of "every routed page" rather than of one page.
            services.AddScoped<NavigationManager>(_ => new FixedNavigationManager(route));
            services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new FakeJsRuntime());

            // Supplied by the Blazor host too, and required because MainLayout HOSTS THE ROUTER —
            // which is the whole reason this file renders the layout rather than a page: the banner
            // sits inside the Router's <Found> branch, so a real route match is part of the thing
            // under test.
            services.AddSingleton<Microsoft.AspNetCore.Components.Routing.INavigationInterception, StubNavigationInterception>();
            services.AddSingleton<Microsoft.AspNetCore.Components.Routing.IScrollToLocationHash, StubScrollToLocationHash>();
            services.AddSingleton<IErrorBoundaryLogger, StubErrorBoundaryLogger>();

            services.AddSharedServices(configuration);

            // Keep the operator's REAL %APPDATA%\SQLTriage out of this graph — UserSettingsService
            // refuses the default path under a test host (RealUserProfileGuard), and
            // InstallProvenanceService defaults to the same place. Registered AFTER
            // AddSharedServices because the last registration wins in MS.DI.
            var profileDir = Path.Combine(NewDir(), "profile");
            Directory.CreateDirectory(profileDir);
            services.AddSingleton(new UserSettingsService(Path.Combine(profileDir, "user-settings.json")));
            services.AddSingleton(new InstallProvenanceService(profileDir));

            // THE SEAM UNDER TEST: the shell injects this one-method handle, never RbacService.
            services.AddSingleton<IRbacEnforcementPostureAccessor>(rbac);

            await using var provider = services.BuildServiceProvider();
            var scope = provider.CreateScope();

            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            await using var renderer = new HtmlRenderer(scope.ServiceProvider, loggerFactory);

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<SQLTriage.Components.Layout.MainLayout>();
                return output.ToHtmlString();
            });
        }

        /// <summary>
        /// Strips tags, decodes entities and collapses whitespace, so an assertion reads the words a
        /// person does.
        ///
        /// <para>⚠ THE DECODE IS LOAD-BEARING, and it cost a red run to learn. Blazor renders through
        /// <c>HtmlEncoder.Default</c>, which escapes every non-ASCII character, so a posture sentence
        /// carrying one reaches the page as a numeric entity and a raw substring assertion against the
        /// service's own string fails while the banner is on screen and perfectly correct. Measured
        /// 2026-09-07, when the Detail still carried an em dash; the repo's voice lint has since
        /// required that copy to be plain ASCII, so the decode is currently belt to the lint's braces
        /// rather than the thing standing between this file and a false red. It stays because the
        /// encoder's behaviour is the durable fact and the next sentence to reach this banner will not
        /// be written with it in mind. An assertion that read the ENCODED form would have been the
        /// wrong fix: it would pass on a page no operator can read.</para>
        /// </summary>
        private static string VisibleText(string html)
            => Regex.Replace(
                System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")),
                @"\s+", " ").Trim();

        /// <summary>
        /// The rendered element carrying <paramref name="cssClass"/>, from its opening tag to its
        /// matching close, by div depth. Used so "there is no dismiss control" is asserted over the
        /// BANNER and not over the whole page, which has buttons everywhere.
        /// </summary>
        private static string ExtractElement(string html, string cssClass)
        {
            var start = html.IndexOf("<div class=\"" + cssClass + "\"", StringComparison.Ordinal);
            Assert.True(start >= 0, $"No element with class \"{cssClass}\" was rendered.");

            var depth = 0;
            var i = start;
            while (i < html.Length)
            {
                if (string.CompareOrdinal(html, i, "<div", 0, 4) == 0) depth++;
                else if (string.CompareOrdinal(html, i, "</div>", 0, 6) == 0)
                {
                    depth--;
                    if (depth == 0) return html.Substring(start, i + 6 - start);
                }
                i++;
            }

            Assert.Fail($"The element with class \"{cssClass}\" was never closed in the rendered output.");
            return string.Empty;
        }

        // ── The renders ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The landing route. A never-configured install renders the bootstrap-admin banner, and it
        /// renders the AMBER one — not the red admission banner, which means something is wrong.
        /// </summary>
        [Fact]
        public async Task An_unconfigured_install_banners_the_bootstrap_admin_posture_on_the_landing_route()
        {
            if (!OperatingSystem.IsWindows()) return;

            var rbac = RealServiceIn(NewDir(), configJson: null, usersJson: null);

            // The precondition, asserted rather than assumed: every assertion below would pass
            // vacuously over an install that was in some other posture entirely.
            Assert.Equal(RbacService.PostureKind.Off, rbac.DescribeEnforcementPosture().Kind);

            var html = await RenderShellAsync(rbac, "/");
            _out.WriteLine(VisibleText(html));

            Assert.Contains(BannerClass, html, StringComparison.Ordinal);
            Assert.DoesNotContain(AdmissionBannerClass, html, StringComparison.Ordinal);
        }

        /// <summary>
        /// …and on a second routed page, because the ruling is "every page", not "the home page".
        /// The shell is what renders it, so a second route is what distinguishes the two.
        /// </summary>
        [Fact]
        public async Task The_bootstrap_banner_renders_on_a_second_routed_page_too()
        {
            if (!OperatingSystem.IsWindows()) return;

            var rbac = RealServiceIn(NewDir(), configJson: null, usersJson: null);
            Assert.Equal(RbacService.PostureKind.Off, rbac.DescribeEnforcementPosture().Kind);

            var html = await RenderShellAsync(rbac, SecondRoute);

            Assert.Contains(BannerClass, html, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE WORDS, and where they come from. The banner must render the posture object's own
        /// Headline and Detail — the same register the log line and the Settings page read — so an
        /// operator at the screen and an auditor in the log file cannot be told different things.
        ///
        /// <para>The Detail is the load-bearing half. "Access control is switched off" on its own
        /// invites the reader to assume the LAN can reach an admin session; the scope sentence is
        /// what stops a loud banner overstating the exposure, and it must survive to the page.</para>
        /// </summary>
        [Fact]
        public async Task The_banner_renders_the_services_own_sentences_including_the_loopback_scope()
        {
            if (!OperatingSystem.IsWindows()) return;

            var rbac = RealServiceIn(NewDir(), configJson: null, usersJson: null);
            var posture = rbac.DescribeEnforcementPosture();
            Assert.Equal(RbacService.PostureKind.Off, posture.Kind);
            Assert.NotEmpty(posture.Detail);

            var text = VisibleText(await RenderShellAsync(rbac, "/"));

            Assert.Contains(posture.Headline, text, StringComparison.Ordinal);
            Assert.Contains(posture.Detail, text, StringComparison.Ordinal);
            Assert.Contains("only for connections from this machine", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// NOT DISMISSABLE. The posture persists until access control is switched on, so a control
        /// that hides the banner would leave the install in exactly the state the ruling is about,
        /// with nothing on screen saying so. Asserted over the banner element alone.
        /// </summary>
        [Fact]
        public async Task The_bootstrap_banner_carries_no_dismiss_control()
        {
            if (!OperatingSystem.IsWindows()) return;

            var rbac = RealServiceIn(NewDir(), configJson: null, usersJson: null);
            var banner = ExtractElement(await RenderShellAsync(rbac, "/"), BannerClass);
            _out.WriteLine(banner);

            Assert.DoesNotContain("<button", banner, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dismiss", banner, StringComparison.OrdinalIgnoreCase);

            // Decoded, because a close glyph would ship as &#xD7; — see VisibleText.
            Assert.DoesNotContain("×", System.Net.WebUtility.HtmlDecode(banner), StringComparison.Ordinal);

            // The one navigation it does carry: the surface that ENDS the posture.
            Assert.Contains("/settings?tab=security", banner, StringComparison.Ordinal);
        }

        /// <summary>
        /// The opposite direction, and the reason the arm is an else-if rather than an extra always-on
        /// block: an ENFORCING install renders neither banner. A banner that is always on is not a
        /// banner, and this is the assertion that keeps the loud arm honest.
        /// </summary>
        [Fact]
        public async Task An_enforcing_install_renders_no_banner_at_all()
        {
            if (!OperatingSystem.IsWindows()) return;

            var rbac = RealServiceIn(
                NewDir(),
                configJson: "{\"enabled\": true, \"windows\": {\"enabled\": true}}",
                usersJson: JsonSerializer.Serialize(new List<RbacUser> { TheAdmin() }));

            // The precondition. Without it this test passes over an install that is merely Lapsed.
            Assert.Equal(RbacService.PostureKind.Enforcing, rbac.DescribeEnforcementPosture().Kind);

            var html = await RenderShellAsync(rbac, "/");

            Assert.DoesNotContain(BannerClass, html, StringComparison.Ordinal);
            Assert.DoesNotContain(AdmissionBannerClass, html, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE HEADLINE AND THE DETAIL MUST BE SEPARATED in the rendered HTML (fold-in MEDIUM 3).
        /// The separator was written as a leading space INSIDE the span markup, and Blazor trims
        /// markup whitespace before an expression, so both sentences reached the page butted together
        /// ("...ENFORCED.Every request..."). It is now a non-breaking space emitted from the
        /// EXPRESSION, which Razor never trims. Both banner arms carry a Detail, so both are checked;
        /// revert the fix and this test reddens.
        /// </summary>
        [Fact]
        public async Task Both_banner_arms_render_a_separator_between_the_headline_and_the_detail()
        {
            if (!OperatingSystem.IsWindows()) return;

            // The amber arm: a never-configured install (Off).
            await AssertHeadlineAndDetailAreSeparatedAsync(
                RealServiceIn(NewDir(), configJson: null, usersJson: null),
                BannerClass, RbacService.PostureKind.Off);

            // The pre-existing red admission arm: switched ON with no enabled Admin -> Lapsed, which
            // IsProblem, so the admission banner renders. It carried the same defect.
            await AssertHeadlineAndDetailAreSeparatedAsync(
                RealServiceIn(NewDir(), configJson: "{\"enabled\": true, \"windows\": {\"enabled\": true}}", usersJson: null),
                AdmissionBannerClass, RbacService.PostureKind.Lapsed);
        }

        private async Task AssertHeadlineAndDetailAreSeparatedAsync(
            RbacService rbac, string cssClass, RbacService.PostureKind expectedKind)
        {
            var posture = rbac.DescribeEnforcementPosture();
            Assert.Equal(expectedKind, posture.Kind);
            Assert.NotEmpty(posture.Detail);

            var banner = ExtractElement(await RenderShellAsync(rbac, "/"), cssClass);

            // Tags removed WITHOUT substituting spaces -- unlike VisibleText, which replaces every tag
            // with a space and would therefore HIDE a missing separator. Here the defect is visible:
            // "<strong>Headline</strong><span>Detail</span>" decodes to "HeadlineDetail".
            var joined = System.Net.WebUtility.HtmlDecode(Regex.Replace(banner, "<[^>]+>", ""));
            _out.WriteLine(joined);

            // The defect shape: the two sentences with nothing between them.
            Assert.DoesNotContain(posture.Headline + posture.Detail, joined, StringComparison.Ordinal);
            // The fix: a non-breaking space (U+00A0) sits between them in the rendered output.
            Assert.Contains(posture.Headline + "\u00A0" + posture.Detail, joined, StringComparison.Ordinal);
        }

        /// <summary>Stands in for the host's NavigationManager, pinned to one route.</summary>
        private sealed class FixedNavigationManager : NavigationManager
        {
            public FixedNavigationManager(string route)
                => Initialize("http://localhost/", "http://localhost" + (route.StartsWith('/') ? route : "/" + route));

            protected override void NavigateToCore(string uri, bool forceLoad) { }
        }

        /// <summary>The Router asks the host to intercept link clicks. Static rendering has no links to intercept.</summary>
        private sealed class StubNavigationInterception : Microsoft.AspNetCore.Components.Routing.INavigationInterception
        {
            public Task EnableNavigationInterceptionAsync() => Task.CompletedTask;
        }

        /// <summary>Same: there is no scroll position in a string of HTML.</summary>
        private sealed class StubScrollToLocationHash : Microsoft.AspNetCore.Components.Routing.IScrollToLocationHash
        {
            public Task RefreshScrollPositionForHash(string locationAbsolute) => Task.CompletedTask;
        }

        /// <summary>
        /// MainLayout wraps the routed page in an ErrorBoundary, which needs a logger. Deliberately
        /// SILENT rather than throwing: a page that fails to render statically must not take the
        /// shell down, because the banner above it is the thing under test and it renders either way.
        /// </summary>
        private sealed class StubErrorBoundaryLogger : IErrorBoundaryLogger
        {
            public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
        }
    }
}
