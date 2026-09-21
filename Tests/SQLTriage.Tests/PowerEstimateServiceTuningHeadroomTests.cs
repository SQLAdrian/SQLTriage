/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    // ── Recovery build 2026-07-12: CIO Power Savings tuning-headroom honesty ──
    // ComputeTuningHeadroom is pure/static (no SQL, no DI), so the zero-evidence
    // path can be verified directly against synthetic CheckResult lists. This is
    // the "targeted unit-style check with synthetic inputs" referenced in the
    // build brief — the live estate under test had a real power-plan FAIL on
    // every server, so the zero-signal branch never actually rendered live.
    public class PowerEstimateServiceTuningHeadroomTests
    {
        private const string Server = "TEST-SERVER";

        private static CheckResult Result(string checkId, bool passed, bool corrupted = false, string? errorMessage = null) => new()
        {
            CheckId = checkId,
            CheckName = checkId,
            Category = "Performance",
            Severity = "Medium",
            Passed = passed,
            IsCorrupted = corrupted,
            ErrorMessage = errorMessage,
            InstanceName = Server,
        };

        [Fact]
        public void No_results_at_all_yields_zero_state()
        {
            var h = PowerEstimateService.ComputeTuningHeadroom(Server, null);
            Assert.Equal(0, h.LowPct);
            Assert.Equal(0, h.HighPct);
            Assert.Empty(h.Drivers);
        }

        [Fact]
        public void All_relevant_checks_passing_yields_zero_state()
        {
            var results = new List<CheckResult>
            {
                Result("SQLT-VA-POWER-PLAN-HIGH-PERFORMANCE", passed: true),
                Result("SQLT-CUSTOM-MAXDOP", passed: true),
                Result("SQLT-CUSTOM-INDEX-FRAGMENTATION", passed: true),
                Result("SQLT-CORE-DETECT-USE-OF-GUID-UNIQUEIDENTIFIER-IN-CLUSTERED-I", passed: true),
            };

            var h = PowerEstimateService.ComputeTuningHeadroom(Server, results);

            Assert.Equal(0, h.LowPct);
            Assert.Equal(0, h.HighPct);
            Assert.Empty(h.Drivers);
        }

        [Fact]
        public void Unrelated_checks_only_yields_zero_state()
        {
            // Real FAILs exist, but none are in the power-plan/parallelism/index-health
            // check-id sets — must contribute nothing (no fabricated range from noise).
            var results = new List<CheckResult>
            {
                Result("SQLT-VA-SOME-UNRELATED-CHECK", passed: false),
            };

            var h = PowerEstimateService.ComputeTuningHeadroom(Server, results);

            Assert.Equal(0, h.LowPct);
            Assert.Equal(0, h.HighPct);
            Assert.Empty(h.Drivers);
        }

        [Fact]
        public void Power_plan_fail_fires_only_that_contribution()
        {
            var results = new List<CheckResult>
            {
                Result("SQLT-VA-POWER-PLAN-HIGH-PERFORMANCE", passed: false),
                Result("SQLT-CUSTOM-MAXDOP", passed: true),
                Result("SQLT-CUSTOM-INDEX-FRAGMENTATION", passed: true),
            };

            var h = PowerEstimateService.ComputeTuningHeadroom(Server, results);

            Assert.Equal(3, h.LowPct);
            Assert.Equal(10, h.HighPct);
            Assert.Equal(new[] { "power-plan" }, h.Drivers);
        }

        [Fact]
        public void All_three_contributions_fire_and_sum()
        {
            var results = new List<CheckResult>
            {
                Result("SQLT-VA-POWER-PLAN-HIGH-PERFORMANCE", passed: false),
                Result("SQLT-CUSTOM-MAXDOP", passed: false),
                Result("SQLT-CUSTOM-INDEX-FRAGMENTATION", passed: false),
            };

            var h = PowerEstimateService.ComputeTuningHeadroom(Server, results);

            Assert.Equal(3 + 1 + 1, h.LowPct);
            Assert.Equal(10 + 4 + 4, h.HighPct);
            Assert.Equal(new[] { "power-plan", "parallelism", "index-health" }, h.Drivers);
        }

        [Fact]
        public void Multiple_failing_checks_in_the_same_class_do_not_stack()
        {
            // Two different MAXDOP-class checks both FAIL for this server — the
            // "parallelism" contribution fires ONCE, not twice.
            var results = new List<CheckResult>
            {
                Result("SQLT-BPCHK-00220-PARALLELISM-MAXDOP", passed: false),
                Result("SQLT-CUSTOM-MAXDOP", passed: false),
            };

            var h = PowerEstimateService.ComputeTuningHeadroom(Server, results);

            Assert.Equal(1, h.LowPct);
            Assert.Equal(4, h.HighPct);
            Assert.Equal(new[] { "parallelism" }, h.Drivers);
        }

        [Fact]
        public void Corrupted_blocked_check_does_not_fire()
        {
            // Passed == false but IsCorrupted == true (integrity block, not executed) —
            // not a real verdict about the server, must not fire.
            var results = new List<CheckResult>
            {
                Result("SQLT-VA-POWER-PLAN-HIGH-PERFORMANCE", passed: false, corrupted: true),
            };

            var h = PowerEstimateService.ComputeTuningHeadroom(Server, results);

            Assert.Equal(0, h.LowPct);
            Assert.Equal(0, h.HighPct);
            Assert.Empty(h.Drivers);
        }

        [Fact]
        public void Execution_error_does_not_fire()
        {
            // Passed == false with a populated ErrorMessage (exception during execution,
            // per CheckExecutionService's catch block) — not an authored verdict, must not fire.
            var results = new List<CheckResult>
            {
                Result("SQLT-VA-POWER-PLAN-HIGH-PERFORMANCE", passed: false, errorMessage: "Timeout expired."),
            };

            var h = PowerEstimateService.ComputeTuningHeadroom(Server, results);

            Assert.Equal(0, h.LowPct);
            Assert.Equal(0, h.HighPct);
            Assert.Empty(h.Drivers);
        }

        // ── The two kinds of zero, and what the chip says about each (2026-08-08) ──────────
        //
        // 0/0 with no drivers was ONE state as far as the DTO was concerned, and the status-bar
        // chip printed "tuning headroom ~0-0% (this server)" for it — on every page, for every
        // server, until an assessment had run. Arithmetically that is the model's output;
        // read by a human it is a measured finding of no headroom, and nothing was measured.
        // The band is unchanged; what is new is a state that says WHY it is zero, and a chip
        // label conditioned on that state.

        [Fact]
        public void No_results_at_all_is_not_assessed_rather_than_a_measured_zero()
        {
            var h = PowerEstimateService.ComputeTuningHeadroom(Server, null);
            Assert.Equal(TuningHeadroomState.NoCheckResults, h.State);

            var est = EstateWith(h);
            Assert.Equal("tuning headroom: not assessed (this server)", est.RelativeChipLabel);
            Assert.Equal("Tuning headroom not assessed — no check results for this server yet",
                est.RelativeHeadline);
            Assert.DoesNotContain("0", est.RelativeChipLabel);
        }

        [Fact]
        public void Results_with_nothing_qualifying_says_no_findings_not_not_assessed()
        {
            // Something WAS examined here, and that is a different sentence.
            var results = new List<CheckResult>
            {
                Result("SQLT-VA-POWER-PLAN-HIGH-PERFORMANCE", passed: true),
                Result("SQLT-CUSTOM-MAXDOP", passed: true),
            };

            var h = PowerEstimateService.ComputeTuningHeadroom(Server, results);
            Assert.Equal(TuningHeadroomState.NoQualifyingFindings, h.State);
            Assert.Equal(0, h.LowPct);

            var est = EstateWith(h);
            Assert.Equal("tuning headroom: no findings (this server)", est.RelativeChipLabel);
            Assert.DoesNotContain("not assessed", est.RelativeChipLabel);
        }

        [Fact]
        public void A_fired_contribution_still_prints_the_band()
        {
            // The control: the whole point is that a REAL band is unaffected by the fix.
            var results = new List<CheckResult>
            {
                Result("SQLT-VA-POWER-PLAN-HIGH-PERFORMANCE", passed: false),
            };

            var h = PowerEstimateService.ComputeTuningHeadroom(Server, results);
            Assert.Equal(TuningHeadroomState.EvidenceFired, h.State);

            var est = EstateWith(h);
            Assert.Equal("tuning headroom ~3–10% (this server)", est.RelativeChipLabel);
            Assert.Equal("Tuning could cut this server's compute energy ~3–10% (modelled)",
                est.RelativeHeadline);
        }

        [Fact]
        public void A_failed_evidence_lookup_claims_neither_a_band_nor_a_finding()
        {
            // The state ApplyTuningHeadroom's catch sets. "No findings" would be a claim about
            // results nobody could read.
            var est = new ServerPowerEstimate { HeadroomState = TuningHeadroomState.LookupFailed };

            Assert.Equal("tuning headroom: unavailable (this server)", est.RelativeChipLabel);
            Assert.Equal("Tuning headroom not established — reading this server's check results failed",
                est.RelativeHeadline);
        }

        [Fact]
        public void An_estimate_nobody_filled_in_claims_nothing()
        {
            // Model() constructs the estimate BEFORE the evidence pass runs, so the default state
            // has to be the one that claims least.
            Assert.Equal(TuningHeadroomState.NoCheckResults, new ServerPowerEstimate().HeadroomState);
            Assert.Equal(TuningHeadroomState.NoCheckResults, new ServerTuningHeadroom().State);
        }

        /// <summary>
        /// The relative branch of the chip: a VM (absolute watts suppressed), carrying the band and
        /// state a headroom computation produced.
        /// </summary>
        private static ServerPowerEstimate EstateWith(ServerTuningHeadroom h) => new()
        {
            ServerName = Server,
            IsVirtual = true,
            AbsoluteApplicable = false,
            TuneCutLowPct = h.LowPct,
            TuneCutHighPct = h.HighPct,
            TuningHeadroomDrivers = h.Drivers,
            HeadroomState = h.State,
        };
    }
}
