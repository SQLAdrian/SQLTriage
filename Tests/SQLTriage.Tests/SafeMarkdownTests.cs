/* In the name of God, the Merciful, the Compassionate */

// Guards the fix for fresh-eyes finding security-9 (2026-08-31), lane/xss-markdown-sinks.
// Eight Blazor sinks cast Markdig's Markdown.ToHtml(text) to MarkupString. On Markdig's DEFAULT
// pipeline that passes raw HTML straight through — so a monitored SQL Server's finding text
// carrying "<img src=x onerror=alert(1)>" or "<script>" would reach the DOM as live markup
// (latent stored XSS). SafeMarkdown routes every sink through one pipeline built with
// DisableHtml(), which escapes raw HTML while leaving CommonMark formatting intact.
//
// DisableHtml() does NOT filter link destinations, so [x](javascript:alert(1)) still rendered a
// live href=javascript:... . The pipeline now also walks the parsed document and neutralises any
// link/image/autolink whose scheme is not on the allow-list (http/https/mailto/tel + scheme-less).
// These tests exercise the scheme policy directly (IsLinkSchemeAllowed, InternalsVisibleTo) and
// end-to-end through the rendered HTML, and prove the filter is load-bearing by mutation.
//
// NO SQL, NO client data — fabricated strings only. Profile-independent (no gated types).

using Markdig;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class SafeMarkdownTests
    {
        [Fact]
        public void Default_markdig_pipeline_passes_raw_html_through_proving_the_fix_is_load_bearing()
        {
            // The vulnerability being closed: Markdig's DEFAULT pipeline emits the tag verbatim.
            var vulnerable = Markdig.Markdown.ToHtml("<img src=x onerror=alert(1)>");
            Assert.Contains("<img", vulnerable);
            Assert.Contains("onerror", vulnerable);

            // SafeMarkdown (DisableHtml) neutralises the exact same input.
            var safe = SafeMarkdown.ToHtml("<img src=x onerror=alert(1)>");
            Assert.DoesNotContain("<img", safe);
            Assert.Contains("&lt;img", safe);
        }

        [Fact]
        public void ToHtml_escapes_an_img_onerror_payload()
        {
            var html = SafeMarkdown.ToHtml("before <img src=x onerror=alert(1)> after");

            // The payload survives only as inert text, never as a live element.
            Assert.DoesNotContain("<img", html);
            Assert.Contains("&lt;img", html);
            Assert.Contains("&gt;", html);
        }

        [Fact]
        public void ToHtml_escapes_a_script_tag_payload()
        {
            var html = SafeMarkdown.ToHtml("intro <script>alert(1)</script> outro");

            Assert.DoesNotContain("<script", html);
            Assert.DoesNotContain("</script>", html);
            Assert.Contains("&lt;script&gt;", html);
        }

        [Fact]
        public void ToHtml_still_renders_legitimate_markdown_formatting()
        {
            var html = SafeMarkdown.ToHtml(
                "**bold** and a [link](https://example.com/x)\n\n- item one\n- item two");

            Assert.Contains("<strong>bold</strong>", html);
            Assert.Contains("href=\"https://example.com/x\"", html);
            Assert.Contains("<li>item one</li>", html);
            Assert.Contains("<li>item two</li>", html);
        }

        [Fact]
        public void ToHtml_html_encodes_the_contents_of_a_fenced_code_block()
        {
            // Governance.razor wraps T-SQL remediation in a ```sql fence. Markdig's code renderer
            // HTML-encodes fenced content regardless of DisableHtml, so a payload inside a fence is
            // also inert — proven here so the ```sql path is not assumed.
            var html = SafeMarkdown.ToHtml("```sql\nEXEC('<script>alert(1)</script>');\n```");

            Assert.DoesNotContain("<script", html);
            Assert.Contains("&lt;script&gt;", html);
        }

        [Fact]
        public void ToHtml_is_null_and_empty_safe()
        {
            Assert.Equal("", SafeMarkdown.ToHtml(null));
            Assert.Equal("", SafeMarkdown.ToHtml(""));
        }

        // ── Link-scheme allow-list ─────────────────────────────────────────────────────────────
        // The policy is measured two ways: directly on exact scheme strings (parse-independent), and
        // end-to-end through the rendered HTML (proves the pipeline wiring reaches every sink).

        [Theory]
        [InlineData("javascript:alert(1)")]
        [InlineData("JaVaScRiPt:alert(1)")]                                     // mixed case
        [InlineData("JAVASCRIPT:alert(1)")]                                     // upper case
        [InlineData("  javascript:alert(1)")]                                   // leading whitespace
        [InlineData("\tjavascript:alert(1)")]                                  // leading tab
        [InlineData("java\tscript:alert(1)")]                                  // tab inside the scheme
        [InlineData("jav\u0000ascript:alert(1)")]                              // NUL inside the scheme
        [InlineData("vbscript:msgbox(1)")]
        [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
        [InlineData("data:text/html,<script>alert(1)</script>")]
        [InlineData("file:///etc/passwd")]
        [InlineData("chrome://settings")]                                       // any other scheme
        public void IsLinkSchemeAllowed_rejects_dangerous_and_obfuscated_schemes(string url)
            => Assert.False(SafeMarkdown.IsLinkSchemeAllowed(url));

        [Theory]
        [InlineData("http://example.com")]
        [InlineData("https://example.com/x?q=1#f")]
        [InlineData("HTTPS://EXAMPLE.COM")]                                      // case-insensitive allow
        [InlineData("mailto:a@b.c")]
        [InlineData("tel:+15551234567")]
        [InlineData("/foo/bar")]                                                // root-relative
        [InlineData("relative/path.html")]                                      // relative, no scheme
        [InlineData("#section")]                                                // anchor only
        [InlineData("foo/bar:baz")]                                             // ':' after '/', not a scheme
        [InlineData("a#b:c")]                                                   // ':' after '#', not a scheme
        [InlineData("")]                                                        // empty
        [InlineData(null)]                                                      // null
        public void IsLinkSchemeAllowed_permits_safe_and_scheme_less_urls(string? url)
            => Assert.True(SafeMarkdown.IsLinkSchemeAllowed(url));

        [Fact]
        public void ToHtml_neutralizes_a_javascript_link_and_the_filter_is_load_bearing()
        {
            const string payload = "[x](javascript:alert(1))";

            // Without the scheme filter (DisableHtml only — the pipeline before this fix) Markdig
            // emits a LIVE javascript: href. This is the residual the filter closes.
            var unfiltered = Markdig.Markdown.ToHtml(
                payload, new Markdig.MarkdownPipelineBuilder().DisableHtml().Build());
            Assert.Contains("href=\"javascript:alert(1)\"", unfiltered);

            // SafeMarkdown neutralises the same input: no javascript: href, an inert about:blank
            // instead, and the visible link text survives.
            var safe = SafeMarkdown.ToHtml(payload);
            Assert.DoesNotContain("javascript:", safe);
            Assert.Contains("href=\"about:blank\"", safe);
            Assert.Contains(">x</a>", safe);
        }

        [Fact]
        public void ToHtml_neutralizes_a_mixed_case_javascript_link()
        {
            var safe = SafeMarkdown.ToHtml("[x](JaVaScRiPt:alert(1))");

            Assert.DoesNotContain("JaVaScRiPt:", safe);
            Assert.DoesNotContain("javascript:", safe);
            Assert.Contains("href=\"about:blank\"", safe);
        }

        [Fact]
        public void ToHtml_neutralizes_a_data_uri_link()
        {
            var safe = SafeMarkdown.ToHtml(
                "[x](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)");

            Assert.DoesNotContain("data:", safe);
            Assert.Contains("href=\"about:blank\"", safe);
        }

        [Fact]
        public void ToHtml_neutralizes_a_javascript_image_source_but_preserves_a_legit_image()
        {
            var evil = SafeMarkdown.ToHtml("![x](javascript:alert(1))");
            Assert.DoesNotContain("javascript:", evil);
            Assert.Contains("src=\"about:blank\"", evil);

            var ok = SafeMarkdown.ToHtml("![x](https://example.com/a.png)");
            Assert.Contains("src=\"https://example.com/a.png\"", ok);
        }

        [Fact]
        public void ToHtml_never_emits_a_live_javascript_href_for_an_autolink()
        {
            // <javascript:...> parses as a CommonMark autolink; the filter neutralises its scheme.
            // However Markdig chooses to treat it, the rendered output must never carry a live
            // javascript: href.
            var safe = SafeMarkdown.ToHtml("<javascript:alert(1)>");

            Assert.DoesNotContain("href=\"javascript:", safe);
        }

        [Fact]
        public void ToHtml_preserves_legitimate_link_schemes_and_relative_targets()
        {
            Assert.Contains("href=\"https://example.com\"", SafeMarkdown.ToHtml("[x](https://example.com)"));
            Assert.Contains("href=\"http://example.com\"", SafeMarkdown.ToHtml("[x](http://example.com)"));
            Assert.Contains("href=\"mailto:", SafeMarkdown.ToHtml("[m](mailto:a@b.c)"));   // mailto permitted
            Assert.Contains("href=\"tel:", SafeMarkdown.ToHtml("[t](tel:+15551234567)"));  // tel permitted
            Assert.Contains("href=\"/foo\"", SafeMarkdown.ToHtml("[r](/foo)"));
            Assert.Contains("href=\"#x\"", SafeMarkdown.ToHtml("[a](#x)"));

            // A permitted link is never rewritten to the neutral target.
            Assert.DoesNotContain("about:blank", SafeMarkdown.ToHtml("[x](https://example.com)"));
        }
    }
}
