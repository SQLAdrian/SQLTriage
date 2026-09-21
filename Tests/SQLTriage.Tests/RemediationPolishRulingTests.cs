/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The polish round on the remediation-safety lane (DECISIONS 2026-08-25 17:33). One file for
    /// the rulings that changed behaviour, plus the two gate residuals that are pure functions.
    ///
    /// <list type="bullet">
    /// <item><b>Ruling 2</b> — refunds go CONFIRMED-ONLY. An inverse that ran and could not be
    /// confirmed keeps the charge.</item>
    /// <item><b>Ruling 3</b> — an acknowledged force-install over existing Maintenance Solution
    /// objects, with the consequence stated and the acknowledgement logged. Refusal stays default.</item>
    /// <item><b>Ruling 7</b> — an acknowledged off-recommended route, with the operator's stated
    /// intent logged. The absolute refusal stands without both halves.</item>
    /// <item><b>Ruling 8</b> — the DRAFT marker is gone from operator-facing error and banner copy.</item>
    /// <item><b>Ruling 10</b> — AgentJobSync / AgentJobGuard read the damage-aware ledger seam.</item>
    /// <item><b>R5</b> — the store-recovery register carries no em-dash.</item>
    /// <item><b>R6</b> — a per-database inverse that never rendered is not reported as one that ran.</item>
    /// </list>
    /// </summary>
    public sealed class RemediationPolishRulingTests : IDisposable
    {
        private readonly string _auditDir;

        public RemediationPolishRulingTests()
        {
            _auditDir = Path.Combine(Path.GetTempPath(), "remsafe-polish-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_auditDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_auditDir, recursive: true); } catch { /* test cleanup */ }
        }

        private sealed class GrantedCapability : IRemediationCapability { public bool IsGranted => true; }

        /// <summary>An executor whose terminal state and rollback state the test dictates.</summary>
        private sealed class ScriptedExecutor : IRemediationExecutor
        {
            private readonly RemediationOutcome _outcome;
            private readonly RemediationRollbackState _rollback;
            public IReadOnlyDictionary<string, string>? LastParameters;

            public ScriptedExecutor(RemediationOutcome outcome, RemediationRollbackState rollback)
            {
                _outcome = outcome; _rollback = rollback;
            }

            public Task<RemediationPreview> PreviewAsync(RemediationRequest r, CancellationToken ct = default) =>
                Task.FromResult(new RemediationPreview { Succeeded = true, WhatIfText = "would set MAXDOP" });

            public Task<RemediationExecution> ExecuteAsync(RemediationRequest r, CancellationToken ct = default)
            {
                LastParameters = r.Parameters;
                return Task.FromResult(new RemediationExecution
                {
                    Outcome = _outcome,
                    RollbackState = _rollback,
                    Error = "verify failed",
                    PreChangeValue = 4,
                });
            }

            public bool CanWriteAudit() => true;
        }

        private (RemediationRunner Runner, InMemoryRemediationCreditLedger Ledger, AuditLogService Audit)
            NewRunner(IRemediationExecutor exec, int credits = 5)
        {
            var ledger = new InMemoryRemediationCreditLedger(initialCreditsPerServer: credits);
            var audit = new AuditLogService(_auditDir, startFlushTimer: false);
            var runner = new RemediationRunner(
                new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance),
                new GrantedCapability(), ledger, exec, audit,
                NullLogger<RemediationRunner>.Instance);
            return (runner, ledger, audit);
        }

        private List<AuditLogEntry> ReadAuditEntries()
        {
            var file = Directory.GetFiles(_auditDir, "audit-*.jsonl")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).First();
            return File.ReadAllLines(file)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonSerializer.Deserialize<AuditLogEntry>(l)!)
                .ToList();
        }

        // ── Ruling 2: refunds are CONFIRMED-ONLY ─────────────────────────────────

        [Fact]
        public async Task AConfirmedRollback_Refunds()
        {
            var (runner, ledger, _) = NewRunner(
                new ScriptedExecutor(RemediationOutcome.AppliedVerifyFailed, RemediationRollbackState.Confirmed));

            var before = ledger.AvailableFor("srv1");
            var result = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian",
                new Dictionary<string, string> { ["MaxDop"] = "4" });

            Assert.Equal(RemediationOutcome.AppliedVerifyFailed, result.Outcome);
            Assert.Equal(RemediationRollbackState.Confirmed, result.RollbackState);
            Assert.Equal(before, ledger.AvailableFor("srv1"));
        }

        [Fact]
        public async Task AnUnconfirmedRollback_KeepsTheCharge()
        {
            // The ruling-2 change, and the arm that was GREEN the other way before it: a rollback
            // that ran without a confirming read used to refund. Nobody read the server, so a refund
            // asserted the change was gone.
            var (runner, ledger, _) = NewRunner(
                new ScriptedExecutor(RemediationOutcome.AppliedVerifyFailed, RemediationRollbackState.Unconfirmed));

            var before = ledger.AvailableFor("srv1");
            var result = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian",
                new Dictionary<string, string> { ["MaxDop"] = "4" });

            Assert.Equal(RemediationRollbackState.Unconfirmed, result.RollbackState);
            Assert.Equal(before - 1, ledger.AvailableFor("srv1"));
        }

        [Theory]
        [InlineData(RemediationRollbackState.Confirmed, false)]
        [InlineData(RemediationRollbackState.Unconfirmed, true)]
        [InlineData(RemediationRollbackState.Failed, true)]
        [InlineData(RemediationRollbackState.NotAvailable, true)]
        [InlineData(RemediationRollbackState.NotAttempted, true)]
        public void TheChargeRule_ChargesEveryStateExceptConfirmed(RemediationRollbackState state, bool stuck)
        {
            Assert.Equal(stuck,
                RemediationCreditOutcome.ChangeStuck(RemediationOutcome.AppliedVerifyFailed, state));
        }

        [Fact]
        public void TheOperatorSentence_ReadsFromTheSamePredicateTheRunnerCharges()
        {
            // The page's "credits were charged / refunded" line is not a second opinion.
            foreach (var state in Enum.GetValues<RemediationRollbackState>())
            {
                var stuck = RemediationCreditOutcome.ChangeStuck(RemediationOutcome.AppliedVerifyFailed, state);
                var sentence = RemediationCreditOutcome.DescribeCharge(RemediationOutcome.AppliedVerifyFailed, state, credits: 3);
                Assert.Equal(stuck, sentence.Contains("charged", StringComparison.Ordinal));
                Assert.Equal(!stuck, sentence.Contains("refunded", StringComparison.Ordinal));
            }
        }

        // ── VOICE-1: the credit line states the REAL count, with grammar to match ─────

        [Theory]
        [InlineData(3, "3 change credits were charged.")]   // BACKUPDATABASENOW / CHECKDBNOW price
        [InlineData(2, "2 change credits were charged.")]   // ADDMISSINGINDEX price
        [InlineData(1, "1 change credit was charged.")]     // an ordinary bounded fix
        public void AChargedApply_StatesTheRealCreditCount(int credits, string expected)
        {
            // Defect (DECISIONS 2026-08-25 23:01, VOICE-1): the line read "The change credit was
            // charged." for every op, while the lane prices applies at one to five credits. A verified
            // apply sticks, so it is charged. The count is the number the runner reserved, and the
            // noun and verb agree with it.
            var sentence = RemediationCreditOutcome.DescribeCharge(
                RemediationOutcome.AppliedVerified, RemediationRollbackState.NotAttempted, credits);
            Assert.Equal(expected, sentence);
        }

        [Theory]
        [InlineData(3, "3 change credits were refunded.")]
        [InlineData(1, "1 change credit was refunded.")]
        public void ARefundedApply_StatesTheRealCreditCount(int credits, string expected)
        {
            // A confirmed rollback refunds — the whole reservation, so the count is the same one that
            // was reserved. Singular for one, plural for more, both ways.
            var sentence = RemediationCreditOutcome.DescribeCharge(
                RemediationOutcome.AppliedVerifyFailed, RemediationRollbackState.Confirmed, credits);
            Assert.Equal(expected, sentence);
        }

        [Fact]
        public void ANoReservationOutcome_SaysNoChargeRatherThanAZeroRefund()
        {
            // VOICE-1 parked-path ruling (DECISIONS 2026-08-25): the parked-server path returns an
            // Applied CouldNotRun without ever reserving a credit, so its count is 0. Zero must read
            // as "nothing was charged", not "0 change credits were refunded." — a refund claim about
            // a refund that never happened. A real reservation is always at least Min, so 0 can only
            // mean no charge.
            var sentence = RemediationCreditOutcome.DescribeCharge(
                RemediationOutcome.CouldNotRun, RemediationRollbackState.NotAttempted, credits: 0);
            Assert.Equal("No change credit was charged.", sentence);
            Assert.DoesNotContain("refunded", sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("0 change", sentence, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TheCreditLineCount_IsTheAmountTheLedgerActuallyCharged()
        {
            // End to end: the RESULT the page renders from carries the count the runner reserved
            // (RemediationResult.CreditsCharged), so the sentence cannot drift from the ledger. The
            // count is READ from RemediationCreditCost.For(template) — the same source the runner
            // charges from — never hard-coded here.
            var template = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance).TryGet("MAXDOP");
            Assert.NotNull(template);
            var cost = RemediationCreditCost.For(template);

            var (runner, ledger, _) = NewRunner(
                new ScriptedExecutor(RemediationOutcome.AppliedVerified, RemediationRollbackState.NotAttempted));
            var before = ledger.AvailableFor("srv1");
            var applied = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian",
                new Dictionary<string, string> { ["MaxDop"] = "4" });

            Assert.Equal(cost, applied.CreditsCharged);
            Assert.Equal(before - cost, ledger.AvailableFor("srv1"));

            var sentence = RemediationCreditOutcome.DescribeCharge(
                applied.Outcome!.Value, applied.RollbackState, applied.CreditsCharged);
            Assert.Contains($"{cost} change {RemediationCreditCost.Noun(cost)}", sentence, StringComparison.Ordinal);
            Assert.Contains("charged", sentence, StringComparison.Ordinal);
        }

        [Fact]
        public void EveryRefundRuleStatementInTheCode_SaysConfirmed()
        {
            // Ruling 2 required the captions and the ledger prose to move with the rule. These three
            // files each state the refund rule in prose, and each used to say a rolled-back apply
            // refunds without qualification.
            var root = RawPassedScan.RepoRoot().FullName;
            foreach (var rel in new[]
                     {
                         Path.Combine("Data", "Services", "Remediation", "PersistedRemediationCreditLedger.cs"),
                         Path.Combine("Data", "Services", "Remediation", "RemediationContracts.cs"),
                         Path.Combine("Data", "Services", "Remediation", "RemediationCreditCost.cs"),
                     })
            {
                var text = File.ReadAllText(Path.Combine(root, rel));
                Assert.False(Regex.IsMatch(text, @"rolled-back apply\s+refunds", RegexOptions.IgnoreCase),
                    rel + " still states the old refund rule.");
                Assert.Contains("CONFIRMED", text, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ── Ruling 7: the acknowledged off-recommended route ─────────────────────

        [Fact]
        public void ATickWithNoStatedReason_IsNotAnAcknowledgement()
        {
            // The ruling's own words: the operator STATES INTENT. A tick on its own is a click.
            var tickOnly = new Dictionary<string, string>
            {
                [RemediationOpRenderer.AcknowledgeRegressionParam] = "true",
            };
            Assert.False(RemediationOpRenderer.IsRegressionAcknowledged(tickOnly));

            var blankReason = new Dictionary<string, string>(tickOnly)
            {
                [RemediationOpRenderer.RegressionIntentParam] = "   ",
            };
            Assert.False(RemediationOpRenderer.IsRegressionAcknowledged(blankReason));
        }

        [Theory]
        [InlineData("false")]
        [InlineData("")]
        [InlineData("yes")]
        [InlineData("1")]
        [InlineData("TRUE ")]   // a trailing space defeats bool.TryParse's strict form... it does not; see below
        public void AMalformedTick_FailsClosed_EvenWithAReason(string raw)
        {
            var p = new Dictionary<string, string>
            {
                [RemediationOpRenderer.AcknowledgeRegressionParam] = raw,
                [RemediationOpRenderer.RegressionIntentParam] = "testing a different value",
            };
            // "TRUE " parses (bool.TryParse trims), so it is the one member of this set that is a
            // real acknowledgement. Everything else fails closed.
            var expected = raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(expected, RemediationOpRenderer.IsRegressionAcknowledged(p));
        }

        [Fact]
        public void ATickAndAReason_IsAnAcknowledgement_AndTheReasonIsReadable()
        {
            var p = new Dictionary<string, string>
            {
                [RemediationOpRenderer.AcknowledgeRegressionParam] = "true",
                [RemediationOpRenderer.RegressionIntentParam] = "  vendor requires MAXDOP 1  ",
            };
            Assert.True(RemediationOpRenderer.IsRegressionAcknowledged(p));
            Assert.Equal("vendor requires MAXDOP 1", RemediationOpRenderer.ReadRegressionIntent(p));
        }

        [Fact]
        public void TheRefusal_NamesTheRoute_AndIsRecognisedByThePage()
        {
            var op = new RemediationOperation
            {
                ConfigName = "optimize for ad hoc workloads",
                MinValue = 0, MaxValue = 1, RecommendedValue = 1,
            };
            var text = RemediationOpRenderer.DescribeRegressionRefusal(op, target: 0);

            // The refusal states what happened to the money — the r2-01 rule, unchanged.
            Assert.Contains("nothing was charged", text, StringComparison.OrdinalIgnoreCase);
            // It names the acknowledged route ruling 7 opened, instead of the two controls that are
            // not on screen in the case it fires for.
            Assert.Contains("state why", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("audit ledger", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Revert to history", text, StringComparison.OrdinalIgnoreCase);
            // And the page recognises it through the renderer's OWN marker, so the acknowledgement
            // box can never appear beside a refusal it is not the answer to.
            Assert.True(RemediationOpRenderer.LooksLikeRegressionRefusal(text));
            Assert.False(RemediationOpRenderer.LooksLikeRegressionRefusal("Refused: insufficient credits."));
            Assert.False(RemediationOpRenderer.LooksLikeRegressionRefusal(null));
        }

        [Fact]
        public async Task AnAcknowledgedOffRecommendedApply_WritesTheReasonToTheLedger()
        {
            var exec = new ScriptedExecutor(RemediationOutcome.AppliedVerified, RemediationRollbackState.NotAttempted);
            var (runner, _, audit) = NewRunner(exec);

            await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian",
                new Dictionary<string, string>
                {
                    ["MaxDop"] = "1",
                    [RemediationOpRenderer.AcknowledgeRegressionParam] = "true",
                    [RemediationOpRenderer.RegressionIntentParam] = "vendor requires MAXDOP 1",
                });
            audit.Flush();

            var approved = ReadAuditEntries().Single(e => e.EventType == AuditEventType.RemediationApproved);
            var details = approved.Details.TryGetValue("Details", out var d) ? d : string.Empty;
            Assert.Contains("Off-recommended change acknowledged", details, StringComparison.Ordinal);
            Assert.Contains("vendor requires MAXDOP 1", details, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AnOrdinaryApply_WritesNoAcknowledgementLine()
        {
            var exec = new ScriptedExecutor(RemediationOutcome.AppliedVerified, RemediationRollbackState.NotAttempted);
            var (runner, _, audit) = NewRunner(exec);

            await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian",
                new Dictionary<string, string> { ["MaxDop"] = "4" });
            audit.Flush();

            var approved = ReadAuditEntries().Single(e => e.EventType == AuditEventType.RemediationApproved);
            var details = approved.Details.TryGetValue("Details", out var d) ? d : string.Empty;
            Assert.True(string.IsNullOrEmpty(details), "An ordinary apply logged an acknowledgement it did not have.");
        }

        [Fact]
        public void TheAcknowledgementDescriber_SaysNothing_WhenNothingWasAcknowledged()
        {
            Assert.Null(RemediationAcknowledgements.Describe(null));
            Assert.Null(RemediationAcknowledgements.Describe(new Dictionary<string, string>()));
            Assert.Null(RemediationAcknowledgements.Describe(new Dictionary<string, string> { ["MaxDop"] = "4" }));
            // A tick with no reason is not an acknowledgement, so it is not described as one either.
            Assert.Null(RemediationAcknowledgements.Describe(new Dictionary<string, string>
            {
                [RemediationOpRenderer.AcknowledgeRegressionParam] = "true",
            }));
        }

        [Fact]
        public void ThePage_OffersTheRouteOnlyBesideTheRefusal_AndRequiresBothHalves()
        {
            var markup = File.ReadAllText(Path.Combine(RawPassedScan.RepoRoot().FullName, "Pages", "Remediation.razor"));

            // The offer is gated on the renderer's own recogniser, not on a copy of the sentence.
            Assert.Contains("RemediationOpRenderer.LooksLikeRegressionRefusal", markup, StringComparison.Ordinal);
            Assert.Contains("OffersOffRecommendedRoute(row)", markup, StringComparison.Ordinal);
            // The button will not send until both halves are present.
            Assert.Contains("row.OffRecommendedAcknowledged && !string.IsNullOrWhiteSpace(row.OffRecommendedIntent)",
                markup, StringComparison.Ordinal);
            // And the two revert routes state their own reason rather than ticking silently.
            Assert.Equal(3, Regex.Matches(markup, Regex.Escape("RemediationOpRenderer.RegressionIntentParam")).Count);
        }

        // ── Ruling 3: the acknowledged force-install ─────────────────────────────

        [Fact]
        public void TheOverwriteAcknowledgement_FailsClosed()
        {
            Assert.False(MaintenanceSolutionOpRenderer.IsOverwriteAcknowledged(null));
            Assert.False(MaintenanceSolutionOpRenderer.IsOverwriteAcknowledged(new Dictionary<string, string>()));
            foreach (var raw in new[] { "false", "", "yes", "1", "TRUE-ish" })
                Assert.False(MaintenanceSolutionOpRenderer.IsOverwriteAcknowledged(
                    new Dictionary<string, string> { [MaintenanceSolutionOpRenderer.AcknowledgeOverwriteParam] = raw }));

            Assert.True(MaintenanceSolutionOpRenderer.IsOverwriteAcknowledged(
                new Dictionary<string, string> { [MaintenanceSolutionOpRenderer.AcknowledgeOverwriteParam] = "true" }));
        }

        [Fact]
        public void ThePartialInstallRefusal_StatesTheConsequenceInTheRuledWords()
        {
            var preExisting = new HashSet<string>(new[] { "CommandExecute", "DatabaseBackup" }, StringComparer.OrdinalIgnoreCase);
            var refusal = MaintenanceSolutionOpRenderer.DescribePartialInstallRefusal(preExisting);

            // Ruling 3's own sentence, verbatim.
            Assert.Contains("overwrites the existing objects and is not reversible", refusal, StringComparison.Ordinal);
            // It names what is in the way, and it names the route past it.
            Assert.Contains("CommandExecute", refusal, StringComparison.Ordinal);
            Assert.Contains("DatabaseIntegrityCheck", refusal, StringComparison.Ordinal); // the missing side
            Assert.Contains("tick the acknowledgement", refusal, StringComparison.OrdinalIgnoreCase);
            Assert.True(MaintenanceSolutionOpRenderer.LooksLikePartialInstallRefusal(refusal));
            Assert.False(MaintenanceSolutionOpRenderer.LooksLikePartialInstallRefusal("Snapshot read failed: timeout."));
        }

        [Fact]
        public void TheForcedInstallConsequence_SaysNotReversible_AndNamesWhatItOverwrites()
        {
            var preExisting = new HashSet<string>(new[] { "CommandExecute", "CommandLog" }, StringComparer.OrdinalIgnoreCase);
            var text = MaintenanceSolutionOpRenderer.DescribeForcedInstallConsequence(preExisting);

            Assert.Contains("not reversible", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CommandExecute", text, StringComparison.Ordinal);
            Assert.Contains("CommandLog", text, StringComparison.Ordinal);
            // It must not name an object that is NOT there, which is what makes it a consequence
            // rather than a warning template.
            Assert.DoesNotContain("IndexOptimize", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AnAcknowledgedForceInstall_IsRecordedAsSuch()
        {
            var exec = new ScriptedExecutor(RemediationOutcome.AppliedVerified, RemediationRollbackState.NotAttempted);
            var (runner, _, audit) = NewRunner(exec);

            await runner.ApplyAsync("INSTALLMAINTENANCESOLUTION", "srv1", approved: true, "adrian",
                new Dictionary<string, string>
                {
                    [MaintenanceSolutionOpRenderer.AcknowledgeOverwriteParam] = "true",
                });
            audit.Flush();

            var approved = ReadAuditEntries().Single(e => e.EventType == AuditEventType.RemediationApproved);
            var details = approved.Details.TryGetValue("Details", out var d) ? d : string.Empty;
            Assert.Contains("Forced install acknowledged", details, StringComparison.Ordinal);
            Assert.Contains("not reversible", details, StringComparison.OrdinalIgnoreCase);

            // The parameter really did reach the executor, so the page is not deciding this itself.
            Assert.True(MaintenanceSolutionOpRenderer.IsOverwriteAcknowledged(exec.LastParameters));
        }

        [Fact]
        public void ThePage_TicksTheForceBoxNowhereInCode()
        {
            var markup = File.ReadAllText(Path.Combine(RawPassedScan.RepoRoot().FullName, "Pages", "Remediation.razor"));

            // Refusal is the default path: the flag is declared false and only the operator's own
            // checkbox binding ever sets it. An assignment in code would be the page forcing an
            // install nobody agreed to.
            Assert.Contains("private bool _maintForceAcknowledged;", markup, StringComparison.Ordinal);
            // The DANGEROUS assignment is the one that TICKS the box in code — that would force an
            // install nobody agreed to. Clearing it to false is the opposite: SEC-1 (DECISIONS
            // 2026-08-25 23:01) resets it to false on a server change so an acknowledgement for
            // server A cannot travel to server B. So this forbids "= true", never the fail-closed
            // "= false" the reset relies on.
            Assert.False(Regex.IsMatch(markup, @"_maintForceAcknowledged\s*=\s*true\b"),
                "Pages/Remediation.razor ticks the force-install acknowledgement in code.");
            Assert.Contains("MaintenanceSolutionOpRenderer.AcknowledgeOverwriteParam", markup, StringComparison.Ordinal);
        }

        // ── SEC-1: a server change re-arms every destructive acknowledgement ──────

        // The behavioural half of SEC-1 instantiates the /remediation page and drives its reset by
        // reflection. That page is Content-Removed from a COMMUNITY build (buildprofile.targets), so
        // SQLTriage.Pages.Remediation is absent from the community assembly and the reflection would
        // fail at RUN time (the ProfileGatedTestSyncTests lint strips string literals, so it does not
        // catch a type reached only through a string). That test therefore lives in
        // Gated/RemediationServerChangeResetTests.cs, which the csproj's Gated\**\*.cs glob removes
        // from the community build. The wiring check below only reads the .razor FILE from disk, which
        // still ships as source in every profile, so it stays here and runs on both axes.

        [Fact]
        public void OnServerChanged_DelegatesToTheReset()
        {
            // The wiring the reflection test stands on: the server-change handler actually calls the
            // reset. An edit that inlines it partially or drops the call is caught here.
            var markup = File.ReadAllText(Path.Combine(RawPassedScan.RepoRoot().FullName, "Pages", "Remediation.razor"));
            Assert.Matches(@"private void OnServerChanged\(\)\s*\{\s*ResetTransientStateForServerChange\(\);", markup);
        }

        // ── Ruling 8: the DRAFT marker is gone from operator-facing copy ──────────

        // main writes the DRAFT marker with NO period inside the bracket — Config/*.json names and
        // descriptions keep theirs in exactly this form ("(DRAFT wording, voice review pending)").
        // The scan below used to pin the period form "(...pending.)", so a reintroduction written
        // the canonical no-period way would pass unseen (DRAFT-TEST, DECISIONS 2026-08-25 23:01).
        // Match the STEM so both the no-period convention and the lane's old period form are caught.
        private const string DraftMarkerStem = "(DRAFT wording, voice review pending";
        private static bool CarriesDraftMarker(string text) =>
            text.Contains(DraftMarkerStem, StringComparison.Ordinal);

        [Fact]
        public void NoOperatorFacingErrorOrBannerCopy_StillCarriesTheDraftMarker()
        {
            // Ruling 8: strip it from error / banner / refusal copy (names and descriptions keep
            // theirs, and those live in Config/*.json, which this scan does not touch).
            var root = RawPassedScan.RepoRoot().FullName;
            var offenders = new List<string>();
            foreach (var dir in new[] { "Data", "Pages", "Components" })
            {
                var path = Path.Combine(root, dir);
                if (!Directory.Exists(path)) continue;
                foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
                {
                    if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        && !file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)) continue;
                    var text = File.ReadAllText(file);
                    if (CarriesDraftMarker(text))
                        offenders.Add(Path.GetRelativePath(root, file));
                }
            }

            Assert.True(offenders.Count == 0,
                "These files still mark operator-facing copy as DRAFT: " + string.Join(", ", offenders));
        }

        [Fact]
        public void TheDraftMarkerScan_CatchesMainsNoInnerPeriodConvention()
        {
            // The DRAFT-TEST fix: the scan must catch the form main actually uses — no period inside
            // the bracket — not only the period form the lane briefly shipped. Before the fix the
            // first assertion failed, because the scan pinned "(...pending.)".
            Assert.True(CarriesDraftMarker("A wait verdict (DRAFT wording, voice review pending)"),
                "The scan must catch the no-inner-period form main actually uses.");
            Assert.True(CarriesDraftMarker("Legacy copy (DRAFT wording, voice review pending.)"),
                "The scan must still catch the period form the lane briefly shipped.");
            Assert.False(CarriesDraftMarker("Ordinary operator copy with no marker."),
                "The scan must not flag copy that has no DRAFT marker at all.");
        }

        // ── R5: the store-recovery register, rewritten to the voice guide ────────

        [Theory]
        [InlineData(ConfigLoadOutcome.Unreadable, "C:\\x\\settings.json.rejected-20260825")]
        [InlineData(ConfigLoadOutcome.Empty, null)]
        [InlineData(ConfigLoadOutcome.Unreadable, null)]
        public void TheStoreRecoverySentence_UsesNoEmDash(ConfigLoadOutcome outcome, string? quarantined)
        {
            // It renders on /remediation, /settings, /alerting-config and the portal status page.
            // dev/VOICE_GUIDE.md rules out the em-dash aside; this branch shipped two in one sentence.
            var text = ConfigFileHelper.DescribeStoreRecovery(outcome, quarantined);
            Assert.DoesNotContain("\u2014", text);
            Assert.DoesNotContain("\u2013", text);
            Assert.False(string.IsNullOrWhiteSpace(text));
        }

        [Fact]
        public void TheStoreRecoverySentence_StillSaysTheSameThreeThings()
        {
            // The rewrite is a VOICE change. Each branch still states the fact it is conditioned on.
            var quarantined = ConfigFileHelper.DescribeStoreRecovery(
                ConfigLoadOutcome.Unreadable, "C:\\x\\settings.json.rejected-20260825");
            Assert.Contains("settings.json.rejected-20260825", quarantined, StringComparison.Ordinal);
            Assert.Contains("not lost", quarantined, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("restart", quarantined, StringComparison.OrdinalIgnoreCase);

            var empty = ConfigFileHelper.DescribeStoreRecovery(ConfigLoadOutcome.Empty, null);
            Assert.Contains("EMPTY", empty, StringComparison.Ordinal);
            Assert.DoesNotContain(".rejected-", empty, StringComparison.Ordinal);

            var noCopy = ConfigFileHelper.DescribeStoreRecovery(ConfigLoadOutcome.Unreadable, null);
            Assert.Contains("No .rejected- copy was kept", noCopy, StringComparison.Ordinal);
        }

        // ── R5-SCOPE: the whole remediation page family, not one function ─────────

        [Theory]
        [InlineData("Pages/Remediation.razor")]
        [InlineData("Pages/AgentJobGuard.razor")]
        [InlineData("Pages/AgentJobSync.razor")]
        [InlineData("Pages/RemediationLab.razor")]
        public void NoClientVisibleCopyOnTheRemediationPages_CarriesAnEmDashOrEllipsis(string rel)
        {
            // R5-SCOPE (DECISIONS 2026-08-25 23:01): the earlier em-dash fix was scoped to one
            // function, while these four pages still rendered ~40 error / refusal / prose strings with
            // the banned em-dash and the ellipsis char. dev/VOICE_GUIDE.md rules both out of client
            // copy. StripComments blanks // and @* *@ comments while KEEPING rendered markup and
            // string literals, so this scans only what an operator can read; code comments keep their
            // own dashes. En-dash ranges (e.g. "10-20%") are the guide's range case and out of scope.
            var root = RawPassedScan.RepoRoot().FullName;
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            var isRazor = path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
            var rendered = RawPassedScan.StripComments(File.ReadAllLines(path), isRazor);

            var offenders = new List<string>();
            for (var i = 0; i < rendered.Length; i++)
                if (rendered[i].Contains('—') || rendered[i].Contains('…'))
                    offenders.Add($"{rel}:{i + 1}: {rendered[i].Trim()}");

            Assert.True(offenders.Count == 0,
                "Client-visible copy still carries an em-dash (—) or ellipsis (…):\n"
                + string.Join("\n", offenders));
        }

        // ── R6: an inverse that never rendered is not an inverse that failed ─────

        [Fact]
        public void ARollbackWhereNothingRendered_SaysNothingWasSent()
        {
            var verdict = DbatoolsRemediationExecutor.DescribeDbSetOptionRollback(
                neverAttempted: new[] { "bad]name: identifier is not renderable" },
                ranAndFailed: Array.Empty<string>(),
                sent: Array.Empty<string>());

            Assert.NotNull(verdict);
            // NotAvailable, not Failed: nothing was executed, so there is no failed attempt to
            // attest to. The runner writes no "rollback FAILED" ledger line for it.
            Assert.Equal(RemediationRollbackState.NotAvailable, verdict!.Value.State);
            Assert.Contains("No rollback was attempted", verdict.Value.Error!, StringComparison.Ordinal);
            Assert.Contains("nothing was sent", verdict.Value.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("stay changed", verdict.Value.Error!, StringComparison.OrdinalIgnoreCase);
            // The defect wording: this arm used to read exactly like a real failure.
            Assert.DoesNotContain("Rollback failed for", verdict.Value.Error!, StringComparison.Ordinal);

            // Fail-closed: neither NotAvailable nor NotAttempted refunds a credit here, because the
            // change may still be live.
            Assert.True(RemediationCreditOutcome.ChangeStuck(
                RemediationOutcome.AppliedVerifyFailed, verdict.Value.State));
        }

        [Fact]
        public void AMixedRollback_KeepsTheTwoFactsApart()
        {
            var verdict = DbatoolsRemediationExecutor.DescribeDbSetOptionRollback(
                neverAttempted: new[] { "Weird]Db: identifier is not renderable" },
                ranAndFailed: new[] { "Sales: timeout expired" },
                sent: new[] { "Payroll" });

            Assert.NotNull(verdict);
            Assert.Equal(RemediationRollbackState.Failed, verdict!.Value.State);
            var text = verdict.Value.Error!;
            Assert.Contains("The inverse ran and failed for: Sales", text, StringComparison.Ordinal);
            Assert.Contains("Nothing was sent for: Weird]Db", text, StringComparison.Ordinal);
            Assert.Contains("never attempted", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("The inverse was sent for: Payroll", text, StringComparison.Ordinal);
        }

        [Fact]
        public void ACleanRollback_LeavesTheVerdictToTheConfirmingRead()
        {
            // Null means "everything was sent and none threw" — the only case with something left to
            // confirm. The caller then does the confirming read, which is what turns it into
            // Confirmed or Unconfirmed. This function never invents a success.
            Assert.Null(DbatoolsRemediationExecutor.DescribeDbSetOptionRollback(
                Array.Empty<string>(), Array.Empty<string>(), new[] { "Payroll", "Sales" }));
        }

        // ── Ruling 10: the sibling pages read the damage-aware seam ──────────────

        [Theory]
        [InlineData("AgentJobSync.razor")]
        [InlineData("AgentJobGuard.razor")]
        public void TheSiblingPages_ShowLedgerDamage_NeverASilentZero(string page)
        {
            var markup = File.ReadAllText(Path.Combine(RawPassedScan.RepoRoot().FullName, "Pages", page));

            // The damage state is READ (it was not read at all before this ruling)...
            Assert.Contains("Ledger.IsStoreDamaged", markup, StringComparison.Ordinal);
            // ...named on screen rather than rendered as a zero balance...
            Assert.Contains("Credit position unknown", markup, StringComparison.Ordinal);
            // ...carries the same recovery sentence /remediation prints...
            Assert.Contains("Ledger.DescribeStoreRecovery()", markup, StringComparison.Ordinal);
            // ...and blocks the apply button, because a damaged ledger can afford nothing.
            Assert.Contains("!Ledger.IsStoreDamaged &&", markup, StringComparison.Ordinal);
        }

        // The damage seam those pages now read is exercised for real in
        // RemediationCreditLedgerIntegrityTests (a corrupt store reports damage AND zero
        // available), which is exactly why printing the zero on its own was
        // indistinguishable from a spent-out server. Not duplicated here.
    }
}
