// In the name of God, the Merciful, the Compassionate

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The per-dashboard factory reset (DECISIONS 2026-08-26 18:23, ruling 2).
    ///
    /// <para><b>What it replaced.</b> The one control this product had called "reset to default",
    /// <c>DashboardConfigService.ResetToDefault</c>, called <c>DefaultConfigGenerator.Generate()</c> and
    /// saved the result. The generator builds THREE dashboards; the product ships 27. So the reset would
    /// have written a 3-dashboard file over the operator's 27-dashboard one. It had no production caller,
    /// which is the only reason nobody lost 24 dashboards to it, and it is deleted rather than left for
    /// the next caller. <see cref="The_amputating_reset_to_default_does_not_come_back"/> is the guard.</para>
    ///
    /// <para><b>The three claims these tests exist for.</b> A restore puts the named dashboard at the
    /// state this build ships INCLUDING its enable flags (the deliberate difference from
    /// <see cref="DashboardConfigMigrator"/>, which preserves them). It touches no other dashboard. And
    /// it refuses rather than writing when the installed file is not something it can read, because the
    /// config-store write guard says an unreadable store is never replaced with defaults.</para>
    /// </summary>
    public class DashboardFactoryResetTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "sqltriage-factory-reset-" + Guid.NewGuid().ToString("N")[..8]);

        public DashboardFactoryResetTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
            GC.SuppressFinalize(this);
        }

        // ── Fixtures ────────────────────────────────────────────────────────────────────────
        //
        // d1 is the dashboard under restore: two panels, one of them shipped disabled, so the enable
        // claim has something to measure in BOTH directions. d2 is the bystander, and every test that
        // writes asserts d2 came through byte-for-byte.

        private const string Shipped =
            "{\"version\":1,\"dashboards\":[" +
            "{\"id\":\"d1\",\"title\":\"First\",\"enabled\":true,\"panels\":[" +
              "{\"id\":\"p1\",\"title\":\"Shipped one\",\"enabled\":true,\"query\":{\"sqlServer\":\"SELECT 1\"}}," +
              "{\"id\":\"p2\",\"title\":\"Shipped two\",\"enabled\":false,\"query\":{\"sqlServer\":\"SELECT 2\"}}]}," +
            "{\"id\":\"d2\",\"title\":\"Second\",\"enabled\":true,\"panels\":[" +
              "{\"id\":\"q1\",\"title\":\"Shipped q1\",\"enabled\":true,\"query\":{\"sqlServer\":\"SELECT 3\"}}]}" +
            "],\"supportQueries\":{}}";

        /// <summary>The same config after an operator has edited BOTH dashboards: d1's panel SQL, title
        /// and both enable flags, and d2's panel title.</summary>
        private const string Edited =
            "{\"version\":1,\"dashboards\":[" +
            "{\"id\":\"d1\",\"title\":\"First, renamed\",\"enabled\":false,\"panels\":[" +
              "{\"id\":\"p1\",\"title\":\"Edited one\",\"enabled\":false,\"query\":{\"sqlServer\":\"SELECT 999\"}}," +
              "{\"id\":\"p2\",\"title\":\"Edited two\",\"enabled\":true,\"query\":{\"sqlServer\":\"SELECT 2\"}}]}," +
            "{\"id\":\"d2\",\"title\":\"Second\",\"enabled\":true,\"panels\":[" +
              "{\"id\":\"q1\",\"title\":\"THE OPERATOR RENAMED THIS\",\"enabled\":true,\"query\":{\"sqlServer\":\"SELECT 3\"}}]}" +
            "],\"supportQueries\":{}}";

        private static JsonObject Dashboard(string configJson, string id) =>
            JsonNode.Parse(configJson)!.AsObject()["dashboards"]!.AsArray()
                .Select(d => d!.AsObject())
                .Single(d => (string)d["id"]! == id);

        private static string Canonical(string configJson, string dashboardId) =>
            DashboardConfigMigrator.CanonicalNode(Dashboard(configJson, dashboardId));

        private string WriteConfig(string json, string name = "dashboard-config.json")
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return path;
        }

        /// <summary>
        /// The REAL shipped catalogue with an operator's edits on top: the first dashboard broken (its
        /// title, its enable flag and its first panel's title), the second one renamed and left alone.
        /// The IO-half tests need this rather than the inline fixture, because
        /// <see cref="DashboardFactoryReset.ResetDashboard"/> reads the embedded catalogue and would
        /// otherwise be asked to restore a dashboard this build does not ship.
        /// </summary>
        private static string RealConfigWithEdits(out string brokenId, out string bystanderId)
        {
            var installed = JsonNode.Parse(DashboardConfigMigrator.ReadShippedDefaults()!)!.AsObject();
            var dashboards = installed["dashboards"]!.AsArray().Select(d => d!.AsObject()).ToList();

            var broken = dashboards[0];
            var bystander = dashboards[1];
            brokenId = (string)broken["id"]!;
            bystanderId = (string)bystander["id"]!;

            broken["title"] = "OPERATOR BROKE THIS";
            broken["enabled"] = false;
            var firstPanel = DashboardConfigMigrator.EnumerateDashboardPanels(broken).FirstOrDefault();
            Assert.NotNull(firstPanel);
            firstPanel!["title"] = "OPERATOR EDITED THIS PANEL";

            bystander["title"] = "OPERATOR KEEPS THIS";

            return installed.ToJsonString();
        }

        // ══ Tier 1: the catalogue the picker offers ══════════════════════════════════════════

        /// <summary>
        /// The picker is built from the embedded shipped catalogue, so this is the list an operator sees.
        /// 27 is what this build ships (measured on Config/dashboard-config.json at d2196aa); the count is
        /// pinned so a dashboard cannot silently leave the restorable set.
        /// </summary>
        [Fact]
        public void The_picker_offers_every_dashboard_this_build_ships()
        {
            var offered = DashboardFactoryReset.ListShipped();

            Assert.Equal(27, offered.Count);
            Assert.All(offered, d => Assert.False(string.IsNullOrWhiteSpace(d.Id)));
            Assert.All(offered, d => Assert.False(string.IsNullOrWhiteSpace(d.Title)));
            Assert.Equal(offered.Count, offered.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        // ══ Tier 2: the pure reset ═══════════════════════════════════════════════════════════

        [Fact]
        public void A_restore_puts_the_named_dashboard_at_the_shipped_state()
        {
            var ok = DashboardFactoryReset.TryResetDashboard(
                Edited, Shipped, "d1", out var merged, out var outcome, out var title, out var panelCount);

            Assert.True(ok);
            Assert.Equal(DashboardFactoryReset.ResetOutcome.Restored, outcome);
            Assert.Equal("First", title);
            Assert.Equal(2, panelCount);
            Assert.Equal(Canonical(Shipped, "d1"), Canonical(merged!, "d1"));
        }

        /// <summary>
        /// THE DELIBERATE DIFFERENCE FROM THE MIGRATOR. DashboardConfigMigrator preserves <c>enabled</c>
        /// because it corrects a panel the operator never asked it to touch. A reset IS the operator
        /// asking for the shipped state, and shipped state includes which panels ship on. Both directions
        /// are asserted: a panel the operator switched off comes back on, and one they switched on goes
        /// back off, and the dashboard's own flag is restored too.
        /// </summary>
        [Fact]
        public void A_restore_replaces_enabled_at_dashboard_and_panel_level()
        {
            DashboardFactoryReset.TryResetDashboard(
                Edited, Shipped, "d1", out var merged, out _, out _, out _);

            var restored = Dashboard(merged!, "d1");
            Assert.True((bool)restored["enabled"]!);                                   // operator had it false
            var panels = restored["panels"]!.AsArray().Select(p => p!.AsObject()).ToList();
            Assert.True((bool)panels.Single(p => (string)p["id"]! == "p1")["enabled"]!);   // operator had it false
            Assert.False((bool)panels.Single(p => (string)p["id"]! == "p2")["enabled"]!);  // operator had it true
        }

        [Fact]
        public void A_restore_leaves_every_other_dashboard_exactly_as_the_operator_left_it()
        {
            DashboardFactoryReset.TryResetDashboard(
                Edited, Shipped, "d1", out var merged, out _, out _, out _);

            Assert.Equal(Canonical(Edited, "d2"), Canonical(merged!, "d2"));
            Assert.Contains("THE OPERATOR RENAMED THIS", merged!, StringComparison.Ordinal);
        }

        [Fact]
        public void A_restore_keeps_the_document_around_it_and_the_dashboard_order()
        {
            DashboardFactoryReset.TryResetDashboard(
                Edited, Shipped, "d1", out var merged, out _, out _, out _);

            var root = JsonNode.Parse(merged!)!.AsObject();
            Assert.Equal(1, (int)root["version"]!);
            Assert.Empty(root["supportQueries"]!.AsObject());
            Assert.Equal(new[] { "d1", "d2" },
                root["dashboards"]!.AsArray().Select(d => (string)d!.AsObject()["id"]!).ToArray());
        }

        [Fact]
        public void A_dashboard_already_at_the_shipped_state_reports_so_and_writes_nothing()
        {
            var ok = DashboardFactoryReset.TryResetDashboard(
                Shipped, Shipped, "d1", out var merged, out var outcome, out _, out _);

            Assert.False(ok);
            Assert.Null(merged);
            Assert.Equal(DashboardFactoryReset.ResetOutcome.AlreadyShipped, outcome);
        }

        /// <summary>
        /// The already-shipped comparison must count <c>enabled</c>, or a dashboard whose only difference
        /// is a switched-off panel would be reported as already restored and the operator would act on a
        /// false all-clear. That is the exact field the migrator's comparison ignores, so it is the one
        /// worth measuring here.
        /// </summary>
        [Fact]
        public void A_dashboard_differing_only_in_an_enable_flag_is_not_reported_as_already_shipped()
        {
            var installed = Shipped.Replace(
                "{\"id\":\"p1\",\"title\":\"Shipped one\",\"enabled\":true",
                "{\"id\":\"p1\",\"title\":\"Shipped one\",\"enabled\":false",
                StringComparison.Ordinal);
            Assert.NotEqual(Shipped, installed);   // the fixture edit actually landed

            var ok = DashboardFactoryReset.TryResetDashboard(
                installed, Shipped, "d1", out var merged, out var outcome, out _, out _);

            Assert.True(ok);
            Assert.Equal(DashboardFactoryReset.ResetOutcome.Restored, outcome);
            Assert.True((bool)Dashboard(merged!, "d1")["panels"]!.AsArray()
                .Select(p => p!.AsObject()).Single(p => (string)p["id"]! == "p1")["enabled"]!);
        }

        [Fact]
        public void A_dashboard_the_operator_deleted_is_added_back_at_the_shipped_state()
        {
            var withoutD1 = "{\"version\":1,\"dashboards\":[" +
                "{\"id\":\"d2\",\"title\":\"Second\",\"enabled\":true,\"panels\":[" +
                  "{\"id\":\"q1\",\"title\":\"Shipped q1\",\"enabled\":true,\"query\":{\"sqlServer\":\"SELECT 3\"}}]}" +
                "]}";

            var ok = DashboardFactoryReset.TryResetDashboard(
                withoutD1, Shipped, "d1", out var merged, out var outcome, out _, out var panelCount);

            Assert.True(ok);
            Assert.Equal(DashboardFactoryReset.ResetOutcome.Added, outcome);
            Assert.Equal(2, panelCount);
            Assert.Equal(Canonical(Shipped, "d1"), Canonical(merged!, "d1"));
        }

        /// <summary>
        /// A dashboard the operator authored themselves has no shipped state to go back to. Reporting
        /// that is the honest answer; deleting it would be a different and unasked-for action.
        /// </summary>
        [Fact]
        public void A_dashboard_this_build_does_not_ship_is_refused_by_name()
        {
            var ok = DashboardFactoryReset.TryResetDashboard(
                Edited, Shipped, "their-own-dashboard", out var merged, out var outcome, out _, out _);

            Assert.False(ok);
            Assert.Null(merged);
            Assert.Equal(DashboardFactoryReset.ResetOutcome.NotInCatalogue, outcome);
        }

        /// <summary>
        /// Line endings come from the shared helper the doubled-CR fix (0848ec8) lives in, rather than a
        /// second hand-written copy of the rule. A CRLF install stays CRLF with no \r\r\n, and an LF file
        /// gains no CR.
        /// </summary>
        [Fact]
        public void The_write_matches_the_installed_files_own_line_endings()
        {
            // The fixture is one line, so its trailing newline is what declares the file's convention.
            DashboardFactoryReset.TryResetDashboard(
                Edited + "\r\n", Shipped, "d1", out var crlf, out _, out _, out _);
            Assert.Contains("\r\n", crlf!, StringComparison.Ordinal);
            Assert.DoesNotContain("\r\r\n", crlf!, StringComparison.Ordinal);

            DashboardFactoryReset.TryResetDashboard(
                Edited + "\n", Shipped, "d1", out var lf, out _, out _, out _);
            Assert.DoesNotContain("\r", lf!, StringComparison.Ordinal);
        }

        // ══ Tier 3: the IO half and the write guard ══════════════════════════════════════════

        [Fact]
        public void The_previous_file_is_copied_to_the_backup_before_the_write()
        {
            var edited = RealConfigWithEdits(out var brokenId, out _);
            var config = WriteConfig(edited);
            var backup = Path.Combine(_dir, "dashboard-config.backup.json");

            var result = DashboardFactoryReset.ResetDashboard(
                config, backup, brokenId, NullLogger.Instance);

            Assert.Equal(DashboardFactoryReset.ResetOutcome.Restored, result.Outcome);
            Assert.True(File.Exists(backup));
            Assert.Equal(edited, File.ReadAllText(backup));      // the backup is the PRE-restore file
            Assert.NotEqual(edited, File.ReadAllText(config));   // and the config moved on
        }

        /// <summary>
        /// The config-store write guard: an unreadable store is never replaced with defaults. A truncated
        /// or hand-mangled file is the case where a naive reset would be most tempted to "fix" it by
        /// writing the whole shipped catalogue over the top, which would destroy every other dashboard.
        /// </summary>
        [Fact]
        public void An_unparseable_installed_file_is_never_replaced_with_defaults()
        {
            const string corrupt = "{\"dashboards\": [ {\"id\": \"instance\"  ";
            var config = WriteConfig(corrupt);
            var backup = Path.Combine(_dir, "dashboard-config.backup.json");

            var result = DashboardFactoryReset.ResetDashboard(
                config, backup, "instance", NullLogger.Instance);

            Assert.Equal(DashboardFactoryReset.ResetOutcome.Refused, result.Outcome);
            Assert.False(result.Changed);
            Assert.Equal(corrupt, File.ReadAllText(config));
            Assert.False(File.Exists(backup), "a refused restore must not overwrite an older backup either");
        }

        [Fact]
        public void A_missing_installed_file_is_not_created()
        {
            var config = Path.Combine(_dir, "does-not-exist.json");

            var result = DashboardFactoryReset.ResetDashboard(
                config, null, "instance", NullLogger.Instance);

            Assert.Equal(DashboardFactoryReset.ResetOutcome.Refused, result.Outcome);
            Assert.False(File.Exists(config));
        }

        /// <summary>
        /// Running it twice must be safe: the second run finds the dashboard already at shipped state,
        /// writes nothing, and leaves the file's bytes and its last-write time alone. Anything else means
        /// a re-serialisation is churning the operator's file on every click.
        /// </summary>
        [Fact]
        public void A_second_restore_of_the_same_dashboard_writes_nothing()
        {
            var config = WriteConfig(RealConfigWithEdits(out var brokenId, out _));

            var first = DashboardFactoryReset.ResetDashboard(config, null, brokenId, NullLogger.Instance);
            Assert.Equal(DashboardFactoryReset.ResetOutcome.Restored, first.Outcome);

            var afterFirst = File.ReadAllBytes(config);
            var stampAfterFirst = File.GetLastWriteTimeUtc(config);

            var second = DashboardFactoryReset.ResetDashboard(config, null, brokenId, NullLogger.Instance);

            Assert.Equal(DashboardFactoryReset.ResetOutcome.AlreadyShipped, second.Outcome);
            Assert.False(second.Changed);
            Assert.Equal(afterFirst, File.ReadAllBytes(config));
            Assert.Equal(stampAfterFirst, File.GetLastWriteTimeUtc(config));
        }

        // ══ Tier 4: against the REAL embedded catalogue ══════════════════════════════════════

        /// <summary>
        /// The end-to-end shape an operator gets, driven from the catalogue actually compiled into this
        /// build rather than a fixture: take the shipped config as the installed one, edit two dashboards
        /// the way an operator would, restore ONE by name, and check both halves of the promise.
        /// </summary>
        [Fact]
        public void A_real_restore_fixes_the_named_dashboard_and_leaves_the_other_edit_alone()
        {
            var shipped = DashboardConfigMigrator.ReadShippedDefaults();
            Assert.NotNull(shipped);

            var editedJson = RealConfigWithEdits(out var brokenId, out var bystanderId);
            var config = WriteConfig(editedJson);

            var result = DashboardFactoryReset.ResetDashboard(config, null, brokenId, NullLogger.Instance);
            var after = File.ReadAllText(config);

            Assert.Equal(DashboardFactoryReset.ResetOutcome.Restored, result.Outcome);
            Assert.True(result.PanelCount > 0);
            Assert.Equal(Canonical(shipped!, brokenId), Canonical(after, brokenId));
            Assert.DoesNotContain("OPERATOR BROKE THIS", after, StringComparison.Ordinal);
            Assert.DoesNotContain("OPERATOR EDITED THIS PANEL", after, StringComparison.Ordinal);
            Assert.Equal(Canonical(editedJson, bystanderId), Canonical(after, bystanderId));
            Assert.Contains("OPERATOR KEEPS THIS", after, StringComparison.Ordinal);
        }

        // ══ Tier 5: the seam, and what the operator is told ══════════════════════════════════

        private static string SettingsMarkup() =>
            File.ReadAllText(Path.Combine(RawPassedScan.RepoRoot().FullName, "Pages", "Settings.razor"));

        private static string SettingsCode() =>
            File.ReadAllText(Path.Combine(RawPassedScan.RepoRoot().FullName, "Pages", "Settings.razor.cs"));

        /// <summary>
        /// The button must open the confirmation, never call the handler. A restore replaces edits that
        /// cannot be recovered from inside the app, so a single misclick is exactly the failure the
        /// confirmation exists to prevent, and "there is a modal somewhere on the page" is not the same
        /// claim as "the button goes through it".
        /// </summary>
        [Fact]
        public void The_restore_button_opens_the_confirmation_and_never_writes_directly()
        {
            var markup = SettingsMarkup();

            Assert.Contains("@onclick=\"() => _showRestoreDashboardConfirm = true\"", markup, StringComparison.Ordinal);
            Assert.Contains("@if (_showRestoreDashboardConfirm)", markup, StringComparison.Ordinal);
            Assert.Contains("@onclick=\"ConfirmRestoreDashboard\"", markup, StringComparison.Ordinal);

            // The only caller of the service method is the confirmed handler.
            var code = SettingsCode();
            Assert.Equal(1, code.Split("ResetDashboardToShipped").Length - 1);
        }

        /// <summary>
        /// The confirmation has to state the consequence, both halves of it: what the operator loses and
        /// what they keep. Ruling 2 was granted on that condition.
        /// </summary>
        [Fact]
        public void The_confirmation_says_what_is_replaced_and_what_is_kept()
        {
            var markup = SettingsMarkup();
            var start = markup.IndexOf("@if (_showRestoreDashboardConfirm)", StringComparison.Ordinal);
            Assert.True(start > 0);
            var modal = markup.Substring(start, Math.Min(2000, markup.Length - start));

            Assert.Contains("Your edits to this dashboard are replaced", modal, StringComparison.Ordinal);
            Assert.Contains("cannot undo", modal, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No other dashboard is touched", modal, StringComparison.Ordinal);
            Assert.Contains("dashboard-config.backup.json", modal, StringComparison.Ordinal);
        }

        /// <summary>
        /// <c>ResetToDefault</c> regenerated three dashboards over a 27-dashboard file and saved it. It is
        /// gone. A test that only checked today's behaviour could not catch somebody re-adding it, so this
        /// greps for the member.
        /// </summary>
        [Fact]
        public void The_amputating_reset_to_default_does_not_come_back()
        {
            var service = File.ReadAllText(Path.Combine(
                RawPassedScan.RepoRoot().FullName, "Data", "DashboardConfigService.cs"));

            // The member, not the name: the replacement's doc comment names it deliberately, so that
            // the next reader learns what was removed and why rather than rediscovering it.
            Assert.DoesNotContain("public void ResetToDefault", service, StringComparison.Ordinal);
            Assert.Contains("public DashboardResetReport ResetDashboardToShipped", service, StringComparison.Ordinal);
        }
    }
}
