/* In the name of God, the Merciful, the Compassionate */

// platform-r1-10: the runtime legacy applier's ProtectedConfigFiles list (Data/AutoUpdateService.cs)
// and the managed updater's own copy (tools/SQLTriageUpdater/Program.cs) are cross-referenced in a
// comment as "matching" and had drifted by one — the updater protected config\.sqlite-cipher-key and
// the runtime list did not. The runtime list is the only one any install ever executes (the managed
// updater is not shipped — platform-r1-06), so a package carrying a fresh cipher key would let
// robocopy overwrite the client's and leave cached DBs unreadable. This is the tripwire that keeps
// the two lists identical in EITHER direction, so the next edit to one that forgets the other goes
// red here rather than on a client's machine.
//
// It reads both files AS TEXT and compares NORMALISED FULL RELATIVE PATHS - directory segment
// included, backslashes collapsed (the escaped-string "config\\x" and verbatim-string @"config\x"
// syntaxes both normalise to config\x), lowercased. It used to compare LEAF FILENAMES ONLY, on the
// reasoning that no two entries share a leaf so the prefix carried no information. That reasoning
// covered drift in the SET of entries and nothing else: it could not see a DIRECTORY divergence.
// Both lists are consumed as paths - the runtime applier's robocopy line and the updater's
// IsProtected both compare against a path relative to the extract root - so "config\.sqlite-cipher-key"
// in one list and ".sqlite-cipher-key" in the other are two different files, and leaf comparison
// called them identical. Full-path comparison is what makes a directory-only divergence go red, and
// Each_protected_entry_keeps_its_directory pins every entry's directory by name on top of that, so a
// move that both lists made together still fails.
//
// The extractor USED to require a backslash immediately before the leaf, which silently made any
// entry with no directory segment invisible to it. That was not academic: .governance-hmac-key
// lives at the app ROOT (GovernanceHistoryService writes it to BaseDirectory), so adding it to
// both lists would have left parity passing VACUOUSLY over it - the tripwire would not have
// existed for the one entry whose loss makes a client's whole governance chain read as tampered.
// The directory segment is therefore optional here, and Both_lists_protect_the_governance_hmac_key
// pins that entry by name so the extractor cannot regress back to blindness without going red.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SQLTriage.Tests;

public class ProtectedConfigParityTests
{
    private static string RepoRoot() => FrkContractTests.RepoRoot();

    /// <summary>
    /// Normalised full relative paths inside the named string-array literal in a source file.
    /// Normalisation is: escaped "\\" and forward slashes collapsed to a single backslash, then
    /// lowercased, so the escaped and verbatim spellings of the same path compare equal.
    /// </summary>
    private static IReadOnlyList<string> ConfigPaths(string filePath, string arrayMarker)
    {
        var text = File.ReadAllText(filePath);
        var start = text.IndexOf(arrayMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, arrayMarker + " not found in " + filePath);
        var end = text.IndexOf("};", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of " + arrayMarker + " in " + filePath);
        var body = text.Substring(start, end - start);

        // Strip // comments first. The entry pattern below matches a whole quoted literal, so
        // without this any double-quoted word in an explanatory comment inside the array would be
        // read as a protected filename. (The old backslash-anchored pattern filtered comments by
        // accident; now that a directory segment is optional, the filtering has to be deliberate.)
        body = Regex.Replace(body, "//[^\r\n]*", string.Empty);

        // A complete quoted path literal, kept WHOLE - directory segment and all:
        //   "config\\name" (escaped)  -> config\name
        //   @"config\name" (verbatim) -> config\name
        //   ".governance-hmac-key"    -> .governance-hmac-key   (no directory segment)
        // The directory part is optional ON PURPOSE - see the header note - but when it is there
        // it is KEPT, which is what lets a directory-only divergence fail.
        var rx = new Regex("\"(?<path>[A-Za-z0-9._\\-\\\\/]+)\"");
        return rx.Matches(body)
            .Select(m => NormalisePath(m.Groups["path"].Value))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Collapse escaped/duplicated and forward slashes to one backslash, then lowercase.</summary>
    private static string NormalisePath(string raw) =>
        Regex.Replace(raw.Replace('/', '\\'), @"\\+", "\\").Trim('\\').ToLowerInvariant();

    private static IReadOnlyList<string> RuntimeList() =>
        ConfigPaths(Path.Combine(RepoRoot(), "Data", "AutoUpdateService.cs"), "ProtectedConfigFiles = new[]");

    private static IReadOnlyList<string> UpdaterList() =>
        ConfigPaths(Path.Combine(RepoRoot(), "tools", "SQLTriageUpdater", "Program.cs"), "ProtectedConfig =");

    [Fact]
    public void The_readers_are_not_vacuous()
    {
        Assert.True(RuntimeList().Count >= 10, "runtime ProtectedConfigFiles reader found almost nothing");
        Assert.True(UpdaterList().Count >= 10, "updater ProtectedConfig reader found almost nothing");
    }

    [Fact]
    public void The_reader_sees_an_entry_with_no_directory_segment()
    {
        // Guards the extractor itself, not the lists. If ConfigPaths regresses to requiring a
        // backslash before the leaf, every root-level entry becomes invisible and the parity
        // assertions below start passing vacuously over it. Exercised against a literal in each
        // of the two syntaxes the real files use, so the guard cannot be satisfied by accident.
        var probe = Path.Combine(Path.GetTempPath(), "protcfg-probe-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(probe,
            "Marker = new[]\n{\n"
            + "    \"config\\\\nested-entry.json\",\n"   // escaped, WITH a directory segment
            + "    \".root-entry-escaped\",\n"           // escaped, WITHOUT one
            + "    @\".root-entry-verbatim\",\n"         // verbatim, WITHOUT one
            + "    // \"commented-out-entry.json\" must not be read as a protected file\n"
            + "};\n");
        try
        {
            var paths = ConfigPaths(probe, "Marker = new[]");
            // The directory segment survives normalisation - the escaped "\\" collapses to one "\".
            Assert.Contains(@"config\nested-entry.json", paths, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("nested-entry.json", paths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(".root-entry-escaped", paths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(".root-entry-verbatim", paths, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("commented-out-entry.json", paths, StringComparer.OrdinalIgnoreCase);
        }
        finally { File.Delete(probe); }
    }

    [Fact]
    public void Both_lists_protect_the_governance_hmac_key()
    {
        // GovernanceHistoryService writes .governance-hmac-key to the app ROOT. Every row already
        // in the client's governance-history.db was signed with it, so an update package that
        // overwrote it would not merely lose a secret - it would make the entire existing history
        // verify as tampered. Named explicitly rather than left to the parity check alone: parity
        // only proves the two lists AGREE, and they agreed while both omitted this key.
        Assert.Contains(".governance-hmac-key", RuntimeList(), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(".governance-hmac-key", UpdaterList(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runtime_protects_the_sqlite_cipher_key()
    {
        // The exact platform-r1-10 fix: the runtime list must protect the cipher key the updater
        // already did. RED before the fix (the runtime list omitted it). Named by its FULL path:
        // SqliteCipherHelper writes it under config\, and a bare leaf here would pass over an
        // entry that had drifted to the app root.
        Assert.Contains(@"config\.sqlite-cipher-key", RuntimeList(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every protected entry, with the directory it must live in. One case per entry: the parity
    /// test proves the two lists AGREE, this proves they agree on the RIGHT path. A single
    /// [Theory] case going red names the entry that moved.
    /// </summary>
    public static IEnumerable<object[]> ExpectedProtectedPaths() => new[]
    {
        new object[] { @"config\server-connections.json" },
        new object[] { @"config\alert-definitions.json" },
        new object[] { @"config\alert-thresholds.json" },
        new object[] { @"config\notification-channels.json" },
        new object[] { @"config\scheduled-tasks.json" },
        new object[] { @"config\user-settings.json" },
        new object[] { @"config\appsettings.json" },
        new object[] { @"config\dashboard-config.json" },
        new object[] { @"config\.sqlite-cipher-key" },
        // ROOT, not config\ - GovernanceHistoryService writes it to BaseDirectory. Pinning the
        // absence of a directory segment is as load-bearing as pinning a present one: an entry
        // "helpfully" moved under config\ would protect a path the app never writes.
        new object[] { @".governance-hmac-key" },
    };

    [Theory]
    [MemberData(nameof(ExpectedProtectedPaths))]
    public void Each_protected_entry_keeps_its_directory(string expectedPath)
    {
        Assert.Contains(expectedPath, RuntimeList(), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(expectedPath, UpdaterList(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_expected_path_table_covers_every_entry_in_both_lists()
    {
        // Without this, an entry ADDED to both lists would be pinned by nothing: the theory above
        // only judges the paths it names. Keeps the table honest as the lists grow.
        var expected = ExpectedProtectedPaths()
            .Select(row => (string)row[0])
            .Select(NormalisePath)
            .ToList();

        var missing = RuntimeList().Concat(UpdaterList())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Except(expected, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(missing.Count == 0,
            "these protected entries have no directory-pinning case in ExpectedProtectedPaths: "
            + string.Join(", ", missing) + ". Add each one there when you add it to the lists.");
    }

    [Fact]
    public void Runtime_and_updater_protected_lists_are_identical()
    {
        var runtime = RuntimeList();
        var updater = UpdaterList();

        var onlyUpdater = updater.Except(runtime, StringComparer.OrdinalIgnoreCase).ToList();
        var onlyRuntime = runtime.Except(updater, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(onlyUpdater.Count == 0,
            "tools/SQLTriageUpdater/Program.cs protects config files the runtime "
            + "Data/AutoUpdateService.cs list does not: " + string.Join(", ", onlyUpdater)
            + ". The runtime list is the only one any install executes, so anything here is "
            + "unprotected on a real update (platform-r1-10).");
        Assert.True(onlyRuntime.Count == 0,
            "Data/AutoUpdateService.cs protects config files the updater's list does not: "
            + string.Join(", ", onlyRuntime) + ". Keep the two lists identical.");
    }
}
