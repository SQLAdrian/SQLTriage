/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationOperatorProofingTests — Phase-2 item 6, parts (b), (c) and (d).
 *
 * Adrian drives this product himself. The instruction that opened this half of the lane, mid-lane
 * 2026-09-01: "make it idiot proof - I will be driving it." That is not a request for more warnings.
 * It is a request that the app refuse the things a tired person does at 2am, and that it say what it
 * refused and why, in words, before the decision rather than after it.
 *
 *   (b) REVERSIBILITY BEFORE APPROVAL. Reversibility was a post-apply enum field. The one moment an
 *       operator needs to know whether a fix can be undone is the moment BEFORE they approve it, and
 *       that was the one moment the app said nothing. The preview now carries it, as a sentence in
 *       the text every surface already renders and as a structured field a surface can act on.
 *   (c) SENSITIVE OPS OUT OF BATCHES. The standing ruling has always been one-at-a-time for
 *       Sensitive fixes. Nothing enforced it: the driver's own header said so in prose and a caller
 *       could batch one today. Prose is not a guard.
 *   (d) is in RemediationRollbackLevelingTests, with the rest of the rollback prose.
 *
 * NO SERVER IS TOUCHED. The preview arm runs the REAL executor against an UNREGISTERED server, so
 * every read fails to null before a connection is opened - which is also the exact case where the
 * honest answer to "can this be undone" is no.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    public class RemediationOperatorProofingTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _scratch;

        public RemediationOperatorProofingTests(ITestOutputHelper output)
        {
            _out = output;
            _scratch = Path.Combine(Path.GetTempPath(), "op-proofing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
        }

        public void Dispose()
        {
            try { Directory.Delete(_scratch, recursive: true); } catch { /* test cleanup */ }
        }

        private const string Server = "OPPROOF-NO-SUCH-SERVER";

        private DbatoolsRemediationExecutor RealExecutorWithNoServer()
        {
            var connections = new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance);
            return new DbatoolsRemediationExecutor(
                new PowerShellService(NullLogger<PowerShellService>.Instance),
                connections,
                new AuditLogService(Path.Combine(_scratch, "audit"), startFlushTimer: false),
                new DiskIoService(NullLogger<DiskIoService>.Instance),
                NullLogger<DbatoolsRemediationExecutor>.Instance);
        }

        // ── (b) REVERSIBILITY, IN THE PREVIEW, BEFORE APPROVAL ──

        [Fact]
        public async Task TheConfigurationPreview_CarriesReversibility_AsProseANDAsAField()
        {
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var t = store.TryGet("MAXDOP")!;
            var request = new RemediationRequest(t, Server, new Dictionary<string, string> { ["MaxDop"] = "4" });

            var preview = await RealExecutorWithNoServer().PreviewAsync(request, CancellationToken.None);

            _out.WriteLine(preview.WhatIfText);
            Assert.True(preview.Succeeded);

            // With no server registered the configured value could not be read, so the honest
            // answer is NO. This is the case the old code was silent about: it rendered the T-SQL
            // and said nothing at all about whether it could be undone.
            Assert.False(preview.CanRollBack);
            Assert.StartsWith(RemediationRollbackProse.NoRollbackMarker, preview.ReversibilityNote);

            // And the operator sees it: the sentence is in the text BOTH surfaces render, so
            // neither page has to restate it and neither can forget to.
            Assert.Contains(preview.ReversibilityNote, preview.WhatIfText);
            Assert.Contains(RemediationRollbackProse.NoRollbackMarker, preview.WhatIfText);
        }

        [Fact]
        public async Task TheReversibilityLineDoesNotBreakTheNoChangeDETECTOR()
        {
            // ⚠ THE REGRESSION THIS FILE EXISTS TO CATCH. BatchRemediationDriver.DetectNoChange
            // parses the preview's own current-vs-target sentence out of free text. Appending lines
            // after that sentence must leave the parse working, or the batch surface silently stops
            // detecting no-change items and prices them as changes (spike S1 section 3.9).
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var t = store.TryGet("MAXDOP")!;
            var request = new RemediationRequest(t, Server, new Dictionary<string, string> { ["MaxDop"] = "4" });
            var preview = await RealExecutorWithNoServer().PreviewAsync(request, CancellationToken.None);

            // The sentence is still parseable, and an unread current still answers "unknown" (null)
            // rather than "no change" - the third answer this lane must not lose.
            var m = RemediationPreviewSentence.CurrentVersusTargetPattern.Match(preview.WhatIfText);
            Assert.True(m.Success, "the reversibility line broke the current-vs-target parse");
            Assert.Equal("4", m.Groups["target"].Value);
            Assert.Equal(RemediationPreviewSentence.UnreadCurrent, m.Groups["current"].Value.Trim());

            Assert.Null(BatchRemediationDriver.DetectNoChange(RemediationProposal.Previewed(preview)));
        }

        [Fact]
        public async Task ABadValueIsRefusedInThePREVIEW_NotDiscoveredAtApplyTime()
        {
            // Operator-proofing means the refusal arrives before the approval, not after it.
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var t = store.TryGet("MAXDOP")!;
            var request = new RemediationRequest(t, Server, new Dictionary<string, string> { ["MaxDop"] = "999" });

            var preview = await RealExecutorWithNoServer().PreviewAsync(request, CancellationToken.None);

            Assert.False(preview.Succeeded);
            // ⚠ THE SENTENCE CHANGED IN THE FIX ROUND (gate blocker 1) and the change is the point.
            // It used to read "Nothing was sent to the server" on EVERY refusal, including the one
            // path where a read-only host probe had already run. There are now two sentences, each
            // true only of its own path, and this is the no-SQL one: MAXDOP has no host-relative
            // bound, so nothing can have run.
            Assert.Contains(RemediationValueBounds.RefusedBeforeAnySqlSentence, preview.Error);
            Assert.DoesNotContain(RemediationValueBounds.HostCheckRanSentence, preview.Error);
            Assert.Contains("Enter a whole number from 0 to 64", preview.Error);
        }

        // ── (c) SENSITIVE OPS ARE REFUSED FROM A BATCH, AT THE DRIVER ──

        private sealed class ExplodingExecutor : IRemediationExecutor
        {
            public Task<RemediationPreview> PreviewAsync(RemediationRequest r, CancellationToken ct = default) =>
                throw new InvalidOperationException("PreviewAsync must not be reached on a refused batch.");
            public Task<RemediationExecution> ExecuteAsync(RemediationRequest r, CancellationToken ct = default) =>
                throw new InvalidOperationException("ExecuteAsync must not be reached on a refused batch.");
            public bool CanWriteAudit() => true;
        }

        private (BatchRemediationDriver Driver, string AuditDir) WireDriver(int creditsPerServer, string tag)
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
                new BundleBackedRemediationCapability(bundle), credits, new ExplodingExecutor(), audit,
                NullLogger<RemediationRunner>.Instance);

            return (new BatchRemediationDriver(runner, templates, credits), auditDir);
        }

        [Fact]
        public async Task ASensitiveOpInABatch_RefusesTheWHOLEBatch_AndExecutesNothing()
        {
            var (driver, auditDir) = WireDriver(50, "sensitive");

            // ADDMISSINGINDEX ships RiskClass = Sensitive. Mixed with two ordinary config fixes,
            // which is exactly how it would arrive: an operator ticking everything on the page.
            var items = new List<BatchRemediationItem>
            {
                new("MAXDOP", new Dictionary<string, string> { ["MaxDop"] = "4" }),
                new("ADDMISSINGINDEX"),
                new("CTFP", new Dictionary<string, string> { ["CostThreshold"] = "50" }),
            };

            var result = await driver.ApplyBatchAsync(Server, items, approved: true, approvedBy: "adrian");

            Assert.True(result.IsRefused);
            Assert.Equal(BatchRefusal.SensitiveOpInBatch, result.Refusal);
            Assert.Contains("ADDMISSINGINDEX", result.Message);
            Assert.Contains("one at a time", result.Message);
            Assert.Contains("Nothing was applied", result.Message);

            // Nothing ran: the ExplodingExecutor would have thrown, and no item result exists.
            Assert.Empty(result.Items);

            // And nothing was ledgered - a refusal before the runner writes no approval entries.
            var wrote = Directory.Exists(auditDir) && Directory.GetFiles(auditDir, "audit-*.jsonl").Length > 0;
            _out.WriteLine($"audit files written by the refused batch: {wrote}");
            Assert.False(wrote);
        }

        [Fact]
        public async Task TheSensitiveGuardFiresBEFORETheCreditPreCheck()
        {
            // Order matters: a Sensitive item masked by "you cannot afford this batch" would send
            // the operator to buy credits for a batch that must never run at all.
            var (driver, _) = WireDriver(1, "sensitive-poor");   // 1 credit: the batch is unaffordable too

            var items = new List<BatchRemediationItem>
            {
                new("MAXDOP", new Dictionary<string, string> { ["MaxDop"] = "4" }),
                new("ADDMISSINGINDEX"),
            };

            var result = await driver.ApplyBatchAsync(Server, items, approved: true, approvedBy: "adrian");

            Assert.Equal(BatchRefusal.SensitiveOpInBatch, result.Refusal);
        }

        [Fact]
        public async Task AnOrdinaryBatchOfNonSensitiveFixesIsSTILLAllowedThrough()
        {
            // The negative control. A guard that refused every batch would pass both tests above.
            var (driver, _) = WireDriver(50, "sensitive-control");
            var items = new List<BatchRemediationItem>
            {
                new("MAXDOP", new Dictionary<string, string> { ["MaxDop"] = "4" }),
                new("CTFP", new Dictionary<string, string> { ["CostThreshold"] = "50" }),
            };

            // The ExplodingExecutor throws the moment an item reaches the executor, which is how
            // this test proves the batch got PAST the scope guard rather than being refused by it.
            var result = await driver.ApplyBatchAsync(Server, items, approved: true, approvedBy: "adrian");

            Assert.False(result.IsRefused);
            Assert.NotEqual(BatchRefusal.SensitiveOpInBatch, result.Refusal);
            Assert.True(result.Items[0].Attempted);
            Assert.Equal(RemediationOutcome.CouldNotRun, result.Items[0].Result.Outcome);
        }

        [Fact]
        public void TheBatchableRuleIsONEPredicate_SoASurfaceCannotDisagreeWithTheDriver()
        {
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);

            Assert.False(BatchRemediationDriver.IsBatchable(store.TryGet("ADDMISSINGINDEX")));
            Assert.False(BatchRemediationDriver.IsBatchable(store.TryGet("BACKUPDATABASENOW")));
            Assert.False(BatchRemediationDriver.IsBatchable(store.TryGet("CHECKDBNOW")));
            Assert.False(BatchRemediationDriver.IsBatchable(store.TryGet("DELETEEXTRAJOB")));
            Assert.False(BatchRemediationDriver.IsBatchable(null));

            Assert.True(BatchRemediationDriver.IsBatchable(store.TryGet("MAXDOP")));
            Assert.True(BatchRemediationDriver.IsBatchable(store.TryGet("CTFP")));

            // Every Sensitive template the store ships is unbatchable, measured rather than listed:
            // a new Sensitive template is covered the day it is added.
            foreach (var t in store.All().Where(t => t.RiskClass == RemediationRiskClass.Sensitive))
                Assert.False(BatchRemediationDriver.IsBatchable(t), t.Key);
        }
    }
}
