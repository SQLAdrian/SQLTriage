/* In the name of God, the Merciful, the Compassionate */

// -- Alerts read cumulative counters as if they were rates, 2026-08-22 ------------------------
//
// WHY THIS FILE EXISTS. Six shipped alerts asked SQL Server for a RUNNING TOTAL and compared it
// to a per-second threshold. "SELECT cntr_value FROM sys.dm_os_performance_counters" on any
// "/sec" counter returns a PERF_COUNTER_BULK_COUNT: cntr_type 272696576, a number that only
// climbs while the service is up. Same for "SUM(wait_time_ms) FROM sys.dm_os_wait_stats". The
// live burst of 2026-08-22 18:17 is what that looks like from the client's side:
//
//     Excessive Recompilations   97,463          (Critical, threshold 50/s)
//     Excessive Page Splits      1,554,744       (Critical, threshold 500/s)
//     OS Paging                  78,684,539      (Critical, threshold 50/s)
//     Wait Time Anomaly          44,239,320,670  (Critical, threshold 15,000 ms)
//
// 44,239,320,670 ms is about 1.4 years of accrued wait. None of those numbers is the quantity
// its alert is named for, so every one of them fires permanently from the moment the service
// starts and never clears. That is the house class: an artifact stating a measurement nobody
// took.
//
// THE COUNT IS SIX, NOT FOUR. The four above are the ones that fired that evening. Grepping
// every shipped definition for a bare cntr_value read, or a SUM over dm_os_wait_stats with no
// second cumulative column to divide by, found two more of exactly the same shape:
// batch_requests (Batch Requests/sec) and deadlock_rate (Number of Deadlocks/sec). Counter types
// read live off .\NEW2022 on 2026-08-23 confirm all five perf-counter rows are cntr_type
// 272696576. Two near neighbours are NOT in the class and are deliberately left alone:
// page_life_expectancy is cntr_type 65792, an instantaneous gauge, and buffer_cache_hit already
// divides by its own base counter.
//
// THE FIX. Store the previous raw reading per alert and server, and evaluate the DIFFERENCE over
// the elapsed time. The first cycle after a start - and any cycle where the counter went
// backwards, which only happens when SQL Server restarted - produces NO measurement. Not zero,
// not the total: the cycle is skipped and the reason is logged.
//
// WHAT IS REAL HERE.
//   * Tier 1 is the arithmetic, in the production internal static that the evaluator and the
//     baseline seeder both call. Nothing here re-implements it.
//   * Tier 2 drives the REAL AlertEvaluationService - the collaborator set
//     AlertHitCountHonestyTests already proves is constructible - through ObserveAndApplyAsync,
//     which IS the production call site. A raw counter total goes in and the assertions are on
//     the service's own ActiveAlerts and on the rows its baseline sampler wrote. Handing the raw
//     value to the comparison, which is what the shipped code did, makes tier 2 fail.
//   * Tier 3 opens a database created with the OLD schema and asserts the upgrade in place.
//   * Tier 4 asserts what the shipped Config/alert-definitions.json actually says, so a seventh
//     cumulative alert added later without the marker fails the build.
//   * Tier 5 is the live probe against a real instance, inert unless RAWCOUNTER_LIVE_TARGET is
//     set.
//
// MUTATIONS RUN 2026-08-24, every one red (red/green logs under C:\temp\rawcounters\mutations\):
//   M1  hand `raw` to ApplyObservedValueAsync at the substitution call site   -> 3 red
//   M2  return `raw` when there is no previous sample                        -> 4 red
//   M3  the seeder persists `raw` instead of the computed rate               -> 1 red
//   M4  drop "valueKind" from query_recompilations in the shipped definitions -> 2 red
//   M5  remove the counter-reset guard                                       -> 1 red
//   M6  the one-shot purge stops deleting anything                           -> 1 red
//   M7  Alerts.razor goes back to a hand-written clone missing valueKind     -> 1 red
//   M8  valueKind stops being part of the alert's JSON contract              -> 3 red
// Reverted and rebuilt each time: 18 passed, 1 skipped (the live probe).
//
// LIVE, 2026-08-23T12:12Z against .\NEW2022, two samples ten seconds apart (evidence:
// C:\temp\rawcounters\live\probe-2026-08-24.txt). Five of the six now measure a real rate and
// fire nothing on an idle instance - query_recompilations 0.199/s, batch_requests 19.322/s,
// page_splits 3.685/s, os_paging 0.199/s, deadlock_rate 0.000/min - where before this change
// four of them fired Critical on every cycle. Forcing 200 recompilations between two samples
// moved that alert's rate to 174.852/s, over its critical 50, so the measurement tracks the
// counter in both directions.
//
// ONE ALERT WAS STILL NOISY. RULED AND FIXED 2026-08-24. wait_time_anomaly measured
// 23,916 ms/s against its shipped critical of 15,000 - on an IDLE instance. That was an
// honest number rather than a fabricated one (it was 44,239,320,670 before), but the threshold
// itself was too low for what it meant: 15,000 ms of accrued wait per wall-clock second is
// roughly fifteen threads waiting continuously, which any real server exceeds at rest. Retuning
// it needed a ruling, and got one: D2(b) re-based the alert onto the signal-wait ratio instead,
// which is a percentage and does not move when a client buys cores. See WaitSignalRatioAlertTests
// for the new contract, and note that this file's census is FIVE from that date, not six.

// -- FIX PASS, 2026-08-24 ---------------------------------------------------------------------
//
// THE FIX ABOVE REACHED FRESH INSTALLS ONLY. valueKind is the one switch that turns the rate
// substitution on, and it existed only in the packaged Config/alert-definitions.json. That file
// is preserved on upgrade by four independent mechanisms - installer/SQLTriage.iss ships it
// onlyifdoesntexist, AutoUpdateService.ProtectedConfigFiles copies the operator's copy back OVER
// the package's, tools/SQLTriageUpdater lists it as never overwritten, and the service deploy
// script never copies the publish config folder. So the installs that were firing the permanent
// false Criticals, which are the whole reason for this work, would have gone on firing them.
// Proved by execution against a byte copy of a real install's file, not inferred: all six read
// back IsCumulativeCounter=false and ObserveValue handed the running total straight back.
//
// AlertDefinitionMigrator closes it, on the same pattern the base commit already carried for
// script-configurations.json under ruling 2026-08-23 #4. Tier 6 below is that path end to end.
// The purge marker was NOT already spent on those installs, which is what makes the repair still
// available: PurgeStaleCumulativeBaselinesAsync returns before claiming whenever no alert is
// marked cumulative, and that ordering is now pinned by a test rather than assumed.
//
// THREE MORE, all from the same verification pass:
//   * deadlock_rate read ONE lock-resource instance, not the deadlock counter. The query had no
//     instance_name filter and no ORDER BY, and ExecuteScalarAsync takes the first row: fifteen
//     rows on .\NEW2022, the first of them Xact at 0 while Key and _Total both stood at 8. SUM
//     would double-count (_Total duplicates Key; the fifteen rows total 16 for 8 deadlocks), so
//     the filter is instance_name = '_Total'. Live before/after on .\NEW2022 2026-08-24: the
//     shipped query returned 0, it now returns 8.
//   * The unresolved alert-history rows the defect left on the client's screen survived the fix
//     and nothing in the product could clear them - ResolveAlert is reached only through a branch
//     gated on an in-memory dictionary a restart empties, and these alerts now correctly never
//     re-enter it. A real install's alert-history.db held 54 such rows. One-shot, marker-guarded,
//     before the first evaluation cycle. Tier 7.
//   * The not-measured reason claimed "SQL Server restarted", which is an inference and not what
//     was observed; a multi-row scalar changing its first row produces the same signal. It now
//     says the counter went backwards and names a restart as the usual cause.
//   * A raw sample the cache store could not take was swallowed at Debug, so a store that stays
//     unwritable makes every cumulative alert permanently silent with a UI that reads as healthy.
//     The store reports the failure and the evaluator says so at Warning. Tier 8.
//
// WAS OPEN HERE, CLOSED LATER THE SAME DAY. wait_time_anomaly's shipped critical of 15,000
// ms/s was far below what an idle instance produces: 27,167.864 ms/s measured live on .\NEW2022
// on 2026-08-24 through the production arithmetic. The number was honest; the threshold was
// not meaningful. Ruling D2(b) re-based the alert rather than retuning the number - it now reads
// the signal-wait ratio (percent, warning 30 / critical 50 from sp_PerfCheck.sql:2494-2497) and
// measured 0.04% live on .\NEW2022 on 2026-08-24. Contract in WaitSignalRatioAlertTests.
//
// MUTATIONS RUN 2026-08-24 on the fix pass, every one red (logs C:\temp\rawcounters\mutations\):
//   FM1 delete the migration call from AlertDefinitionService's constructor      -> 1 red
//   FM2 the merge overwrites a property the installed file already answers       -> 2 red
//   FM3 drop the instance_name filter from the deadlock query                    -> 1 red
//   FM4 the stranded-alert repair claims its marker and resolves nothing         -> 1 red
//   FM5 the cache store reports success on a write it could not do               -> 1 red
//   FM6 the not-measured reason states a restart as observed fact                -> 1 red
//   FM7 the shipped catalogue stops being embedded in the assembly               -> 6 red
// Reverted and rebuilt each time: 30 passed, 3 skipped (the three live probes).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public sealed class AlertCumulativeRateTests : IDisposable
    {
        private readonly string _dir;
        private readonly ITestOutputHelper _out;
        private readonly List<IDisposable> _disposables = new();

        public AlertCumulativeRateTests(ITestOutputHelper output)
        {
            _out = output;
            _dir = Path.Combine(Path.GetTempPath(), "sqlt-rawcounters-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            foreach (var d in _disposables)
            {
                try { d.Dispose(); } catch { /* best effort */ }
            }
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private static AlertDefinition Recompilations() => new()
        {
            Id = "query_recompilations",
            Name = "Excessive Recompilations",
            Unit = "per_second",
            Operator = "greater_than",
            ValueKind = AlertDefinition.CumulativeCounterKind,
            Thresholds = new AlertThresholds { Warning = 10, Critical = 50 },
        };

        // -- Tier 1: the arithmetic ------------------------------------------------

        [Fact]
        public void A_counter_that_advanced_a_little_over_five_minutes_is_a_low_rate()
        {
            var t0 = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
            var obs = AlertEvaluationService.ObserveValue(
                Recompilations(), raw: 1_000_300, prevRaw: 1_000_000, prevUtc: t0, nowUtc: t0.AddSeconds(300));

            obs.Value.Should().Be(1.0, "300 recompilations over 300 seconds is one per second");
            AlertEvaluationService.IsThresholdBreached(obs.Value!.Value, 50, "greater_than")
                .Should().BeFalse("one per second is nowhere near the critical threshold of fifty");
        }

        [Fact]
        public void The_same_counter_advancing_hard_is_a_critical_rate()
        {
            var t0 = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
            var obs = AlertEvaluationService.ObserveValue(
                Recompilations(), raw: 1_030_000, prevRaw: 1_000_000, prevUtc: t0, nowUtc: t0.AddSeconds(300));

            obs.Value.Should().Be(100.0, "30,000 recompilations over 300 seconds is a hundred per second");
            AlertEvaluationService.IsThresholdBreached(obs.Value!.Value, 50, "greater_than")
                .Should().BeTrue("a hundred per second is over the critical threshold of fifty");
        }

        [Fact]
        public void The_first_sample_is_not_a_measurement()
        {
            var obs = AlertEvaluationService.ObserveValue(
                Recompilations(), raw: 97_463, prevRaw: null, prevUtc: null,
                nowUtc: new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc));

            obs.Value.Should().BeNull("a rate needs two samples and this is the first");
            obs.NotMeasuredReason.Should().Contain("first sample");
        }

        [Fact]
        public void A_counter_that_went_backwards_is_a_restart_not_a_negative_rate()
        {
            var t0 = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
            var obs = AlertEvaluationService.ObserveValue(
                Recompilations(), raw: 42, prevRaw: 1_000_000, prevUtc: t0, nowUtc: t0.AddSeconds(300));

            obs.Value.Should().BeNull("the counter reset, so the difference means nothing");
            obs.NotMeasuredReason.Should().Contain("counter reset");

            // What was observed is that the counter went backwards. A restart is the usual
            // explanation and not the only one - the deadlock counter is read from a multi-row
            // result, and a first row that changes identity between samples produces exactly this
            // signal - so the reason line may not state a restart as a fact about the server.
            obs.NotMeasuredReason.Should().Contain("lower than the previous sample");
            obs.NotMeasuredReason.Should().NotContain("SQL Server restarted",
                "that is an inference about the server, not something this code observed");
        }

        [Fact]
        public void No_elapsed_time_yields_no_rate_rather_than_an_infinity()
        {
            var t0 = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
            foreach (var now in new[] { t0, t0.AddSeconds(-30) })
            {
                var obs = AlertEvaluationService.ObserveValue(
                    Recompilations(), raw: 1_000_300, prevRaw: 1_000_000, prevUtc: t0, nowUtc: now);
                obs.Value.Should().BeNull(
                    "dividing by {0} seconds would fabricate a rate", (now - t0).TotalSeconds);
            }
        }

        [Fact]
        public void An_alert_that_is_not_a_cumulative_counter_is_untouched()
        {
            var cpu = new AlertDefinition { Id = "sql_cpu", Unit = "percent", Operator = "greater_than" };
            var obs = AlertEvaluationService.ObserveValue(
                cpu, raw: 97, prevRaw: null, prevUtc: null, nowUtc: DateTime.UtcNow);

            obs.Value.Should().Be(97, "a percentage is already the quantity its threshold describes");
            obs.NotMeasuredReason.Should().BeNull();
        }

        [Fact]
        public void A_per_minute_threshold_is_compared_to_a_per_minute_rate()
        {
            var deadlocks = new AlertDefinition
            {
                Id = "deadlock_rate",
                Unit = "per_minute",
                Operator = "greater_than",
                ValueKind = AlertDefinition.CumulativeCounterKind,
            };
            var t0 = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

            AlertEvaluationService.RateWindowSeconds(deadlocks).Should().Be(60);
            AlertEvaluationService.RateWindowSeconds(Recompilations()).Should().Be(1);

            // Six deadlocks in two minutes is three a minute: this definition's warning number.
            var obs = AlertEvaluationService.ObserveValue(
                deadlocks, raw: 106, prevRaw: 100, prevUtc: t0, nowUtc: t0.AddSeconds(120));
            obs.Value.Should().Be(3.0, "the declared unit is per_minute, so the rate is per minute");
        }

        // -- Tier 2: the real service, from a raw counter reading -------------------

        private (AlertEvaluationService svc, AlertBaselineService baseline, liveQueriesCacheStore cache)
            RealService()
        {
            var b = Build(File.ReadAllText(ShippedDefinitionsPath()), dryRun: true);
            return (b.svc, b.baseline, b.cache);
        }

        /// <summary>
        /// The same collaborator set, over a catalogue the caller chooses. Tier 6 and tier 7 need
        /// definitions that are NOT the shipped ones - an install that predates the marker, and a
        /// pair of alerts whose ids cannot collide with another test's rows in the shared
        /// alert-history database.
        /// </summary>
        private (AlertEvaluationService svc, AlertBaselineService baseline,
                 liveQueriesCacheStore cache, AlertHistoryService history)
            Build(string definitionsJson, bool dryRun)
        {
            var run = Guid.NewGuid().ToString("N").Substring(0, 8);

            var cache = new liveQueriesCacheStore();
            _disposables.Add(cache);
            Repoint(cache, "_connectionString",
                "Data Source=" + Path.Combine(_dir, "cache-" + run + ".db") + ";Mode=ReadWriteCreate;");
            Invoke(cache, "InitializeSchema");

            var localDefs = Path.Combine(_dir, "alert-definitions-" + run + ".json");
            File.WriteAllText(localDefs, definitionsJson);
            var definitions = new AlertDefinitionService(
                NullLogger<AlertDefinitionService>.Instance, localDefs);

            var settings = new UserSettingsService(Path.Combine(_dir, "user-settings.json"));
            settings.SetAlertBaselineEnabled(true);
            settings.SetAlertBaselinePerServer(false);

            var baseline = new AlertBaselineService(
                NullLogger<AlertBaselineService>.Instance,
                definitions,
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                cache,
                settings);
            _disposables.Add(baseline);

            var templates = new AlertTemplateService(
                NullLogger<AlertTemplateService>.Instance, Path.Combine(_dir, "alert-templates.json"));

            var history = new AlertHistoryService(NullLogger<AlertHistoryService>.Instance);
            _disposables.Add(history);

            var svc = new AlertEvaluationService(
                NullLogger<AlertEvaluationService>.Instance,
                definitions,
                history,
                new AlertingService(NullLogger<AlertingService>.Instance),
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                new ToastService(),
                new NotificationChannelService(
                    NullLogger<NotificationChannelService>.Instance, templates),
                cache,
                new NullOrchestrator(),
                baseline)
            {
                DryRun = dryRun,
            };
            _disposables.Add(svc);

            return (svc, baseline, cache, history);
        }

        /// <summary>
        /// The whole defect in one test. A cumulative counter standing at 97,463 - the live
        /// 2026-08-22 reading - goes into the REAL service. Before this change that number was
        /// compared to the threshold of 50 and fired Critical on the spot, every cycle, for as
        /// long as the service stayed up. After it, the first cycle measures nothing, and the
        /// second cycle five minutes later measures a rate of one per second.
        /// </summary>
        [Fact]
        public async Task The_service_does_not_fire_on_a_raw_counter_total()
        {
            var (svc, _, _) = RealService();
            var alert = Recompilations();
            var t0 = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

            await svc.ObserveAndApplyAsync(alert, ".\\NEW2022", raw: 97_463, nowUtc: t0);

            svc.ActiveAlerts.Should().BeEmpty(
                "97,463 is a since-startup total, not 97,463 recompilations per second; with no "
                + "previous sample this cycle measured nothing and must not have fired");

            // Five minutes later the counter has advanced by 300: one a second, well under 50.
            await svc.ObserveAndApplyAsync(alert, ".\\NEW2022", raw: 97_763, nowUtc: t0.AddSeconds(300));

            svc.ActiveAlerts.Should().BeEmpty("one recompilation per second is not a Critical");
        }

        [Fact]
        public async Task The_service_still_fires_when_the_rate_itself_is_critical()
        {
            var (svc, _, _) = RealService();
            var alert = Recompilations();
            var t0 = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

            await svc.ObserveAndApplyAsync(alert, ".\\NEW2022", raw: 97_463, nowUtc: t0);
            await svc.ObserveAndApplyAsync(alert, ".\\NEW2022", raw: 127_463, nowUtc: t0.AddSeconds(300));

            var fired = svc.ActiveAlerts.Should().ContainSingle(
                "30,000 recompilations in 300 seconds is a hundred a second, over the critical 50")
                .Subject;

            fired.Severity.Should().Be("Critical");
            fired.LastValue.Should().Be(100.0, "the alert must carry the rate, never the raw total");
            fired.Message.Should().Contain("100.0/s", "the rendered value must name the rate unit");
            fired.Message.Should().NotContain("127", "no raw counter total may reach the client");
        }

        /// <summary>
        /// The learned baseline must be taught the rate. A baseline built from since-startup
        /// totals produces fences millions above any real rate, so a genuine breach would never
        /// fire; and because a total only ever climbs, it also produces a permanent "rising
        /// trend", which the evaluator promotes to Critical without consulting the value at all.
        /// </summary>
        [Fact]
        public async Task The_learned_baseline_is_taught_the_rate_and_never_the_total()
        {
            var (svc, _, cache) = RealService();
            var alert = Recompilations();
            var t0 = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

            await svc.ObserveAndApplyAsync(alert, ".\\NEW2022", raw: 97_463, nowUtc: t0);
            await svc.ObserveAndApplyAsync(alert, ".\\NEW2022", raw: 97_763, nowUtc: t0.AddSeconds(300));

            var samples = await WaitForSamplesAsync(cache, alert.Id);
            samples.Should().ContainSingle("only the second cycle produced a measurement");
            samples[0].Should().Be(1.0, "the sampler must receive the rate, not the counter total");
        }

        /// <summary>
        /// The baseline SEEDER is a second, independent path into the same sample table: its own
        /// timer, its own connection, running the same query every 15 s. Fixing only the
        /// evaluator would have left it feeding since-startup totals into the fences the
        /// evaluator then reads back. This drives its real recording call site from a raw
        /// counter reading.
        /// </summary>
        [Fact]
        public async Task The_baseline_seeder_stores_the_rate_and_never_the_total()
        {
            var (_, baseline, cache) = RealService();
            var alert = Recompilations();
            var t0 = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

            await baseline.RecordSeedObservationAsync(alert, ".\\NEW2022", raw: 97_463, nowUtc: t0);
            ReadSamples(cache, alert.Id).Should().BeEmpty(
                "the seeder's first reading of a cumulative counter is a total, not a sample");

            await baseline.RecordSeedObservationAsync(
                alert, ".\\NEW2022", raw: 97_763, nowUtc: t0.AddSeconds(300));

            var samples = ReadSamples(cache, alert.Id);
            samples.Should().ContainSingle();
            samples[0].Should().Be(1.0,
                "the seeder must persist the rate; a fence learned from 97,763 would sit so far "
                + "above any real rate that a genuine breach could never reach it");
        }

        private static async Task<List<double>> WaitForSamplesAsync(
            liveQueriesCacheStore cache, string alertId)
        {
            // RecordSample persists on a discarded task, so poll rather than assume.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var found = ReadSamples(cache, alertId);
            while (found.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
                found = ReadSamples(cache, alertId);
            }
            return found;
        }

        private static List<double> ReadSamples(liveQueriesCacheStore cache, string alertId)
        {
            var values = new List<double>();
            using var conn = cache.CreateExternalConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT value FROM alert_baseline_samples WHERE alert_id = @a ORDER BY sampled_at";
            cmd.Parameters.AddWithValue("@a", alertId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) values.Add(reader.GetDouble(0));
            return values;
        }

        // -- Tier 3: an old database upgrades in place ------------------------------

        [Fact]
        public void A_database_written_before_this_change_gains_the_tables_and_keeps_its_rows()
        {
            var path = Path.Combine(_dir, "old-schema.db");
            using var conn = new SqliteConnection("Data Source=" + path + ";Mode=ReadWriteCreate;");
            conn.Open();

            using (var create = conn.CreateCommand())
            {
                create.CommandText = @"
                    CREATE TABLE alert_baseline_samples (
                        alert_id TEXT NOT NULL, server_name TEXT NOT NULL, sampled_at TEXT NOT NULL,
                        value REAL NOT NULL, hour_of_day INTEGER NOT NULL, day_of_week INTEGER NOT NULL,
                        fetched_at TEXT NOT NULL,
                        PRIMARY KEY (alert_id, server_name, sampled_at));";
                create.ExecuteNonQuery();
            }

            using (var seed = conn.CreateCommand())
            {
                seed.CommandText = @"INSERT INTO alert_baseline_samples VALUES
                    ('sql_cpu', 'SRV', '2026-08-01T00:00:00.0000000Z', 41.5, 0, 6,
                     '2026-08-01T00:00:00.0000000Z');";
                seed.ExecuteNonQuery();
            }

            TableExists(conn, "alert_raw_samples").Should().BeFalse("this is the old schema");
            TableExists(conn, "alert_schema_markers").Should().BeFalse("this is the old schema");

            liveQueriesCacheStore.MigrateAlertRawSamples(conn);
            liveQueriesCacheStore.MigrateAlertSchemaMarkers(conn);

            TableExists(conn, "alert_raw_samples").Should().BeTrue();
            TableExists(conn, "alert_schema_markers").Should().BeTrue();

            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT value FROM alert_baseline_samples WHERE alert_id = 'sql_cpu'";
                Convert.ToDouble(check.ExecuteScalar()).Should().Be(41.5, "the existing row must survive");
            }

            // Idempotent: running the upgrade again on the upgraded database changes nothing.
            liveQueriesCacheStore.MigrateAlertRawSamples(conn);
            liveQueriesCacheStore.MigrateAlertSchemaMarkers(conn);
            TableExists(conn, "alert_raw_samples").Should().BeTrue();
        }

        [Fact]
        public async Task A_one_shot_marker_is_claimable_exactly_once()
        {
            var cache = new liveQueriesCacheStore();
            _disposables.Add(cache);
            Repoint(cache, "_connectionString",
                "Data Source=" + Path.Combine(_dir, "marker.db") + ";Mode=ReadWriteCreate;");
            Invoke(cache, "InitializeSchema");

            (await cache.TryClaimSchemaMarkerAsync("some-repair")).Should().BeTrue("nobody has claimed it");
            (await cache.TryClaimSchemaMarkerAsync("some-repair")).Should().BeFalse("it is already claimed");
            (await cache.TryClaimSchemaMarkerAsync("a-different-repair")).Should().BeTrue();
        }

        [Fact]
        public async Task The_stale_total_based_baseline_is_purged_once_and_then_left_alone()
        {
            var (_, baseline, cache) = RealService();

            SeedStaleRows(cache, "query_recompilations", 1_554_744);
            SeedStaleRows(cache, "sql_cpu", 41.5);

            await baseline.PurgeStaleCumulativeBaselinesAsync();

            ReadSamples(cache, "query_recompilations").Should().BeEmpty(
                "those samples were since-startup totals, a different quantity from the rate this "
                + "alert now records, and a rising series of totals is a permanent fake trend");
            ReadStatsCount(cache, "query_recompilations").Should().Be(0,
                "the fences learned from totals must go with them");
            ReadSamples(cache, "sql_cpu").Should().ContainSingle(
                "a percentage alert is not in this class and must keep its baseline");

            // Second run: the marker is claimed, so a legitimately re-learned baseline survives.
            SeedStaleRows(cache, "query_recompilations", 3.0);
            await baseline.PurgeStaleCumulativeBaselinesAsync();
            ReadSamples(cache, "query_recompilations").Should().ContainSingle(
                "the purge is one-shot; it must not wipe the baseline on every start");
        }

        private static void SeedStaleRows(liveQueriesCacheStore cache, string alertId, double value)
        {
            using var conn = cache.CreateExternalConnection();
            conn.Open();

            using (var s = conn.CreateCommand())
            {
                s.CommandText = @"INSERT OR REPLACE INTO alert_baseline_samples
                    (alert_id, server_name, sampled_at, value, hour_of_day, day_of_week, fetched_at)
                    VALUES (@a, 'SRV', '2026-08-01T00:00:00.0000000Z', @v, 0, 6,
                            '2026-08-01T00:00:00.0000000Z')";
                s.Parameters.AddWithValue("@a", alertId);
                s.Parameters.AddWithValue("@v", value);
                s.ExecuteNonQuery();
            }

            using (var t = conn.CreateCommand())
            {
                t.CommandText = @"INSERT OR REPLACE INTO alert_baseline_stats
                    (alert_id, server_name, sample_count, p25, p50, p75, p95, iqr,
                     threshold_warn, threshold_crit, last_computed)
                    VALUES (@a, 'SRV', 40, @v, @v, @v, @v, 0, @v, @v, '2026-08-01T00:00:00.0000000Z')";
                t.Parameters.AddWithValue("@a", alertId);
                t.Parameters.AddWithValue("@v", value);
                t.ExecuteNonQuery();
            }
        }

        private static long ReadStatsCount(liveQueriesCacheStore cache, string alertId)
        {
            using var conn = cache.CreateExternalConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM alert_baseline_stats WHERE alert_id = @a";
            cmd.Parameters.AddWithValue("@a", alertId);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        private static bool TableExists(SqliteConnection conn, string name)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
            cmd.Parameters.AddWithValue("@n", name);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }

        // -- Tier 4: the shipped definitions ----------------------------------------

        /// <summary>
        /// The census that makes a seventh one impossible to add quietly. Every shipped
        /// definition whose SQL reads a bare cntr_value, or sums wait_time_ms over
        /// dm_os_wait_stats with no second cumulative column to divide by, must carry the marker;
        /// nothing else may.
        /// </summary>
        [Fact]
        public void Exactly_the_cumulative_queries_carry_the_marker()
        {
            var alerts = ShippedAlerts();

            // FIVE, not six. wait_time_anomaly left this class on 2026-08-24 under ruling D2(b)
            // (DECISIONS 2026-08-24, research/DESIGN-2026-08-24-app-wait-verdict-and-system-health.md
            // section 4.4): its query was re-based from SUM(wait_time_ms) - a running total whose
            // ms/s threshold scales with core count - onto the signal-wait RATIO, signal over total.
            // A ratio of two cumulative columns is already scale-free, so it must NOT be differenced
            // between samples; that is the same reason buffer_cache_hit has never carried the marker.
            var expected = new[]
            {
                "batch_requests", "deadlock_rate", "os_paging",
                "page_splits", "query_recompilations",
            };

            var marked = alerts.Where(a => a.IsCumulativeCounter).Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            marked.Should().Equal(expected,
                "these are the shipped queries that return a running total rather than a rate");

            var suspects = alerts.Where(LooksCumulative).Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            suspects.Should().Equal(expected,
                "a definition whose SQL reads a raw cumulative counter must carry the marker; if "
                + "this fails, an alert was added in the same shape and nobody marked it");

            foreach (var a in alerts.Where(a => a.IsCumulativeCounter))
            {
                a.QueryMode.Should().BeNull(
                    "the rate substitution lives on the query path; a queryMode alert would skip it");
                a.Query.Should().NotBeNullOrWhiteSpace();
            }

            // The two near neighbours that are NOT in the class, named so a later reader can see
            // the line was drawn deliberately rather than missed.
            alerts.Single(a => a.Id == "page_life_expectancy").IsCumulativeCounter.Should().BeFalse(
                "PLE is cntr_type 65792, an instantaneous gauge, read live off .\\NEW2022");
            alerts.Single(a => a.Id == "buffer_cache_hit").IsCumulativeCounter.Should().BeFalse(
                "that query already divides by its own base counter");

            // The third near neighbour, and the one that MOVED. Named here so the 6-to-5 change
            // reads as a decision rather than a deletion.
            var wait = alerts.Single(a => a.Id == "wait_time_anomaly");
            wait.IsCumulativeCounter.Should().BeFalse(
                "D2(b): signal over total is a ratio of two cumulative columns, so it is scale-free "
                + "and must not be differenced - marking it would make the evaluator compare the "
                + "CHANGE in a percentage against a percentage threshold");
            wait.Unit.Should().Be("percent",
                "the re-based query returns a percentage, and the unit is what FormatMessage prints");
            wait.Thresholds.Warning.Should().Be(30);
            wait.Thresholds.Critical.Should().Be(50);
            wait.Query.Should().Contain("signal_wait_time_ms",
                "the whole point of D2(b) is that the alert reads the signal column");
            wait.Query!.Replace(" ", string.Empty).ToLowerInvariant()
                .Should().Contain("/nullif(sum(wait_time_ms)",
                    "the divisor is what makes it a ratio; drop it and LooksCumulative above is "
                    + "entitled to demand the marker back");
        }

        /// <summary>
        /// A perf-counter read has to name the instance it counts. sys.dm_os_performance_counters
        /// returns ONE ROW PER INSTANCE for a multi-instance object, ExecuteScalarAsync takes the
        /// first row, and the shipped deadlock query had no instance_name filter and no ORDER BY,
        /// so it read whichever row the engine happened to return first. Proved live on
        /// .\NEW2022 on 2026-08-24: fifteen rows for "Number of Deadlocks/sec", the first of them
        /// Xact at 0 while Key and _Total both stood at 8. The alert reported no deadlocks while
        /// eight had happened.
        ///
        /// <para>SUM would be wrong, not merely different: _Total duplicates the per-resource
        /// rows, so the fifteen rows sum to 16 for 8 deadlocks. _Total is the discriminator.</para>
        ///
        /// <para>Differencing made it worse than under-reporting. If the first row changes
        /// identity between two samples the difference is meaningless, and a smaller second
        /// reading routes to the counter-reset branch.</para>
        /// </summary>
        [Fact]
        public void Every_multi_instance_counter_read_names_the_instance_it_counts()
        {
            var deadlocks = ShippedAlerts().Single(a => a.Id == "deadlock_rate");

            deadlocks.Query.Should().Contain("instance_name = '_Total'",
                "without it this reads one lock-resource type - Xact, which is 0 while Key and "
                + "_Total stand at 8 - and calls the answer the deadlock count");

            // The other four perf-counter alerts read single-instance objects (SQL Statistics,
            // Access Methods, Buffer Manager): one row each on .\NEW2022. The live tier asserts
            // the row count per run rather than trusting this comment.
            foreach (var a in ShippedAlerts().Where(a => a.IsCumulativeCounter
                                                         && a.Query.Contains("dm_os_performance_counters")))
            {
                a.Query.Should().Contain("counter_name",
                    "{0} must name the counter it reads, or the first row decides", a.Id);
            }
        }

        /// <summary>
        /// Reads a shipped query and says whether it returns a running total.
        ///
        /// <para>The discriminator for a perf-counter read is the COUNTER NAME, not the SQL: a
        /// Windows counter whose name ends "/sec" is a PERF_COUNTER_BULK_COUNT, and a
        /// PERF_COUNTER_BULK_COUNT is a running total that the consumer is expected to
        /// difference. That is why "Page life expectancy" - a bare cntr_value read with no
        /// division, and so indistinguishable from the others by SQL shape alone - is correctly
        /// excluded: no "/sec" in the name, and cntr_type 65792 when read live. All five "/sec"
        /// rows here read back 272696576 on .\NEW2022 on 2026-08-23.</para>
        /// </summary>
        private static bool LooksCumulative(AlertDefinition a)
        {
            var q = (a.Query ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
            if (q.Length == 0) return false;

            // A bare cntr_value projection of a "/sec" counter, with no base-counter division.
            if (q.Contains("selectcntr_value") && q.Contains("/sec'") && !q.Contains("/nullif"))
                return true;

            // A wait-stats total that is not divided by a second cumulative column.
            // tempdb_contention divides wait_time_ms by waiting_tasks_count, so it is a mean.
            if (q.Contains("sum(wait_time_ms)") && !q.Contains("/nullif")) return true;

            return false;
        }

        /// <summary>
        /// The edit round-trip must not be able to delete the marker.
        ///
        /// <para>Alerts.razor clones a definition to edit it, SaveEdit hands the clone to
        /// AlertDefinitionService.UpdateAlert, and UpdateAlert REPLACES the stored definition with
        /// the clone and writes the file. So any property the clone drops is deleted from disk the
        /// first time an operator opens that alert and presses Save - which for valueKind would
        /// mean six alerts silently going back to comparing a running total against a per-second
        /// threshold, with no code change and nothing in the log.
        /// </para>
        ///
        /// <para>This asserts the general property, not just valueKind: every settable property of
        /// AlertDefinition that carries a JSON name survives the round-trip with a distinctive
        /// value. It is written this way because the hand-written clone it replaced had ALREADY
        /// lost canBaseline and requiresOnPrem, and a test naming only the field of the day would
        /// have passed over both.</para>
        /// </summary>
        [Fact]
        public void Editing_an_alert_cannot_silently_drop_any_of_its_properties()
        {
            var original = new AlertDefinition
            {
                Id = "query_recompilations",
                ValueKind = AlertDefinition.CumulativeCounterKind,
                CanBaseline = true,
                RequiresOnPrem = true,
                Unit = "per_second",
                Operator = "greater_than",
                Thresholds = new AlertThresholds { Warning = 10, Critical = 50 },
            };

            // The clone the page makes, by the same mechanism the page uses.
            var clone = JsonSerializer.Deserialize<AlertDefinition>(JsonSerializer.Serialize(original))!;

            clone.IsCumulativeCounter.Should().BeTrue(
                "an edited alert that loses its marker goes straight back to firing on a raw total");
            clone.CanBaseline.Should().BeTrue();
            clone.RequiresOnPrem.Should().BeTrue();
            clone.Thresholds.Critical.Should().Be(50);

            // The general form: no JSON-mapped property may be lost, whatever gets added later.
            var carried = JsonSerializer.Deserialize<AlertDefinition>(
                JsonSerializer.Serialize(ShippedAlerts().Single(a => a.Id == "wait_time_anomaly")))!;
            var source = ShippedAlerts().Single(a => a.Id == "wait_time_anomaly");

            foreach (var p in typeof(AlertDefinition).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.CanRead && p.CanWrite
                                     && p.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>() != null))
            {
                var before = p.GetValue(source);
                var after = p.GetValue(carried);
                if (before is AlertThresholds bt && after is AlertThresholds at)
                {
                    at.Warning.Should().Be(bt.Warning, "thresholds.warning must survive an edit");
                    at.Critical.Should().Be(bt.Critical, "thresholds.critical must survive an edit");
                    continue;
                }
                after.Should().BeEquivalentTo(before,
                    "{0} must survive the edit round-trip; UpdateAlert replaces the stored "
                    + "definition with this object and writes the file", p.Name);
            }
        }

        /// <summary>
        /// The test above proves the round-trip CARRIES everything. This proves the page still
        /// USES it, which is a different claim and the one that actually protects the file on
        /// disk: a future hand-written property list in EditAlert would pass the test above
        /// untouched, because that test re-does the serialisation rather than calling the page.
        /// Asserted against the razor source, so it is a lint and says so.
        /// </summary>
        [Fact]
        public void The_alerts_page_still_clones_through_the_json_contract()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln")))
                dir = dir.Parent;
            dir.Should().NotBeNull("this lint reads app source, so it needs the repo root");

            var path = Path.Combine(dir!.FullName, "Pages", "Alerts.razor");
            File.Exists(path).Should().BeTrue();
            var source = File.ReadAllText(path);

            var start = source.IndexOf("private void EditAlert(", StringComparison.Ordinal);
            start.Should().BeGreaterThan(0, "EditAlert is the clone site under test");
            var end = source.IndexOf("private void SaveEdit(", start, StringComparison.Ordinal);
            end.Should().BeGreaterThan(start);

            var body = source.Substring(start, end - start);

            body.Should().Contain("Deserialize<AlertDefinition>",
                "the clone must go through the alert's own JSON contract");
            body.Should().NotContain("new AlertDefinition",
                "a hand-written property list here silently deletes from the saved file every "
                + "property it forgets; it had already forgotten canBaseline and requiresOnPrem, "
                + "and forgetting valueKind would return six alerts to firing on a raw total");
        }

        [Fact]
        public void Every_cumulative_alert_renders_its_value_with_a_rate_unit()
        {
            foreach (var a in ShippedAlerts().Where(a => a.IsCumulativeCounter))
            {
                var message = AlertEvaluationService.FormatMessage(
                    a, "SRV", 3.0, FiringBasis.Fixed(a.Thresholds.Critical ?? 1), "Critical");

                // The suffix is appended to whatever the definition already declared, so
                // page_splits (declared unit "count") renders "3.0count/s". wait_time_anomaly used
                // to be the example here; it left this class under D2(b) on 2026-08-24 and now
                // declares unit "percent" with no rate suffix at all.
                message.Should().MatchRegex(@"3\.0[A-Za-z ]*(/s|/min)\b",
                    "{0} measures a rate now, so its rendered value must say so", a.Id);
            }
        }

        private static List<AlertDefinition> ShippedAlerts()
        {
            var file = JsonSerializer.Deserialize<AlertDefinitionsFile>(
                File.ReadAllText(ShippedDefinitionsPath()));
            file.Should().NotBeNull();
            file!.Alerts.Should().NotBeEmpty();
            return file.Alerts;
        }

        private static string ShippedDefinitionsPath()
        {
            var baseDir = AppContext.BaseDirectory;
            foreach (var folder in new[] { "config", "Config" })
            {
                var candidate = Path.Combine(baseDir, folder, "alert-definitions.json");
                if (File.Exists(candidate)) return candidate;
            }

            throw new FileNotFoundException(
                "The shipped alert definitions must be beside the test assembly: " + baseDir);
        }


        // -- Tier 6: the upgrade path -----------------------------------------------
        //
        // The blocking defect found on 2026-08-24, and the reason this tier exists. valueKind is
        // the ONLY switch that turns the rate substitution on, and it lived only in the packaged
        // Config/alert-definitions.json. That file is preserved on upgrade by four independent
        // mechanisms - the Inno installer ships it onlyifdoesntexist,
        // AutoUpdateService.ProtectedConfigFiles copies the operator's copy back OVER the
        // package's, tools/SQLTriageUpdater lists it as never overwritten, and the service deploy
        // script never copies the publish config folder. So on every install that already exists,
        // which is exactly the population firing the permanent false Criticals, the marker never
        // arrived and the six alerts went on comparing a since-startup total to a per-second
        // threshold. Proved by execution against a byte copy of a real install's file, not
        // inferred. AlertDefinitionMigrator closes it on the same pattern as
        // ScriptConfigurationMigrator (ruling 2026-08-23 #4).

        /// <summary>
        /// An installed file that predates the marker is the shipped catalogue with the valueKind
        /// lines never written. This drives the whole path: the raw total straight through the
        /// production arithmetic BEFORE the migration, the rate AFTER it, on definitions read off
        /// the file on disk.
        /// </summary>
        [Fact]
        public void An_upgraded_install_gets_the_marker_and_stops_reading_totals_as_rates()
        {
            var path = Path.Combine(_dir, "installed-alert-definitions.json");
            File.WriteAllText(path, WithoutValueKind(File.ReadAllText(ShippedDefinitionsPath())));

            var t0 = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

            // Before: this IS what a real upgraded install does with a page-splits reading.
            var before = LoadAlerts(path).Single(a => a.Id == "page_splits");
            before.IsCumulativeCounter.Should().BeFalse("the marker never reached this install");
            AlertEvaluationService.ObserveValue(before, 1_957_894, 1_957_857, t0, t0.AddSeconds(10))
                .Value.Should().Be(1_957_894,
                    "without the marker the running total is handed to the comparison unchanged, "
                    + "which is the defect this lane exists to remove");

            var added = AlertDefinitionMigrator.EnsureShippedProperties(path, NullLogger.Instance);
            added.Should().HaveCount(5, "five shipped alerts carry the marker: {0}", string.Join(", ", added));

            var after = LoadAlerts(path).Single(a => a.Id == "page_splits");
            after.IsCumulativeCounter.Should().BeTrue();
            AlertEvaluationService.ObserveValue(after, 1_957_894, 1_957_857, t0, t0.AddSeconds(10))
                .Value!.Value.Should().BeApproximately(3.7, 0.0001,
                    "37 page splits over ten seconds is 3.7 a second");

            LoadAlerts(path).Where(a => a.IsCumulativeCounter).Select(a => a.Id)
                .OrderBy(x => x, StringComparer.Ordinal).Should().Equal(
                    "batch_requests", "deadlock_rate", "os_paging",
                    "page_splits", "query_recompilations");
        }

        /// <summary>
        /// The loader must run the migration itself. Every other test in this tier would still
        /// pass with the call deleted from AlertDefinitionService, and the upgraded install would
        /// still be broken, so this drives the production constructor against a file on disk.
        /// </summary>
        [Fact]
        public void The_definitions_loader_runs_the_migration_before_it_reads()
        {
            var path = Path.Combine(_dir, "loader-alert-definitions.json");
            File.WriteAllText(path, WithoutValueKind(File.ReadAllText(ShippedDefinitionsPath())));

            var service = new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance, path);

            service.GetAlert("query_recompilations")!.IsCumulativeCounter.Should().BeTrue(
                "the loader is the only thing an upgraded install runs; if the migration is not "
                + "called here the marker never arrives and five alerts keep firing on raw totals");
            service.GetAllAlerts().Count(a => a.IsCumulativeCounter).Should().Be(5);
        }

        /// <summary>
        /// The purge must not spend its one-shot marker on a run that had nothing to purge.
        ///
        /// <para>This is what kept the repair available. Every install that predates the marker
        /// started the baseline service with no alert marked cumulative, so the purge ran, found
        /// nothing, and returned BEFORE claiming - which is why the marker is still there to be
        /// claimed on the first start after the migration delivers valueKind. Reorder those two
        /// lines and the poisoned baselines survive the upgrade forever, with is_trend_critical
        /// set, and the evaluator promotes a trend-critical without consulting the value at all.</para>
        /// </summary>
        [Fact]
        public async Task The_purge_marker_is_not_burned_by_a_run_that_had_nothing_to_purge()
        {
            var nothingCumulative = new AlertDefinitionsFile
            {
                Alerts =
                {
                    new AlertDefinition
                    {
                        Id = "sql_cpu", Name = "CPU", Unit = "percent", Operator = "greater_than",
                        Thresholds = new AlertThresholds { Warning = 80, Critical = 95 },
                    },
                },
            };

            var b = Build(
                JsonSerializer.Serialize(nothingCumulative, new JsonSerializerOptions { WriteIndented = true }),
                dryRun: true);

            SeedStaleRows(b.cache, "sql_cpu", 41.5);
            await b.baseline.PurgeStaleCumulativeBaselinesAsync();

            ReadSamples(b.cache, "sql_cpu").Should().ContainSingle(
                "no alert is marked cumulative, so there is nothing to purge");
            (await b.cache.TryClaimSchemaMarkerAsync(AlertBaselineService.CumulativePurgeMarker))
                .Should().BeTrue(
                    "the purge must not claim its marker on a run that did nothing, or an install "
                    + "that gets its first cumulative alert later would find the repair spent");
        }

        [Fact]
        public void The_migration_never_changes_a_value_the_installed_file_already_holds()
        {
            var path = Path.Combine(_dir, "edited-alert-definitions.json");
            var installed = WithoutValueKind(File.ReadAllText(ShippedDefinitionsPath()))
                // An operator's answer, however odd, is an answer.
                .Replace("\"id\": \"page_splits\",",
                         "\"id\": \"page_splits\", \"valueKind\": \"something_else\",", StringComparison.Ordinal)
                // A property no model of ours carries. AlertDefinitionService.Save drops one of
                // these on the next UI save; this migration must not.
                .Replace("\"id\": \"os_paging\",",
                         "\"id\": \"os_paging\", \"operatorNote\": \"keep me\",", StringComparison.Ordinal);
            File.WriteAllText(path, installed);

            var added = AlertDefinitionMigrator.EnsureShippedProperties(path, NullLogger.Instance);
            added.Should().HaveCount(4, "page_splits already had an answer: {0}", string.Join(", ", added));

            var alerts = JsonNode.Parse(File.ReadAllText(path))!.AsObject()["alerts"]!.AsArray();

            Alert(alerts, "page_splits")["valueKind"]!.GetValue<string>().Should().Be("something_else",
                "a value already in the installed file is never a second opinion for this merge");
            Alert(alerts, "os_paging")["operatorNote"]!.GetValue<string>().Should().Be("keep me",
                "the merge carries properties the typed model does not model");
            Alert(alerts, "os_paging")["valueKind"]!.GetValue<string>()
                .Should().Be(AlertDefinition.CumulativeCounterKind);
        }

        [Fact]
        public void The_operators_own_edits_survive_the_migration()
        {
            var path = Path.Combine(_dir, "operator-alert-definitions.json");

            // Two edits an operator can actually make through the product, on an alert in the
            // class being migrated: turn it off, and move its critical threshold.
            var root = JsonNode.Parse(WithoutValueKind(File.ReadAllText(ShippedDefinitionsPath())))!.AsObject();
            var target = Alert(root["alerts"]!.AsArray(), "os_paging");
            target["enabled"] = false;
            target["thresholds"]!["critical"] = 1234;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            AlertDefinitionMigrator.EnsureShippedProperties(path, NullLogger.Instance)
                .Should().HaveCount(5);

            var reloaded = LoadAlerts(path).Single(a => a.Id == "os_paging");
            reloaded.Enabled.Should().BeFalse("the operator turned this alert off");
            reloaded.Thresholds.Critical.Should().Be(1234, "the operator set this number");
            reloaded.IsCumulativeCounter.Should().BeTrue("and the marker still arrived");
        }

        [Fact]
        public void The_migration_writes_once_and_then_leaves_the_file_alone()
        {
            var path = Path.Combine(_dir, "idempotent-alert-definitions.json");
            File.WriteAllText(path, WithoutValueKind(File.ReadAllText(ShippedDefinitionsPath())));

            AlertDefinitionMigrator.EnsureShippedProperties(path, NullLogger.Instance).Should().HaveCount(5);
            var afterFirst = File.ReadAllBytes(path);

            AlertDefinitionMigrator.EnsureShippedProperties(path, NullLogger.Instance).Should().BeEmpty(
                "everything shipped is already there, so there is nothing to add");
            File.ReadAllBytes(path).Should().Equal(afterFirst,
                "a migration that rewrites the file on every start is a migration that can lose it");
        }

        [Fact]
        public void A_definitions_file_the_migration_cannot_read_is_announced_and_left_alone()
        {
            var unreadable = new[]
            {
                "{ this is not json",
                "[]",                                     // an array, not the definitions object
                "{ \"version\": \"1.0\" }",               // no alerts array
                "{ \"alerts\": [] /* a comment */ }",     // the app's reader allows these; this does not
            };

            foreach (var content in unreadable)
            {
                var path = Path.Combine(_dir, "bad-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(path, content);

                AlertDefinitionMigrator.EnsureShippedProperties(path, NullLogger.Instance).Should().BeEmpty();
                File.ReadAllText(path).Should().Be(content,
                    "an unreadable store is announced, never replaced with the shipped defaults");
            }

            var missing = Path.Combine(_dir, "not-there.json");
            AlertDefinitionMigrator.EnsureShippedProperties(missing, NullLogger.Instance).Should().BeEmpty();
            File.Exists(missing).Should().BeFalse("a migration does not create a file that is not there");
        }

        /// <summary>
        /// The embedded copy the migration reads and the config/ copy the app loads have to be one
        /// source, or the migration splices properties from a catalogue nobody ships.
        /// </summary>
        [Fact]
        public void The_embedded_catalogue_is_byte_identical_to_the_shipped_one()
        {
            var embedded = AlertDefinitionMigrator.ReadShippedDefaultBytes();
            embedded.Should().NotBeNull(
                "SQLTriage.csproj must embed Config\\alert-definitions.json; without it an "
                + "upgraded install has no shipped catalogue to merge from");
            embedded!.Should().Equal(File.ReadAllBytes(ShippedDefinitionsPath()),
                "the embedded copy and the config/ copy are the same file in the csproj and must "
                + "stay byte-identical");
        }

        /// <summary>
        /// The allow-list is the whole safety argument for this merge, so it is asserted rather
        /// than trusted: a nullable operator setting must never be in it. The writer omits nulls,
        /// so an operator who CLEARS such a setting leaves the property absent - indistinguishable
        /// from never received - and re-adding the shipped value would revert the edit on the next
        /// start.
        /// </summary>
        [Fact]
        public void The_merge_allow_list_holds_no_setting_an_operator_can_clear()
        {
            var nullable = typeof(AlertDefinition)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && Nullable.GetUnderlyingType(p.PropertyType) != null)
                .Select(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name)
                .Where(n => n != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            nullable.Should().Contain("cooldownMinutes", "this is the case the rule was written for");

            AlertDefinitionMigrator.MergeableProperties.Should().NotBeEmpty();
            foreach (var property in AlertDefinitionMigrator.MergeableProperties)
            {
                if (string.Equals(property, "valueKind", StringComparison.OrdinalIgnoreCase)) continue;
                nullable.Should().NotContain(property,
                    "{0} is a nullable setting; an operator clearing it leaves it absent, and "
                    + "adding the shipped value back would revert their edit", property);
            }
        }

        private static JsonObject Alert(JsonArray alerts, string id) =>
            alerts.Select(n => n!.AsObject()).Single(o => o["id"]!.GetValue<string>() == id);

        /// <summary>
        /// The shipped catalogue as an install predating the marker holds it: the valueKind lines
        /// simply never written. Deleting whole lines rather than re-serialising is deliberate -
        /// it is what the real file looks like, byte for byte, everywhere else.
        /// </summary>
        private static string WithoutValueKind(string shipped)
        {
            var all = shipped.Split('\n');
            var kept = all.Where(l => !l.Contains("\"valueKind\"", StringComparison.Ordinal)).ToArray();
            (all.Length - kept.Length).Should().Be(5,
                "the shipped catalogue carries exactly five valueKind lines since D2(b) re-based wait_time_anomaly on 2026-08-24");
            return string.Join("\n", kept);
        }

        private static List<AlertDefinition> LoadAlerts(string path)
        {
            var file = JsonSerializer.Deserialize<AlertDefinitionsFile>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            file.Should().NotBeNull();
            return file!.Alerts;
        }

        // -- Tier 7: the artifacts the defect left on the client's screen -----------

        /// <summary>
        /// Purging the poisoned baselines cleans what the defect taught the software. These rows
        /// are what it showed the CLIENT, and before this repair nothing in the product could
        /// clear them: an alert leaves the active list only through the resolve branch of an
        /// evaluation cycle, that branch is gated on removing the alert from an in-memory
        /// dictionary a restart empties, and after the fix these alerts correctly fire nothing and
        /// so never re-enter it. A real install's alert-history.db held 54 such rows on
        /// 2026-08-24, os_paging among them at 98,250,865 against a threshold of 50.
        /// </summary>
        [Fact]
        public async Task Alert_rows_stranded_by_the_raw_total_defect_are_resolved_once()
        {
            var run = Guid.NewGuid().ToString("N").Substring(0, 8);
            var cumulativeId = "stranded_cumulative_" + run;
            var plainId = "stranded_plain_" + run;
            var server = "SRV_" + run;

            var catalogue = new AlertDefinitionsFile
            {
                Alerts =
                {
                    new AlertDefinition
                    {
                        Id = cumulativeId, Name = "Stranded Cumulative", Unit = "per_second",
                        Operator = "greater_than", ValueKind = AlertDefinition.CumulativeCounterKind,
                        Thresholds = new AlertThresholds { Warning = 10, Critical = 50 },
                    },
                    new AlertDefinition
                    {
                        Id = plainId, Name = "Stranded Plain", Unit = "percent",
                        Operator = "greater_than",
                        Thresholds = new AlertThresholds { Warning = 80, Critical = 95 },
                    },
                },
            };

            var b = Build(
                JsonSerializer.Serialize(catalogue, new JsonSerializerOptions { WriteIndented = true }),
                dryRun: false);

            b.history.UpsertAlert(Stranded(cumulativeId, server, 98_250_865));
            b.history.UpsertAlert(Stranded(plainId, server, 97));

            Active(b.history, cumulativeId, server).Should().Be(1, "the defect left this row behind");
            Active(b.history, plainId, server).Should().Be(1);

            (await b.svc.ResolveStrandedCumulativeAlertsAsync()).Should().Be(1);

            Active(b.history, cumulativeId, server).Should().Be(0,
                "98,250,865 was a since-startup total, never 98 million page reads a second, and "
                + "nothing else in the product can take that row off the client's screen");
            Active(b.history, plainId, server).Should().Be(1,
                "a percentage alert is not in this class and its active row is a real alert");

            // One-shot: a later, honest firing of the same alert must survive a restart.
            b.history.UpsertAlert(Stranded(cumulativeId, server, 61));
            (await b.svc.ResolveStrandedCumulativeAlertsAsync()).Should().Be(0,
                "the marker is claimed, so this repair never runs again on this database");
            Active(b.history, cumulativeId, server).Should().Be(1,
                "a rate of 61 a second is a real Critical and must not be swept away");
        }

        private static AlertState Stranded(string alertId, string server, double value) => new()
        {
            AlertId = alertId,
            AlertName = alertId,
            ServerName = server,
            Severity = "Critical",
            Status = AlertStatus.Active,
            LastValue = value,
            ThresholdValue = 50,
            HitCount = 1423,
            FirstTriggered = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc),
            LastTriggered = DateTime.UtcNow,
            Message = alertId + " is " + value,
        };

        private static int Active(AlertHistoryService history, string alertId, string server) =>
            history.GetActiveAlerts().Count(r => r.AlertId == alertId && r.ServerName == server);

        // -- Tier 8: a store that cannot take the sample says so --------------------

        /// <summary>
        /// A cumulative alert that can never store a raw sample can never obtain a previous one,
        /// so it reports "not yet measured" forever: no comparison, no history row, no active
        /// alert, and a UI that reads as healthy. Honest, and invisible unless the store reports
        /// the failure so the evaluator can say it out loud.
        /// </summary>
        [Fact]
        public async Task A_raw_sample_the_store_cannot_take_is_reported_rather_than_swallowed()
        {
            var good = new liveQueriesCacheStore();
            _disposables.Add(good);
            Repoint(good, "_connectionString",
                "Data Source=" + Path.Combine(_dir, "writable.db") + ";Mode=ReadWriteCreate;");
            Invoke(good, "InitializeSchema");

            (await good.SaveLastRawSampleAsync("query_recompilations", "SRV", 1.0, DateTime.UtcNow))
                .Should().BeTrue("a writable store takes the sample");

            var broken = new liveQueriesCacheStore();
            _disposables.Add(broken);
            Repoint(broken, "_connectionString",
                "Data Source=" + Path.Combine(_dir, "broken.db") + ";Mode=ReadWriteCreate;");
            Invoke(broken, "InitializeSchema");
            // A directory is not a database file, so every open from here on fails.
            Repoint(broken, "_connectionString", "Data Source=" + _dir + ";Mode=ReadWrite;");

            (await broken.SaveLastRawSampleAsync("query_recompilations", "SRV", 1.0, DateTime.UtcNow))
                .Should().BeFalse(
                    "silence from an alert that CANNOT measure must be distinguishable from "
                    + "silence from an alert that measured and found nothing");
        }

        // -- Tier 5: live, against a real instance ----------------------------------

        private const string LiveVar = "RAWCOUNTER_LIVE_TARGET";
        private const string EvidenceVar = "RAWCOUNTER_LIVE_EVIDENCE";

        /// <summary>Path to a REAL install's config/alert-definitions.json. The probe copies it
        /// before touching it, so it is safe to point at a running service.</summary>
        private const string InstalledVar = "RAWCOUNTER_INSTALLED_DEFINITIONS";

        /// <summary>
        /// Inert in a normal run: the Skip is computed at discovery time when the environment
        /// variable naming the target instance is not set. Restated here rather than bound from
        /// another file for the same reason AccessProseLiveSmokeTests restates it - the community
        /// profile Compile-Removes parts of the test tree.
        /// </summary>
        [AttributeUsage(AttributeTargets.Method)]
        public sealed class LiveFactAttribute : FactAttribute
        {
            public LiveFactAttribute(params string[] required)
            {
                var missing = required
                    .Where(v => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)))
                    .ToList();
                if (missing.Count > 0)
                    Skip = "live harness not armed; set " + string.Join(", ", missing);
            }
        }

        /// <summary>
        /// Two real samples off a real instance, ten seconds apart, through the production
        /// arithmetic. On an idle box every one of the six alerts must come back with a rate that
        /// fires nothing - which is the whole point, because before this change four of them
        /// fired Critical on every cycle. Then the counter is pushed: forced recompilations
        /// between two samples must move the query_recompilations rate above zero. The assertion
        /// is "above zero", not a magic number, because the box's own load is not controlled.
        /// </summary>
        [LiveFact(LiveVar)]
        public async Task On_a_real_instance_the_five_alerts_measure_rates_and_stay_quiet_when_idle()
        {
            var target = Environment.GetEnvironmentVariable(LiveVar)!;
            var evidence = Environment.GetEnvironmentVariable(EvidenceVar);
            var alerts = ShippedAlerts().Where(a => a.IsCumulativeCounter).ToList();
            alerts.Should().HaveCount(5);

            var lines = new List<string>
            {
                "live probe " + DateTime.UtcNow.ToString("o") + " against " + target,
            };

            var first = new Dictionary<string, (double raw, DateTime at)>();
            foreach (var a in alerts) first[a.Id] = await ReadRawAsync(target, a.Query);

            await Task.Delay(TimeSpan.FromSeconds(10));

            var quiet = new List<string>();
            foreach (var a in alerts)
            {
                var (raw, at) = await ReadRawAsync(target, a.Query);
                var obs = AlertEvaluationService.ObserveValue(a, raw, first[a.Id].raw, first[a.Id].at, at);
                obs.Value.Should().NotBeNull("{0} had a previous sample ten seconds earlier", a.Id);

                var rate = obs.Value!.Value;
                var warn = AlertEvaluationService.IsThresholdBreached(rate, a.Thresholds.Warning, a.Operator);
                var crit = AlertEvaluationService.IsThresholdBreached(rate, a.Thresholds.Critical, a.Operator);

                lines.Add(string.Format(
                    "{0,-22} raw {1,20:N0} -> {2,20:N0}  rate {3,14:N3}  warn {4} crit {5}  fires: {6}",
                    a.Id, first[a.Id].raw, raw, rate, a.Thresholds.Warning, a.Thresholds.Critical,
                    crit ? "CRITICAL" : warn ? "warning" : "no"));

                rate.Should().BeGreaterThanOrEqualTo(0, "{0} produced a negative rate", a.Id);
                if (!crit && !warn) quiet.Add(a.Id);
            }

            // Now push the recompilation counter and prove the measured rate follows it.
            var recomp = alerts.Single(a => a.Id == "query_recompilations");
            var before = await ReadRawAsync(target, recomp.Query);
            await ForceRecompilationsAsync(target, 200);
            var after = await ReadRawAsync(target, recomp.Query);

            var moved = AlertEvaluationService.ObserveValue(recomp, after.raw, before.raw, before.at, after.at);
            moved.Value.Should().NotBeNull();
            moved.Value!.Value.Should().BeGreaterThan(0,
                "200 forced recompilations between the two samples must show up as a positive rate");

            lines.Add(string.Format(
                "forced workload: raw {0:N0} -> {1:N0}, rate {2:N3}/s over {3:N1}s",
                before.raw, after.raw, moved.Value.Value, (after.at - before.at).TotalSeconds));
            lines.Add("quiet on this instance: " + string.Join(", ", quiet));

            foreach (var l in lines) _out.WriteLine(l);
            if (!string.IsNullOrWhiteSpace(evidence)) File.WriteAllLines(evidence, lines);

            quiet.Should().Contain(
                new[] { "query_recompilations", "page_splits", "os_paging", "batch_requests", "deadlock_rate" },
                "these fired a permanent Critical off the raw total before this change and must "
                + "now be quiet on an instance doing nothing");
        }

        /// <summary>
        /// Every cumulative query must return exactly ONE row on a real instance. The alert path
        /// reads its number with ExecuteScalarAsync, which silently takes the first row of
        /// whatever comes back, and sys.dm_os_performance_counters returns one row per instance
        /// for a multi-instance object. The shipped deadlock query returned fifteen rows on
        /// .\NEW2022 and the alert reported the first of them - Xact at 0 - while Key and _Total
        /// both stood at 8. A row count is the only assertion that catches this class, and it can
        /// only be taken against a live instance.
        /// </summary>
        [LiveFact(LiveVar)]
        public async Task On_a_real_instance_every_cumulative_query_returns_exactly_one_row()
        {
            var target = Environment.GetEnvironmentVariable(LiveVar)!;

            foreach (var a in ShippedAlerts().Where(a => a.IsCumulativeCounter))
            {
                var rows = await RowCountAsync(target, a.Query);
                _out.WriteLine(string.Format("{0,-22} rows {1}", a.Id, rows));
                rows.Should().Be(1,
                    "{0} reads its value with ExecuteScalarAsync, which takes the first row and "
                    + "discards the rest; {1} rows means the alert is reporting one arbitrary "
                    + "slice of the counter", a.Id, rows);
            }
        }

        /// <summary>
        /// The probe that had to flip. Point INSTALLED_VAR at a real install's
        /// config/alert-definitions.json and this reads it, drives it through the PRODUCTION
        /// loader and the PRODUCTION arithmetic, and asserts the six alerts measure a rate.
        ///
        /// <para>Before AlertDefinitionMigrator existed this failed against every install that
        /// already had a config file: the marker only ever shipped in the package, and the
        /// package's copy is discarded on upgrade by the installer, by AutoUpdateService, by
        /// SQLTriageUpdater and by the service deploy script. So the fix reached fresh installs
        /// and missed exactly the population that was firing the false Criticals.</para>
        ///
        /// <para>NOTHING IS WRITTEN AT THE GIVEN PATH. The file is copied into this test's own
        /// temporary directory and the migration runs on the copy, so the probe can be pointed
        /// straight at a live install without changing it.</para>
        /// </summary>
        [LiveFact(InstalledVar)]
        public void An_installed_definitions_file_measures_rates_after_the_migration()
        {
            var source = Environment.GetEnvironmentVariable(InstalledVar)!;
            File.Exists(source).Should().BeTrue("the probe needs a real installed file: {0}", source);

            var copy = Path.Combine(_dir, "probed-alert-definitions.json");
            File.Copy(source, copy);

            var t0 = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

            // FIVE since 2026-08-24. wait_time_anomaly is deliberately absent: under D2(b) the
            // SHIPPED definition no longer carries valueKind, so the migration has nothing to give
            // an installed copy for that id, and probing it here would assert a repair that this
            // build does not perform. What an upgraded install actually gets for that one alert is
            // measured separately, in WaitSignalRatioAlertTests.
            var ids = new[]
            {
                "batch_requests", "deadlock_rate", "os_paging",
                "page_splits", "query_recompilations",
            };

            _out.WriteLine("installed file: " + source);
            foreach (var id in ids)
            {
                var a = LoadAlerts(copy).SingleOrDefault(x => x.Id == id);
                _out.WriteLine(string.Format("  before  {0,-22} valueKind={1}",
                    id, a?.ValueKind ?? "<null>"));
            }

            var added = AlertDefinitionMigrator.EnsureShippedProperties(copy, NullLogger.Instance);
            _out.WriteLine("migration added: " + (added.Count == 0 ? "<nothing>" : string.Join(", ", added)));

            foreach (var id in ids)
            {
                var a = LoadAlerts(copy).SingleOrDefault(x => x.Id == id);
                if (a is null) continue;                     // an install may not carry every alert

                a.IsCumulativeCounter.Should().BeTrue(
                    "{0} exists in this installed file and must measure a rate after the migration", id);

                var observed = AlertEvaluationService.ObserveValue(a, 1_957_894, 1_957_857, t0, t0.AddSeconds(10));
                _out.WriteLine(string.Format("  after   {0,-22} valueKind={1} rate={2:N3}",
                    id, a.ValueKind, observed.Value));

                // 37 counts over 10 seconds is 3.7 a second, and 222 a minute for the one alert
                // whose declared unit is per_minute. Scaling by the definition's own unit rather
                // than asserting a single number is the point: reading 3.7 under a label saying
                // per minute would be the same class of defect this lane exists to remove.
                var expected = 3.7 * AlertEvaluationService.RateWindowSeconds(a);
                observed.Value!.Value.Should().BeApproximately(expected, 0.0001,
                    "{0} must difference the two samples and scale to its own declared unit, not "
                    + "hand back the running total", id);
            }
        }

        private static async Task<int> RowCountAsync(string target, string query)
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(
                "Server=" + target + ";Database=master;Integrated Security=true;TrustServerCertificate=true;");
            await conn.OpenAsync();
            using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
                "SELECT COUNT_BIG(*) FROM (" + query + ") AS shipped", conn) { CommandTimeout = 30 };
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        private static async Task<(double raw, DateTime at)> ReadRawAsync(string target, string query)
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(
                "Server=" + target + ";Database=master;Integrated Security=true;TrustServerCertificate=true;");
            await conn.OpenAsync();
            using var cmd = new Microsoft.Data.SqlClient.SqlCommand(query, conn) { CommandTimeout = 30 };
            var result = await cmd.ExecuteScalarAsync();
            return (Convert.ToDouble(result), DateTime.UtcNow);
        }

        private static async Task ForceRecompilationsAsync(string target, int executions)
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(
                "Server=" + target + ";Database=tempdb;Integrated Security=true;TrustServerCertificate=true;");
            await conn.OpenAsync();
            for (var i = 0; i < executions; i++)
            {
                using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
                    "SELECT COUNT(*) FROM sys.objects WHERE object_id > @i OPTION (RECOMPILE);", conn)
                {
                    CommandTimeout = 30,
                };
                cmd.Parameters.AddWithValue("@i", i);
                await cmd.ExecuteScalarAsync();
            }
        }

        // -- harness ----------------------------------------------------------------

        private static void Repoint(object target, string field, object? value)
        {
            var f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            f.Should().NotBeNull("the harness repoints {0}; a rename must fail loudly", field);
            f!.SetValue(target, value);
        }

        private static void Invoke(object target, string method)
        {
            var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            m.Should().NotBeNull("the harness calls {0}; a rename must fail loudly", method);
            m!.Invoke(target, null);
        }

        private sealed class NullOrchestrator : IQueryOrchestrator
        {
            public async Task<QueryResult> EnqueueAsync(
                QueryRequest request, QueryPriority priority, CancellationToken cancellationToken = default)
            {
                await request.Work(cancellationToken);
                return new QueryResult { QueryId = request.QueryId, Success = true };
            }

            public Task<OrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new OrchestratorHealth());

            public Task<OrchestratorMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new OrchestratorMetrics());

            public void UpdateLimits(int globalConcurrency, int perServerConcurrency) { }
            public void Start() { }
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
