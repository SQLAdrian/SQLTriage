/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Guards against raw C0 control bytes (anything below 0x20 except TAB/LF/CR) landing in
    /// shipped or test source. Found 2026-09-08 (lane hygiene-tail): 25 such bytes across four
    /// files — 0x1F/0x1E used as field/record separators in a diff hash, and 0x01/0x00 used as
    /// composite-key/test-data separators. No runtime defect: the damage is that git/ripgrep
    /// treat the carrying files as binary, hiding their content from every future grep-based
    /// census. Twenty-four of the twenty-five bytes were rewritten to \u escapes in this lane;
    /// this test pins that they stay rewritten, and holds the ONE deliberate exception to an
    /// exact, non-widening count.
    ///
    /// <see cref="RawPassedScan.SourceFiles"/> is NOT reused here: that scan is scoped to
    /// shipped source (Data/Pages/Cli/Components, .cs/.razor only) for a different guard, and
    /// two of the four sites this test must catch live under Tests/. This walks the whole repo
    /// itself, restricted to the same three extensions the brief named.
    /// </summary>
    public class RawControlByteGuardTests
    {
        private static readonly string[] Extensions = { ".cs", ".razor", ".cshtml" };

        private static readonly string[] ExcludedSegments =
            { "bin", "obj", ".git", "node_modules", "wwwroot" + Path.DirectorySeparatorChar + "_framework" };

        /// <summary>Allow-listed exception: relative path, byte value, and the EXACT count that
        /// must hold. A change in either direction — the count rising (a new raw byte crept in)
        /// or falling to zero (someone "fixed" it without updating this list) — fails the test,
        /// so the allow-list cannot silently widen.</summary>
        private sealed record AllowEntry(string RelativePath, byte Value, int ExpectedCount);

        private static readonly AllowEntry[] Allowed =
        {
            // Deliberate test data (UtilityTests.cs:92): asserts that GetServerList strips a
            // literal null byte out of a server name. Ruled NOT-DEFECT, lane hygiene-tail 09-08.
            new("Tests" + Path.DirectorySeparatorChar + "SQLTriage.Tests" + Path.DirectorySeparatorChar + "UtilityTests.cs", 0x00, 1),
        };

        internal sealed record Offender(string RelativePath, byte Value, int Line);

        private static DirectoryInfo RepoRoot() => RawPassedScan.RepoRoot();

        private static bool IsExcluded(string fullPath)
        {
            var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var seg in ExcludedSegments)
            {
                if (seg.Contains(Path.DirectorySeparatorChar))
                {
                    if (fullPath.Replace('/', Path.DirectorySeparatorChar).Contains(seg, StringComparison.OrdinalIgnoreCase))
                        return true;
                    continue;
                }
                if (parts.Any(p => string.Equals(p, seg, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        internal static List<string> Walk(DirectoryInfo root, string[] extensions)
        {
            return Directory.EnumerateFiles(root.FullName, "*.*", SearchOption.AllDirectories)
                .Where(f => extensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .Where(f => !IsExcluded(f))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }

        internal static List<Offender> Scan(DirectoryInfo root, string[] extensions)
        {
            var offenders = new List<Offender>();
            foreach (var file in Walk(root, extensions))
            {
                var bytes = File.ReadAllBytes(file);
                var rel = Path.GetRelativePath(root.FullName, file);
                int line = 1;
                for (int i = 0; i < bytes.Length; i++)
                {
                    var b = bytes[i];
                    if (b == (byte)'\n') { line++; continue; }
                    if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D)
                        offenders.Add(new Offender(rel, b, line));
                }
            }
            return offenders;
        }

        [Fact]
        public void No_raw_control_bytes_outside_the_one_allowed_exception()
        {
            var root = RepoRoot();
            var offenders = Scan(root, Extensions);

            // Group by (path, byte) so the allow-list check is exact-count, not just presence.
            var byKey = offenders
                .GroupBy(o => (o.RelativePath, o.Value))
                .ToDictionary(g => g.Key, g => g.ToList());

            var unexpected = new List<Offender>();
            var seenAllowed = new HashSet<(string, byte)>();

            foreach (var group in byKey)
            {
                var allow = Allowed.FirstOrDefault(a =>
                    string.Equals(a.RelativePath, group.Key.RelativePath, StringComparison.OrdinalIgnoreCase)
                    && a.Value == group.Key.Value);

                if (allow == null)
                {
                    unexpected.AddRange(group.Value);
                    continue;
                }

                seenAllowed.Add((allow.RelativePath, allow.Value));
                if (group.Value.Count != allow.ExpectedCount)
                {
                    unexpected.Add(new Offender(
                        group.Key.RelativePath, group.Key.Value, -1)); // -1 marks a COUNT mismatch, not a new site
                }
            }

            // An allow-listed entry that no longer appears at all is also a count mismatch (0 != expected).
            foreach (var allow in Allowed)
            {
                if (!seenAllowed.Contains((allow.RelativePath, allow.Value)))
                    unexpected.Add(new Offender(allow.RelativePath, allow.Value, -1));
            }

            if (unexpected.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"{unexpected.Count} unexpected raw control byte(s) found (allow-list mismatch or new site):");
                foreach (var o in unexpected.OrderBy(o => o.RelativePath, StringComparer.Ordinal))
                {
                    var where = o.Line < 0 ? "count-mismatch" : $"line {o.Line}";
                    sb.AppendLine($"  {o.RelativePath}: 0x{o.Value:X2} ({where})");
                }
                Assert.Fail(sb.ToString());
            }
        }

        [Fact]
        public void Wider_extension_set_has_no_raw_control_bytes_either()
        {
            // Item 1 step 3, WIDENED on purpose and gate-corrected 2026-09-08: the wider set below was
            // clean at base 0e93d36 (0 hits over 489 tracked files, re-proved by the cold gate), so this
            // Fact ENFORCES it. Its first name and comment said "reported, not enforced" while the body
            // asserted from the start (gate finding MEDIUM-2). A raw control byte in any of these files
            // fails the suite naming file, byte and line - the eaten-escape / journal-damage class. A
            // legitimate control byte needs an explicit, count-pinned allow-list entry like the
            // UtilityTests one above, never a silent bump. The walk is filesystem-based: untracked files
            // under the worktree are in scope unless they sit under an excluded segment.
            var root = RepoRoot();
            var wideExtensions = new[] { ".ps1", ".psm1", ".sql", ".js", ".css", ".json", ".yml", ".md" };
            var offenders = Scan(root, wideExtensions);
            Assert.Empty(offenders); // PROVED 2026-09-08: zero hits across this wider set on this box.
        }
    }
}
