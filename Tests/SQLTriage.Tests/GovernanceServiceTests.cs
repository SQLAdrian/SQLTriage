/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class GovernanceServiceTests
    {
        private static GovernanceService CreateService(GovernanceWeights? weights = null)
        {
            var w = weights ?? new GovernanceWeights();
            var provider = new FixedWeightsProvider(w);
            return new GovernanceService(NullLogger<GovernanceService>.Instance, provider);
        }

        [Fact]
        public async Task EmptyResults_ReturnsZeroEmerging()
        {
            var svc = CreateService();
            var score = await svc.ComputeIndicativeAsync(Array.Empty<CheckResult>());

            Assert.Equal(0, score.Overall);
            Assert.Equal(ScoreBand.Emerging, score.Band);
            Assert.True(score.IsIndicative);
            Assert.Empty(score.Categories);
        }

        [Fact]
        public async Task AllPassed_ReturnsPlatinum()
        {
            var svc = CreateService();
            var categories = new[] { "Security", "Security", "Security", "Performance", "Performance", "Reliability", "Reliability", "Cost", "Cost", "Compliance", "Compliance" };
            var results = categories.Select((cat, i) => new CheckResult
            {
                CheckId = $"CHK-{i:D3}",
                Category = cat,
                Severity = "MEDIUM",
                Passed = true
            });

            var score = await svc.ComputeFullAsync(results);

            // Security 3×10=30→cap25, Perf 2×10=20→20, Rel 2×10=20→20, Cost 2×10=20→cap15, Comp 2×10=20→20 = 100
            Assert.Equal(100, score.Overall);
            Assert.Equal(ScoreBand.Platinum, score.Band);
            Assert.Equal(11, score.PassedFindings);
            Assert.Equal(0, score.FailedFindings);
        }

        // Weighted-ratio model (rewritten 2026-05-15, replaces stale per-finding-cap tests):
        //   check_value = max(score_weight, 1) × max(effort_hours, 1.0)
        //   category ratio = Σ(pass-or-info check_value) / Σ(non-SKIP check_value) × 100

        [Fact]
        public async Task HighWeightFailure_DragsCategoryRatioDownMoreThanLowWeightFailure()
        {
            var svc = CreateService();

            // Both servers: one passing + one failing in Security. Differ only in the
            // failing check's score_weight: heavy (20) vs light (1). Same effort.
            var heavyFail = new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true,  ScoreWeight = 1,  EffortHours = 1 },
                new CheckResult { CheckId = "F", Category = "Security", Passed = false, ScoreWeight = 20, EffortHours = 1 }
            };
            var lightFail = new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true,  ScoreWeight = 1,  EffortHours = 1 },
                new CheckResult { CheckId = "F", Category = "Security", Passed = false, ScoreWeight = 1,  EffortHours = 1 }
            };

            var heavy = await svc.ComputeFullAsync(heavyFail);
            var light = await svc.ComputeFullAsync(lightFail);

            // Heavy: 1/(1+20) ≈ 4.76%   Light: 1/(1+1) = 50%
            Assert.Equal(100.0 * 1 / 21, heavy.Categories["Security"].RawScore, 3);
            Assert.Equal(50.0, light.Categories["Security"].RawScore, 3);
            Assert.True(heavy.Overall < light.Overall,
                $"Heavy-weighted failure should drop overall lower (heavy={heavy.Overall}, light={light.Overall})");
        }

        [Fact]
        public async Task HighEffortFailure_DragsCategoryRatioDownMoreThanLowEffortFailure()
        {
            var svc = CreateService();
            var sameWeights = new { ScoreWeight = 5 };

            var heavyEffort = new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true,  ScoreWeight = sameWeights.ScoreWeight, EffortHours = 1 },
                new CheckResult { CheckId = "F", Category = "Security", Passed = false, ScoreWeight = sameWeights.ScoreWeight, EffortHours = 40 }
            };
            var lightEffort = new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true,  ScoreWeight = sameWeights.ScoreWeight, EffortHours = 1 },
                new CheckResult { CheckId = "F", Category = "Security", Passed = false, ScoreWeight = sameWeights.ScoreWeight, EffortHours = 1 }
            };

            var heavy = await svc.ComputeFullAsync(heavyEffort);
            var light = await svc.ComputeFullAsync(lightEffort);

            // Heavy effort: pass=5×1=5, fail=5×40=200  → 5/205 ≈ 2.44%
            // Light effort: pass=5×1=5, fail=5×1 =5    → 5/10 = 50%
            Assert.Equal(100.0 * 5 / 205, heavy.Categories["Security"].RawScore, 3);
            Assert.Equal(50.0, light.Categories["Security"].RawScore, 3);
            Assert.True(heavy.Overall < light.Overall);
        }

        [Fact]
        public async Task EffortHoursZero_IsTreatedAsOne_SoZeroEffortChecksStillCount()
        {
            var svc = CreateService();

            // EffortHours=0 should be clamped to 1.0 — meaning these two equivalent-weight
            // checks contribute equally, and a single failing one halves the ratio.
            var results = new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true,  ScoreWeight = 1, EffortHours = 0 },
                new CheckResult { CheckId = "F", Category = "Security", Passed = false, ScoreWeight = 1, EffortHours = 0 }
            };

            var score = await svc.ComputeFullAsync(results);

            // Both check_value = 1×max(0,1) = 1 → ratio = 1/(1+1) = 50%
            Assert.Equal(50.0, score.Categories["Security"].RawScore, 3);
        }

        [Fact]
        public async Task ScoreWeightZero_IsTreatedAsOne()
        {
            var svc = CreateService();

            // ScoreWeight is documented as 1-25 (default 1) but the service guards against
            // < 1 by clamping. Two checks with ScoreWeight=0 still each contribute weight 1.
            var results = new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true,  ScoreWeight = 0, EffortHours = 1 },
                new CheckResult { CheckId = "F", Category = "Security", Passed = false, ScoreWeight = 0, EffortHours = 1 }
            };

            var score = await svc.ComputeFullAsync(results);
            Assert.Equal(50.0, score.Categories["Security"].RawScore, 3);
        }

        // ── F6: accepted findings ride the pass tier ──────────────────────

        [Fact]
        public async Task AcceptedFinding_RidesPassTier_SoItDoesNotDragTheScore()
        {
            var svc = CreateService();

            // One passing + one FAILING check. Without acceptance the category is 50%.
            var open = new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true,  ScoreWeight = 1, EffortHours = 1 },
                new CheckResult { CheckId = "F", Category = "Security", Passed = false, ScoreWeight = 1, EffortHours = 1 }
            };
            // Same set, but the failing one is client-accepted (IsAccepted=true, Passed still false).
            var accepted = new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true,  ScoreWeight = 1, EffortHours = 1 },
                new CheckResult { CheckId = "F", Category = "Security", Passed = false, IsAccepted = true, ScoreWeight = 1, EffortHours = 1 }
            };

            var openScore = await svc.ComputeFullAsync(open);
            var acceptedScore = await svc.ComputeFullAsync(accepted);

            Assert.Equal(50.0, openScore.Categories["Security"].RawScore, 3);   // FAIL drags to 50%
            Assert.Equal(100.0, acceptedScore.Categories["Security"].RawScore, 3); // accepted → rides pass tier
            Assert.True(acceptedScore.Overall > openScore.Overall);
        }

        [Fact]
        public async Task AcceptedFinding_IsCountedSeparately_NotAsOpenFail()
        {
            var svc = CreateService();
            var results = new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true },
                new CheckResult { CheckId = "OPEN", Category = "Security", Passed = false },
                new CheckResult { CheckId = "ACC", Category = "Security", Passed = false, IsAccepted = true }
            };

            var score = await svc.ComputeFullAsync(results);

            Assert.Equal(1, score.FailedFindings);    // only the genuinely open one
            Assert.Equal(1, score.AcceptedFindings);  // the accepted one, surfaced separately
            Assert.Equal(2, score.PassedFindings);    // pass + accepted both ride the pass tier
        }

        [Fact]
        public async Task UnassessedCategories_AreExcludedFromOverall_NotScored100()
        {
            var svc = CreateService();

            // Only Security has checks (one pass + one fail of equal value → 50%).
            // Every other dimension has zero scorable checks. The overall must reflect
            // Security alone (≈50), NOT be dragged up toward 100 by empty dimensions
            // each silently scoring a free 100%.
            var results = new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true,  ScoreWeight = 1, EffortHours = 1 },
                new CheckResult { CheckId = "F", Category = "Security", Passed = false, ScoreWeight = 1, EffortHours = 1 },
            };

            var score = await svc.ComputeFullAsync(results);

            Assert.Equal(50.0, score.Overall, 1);
            Assert.True(score.Categories["Security"].Assessed);
            Assert.False(score.Categories["Cost"].Assessed);
            Assert.False(score.Categories["Performance"].Assessed);
        }

        [Fact]
        public async Task InfoSeverity_IsExcluded_NeitherRewardsNorPenalises()
        {
            var svc = CreateService();

            // Changed 2026-06-04: INFO is now EXCLUDED from both sides (like SKIP) —
            // informational, not a clean pass. Previously it counted as PASS and padded the score.
            var results = new[]
            {
                new CheckResult { CheckId = "I", Category = "Security", Severity = "INFO", Passed = false, ScoreWeight = 5, EffortHours = 1 },
                new CheckResult { CheckId = "F", Category = "Security", Severity = "HIGH", Passed = false, ScoreWeight = 5, EffortHours = 1 }
            };

            var score = await svc.ComputeFullAsync(results);

            // INFO excluded → only the HIGH fail is scorable: numerator = 0, denominator = HIGH(5) → 0%.
            Assert.Equal(0.0, score.Categories["Security"].RawScore, 3);
            // And INFO must not inflate the evaluated/finding counts.
            Assert.Equal(1, score.FailedFindings);
            Assert.Equal(0, score.PassedFindings);
        }

        [Fact]
        public async Task InfoSeverity_DoesNotChangeScore_VersusOmittingIt()
        {
            var svc = CreateService();

            var withInfo = await svc.ComputeFullAsync(new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true,  ScoreWeight = 5, EffortHours = 1 },
                new CheckResult { CheckId = "I", Category = "Security", Severity = "INFO", Passed = false, ScoreWeight = 5, EffortHours = 1 },
            });
            var withoutInfo = await svc.ComputeFullAsync(new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true,  ScoreWeight = 5, EffortHours = 1 },
            });

            // Excluding INFO ⇒ identical score whether or not the INFO row is present.
            Assert.Equal(withoutInfo.Categories["Security"].RawScore, withInfo.Categories["Security"].RawScore, 3);
        }

        [Fact]
        public async Task SkipResults_AreExcludedFromBothSides()
        {
            var svc = CreateService();

            var results = new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true,  ScoreWeight = 1, EffortHours = 1 },
                // SKIP via Message — should be excluded entirely
                new CheckResult { CheckId = "S1", Category = "Security", Passed = false, ScoreWeight = 25, EffortHours = 40, Message = "SKIP: not applicable to this edition" },
                // SKIP via ErrorMessage — should be excluded entirely
                new CheckResult { CheckId = "S2", Category = "Security", Passed = false, ScoreWeight = 25, EffortHours = 40, ErrorMessage = "Query failed: permission denied" }
            };

            var score = await svc.ComputeFullAsync(results);

            // Only the passing check remains in the denominator → 100%
            Assert.Equal(100.0, score.Categories["Security"].RawScore, 3);
            Assert.Equal(1, score.Categories["Security"].FindingCount);
            // PassedFindings/FailedFindings on the score also exclude SKIPs
            Assert.Equal(1, score.PassedFindings);
            Assert.Equal(0, score.FailedFindings);
            // But TotalFindings is the raw count of inputs (incl. SKIPs)
            Assert.Equal(3, score.TotalFindings);
        }

        // ── 2026-07-16: Verdict-INFO closes the same gap as Severity-INFO ──────────────────

        [Fact]
        public async Task VerdictInfo_IsExcluded_LikeSeverityInfo()
        {
            var svc = CreateService();

            // The check's OWN declared Severity is HIGH (not INFO) — only its raw SQL Verdict
            // says INFO (a verdict-contract check, e.g. SQLT-CUSTOM-MAXDOP for MAXDOP=1 on a
            // 22-core box: a computed recommendation, not an assertion the control is correct).
            // CheckExecutionService sets Passed=true for a PASS/SKIP/INFO verdict alike, so
            // without the Verdict check this rode straight into PassedFindings as a green PASS.
            var results = new[]
            {
                new CheckResult { CheckId = "V", Category = "Performance", Severity = "HIGH", Verdict = "INFO", Passed = true, ScoreWeight = 5, EffortHours = 1 },
                new CheckResult { CheckId = "F", Category = "Performance", Severity = "HIGH", Passed = false, ScoreWeight = 5, EffortHours = 1 }
            };

            var score = await svc.ComputeFullAsync(results);

            // Verdict-INFO excluded → only the genuine fail is scorable: 0/5 → 0%.
            Assert.Equal(0.0, score.Categories["Performance"].RawScore, 3);
            Assert.Equal(1, score.FailedFindings);
            Assert.Equal(0, score.PassedFindings);
            // TotalFindings still counts it — it ran, it just isn't a pass/fail verdict.
            Assert.Equal(2, score.TotalFindings);
        }

        [Fact]
        public async Task ScoredSetTotals_FootAgainstTotalFindings()
        {
            var svc = CreateService();

            // 1 pass, 1 fail, 1 SKIP-by-message, 1 SKIP-by-error, 1 Severity-INFO, 1 Verdict-INFO.
            var results = new[]
            {
                new CheckResult { CheckId = "P",  Category = "Security", Passed = true },
                new CheckResult { CheckId = "F",  Category = "Security", Passed = false },
                new CheckResult { CheckId = "S1", Category = "Security", Passed = true, Message = "SKIP: not applicable" },
                new CheckResult { CheckId = "S2", Category = "Security", Passed = true, ErrorMessage = "permission denied" },
                new CheckResult { CheckId = "I1", Category = "Security", Severity = "INFO", Passed = true },
                new CheckResult { CheckId = "I2", Category = "Security", Severity = "HIGH", Verdict = "INFO", Passed = true },
            };

            var score = await svc.ComputeFullAsync(results);

            Assert.Equal(6, score.TotalFindings);
            Assert.Equal(1, score.PassedFindings);
            Assert.Equal(1, score.FailedFindings);
            // The remaining 4 (2 SKIP + 2 INFO) are excluded from both sides — neither pass nor fail.
            Assert.Equal(4, score.TotalFindings - score.PassedFindings - score.FailedFindings);
        }

        [Fact]
        public async Task ErroredResult_StillCountsAsSkipped_NotPassOrFail()
        {
            var svc = CreateService();

            var results = new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true, ScoreWeight = 1, EffortHours = 1 },
                new CheckResult { CheckId = "E", Category = "Security", Passed = false, ErrorMessage = "Timeout expired", ScoreWeight = 25, EffortHours = 40 },
            };

            var score = await svc.ComputeFullAsync(results);

            Assert.Equal(100.0, score.Categories["Security"].RawScore, 3);   // only the pass is scorable
            Assert.Equal(1, score.PassedFindings);
            Assert.Equal(0, score.FailedFindings);
            Assert.Equal(1, score.ErroredFindings);
        }

        [Fact]
        public async Task AcceptedFinding_StillCountsAsPass_WhenMixedWithSkipAndInfo()
        {
            var svc = CreateService();

            var results = new[]
            {
                new CheckResult { CheckId = "ACC", Category = "Security", Passed = false, IsAccepted = true },
                new CheckResult { CheckId = "I",   Category = "Security", Severity = "INFO", Passed = true },
                new CheckResult { CheckId = "S",   Category = "Security", Passed = true, Message = "SKIP: n/a" },
            };

            var score = await svc.ComputeFullAsync(results);

            // The accepted finding rides the pass tier; SKIP/INFO are excluded entirely.
            Assert.Equal(1, score.PassedFindings);
            Assert.Equal(0, score.FailedFindings);
            Assert.Equal(1, score.AcceptedFindings);
        }

        [Fact]
        public async Task IsBad_HasNoEffectOnScore()
        {
            var svc = CreateService();

            // IsBad is costing-only per memory/project_score_weight_model.md.
            // Two identical result sets, one with IsBad=true on the failing check, should
            // produce the same score.
            var withIsBad = new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true,  ScoreWeight = 1, EffortHours = 1, IsBad = false },
                new CheckResult { CheckId = "F", Category = "Security", Passed = false, ScoreWeight = 1, EffortHours = 1, IsBad = true }
            };
            var withoutIsBad = new[]
            {
                new CheckResult { CheckId = "P", Category = "Security", Passed = true,  ScoreWeight = 1, EffortHours = 1, IsBad = false },
                new CheckResult { CheckId = "F", Category = "Security", Passed = false, ScoreWeight = 1, EffortHours = 1, IsBad = false }
            };

            var a = await svc.ComputeFullAsync(withIsBad);
            var b = await svc.ComputeFullAsync(withoutIsBad);

            Assert.Equal(a.Overall, b.Overall);
            Assert.Equal(a.Categories["Security"].RawScore, b.Categories["Security"].RawScore, 3);
        }

        [Fact]
        public async Task CategoryMapping_FallsBackToReliability()
        {
            var svc = CreateService();
            var results = new[]
            {
                new CheckResult { CheckId = "C1", Category = "UnknownCategory", Severity = "LOW", Passed = true }
            };

            var score = await svc.ComputeIndicativeAsync(results);

            Assert.True(score.Categories.ContainsKey("Reliability"));
            Assert.False(score.Categories.ContainsKey("UnknownCategory"));
        }

        [Fact]
        public async Task IsIndicative_FlagSetCorrectly()
        {
            var svc = CreateService();
            var results = new[] { new CheckResult { CheckId = "C1", Category = "Security", Passed = true } };

            var indicative = await svc.ComputeIndicativeAsync(results);
            var full = await svc.ComputeFullAsync(results);

            Assert.True(indicative.IsIndicative);
            Assert.False(full.IsIndicative);
        }

        [Fact]
        public async Task FailedFindings_ContributeZero()
        {
            var svc = CreateService();
            var results = new[]
            {
                new CheckResult { CheckId = "C1", Category = "Security", Severity = "CRITICAL", Passed = false },
                new CheckResult { CheckId = "C2", Category = "Security", Severity = "CRITICAL", Passed = true }
            };

            var score = await svc.ComputeFullAsync(results);

            Assert.Equal(1, score.PassedFindings);
            Assert.Equal(1, score.FailedFindings);
            var sec = score.Categories["Security"];
            Assert.Equal(1, sec.PassedCount);
            Assert.Equal(2, sec.FindingCount);
        }

        /// <summary>
        /// Test-only provider that returns a fixed <see cref="GovernanceWeights"/> instance.
        /// Implements <see cref="IGovernanceWeightsProvider"/> so it can be passed to
        /// <see cref="GovernanceService"/> without any disk I/O.
        /// </summary>
        private sealed class FixedWeightsProvider : IGovernanceWeightsProvider
        {
            private readonly GovernanceWeights _weights;
            public FixedWeightsProvider(GovernanceWeights weights) => _weights = weights;
            public GovernanceWeights Current => _weights;
            public event EventHandler? WeightsChanged;
        }
    }
}
