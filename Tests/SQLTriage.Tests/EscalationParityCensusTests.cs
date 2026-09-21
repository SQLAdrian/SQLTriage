/* In the name of God, the Merciful, the Compassionate */

// -- special-alerts-escalation-parity (2026-09-11): the census, and why it is shaped this way -----
//
// THE INVARIANT: every evaluation path that can fire an alert must evaluate escalation for that
// alert. Until this lane, one of the two firing paths did. The behavioural tests in
// SpecialAlertEscalationTests pin TODAY'S two paths; this file is what stops a THIRD path shipping
// without escalation, because a behavioural test can only ever cover a path someone remembered to
// write a test for.
//
// ⚠ THE TRAP THIS FILE DELIBERATELY AVOIDS. There is an existing census in this suite -
// SpecialAlertFireIsVisibleTests.EverySiteThatSetsANewActiveState_alsoEmitsTheFireLine - built on an
// `_activeStates[...] =` ASSIGNMENT regex. For its own question (does every NEW state log a fire
// line?) that is the right enumerator, because the fire line is written on first fire only. It is
// the WRONG enumerator for escalation, which is decided on RE-FIRE, in a branch that assigns
// nothing. Copying that shape here would have produced a green test that never looks at the branch
// that decides. So this census enumerates METHODS, not lines: whichever branch inside a firing
// method takes the decision, the method must contain the call.
//
// AND IT IS DERIVED FROM THE CODE, NOT FROM A COUNT. Nothing here pins "there are N sites". A count
// measures the pattern; it goes green the day the pattern stops matching. Every assertion below
// names the method it is unhappy about, and the non-vacuity guard fails loudly if the parser stops
// recognising the two paths we know exist.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SQLTriage.Tests
{
    public class EscalationParityCensusTests
    {
        /// <summary>The one shared callee every firing path must reach.</summary>
        private const string EscalationCallee = "ApplyEscalationForFiringCycle(";

        // -- 1. THE CENSUS ------------------------------------------------------------

        /// <summary>
        /// THE PIN THAT WOULD HAVE CAUGHT THE ORIGINAL DEFECT. Every method under <c>Data/</c> that
        /// can fire an alert must call <see cref="EscalationCallee"/>.
        ///
        /// <para><b>How a firing path is recognised, from the code.</b> Two mechanical signals,
        /// unioned, both read off the source with comments stripped so no doc-comment prose can
        /// create or hide a match:</para>
        /// <list type="bullet">
        /// <item><c>LogAlertFired(</c> - the one place the "Alert fired" line is written, by that
        ///   helper's own doc contract.</item>
        /// <item><c>_activeStates[...] =</c> - the creation of an active alert state.</item>
        /// </list>
        ///
        /// <para><b>What this census does NOT cover, stated rather than left to be discovered.</b>
        /// A firing path that neither logs a fire line nor creates an <c>_activeStates</c> entry is
        /// invisible to both signals. Exactly one such thing exists in this repository today -
        /// <c>AlertingService.EvaluateAlerts</c>, a separate legacy engine with its own notification
        /// queue - and it is covered instead by
        /// <see cref="LegacyEvaluateAlerts_hasNoCallerAnywhere_whichIsWhyThisCensusNeedNotCoverIt"/>,
        /// which pins the reason it is out of scope rather than assuming it. If that test goes red,
        /// this one has a hole and the legacy path must be brought inside.</para>
        /// </summary>
        [Fact]
        public void EveryFiringPathEvaluatesEscalation()
        {
            var firing = new List<SourceMethod>();

            foreach (var file in DataSourceFiles())
                foreach (var m in MethodsIn(file))
                    if (CanFireAnAlert(m))
                        firing.Add(m);

            // NON-VACUITY, and it is not a count pinned from a regex: these two methods exist and
            // this parser must be able to see them. If the extractor drifts, this fails HERE with a
            // list of what it did find, rather than passing on an empty census.
            var names = firing.Select(m => m.Name).ToList();
            Assert.True(
                names.Contains("EvaluateSpecialAlertAsync") && names.Contains("ApplyObservedValueAsync"),
                "the census did not recognise the two known firing paths (EvaluateSpecialAlertAsync, "
                + "ApplyObservedValueAsync). It found: "
                + (names.Count == 0 ? "nothing at all" : string.Join(", ", names))
                + ". That is a parser failure, and a parser failure here is a SILENT hole, not a pass.");

            var missing = firing
                .Where(m => !m.Code.Contains(EscalationCallee, StringComparison.Ordinal))
                .Select(m => $"{m.File}:{m.Line} {m.Name}")
                .ToList();

            Assert.True(missing.Count == 0,
                "these methods can fire an alert and never evaluate escalation for it, which is the "
                + "exact defect special-alerts-escalation-parity closed:\n  "
                + string.Join("\n  ", missing)
                + "\n\nThe fix is to call " + EscalationCallee + "alert, stateKey, serverName) from "
                + "the breaching arm, OUTSIDE the new-vs-re-fire branch - not to add this method to "
                + "an exclusion list.");
        }

        // -- 2. THE ONE THING THE CENSUS DELIBERATELY DOES NOT COVER -------------------

        /// <summary>
        /// <c>AlertingService.EvaluateAlerts</c> evaluates thresholds and hands notifications to the
        /// real channel dispatcher, and it has no escalation of any kind. It is out of this census's
        /// scope for one reason only: <b>nothing calls it</b>. That reason is measured here, on every
        /// run, rather than believed - which is the difference between an exclusion and a blind spot.
        ///
        /// <para>If this test goes red, a caller appeared and the legacy engine is live again. It
        /// must then either gain escalation or be brought under the census above; do not relax this
        /// test to make the red go away.</para>
        ///
        /// <para>The single permitted call site is <c>AlertingService</c>'s own one-argument overload
        /// delegating to the two-argument one. That is not a caller, it is the same dead method
        /// wearing two hats, and it is pinned at exactly one so a real caller added inside that file
        /// still fails.</para>
        ///
        /// <para><b>Scope: shipped product source only, and deliberately.</b> The question is whether
        /// the PRODUCT calls this method, because that is what decides whether it is a live firing
        /// path; a test exercising a dead method does not revive it. Scoping this way also removes a
        /// false positive this test caught on itself - the message string a few lines below contains
        /// the words "call to EvaluateAlerts (", which any regex honest enough to find a real call
        /// will also find in a string literal describing one.</para>
        /// </summary>
        [Fact]
        public void LegacyEvaluateAlerts_hasNoCallerAnywhere_whichIsWhyThisCensusNeedNotCoverIt()
        {
            var call = new Regex(@"\bEvaluateAlerts\s*\(", RegexOptions.Compiled);
            var declaration = new Regex(@"\bList<AlertEvaluationResult>\s+EvaluateAlerts\s*\(", RegexOptions.Compiled);

            var external = new List<string>();
            var selfDelegations = 0;

            foreach (var file in ProductSourceFiles())
            {
                var rel = Relative(file);
                var lines = StripComments(File.ReadAllText(file.FullName)).Replace("\r\n", "\n").Split('\n');

                for (var i = 0; i < lines.Length; i++)
                {
                    if (!call.IsMatch(lines[i])) continue;
                    if (declaration.IsMatch(lines[i])) continue;      // the declaration itself

                    if (rel.EndsWith("Data/AlertingService.cs", StringComparison.OrdinalIgnoreCase))
                        selfDelegations++;
                    else
                        external.Add($"{rel}:{i + 1}  {lines[i].Trim()}");
                }
            }

            Assert.True(external.Count == 0,
                "AlertingService.EvaluateAlerts now has a caller, so it is a LIVE alert-firing path "
                + "with no escalation and the escalation census above no longer covers the product:\n  "
                + string.Join("\n  ", external));

            Assert.True(selfDelegations == 1,
                $"expected exactly one in-file call to EvaluateAlerts (the one-argument overload "
                + $"delegating to the two-argument one) and found {selfDelegations}. Either the "
                + "overload pair changed shape, or something inside AlertingService started calling it.");
        }

        // -- source extraction ---------------------------------------------------------

        private sealed record SourceMethod(string File, string Name, int Line, string Code);

        private static readonly Regex ActiveStateAssignment =
            new(@"_activeStates\[[^\]]+\]\s*=(?!=)", RegexOptions.Compiled);

        /// <summary>
        /// A method can fire an alert if its CODE (comments already stripped) writes the fire line or
        /// creates an active alert state. The declaration line is excluded so
        /// <c>LogAlertFired</c>'s own declaration is not mistaken for a firing path.
        /// </summary>
        private static bool CanFireAnAlert(SourceMethod m)
        {
            var body = m.Code.Contains('\n')
                ? m.Code.Substring(m.Code.IndexOf('\n') + 1)
                : string.Empty;
            return body.Contains("LogAlertFired(", StringComparison.Ordinal)
                || ActiveStateAssignment.IsMatch(body);
        }

        /// <summary>
        /// Splits a source file into member regions. A region runs from one member declaration to the
        /// next, which is enough for this census and needs no brace matching - and brace matching is
        /// what would break here, because this codebase's log templates and SQL literals are full of
        /// braces inside strings.
        ///
        /// <para>A declaration line is one that begins with an access modifier, contains a '(', and
        /// whose '(' comes before any '=' - which admits methods, constructors and expression-bodied
        /// members, and excludes fields initialised with <c>= new(...)</c> and expression-bodied
        /// properties, which carry no '(' before the '='.</para>
        /// </summary>
        private static List<SourceMethod> MethodsIn(FileInfo file)
        {
            var lines = StripComments(File.ReadAllText(file.FullName)).Replace("\r\n", "\n").Split('\n');
            var decls = new List<(int Index, string Name)>();

            for (var i = 0; i < lines.Length; i++)
            {
                var name = DeclaredMemberName(lines[i]);
                if (name != null) decls.Add((i, name));
            }

            var rel = Relative(file);
            var result = new List<SourceMethod>();
            for (var d = 0; d < decls.Count; d++)
            {
                var start = decls[d].Index;
                var end = d + 1 < decls.Count ? decls[d + 1].Index : lines.Length;
                result.Add(new SourceMethod(rel, decls[d].Name, start + 1,
                    string.Join("\n", lines.Skip(start).Take(end - start))));
            }
            return result;
        }

        private static readonly Regex Declaration = new(
            @"^\s*(?:public|private|protected|internal)\s+[^;=]*?\b([A-Za-z_]\w*)\s*(?:<[^>()]*>)?\s*\(",
            RegexOptions.Compiled);

        private static string? DeclaredMemberName(string line)
        {
            var m = Declaration.Match(line);
            if (!m.Success) return null;

            // A field initialiser (`private readonly X _x = new(...)`) puts '=' before '('.
            var paren = line.IndexOf('(');
            var equals = line.IndexOf('=');
            if (equals >= 0 && equals < paren) return null;

            return m.Groups[1].Value;
        }

        /// <summary>
        /// Removes line comments and block comments so the census reads CODE. Prose about a pattern
        /// must never be able to satisfy - or to hide - a census of that pattern; this file's own
        /// header would otherwise match half of it.
        /// </summary>
        private static string StripComments(string src)
        {
            var withoutBlocks = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
            var kept = withoutBlocks
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : l);
            return string.Join("\n", kept);
        }

        private static IEnumerable<FileInfo> DataSourceFiles() =>
            SourceFilesUnder(new DirectoryInfo(Path.Combine(RepoRoot().FullName, "Data")), "*.cs");

        /// <summary>Every shipped source file: the whole repository except the test tree.</summary>
        private static IEnumerable<FileInfo> ProductSourceFiles() =>
            SourceFilesUnder(RepoRoot(), "*.cs")
                .Concat(SourceFilesUnder(RepoRoot(), "*.razor"))
                .Where(f => !f.FullName.Replace('\\', '/')
                    .Contains("/Tests/", StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<FileInfo> SourceFilesUnder(DirectoryInfo root, string pattern) =>
            root.EnumerateFiles(pattern, SearchOption.AllDirectories)
                .Where(f => !f.FullName.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.FullName.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase));

        private static DirectoryInfo RepoRoot() => RawPassedScan.RepoRoot();

        private static string Relative(FileInfo f) =>
            f.FullName.Substring(RepoRoot().FullName.Length).TrimStart('\\', '/').Replace('\\', '/');
    }
}
