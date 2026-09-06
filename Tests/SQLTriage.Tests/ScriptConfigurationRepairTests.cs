/* In the name of God, the Merciful, the Compassionate */

// DOES THE FIX REACH AN INSTALL THAT ALREADY EXISTS?
//
// installer/SQLTriage.iss ships Config/script-configurations.json `onlyifdoesntexist`, so an upgrade
// keeps the operator's file. ScriptConfigurationMigrator.EnsureShippedEntries covers a shipped entry
// that is MISSING. Nothing covered a shipped value that CHANGED - and the sp_Blitz output query is
// exactly that: the value every install carries produces a CSV the Export Pack refuses, which is why
// every pack built between 2026-07-23 and 2026-08-24 shipped without sp_Blitz. A fix that only
// touched the shipped file would have fixed it for new installs, which are the installs that never
// had the problem. Same class as the alert-definitions delivery gap the raw-counter lane hit
// (DECISIONS 2026-08-24 02:14), where the whole feature would have been inert for every client.
//
// The fixture is the REAL installed file: Config/script-configurations.json as it stood at dev main
// a4c3a1c, which is what an install upgraded from any release since 2026-03-27 has on disk.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    public class ScriptConfigurationRepairTests
    {
        private const string InstalledFixture = "script-configurations-a4c3a1c.json";

        private static string Installed() =>
            AuditOutputScannerBlitzTests.FixtureText(InstalledFixture);

        private static string Shipped() =>
            ScriptConfigurationMigrator.ReadShippedDefaults()
            ?? throw new InvalidOperationException("the shipped defaults are not embedded in this build");

        private static string QueryFor(string json, string scriptPath)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray()
                .Single(e => e.GetProperty("ScriptPath").GetString() == scriptPath)
                .GetProperty("SqlQueryForOutput").GetString()!;
        }

        // ── The premise, measured ───────────────────────────────────────────────────────────

        [Fact]
        public void The_installed_file_really_does_hold_the_query_that_broke_the_pack()
        {
            var query = QueryFor(Installed(), "sp_Blitz.sql");

            query.Should().Contain("xp_regread", "this is the value a4c3a1c shipped");
            BlitzCsvContractTests.SelectListColumns(query).Should().NotEqual(
                BlitzCsvContractTests.OutputTableColumnsFromScript(),
                "if this ever matched, the premise of the repair would be gone");
        }

        [Fact]
        public void The_query_shipped_now_is_not_itself_in_the_superseded_set()
        {
            // The guard that keeps the repair from ever rewriting a value into itself.
            var shippedHash = ScriptConfigurationMigrator.Sha256OfNormalised(
                QueryFor(Shipped(), "sp_Blitz.sql"));
            var installedHash = ScriptConfigurationMigrator.Sha256OfNormalised(
                QueryFor(Installed(), "sp_Blitz.sql"));

            shippedHash.Should().NotBe(installedHash);
            ScriptConfigurationMigrator.TryRepairSupersededOutputQueries(
                Shipped(), Shipped(), out _, out _).Should().BeFalse(
                "the file we ship must never be a candidate for repair");
        }

        // ── The repair itself ───────────────────────────────────────────────────────────────

        [Fact]
        public void An_installed_file_gets_the_query_that_the_Export_Pack_can_read()
        {
            ScriptConfigurationMigrator.TryRepairSupersededOutputQueries(
                Installed(), Shipped(), out var repaired, out var keys).Should().BeTrue();

            keys.Should().Equal(new[] { "sp_Blitz.sql" });
            QueryFor(repaired!, "sp_Blitz.sql").Should().Be(QueryFor(Shipped(), "sp_Blitz.sql"));
            BlitzCsvContractTests.SelectListColumns(QueryFor(repaired!, "sp_Blitz.sql"))
                .Should().Equal(BlitzCsvContractTests.OutputTableColumnsFromScript());
        }

        [Fact]
        public void Nothing_outside_that_one_string_literal_is_touched()
        {
            var original = Installed();
            ScriptConfigurationMigrator.TryRepairSupersededOutputQueries(
                original, Shipped(), out var repaired, out _).Should().BeTrue();

            // Every other property of every entry, byte for byte, in order.
            var before = Properties(original);
            var after = Properties(repaired!);
            after.Keys.Should().Equal(before.Keys, "no entry and no property is added, removed or reordered");
            foreach (var key in before.Keys.Where(k => k != "sp_Blitz.sql|SqlQueryForOutput"))
                after[key].Should().Be(before[key], key + " must be exactly as the operator left it");

            // And the file's own shape: its line endings, its indentation, its lack of a BOM.
            CountOf(repaired!, "\r\n").Should().Be(CountOf(original, "\r\n"));
            CountOf(repaired!, "\n").Should().Be(CountOf(original, "\n"));
            repaired!.StartsWith("[", StringComparison.Ordinal).Should().BeTrue();
        }

        [Fact]
        public void An_operator_who_edited_the_query_keeps_their_edit()
        {
            var edited = Installed().Replace(
                "WHERE CheckDate = (SELEct max([CheckDate])",
                "WHERE CheckDate = (SELECT MAX([CheckDate])",
                StringComparison.Ordinal);
            edited.Should().NotBe(Installed(), "this test's premise: one character of the query differs");

            ScriptConfigurationMigrator.TryRepairSupersededOutputQueries(
                edited, Shipped(), out var repaired, out var keys).Should().BeFalse(
                "the value is no longer byte-identical to anything this product shipped, so it is an edit");
            repaired.Should().BeNull();
            keys.Should().BeEmpty();
        }

        [Fact]
        public void A_cloned_entry_holding_the_same_superseded_text_is_left_alone()
        {
            // An operator who copied sp_Blitz's entry to author their own script keeps the OLD query
            // byte-identical under a DIFFERENT ScriptPath. The hash alone cannot tell the clone from
            // the real sp_Blitz.sql entry - only the enclosing entry's own ScriptPath can - so this
            // proves the repair is scoped to the entry the hash's ScriptPath actually names, not to
            // any text in the file that happens to match. Regression for the defect the honesty lens
            // proved 2026-08-24: the un-scoped regex rewrote a clone named "my_custom_blitz.sql" too,
            // and logged "sp_Blitz.sql" for both changes.
            var array = JsonNode.Parse(Installed())!.AsArray();
            var original = array.Single(e => e!["ScriptPath"]!.GetValue<string>() == "sp_Blitz.sql");
            var clone = JsonNode.Parse(original!.ToJsonString())!.AsObject();
            clone["ScriptPath"] = "my_custom_blitz.sql";
            clone["Name"] = "my_custom_blitz";
            clone["Id"] = Guid.NewGuid().ToString();
            array.Add(clone);

            var withClone = array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var cloneQueryBefore = QueryFor(withClone, "my_custom_blitz.sql");
            cloneQueryBefore.Should().Be(QueryFor(Installed(), "sp_Blitz.sql"),
                "this test's premise: the clone holds the exact old query, byte for byte");

            ScriptConfigurationMigrator.TryRepairSupersededOutputQueries(
                withClone, Shipped(), out var repaired, out var keys).Should().BeTrue();

            keys.Should().Equal(new[] { "sp_Blitz.sql" },
                "only the real sp_Blitz.sql entry is named - the clone is never touched, so it can "
                + "never be named as changed either");
            QueryFor(repaired!, "sp_Blitz.sql").Should().Be(QueryFor(Shipped(), "sp_Blitz.sql"),
                "the real entry is still repaired");
            QueryFor(repaired!, "my_custom_blitz.sql").Should().Be(cloneQueryBefore,
                "the clone keeps the operator's own script's query exactly as it was, superseded text "
                + "and all - it is a different entry, not a second copy of sp_Blitz.sql");
        }

        [Fact]
        public void Repairing_twice_changes_nothing_the_second_time()
        {
            ScriptConfigurationMigrator.TryRepairSupersededOutputQueries(
                Installed(), Shipped(), out var once, out _).Should().BeTrue();
            ScriptConfigurationMigrator.TryRepairSupersededOutputQueries(
                once!, Shipped(), out var twice, out var keys).Should().BeFalse();
            twice.Should().BeNull();
            keys.Should().BeEmpty();
        }

        [Fact]
        public void A_file_that_is_not_a_JSON_array_is_never_rewritten()
        {
            ScriptConfigurationMigrator.TryRepairSupersededOutputQueries(
                "{ not an array", Shipped(), out var repaired, out var keys).Should().BeFalse();
            repaired.Should().BeNull();
            keys.Should().BeEmpty();
        }

        [Fact]
        public void A_config_shipped_before_6f15efe_is_repaired_too()
        {
            // The OTHER value this product shipped (c3e91cd through a0c9566, 2026-03-12 to
            // 2026-03-26). Constructed from the a4c3a1c fixture by removing the one statement 6f15efe
            // added - "SET @ThisDomain = ISNULL(@ThisDomain, DEFAULT_DOMAIN());" - which is the ENTIRE
            // difference between the two shipped values; both always schema-qualified the output
            // table as master.dbo, contrary to what an earlier draft of this comment (and of the
            // SupersededOutputQueries doc comment) claimed.
            var older = Installed().Replace(
                "OUTPUT;SET @ThisDomain = ISNULL(@ThisDomain, DEFAULT_DOMAIN());", "OUTPUT;",
                StringComparison.Ordinal);
            older.Should().NotBe(Installed(), "this test's premise: the ISNULL/DEFAULT_DOMAIN statement was removed");
            var hash = ScriptConfigurationMigrator.Sha256OfNormalised(QueryFor(older, "sp_Blitz.sql"));

            // If this premise ever fails, the first hash in SupersededOutputQueries is not what this
            // test thinks it is - say so rather than asserting a repair that proves nothing.
            var repairable = ScriptConfigurationMigrator.TryRepairSupersededOutputQueries(
                older, Shipped(), out var repaired, out var keys);

            repairable.Should().BeTrue(
                "the pre-6f15efe value hashes to " + hash + ", which must be one of the two "
                + "SupersededOutputQueries entries");
            keys.Should().Equal(new[] { "sp_Blitz.sql" });
            QueryFor(repaired!, "sp_Blitz.sql").Should().Be(QueryFor(Shipped(), "sp_Blitz.sql"));
        }

        // ── The IO half, on a real file ─────────────────────────────────────────────────────

        [Fact]
        public void The_loader_path_repairs_an_installed_file_on_disk_and_keeps_its_encoding()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sqlt-repair-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "script-configurations.json");
                var bytes = File.ReadAllBytes(AuditOutputScannerBlitzTests.FixturePath(InstalledFixture));
                File.WriteAllBytes(path, bytes);

                var keys = ScriptConfigurationMigrator.RepairSupersededOutputQueries(
                    path, NullLogger.Instance);

                keys.Should().Equal(new[] { "sp_Blitz.sql" });
                var after = File.ReadAllBytes(path);
                after.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF },
                    "the fixture has no byte-order mark and must not gain one");
                QueryFor(new UTF8Encoding(false).GetString(after), "sp_Blitz.sql")
                    .Should().Be(QueryFor(Shipped(), "sp_Blitz.sql"));

                // Second run: nothing to do, and nothing written.
                var stamp = File.GetLastWriteTimeUtc(path);
                ScriptConfigurationMigrator.RepairSupersededOutputQueries(path, NullLogger.Instance)
                    .Should().BeEmpty();
                File.GetLastWriteTimeUtc(path).Should().Be(stamp);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void A_missing_file_is_not_created()
        {
            var path = Path.Combine(Path.GetTempPath(), "sqlt-repair-absent-" + Guid.NewGuid().ToString("N") + ".json");
            ScriptConfigurationMigrator.RepairSupersededOutputQueries(path, NullLogger.Instance)
                .Should().BeEmpty();
            File.Exists(path).Should().BeFalse();
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────

        private static Dictionary<string, string> Properties(string json)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(json);
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var scriptPath = entry.TryGetProperty("ScriptPath", out var sp)
                    ? sp.GetString() ?? "" : entry.GetProperty("Id").GetString() ?? "";
                foreach (var property in entry.EnumerateObject())
                    map[scriptPath + "|" + property.Name] = property.Value.ToString();
            }
            return map;
        }

        private static int CountOf(string text, string needle)
        {
            var count = 0;
            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
