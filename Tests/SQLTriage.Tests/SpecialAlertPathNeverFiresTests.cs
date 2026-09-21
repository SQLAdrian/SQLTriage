/* In the name of God, the Merciful, the Compassionate */

// ── The never-fires class on the SPECIAL path (strings lane fix round, 2026-08-28) ─────────────
//
// WHAT WAS WRONG, AND WHY THE LANE'S OWN GUARD COULD NOT SEE IT. Cluster 1 closed the
// strict-comparison defect for every alert whose reachable range could be read off its query text:
// IsThresholdBreached is `value > threshold`, never `>=`, so a query capped at 1 against a warning
// of 1 is silent for ever. The lint it left behind reads the shape off AlertDefinition.Query. An
// alert routed by queryMode has no executable query - the evaluator hands it to a built-in handler
// that runs its own SQL - so SmallestAlertableValue returned null for the whole class and all four
// lints skipped it. Three shipped alerts were sitting in that shadow at warning 1:
//
//   instance_unreachable   Critical, enabled. CheckConnectivityAsync returns exactly 1 for an
//                          unreachable server. 1 > 1 is false, so a DOWN INSTANCE was silence.
//   machine_unreachable    Critical, enabled. Same handler, same arithmetic, same silence.
//   deadlock               enabled, alwaysAlert. CountDeadlocksAsync returns 1 for one deadlock,
//                          under a description reading "One or more deadlocks detected ... in the
//                          last 5 minutes". It took two.
//
// WHAT IS PROVED HERE, BY EXECUTION. These tests drive the production evaluator,
// AlertEvaluationService.EvaluateSpecialAlertAsync, against a REAL dead endpoint (127.0.0.1,1 -
// nothing listens, the connection is refused immediately), with the definitions read off the
// shipped Config/alert-definitions.json rather than hand-built. The alert either appears in
// ActiveAlerts or it does not; nothing about the threshold is inferred.
//
// NON-VACUITY IS PART OF THE TEST, not a note beside it. The last test re-runs the identical path
// with the pre-fix threshold of 1 and asserts SILENCE. Without that row a broken evaluator that
// fired on everything would pass the rest.
//
// THE DEADLOCK HALF WAS ALSO PROVED LIVE, ONCE, AND IS NOT RE-RUN HERE. On .\new2022 a real
// deadlock was generated between two sessions on a scratch database; the victim reported
// "Msg 1205 ... was deadlocked on lock resources ... chosen as the deadlock victim", and the exact
// SQL CountDeadlocksAsync runs then returned 1 against master. Reproducing a deadlock inside a unit
// test needs a live instance and two connections, so what is pinned here is the arithmetic that 1
// then has to clear, through the real predicate.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class SpecialAlertPathNeverFiresTests
    {
        // Anchored on the solution file: probing for Config/alert-definitions.json would find the
        // test output copy instead of the bytes that install.
        private static AlertDefinition Shipped(string id)
        {
            var json = File.ReadAllText(
                Path.Combine(RawPassedScan.RepoRoot().FullName, "Config", "alert-definitions.json"));
            var file = JsonSerializer.Deserialize<AlertDefinitionsFile>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(file);
            return file!.Alerts.Single(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// THE BLOCKER, closed and measured. The shipped instance_unreachable, driven through the
        /// production special path against a dead endpoint, must produce an active alert.
        /// </summary>
        [Fact]
        public async Task Instance_unreachable_fires_against_a_dead_endpoint()
        {
            using var svc = BuildEngine();
            svc.DryRun = true;
            var (connection, deadEndpoint) = DeadEndpoint();

            await svc.EvaluateSpecialAlertAsync(
                Shipped("instance_unreachable"), connection, deadEndpoint, new AlertGlobalDefaults());

            Assert.True(svc.ActiveAlerts.Count > 0,
                "instance_unreachable did not fire against a dead endpoint. CheckConnectivityAsync "
                + "returns exactly 1 for unreachable and IsThresholdBreached is strict, so any "
                + "warning threshold at or above 1 makes a down instance produce silence.");
        }

        /// <summary>
        /// The sibling, which is the same handler. Its NAME says host and its old description said
        /// "The host machine does not respond to monitoring requests"; EvaluateSpecialAlertAsync maps
        /// both connectivity_check and host_connectivity_check to CheckConnectivityAsync, so what it
        /// actually measures is a SQL connection. The threshold fix is what makes that prose live, so
        /// the prose was corrected in the same commit and is pinned here.
        /// </summary>
        [Fact]
        public async Task Machine_unreachable_fires_against_a_dead_endpoint_and_says_what_it_measures()
        {
            var alert = Shipped("machine_unreachable");

            Assert.Contains("same connection test as Instance Unreachable", alert.Description,
                StringComparison.OrdinalIgnoreCase);

            using var svc = BuildEngine();
            svc.DryRun = true;
            var (connection, deadEndpoint) = DeadEndpoint();

            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());

            Assert.True(svc.ActiveAlerts.Count > 0,
                "machine_unreachable did not fire against a dead endpoint.");
        }

        /// <summary>
        /// A reachable server must stay quiet, or the fix would have swapped a silent alert for a
        /// permanent one - which is not a fix. Armed by SQLTRIAGE_LIVE_INSTANCE (set it to an
        /// instance name such as <c>.\NEW2022</c>); reports SKIPPED, never a vacuous pass, when it is
        /// unset. RUN ARMED on 2026-08-28 against <c>.\NEW2022</c>: no alert.
        /// </summary>
        [LiveFact("SQLTRIAGE_LIVE_INSTANCE")]
        public async Task A_reachable_instance_produces_no_connectivity_alert()
        {
            var target = Environment.GetEnvironmentVariable("SQLTRIAGE_LIVE_INSTANCE");
            Assert.False(string.IsNullOrWhiteSpace(target),
                "the attribute skips an unarmed run; this assertion is what makes a WEAKENED "
                + "attribute fail instead of passing on nothing");

            using var svc = BuildEngine();
            svc.DryRun = true;

            var connection = new ServerConnection
            {
                Id = Guid.NewGuid().ToString(),
                ServerNames = target!,
                UseWindowsAuthentication = true,
                // A local dev instance carries a self-signed certificate, and the driver encrypts by
                // default. Without this the handler returns 1 for a server that is plainly up, and
                // this test would "prove" the fix by measuring a certificate error.
                TrustServerCertificate = true,
                ConnectionTimeout = 5,
                IsEnabled = true,
            };

            await svc.EvaluateSpecialAlertAsync(
                Shipped("instance_unreachable"), connection, target!, new AlertGlobalDefaults());

            Assert.Empty(svc.ActiveAlerts);
        }

        /// <summary>
        /// The same claim without a server, through the real predicate: the handler answers 0 for a
        /// reachable instance, and 0 does not breach a warning of 0 under strict greater_than.
        /// </summary>
        [Fact]
        public void A_reachable_reading_of_zero_does_not_breach_the_new_threshold()
        {
            foreach (var id in new[] { "instance_unreachable", "machine_unreachable" })
            {
                var a = Shipped(id);
                Assert.False(AlertEvaluationService.IsThresholdBreached(0.0, a.Thresholds.Warning, a.Operator),
                    id + " must stay silent while the server answers");
                Assert.True(AlertEvaluationService.IsThresholdBreached(1.0, a.Thresholds.Warning, a.Operator),
                    id + " must speak the moment it does not");
            }
        }

        /// <summary>
        /// NON-VACUITY. The same path, the same dead endpoint, the same handler - with the threshold
        /// exactly as it shipped at 84beb1c. This is the defect, reproduced, and it is what tells a
        /// later reader that the three tests above measure the threshold and not the evaluator's
        /// willingness to fire at anything.
        /// </summary>
        [Fact]
        public async Task The_prefix_threshold_of_one_produced_silence_on_the_same_dead_endpoint()
        {
            using var svc = BuildEngine();
            svc.DryRun = true;
            var (connection, deadEndpoint) = DeadEndpoint();

            var prefix = Shipped("instance_unreachable");
            prefix.Thresholds = new AlertThresholds { Warning = 1 };   // as it shipped at 84beb1c

            await svc.EvaluateSpecialAlertAsync(prefix, connection, deadEndpoint, new AlertGlobalDefaults());

            Assert.Empty(svc.ActiveAlerts);
            Assert.False(svc.HasEvaluationFailure(deadEndpoint),
                "the handler MEASURED - it returned 1 for unreachable. The silence is the threshold, "
                + "not a failure to measure, which is precisely why nothing showed Unknown either.");
        }

        /// <summary>
        /// The deadlock arithmetic, through the real predicate: one deadlock must clear the shipped
        /// threshold, and a quiet five minutes must not.
        /// </summary>
        [Fact]
        public void One_deadlock_clears_the_shipped_deadlock_threshold()
        {
            var a = Shipped("deadlock");

            Assert.True(AlertEvaluationService.IsThresholdBreached(1.0, a.Thresholds.Warning, a.Operator),
                "its own description reads \"One or more deadlocks detected\"");
            Assert.False(AlertEvaluationService.IsThresholdBreached(0.0, a.Thresholds.Warning, a.Operator));
            Assert.False(AlertEvaluationService.IsThresholdBreached(5.0, a.Thresholds.Critical, a.Operator),
                "five is the band, so five is still a warning and six is critical");
            Assert.True(AlertEvaluationService.IsThresholdBreached(6.0, a.Thresholds.Critical, a.Operator));
        }

        // ── sql_response_time, timed in the app (lane Q14, 2026-09-18) ───────────────────────────
        //
        // INVARIANT: every enabled shipped alert must be able to produce a value that can cross its own
        // threshold, and a probe that FAILS must never be mistaken for one that measured. The shipped
        // standard query returned a constant SELECT 1 as its first result set, so the alert could never
        // fire; ruling R1 (DECISIONS 2026-09-18 01:40) moved it to the response_time_probe handler.

        /// <summary>
        /// A dead endpoint is not a latency. The handler must return the SAME failure outcome the other
        /// measuring handlers return - no value, an evaluation failure naming the exception, the alert
        /// Unknown - and never a fired latency alert. Non-vacuous twice over: the threshold is dropped to
        /// -1, so ANY reading at all would fire; and the recorded reason must name an exception, not the
        /// fallback "queryMode ... produced no measurement" sentence, which is what an alert whose mode
        /// had no dispatch entry would record instead.
        /// </summary>
        [Fact]
        public async Task Sql_response_time_against_a_dead_endpoint_is_unknown_and_never_a_latency()
        {
            var alert = Shipped("sql_response_time");
            Assert.Equal("response_time_probe", alert.QueryMode);
            alert.Thresholds = new AlertThresholds { Warning = -1, Critical = -1 };   // any reading would fire
            // These tests measure the READING, not the 2-minute hold the alert ships (ruling 2, DECISIONS
            // 2026-09-18 04:21). With the hold on, one evaluation never opens an episode, so an empty
            // ActiveAlerts would prove nothing here. The hold is proved by AlertBreachHoldTests.
            Assert.Equal(120, alert.HoldSeconds);
            alert.HoldSeconds = 0;

            using var svc = BuildEngine();
            svc.DryRun = true;
            var (connection, deadEndpoint) = DeadEndpoint();

            await svc.EvaluateSpecialAlertAsync(alert, connection, deadEndpoint, new AlertGlobalDefaults());

            Assert.Empty(svc.ActiveAlerts);
            Assert.True(svc.HasEvaluationFailure(deadEndpoint),
                "a probe that could not connect measured nothing, so the alert must read Unknown");
            var failure = Assert.Single(svc.EvaluationFailures, f => f.AlertId == "sql_response_time");
            Assert.DoesNotContain("produced no measurement", failure.ErrorSummary);
        }

        /// <summary>
        /// The handler measures a real round trip through the real dispatch. Armed by
        /// SQLTRIAGE_LIVE_INSTANCE (for example lpc:MSI\NEW2022). At warning -1 the measured value must
        /// fire, which proves it is a number and not null; at the shipped thresholds a healthy local
        /// instance must stay silent and record no failure.
        /// </summary>
        [LiveFact("SQLTRIAGE_LIVE_INSTANCE")]
        public async Task Sql_response_time_measures_a_real_round_trip_on_a_live_instance()
        {
            var target = Environment.GetEnvironmentVariable("SQLTRIAGE_LIVE_INSTANCE");
            Assert.False(string.IsNullOrWhiteSpace(target),
                "the attribute skips an unarmed run; this assertion is what makes a WEAKENED "
                + "attribute fail instead of passing on nothing");

            var connection = new ServerConnection
            {
                Id = Guid.NewGuid().ToString(),
                ServerNames = target!,
                UseWindowsAuthentication = true,
                TrustServerCertificate = true,
                ConnectionTimeout = 5,
                IsEnabled = true,
            };

            using (var svc = BuildEngine())
            {
                svc.DryRun = true;
                var anyReading = Shipped("sql_response_time");
                anyReading.Thresholds = new AlertThresholds { Warning = -1 };
                // These tests measure the READING, not the 2-minute hold the alert ships (ruling 2, DECISIONS
                // 2026-09-18 04:21). With the hold on, one evaluation never opens an episode, so an empty
                // ActiveAlerts would prove nothing here. The hold is proved by AlertBreachHoldTests.
                anyReading.HoldSeconds = 0;
                await svc.EvaluateSpecialAlertAsync(anyReading, connection, target!, new AlertGlobalDefaults());

                var fired = Assert.Single(svc.ActiveAlerts);
                Assert.True(fired.LastValue >= 0 && fired.LastValue < 15_000,
                    "a measured round trip is a non-negative number of milliseconds under the command timeout; got " + fired.LastValue);
            }

            using (var svc = BuildEngine())
            {
                svc.DryRun = true;
                var atShippedThresholds = Shipped("sql_response_time");
                atShippedThresholds.HoldSeconds = 0;   // so an empty result means "did not breach", not "held"
                await svc.EvaluateSpecialAlertAsync(atShippedThresholds, connection, target!, new AlertGlobalDefaults());
                Assert.Empty(svc.ActiveAlerts);
                Assert.False(svc.HasEvaluationFailure(target!));
            }
        }

        /// <summary>
        /// THE POOL TWIN (lane Q14 fix round, 2026-09-18). SQLTriage's own connection pool blocks a
        /// rent while the per-server cap is reached, and the first version of the handler started its
        /// clock before the rent, so a busy SQLTriage read as a slow server. The Q14 gate PROVED it on a
        /// healthy .\NEW2022: 13 held pooled connections, a 2,604 ms reading, a Critical at the shipped
        /// thresholds. Here a REAL pool is capped at one connection per server, that connection is held,
        /// and it is returned after HoldMs while the probe waits. Non-vacuous: the wall clock around the
        /// evaluation must show the probe really waited at least HoldMs, which is what a clock around the
        /// rent would have read; the reading itself must stay under the shipped warning. Armed by
        /// SQLTRIAGE_LIVE_INSTANCE (for example lpc:MSI\NEW2022).
        /// </summary>
        [LiveFact("SQLTRIAGE_LIVE_INSTANCE")]
        public async Task Sql_response_time_does_not_count_a_wait_for_the_apps_own_pool_on_a_live_instance()
        {
            const int HoldMs = 2500;
            var target = Environment.GetEnvironmentVariable("SQLTRIAGE_LIVE_INSTANCE");
            Assert.False(string.IsNullOrWhiteSpace(target),
                "the attribute skips an unarmed run; this assertion is what makes a WEAKENED "
                + "attribute fail instead of passing on nothing");

            var shippedWarning = Shipped("sql_response_time").Thresholds.Warning;
            Assert.True(shippedWarning.HasValue, "the shipped warning is the bar this reading must stay under");
            var warningMs = shippedWarning!.Value;

            var connection = new ServerConnection
            {
                Id = Guid.NewGuid().ToString(),
                ServerNames = target!,
                UseWindowsAuthentication = true,
                TrustServerCertificate = true,
                ConnectionTimeout = 5,
                IsEnabled = true,
            };

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionPool:MaxPerServer"] = "1",
                    ["ConnectionPool:MinSize"] = "0",
                    ["ConnectionPool:TimeoutSeconds"] = "30",
                })
                .Build();
            using var pool = new SqlConnectionPoolService(config, NullLogger<SqlConnectionPoolService>.Instance);

            // The handler rents with exactly this string, so this takes the server's only slot.
            var connStr = connection.GetConnectionString(target!, "master");
            var held = await pool.GetConnectionAsync(connStr);
            Assert.Equal(1, pool.ActiveCountForServer(connStr));

            using var svc = BuildEngine(pool);
            svc.DryRun = true;
            var anyReading = Shipped("sql_response_time");
            anyReading.Thresholds = new AlertThresholds { Warning = -1 };   // any reading at all fires
            // These tests measure the READING, not the 2-minute hold the alert ships (ruling 2, DECISIONS
            // 2026-09-18 04:21). With the hold on, one evaluation never opens an episode, so an empty
            // ActiveAlerts would prove nothing here. The hold is proved by AlertBreachHoldTests.
            anyReading.HoldSeconds = 0;

            var wall = System.Diagnostics.Stopwatch.StartNew();
            var release = Task.Run(async () =>
            {
                await Task.Delay(HoldMs);
                pool.ReturnConnection(held, connStr);
            });
            await svc.EvaluateSpecialAlertAsync(anyReading, connection, target!, new AlertGlobalDefaults());
            wall.Stop();
            await release;

            Assert.True(wall.ElapsedMilliseconds >= HoldMs - 50,
                "the probe did not wait on the saturated pool, so this run proves nothing; wall "
                + wall.ElapsedMilliseconds + " ms");
            Assert.False(svc.HasEvaluationFailure(target!));
            var fired = Assert.Single(svc.ActiveAlerts);
            Assert.True(fired.LastValue >= 0 && fired.LastValue < warningMs,
                "the probe waited " + wall.ElapsedMilliseconds + " ms for SQLTriage's own pool and read "
                + fired.LastValue + " ms; a wait inside the app is not the server's response time");
        }

        /// <summary>
        /// THE BASELINE TWIN. The seeder executes <c>alert.Query</c> for every alert it seeds. Lane Q14
        /// moved sql_response_time to a handler while it shipped canBaseline:true, so without the seeder's
        /// routed-exclusion clause the seeder would have learned fences over its inert query text. Driven
        /// through the REAL <c>RunSeedCycleAsync</c> against a dead endpoint: the seeder registers a
        /// tracking key for every alert it tries to seed, so the keys ARE the seeded set.
        ///
        /// <para>THE SPECIMEN IS SYNTHETIC (lane alert-correctness 2, 2026-09-19). A2 turned
        /// sql_response_time's canBaseline off, and AlertRoutedBaselineCensusTests now keeps every shipped
        /// routed alert off it, so no shipped alert is routed and baselined any more. The specimen is a copy
        /// of sql_response_time under its own id with canBaseline true, added to the copied catalogue, so the
        /// routed-exclusion clause keeps a guard. Non-vacuous: the specimen passes the seeder's OLD filter
        /// (enabled and canBaseline), and a standard canBaseline alert is seeded. PROVED to go red when the
        /// routed-exclusion clause is removed from AlertBaselineService.SeedableAlerts (one mutation,
        /// reverted; the project's private evidence archive, evidence/alert-correctness-l2-2026-09-19).</para>
        /// </summary>
        [Fact]
        public async Task The_baseline_seeder_never_runs_a_handler_routed_alerts_inert_query()
        {
            const string SpecimenId = "specimen_routed_and_baselined";

            var dir = Path.Combine(Path.GetTempPath(), "sqlt-q14-seed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var defsPath = Path.Combine(dir, "alert-definitions.json");
                var catalogue = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(
                    Path.Combine(RawPassedScan.RepoRoot().FullName, "Config", "alert-definitions.json")))!;
                var alertsArray = catalogue["alerts"]!.AsArray();
                var template = alertsArray.Single(n => n!["id"]!.GetValue<string>() == "sql_response_time")!.DeepClone().AsObject();
                template["id"] = SpecimenId;
                template["name"] = "Synthetic routed and baselined specimen";
                template["enabled"] = true;
                template["canBaseline"] = true;
                alertsArray.Add(template);
                File.WriteAllText(defsPath, catalogue.ToJsonString());
                var definitions = new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance, defsPath);

                var specimen = definitions.GetAlert(SpecimenId);
                Assert.True(specimen != null && specimen.Enabled && specimen.CanBaseline,
                    "the specimen must pass the seeder's OLD filter (Enabled && CanBaseline), which is what makes this test mean something");
                Assert.True(AlertEvaluationService.IsRoutedToBuiltInHandler(specimen!),
                    "the specimen must be handler-routed; CHECK its queryMode, copied from sql_response_time");

                var settings = new UserSettingsService(Path.Combine(dir, "user-settings.json"));
                settings.SetAlertBaselineEnabled(true);

                using var cache = new liveQueriesCacheStore();
                SetField(cache, "_connectionString", "Data Source=" + Path.Combine(dir, "cache.db") + ";Mode=ReadWriteCreate;");
                InvokePrivate(cache, "InitializeSchema");

                var connections = new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance);
                var (dead, deadEndpoint) = DeadEndpoint();
                SetField(connections, "_connections", new List<ServerConnection> { dead });

                using var baseline = new AlertBaselineService(
                    NullLogger<AlertBaselineService>.Instance, definitions, connections, cache, settings);

                var seed = typeof(AlertBaselineService).GetMethod("RunSeedCycleAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(seed);
                await (Task)seed!.Invoke(baseline, null)!;

                var counts = typeof(AlertBaselineService).GetField("_sampleCounts", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(baseline) as System.Collections.IDictionary;
                Assert.NotNull(counts);
                var seeded = counts!.Keys.Cast<string>().ToList();

                var standardSeedable = definitions.GetAllAlerts()
                    .First(a => a.Enabled && a.CanBaseline && !AlertEvaluationService.IsRoutedToBuiltInHandler(a));
                Assert.Contains((standardSeedable.Id + ":" + deadEndpoint).ToLowerInvariant(), seeded);
                Assert.DoesNotContain(seeded, k => k.StartsWith(SpecimenId + ":", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(seeded, k => k.StartsWith("sql_response_time:", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(
                    AlertBaselineService.SeedableAlerts(definitions.GetAllAlerts()).Count,
                    seeded.Count);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* a locked sqlite file is not this test's verdict */ }
            }
        }

        /// <summary>
        /// THE COMPLETION TWIN (lane Q14 fix round, 2026-09-18). Stale samples for a pair the seeder
        /// never visits must not hold "seeding complete" false for ever. Two members are pre-loaded
        /// below the 10-sample minimum: sql_response_time, which left SeedableAlerts in lane Q14 (the
        /// gate PROVED an upgraded install with 3 old samples never completing), and a server that is
        /// no longer in the connection list, the older member of the same class. Every pair the seeder
        /// DOES visit is pre-loaded full, so no live server is needed. Non-vacuous: after the load both
        /// stale keys sit in the counts at 3, which the old rule (every loaded key must reach 10) read
        /// as incomplete.
        /// </summary>
        [Fact]
        public async Task Seeding_completes_when_stale_samples_belong_to_pairs_the_seeder_never_visits()
        {
            const string removedServer = "a-server-since-removed";
            var dir = Path.Combine(Path.GetTempPath(), "sqlt-q14-complete-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var defsPath = Path.Combine(dir, "alert-definitions.json");
                File.Copy(Path.Combine(RawPassedScan.RepoRoot().FullName, "Config", "alert-definitions.json"), defsPath);
                var definitions = new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance, defsPath);

                var settings = new UserSettingsService(Path.Combine(dir, "user-settings.json"));
                settings.SetAlertBaselineEnabled(true);

                using var cache = new liveQueriesCacheStore();
                SetField(cache, "_connectionString", "Data Source=" + Path.Combine(dir, "cache.db") + ";Mode=ReadWriteCreate;");
                InvokePrivate(cache, "InitializeSchema");

                var connections = new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance);
                var (dead, deadEndpoint) = DeadEndpoint();
                SetField(connections, "_connections", new List<ServerConnection> { dead });

                var seedable = AlertBaselineService.SeedableAlerts(definitions.GetAllAlerts());
                Assert.NotEmpty(seedable);
                Assert.DoesNotContain(seedable, a => a.Id == "sql_response_time");

                var now = DateTime.UtcNow;
                using (var db = cache.CreateExternalConnection())
                {
                    await db.OpenAsync();
                    using var tx = db.BeginTransaction();
                    void Insert(string alertId, string server, int n)
                    {
                        for (var i = 0; i < n; i++)
                        {
                            using var cmd = db.CreateCommand();
                            cmd.Transaction = tx;
                            cmd.CommandText =
                                "INSERT INTO alert_baseline_samples "
                                + "(alert_id, server_name, sampled_at, value, hour_of_day, day_of_week, fetched_at) "
                                + "VALUES (@aid, @srv, @sat, 1.0, 0, 0, @sat)";
                            cmd.Parameters.AddWithValue("@aid", alertId);
                            cmd.Parameters.AddWithValue("@srv", server);
                            cmd.Parameters.AddWithValue("@sat", now.AddSeconds(-i).ToString("o"));
                            cmd.ExecuteNonQuery();
                        }
                    }
                    foreach (var a in seedable) Insert(a.Id, deadEndpoint, 10);
                    Insert("sql_response_time", deadEndpoint, 3);
                    Insert(seedable[0].Id, removedServer, 3);
                    tx.Commit();
                }

                using var baseline = new AlertBaselineService(
                    NullLogger<AlertBaselineService>.Instance, definitions, connections, cache, settings);

                var load = typeof(AlertBaselineService).GetMethod("LoadSampleCountsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(load);
                await (Task)load!.Invoke(baseline, null)!;

                var counts = typeof(AlertBaselineService).GetField("_sampleCounts", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(baseline) as System.Collections.IDictionary;
                Assert.NotNull(counts);
                Assert.Equal(seedable.Count + 2, counts!.Count);
                Assert.Equal(3, (int)counts[("sql_response_time:" + deadEndpoint).ToLowerInvariant()]!);
                Assert.Equal(3, (int)counts[(seedable[0].Id + ":" + removedServer).ToLowerInvariant()]!);

                var seed = typeof(AlertBaselineService).GetMethod("RunSeedCycleAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(seed);
                await (Task)seed!.Invoke(baseline, null)!;

                Assert.True(baseline.SeedingComplete,
                    "every pair the seeder visits holds 10 samples; the two short pairs are ones it never visits");
                Assert.Equal(seedable.Count, baseline.TotalPairCount);
                Assert.Equal(seedable.Count, baseline.SeededCount);
                Assert.Equal(1.0, baseline.SeedingProgress);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* a locked sqlite file is not this test's verdict */ }
            }
        }

        private static void SetField(object target, string field, object? value)
        {
            var f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(f != null, "the harness sets " + field + "; a rename must fail loudly");
            f!.SetValue(target, value);
        }

        private static void InvokePrivate(object target, string method)
        {
            var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(m != null, "the harness calls " + method + "; a rename must fail loudly");
            m!.Invoke(target, null);
        }

        // ── helpers (the shape AlertHonestyLaneTests established) ──

        private static AlertEvaluationService BuildEngine(SqlConnectionPoolService? pool = null)
        {
            var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
            var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);
            var cache = new liveQueriesCacheStore();

            return new AlertEvaluationService(
                NullLogger<AlertEvaluationService>.Instance,
                new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance),
                new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
                new AlertingService(NullLogger<AlertingService>.Instance),
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                new ToastService(),
                channels,
                cache,
                new InlineOrchestrator(),
                pool: pool);
        }

        private static (ServerConnection, string) DeadEndpoint()
        {
            const string deadEndpoint = "127.0.0.1,1"; // nothing listens here - refused immediately
            var connection = new ServerConnection
            {
                Id = Guid.NewGuid().ToString(),
                ServerNames = deadEndpoint,
                UseWindowsAuthentication = true,
                ConnectionTimeout = 2,
                IsEnabled = true,
            };
            return (connection, deadEndpoint);
        }

        /// <summary>Runs the work inline, exactly as the real orchestrator does on the happy path.</summary>
        private sealed class InlineOrchestrator : IQueryOrchestrator
        {
            public async Task<QueryResult> EnqueueAsync(
                QueryRequest request, QueryPriority priority, System.Threading.CancellationToken ct = default)
            {
                try
                {
                    await request.Work(ct);
                    return new QueryResult { QueryId = request.QueryId, Success = true };
                }
                catch (Exception ex)
                {
                    return new QueryResult { QueryId = request.QueryId, Success = false, Exception = ex };
                }
            }

            public Task<OrchestratorHealth> GetHealthAsync(System.Threading.CancellationToken ct = default)
                => Task.FromResult(new OrchestratorHealth());
            public Task<OrchestratorMetrics> GetMetricsAsync(System.Threading.CancellationToken ct = default)
                => Task.FromResult(new OrchestratorMetrics());
            public void UpdateLimits(int globalConcurrency, int perServerConcurrency) { }
            public void Start() { }
            public Task StopAsync(System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        }
    }
}
