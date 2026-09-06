/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationPhase3LiveSmokeTests — THE LANE'S CANONICAL PROOF. Rollback INSIDE a batch has been
 * named untested by anyone since spike S1; this file is where that stops being true.
 *
 * WHAT IS PROVED HERE AND NOWHERE ELSE. The offline arm (BatchRollbackTests) drives every branch of
 * the decision-making above the server — order, reporting, credits, refusals — with a faked executor.
 * It cannot prove that the inverse SQL is right, that the confirming read reads the column the
 * inverse wrote, or that a real SQL Server ends up back where it started. Only these facts can, and
 * each one reads the server back on a SECOND connection the services never saw.
 *
 *   FACT 1 — the whole shape, end to end: three real config fixes applied as ONE batch under ONE
 *            approval, each proved CHANGED by an independent read, then RollBackBatchAsync, then
 *            each proved BACK by an independent read, with AllConfirmed true.
 *   FACT 2 — rollback INSIDE a batch, mid-flight: a forced verify-fail item whose OWN rollback
 *            confirms, and the stop policy holding the line so the next item is never attempted —
 *            proved by an independent read showing that next item untouched.
 *   FACT 3 — zero residue: every option this file touches reads back at its baseline.
 *
 * THE COERCION FIXTURE IS MEASURED, NOT ASSUMED. Fact 2 needs an option SQL Server will not honour
 * exactly as written. `min server memory (MB)` configured to 0 reads value_in_use = 16 (the engine's
 * floor) — proved on THIS instance before this file was written, and asserted again at run time as a
 * PRECONDITION, so an instance that stopped coercing fails loudly instead of green-lighting a branch
 * that was never reached.
 *
 * SKIPPED unless REMSAFE_LIVE_TARGET names a reachable instance, so a normal `dotnet test` run never
 * touches a server. LiveFactAttribute computes Skip at DISCOVERY time; RequireTarget() makes the body
 * FAIL rather than pass vacuously if that attribute is ever weakened.
 *
 * INVOCATION:
 *   $env:REMSAFE_LIVE_TARGET = ".\new2022"
 *   dotnet test Tests/SQLTriage.Tests --filter "FullyQualifiedName~RemediationPhase3LiveSmokeTests"
 *
 * SUBSTITUTIONS, stated so dependent claims can be downgraded:
 *   - Gate 2 runs the REAL BundleBackedRemediationCapability over a FakeBundleAccessor. The gate
 *     LOGIC is exercised; the signature check on a minted bundle is NOT.
 *   - The min-server-memory template is promoted through the REAL corpus path, zero app-code change.
 *   - Everything else is real: driver, runner, renderer, safety validator, exec guard, executor,
 *     persisted credit ledger, HMAC audit chain, and the SQL Server itself.
 *
 * RESIDUE: every value touched is captured before, restored after, and RE-READ to prove it. Every
 * option written is asserted is_dynamic = 1, so no restart-required setting is ever touched.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Remediation;
using SQLTriage.Tests.Licensing;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class RemediationPhase3LiveSmokeTests
    {
        private readonly ITestOutputHelper _out;
        public RemediationPhase3LiveSmokeTests(ITestOutputHelper output) => _out = output;

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

        private const string TargetVariable = "REMSAFE_LIVE_TARGET";
        private static string? Target => Environment.GetEnvironmentVariable(TargetVariable);

        private static string RequireTarget()
        {
            Assert.False(string.IsNullOrWhiteSpace(Target),
                TargetVariable + " is not set, so this test has no instance to touch and nothing to "
                + "assert. It should have been SKIPPED by LiveFactAttribute; if it ran, that "
                + "attribute is no longer doing its job.");
            return Target!;
        }

        private const string MaxDop = "max degree of parallelism";
        private const string CostThreshold = "cost threshold for parallelism";
        private const string AdHoc = "optimize for ad hoc workloads";
        private const string MinServerMemory = "min server memory (MB)";
        private const string ShowAdvanced = "show advanced options";
        private const string PromotedMinMemKey = "SQLT-P3LIVE-MINSERVERMEM";

        private static readonly string[] EveryOptionThisFileTouches =
            { MaxDop, CostThreshold, AdHoc, MinServerMemory, ShowAdvanced };

        private static string ConnString(string target) =>
            $"Server={target};Database=master;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=15;";

        // ── Independent reads and writes: a SECOND connection the services never saw ──

        private static (int Value, int InUse, bool IsDynamic) ReadConfig(string target, string name)
        {
            using var conn = new SqlConnection(ConnString(target));
            conn.Open();
            using var cmd = new SqlCommand(
                "SELECT value, value_in_use, is_dynamic FROM sys.configurations WHERE name = @n;", conn);
            cmd.Parameters.AddWithValue("@n", name);
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read(), $"'{name}' is not a configuration option on this instance.");
            return (Convert.ToInt32(r.GetValue(0)), Convert.ToInt32(r.GetValue(1)), Convert.ToInt32(r.GetValue(2)) == 1);
        }

        /// <summary>Writes a configuration value from OUTSIDE the app, so the fixture the code under test reads was not created by it.</summary>
        private static void SetConfig(string target, string name, int value)
        {
            using var conn = new SqlConnection(ConnString(target));
            conn.Open();
            var sql = "EXEC sp_configure 'show advanced options', 1; RECONFIGURE; "
                    + $"EXEC sp_configure '{name.Replace("'", "''")}', {value}; RECONFIGURE;";
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            cmd.ExecuteNonQuery();
        }

        private Dictionary<string, int> CaptureBaseline(string target, params string[] names)
        {
            var baseline = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names)
            {
                var c = ReadConfig(target, n);
                baseline[n] = c.Value;
                _out.WriteLine($"BASELINE '{n}': value={c.Value} value_in_use={c.InUse} is_dynamic={c.IsDynamic}");
                Assert.True(c.IsDynamic, $"this test refuses to write '{n}': it is not is_dynamic.");
            }
            return baseline;
        }

        private void RestoreAndProve(string target, Dictionary<string, int> baseline)
        {
            // 'show advanced options' LAST: turning it off before the others are back would make
            // them unsettable — the same ordering the executor's own side-effect restore uses.
            foreach (var n in baseline.Keys.Where(k => !string.Equals(k, ShowAdvanced, StringComparison.OrdinalIgnoreCase))
                                            .Concat(baseline.Keys.Where(k => string.Equals(k, ShowAdvanced, StringComparison.OrdinalIgnoreCase))))
            {
                SetConfig(target, n, baseline[n]);
                var after = ReadConfig(target, n);
                _out.WriteLine($"RESTORED '{n}': value={after.Value} (baseline {baseline[n]}) value_in_use={after.InUse}");
                Assert.Equal(baseline[n], after.Value);
            }
        }

        // ── The lane, wired over scratch stores. No real install path is touched. ──

        private sealed class Wiring
        {
            public BatchRemediationDriver Driver = default!;
            public RemediationRunner Runner = default!;
            public RemediationTemplateStore Templates = default!;
            public PersistedRemediationCreditLedger Credits = default!;
            public AuditLogService Audit = default!;
            public string AuditDir = string.Empty;
        }

        private static Wiring Wire(string target, int creditsPerServer = 50)
        {
            var scratch = Path.Combine(Path.GetTempPath(), "remp3-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);

            var connections = new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance);
            connections.AddConnection(new ServerConnection
            {
                ServerNames = target,
                UseWindowsAuthentication = true,
                TrustServerCertificate = true,
                IsEnabled = true,
            });

            var auditDir = Path.Combine(scratch, "audit-logs");
            var audit = new AuditLogService(auditDir, startFlushTimer: false);
            var executor = new DbatoolsRemediationExecutor(
                new PowerShellService(NullLogger<PowerShellService>.Instance),
                connections, audit,
                new DiskIoService(NullLogger<DiskIoService>.Instance),
                NullLogger<DbatoolsRemediationExecutor>.Instance);

            var bundle = new FakeBundleAccessor
            {
                IsUnlocked = true,
                Tier = Tier.Full,
                Features = new BundleFeatures(
                    RagEnabled: false, SpBlitzImport: false, FullCorpus: false,
                    PermittedCheckIds: Array.Empty<int>(),
                    Remediation: true, RemediationCreditsPerServer: creditsPerServer),
            };
            var credits = new PersistedRemediationCreditLedger(
                bundle, NullLogger<PersistedRemediationCreditLedger>.Instance, null,
                Path.Combine(scratch, "remediation-credit-ledger.json"));
            var templates = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var runner = new RemediationRunner(templates, new BundleBackedRemediationCapability(bundle),
                credits, executor, audit, NullLogger<RemediationRunner>.Instance);

            return new Wiring
            {
                Driver = new BatchRemediationDriver(runner, templates, credits),
                Runner = runner,
                Templates = templates,
                Credits = credits,
                Audit = audit,
                AuditDir = auditDir,
            };
        }

        /// <summary>Promotes a min-server-memory fix through the REAL corpus path — zero app-code change.</summary>
        private static void PromoteMinServerMemory(RemediationTemplateStore templates)
        {
            templates.LoadCorpusTemplates(new[]
            {
                new SqlCheck
                {
                    Id = PromotedMinMemKey,
                    Name = "Minimum server memory (live Phase-3 proof)",
                    Remediation = new CheckRemediation
                    {
                        AutoFixable = true,
                        RiskClass = "standard",
                        Reversible = true,
                        CmdletOrTemplate = "sp_configure 'min server memory (MB)'",
                        Operation = new CheckRemediationOperation
                        {
                            OpKind = "sp_configure",
                            ConfigName = MinServerMemory,
                            AdvancedOption = true,
                            ValueParam = "MinServerMemoryMb",
                            MinValue = 0,
                            MaxValue = 2147483647,
                        },
                    },
                },
            });
            Assert.True(templates.IsRegistered(PromotedMinMemKey),
                "the corpus promotion did not register; the rest of this test would prove nothing.");
        }

        // ══ FACT 1 — THE CANONICAL PROOF: a real batch applied, then a real batch UNDONE ══

        [LiveFact(TargetVariable)]
        public async Task ABatchOfThreeRealFixes_IsAppliedUnderONEApproval_AndRollBackBatchPutsEVERYOneBack()
        {
            var target = RequireTarget();
            var baseline = CaptureBaseline(target, MaxDop, CostThreshold, AdHoc, ShowAdvanced);

            try
            {
                // The fixture, set from OUTSIDE the app: three settings away from where the batch
                // will put them, so every item has real work to do and none can be a no-op.
                SetConfig(target, MaxDop, 0);
                SetConfig(target, CostThreshold, 5);
                SetConfig(target, AdHoc, 0);

                var fixture = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [MaxDop] = ReadConfig(target, MaxDop).Value,
                    [CostThreshold] = ReadConfig(target, CostThreshold).Value,
                    [AdHoc] = ReadConfig(target, AdHoc).Value,
                };
                foreach (var kv in fixture) _out.WriteLine($"AS FOUND '{kv.Key}': value={kv.Value}");
                Assert.Equal(0, fixture[MaxDop]);
                Assert.Equal(5, fixture[CostThreshold]);
                Assert.Equal(0, fixture[AdHoc]);

                var w = Wire(target);
                var items = new List<BatchRemediationItem>
                {
                    new("MAXDOP", new Dictionary<string, string> { ["MaxDop"] = "2" }),
                    new("CTFP", new Dictionary<string, string> { ["CostThreshold"] = "50" }),
                    new("OPTIMIZEFORADHOC", new Dictionary<string, string> { ["Enabled"] = "1" }),
                };

                // ONE decision: preview, then apply under the SAME run-id, exactly as a UI must.
                var runId = BatchRemediationDriver.NewRunId();
                var preview = await w.Driver.PreviewBatchDetailedAsync(target, items, runId);
                _out.WriteLine($"PREVIEW: exposure={preview.PricedTotal} changing={preview.ChangingPrice} "
                             + $"noChange={preview.NoChangeCount}");
                Assert.All(preview.Items, p => Assert.False(p.Proposal.IsRefused));

                int availBefore = w.Credits.AvailableFor(target);
                var applied = await w.Driver.ApplyBatchAsync(target, items, approved: true, "p3-live",
                    BatchStopPolicy.StopOnFirstFailure, runId);

                foreach (var it in applied.PerItem)
                    _out.WriteLine($"APPLY {it.TemplateKey}: attempted={it.Attempted} outcome={it.Outcome} "
                                 + $"pre={it.PreChangeValue} rollback={it.RollbackState} :: {it.Message}");
                _out.WriteLine($"APPLY TOTALS: reserved={applied.TotalReserved} committed={applied.TotalCommitted} "
                             + $"stopped={applied.Stopped} :: {applied.StopReason}");

                Assert.False(applied.IsRefused);
                Assert.False(applied.Stopped);
                Assert.All(applied.PerItem, i => Assert.Equal(RemediationOutcome.AppliedVerified, i.Outcome));

                // INDEPENDENT READS — the server really moved, all three of it.
                var afterApply = EveryOptionThisFileTouches.Take(3)
                    .ToDictionary(n => n, n => ReadConfig(target, n).Value, StringComparer.OrdinalIgnoreCase);
                foreach (var kv in afterApply) _out.WriteLine($"INDEPENDENT READ after apply '{kv.Key}': value={kv.Value}");
                Assert.Equal(2, afterApply[MaxDop]);
                Assert.Equal(50, afterApply[CostThreshold]);
                Assert.Equal(1, afterApply[AdHoc]);

                int availAfterApply = w.Credits.AvailableFor(target);
                Assert.Equal(availBefore - applied.TotalCommitted, availAfterApply);

                // ══ THE UNDO — the thing nobody has ever proved ══
                var undo = await w.Driver.RollBackBatchAsync(applied, "p3-live-undo");

                foreach (var r in undo.PerItem)
                    _out.WriteLine($"UNDO {r.TemplateKey}: {r.RollbackState} -> {r.RestoredToValue} "
                                 + $"refunded={r.CreditsRefunded} :: {r.Message}");
                _out.WriteLine($"UNDO TOTALS: allConfirmed={undo.AllConfirmed} noRollback={undo.NoRollbackCount} "
                             + $"creditsBack={undo.TotalCreditsRefunded} :: {undo.Message}");

                Assert.False(undo.IsRefused);
                Assert.Equal(3, undo.PerItem.Count);
                Assert.True(undo.AllConfirmed, "at least one item did not come back confirmed: " + undo.Message);

                // REVERSE ORDER on a real server, not just in a fake's call log.
                Assert.Equal(new[] { "OPTIMIZEFORADHOC", "CTFP", "MAXDOP" },
                    undo.PerItem.Select(i => i.TemplateKey));

                // INDEPENDENT READS — every one of the three is back at the FIXTURE value, read on a
                // connection none of the services under test has ever seen. This is the assertion the
                // whole lane exists for.
                foreach (var kv in fixture)
                {
                    var back = ReadConfig(target, kv.Key);
                    _out.WriteLine($"INDEPENDENT READ after UNDO '{kv.Key}': value={back.Value} (fixture was {kv.Value})");
                    Assert.Equal(kv.Value, back.Value);
                }

                // The undo joins the apply in the ledger, and the chain still verifies with all of it.
                w.Audit.Flush();
                Assert.Equal(applied.RunId, undo.RunId);
                var chain = w.Audit.VerifyChain("p3-live");
                _out.WriteLine($"VERIFYCHAIN intact={chain.Intact} entries={chain.EntryCount} broken={chain.BrokenCount}");
                Assert.True(chain.Intact);

                // Credits came back for every confirmed undo, and the LEDGER agrees with the number
                // the operator was shown.
                int availAfterUndo = w.Credits.AvailableFor(target);
                _out.WriteLine($"CREDITS: {availBefore} -> {availAfterApply} (apply) -> {availAfterUndo} (undo)");
                Assert.Equal(availAfterApply + undo.TotalCreditsRefunded, availAfterUndo);
                Assert.Equal(applied.TotalCommitted, undo.TotalCreditsRefunded);
            }
            finally
            {
                RestoreAndProve(target, baseline);
            }
        }

        // ══ FACT 2 — ROLLBACK INSIDE A BATCH, MID-FLIGHT, AND THE STOP POLICY HOLDING ══

        [LiveFact(TargetVariable)]
        public async Task AVerifyFailMidBatch_RollsThatItemBackAndCONFIRMSIt_AndTheNextItemIsNeverAttempted()
        {
            var target = RequireTarget();
            var baseline = CaptureBaseline(target, MinServerMemory, CostThreshold, ShowAdvanced);

            try
            {
                // The coercion fixture. Configured 0; SQL Server runs its own floor instead.
                SetConfig(target, MinServerMemory, 0);
                SetConfig(target, CostThreshold, 5);

                var asFound = ReadConfig(target, MinServerMemory);
                _out.WriteLine($"AS FOUND '{MinServerMemory}': value={asFound.Value} value_in_use={asFound.InUse}");

                // THE PRECONDITION THIS PROOF RESTS ON. If this instance stopped coercing, the verify
                // would pass, the mid-batch rollback would never fire, and a green test would prove
                // nothing at all. Fail loudly instead.
                Assert.True(asFound.InUse != asFound.Value,
                    $"this instance is not coercing '{MinServerMemory}' (value={asFound.Value}, "
                    + $"value_in_use={asFound.InUse}), so the mid-batch rollback branch cannot be "
                    + "reached and this test would pass vacuously.");

                var w = Wire(target);
                PromoteMinServerMemory(w.Templates);

                var items = new List<BatchRemediationItem>
                {
                    // Item 1: will apply, will fail verify (coerced), and must roll ITSELF back.
                    new(PromotedMinMemKey, new Dictionary<string, string> { ["MinServerMemoryMb"] = "8" }),
                    // Item 2: must NEVER be attempted.
                    new("CTFP", new Dictionary<string, string> { ["CostThreshold"] = "50" }),
                };

                var applied = await w.Driver.ApplyBatchAsync(target, items, approved: true, "p3-live-stop");

                foreach (var it in applied.PerItem)
                    _out.WriteLine($"ITEM {it.TemplateKey}: attempted={it.Attempted} outcome={it.Outcome} "
                                 + $"pre={it.PreChangeValue} preInUse={it.PreChangeValueInUse} "
                                 + $"rollback={it.RollbackState}");
                _out.WriteLine($"ROLLBACK REASON: {applied.PerItem[0].RollbackReason}");
                _out.WriteLine($"STOP REASON:     {applied.StopReason}");

                // The verify-fail item rolled ITSELF back, and the rollback was CONFIRMED by a read
                // of the configured column (the P2 leveling, now proved inside a batch).
                Assert.Equal(RemediationOutcome.AppliedVerifyFailed, applied.PerItem[0].Outcome);
                Assert.Equal(RemediationRollbackState.Confirmed, applied.PerItem[0].RollbackState);
                Assert.Equal(0, applied.PerItem[0].PreChangeValue);

                // AND THE BATCH STOPPED. The next item was never attempted.
                Assert.True(applied.Stopped);
                Assert.Contains(PromotedMinMemKey, applied.StopReason, StringComparison.Ordinal);
                Assert.False(applied.PerItem.Single(i => i.TemplateKey == "CTFP").Attempted);

                // INDEPENDENT READS. The rolled-back item is back at 0; the never-attempted item is
                // untouched at its fixture value of 5, which is the whole point of a stop policy.
                var mem = ReadConfig(target, MinServerMemory);
                var ctfp = ReadConfig(target, CostThreshold);
                _out.WriteLine($"INDEPENDENT READ '{MinServerMemory}': value={mem.Value} value_in_use={mem.InUse}");
                _out.WriteLine($"INDEPENDENT READ '{CostThreshold}': value={ctfp.Value} (fixture was 5)");
                Assert.Equal(0, mem.Value);
                Assert.Equal(5, ctfp.Value);

                // The rolled-back item refunded, so the batch's real bill is zero.
                _out.WriteLine($"CREDITS: reserved={applied.TotalReserved} committed={applied.TotalCommitted}");
                Assert.Equal(0, applied.TotalCommitted);

                // Nothing to undo afterwards: the only item that ran was already put back.
                var undo = await w.Driver.RollBackBatchAsync(applied, "p3-live-stop-undo");
                _out.WriteLine("UNDO AFTER STOP: " + undo.Message);
                Assert.Empty(undo.PerItem);
                Assert.Contains("Nothing to undo", undo.Message, StringComparison.Ordinal);
            }
            finally
            {
                RestoreAndProve(target, baseline);
            }
        }

        // ══ FACT 4 — THE SIGNED LEDGER ATTESTS THE STATEMENT THAT RAN ══════════════
        //
        // FIX ROUND 1, GATE BLOCKER 1. The gate proved live that a batch apply wrote signed
        // RemediationProposed entries quoting the gate's VALUE-INDEPENDENT classification render —
        // "..., 0; RECONFIGURE;" — for applies of 4 and 42. A fabricated fact inside an HMAC-chained
        // audit ledger. The offline pin (BatchRollbackTests) proves the producer emits the resolved
        // text; only this fact proves the text it emits is what a real SQL Server actually received,
        // by reading the server back on a connection the services never saw.

        [LiveFact(TargetVariable)]
        public async Task TheProposedEntriesAttestTheREALVALUES_AndTheServerConfirmsThoseAreTheValuesItGot()
        {
            var target = RequireTarget();
            var baseline = CaptureBaseline(target, MaxDop, CostThreshold, ShowAdvanced);

            const int MaxDopTarget = 4;
            const int CtfpTarget = 42;

            try
            {
                // Set from OUTSIDE the app, and deliberately NOT the targets, so neither item can
                // no-op its way to a green and neither target can coincide with the lower bound the
                // broken record used to print.
                SetConfig(target, MaxDop, 0);
                SetConfig(target, CostThreshold, 5);

                var w = Wire(target);

                // NO run-id is passed, which is what makes each apply write its own proposal record
                // (recordProposal). That is precisely the path the defect lived on.
                var applied = await w.Driver.ApplyBatchAsync(target, new List<BatchRemediationItem>
                {
                    new("MAXDOP", new Dictionary<string, string> { ["MaxDop"] = MaxDopTarget.ToString() }),
                    new("CTFP", new Dictionary<string, string> { ["CostThreshold"] = CtfpTarget.ToString() }),
                }, approved: true, "adrian-p3-attest");
                w.Audit.Flush();

                foreach (var i in applied.PerItem)
                    _out.WriteLine($"ITEM {i.TemplateKey}: outcome={i.Outcome} pre={i.PreChangeValue?.ToString() ?? "<null>"}");
                Assert.False(applied.IsRefused, applied.Message);

                // ── THE SERVER'S OWN ANSWER, read independently. This is what makes the ledger
                //    claim checkable: the attested statement must be the one that produced THIS.
                var maxdopNow = ReadConfig(target, MaxDop);
                var ctfpNow = ReadConfig(target, CostThreshold);
                _out.WriteLine($"INDEPENDENT READ after apply: maxdop={maxdopNow.Value} ctfp={ctfpNow.Value}");
                Assert.Equal(MaxDopTarget, maxdopNow.Value);
                Assert.Equal(CtfpTarget, ctfpNow.Value);

                // ── THE LEDGER, read off disk rather than from the service that wrote it.
                var proposed = Directory.GetFiles(w.AuditDir, "audit-*.jsonl")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(File.ReadAllLines)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => System.Text.Json.JsonSerializer.Deserialize<AuditLogEntry>(l)!)
                    .Where(e => e.EventType == AuditEventType.RemediationProposed)
                    .ToList();
                foreach (var e in proposed) _out.WriteLine("PROPOSED :: " + e.Details["Details"]);

                Assert.Equal(2, proposed.Count);
                var maxdopDetail = proposed.Single(e => e.Details["TemplateKey"] == "MAXDOP").Details["Details"];
                var ctfpDetail = proposed.Single(e => e.Details["TemplateKey"] == "CTFP").Details["Details"];

                Assert.Contains($"EXEC sp_configure '{MaxDop}', {MaxDopTarget}; RECONFIGURE;",
                    maxdopDetail, StringComparison.Ordinal);
                Assert.Contains($"EXEC sp_configure '{CostThreshold}', {CtfpTarget}; RECONFIGURE;",
                    ctfpDetail, StringComparison.Ordinal);

                // The exact fabrication the gate found, forbidden by name on a live run.
                Assert.DoesNotContain($"'{MaxDop}', 0;", maxdopDetail, StringComparison.Ordinal);
                Assert.DoesNotContain($"'{CostThreshold}', 0;", ctfpDetail, StringComparison.Ordinal);

                var chain = w.Audit.VerifyChain("p3-live-attest");
                _out.WriteLine($"VERIFYCHAIN intact={chain.Intact} entries={chain.EntryCount} broken={chain.BrokenCount}");
                Assert.True(chain.Intact);
            }
            finally
            {
                RestoreAndProve(target, baseline);
            }
        }

        // ══ FACT 3 — ZERO RESIDUE ══

        [LiveFact(TargetVariable)]
        public void EveryOptionThisFileTouches_ReadsBackAtSomethingSaneAndIsDynamic()
        {
            // Runs alongside the others rather than after them (xunit orders nothing), so this is a
            // STATE CHECK, not an ordering claim: it prints every option this file can touch and
            // asserts each is is_dynamic, which is the property that makes capture-and-restore
            // possible at all. The per-fact restores above are what prove no residue was left; this
            // is the printed record a reader can check them against.
            var target = RequireTarget();
            foreach (var n in EveryOptionThisFileTouches)
            {
                var c = ReadConfig(target, n);
                _out.WriteLine($"RESIDUE CHECK '{n}': value={c.Value} value_in_use={c.InUse} is_dynamic={c.IsDynamic}");
                Assert.True(c.IsDynamic, $"'{n}' is not is_dynamic on this instance.");
            }
        }
    }
}
