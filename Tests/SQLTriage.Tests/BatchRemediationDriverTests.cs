/* In the name of God, the Merciful, the Compassionate */
/*
 * BatchRemediationDriverTests — the OFFLINE arm of the batch envelope.
 *
 * Ported from the lane/remediation-concentrate spike (spike/rem-concentrate-a @ 10f6c13,
 * 2026-09-01) and re-pointed: the spike's assertions PINNED THE DEFECT (summing the per-item
 * reserved field over-reports the bill). This file pins the FIX — RemediationResult.CreditsCommitted
 * is the number that matches the ledger, and reserved != committed on a batch containing a no-op.
 * The defect is kept visible as a fact about that field (the refunded no-op still REPORTS its
 * reservation) so nobody "fixes" the total by zeroing CreditsCharged and breaking the refund line.
 *
 * It drives the driver over the REAL RemediationRunner, the REAL RemediationTemplateStore, the REAL
 * BundleBackedRemediationCapability, the REAL PersistedRemediationCreditLedger (scratch path) and
 * the REAL AuditLogService (scratch dir) — substituting ONLY the executor, so it needs no SQL
 * Server and runs in every `dotnet test`.
 *
 * WHAT THIS ARM CAN AND CANNOT PROVE:
 *   CAN — the driver's refusal paths never reach the executor at all; the credit arithmetic over
 *         the REAL persisted ledger; that CreditsCharged is the RESERVED amount while
 *         CreditsCommitted is the bill; that the HMAC audit chain verifies Intact after a batch;
 *         that one run-id now joins every entry one approval produced.
 *   CANNOT — that a real SQL Server produces the NoOp / AppliedVerified terminal states this arm
 *         scripts. Only the live arm (BatchRemediationDriverLiveSmokeTests) proves that. Any claim
 *         resting on the SERVER's behaviour must come from there, not from here.
 *
 * The tell that the refusal paths are real: ExplodingExecutor throws on every call. If a refusal
 * path ever touched the executor, these tests fail loudly instead of passing quietly.
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
    public class BatchRemediationDriverTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _scratch;

        public BatchRemediationDriverTests(ITestOutputHelper output)
        {
            _out = output;
            _scratch = Path.Combine(Path.GetTempPath(), "batch-driver-offline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
        }

        public void Dispose()
        {
            try { Directory.Delete(_scratch, recursive: true); } catch { /* test cleanup */ }
        }

        private const string Server = "BATCH-FAKE-SERVER";

        /// <summary>Fails the test if anything reaches it. Proves a refusal path executed nothing.</summary>
        private sealed class ExplodingExecutor : IRemediationExecutor
        {
            public Task<RemediationPreview> PreviewAsync(RemediationRequest r, CancellationToken ct = default) =>
                throw new InvalidOperationException("PreviewAsync must not be reached on a refused batch.");
            public Task<RemediationExecution> ExecuteAsync(RemediationRequest r, CancellationToken ct = default) =>
                throw new InvalidOperationException("ExecuteAsync must not be reached on a refused batch.");
            public bool CanWriteAudit() => true;
        }

        /// <summary>Returns a scripted terminal state per template key, in call order.</summary>
        private sealed class PerKeyExecutor : IRemediationExecutor
        {
            private readonly Dictionary<string, RemediationOutcome> _outcomes;
            /// <summary>Optional per-key preview text, so the no-change detector can be exercised.</summary>
            private readonly Dictionary<string, string>? _previewText;
            public readonly List<string> Calls = new();

            public PerKeyExecutor(Dictionary<string, RemediationOutcome> outcomes,
                Dictionary<string, string>? previewText = null)
            {
                _outcomes = outcomes;
                _previewText = previewText;
            }

            public Task<RemediationPreview> PreviewAsync(RemediationRequest r, CancellationToken ct = default) =>
                Task.FromResult(new RemediationPreview
                {
                    Succeeded = true,
                    WhatIfText = _previewText is not null && _previewText.TryGetValue(r.Template.Key, out var t)
                        ? t
                        : $"would apply {r.Template.Key}",
                });

            public Task<RemediationExecution> ExecuteAsync(RemediationRequest r, CancellationToken ct = default)
            {
                Calls.Add(r.Template.Key);
                var outcome = _outcomes.TryGetValue(r.Template.Key, out var o) ? o : RemediationOutcome.AppliedVerified;
                return Task.FromResult(new RemediationExecution
                {
                    Outcome = outcome,
                    RollbackState = RemediationRollbackState.NotAttempted,
                    PreChangeValue = outcome == RemediationOutcome.NoOp ? null : 4,
                });
            }

            public bool CanWriteAudit() => true;
        }

        private sealed class Wiring
        {
            public BatchRemediationDriver Driver = default!;
            public PersistedRemediationCreditLedger Credits = default!;
            public AuditLogService Audit = default!;
            public string AuditDir = string.Empty;
        }

        private Wiring Wire(IRemediationExecutor executor, int creditsPerServer, string tag)
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
                    Remediation: true, RemediationCreditsPerServer: creditsPerServer),
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
            Directory.GetFiles(auditDir, "audit-*.jsonl")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .SelectMany(File.ReadAllLines)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonSerializer.Deserialize<AuditLogEntry>(l)!)
                .ToList();

        // ── The batch refusals, each proved to have executed nothing ────────────────

        [Fact]
        public async Task AnUnapprovedBatch_IsRefusedWhole_AndTheExecutorIsNeverReached()
        {
            var w = Wire(new ExplodingExecutor(), 10, "unapproved");
            var before = w.Credits.AvailableFor(Server);

            var r = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: false, "adrian");

            _out.WriteLine($"refused={r.IsRefused} reason={r.Refusal} :: {r.Message}");
            Assert.True(r.IsRefused);
            Assert.Equal(BatchRefusal.NotApproved, r.Refusal);
            Assert.Empty(r.Items);
            Assert.Equal(before, w.Credits.AvailableFor(Server));
            Assert.Equal(0, w.Credits.GetBreakdown(Server).Outstanding);
        }

        [Fact]
        public async Task ABatchWithOneUnregisteredKey_IsRefusedWhole_SoTheGoodItemsDoNotHalfApply()
        {
            var w = Wire(new ExplodingExecutor(), 10, "unregistered");
            var items = ThreeItems();
            items.Insert(1, new BatchRemediationItem("NOT-A-REAL-TEMPLATE"));

            var r = await w.Driver.ApplyBatchAsync(Server, items, approved: true, "adrian");

            _out.WriteLine($"refused={r.IsRefused} reason={r.Refusal} :: {r.Message}");
            Assert.True(r.IsRefused);
            Assert.Equal(BatchRefusal.UnregisteredTemplate, r.Refusal);
            Assert.Contains("NOT-A-REAL-TEMPLATE", r.Message, StringComparison.Ordinal);
            Assert.Empty(r.Items);
            Assert.Equal(0, w.Credits.GetBreakdown(Server).Committed);
        }

        [Fact]
        public async Task ABatchThatOutpricesTheBalance_IsRefusedBeforeAnyItemRuns()
        {
            // 2 credits allocated; the 3-item batch prices at 3.
            var w = Wire(new ExplodingExecutor(), 2, "outpriced");

            var r = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");

            _out.WriteLine($"refused={r.IsRefused} reason={r.Refusal} priced={r.PricedTotal} "
                         + $"available={r.AvailableAtPreCheck} :: {r.Message}");
            Assert.True(r.IsRefused);
            Assert.Equal(BatchRefusal.InsufficientCreditsForBatch, r.Refusal);
            Assert.Equal(3, r.PricedTotal);
            Assert.Equal(2, r.AvailableAtPreCheck);
            Assert.Empty(r.Items);
            Assert.Equal(2, w.Credits.AvailableFor(Server));
        }

        [Fact]
        public async Task AnEmptyBatch_IsRefused_AndTheExecutorIsNeverReached()
        {
            var w = Wire(new ExplodingExecutor(), 10, "empty");

            var r = await w.Driver.ApplyBatchAsync(Server, new List<BatchRemediationItem>(), approved: true, "adrian");

            Assert.True(r.IsRefused);
            Assert.Equal(BatchRefusal.NothingToApply, r.Refusal);
            Assert.Empty(r.Items);
        }

        // ── ITEM 1.2: the credit split, against the REAL persisted ledger ───────────

        [Fact]
        public async Task ABatchContainingANoOp_RefundsThatItem_AndCreditsCommittedMatchesTheLedgerWhileReservedDoesNot()
        {
            var exec = new PerKeyExecutor(new Dictionary<string, RemediationOutcome>
            {
                ["MAXDOP"] = RemediationOutcome.AppliedVerified,
                ["OPTIMIZEFORADHOC"] = RemediationOutcome.NoOp,      // the forced no-op, mid-batch
                ["CTFP"] = RemediationOutcome.AppliedVerified,
            });
            var w = Wire(exec, 10, "noop");
            int before = w.Credits.AvailableFor(Server);

            var r = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian-p1");

            Assert.False(r.IsRefused);
            foreach (var it in r.Items)
                _out.WriteLine($"ITEM {it.TemplateKey}: outcome={it.Result.Outcome} "
                             + $"CreditsCharged(reserved)={it.Result.CreditsCharged} "
                             + $"CreditsCommitted={it.Result.CreditsCommitted}");

            // The no-op did NOT stop the batch: all three ran, in order.
            Assert.Equal(new[] { "MAXDOP", "OPTIMIZEFORADHOC", "CTFP" }, exec.Calls);
            Assert.All(r.Items, i => Assert.True(i.Attempted));

            int after = w.Credits.AvailableFor(Server);
            var breakdown = w.Credits.GetBreakdown(Server);
            _out.WriteLine($"CREDITS reserved={r.CreditsReserved} committed={r.CreditsCommitted} "
                         + $"refunded={r.CreditsRefunded} | ledger {before} -> {after} | "
                         + $"committed(ledger)={breakdown.Committed} outstanding={breakdown.Outstanding}");

            // THE HEADLINE: three reservations of 1, one refunded by the no-op.
            Assert.Equal(3, r.CreditsReserved);
            Assert.Equal(2, r.CreditsCommitted);
            Assert.Equal(1, r.CreditsRefunded);

            // THE FIX, pinned: the ledger delta equals COMMITTED and does NOT equal RESERVED.
            Assert.Equal(before - r.CreditsCommitted, after);
            Assert.NotEqual(before - after, r.CreditsReserved);
            Assert.Equal(2, breakdown.Committed);
            Assert.Equal(0, breakdown.Outstanding);      // nothing left dangling

            // The per-item split, structurally: the refunded no-op still REPORTS its reservation
            // (so "1 change credit was refunded" names the real 1) but commits nothing. A future
            // edit that "fixes" the total by zeroing CreditsCharged on a no-op fails here.
            var noop = r.Items.Single(i => i.TemplateKey == "OPTIMIZEFORADHOC").Result;
            Assert.Equal(1, noop.CreditsCharged);
            Assert.Equal(0, noop.CreditsCommitted);
            var applied = r.Items.Single(i => i.TemplateKey == "MAXDOP").Result;
            Assert.Equal(1, applied.CreditsCharged);
            Assert.Equal(1, applied.CreditsCommitted);

            _out.WriteLine("PROVED: reserved sums to 3, committed sums to 2, and the ledger took 2. "
                         + "An operator-facing batch total must read CreditsCommitted.");
        }

        [Fact]
        public async Task ARefusedItem_CommitsNothing_BecauseItNeverReserved()
        {
            // All items registered but the runner refuses each one (no capability): reserved and
            // committed are both zero, and they are zero for a DIFFERENT reason than a refund —
            // nothing was ever reserved.
            var root = Path.Combine(_scratch, "refused-item");
            Directory.CreateDirectory(root);
            var bundle = new FakeBundleAccessor
            {
                IsUnlocked = true,
                Tier = Tier.Full,
                Features = new BundleFeatures(
                    RagEnabled: false, SpBlitzImport: false, FullCorpus: false,
                    PermittedCheckIds: Array.Empty<int>(),
                    Remediation: false, RemediationCreditsPerServer: 10),
            };
            using var audit = new AuditLogService(Path.Combine(root, "audit-logs"), startFlushTimer: false);
            var templates = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var credits = new PersistedRemediationCreditLedger(
                bundle, NullLogger<PersistedRemediationCreditLedger>.Instance, null,
                Path.Combine(root, "remediation-credit-ledger.json"));
            var runner = new RemediationRunner(templates, new BundleBackedRemediationCapability(bundle),
                credits, new PerKeyExecutor(new Dictionary<string, RemediationOutcome>()), audit,
                NullLogger<RemediationRunner>.Instance);
            var driver = new BatchRemediationDriver(runner, templates, credits);

            var r = await driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");

            // ⚠ THIS ASSERTION WAS CORRECTED IN FIX ROUND 1 (gate blocker 3), and the correction is
            // the finding. It read `Assert.All(r.Items, i => Assert.True(i.Result.IsRefused))` and
            // passed — but only the FIRST item was ever put to a gate. The stop policy ends the
            // batch on that refusal, so items 2 and 3 were never attempted at all, and the driver
            // was labelling them Refused(NotApproved): a gate-4 verdict on a change nobody submitted
            // to gate 4, over a batch the operator HAD approved. The test was green over a
            // fabricated per-item verdict. It now asserts the shape that is true.
            Assert.True(r.Stopped);

            var refused = r.Items.Where(i => i.Attempted).ToList();
            var neverTried = r.Items.Where(i => !i.Attempted).ToList();
            Assert.Single(refused);
            Assert.Equal(2, neverTried.Count);

            Assert.True(refused[0].Result.IsRefused);
            Assert.Equal(RemediationRefusal.CapabilityDenied, refused[0].Refusal);

            // Never attempted carries NO refusal at all — the whole point of the correction.
            Assert.All(neverTried, i => Assert.False(i.Result.IsRefused));
            Assert.All(neverTried, i => Assert.Null(i.Refusal));

            // The money claim is unchanged and is still the point of this test: zero everywhere,
            // and zero because nothing was ever reserved rather than because it was refunded.
            Assert.All(r.Items, i => Assert.Equal(0, i.Result.CreditsCharged));
            Assert.All(r.Items, i => Assert.Equal(0, i.Result.CreditsCommitted));
            Assert.Equal(0, r.CreditsReserved);
            Assert.Equal(0, r.CreditsCommitted);
        }

        // ── ITEM 1.5: the audit chain after a batch, and the run-id that joins it ───

        [Fact]
        public async Task AfterABatch_TheHmacChainVerifiesIntact_AndOneRunIdJoinsEveryEntry()
        {
            var exec = new PerKeyExecutor(new Dictionary<string, RemediationOutcome>
            {
                ["MAXDOP"] = RemediationOutcome.AppliedVerified,
                ["OPTIMIZEFORADHOC"] = RemediationOutcome.NoOp,
                ["CTFP"] = RemediationOutcome.AppliedVerified,
            });
            var w = Wire(exec, 10, "chain");

            // ONE decision: preview then apply, carrying ONE run-id across both, exactly as a UI
            // that previews before approving must.
            var runId = BatchRemediationDriver.NewRunId();
            await w.Driver.PreviewBatchAsync(Server, ThreeItems(), runId);
            var batch = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian-p1",
                BatchStopPolicy.StopOnFirstFailure, runId);
            w.Audit.Flush();

            Assert.Equal(runId, batch.RunId);

            var v = w.Audit.VerifyChain("p1-batch-offline");
            _out.WriteLine($"VERIFYCHAIN intact={v.Intact} status={v.Status} entries={v.EntryCount} "
                         + $"broken={v.BrokenCount} unverifiable={v.UnverifiableCount} "
                         + $"linkBreaks={v.LinkBreakCount} duplicates={v.DuplicateEntryCount}");

            // The chain still verifies WITH the new Details key present on every remediation entry.
            Assert.True(v.Intact);
            Assert.Equal(AuditLogService.ChainVerificationStatus.Intact, v.Status);
            Assert.Equal(0, v.BrokenCount);
            Assert.Equal(0, v.LinkBreakCount);

            var remediation = ReadAuditEntries(w.AuditDir)
                .Where(e => e.EventType is AuditEventType.RemediationProposed
                                        or AuditEventType.RemediationApproved
                                        or AuditEventType.RemediationApplied
                                        or AuditEventType.RemediationRolledBack)
                .ToList();
            foreach (var e in remediation)
                _out.WriteLine($"  {e.EventType} :: "
                             + string.Join("; ", e.Details.Select(kv => $"{kv.Key}={kv.Value}")));

            // ONE operator decision produced 3 Proposed + 3 Approved + 3 Applied entries…
            Assert.Equal(3, remediation.Count(e => e.EventType == AuditEventType.RemediationProposed));
            Assert.Equal(3, remediation.Count(e => e.EventType == AuditEventType.RemediationApproved));
            Assert.Equal(3, remediation.Count(e => e.EventType == AuditEventType.RemediationApplied));

            // …and THE JOIN, asserted POSITIVELY: every one of the nine carries the same RunId, so
            // "show me everything that one approval authorised" is a ledger query, not an inference
            // from adjacency. This is the assertion the spike could only make negatively.
            Assert.Equal(9, remediation.Count);
            Assert.All(remediation, e =>
            {
                Assert.True(e.Details.ContainsKey(AuditLogService.CorrelationDetailKey),
                    $"{e.EventType} carries no {AuditLogService.CorrelationDetailKey}");
                Assert.Equal(runId, e.Details[AuditLogService.CorrelationDetailKey]);
            });
            Assert.Single(remediation.Select(e => e.Details[AuditLogService.CorrelationDetailKey]).Distinct());
            _out.WriteLine($"PROVED: all {remediation.Count} entries of one approval share RunId={runId}.");
        }

        [Fact]
        public async Task TwoSeparateBatches_GetDifferentRunIds_SoTheJoinDoesNotOverCollect()
        {
            // A join that matched everything would be as useless as no join at all.
            var exec = new PerKeyExecutor(new Dictionary<string, RemediationOutcome>());
            var w = Wire(exec, 10, "two-batches");

            var a = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            var b = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");
            w.Audit.Flush();

            Assert.NotEqual(string.Empty, a.RunId);
            Assert.NotEqual(a.RunId, b.RunId);

            var applied = ReadAuditEntries(w.AuditDir)
                .Where(e => e.EventType == AuditEventType.RemediationApplied)
                .ToList();
            Assert.Equal(6, applied.Count);
            Assert.Equal(3, applied.Count(e => e.Details[AuditLogService.CorrelationDetailKey] == a.RunId));
            Assert.Equal(3, applied.Count(e => e.Details[AuditLogService.CorrelationDetailKey] == b.RunId));
        }

        // ── The stop policy ─────────────────────────────────────────────────────────

        [Fact]
        public async Task WhenAnItemCouldNotRun_TheBatchStops_AndLaterItemsAreNotAttempted()
        {
            var exec = new PerKeyExecutor(new Dictionary<string, RemediationOutcome>
            {
                ["MAXDOP"] = RemediationOutcome.AppliedVerified,
                ["OPTIMIZEFORADHOC"] = RemediationOutcome.CouldNotRun,   // the failure
                ["CTFP"] = RemediationOutcome.AppliedVerified,           // must never be called
            });
            var w = Wire(exec, 10, "stop");
            int before = w.Credits.AvailableFor(Server);

            var r = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian-p1");

            foreach (var it in r.Items)
                _out.WriteLine($"ITEM {it.TemplateKey}: attempted={it.Attempted} outcome={it.Result.Outcome}");
            _out.WriteLine("Executor calls: " + string.Join(", ", exec.Calls));

            Assert.Equal(new[] { "MAXDOP", "OPTIMIZEFORADHOC" }, exec.Calls);   // CTFP never ran
            Assert.False(r.Items.Single(i => i.TemplateKey == "CTFP").Attempted);

            // One commit (MAXDOP), one refund (CouldNotRun), one never reserved.
            Assert.Equal(2, r.CreditsReserved);
            Assert.Equal(1, r.CreditsCommitted);
            Assert.Equal(before - 1, w.Credits.AvailableFor(Server));
            Assert.Equal(0, w.Credits.GetBreakdown(Server).Outstanding);
            _out.WriteLine($"Stop policy held. Credits {before} -> {w.Credits.AvailableFor(Server)}.");
        }

        [Fact]
        public async Task UnderContinueOnFailure_EveryItemIsStillAttempted()
        {
            var exec = new PerKeyExecutor(new Dictionary<string, RemediationOutcome>
            {
                ["MAXDOP"] = RemediationOutcome.AppliedVerified,
                ["OPTIMIZEFORADHOC"] = RemediationOutcome.CouldNotRun,
                ["CTFP"] = RemediationOutcome.AppliedVerified,
            });
            var w = Wire(exec, 10, "continue");

            var r = await w.Driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian-p1",
                BatchStopPolicy.ContinueOnFailure);

            Assert.Equal(new[] { "MAXDOP", "OPTIMIZEFORADHOC", "CTFP" }, exec.Calls);
            Assert.All(r.Items, i => Assert.True(i.Attempted));
            Assert.Equal(3, r.CreditsReserved);
            Assert.Equal(2, r.CreditsCommitted);   // the CouldNotRun refunded
        }

        // ── The 5-gate core is genuinely untouched ──────────────────────────────────

        [Fact]
        public async Task TheDriverAddsNoGate_ACapabilityDenialStillRefusesEveryItemAtTheRunner()
        {
            // A licence WITHOUT the remediation claim. The driver has no capability logic at all,
            // so the refusal must come from the runner's gate 2, per item.
            var root = Path.Combine(_scratch, "nocap");
            Directory.CreateDirectory(root);
            var bundle = new FakeBundleAccessor
            {
                IsUnlocked = true,
                Tier = Tier.Full,
                Features = new BundleFeatures(
                    RagEnabled: false, SpBlitzImport: false, FullCorpus: false,
                    PermittedCheckIds: Array.Empty<int>(),
                    Remediation: false, RemediationCreditsPerServer: 10),   // claim NOT granted
            };
            var capability = new BundleBackedRemediationCapability(bundle);
            _out.WriteLine($"ServerConfigSuiteGate.Evaluate = {ServerConfigSuiteGate.Evaluate(bundle)}; "
                         + $"capability.IsGranted = {capability.IsGranted}");

            using var audit = new AuditLogService(Path.Combine(root, "audit-logs"), startFlushTimer: false);
            var templates = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var credits = new PersistedRemediationCreditLedger(
                bundle, NullLogger<PersistedRemediationCreditLedger>.Instance, null,
                Path.Combine(root, "remediation-credit-ledger.json"));
            var exec = new PerKeyExecutor(new Dictionary<string, RemediationOutcome>());
            var runner = new RemediationRunner(templates, capability, credits, exec, audit,
                NullLogger<RemediationRunner>.Instance);
            var driver = new BatchRemediationDriver(runner, templates, credits);

            var r = await driver.ApplyBatchAsync(Server, ThreeItems(), approved: true, "adrian");

            // The batch is NOT refused by the driver — it has no capability gate. Every ITEM is
            // refused by the runner, which is the point: the gate stayed where it was.
            Assert.False(r.IsRefused);
            Assert.All(r.Items.Where(i => i.Attempted), i =>
            {
                Assert.True(i.Result.IsRefused);
                Assert.Equal(RemediationRefusal.CapabilityDenied, i.Result.Refusal);
            });
            Assert.Empty(exec.Calls);   // nothing reached the executor
            Assert.Equal(0, r.CreditsReserved);
            _out.WriteLine("PROVED: the driver adds no gate; gate 2 refused each item at the runner, "
                         + "and the executor was never reached.");
        }

        // ── The combined preview: per-item "no change" + the two prices ─────────────

        [Fact]
        public async Task TheCombinedPreview_FlagsAnAlreadyCompliantItem_AndQuotesTheChangingPriceApart()
        {
            // ⚠ THE PREVIEW TEXT IS BUILT BY THE PRODUCTION FORMATTER, not written out here.
            // It used to be three hand-typed literals "in the shipped executor's exact shape" —
            // which is a belief about the executor, not a binding to it. RemediationPreviewSentence
            // .ConfigurationPreview is the method DbatoolsRemediationExecutor itself calls, so an
            // executor wording change now moves BOTH sides of this test and DetectNoChange with it.
            var exec = new PerKeyExecutor(new Dictionary<string, RemediationOutcome>(),
                new Dictionary<string, string>
                {
                    ["MAXDOP"] = RemediationPreviewSentence.ConfigurationPreview(
                        "X", "EXEC sp_configure ...", "max degree of parallelism", "0", 4),
                    ["OPTIMIZEFORADHOC"] = RemediationPreviewSentence.ConfigurationPreview(
                        "X", "EXEC sp_configure ...", "optimize for ad hoc workloads", "1", 1),
                    // current: null is what the executor passes when the read failed or was never
                    // attempted, and the formatter is the thing that turns that into "(unread)".
                    ["CTFP"] = RemediationPreviewSentence.ConfigurationPreview(
                        "X", "EXEC sp_configure ...", "cost threshold for parallelism", null, 50),
                });
            var w = Wire(exec, 10, "preview");

            var preview = await w.Driver.PreviewBatchDetailedAsync(Server, ThreeItems());

            foreach (var row in preview.Items)
                _out.WriteLine($"PREVIEW {row.TemplateKey}: price={row.Price} "
                             + $"noChange={row.IsNoChange?.ToString() ?? "unknown"}");

            Assert.False(preview.Items.Single(i => i.TemplateKey == "MAXDOP").IsNoChange);
            Assert.True(preview.Items.Single(i => i.TemplateKey == "OPTIMIZEFORADHOC").IsNoChange);
            // (unread) is UNKNOWN, not "no change" — and an unknown is priced, never quietly free.
            Assert.Null(preview.Items.Single(i => i.TemplateKey == "CTFP").IsNoChange);

            Assert.Equal(3, preview.PricedTotal);    // worst-case exposure
            Assert.Equal(2, preview.ChangingPrice);  // the honest quote
            Assert.Equal(1, preview.NoChangeCount);

            // Preview is a pure read: nothing reserved, nothing committed, executor never applied.
            Assert.Empty(exec.Calls);
            Assert.Equal(10, w.Credits.AvailableFor(Server));
            Assert.Equal(0, w.Credits.GetBreakdown(Server).Outstanding);
        }

        [Fact]
        public async Task ThePreviewWritesItsProposedEntriesUnderTheSameRunId()
        {
            var exec = new PerKeyExecutor(new Dictionary<string, RemediationOutcome>());
            var w = Wire(exec, 10, "preview-audit");

            var preview = await w.Driver.PreviewBatchDetailedAsync(Server, ThreeItems());
            w.Audit.Flush();

            var proposed = ReadAuditEntries(w.AuditDir)
                .Where(e => e.EventType == AuditEventType.RemediationProposed).ToList();
            Assert.Equal(3, proposed.Count);
            Assert.All(proposed, e => Assert.Equal(preview.RunId, e.Details[AuditLogService.CorrelationDetailKey]));
        }

        // ── The no-change detector is BOUND to the sentence's real producer ─────────
        //
        // THE DEFECT THIS SECTION CLOSES (Phase-1 gate, 2026-09-01). DetectNoChange parses a
        // sentence DbatoolsRemediationExecutor writes. The two lived in different files with no
        // shared declaration, and the tests above hand-typed the sentence a third time — so the
        // only thing holding producer and parser together was that three authors had agreed. An
        // executor wording change would have left every test green while the batch surface answered
        // "unknown" for every item, and an unknown is priced as a change: the operator would be
        // quoted for work that was already done.

        [Theory]
        [InlineData("4", 4, true)]          // already at target
        [InlineData("0", 4, false)]         // would change
        [InlineData("-1", -1, true)]        // negative values round-trip
        [InlineData(null, 4, null)]         // the read failed -> "(unread)" -> unknown, never "no change"
        [InlineData("", 4, null)]           // a read that returned nothing is also not a value
        [InlineData("NULL", 4, null)]       // a non-numeric reading is unknown, not equal-by-string
        public void DetectNoChange_ReadsTheTextTheRealProducerEmits(string? current, int target, bool? expected)
        {
            // The text is produced by the SAME method DbatoolsRemediationExecutor calls — the
            // production formatting code, driven offline, with no server anywhere near it.
            var text = RemediationPreviewSentence.ConfigurationPreview(
                "SRV\\INST", "EXEC sp_configure 'max degree of parallelism', 4; RECONFIGURE;",
                "max degree of parallelism", current, target);

            var answer = BatchRemediationDriver.DetectNoChange(
                RemediationProposal.Previewed(new RemediationPreview { Succeeded = true, WhatIfText = text }));

            _out.WriteLine($"current={current ?? "(null)"} target={target} -> "
                         + $"{answer?.ToString() ?? "unknown"}\n{text}");
            Assert.Equal(expected, answer);
        }

        [Fact]
        public void TheParserIsBuiltFromTheProducersOwnFormat()
        {
            // Not "the parser happens to match a sentence I typed": the pattern is derived from the
            // format constant at type-init, so this fails the moment the two can disagree.
            var pattern = RemediationPreviewSentence.CurrentVersusTargetPattern;
            var sentence = RemediationPreviewSentence.CurrentVersusTarget("some option", "7", 9);

            var m = pattern.Match(sentence);
            Assert.True(m.Success, $"the derived pattern does not match its own producer's output: {sentence}");
            Assert.Equal("some option", m.Groups["config"].Value);
            Assert.Equal("7", m.Groups["current"].Value);
            Assert.Equal("9", m.Groups["target"].Value);

            // And "(unread)" matches the SHAPE — it must, or an unreadable value would look like a
            // sentence that was never emitted. It is rejected later, when it fails to parse.
            Assert.True(pattern.IsMatch(RemediationPreviewSentence.CurrentVersusTarget("o", null, 1)));
        }

        [Fact]
        public void TheExecutorBuildsThePreviewSentenceThroughTheSharedFormat()
        {
            // The source half of the binding. Sharing a constant only helps while the producer
            // actually uses it, and nothing in the type system stops someone re-typing the sentence
            // inline. This reads the executor and fails if they do.
            var path = Path.Combine(RawPassedScan.RepoRoot().FullName,
                "Data", "Services", "Remediation", "DbatoolsRemediationExecutor.cs");
            var code = string.Join("\n", RawPassedScan.StripComments(
                File.ReadAllText(path).Replace("\r\n", "\n").Split('\n'), isRazor: false));

            Assert.Contains("RemediationPreviewSentence.ConfigurationPreview", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Current '", code, StringComparison.Ordinal);
        }
    }
}
