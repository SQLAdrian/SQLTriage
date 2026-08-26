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
// It reads both files AS TEXT and compares the leaf filenames (after config\). Comparing leaves
// sidesteps the escaped-string ("config\\x") vs verbatim-string (@"config\x") syntax difference
// between the two files, and every entry in both lists is under config\, so the prefix carries no
// information the leaf does not.

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

    /// <summary>Leaf filenames inside the named string-array literal in a source file.</summary>
    private static IReadOnlyList<string> ConfigLeaves(string filePath, string arrayMarker)
    {
        var text = File.ReadAllText(filePath);
        var start = text.IndexOf(arrayMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, arrayMarker + " not found in " + filePath);
        var end = text.IndexOf("};", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of " + arrayMarker + " in " + filePath);
        var body = text.Substring(start, end - start);

        // The FINAL backslash before the leaf, then the leaf, then the closing quote. Matches both
        // "config\\name" (escaped: two backslashes, the second immediately precedes the leaf) and
        // @"config\name" (verbatim: one backslash) because only the last backslash matters.
        var rx = new Regex("\\\\(?<leaf>[A-Za-z0-9._-]+)\"");
        return rx.Matches(body)
            .Select(m => m.Groups["leaf"].Value)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> RuntimeList() =>
        ConfigLeaves(Path.Combine(RepoRoot(), "Data", "AutoUpdateService.cs"), "ProtectedConfigFiles = new[]");

    private static IReadOnlyList<string> UpdaterList() =>
        ConfigLeaves(Path.Combine(RepoRoot(), "tools", "SQLTriageUpdater", "Program.cs"), "ProtectedConfig =");

    [Fact]
    public void The_readers_are_not_vacuous()
    {
        Assert.True(RuntimeList().Count >= 8, "runtime ProtectedConfigFiles reader found almost nothing");
        Assert.True(UpdaterList().Count >= 8, "updater ProtectedConfig reader found almost nothing");
    }

    [Fact]
    public void Runtime_protects_the_sqlite_cipher_key()
    {
        // The exact platform-r1-10 fix: the runtime list must protect the cipher key the updater
        // already did. RED before the fix (the runtime list omitted it).
        Assert.Contains(".sqlite-cipher-key", RuntimeList(), StringComparer.OrdinalIgnoreCase);
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
