/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// R1 (gate residual, 2026-08-25) and ruling 1. Two halves, and the first one is the reason this
    /// file exists.
    ///
    /// <para><b>The seeding pin.</b> The lane's headline defect was <c>Pages/Remediation.razor</c>
    /// seeding every one-click fix's target from <c>Operation.MinValue</c> — the schema FLOOR, which
    /// for every "Enable (1)" toggle is 0, the exact opposite of the fix. It regressed a compliant
    /// server, verified clean, and charged a credit. The lane fixed it and shipped no offline test
    /// that would go red if anyone put the one word back. The gate called that its strongest ask
    /// before merge. <see cref="ThePageSeedsFromTheRecommendation_NeverFromTheSchemaFloor"/> reads
    /// the real .razor and is that pin: revert the seeding line to MinValue and it fails.</para>
    ///
    /// <para><b>The recommendation.</b> Ruling 1 (DECISIONS 2026-08-25 17:33) then said the three
    /// templates that declare NO constant recommendation — MAXDOP, cost threshold for parallelism,
    /// max server memory — should pre-fill the standard published recommendation for the connected
    /// host, with the operator still confirming. The formulas live in
    /// <see cref="RemediationRecommendedValues"/> and are pinned here against the published tables.
    /// Adrian reviews the formulas themselves at merge review; these tests pin that the CODE does
    /// what the comments beside it claim, not that the guidance is right.</para>
    /// </summary>
    public class RemediationPrefillSeedingTests
    {
        private static RemediationTemplateStore Store() => new(NullLogger<RemediationTemplateStore>.Instance);

        // ── The seeding pin ──────────────────────────────────────────────────────

        private static string PageMarkup() =>
            File.ReadAllText(Path.Combine(RawPassedScan.RepoRoot().FullName, "Pages", "Remediation.razor"));

        [Fact]
        public void ThePageSeedsFromTheRecommendation_NeverFromTheSchemaFloor()
        {
            var markup = PageMarkup();

            // The seeding expression, as it must appear. RemediationRecommendedValues.For returns the
            // template's own declared RecommendedValue untouched when it has one, so this line is
            // still "seed from the recommendation" and never from a bound.
            Assert.Contains(
                "new FixRow { Template = t, Value = RemediationRecommendedValues.For(t, _sizing) }",
                markup, StringComparison.Ordinal);

            // And the defect shape cannot come back under any spelling. This is the assertion that
            // goes red on the one-line revert the gate asked to be pinned.
            var floorSeed = new Regex(@"Value\s*=\s*[A-Za-z_.!]*\bOperation!?\.(MinValue|MaxValue)\b");
            Assert.False(floorSeed.IsMatch(markup),
                "Pages/Remediation.razor seeds a fix's target from a schema bound. " +
                "MinValue is the floor: for an \"Enable (1)\" toggle it is 0, the opposite of the fix.");
        }

        [Fact]
        public void TheRecommendationForADeclaredTemplate_IsTheDeclaredValue_NotABound()
        {
            // Every shipped Configuration template that declares its own target: the seeder returns
            // that target, whatever the host facts say. This is the half of the seeding rule the
            // markup scan cannot see.
            var store = Store();
            var checkedAny = false;
            foreach (var t in store.All())
            {
                if (t.Operation?.RecommendedValue is not int declared) continue;
                checkedAny = true;
                Assert.Equal(declared, RemediationRecommendedValues.For(t, ServerSizingFacts.None));
                Assert.Equal(declared, RemediationRecommendedValues.For(t, new ServerSizingFacts(64, 4, 262144)));
                if (declared != t.Operation.MinValue)
                    Assert.NotEqual(t.Operation.MinValue, RemediationRecommendedValues.For(t, ServerSizingFacts.None));
            }
            Assert.True(checkedAny, "No shipped template declares a RecommendedValue — the seeding rule has nothing to pin.");
        }

        [Fact]
        public void EveryEnableToggle_SeedsOne_NotZero()
        {
            // The exact live defect, at the templates it fired on: an "Enable ... (1)" toggle whose
            // floor is 0. Seeded from MinValue these all came out 0.
            var store = Store();
            foreach (var key in new[] { "OPTIMIZEFORADHOC", "BACKUPCOMPRESSION" })
            {
                var t = store.TryGet(key);
                Assert.NotNull(t);
                Assert.Equal(0, t!.Operation!.MinValue);
                Assert.Equal(1, RemediationRecommendedValues.For(t, ServerSizingFacts.None));
            }
        }

        // ── Ruling 1: MAXDOP, per Microsoft's published table ─────────────────────

        [Theory]
        // One NUMA node, 8 or fewer logical processors: MAXDOP = the processor count.
        [InlineData(4, 1, 4)]
        [InlineData(8, 1, 8)]
        // One NUMA node, more than 8: MAXDOP = 8.
        [InlineData(16, 1, 8)]
        [InlineData(64, 1, 8)]
        // More than one node, 16 or fewer per node: MAXDOP = processors per node.
        [InlineData(24, 2, 12)]
        [InlineData(32, 2, 16)]
        // More than one node, more than 16 per node: half the processors per node, capped at 16.
        [InlineData(48, 2, 12)]
        [InlineData(96, 2, 16)]
        public void MaxDop_FollowsThePublishedTable(int cores, int nodes, int expected)
        {
            Assert.Equal(expected, RemediationRecommendedValues.MaxDop(new ServerSizingFacts(cores, nodes, 65536)));
        }

        [Fact]
        public void MaxDop_RecommendsNothing_WhenTheHostWasNotRead()
        {
            Assert.Null(RemediationRecommendedValues.MaxDop(ServerSizingFacts.None));
            Assert.Null(RemediationRecommendedValues.MaxDop(null));
            // A single-core server: the only value this rule could offer is 1, which the operator
            // already has. Nothing is pre-filled rather than a number carrying no information.
            Assert.Null(RemediationRecommendedValues.MaxDop(new ServerSizingFacts(1, 1, 8192)));
        }

        [Fact]
        public void MaxDopRow_PreFillsNothing_WhenTheHostWasNotRead()
        {
            // The end-to-end shape: the honest "no pre-fill" state the lane shipped is exactly what a
            // failed sizing read falls back to. A read failure can never manufacture a number.
            var t = Store().TryGet("MAXDOP");
            Assert.NotNull(t);
            Assert.Null(t!.Operation!.RecommendedValue);
            Assert.Null(RemediationRecommendedValues.For(t, ServerSizingFacts.None));
            Assert.Equal(8, RemediationRecommendedValues.For(t, new ServerSizingFacts(32, 1, 131072)));
        }

        // ── Ruling 1: cost threshold for parallelism ──────────────────────────────

        [Fact]
        public void CostThreshold_IsFifty_AndDoesNotNeedTheHost()
        {
            var t = Store().TryGet("CTFP");
            Assert.NotNull(t);
            Assert.Null(t!.Operation!.RecommendedValue);   // no constant is DECLARED on the template
            Assert.Equal(50, RemediationRecommendedValues.CostThresholdForParallelism);
            // It is host-independent, so it recommends the same value with no facts at all. That is
            // the one of the three that a failed sizing read does not silence.
            Assert.Equal(50, RemediationRecommendedValues.For(t, ServerSizingFacts.None));
            // And it is not SQL Server's shipped 5, which is the whole point of the fix.
            Assert.NotEqual(5, RemediationRecommendedValues.For(t, ServerSizingFacts.None));
        }

        // ── Ruling 1: max server memory ───────────────────────────────────────────

        [Theory]
        // 1 GB for the OS, +1 GB per 4 GB in the 4-16 GB band, +1 GB per 8 GB above 16 GB.
        [InlineData(8192, 6144)]      // 8 GB host:  reserve 1 + 1  = 2 GB
        [InlineData(16384, 12288)]    // 16 GB host: reserve 1 + 3  = 4 GB
        [InlineData(32768, 26624)]    // 32 GB host: reserve 1 + 3 + 2 = 6 GB
        [InlineData(65536, 55296)]    // 64 GB host: reserve 1 + 3 + 6 = 10 GB
        [InlineData(262144, 227328)]  // 256 GB host: reserve 1 + 3 + 30 = 34 GB
        public void MaxServerMemory_FollowsTheReservationFormula(int hostMb, int expectedCapMb)
        {
            Assert.Equal(expectedCapMb, RemediationRecommendedValues.MaxServerMemoryMb(new ServerSizingFacts(8, 1, hostMb)));
        }

        [Fact]
        public void MaxServerMemory_RecommendsNothing_OnASmallOrUnreadHost()
        {
            Assert.Null(RemediationRecommendedValues.MaxServerMemoryMb(ServerSizingFacts.None));
            // Below 4 GB the reservation would leave SQL Server less than the engine's own minimum,
            // so there is no honest cap to offer.
            Assert.Null(RemediationRecommendedValues.MaxServerMemoryMb(new ServerSizingFacts(2, 1, 2048)));
        }

        [Fact]
        public void MaxServerMemory_NeverPreFillsTheOneHundredAndTwentyEightMegabyteFloor()
        {
            // The live defect this replaced: MinValue on MAXSERVERMEMORY is 128, so the old seeding
            // offered a 128 MB cap on every server it rendered.
            var t = Store().TryGet("MAXSERVERMEMORY");
            Assert.NotNull(t);
            Assert.Equal(128, t!.Operation!.MinValue);
            foreach (var hostMb in new[] { 8192, 16384, 65536, 262144 })
                Assert.NotEqual(128, RemediationRecommendedValues.For(t, new ServerSizingFacts(8, 1, hostMb)));
        }

        // ── The r2-01 guard is untouched by the pre-fill ──────────────────────────

        [Fact]
        public void TheRegressionGuard_StillRefuses_AfterThePreFillLanded()
        {
            // Ruling 1 required the r2-01 guard to survive intact. It keys off the template's own
            // DECLARED RecommendedValue, which the pre-fill does not write to, so a compliant server
            // is still refused and still not charged.
            var t = Store().TryGet("OPTIMIZEFORADHOC");
            Assert.NotNull(t);
            Assert.True(RemediationOpRenderer.IsRegressionFromRecommended(t!.Operation, currentValue: 1, target: 0));

            // And the three computed recommendations declare nothing, so the guard has no opinion on
            // them — which is what "no defensible definition of worse" means in code.
            foreach (var key in new[] { "MAXDOP", "CTFP", "MAXSERVERMEMORY" })
            {
                var computed = Store().TryGet(key);
                Assert.Null(computed!.Operation!.RecommendedValue);
                Assert.False(RemediationOpRenderer.IsRegressionFromRecommended(computed.Operation, currentValue: 50, target: 5));
            }
        }

        [Fact]
        public void ARecommendationOutsideTheTemplatesOwnBounds_IsNotPreFilled()
        {
            // A pre-filled value the executor would refuse on sight is worse than an empty box.
            var narrow = new RemediationTemplate
            {
                Key = "ZZTEST-NARROW",
                DisplayName = "Test",
                Kind = RemediationKind.Configuration,
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.SpConfigure,
                    ConfigName = "cost threshold for parallelism",
                    ValueParam = "CostThreshold",
                    MinValue = 0,
                    MaxValue = 10,          // 50 does not fit
                    RecommendedValue = null,
                },
            };
            Assert.Null(RemediationRecommendedValues.For(narrow, ServerSizingFacts.None));
        }

        // ── The tooltip says it is a recommendation ───────────────────────────────

        [Theory]
        [InlineData("MAXDOP")]
        [InlineData("CTFP")]
        [InlineData("MAXSERVERMEMORY")]
        public void TheTooltip_CallsItARecommendation_AndSaysTheOperatorConfirms(string key)
        {
            var t = Store().TryGet(key);
            var facts = new ServerSizingFacts(16, 1, 65536);
            var value = RemediationRecommendedValues.For(t, facts);
            Assert.NotNull(value);

            var text = RemediationRecommendedValues.DescribeRecommendation(t, facts, value!.Value);
            Assert.Contains("Recommended", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("confirm", text, StringComparison.OrdinalIgnoreCase);
            // It may never read as a measurement of the operator's workload.
            Assert.DoesNotContain("your workload needs", text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheSizingQuery_IsReadOnly()
        {
            // The pre-fill's only server contact. It must classify Safe: a recommendation that
            // writes to find out what to recommend is not a recommendation.
            var verdict = SqlSafetyValidator.Validate(RemediationRecommendedValues.SizingQuery);
            Assert.True(verdict.IsSafe, "The host-sizing read must be read-only. " + verdict.Reason);
        }
    }
}
