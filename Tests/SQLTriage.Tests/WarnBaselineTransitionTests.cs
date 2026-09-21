/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// B1 (2026-07-20) — baseline transitions must be VERDICT-AWARE.
    ///
    /// <para>The defect this file pins: <c>ComputeTransitions</c> compared raw
    /// <see cref="CheckResult.Passed"/> against the raw baseline <c>passed</c> column. WARN rides
    /// Passed=true (ruling #4), so a check that went FAIL → WARN satisfied
    /// <c>!wasPassed &amp;&amp; cur.Passed</c> and was filed as <b>Resolved</b> — rendered at
    /// Pages/BaselineProgress.razor as a green "Resolved — fail → pass", and summed into the
    /// client-facing "remediation effort delivered" hours via ValueNarrativeService. A check that
    /// LOST VISIBILITY was being sold as a check that got FIXED. PASS → WARN fell through every
    /// arm and vanished silently.</para>
    ///
    /// <para>The same seam is wider than WARN: SKIP and INFO also ride Passed=true
    /// (CheckExecutionService sets Passed=true for both), so FAIL → SKIP and FAIL → INFO were
    /// filed as Resolved too. That variant PREDATES the WARN slice. The fix is therefore keyed on
    /// the classification discipline rather than on WARN specifically: <b>Resolved requires the
    /// current result to be a scorable PASS</b> — an actual assertion that the control is now
    /// correct. Anything non-scorable now (WARN/SKIP/INFO) is a loss of assertion, not a fix.</para>
    ///
    /// <para>The property, stated once: <b>a check that lost visibility must never report as
    /// fixed, and must never silently disappear.</b></para>
    /// </summary>
    public class WarnBaselineTransitionTests : IDisposable
    {
        private readonly string _tempDir;

        public WarnBaselineTransitionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "warn-transition-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(_tempDir, recursive: true);
            }
            catch { /* test cleanup; ignore */ }
        }

        private GovernanceHistoryService NewService()
            => new(NullLogger<GovernanceHistoryService>.Instance, retentionDays: 365, dbDir: _tempDir);

        /// <summary>A plain scorable check — a real PASS/FAIL assertion, no verdict tier.</summary>
        private static CheckResult Chk(string id, bool passed, double effort = 1.0) => new()
        {
            CheckId = id,
            CheckName = id + " name",
            Category = "Security",
            Severity = "High",
            Passed = passed,
            EffortHours = effort,
        };

        /// <summary>A corpus WARN exactly as CheckExecutionService produces one: Passed=true
        /// ("not a failure"), Verdict="WARN", no ErrorMessage — it ran fine, it just could not
        /// see everything.</summary>
        private static CheckResult WarnChk(string id, double effort = 1.0) => new()
        {
            CheckId = id,
            CheckName = id + " name",
            Category = "Security",
            Severity = "High",
            Passed = true,
            Verdict = "WARN",
            EffortHours = effort,
            Message = "Could not read 3 of 14 databases (insufficient permissions).",
        };

        /// <summary>A verdict-contract SKIP (Passed=true, Verdict="SKIP").</summary>
        private static CheckResult SkipChk(string id) => new()
        {
            CheckId = id,
            CheckName = id + " name",
            Category = "Security",
            Severity = "High",
            Passed = true,
            Verdict = "SKIP",
            EffortHours = 1.0,
        };

        /// <summary>An INFO result (Passed=true, Severity="INFO").</summary>
        private static CheckResult InfoChk(string id) => new()
        {
            CheckId = id,
            CheckName = id + " name",
            Category = "Security",
            Severity = "INFO",
            Passed = true,
            EffortHours = 1.0,
        };

        // ── B1: the reproduction ──────────────────────────────────────────────────────────────

        [Fact]
        public void FailToWarn_IsNeverResolved()
        {
            using var svc = NewService();
            svc.RecordBaseline("SRV1", compositeScore: 70, new[]
            {
                Chk("chk-a", passed: false),   // failing at baseline
                Chk("chk-b", passed: true),
                Chk("chk-c", passed: false),
            });

            // chk-a degrades FAIL → WARN (lost visibility). chk-c genuinely got fixed.
            var t = svc.ComputeTransitions("SRV1", new[]
            {
                WarnChk("chk-a"),
                Chk("chk-b", passed: true),
                Chk("chk-c", passed: true),
            }, currentCompositeScore: 75);

            Assert.NotNull(t);

            // THE defect: chk-a must not be sold as fixed.
            Assert.DoesNotContain(t!.Resolved, x => x.CheckId == "chk-a");
            // The genuine fix must still be reported.
            Assert.Contains(t.Resolved, x => x.CheckId == "chk-c");
            Assert.Single(t.Resolved);

            // A lost-visibility check is not a regression either — the config did not get worse,
            // we simply stopped being able to tell. It gets its own honest bucket.
            Assert.DoesNotContain(t.Regressed, x => x.CheckId == "chk-a");
            Assert.Contains(t.Unassessable, x => x.CheckId == "chk-a");

            // And it must carry which side of the line it fell from, so the page can say
            // "was failing at baseline — now unverifiable" rather than a bare shrug.
            var lost = t.Unassessable.Single(x => x.CheckId == "chk-a");
            Assert.False(lost.BaselineWasPassing);
        }

        [Fact]
        public void PassToWarn_IsSurfaced_NotSilent()
        {
            using var svc = NewService();
            svc.RecordBaseline("SRV1", compositeScore: 90, new[] { Chk("chk-p", passed: true) });

            var t = svc.ComputeTransitions("SRV1", new[] { WarnChk("chk-p") }, currentCompositeScore: 90);

            Assert.NotNull(t);
            // Before the fix this fell through every arm and vanished.
            Assert.Contains(t!.Unassessable, x => x.CheckId == "chk-p");
            var lost = t.Unassessable.Single(x => x.CheckId == "chk-p");
            Assert.True(lost.BaselineWasPassing);

            Assert.Empty(t.Resolved);
            Assert.Empty(t.Regressed);
        }

        [Fact]
        public void FailToSkipOrInfo_IsNeverResolved()
        {
            // The pre-existing variant of the same seam: SKIP and INFO also ride Passed=true.
            using var svc = NewService();
            svc.RecordBaseline("SRV1", compositeScore: 50, new[]
            {
                Chk("chk-skip", passed: false),
                Chk("chk-info", passed: false),
            });

            var t = svc.ComputeTransitions("SRV1", new[]
            {
                SkipChk("chk-skip"),
                InfoChk("chk-info"),
            }, currentCompositeScore: 50);

            Assert.NotNull(t);
            Assert.Empty(t!.Resolved);
            Assert.Contains(t.Unassessable, x => x.CheckId == "chk-skip");
            Assert.Contains(t.Unassessable, x => x.CheckId == "chk-info");
        }

        [Fact]
        public void GenuineFailToPass_StillResolved_AndPassToFail_StillRegressed()
        {
            // Guard against over-correction: the real signals must survive the fix.
            using var svc = NewService();
            svc.RecordBaseline("SRV1", compositeScore: 60, new[]
            {
                Chk("chk-fixed", passed: false),
                Chk("chk-broke", passed: true),
            });

            var t = svc.ComputeTransitions("SRV1", new[]
            {
                Chk("chk-fixed", passed: true),
                Chk("chk-broke", passed: false),
            }, currentCompositeScore: 80);

            Assert.NotNull(t);
            Assert.Contains(t!.Resolved, x => x.CheckId == "chk-fixed");
            Assert.Contains(t.Regressed, x => x.CheckId == "chk-broke");
            Assert.Empty(t.Unassessable);
        }

        [Fact]
        public void WarnAtBaseline_IsNotTreatedAsAPassingBaseline()
        {
            // The baseline side of the same discipline. A WARN frozen INTO a baseline writes
            // passed=1, so on the raw column it reads "was passing" — and a later genuine FAIL
            // would render as a pass → fail REGRESSION that never happened (we never knew it
            // passed). The persisted baseline verdict is what prevents that.
            using var svc = NewService();
            svc.RecordBaseline("SRV1", compositeScore: 70, new[] { WarnChk("chk-w") });

            var t = svc.ComputeTransitions("SRV1", new[] { Chk("chk-w", passed: false) },
                currentCompositeScore: 70);

            Assert.NotNull(t);
            Assert.DoesNotContain(t!.Regressed, x => x.CheckId == "chk-w");
            // We gained visibility and found it failing. That is newly-established failure, not
            // a regression from a state we never observed.
            Assert.Contains(t.NewlyFailing, x => x.CheckId == "chk-w");
        }

        [Fact]
        public void BaselineRowWithNoVerdictRecorded_KeepsPreExistingPassedBasedReading()
        {
            // Baselines frozen before the verdict column existed carry NULL. The honest reading of
            // "we never recorded the tier" is UNKNOWN — so those rows stay on the pre-existing
            // passed-based basis rather than being retro-labelled either way (the same rule
            // CheckClassification.IsWarnVerdict already states for check_results.verdict).
            using var svc = NewService();
            var baselineId = svc.RecordBaseline("SRV1", compositeScore: 60, new[]
            {
                Chk("chk-old-fail", passed: false),
                Chk("chk-old-pass", passed: true),
            });

            // Simulate a pre-migration baseline: blank the verdict column back to NULL.
            NullOutBaselineVerdicts(baselineId);

            var t = svc.ComputeTransitions("SRV1", new[]
            {
                Chk("chk-old-fail", passed: true),   // fail → pass on the raw basis
                Chk("chk-old-pass", passed: false),  // pass → fail on the raw basis
            }, currentCompositeScore: 70);

            Assert.NotNull(t);
            Assert.Contains(t!.Resolved, x => x.CheckId == "chk-old-fail");
            Assert.Contains(t.Regressed, x => x.CheckId == "chk-old-pass");
        }

        // ── the bucket must be RENDERED, or the fix is just a quieter disappearance ───────────

        [Fact]
        public void BaselineProgressPage_RendersTheUnassessableBucket_AndSaysNoFixIsClaimed()
        {
            // Moving FAIL→WARN out of Resolved is only half the fix: if nothing renders the new
            // bucket, a check that lost visibility vanishes from the page instead of being sold as
            // fixed — quieter, but still a false-clean. Asserted against the real shipped markup
            // (BaselineProgress.razor is copied to the test output by SQLTriage.Tests.csproj).
            //
            // NOT claimed by this test: that the section LOOKS right. No pixels were rendered for
            // this change — see the round's report.
            var markup = ReadBaselineProgressMarkup();

            markup.Should().Contain("_t.Unassessable.Count > 0",
                "the section must be driven by the real bucket");
            markup.Should().Contain("@_t.Unassessable.Count",
                "the count must be shown, not just the rows");
            markup.Should().MatchRegex("No fix is claimed and no regression is alleged",
                "the page must state the epistemics plainly — this is the sentence that stops a " +
                "reader inferring either a fix or a breach from a check that asserted neither");
            markup.Should().Contain("BaselineWasPassing",
                "a check that was FAILING at baseline and is now unverifiable is the urgent case " +
                "and must be distinguishable from one that was passing");
            markup.Should().Contain("excluded from the effort-delivered figure",
                "the page must say the lost-visibility checks are NOT in the remediation-hours " +
                "number, because that number is the one a client reads as value delivered");
        }

        private static string ReadBaselineProgressMarkup()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Markup", "BaselineProgress.razor");
            File.Exists(path).Should().BeTrue(
                "BaselineProgress.razor is copied to the test output by SQLTriage.Tests.csproj; " +
                "if this fails the assertions below would vacuously pass");
            return File.ReadAllText(path);
        }

        /// <summary>Rewrites the frozen baseline rows to verdict IS NULL, reproducing a database
        /// created before the baseline verdict column existed.</summary>
        private void NullOutBaselineVerdicts(long baselineId)
        {
            var dbPath = Path.Combine(_tempDir, "governance-history.db");
            using var conn = SqliteCipherHelper.OpenEncrypted($"Data Source={dbPath}");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE baseline_check_results SET verdict = NULL WHERE baseline_id = @bid;";
            cmd.Parameters.AddWithValue("@bid", baselineId);
            cmd.ExecuteNonQuery();
        }
    }
}
