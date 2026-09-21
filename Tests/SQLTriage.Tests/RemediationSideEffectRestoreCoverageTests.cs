/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationSideEffectRestoreCoverageTests — the CALL-SITE half of Phase-2 item 2.3.
 * Written in the fix round after the gate proved blocker 2 live, 2026-09-01.
 *
 * WHAT THE GATE PROVED, on `.\old2017`. Fixture: 'show advanced options' set to 0 from outside the
 * app. An apply whose target statement FAILED at the server returned CouldNotRun, and an independent
 * read afterwards said 'show advanced options' = 1. No restore, no note, and PreChangeValue empty.
 * The rendered batch is
 *
 *     EXEC sp_configure 'show advanced options', 1; RECONFIGURE; EXEC sp_configure '<target>', N; RECONFIGURE;
 *
 * and it is NOT in a transaction, so the prelude commits even when the target statement raises. Item
 * 2.3 restored that option on the paths the lane happened to look at - the verified return, the
 * verify-read failure, the three no-rollback returns and the confirmed-rollback return - and missed
 * the three where an exception or a guard cut the method short. A restore that covers most exits is
 * not a control; it is a habit.
 *
 * ⚠ WHY THIS TEST READS SOURCE INSTEAD OF DRIVING BEHAVIOUR. The missing restores are on paths that
 * only a real server can produce: a statement the ENGINE rejects, a rollback the ENGINE refuses.
 * There is no seam to inject a failing connection into DbatoolsRemediationExecutor, and inventing
 * one to test this would be testing the seam. So the offline instrument measures the INVARIANT -
 * between the apply boundary and every subsequent return, a restore call must appear - and the
 * BEHAVIOUR is proved live in RemediationPhase2LiveSmokeTests
 * (TheRenderedSideEffect_IsPutBack_WhenTheApplyItselfFAILS), which replicates the gate's fixture
 * exactly and reads the option back on a second connection.
 *
 * WHAT THIS PROVES: no return after the apply skips the restore.
 * WHAT IT DOES NOT PROVE: that the restore succeeds, or that its note is right. That is the live
 * test's job, and the offline scan would happily pass over a restore that threw everything away.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class RemediationSideEffectRestoreCoverageTests
    {
        private readonly ITestOutputHelper _out;
        public RemediationSideEffectRestoreCoverageTests(ITestOutputHelper output) => _out = output;

        private const string BoundaryMarker = "── APPLY BOUNDARY ──";
        private const string RestoreCall = "RestoreSideEffectOptionsAsync(";
        private const string MethodSignature = "private async Task<RemediationExecution> ExecuteConfigurationAsync(";

        private static string ExecutorSource() =>
            File.ReadAllText(Path.Combine(RawPassedScan.RepoRoot().FullName,
                "Data", "Services", "Remediation", "DbatoolsRemediationExecutor.cs"));

        /// <summary>
        /// The body of ExecuteConfigurationAsync, by depth-matched braces from its signature. Reading
        /// the whole file would count returns from twenty other methods; reading by line offsets
        /// would rot the first time somebody edits above it.
        /// </summary>
        private static string ConfigurationApplyBody(string source)
        {
            int sig = source.IndexOf(MethodSignature, StringComparison.Ordinal);
            Assert.True(sig >= 0,
                "ExecuteConfigurationAsync was not found by signature, so this guard cannot read the "
                + "method it exists to check. It must not pass. Signature sought: " + MethodSignature);

            int open = source.IndexOf('{', sig);
            Assert.True(open > 0, "ExecuteConfigurationAsync has no body.");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(open, i - open + 1);
                }
            }
            throw new InvalidOperationException("ExecuteConfigurationAsync's braces never balanced.");
        }

        /// <summary>Comments out, so a `return` written in prose cannot be mistaken for one in code.</summary>
        private static string WithoutComments(string code)
        {
            code = Regex.Replace(code, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(code, @"//[^\n]*", " ");
        }

        [Fact]
        public void EveryReturnAfterTheApplyRestoresTheRendersSideEffectsFirst()
        {
            var body = ConfigurationApplyBody(ExecutorSource());

            int boundary = body.IndexOf(BoundaryMarker, StringComparison.Ordinal);
            Assert.True(boundary >= 0,
                "The apply boundary marker is gone from ExecuteConfigurationAsync, so this guard "
                + "cannot tell which returns are after the apply. Restore the marker rather than "
                + "deleting the guard. Marker: " + BoundaryMarker);

            var after = WithoutComments(body.Substring(boundary));

            // Every restore call and every return statement, in source order. The invariant the fix
            // round established is that they strictly alternate RESTORE, RETURN: each exit puts the
            // render's side effects back before it leaves.
            var events = new List<(int Index, string Kind)>();
            foreach (Match m in Regex.Matches(after, Regex.Escape(RestoreCall)))
                events.Add((m.Index, "RESTORE"));
            foreach (Match m in Regex.Matches(after, @"\breturn\b"))
                events.Add((m.Index, "RETURN"));
            events.Sort((a, b) => a.Index.CompareTo(b.Index));

            _out.WriteLine("after the apply boundary: " + string.Join(" -> ", events.Select(e => e.Kind)));

            var offences = new List<string>();
            string? previous = null;
            foreach (var e in events)
            {
                if (e.Kind == "RETURN" && previous != "RESTORE")
                    offences.Add("a return at offset " + e.Index + " with no RestoreSideEffectOptionsAsync "
                               + "before it: " + Excerpt(after, e.Index));
                previous = e.Kind;
            }

            Assert.True(offences.Count == 0,
                "ExecuteConfigurationAsync returns after applying without putting the rendered batch's "
                + "side effects back. The rendered prelude ('show advanced options', 1) commits even "
                + "when the target statement fails, so an unrestored exit leaves a second server "
                + "setting changed by a fix that may not have landed - the exact defect the fix-round "
                + "gate proved live.\n" + string.Join("\n", offences));

            // The instrument must have seen something. A body that stopped returning at all, or a
            // marker that landed at the very end, would satisfy every assertion above.
            Assert.True(events.Count(e => e.Kind == "RETURN") >= 4,
                "fewer than four returns were found after the apply boundary, which does not match "
                + "this method. The scan is probably reading the wrong region.");
            Assert.True(events.Count(e => e.Kind == "RESTORE") >= 4,
                "fewer than four restore calls were found after the apply boundary.");
        }

        [Fact]
        public void TheScannerItselfCatchesAPlantedUnrestoredReturn()
        {
            // A guard nobody has watched fail is a guard nobody has tested. The same rule, applied
            // to a body with the defect the gate found: an apply catch that returns without restoring.
            const string planted = @"{
                " + BoundaryMarker + @"
                try { await ExecuteNonQueryAsync(connString, applySql, ct); }
                catch (SqlException ex) { return PermsAwareFailure(ex, ""Apply failed""); }
                var (note, _) = await " + RestoreCall + @"connString, applySql, name, before, ct);
                return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerified };
            }";

            var after = WithoutComments(planted.Substring(planted.IndexOf(BoundaryMarker, StringComparison.Ordinal)));
            var events = new List<(int Index, string Kind)>();
            foreach (Match m in Regex.Matches(after, Regex.Escape(RestoreCall))) events.Add((m.Index, "RESTORE"));
            foreach (Match m in Regex.Matches(after, @"\breturn\b")) events.Add((m.Index, "RETURN"));
            events.Sort((a, b) => a.Index.CompareTo(b.Index));

            string? previous = null;
            int offences = 0;
            foreach (var e in events)
            {
                if (e.Kind == "RETURN" && previous != "RESTORE") offences++;
                previous = e.Kind;
            }

            _out.WriteLine("planted body events: " + string.Join(" -> ", events.Select(e => e.Kind)));
            Assert.Equal(1, offences);
        }

        private static string Excerpt(string text, int index)
        {
            int start = Math.Max(0, index - 40);
            int length = Math.Min(120, text.Length - start);
            return Regex.Replace(text.Substring(start, length), @"\s+", " ").Trim();
        }
    }
}
