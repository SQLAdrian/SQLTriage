/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data
{
    /// <summary>
    /// Restores ONE dashboard in an installed Config/dashboard-config.json to the state this build ships,
    /// from the shipped catalogue <see cref="DashboardConfigMigrator"/> already embeds. Every other
    /// dashboard is left byte-for-byte as the operator left it.
    ///
    /// <para>WHY THIS EXISTS. The only "reset" this product had was
    /// <c>DashboardConfigService.ResetToDefault</c>, which calls <c>DefaultConfigGenerator.Generate()</c>.
    /// That generator builds THREE dashboards (repository, instance, bpcheck). The product ships 27. So the
    /// one control named "reset to default" would have replaced a 27-dashboard configuration with a
    /// 3-dashboard one and saved it over the operator's file: not a factory reset, a factory amputation. It
    /// had no production caller, which is the only reason nobody lost 24 dashboards to it. The shipped
    /// catalogue became reachable in-process when the dashboards lane embedded it for the migrator
    /// (<see cref="DashboardConfigMigrator.ReadShippedDefaults"/>), so a real reset is now buildable, and
    /// this is it. DECISIONS 2026-08-26 18:23, ruling 2.</para>
    ///
    /// <para>THIS REPLACES <c>enabled</c>, AND THAT IS THE DELIBERATE DIFFERENCE FROM THE MIGRATOR. The
    /// migrator carries a corrected body to a panel the operator never touched and preserves their
    /// enable/disable toggle, because they did not ask for it. A reset is the operator asking, by name, for
    /// the shipped state of one dashboard. Shipped state includes which panels ship enabled. So the whole
    /// dashboard node is replaced: panels, queries, titles, thresholds, layout, tabs, and every
    /// <c>enabled</c> flag at both dashboard and panel level. The confirmation in Settings says exactly
    /// that before anything is written, because it cannot be undone from inside the app.</para>
    ///
    /// <para>SCOPE IS ONE ARRAY ELEMENT. Only the matching entry of the top-level <c>dashboards</c> array is
    /// touched. <c>version</c>, <c>supportQueries</c> and every other dashboard keep their exact nodes,
    /// including any property this app's typed model does not carry — the reset works on JsonNode for the
    /// same reason the migrator does.</para>
    ///
    /// <para>AN UNREADABLE STORE IS NEVER REPLACED WITH DEFAULTS (the config-store write-guard convention).
    /// Missing, unreadable, or not a JSON object this parser accepts: it reports
    /// <see cref="ResetOutcome.Refused"/>, writes nothing, and never throws. A reset of a dashboard the
    /// shipped catalogue does not define is <see cref="ResetOutcome.NotInCatalogue"/> and also writes
    /// nothing — a dashboard an operator authored themselves has no shipped state to go back to, and
    /// deleting it would be a different and unasked-for action.</para>
    /// </summary>
    internal static class DashboardFactoryReset
    {
        /// <summary>One restorable dashboard: the id the reset takes, and the title an operator recognises.</summary>
        internal sealed record ShippedDashboard(string Id, string Title);

        internal enum ResetOutcome
        {
            /// <summary>The dashboard was present and differed from shipped. It now matches shipped.</summary>
            Restored,

            /// <summary>The dashboard was absent from the installed file and has been added back at shipped state.</summary>
            Added,

            /// <summary>The dashboard already matched shipped in every field including every <c>enabled</c>
            /// flag. Nothing was written, so the file's timestamp and bytes are untouched.</summary>
            AlreadyShipped,

            /// <summary>This build's catalogue defines no dashboard with that id. Nothing was written.</summary>
            NotInCatalogue,

            /// <summary>The installed file is missing, unreadable, or not a config document this can parse,
            /// or the shipped catalogue is not embedded in this build. Nothing was written.</summary>
            Refused
        }

        /// <summary>What happened, in the terms the Settings page reports to the operator.</summary>
        internal sealed record ResetResult(ResetOutcome Outcome, string DashboardId, string Title, int PanelCount)
        {
            /// <summary>True only when the file on disk changed.</summary>
            internal bool Changed => Outcome is ResetOutcome.Restored or ResetOutcome.Added;
        }

        /// <summary>
        /// The dashboards this build can restore, in the catalogue's own order. Empty when the catalogue is
        /// not embedded, which is what the Settings picker shows rather than offering a control that would
        /// refuse every id.
        /// </summary>
        internal static IReadOnlyList<ShippedDashboard> ListShipped()
        {
            var shipped = DashboardConfigMigrator.ReadShippedDefaults();
            return shipped is null ? Array.Empty<ShippedDashboard>() : ListShipped(shipped);
        }

        /// <summary>The pure half of <see cref="ListShipped()"/>, so a test can drive it from text.</summary>
        internal static IReadOnlyList<ShippedDashboard> ListShipped(string shippedJson)
        {
            var root = DashboardConfigMigrator.ParseObject(shippedJson);
            if (root is null) return Array.Empty<ShippedDashboard>();
            if (!root.TryGetPropertyValue("dashboards", out var node) || node is not JsonArray dashboards)
                return Array.Empty<ShippedDashboard>();

            var found = new List<ShippedDashboard>();
            foreach (var entry in dashboards)
            {
                if (entry is not JsonObject dashboard) continue;
                var id = DashboardConfigMigrator.ReadString(dashboard, "id");
                if (id is null) continue;
                var title = DashboardConfigMigrator.ReadString(dashboard, "title")
                            ?? DashboardConfigMigrator.ReadString(dashboard, "navTitle")
                            ?? id;
                found.Add(new ShippedDashboard(id, title));
            }
            return found;
        }

        // ── The pure half ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Replaces the <paramref name="dashboardId"/> entry of <paramref name="installedJson"/>'s
        /// <c>dashboards</c> array with the shipped entry, whole. Returns the outcome; <paramref
        /// name="mergedJson"/> is non-null only when the caller should write.
        /// </summary>
        internal static bool TryResetDashboard(
            string installedJson,
            string shippedJson,
            string dashboardId,
            out string? mergedJson,
            out ResetOutcome outcome,
            out string title,
            out int panelCount)
        {
            mergedJson = null;
            outcome = ResetOutcome.Refused;
            title = dashboardId;
            panelCount = 0;

            var installedRoot = DashboardConfigMigrator.ParseObject(installedJson);
            var shippedRoot = DashboardConfigMigrator.ParseObject(shippedJson);
            if (installedRoot is null || shippedRoot is null) return false;

            var shippedDashboard = FindDashboard(shippedRoot, dashboardId);
            if (shippedDashboard is null)
            {
                outcome = ResetOutcome.NotInCatalogue;
                return false;
            }

            title = DashboardConfigMigrator.ReadString(shippedDashboard, "title") ?? dashboardId;
            panelCount = DashboardConfigMigrator.EnumerateDashboardPanels(shippedDashboard).Count();

            // The installed file must carry a dashboards array we can write into. A config object without
            // one is structurally not a dashboard config, and the write guard says refuse, not repair.
            if (!installedRoot.TryGetPropertyValue("dashboards", out var installedNode)
                || installedNode is not JsonArray installedDashboards)
            {
                outcome = ResetOutcome.Refused;
                return false;
            }

            var index = IndexOfDashboard(installedDashboards, dashboardId);
            if (index >= 0)
            {
                // enabled counts here: a dashboard whose only difference is a disabled panel is not at the
                // shipped state, and reporting it as already restored would be a lie the operator acts on.
                if (DashboardConfigMigrator.CanonicalNode(installedDashboards[index])
                    == DashboardConfigMigrator.CanonicalNode(shippedDashboard))
                {
                    outcome = ResetOutcome.AlreadyShipped;
                    return false;
                }

                installedDashboards[index] = shippedDashboard.DeepClone();
                outcome = ResetOutcome.Restored;
            }
            else
            {
                // Deleted, or never present on an install that predates the dashboard. Restoring it is what
                // "restore to shipped state" means; it goes on the end, and navOrder (not array position)
                // is what decides where its nav link lands.
                installedDashboards.Add(shippedDashboard.DeepClone());
                outcome = ResetOutcome.Added;
            }

            mergedJson = DashboardConfigMigrator.SerializeMatchingLineEndings(installedRoot, installedJson);
            return true;
        }

        // ── The IO half ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads <paramref name="configPath"/>, restores one dashboard, and writes it back in the file's own
        /// encoding and line endings. Copies the file to <paramref name="backupPath"/> immediately before
        /// the write and only then, so a refused reset never destroys an older backup. Announces and returns
        /// rather than throwing.
        /// </summary>
        internal static ResetResult ResetDashboard(
            string configPath, string? backupPath, string dashboardId, ILogger logger)
        {
            try
            {
                var shipped = DashboardConfigMigrator.ReadShippedDefaults();
                if (shipped is null)
                {
                    logger.LogWarning(
                        "The shipped dashboard catalogue is not embedded in this build, so {DashboardId} cannot be restored to its shipped state. {Path} has NOT been changed",
                        dashboardId, configPath);
                    return new ResetResult(ResetOutcome.Refused, dashboardId, dashboardId, 0);
                }

                if (!File.Exists(configPath))
                {
                    logger.LogWarning(
                        "{Path} does not exist, so there is nothing to restore {DashboardId} in. No file has been created",
                        configPath, dashboardId);
                    return new ResetResult(ResetOutcome.Refused, dashboardId, dashboardId, 0);
                }

                string installed;
                Encoding encoding;
                try
                {
                    using var reader = new StreamReader(
                        configPath, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                    installed = reader.ReadToEnd();
                    encoding = reader.CurrentEncoding;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Could not read {Path} to restore {DashboardId}. It has NOT been replaced or rewritten - whatever is in it is still what runs",
                        configPath, dashboardId);
                    return new ResetResult(ResetOutcome.Refused, dashboardId, dashboardId, 0);
                }

                var wrote = TryResetDashboard(
                    installed, shipped, dashboardId, out var merged, out var outcome, out var title, out var panelCount);

                if (!wrote || merged is null)
                {
                    if (outcome == ResetOutcome.Refused)
                        logger.LogWarning(
                            "{Path} is not a dashboard configuration this can read, so {DashboardId} cannot be restored. It has NOT been replaced with the shipped defaults",
                            configPath, dashboardId);
                    else
                        logger.LogInformation(
                            "Restore of dashboard {DashboardId} wrote nothing ({Outcome}). {Path} is unchanged",
                            dashboardId, outcome, configPath);
                    return new ResetResult(outcome, dashboardId, title, panelCount);
                }

                if (!string.IsNullOrEmpty(backupPath))
                {
                    try { File.Copy(configPath, backupPath, overwrite: true); }
                    catch (Exception ex)
                    {
                        // A reset the operator cannot roll back is not the reset the confirmation promised.
                        logger.LogWarning(ex,
                            "Could not copy {Path} to {BackupPath} before restoring {DashboardId}, so nothing has been written",
                            configPath, backupPath, dashboardId);
                        return new ResetResult(ResetOutcome.Refused, dashboardId, title, panelCount);
                    }
                }

                File.WriteAllText(configPath, merged, encoding);

                logger.LogInformation(
                    "Restored dashboard {DashboardId} ({Title}, {PanelCount} panel(s)) in {Path} to the state this build ships ({Outcome}). Its panels, queries, layout and enable/disable state are now the shipped ones; no other dashboard was read or written",
                    dashboardId, title, panelCount, configPath, outcome);

                return new ResetResult(outcome, dashboardId, title, panelCount);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not restore dashboard {DashboardId} in {Path}. The installed file is unchanged",
                    dashboardId, configPath);
                return new ResetResult(ResetOutcome.Refused, dashboardId, dashboardId, 0);
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────

        private static JsonObject? FindDashboard(JsonObject root, string dashboardId)
        {
            if (!root.TryGetPropertyValue("dashboards", out var node) || node is not JsonArray dashboards)
                return null;
            var index = IndexOfDashboard(dashboards, dashboardId);
            return index < 0 ? null : dashboards[index] as JsonObject;
        }

        private static int IndexOfDashboard(JsonArray dashboards, string dashboardId)
        {
            for (var i = 0; i < dashboards.Count; i++)
            {
                if (dashboards[i] is not JsonObject dashboard) continue;
                var id = DashboardConfigMigrator.ReadString(dashboard, "id");
                if (id is not null && string.Equals(id, dashboardId, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }
    }
}
