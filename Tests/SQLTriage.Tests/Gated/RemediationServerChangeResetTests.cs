/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests.Gated
{
    /// <summary>
    /// SEC-1 (DECISIONS 2026-08-25 23:01), the behavioural half. A target-server change must re-arm
    /// every per-server destructive acknowledgement on /remediation, so a "this overwrites the
    /// existing objects and is not reversible" tick made for server A cannot ride
    /// AcknowledgeOverwrite to server B on the next Apply.
    ///
    /// <para><b>Why this is in Gated/.</b> The test drives the page by reflection, which reaches
    /// SQLTriage.Pages.Remediation at RUN time. buildprofile.targets Content-Removes that page (and
    /// the AgentJob and RemediationLab pages) from a COMMUNITY build, so the type is not in the
    /// community assembly and the reflection would throw there. The ProfileGatedTestSyncTests lint
    /// strips string literals, so it cannot see a type reached only through a string; without this
    /// folder the test would compile into the community suite and fail at run time. The Gated\**\*.cs
    /// glob in SQLTriage.Tests.csproj removes the whole folder from the community build, so this runs
    /// only where the page exists (full / dev). See Gated/README.md.</para>
    /// </summary>
    public sealed class RemediationServerChangeResetTests
    {
        [Fact]
        public void AServerChange_ResetsEveryDestructiveAcknowledgement()
        {
            var page = NewRemediationPage(out var t);

            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
            void Tick(string field) => t.GetField(field, F)!.SetValue(page, true);
            bool Read(string field) => (bool)t.GetField(field, F)!.GetValue(page)!;
            Dictionary<string, string> MaintParams() =>
                (Dictionary<string, string>)t.GetMethod("MaintInstallParams", F)!.Invoke(page, null)!;

            // The operator ticked all three destructive acknowledgements for the OLD server.
            Tick("_maintForceAcknowledged");
            Tick("_backupNowConfirm");
            Tick("_checkDbNowConfirm");
            // Sanity: while it is ticked, the overwrite acknowledgement really does travel on the request.
            Assert.True(MaintParams().ContainsKey(MaintenanceSolutionOpRenderer.AcknowledgeOverwriteParam));

            // Switch target server: OnServerChanged calls this no-DI reset (wiring is checked in
            // RemediationPolishRulingTests.OnServerChanged_DelegatesToTheReset).
            t.GetMethod("ResetTransientStateForServerChange", F)!.Invoke(page, null);

            // Fail closed: nothing ticked for server A can reach server B, and the button re-disables
            // because its gate reads _maintForceAcknowledged.
            Assert.False(Read("_maintForceAcknowledged"));
            Assert.False(Read("_backupNowConfirm"));
            Assert.False(Read("_checkDbNowConfirm"));
            Assert.Empty(MaintParams());
        }

        [Fact]
        public void AServerChange_DropsTheHardeningPreview()
        {
            // Gate residual (DECISIONS 2026-08-25). The Server Configuration & Hardening preview is
            // per-server: RunPreviewAsync targets the connected server with no server argument. When
            // the reset skipped it, a switch left server A's gap rows on screen under server B's name
            // — or its green "already at the baseline" banner — a stale compliance claim. The reset
            // must drop the rows, the log, and the batch errors.
            var page = NewRemediationPage(out var t);
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;

            var rowsField = t.GetField("_hardeningRows", F)!;
            var rowType = rowsField.FieldType.GetGenericArguments()[0]; // IReadOnlyList<ConfigCheckRow> -> ConfigCheckRow
            rowsField.SetValue(page, Array.CreateInstance(rowType, 0)); // a non-null preview for server A

            var batchErrors = (System.Collections.IList)t.GetField("_hardeningBatchErrors", F)!.GetValue(page)!;
            batchErrors.Add("server A batch error");
            var log = (System.Collections.IList)t.GetField("_hardeningLog", F)!.GetValue(page)!;
            log.Add(("server A log line", false));

            Assert.NotNull(rowsField.GetValue(page)); // sanity: a preview is present before the switch

            t.GetMethod("ResetTransientStateForServerChange", F)!.Invoke(page, null);

            Assert.Null(rowsField.GetValue(page)); // no stale verdict carries to the new server
            Assert.Empty(batchErrors);
            Assert.Empty(log);
        }

        /// <summary>Instantiates the /remediation component for a reflection test. Its collections are
        /// field-initialised, so the reset method runs without the Blazor DI/render lifecycle.</summary>
        private static object NewRemediationPage(out Type type)
        {
            var asm = typeof(RemediationRunner).Assembly;
            type = asm.GetType("SQLTriage.Pages.Remediation")
                   ?? asm.GetTypes().Single(x => x.Name == "Remediation" && x.Namespace == "SQLTriage.Pages");
            return Activator.CreateInstance(type)!;
        }
    }
}
