/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The two host pages must load the same assets.
    ///
    /// <para><b>Why.</b> <c>wwwroot/index.html</c> is the WebView2 (desktop) host page and
    /// <c>Components/ServerApp.razor</c> is the Blazor Server one. They are two hand-maintained
    /// lists of the same thing, and they drifted: server mode shipped WITHOUT
    /// <c>css/Modal.css</c>, so every modal in the app rendered with no rules at all for
    /// <c>.modal-backdrop</c> / <c>.modal-content</c> / <c>.modal-close</c> — no dim, no blur, no
    /// stacking. Measured 2026-08-02 at <c>127.0.0.1:5187/servers</c> on an install with no server
    /// configured: the page's own Add Server dialog and the layout's onboarding wizard drew on top
    /// of each other and the text was unreadable, because the wizard's <c>--bg-panel</c> is a
    /// deliberately 65%-alpha "glass" colour that only reads over a blurred backdrop.</para>
    ///
    /// <para>It had happened before, in the same file, and been fixed one entry at a time:
    /// <c>scripts/themes.js</c> was written <c>scripts/themes.js</c> in ServerApp when the file
    /// lives at the wwwroot root, and 404'd on every server-mode page load until 2026-07-20.
    /// Fixing entries does not fix the shape. This test is the shape.</para>
    ///
    /// <para>Also missing and now added: <c>scripts/welcomeTour.js</c> (so
    /// <c>window.welcomeTourInterop</c> did not exist and WelcomeTourOverlay's keyboard
    /// registration faulted into its own catch on every server-mode circuit), the three background
    /// renderers, and the INLINE <c>window.sqltriageCommandPalette</c> registry — without which
    /// CommandPalette's <c>register</c> interop faulted and Ctrl+K did nothing in server mode.</para>
    /// </summary>
    public class ServerModeHostAssetsTests
    {
        /// <summary>
        /// Assets index.html loads that ServerApp deliberately does not, each with the reason.
        /// Anything else missing is drift and fails.
        /// </summary>
        private static readonly Dictionary<string, string> DeliberatelyDesktopOnly =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["_framework/blazor.webview.js"] =
                    "The WebView2 client bootstrap. Server mode's equivalent is _framework/blazor.web.js.",
            };

        [Fact]
        public void ServerModeLoadsEveryStylesheetTheDesktopHostDoes()
        {
            var missing = Missing(@"<link\s+href=""([^""]+)""[^>]*rel=""stylesheet""");

            Assert.True(missing.Count == 0,
                "wwwroot/index.html loads these stylesheets and Components/ServerApp.razor does "
                + "not. A rule that exists in one host and not the other is a UI defect that only "
                + "appears over the LAN, which is the half nobody is looking at: css/Modal.css was "
                + "missing on 2026-08-02 and two modals drew on top of each other, unreadable.\n  "
                + string.Join("\n  ", missing));
        }

        [Fact]
        public void ServerModeLoadsEveryScriptTheDesktopHostDoes()
        {
            var missing = Missing(@"<script\s+src=""([^""]+)""");

            Assert.True(missing.Count == 0,
                "wwwroot/index.html loads these scripts and Components/ServerApp.razor does not. "
                + "The failures are silent — welcomeTourInterop's absence is swallowed by "
                + "WelcomeTourOverlay's own catch — so nothing says so until somebody drives the "
                + "UI. If a script really is desktop-only, name it in DeliberatelyDesktopOnly with "
                + "the reason:\n  " + string.Join("\n  ", missing));
        }

        /// <summary>
        /// The command-palette registry is INLINE in index.html rather than a file, so the two
        /// tests above cannot see it. It is asserted by name because CommandPalette calls into it
        /// on every circuit.
        /// </summary>
        [Fact]
        public void ServerModeDefinesTheCommandPaletteRegistryTheComponentCallsInto()
        {
            var serverApp = Read("Components/ServerApp.razor");
            var palette = Read("Components/Shared/CommandPalette.razor");

            Assert.Contains("sqltriageCommandPalette.register", palette, StringComparison.Ordinal);

            Assert.Contains("window.sqltriageCommandPalette", serverApp, StringComparison.Ordinal);
            Assert.Contains("register: function", serverApp, StringComparison.Ordinal);
            Assert.Contains("unregister: function", serverApp, StringComparison.Ordinal);

            // Round 5 replaced a static [JSInvokable] with a per-page DotNetObjectReference,
            // because one process-wide slot meant one browser's Ctrl+K opened another's palette.
            // Copying the inline block across must not copy that back.
            Assert.DoesNotContain("DotNet.invokeMethodAsync('SQLTriage'", serverApp, StringComparison.Ordinal);
        }

        /// <summary>
        /// The onboarding wizard must not depend on a backdrop stylesheet to be readable. Asserted
        /// on the shipped CSS, because that dependency is what made the defect invisible until
        /// somebody looked at pixels.
        /// </summary>
        [Fact]
        public void TheOnboardingWizardPanelIsOpaque()
        {
            var css = Read("wwwroot/css/app.css");

            var at = css.IndexOf(".onboarding-wizard {", StringComparison.Ordinal);
            Assert.True(at > 0, ".onboarding-wizard has gone from app.css.");

            // Wide enough to clear the block's own explanatory comment, which names --bg-panel.
            var block = css[at..Math.Min(css.Length, at + 1200)];
            var background = Regex.Match(block, @"background:\s*([^;]+);");
            Assert.True(background.Success, ".onboarding-wizard must declare a background.");

            Assert.False(background.Groups[1].Value.Contains("--bg-panel", StringComparison.Ordinal),
                "--bg-panel is rgba(17,23,21,0.65) — a glass colour that only reads over a blurred "
                + ".modal-backdrop. A BLOCKING first-run modal rendered over an arbitrary page must "
                + "be legible on its own.");
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static List<string> Missing(string pattern)
        {
            var indexHtml = Read("wwwroot/index.html");
            var serverApp = Read("Components/ServerApp.razor");

            var desktop = Regex.Matches(indexHtml, pattern).Select(m => m.Groups[1].Value);
            var server = new HashSet<string>(
                Regex.Matches(serverApp, pattern).Select(m => m.Groups[1].Value),
                StringComparer.OrdinalIgnoreCase);

            return desktop
                .Where(a => !server.Contains(a))
                .Where(a => !DeliberatelyDesktopOnly.ContainsKey(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string Read(string relativePath)
        {
            var full = Path.Combine(RawPassedScan.RepoRoot().FullName,
                                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), relativePath + " is gone; the test moves with the file.");
            return File.ReadAllText(full);
        }
    }
}
