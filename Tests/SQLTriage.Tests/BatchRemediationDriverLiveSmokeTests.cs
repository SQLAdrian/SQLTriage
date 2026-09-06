/* In the name of God, the Merciful, the Compassionate */
/*
 * BatchRemediationDriverLiveSmokeTests — the LIVE arm of the batch envelope.
 *
 * Ported from the lane/remediation-concentrate spike (spike/rem-concentrate-a @ a81dc89,
 * 2026-09-01) and re-pointed at the Phase-1 fixes: the credit assertions now pin the SPLIT
 * (CreditsCommitted matches the ledger delta; CreditsReserved does not), and the audit assertions
 * pin the RUN-ID POSITIVELY (every entry one approval wrote carries the same join key), where the
 * spike could only assert the absence of one.
 *
 * Exercises BatchRemediationDriver over the REAL RemediationRunner, the REAL
 * DbatoolsRemediationExecutor, the REAL PersistedRemediationCreditLedger (over a scratch path) and
 * the REAL AuditLogService (over a scratch directory) against a LIVE SQL Server.
 *
 * SKIPPED unless REMSAFE_LIVE_TARGET names a reachable instance, so a normal `dotnet test` run
 * never touches a server. The arming check is a RUNTIME one, deliberately, not a compile-time
 * profile symbol: a test gated on a build symbol silently disappears from the profile that does not
 * define it.
 *
 * ⚠ CORRECTED AT THE PHASE-1 GATE, 2026-09-01. The first version of this file wrote its arming
 * check as `if (string.IsNullOrWhiteSpace(target)) { ...; return; }`, which reports PASSED — two
 * assertion-free passes in 8ms on a box with no armed target, folded into every later "the suite is
 * green" claim. That is the defect class Portal/LiveHarnessArmingCensusTests exists to kill, and
 * that census did not catch it: its structural walk needs the GetEnvironmentVariable read and the
 * bare return within six lines of each other, and this file hoists the read into the Target
 * property — the property-hoisting blind spot that census records against itself, measured on
 * IndexAnalysisLiveSmokeTests on 2026-08-13 and still open. Both tests now carry LiveFactAttribute,
 * which computes Skip at DISCOVERY time, plus RequireTarget() in the body so removing the attribute
 * fails rather than passes.
 *
 * INVOCATION:
 *   $env:REMSAFE_LIVE_TARGET = ".\new2022"
 *   dotnet test Tests/SQLTriage.Tests --filter "FullyQualifiedName~BatchRemediationDriverLiveSmokeTests"
 *
 * SUBSTITUTIONS, stated so dependent claims can be downgraded:
 *   - Gate 2 (capability) runs the REAL BundleBackedRemediationCapability over the REAL
 *     ServerConfigSuiteGate. What is substituted is the LICENCE SOURCE: a FakeBundleAccessor stands
 *     in for a signed bundle file (Tier=Full, Features.Remediation=true,
 *     RemediationCreditsPerServer=N). The gate LOGIC is exercised; the signature check on a real
 *     minted bundle is NOT — that stays untested by this file. This is the shipped test convention
 *     (ConfigStoreWriteGuardTests).
 *   - Everything else — templates, renderer, safety validator, executor, credit ledger, audit
 *     chain, and the SQL Server itself — is the real component.
 *
 * RESIDUE: every sp_configure value touched is captured before and restored after, then re-read to
 * prove restoration. All three targets are asserted is_dynamic = 1, so no restart-required option
 * is ever written.
 *
 * ⚠ KNOWN AND UNADDRESSED IN PHASE 1 (spike S1 §3.10): the renderer emits
 * `EXEC sp_configure 'show advanced options', 1` ahead of every advanced-option change, and neither
 * this harness nor the engine's before-state capture baselines or restores THAT option. On a server
 * where it starts at 0 this run would leave it at 1, unrecorded. Capturing from the RENDERED
 * statement is plan item 2.3. Do not read this file's restore proof as covering that option.
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
    public class BatchRemediationDriverLiveSmokeTests
    {
        private readonly ITestOutputHelper _out;
        public BatchRemediationDriverLiveSmokeTests(ITestOutputHelper output) => _out = output;

        /// <summary>The environment variable that arms this harness. Named once, used by both the
        /// discovery-time <see cref="LiveFactAttribute"/> and the in-body assertion below.</summary>
        private const string TargetVariable = "REMSAFE_LIVE_TARGET";

        private static string? Target => Environment.GetEnvironmentVariable(TargetVariable);

        /// <summary>
        /// ⚠ THE ATTRIBUTE IS NOT THE WHOLE GUARD (2026-09-01 gate fix). Both tests below used to
        /// early-return to GREEN when the target was unset: two assertion-free passes in 8ms, folded
        /// into every later "the suite is green" claim — the house defect class a verdict not
        /// conditioned on the measurement it names. <see cref="LiveFactAttribute"/> now reports them
        /// SKIPPED at discovery time, and this assertion makes the body FAIL rather than pass
        /// vacuously if that attribute is ever weakened or removed. Same pattern as
        /// IndexAnalysisLiveSmokeTests.RequireTarget.
        /// </summary>
        private static string RequireTarget()
        {
            Assert.False(string.IsNullOrWhiteSpace(Target),
                TargetVariable + " is not set, so this test has no instance to touch and nothing to "
                + "assert. It should have been SKIPPED by LiveFactAttribute; if it ran, that "
                + "attribute is no longer doing its job.");
            return Target!;
        }

        // The three batched templates and the sp_configure option each one writes.
        private const string KeyMaxDop = "MAXDOP";
        private const string CfgMaxDop = "max degree of parallelism";
        private const string KeyCtfp = "CTFP";
        private const string CfgCtfp = "cost threshold for parallelism";
        private const string KeyAdHoc = "OPTIMIZEFORADHOC";
        private const string CfgAdHoc = "optimize for ad hoc workloads";

        private static string ConnString(string target, string db = "master") =>
            $"Server={target};Database={db};Integrated Security=true;TrustServerCertificate=true;Connection Timeout=15;";

        private sealed class Wiring
        {
            public BatchRemediationDriver Driver = default!;
            public RemediationRunner Runner = default!;
            public PersistedRemediationCreditLedger Credits = default!;
            public AuditLogService Audit = default!;
            public RemediationTemplateStore Templates = default!;
            public string AuditDir = string.Empty;
            public string LedgerPath = string.Empty;
            public string ScratchRoot = string.Empty;
        }

        /// <summary>
        /// Builds the whole lane against SCRATCH stores inside the test temp tree. No real install
        /// path is touched: the audit chain (and its HMAC key) lives in a fresh directory, and the
        /// credit ledger's json sits beside it via the shipped pathOverride test seam.
        /// </summary>
        private static Wiring Wire(string target, int creditsPerServer)
        {
            var scratch = Path.Combine(Path.GetTempPath(), "batch-driver-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            var auditDir = Path.Combine(scratch, "audit-logs");
            var ledgerPath = Path.Combine(scratch, "remediation-credit-ledger.json");

            var connections = new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance);
            connections.AddConnection(new ServerConnection
            {
                ServerNames = target,
                UseWindowsAuthentication = true,
                TrustServerCertificate = true,
                IsEnabled = true,
            });

            var audit = new AuditLogService(auditDir, startFlushTimer: false);

            var executor = new DbatoolsRemediationExecutor(
                new PowerShellService(NullLogger<PowerShellService>.Instance),
                connections, audit,
                new DiskIoService(NullLogger<DiskIoService>.Instance),
                NullLogger<DbatoolsRemediationExecutor>.Instance);

            // Gate 2: the REAL capability object over a substituted licence source.
            var bundle = new FakeBundleAccessor
            {
                IsUnlocked = true,
                Tier = Tier.Full,
                Features = new BundleFeatures(
                    RagEnabled: false, SpBlitzImport: false, FullCorpus: false,
                    PermittedCheckIds: Array.Empty<int>(),
                    Remediation: true, RemediationCreditsPerServer: creditsPerServer),
            };
            var capability = new BundleBackedRemediationCapability(bundle);

            // Gate 3: the REAL persisted ledger, over a scratch file.
            var credits = new PersistedRemediationCreditLedger(
                bundle, NullLogger<PersistedRemediationCreditLedger>.Instance, null, ledgerPath);

            var templates = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var runner = new RemediationRunner(templates, capability, credits, executor, audit,
                NullLogger<RemediationRunner>.Instance);

            return new Wiring
            {
                Driver = new BatchRemediationDriver(runner, templates, credits),
                Runner = runner,
                Credits = credits,
                Audit = audit,
                Templates = templates,
                AuditDir = auditDir,
                LedgerPath = ledgerPath,
                ScratchRoot = scratch,
            };
        }

        // ── independent SQL reads/writes (a second connection the services never saw) ──

        private static int ReadConfig(string target, string name)
        {
            using var conn = new SqlConnection(ConnString(target));
            conn.Open();
            using var cmd = new SqlCommand("SELECT value_in_use FROM sys.configurations WHERE name = @n;", conn);
            cmd.Parameters.AddWithValue("@n", name);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private static bool ReadIsDynamic(string target, string name)
        {
            using var conn = new SqlConnection(ConnString(target));
            conn.Open();
            using var cmd = new SqlCommand("SELECT is_dynamic FROM sys.configurations WHERE name = @n;", conn);
            cmd.Parameters.AddWithValue("@n", name);
            return Convert.ToBoolean(cmd.ExecuteScalar());
        }

        private static void SetConfig(string target, string name, int value)
        {
            using var conn = new SqlConnection(ConnString(target));
            conn.Open();
            using var cmd = new SqlCommand(
                "EXEC sp_configure 'show advanced options', 1; RECONFIGURE; " +
                $"EXEC sp_configure '{name}', {value}; RECONFIGURE;", conn)
            { CommandTimeout = 30 };
            cmd.ExecuteNonQuery();
        }

        private static List<AuditLogEntry> ReadAuditEntries(string auditDir) =>
            Directory.GetFiles(auditDir, "audit-*.jsonl")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .SelectMany(File.ReadAllLines)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonSerializer.Deserialize<AuditLogEntry>(l)!)
                .ToList();

        // ══════════════════════════════════════════════════════════════════════════════
        //  One batch of 3, one approval, one run-id, per-item credits, one NoOp forced.
        // ══════════════════════════════════════════════════════════════════════════════

        [LiveFact(TargetVariable)]
        public async Task Batch_OfThreeConfigFixes_UnderOneApproval_SplitsCreditsCorrectly_AndJoinsEveryAuditEntryByRunId()
        {
            var target = RequireTarget();

            // ── BASELINE (captured before anything is touched) ────────────────────────
            int baseMaxDop = ReadConfig(target, CfgMaxDop);
            int baseCtfp = ReadConfig(target, CfgCtfp);
            int baseAdHoc = ReadConfig(target, CfgAdHoc);
            _out.WriteLine($"BASELINE {target}: {CfgMaxDop}={baseMaxDop} (is_dynamic={ReadIsDynamic(target, CfgMaxDop)}) | "
                         + $"{CfgCtfp}={baseCtfp} (is_dynamic={ReadIsDynamic(target, CfgCtfp)}) | "
                         + $"{CfgAdHoc}={baseAdHoc} (is_dynamic={ReadIsDynamic(target, CfgAdHoc)})");

            // Every option this test writes must be dynamic — no restart-required option is touched.
            Assert.True(ReadIsDynamic(target, CfgMaxDop));
            Assert.True(ReadIsDynamic(target, CfgCtfp));
            Assert.True(ReadIsDynamic(target, CfgAdHoc));

            // Targets chosen to differ from baseline for the two that must really change.
            int wantMaxDop = baseMaxDop == 2 ? 4 : 2;
            int wantCtfp = baseCtfp == 50 ? 45 : 50;
            int wantAdHoc = 1; // the template's own RecommendedValue

            var w = Wire(target, creditsPerServer: 10);
            try
            {
                // ── FORCE THE NoOp: pre-set ad-hoc to the value the batch will ask for ──
                if (baseAdHoc != wantAdHoc) SetConfig(target, CfgAdHoc, wantAdHoc);
                Assert.Equal(wantAdHoc, ReadConfig(target, CfgAdHoc));
                _out.WriteLine($"NoOp armed: '{CfgAdHoc}' pre-set to {wantAdHoc}; the batch will request {wantAdHoc}.");

                var items = new List<BatchRemediationItem>
                {
                    new(KeyMaxDop, new Dictionary<string, string> { ["MaxDop"] = wantMaxDop.ToString() }),
                    new(KeyAdHoc,  new Dictionary<string, string> { ["Enabled"] = wantAdHoc.ToString() }),
                    new(KeyCtfp,   new Dictionary<string, string> { ["CostThreshold"] = wantCtfp.ToString() }),
                };

                // ONE run-id for the whole decision, carried across preview AND apply.
                var runId = BatchRemediationDriver.NewRunId();

                // ── STEP 1: PRICE + COMBINED PREVIEW. Nothing reserved, nothing written. ──
                int priced = w.Driver.PriceBatch(items);
                int availBefore = w.Credits.AvailableFor(target);
                var breakdownBefore = w.Credits.GetBreakdown(target);
                _out.WriteLine($"PRICE: batch priced at {priced} credits. Ledger before: "
                             + $"alloc={breakdownBefore.Allocation} committed={breakdownBefore.Committed} "
                             + $"outstanding={breakdownBefore.Outstanding} available={breakdownBefore.Available}");
                Assert.Equal(3, priced); // 3 x 1 credit (Standard/Trivial + Reversible)

                var preview = await w.Driver.PreviewBatchDetailedAsync(target, items, runId);
                foreach (var row in preview.Items)
                    _out.WriteLine($"PREVIEW {row.TemplateKey}: refused={row.Proposal.IsRefused} "
                                 + $"price={row.Price} noChange={row.IsNoChange?.ToString() ?? "unknown"} "
                                 + $":: {row.Proposal.Preview?.WhatIfText ?? row.Proposal.Message}");
                Assert.All(preview.Items, x => Assert.False(x.Proposal.IsRefused));

                // The preview READ the server and can therefore say which item will do nothing.
                // This is the §3.9 defect closed: the operator sees 3 exposed / 2 changing.
                Assert.True(preview.Items.Single(i => i.TemplateKey == KeyAdHoc).IsNoChange,
                    "the pre-set ad-hoc item must be flagged as a no-change item by the live preview");
                Assert.Equal(3, preview.PricedTotal);
                Assert.Equal(2, preview.ChangingPrice);
                _out.WriteLine($"PREVIEW TOTALS: exposure={preview.PricedTotal} changing={preview.ChangingPrice} "
                             + $"noChangeItems={preview.NoChangeCount}");

                // The preview must not have moved the server.
                Assert.Equal(baseMaxDop, ReadConfig(target, CfgMaxDop));
                Assert.Equal(baseCtfp, ReadConfig(target, CfgCtfp));
                _out.WriteLine("PREVIEW left the server unchanged (independent re-read).");

                // ── STEP 2: an UNAPPROVED batch is refused whole, touching nothing. ─────
                var unapproved = await w.Driver.ApplyBatchAsync(target, items, approved: false, "adrian");
                Assert.True(unapproved.IsRefused);
                Assert.Equal(BatchRefusal.NotApproved, unapproved.Refusal);
                Assert.Equal(availBefore, w.Credits.AvailableFor(target));
                _out.WriteLine($"UNAPPROVED batch refused: {unapproved.Message} (credits still {availBefore})");

                // ── STEP 3: ONE approval -> three sequential gated applies. ─────────────
                var batch = await w.Driver.ApplyBatchAsync(target, items, approved: true, "adrian-p1",
                    BatchStopPolicy.StopOnFirstFailure, runId);

                Assert.False(batch.IsRefused);
                Assert.Equal(runId, batch.RunId);
                foreach (var it in batch.Items)
                    _out.WriteLine($"ITEM {it.TemplateKey}: attempted={it.Attempted} refused={it.Result.IsRefused} "
                                 + $"outcome={it.Result.Outcome} rollback={it.Result.RollbackState} "
                                 + $"reserved={it.Result.CreditsCharged} committed={it.Result.CreditsCommitted} "
                                 + $"pre={it.Result.PreChangeValue} msg={it.Result.Message}");

                var byKey = batch.Items.ToDictionary(i => i.TemplateKey, StringComparer.OrdinalIgnoreCase);
                Assert.Equal(RemediationOutcome.AppliedVerified, byKey[KeyMaxDop].Result.Outcome);
                Assert.Equal(RemediationOutcome.NoOp, byKey[KeyAdHoc].Result.Outcome);
                Assert.Equal(RemediationOutcome.AppliedVerified, byKey[KeyCtfp].Result.Outcome);

                // ── INDEPENDENT proof the two real writes landed and the no-op did not move ──
                Assert.Equal(wantMaxDop, ReadConfig(target, CfgMaxDop));
                Assert.Equal(wantCtfp, ReadConfig(target, CfgCtfp));
                Assert.Equal(wantAdHoc, ReadConfig(target, CfgAdHoc));
                _out.WriteLine($"INDEPENDENT re-read after batch: {CfgMaxDop}={ReadConfig(target, CfgMaxDop)} "
                             + $"{CfgCtfp}={ReadConfig(target, CfgCtfp)} {CfgAdHoc}={ReadConfig(target, CfgAdHoc)}");

                // ── STEP 4: ITEM 1.2 — the credit split, against the REAL ledger. ───────
                int availAfter = w.Credits.AvailableFor(target);
                var breakdownAfter = w.Credits.GetBreakdown(target);
                _out.WriteLine($"CREDITS: reserved={batch.CreditsReserved} committed={batch.CreditsCommitted} "
                             + $"refunded={batch.CreditsRefunded} | ledger available {availBefore} -> {availAfter} | "
                             + $"breakdown after: alloc={breakdownAfter.Allocation} committed={breakdownAfter.Committed} "
                             + $"outstanding={breakdownAfter.Outstanding} available={breakdownAfter.Available}");

                Assert.Equal(3, batch.CreditsReserved);
                Assert.Equal(2, batch.CreditsCommitted);
                Assert.Equal(1, batch.CreditsRefunded);
                // The REAL ledger agrees with COMMITTED and NOT with RESERVED. Both directions are
                // asserted so a regression in either field is caught, not just an inequality.
                Assert.Equal(availBefore - batch.CreditsCommitted, availAfter);
                Assert.NotEqual(availBefore - availAfter, batch.CreditsReserved);
                Assert.Equal(batch.CreditsCommitted, breakdownAfter.Committed);
                Assert.Equal(0, breakdownAfter.Outstanding); // no reservation left dangling

                // Per item: the refunded no-op still reports its reservation and commits nothing.
                Assert.Equal(1, byKey[KeyAdHoc].Result.CreditsCharged);
                Assert.Equal(0, byKey[KeyAdHoc].Result.CreditsCommitted);
                Assert.Equal(1, byKey[KeyMaxDop].Result.CreditsCommitted);

                // ── STEP 5: ITEM 1.5 — the chain verifies WITH RunId, and RunId joins it. ──
                w.Audit.Flush();
                var verification = w.Audit.VerifyChain("p1-batch-live");
                _out.WriteLine($"VERIFYCHAIN: intact={verification.Intact} status={verification.Status} "
                             + $"entries={verification.EntryCount} broken={verification.BrokenCount} "
                             + $"unverifiable={verification.UnverifiableCount} linkBreaks={verification.LinkBreakCount} "
                             + $"duplicates={verification.DuplicateEntryCount} restarts={verification.RestartCount}");
                Assert.True(verification.Intact);
                Assert.Equal(AuditLogService.ChainVerificationStatus.Intact, verification.Status);
                Assert.Equal(0, verification.BrokenCount);
                Assert.Equal(0, verification.LinkBreakCount);

                var entries = ReadAuditEntries(w.AuditDir);
                var remediationEntries = entries
                    .Where(e => e.EventType is AuditEventType.RemediationProposed
                                            or AuditEventType.RemediationApproved
                                            or AuditEventType.RemediationApplied
                                            or AuditEventType.RemediationRolledBack)
                    .ToList();
                _out.WriteLine("AUDIT remediation entries, in chain order:");
                foreach (var e in remediationEntries)
                {
                    var det = string.Join("; ", e.Details.Select(kv => $"{kv.Key}={kv.Value}"));
                    _out.WriteLine($"  {e.Timestamp:O} {e.EventType} :: {det}");
                }

                int proposedEntries = remediationEntries.Count(e => e.EventType == AuditEventType.RemediationProposed);
                int approvedEntries = remediationEntries.Count(e => e.EventType == AuditEventType.RemediationApproved);
                int appliedEntries = remediationEntries.Count(e => e.EventType == AuditEventType.RemediationApplied);
                _out.WriteLine($"AUDIT SHAPE: one operator decision -> {proposedEntries} Proposed, "
                             + $"{approvedEntries} Approved, {appliedEntries} Applied.");
                Assert.Equal(3, proposedEntries);
                Assert.Equal(3, approvedEntries);
                Assert.Equal(3, appliedEntries);

                // THE JOIN, asserted positively. Before this field an auditor asked "show me
                // everything that one approval authorised" and the ledger could not answer.
                Assert.All(remediationEntries, e =>
                {
                    Assert.True(e.Details.ContainsKey(AuditLogService.CorrelationDetailKey),
                        $"{e.EventType} carries no {AuditLogService.CorrelationDetailKey}");
                    Assert.Equal(runId, e.Details[AuditLogService.CorrelationDetailKey]);
                });
                _out.WriteLine($"PROVED: all {remediationEntries.Count} entries of one decision share "
                             + $"{AuditLogService.CorrelationDetailKey}={runId}, and the chain is Intact with it present.");
            }
            finally
            {
                // ── RESTORE + PROVE RESTORED ───────────────────────────────────────────
                if (ReadConfig(target, CfgMaxDop) != baseMaxDop) SetConfig(target, CfgMaxDop, baseMaxDop);
                if (ReadConfig(target, CfgCtfp) != baseCtfp) SetConfig(target, CfgCtfp, baseCtfp);
                if (ReadConfig(target, CfgAdHoc) != baseAdHoc) SetConfig(target, CfgAdHoc, baseAdHoc);
                _out.WriteLine($"RESTORED (re-read): {CfgMaxDop}={ReadConfig(target, CfgMaxDop)} (baseline {baseMaxDop}) | "
                             + $"{CfgCtfp}={ReadConfig(target, CfgCtfp)} (baseline {baseCtfp}) | "
                             + $"{CfgAdHoc}={ReadConfig(target, CfgAdHoc)} (baseline {baseAdHoc})");
                Assert.Equal(baseMaxDop, ReadConfig(target, CfgMaxDop));
                Assert.Equal(baseCtfp, ReadConfig(target, CfgCtfp));
                Assert.Equal(baseAdHoc, ReadConfig(target, CfgAdHoc));

                try { Directory.Delete(w.ScratchRoot, recursive: true); } catch { /* cleanup */ }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════
        //  The aggregate pre-check: a batch that cannot be paid for in full never starts.
        // ══════════════════════════════════════════════════════════════════════════════

        [LiveFact(TargetVariable)]
        public async Task Batch_ThatOutpricesTheServerBalance_IsRefusedWhole_AndNothingOnTheServerMoves()
        {
            var target = RequireTarget();

            int baseMaxDop = ReadConfig(target, CfgMaxDop);
            int baseCtfp = ReadConfig(target, CfgCtfp);
            _out.WriteLine($"BASELINE: {CfgMaxDop}={baseMaxDop} {CfgCtfp}={baseCtfp}");

            // 2 credits allocated, a 3-credit batch requested.
            var w = Wire(target, creditsPerServer: 2);
            try
            {
                var items = new List<BatchRemediationItem>
                {
                    new(KeyMaxDop, new Dictionary<string, string> { ["MaxDop"] = (baseMaxDop == 2 ? 4 : 2).ToString() }),
                    new(KeyCtfp,   new Dictionary<string, string> { ["CostThreshold"] = (baseCtfp == 50 ? 45 : 50).ToString() }),
                    new(KeyAdHoc,  new Dictionary<string, string> { ["Enabled"] = "1" }),
                };

                var batch = await w.Driver.ApplyBatchAsync(target, items, approved: true, "adrian-p1");
                _out.WriteLine($"OUTPRICED batch: refused={batch.IsRefused} reason={batch.Refusal} "
                             + $"priced={batch.PricedTotal} available={batch.AvailableAtPreCheck} :: {batch.Message}");

                Assert.True(batch.IsRefused);
                Assert.Equal(BatchRefusal.InsufficientCreditsForBatch, batch.Refusal);
                Assert.Equal(3, batch.PricedTotal);
                Assert.Equal(2, batch.AvailableAtPreCheck);
                Assert.Empty(batch.Items);

                // Nothing ran: the server is untouched and the ledger is untouched.
                Assert.Equal(baseMaxDop, ReadConfig(target, CfgMaxDop));
                Assert.Equal(baseCtfp, ReadConfig(target, CfgCtfp));
                Assert.Equal(2, w.Credits.AvailableFor(target));
                Assert.Equal(0, w.Credits.GetBreakdown(target).Committed);
                _out.WriteLine("Server and ledger both unmoved by the refused batch (independent re-read).");

                // Contrast, proved rather than asserted: WITHOUT the pre-check the same 3 items
                // half-apply. Drive the runner directly for all three and watch the third refuse
                // for money after two have already landed on a live server.
                _out.WriteLine("CONTRAST ARM (no pre-check): applying the same 3 items item-by-item.");
                var r1 = await w.Runner.ApplyAsync(items[0].TemplateKey, target, true, "contrast", items[0].Parameters);
                var r2 = await w.Runner.ApplyAsync(items[1].TemplateKey, target, true, "contrast", items[1].Parameters);
                var r3 = await w.Runner.ApplyAsync(items[2].TemplateKey, target, true, "contrast", items[2].Parameters);
                _out.WriteLine($"  1 {items[0].TemplateKey}: refused={r1.IsRefused} outcome={r1.Outcome}");
                _out.WriteLine($"  2 {items[1].TemplateKey}: refused={r2.IsRefused} outcome={r2.Outcome}");
                _out.WriteLine($"  3 {items[2].TemplateKey}: refused={r3.IsRefused} refusal={r3.Refusal} :: {r3.Message}");
                Assert.False(r1.IsRefused);
                Assert.False(r2.IsRefused);
                Assert.True(r3.IsRefused);
                Assert.Equal(RemediationRefusal.InsufficientCredits, r3.Refusal);
                _out.WriteLine("PROVED: without the aggregate pre-check the batch half-applies and refuses at item 3.");
            }
            finally
            {
                if (ReadConfig(target, CfgMaxDop) != baseMaxDop) SetConfig(target, CfgMaxDop, baseMaxDop);
                if (ReadConfig(target, CfgCtfp) != baseCtfp) SetConfig(target, CfgCtfp, baseCtfp);
                _out.WriteLine($"RESTORED (re-read): {CfgMaxDop}={ReadConfig(target, CfgMaxDop)} (baseline {baseMaxDop}) | "
                             + $"{CfgCtfp}={ReadConfig(target, CfgCtfp)} (baseline {baseCtfp})");
                Assert.Equal(baseMaxDop, ReadConfig(target, CfgMaxDop));
                Assert.Equal(baseCtfp, ReadConfig(target, CfgCtfp));
                try { Directory.Delete(w.ScratchRoot, recursive: true); } catch { /* cleanup */ }
            }
        }
    }
}
