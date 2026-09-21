/* In the name of God, the Merciful, the Compassionate */
/*
 * BatchRollbackTests — the OFFLINE arm of Phase 3: one test per branch of the mutating batch and its
 * undo, driven through the REAL runner and the REAL shipped template store, with only the executor
 * faked.
 *
 * WHY THE EXECUTOR IS THE ONLY FAKE. Everything this file asserts is a decision made ABOVE the
 * server: the order inverses are attempted in, which items get reported and which get counted, which
 * states give a credit back, what the operator is told when nothing could be undone, and whether a
 * refusal sent anything. Faking the executor is what lets each of those be driven at will — an
 * unconfirmed rollback, in particular, needs a connection to die between a successful inverse and its
 * confirming read, which nobody can arrange on a live server on demand. The parts that CANNOT be
 * proved here — that the inverse SQL is right, that the confirming read reads the column the inverse
 * wrote, that a real server goes back — are the live arm's job (RemediationPhase3LiveSmokeTests) and
 * are named as untested here rather than implied to be covered.
 *
 * EVERY FAKE COUNTS ITS CALLS. A refusal that "sends nothing" is asserted by an execution count of
 * zero, not by the absence of a symptom.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Remediation;
using SQLTriage.Tests.Licensing;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class BatchRollbackTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _scratch;

        public BatchRollbackTests(ITestOutputHelper output)
        {
            _out = output;
            _scratch = Path.Combine(Path.GetTempPath(), "batch-rollback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
        }

        public void Dispose()
        {
            try { Directory.Delete(_scratch, recursive: true); } catch { /* test cleanup */ }
            GC.SuppressFinalize(this);
        }

        private const string Server = "P3-FAKE-SERVER";

        // ── The fakes, each an instrument ───────────────────────────────────────────

        /// <summary>
        /// Scripted per key, and it COUNTS. ApplyCalls / UndoCalls are the instrument every
        /// "nothing was sent" assertion in this file reads: an empty list is the proof, and a
        /// symptom's absence is not.
        /// </summary>
        private sealed class ScriptedExecutor : IRemediationExecutor, IRemediationRollbackExecutor
        {
            public readonly List<string> ApplyCalls = new();
            public readonly List<(string Key, int Value)> UndoCalls = new();

            public Dictionary<string, RemediationOutcome> Outcomes = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int?> PreChange = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, RemediationRollbackState> UndoStates = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>The rollback state the APPLY itself reports (the verify-fail path).</summary>
            public Dictionary<string, RemediationRollbackState> ApplyRollbackStates = new(StringComparer.OrdinalIgnoreCase);

            public Task<RemediationPreview> PreviewAsync(RemediationRequest r, CancellationToken ct = default) =>
                Task.FromResult(new RemediationPreview { Succeeded = true, WhatIfText = $"would apply {r.Template.Key}" });

            public Task<RemediationExecution> ExecuteAsync(RemediationRequest r, CancellationToken ct = default)
            {
                ApplyCalls.Add(r.Template.Key);
                var outcome = Outcomes.TryGetValue(r.Template.Key, out var o) ? o : RemediationOutcome.AppliedVerified;
                // Default 4: a captured pre-change value, so the item is undoable. An explicit null
                // entry is how a test drives the "nothing was captured" branch.
                int? pre = PreChange.TryGetValue(r.Template.Key, out var p) ? p : 4;
                var rb = ApplyRollbackStates.TryGetValue(r.Template.Key, out var s)
                    ? s : RemediationRollbackState.NotAttempted;
                return Task.FromResult(new RemediationExecution
                {
                    Outcome = outcome,
                    PreChangeValue = outcome == RemediationOutcome.NoOp ? null : pre,
                    RollbackState = rb,
                    RollbackError = rb == RemediationRollbackState.NotAttempted
                        ? null
                        : $"scripted apply-time rollback: {rb}",
                });
            }

            public Task<RemediationRollbackExecution> RollBackConfigurationAsync(
                RemediationRequest request, int toConfiguredValue, CancellationToken ct = default)
            {
                UndoCalls.Add((request.Template.Key, toConfiguredValue));
                var state = UndoStates.TryGetValue(request.Template.Key, out var s)
                    ? s : RemediationRollbackState.Confirmed;
                return Task.FromResult(new RemediationRollbackExecution
                {
                    State = state,
                    Reason = state == RemediationRollbackState.Confirmed
                        ? $"'{request.Template.Key}' was put back to {toConfiguredValue}, and a read of the server confirmed it."
                        : $"the undo of '{request.Template.Key}' reported {state}.",
                    ObservedConfiguredValue = state == RemediationRollbackState.Confirmed ? toConfiguredValue : 99,
                });
            }

            public bool CanWriteAudit() => true;
        }

        /// <summary>An executor with no undo seam at all — the honest-refusal case.</summary>
        private sealed class ApplyOnlyExecutor : IRemediationExecutor
        {
            public readonly List<string> ApplyCalls = new();
            public Task<RemediationPreview> PreviewAsync(RemediationRequest r, CancellationToken ct = default) =>
                Task.FromResult(new RemediationPreview { Succeeded = true, WhatIfText = "x" });
            public Task<RemediationExecution> ExecuteAsync(RemediationRequest r, CancellationToken ct = default)
            {
                ApplyCalls.Add(r.Template.Key);
                return Task.FromResult(new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerified,
                    PreChangeValue = 4,
                });
            }
            public bool CanWriteAudit() => true;
        }

        private sealed class Wiring
        {
            public BatchRemediationDriver Driver = default!;
            public RemediationRunner Runner = default!;
            public PersistedRemediationCreditLedger Credits = default!;
            public AuditLogService Audit = default!;
            public string AuditDir = string.Empty;
        }

        private Wiring Wire(IRemediationExecutor executor, string tag,
            int creditsPerServer = 10, bool remediationGranted = true)
        {
            var root = Path.Combine(_scratch, tag);
            Directory.CreateDirectory(root);
            var auditDir = Path.Combine(root, "audit-logs");

            var bundle = new FakeBundleAccessor
            {
                IsUnlocked = true,
                Tier = Tier.Full,
                Features = new BundleFeatures(
                    RagEnabled: false, SpBlitzImport: false, FullCorpus: false,
                    PermittedCheckIds: Array.Empty<int>(),
                    Remediation: remediationGranted, RemediationCreditsPerServer: creditsPerServer),
            };

            var audit = new AuditLogService(auditDir, startFlushTimer: false);
            var templates = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var credits = new PersistedRemediationCreditLedger(
                bundle, NullLogger<PersistedRemediationCreditLedger>.Instance, null,
                Path.Combine(root, "remediation-credit-ledger.json"));
            var runner = new RemediationRunner(templates,
                new BundleBackedRemediationCapability(bundle), credits, executor, audit,
                NullLogger<RemediationRunner>.Instance);

            return new Wiring
            {
                Driver = new BatchRemediationDriver(runner, templates, credits),
                Runner = runner,
                Credits = credits,
                Audit = audit,
                AuditDir = auditDir,
            };
        }

        private static List<BatchRemediationItem> ThreeItems() => new()
        {
            new("MAXDOP", new Dictionary<string, string> { ["MaxDop"] = "4" }),
            new("OPTIMIZEFORADHOC", new Dictionary<string, string> { ["Enabled"] = "1" }),
            new("CTFP", new Dictionary<string, string> { ["CostThreshold"] = "50" }),
        };

        private static List<AuditLogEntry> ReadAuditEntries(string auditDir) =>
            Directory.Exists(auditDir)
                ? Directory.GetFiles(auditDir, "audit-*.jsonl")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(File.ReadAllLines)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => System.Text.Json.JsonSerializer.Deserialize<AuditLogEntry>(l)!)
                    .ToList()
                : new List<AuditLogEntry>();

        // ── ORDER ───────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheInversesRunInREVERSEApplyOrder()
        {
            // Batch items are not independent: later applies land on the state earlier ones left,
            // and the renderer's 'show advanced options' prelude is shared. Reverse order is the
            // only order in which each inverse sees the state its own apply produced.
            var exec = new ScriptedExecutor();
            var w = Wire(exec, "reverse-order");

            var applied = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            Assert.Equal(new[] { "MAXDOP", "OPTIMIZEFORADHOC", "CTFP" }, exec.ApplyCalls);

            var undo = await w.Driver.RollBackBatchAsync(applied, "adrian");

            _out.WriteLine("APPLY ORDER: " + string.Join(" -> ", exec.ApplyCalls));
            _out.WriteLine("UNDO  ORDER: " + string.Join(" -> ", exec.UndoCalls.Select(u => $"{u.Key}={u.Value}")));

            Assert.Equal(new[] { "CTFP", "OPTIMIZEFORADHOC", "MAXDOP" }, exec.UndoCalls.Select(u => u.Key));
            // And the REPORTED order matches the order they were attempted in, so an operator
            // reading the list is reading what happened rather than the apply order re-sorted.
            Assert.Equal(new[] { "CTFP", "OPTIMIZEFORADHOC", "MAXDOP" }, undo.PerItem.Select(i => i.TemplateKey));
            Assert.True(undo.AllConfirmed);
        }

        [Fact]
        public async Task EachInverseTargetsTheValueITSOWNApplyCaptured()
        {
            // A single restore value applied to every item would pass the order test above and be
            // catastrophically wrong on a real server.
            var exec = new ScriptedExecutor
            {
                PreChange = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MAXDOP"] = 0,
                    ["OPTIMIZEFORADHOC"] = 1,
                    ["CTFP"] = 5,
                },
            };
            var w = Wire(exec, "per-item-value");

            var applied = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            await w.Driver.RollBackBatchAsync(applied, "adrian");

            var byKey = exec.UndoCalls.ToDictionary(u => u.Key, u => u.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var u in exec.UndoCalls) _out.WriteLine($"UNDO {u.Key} -> {u.Value}");

            Assert.Equal(0, byKey["MAXDOP"]);
            Assert.Equal(1, byKey["OPTIMIZEFORADHOC"]);
            Assert.Equal(5, byKey["CTFP"]);
        }

        // ── NO-ROLLBACK IS REPORTED, NEVER SILENTLY SKIPPED ─────────────────────────

        [Fact]
        public async Task AnItemWithNoCapturedValue_IsREPORTED_WithItsReason_AndNothingIsSentForIt()
        {
            // The item CHANGED the server (AppliedVerified) but its pre-change value was never
            // read, so there is no inverse. "This one is still in place" is exactly what an
            // operator undoing a batch must be told, and it is the fact a silent skip destroys.
            var exec = new ScriptedExecutor
            {
                PreChange = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["OPTIMIZEFORADHOC"] = null,
                },
            };
            var w = Wire(exec, "no-captured-value");

            var applied = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            var undo = await w.Driver.RollBackBatchAsync(applied, "adrian");

            foreach (var r in undo.PerItem) _out.WriteLine($"{r.TemplateKey}: {r.RollbackState} :: {r.Message}");

            var orphan = undo.PerItem.Single(i => i.TemplateKey == "OPTIMIZEFORADHOC");
            Assert.Equal(RemediationRollbackState.NotAvailable, orphan.RollbackState);
            Assert.StartsWith(RemediationRollbackProse.NoRollbackMarker, orphan.Message, StringComparison.Ordinal);
            Assert.Contains("still in place", orphan.Message, StringComparison.Ordinal);
            Assert.False(orphan.InverseRan);

            // Nothing was sent FOR IT — the other two were still undone.
            Assert.DoesNotContain(exec.UndoCalls, u => u.Key == "OPTIMIZEFORADHOC");
            Assert.Equal(2, exec.UndoCalls.Count);

            // And the batch does not claim to be back.
            Assert.False(undo.AllConfirmed);
            Assert.Equal(1, undo.NoRollbackCount);
        }

        [Fact]
        public async Task AnExecutorThatCannotUndoAnything_SaysSoPerItem_RatherThanReportingASilentSuccess()
        {
            var exec = new ApplyOnlyExecutor();
            var w = Wire(exec, "no-undo-seam");

            var applied = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            var undo = await w.Driver.RollBackBatchAsync(applied, "adrian");

            foreach (var r in undo.PerItem) _out.WriteLine($"{r.TemplateKey}: {r.RollbackState} :: {r.Message}");

            Assert.Equal(3, undo.PerItem.Count);
            Assert.All(undo.PerItem, r =>
            {
                Assert.Equal(RemediationRollbackState.NotAvailable, r.RollbackState);
                Assert.StartsWith(RemediationRollbackProse.NoRollbackMarker, r.Message, StringComparison.Ordinal);
                Assert.Contains("cannot undo changes", r.Message, StringComparison.Ordinal);
            });
            Assert.False(undo.AllConfirmed);
        }

        [Fact]
        public async Task ItemsThatChangedNothing_AreNOTReportedAsFailedUndos_ButAreCountedInTheMessage()
        {
            // A no-op did not change the server, so there is nothing about it to undo. Reporting it
            // as a failed undo would drag AllConfirmed to false for a batch that IS fully back —
            // the same over-report this lane keeps closing, inverted.
            var exec = new ScriptedExecutor
            {
                Outcomes = new Dictionary<string, RemediationOutcome>(StringComparer.OrdinalIgnoreCase)
                {
                    ["OPTIMIZEFORADHOC"] = RemediationOutcome.NoOp,
                },
            };
            var w = Wire(exec, "noop-not-undone");

            var applied = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            var undo = await w.Driver.RollBackBatchAsync(applied, "adrian");

            _out.WriteLine("MESSAGE: " + undo.Message);
            Assert.Equal(2, undo.PerItem.Count);
            Assert.DoesNotContain(undo.PerItem, r => r.TemplateKey == "OPTIMIZEFORADHOC");
            Assert.True(undo.AllConfirmed);

            // Stated, not silent.
            Assert.Contains("1 item in that batch changed nothing", undo.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ABatchThatChangedNothingAtAll_SaysSo_AndSendsNothing()
        {
            var exec = new ScriptedExecutor
            {
                Outcomes = new Dictionary<string, RemediationOutcome>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MAXDOP"] = RemediationOutcome.NoOp,
                    ["OPTIMIZEFORADHOC"] = RemediationOutcome.NoOp,
                    ["CTFP"] = RemediationOutcome.NoOp,
                },
            };
            var w = Wire(exec, "all-noop");

            var applied = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            var undo = await w.Driver.RollBackBatchAsync(applied, "adrian");

            _out.WriteLine("MESSAGE: " + undo.Message);
            Assert.Empty(undo.PerItem);
            Assert.Empty(exec.UndoCalls);
            Assert.Contains("Nothing to undo", undo.Message, StringComparison.Ordinal);
            // AllConfirmed is FALSE on an empty list, deliberately: "everything is back" is a claim
            // about items, and there are none.
            Assert.False(undo.AllConfirmed);
        }

        // ── OPERATOR-PROOFING: refusals are truthful, and nothing mutates on them ───

        [Fact]
        public async Task AnUndoWithNoNamedApprover_IsRefused_AndNOTHINGIsSent()
        {
            var exec = new ScriptedExecutor();
            var w = Wire(exec, "undo-unattributed");

            var applied = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            int appliesBefore = exec.ApplyCalls.Count;

            var undo = await w.Driver.RollBackBatchAsync(applied, "   ");

            _out.WriteLine($"refused={undo.IsRefused} :: {undo.Message}");
            _out.WriteLine($"undo calls={exec.UndoCalls.Count} apply calls {appliesBefore} -> {exec.ApplyCalls.Count}");

            Assert.True(undo.IsRefused);
            Assert.Contains("named approver", undo.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was sent to the server", undo.Message, StringComparison.Ordinal);
            Assert.Empty(exec.UndoCalls);
            Assert.Equal(appliesBefore, exec.ApplyCalls.Count);
        }

        [Fact]
        public async Task UndoingARefusedBatch_IsRefused_AndSendsNothing()
        {
            var exec = new ScriptedExecutor();
            var w = Wire(exec, "undo-refused-batch");

            var refused = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: false, "adrian");
            Assert.True(refused.IsRefused);

            var undo = await w.Driver.RollBackBatchAsync(refused, "adrian");

            _out.WriteLine($"refused={undo.IsRefused} :: {undo.Message}");
            Assert.True(undo.IsRefused);
            Assert.Contains("nothing to undo", undo.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(exec.UndoCalls);
            Assert.Empty(exec.ApplyCalls);
        }

        [Fact]
        public async Task TheUndoIsGATED_ACapabilityDenialRefusesEveryItem_AndSendsNothing()
        {
            // The undo writes to a production server, so it is not a lesser act than the apply and
            // does not get a lesser path. This drives gate 2 by revoking the claim between the
            // apply and the undo — the same instrument the P1 arm used to prove the driver adds no
            // gate of its own.
            var exec = new ScriptedExecutor();
            var granted = Wire(exec, "undo-gated-granted");
            var applied = await granted.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            Assert.Equal(3, exec.ApplyCalls.Count);

            var denied = Wire(exec, "undo-gated-denied", remediationGranted: false);
            var undo = await denied.Driver.RollBackBatchAsync(applied, "adrian");

            foreach (var r in undo.PerItem) _out.WriteLine($"{r.TemplateKey}: {r.RollbackState} :: {r.Message}");
            Assert.All(undo.PerItem, r => Assert.Equal(RemediationRollbackState.NotAvailable, r.RollbackState));
            Assert.All(undo.PerItem, r =>
                Assert.Contains("does not carry the remediation capability", r.Message, StringComparison.Ordinal));
            Assert.All(undo.PerItem, r =>
                Assert.Contains("nothing was sent to the server", r.Message, StringComparison.Ordinal));
            Assert.Empty(exec.UndoCalls);
        }

        // ── CREDITS ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task OnlyACONFIRMEDUndoGivesTheCreditBack()
        {
            // The shipped rule (Ruling 2, 2026-08-25): a credit-back asserts the change is gone, and
            // only a confirming read can say that. Unconfirmed keeps the charge; so does Failed.
            var exec = new ScriptedExecutor
            {
                UndoStates = new Dictionary<string, RemediationRollbackState>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MAXDOP"] = RemediationRollbackState.Confirmed,
                    ["OPTIMIZEFORADHOC"] = RemediationRollbackState.Unconfirmed,
                    ["CTFP"] = RemediationRollbackState.Failed,
                },
            };
            var w = Wire(exec, "credits", creditsPerServer: 10);

            int before = w.Credits.AvailableFor(Server);
            var applied = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            int afterApply = w.Credits.AvailableFor(Server);

            var undo = await w.Driver.RollBackBatchAsync(applied, "adrian");
            int afterUndo = w.Credits.AvailableFor(Server);

            foreach (var r in undo.PerItem)
                _out.WriteLine($"{r.TemplateKey}: {r.RollbackState} refunded={r.CreditsRefunded} :: {r.Message}");
            _out.WriteLine($"LEDGER available {before} -> {afterApply} (apply) -> {afterUndo} (undo); "
                         + $"batch committed={applied.TotalCommitted} undo refunded={undo.TotalCreditsRefunded}");

            Assert.Equal(3, applied.TotalCommitted);
            Assert.Equal(1, undo.TotalCreditsRefunded);
            Assert.Equal(1, undo.PerItem.Single(i => i.TemplateKey == "MAXDOP").CreditsRefunded);
            Assert.Equal(0, undo.PerItem.Single(i => i.TemplateKey == "OPTIMIZEFORADHOC").CreditsRefunded);
            Assert.Equal(0, undo.PerItem.Single(i => i.TemplateKey == "CTFP").CreditsRefunded);

            // The LEDGER moved by the same one, which is the assertion that matters: the number the
            // operator is shown and the balance cannot disagree.
            Assert.Equal(afterApply + 1, afterUndo);
        }

        [Fact]
        public async Task ALedgerThatCannotCreditBack_SaysSoInTheOperatorsSENTENCE_RatherThanShowingAPhantomRefund()
        {
            // The default IRemediationCreditLedger.TryCreditBack refuses. A surface that assumed
            // success would print "1 change credit was given back" over a balance that never moved.
            var exec = new ScriptedExecutor();
            var ledger = new NoCreditBackLedger();
            var templates = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var audit = new AuditLogService(Path.Combine(_scratch, "nocb", "audit-logs"), startFlushTimer: false);
            var bundle = new FakeBundleAccessor
            {
                IsUnlocked = true,
                Tier = Tier.Full,
                Features = new BundleFeatures(
                    RagEnabled: false, SpBlitzImport: false, FullCorpus: false,
                    PermittedCheckIds: Array.Empty<int>(),
                    Remediation: true, RemediationCreditsPerServer: 10),
            };
            var runner = new RemediationRunner(templates, new BundleBackedRemediationCapability(bundle),
                ledger, exec, audit, NullLogger<RemediationRunner>.Instance);
            var driver = new BatchRemediationDriver(runner, templates, ledger);

            var applied = await driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            var undo = await driver.RollBackBatchAsync(applied, "adrian");

            foreach (var r in undo.PerItem) _out.WriteLine($"{r.TemplateKey}: refunded={r.CreditsRefunded} :: {r.Message}");

            Assert.True(undo.AllConfirmed);                 // the SERVER went back…
            Assert.Equal(0, undo.TotalCreditsRefunded);     // …and the money did not come back…
            Assert.All(undo.PerItem, r =>                    // …and the sentence says exactly that.
                Assert.Contains("could not be given back", r.Message, StringComparison.Ordinal));
        }

        /// <summary>
        /// Uses the DEFAULT <see cref="IRemediationCreditLedger.TryCreditBack"/> — the interface's own
        /// refusal — so this exercises the shipped default rather than a fake behaviour invented here.
        /// </summary>
        private sealed class NoCreditBackLedger : IRemediationCreditLedger
        {
            private int _available = 10;
            private int _spent;
            public int AvailableFor(string serverName) => _available;
            public CreditReservation? Reserve(string serverName, int cost)
            {
                if (_available < cost) return null;
                _available -= cost;
                return new CreditReservation(Guid.NewGuid().ToString("N"), serverName, cost);
            }
            public void Commit(CreditReservation reservation) => _spent += reservation.Cost;
            public void Refund(CreditReservation reservation) => _available += reservation.Cost;
            public CreditBreakdown GetBreakdown(string serverName) =>
                new(10, _spent, 0, _available);
            public bool IsStoreDamaged => false;
            public string DescribeStoreRecovery() => string.Empty;
        }

        // ── STOP POLICY, and the refund that must follow it ────────────────────────

        [Fact]
        public async Task WhenAnItemFails_TheBatchStops_AndTheStopReasonNAMESTheItem()
        {
            var exec = new ScriptedExecutor
            {
                Outcomes = new Dictionary<string, RemediationOutcome>(StringComparer.OrdinalIgnoreCase)
                {
                    ["OPTIMIZEFORADHOC"] = RemediationOutcome.CouldNotRun,
                },
            };
            var w = Wire(exec, "stop-names-item");

            var r = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");

            _out.WriteLine($"stopped={r.Stopped} :: {r.StopReason}");
            Assert.True(r.Stopped);
            Assert.Contains("OPTIMIZEFORADHOC", r.StopReason, StringComparison.Ordinal);
            Assert.Contains("could not run", r.StopReason, StringComparison.Ordinal);
            Assert.Contains("nothing after this one was changed", r.StopReason, StringComparison.OrdinalIgnoreCase);

            Assert.False(r.PerItem.Single(i => i.TemplateKey == "CTFP").Attempted);
            Assert.Equal(new[] { "MAXDOP", "OPTIMIZEFORADHOC" }, exec.ApplyCalls);
        }

        [Fact]
        public async Task AStoppedBatch_RefundsTheFailedItem_AndUndoingItTouchesOnlyWhatCHANGED()
        {
            var exec = new ScriptedExecutor
            {
                Outcomes = new Dictionary<string, RemediationOutcome>(StringComparer.OrdinalIgnoreCase)
                {
                    ["OPTIMIZEFORADHOC"] = RemediationOutcome.CouldNotRun,
                },
            };
            var w = Wire(exec, "stop-refund");

            int before = w.Credits.AvailableFor(Server);
            var r = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            int after = w.Credits.AvailableFor(Server);

            _out.WriteLine($"reserved={r.TotalReserved} committed={r.TotalCommitted} refunded={r.CreditsRefunded} "
                         + $"| ledger {before} -> {after}");

            // Two items were attempted, so two reservations were made; only the one that CHANGED
            // something committed. The ledger delta matches the COMMITTED total, never the reserved.
            Assert.Equal(2, r.TotalReserved);
            Assert.Equal(1, r.TotalCommitted);
            Assert.Equal(before - 1, after);
            Assert.NotEqual(before - after, r.TotalReserved);

            var undo = await w.Driver.RollBackBatchAsync(r, "adrian");
            _out.WriteLine("UNDO: " + string.Join(", ", exec.UndoCalls.Select(u => u.Key)));

            // Only MAXDOP changed anything, so only MAXDOP is undone. The failed item and the
            // never-attempted item are counted, not reported as failed undos.
            Assert.Equal(new[] { "MAXDOP" }, exec.UndoCalls.Select(u => u.Key));
            Assert.Single(undo.PerItem);
            Assert.True(undo.AllConfirmed);
            Assert.Contains("2 items in that batch changed nothing", undo.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AVerifyFailSTOPSTheBatchToo_AndTheReasonSaysWhetherThatItemWasPutBack()
        {
            // WIDER THAN PHASE 1, deliberately. AppliedVerifyFailed is the state where the statement
            // RAN and the server did not end up where the fix wanted it. Carrying on applies the
            // rest of an approved set onto a server that has just behaved unexpectedly.
            var exec = new ScriptedExecutor
            {
                Outcomes = new Dictionary<string, RemediationOutcome>(StringComparer.OrdinalIgnoreCase)
                {
                    ["OPTIMIZEFORADHOC"] = RemediationOutcome.AppliedVerifyFailed,
                },
                ApplyRollbackStates = new Dictionary<string, RemediationRollbackState>(StringComparer.OrdinalIgnoreCase)
                {
                    ["OPTIMIZEFORADHOC"] = RemediationRollbackState.Confirmed,
                },
            };
            var w = Wire(exec, "verifyfail-stops");

            var r = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");

            _out.WriteLine($"stopped={r.Stopped} :: {r.StopReason}");
            Assert.True(r.Stopped);
            Assert.Equal(new[] { "MAXDOP", "OPTIMIZEFORADHOC" }, exec.ApplyCalls);
            Assert.False(r.PerItem.Single(i => i.TemplateKey == "CTFP").Attempted);
            Assert.Contains("did not end up where the fix wanted it", r.StopReason, StringComparison.Ordinal);
            // The reason states the OTHER fact the operator needs: is that change still there?
            Assert.Contains("put back and the server was read to confirm it", r.StopReason, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AVerifyFailWhoseOwnRollbackWasNotConfirmed_TELLSTheOperatorToCheckThatItem()
        {
            var exec = new ScriptedExecutor
            {
                Outcomes = new Dictionary<string, RemediationOutcome>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MAXDOP"] = RemediationOutcome.AppliedVerifyFailed,
                },
                ApplyRollbackStates = new Dictionary<string, RemediationRollbackState>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MAXDOP"] = RemediationRollbackState.Unconfirmed,
                },
            };
            var w = Wire(exec, "verifyfail-unconfirmed");

            var r = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");

            _out.WriteLine($"stopped={r.Stopped} :: {r.StopReason}");
            _out.WriteLine("ITEM ROLLBACK REASON: " + r.PerItem[0].RollbackReason);

            Assert.True(r.Stopped);
            Assert.Single(exec.ApplyCalls);
            Assert.Contains("Check that item before doing anything else", r.StopReason, StringComparison.Ordinal);
            // …and the executor's own sentence survives the trip, rather than being replaced by a
            // state name (P2 fix-round blocker 3, in a new place).
            Assert.Contains("could not be confirmed", r.PerItem[0].RollbackReason, StringComparison.Ordinal);
            Assert.Contains("scripted apply-time rollback", r.PerItem[0].RollbackReason, StringComparison.Ordinal);

            // A verify-fail with an UNCONFIRMED rollback keeps its charge, per the shipped rule.
            Assert.Equal(1, r.TotalReserved);
            Assert.Equal(1, r.TotalCommitted);
        }

        [Fact]
        public async Task UnderContinueOnFailure_AVerifyFailDoesNotStopTheBatch()
        {
            // The negative control for the widened predicate: it must widen the STOP rule, not
            // override the operator's explicit choice to run everything.
            var exec = new ScriptedExecutor
            {
                Outcomes = new Dictionary<string, RemediationOutcome>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MAXDOP"] = RemediationOutcome.AppliedVerifyFailed,
                },
            };
            var w = Wire(exec, "verifyfail-continue");

            var r = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian",
                BatchStopPolicy.ContinueOnFailure);

            Assert.False(r.Stopped);
            Assert.Equal(3, exec.ApplyCalls.Count);
            Assert.Equal(string.Empty, r.StopReason);
        }

        // ── THE PROPOSAL RECORD (P1 §3.5) ──────────────────────────────────────────

        [Fact]
        public async Task AnApplyWITHOUTAPreview_STILLWritesTheProposedEntry_SoTheChainIsATriple()
        {
            var exec = new ScriptedExecutor();
            var w = Wire(exec, "proposal-record");

            var r = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            w.Audit.Flush();

            var entries = ReadAuditEntries(w.AuditDir);
            int proposed = entries.Count(e => e.EventType == AuditEventType.RemediationProposed);
            int approved = entries.Count(e => e.EventType == AuditEventType.RemediationApproved);
            int appliedN = entries.Count(e => e.EventType == AuditEventType.RemediationApplied);
            _out.WriteLine($"NO PREVIEW: proposed={proposed} approved={approved} applied={appliedN}");

            Assert.Equal(3, proposed);
            Assert.Equal(3, approved);
            Assert.Equal(3, appliedN);

            // And every one carries the batch's run-id, so the triple JOINS.
            var remediation = entries.Where(e => e.EventType is AuditEventType.RemediationProposed
                or AuditEventType.RemediationApproved or AuditEventType.RemediationApplied).ToList();
            Assert.All(remediation, e =>
                Assert.Equal(r.RunId, e.Details[AuditLogService.CorrelationDetailKey]));

            // The proposal record carries WHAT WOULD RUN, not a re-worded sentence.
            var one = entries.First(e => e.EventType == AuditEventType.RemediationProposed);
            _out.WriteLine("PROPOSED DETAILS: " + string.Join("; ", one.Details.Select(kv => $"{kv.Key}={kv.Value}")));
            Assert.Contains(one.Details.Values, v => v.Contains("sp_configure", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task APreviewedBatch_DoesNotWriteASECONDProposedEntryPerItem()
        {
            // The counterweight. A proposal record written unconditionally would double every
            // previewed batch's Proposed entries, which is the same audit-noise defect pointing the
            // other way.
            var exec = new ScriptedExecutor();
            var w = Wire(exec, "proposal-no-double");

            var runId = BatchRemediationDriver.NewRunId();
            await w.Driver.PreviewBatchDetailedAsync(Server, ThreeItems(), runId);
            await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian",
                BatchStopPolicy.StopOnFirstFailure, runId);
            w.Audit.Flush();

            int proposed = ReadAuditEntries(w.AuditDir).Count(e => e.EventType == AuditEventType.RemediationProposed);
            _out.WriteLine($"PREVIEWED: proposed={proposed} (must be 3, one per item, from the preview)");
            Assert.Equal(3, proposed);
        }

        // ── THE LEDGER MUST ATTEST THE STATEMENT THAT RAN (fix round 1, gate blocker 1) ──────

        [Fact]
        public async Task TheProposalRecordAttestsTheRESOLVEDStatement_NotTheValueIndependentPlaceholder()
        {
            // THE DEFECT THIS PINS, proved live by the gate before it was fixed. The Proposed entry
            // was written from RemediationOpRenderer.TryRenderForClassification — a deliberately
            // VALUE-INDEPENDENT render that always emits the op's lower bound and ignores the
            // operator's parameters entirely. Applies of MAXDOP=4 and CTFP=42 therefore wrote signed
            // Proposed entries both attesting "..., 0; RECONFIGURE;". A statement nobody ran, inside
            // an HMAC-chained audit ledger, described as the authorised one.
            //
            // Two items with DIFFERENT non-default values, because a single item could pass this by
            // accident if the placeholder and the target happened to agree.
            var exec = new ScriptedExecutor();
            var w = Wire(exec, "proposal-resolved-value");

            var items = new List<BatchRemediationItem>
            {
                new("MAXDOP", new Dictionary<string, string> { ["MaxDop"] = "4" }),
                new("CTFP", new Dictionary<string, string> { ["CostThreshold"] = "42" }),
            };

            await w.Driver.ApplyBatchAsync(Server, items, approved: true, "adrian");
            w.Audit.Flush();

            var proposed = ReadAuditEntries(w.AuditDir)
                .Where(e => e.EventType == AuditEventType.RemediationProposed).ToList();
            foreach (var e in proposed)
                _out.WriteLine("PROPOSED :: " + string.Join("; ", e.Details.Select(kv => $"{kv.Key}={kv.Value}")));

            Assert.Equal(2, proposed.Count);

            string DetailsOf(string key) =>
                proposed.Single(e => e.Details["TemplateKey"] == key).Details["Details"];

            var maxdop = DetailsOf("MAXDOP");
            var ctfp = DetailsOf("CTFP");

            // ── THE RESOLVED VALUE IS THERE, and it is the executor's own render. Not "contains a 4"
            //    somewhere: the exact sp_configure clause the executor sends.
            Assert.Contains("EXEC sp_configure 'max degree of parallelism', 4; RECONFIGURE;", maxdop, StringComparison.Ordinal);
            Assert.Contains("EXEC sp_configure 'cost threshold for parallelism', 42; RECONFIGURE;", ctfp, StringComparison.Ordinal);

            // ── AND THE PLACEHOLDER IS NOT. This is the half that fails against the old behaviour:
            //    the classification render wrote ", 0; RECONFIGURE;" for both of these.
            Assert.DoesNotContain("'max degree of parallelism', 0;", maxdop, StringComparison.Ordinal);
            Assert.DoesNotContain("'cost threshold for parallelism', 0;", ctfp, StringComparison.Ordinal);

            // ── AND THE CHAIN IS STILL INTACT over the corrected details.
            var chain = w.Audit.VerifyChain("p3-proposal-resolved");
            _out.WriteLine($"VERIFYCHAIN intact={chain.Intact} entries={chain.EntryCount} broken={chain.BrokenCount}");
            Assert.True(chain.Intact);
        }

        [Fact]
        public void TheProposalSentence_QuotesNoExecutedSqlWhenTheTextCannotBeKnownYet()
        {
            // The counterweight to the test above, and the reason the fix is a producer rather than
            // an inline string. Not every op kind CAN name its statement before it runs: a
            // per-database SET, a created index and a one-shot backup all compose their text from
            // what the server reports at execution time. For those, quoting anything as "the
            // statement this apply runs" would re-introduce the same fabrication in a new place.
            // The fallback arm must therefore say why, and may name the classification render ONLY
            // as the never-executed shape it is.
            var templates = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var index = templates.TryGet("ADDMISSINGINDEX");
            Assert.NotNull(index);

            var sentence = RemediationRunner.DescribeUnpreviewedProposal(
                index!, null, "CREATE INDEX [ix_remediation_representative] ON [dbo].[t] ([c]);");
            _out.WriteLine("FALLBACK :: " + sentence);

            Assert.Contains("cannot be quoted here", sentence, StringComparison.Ordinal);
            Assert.Contains("composes its statement while it runs", sentence, StringComparison.Ordinal);
            // The placeholder is present but LABELLED. Both halves matter: an auditor must be able
            // to see the shape, and must never read it as the executed text.
            Assert.Contains("NOT what runs and was never executed", sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("The statement this apply runs is", sentence, StringComparison.Ordinal);
        }

        // ── THE UNDO REASON MATCHES THE OP KIND (fix round 1, gate blocker 5) ───────

        [Fact]
        public void TheNoUndoReason_IsCHOSENByOpKind_AndOnlySpConfigureIsToldAValueWasNotRead()
        {
            // THE DEFECT THIS PINS. The batch undo printed one sentence for every item with no
            // captured value: "the value ... found before it changed anything was never read". For
            // sp_configure that is exactly right — there IS a value and it was not read. For every
            // other kind there is no value in existence to read: an index creation, a one-shot
            // backup and a per-database SET capture no integer before-state by design. Telling an
            // operator a read was missed sends them hunting a fault that cannot occur, and implies
            // a retry would capture it.
            var spConfigure = new RemediationTemplate
            {
                Key = "TEST-SPCONFIGURE",
                Kind = RemediationKind.Configuration,
                Reversible = true,
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.SpConfigure,
                    ConfigName = "max degree of parallelism",
                    ValueParam = "MaxDop",
                    MinValue = 0,
                    MaxValue = 64,
                },
            };
            var dbSetOption = new RemediationTemplate
            {
                Key = "TEST-DBSETOPTION",
                Kind = RemediationKind.Configuration,
                Reversible = true,
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.DbSetOption,
                    OptionSql = "SET AUTO_CLOSE OFF",
                },
            };
            var oneShot = new RemediationTemplate
            {
                Key = "TEST-ONESHOT",
                Kind = RemediationKind.Transactable,
                Reversible = true,
                Operation = new RemediationOperation { OpKind = RemediationOpKind.CreateIndex },
            };
            var irreversible = new RemediationTemplate
            {
                Key = "TEST-IRREVERSIBLE",
                Kind = RemediationKind.Transactable,
                Reversible = false,
                Operation = new RemediationOperation { OpKind = RemediationOpKind.BackupDatabaseNow },
            };

            var cfg = RemediationRollbackProse.NoBatchUndoReason(spConfigure, spConfigure.Key);
            var db = RemediationRollbackProse.NoBatchUndoReason(dbSetOption, dbSetOption.Key);
            var one = RemediationRollbackProse.NoBatchUndoReason(oneShot, oneShot.Key);
            var irr = RemediationRollbackProse.NoBatchUndoReason(irreversible, irreversible.Key);
            var unknown = RemediationRollbackProse.NoBatchUndoReason(null, "TEST-UNREGISTERED");
            foreach (var s in new[] { cfg, db, one, irr, unknown }) _out.WriteLine("REASON :: " + s);

            // sp_configure — the ONLY kind that may be told a value was not read, and it names the
            // setting rather than the template key, because that is what the operator will go and
            // look at on the server.
            Assert.Contains("configured value of 'max degree of parallelism' was not read", cfg, StringComparison.Ordinal);

            // Per-database SET — no per-database before-state is recorded, so there is no inverse.
            Assert.Contains("does not record how each one was set beforehand", db, StringComparison.Ordinal);
            Assert.DoesNotContain("was not read", db, StringComparison.Ordinal);

            // A one-shot action — there is no earlier value at all, so nothing was missed.
            Assert.Contains("is not a settings change", one, StringComparison.Ordinal);
            Assert.DoesNotContain("was not read", one, StringComparison.Ordinal);

            // Declared irreversible wins over its kind: nothing was ever captured.
            Assert.Contains("declares itself not reversible", irr, StringComparison.Ordinal);

            // An unregistered key is an honest unknown, not a missed read.
            Assert.Contains("not a template this build carries", unknown, StringComparison.Ordinal);
            Assert.DoesNotContain("was not read", unknown, StringComparison.Ordinal);

            // Every arm ends on the fact the operator must act on.
            foreach (var s in new[] { cfg, db, one, irr, unknown })
                Assert.Contains("still in place on the server", s, StringComparison.Ordinal);

            // And the four kind-arms are FOUR DIFFERENT SENTENCES. One producer returning one string
            // for every kind is the defect itself, and would pass every "contains" above if the
            // shared tail were the only thing asserted.
            Assert.Equal(4, new[] { cfg, db, one, unknown }.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public async Task TheBatchUndoRoutesTheOpKindREASON_NotOneSentenceForEverything()
        {
            // The wiring half: the producer above must be what the DRIVER prints, or the split is
            // decoration. MAXDOP is sp_configure, so this is the one arm the batch path can reach
            // with a shipped template — and it is the arm that must still say a value was not read.
            var exec = new ScriptedExecutor
            {
                PreChange = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MAXDOP"] = null,
                },
            };
            var w = Wire(exec, "undo-reason-by-kind");

            var applied = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            var undo = await w.Driver.RollBackBatchAsync(applied, "adrian");

            var orphan = undo.PerItem.Single(i => i.TemplateKey == "MAXDOP");
            _out.WriteLine("MAXDOP :: " + orphan.Message);

            Assert.Equal(RemediationRollbackState.NotAvailable, orphan.RollbackState);
            Assert.Contains("configured value of 'max degree of parallelism' was not read",
                orphan.Message, StringComparison.Ordinal);
        }

        // ── AN ITEM THE STOP POLICY NEVER REACHED IS NOT A REFUSAL (gate blocker 3) ─

        [Fact]
        public async Task AnItemTheBatchNeverReached_CarriesNoRefusal_AndNamesTheItemThatStoppedIt()
        {
            // THE DEFECT THIS PINS. A skipped item was recorded as
            // Refused(RemediationRefusal.NotApproved, ...) — a gate-4 verdict on a change that never
            // reached a gate. Gate 4 is APPROVAL, so the record accused the operator of not
            // approving the very batch they had just approved, and any surface reading Refusal
            // would print that enum over an item nobody tried.
            var exec = new ScriptedExecutor
            {
                Outcomes = new Dictionary<string, RemediationOutcome>(StringComparer.OrdinalIgnoreCase)
                {
                    ["OPTIMIZEFORADHOC"] = RemediationOutcome.CouldNotRun,
                },
            };
            var w = Wire(exec, "skipped-not-refused");

            var r = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            foreach (var i in r.PerItem)
                _out.WriteLine($"{i.TemplateKey}: attempted={i.Attempted} refused={i.Result.IsRefused} "
                             + $"refusal={i.Refusal?.ToString() ?? "<none>"} :: {i.Message}");

            Assert.True(r.Stopped);
            var skipped = r.PerItem.Single(i => i.TemplateKey == "CTFP");

            Assert.False(skipped.Attempted);
            Assert.False(skipped.Result.IsRefused);
            Assert.Null(skipped.Refusal);           // the fail-first assertion: this was NotApproved
            Assert.Null(skipped.Outcome);

            // And it names the item that stopped the batch, because "why not mine?" is the only
            // question that row raises.
            Assert.Contains("not attempted", skipped.Message, StringComparison.Ordinal);
            Assert.Contains("OPTIMIZEFORADHOC", skipped.Message, StringComparison.Ordinal);

            // A skipped item is still not treated as mutated and still bills nothing.
            Assert.False(skipped.Mutated);
            Assert.Equal(0, skipped.CreditsCommitted);
        }

        // ── THE UNDO IS LEDGERED, UNDER THE SAME RUN-ID ────────────────────────────

        [Fact]
        public async Task EveryUndoThatRAN_IsLedgered_UnderTheSAMERunIdAsTheApply_AndOnesThatDidNotRunAreNot()
        {
            // A "rollback FAILED" line about a statement nobody sent is a fabricated event in an
            // HMAC-chained ledger. Only an inverse that ran gets an entry.
            var exec = new ScriptedExecutor
            {
                PreChange = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase) { ["CTFP"] = null },
            };
            var w = Wire(exec, "undo-ledgered");

            var applied = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            var undo = await w.Driver.RollBackBatchAsync(applied, "adrian-undo");
            w.Audit.Flush();

            var rolled = ReadAuditEntries(w.AuditDir)
                .Where(e => e.EventType == AuditEventType.RemediationRolledBack).ToList();
            foreach (var e in rolled)
                _out.WriteLine("ROLLEDBACK :: " + string.Join("; ", e.Details.Select(kv => $"{kv.Key}={kv.Value}")));

            // Two inverses ran (CTFP had nothing to put back), so exactly two entries exist.
            Assert.Equal(2, rolled.Count);
            Assert.All(rolled, e => Assert.Equal(applied.RunId, e.Details[AuditLogService.CorrelationDetailKey]));
            Assert.Equal(applied.RunId, undo.RunId);

            var chain = w.Audit.VerifyChain("p3-undo-offline");
            _out.WriteLine($"VERIFYCHAIN intact={chain.Intact} entries={chain.EntryCount} broken={chain.BrokenCount}");
            Assert.True(chain.Intact);
        }
    }
}
