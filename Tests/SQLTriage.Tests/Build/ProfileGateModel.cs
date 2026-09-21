/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SQLTriage.Tests.Build
{
    /// <summary>
    /// Derives, from the SAME build inputs the compiler uses, the set of symbols a COMMUNITY build
    /// removes from SQLTriage.dll — so a test can be held to it without anyone maintaining a list
    /// of gated names by hand.
    ///
    /// <para>Inputs: <c>buildprofile.json</c> (module + per-report states), <c>buildprofile.targets</c>
    /// (the Compile/Content Removes and the DefineConstants they drive) and the app sources (the
    /// <c>#if</c> fences those constants switch). Nothing here is a hardcoded list of gated types;
    /// add a new gate in the targets file and this picks it up.</para>
    ///
    /// <para>⚠ This is a LINT, not the boundary. The boundary is the community build itself, which
    /// CI runs on every push. A source scan can be walked around — by reflection, by a type alias,
    /// by a name this parser does not recognise as a declaration — and this repo has already
    /// watched four successive static scanners get defeated (see the RBAC rounds). Its value is
    /// EARLINESS, not authority: it fails in the default-profile suite a developer actually runs,
    /// which stayed green through the whole 11-day outage that started this.</para>
    ///
    /// <para>This class only models and enumerates; <see cref="ProfileGatedTestSyncTests"/> holds
    /// the tree to it.</para>
    /// </summary>
    internal static class ProfileGateModel
    {
        // ── MSBuild-ish evaluation, scoped to exactly what buildprofile.targets does ──────────

        private static readonly Regex CondEquality =
            new(@"^\s*'\$\((?<prop>\w+)\)'\s*(?<op>==|!=)\s*'(?<val>[^']*)'\s*$", RegexOptions.Compiled);

        /// <summary>The one property function buildprofile.targets uses, verbatim: pull a
        /// <c>"key": "value"</c> out of buildprofile.json. Recognising the SHAPE (rather than the
        /// eight keys that exist today) is what makes a newly added report key self-covering.</summary>
        private static readonly Regex JsonKeyProbe =
            new(@"Regex\]::Match\(.*?'""(?<key>[\w-]+)""", RegexOptions.Compiled);

        private static readonly Regex JsonPair =
            new(@"""(?<k>[\w-]+)""\s*:\s*""(?<v>[^""]*)""", RegexOptions.Compiled);

        /// <summary>
        /// Evaluates buildprofile.targets the way MSBuild would for the profile described by
        /// <paramref name="seed"/> (e.g. SQLTriageProfile=community), returning the resulting
        /// property bag and the DefineConstants symbols.
        /// </summary>
        internal sealed record ProfileEvaluation(
            IReadOnlyDictionary<string, string> Properties,
            IReadOnlyCollection<string> DefinedSymbols,
            XElement TargetsRoot);

        internal static ProfileEvaluation Evaluate(DirectoryInfo repoRoot, IDictionary<string, string> seed)
        {
            var targetsPath = Path.Combine(repoRoot.FullName, "buildprofile.targets");
            var jsonPath = Path.Combine(repoRoot.FullName, "buildprofile.json");
            if (!File.Exists(targetsPath))
                throw new InvalidOperationException(
                    "buildprofile.targets is missing at " + targetsPath +
                    ". The gate model cannot be derived, and must fail rather than pass over nothing.");

            var json = File.Exists(jsonPath) ? File.ReadAllText(jsonPath) : "";
            var jsonPairs = JsonPair.Matches(json)
                .Cast<Match>()
                .GroupBy(m => m.Groups["k"].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Groups["v"].Value, StringComparer.OrdinalIgnoreCase);

            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in seed) props[kv.Key] = kv.Value;

            var root = XDocument.Load(targetsPath).Root!;
            var symbols = new HashSet<string>(StringComparer.Ordinal);

            // Document order matters: the community PropertyGroup sets the SQLTExclude* flags that
            // later PropertyGroups' conditions read. One pass in order reproduces that.
            foreach (var pg in root.Elements("PropertyGroup"))
            {
                if (!ConditionHolds(pg.Attribute("Condition")?.Value, props)) continue;

                foreach (var prop in pg.Elements())
                {
                    if (!ConditionHolds(prop.Attribute("Condition")?.Value, props)) continue;

                    var name = prop.Name.LocalName;
                    var raw = prop.Value;

                    if (name == "DefineConstants")
                    {
                        // "$(DefineConstants);SQLT_NO_PREMIUM" -> SQLT_NO_PREMIUM
                        foreach (var piece in raw.Split(';'))
                        {
                            var t = piece.Trim();
                            if (t.Length > 0 && !t.StartsWith("$(", StringComparison.Ordinal))
                                symbols.Add(t);
                        }
                        continue;
                    }

                    var probe = JsonKeyProbe.Match(raw);
                    props[name] = probe.Success
                        ? (jsonPairs.TryGetValue(probe.Groups["key"].Value, out var v) ? v : "")
                        : raw.Trim();
                }
            }

            return new ProfileEvaluation(props, symbols, root);
        }

        /// <summary>
        /// True when an MSBuild Condition holds under <paramref name="props"/>. An absent condition
        /// is unconditional. Anything this parser does not understand is treated as HOLDING —
        /// fail-closed, matching the rest of the profile machinery: an unrecognised new gate is
        /// assumed to exclude things, so its files are treated as gated and a test that binds them
        /// is reported. A false positive here is a loud prompt to teach the parser; the opposite
        /// error is the silent one that produced this guard.
        /// </summary>
        private static bool ConditionHolds(string? condition, IReadOnlyDictionary<string, string> props)
        {
            if (string.IsNullOrWhiteSpace(condition)) return true;

            // Only the AND of simple equalities appears in buildprofile.targets.
            foreach (var clause in Regex.Split(condition, @"\s+AND\s+", RegexOptions.IgnoreCase))
            {
                var m = CondEquality.Match(clause);
                if (!m.Success) continue; // not understood -> treat this clause as satisfied

                var actual = props.TryGetValue(m.Groups["prop"].Value, out var v) ? v : "";
                var expected = m.Groups["val"].Value;
                var equal = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
                if (m.Groups["op"].Value == "==" ? !equal : equal) return false;
            }
            return true;
        }

        // ── What a community build removes ───────────────────────────────────────────────────

        internal sealed record GatedSurface(
            IReadOnlyCollection<string> Files,        // repo-relative, forward slashes
            IReadOnlyCollection<string> RemovedPages, // .razor Content Removes, repo-relative
            IReadOnlyCollection<string> Symbols);     // DefineConstants in force

        /// <summary>Files and pages a community build compiles OUT, resolved from the targets file.</summary>
        internal static GatedSurface CommunitySurface(DirectoryInfo repoRoot)
        {
            var eval = Evaluate(repoRoot, new Dictionary<string, string>
            {
                ["SQLTriageProfile"] = "community",
                ["SQLTriagePrivate"] = "",
            });

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ig in eval.TargetsRoot.Elements("ItemGroup"))
            {
                if (!ConditionHolds(ig.Attribute("Condition")?.Value, eval.Properties)) continue;

                foreach (var item in ig.Elements())
                {
                    var remove = item.Attribute("Remove")?.Value;
                    if (string.IsNullOrWhiteSpace(remove)) continue;
                    if (!ConditionHolds(item.Attribute("Condition")?.Value, eval.Properties)) continue;

                    var kind = item.Name.LocalName;
                    foreach (var hit in Expand(repoRoot, remove!))
                    {
                        if (kind == "Compile" && hit.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                            files.Add(hit);
                        else if (hit.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                            pages.Add(hit);
                    }
                }
            }

            return new GatedSurface(files, pages, eval.DefinedSymbols);
        }

        /// <summary>Resolves an MSBuild Remove pattern (literal or <c>**\*.ext</c> glob) against disk.</summary>
        internal static IEnumerable<string> Expand(DirectoryInfo root, string pattern)
        {
            var rel = pattern.Replace('\\', '/').Trim();
            if (rel.Length == 0) yield break;

            var star = rel.IndexOf('*');
            if (star < 0)
            {
                var full = Path.Combine(root.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) yield return rel;
                yield break;
            }

            var slash = rel.LastIndexOf('/', Math.Max(star - 1, 0));
            var dirPart = slash > 0 ? rel.Substring(0, slash) : "";
            var tail = rel.Substring(rel.LastIndexOf('.') >= 0 ? rel.LastIndexOf('.') : rel.Length);
            var dir = Path.Combine(root.FullName, dirPart.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) yield break;

            var searchOption = rel.Contains("**") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var f in Directory.EnumerateFiles(dir, "*" + tail, searchOption))
                yield return Rel(root, f);
        }

        internal static string Rel(DirectoryInfo root, string full) =>
            Path.GetRelativePath(root.FullName, full).Replace('\\', '/');

        /// <summary>
        /// The fully qualified class name Razor generates for a Content-Removed <c>.razor</c> page,
        /// e.g. <c>Pages/Sessions.razor</c> -&gt; <c>SQLTriage.Pages.Sessions</c> (folder path becomes
        /// namespace, exactly as Razor's own generator does, under the "SQLTriage" root namespace this
        /// file already hardcodes elsewhere — see the <c>SQLTriage(\.\w+)+</c> probe below). Returned
        /// as ONE dotted string, never split into a bare class name: a bare basename such as
        /// "Services" (<c>Pages/Services.razor</c>) or "Remediation" (<c>Pages/Remediation.razor</c>)
        /// collides with unrelated shipped namespace segments (<c>SQLTriage.Data.Services</c>,
        /// <c>SQLTriage.Data.Services.Remediation</c>) — proven by mutation on 2026-08-21: trying the
        /// bare form flooded the offender scan with ~220 false positives before this qualified form
        /// replaced it.
        /// </summary>
        internal static IEnumerable<string> QualifiedTypeNamesFromRemovedPages(IEnumerable<string> removedPages)
        {
            const string rootNamespace = "SQLTriage";
            foreach (var page in removedPages)
            {
                var rel = page.Replace('\\', '/');
                var name = Path.GetFileNameWithoutExtension(rel);
                if (string.IsNullOrEmpty(name)) continue;
                var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
                var ns = dir.Length == 0 ? rootNamespace : rootNamespace + "." + dir.Replace('/', '.');
                yield return ns + "." + name;
            }
        }

        // ── Declarations, so the gated FILES can be turned into gated NAMES ──────────────────

        private static readonly Regex TypeDecl = new(
            @"^\s*(?:\[[^\]]*\]\s*)*(?<access>public|internal|private|protected)?\s*" +
            @"(?:sealed\s+|static\s+|abstract\s+|partial\s+|readonly\s+|ref\s+|file\s+)*" +
            @"(?:class|record|struct|interface|enum)\s+(?<name>[A-Za-z_]\w*)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex NamespaceDecl =
            new(@"^\s*namespace\s+(?<ns>[\w.]+)", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// Type names a test could bind. <c>private</c>/<c>protected</c> nested types are skipped:
        /// no test can reference one, so gating on them can only ever produce a false positive —
        /// which is exactly what <c>ReplicationMap.razor.cs</c>'s <c>private sealed class Row</c>
        /// did against an unrelated test's own <c>Row(...)</c> helper.
        /// </summary>
        internal static IEnumerable<string> TypeNamesIn(string source) =>
            TypeDecl.Matches(source).Cast<Match>()
                .Where(m => m.Groups["access"].Value is not ("private" or "protected"))
                .Select(m => m.Groups["name"].Value);

        internal static IEnumerable<string> NamespacesIn(string source) =>
            NamespaceDecl.Matches(source).Cast<Match>().Select(m => m.Groups["ns"].Value);

        private static readonly Regex MemberDecl = new(
            @"^\s*(?:public|internal)\s+" +
            @"(?:static\s+|async\s+|partial\s+|sealed\s+|virtual\s+|override\s+|readonly\s+|const\s+|new\s+|unsafe\s+|extern\s+)*" +
            @"[A-Za-z_][\w<>,.\[\]?\s]*?\b(?<name>[A-Za-z_]\w*)\s*(?:\(|=>|\{|;|=)", RegexOptions.Compiled);

        /// <summary>
        /// Members a community build compiles OUT of a file that OTHERWISE STILL COMPILES, reported
        /// as <c>DeclaringType.Member</c>. THIS is the category that hides: the file is still in the
        /// build, so any audit that reasons about files sees nothing — and two of the four tests
        /// stranded on 2026-08-04 were of exactly this kind
        /// (<c>AssessmentPdf.BuildFindingsReport</c>, <c>AssessmentPdf.BuildDbaHandoffBundle</c>).
        ///
        /// <para><paramref name="alsoCompiled"/> receives the members declared in the regions that
        /// ARE compiled. Both halves are needed: <c>BuildModules</c> declares each module const in
        /// BOTH arms of an <c>#if/#else</c>, so the const is never actually absent — reporting the
        /// excluded arm alone made <c>BuildModules.Portal</c> look gated when it is not.</para>
        ///
        /// <para>Qualifying by declaring type is what keeps this usable: bare member names collide
        /// constantly with unrelated code (a test's own private <c>Row(...)</c> helper, a
        /// <c>ManifestFeatures.Portal</c> property), and an unqualified match reported both.</para>
        /// </summary>
        internal static IEnumerable<string> FencedOutMembersIn(
            string source, ISet<string> definedSymbols, ICollection<string> alsoCompiled)
        {
            var excluded = new Stack<bool>();
            var declaringType = "";

            foreach (var raw in source.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var t = line.TrimStart();

                if (t.StartsWith("#if", StringComparison.Ordinal)) { excluded.Push(RegionIsOut(t, definedSymbols)); continue; }
                if (t.StartsWith("#elif", StringComparison.Ordinal))
                {
                    if (excluded.Count > 0) { excluded.Pop(); excluded.Push(RegionIsOut(t, definedSymbols)); }
                    continue;
                }
                if (t.StartsWith("#else", StringComparison.Ordinal))
                {
                    if (excluded.Count > 0) { var cur = excluded.Pop(); excluded.Push(!cur); }
                    continue;
                }
                if (t.StartsWith("#endif", StringComparison.Ordinal))
                {
                    if (excluded.Count > 0) excluded.Pop();
                    continue;
                }

                var typeHit = TypeDecl.Match(line);
                if (typeHit.Success) { declaringType = typeHit.Groups["name"].Value; continue; }

                var m = MemberDecl.Match(line);
                if (!m.Success || declaringType.Length == 0) continue;

                var qualified = declaringType + "." + m.Groups["name"].Value;
                if (excluded.Contains(true)) yield return qualified;
                else alsoCompiled.Add(qualified);
            }
        }

        /// <summary>
        /// True when the region opened by <paramref name="directive"/> is NOT compiled under
        /// <paramref name="defined"/>. Only the SQLT_* profile vocabulary is modelled: DEBUG and
        /// ENABLE_MCP are deliberately out of scope (they are not profile gates, and DEBUG flips
        /// with configuration rather than with a shipped edition), so a member fenced on those is
        /// invisible to this lint. Stated, not silently assumed.
        /// </summary>
        private static bool RegionIsOut(string directive, ISet<string> defined)
        {
            var cond = directive.Substring(directive.IndexOf(' ') + 1).Trim();
            var negated = new HashSet<string>(Regex.Matches(cond, @"!\s*(?<s>\w+)")
                .Cast<Match>().Select(m => m.Groups["s"].Value), StringComparer.Ordinal);
            var all = new HashSet<string>(Regex.Matches(cond, @"\w+")
                .Cast<Match>().Select(m => m.Value), StringComparer.Ordinal);
            var positive = all.Except(negated).ToList();

            // "#if !SQLT_NO_X" with SQLT_NO_X defined  -> the region is compiled OUT.
            if (negated.Any(defined.Contains)) return true;
            // "#if SQLT_PERFREPORT" with the symbol undefined -> compiled OUT (profile symbols only).
            if (positive.Any(s => s.StartsWith("SQLT_", StringComparison.Ordinal) && !defined.Contains(s)))
                return true;
            return false;
        }

        // ── Reading a test project's own community gating ────────────────────────────────────

        /// <summary>The Compile Remove patterns the TEST project applies for a community build.</summary>
        internal static IReadOnlyList<string> TestProjectCommunityRemovals(DirectoryInfo repoRoot)
        {
            var csproj = Path.Combine(repoRoot.FullName, "Tests", "SQLTriage.Tests", "SQLTriage.Tests.csproj");
            var root = XDocument.Load(csproj).Root!;
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SQLTriageProfile"] = "community",
            };

            return (from ig in root.Elements("ItemGroup")
                    where ConditionHolds(ig.Attribute("Condition")?.Value, props)
                       && (ig.Attribute("Condition")?.Value ?? "").Contains("SQLTriageProfile")
                    from item in ig.Elements("Compile")
                    let r = item.Attribute("Remove")?.Value
                    where !string.IsNullOrWhiteSpace(r)
                    select r!.Replace('\\', '/'))
                   .ToList();
        }

        /// <summary>Does a community test build still compile this repo-relative test file?</summary>
        internal static bool TestFileIsCompiledInCommunity(string relativeToTestProject, IReadOnlyList<string> removals)
        {
            var p = relativeToTestProject.Replace('\\', '/');
            foreach (var pattern in removals)
            {
                if (pattern.IndexOf('*') < 0)
                {
                    if (string.Equals(pattern, p, StringComparison.OrdinalIgnoreCase)) return false;
                    continue;
                }
                var prefix = pattern.Substring(0, pattern.IndexOf('*')).TrimEnd('/');
                var ext = Path.GetExtension(pattern);
                if (p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
                    && p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        // ── Source text, minus the places a NAME is not a BINDING ────────────────────────────

        /// <summary>
        /// Blanks comments and string literals. Without this the scan drowns in false positives:
        /// the RBAC census tests name gated .razor paths in string literals by design, and the
        /// gating comments in the test csproj's own siblings name the gated types on purpose.
        /// <para>⚠ Interpolated holes are blanked with the rest of the string, so a gated type named
        /// only inside <c>$"{...}"</c> is invisible to this lint.</para>
        /// </summary>
        internal static string StripCommentsAndStrings(string src)
        {
            var sb = new System.Text.StringBuilder(src.Length);
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];

                if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
                {
                    while (i < src.Length && src[i] != '\n') i++;
                    sb.Append('\n');
                    continue;
                }
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) { if (src[i] == '\n') sb.Append('\n'); i++; }
                    i++;
                    continue;
                }
                if (c == '@' && i + 1 < src.Length && src[i + 1] == '"')
                {
                    i += 2;
                    while (i < src.Length)
                    {
                        if (src[i] == '"' && i + 1 < src.Length && src[i + 1] == '"') { i += 2; continue; }
                        if (src[i] == '"') break;
                        if (src[i] == '\n') sb.Append('\n');
                        i++;
                    }
                    continue;
                }
                if (c == '"')
                {
                    i++;
                    while (i < src.Length && src[i] != '"')
                    {
                        if (src[i] == '\\') i++;
                        i++;
                    }
                    continue;
                }
                if (c == '\'')
                {
                    i++;
                    while (i < src.Length && src[i] != '\'') { if (src[i] == '\\') i++; i++; }
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
