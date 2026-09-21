/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The delivery half of the strings lane's cluster 2. Editing Config/alert-definitions.json
    /// reaches FRESH installs only: an upgraded install keeps its own copy by four independent
    /// mechanisms, so without a superseded-signature registration every fix in this cluster would
    /// land nowhere except a clean install. That is exactly the gap strings-r1-11 named.
    ///
    /// <para>The fixture <c>prelane-cluster2-alerts-84beb1c.json</c> holds the twelve alert bodies
    /// as they shipped at 84beb1c, captured from the file BEFORE it was edited. These tests drive
    /// the real migrator over that fixture against the real shipped catalogue, so a registration
    /// that is merely present but does not match cannot pass.</para>
    /// </summary>
    public class AlertDefinitionMigratorCluster2Tests
    {
        private static readonly string[] Cluster2Ids =
        {
            "io_error", "disk_space_low", "database_space_full", "log_space_full", "filegroup_space",
            "vlf_count", "io_stall_time", "disk_latency_read", "disk_latency_write",
            "tempdb_contention", "processor_under_utilization", "agent_job_long_running"
        };

        // Anchored on SQLTriage.sln, the repo's own convention (RawPassedScan.RepoRoot). Probing for
        // Config/alert-definitions.json instead would match the TEST OUTPUT directory, which carries
        // its own copy - and this suite would then measure the build output, not what ships.
        private static string RepoRoot() => RawPassedScan.RepoRoot().FullName;

        private static string ShippedJson() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Config", "alert-definitions.json"));

        private static string PreLaneJson() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Tests", "SQLTriage.Tests", "Fixtures",
                "prelane-cluster2-alerts-84beb1c.json"));

        private static JsonObject AlertOf(string json, string id) =>
            JsonNode.Parse(json)!["alerts"]!.AsArray()
                .Select(n => n!.AsObject())
                .Single(o => string.Equals(o["id"]!.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The registration is real: every pre-fix body hashes to a signature the migrator holds.
        /// A hand-copied hash that does not match would leave the alert un-repaired forever, and
        /// silently - the migrator simply skips what it does not recognise.
        /// </summary>
        [Theory]
        [MemberData(nameof(Ids))]
        public void The_prefix_body_hashes_to_a_registered_superseded_signature(string id)
        {
            Assert.True(AlertDefinitionMigrator.SupersededDefinitionSignatures.TryGetValue(id, out var known),
                id + " has no superseded signature registered, so its fix reaches fresh installs only");

            var signature = AlertDefinitionMigrator.DefinitionSignature(AlertOf(PreLaneJson(), id));
            Assert.True(known!.Contains(signature),
                id + " ships a fix but its pre-fix body hashes to " + signature + ", which is not registered");
        }

        /// <summary>The whole point, end to end: an install still carrying the pre-fix catalogue is
        /// re-based onto the shipped one.</summary>
        [Fact]
        public void An_install_carrying_the_prefix_bodies_is_repaired_to_what_ships_now()
        {
            Assert.True(AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                PreLaneJson(), ShippedJson(), out var merged, out var repaired));
            Assert.NotNull(merged);

            foreach (var id in Cluster2Ids)
                Assert.Contains(id, repaired, StringComparer.OrdinalIgnoreCase);

            foreach (var id in Cluster2Ids)
            {
                var got = AlertOf(merged!, id);
                var want = AlertOf(ShippedJson(), id);
                foreach (var p in AlertDefinitionMigrator.CatalogueFactProperties)
                {
                    var wantHas = want.TryGetPropertyValue(p, out var wantVal);
                    var gotHas = got.TryGetPropertyValue(p, out var gotVal);
                    Assert.True(wantHas == gotHas, id + "." + p + ": presence differs after repair");
                    if (wantHas)
                        Assert.Equal(wantVal!.ToJsonString(), gotVal!.ToJsonString());
                }
            }
        }

        /// <summary>
        /// io_error's repair is the one that needs <c>queryMode</c> in the replaced set. Its honest
        /// SQL now lives in the config query field, which the evaluator only reads once the alert
        /// stops routing to the built-in handler. Leave queryMode behind and the re-base installs
        /// correct SQL into a field the stale routing still ignores - the dead-query defect again,
        /// delivered by the fix meant to cure it.
        /// </summary>
        [Fact]
        public void Repairing_io_error_removes_the_queryMode_that_routed_past_its_query()
        {
            Assert.Contains("queryMode", AlertDefinitionMigrator.CatalogueFactProperties);

            Assert.True(AlertOf(PreLaneJson(), "io_error").TryGetPropertyValue("queryMode", out var before));
            Assert.Equal("io_error_check", before!.GetValue<string>());

            Assert.True(AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                PreLaneJson(), ShippedJson(), out var merged, out _));
            Assert.False(AlertOf(merged!, "io_error").TryGetPropertyValue("queryMode", out _),
                "the repaired io_error still routes to the built-in handler, so its new query is dead text");
        }

        /// <summary>
        /// An operator who tuned what an alert measures keeps their edit. This is the discipline the
        /// mechanism rests on: the re-base is gated on an EXACT match of a body this product itself
        /// shipped, never a blanket overwrite.
        /// </summary>
        [Fact]
        public void An_operator_edited_alert_is_left_exactly_as_they_left_it()
        {
            var root = JsonNode.Parse(PreLaneJson())!;
            var edited = root["alerts"]!.AsArray().Select(n => n!.AsObject())
                .Single(o => o["id"]!.GetValue<string>() == "disk_latency_read");
            edited["thresholds"]!["warning"] = 999;   // an operator's own band

            Assert.True(AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                root.ToJsonString(), ShippedJson(), out var merged, out var repaired));

            Assert.DoesNotContain("disk_latency_read", repaired, StringComparer.OrdinalIgnoreCase);
            var after = AlertOf(merged!, "disk_latency_read");
            Assert.Equal(999, after["thresholds"]!["warning"]!.GetValue<int>());
            Assert.Contains("io_stall_read_ms / num_of_reads", after["query"]!.GetValue<string>());
        }

        /// <summary>
        /// Idempotence, and the guard against a signature registered against the CURRENT body by
        /// mistake: repairing what already ships must match nothing at all. A registration that
        /// caught the current body would re-write the file on every start.
        /// </summary>
        [Fact]
        public void Repairing_the_shipped_catalogue_against_itself_changes_nothing()
        {
            Assert.False(AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                ShippedJson(), ShippedJson(), out var merged, out var repaired));
            Assert.Null(merged);
            Assert.Empty(repaired);
        }

        /// <summary>Every cluster-2 alert really did change: a registration for a body that is
        /// byte-identical to what ships would be a no-op dressed as a fix.</summary>
        [Theory]
        [MemberData(nameof(Ids))]
        public void The_shipped_body_differs_from_the_prefix_body(string id)
        {
            var before = AlertOf(PreLaneJson(), id);
            var after = AlertOf(ShippedJson(), id);
            var differing = AlertDefinitionMigrator.CatalogueFactProperties
                .Where(p =>
                {
                    var a = before.TryGetPropertyValue(p, out var av) && av is not null ? av.ToJsonString() : null;
                    var b = after.TryGetPropertyValue(p, out var bv) && bv is not null ? bv.ToJsonString() : null;
                    return a != b;
                })
                .ToList();

            Assert.True(differing.Count > 0, id + " registers a superseded signature but nothing about it changed");
        }

        /// <summary>
        /// The prose-only case, on the record rather than glossed over. The signature covers
        /// MEASUREMENT facts only, so an alert whose words were wrong and whose query was right
        /// keeps the SAME signature after the fix. processor_under_utilization is the one:
        /// it read the SQL Server process's CPU share under a description claiming "Total processor
        /// utilization". Its registration therefore matches its own repaired body, and only the
        /// no-op guard in TryRepairSupersededDefinitions stops the file being rewritten on every
        /// start. That guard is load-bearing, so it is pinned here and not merely assumed.
        /// </summary>
        [Fact]
        public void A_prose_only_fix_still_reaches_an_install_and_still_settles()
        {
            const string Id = "processor_under_utilization";

            Assert.Equal(
                AlertDefinitionMigrator.DefinitionSignature(AlertOf(PreLaneJson(), Id)),
                AlertDefinitionMigrator.DefinitionSignature(AlertOf(ShippedJson(), Id)));

            // It reaches the install: the false description is replaced.
            Assert.Contains("Total processor utilization",
                AlertOf(PreLaneJson(), Id)["description"]!.GetValue<string>());
            Assert.True(AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                PreLaneJson(), ShippedJson(), out var merged, out var repaired));
            Assert.Contains(Id, repaired, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Total processor utilization is abnormally low",
                AlertOf(merged!, Id)["description"]!.GetValue<string>());

            // And it settles: running the migrator over its own output repairs nothing.
            Assert.True(AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                            merged!, ShippedJson(), out _, out var second) == false
                        || !second.Contains(Id, StringComparer.OrdinalIgnoreCase),
                Id + " would be repaired again on the next start, rewriting the file for ever");
        }

        public static IEnumerable<object[]> Ids() => Cluster2Ids.Select(i => new object[] { i });
    }
}
