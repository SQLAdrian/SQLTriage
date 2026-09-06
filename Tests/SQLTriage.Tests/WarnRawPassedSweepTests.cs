/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Cli;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The raw-<c>.Passed</c> sweep (Adrian ruled it in, 2026-07-20).
    ///
    /// <para>Three rounds each surfaced a NEW false-clean at the same seam: a consumer reading raw
    /// <see cref="CheckResult.Passed"/> outside the classification discipline. WARN, SKIP and INFO
    /// all ride Passed=true, so any such consumer silently promotes a non-assertion to a pass.
    /// These tests probe the consumers the sweep found and PIN the ruling made on each, so the
    /// next round cannot rediscover them.</para>
    /// </summary>
    public class WarnRawPassedSweepTests
    {
        private static CheckResult Warn(string id = "W1") => new()
        {
            CheckId = id, CheckName = "w-" + id, Category = "Security", Severity = "High",
            InstanceName = "SRV1", Passed = true, Verdict = "WARN",
            Message = "Could not read 3 of 14 databases (insufficient permissions).",
        };

        private static CheckResult Pass(string id = "P1") => new()
        {
            CheckId = id, CheckName = "p-" + id, Category = "Security", Severity = "High",
            InstanceName = "SRV1", Passed = true, Verdict = "PASS",
        };

        private static CheckResult Fail(string id = "F1") => new()
        {
            CheckId = id, CheckName = "f-" + id, Category = "Security", Severity = "High",
            InstanceName = "SRV1", Passed = false, Verdict = "FAIL",
        };

        // ── CSV export: Status is the truth column; Passed stays raw (ruled, pinned) ──────────

        [Fact]
        public void CsvResultWriter_WarnRowCarriesWarnStatus_WhilePassedStaysRaw()
        {
            // RULED: knowingly accepted, not fixed. The 2026-07-19 header ruling deliberately froze
            // the Passed column's raw value and introduced Status BEFORE it as the truth column, so
            // reversing it is a machine-readable contract change that belongs to Adrian. Pinned
            // here so the pairing is EXERCISED rather than assumed, and so a future edit that
            // changes either column has to come past this test.
            var dir = Path.Combine(Path.GetTempPath(), "csv-sweep-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = CsvResultWriter.Write(new[] { Warn(), Pass(), Fail() }, dir, DateTime.UtcNow);
                var lines = File.ReadAllLines(path);

                var header = lines[0].Split(',');
                var statusIdx = Array.IndexOf(header, "Status");
                var passedIdx = Array.IndexOf(header, "Passed");
                statusIdx.Should().BeGreaterThan(-1);
                passedIdx.Should().BeGreaterThan(statusIdx, "Status must precede Passed so a reader does not stop at Passed");

                var warnRow = lines.Skip(1).First(l => l.Contains("W1", StringComparison.Ordinal)).Split(',');
                warnRow[statusIdx].Should().Be("WARN", "Status is the truth column for a WARN row");
                warnRow[passedIdx].Should().Be("True",
                    "DELIBERATE and accepted: the Passed column keeps its raw value per the 2026-07-19 " +
                    "contract ruling. If this ever changes, it is a breaking change for CSV consumers " +
                    "and must be an explicit decision, not a side effect");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ── ChangeItemService: an overdue vendor item whose check went blind ──────────────────

        [Fact]
        public async Task EvaluateFollowUps_FlagsAnOverdueItemWhoseCheckWentBlind_WithoutClaimingItStillFails()
        {
            // FIXED. Was: `failing = !r.Passed && ...`, so a FAIL → WARN degradation scored
            // failing=false and the overdue item escaped the sweep entirely — the deadline passed,
            // nothing escalated, and the item aged in HandedToVendor looking handled.
            var dir = Path.Combine(Path.GetTempPath(), "chg-sweep-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var svc = new ChangeItemService(NullLogger<ChangeItemService>.Instance, dbPath: Path.Combine(dir, "change-items.db"));

                var blind = await HandOverdueItem(svc, "SRV1", "W1");
                var stillBad = await HandOverdueItem(svc, "SRV1", "F1");

                var flagged = await svc.EvaluateFollowUpsAsync("SRV1", new[] { Warn("W1"), Fail("F1") });

                flagged.Should().Be(2, "both the confirmed failure AND the blind check are overdue and unconfirmed");

                var blindItem = svc.GetAll().Single(c => c.Id == blind);
                var badItem = svc.GetAll().Single(c => c.Id == stillBad);

                blindItem.Status.Should().Be(ChangeItemStatus.StillFailing, "it must escalate, not age silently");
                blindItem.ResolutionNote.Should().Contain("UNCONFIRMED",
                    "the note must say we could not confirm — never assert a failure nobody observed");
                blindItem.ResolutionNote.Should().NotContain("still failing after the due date");

                badItem.ResolutionNote.Should().Contain("still failing after the due date",
                    "a genuinely failing check keeps the assertive note, which we DID exercise");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public async Task EvaluateFollowUps_LeavesAPassingCheckAlone()
        {
            // Guard against over-correction: the fix must not start auto-flagging healthy items.
            var dir = Path.Combine(Path.GetTempPath(), "chg-sweep2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var svc = new ChangeItemService(NullLogger<ChangeItemService>.Instance, dbPath: Path.Combine(dir, "change-items.db"));
                var id = await HandOverdueItem(svc, "SRV1", "P1");

                var flagged = await svc.EvaluateFollowUpsAsync("SRV1", new[] { Pass("P1") });

                flagged.Should().Be(0);
                svc.GetAll().Single(c => c.Id == id).Status.Should().Be(ChangeItemStatus.HandedToVendor,
                    "a passing check is confirmed by a human, never auto-closed and never auto-flagged");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>Creates a change item, hands it to a vendor, and back-dates the due date so the
        /// follow-up sweep considers it overdue.</summary>
        private static async Task<long> HandOverdueItem(ChangeItemService svc, string server, string checkId)
        {
            var item = await svc.LogChange(server, checkId, checkId + " name", "sweep test rationale");
            await svc.HandToVendor(item.Id, "vendor", DateTime.UtcNow.AddDays(-1));
            return item.Id;
        }

        // ── REST API: passedOnly must not hand an integration a blind check as a pass ─────────

        [Fact]
        public void GetResults_PassedOnlyTrue_ExcludesWarn()
        {
            // FIXED. Was `r.Passed == passedOnly.Value`, so /api/v1/checks/results/{i}?passedOnly=true
            // handed an RMM/PSA every "could not fully assess" result as a passing check.
            var got = GetResults(new[] { Pass("P1"), Warn("W1"), Fail("F1") }, passedOnly: true);
            got.Select(r => r.CheckId).Should().BeEquivalentTo(new[] { "P1" });
        }

        [Fact]
        public void GetResults_PassedOnlyFalse_AlsoExcludesWarn()
        {
            // A non-assertion is neither a pass NOR a failure — it must not be libelled into the
            // failure view either.
            var got = GetResults(new[] { Pass("P1"), Warn("W1"), Fail("F1") }, passedOnly: false);
            got.Select(r => r.CheckId).Should().BeEquivalentTo(new[] { "F1" });
        }

        [Fact]
        public void GetResults_Unfiltered_StillReturnsTheWarn()
        {
            // Excluding WARN from BOTH filtered views is only honest if the unfiltered call still
            // shows it. Otherwise the fix would have replaced a false-clean with a disappearance.
            var got = GetResults(new[] { Pass("P1"), Warn("W1"), Fail("F1") }, passedOnly: null);
            got.Select(r => r.CheckId).Should().BeEquivalentTo(new[] { "P1", "W1", "F1" });
        }

        /// <summary>
        /// Calls the REAL <see cref="CheckExecutionService.GetResults"/> over a seeded hot cache.
        /// The service is allocated uninitialised and given only the one field GetResults needs —
        /// every other dependency it touches on this path (<c>_seats</c>, <c>_resultStore</c>,
        /// <c>_historyService</c>, <c>_acceptedFindings</c>) is null-guarded, so the shipped
        /// filter itself runs. Modelling the predicate in the test instead would be exactly the
        /// "test name asserting a property nobody exercised" defect this repo keeps finding.
        /// </summary>
        private static List<CheckResult> GetResults(IEnumerable<CheckResult> rows, bool? passedOnly)
        {
            var svc = (CheckExecutionService)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(CheckExecutionService));

            var field = typeof(CheckExecutionService).GetField("_resultsByInstance",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, List<CheckResult>>();
            cache["SRV1"] = rows.ToList();
            field.SetValue(svc, cache);

            return svc.GetResults("SRV1", maxCount: 50, category: null, passedOnly: passedOnly);
        }

        // ── Machine-readable payloads must be able to REPRESENT a WARN at all ─────────────────

        [Fact]
        public void RestResultsPayload_CarriesTheVerdict_SoPassedTrueIsNotTheWholeStory()
        {
            // FIXED (additive). The /checks/results payload had no status field of any kind, so a
            // WARN serialised as an unqualified passed:true — worse than the CSV, which at least
            // has a Status column.
            ReadSource("Services/ApiEndpoints.cs")
                .Should().Contain("verdict = r.Verdict,",
                    "the REST results payload must expose the tier, not just the pass bit");
        }

        [Fact]
        public void SummaryPayloads_ExposeInformational_SoTheBucketsFootToTotal()
        {
            // FIXED (additive). WARN/SKIP/INFO land in summary.Informational, which was not
            // exposed — so passed+failed+errors silently did not foot to total on any run with a
            // blind spot, and a consumer had no way to tell why.
            var src = ReadSource("Services/ApiEndpoints.cs");
            src.Should().Contain("informational = kvp.Value.Informational");
            System.Text.RegularExpressions.Regex.Matches(src, @"informational = kvp\.Value\.Informational")
                .Count.Should().Be(2, "both /status and /checks/summary expose the bucket");
        }

        [Fact]
        public void AuditDiagnosticSink_RecordsTheVerdict()
        {
            // FIXED (additive). This sink is what someone reads to work out WHY a check reported
            // what it did — precisely the WARN case — and it recorded only the pass bit.
            ReadSource("AuditDiagnosticSink.cs")
                .Should().Contain("verdict = result.Verdict,");
        }

        /// <summary>Reads a shipped source file from the repo so a claim about the shipped code is
        /// made against the shipped code. Walks up from the test binary to the repo root.</summary>
        private static string ReadSource(string relativeToData)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln")))
                dir = dir.Parent;

            dir.Should().NotBeNull("the repo root (SQLTriage.sln) must be findable from the test binary");
            var path = Path.Combine(dir!.FullName, "Data", relativeToData);
            File.Exists(path).Should().BeTrue($"{path} must exist, or the assertions below pass vacuously");
            return File.ReadAllText(path);
        }
    }
}
