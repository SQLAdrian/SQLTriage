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
    /// A2 (Adrian ruled it in, 2026-07-20) — the enumerator behind the raw-<c>.Passed</c> guard.
    ///
    /// <para>FOUR consecutive cold gates each found a NEW false-clean at the same seam: a consumer
    /// reading <c>CheckResult.Passed</c> raw, outside the classification discipline. WARN, SKIP and
    /// INFO all ride Passed=true, so every such read silently promotes a non-assertion to a pass.
    /// Fixing them one at a time has now failed four times running. This makes ADDING one loud.</para>
    ///
    /// <para>This class only enumerates and classifies; <c>RawPassedGuardTests</c> holds it to the
    /// checked-in allowlist. See that file for the guard's stated limits.</para>
    /// </summary>
    internal static class RawPassedScan
    {
        /// <summary>Directories scanned, relative to the repo root. Data/ covers the portal paths
        /// (Data/Services/Portal). Components/ is in scope because the trend page's sparkline
        /// mapping lives there.</summary>
        internal static readonly string[] ScanRoots = { "Data", "Pages", "Cli", "Components" };

        /// <summary>Individual files scanned that sit outside those roots.</summary>
        internal static readonly string[] ScanFiles = { "Program.cs" };

        private static readonly Regex PassedRead = new(@"\.Passed\b", RegexOptions.Compiled);

        /// <summary>
        /// The PATTERN channel: <c>Passed</c> used as a property-pattern subpattern name, which
        /// reads the bit without ever writing <c>.Passed</c>. All three shapes share this text —
        /// property (<c>r is { Passed: true }</c>), list (<c>rows is [{ Passed: true }, ..]</c>)
        /// and switch (<c>case { Passed: false }:</c>, <c>{ Passed: true } =&gt; …</c>).
        ///
        /// <para>The lookbehind drops <c>x.Passed :</c> (a ternary) and <c>Inner.Passed:</c> (an
        /// extended property pattern) — both already carry a literal <c>.Passed</c> and are counted
        /// by <see cref="PassedRead"/>; matching here too would double-count them.</para>
        /// </summary>
        private static readonly Regex PassedPattern = new(@"(?<![.\w])Passed\s*:", RegexOptions.Compiled);

        /// <summary>One <c>.Passed</c> occurrence in shipped source.</summary>
        internal sealed record Occurrence(string RelativePath, int Line, string Code, bool IsWrite)
        {
            /// <summary>The allowlist key: path plus the normalised code text. Deliberately NOT
            /// the line number — anchoring on line numbers would make every unrelated edit above a
            /// site churn the allowlist, and an allowlist that churns is an allowlist nobody
            /// reads.</summary>
            public string Key => RelativePath + "\t" + Code;
        }

        /// <summary>Walks up from the test binary to the repo root (the directory holding the .sln).</summary>
        internal static DirectoryInfo RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln")))
                dir = dir.Parent;

            if (dir == null)
                throw new InvalidOperationException(
                    "Could not find the repo root (SQLTriage.sln) from " + AppContext.BaseDirectory +
                    ". The guard cannot scan, and must fail rather than pass over nothing.");

            return dir;
        }

        internal static List<string> SourceFiles(DirectoryInfo root)
        {
            var files = new List<string>();

            foreach (var rel in ScanRoots)
            {
                var dir = Path.Combine(root.FullName, rel);
                if (!Directory.Exists(dir)) continue;

                files.AddRange(Directory
                    .EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                    .Where(f => !IsBuildOutput(f)));
            }

            foreach (var rel in ScanFiles)
            {
                var f = Path.Combine(root.FullName, rel);
                if (File.Exists(f)) files.Add(f);
            }

            return files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        private static bool IsBuildOutput(string path)
        {
            var p = path.Replace('/', Path.DirectorySeparatorChar);
            var sep = Path.DirectorySeparatorChar;
            return p.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)
                || p.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Every <c>.Passed</c> occurrence in the scanned source, writes included.</summary>
        internal static List<Occurrence> ScanAll(DirectoryInfo root)
        {
            var results = new List<Occurrence>();

            foreach (var file in SourceFiles(root))
            {
                var rel = Path.GetRelativePath(root.FullName, file).Replace('\\', '/');
                var isRazor = file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
                var lines = File.ReadAllLines(file);
                var code = StripComments(lines, isRazor);

                for (var i = 0; i < code.Length; i++)
                {
                    foreach (Match m in PassedRead.Matches(code[i]))
                    {
                        results.Add(new Occurrence(
                            rel,
                            i + 1,
                            Normalize(code[i]),
                            IsWrite(code[i], m.Index + m.Length)));
                    }

                    foreach (Match m in PassedPattern.Matches(code[i]))
                    {
                        // A subpattern is always a READ — you cannot assign through a pattern.
                        if (IsPatternSubpattern(code[i], m.Index))
                            results.Add(new Occurrence(rel, i + 1, Normalize(code[i]), IsWrite: false));
                    }
                }
            }

            return results;
        }

        /// <summary>The reads — what the allowlist governs. A write is not a consumer.</summary>
        internal static List<Occurrence> ScanReads(DirectoryInfo root) =>
            ScanAll(root).Where(o => !o.IsWrite).ToList();

        /// <summary>
        /// True when the occurrence is being ASSIGNED rather than read: <c>x.Passed = v</c>,
        /// <c>x.Passed++</c>, <c>x.Passed += v</c>. <c>==</c> is a comparison, so it is a read.
        /// </summary>
        private static bool IsWrite(string line, int afterMatch)
        {
            var i = afterMatch;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) return false;

            if (line[i] == '=')
                return i + 1 >= line.Length || line[i + 1] != '=';

            if (i + 1 < line.Length && (line[i] == '+' || line[i] == '-') &&
                (line[i + 1] == line[i] || line[i + 1] == '='))
                return true;

            return false;
        }

        /// <summary>
        /// True when the <c>Passed:</c> at <paramref name="index"/> is a property-pattern
        /// subpattern rather than a tuple element name, a named argument or message text.
        ///
        /// <para><b>The discriminator is the innermost unclosed bracket to its left on the same
        /// line.</b> A subpattern is always inside a brace — <c>{ Passed: true }</c>,
        /// <c>[{ Passed: true }, ..]</c>, <c>case { Passed: false }:</c>. The near-misses that
        /// actually exist in this repo are all inside a PAREN: the named tuple elements in
        /// <c>AssessmentPdf</c> (<c>(Area: g.Key, Passed: pas, …)</c>), the named argument in
        /// <c>GovernanceHistoryService</c>, and the log/format strings in
        /// <c>SqlAssessmentService</c> and <c>CioDashboard.razor</c>
        /// (<c>$"Passed: {n}"</c>). Anchoring on <c>{</c> alone would miss a second subpattern
        /// (<c>{ Severity: "High", Passed: true }</c>, preceded by a comma); anchoring on
        /// <c>{</c>-or-<c>,</c> would swallow every one of those tuples. The bracket walk
        /// separates them without a parser.</para>
        ///
        /// <para><b>Limit — it is line-local.</b> A property pattern split so that
        /// <c>Passed:</c> lands on a line carrying no unclosed opener of its own reads as
        /// "no enclosing bracket" and is NOT reported. That is stated in the guard's limits list;
        /// it is not silently assumed away here.</para>
        /// </summary>
        internal static bool IsPatternSubpattern(string line, int index)
            => EnclosingOpener(line, index) == '{';

        /// <summary>The innermost bracket still open to the left of <paramref name="index"/> on
        /// this line, or <c>'\0'</c> if none is.</summary>
        internal static char EnclosingOpener(string line, int index)
        {
            var depth = 0;

            for (var i = Math.Min(index, line.Length) - 1; i >= 0; i--)
            {
                var c = line[i];

                if (c == ')' || c == ']' || c == '}')
                {
                    depth++;
                }
                else if (c == '(' || c == '[' || c == '{')
                {
                    if (depth == 0) return c;
                    depth--;
                }
            }

            return '\0';
        }

        /// <summary>Whitespace-collapsed, trimmed code text — stable against reindentation.</summary>
        internal static string Normalize(string line) =>
            Regex.Replace(line, @"\s+", " ").Trim();

        /// <summary>
        /// Blanks comments while KEEPING string contents.
        ///
        /// <para>String bodies are kept on purpose: C# interpolated strings contain real code
        /// (<c>$"...{results.Count(r =&gt; r.Outcome == X.Passed)}..."</c>), and Razor passes C#
        /// expressions inside quoted attributes. Blanking strings would hide those — a guard that
        /// hides reads is worse than no guard. The cost is that a <c>.Passed</c> appearing as
        /// literal text inside a message string is reported and needs an allowlist line; that
        /// direction of error is the safe one.</para>
        ///
        /// <para>String state is reset at every newline. Verbatim and raw string literals can
        /// therefore be mis-lexed on their continuation lines, which at worst truncates a line at
        /// a <c>//</c> inside embedded text. That cannot manufacture a false PASS for the guard —
        /// it can only make it report a spurious site, which surfaces as a failing guard, not a
        /// silent one.</para>
        /// </summary>
        internal static string[] StripComments(string[] lines, bool isRazor)
        {
            var output = new string[lines.Length];
            var inBlockComment = false;
            var inRazorComment = false;

            for (var li = 0; li < lines.Length; li++)
            {
                var line = lines[li];
                var sb = new StringBuilder(line.Length);
                var i = 0;
                var inString = false;
                var inChar = false;

                while (i < line.Length)
                {
                    if (inRazorComment)
                    {
                        var end = line.IndexOf("*@", i, StringComparison.Ordinal);
                        if (end < 0) break;
                        inRazorComment = false;
                        i = end + 2;
                        continue;
                    }

                    if (inBlockComment)
                    {
                        var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                        if (end < 0) break;
                        inBlockComment = false;
                        i = end + 2;
                        continue;
                    }

                    var c = line[i];

                    if (inString || inChar)
                    {
                        if (c == '\\' && i + 1 < line.Length)
                        {
                            sb.Append(c).Append(line[i + 1]);
                            i += 2;
                            continue;
                        }
                        if (inString && c == '"') inString = false;
                        if (inChar && c == '\'') inChar = false;
                        sb.Append(c);
                        i++;
                        continue;
                    }

                    if (isRazor && c == '@' && i + 1 < line.Length && line[i + 1] == '*')
                    {
                        inRazorComment = true;
                        i += 2;
                        continue;
                    }

                    if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                        break;                      // line comment — discard the remainder

                    if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                    {
                        inBlockComment = true;
                        i += 2;
                        continue;
                    }

                    if (c == '"') { inString = true; sb.Append(c); i++; continue; }
                    if (c == '\'') { inChar = true; sb.Append(c); i++; continue; }

                    sb.Append(c);
                    i++;
                }

                output[li] = sb.ToString();
            }

            return output;
        }
    }
}
