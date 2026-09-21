/* In the name of God, the Merciful, the Compassionate */

// Two rename casualties in markup, both of the same shape: the page kept naming something the
// rest of the app had renamed, and nothing failed - the link 404s and the readonly field prints
// a default nobody reads. A compiler cannot see either, because both are strings.
//
//   Board #40  - Pages/Remediation.razor's batch empty state linked href="/quickcheck".
//                No @page "/quickcheck" exists; the route has been "/audit" since 2026-05-18
//                (Data/RouteConstants.cs:41-42). The link is now @RouteConstants.QuickCheck,
//                which is the same constant NavMenu and CommandPalette use, so the route can
//                only move for all of them at once.
//   Triage C1  - Pages/Settings.razor printed "sqliteMaintenanceIntervalHours" and
//                "sqliteIntegrityCheckEveryNRuns". Data/Caching/SqliteMaintenanceService.cs
//                reads "liveQueries..." keys. Both sides default to 4 and 6, so the displayed
//                numbers were right by coincidence: set the real key and the page kept showing
//                the default.
//
// These read source text on purpose. A render test proves what one page did on one run; the
// scan proves no page anywhere carries the dead route.

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SQLTriage.Tests
{
    public class StaleMarkupReferenceTests
    {
        private static string RepoFile(params string[] parts) =>
            Path.Combine(RawPassedScan.RepoRoot().FullName, Path.Combine(parts));

        [Fact]
        public void NoMarkupLinksToTheDeadQuickcheckRoute()
        {
            var root = RawPassedScan.RepoRoot().FullName;
            var files = new[] { "Pages", "Components" }
                .Select(d => Path.Combine(root, d))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.GetFiles(d, "*.razor", SearchOption.AllDirectories))
                .ToList();

            Assert.True(files.Count > 0, "No .razor files were scanned - the guard would pass over nothing.");

            var offenders = files
                .Where(f => File.ReadAllText(f).Contains("\"/quickcheck\""))
                .Select(f => Path.GetRelativePath(root, f))
                .ToList();

            Assert.True(offenders.Count == 0,
                "These files still link to /quickcheck, which no @page declares - the link 404s: "
                + string.Join(", ", offenders));
        }

        [Fact]
        public void RemediationBatchEmptyState_LinksThroughTheRouteConstant()
        {
            var markup = File.ReadAllText(RepoFile("Pages", "Remediation.razor"));

            Assert.Contains("href=\"@RouteConstants.QuickCheck\"", markup);
        }

        [Fact]
        public void SettingsDisplaysTheMaintenanceKeysTheEngineActuallyReads()
        {
            var engine = File.ReadAllText(RepoFile("Data", "Caching", "SqliteMaintenanceService.cs"));
            var settings = File.ReadAllText(RepoFile("Pages", "Settings.razor"));

            // The keys the maintenance service reads, taken from the service rather than
            // restated here, so a key renamed on the engine side fails this instead of drifting.
            var engineKeys = Regex.Matches(engine, @"config\[""(?<k>[^""]+)""\]")
                                  .Select(m => m.Groups["k"].Value)
                                  .Where(k => k.StartsWith("liveQueries"))
                                  .Distinct()
                                  .ToList();

            Assert.Equal(2, engineKeys.Count);

            foreach (var key in engineKeys)
                Assert.True(settings.Contains("\"" + key + "\""),
                    $"Settings.razor does not read '{key}', so the value it shows for that field is "
                    + "the hard-coded default no matter what the operator configured.");

            Assert.DoesNotContain("\"sqliteMaintenanceIntervalHours\"", settings);
            Assert.DoesNotContain("\"sqliteIntegrityCheckEveryNRuns\"", settings);
        }
    }
}
