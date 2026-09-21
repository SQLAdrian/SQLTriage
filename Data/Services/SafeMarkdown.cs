/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Single Markdig entry point for every <c>MarkupString</c> render sink in the app.
    /// The shared pipeline calls <see cref="MarkdownPipelineBuilder.DisableHtml"/>, so raw HTML
    /// embedded in markdown is HTML-escaped (rendered as literal text) instead of being passed
    /// straight through to the DOM. Markdig's <em>default</em> pipeline does the opposite: it lets
    /// <c>&lt;img src=x onerror=...&gt;</c> or <c>&lt;script&gt;</c> reach the browser the moment a
    /// rendered description carries server-sourced text -- latent stored XSS.
    ///
    /// Fresh-eyes finding security-9 (2026-08-31), closed under lane/xss-markdown-sinks: eight
    /// sinks cast <c>Markdown.ToHtml(text)</c> to <c>MarkupString</c> on the default pipeline. Route
    /// them all through here instead.
    ///
    /// CommonMark formatting is untouched: <c>DisableHtml()</c> only turns off the raw-HTML
    /// passthrough parsers (HtmlBlock / HtmlInline). Bold, links, lists and headings still render,
    /// and fenced-code content is HTML-encoded by Markdig's own code renderer regardless -- so a
    /// <c>```sql</c> block cannot smuggle markup either.
    ///
    /// <para><b>Link-scheme allow-list.</b> <c>DisableHtml()</c> stops raw-HTML passthrough but does
    /// NOT filter a link's destination. The link form <c>[x](javascript:alert(1))</c>, the image
    /// form <c>![x](javascript:alert(1))</c>, and the autolink form <c>&lt;javascript:alert(1)&gt;</c>
    /// are all parsed into the object model with the scheme intact, and Markdig's renderer emits a
    /// live <c>href="javascript:..."</c> / <c>src="javascript:..."</c>. This pipeline walks the parsed
    /// document on Markdig's <c>DocumentProcessed</c> hook (after inline parsing, before rendering)
    /// and rewrites the destination of any link, image or autolink whose scheme is not on the
    /// allow-list to <c>about:blank</c>. This is the object-model fix, not a post-render regex over
    /// the HTML string. Legitimate destinations are left byte-for-byte intact.</para>
    ///
    /// <para><b>Policy.</b> PERMITTED: <c>http</c>, <c>https</c>, <c>mailto</c>, <c>tel</c>, and every
    /// scheme-less destination (a relative path, a <c>/root</c> path, or a <c>#anchor</c>).
    /// NEUTRALISED: <c>javascript</c>, <c>vbscript</c>, <c>data</c>, <c>file</c>, and every other
    /// scheme not on the allow-list. Classification is case-insensitive and collapses the
    /// browser-stripped obfuscations first -- leading/trailing whitespace, plus tab / newline /
    /// control characters embedded in the scheme (a browser removes those before it reads the scheme,
    /// so <c>jAvA&#9;ScRiPt:</c> resolves to <c>javascript:</c>). See
    /// <see cref="IsLinkSchemeAllowed"/> for the exact decision.</para>
    /// </summary>
    public static class SafeMarkdown
    {
        /// <summary>Schemes rendered as live links; every other scheme is neutralised. Stored
        /// lower-case and compared ordinally -- <see cref="IsLinkSchemeAllowed"/> lower-cases the
        /// candidate before the lookup, so the match is case-insensitive by construction.</summary>
        private static readonly HashSet<string> AllowedSchemes =
            new HashSet<string>(StringComparer.Ordinal) { "http", "https", "mailto", "tel" };

        /// <summary>Inert replacement for a dangerous destination: <c>about:blank</c> navigates
        /// nowhere and executes nothing, and reads unambiguously in the rendered markup.</summary>
        private const string NeutralUrl = "about:blank";

        private static readonly MarkdownPipeline Pipeline = BuildPipeline();

        private static MarkdownPipeline BuildPipeline()
        {
            MarkdownPipelineBuilder builder = new MarkdownPipelineBuilder().DisableHtml();
            // DocumentProcessed fires once the document is fully parsed (every LinkInline.Url and
            // AutolinkInline.Url is populated) and before rendering, so edits made here reach the
            // renderer. This is the clean object-model seam -- no fragile post-render string surgery.
            builder.DocumentProcessed += NeutralizeDangerousLinks;
            return builder.Build();
        }

        /// <summary>
        /// Render markdown to HTML with raw-HTML passthrough disabled and dangerous link schemes
        /// neutralised. Null or empty input renders as the empty string (matches the old
        /// <c>?? ""</c> call sites).
        /// </summary>
        public static string ToHtml(string? markdown)
            => string.IsNullOrEmpty(markdown) ? string.Empty : Markdown.ToHtml(markdown, Pipeline);

        private static void NeutralizeDangerousLinks(MarkdownDocument document)
        {
            // LinkInline covers BOTH [text](url) links and ![alt](url) images (IsImage marks the
            // image form); the renderer reads Url for the href and the src alike, so one pass over
            // LinkInline neutralises both surfaces.
            foreach (LinkInline link in document.Descendants<LinkInline>())
            {
                if (!IsLinkSchemeAllowed(link.Url))
                {
                    link.Url = NeutralUrl;
                    link.GetDynamicUrl = null; // defensive: a dynamic url would otherwise win at render
                }
            }

            // AutolinkInline covers <scheme:...> autolinks. An email autolink is rendered with a
            // mailto: prefix and is safe; a URL autolink carries its scheme verbatim into the href,
            // so it gets the same allow-list.
            foreach (AutolinkInline auto in document.Descendants<AutolinkInline>())
            {
                if (auto.IsEmail)
                {
                    continue;
                }
                if (!IsLinkSchemeAllowed(auto.Url))
                {
                    auto.Url = NeutralUrl;
                }
            }
        }

        /// <summary>
        /// True if <paramref name="url"/> may be rendered as a live link. A scheme-less destination
        /// (a relative path, a <c>/root</c> path, or a <c>#anchor</c>) is always allowed; a
        /// destination that carries a scheme is allowed only when that scheme is on
        /// <see cref="AllowedSchemes"/>. Internal (InternalsVisibleTo SQLTriage.Tests) so the
        /// allow/neutralise decision is measured on exact scheme strings, independent of how Markdig
        /// happens to parse any particular link destination.
        /// </summary>
        internal static bool IsLinkSchemeAllowed(string? url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return true; // no destination to neutralise
            }

            int colon = url.IndexOf(':');
            if (colon < 0)
            {
                return true; // no scheme: relative path, "/root", or "#anchor"
            }

            // Build the candidate scheme from the text before the first ':', dropping ASCII
            // whitespace, C0 control characters and DEL, and lower-casing. Those are the exact
            // characters a browser strips (tab/newline anywhere; leading/trailing controls and space)
            // before it reads a scheme, so collapsing them here makes jAvA<tab>ScRiPt: and
            // "  javascript:" both resolve to "javascript" and be caught.
            var scheme = new StringBuilder(colon);
            for (int i = 0; i < colon; i++)
            {
                char c = url[i];
                if (c <= ' ' || c == '\u007f')
                {
                    continue;
                }
                scheme.Append(char.ToLowerInvariant(c));
            }

            if (scheme.Length == 0)
            {
                return true; // ':' with nothing scheme-like before it -> treat as relative
            }

            // A real scheme is a letter followed by letters / digits / '+' / '-' / '.'. If the
            // candidate starts with anything else, or contains a non-scheme character, the ':' is not
            // a scheme separator (e.g. "/path:x" or "a#b:c") -> relative reference, allow.
            char first = scheme[0];
            if (first < 'a' || first > 'z')
            {
                return true;
            }
            for (int i = 0; i < scheme.Length; i++)
            {
                char c = scheme[i];
                bool schemeChar = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                                  || c == '+' || c == '-' || c == '.';
                if (!schemeChar)
                {
                    return true;
                }
            }

            return AllowedSchemes.Contains(scheme.ToString());
        }
    }
}
