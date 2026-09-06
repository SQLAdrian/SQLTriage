/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// remediation-r1-03 + remediation-r2-02 (honesty hunt, 2026-08-25): the change-control PREVIEW
    /// lane dropped every per-batch SqlException into a logger warning. <c>InstanceChangeControlResult</c>
    /// carried no Succeeded/BatchErrors at all, the export printed the benign "No change-control
    /// rows were captured.", and the audit ledger write was hardcoded true. The apply lane was fixed
    /// for this exact shape in the 2026-08-20 D1/D2 round; preview was left behind. The hunt proved
    /// it live against .\new2022 with a crafted script: Refused=False, RefusalReason=null,
    /// Rows.Count=0, and a clean-looking export.
    ///
    /// <para>The /remediation hardening panel rode the same mechanism and rendered a green tick
    /// ("Already at the conservative hardening baseline") over an all-error run, with the ERROR
    /// lines reachable only inside a details pane that is collapsed by default.</para>
    ///
    /// <para>Preview now carries BatchErrors and Succeeded with the same shape and the same
    /// definition as apply, and BOTH the multi-instance page and the hardening panel read the one
    /// shared <see cref="ServerConfigScriptService.PreviewIsReadable"/> rule.</para>
    /// </summary>
    public class ServerConfigPreviewBatchErrorTests
    {
        private static ServerConfigScriptService.ConfigCheckRow Row(string mode = "PLANNED") =>
            new(1, DateTime.UtcNow, mode, "Section", "Setting", "0", "1", "detail");

        private static ServerConfigScriptService.InstanceChangeControlResult Preview(
            IReadOnlyList<ServerConfigScriptService.ConfigCheckRow> rows,
            IReadOnlyList<string>? batchErrors = null,
            bool refused = false) =>
            new("SQL01", refused, refused ? "reason" : null, rows, "C:\\out\\x.txt", batchErrors);

        // ── The preview record can now express failure at all ────────────────

        [Fact]
        public void ARunThatCapturedRowsWithNoBatchErrors_Succeeded()
        {
            Assert.True(Preview(new[] { Row() }).Succeeded);
        }

        [Fact]
        public void ARunWhereEveryBatchFailed_DidNotSucceed_EvenThoughItWasNotRefused()
        {
            // The exact live-proved shape: not refused, no refusal reason, zero rows.
            var result = Preview(Array.Empty<ServerConfigScriptService.ConfigCheckRow>(),
                new[] { "[batch 7] ERROR 208: Invalid object name 'x'." });

            Assert.False(result.Refused);
            Assert.Null(result.RefusalReason);
            Assert.False(result.Succeeded);
            Assert.NotEmpty(result.BatchErrors!);
        }

        [Fact]
        public void ARunThatReachedTheServerAndCapturedNothing_DidNotSucceed()
        {
            // No batch error recorded, still zero rows. The script reports SOMETHING for a server it
            // can read, so this is a read that did not happen, not a clean server.
            Assert.False(Preview(Array.Empty<ServerConfigScriptService.ConfigCheckRow>()).Succeeded);
        }

        [Fact]
        public void ARunWithRowsButAlsoABatchError_DidNotSucceed()
        {
            // Partial capture is still a partial read. A verdict drawn from it would be drawn from
            // an unknown fraction of the server.
            Assert.False(Preview(new[] { Row() }, new[] { "[batch 3] ERROR 229: permission denied." }).Succeeded);
        }

        [Fact]
        public void ARefusedRun_DidNotSucceed()
        {
            Assert.False(Preview(new[] { Row() }, refused: true).Succeeded);
        }

        // ── One rule, shared by the service and the hardening panel ──────────

        [Fact]
        public void PreviewIsReadable_IsFalseForEveryUnreadableShape()
        {
            var rows = new[] { Row() };
            var errors = new[] { "boom" };

            Assert.False(ServerConfigScriptService.PreviewIsReadable(null, null));
            Assert.False(ServerConfigScriptService.PreviewIsReadable(Array.Empty<ServerConfigScriptService.ConfigCheckRow>(), null));
            Assert.False(ServerConfigScriptService.PreviewIsReadable(rows, errors));
            Assert.False(ServerConfigScriptService.PreviewIsReadable(null, errors));
        }

        [Fact]
        public void PreviewIsReadable_IsTrueOnlyWhenRowsWereCapturedCleanly()
        {
            Assert.True(ServerConfigScriptService.PreviewIsReadable(new[] { Row() }, null));
            Assert.True(ServerConfigScriptService.PreviewIsReadable(new[] { Row() }, new List<string>()));
        }

        // ── The sentence printed when the verdict is withheld ────────────────

        [Fact]
        public void ARunThatCapturedRowsAndAlsoFailed_IsNotDescribedAsNotHavingReadTheServer()
        {
            // The panel printed one sentence for every unreadable shape: "This preview did not read
            // SQL01." A run with rows AND a batch error read the server, partially. Withholding the
            // verdict is right; saying nothing was read is a second false statement on top of it.
            var sentence = ServerConfigScriptService.DescribeUnreadablePreview(
                new[] { Row() }, new[] { "[batch 3] ERROR 229: permission denied." }, "SQL01");

            Assert.DoesNotContain("did not read", sentence, StringComparison.Ordinal);
            Assert.Contains("read SQL01", sentence, StringComparison.Ordinal);
            Assert.Contains("One batch failed", sentence, StringComparison.Ordinal);
            Assert.Contains("no hardening verdict is shown", sentence, StringComparison.Ordinal);
        }

        [Fact]
        public void ARunThatCapturedNothingAndFailed_SaysSo_AndCountsTheFailures()
        {
            var sentence = ServerConfigScriptService.DescribeUnreadablePreview(
                Array.Empty<ServerConfigScriptService.ConfigCheckRow>(),
                new[] { "[batch 2] ERROR 208", "[batch 3] ERROR 50000" }, "SQL01");

            Assert.Contains("captured nothing from SQL01", sentence, StringComparison.Ordinal);
            Assert.Contains("2 batches failed", sentence, StringComparison.Ordinal);
        }

        [Fact]
        public void ARunThatReachedTheServerCleanlyAndCapturedNothing_ClaimsNoFailure()
        {
            // No batch raised. Claiming a failure here would be the same overclaim pointing the
            // other way.
            var sentence = ServerConfigScriptService.DescribeUnreadablePreview(
                Array.Empty<ServerConfigScriptService.ConfigCheckRow>(), null, "SQL01");

            Assert.Contains("reached SQL01", sentence, StringComparison.Ordinal);
            Assert.Contains("captured no settings", sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("failed", sentence, StringComparison.Ordinal);
        }

        [Fact]
        public void EveryUnreadableShape_HasASentence_AndNoneOfThemCarriesAnEmDash()
        {
            // dev/VOICE_GUIDE.md: no em-dashes in client-facing text.
            var rows = new[] { Row() };
            var empty = Array.Empty<ServerConfigScriptService.ConfigCheckRow>();
            var errors = new[] { "boom" };

            foreach (var sentence in new[]
                     {
                         ServerConfigScriptService.DescribeUnreadablePreview(rows, errors, "SQL01"),
                         ServerConfigScriptService.DescribeUnreadablePreview(empty, errors, "SQL01"),
                         ServerConfigScriptService.DescribeUnreadablePreview(empty, null, "SQL01"),
                         ServerConfigScriptService.DescribeUnreadablePreview(null, null, "SQL01"),
                     })
            {
                Assert.False(string.IsNullOrWhiteSpace(sentence));
                Assert.DoesNotContain('—', sentence); // em-dash
            }
        }

        [Fact]
        public void ThePreviewAndApplyLanes_AgreeOnWhatSuccessMeans()
        {
            // Same inputs, same verdict. The two records are separate types, so nothing but a test
            // stops them drifting apart again.
            var rows = new[] { Row() };
            var errors = new[] { "[batch 2] ERROR 208" };

            var preview = new ServerConfigScriptService.InstanceChangeControlResult("SQL01", false, null, rows, null, errors);
            var apply = new ServerConfigScriptService.InstanceApplyResult("SQL01", false, null, rows, null, errors);
            Assert.Equal(apply.Succeeded, preview.Succeeded);

            var cleanPreview = new ServerConfigScriptService.InstanceChangeControlResult("SQL01", false, null, rows, null, null);
            var cleanApply = new ServerConfigScriptService.InstanceApplyResult("SQL01", false, null, rows, null, null);
            Assert.Equal(cleanApply.Succeeded, cleanPreview.Succeeded);

            var emptyPreview = new ServerConfigScriptService.InstanceChangeControlResult(
                "SQL01", false, null, Array.Empty<ServerConfigScriptService.ConfigCheckRow>(), null, null);
            var emptyApply = new ServerConfigScriptService.InstanceApplyResult(
                "SQL01", false, null, Array.Empty<ServerConfigScriptService.ConfigCheckRow>(), null, null);
            Assert.Equal(emptyApply.Succeeded, emptyPreview.Succeeded);
        }
    }
}
