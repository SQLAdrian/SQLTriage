/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SQLTriage.Tests
{
    /// <summary>
    /// THE MARKUP SCANNER. It answers exactly one question about a shell component, and it is a
    /// question the handler's code cannot influence:
    ///
    /// <para><b>Is this interactive element inside an authorization boundary?</b></para>
    ///
    /// <para><b>Why the question changed.</b> Three instruments have now been defeated in
    /// sequence, each through the thing it could not see. Round 4's census matched a verb lexicon
    /// and missed <c>SetAnonymiseServerNames</c>. Round 5's fix anchored the verb to a prefix and
    /// missed <c>ShortcutSvc.TriggerRun</c>. Round 6 threw the lexicon away and enumerated EVERY
    /// call the shell makes on an injected service — and was defeated on 2026-08-02 by one extra
    /// line of C#:</para>
    /// <code>
    /// P1   UserSettings.NudgeReportOperator("P1-DIRECT-ALIAS")            → census RED
    /// P2   var s = UserSettings; s.NudgeReportOperator("P2-LOCAL-ALIAS")  → census GREEN
    /// </code>
    /// <para>P2 was then built, served on :5300, and driven from
    /// <c>http://192.10.10.32:5300/scheduled-tasks</c> — an unauthenticated non-loopback caller,
    /// badge <c>viewer</c>, on a page that reads "restricted to Admin users". The control rendered
    /// and it worked, while every round-6-gated control was correctly absent. Green suite, live
    /// exploit.</para>
    ///
    /// <para>The receiver alias is not a bug in the round-6 regex; it is the FIRST member of an
    /// infinite family. After <c>var s = UserSettings</c> come lambdas, method groups, an
    /// interface reference, a local function, a delegate field, reflection,
    /// <c>Delegate.DynamicInvoke</c>. A source scanner that asks "what does this handler call?"
    /// cannot win that race, and widening it once more is precisely what rounds 4, 5 and 6 each
    /// did.</para>
    ///
    /// <para><b>So this scanner never looks at the call.</b> It finds interactive elements in
    /// MARKUP — event-handler attributes, <c>@bind</c>, <c>&lt;form&gt;</c>, and a component tag
    /// that binds one of the child's declared <c>EventCallback</c> parameters — and reports, for
    /// each, whether it lies inside a <c>&lt;ShellGate&gt;</c> region. P1 and P2 differ only in
    /// the C# inside the quotes, so they are now judged identically, and so is every shape nobody
    /// has thought of yet.</para>
    ///
    /// <para>The cost is honest and stated: a control that is inside a boundary is judged by the
    /// PERMISSION somebody chose for it, and a control that is on
    /// <see cref="ShellSurfaceRegistry.InertControls"/> is judged by a written claim that it
    /// touches nothing but this circuit's own view. Neither is free. Both are visible, in the
    /// diff, at review time — which is what none of the three defeated instruments could say.</para>
    /// </summary>
    internal static class ShellBoundaryScan
    {
        /// <summary>The boundary component. One name, so the scan is a substring away from proof.</summary>
        internal const string BoundaryTag = "ShellGate";

        /// <summary>
        /// One interactive element found in markup. <see cref="Permission"/> is non-null when the
        /// element sits inside a boundary; <see cref="Key"/> is the register key used when it does
        /// not.
        /// </summary>
        internal sealed record Element(
            string File,
            int Line,
            string Kind,
            string Attribute,
            string Expression,
            string? Permission,
            bool BreakGlass)
        {
            /// <summary>
            /// Stable identity for the exemption register: the file, the attribute, and the exact
            /// expression text. The expression is part of the KEY, never part of the decision —
            /// nothing here parses it. A control whose expression differs by one character is a
            /// different key and is therefore unreviewed, which is what makes an exemption
            /// impossible to inherit.
            /// </summary>
            internal string Key => File + " → " + Attribute
                                   + (Expression.Length == 0 ? "" : "=\"" + Expression + "\"");

            internal string Describe => Key + "   (line " + Line + ", " + Kind + ")";
        }

        // ── Public entry points ──────────────────────────────────────────

        /// <summary>Every interactive element in a shell component, with its boundary verdict.</summary>
        internal static List<Element> ElementsOf(string relativePath)
        {
            var full = Path.Combine(RawPassedScan.RepoRoot().FullName,
                                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return new List<Element>();
            return ElementsIn(relativePath, File.ReadAllText(full));
        }

        /// <summary>
        /// <see cref="ElementsOf"/> over text rather than a file, so the canaries can hand the
        /// scanner the exact shapes that defeated the last three instruments and prove it judges
        /// them all the same.
        /// </summary>
        internal static List<Element> ElementsIn(string file, string rawText)
        {
            var text = Blank(rawText);
            var regions = BoundaryRegions(text);
            var found = new List<Element>();

            void Add(int index, string kind, string attribute, string expression)
            {
                var region = regions.LastOrDefault(r => index >= r.Start && index < r.End);
                found.Add(new Element(
                    file,
                    LineOf(text, index),
                    kind,
                    attribute,
                    Collapse(expression),
                    region?.Permission,
                    region?.BreakGlass ?? false));
            }

            // (a) DOM event handlers: @onclick, @onchange, @oninput, @onsubmit, @onkeydown,
            //     @onkeypress, @onfocus, @onmouseenter — every @on… there is, not a chosen list.
            //     The lookahead for `=` is what excludes the MODIFIERS `@onclick:stopPropagation`
            //     and `@onclick:preventDefault`, which take a bool and register no delegate.
            foreach (Match m in Regex.Matches(text, @"@(on[a-z]+)(?=\s*=)"))
                Add(m.Index, "dom-event", "@" + m.Groups[1].Value, ValueAt(text, m.Index + m.Length));

            // (b) @bind and @bind-Value — a two-way binding IS a change handler. `@bind:event`
            //     and `@bind:format` are modifiers and are excluded by the same lookahead.
            foreach (Match m in Regex.Matches(text, @"@bind(-[A-Za-z0-9_]+)?(?=\s*=)"))
                Add(m.Index, "bind", m.Value, ValueAt(text, m.Index + m.Length));

            // (c) <form> — submits without any @on… attribute at all.
            foreach (Match m in Regex.Matches(text, @"<form\b"))
                Add(m.Index, "form", "<form>", "");

            // (d) A component tag that binds one of the CHILD's declared EventCallback
            //     parameters. <ToggleSwitch ValueChanged="ToggleExperimentalMode" /> is an
            //     interactive control at THIS use site: the switch's own @onclick lives in
            //     ToggleSwitch.razor and is generic, so censusing it there would say nothing about
            //     who may flip an install-wide setting here. Derived from the child's [Parameter]
            //     declarations rather than from a name pattern — "starts with On, ends with
            //     Changed" is a lexicon, and a lexicon is what this round exists to stop using.
            foreach (Match m in Regex.Matches(text, @"<([A-Z][A-Za-z0-9_]*)\b"))
            {
                var callbacks = CallbackParametersOf(m.Groups[1].Value);
                if (callbacks.Count == 0) continue;

                var close = FindTagEnd(text, m.Index);
                if (close < 0) continue;
                var tagText = text[m.Index..close];

                foreach (var name in callbacks)
                    foreach (Match a in Regex.Matches(tagText, @"(?<![A-Za-z0-9_@])@?" + Regex.Escape(name) + @"(?=\s*=)"))
                        Add(m.Index + a.Index, "component-callback",
                            m.Groups[1].Value + "." + name,
                            ValueAt(text, m.Index + a.Index + a.Length));
            }

            return found.OrderBy(e => e.Line).ThenBy(e => e.Attribute, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// <c>[JSInvokable]</c> methods in a shell component. A C# method cannot sit inside a
        /// markup boundary, so these cannot be answered by the boundary rule and are answered by
        /// <see cref="ShellSurfaceRegistry.ShellJsInvokables"/> instead — a register entry per
        /// method, each carrying the reason it is safe to expose to the browser's own JS.
        /// </summary>
        internal static List<string> JsInvokablesOf(string relativePath)
        {
            var full = Path.Combine(RawPassedScan.RepoRoot().FullName,
                                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            var text = File.Exists(full) ? File.ReadAllText(full) : "";
            if (File.Exists(full + ".cs")) text += File.ReadAllText(full + ".cs");

            var code = string.Join("\n", text.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith("*", StringComparison.Ordinal)));

            return Regex.Matches(code,
                    @"\[(?:Microsoft\.JSInterop\.)?JSInvokable[^\]]*\]\s*(?:public\s+)?(?:async\s+)?[A-Za-z0-9_<>?\.\[\]]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(")
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }

        // ── Boundary regions ─────────────────────────────────────────────

        internal sealed record Region(int Start, int End, string Permission, bool BreakGlass);

        /// <summary>
        /// The <c>&lt;ShellGate&gt;…&lt;/ShellGate&gt;</c> spans in a file, nesting respected. A
        /// self-closing <c>&lt;ShellGate /&gt;</c> has no children and opens no region, so it
        /// cannot be used to "cover" the markup that follows it.
        /// </summary>
        internal static List<Region> BoundaryRegions(string text)
        {
            var open = new Regex(@"<" + BoundaryTag + @"\b", RegexOptions.Compiled);
            var close = new Regex(@"</" + BoundaryTag + @"\s*>", RegexOptions.Compiled);

            var marks = new List<(int Index, bool IsOpen, int End, string Permission, bool BreakGlass)>();

            foreach (Match m in open.Matches(text))
            {
                var end = FindTagEnd(text, m.Index);
                if (end < 0) continue;
                var tag = text[m.Index..end];
                if (tag.TrimEnd().EndsWith("/", StringComparison.Ordinal)) continue;   // self-closing

                var perm = Regex.Match(tag, @"Permission\s*=\s*""([^""]*)""");
                var bg = Regex.IsMatch(tag, @"(?<![A-Za-z0-9_])BreakGlass\b(?!\s*=\s*""?false)");
                marks.Add((m.Index, true, end, perm.Success ? perm.Groups[1].Value : "", bg));
            }
            foreach (Match m in close.Matches(text))
                marks.Add((m.Index, false, m.Index + m.Length, "", false));

            marks.Sort((a, b) => a.Index.CompareTo(b.Index));

            var regions = new List<Region>();
            var stack = new Stack<(int ContentStart, string Permission, bool BreakGlass)>();
            foreach (var mark in marks)
            {
                if (mark.IsOpen) stack.Push((mark.End, mark.Permission, mark.BreakGlass));
                else if (stack.Count > 0)
                {
                    var opened = stack.Pop();
                    regions.Add(new Region(opened.ContentStart, mark.Index, opened.Permission, opened.BreakGlass));
                }
            }
            return regions.OrderBy(r => r.Start).ToList();
        }

        // ── Text helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Replaces comment bodies with spaces, preserving every offset and line break so the
        /// reported line numbers stay true. A control commented out is not a control; a control
        /// MENTIONED in a comment is not one either, and five rounds of these censuses have all
        /// had to say so.
        /// </summary>
        internal static string Blank(string text)
        {
            var buffer = new StringBuilder(text);

            void Wipe(string pattern)
            {
                foreach (Match m in Regex.Matches(buffer.ToString(), pattern, RegexOptions.Singleline))
                    for (var i = m.Index; i < m.Index + m.Length; i++)
                        if (buffer[i] != '\n' && buffer[i] != '\r') buffer[i] = ' ';
            }

            Wipe(@"@\*.*?\*@");          // razor comment
            Wipe(@"<!--.*?-->");         // html comment
            Wipe(@"^[ \t]*///.*$");      // xml doc line
            return buffer.ToString();
        }

        /// <summary>Index just past the '&gt;' that closes the tag opening at <paramref name="start"/>.</summary>
        internal static int FindTagEnd(string text, int start)
        {
            char quote = '\0';
            for (var i = start; i < text.Length; i++)
            {
                var c = text[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '>') return i + 1;
            }
            return -1;
        }

        /// <summary>
        /// The quoted attribute value starting at or after <paramref name="from"/>. Handles both
        /// quote characters, because the shell uses single quotes wherever the expression itself
        /// contains a string — <c>@onclick='() =&gt; Navigation.NavigateTo("/settings")'</c>.
        /// </summary>
        internal static string ValueAt(string text, int from)
        {
            var i = from;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || text[i] != '=') return "";
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) return "";

            if (text[i] == '"' || text[i] == '\'')
            {
                // Razor balances parentheses inside an @(…) expression, so a double-quoted
                // attribute may legally contain double quotes:
                //     @onclick="@(() => SetCategory("Governance"))"
                // Stopping at the first matching quote truncates that to `@(() => SetCategory(`
                // — which collapsed NavMenu's four category controls into ONE register key, i.e.
                // one exemption would have covered four different controls. Depth-aware.
                var quote = text[i];
                var depth = 0;
                for (var j = i + 1; j < text.Length; j++)
                {
                    var c = text[j];
                    if (c == '(') depth++;
                    else if (c == ')') { if (depth > 0) depth--; }
                    else if (c == quote && depth == 0) return text[(i + 1)..j];
                }
                return "";
            }

            var stop = i;
            while (stop < text.Length && !char.IsWhiteSpace(text[stop]) && text[stop] != '>') stop++;
            return text[i..stop];
        }

        internal static string Collapse(string value) =>
            Regex.Replace(value, @"\s+", " ").Trim();

        internal static int LineOf(string text, int index) =>
            text.Take(Math.Min(index, text.Length)).Count(c => c == '\n') + 1;

        // ── The child components' declared EventCallback parameters ──────

        private static readonly Lazy<Dictionary<string, List<string>>> _callbacks =
            new(ComputeCallbackParameters);

        internal static List<string> CallbackParametersOf(string componentName) =>
            _callbacks.Value.TryGetValue(componentName, out var names) ? names : new List<string>();

        private static Dictionary<string, List<string>> ComputeCallbackParameters()
        {
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var root = RawPassedScan.RepoRoot();

            foreach (var dirName in new[] { "Components", "Pages" })
            {
                var dir = new DirectoryInfo(Path.Combine(root.FullName, dirName));
                if (!dir.Exists) continue;

                foreach (var file in dir.EnumerateFiles("*.razor", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(file.Name);
                    if (map.ContainsKey(name)) continue;

                    var text = File.ReadAllText(file.FullName);
                    var behind = file.FullName + ".cs";
                    if (File.Exists(behind)) text += File.ReadAllText(behind);

                    map[name] = Regex.Matches(text,
                            @"\[Parameter[^\]]*\]\s*(?:\[[^\]]*\]\s*)*public\s+EventCallback(?:<[^>]*>)?\s+([A-Za-z_][A-Za-z0-9_]*)")
                        .Select(m => m.Groups[1].Value)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                }
            }
            return map;
        }
    }
}
