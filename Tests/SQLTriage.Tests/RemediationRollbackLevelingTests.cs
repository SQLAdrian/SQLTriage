/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationRollbackLevelingTests — Phase-2 items 2.1, 2.2 and 2.3, offline.
 *
 * WHAT THE DEFECT WAS, in one paragraph, because these assertions are meaningless without it.
 * Spike S2 section 3.1 drove a real verify-failure through the real engine against SQL 2017 on
 * `min server memory (MB)`, an option SQL Server COERCES: the configured value goes where you put
 * it, and value_in_use stays pinned at a 16 MB floor. The engine captured value_in_use (16), applied
 * 8, verified against value_in_use (16, so it failed, correctly), rolled back by re-applying its
 * captured 16, and then "confirmed" the rollback by reading value_in_use (16) and comparing it
 * against its value_in_use snapshot (16). Two facts fall out of that. The confirming read was NON
 * DISCRIMINATING: 16 == 16 was true before the inverse ran, so Confirmed was decided in advance.
 * And the WRONG COLUMN was restored: the server's configured value went 0 -> 8 -> 16 and never back
 * to 0, while the ledger recorded a confirmed rollback.
 *
 * The fix has three parts and each is pinned below:
 *   2.1  capture, inverse and confirming read all move to sys.configurations.value; the VERIFY
 *        stays on value_in_use, because a setting the engine coerced has not taken effect and
 *        reporting it verified would be a different lie.
 *   2.2  PreChangeValue is set on the rollback return, not only the verified one.
 *   2.3  capture derives from the RENDERED statement, so the 'show advanced options' prelude the
 *        renderer emits is captured and restored (spike S1 section 3.10).
 *
 * The live proof of 2.1 - the exact scenario above, run again and landing at value 0 - is
 * RemediationPhase2LiveSmokeTests. This file is the offline half: it pins the RENDERINGS and the
 * SCAN, which is where the fix actually lives.
 */

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class RemediationRollbackLevelingTests
    {
        private readonly ITestOutputHelper _out;
        public RemediationRollbackLevelingTests(ITestOutputHelper output) => _out = output;

        private static RemediationOperation MinServerMemoryOp() => new()
        {
            OpKind = RemediationOpKind.SpConfigure,
            ConfigName = "min server memory (MB)",
            AdvancedOption = true,
            ValueParam = "MinServerMemoryMb",
            MinValue = 0,
            MaxValue = 2147483647,
        };

        private static RemediationOperation NonAdvancedOp() => new()
        {
            OpKind = RemediationOpKind.SpConfigure,
            ConfigName = "show advanced options",
            AdvancedOption = false,
            ValueParam = "Enabled",
            MinValue = 0,
            MaxValue = 1,
        };

        // ── 2.1: the two reads are DIFFERENT reads, and each one reads what it is for ──

        [Fact]
        public void TheConfiguredRead_ReadsValue_AndTheVerifyRead_ReadsValueInUse()
        {
            var op = MinServerMemoryOp();

            Assert.True(RemediationOpRenderer.TryRenderConfiguredRead(op, out var configured, out _));
            Assert.True(RemediationOpRenderer.TryRenderRead(op, out var effective, out _));

            Assert.Equal("SELECT value FROM sys.configurations WHERE name = 'min server memory (MB)';", configured);
            Assert.Equal("SELECT value_in_use FROM sys.configurations WHERE name = 'min server memory (MB)';", effective);

            // The property that matters is that they are not the same query. A refactor that
            // pointed both at one column would restore the exact defect, and would still pass a
            // test that only asserted "the read mentions sys.configurations".
            Assert.NotEqual(configured, effective);
            Assert.DoesNotContain("value_in_use", configured);
        }

        [Fact]
        public void TheConfiguredRead_EscapesAndCharsetGuardsTheOptionName()
        {
            // Same guard the apply render uses: the name is shipped text, and the guard is
            // defence in depth against a crafted overlay template, not against the operator.
            Assert.False(RemediationOpRenderer.TryRenderConfiguredRead("'; DROP DATABASE payroll; --", out _, out var err));
            Assert.Contains("Unsafe or empty", err);

            Assert.False(RemediationOpRenderer.TryRenderConfiguredRead("   ", out _, out _));
            Assert.False(RemediationOpRenderer.TryRenderConfiguredRead((string?)null, out _, out _));
        }

        [Fact]
        public void TheBareNameWrite_RendersAConfigureWithNoAdvancedPrelude()
        {
            // The side-effect restore writes 'show advanced options' itself, which is NOT an
            // advanced option: emitting a prelude in front of it would be the app fighting itself.
            Assert.True(RemediationOpRenderer.TryRenderConfigureWrite("show advanced options", 0, out var sql, out _));
            Assert.Equal("EXEC sp_configure 'show advanced options', 0; RECONFIGURE;", sql);
            Assert.Single(RenderedConfigurationScan.OptionWrites(sql));
        }

        // ── 2.3: capture derives from the RENDERED statement, not the template field ──

        [Fact]
        public void TheScan_SeesEveryOptionTheRealRendererWrites_IncludingThePrelude()
        {
            var op = MinServerMemoryOp();
            Assert.True(RemediationOpRenderer.TryRender(op, 8, out var applySql, out _));

            // Drive the REAL renderer rather than a hand-typed batch: the whole point of item 2.3 is
            // that capture follows the renderer, so a test that types its own SQL would pass even if
            // the renderer changed shape underneath it.
            var touched = RenderedConfigurationScan.OptionNamesTouched(applySql);

            Assert.Equal(new[] { "show advanced options", "min server memory (MB)" }, touched);

            // And the side-effect list is the prelude ONLY, in restore order.
            var side = RenderedConfigurationScan.SideEffectOptions(applySql, op.ConfigName);
            Assert.Equal(new[] { "show advanced options" }, side);
        }

        [Fact]
        public void ANonAdvancedOption_HasNoSideEffectsToRestore()
        {
            var op = NonAdvancedOp();
            Assert.True(RemediationOpRenderer.TryRender(op, 1, out var applySql, out _));

            Assert.Equal(new[] { "show advanced options" }, RenderedConfigurationScan.OptionNamesTouched(applySql));
            // The negative control: its own target is not a "side effect" of itself.
            Assert.Empty(RenderedConfigurationScan.SideEffectOptions(applySql, op.ConfigName));
        }

        [Fact]
        public void SideEffectsComeBackInREVERSERenderOrder_BecauseTheOrderIsLoadBearing()
        {
            // A synthetic three-write batch, because no shipped template renders one yet and the
            // ordering rule must be pinned before one does. Restoring 'show advanced options' to 0
            // BEFORE putting the advanced option back would make the advanced option unsettable.
            const string batch =
                "EXEC sp_configure 'show advanced options', 1; RECONFIGURE; " +
                "EXEC sp_configure 'another prelude', 1; RECONFIGURE; " +
                "EXEC sp_configure 'max degree of parallelism', 4; RECONFIGURE;";

            Assert.Equal(
                new[] { "show advanced options", "another prelude", "max degree of parallelism" },
                RenderedConfigurationScan.OptionNamesTouched(batch));

            Assert.Equal(
                new[] { "another prelude", "show advanced options" },
                RenderedConfigurationScan.SideEffectOptions(batch, "max degree of parallelism"));
        }

        [Fact]
        public void TheScan_ReadsValuesAndIgnoresTheReadFormOfSpConfigure()
        {
            // `sp_configure 'x'` with no value is a READ. Reporting it as a write would make the
            // executor capture and then "restore" an option nothing touched.
            const string batch = "EXEC sp_configure 'show advanced options'; EXEC sp_configure 'maxdop', -1; RECONFIGURE;";
            var writes = RenderedConfigurationScan.OptionWrites(batch);

            Assert.Single(writes);
            Assert.Equal("maxdop", writes[0].OptionName);
            Assert.Equal(-1, writes[0].Value);
        }

        [Fact]
        public void TheScan_HandlesAnEscapedQuoteInAnOptionName_AndEmptyInput()
        {
            var writes = RenderedConfigurationScan.OptionWrites("EXEC sp_configure 'it''s odd', 1; RECONFIGURE;");
            Assert.Single(writes);
            Assert.Equal("it's odd", writes[0].OptionName);

            Assert.Empty(RenderedConfigurationScan.OptionWrites(null));
            Assert.Empty(RenderedConfigurationScan.OptionWrites("   "));
            Assert.Empty(RenderedConfigurationScan.OptionWrites("SELECT value FROM sys.configurations;"));
        }

        // ── Every SHIPPED sp_configure template is scannable: the capture cannot silently
        //    cover nothing, which is how this fix would fail without failing a test. ──

        [Fact]
        public void EveryShippedSpConfigureTemplate_RendersABatchTheScanUnderstands()
        {
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var configTemplates = store.All()
                .Where(t => t.Kind == RemediationKind.Configuration
                            && t.Operation?.OpKind == RemediationOpKind.SpConfigure)
                .ToList();

            Assert.NotEmpty(configTemplates);

            foreach (var t in configTemplates)
            {
                Assert.True(RemediationOpRenderer.TryRender(t.Operation!, t.Operation!.MinValue, out var sql, out var err),
                    $"{t.Key}: {err}");

                var touched = RenderedConfigurationScan.OptionNamesTouched(sql);

                // The template's own option is ALWAYS in the scan. If it were not, the executor
                // would capture nothing for the very option it is about to change.
                Assert.Contains(t.Operation!.ConfigName, touched, System.StringComparer.OrdinalIgnoreCase);

                // And an advanced option always brings the prelude with it, which is the side
                // effect nothing used to restore.
                if (t.Operation!.AdvancedOption)
                    Assert.Contains("show advanced options", touched, System.StringComparer.OrdinalIgnoreCase);
                else
                    Assert.Single(touched);

                Assert.True(RemediationOpRenderer.TryRenderConfiguredRead(t.Operation!, out var cfgRead, out var readErr),
                    $"{t.Key}: {readErr}");
                Assert.Contains("SELECT value FROM sys.configurations", cfgRead);
            }
        }

        // ── 2.2 + the NO ROLLBACK marker: the words an operator reads ──

        [Fact]
        public void AnUnreadPreChangeValue_IsAnHonestNoRollback_NotASilentOne()
        {
            var t = new RemediationTemplate { Key = "MAXDOP", Reversible = true };

            var unread = RemediationRollbackProse.ForConfiguration(t, "max degree of parallelism", null);
            Assert.False(unread.CanRollBack);
            Assert.StartsWith(RemediationRollbackProse.NoRollbackMarker, unread.Sentence);
            Assert.Contains("could not be read", unread.Sentence);

            var known = RemediationRollbackProse.ForConfiguration(t, "max degree of parallelism", 4);
            Assert.True(known.CanRollBack);
            Assert.Contains("set back to 4", known.Sentence);
            Assert.DoesNotContain(RemediationRollbackProse.NoRollbackMarker, known.Sentence);
        }

        [Fact]
        public void AnIrreversibleTemplate_SaysSo_EvenWhenTheValueWasReadFine()
        {
            var t = new RemediationTemplate { Key = "BACKUPDATABASENOW", Reversible = false };
            var r = RemediationRollbackProse.ForConfiguration(t, "whatever", 3);

            Assert.False(r.CanRollBack);
            Assert.StartsWith(RemediationRollbackProse.NoRollbackMarker, r.Sentence);
        }

        [Fact]
        public void ACompoundDatabaseOption_HasNoDerivableInverse_AndTheOperatorIsTold()
        {
            var t = new RemediationTemplate { Key = "DBOPT", Reversible = true };

            var toggle = RemediationRollbackProse.ForDbSetOption(t, "SET DB_CHAINING OFF");
            Assert.True(toggle.CanRollBack);
            Assert.Contains("SET DB_CHAINING ON", toggle.Sentence);

            var compound = RemediationRollbackProse.ForDbSetOption(t, "SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE)");
            Assert.False(compound.CanRollBack);
            Assert.StartsWith(RemediationRollbackProse.NoRollbackMarker, compound.Sentence);

            // ⚠ THE ASSERTION MOVED WITH THE WORDING (lane/remediation-enum-prose-2, 2026-09-02).
            // It used to pin "not a simple ON or OFF toggle" — the MECHANISM clause, which led the
            // sentence. The sentence now leads with the consequence and keeps the mechanism second,
            // so what is asserted is: the operator is told the app cannot undo it, the reason is
            // still stated, and they are told who puts it back.
            Assert.Contains("the app cannot undo this one on its own", compound.Sentence);
            Assert.Contains("switching a single option on or off", compound.Sentence);
            Assert.Contains("you would put it back yourself", compound.Sentence);
        }

        // ── 6d: every post-apply state renders its REASON, not just its name ──

        [Fact]
        public void UnconfirmedAndFailed_RenderTheExecutorsOwnReason()
        {
            const string executorReason =
                "The rollback ran without error. The confirming read then failed, so this server's "
                + "current state is unknown. Read error: Divide by zero error encountered.";

            var unconfirmed = RemediationRollbackProse.DescribeState(
                RemediationRollbackState.Unconfirmed, executorReason, "the old value is back");
            Assert.Contains("could not be confirmed", unconfirmed);
            Assert.Contains("Divide by zero", unconfirmed);

            var failed = RemediationRollbackProse.DescribeState(
                RemediationRollbackState.Failed, "the server did not read back as the pre-change state",
                "the old value is back");
            Assert.Contains("FAILED", failed);
            Assert.Contains("did not read back", failed);

            // A state with nothing to add does not invent a reason, and NotAttempted says nothing
            // at all rather than reassuring the operator about a rollback that never happened.
            Assert.Equal("rolled back, the old value is back",
                RemediationRollbackProse.DescribeState(RemediationRollbackState.Confirmed, null, "the old value is back"));
            Assert.Equal(string.Empty,
                RemediationRollbackProse.DescribeState(RemediationRollbackState.NotAttempted, null, "x"));
        }

        [Fact]
        public void Confirmed_CARRIESItsReasonToo_BecauseThatIsWhereTheCoercionSentenceLives()
        {
            // ⚠ THE FIX-ROUND BLOCKER (gate blocker 3). The Confirmed arm returned
            // "rolled back, {confirmedText}" and dropped RollbackError entirely - and Confirmed is
            // the ONE state this lane's new honesty sentence is written onto. The executor sets, on
            // a CONFIRMED rollback of a coerced option, "The configured value is back at 0. The
            // engine is still using 16, which it was before this change as well (SQL Server coerces
            // this setting or needs a restart)." The operator read the word "confirmed" and was
            // never told the engine was still running the other number: the exact half-truth the
            // whole rollback-leveling item exists to remove, surviving at the last layer.
            const string coercionSentence =
                " The configured value is back at 0. The engine is still using 16, which it was "
                + "before this change as well (SQL Server coerces this setting or needs a restart).";

            var text = RemediationRollbackProse.DescribeState(
                RemediationRollbackState.Confirmed, coercionSentence, "value restored");

            _out.WriteLine(text);
            Assert.StartsWith("rolled back, value restored", text);
            Assert.Contains("The configured value is back at 0", text);
            Assert.Contains("The engine is still using 16", text);

            // A sentinel nobody could produce by accident, so the assertion is about CARRYING the
            // string rather than about any particular wording of it.
            const string sentinel = "SENTINEL-9F3A: the reason reached the operator.";
            Assert.Contains(sentinel,
                RemediationRollbackProse.DescribeState(RemediationRollbackState.Confirmed, sentinel, "value restored"));
        }

        [Fact]
        public void NotAvailable_PrefersTheStatedReasonOverTheGenericSentence()
        {
            var withReason = RemediationRollbackProse.DescribeState(
                RemediationRollbackState.NotAvailable,
                RemediationRollbackProse.NoRollbackMarker + " this fix declares itself not reversible.",
                "x");
            Assert.StartsWith(RemediationRollbackProse.NoRollbackMarker, withReason);

            var without = RemediationRollbackProse.DescribeState(
                RemediationRollbackState.NotAvailable, null, "x");
            Assert.Contains("no rollback was attempted", without);
        }
    }
}
