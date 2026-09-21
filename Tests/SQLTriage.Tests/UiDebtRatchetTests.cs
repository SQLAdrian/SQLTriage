/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// A ratchet against inline-style debt in source .razor markup, born from the gemini-salvage
    /// lane (2026-08-31, plan §4 item 6 / §5). Gemini's diff extracted 15 files' <c>&lt;style&gt;</c>
    /// blocks into <c>wwwroot/css/*.css</c> (a genuine improvement, kept). The inline
    /// <c>style="..."</c> ATTRIBUTE count on individual elements is a separate, much larger debt
    /// (3787 occurrences across 111 files, frozen below) that the extractions did not touch and
    /// this test does not try to fix. It only stops the count from growing.
    ///
    /// <para><b>The ratchet contract.</b> <see cref="FrozenPerFile"/> and
    /// <see cref="StyleAttributeBaseline"/> were re-measured on 2026-08-31 on
    /// <c>lane/gemini-salvage</c>, AFTER the colour reverts ruled in the plan's "§5 ADDENDUM — Fable
    /// ruling on the verify round" were applied (every inline colour change in <c>*.razor</c> restored
    /// to <c>main</c>'s exact value) and after the <c>app.css</c> import-order cascade fix, then
    /// RE-FROZEN the same day after "§5 ADDENDUM 2" ruled five more files back to <c>main</c> in full
    /// (AgentJobTimeline, ComplianceBoard, DiagnosticsRoadmap, Governance, and VulnerabilityAssessment
    /// minus its <c>&lt;style&gt;</c>-block extraction). That re-freeze RAISED the numbers, by +62:
    /// the lane had been converting inline styles to CSS classes, and the conversions were reverted
    /// because one of them (AgentJobTimeline's Gantt bars) dropped working colour fallbacks onto
    /// tokens defined nowhere and rendered the bars transparent. The debt came back with the
    /// shipped appearance; the shipped appearance wins. This is the "raise it explicitly, with a
    /// reason" path in <see cref="No_source_razor_file_grew_its_inline_style_count"/>'s own failure
    /// message, exercised deliberately and once. Cross-check anchoring the freeze: every file in the
    /// map below now equals its count on <c>main</c> (<c>fc67a2c</c>) except
    /// <c>Pages/PerformanceReport.razor</c>, +1 for the kept try/catch error-alert fix. Numbers may
    /// only go DOWN. If a change removes inline <c>style=</c> attributes (an extraction, a cleanup),
    /// lower the affected map entries and the total in the SAME commit — a ratchet that lags its own
    /// improvement stops being evidence of anything. If a change legitimately needs to ADD one, that is
    /// a decision for a human to make explicitly by editing this map with a reason, not something a
    /// failing CI run should be worked around silently.</para>
    ///
    /// <para><b>Why per-file, not one total.</b> The first version of this test froze a single total.
    /// The 2026-08-31 verifier mutation-proved that it failed correctly but named the ten LARGEST
    /// files, none of which had changed — a developer tripping it in CI was pointed at the wrong
    /// code. The frozen map below lets the failure name exactly the file that grew, plus any file
    /// that acquired its first inline style.</para>
    ///
    /// <para><b>What this does and does not prove.</b> It is a lint on markup text, not a render —
    /// it cannot tell a necessary one-off inline style from a copy-pasted card border. It cannot
    /// distinguish a swap's target token from an unrelated new inline style landing in the same
    /// commit. It only pins a number, per file, and a direction.</para>
    /// </summary>
    public class UiDebtRatchetTests
    {
        /// <summary>Frozen 2026-08-31 — the sum of <see cref="FrozenPerFile"/>. Kept as its own
        /// constant so a half-finished edit (map lowered, total not, or the reverse) fails loudly in
        /// <see cref="Frozen_map_sum_equals_the_frozen_total"/> instead of silently slackening.</summary>
        // 3787 until 2026-09-09. -3: the operator-picker-mailchain lane converted three inline
        // styles on Pages/ServerConfiguration.razor to CSS classes and the map was left at 75 while the
        // page measured 72, so three new inline styles were free (gate finding F5, proved by mutation:
        // an added style="x" left this class 3/3 green). Lowered to the measured count in the fix round.
        private const int StyleAttributeBaseline = 3784;

        private static readonly Regex StyleAttr = new(@"style=[""']", RegexOptions.Compiled);
        private static readonly Regex StyleBlock = new(@"<style\b", RegexOptions.Compiled);

        /// <summary>
        /// Repo-relative path (forward slashes, ordinal comparison) to inline <c>style=</c>
        /// attribute count, measured 2026-08-31. Only files with a non-zero count appear; a file
        /// absent from this map is expected to carry ZERO inline styles, so its first one is
        /// reported as a new offender by name.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, int> FrozenPerFile =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["Components/Dashboards/LiveDashboard.razor"] = 1,
                ["Components/DevTools/PerfLoadWaterfall.razor"] = 7,
                ["Components/Layout/DashboardToolbar.razor"] = 1,
                ["Components/Layout/MainLayout.razor"] = 30,
                ["Components/Layout/NavMenu.razor"] = 36,
                ["Components/Layout/StatusBar.razor"] = 2,
                ["Components/Shared/ActivateFullAuditCard.razor"] = 4,
                ["Components/Shared/AddSectionModal.razor"] = 4,
                ["Components/Shared/AdminGuard.razor"] = 5,
                ["Components/Shared/BarGauge.razor"] = 2,
                ["Components/Shared/BaselineSeedingProgress.razor"] = 10,
                ["Components/Shared/BlockingTreeViewer.razor"] = 3,
                ["Components/Shared/BoundaryCanary.razor"] = 1,
                ["Components/Shared/CheckValidatorTable.razor"] = 49,
                ["Components/Shared/DashboardPanelWrapper.razor"] = 1,
                ["Components/Shared/DataGrid.razor"] = 8,
                ["Components/Shared/DeadlockViewer.razor"] = 55,
                ["Components/Shared/DeltaStatCard.razor"] = 3,
                ["Components/Shared/DynamicDashboard.razor"] = 26,
                ["Components/Shared/DynamicPanel.razor"] = 13,
                ["Components/Shared/FixPowerBadge.razor"] = 1,
                ["Components/Shared/GlobalServerSelector.razor"] = 6,
                ["Components/Shared/GovernanceTimeline.razor"] = 20,
                ["Components/Shared/HealthBadge.razor"] = 3,
                ["Components/Shared/LoadingIndicator.razor"] = 3,
                ["Components/Shared/OnboardingWizard.razor"] = 13,
                ["Components/Shared/PageLoadingSpinner.razor"] = 3,
                ["Components/Shared/PanelEditorModal.razor"] = 33,
                ["Components/Shared/QueryPlanModal.razor"] = 31,
                ["Components/Shared/ReleaseNotesModal.razor"] = 9,
                ["Components/Shared/SectionEditorModal.razor"] = 4,
                ["Components/Shared/ServerModeToggle.razor"] = 1,
                ["Components/Shared/SessionBubbleView.razor"] = 3,
                ["Components/Shared/SessionDetailPanel.razor"] = 9,
                ["Components/Shared/SessionLegend.razor"] = 1,
                ["Components/Shared/StatCard.razor"] = 2,
                ["Components/Shared/ToggleSwitch.razor"] = 1,
                ["Components/Shared/WelcomeTourOverlay.razor"] = 1,
                ["Pages/About.razor"] = 5,
                ["Pages/AccessSurface.razor"] = 77,
                ["Pages/AdvancedReporting.razor"] = 2,
                ["Pages/AgentJobGuard.razor"] = 47,
                ["Pages/AgentJobSync.razor"] = 46,
                ["Pages/AgentJobTimeline.razor"] = 14,
                ["Pages/AlertingConfig.razor"] = 210,
                ["Pages/Alerts.razor"] = 242,
                ["Pages/AlertsNoc.razor"] = 76,
                ["Pages/AppMetrics.razor"] = 10,
                ["Pages/AuditLogViewer.razor"] = 8,
                ["Pages/BaselineProgress.razor"] = 36,
                ["Pages/Benchmark.razor"] = 8,
                ["Pages/BestPractice.razor"] = 43,
                ["Pages/BlitzDashboard.razor"] = 3,
                ["Pages/BlockingForensics.razor"] = 3,
                ["Pages/BuildProfile.razor"] = 34,
                ["Pages/CapacityConsolidation.razor"] = 46,
                ["Pages/CapacityPlanning.razor"] = 66,
                ["Pages/CheckTrend.razor"] = 7,
                ["Pages/CheckValidator.razor"] = 26,
                ["Pages/Checks.razor"] = 70,
                ["Pages/CioDashboard.razor"] = 63,
                ["Pages/CodeHotspots.razor"] = 5,
                ["Pages/ComplianceBoard.razor"] = 17,
                ["Pages/ComplianceMap.razor"] = 7,
                ["Pages/ComplianceTree.razor"] = 6,
                ["Pages/DbaTools.razor"] = 26,
                ["Pages/DiagnosticsRoadmap.razor"] = 242,
                ["Pages/DiskIo.razor"] = 2,
                ["Pages/Documentation.razor"] = 12,
                ["Pages/EditAuditScripts.razor"] = 10,
                ["Pages/EnvironmentView.razor"] = 47,
                ["Pages/FullAudit.razor"] = 35,
                ["Pages/Governance.razor"] = 26,
                ["Pages/Health.razor"] = 9,
                ["Pages/ImportResults.razor"] = 33,
                ["Pages/IndexAnalysis.razor"] = 2,
                ["Pages/InstallationHelper.razor"] = 25,
                ["Pages/Login.razor"] = 9,
                ["Pages/MaintenanceRecommendations.razor"] = 13,
                ["Pages/OffboardingTrace.razor"] = 55,
                ["Pages/Onboarding.razor"] = 5,
                ["Pages/PerformanceReport.razor"] = 30,
                ["Pages/PerformanceTrends.razor"] = 50,
                ["Pages/Playbooks.razor"] = 5,
                ["Pages/Portal/ExportPack.razor"] = 87,
                ["Pages/Portal/PortalStatus.razor"] = 73,
                ["Pages/Portal/PublishToPortal.razor"] = 23,
                ["Pages/Premium.razor"] = 45,
                ["Pages/QueryExecutor.razor"] = 16,
                ["Pages/QuickCheck.razor"] = 12,
                ["Pages/Remediation.razor"] = 275,
                ["Pages/RemediationLab.razor"] = 19,
                ["Pages/RemediationTuner.razor"] = 4,
                ["Pages/ReplicationMap.razor"] = 1,
                ["Pages/ReportBundles.razor"] = 43,
                ["Pages/RiskReport.razor"] = 8,
                ["Pages/ScheduledTasks.razor"] = 51,
                ["Pages/SchedulerHealth.razor"] = 1,
                ["Pages/ServerComparison.razor"] = 11,
                ["Pages/ServerConfigDiff.razor"] = 2,
                ["Pages/ServerConfiguration.razor"] = 72,
                ["Pages/ServerDocs.razor"] = 1,
                ["Pages/Servers.razor"] = 37,
                ["Pages/ServiceManagement.razor"] = 108,
                ["Pages/Services.razor"] = 13,
                ["Pages/Sessions.razor"] = 8,
                ["Pages/Settings.razor"] = 423,
                ["Pages/SodMatrix.razor"] = 135,
                ["Pages/TestPlan.razor"] = 3,
                ["Pages/VulnerabilityAssessment.razor"] = 94,
                ["Pages/XEvents.razor"] = 1,
            };

        /// <summary>
        /// Every source <c>.razor</c> file: the whole repo tree, excluding the top-level
        /// <c>Tests/</c> directory and any <c>bin/</c> or <c>obj/</c> build-output directory
        /// anywhere in the tree (mirrors <see cref="RawPassedScan.IsBuildOutput"/>'s exclusion,
        /// widened to also drop <c>Tests/</c> so a fixture or a copied Markup dump under a test
        /// project can never inflate the count).
        /// </summary>
        private static List<string> SourceRazorFiles()
        {
            var root = RawPassedScan.RepoRoot();
            var sep = Path.DirectorySeparatorChar;

            return Directory
                .EnumerateFiles(root.FullName, "*.razor", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var p = f.Replace('/', sep);
                    return !p.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)
                        && !p.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)
                        && !p.Contains($"{sep}Tests{sep}", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }

        private static string Rel(string path) =>
            Path.GetRelativePath(RawPassedScan.RepoRoot().FullName, path).Replace('\\', '/');

        private static int CountInFile(string path, Regex pattern) =>
            pattern.Matches(File.ReadAllText(path)).Count;

        private static Dictionary<string, int> MeasurePerFile()
        {
            var measured = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var f in SourceRazorFiles())
            {
                var n = CountInFile(f, StyleAttr);
                if (n > 0)
                {
                    measured[Rel(f)] = n;
                }
            }

            return measured;
        }

        [Fact]
        public void Frozen_map_sum_equals_the_frozen_total()
        {
            var sum = FrozenPerFile.Values.Sum();
            Assert.True(sum == StyleAttributeBaseline,
                $"The frozen per-file map sums to {sum} but {nameof(StyleAttributeBaseline)} is " +
                $"{StyleAttributeBaseline}. Both are edited by hand; edit them together, in the same " +
                "commit, from one measurement.");
        }

        [Fact]
        public void No_source_razor_file_grew_its_inline_style_count()
        {
            var measured = MeasurePerFile();

            var grew = measured
                .Where(kv => FrozenPerFile.TryGetValue(kv.Key, out var frozen) && kv.Value > frozen)
                .Select(kv => $"  {kv.Key}: frozen {FrozenPerFile[kv.Key]} -> measured {kv.Value} " +
                              $"(+{kv.Value - FrozenPerFile[kv.Key]})")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            var appeared = measured
                .Where(kv => !FrozenPerFile.ContainsKey(kv.Key))
                .Select(kv => $"  {kv.Key}: frozen 0 (absent from the map) -> measured {kv.Value}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            if (grew.Count == 0 && appeared.Count == 0)
            {
                return;
            }

            var total = measured.Values.Sum();
            var shrank = measured
                .Where(kv => FrozenPerFile.TryGetValue(kv.Key, out var frozen) && kv.Value < frozen)
                .Select(kv => $"  {kv.Key}: frozen {FrozenPerFile[kv.Key]} -> measured {kv.Value}")
                .Concat(FrozenPerFile.Keys.Where(k => !measured.ContainsKey(k))
                    .Select(k => $"  {k}: frozen {FrozenPerFile[k]} -> measured 0"))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            var lines = new List<string>
            {
                $"Inline style=\"/style=' attributes grew. Total {total} vs frozen " +
                $"{StyleAttributeBaseline} (delta {total - StyleAttributeBaseline}). " +
                "The ratchet only tightens — either undo the new inline styles (prefer a CSS class or " +
                "an existing wwwroot/css/*.css rule) or, if the growth is deliberate, raise the entry " +
                $"in {nameof(FrozenPerFile)} AND {nameof(StyleAttributeBaseline)} explicitly, with a reason.",
            };

            if (grew.Count > 0)
            {
                lines.Add($"Files that GREW ({grew.Count}):");
                lines.AddRange(grew);
            }

            if (appeared.Count > 0)
            {
                lines.Add($"Files that gained their FIRST inline style ({appeared.Count}):");
                lines.AddRange(appeared);
            }

            if (shrank.Count > 0)
            {
                lines.Add($"For context, files that shrank since the freeze ({shrank.Count}) — " +
                          "lowering their map entries is welcome, but it does not license the growth above:");
                lines.AddRange(shrank);
            }

            Assert.Fail(string.Join("\n", lines));
        }

        [Fact]
        public void No_source_razor_file_has_a_scoped_style_block()
        {
            // A true floor, not a ratchet: the 2026-08-30/31 extractions moved every <style> block
            // out of source .razor files and into wwwroot/css/*.css (CLAUDE.md convention). Zero is
            // the correct permanent value here, not a number to be lowered later.
            var files = SourceRazorFiles();
            var offenders = files
                .Where(f => CountInFile(f, StyleBlock) > 0)
                .Select(Rel)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            Assert.True(offenders.Count == 0,
                "Found <style> block(s) reintroduced into source .razor files (should have been " +
                "extracted to wwwroot/css/*.css): " + string.Join(", ", offenders));
        }
    }
}
