/* In the name of God, the Merciful, the Compassionate */
/*
 * BatchCandidateSelectorTests — the selection half of the preview-only batch surface
 * (plan item 1.1, Phase 1 build lane, 2026-09-01).
 *
 * WHAT THESE PROVE AND WHAT THEY DO NOT. Every test here is OFFLINE and exercises
 * BatchCandidateSelector against the REAL shipped RemediationTemplateStore and the REAL
 * CheckResolutionLookup — the same objects Pages/Remediation.razor injects. They prove which
 * findings the page will OFFER to batch. They prove nothing about what a preview renders, what a
 * server holds, or what anything costs: the driver's preview path is covered by
 * BatchRemediationDriverTests (offline) and BatchRemediationDriverLiveSmokeTests (live, not run by
 * this lane).
 *
 * Check ids used below are the SHIPPED links, read out of RemediationTemplateStore at this SHA —
 * not invented for the test. If a link is renamed or removed, these go red, which is the point:
 * the corpus half of the linkage cannot be pinned from here, so the app half is pinned hard.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    public class BatchCandidateSelectorTests
    {
        private const string ServerA = "SQLPROD01\\INST";
        private const string ServerB = "SQLPROD02";

        // Three of the nine links this lane wired: all three resolve to the ONE built-in MAXDOP.
        private const string Maxdop1 = "SQLT-BPCHK-00220-PARALLELISM-MAXDOP";
        private const string Maxdop2 = "SQLT-CUSTOM-MAXDOP";
        private const string Maxdop3 = "SQLT-FRONTIER-MAXDOP-CXPACKET";

        // A pre-existing link on a TRANSACTABLE template — a real one-click fix that Phase 1
        // deliberately does not batch.
        private const string AlertPackCheck = "SQLT-BLITZ-NO-OPERATORS";

        // A corpus-ruled maintenance check: resolves to a review-only script generator, never a
        // one-click state flip.
        private const string MaintenanceCheck = "SQLT-CUSTOM-INDEX-FRAGMENTATION";

        private static CheckResolutionLookup NewLookup() =>
            new(new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance));

        /// <summary>An OPEN finding: scorable, failed, unaccepted, no execution error.</summary>
        private static CheckResult Finding(string checkId, string server = ServerA) => new()
        {
            CheckId = checkId,
            CheckName = checkId + " name",
            Category = "Configuration",
            Severity = "High",
            Passed = false,
            Message = "The setting is not at the recommended value.",
            InstanceName = server,
        };

        private static BatchCandidateSet Select(params CheckResult[] results) =>
            BatchCandidateSelector.Select(ServerA, results, NewLookup());

        // ── The join itself ──────────────────────────────────────────────────────

        [Fact]
        public void ThreeFindingsOnOneSetting_ProduceOneCandidate_CarryingAllThree()
        {
            // The shape the nine dedupe links MAKE ordinary. Without the dedupe the batch would
            // list MAXDOP three times, price it three times and apply it three times.
            var set = Select(Finding(Maxdop1), Finding(Maxdop2), Finding(Maxdop3));

            var candidate = Assert.Single(set.Candidates);
            Assert.Equal("MAXDOP", candidate.TemplateKey);
            Assert.Equal(3, candidate.Findings.Count);
            Assert.Equal(
                new[] { Maxdop1, Maxdop3, Maxdop2 }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                candidate.Findings.Select(f => f.CheckId).ToArray());
            Assert.Equal(3, set.FindingsOnThisServer);
            Assert.Equal(0, set.UnresolvedCount);
        }

        [Fact]
        public void ACandidateForAConfigurationFix_IsBatchableThisPhase()
        {
            var set = Select(Finding(Maxdop1));
            Assert.Single(set.Batchable);
            Assert.Empty(set.NotBatchableThisPhase);
        }

        [Fact]
        public void ATransactableFix_ResolvesTheFinding_ButIsNotBatchableThisPhase()
        {
            // AGENTALERTPACK is a real, shipped one-click fix for this finding. It is Transactable
            // with its own parameter shape, so Phase 1 does not batch it — and the surface must
            // still NAME it rather than reporting "no fix", which is why it stays in Candidates.
            var set = Select(Finding(AlertPackCheck));

            var candidate = Assert.Single(set.Candidates);
            Assert.Equal("AGENTALERTPACK", candidate.TemplateKey);
            Assert.False(candidate.IsBatchablePhase1);
            Assert.Empty(set.Batchable);
            Assert.Single(set.NotBatchableThisPhase);
            Assert.Equal(0, set.UnresolvedCount); // NOT counted as "no fix registered"
        }

        [Fact]
        public void AMaintenanceCheck_IsNeverACandidate_AndIsNotCountedUnresolved()
        {
            // Corpus ruling: maintenance work, not a state flip. It resolves — on another page.
            var set = Select(Finding(MaintenanceCheck));

            Assert.Empty(set.Candidates);
            var m = Assert.Single(set.MaintenanceOnly);
            Assert.Equal(MaintenanceCheck, m.CheckId);
            Assert.Equal(0, set.UnresolvedCount);
        }

        [Fact]
        public void AFindingWithNoRegisteredFix_IsCounted_NotSilentlyDropped()
        {
            var set = Select(Finding("SQLT-ZZ-NO-SUCH-CHECK-ID"));

            Assert.Empty(set.Candidates);
            Assert.Empty(set.MaintenanceOnly);
            Assert.Equal(1, set.UnresolvedCount);
            Assert.Equal(1, set.FindingsOnThisServer);
        }

        // ── The server filter — the worst thing this surface could get wrong ─────

        [Fact]
        public void AFindingMeasuredOnAnotherServer_IsExcluded_AndCounted()
        {
            var set = Select(Finding(Maxdop1, ServerA), Finding(Maxdop2, ServerB));

            // Negative control on the filter itself: BOTH findings resolve to MAXDOP, so a
            // selector that ignored InstanceName would report the same single candidate. The
            // numbers below are what distinguishes the two implementations — the candidate's
            // finding count, and OtherServerCount.
            var candidate = Assert.Single(set.Candidates);
            Assert.Single(candidate.Findings);
            Assert.Equal(Maxdop1, candidate.Findings[0].CheckId);
            Assert.Equal(1, set.OtherServerCount);
            Assert.Equal(1, set.FindingsOnThisServer);
        }

        [Fact]
        public void TheServerMatch_IsCaseInsensitive()
        {
            // Both strings come from the same ServerConnection.GetServerList() entry, and SQL
            // Server instance names are case-insensitive, so a case difference is a display
            // artefact — never a different machine.
            var set = BatchCandidateSelector.Select(
                ServerA.ToLowerInvariant(), new[] { Finding(Maxdop1, ServerA.ToUpperInvariant()) }, NewLookup());

            Assert.Single(set.Candidates);
            Assert.Equal(0, set.OtherServerCount);
        }

        [Fact]
        public void NoSelectedServer_YieldsNothing_RatherThanGuessingOne()
        {
            foreach (var server in new string?[] { null, "", "   " })
            {
                var set = BatchCandidateSelector.Select(server, new[] { Finding(Maxdop1) }, NewLookup());
                Assert.Empty(set.Candidates);
                Assert.Equal(0, set.FindingsOnThisServer);
                Assert.Equal(0, set.OtherServerCount);
            }
        }

        [Fact]
        public void NullInputs_YieldAnEmptySet_NotAnException()
        {
            Assert.Empty(BatchCandidateSelector.Select(ServerA, null, NewLookup()).Candidates);
            Assert.Empty(BatchCandidateSelector.Select(ServerA, new[] { Finding(Maxdop1) }, null).Candidates);
        }

        // ── What counts as a finding ─────────────────────────────────────────────

        [Fact]
        public void APassedCheck_IsNotAFinding()
        {
            var r = Finding(Maxdop1);
            r.Passed = true;
            var set = Select(r);
            Assert.Empty(set.Candidates);
            Assert.Equal(0, set.FindingsOnThisServer);
        }

        [Fact]
        public void AClientAcceptedFinding_IsNotOffered()
        {
            // Accepted-by-design rides the Passed tier for scoring; offering to "fix" it would
            // undo a decision the client already made and recorded.
            var r = Finding(Maxdop1);
            r.IsAccepted = true;
            Assert.Empty(Select(r).Candidates);
        }

        [Theory]
        [InlineData("SKIP")]
        [InlineData("INFO")]
        [InlineData("WARN")]
        public void ASkipInfoOrWarnVerdict_IsNotAFinding(string verdict)
        {
            // A WARN means the check could not fully assess the target. It is not a pass and it is
            // certainly not a licence to change a production setting.
            var r = Finding(Maxdop1);
            r.Verdict = verdict;
            Assert.Empty(Select(r).Candidates);
        }

        [Fact]
        public void ACheckThatErrored_IsNotAFinding()
        {
            var r = Finding(Maxdop1);
            r.ErrorMessage = "Login failed for user.";
            Assert.Empty(Select(r).Candidates);
            Assert.False(BatchCandidateSelector.IsOpenFinding(r));
        }

        [Fact]
        public void AMessagePrefixSkip_IsNotAFinding()
        {
            var r = Finding(Maxdop1);
            r.Message = "SKIP - not applicable on this edition.";
            Assert.Empty(Select(r).Candidates);
        }

        [Fact]
        public void TheSameCheckIdTwice_CountsOnce()
        {
            // Imported and rehydrated runs feed the same list; a duplicate would inflate both the
            // "clears N findings" caption and the denominator printed beside it.
            var set = Select(Finding(Maxdop1), Finding(Maxdop1));

            var candidate = Assert.Single(set.Candidates);
            Assert.Single(candidate.Findings);
            Assert.Equal(1, set.FindingsOnThisServer);
        }

        // ── The scope predicate, on its own ──────────────────────────────────────

        [Fact]
        public void IsBatchablePhase1_RequiresAConfigurationTemplateWithAnOperation()
        {
            Assert.False(BatchCandidateSelector.IsBatchablePhase1(null));

            var configNoOp = new RemediationTemplate
            {
                Key = "ZZTEST-CONFIG-NO-OP",
                Kind = RemediationKind.Configuration,
                Operation = null,
            };
            Assert.False(BatchCandidateSelector.IsBatchablePhase1(configNoOp));

            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var maxdop = store.TryGet("MAXDOP");
            Assert.NotNull(maxdop);
            Assert.True(BatchCandidateSelector.IsBatchablePhase1(maxdop));

            var alertPack = store.TryGet("AGENTALERTPACK");
            Assert.NotNull(alertPack);
            Assert.NotNull(alertPack!.Operation);              // it HAS an operation
            Assert.NotEqual(RemediationKind.Configuration, alertPack.Kind); // the Kind is what excludes it
            Assert.False(BatchCandidateSelector.IsBatchablePhase1(alertPack));
        }

        // ── Ordering ─────────────────────────────────────────────────────────────

        [Fact]
        public void CandidateOrder_IsDeterministic_ByDisplayName()
        {
            // Two runs of the same input must render the same list in the same order, or a
            // screenshot, a support ticket and the page disagree with each other.
            var input = new[] { Finding(AlertPackCheck), Finding(Maxdop1), Finding(MaintenanceCheck) };

            var first = BatchCandidateSelector.Select(ServerA, input, NewLookup())
                .Candidates.Select(c => c.TemplateKey).ToArray();
            var second = BatchCandidateSelector.Select(ServerA, input.Reverse().ToArray(), NewLookup())
                .Candidates.Select(c => c.TemplateKey).ToArray();

            Assert.Equal(first, second);
            Assert.Equal(2, first.Length);
        }

        // ── The whole reading, end to end ────────────────────────────────────────

        [Fact]
        public void AMixedRun_SortsIntoEveryBucket_AndTheBucketsAccountForEveryFinding()
        {
            var input = new List<CheckResult>
            {
                Finding(Maxdop1), Finding(Maxdop2),          // -> one batchable candidate
                Finding(AlertPackCheck),                     // -> one candidate, not batchable
                Finding(MaintenanceCheck),                   // -> maintenance-only
                Finding("SQLT-ZZ-NO-SUCH-CHECK-ID"),         // -> unresolved
                Finding(Maxdop3, ServerB),                   // -> other server
            };
            var set = BatchCandidateSelector.Select(ServerA, input, NewLookup());

            Assert.Equal(5, set.FindingsOnThisServer);
            Assert.Equal(1, set.OtherServerCount);
            Assert.Single(set.Batchable);
            Assert.Single(set.NotBatchableThisPhase);
            Assert.Single(set.MaintenanceOnly);
            Assert.Equal(1, set.UnresolvedCount);

            // Every finding on this server lands in exactly one bucket. This is the assertion that
            // makes the denominator on the page true: 5 findings = 2 + 1 + 1 + 1.
            var accountedFor =
                set.Candidates.Sum(c => c.Findings.Count) + set.MaintenanceOnly.Count + set.UnresolvedCount;
            Assert.Equal(set.FindingsOnThisServer, accountedFor);
        }

        // ── Both counts over the SAME set (gate fix, 2026-09-01) ─────────────────

        [Fact]
        public void WithDuplicateCheckIds_TheTwoServerCountsAreTakenOverTheSameDedupedSet()
        {
            // THE DEFECT THIS PINS. OtherServerCount used to be `open.Count - onThisServer.Count` —
            // the RAW list minus the deduped one — while FindingsOnThisServer counted the deduped
            // set. The page prints both in ONE sentence, so any duplicate anywhere made that
            // sentence stop adding up.
            //
            // Six raw findings: three on A carrying one duplicate, three on B that are all the same
            // id. Deduped that is 2 on A and 1 on B.
            var input = new List<CheckResult>
            {
                Finding(Maxdop1, ServerA), Finding(Maxdop1, ServerA), Finding(Maxdop2, ServerA),
                Finding(Maxdop3, ServerB), Finding(Maxdop3, ServerB), Finding(Maxdop3, ServerB),
            };
            var set = BatchCandidateSelector.Select(ServerA, input, NewLookup());

            Assert.Equal(2, set.FindingsOnThisServer);

            // The number that used to be wrong. The old formula returned 6 - 3 = 3 here: it counted
            // A's own duplicate as a finding "measured on another server".
            Assert.Equal(1, set.OtherServerCount);

            // The property the page's sentence depends on, stated directly.
            Assert.Equal(3, set.FindingsOnThisServer + set.OtherServerCount);

            // And the buckets still account for every finding on this server.
            var accountedFor =
                set.Candidates.Sum(c => c.Findings.Count) + set.MaintenanceOnly.Count + set.UnresolvedCount;
            Assert.Equal(set.FindingsOnThisServer, accountedFor);
        }

        [Fact]
        public void TheSameCheckIdOnTwoServers_IsNotCollapsed_BecauseTheyAreTwoFindings()
        {
            // The negative control on the dedupe key: it is (server, check id), not check id. One
            // server-wide dedupe would report zero findings elsewhere and drop the exclusion notice
            // the page prints.
            var set = BatchCandidateSelector.Select(
                ServerA, new[] { Finding(Maxdop1, ServerA), Finding(Maxdop1, ServerB) }, NewLookup());

            Assert.Equal(1, set.FindingsOnThisServer);
            Assert.Equal(1, set.OtherServerCount);
        }
    }
}
