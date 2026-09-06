/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The delivery half of the strings lane's cluster 1. Editing Config/alert-definitions.json
    /// reaches FRESH installs only: an upgraded install keeps its own copy by four independent
    /// mechanisms, so without a superseded-signature registration all thirty-two fixes here would
    /// land nowhere except a clean install. That is the gap strings-r1-11 named, and it matters more
    /// for this cluster than for any other in the lane: the installs that have been running a silent
    /// alert for a year are precisely the ones the fix exists for.
    ///
    /// <para>The fixture <c>prelane-cluster1-alerts-84beb1c.json</c> holds the thirty-two alert
    /// bodies as they shipped at 84beb1c, captured from the file BEFORE it was edited and verified
    /// byte-identical to <c>git show 84beb1c:Config/alert-definitions.json</c> at capture time (the
    /// cluster-2 commit that precedes this one in the same worktree touched twelve OTHER alerts, and
    /// none of these thirty-two). These tests drive the real migrator over that fixture against the
    /// real shipped catalogue, so a registration that is present but does not match cannot pass.</para>
    /// </summary>
    public class AlertDefinitionMigratorCluster1Tests
    {
        /// <summary>Sixteen yes/no queries under warning 1: capped at 1, never greater than 1.</summary>
        private static readonly string[] BooleanShaped =
        {
            "agent_stopped", "fulltext_stopped", "dtc_stopped", "browser_stopped", "ssis_stopped",
            "sql_analysis_service_stopped", "sql_reporting_service_stopped",
            "database_mail_not_configured", "unencrypted_tcp_connections", "high_risk_linked_servers",
            "public_role_dangerous_permissions", "orphaned_sql_agent_jobs",
            "sql_agent_jobs_without_notifications", "auto_update_stats_async",
            "implicit_column_conversions", "cluster_failover"
        };

        /// <summary>One literal: SELECT 0.</summary>
        private static readonly string[] ConstantShaped = { "low_compression_success_rates" };

        /// <summary>Fifteen real counts under the same warning 1, so they needed TWO.</summary>
        private static readonly string[] CountShaped =
        {
            "data_file_autogrow", "log_file_autogrow", "tempdb_autogrow", "database_unavailable",
            "agent_job_failure", "agent_job_completion", "ag_failover", "ag_replica_unhealthy",
            "ag_listener_offline", "mirror_status_change", "error_log_severity", "error_log_fatal",
            "config_change", "page_verify_disabled", "ag_not_ready_automatic_failover"
        };

        private static IEnumerable<string> AllIds =>
            BooleanShaped.Concat(ConstantShaped).Concat(CountShaped);

        // Anchored on SQLTriage.sln, the repo's own convention. Probing for
        // Config/alert-definitions.json would match the TEST OUTPUT copy, not what ships.
        private static string RepoRoot() => RawPassedScan.RepoRoot().FullName;

        private static string ShippedJson() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Config", "alert-definitions.json"));

        private static string PreLaneJson() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Tests", "SQLTriage.Tests", "Fixtures",
                "prelane-cluster1-alerts-84beb1c.json"));

        private static JsonObject AlertOf(string json, string id) =>
            JsonNode.Parse(json)!["alerts"]!.AsArray()
                .Select(n => n!.AsObject())
                .Single(o => string.Equals(o["id"]!.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));

        public static IEnumerable<object[]> Ids() => AllIds.Select(i => new object[] { i });

        /// <summary>
        /// The registration is real: every pre-fix body hashes to a signature the migrator holds. A
        /// hand-copied hash that does not match leaves the alert un-repaired for ever, and silently,
        /// because the migrator simply skips what it does not recognise.
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

        /// <summary>The count, stated rather than implied: 31 unreachable thresholds plus 1 literal.</summary>
        [Fact]
        public void All_thirty_two_repaired_alerts_are_registered()
        {
            Assert.Equal(32, AllIds.Count());
            Assert.Equal(32, AllIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            foreach (var id in AllIds)
                Assert.True(AlertDefinitionMigrator.SupersededDefinitionSignatures.ContainsKey(id), id);
        }

        /// <summary>The whole point, end to end: an install still carrying the pre-fix catalogue is
        /// re-based onto the shipped one, in every catalogue fact.</summary>
        [Fact]
        public void An_install_carrying_the_prefix_bodies_is_repaired_to_what_ships_now()
        {
            Assert.True(AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                PreLaneJson(), ShippedJson(), out var merged, out var repaired));
            Assert.NotNull(merged);

            foreach (var id in AllIds)
                Assert.Contains(id, repaired, StringComparer.OrdinalIgnoreCase);

            foreach (var id in AllIds)
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
        /// The repair is what makes a silent alert speak on an EXISTING install. Asserted through the
        /// real predicate rather than by reading the threshold, because the threshold on its own is
        /// not the claim: the claim is that the smallest real occurrence now fires.
        /// </summary>
        [Fact]
        public void After_the_repair_a_single_occurrence_fires_on_an_upgraded_install()
        {
            var beforeSilent = AllIds
                .Where(id => !Fires(AlertOf(PreLaneJson(), id)))
                .ToList();
            Assert.Equal(32, beforeSilent.Count);   // every one of them was unreachable at 1

            Assert.True(AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                PreLaneJson(), ShippedJson(), out var merged, out _));

            // Two of the thirty-two are deliberately not "fires at 1", and both are recorded here
            // rather than quietly skipped, because a blanket skip is how a carve-out becomes a hole.
            var banded = new[] { "low_compression_success_rates", "implicit_column_conversions" };

            var stillSilent = AllIds
                .Where(id => !banded.Contains(id, StringComparer.Ordinal))
                .Where(id => !Fires(AlertOf(merged!, id)))
                .ToList();
            Assert.Empty(stillSilent);

            // The rate alert inverts: it now fires when the value FALLS below its warning band, so
            // "does 1 breach it" is the wrong question. A 10 percent success rate must fire; 90 must not.
            var rate = AlertOf(merged!, "low_compression_success_rates");
            Assert.Equal("less_than", rate["operator"]!.GetValue<string>());
            Assert.True(Breaches(rate, 10.0));
            Assert.False(Breaches(rate, 90.0));

            // The implicit-conversion alert reports the PERCENTAGE of the sampled expensive plans
            // that are affected, and a substantial share is normal - MEASURED 2026-08-28 at 46 then
            // 42 percent on an idle .\NEW2022 and 0 percent on an idle .\OLD2017. Firing at one, or
            // at the 25 the first repair shipped, replaces a silent alert with a permanent one.
            var conversions = AlertOf(merged!, "implicit_column_conversions");
            Assert.False(Breaches(conversions, 46.0));
            Assert.True(Breaches(conversions, 85.0));
        }

        private static bool Fires(JsonObject alert) => Breaches(alert, 1.0);

        private static bool Breaches(JsonObject alert, double value)
        {
            var op = alert["operator"]!.GetValue<string>();
            var th = alert["thresholds"]!.AsObject();
            double? warn = th.TryGetPropertyValue("warning", out var w) && w is not null ? w.GetValue<double>() : null;
            double? crit = th.TryGetPropertyValue("critical", out var c) && c is not null ? c.GetValue<double>() : null;
            return SQLTriage.Data.Services.AlertEvaluationService.IsThresholdBreached(value, warn, op)
                || SQLTriage.Data.Services.AlertEvaluationService.IsThresholdBreached(value, crit, op);
        }

        /// <summary>
        /// An operator who tuned what an alert measures keeps their edit. This is the discipline the
        /// whole mechanism rests on: the re-base is gated on an EXACT match of a body this product
        /// itself shipped, never a blanket overwrite.
        /// </summary>
        [Fact]
        public void An_operator_edited_alert_is_left_exactly_as_they_left_it()
        {
            var root = JsonNode.Parse(PreLaneJson())!;
            var edited = root["alerts"]!.AsArray().Select(n => n!.AsObject())
                .Single(o => o["id"]!.GetValue<string>() == "database_unavailable");
            edited["thresholds"]!["warning"] = 7;    // an operator who decided seven databases is fine

            Assert.True(AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                root.ToJsonString(), ShippedJson(), out var merged, out var repaired));

            Assert.DoesNotContain("database_unavailable", repaired, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(7, AlertOf(merged!, "database_unavailable")["thresholds"]!["warning"]!.GetValue<int>());
        }

        /// <summary>
        /// Idempotence, and the guard against a signature registered against the CURRENT body by
        /// mistake: repairing what already ships must match nothing at all, or the file would be
        /// rewritten on every start for ever.
        /// </summary>
        [Fact]
        public void Repairing_the_shipped_catalogue_against_itself_changes_nothing()
        {
            Assert.False(AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                ShippedJson(), ShippedJson(), out var merged, out var repaired));
            Assert.Null(merged);
            Assert.Empty(repaired);
        }

        /// <summary>Every registered alert really did change: a signature for a body that is
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
        /// The nine whose SQL was replaced, separated from the twenty-three whose only defect was
        /// the threshold. Stated as a test so the commit's own count is measured rather than
        /// asserted, and so a later query edit that forgets its superseded registration shows up as
        /// a changed list rather than as nothing at all.
        /// </summary>
        [Fact]
        public void Exactly_nine_alerts_had_their_query_replaced()
        {
            var queryChanged = AllIds
                .Where(id => AlertOf(PreLaneJson(), id)["query"]!.GetValue<string>()
                             != AlertOf(ShippedJson(), id)["query"]!.GetValue<string>())
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(
                new[]
                {
                    "auto_update_stats_async", "cluster_failover", "high_risk_linked_servers",
                    "implicit_column_conversions", "low_compression_success_rates",
                    "orphaned_sql_agent_jobs", "public_role_dangerous_permissions",
                    "sql_agent_jobs_without_notifications", "unencrypted_tcp_connections"
                },
                queryChanged);
        }
    }
}
