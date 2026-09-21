/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationExecGuardTests — Phase-2 item 2.4, ROUTE A (ruled 2026-09-01).
 *
 * This file re-runs spike S2 section 6's live matrix as a battery, with the EXPECTED verdicts per
 * row rather than the observed ones, so the two holes it measured cannot come back:
 *
 *   HOLE 1 - SqlSafetyValidator had no pattern for xp_instance_regwrite at all, so Validate
 *            returned IsSafe = TRUE and Classify returned Safe. Safe is the classification for
 *            read-only SQL that may run on the ordinary read path with NO remediation gate. A
 *            registry WRITE was reading as a diagnostic READ.
 *   HOLE 2 - the wall's batch waiver defeated the one pattern that did exist: prefixing
 *            `SELECT 1 FROM sys.databases;` made plain xp_regwrite classify Safe. Any rendered
 *            inverse doing a before/after capture would have carried that waiver with it.
 *
 * And it pins the wiring itself: the guard is now ON the remediation path (it was invoked at four
 * production call sites, none of them under Data/Services/Remediation, at both spike SHAs), and
 * every SHIPPED template still renders, classifies and registers - the wiring must break nothing
 * that ships, which is the failure mode a security tightening usually has.
 *
 * NO SERVER IS TOUCHED. Every assertion here is text through two classifiers.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class RemediationExecGuardTests
    {
        private readonly ITestOutputHelper _out;
        public RemediationExecGuardTests(ITestOutputHelper output) => _out = output;

        private const string InstanceRegWrite =
            "EXEC master.sys.xp_instance_regwrite N'HKEY_LOCAL_MACHINE', N'Software\\Microsoft\\MSSQLServer\\MSSQLServer', N'NumErrorLogs', REG_DWORD, 6;";
        private const string PlainRegWrite =
            "EXEC master.sys.xp_regwrite N'HKEY_LOCAL_MACHINE', N'Software\\Test', N'V', REG_DWORD, 1;";
        private const string InstanceRegRead =
            "EXEC master.sys.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'Software\\Microsoft\\MSSQLServer\\MSSQLServer', N'AuditLevel';";
        private const string PlainRegRead =
            "EXEC master.sys.xp_regread N'HKEY_LOCAL_MACHINE', N'Software\\Test', N'V';";
        private const string SysPrefix = "SELECT 1 FROM sys.databases; ";

        // ── THE MATRIX. Every row is a spike S2 section 6 row, with the verdict it must produce. ──

        [Theory]
        // input                                    guardAllows  strictValidateSafe
        [InlineData(InstanceRegWrite,               false,       false)]  // hole 1: was Safe
        [InlineData(PlainRegWrite,                  false,       false)]
        [InlineData(SysPrefix + InstanceRegWrite,   false,       false)]  // hole 2: was Safe
        [InlineData(SysPrefix + PlainRegWrite,      false,       false)]  // hole 2: was Safe
        [InlineData(InstanceRegRead,                true,        true)]   // a pure READ stays allowed
        [InlineData(PlainRegRead,                   true,        true)]
        [InlineData(SysPrefix + InstanceRegRead,    true,        true)]
        public void TheRegistryMatrix_ProducesTheRuledVerdicts(string sql, bool guardAllows, bool strictSafe)
        {
            var allowed = RemediationExecGuard.Allows(sql, RemediationExecGuard.RenderedFix, out var refusal);
            var strict = SqlSafetyValidator.ValidateStrict(sql).IsSafe;

            _out.WriteLine($"guardAllows={allowed} strictSafe={strict} :: {sql}");

            Assert.Equal(guardAllows, allowed);
            Assert.Equal(strictSafe, strict);

            if (!allowed)
            {
                // A refusal an operator cannot act on is a refusal they will work around.
                Assert.Contains("Refused before anything ran", refusal);
                Assert.Contains("Nothing was changed and nothing was charged", refusal);
                Assert.Contains("Registry", refusal, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.Equal(string.Empty, refusal);
            }
        }

        [Fact]
        public void AWriteIsNeverClassifiedSafeAgain_WithOrWithoutAContext()
        {
            // The exact defect, stated as the classification it produced. Without a registered
            // context a registry write is Blocked; with one it is Remediation, which is a write
            // that may run ONLY through the runner's gates - and the runner's gate now inspects it
            // with DangerousExecGuard and refuses. What it must never be again is Safe.
            foreach (var sql in new[] { InstanceRegWrite, PlainRegWrite, SysPrefix + InstanceRegWrite, SysPrefix + PlainRegWrite })
            {
                Assert.NotEqual(SqlClassification.Safe, SqlSafetyValidator.Classify(sql));
                Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(sql));
                Assert.NotEqual(SqlClassification.Safe,
                    SqlSafetyValidator.Classify(sql, new RemediationContext("MAXDOP")));
            }

            // The negative control: a pure registry READ is still Safe, and must be. A shipped
            // read-only telemetry panel calls xp_instance_regread to display host configuration.
            Assert.Equal(SqlClassification.Safe, SqlSafetyValidator.Classify(InstanceRegRead));
            Assert.Equal(SqlClassification.Safe, SqlSafetyValidator.Classify(PlainRegRead));
        }

        [Fact]
        public void TheReadPathWallIsDELIBERATELYUnchanged_BecauseShippedDiagnosticsRelyOnTheWaiver()
        {
            // ⚠ THIS IS NOT AN OVERSIGHT, IT IS THE DESIGN, and it is asserted so nobody "fixes" it.
            // Validate() is the shared read-path wall, and it is batch-level by necessity: sp_Blitz
            // and usp_bpcheck genuinely execute blocked statements, and scripts/Check_BP_Servers.sql
            // and scripts/stpChecklist_Seguranca.sql really do carry registry writes beside sys.*
            // reads. Making the shared wall strict blocks them - EveryShippedScript_StillPassesValidate
            // is the test that would go red. The strictness lives on the REMEDIATION path only.
            Assert.True(SqlSafetyValidator.Validate(SysPrefix + PlainRegWrite).IsSafe);
            Assert.False(SqlSafetyValidator.ValidateStrict(SysPrefix + PlainRegWrite).IsSafe);

            // Without the waiver present, even the lenient wall blocks it now (hole 1 closed for
            // both walls - this is the half that needed a PATTERN, not a mode).
            Assert.False(SqlSafetyValidator.Validate(InstanceRegWrite).IsSafe);
        }

        [Fact]
        public void TheGuardIsPerStatement_SoNoLeadingBenignStatementShieldsATrailingWrite()
        {
            // The property the shared wall structurally cannot provide, asserted directly rather
            // than inferred from the matrix rows.
            Assert.False(RemediationExecGuard.Allows(
                "SELECT 1 FROM sys.databases; SELECT 2 FROM sys.tables; " + PlainRegWrite,
                RemediationExecGuard.RenderedFix, out _));

            // And the converse: a batch of pure reads is allowed however long it is.
            Assert.True(RemediationExecGuard.Allows(
                "SELECT 1 FROM sys.databases; " + InstanceRegRead + " " + PlainRegRead,
                RemediationExecGuard.RenderedFix, out _));
        }

        // ── THE WIRING MUST BREAK NOTHING THAT SHIPS ──

        [Fact]
        public void EveryShippedTemplate_StillRendersClassifiesAndPassesTheGuard()
        {
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var all = store.All().ToList();
            Assert.NotEmpty(all);

            var failures = new List<string>();
            foreach (var t in all)
            {
                if (t.Operation is null) { failures.Add($"{t.Key}: no structured operation"); continue; }

                if (!RemediationOpRenderer.TryRenderForClassification(t.Operation, out var sql, out var err))
                {
                    failures.Add($"{t.Key}: render failed - {err}");
                    continue;
                }

                if (!RemediationExecGuard.Allows(sql, RemediationExecGuard.RenderedFix, out var guardErr))
                {
                    failures.Add($"{t.Key}: GUARD BLOCKED the shipped rendering - {guardErr}");
                    continue;
                }

                var classification = SqlSafetyValidator.Classify(sql, new RemediationContext(t.Key), store.RegisteredKeys());
                if (classification != SqlClassification.Remediation)
                    failures.Add($"{t.Key}: classified {classification}, expected Remediation");
            }

            _out.WriteLine($"shipped templates inspected: {all.Count}");
            Assert.True(failures.Count == 0,
                "Route A wiring broke shipped templates:\n" + string.Join("\n", failures));
        }

        [Fact]
        public void TheInverseOfEveryShippedSpConfigureTemplate_AlsoPassesTheGuard()
        {
            // The brief's requirement in one assertion: BOTH the rendered fix and any rendered
            // inverse go through the guard. The inverse of an sp_configure fix is the same render
            // with the captured value, so if any shipped option were a blocked toggle
            // (xp_cmdshell, ole automation, clr enabled, clr strict security) the inverse would be
            // refused and the fix would be one-way. None are, and this says so by measurement.
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            foreach (var t in store.All().Where(t => t.Operation?.OpKind == RemediationOpKind.SpConfigure))
            {
                foreach (var v in new[] { t.Operation!.MinValue, t.Operation!.MaxValue })
                {
                    Assert.True(RemediationOpRenderer.TryRender(t.Operation!, v, out var sql, out var e), $"{t.Key}: {e}");
                    Assert.True(RemediationExecGuard.Allows(sql, RemediationExecGuard.RenderedInverse, out var g),
                        $"{t.Key} inverse at {v} was refused: {g}");
                }
            }
        }

        [Fact]
        public void TheGateRefusesATemplateWhoseRenderingTheGuardBlocks()
        {
            // The wiring, exercised on the rendering the gate classifies rather than asserted from
            // the source. A template that toggles 'clr enabled' renders fine and classifies as a
            // write, and is refused by the SECOND wall - which is exactly what Route A buys.
            //
            // ⚠ 'clr enabled' rather than 'xp_cmdshell' for a measured reason, not a preference:
            // RemediationOpRenderer.SafeConfigName admits letters, digits, spaces and parens only,
            // so 'xp_cmdshell' fails the CHARSET guard before the exec guard ever sees it. That is
            // a real second layer, and it is asserted below rather than relied on silently.
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var blockedOp = new RemediationOperation
            {
                OpKind = RemediationOpKind.SpConfigure,
                ConfigName = "clr enabled",
                AdvancedOption = true,
                ValueParam = "Enabled",
                MinValue = 0,
                MaxValue = 1,
            };

            Assert.True(RemediationOpRenderer.TryRenderForClassification(blockedOp, out var sql, out _));
            Assert.False(RemediationExecGuard.Allows(sql, RemediationExecGuard.RenderedFix, out var refusal));
            Assert.Contains("clr enabled", refusal);

            // The charset guard, stated as its own fact: an underscore-bearing proc name cannot
            // even be rendered, so xp_cmdshell never reaches the exec guard from this shape.
            var underscored = new RemediationOperation
            {
                OpKind = RemediationOpKind.SpConfigure, ConfigName = "xp_cmdshell",
                ValueParam = "Enabled", MinValue = 0, MaxValue = 1,
            };
            Assert.False(RemediationOpRenderer.TryRenderForClassification(underscored, out _, out var charsetErr));
            Assert.Contains("Unsafe or empty sp_configure option name", charsetErr);

            // The negative control on the same shape: the real MAXDOP option passes.
            var ok = store.TryGet("MAXDOP");
            Assert.NotNull(ok);
            Assert.True(RemediationOpRenderer.TryRenderForClassification(ok!.Operation!, out var okSql, out _));
            Assert.True(RemediationExecGuard.Allows(okSql, RemediationExecGuard.RenderedFix, out _));
        }

        [Fact]
        public async System.Threading.Tasks.Task TheRUNNERSGateRefusesIt_WhichIsWhereTheWiringLives()
        {
            // ⚠ THE WIRING TEST. Every other assertion in this file exercises the guard as a
            // function; spike S2 proved the guard was CORRECT and simply never called on this path
            // (zero hits under Data/Services/Remediation at both pinned SHAs). So the property that
            // actually had to change is "the runner's gate calls it", and this is the test that
            // goes red if somebody removes that call.
            //
            // A 'clr enabled' fix is promoted through the REAL corpus path - zero app-code change,
            // the same route any corpus check takes - and the runner must refuse it at gate 1,
            // before any preview, on the guard's reason. It classifies as Remediation under a
            // registered key, so the FIRST wall lets it through: only the second one stops it.
            var scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guard-wiring-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(scratch);
            try
            {
                var templates = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
                templates.LoadCorpusTemplates(new[]
                {
                    new SQLTriage.Data.Models.SqlCheck
                    {
                        Id = "SQLT-GUARDWIRE-CLR",
                        Name = "CLR enabled (guard wiring fixture)",
                        Remediation = new SQLTriage.Data.Models.CheckRemediation
                        {
                            AutoFixable = true, RiskClass = "standard", Reversible = true,
                            Operation = new SQLTriage.Data.Models.CheckRemediationOperation
                            {
                                OpKind = "sp_configure", ConfigName = "clr enabled",
                                AdvancedOption = true, ValueParam = "Enabled", MinValue = 0, MaxValue = 1,
                            },
                        },
                    },
                });
                Assert.True(templates.IsRegistered("SQLT-GUARDWIRE-CLR"),
                    "the fixture did not register, so this test would prove nothing.");

                // The FIRST wall would authorise it: a registered key over a write classifies as
                // Remediation. Stated here so the refusal below cannot be mistaken for the
                // validator doing the work.
                Assert.True(RemediationOpRenderer.TryRenderForClassification(
                    templates.TryGet("SQLT-GUARDWIRE-CLR")!.Operation!, out var rendered, out _));
                Assert.Equal(SqlClassification.Remediation,
                    SqlSafetyValidator.Classify(rendered, new RemediationContext("SQLT-GUARDWIRE-CLR"), templates.RegisteredKeys()));

                var audit = new AuditLogService(System.IO.Path.Combine(scratch, "audit"), startFlushTimer: false);
                var bundle = new SQLTriage.Tests.Licensing.FakeBundleAccessor
                {
                    IsUnlocked = true,
                    Tier = SQLTriage.Data.Services.Licensing.Tier.Full,
                    Features = new SQLTriage.Data.Services.Licensing.BundleFeatures(
                        RagEnabled: false, SpBlitzImport: false, FullCorpus: false,
                        PermittedCheckIds: Array.Empty<int>(),
                        Remediation: true, RemediationCreditsPerServer: 50),
                };
                var credits = new PersistedRemediationCreditLedger(
                    bundle, NullLogger<PersistedRemediationCreditLedger>.Instance, null,
                    System.IO.Path.Combine(scratch, "ledger.json"));
                var runner = new RemediationRunner(templates,
                    new BundleBackedRemediationCapability(bundle), credits,
                    new NeverReachedExecutor(), audit, NullLogger<RemediationRunner>.Instance);

                var proposal = await runner.ProposeAsync("SQLT-GUARDWIRE-CLR", "GUARDWIRE-NO-SUCH-SERVER");
                _out.WriteLine(proposal.Message);
                Assert.True(proposal.IsRefused);
                Assert.Contains("clr enabled", proposal.Message);
                Assert.Contains("Refused before anything ran", proposal.Message);

                var applied = await runner.ApplyAsync("SQLT-GUARDWIRE-CLR", "GUARDWIRE-NO-SUCH-SERVER",
                    approved: true, approvedBy: "test",
                    parameters: new Dictionary<string, string> { ["Enabled"] = "1" });
                Assert.True(applied.IsRefused);
                Assert.Contains("Refused before anything ran", applied.Message);

                // The negative control on the same runner: a shipped fix still gets through the
                // gate and reaches the executor, so the guard is not simply refusing everything.
                var ok = await runner.ApplyAsync("MAXDOP", "GUARDWIRE-NO-SUCH-SERVER",
                    approved: true, approvedBy: "test",
                    parameters: new Dictionary<string, string> { ["MaxDop"] = "4" });
                Assert.False(ok.IsRefused);
                Assert.Equal(RemediationOutcome.CouldNotRun, ok.Outcome);   // the executor was reached
            }
            finally
            {
                try { System.IO.Directory.Delete(scratch, recursive: true); } catch { /* cleanup */ }
            }
        }

        /// <summary>Throws if an item ever reaches it, so a gate refusal is proved to have run nothing.</summary>
        private sealed class NeverReachedExecutor : IRemediationExecutor
        {
            public System.Threading.Tasks.Task<RemediationPreview> PreviewAsync(RemediationRequest r, System.Threading.CancellationToken ct = default) =>
                throw new InvalidOperationException("PreviewAsync must not be reached past a gate refusal.");
            public System.Threading.Tasks.Task<RemediationExecution> ExecuteAsync(RemediationRequest r, System.Threading.CancellationToken ct = default) =>
                throw new InvalidOperationException("ExecuteAsync must not be reached past a gate refusal.");
            public bool CanWriteAudit() => true;
        }

        [Fact]
        public void TheVendorInstallScriptIsExemptFromTheGuard_AndTheExemptionIsMEASUREDNotAssumed()
        {
            // ⚠ THE NAMED LIMITATION OF ROUTE A, held down with evidence.
            //
            // The guard inspects the REPRESENTATIVE rendering of InstallMaintenanceSolution (which
            // is what the classifier vets) and NOT the embedded 9500-line Ola Hallengren script the
            // apply runs. That is a deliberate exemption, and the reason is measurable: Ola's
            // procedure BODIES contain xp_fileexist and friends AS PROCEDURE TEXT, so inspecting the
            // install batch would block a shipped, gate-blessed template for tokens that are data
            // inside a CREATE PROCEDURE rather than an invocation.
            //
            // This test asserts BOTH halves, so the exemption cannot quietly become "we forgot":
            //   (a) the representative rendering passes;
            //   (b) a body containing those tokens really WOULD be blocked.
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var install = store.TryGet("INSTALLMAINTENANCESOLUTION");
            Assert.NotNull(install);
            Assert.True(RemediationOpRenderer.TryRenderForClassification(install!.Operation!, out var repSql, out _));
            Assert.True(RemediationExecGuard.Allows(repSql, RemediationExecGuard.RenderedFix, out _));

            const string vendorBodyShape =
                "CREATE PROCEDURE [dbo].[DatabaseBackup] AS BEGIN " +
                "EXECUTE [master].dbo.xp_fileexist @DirectoryPath, @FileExists OUTPUT; END";
            Assert.False(RemediationExecGuard.Allows(vendorBodyShape, RemediationExecGuard.RenderedFix, out var wouldBlock));
            Assert.Contains("Filesystem enumeration", wouldBlock);
        }
    }
}
