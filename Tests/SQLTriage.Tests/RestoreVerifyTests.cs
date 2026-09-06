/* In the name of God, the Merciful, the Compassionate */
/*
 * MSP #9 — automated restore-test, VERIFYONLY-lite tier.
 *
 * Synthetic-data tests. NO SQL, NO client data, NO network — fabricated DTOs and SQL error
 * NUMBERS only. Pins:
 *   1) HEADLINE — the failed-vs-couldn't-run classifier. A corrupt-backup fault and a
 *      path-unreadable fault MUST land in DISTINCT outcomes: "verify FAILED" (the backup is
 *      corrupt = the real recoverability signal) vs "verify COULD NOT RUN" (environment: path
 *      unreadable / TDE cert missing / timeout). A naive impl that dumps every RESTORE exception
 *      into one Status=Failed field would conflate them and over/under-claim recoverability.
 *      Corruption must NEVER be masked as could-not-run (corrupt-first precedence).
 *   2) Striped-backup statement generation — all stripes in ONE RESTORE ... FROM DISK=a,DISK=b,
 *      and CHECKSUM is only forced when the backup actually carries checksums (else SQL 3187).
 *   3) Most-recent-full selection — newest full per DB, stripes assembled in family order.
 *   4) Opt-in — a fresh install with no created task never runs a restore-verify.
 *   5) Lite-tier honest omission — URL/Azure-blob (WITH CREDENTIAL) and non-disk media are
 *      "not supported", never a false pass.
 *
 * The SQL error numbers below are grounded in a LIVE probe on local SQL 2017 (14.0.2110.2) AND SQL 2022
 * (16.0.4252.3), 2026-07-09 (see the MSP #9 report). Identical on BOTH instances: a WITH CHECKSUM verify
 * over a single content byte flipped in the backup raised 3203 (+3013) — "Read … failed: 13(The data is
 * invalid.)" — the WITH-CHECKSUM content-damage signal, so 3203 is corrupt-first (3183/3189 NEVER fired
 * on this path); a truncated backup also raised 3203; a garbage / header-damaged file raised 3241
 * (malformed media family); a missing OR exclusively-locked backup path raised 3201; forcing WITH CHECKSUM
 * on a non-checksum backup raises 3187. 3271 (nonrecoverable device I/O fault) could NOT be provoked from
 * content damage — that path is 3203/3241 — so it stays could-not-run, but LOUD. This REVERSES commit
 * d33f509, which had mis-filed 3203 as could-not-run and thereby under-alarmed a corrupt backup as merely
 * "could not be checked" — the exact honesty failure this suite exists to catch.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class RestoreVerifyTests
    {
        // ── Pin 1: HEADLINE — failed (corrupt) vs could-not-run (environment) are DISTINCT ──────

        [Fact]
        public void Classify_CorruptBackup_And_PathUnreadable_AreDistinct()
        {
            // Corrupt backup CONTENT — the real recoverability signal. 3203 = the WITH CHECKSUM content-
            // damage error (empirically the byte-flip / truncation signal on BOTH instances — the flagship
            // path); 3183 = page-level damage; 3189 = backup-set checksum damage; 3241 = malformed media.
            var checksumDamage = RestoreVerifyService.Classify(new[] { 3203 });
            var corrupt = RestoreVerifyService.Classify(new[] { 3189 });
            var corrupt2 = RestoreVerifyService.Classify(new[] { 3183 });
            var malformed = RestoreVerifyService.Classify(new[] { 3241 });
            // Path unreadable / missing / access-denied to the SQL service account (empirically 3201).
            var pathGone = RestoreVerifyService.Classify(new[] { 3201 });

            Assert.Equal(RestoreVerifyOutcome.Failed, checksumDamage.Outcome);
            Assert.Equal(RestoreVerifyOutcome.Failed, corrupt.Outcome);
            Assert.Equal(RestoreVerifyOutcome.Failed, corrupt2.Outcome);
            Assert.Equal(RestoreVerifyOutcome.Failed, malformed.Outcome);
            Assert.Equal(RestoreVerifyOutcome.CouldNotRun, pathGone.Outcome);

            // THE pin: a corrupt backup and an unreadable path must NOT collapse to the same outcome.
            Assert.NotEqual(checksumDamage.Outcome, pathGone.Outcome);
            Assert.NotEqual(corrupt.Outcome, pathGone.Outcome);
        }

        [Fact]
        public void Classify_WithChecksumContentDamage_3203_IsFailed_And_3271_IsCouldNotRunLoud()
        {
            // LIVE probe (SQL 2017 14.0.2110.2 + SQL 2022 16.0.4252.3, 2026-07-09): a single content byte
            // flipped in a WITH CHECKSUM backup, verified WITH CHECKSUM, raised 3203 (+3013) on BOTH
            // instances — "Read on '…' failed: 13(The data is invalid.)". 3203 IS the flagship WITH-CHECKSUM
            // content-damage signal, so it MUST be Failed/corrupt (this REVERSES d33f509). Reporting it
            // could-not-run would under-alarm a genuinely corrupt backup as "could not be checked" — the
            // exact false-safety MSP #9 exists to prevent.
            var damage = RestoreVerifyService.Classify(new[] { 3203 });
            Assert.Equal(RestoreVerifyOutcome.Failed, damage.Outcome);
            Assert.NotEqual(RestoreVerifyOutcome.CouldNotRun, damage.Outcome);

            // 3013 ("VERIFY … terminating abnormally") ACCOMPANIES 3203 on every real verify — corruption
            // still wins by precedence; 3013 must never demote a corrupt backup to could-not-run.
            var withTerminator = RestoreVerifyService.Classify(new[] { 3203, 3013 });
            Assert.Equal(RestoreVerifyOutcome.Failed, withTerminator.Outcome);

            // 3271 ("a nonrecoverable I/O error occurred on file") — the probe could NOT provoke this from
            // content damage (that path is 3203/3241) nor from a missing/locked file (that is 3201), so it
            // does not by itself PROVE corruption ⇒ could-not-run. But the reason must be LOUD: it flags
            // possible damaged/failing media and that the backup was NOT confirmed readable — never a bland
            // "could not be checked" that reads as safe.
            var io = RestoreVerifyService.Classify(new[] { 3271 });
            Assert.Equal(RestoreVerifyOutcome.CouldNotRun, io.Outcome);
            Assert.NotEqual(RestoreVerifyOutcome.Passed, io.Outcome);
            Assert.Contains("nonrecoverable", io.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("media", io.Reason, StringComparison.OrdinalIgnoreCase);

            // Content damage raised ALONGSIDE the 3271 I/O fault still WINS (corruption-first precedence).
            var mixed = RestoreVerifyService.Classify(new[] { 3271, 3203 });
            Assert.Equal(RestoreVerifyOutcome.Failed, mixed.Outcome);
        }

        [Fact]
        public void Classify_BackupSetChecksumDamage_3189_IsFailed_NotCouldNotRun()
        {
            // 3189 = "Damage to the backup set was detected" — the error a WITH CHECKSUM verify raises
            // when the backup-set (media) checksum fails but no page-level 3183 is emitted. A damaged
            // backup set is a real recoverability FAILURE; classifying it CouldNotRun under-alarms the
            // flagship WITH CHECKSUM path (would report a corrupt backup as merely un-checked).
            var damaged = RestoreVerifyService.Classify(new[] { 3189 });
            Assert.Equal(RestoreVerifyOutcome.Failed, damaged.Outcome);
            Assert.NotEqual(RestoreVerifyOutcome.CouldNotRun, damaged.Outcome);

            // And it must WIN over the generic 3013 terminator that accompanies it on a real verify.
            var withTerminator = RestoreVerifyService.Classify(new[] { 3189, 3013 });
            Assert.Equal(RestoreVerifyOutcome.Failed, withTerminator.Outcome);
        }

        [Fact]
        public void Classify_TdeCertMissing_IsCouldNotRun_NotFailed()
        {
            var tde = RestoreVerifyService.Classify(new[] { 33111 });
            Assert.Equal(RestoreVerifyOutcome.CouldNotRun, tde.Outcome);
            // A missing TDE cert is an environment gap, not a corrupt backup — must not read as Failed.
            Assert.NotEqual(RestoreVerifyOutcome.Failed, tde.Outcome);
        }

        [Fact]
        public void Classify_UnknownOrEmpty_DefaultsToCouldNotRun_NeverFailedNeverPassed()
        {
            // We must not FABRICATE a corrupt verdict from an error we cannot interpret, nor claim a pass.
            Assert.Equal(RestoreVerifyOutcome.CouldNotRun, RestoreVerifyService.Classify(new[] { 9999 }).Outcome);
            Assert.Equal(RestoreVerifyOutcome.CouldNotRun, RestoreVerifyService.Classify(Array.Empty<int>()).Outcome);
        }

        [Fact]
        public void Classify_CorruptTakesPrecedence_OverEnvironmentError()
        {
            // A single RESTORE can raise several errors (e.g. corruption + the generic 3013 terminator,
            // or a device error). Corruption must WIN so a real recoverability failure is never hidden.
            var mixed = RestoreVerifyService.Classify(new[] { 3201, 3183, 3013 });
            Assert.Equal(RestoreVerifyOutcome.Failed, mixed.Outcome);
        }

        [Fact]
        public void Classify_PathAndTde_ProduceDifferentReasons()
        {
            var path = RestoreVerifyService.Classify(new[] { 3201 });
            var tde = RestoreVerifyService.Classify(new[] { 33111 });
            Assert.NotEqual(path.Reason, tde.Reason);
            // Reasons are fixed, safe strings — never a raw exception message (design §4.5).
            Assert.DoesNotContain("password", path.Reason, StringComparison.OrdinalIgnoreCase);
        }

        // ── Pin 2: striped-backup statement generation (all stripes in one RESTORE) ──────────────

        [Fact]
        public void BuildStatement_StripedBackup_EmitsAllDisksInOneRestore_WithChecksum()
        {
            var b = new BackupToVerify("Sales", 42, new DateTime(2026, 7, 9), true, new[]
            {
                new StripeFile(2, 2, @"Y:\bk\sales_2.bak"),
                new StripeFile(1, 2, @"X:\bk\sales_1.bak"),
            });

            var sql = RestoreVerifyService.BuildVerifyStatement(b);

            // ONE statement, both stripes, ordered by family sequence (STRIPED-safe).
            Assert.Equal(
                @"RESTORE VERIFYONLY FROM DISK = N'X:\bk\sales_1.bak', DISK = N'Y:\bk\sales_2.bak' WITH CHECKSUM",
                sql);
        }

        [Fact]
        public void BuildStatement_NoChecksumBackup_UsesNoChecksum_NotForcedChecksum()
        {
            // Forcing WITH CHECKSUM on a backup taken WITHOUT checksums raises SQL 3187 (empirically
            // confirmed) — which would be a FALSE "could not run". Verify-without-checksum instead.
            var b = new BackupToVerify("Ops", 7, new DateTime(2026, 7, 9), false,
                new[] { new StripeFile(1, 2, @"D:\b\ops.bak") });

            var sql = RestoreVerifyService.BuildVerifyStatement(b);
            Assert.Equal(@"RESTORE VERIFYONLY FROM DISK = N'D:\b\ops.bak' WITH NO_CHECKSUM", sql);
        }

        [Fact]
        public void BuildStatement_EscapesSingleQuotesInPath()
        {
            var b = new BackupToVerify("Weird", 1, new DateTime(2026, 7, 9), true,
                new[] { new StripeFile(1, 2, @"D:\o'brien\db.bak") });

            var sql = RestoreVerifyService.BuildVerifyStatement(b);
            Assert.Contains(@"N'D:\o''brien\db.bak'", sql);
        }

        // ── Pin 3: most-recent-full selection ────────────────────────────────────────────────────

        [Fact]
        public void SelectMostRecentFull_PicksNewestPerDb_AssemblesStripesInOrder()
        {
            var older = new DateTime(2026, 7, 1);
            var newer = new DateTime(2026, 7, 8);
            var candidates = new List<BackupCandidate>
            {
                // DB "A" — an OLD single-file full (media set 100) and a NEW striped full (media set 200).
                new("A", 100, older, true, 1, 2, @"X:\a_old.bak"),
                new("A", 200, newer, true, 2, 2, @"Y:\a_new_2.bak"),
                new("A", 200, newer, true, 1, 2, @"X:\a_new_1.bak"),
                // DB "B" — one full.
                new("B", 300, newer, false, 1, 2, @"X:\b.bak"),
            };

            var picked = RestoreVerifyService.SelectMostRecentFull(candidates);

            var a = picked.Single(p => p.DatabaseName == "A");
            Assert.Equal(200, a.MediaSetId);                       // newest set won
            Assert.Equal(2, a.Stripes.Count);
            Assert.Equal(@"X:\a_new_1.bak", a.Stripes[0].PhysicalName);  // ordered by family sequence
            Assert.Equal(@"Y:\a_new_2.bak", a.Stripes[1].PhysicalName);

            var b = picked.Single(p => p.DatabaseName == "B");
            Assert.False(b.HasChecksums);
            Assert.Equal(2, picked.Count);
        }

        // ── Pin 5: lite-tier honest omission (never a false pass) ────────────────────────────────

        [Fact]
        public void IsSupported_UrlBackup_IsNotSupported_NeverAPass()
        {
            var url = new BackupToVerify("Cloud", 1, new DateTime(2026, 7, 9), true,
                new[] { new StripeFile(1, 9, "https://acct.blob.core.windows.net/bk/cloud.bak") });
            var (supported, reason) = RestoreVerifyService.IsSupported(url);
            Assert.False(supported);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Fact]
        public void IsSupported_DiskBackup_IsSupported()
        {
            var disk = new BackupToVerify("Local", 1, new DateTime(2026, 7, 9), true,
                new[] { new StripeFile(1, 2, @"D:\bk\local.bak") });
            Assert.True(RestoreVerifyService.IsSupported(disk).Supported);
        }

        // ── Pin 4: opt-in — a fresh install with no created task never runs a restore-verify ─────

        [Fact]
        public void OptIn_FreshDefinitions_HaveNoRestoreVerifyTask()
        {
            var svc = new ScheduledTaskDefinitionService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ScheduledTaskDefinitionService>.Instance);
            Assert.DoesNotContain(svc.GetEnabledTasks(), t => t.TaskType == TaskType.RestoreVerify);
        }

        // ── Pin 6: ReadResultsFile round-trips SaveResults — the HA/DR & Backup Posture report's
        //    restore-verify section reads this artifact back to recompose a pass-% verdict, so the
        //    reader MUST agree with the writer on this class's own format. ─────────────────────────

        [Fact]
        public void ReadResultsFile_RoundTrips_SaveResults_MixedOutcomes()
        {
            var now = new DateTime(2026, 7, 16, 3, 0, 0, DateTimeKind.Utc);
            var results = new List<RestoreVerifyResult>
            {
                new RestoreVerifyResult("AdventureWorks", RestoreVerifyOutcome.Passed,
                    "Backup set verified (checksums validated).", now.AddDays(-1), 1, true),
                new RestoreVerifyResult("Contoso", RestoreVerifyOutcome.Failed,
                    "Backup verification FAILED — the backup set is damaged or corrupt (SQL error 3203). " +
                    "This backup is not a reliable recovery point.", now.AddDays(-1), 1, true),
                new RestoreVerifyResult("Northwind", RestoreVerifyOutcome.CouldNotRun,
                    "Could not verify — the backup file is missing, unreadable, or access-denied to the " +
                    "SQL Server service account (SQL error 3201). The backup could not be checked.",
                    now.AddDays(-2), 1, false),
                new RestoreVerifyResult("Fabrikam", RestoreVerifyOutcome.NotSupported,
                    "Not supported in the lite tier — this is a URL/Azure-blob backup (requires WITH " +
                    "CREDENTIAL). Reported as not-checked, never a pass.", now.AddDays(-1), 1, false),
            };

            var path = RestoreVerifyService.SaveResults("TestServer01", results, now);
            try
            {
                var summaryOrNull = RestoreVerifyService.ReadResultsFile(path);
                Assert.NotNull(summaryOrNull);
                var summary = summaryOrNull!.Value;

                Assert.Equal(1, summary.Passed);
                Assert.Equal(1, summary.Failed);
                Assert.Equal(1, summary.CouldNotRun);
                Assert.Equal(1, summary.NotSupported);
                Assert.Equal(4, summary.Items.Count);

                var failedItem = summary.Items.Single(i => i.Outcome == RestoreVerifyOutcome.Failed);
                Assert.Equal("Contoso", failedItem.DatabaseName);
                Assert.Contains("damaged or corrupt", failedItem.Reason);

                var couldNotItem = summary.Items.Single(i => i.Outcome == RestoreVerifyOutcome.CouldNotRun);
                Assert.Equal("Northwind", couldNotItem.DatabaseName);

                var passedItem = summary.Items.Single(i => i.Outcome == RestoreVerifyOutcome.Passed);
                Assert.Equal("AdventureWorks", passedItem.DatabaseName);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ReadResultsFile_MissingFile_ReturnsNull_NeverFabricatesAResult()
        {
            var missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.txt");
            Assert.Null(RestoreVerifyService.ReadResultsFile(missing));
        }

        [Fact]
        public void ReadResultsFile_NullOrEmptyPath_ReturnsNull()
        {
            Assert.Null(RestoreVerifyService.ReadResultsFile(null));
            Assert.Null(RestoreVerifyService.ReadResultsFile(""));
        }

        // ── Pin 7: two verifies of one server inside one second keep TWO records ─────────────────
        //    The artifact IS the record — RESTORE VERIFYONLY writes nothing to msdb — so an
        //    overwritten file is a run that left no trace anywhere.

        private static List<RestoreVerifyResult> OneResult(string db, RestoreVerifyOutcome outcome, DateTime backupDate)
            => new()
            {
                new RestoreVerifyResult(db, outcome, $"reason for {db}", backupDate, 1, true),
            };

        [Fact]
        public void SaveResults_SameServerSameSecond_WritesTwoFiles_AndDestroysNeither()
        {
            // Identical to the second, which is all the old name carried.
            var now = new DateTime(2026, 8, 6, 11, 22, 33, DateTimeKind.Utc);
            var server = $"CollideSvr{Guid.NewGuid():N}";

            var firstPath = RestoreVerifyService.SaveResults(
                server, OneResult("FirstRunDb", RestoreVerifyOutcome.Failed, now.AddDays(-1)), now);
            var secondPath = RestoreVerifyService.SaveResults(
                server, OneResult("SecondRunDb", RestoreVerifyOutcome.Passed, now.AddDays(-1)), now);

            try
            {
                Assert.NotEqual(firstPath, secondPath);
                Assert.True(File.Exists(firstPath), "the first run's record was destroyed by the second");
                Assert.True(File.Exists(secondPath));

                // Not just two files — each still holds ITS OWN run. A swap would pass a
                // distinct-paths assertion on its own.
                var first = RestoreVerifyService.ReadResultsFile(firstPath);
                var second = RestoreVerifyService.ReadResultsFile(secondPath);
                Assert.NotNull(first);
                Assert.NotNull(second);
                Assert.Equal("FirstRunDb", Assert.Single(first!.Value.Items).DatabaseName);
                Assert.Equal(1, first.Value.Failed);
                Assert.Equal("SecondRunDb", Assert.Single(second!.Value.Items).DatabaseName);
                Assert.Equal(1, second.Value.Passed);
            }
            finally
            {
                File.Delete(firstPath);
                File.Delete(secondPath);
            }
        }

        [Fact]
        public void SaveResults_TenSameSecondRuns_KeepTenDistinctRecords()
        {
            var now = new DateTime(2026, 8, 6, 11, 22, 33, DateTimeKind.Utc);
            var server = $"CollideSvr{Guid.NewGuid():N}";
            var paths = new List<string>();

            try
            {
                for (var i = 0; i < 10; i++)
                {
                    paths.Add(RestoreVerifyService.SaveResults(
                        server, OneResult($"Db{i}", RestoreVerifyOutcome.Passed, now.AddDays(-1)), now));
                }

                Assert.Equal(10, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Assert.Equal(10, paths.Count(File.Exists));

                for (var i = 0; i < 10; i++)
                {
                    var read = RestoreVerifyService.ReadResultsFile(paths[i]);
                    Assert.NotNull(read);
                    Assert.Equal($"Db{i}", Assert.Single(read!.Value.Items).DatabaseName);
                }
            }
            finally
            {
                foreach (var p in paths) { try { File.Delete(p); } catch (IOException) { } }
            }
        }

        [Fact]
        public void SaveResults_FirstRunOfASecond_KeepsThePlainTimestampedName()
        {
            // The uniquifier must not tax the normal case: the ordinal only appears on collision.
            var now = new DateTime(2026, 8, 6, 11, 22, 33, DateTimeKind.Utc);
            var server = $"SoloSvr{Guid.NewGuid():N}";

            var path = RestoreVerifyService.SaveResults(
                server, OneResult("OnlyDb", RestoreVerifyOutcome.Passed, now.AddDays(-1)), now);
            try
            {
                Assert.Equal($"RestoreVerify_{server}_20260806_112233Z.txt", Path.GetFileName(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void SaveResults_ConcurrentSameSecondRuns_LoseNothing()
        {
            // The File.Exists-then-write shape passes the sequential tests above and still loses a
            // record under a race. FileMode.CreateNew is what makes this one pass.
            var now = new DateTime(2026, 8, 6, 11, 22, 33, DateTimeKind.Utc);
            var server = $"RaceSvr{Guid.NewGuid():N}";
            var paths = new System.Collections.Concurrent.ConcurrentBag<string>();

            System.Threading.Tasks.Parallel.For(0, 16, i =>
            {
                paths.Add(RestoreVerifyService.SaveResults(
                    server, OneResult($"RaceDb{i}", RestoreVerifyOutcome.Passed, now.AddDays(-1)), now));
            });

            var all = paths.ToList();
            try
            {
                Assert.Equal(16, all.Count);
                Assert.Equal(16, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Assert.Equal(16, all.Count(File.Exists));

                // Every one of the 16 databases survived somewhere; none was overwritten.
                var seen = all
                    .Select(RestoreVerifyService.ReadResultsFile)
                    .Where(s => s.HasValue)
                    .SelectMany(s => s!.Value.Items.Select(i => i.DatabaseName))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < 16; i++)
                    Assert.Contains($"RaceDb{i}", seen);
            }
            finally
            {
                foreach (var p in all) { try { File.Delete(p); } catch (IOException) { } }
            }
        }
    }
}
