/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The animated backdrop was selected and did not render.
    ///
    /// <para><b>Measured cause.</b> Not the JS. Both canvases are created, seeded and painted, in
    /// both hosts. They sit at <c>z-index:-2, position:fixed</c>, and a negative-z child paints
    /// BEFORE its non-positioned ancestors' background boxes — so any opaque background between
    /// <c>&lt;html&gt;</c> and the app content buries them. <c>app.css:253</c> carries a comment from
    /// an earlier fix that made <c>&lt;body&gt;</c> transparent for exactly this reason ("particles
    /// were invisible"); a later section headed "GLOBAL V2 GLASSMORPHISM OVERRIDES (SWEEP)" re-set
    /// <c>body</c>'s background-color and gave <c>.app-layout</c> a 0.8-to-1.0-alpha radial gradient,
    /// reinstating the regression the comment warns against.</para>
    ///
    /// <para><b>What these tests do and do not prove.</b> They pin the CONTRACT between the three
    /// files: the canvases mark the body, and the stylesheet makes the layers above them transparent
    /// under that mark. They are a lint, not a render. Whether pixels appear is a live check, and the
    /// app answers it itself now — bgConfig.status() measures the chain and Settings prints what it
    /// found, so a NEW opaque layer surfaces as a sentence instead of a silent flat screen.</para>
    /// </summary>
    public class AnimatedBackgroundTests
    {
        private const string Marker = "bg-canvas-on";

        private static string Read(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { RawPassedScan.RepoRoot().FullName }.Concat(parts).ToArray()));

        private static string Css() => Read("wwwroot", "css", "app.css");
        private static string Wave() => Read("wwwroot", "scripts", "waveBg.js");
        private static string Particle() => Read("wwwroot", "scripts", "particleBg.js");
        private static string Config() => Read("wwwroot", "scripts", "bgConfig.js");

        // ── the contract: one marker name, all three files ───────────────────────

        [Fact]
        public void Both_canvases_mark_the_body_while_they_are_on_screen()
        {
            Assert.Contains($"classList.toggle(\"{Marker}\"", Wave());
            Assert.Contains($"classList.toggle(\"{Marker}\"", Particle());
        }

        [Fact]
        public void The_stylesheet_uses_the_same_marker_the_scripts_set()
        {
            Assert.Contains($"body.{Marker}", Css());
        }

        [Fact]
        public void The_marker_makes_the_layers_above_the_canvas_transparent()
        {
            var css = Css();

            // body itself…
            Assert.Matches(new Regex($@"body\.{Marker}\s*\{{[^}}]*background-color:\s*transparent", RegexOptions.Singleline), css);
            // …and the flex chain that carries the sweep's opaque gradient.
            Assert.Matches(new Regex($@"body\.{Marker}\s+\.app-layout\s*\{{[^}}]*background:\s*transparent", RegexOptions.Singleline), css);
        }

        [Fact]
        public void The_flat_default_keeps_its_solid_chrome()
        {
            // The fix is scoped to the marker: with no backdrop on screen, .app-layout must still
            // have its gradient. A blanket transparency here would change the default look, and
            // the ruling was explicit that the default does not change.
            var css = Css();
            Assert.Matches(new Regex(@"^\.app-layout\s*\{[^}]*radial-gradient", RegexOptions.Multiline | RegexOptions.Singleline), css);
        }

        [Fact]
        public void Both_canvases_report_what_they_actually_drew()
        {
            foreach (var js in new[] { Wave(), Particle() })
            {
                Assert.Contains("getStatus:", js);
                Assert.Contains("canvasPresent:", js);
                Assert.Contains("reducedMotion:", js);
            }
        }

        [Fact]
        public void The_config_layer_measures_occlusion_rather_than_assuming_none()
        {
            var js = Config();
            Assert.Contains("occluders", js);
            Assert.Contains("getComputedStyle", js);
            Assert.Contains("status:", js);
        }

        // ── the sentence Settings prints is derived from that measurement ────────

        [Fact]
        public void Nothing_measured_yet_is_said_plainly_and_is_not_a_claim_of_success()
        {
            var line = BackgroundStatusReporter.Describe(null);
            Assert.Equal(BackgroundStatusReporter.NotMeasured, line);
            Assert.DoesNotContain("drawing", line);
        }

        [Fact]
        public void A_working_wave_backdrop_says_so_and_names_what_it_drew()
        {
            var line = BackgroundStatusReporter.Describe(new BackgroundStatus
            {
                Style = "wave", CanvasPresent = true, Marks = 460, ReducedMotion = false
            });

            Assert.Contains("wave backdrop", line);
            Assert.Contains("460 marks", line);
            Assert.Contains("animates", line);
        }

        [Fact]
        public void A_buried_backdrop_says_it_is_not_visible_and_names_the_layer()
        {
            var line = BackgroundStatusReporter.Describe(new BackgroundStatus
            {
                Style = "wave", CanvasPresent = true, Marks = 460,
                Occluders = new List<string> { "the page body", "the app layout" }
            });

            Assert.Contains("not visible", line);
            Assert.Contains("the page body", line);
            Assert.Contains("the app layout", line);
            Assert.DoesNotContain("animates on pointer movement", line);
        }

        [Fact]
        public void Reduced_motion_is_disclosed_as_a_still_frame_not_reported_as_animating()
        {
            var line = BackgroundStatusReporter.Describe(new BackgroundStatus
            {
                Style = "wave", CanvasPresent = true, Marks = 460, ReducedMotion = true
            });

            Assert.Contains("still frame", line);
            Assert.Contains("reduce motion", line);
            Assert.DoesNotContain("animates on pointer movement", line);
        }

        [Fact]
        public void Occlusion_outranks_reduced_motion_because_an_invisible_field_is_the_bigger_fact()
        {
            var line = BackgroundStatusReporter.Describe(new BackgroundStatus
            {
                Style = "wave", CanvasPresent = true, Marks = 460, ReducedMotion = true,
                Occluders = new List<string> { "the page body" }
            });

            Assert.Contains("not visible", line);
        }

        [Fact]
        public void A_missing_canvas_is_never_reported_as_hidden_behind_something()
        {
            var line = BackgroundStatusReporter.Describe(new BackgroundStatus
            {
                Style = "wave", CanvasPresent = false, Marks = 0
            });

            Assert.Contains("no backdrop canvas", line);
            Assert.DoesNotContain("opaque", line);
        }

        [Fact]
        public void A_canvas_that_drew_nothing_is_called_a_defect_not_a_setting()
        {
            var line = BackgroundStatusReporter.Describe(new BackgroundStatus
            {
                Style = "particle", CanvasPresent = true, Marks = 0
            });

            Assert.Contains("drawn nothing", line);
            Assert.Contains("defect", line);
        }

        [Fact]
        public void The_flat_default_says_only_that_nothing_is_running()
        {
            var line = BackgroundStatusReporter.Describe(new BackgroundStatus { Style = "none" });
            Assert.Contains("Flat backdrop", line);
            Assert.DoesNotContain("marks", line);
        }

        [Fact]
        public void The_animations_toggle_no_longer_claims_to_cover_the_backdrop()
        {
            // body.no-animations sets animation:none / transition:none — CSS only. It never
            // touched the canvas's requestAnimationFrame loop, but the label said it did.
            var settings = Read("Pages", "Settings.razor");
            Assert.DoesNotContain("fades, and background effects", settings);
        }
    }
}
