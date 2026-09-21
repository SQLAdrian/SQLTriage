/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// remediation-r2-01 (honesty hunt, 2026-08-25; #3 in the wave top-ten): every one-click
    /// Configuration row was pre-populated with <c>Operation.MinValue</c> — the schema FLOOR, not a
    /// recommended target. For five of the eight shipped fixes that floor is the OPPOSITE of what
    /// the fix's own description offers ("Enable ... (1)" pre-filled 0; max server memory pre-filled
    /// a 128 MB cap). Applying the shipped default to an already-compliant server wrote the
    /// regression, verified it (post == target), committed a change credit and ledgered an
    /// "applied" remediation. The hunt re-proved it live: on .\new2022 four of the five shipped
    /// defaults would have been a real regressing write that day.
    ///
    /// <para>Two things close it. The template now declares the value it exists to reach, so the UI
    /// pre-fills that or nothing at all; and the executor refuses a write that would move a server
    /// already AT that value off it, so a bad parameter dictionary cannot regress a server either.
    /// The refusal is CouldNotRun, which the runner refunds: a server that needed nothing is never
    /// charged.</para>
    /// </summary>
    public class RemediationConfigDefaultRegressionTests
    {
        private static RemediationTemplateStore Store() => new(NullLogger<RemediationTemplateStore>.Instance);

        // ── The shipped defaults now point AT the fix, or nowhere at all ─────

        [Theory]
        [InlineData("OPTIMIZEFORADHOC", 1)]
        [InlineData("BACKUPCOMPRESSION", 1)]
        [InlineData("DEFAULTTRACE", 1)]
        [InlineData("CROSSDBOWNERSHIP", 0)]
        [InlineData("ADHOCDISTRIBUTEDQUERIES", 0)]
        public void ShippedToggle_RecommendsTheValueItsOwnDescriptionDeclares(string key, int expected)
        {
            var op = Store().TryGet(key)!.Operation!;
            Assert.Equal(expected, op.RecommendedValue);
        }

        [Theory]
        [InlineData("MAXDOP")]
        [InlineData("CTFP")]
        [InlineData("MAXSERVERMEMORY")]
        public void AWorkloadDependentFix_RecommendsNothing_RatherThanGuessing(string key)
        {
            // These three have no defensible constant. Null means the page pre-fills nothing and
            // refuses to run without an operator-supplied target, which is the honest floor.
            var op = Store().TryGet(key)!.Operation!;
            Assert.Null(op.RecommendedValue);
        }

        [Fact]
        public void NoShippedConfigurationFix_StillPreFillsTheSchemaFloorAsItsRecommendation()
        {
            // The exact defect shape, pinned: a recommendation that equals MinValue on a bounded
            // toggle is the floor wearing a recommendation's clothes. The two legitimate "disable"
            // fixes recommend 0, which IS their floor, so they are named rather than swept in.
            var deliberateZeroTargets = new HashSet<string> { "CROSSDBOWNERSHIP", "ADHOCDISTRIBUTEDQUERIES" };

            var offenders = Store().All()
                .Where(t => t.Kind == RemediationKind.Configuration && t.Operation is not null)
                .Where(t => t.Operation!.RecommendedValue is int r
                            && r == t.Operation.MinValue
                            && !deliberateZeroTargets.Contains(t.Key))
                .Select(t => t.Key)
                .ToList();

            Assert.Empty(offenders);
        }

        [Fact]
        public void EveryRecommendedValue_SitsInsideItsOwnBounds()
        {
            var outOfRange = Store().All()
                .Where(t => t.Operation?.RecommendedValue is int r
                            && (r < t.Operation.MinValue || r > t.Operation.MaxValue))
                .Select(t => t.Key)
                .ToList();

            Assert.Empty(outOfRange);
        }

        // ── The executor's guard: never write a value worse than the current one ──

        [Fact]
        public void ApplyingZeroToAServerAlreadyAtOne_IsARegression()
        {
            var op = Store().TryGet("OPTIMIZEFORADHOC")!.Operation!;
            Assert.True(RemediationOpRenderer.IsRegressionFromRecommended(op, currentValue: 1, target: 0));
        }

        [Fact]
        public void ApplyingTheRecommendedValue_IsNeverARegression()
        {
            var op = Store().TryGet("OPTIMIZEFORADHOC")!.Operation!;
            Assert.False(RemediationOpRenderer.IsRegressionFromRecommended(op, currentValue: 0, target: 1));
            Assert.False(RemediationOpRenderer.IsRegressionFromRecommended(op, currentValue: 1, target: 1));
        }

        [Fact]
        public void AServerAtTheSameValueItIsBeingAskedFor_IsNotGuarded()
        {
            // The DEGENERATE case, kept and now named for what it is. target == current, which
            // ExecuteConfigurationAsync short-circuits as NoOp before the guard is ever consulted.
            // This was the whole of this file's coverage for the claim below, which is the
            // test-side blind spot this wave is about: an assertion narrower than its own name.
            var op = Store().TryGet("CROSSDBOWNERSHIP")!.Operation!;
            Assert.False(RemediationOpRenderer.IsRegressionFromRecommended(op, currentValue: 1, target: 1));
        }

        [Fact]
        public void AServerNotYetAtTheRecommendedValue_IsNotGuarded()
        {
            // The guard protects a COMPLIANT server. A server sitting somewhere else entirely is
            // the operator's call, and blocking it would be inventing policy rather than honesty.
            //
            // The general case needs an op with more than two values, so it is built here:
            // recommended 50, a server sitting at 5, an operator moving it to 25. Neither value is
            // the recommended one, and target != current, so nothing short-circuits before the
            // guard. Widen the guard to "anything below the recommended value" and this goes red
            // while the degenerate test above stays green: that gap is the defect.
            var wideRange = new RemediationOperation
            {
                OpKind = RemediationOpKind.SpConfigure,
                ConfigName = "cost threshold for parallelism",
                ValueParam = "Threshold",
                MinValue = 5,
                MaxValue = 32767,
                RecommendedValue = 50,
            };

            Assert.False(RemediationOpRenderer.IsRegressionFromRecommended(wideRange, currentValue: 5, target: 25));
            Assert.False(RemediationOpRenderer.IsRegressionFromRecommended(wideRange, currentValue: 5, target: 50));

            // The guard DOES fire once that same server is compliant, so the two assertions above
            // are not passing because the function never fires on this op at all.
            Assert.True(RemediationOpRenderer.IsRegressionFromRecommended(wideRange, currentValue: 50, target: 25));

            // And the shipped binary toggle, on the only non-degenerate shape it has: not yet at 0,
            // moving to 0.
            var shipped = Store().TryGet("CROSSDBOWNERSHIP")!.Operation!;
            Assert.False(RemediationOpRenderer.IsRegressionFromRecommended(shipped, currentValue: 1, target: 0));
            Assert.NotEqual(1, shipped.RecommendedValue); // the premise: 1 is NOT the recommended value here
        }

        [Fact]
        public void WithNoRecommendedValue_NothingIsCalledARegression()
        {
            // No declared direction means no defensible definition of "worse".
            var op = Store().TryGet("MAXDOP")!.Operation!;
            Assert.False(RemediationOpRenderer.IsRegressionFromRecommended(op, currentValue: 8, target: 1));
            Assert.False(RemediationOpRenderer.IsRegressionFromRecommended(null, currentValue: 8, target: 1));
        }

        [Fact]
        public void AnUnreadCurrentValue_IsNotTreatedAsCompliant()
        {
            // A null snapshot means we do not know the current value. The guard stays out of the
            // way; the apply path's own bounds and verify still run.
            var op = Store().TryGet("DEFAULTTRACE")!.Operation!;
            Assert.False(RemediationOpRenderer.IsRegressionFromRecommended(op, currentValue: null, target: 0));
        }

        // ── The acknowledgement: Undo and Revert are the one legitimate way off ──

        [Fact]
        public void WithoutTheAcknowledgement_ARegressingRequestIsNotAcknowledged()
        {
            Assert.False(RemediationOpRenderer.IsRegressionAcknowledged(null));
            Assert.False(RemediationOpRenderer.IsRegressionAcknowledged(new Dictionary<string, string>()));
        }

        [Theory]
        [InlineData("false")]
        [InlineData("")]
        [InlineData("1")]
        [InlineData("yes")]
        [InlineData("TRUE-ish")]
        public void AMalformedOrNegativeAcknowledgement_FailsClosed(string raw)
        {
            var p = new Dictionary<string, string> { [RemediationOpRenderer.AcknowledgeRegressionParam] = raw };
            Assert.False(RemediationOpRenderer.IsRegressionAcknowledged(p));
        }

        [Theory]
        [InlineData("true")]
        [InlineData("True")]
        [InlineData("TRUE")]
        public void AnExplicitAcknowledgement_WithAStatedReason_IsHonoured(string raw)
        {
            // RULING 7 (DECISIONS 2026-08-25 17:33) added the second half: the operator STATES
            // INTENT. A tick on its own used to be enough, and this test pinned that. It is not
            // enough now, because the ledger has to carry the reason a compliant server was
            // deliberately moved off its recommended value. Undo and Revert to history state their
            // own reason and travel the same road.
            var p = new Dictionary<string, string>
            {
                [RemediationOpRenderer.AcknowledgeRegressionParam] = raw,
                [RemediationOpRenderer.RegressionIntentParam] = "vendor requires this value",
            };
            Assert.True(RemediationOpRenderer.IsRegressionAcknowledged(p));

            // The tick alone no longer is.
            var tickOnly = new Dictionary<string, string>
            {
                [RemediationOpRenderer.AcknowledgeRegressionParam] = raw,
            };
            Assert.False(RemediationOpRenderer.IsRegressionAcknowledged(tickOnly));
        }

        [Fact]
        public void TheRefusalNamesTheSetting_TheCurrentValue_AndWhatToDoInstead()
        {
            var op = Store().TryGet("DEFAULTTRACE")!.Operation!;
            var text = RemediationOpRenderer.DescribeRegressionRefusal(op, target: 0);

            Assert.Contains("default trace enabled", text);
            Assert.Contains("1", text);       // the recommended value it is already at
            Assert.Contains("credit", text);  // the consequence of applying anyway
            // Ruling 7: the sentence now names the acknowledged route rather than two controls that
            // may not be on screen. It says what the operator does next either way.
            Assert.Contains("state why", text);
            Assert.Contains("acknowledgement", text);
            Assert.DoesNotContain("—", text); // house style: no em-dashes in operator copy
        }

        [Fact]
        public void TheRefusal_DoesNotDirectTheOperatorToAControlThatMayNotBeOnScreen()
        {
            // The refusal used to end "Use Undo or Revert to history if you mean to change it
            // back." Both controls are CONDITIONAL on /remediation. Undo renders only after a
            // verified apply in the same session; Revert to history renders only when the audit
            // ledger already holds a pre-change value for this template on this server. In the
            // exact case this refusal fires for, a compliant server SQLTriage has never remediated,
            // neither control exists and the sentence pointed at nothing.
            var op = Store().TryGet("DEFAULTTRACE")!.Operation!;
            var text = RemediationOpRenderer.DescribeRegressionRefusal(op, target: 0);

            // Ruling 7 settled the product question this test's comment left open. The refusal no
            // longer names Undo or Revert to history at all: it names the acknowledged route, which
            // IS on screen beside it in exactly the case the refusal fires for.
            Assert.DoesNotContain("Undo", text);
            Assert.DoesNotContain("Revert to history", text);
            Assert.DoesNotContain("Use Undo or Revert to history if", text);
            Assert.Contains("tick the acknowledgement below", text);

            // The markup premise the sentence rests on. If either control ever becomes
            // unconditional, this goes red and the sentence gets re-read rather than silently
            // becoming an understatement.
            var markup = System.IO.File.ReadAllText(
                System.IO.Path.Combine(RawPassedScan.RepoRoot().FullName, "Pages", "Remediation.razor"));
            Assert.Contains("row.Result is null && row.HistoryPreChange is int", markup);
            Assert.Contains("RemediationOutcome.AppliedVerified && row.Result.PreChangeValue is int", markup);
        }

        // ── Corpus-fed templates: a `value_fixed` IS the recommendation ──────

        [Fact]
        public void ACorpusFixWithAFixedValue_RecommendsThatValue()
        {
            var store = Store();
            store.LoadCorpusTemplates(new[] { CorpusCheckWithFixedValue("ZZ-TEST-FIXED", "blocked process threshold", 20) });

            var op = store.TryGet("ZZ-TEST-FIXED")!.Operation!;
            Assert.Equal(20, op.RecommendedValue);
            Assert.True(RemediationOpRenderer.IsRegressionFromRecommended(op, currentValue: 20, target: 0));
        }

        private static SqlCheck CorpusCheckWithFixedValue(string id, string configName, int valueFixed) =>
            new()
            {
                Id = id,
                Name = id,
                Remediation = new CheckRemediation
                {
                    AutoFixable = true,
                    Reversible = true,
                    RiskClass = "trivial",
                    Operation = new CheckRemediationOperation
                    {
                        OpKind = "sp_configure",
                        ConfigName = configName,
                        ValueFixed = valueFixed,
                    }
                }
            };
    }
}
