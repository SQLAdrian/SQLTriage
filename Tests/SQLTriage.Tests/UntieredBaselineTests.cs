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
    /// A3 (Adrian ruled it in, 2026-07-20) — pre-migration baselines carry no verdict, so a WARN
    /// frozen into one is indistinguishable from a PASS.
    ///
    /// <para>Baselines frozen before <c>baseline_check_results.verdict</c> existed record only the
    /// raw <c>passed</c> bit, and PASS, WARN, SKIP and INFO all wrote 1. A check that was WARN at
    /// baseline and genuinely FAILS now therefore reads as "Regressed (pass → fail)" — a fall from
    /// a state that was never asserted. The gate reproduced it live: AFTER-NULL Regressed =
    /// [chk-pass-c, chk-warnbase]. <b>This is in client deliverables now.</b></para>
    ///
    /// <para><b>THE RULING: surface it, do not correct it.</b> Reclassifying those transitions would
    /// mean rewriting what a client was already told, so the classification is left exactly as it
    /// was and the ambiguity is MARKED — at the number, on the page where it is read, not in a
    /// design note nobody opens.</para>
    /// </summary>
    public class UntieredBaselineTests : IDisposable
    {
        private readonly string _tempDir;

        public UntieredBaselineTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "untiered-baseline-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(_tempDir, recursive: true);
            }
            catch { /* test cleanup */ }
        }

        private GovernanceHistoryService NewService()
            => new(NullLogger<GovernanceHistoryService>.Instance, retentionDays: 365, dbDir: _tempDir);

        private static CheckResult Chk(string id, bool passed) => new()
        {
            CheckId = id, CheckName = id + " name", Category = "Security",
            Severity = "High", Passed = passed, EffortHours = 1.0,
        };

        private static CheckResult WarnChk(string id) => new()
        {
            CheckId = id, CheckName = id + " name", Category = "Security", Severity = "High",
            Passed = true, Verdict = "WARN", EffortHours = 1.0,
            Message = "Could not read 3 of 14 databases (insufficient permissions).",
        };

        // ── the reproduction, and what the fix adds to it ─────────────────────────────────────

        [Fact]
        public void APreMigrationBaseline_StillReportsTheRegression_ButFlagsItAsUntiered()
        {
            using var svc = NewService();

            // chk-warnbase was a WARN at baseline; chk-pass-c was a genuine PASS. Both freeze
            // passed=1, and blanking the verdict column makes them indistinguishable — exactly a
            // baseline captured before ruling #4 shipped.
            var baselineId = svc.RecordBaseline("SRV1", compositeScore: 70, new[]
            {
                WarnChk("chk-warnbase"),
                Chk("chk-pass-c", passed: true),
            });
            NullOutBaselineVerdicts(baselineId);

            var t = svc.ComputeTransitions("SRV1", new[]
            {
                Chk("chk-warnbase", passed: false),
                Chk("chk-pass-c",   passed: false),
            }, currentCompositeScore: 60);

            Assert.NotNull(t);

            // The classification is DELIBERATELY unchanged — history is not retro-labelled.
            t!.Regressed.Select(x => x.CheckId)
                .Should().BeEquivalentTo(new[] { "chk-warnbase", "chk-pass-c" },
                    "the ruling is to surface the ambiguity, NOT to rewrite a client's recorded history");

            // What A3 adds: every one of those rows now says its baseline had no tier recorded.
            t.Regressed.Should().OnlyContain(x => x.BaselineTierUnknown,
                "both were compared against untiered baseline rows, so neither comparison can " +
                "distinguish a genuine pass from a run that asserted nothing");

            t.BaselineIsPreMigration.Should().BeTrue();
            t.UntieredBaselineChecks.Should().Be(2);
            t.BaselineCheckCount.Should().Be(2);
        }

        [Fact]
        public void ATieredBaseline_IsNotFlagged_AndItsRowsAreNotMarked()
        {
            // Guard against over-correction: caveating every baseline would make the caveat noise,
            // and noise is ignored. A baseline frozen WITH verdicts must come back clean.
            using var svc = NewService();
            svc.RecordBaseline("SRV1", compositeScore: 70, new[]
            {
                Chk("chk-a", passed: true),
                Chk("chk-b", passed: false),
            });

            var t = svc.ComputeTransitions("SRV1", new[]
            {
                Chk("chk-a", passed: false),   // genuine pass -> fail
                Chk("chk-b", passed: true),    // genuine fail -> pass
            }, currentCompositeScore: 70);

            Assert.NotNull(t);
            t!.BaselineIsPreMigration.Should().BeFalse();
            t.UntieredBaselineChecks.Should().Be(0);
            t.Regressed.Should().OnlyContain(x => !x.BaselineTierUnknown);
            t.Resolved.Should().OnlyContain(x => !x.BaselineTierUnknown);
        }

        [Fact]
        public void AResolvedRow_IsMarkedToo_NotJustRegressed()
        {
            // The ambiguity cuts both ways: on an untiered baseline a "fail at baseline" is also
            // just the raw bit, so a Resolved claim rests on the same unknown. Marking only the
            // bad news would be its own bias.
            using var svc = NewService();
            var baselineId = svc.RecordBaseline("SRV1", compositeScore: 40, new[] { Chk("chk-x", passed: false) });
            NullOutBaselineVerdicts(baselineId);

            var t = svc.ComputeTransitions("SRV1", new[] { Chk("chk-x", passed: true) }, currentCompositeScore: 90);

            Assert.NotNull(t);
            t!.Resolved.Should().ContainSingle();
            t.Resolved[0].BaselineTierUnknown.Should().BeTrue();
        }

        [Fact]
        public void APartiallyMigratedBaseline_CountsOnlyTheUntieredRows()
        {
            // A baseline can be mixed if rows were written across the migration. The count must be
            // the real number, not a boolean smeared across every row.
            using var svc = NewService();
            var baselineId = svc.RecordBaseline("SRV1", compositeScore: 70, new[]
            {
                Chk("chk-old", passed: true),
                Chk("chk-new", passed: true),
            });
            NullOutBaselineVerdicts(baselineId, onlyCheckId: "chk-old");

            var t = svc.ComputeTransitions("SRV1", new[]
            {
                Chk("chk-old", passed: false),
                Chk("chk-new", passed: false),
            }, currentCompositeScore: 60);

            Assert.NotNull(t);
            t!.UntieredBaselineChecks.Should().Be(1);
            t.BaselineCheckCount.Should().Be(2);
            t.BaselineIsPreMigration.Should().BeTrue();

            t.Regressed.Single(x => x.CheckId == "chk-old").BaselineTierUnknown.Should().BeTrue();
            t.Regressed.Single(x => x.CheckId == "chk-new").BaselineTierUnknown.Should().BeFalse();
        }

        // ── the ruling was "surface it in the UI", so the UI is what is checked ───────────────

        [Fact]
        public void BaselineProgressPage_CaveatsTheNumberWhereItIsRead()
        {
            // The ruling is explicit that a design note nobody opens is not a caveat. Asserted
            // against the real shipped markup (copied to the test output by SQLTriage.Tests.csproj).
            //
            // NOT claimed by this test: that the banner LOOKS right, or that it sits where a reader
            // will see it. No pixels were rendered. Nor that the block is REACHABLE at runtime —
            // an enclosing @if or a CSS rule could still hide it; what is pinned is the condition
            // on the block's own guard, which is where the last mutant hid.
            var markup = ReadBaselineProgressMarkup();

            // Pinned as the WHOLE condition, not merely the identifier. A bare
            // Contain("_t.BaselineIsPreMigration") is satisfied by the identifier appearing
            // ANYWHERE, including inside a condition that can never be true: the gate mutated the
            // guard to '@if (false && _t.BaselineIsPreMigration)', making the banner unreachable,
            // and all five tests in this class stayed GREEN. Requiring the closing paren to follow
            // the flag immediately means any added conjunct, constant or negation moves it and
            // fails. This is the same defect class as M14 below — twice in one round, now three.
            markup.Should().MatchRegex(@"@if \(_t\.BaselineIsPreMigration\)\s*\{",
                "the caveat's guard condition must be EXACTLY the real pre-migration flag — a " +
                "condition that merely mentions the flag can still be constant-false, which is " +
                "how an unreachable banner passed a Contain() pin on this very line");
            markup.Should().Contain("@_t.UntieredBaselineChecks",
                "and must state how many checks are affected, not just that some are");
            // Asserted against the DRIVING EXPRESSION, not merely the identifier. Bare
            // Contain("BaselineTierUnknown") / Contain("tier not recorded") assertions were
            // satisfied by markup that had been made unreachable, so mutation M14 — which set
            // anyUntiered to a constant false and killed the whole column — SURVIVED them. Caught
            // by mutation, not by review; this repo's dominant defect class, twice in one round.
            // Pinned as the WHOLE assignment for the same reason as the guard above: an RHS pin
            // that is a substring survives 'false && rows.Any(...)'.
            markup.Should().Contain("var anyUntiered = rows.Any(r => r.BaselineTierUnknown);",
                "the column must be driven by the real per-row flag and by NOTHING else, so it " +
                "appears exactly when a table actually contains an untiered comparison");
            markup.Should().Contain("@if (c.BaselineTierUnknown)",
                "and each ROW must be marked from its own flag — a page-level banner alone leaves " +
                "the reader unable to tell WHICH regression is the doubtful one");
            markup.Should().Contain("tier not recorded",
                "the row marker needs words, not just a colour");
            markup.Should().MatchRegex(
                "Regressed.{0,120}may be a check that was never actually shown to\\s+pass",
                "the page must state the consequence in plain language — this is the sentence that " +
                "stops a reader acting on a regression that may never have happened");
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────

        private static string ReadBaselineProgressMarkup()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Markup", "BaselineProgress.razor");
            File.Exists(path).Should().BeTrue(
                "BaselineProgress.razor is copied to the test output by SQLTriage.Tests.csproj; " +
                "without it the assertions below would vacuously pass");
            return File.ReadAllText(path);
        }

        /// <summary>Rewrites frozen baseline rows to verdict IS NULL, reproducing a database
        /// created before the baseline verdict column existed.</summary>
        private void NullOutBaselineVerdicts(long baselineId, string? onlyCheckId = null)
        {
            var dbPath = Path.Combine(_tempDir, "governance-history.db");
            using var conn = SqliteCipherHelper.OpenEncrypted($"Data Source={dbPath}");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = onlyCheckId == null
                ? "UPDATE baseline_check_results SET verdict = NULL WHERE baseline_id = @bid;"
                : "UPDATE baseline_check_results SET verdict = NULL WHERE baseline_id = @bid AND check_id = @cid;";
            cmd.Parameters.AddWithValue("@bid", baselineId);
            if (onlyCheckId != null) cmd.Parameters.AddWithValue("@cid", onlyCheckId);
            cmd.ExecuteNonQuery();
        }
    }
}
