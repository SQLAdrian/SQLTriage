/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// remediation-r1-02 (honesty hunt, 2026-08-25): the Maintenance Solution install preview and
    /// the apply gate measured DIFFERENT object sets. Preview asked
    /// <c>MaintenanceSolutionOpRenderer.ProcsExistProbe</c> (4 proc names); the apply gate required
    /// those 4 PLUS dbo.CommandLog. On a database with the procs and no CommandLog, preview said
    /// "apply would be a no-op" and apply then re-ran the 490 KB script over the operator's existing
    /// procedures. The script creates a stub and ALTERs unconditionally, so those procedures are
    /// overwritten, and <c>RenderUninstallSql</c> deliberately never drops a pre-existing name, so
    /// the overwrite has no undo. The hunt proved that exact divergence live on .\new2022 with a
    /// scratch database.
    ///
    /// <para>Both sides now read the same provenance snapshot and classify it with the same pure
    /// function, and the partially-present case is refused rather than silently overwritten.</para>
    /// </summary>
    public class RemediationMaintenanceInstallParityTests
    {
        private static HashSet<string> Present(params string[] names) => new(names);

        // ── One object set, shared by preview, the NoOp gate and verify ──────

        [Fact]
        public void TheInstalledObjectSet_IsTheFourProcsPlusCommandLog()
        {
            Assert.Equal(5, MaintenanceSolutionOpRenderer.AllInstalledObjectNames.Count);
            foreach (var proc in MaintenanceSolutionOpRenderer.CoreProcNames)
                Assert.Contains(proc, MaintenanceSolutionOpRenderer.AllInstalledObjectNames);
            Assert.Contains(MaintenanceSolutionOpRenderer.CommandLogTableName,
                MaintenanceSolutionOpRenderer.AllInstalledObjectNames);
        }

        [Fact]
        public void NothingPresent_IsAbsent()
        {
            Assert.Equal(MaintenanceSolutionOpRenderer.InstallPresence.Absent,
                MaintenanceSolutionOpRenderer.Classify(Present()));
        }

        [Fact]
        public void AllFivePresent_IsFullyPresent_SoApplyIsANoOp()
        {
            var all = Present(MaintenanceSolutionOpRenderer.AllInstalledObjectNames.ToArray());
            Assert.Equal(MaintenanceSolutionOpRenderer.InstallPresence.FullyPresent,
                MaintenanceSolutionOpRenderer.Classify(all));
        }

        [Fact]
        public void TheFourProcsWithoutCommandLog_IsPartiallyPresent_NotANoOp()
        {
            // THE defect shape. Preview called this "already installed"; the apply gate did not.
            var fourProcs = Present(MaintenanceSolutionOpRenderer.CoreProcNames.ToArray());

            Assert.Equal(MaintenanceSolutionOpRenderer.InstallPresence.PartiallyPresent,
                MaintenanceSolutionOpRenderer.Classify(fourProcs));
            Assert.NotEqual(MaintenanceSolutionOpRenderer.InstallPresence.FullyPresent,
                MaintenanceSolutionOpRenderer.Classify(fourProcs));
        }

        [Fact]
        public void OneStrangerProcSharingAName_IsPartiallyPresent_AndIsNeverOverwritten()
        {
            // An operator's own dbo.DatabaseBackup. Installing would ALTER it out of existence with
            // no rollback, because the uninstall never drops a name it did not create.
            var stranger = Present("DatabaseBackup");
            Assert.Equal(MaintenanceSolutionOpRenderer.InstallPresence.PartiallyPresent,
                MaintenanceSolutionOpRenderer.Classify(stranger));
        }

        [Fact]
        public void CommandLogAlone_IsPartiallyPresent()
        {
            Assert.Equal(MaintenanceSolutionOpRenderer.InstallPresence.PartiallyPresent,
                MaintenanceSolutionOpRenderer.Classify(Present(MaintenanceSolutionOpRenderer.CommandLogTableName)));
        }

        // ── The refusal names what is in the way and what it would cost ──────

        [Fact]
        public void TheRefusal_NamesWhatExists_WhatIsMissing_AndThatThereIsNoUndo()
        {
            var fourProcs = Present(MaintenanceSolutionOpRenderer.CoreProcNames.ToArray());
            var text = MaintenanceSolutionOpRenderer.DescribePartialInstallRefusal(fourProcs);

            foreach (var proc in MaintenanceSolutionOpRenderer.CoreProcNames)
                Assert.Contains(proc, text);
            Assert.Contains(MaintenanceSolutionOpRenderer.CommandLogTableName, text);
            Assert.Contains("overwrite", text);
            Assert.Contains("cannot be undone", text);
            Assert.DoesNotContain("—", text); // house style: no em-dashes in operator copy
        }

        // ── Rollback still never touches a pre-existing name ─────────────────

        [Fact]
        public void UninstallStillSkipsEveryPreExistingName()
        {
            // Unchanged behaviour, pinned here because the refusal above is the ONLY thing standing
            // between a partial install and an unrecoverable overwrite. If someone ever relaxes the
            // refusal, this test says out loud what rollback will and will not clean up.
            var fourProcs = Present(MaintenanceSolutionOpRenderer.CoreProcNames.ToArray());
            var sql = MaintenanceSolutionOpRenderer.RenderUninstallSql(fourProcs);

            foreach (var proc in MaintenanceSolutionOpRenderer.CoreProcNames)
                Assert.DoesNotContain($"DROP PROCEDURE dbo.{proc}", sql);
            Assert.Contains("DROP TABLE dbo.CommandLog", sql);
        }
    }
}
