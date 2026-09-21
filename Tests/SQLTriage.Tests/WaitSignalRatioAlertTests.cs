/* In the name of God, the Merciful, the Compassionate */

// ── wait_time_anomaly, RE-BASED ON THE SIGNAL-WAIT RATIO. Ruling D2(b), 2026-08-24 ────────────
//
// WHAT CHANGED AND WHY. The alert used to read SUM(wait_time_ms) over sys.dm_os_wait_stats and
// compare the per-second change against a fixed 15,000 ms/s. Wait time accrues per scheduler, so
// that threshold is really a ceiling on "how many cores may be waiting at once" - 15,000 ms/s is
// fifteen cores' worth - and it therefore gets easier to breach the more CPU a client buys. It
// measured 27,167.864 ms/s on an IDLE .\NEW2022 on 2026-08-24 (AlertCumulativeRateTests header).
// Erik Darling says the same thing about the same quantity in the vendored proc, in the comment
// explaining why he had to widen his own column: scripts/sp_PerfCheck.sql:751-758.
//
// The alert now reads the SIGNAL-WAIT RATIO - signal_wait_time_ms over wait_time_ms, as a
// percentage - which is what sp_PerfCheck itself computes at scripts/sp_PerfCheck.sql:2418-2422.
// THE EXCLUSION LIST DIVERGES FROM HIS, DELIBERATELY (verify round, 2026-08-24): his 48-row
// #benign_waits (scripts/sp_PerfCheck.sql:778-825) predates several modern idle waits, and on an
// idle .\NEW2022 the missing SOS_WORK_DISPATCHER alone was 92.9% of the alert's denominator -
// 54,475,262,005 of 58,666,188,781 ms - diluting the ratio to 0.04 while the signal_pct tile
// beside it read a real number. The alert therefore uses the SAME idle list as the
// livewaits.signal_pct tile - 84 types after XE_LIVE_TARGET_TVF, which his list carries and the tile's 83 lacked, was added to both - a strict superset of his 48; a test below pins alert-tile
// parity so the two can never diverge again. The bands are his:
//   warning  30  scripts/sp_PerfCheck.sql:2496   ("WHEN @signal_wait_ratio >= 30.0 THEN 30" - Medium)
//   critical 50  scripts/sp_PerfCheck.sql:2494   ("WHEN @signal_wait_ratio >= 50.0 THEN 20" - High)
// A percentage does not move when a client buys cores, which is the whole point of the re-base.
//
// valueKind IS DELIBERATELY GONE. A ratio of two cumulative columns is already scale-free, so
// differencing two samples of it is meaningless - it would compare the CHANGE in a percentage to
// a percentage threshold. buffer_cache_hit has never carried the marker for exactly this reason
// and AlertCumulativeRateTests says so in as many words. The shipped census is therefore FIVE.
//
// PROVED LIVE on .\new2022 (SQL 2022 16.0.4262.2, up since 2026-08-17 07:04:12) on 2026-08-24:
// the re-based query runs and returns 0.04 - an honest, quiet number on an idle instance, where
// the old query returned tens of thousands against a critical of 15,000.
//
// THE NAME IS VOICE-REVIEWED. "Signal Wait Ratio" first shipped as a DRAFT under ruling D6(b);
// Adrian voice-reviewed the wording and cleared the marker on 2026-08-26 (DECISIONS 04:20, ruling
// 1). This file pins the MEASUREMENT and now also pins the clean, marker-free name.
//
// ── WHAT AN UPGRADED INSTALL GETS. Measured here, not assumed. ────────────────────────────────
//
// It NOW RECEIVES the re-based alert, and this file proves it end to end rather than leaving it
// implicit. Ruling 2026-08-25 06:53 #7 folded delivery of this re-base to existing installs into
// the alerts lane's protected-config delivery mechanism, and AlertDefinitionMigrator grew a
// hash-gated repair (TryRepairSupersededDefinitions / RepairSupersededDefinitions) to carry it.
//
// The gap that used to make this "get nothing" was real and is named so the fix stays anchored:
//   1. installer/SQLTriage.iss:97 ships Config/alert-definitions.json onlyifdoesntexist, so an
//      upgrade never overwrites the operator's copy. (AutoUpdateService and the updater protect it
//      too - AlertDefinitionMigrator's own summary lists all four mechanisms.)
//   2. AlertDefinitionMigrator.MergeableProperties is { "valueKind" } and only ADDS absent
//      properties, so it can never CHANGE a re-based alert's query, unit, thresholds, name or
//      operator - all of them are already present.
// The repair does not widen that allow-list. It replaces an alert's measurement-defining catalogue
// facts with the shipped values ONLY when the installed ones hash, byte for byte, to a definition
// this product itself shipped and has since retired (SupersededDefinitionSignatures). An operator
// who tuned any of those facts has an alert that matches no signature and is left exactly as they
// left it; operator SETTINGS (enable/disable, cooldown, channels) are never in the replaced set.
//
// TWO OLD SHAPES ARE COVERED BY ONE SIGNATURE. A pre-raw-counter install has no valueKind on this
// alert; a raw-counter install had valueKind added by the merge. valueKind is not one of the fields
// the signature hashes, so both re-base to the current shipped definition (which drops valueKind:
// a ratio of two cumulative columns is scale-free and must not be differenced). The regression the
// re-base commit named - a pre-raw-counter install stuck comparing a raw TOTAL against 15,000 - is
// closed here, not owed. Adrian confirmed the 30/50 bands and cleared the DRAFT wording on
// 2026-08-26 (DECISIONS 04:20, ruling 1); this delivery carries whatever the catalogue holds.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class WaitSignalRatioAlertTests : IDisposable
    {
        private readonly string _dir;
        private readonly ITestOutputHelper _out;

        public WaitSignalRatioAlertTests(ITestOutputHelper output)
        {
            _out = output;
            _dir = Path.Combine(Path.GetTempPath(), "sqlt-waitverdict-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        // Was a hand-rolled config\|Config\ probe until 2026-09-11. Commit 3ba2a2b (2026-09-10) moved
        // the operator-editable configs into config.default\, so that probe found nothing and every
        // test below threw FileNotFoundException before reaching an assertion. ShippedConfig probes the
        // shipped payload FIRST and throws naming every folder it tried; see its header for why the
        // order matters (a config\ copy beside a test assembly is usually a stale bin\ leftover, and
        // preferring it is exactly what made these sixteen failures read green).
        private static string ShippedDefinitionsPath() => ShippedConfig.Path("alert-definitions.json");

        private static string FixturePath(string name)
            => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

        private static AlertDefinition Shipped()
        {
            var file = JsonSerializer.Deserialize<AlertDefinitionsFile>(
                File.ReadAllText(ShippedDefinitionsPath()),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            return file.Alerts.Single(a => a.Id == "wait_time_anomaly");
        }

        // ── Tier 1: the shipped definition's contract ────────────────────────────────────────

        /// <summary>
        /// The measurement contract. Every number here is quoted from the vendored proc, and the
        /// citation is in the comment above so a later reader can re-derive it rather than trust it.
        /// </summary>
        [Fact]
        public void The_shipped_alert_measures_the_signal_wait_ratio_as_a_percentage()
        {
            var a = Shipped();

            a.Unit.Should().Be("percent",
                "the query returns signal over total times 100, and Unit is what the client-facing "
                + "message prints beside the number");
            a.Operator.Should().Be("greater_than",
                "a HIGH ratio is the bad direction");
            a.Thresholds.Warning.Should().Be(30,
                "sp_PerfCheck.sql:2496 - '>= 30.0' is his Medium band for this exact quantity");
            a.Thresholds.Critical.Should().Be(50,
                "sp_PerfCheck.sql:2494 - '>= 50.0' is his High band for this exact quantity");
            a.IsCumulativeCounter.Should().BeFalse(
                "a ratio of two cumulative columns is scale-free; differencing it would compare the "
                + "change in a percentage against a percentage threshold");

            a.Query.Should().NotBeNullOrWhiteSpace();
            a.Query.Should().Contain("signal_wait_time_ms");
            a.Query.Should().Contain("sys.dm_os_wait_stats");
            a.Query!.Replace(" ", string.Empty).ToLowerInvariant()
                .Should().Contain("/nullif(sum(wait_time_ms),0)",
                    "NULLIF is what stops a zero-wait instance dividing by zero, and the divisor is "
                    + "what makes this a ratio rather than the running total it used to be");

            a.Query.Should().NotContain("15000", "the core-count-scaled ceiling is gone");
            a.Query.Should().NotContain("valueKind");
        }

        /// <summary>
        /// THE ONE CANONICAL IDLE LIST. The verify round measured what a verbatim copy of
        /// sp_PerfCheck's 48-row #benign_waits does to this alert on a real instance: the missing
        /// SOS_WORK_DISPATCHER (a 2019+ idle scheduler wait) was 92.9% of the denominator and the
        /// ratio read 0.04 while the signal_pct tile beside it - which filtered 83 idle types (84 after this fix) - read
        /// a real number. Same bands over two different quantities is the house's measured-quantity
        /// defect class. The contract is therefore PARITY: the alert excludes exactly the wait
        /// types the livewaits.signal_pct tile excludes, and that shared list is a strict superset
        /// of sp_PerfCheck's 48 (scripts/sp_PerfCheck.sql:778-825), so his floor still holds.
        /// </summary>
        [Fact]
        public void The_alert_and_the_signal_pct_tile_exclude_the_same_wait_types()
        {
            var alertExcluded = ExtractWaitList(Shipped().Query!);
            var tileExcluded = ExtractWaitList(SignalPctTileQuery());

            alertExcluded.Should().OnlyHaveUniqueItems("a duplicate means the list was edited by hand");
            alertExcluded.Should().BeEquivalentTo(tileExcluded,
                "the alert and the livewaits.signal_pct tile claim the same bands, so they must "
                + "measure the same quantity - one canonical idle list, never two");

            alertExcluded.Should().Contain("SOS_WORK_DISPATCHER",
                "without it the denominator on an idle 2019+ instance is >90% idle scheduler wait "
                + "and the ratio dilutes to noise - measured 92.9% on NEW2022, 2026-08-24");

            // A strict superset of sp_PerfCheck's own 48-row #benign_waits, so his 30/50 bands keep
            // their floor. Parsed from the vendored proc, not hard-coded, so a proc refresh moves it.
            var his = BenignWaitsFromVendoredProc();
            his.Count.Should().BeGreaterThanOrEqualTo(40, "the proc parse must not be vacuous");
            alertExcluded.Should().Contain(his,
                "every wait sp_PerfCheck calls benign must stay excluded; the divergence is "
                + "additions only");

            // TRACEWRITE went the other way: the OLD alert excluded it, neither list does now.
            alertExcluded.Should().NotContain("TRACEWRITE",
                "neither #benign_waits nor the tile's list carries TRACEWRITE; its return would "
                + "mean a hand edit");
        }

        /// <summary>
        /// Ruling 1 (DECISIONS 2026-08-26 04:20): Adrian voice-reviewed this alert's name and
        /// description, so the DRAFT marker is CLEARED and the final wording ships. Ruling D6(b)
        /// held the wording as a DRAFT until then; this replaces that pin. It fixes the clean name
        /// and proves neither field still carries "DRAFT" — a re-added marker or a name drift fails
        /// here. The migrator carries whatever the catalogue holds (see
        /// An_upgraded_install_receives_the_rebased_alert_via_the_migrator), so the de-DRAFTed form
        /// reaches existing installs too.
        /// </summary>
        [Fact]
        public void The_alert_name_and_description_ship_voice_reviewed_without_a_draft_marker()
        {
            var a = Shipped();
            a.Name.Should().Be("Signal Wait Ratio",
                "Adrian confirmed the wording; the DRAFT qualifier is cleared");
            a.Name.Should().NotContainEquivalentOf("draft",
                "the name is client-facing and now voice-reviewed");
            a.Description.Should().NotContainEquivalentOf("draft",
                "the description is client-facing and now voice-reviewed");
        }

        private static List<string> ExtractWaitList(string sql)
            => System.Text.RegularExpressions.Regex
                .Matches(sql, @"N'([A-Z_0-9]+)'")
                .Select(m => m.Groups[1].Value)
                .ToList();

        private static string SignalPctTileQuery()
        {
            // Same fix as ShippedDefinitionsPath above: the shipped dashboard config moved to
            // config.default\ at 3ba2a2b and the hand-rolled two-folder probe went blind.
            var path = ShippedConfig.Path("dashboard-config.json");

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var panel in EnumeratePanels(doc.RootElement))
            {
                if (panel.ValueKind == JsonValueKind.Object
                    && panel.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && id.GetString() == "livewaits.signal_pct")
                {
                    return panel.GetProperty("query").GetProperty("sqlServer").GetString()!;
                }
            }
            throw new Xunit.Sdk.XunitException("livewaits.signal_pct not found in dashboard-config.json");
        }

        private static IEnumerable<JsonElement> EnumeratePanels(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                yield return e;
                foreach (var prop in e.EnumerateObject())
                    foreach (var inner in EnumeratePanels(prop.Value)) yield return inner;
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray())
                    foreach (var inner in EnumeratePanels(item)) yield return inner;
            }
        }

        private static List<string> BenignWaitsFromVendoredProc()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln")))
                dir = dir.Parent;
            (dir != null).Should().BeTrue("the repo root (SQLTriage.sln) must be reachable from the test binary");
            var proc = File.ReadAllText(Path.Combine(dir!.FullName, "scripts", "sp_PerfCheck.sql"));

            // The #benign_waits INSERT block: consecutive (N'WAIT_TYPE') rows after the insert header.
            var m = System.Text.RegularExpressions.Regex.Match(
                proc, @"INSERT\s+(INTO\s+)?#benign_waits[\s\S]*?VALUES\s*(?<rows>(\s*\(\s*N'[A-Z_0-9]+'\s*\)\s*,?)+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            m.Success.Should().BeTrue("the vendored proc must still populate #benign_waits by literal rows");
            return ExtractWaitList(m.Groups["rows"].Value);
        }

        // ── Tier 2: the comparison, at the band edges ────────────────────────────────────────

        /// <summary>
        /// The production comparison, driven directly, either side of both bands. 29 / 31 / 51 are
        /// the values the lane brief names; 30 and 50 are added because greater_than is STRICT and
        /// an off-by-one in either direction is the kind of thing a later edit does silently.
        /// </summary>
        [Theory]
        [InlineData(29.0, false, false)]   // below warning
        [InlineData(30.0, false, false)]   // AT warning: greater_than is strict, so not breached
        [InlineData(31.0, true, false)]    // warning breached, critical not
        [InlineData(50.0, true, false)]    // AT critical: strict again
        [InlineData(51.0, true, true)]     // both breached
        public void The_bands_fire_where_sp_PerfCheck_says_they_should(
            double value, bool expectWarning, bool expectCritical)
        {
            var a = Shipped();

            AlertEvaluationService.IsThresholdBreached(value, a.Thresholds.Warning, a.Operator)
                .Should().Be(expectWarning, "value {0} against warning {1}", value, a.Thresholds.Warning);
            AlertEvaluationService.IsThresholdBreached(value, a.Thresholds.Critical, a.Operator)
                .Should().Be(expectCritical, "value {0} against critical {1}", value, a.Thresholds.Critical);
        }

        /// <summary>
        /// The number that used to fire Critical must now be impossible. 27,167.864 was measured
        /// live on an IDLE .\NEW2022 on 2026-08-24 through the production arithmetic. Against the
        /// old thresholds it was a Critical; a signal-wait RATIO cannot reach 51 by accruing
        /// milliseconds, because it is bounded by 100 whatever the core count.
        /// </summary>
        [Fact]
        public void The_idle_instance_reading_that_used_to_fire_critical_is_no_longer_expressible()
        {
            var a = Shipped();
            a.Thresholds.Critical.Should().BeLessThan(101,
                "a percentage cannot exceed 100, so a threshold above it would be unreachable and a "
                + "threshold this low is only meaningful because the quantity is bounded");

            // The live reading of the re-based query on .\new2022, 2026-08-24. Quiet, as it should be.
            AlertEvaluationService.IsThresholdBreached(0.04, a.Thresholds.Warning, a.Operator)
                .Should().BeFalse("0.04% signal wait on an idle instance must not warn");
        }

        // ── Tier 3: what an UPGRADED install actually gets. The item the brief refuses to leave
        //           implicit. Both files fed to the migrator are real: the current shipped
        //           catalogue, and the pre-lane alert extracted from git at a4c3a1c. ───────────

        private string InstalledFileUpgradedFromPreLane()
        {
            // A realistic upgraded install: the current shipped catalogue, with wait_time_anomaly
            // reverted to the object that install actually holds on disk. Read from the fixture,
            // which was extracted from git at a4c3a1c rather than typed.
            var fixture = JsonNode.Parse(File.ReadAllText(
                FixturePath("prelane-wait-time-anomaly-alert.json")))!.AsObject();
            var preLane = fixture["alert"]!.AsObject();

            var root = JsonNode.Parse(File.ReadAllText(ShippedDefinitionsPath()))!.AsObject();
            var alerts = root["alerts"]!.AsArray();
            for (var i = 0; i < alerts.Count; i++)
            {
                if (alerts[i]!.AsObject()["id"]!.GetValue<string>() != "wait_time_anomaly") continue;
                alerts[i] = preLane.DeepClone();
                break;
            }

            var path = Path.Combine(_dir, "upgraded-alert-definitions.json");
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }

        private static AlertDefinition ReadAlert(string path, string id)
        {
            var file = JsonSerializer.Deserialize<AlertDefinitionsFile>(
                File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            return file.Alerts.Single(a => a.Id == id);
        }

        private static JsonObject TargetAlert(string path, string id)
            => JsonNode.Parse(File.ReadAllText(path))!.AsObject()["alerts"]!.AsArray()
                .First(n => n!.AsObject()["id"]!.GetValue<string>() == id)!.AsObject();

        /// <summary>
        /// THE HASH IS THE REAL ONE. SupersededDefinitionSignatures pins a SHA of the pre-lane alert;
        /// this recomputes it from the fixture the migrator's own doc points at, so a stray character
        /// in either the projection or the pinned constant fails here with the value to paste, rather
        /// than silently making the re-base never fire.
        /// </summary>
        [Fact]
        public void The_pinned_superseded_signature_matches_the_shipped_prelane_alert()
        {
            var preLane = JsonNode.Parse(File.ReadAllText(
                FixturePath("prelane-wait-time-anomaly-alert.json")))!.AsObject()["alert"]!.AsObject();

            var signature = AlertDefinitionMigrator.DefinitionSignature(preLane);
            _out.WriteLine("prelane wait_time_anomaly signature = " + signature);

            AlertDefinitionMigrator.SupersededDefinitionSignatures["wait_time_anomaly"]
                .Should().Contain(signature,
                    "the pinned signature must be the one the shipped pre-lane alert actually hashes "
                    + "to; if this fails, paste the value above into SupersededDefinitionSignatures");
        }

        /// <summary>
        /// THE LIVE-PROBE INSIGHT, PINNED. The signature hashes the measurement and firing only, not
        /// the client-facing prose. The live service's installed alert-definitions.json (copy-read
        /// 2026-08-26) carries an EARLIER description over the identical old query/unit/thresholds; a
        /// description-inclusive signature would have re-based the fixture population and missed the
        /// real install. This pins that a reworded description neither changes the signature nor blocks
        /// the re-base — if someone folds the description back into the signature, this fails.
        /// </summary>
        [Fact]
        public void A_reworded_description_neither_changes_the_signature_nor_blocks_the_rebase()
        {
            var preLane = JsonNode.Parse(File.ReadAllText(
                FixturePath("prelane-wait-time-anomaly-alert.json")))!.AsObject()["alert"]!.AsObject();
            var baseline = AlertDefinitionMigrator.DefinitionSignature(preLane);

            var reworded = preLane.DeepClone()!.AsObject();
            reworded["description"] = "Total wait time is abnormally high compared to typical workload";
            AlertDefinitionMigrator.DefinitionSignature(reworded).Should().Be(baseline,
                "the description is prose the product rewords across releases; it is not in the signature");

            // And an install carrying that earlier description is still re-based end to end.
            var path = InstalledFileUpgradedFromPreLane();
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            root["alerts"]!.AsArray()
                .First(n => n!.AsObject()["id"]!.GetValue<string>() == "wait_time_anomaly")!
                .AsObject()["description"] = "Total wait time is abnormally high compared to typical workload";
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            AlertDefinitionMigrator.RepairSupersededDefinitions(path, NullLogger.Instance)
                .Should().Contain("wait_time_anomaly", "the earlier-description install is a re-base target too");
            ReadAlert(path, "wait_time_anomaly").Unit.Should().Be("percent");
        }

        /// <summary>
        /// THE OUTCOME, MEASURED. An install upgraded over a pre-D2(b) config (raw-counter shape, with
        /// valueKind) now RECEIVES the re-based alert: the migrator replaces its measurement-defining
        /// facts with the shipped signal-wait ratio and drops the cumulative-counter marker, leaving
        /// every operator setting alone. This is the delivery ruling 06:53 #7 mandated.
        /// </summary>
        [Fact]
        public void An_upgraded_install_receives_the_rebased_alert_via_the_migrator()
        {
            var path = InstalledFileUpgradedFromPreLane();

            // The full startup order: EnsureShippedProperties (adds absent allow-listed props) then
            // the re-base. The first adds nothing to this id (shipped has no valueKind to give); the
            // second carries the corrected definition.
            AlertDefinitionMigrator.EnsureShippedProperties(path, NullLogger.Instance);
            var repaired = AlertDefinitionMigrator.RepairSupersededDefinitions(path, NullLogger.Instance);

            repaired.Should().Contain("wait_time_anomaly",
                "the re-based definition must reach an install that already exists");

            var installed = ReadAlert(path, "wait_time_anomaly");
            installed.Unit.Should().Be("percent", "the ratio is a percentage, not milliseconds");
            installed.Thresholds.Warning.Should().Be(30);
            installed.Thresholds.Critical.Should().Be(50);
            installed.Query.Should().Contain("signal_wait_time_ms", "the re-based query reaches this install");
            installed.Query.Should().NotStartWith("SELECT SUM(wait_time_ms)");
            installed.IsCumulativeCounter.Should().BeFalse(
                "the cumulative-counter marker is dropped: a ratio must not be differenced");
            installed.Name.Should().Contain("Signal Wait Ratio");

            _out.WriteLine("upgraded install now reads: unit=" + installed.Unit
                           + " warning=" + installed.Thresholds.Warning
                           + " critical=" + installed.Thresholds.Critical);
        }

        /// <summary>
        /// THE REGRESSION, CLOSED. An install that predates the raw-counter lane has no valueKind at
        /// all - the shape the re-base commit named as a RULING OWED because the merge could no longer
        /// hand it one. valueKind is not one of the fields the signature hashes, so the same signature
        /// covers this shape too, and the re-base reaches it. No permanent-Critical raw total survives.
        /// </summary>
        [Fact]
        public void An_install_predating_the_raw_counter_lane_is_also_rebased()
        {
            var path = InstalledFileUpgradedFromPreLane();

            // Strip valueKind: the state of any install that never took a raw-counter build.
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            root["alerts"]!.AsArray()
                .First(n => n!.AsObject()["id"]!.GetValue<string>() == "wait_time_anomaly")!
                .AsObject().Remove("valueKind");
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            AlertDefinitionMigrator.RepairSupersededDefinitions(path, NullLogger.Instance)
                .Should().Contain("wait_time_anomaly",
                    "the pre-raw-counter shape shares the signature - valueKind is not hashed");

            var installed = ReadAlert(path, "wait_time_anomaly");
            installed.Query.Should().Contain("signal_wait_time_ms");
            installed.IsCumulativeCounter.Should().BeFalse();
            installed.Thresholds.Critical.Should().Be(50);
            installed.Unit.Should().Be("percent");
        }

        /// <summary>
        /// OPERATOR OVERRIDES ARE PRESERVED. The re-base owns the measurement, never the operator's
        /// choices. An operator who turned this alert OFF and set a cooldown keeps both across the
        /// re-base - those fields are not in the signature and not in the replaced set - while the
        /// broken measurement is still corrected.
        /// </summary>
        [Fact]
        public void The_rebase_preserves_operator_settings_it_does_not_own()
        {
            var path = InstalledFileUpgradedFromPreLane();

            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var target = root["alerts"]!.AsArray()
                .First(n => n!.AsObject()["id"]!.GetValue<string>() == "wait_time_anomaly")!.AsObject();
            target["enabled"] = false;
            target["cooldownMinutes"] = 120;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            AlertDefinitionMigrator.RepairSupersededDefinitions(path, NullLogger.Instance)
                .Should().Contain("wait_time_anomaly",
                    "changing enabled/cooldown does not touch the measurement, so the signature matches");

            var reread = TargetAlert(path, "wait_time_anomaly");
            reread["enabled"]!.GetValue<bool>().Should().BeFalse("the operator's disable must survive");
            reread["cooldownMinutes"]!.GetValue<int>().Should().Be(120, "the operator's cooldown must survive");

            ReadAlert(path, "wait_time_anomaly").Unit.Should().Be("percent",
                "and the measurement was still corrected");
        }

        /// <summary>
        /// AN OPERATOR-TUNED MEASUREMENT BLOCKS THE RE-BASE ENTIRELY. Raise the warning threshold on
        /// the old ms/s alert and it matches no shipped signature: the whole alert is left exactly as
        /// tuned, old query and all. Half-migrating it into an incoherent percent-query-with-a-
        /// milliseconds-threshold state would be worse than leaving it; the gate fails closed.
        /// </summary>
        [Fact]
        public void An_operator_tuned_threshold_blocks_the_rebase()
        {
            var path = InstalledFileUpgradedFromPreLane();

            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            root["alerts"]!.AsArray()
                .First(n => n!.AsObject()["id"]!.GetValue<string>() == "wait_time_anomaly")!
                .AsObject()["thresholds"]!.AsObject()["warning"] = 8000;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            AlertDefinitionMigrator.RepairSupersededDefinitions(path, NullLogger.Instance)
                .Should().NotContain("wait_time_anomaly",
                    "an edited threshold matches no shipped signature, so the alert is left alone");

            var installed = ReadAlert(path, "wait_time_anomaly");
            installed.Unit.Should().Be("milliseconds", "untouched");
            installed.Thresholds.Warning.Should().Be(8000, "the operator's edit survives");
            installed.Query.Should().StartWith("SELECT SUM(wait_time_ms)", "the old measurement is untouched");
        }

        /// <summary>
        /// IDEMPOTENT. Once re-based, the alert's signature is the CURRENT shipped one, which is not
        /// in the superseded set, so a second run writes nothing. A migrator that re-based on every
        /// start would rewrite the file forever.
        /// </summary>
        [Fact]
        public void The_rebase_is_idempotent()
        {
            var path = InstalledFileUpgradedFromPreLane();

            AlertDefinitionMigrator.RepairSupersededDefinitions(path, NullLogger.Instance)
                .Should().Contain("wait_time_anomaly", "first run re-bases it");
            AlertDefinitionMigrator.RepairSupersededDefinitions(path, NullLogger.Instance)
                .Should().BeEmpty("second run finds the current signature, not a superseded one");
        }

        /// <summary>
        /// The negative control that keeps the re-base honest about its blast radius. The valueKind
        /// merge still repairs the other five cumulative alerts on the same file, and the re-base
        /// touches ONLY wait_time_anomaly - so neither path is silently doing the other's job.
        /// </summary>
        [Fact]
        public void The_valuekind_merge_still_repairs_the_other_five_and_the_rebase_touches_only_the_one()
        {
            var path = InstalledFileUpgradedFromPreLane();

            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            foreach (var node in root["alerts"]!.AsArray())
                node!.AsObject().Remove("valueKind");
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var added = AlertDefinitionMigrator.EnsureShippedProperties(path, NullLogger.Instance);
            added.Should().BeEquivalentTo(new[]
            {
                "batch_requests.valueKind", "deadlock_rate.valueKind", "os_paging.valueKind",
                "page_splits.valueKind", "query_recompilations.valueKind",
            }, "five, and wait_time_anomaly is re-based by the other path rather than merged");

            var repaired = AlertDefinitionMigrator.RepairSupersededDefinitions(path, NullLogger.Instance);
            repaired.Should().BeEquivalentTo(new[] { "wait_time_anomaly" },
                "the re-base is scoped by id to exactly the superseded alert");
        }
    }
}
