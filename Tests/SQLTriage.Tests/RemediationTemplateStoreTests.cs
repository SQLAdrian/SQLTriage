/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Build step 3: RemediationTemplate model + store. Proves the shipped MAXDOP
    /// template is registered, well-formed, and self-consistent with the step-1
    /// safety gate — its read-only queries classify as Safe, and its key is the
    /// one SqlSafetyValidator recognises to promote a write to Remediation.
    /// </summary>
    public class RemediationTemplateStoreTests
    {
        private static RemediationTemplateStore NewStore() =>
            new(NullLogger<RemediationTemplateStore>.Instance);

        // ── Shipped template is present with no config file ─────────────────

        [Fact]
        public void Store_SeedsMaxDop_WithoutAnyOverlayFile()
        {
            var store = NewStore();
            Assert.True(store.IsRegistered("MAXDOP"));
            Assert.True(store.Count >= 1);
        }

        [Fact]
        public void MaxDop_IsWellFormed()
        {
            var t = NewStore().TryGet("MAXDOP");
            Assert.NotNull(t);
            Assert.Equal("MAXDOP", t!.Key);
            Assert.Equal("Set-DbaMaxDop", t.DbatoolsCommand);
            Assert.Equal(RemediationRiskClass.Standard, t.RiskClass);
            Assert.True(t.Reversible);
            Assert.False(string.IsNullOrWhiteSpace(t.SnapshotQuery));
            Assert.False(string.IsNullOrWhiteSpace(t.VerifyQuery));
        }

        // ── Self-consistency: a template's own queries must be runnable ─────

        [Fact]
        public void MaxDop_SnapshotAndVerifyQueries_ClassifyAsSafe()
        {
            // The runner reads these on the live connection; if the safety gate
            // would block a template's own read-only queries, the template could
            // never be applied or verified. Pin that they are Safe.
            var t = NewStore().TryGet("MAXDOP")!;
            Assert.Equal(SqlClassification.Safe, SqlSafetyValidator.Classify(t.SnapshotQuery));
            Assert.Equal(SqlClassification.Safe, SqlSafetyValidator.Classify(t.VerifyQuery));
        }

        // ── Cross-component: store key ↔ validator authorisation agree ──────

        [Fact]
        public void EveryRegisteredKey_IsAcceptedByTheSafetyGate()
        {
            // Each registered template's key must actually promote a blocked write
            // to Remediation — otherwise a "registered" template is a dead key the
            // runner could never apply (a shipped-half-built bug). Uses a write the
            // validator blocks so we exercise the promotion path, not the read path.
            const string aBlockedWrite = "EXEC sp_configure 'max degree of parallelism', 4; RECONFIGURE;";
            foreach (var key in NewStore().RegisteredKeys())
            {
                var classification = SqlSafetyValidator.Classify(aBlockedWrite, new RemediationContext(key));
                Assert.Equal(SqlClassification.Remediation, classification);
            }
        }

        // ── Lookups are exact and null-safe ────────────────────────────────

        [Theory]
        [InlineData("maxdop")]   // case-sensitive key; lower-case is not registered
        [InlineData(" MAXDOP ")] // padded
        [InlineData("NOPE")]
        [InlineData("")]
        [InlineData(null)]
        public void UnregisteredOrMalformedKey_IsNotRegistered(string? key)
        {
            var store = NewStore();
            Assert.False(store.IsRegistered(key!));
            Assert.Null(store.TryGet(key!));
        }

        [Fact]
        public void All_ReturnsTheSeededTemplate()
        {
            var all = NewStore().All();
            Assert.Contains(all, t => t.Key == "MAXDOP");
        }

        // ── Add-missing-index template + the overlay Kind/OpKind divergence guard ──

        [Fact]
        public void AddMissingIndex_IsSeeded_AsTransactableCreateIndex()
        {
            var t = NewStore().TryGet("ADDMISSINGINDEX");
            Assert.NotNull(t);
            Assert.Equal(RemediationKind.Transactable, t!.Kind);
            Assert.NotNull(t.Operation);
            Assert.Equal(RemediationOpKind.CreateIndex, t.Operation!.OpKind);
        }

        private static RemediationTemplateStore StoreWithOverlay(string overlayJson, out string tempPath)
        {
            tempPath = Path.Combine(Path.GetTempPath(), "rem-overlay-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(tempPath, overlayJson);
            return new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance, tempPath);
        }

        [Fact]
        public void Overlay_RejectsConfigurationKind_CarryingCreateIndexOp()
        {
            // The HIGH adversarial finding: a Config/-writable overlay entry with kind=Configuration
            // (passes the kind check) but operation.opKind=CreateIndex on a shipped ConfigName must
            // NOT be admitted — otherwise it would route to the CreateIndex executor. The guard now
            // pins OpKind==SpConfigure too.
            const string evil = @"{""schemaVersion"":1,""templates"":[
                {""key"":""EVILIDX"",""kind"":""Configuration"",""reversible"":true,
                 ""operation"":{""opKind"":""CreateIndex"",""configName"":""max degree of parallelism""}}]}";
            var store = StoreWithOverlay(evil, out var path);
            try { Assert.False(store.IsRegistered("EVILIDX")); }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void Overlay_AcceptsConfiguration_SpConfigure_OnShippedSetting()
        {
            // The legitimate overlay shape still works: a Configuration/SpConfigure op on a setting
            // a shipped template already remediates.
            const string ok = @"{""schemaVersion"":1,""templates"":[
                {""key"":""EXTRAMAXDOP"",""kind"":""Configuration"",""reversible"":true,
                 ""operation"":{""opKind"":""SpConfigure"",""configName"":""max degree of parallelism"",""valueParam"":""MaxDop"",""minValue"":0,""maxValue"":64}}]}";
            var store = StoreWithOverlay(ok, out var path);
            try { Assert.True(store.IsRegistered("EXTRAMAXDOP")); }
            finally { try { File.Delete(path); } catch { } }
        }
    }
}
