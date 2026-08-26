/* In the name of God, the Merciful, the Compassionate */

// -- The Alerts page's three visible-surface fixes, held against the SHIPPED files -------------
//
// WHY THIS FILE EXISTS. All three defects were things an operator could see, and none of them
// could fail a test, because nothing in the suite read the stylesheet or the page markup for
// these contracts.
//
//   A. .severity-badge had rules for critical, warning, info and default. The Alert Definitions
//      editor offers five severities, and Low/Medium/High had no rule, so the Configuration tab
//      painted a bare word where every other tab paints a chip.
//
//   B. --bg-tertiary and --text were referenced at 42 and 19 sites and declared nowhere, so
//      those declarations were invalid at computed-value time.
//
//   C. The Templates tab told the operator to customise "the message sent to each channel".
//      Only the email template is ever rendered. ChannelPayloadReachCensusTests already proved
//      that ON THE WIRE; what was missing was anything tying the PAGE'S CLAIM to that proof, so
//      the page could go on promising reach the code does not have.
//
// WHAT THESE ARE. Lints, not renders. They pin contracts between files by reading the real bytes
// a build ships. Whether the chip lands on the right pixel is a live check and is not claimed
// here. The load-bearing one is the reach census: it derives the rendered-channel set from the
// SERVICE'S own call sites rather than restating it, so wiring a second channel up turns the
// page's sentence red instead of leaving it quietly false.
//
// MUTATIONS THAT MUST FAIL (all run):
//   * delete the .severity-badge.severity-low rule from app.css -> the badge-rule test.
//   * delete the --bg-tertiary declaration from :root -> the token test.
//   * hardcode severity-critical at the Configuration tab's badge -> the derived-class test.
//   * flip the teams card's IsRendered to true -> the reach-census test.
//   * delete the @if (!card.IsRendered) warning block -> the per-card warning test. That one
//     used to pass only because the tab-level paragraph wraps "posts a / fixed payload" across a
//     line break, so a reflow would have released the grip silently. It is sliced now.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SQLTriage.Tests
{
    public class AlertsUiSurfaceTests
    {
        private static string Read(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { RawPassedScan.RepoRoot().FullName }.Concat(parts).ToArray()));

        private static string Css()     => Read("wwwroot", "css", "app.css");
        private static string Markup()  => Read("Pages", "Alerts.razor");
        private static string Service() => Read("Data", "Services", "NotificationChannelService.cs");
        private static string Model()   => Read("Data", "Models", "AlertConfiguration.cs");

        /// <summary>
        /// The slice of the page between one tab's @if and whatever ends it: the next tab, or the
        /// @code block. The @code bound matters. Without it the LAST tab's slice runs to EOF and
        /// every assertion below would also pass on a string sitting in a C# comment.
        /// </summary>
        private static string Tab(string key)
        {
            var markup = Markup();
            var marker = "@if (_activeTab == \"" + key + "\")";
            var start = markup.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0, "the " + key + " tab must exist for this test to mean anything");

            var end = markup.Length;
            foreach (var bound in new[] { "@if (_activeTab == \"", "@code {" })
            {
                var at = markup.IndexOf(bound, start + marker.Length, StringComparison.Ordinal);
                if (at >= 0 && at < end) end = at;
            }
            return markup.Substring(start, end - start);
        }

        // -- A. every severity the editor offers can paint a chip ------------------------------

        /// <summary>
        /// The severity list is READ OUT of the Definitions editor's own select rather than
        /// retyped here, so adding a sixth severity without a badge rule fails this test.
        /// </summary>
        private static IReadOnlyList<string> SeveritiesTheEditorOffers()
        {
            var definitions = Tab("definitions");
            var start = definitions.IndexOf("OnSeverityChanged", StringComparison.Ordinal);
            Assert.True(start > 0, "the severity select is the source of truth for this list");
            var end = definitions.IndexOf("</select>", start, StringComparison.Ordinal);
            Assert.True(end > start, "the severity select must close");

            var options = Regex.Matches(definitions.Substring(start, end - start), "<option value=\"(?<v>[^\"]+)\"")
                               .Select(m => m.Groups["v"].Value.ToLowerInvariant())
                               .ToList();
            Assert.NotEmpty(options);
            return options;
        }

        [Fact]
        public void Every_severity_the_definitions_editor_offers_has_a_badge_rule()
        {
            var css = Css();
            foreach (var severity in SeveritiesTheEditorOffers())
            {
                var rule = new Regex(
                    @"\.severity-badge\.severity-" + Regex.Escape(severity) + @"\s*\{[^}]*background:[^}]*color:[^}]*\}",
                    RegexOptions.Singleline);
                Assert.True(rule.IsMatch(css),
                    "the editor offers severity '" + severity + "' and app.css carries no " +
                    ".severity-badge.severity-" + severity + " rule with a background and a colour, " +
                    "so that badge paints as bare text");
            }
        }

        [Fact]
        public void The_configuration_tab_derives_its_badge_class_from_the_alert_severity()
        {
            Assert.Contains("severity-badge severity-@(alert.Severity.ToLower())", Tab("config"));
        }

        // -- B. the two tokens every stylesheet already used -----------------------------------

        private static string RootBlock()
        {
            var css = Css();
            var start = css.IndexOf(":root {", StringComparison.Ordinal);
            Assert.True(start >= 0, "app.css must declare :root");
            var end = css.IndexOf("\n}", start, StringComparison.Ordinal);
            Assert.True(end > start, ":root must close");
            return css.Substring(start, end - start);
        }

        [Theory]
        [InlineData("--bg-tertiary")]
        [InlineData("--text")]
        public void The_token_the_stylesheets_already_reference_is_declared_in_root(string token)
        {
            var declaration = new Regex(Regex.Escape(token) + @"\s*:\s*[^;]+;");
            Assert.True(declaration.IsMatch(RootBlock()),
                token + " is referenced across the stylesheet tree and is not declared in :root, " +
                "so every one of those declarations is invalid at computed-value time");
        }

        // -- C. the Templates tab may only claim the reach the service has ---------------------

        /// <summary>
        /// AlertTemplateConfig property name to the channel key the page uses. A property missing
        /// from here fails the census loudly rather than being skipped.
        /// </summary>
        private static readonly Dictionary<string, string> TemplatePropertyToChannelKey =
            new(StringComparer.Ordinal)
            {
                ["Email"]      = "email",
                ["Teams"]      = "teams",
                ["Slack"]      = "slack",
                ["Webhook"]    = "webhook",
                ["PagerDuty"]  = "pagerduty",
                ["ServiceNow"] = "servicenow",
                ["WhatsApp"]   = "whatsapp",
            };

        /// <summary>
        /// Which channels the notification service reads a template for AT ALL. This is a source
        /// lint and it says so: the authority on what reaches the wire is
        /// ChannelPayloadReachCensusTests, which captures the bytes. This one exists to keep the
        /// PAGE'S SENTENCE tied to that fact.
        /// </summary>
        private static SortedSet<string> ChannelsWhoseTemplateTheServiceReads()
        {
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(Service(), @"_templates\.Config\.(?<p>\w+)"))
            {
                var property = m.Groups["p"].Value;
                Assert.True(TemplatePropertyToChannelKey.ContainsKey(property),
                    "NotificationChannelService reads _templates.Config." + property + ", which this " +
                    "census cannot map to a channel key. Map it rather than letting it go uncounted.");
                found.Add(TemplatePropertyToChannelKey[property]);
            }
            Assert.NotEmpty(found);
            return found;
        }

        private static SortedSet<string> ChannelsTheTemplateCardsClaimAreRendered()
        {
            var markup = Markup();
            var start = markup.IndexOf("_templateCards = new List<TemplateCard>", StringComparison.Ordinal);
            Assert.True(start > 0, "LoadTemplateCards must build the card list");
            var end = markup.IndexOf("};", start, StringComparison.Ordinal);
            Assert.True(end > start, "the card list must close");

            var claimed = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(markup.Substring(start, end - start), "new\\(\"(?<k>[a-z]+)\"(?<rest>[^\r\n]*)"))
            {
                if (Regex.IsMatch(m.Groups["rest"].Value, @",\s*true\s*\)\s*,?\s*$"))
                    claimed.Add(m.Groups["k"].Value);
            }
            return claimed;
        }

        [Fact]
        public void The_cards_claim_rendering_for_exactly_the_channels_the_service_renders()
        {
            Assert.Equal(ChannelsWhoseTemplateTheServiceReads(), ChannelsTheTemplateCardsClaimAreRendered());
        }

        /// <summary>
        /// The body of the first block guarded by <paramref name="guard"/>, brace-matched. Slicing
        /// it is what makes the per-card assertion below grip the GUARDED markup rather than any
        /// occurrence of the same words elsewhere on the tab.
        /// </summary>
        private static string GuardedBlock(string haystack, string guard)
        {
            var at = haystack.IndexOf(guard, StringComparison.Ordinal);
            Assert.True(at >= 0, "expected a block guarded by " + guard);
            var open = haystack.IndexOf('{', at + guard.Length);
            Assert.True(open > at, guard + " must open a block");

            var depth = 0;
            for (var i = open; i < haystack.Length; i++)
            {
                if (haystack[i] == '{') depth++;
                else if (haystack[i] == '}' && --depth == 0)
                    return haystack.Substring(open + 1, i - open - 1);
            }
            Assert.Fail("the block guarded by " + guard + " never closes");
            return "";
        }

        /// <summary>Markup wraps. A reflow must not change any verdict in this file.</summary>
        private static string Flat(string markup) => Regex.Replace(markup, @"\s+", " ").Trim();

        [Fact]
        public void The_templates_tab_says_the_other_channels_ignore_their_template()
        {
            Assert.Contains("Only the email template is rendered and sent today.", Flat(Tab("templates")));
        }

        /// <summary>
        /// The per-card warning has to be GATED ON THE FLAG, not merely present somewhere on the
        /// tab, or a card that sends nothing could lose its warning while the tab-level paragraph
        /// kept this test green. The flag itself is what the reach census pins to the service.
        /// </summary>
        [Fact]
        public void Each_card_that_does_not_send_its_template_carries_its_own_warning()
        {
            var warning = Flat(GuardedBlock(Tab("templates"), "@if (!card.IsRendered)"));
            Assert.Contains("posts a fixed payload", warning);
            Assert.Contains("do not change what it delivers", warning);
        }

        /// <summary>
        /// The page quotes the phrase a token prints when nobody measured. It must quote the
        /// phrase the model actually returns, not a paraphrase of it.
        /// </summary>
        [Theory]
        [InlineData("not measured")]  // AlertNotification.CurrentValueText
        [InlineData("not recorded")]  // AlertNotification.HitCountText
        public void The_phrase_the_page_promises_is_the_phrase_the_model_returns(string phrase)
        {
            Assert.Contains("\"" + phrase + "\"", Model());
            Assert.Contains("\"" + phrase + "\"", Tab("templates"));
        }
    }
}
