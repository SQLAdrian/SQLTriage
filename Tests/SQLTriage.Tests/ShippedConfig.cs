/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLTriage.Data.Services;

namespace SQLTriage.Tests
{
    /// <summary>
    /// THE INVARIANT THIS EXISTS TO HOLD: <b>a test that reads a config file THIS PRODUCT SHIPS must
    /// resolve it the way the shipped payload lays it out, and must fail loudly — naming every folder
    /// it probed — when it finds none.</b>
    ///
    /// <para>WHY IT EXISTS (measured 2026-09-11, lane two-real-failures). Commit <c>3ba2a2b</c>
    /// (2026-09-10, lane customer-update-path) moved the operator-editable configs out of the payload's
    /// <c>config\</c> and into <c>config.default\</c>, so that no update route could write over an
    /// operator's tuning. The runtime <c>config\</c> folder is now created by
    /// <see cref="ConfigDefaultsSeeder"/> from <c>Program.Main</c> — which no test process ever runs.
    /// Seven test helpers had each hand-copied the same two-folder probe
    /// (<c>new[] { "config", "Config" }</c>) and every one of them went blind at that commit.
    /// <c>ServiceCatalogHonestyTests.cs:49</c> was fixed in that lane and the other six were not:
    /// a SET completed as an INSTANCE, which is the defect class CLAUDE.md's generation discipline
    /// names first. This is the shared thing all of them now call.</para>
    ///
    /// <para><b>THE PAYLOAD FOLDER IS PROBED FIRST, DELIBERATELY.</b> These tests assert properties of
    /// the definition this product SHIPS, and the artefact that ships is <c>config.default\</c>. A
    /// <c>config\</c> copy beside a test assembly is either the operator's/seeded file or — far more
    /// likely in a build tree — a stale leftover from a build that predates the split, because
    /// <c>bin\</c> is never cleaned of content items that stopped shipping. Preferring <c>config\</c>
    /// is exactly what re-arms that false green: PROVED 2026-09-11 — dropping one file into the test
    /// output's <c>config\</c> turned sixteen genuinely-red <c>WaitSignalRatioAlertTests</c> green
    /// without touching a line of product or test code. The order here matches the precedent already
    /// ruled at <c>ServiceCatalogHonestyTests.cs:49</c>.</para>
    ///
    /// <para><b>IT NEVER RETURNS "NOT FOUND" AS A PASS.</b> <see cref="Path"/> throws. A test that
    /// wants the softer behaviour must ask for it explicitly with <see cref="TryPath"/> and say in its
    /// own words why a missing shipped file is not a failure there.</para>
    ///
    /// <para>The folder NAMES are taken from <see cref="ConfigDefaultsSeeder"/> rather than retyped, so
    /// a rename in the product moves this resolver with it and cannot leave the tests behind again.</para>
    /// </summary>
    internal static class ShippedConfig
    {
        /// <summary>The payload's shipped-defaults folder — the artefact that actually ships.</summary>
        internal static string DefaultsFolderName => ConfigDefaultsSeeder.DefaultsFolderName;

        /// <summary>
        /// The runtime folder, in probe order. Windows resolves both spellings to one directory; the
        /// second entry matters only where the case is meaningful, and mirrors the product's own
        /// two-spelling probe (<c>AlertDefinitionService.cs:88-90</c>).
        /// </summary>
        internal static IReadOnlyList<string> RuntimeFolderNames { get; } =
            new[] { "config", ConfigDefaultsSeeder.ConfigFolderName };

        /// <summary>Every folder this resolver probes, shipped-payload first. Named in the throw.</summary>
        internal static IReadOnlyList<string> ProbeOrder { get; } =
            new[] { ConfigDefaultsSeeder.DefaultsFolderName, "config", ConfigDefaultsSeeder.ConfigFolderName };

        /// <summary>
        /// The shipped config file of that name beside the test assembly, or null when this build
        /// placed none there. Callers that use this MUST say why an absent shipped file is not a
        /// failure for them — an absent file is otherwise a defect, not a skip.
        /// </summary>
        internal static string? TryPath(string fileName)
        {
            var baseDir = AppContext.BaseDirectory;
            foreach (var folder in ProbeOrder)
            {
                var candidate = System.IO.Path.Combine(baseDir, folder, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>
        /// The shipped config file of that name beside the test assembly. Throws, naming every folder
        /// probed and the base directory, when the build placed none there — because a test that
        /// cannot find the artefact it is asserting about has not passed, it has not run.
        /// </summary>
        internal static string Path(string fileName)
            => TryPath(fileName)
               ?? throw new FileNotFoundException(
                   $"The shipped '{fileName}' must be beside the test assembly. Probed "
                   + string.Join(", ", ProbeOrder.Select(f => f + "\\")) + " under " + AppContext.BaseDirectory
                   + ". Since 2026-09-10 the operator-editable configs ship in "
                   + DefaultsFolderName + "\\ and ConfigDefaultsSeeder creates config\\ at run time, "
                   + "which no test process does — so a build that ships the file will have it in "
                   + DefaultsFolderName + "\\.");

        /// <summary>The text of that shipped config file. Throws exactly as <see cref="Path"/> does.</summary>
        internal static string ReadAllText(string fileName) => File.ReadAllText(Path(fileName));

        /// <summary>
        /// Every file this build placed in the shipped-defaults folder, by file name. Empty when the
        /// build ships no such folder. This is the CONSUMER-DERIVED enumeration the completeness
        /// census runs over — it is read off the payload, never a hand-kept list.
        /// </summary>
        internal static IReadOnlyList<string> EnumerateShippedDefaults()
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, DefaultsFolderName);
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir).Select(System.IO.Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()!
                : Array.Empty<string>();
        }
    }
}
