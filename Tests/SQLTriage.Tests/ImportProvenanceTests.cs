/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// #84 results-import: pins the two invariants the /audit grid relies on when it renders
    /// imported runs —
    ///   (1) PRECEDENCE (<see cref="ImportProvenance.ImportedRunWins"/>): honest newest-run-wins,
    ///       so an imported run displaces a server's local run only when strictly newer and is
    ///       never silently merged into it; and
    ///   (2) CLASSIFICATION PARITY: an imported result flows through the shared
    ///       <see cref="CheckClassification"/> pipeline IDENTICALLY to a local one — stamping import
    ///       provenance never changes which Pass/Fail/Skip/Info bucket a result lands in (no
    ///       separate counting path — that is how skip-as-pass was born).
    /// </summary>
    public class ImportProvenanceTests
    {
        private static CheckResult Imported(DateTime? at) =>
            new() { CheckId = "C", InstanceName = "S", ImportedAtUtc = at };

        // ── Provenance predicates ────────────────────────────────────────────
        [Fact]
        public void IsImported_TrueOnlyWhenStamped()
        {
            Assert.True(ImportProvenance.IsImported(Imported(DateTime.UtcNow)));
            Assert.False(ImportProvenance.IsImported(Imported(null)));
        }

        [Fact]
        public void RunIsImported_TrueIfAnyRowImported()
        {
            var local = Imported(null);
            var imported = Imported(DateTime.UtcNow);

            Assert.False(ImportProvenance.RunIsImported(new[] { local }));
            Assert.True(ImportProvenance.RunIsImported(new[] { local, imported })); // gap-fill mix still reads imported
            Assert.False(ImportProvenance.RunIsImported(new List<CheckResult>()));
        }

        [Fact]
        public void RunImportTime_IsMaxImportedAt_OrNull()
        {
            var t1 = new DateTime(2026, 7, 17, 9, 0, 0, DateTimeKind.Utc);
            var t2 = new DateTime(2026, 7, 17, 10, 0, 0, DateTimeKind.Utc);

            Assert.Equal(t2, ImportProvenance.RunImportTime(new[] { Imported(t1), Imported(t2), Imported(null) }));
            Assert.Null(ImportProvenance.RunImportTime(new[] { Imported(null) }));
        }

        // ── Precedence rule: newest run wins, ties keep the local incumbent ──
        [Fact]
        public void ImportedRunWins_WhenLocalTimeUnknown()
        {
            // A purely imported-only server (no configured local run) — imported always shows.
            Assert.True(ImportProvenance.ImportedRunWins(DateTime.UtcNow, localRunTimeUtc: null));
        }

        [Fact]
        public void ImportedRunWins_WhenStrictlyNewerThanLocal()
        {
            var local = new DateTime(2026, 7, 17, 9, 0, 0, DateTimeKind.Utc);
            var importedNewer = local.AddMinutes(1);
            Assert.True(ImportProvenance.ImportedRunWins(importedNewer, local));
        }

        [Fact]
        public void ImportedRunLoses_WhenOlderThanOrEqualToLocal()
        {
            var local = new DateTime(2026, 7, 17, 9, 0, 0, DateTimeKind.Utc);
            Assert.False(ImportProvenance.ImportedRunWins(local.AddMinutes(-1), local)); // older → local wins
            Assert.False(ImportProvenance.ImportedRunWins(local, local));               // tie → local (incumbent) wins
        }

        // ── Classification parity: provenance never perturbs the bucket ──────
        // Every result shape is classified as a LOCAL twin (no provenance) and an IMPORTED twin
        // (ImportedAtUtc + source file stamped). All four CheckClassification predicates must agree
        // across the pair — stamping provenance must never move a result between Pass/Fail/Skip/Info,
        // because both flow through the identical classification path.
        [Fact]
        public void Classification_IsIdentical_WhetherOrNotImported()
        {
            var shapes = new (string Name, Func<CheckResult> Make)[]
            {
                ("pass",         () => new CheckResult { CheckId = "C", Passed = true }),
                ("openFail",     () => new CheckResult { CheckId = "C", Passed = false }),
                ("severityInfo", () => new CheckResult { CheckId = "C", Passed = true, Severity = "INFO" }),
                ("verdictInfo",  () => new CheckResult { CheckId = "C", Passed = true, Severity = "High", Verdict = "INFO" }),
                ("verdictSkip",  () => new CheckResult { CheckId = "C", Passed = true, Severity = "High", Verdict = "SKIP", Message = "No AGs configured." }),
                ("messageSkip",  () => new CheckResult { CheckId = "C", Passed = true, Message = "SKIP - not applicable" }),
                ("errorSkip",    () => new CheckResult { CheckId = "C", Passed = false, ErrorMessage = "permission denied" }),
                ("acceptedFail", () => new CheckResult { CheckId = "C", Passed = false, IsAccepted = true }),
            };

            foreach (var (name, make) in shapes)
            {
                var local = make();       // no provenance
                var imported = make();    // same shape, now stamped imported
                imported.ImportedAtUtc = DateTime.UtcNow;
                imported.ImportSourceFile = "capture.json";

                Assert.True(CheckClassification.IsSkip(local)      == CheckClassification.IsSkip(imported),      $"IsSkip diverged for '{name}'");
                Assert.True(CheckClassification.IsInfo(local)      == CheckClassification.IsInfo(imported),      $"IsInfo diverged for '{name}'");
                Assert.True(CheckClassification.IsScorable(local)  == CheckClassification.IsScorable(imported),  $"IsScorable diverged for '{name}'");
                Assert.True(CheckClassification.CountsAsPass(local) == CheckClassification.CountsAsPass(imported), $"CountsAsPass diverged for '{name}'");
            }
        }
    }
}
