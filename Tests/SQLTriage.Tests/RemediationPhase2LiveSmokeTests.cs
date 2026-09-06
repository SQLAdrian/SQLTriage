/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationPhase2LiveSmokeTests — THE PROOF OF CURE for Phase-2 item 2.1, plus live confirmations
 * of items 2.2, 2.3 and 6a.
 *
 * THIS FILE RE-RUNS THE EXACT SCENARIO THAT PROVED THE DEFECT. Spike S2 section 3.1, 2026-09-01,
 * on SQL Server 2017: `min server memory (MB)` sitting at configured value 0 with value_in_use
 * pinned at the engine's 16 MB floor. An apply of 8 goes through all five gates, SQL Server coerces
 * it, the engine's verify (on value_in_use) correctly fails, and the rollback fires. What the
 * ENGINE then did was report RollbackState = Confirmed while leaving the CONFIGURED value at 16,
 * where as-found was 0. Two separate faults in one run:
 *
 *   - the confirming read was NON DISCRIMINATING: it read value_in_use (16) and compared it against
 *     a value_in_use snapshot (16), which was true before the inverse ran;
 *   - the WRONG COLUMN was restored: the inverse re-applied the captured value_in_use.
 *
 * The spike's own harness had to put the server back afterwards. The engine did not.
 *
 * WHAT MUST NOW HAPPEN, and what Fact 1 asserts: same scenario, same option, same instance, and the
 * independent read after the engine's rollback must show CONFIGURED value 0 - restored by the
 * ENGINE, confirmed by a read of the column the inverse actually sets.
 *
 * SKIPPED unless REMP2_LIVE_TARGET names a reachable instance, so a normal `dotnet test` run never
 * touches a server. LiveFactAttribute computes Skip at DISCOVERY time and RequireTarget() makes the
 * body FAIL rather than pass vacuously if that attribute is ever weakened (the arming-census defect
 * class; see BatchRemediationDriverLiveSmokeTests for the full note).
 *
 * INVOCATION:
 *   $env:REMP2_LIVE_TARGET = ".\old2017"
 *   dotnet test Tests/SQLTriage.Tests --filter "FullyQualifiedName~RemediationPhase2LiveSmokeTests"
 *
 * SUBSTITUTIONS, stated so dependent claims can be downgraded:
 *   - Gate 2 runs the REAL BundleBackedRemediationCapability over a FakeBundleAccessor. The gate
 *     LOGIC is exercised; the signature check on a minted bundle is NOT.
 *   - The min-server-memory template is promoted through the REAL corpus path
 *     (RemediationTemplateStore.LoadCorpusTemplates over a composed SqlCheck), with zero app-code
 *     change - the same route the spike used, and the same route a real corpus check takes.
 *   - Everything else is the real component: renderer, safety validator, exec guard, executor,
 *     credit ledger, audit chain, and the SQL Server itself.
 *
 * RESIDUE: every value touched is captured before, restored after, and RE-READ to prove the
 * restoration. Every option touched is asserted is_dynamic = 1, so no restart-required setting is
 * ever written.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Remediation;
using SQLTriage.Tests.Licensing;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class RemediationPhase2LiveSmokeTests
    {
        private readonly ITestOutputHelper _out;
        public RemediationPhase2LiveSmokeTests(ITestOutputHelper output) => _out = output;

        public sealed class LiveFactAttribute : FactAttribute
        {
            public LiveFactAttribute(params string[] required)
            {
                var missing = required
                    .Where(v => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)))
                    .ToList();
                if (missing.Count > 0)
                    Skip = "live harness not armed; set " + string.Join(", ", missing);
            }
        }

        private const string TargetVariable = "REMP2_LIVE_TARGET";
        private static string? Target => Environment.GetEnvironmentVariable(TargetVariable);

        private static string RequireTarget()
        {
            Assert.False(string.IsNullOrWhiteSpace(Target),
                TargetVariable + " is not set, so this test has no instance to touch and nothing to "
                + "assert. It should have been SKIPPED by LiveFactAttribute; if it ran, that "
                + "attribute is no longer doing its job.");
            return Target!;
        }

        private const string MinServerMemory = "min server memory (MB)";
        private const string ShowAdvanced = "show advanced options";
        private const string CostThreshold = "cost threshold for parallelism";
        private const string PromotedKey = "SQLT-P2LIVE-MINSERVERMEM";

        private static string ConnString(string target) =>
            $"Server={target};Database=master;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=15;";

        // ── Independent reads and writes: a SECOND connection the services never saw ──

        private (int Value, int InUse, bool IsDynamic) ReadConfig(string target, string name)
        {
            using var conn = new SqlConnection(ConnString(target));
            conn.Open();
            using var cmd = new SqlCommand(
                "SELECT value, value_in_use, is_dynamic FROM sys.configurations WHERE name = @n;", conn);
            cmd.Parameters.AddWithValue("@n", name);
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read(), $"'{name}' is not a configuration option on this instance.");
            return (Convert.ToInt32(r.GetValue(0)), Convert.ToInt32(r.GetValue(1)), Convert.ToInt32(r.GetValue(2)) == 1);
        }

        /// <summary>
        /// Sets a configuration value OUTSIDE the app, so the fixture the app then reads was not
        /// created by the code under test. sp_configure needs 'show advanced options' for an
        /// advanced setting; the caller restores that itself where it matters.
        /// </summary>
        private static void SetConfig(string target, string name, int value, bool advanced = true)
        {
            using var conn = new SqlConnection(ConnString(target));
            conn.Open();
            var sql = (advanced ? "EXEC sp_configure 'show advanced options', 1; RECONFIGURE; " : "")
                    + $"EXEC sp_configure '{name.Replace("'", "''")}', {value}; RECONFIGURE;";
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            cmd.ExecuteNonQuery();
        }

        // ── The lane, wired over scratch stores. No real install path is touched. ──

        private sealed class Wiring
        {
            public RemediationRunner Runner = default!;
            public RemediationTemplateStore Templates = default!;
            public PersistedRemediationCreditLedger Credits = default!;
            public string ScratchRoot = string.Empty;
        }

        private static Wiring Wire(string target, int creditsPerServer = 50)
        {
            var scratch = Path.Combine(Path.GetTempPath(), "remp2-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);

            var connections = new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance);
            connections.AddConnection(new ServerConnection
            {
                ServerNames = target,
                UseWindowsAuthentication = true,
                TrustServerCertificate = true,
                IsEnabled = true,
            });

            var audit = new AuditLogService(Path.Combine(scratch, "audit-logs"), startFlushTimer: false);
            var executor = new DbatoolsRemediationExecutor(
                new PowerShellService(NullLogger<PowerShellService>.Instance),
                connections, audit,
                new DiskIoService(NullLogger<DiskIoService>.Instance),
                NullLogger<DbatoolsRemediationExecutor>.Instance);

            var bundle = new FakeBundleAccessor
            {
                IsUnlocked = true,
                Tier = Tier.Full,
                Features = new BundleFeatures(
                    RagEnabled: false, SpBlitzImport: false, FullCorpus: false,
                    PermittedCheckIds: Array.Empty<int>(),
                    Remediation: true, RemediationCreditsPerServer: creditsPerServer),
            };
            var credits = new PersistedRemediationCreditLedger(
                bundle, NullLogger<PersistedRemediationCreditLedger>.Instance, null,
                Path.Combine(scratch, "remediation-credit-ledger.json"));
            var templates = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);

            return new Wiring
            {
                Runner = new RemediationRunner(templates, new BundleBackedRemediationCapability(bundle),
                    credits, executor, audit, NullLogger<RemediationRunner>.Instance),
                Templates = templates,
                Credits = credits,
                ScratchRoot = scratch,
            };
        }

        /// <summary>
        /// Promotes a min-server-memory fix through the REAL corpus path — a composed SqlCheck with
        /// auto_fixable and a structured sp_configure op. Zero app-code change, which is the same
        /// route the spike used and the same route a real corpus check takes.
        /// </summary>
        private static void PromoteMinServerMemory(RemediationTemplateStore templates)
        {
            templates.LoadCorpusTemplates(new[]
            {
                new SqlCheck
                {
                    Id = PromotedKey,
                    Name = "Minimum server memory (live Phase-2 proof)",
                    Remediation = new CheckRemediation
                    {
                        AutoFixable = true,
                        RiskClass = "standard",
                        Reversible = true,
                        CmdletOrTemplate = "sp_configure 'min server memory (MB)'",
                        Operation = new CheckRemediationOperation
                        {
                            OpKind = "sp_configure",
                            ConfigName = MinServerMemory,
                            AdvancedOption = true,
                            ValueParam = "MinServerMemoryMb",
                            MinValue = 0,
                            MaxValue = 2147483647,
                        },
                    },
                },
            });
            Assert.True(templates.IsRegistered(PromotedKey),
                "the corpus promotion did not register; the rest of this test would prove nothing.");
        }

        // ── FACT 1 — THE PROOF OF CURE (item 2.1) + PreChangeValue on the rollback return (2.2) ──

        [LiveFact(TargetVariable)]
        public async Task ACoercingOption_RollsBackToTheCONFIGUREDValue_AndConfirmsItByReadingThatColumn()
        {
            var target = RequireTarget();

            var baseline = ReadConfig(target, MinServerMemory);
            var advancedBaseline = ReadConfig(target, ShowAdvanced);
            _out.WriteLine($"BASELINE '{MinServerMemory}': value={baseline.Value} value_in_use={baseline.InUse} is_dynamic={baseline.IsDynamic}");
            _out.WriteLine($"BASELINE '{ShowAdvanced}':    value={advancedBaseline.Value} value_in_use={advancedBaseline.InUse}");

            Assert.True(baseline.IsDynamic, "this test refuses to write a restart-required option.");

            try
            {
                // The fixture: configured 0. Set from OUTSIDE the app, so what the engine reads
                // was not written by the code under test.
                SetConfig(target, MinServerMemory, 0);
                var asFound = ReadConfig(target, MinServerMemory);
                _out.WriteLine($"AS FOUND: value={asFound.Value} value_in_use={asFound.InUse}");
                Assert.Equal(0, asFound.Value);

                // THE PRECONDITION THIS WHOLE PROOF RESTS ON: the engine coerces. If value_in_use
                // tracked value on this instance, the verify would pass, the rollback would never
                // fire, and a green test would prove nothing at all.
                Assert.True(asFound.InUse != asFound.Value,
                    $"this instance is not coercing '{MinServerMemory}' (value={asFound.Value}, "
                    + $"value_in_use={asFound.InUse}), so the rollback branch cannot be reached and "
                    + "this test would pass vacuously.");

                var w = Wire(target);
                PromoteMinServerMemory(w.Templates);

                var before = w.Credits.AvailableFor(target);
                var result = await w.Runner.ApplyAsync(PromotedKey, target, approved: true, approvedBy: "phase2-live",
                    parameters: new Dictionary<string, string> { ["MinServerMemoryMb"] = "8" });

                _out.WriteLine($"APPLY -> Outcome={result.Outcome} Rollback={result.RollbackState}");
                _out.WriteLine($"        Message       = {result.Message}");
                _out.WriteLine($"        RollbackError = {result.RollbackError}");
                _out.WriteLine($"        PreChangeValue={result.PreChangeValue} PreChangeValueInUse={result.PreChangeValueInUse}");

                // The engine's own verify failed, exactly as in the spike: a coerced setting has
                // not taken effect, and saying otherwise would be the opposite lie.
                Assert.False(result.IsRefused, result.Message);
                Assert.Equal(RemediationOutcome.AppliedVerifyFailed, result.Outcome);

                // ⚠ THE ASSERTION THIS FILE EXISTS FOR. The rollback is Confirmed, and it is
                // confirmed by a read of the CONFIGURED value, which is the column the inverse set.
                Assert.Equal(RemediationRollbackState.Confirmed, result.RollbackState);

                // Item 2.2: the pre-change value survives the rollback return. It used to be
                // dropped on exactly this path.
                Assert.Equal(0, result.PreChangeValue);
                Assert.Equal(asFound.InUse, result.PreChangeValueInUse);

                // ⚠ AND THE INDEPENDENT PROOF. A second connection the app never saw. In the spike
                // this read said value=16 - the coerced number, re-applied over a configured 0.
                var after = ReadConfig(target, MinServerMemory);
                _out.WriteLine($"INDEPENDENT READ after apply+rollback: value={after.Value} value_in_use={after.InUse}");
                Assert.Equal(0, after.Value);

                // A confirmed rollback refunds (ruling 2), so the balance is back where it started.
                Assert.Equal(before, w.Credits.AvailableFor(target));
            }
            finally
            {
                SetConfig(target, MinServerMemory, baseline.Value);
                SetConfig(target, ShowAdvanced, advancedBaseline.Value, advanced: false);

                var restored = ReadConfig(target, MinServerMemory);
                var advancedRestored = ReadConfig(target, ShowAdvanced);
                _out.WriteLine($"RESTORED '{MinServerMemory}': value={restored.Value} value_in_use={restored.InUse} (baseline {baseline.Value}/{baseline.InUse})");
                _out.WriteLine($"RESTORED '{ShowAdvanced}':    value={advancedRestored.Value} (baseline {advancedBaseline.Value})");
                Assert.Equal(baseline.Value, restored.Value);
                Assert.Equal(advancedBaseline.Value, advancedRestored.Value);
            }
        }

        // ── FACT 2 — the render's own side effect is captured AND restored (item 2.3), live ──

        [LiveFact(TargetVariable)]
        public async Task TheShowAdvancedOptionsPrelude_IsPutBack_OnAServerWhereItStartsAtZero()
        {
            var target = RequireTarget();

            var advancedBaseline = ReadConfig(target, ShowAdvanced);
            var ctfpBaseline = ReadConfig(target, CostThreshold);
            _out.WriteLine($"BASELINE '{ShowAdvanced}'={advancedBaseline.Value} '{CostThreshold}'={ctfpBaseline.Value}");
            Assert.True(advancedBaseline.IsDynamic && ctfpBaseline.IsDynamic);

            try
            {
                // THE FIXTURE THE SPIKE NEVER HAD. Both test instances happened to sit at 1, which
                // is why the side effect was invisible: the renderer's prelude changed nothing.
                // Starting from 0 is the case that exposes it.
                SetConfig(target, ShowAdvanced, 0, advanced: false);
                Assert.Equal(0, ReadConfig(target, ShowAdvanced).Value);

                var w = Wire(target);
                var newTarget = ctfpBaseline.Value == 50 ? 45 : 50;

                var result = await w.Runner.ApplyAsync("CTFP", target, approved: true, approvedBy: "phase2-live",
                    parameters: new Dictionary<string, string> { ["CostThreshold"] = newTarget.ToString() });

                _out.WriteLine($"APPLY CTFP -> Outcome={result.Outcome} Message={result.Message}");
                Assert.False(result.IsRefused, result.Message);
                Assert.Equal(RemediationOutcome.AppliedVerified, result.Outcome);

                // The fix itself landed - the negative control on the restore below.
                var ctfpAfter = ReadConfig(target, CostThreshold);
                Assert.Equal(newTarget, ctfpAfter.Value);

                // ⚠ THE ASSERTION FOR ITEM 2.3. The renderer turned 'show advanced options' on to
                // make the change possible. Before this lane it stayed on, unrecorded and
                // unrestored: a second server setting changed by a fix nobody approved for it.
                var advancedAfter = ReadConfig(target, ShowAdvanced);
                _out.WriteLine($"INDEPENDENT READ '{ShowAdvanced}' after the apply: value={advancedAfter.Value} (fixture was 0)");
                Assert.Equal(0, advancedAfter.Value);
            }
            finally
            {
                SetConfig(target, ShowAdvanced, 1, advanced: false);
                SetConfig(target, CostThreshold, ctfpBaseline.Value);
                SetConfig(target, ShowAdvanced, advancedBaseline.Value, advanced: false);

                var advRestored = ReadConfig(target, ShowAdvanced);
                var ctfpRestored = ReadConfig(target, CostThreshold);
                _out.WriteLine($"RESTORED '{ShowAdvanced}'={advRestored.Value} (baseline {advancedBaseline.Value}) "
                             + $"'{CostThreshold}'={ctfpRestored.Value} (baseline {ctfpBaseline.Value})");
                Assert.Equal(advancedBaseline.Value, advRestored.Value);
                Assert.Equal(ctfpBaseline.Value, ctfpRestored.Value);
            }
        }

        // ── FACT 3 — a garbage value is refused and NOTHING reaches the server (item 6a) ──

        [LiveFact(TargetVariable)]
        public async Task AGarbageValue_IsRefusedLoudly_AndTheServerNeverMoves()
        {
            var target = RequireTarget();

            var ctfpBaseline = ReadConfig(target, CostThreshold);
            var advancedBaseline = ReadConfig(target, ShowAdvanced);
            _out.WriteLine($"BASELINE '{CostThreshold}': value={ctfpBaseline.Value} value_in_use={ctfpBaseline.InUse}");

            var w = Wire(target);

            foreach (var garbage in new[] { "", "lots", "50; DROP DATABASE payroll", "999999", "-1" })
            {
                var result = await w.Runner.ApplyAsync("CTFP", target, approved: true, approvedBy: "phase2-live",
                    parameters: new Dictionary<string, string> { ["CostThreshold"] = garbage });

                _out.WriteLine($"[{garbage}] -> Outcome={result.Outcome} Message={result.Message}");

                // CouldNotRun, not a refusal at a gate: the value is resolved inside the executor,
                // before its first read. Either way NOTHING ran.
                Assert.Equal(RemediationOutcome.CouldNotRun, result.Outcome);
                // The sentence is the NO-SQL one, and for this option it is true unconditionally:
                // 'cost threshold for parallelism' has no host-relative bound, so no probe can run
                // for it at all (asserted offline by AnOptionWithNoHostRelativeBound_NeverReadsTheHostAtAll).
                Assert.Contains(RemediationValueBounds.RefusedBeforeAnySqlSentence, result.Message);
                Assert.Contains("Enter a whole number from 0 to 32767", result.Message);

                // The independent proof, per rejected value: the setting did not move, and neither
                // did the advanced-options flag the renderer would have flipped had it rendered.
                var now = ReadConfig(target, CostThreshold);
                Assert.Equal(ctfpBaseline.Value, now.Value);
                Assert.Equal(ctfpBaseline.InUse, now.InUse);
                Assert.Equal(advancedBaseline.Value, ReadConfig(target, ShowAdvanced).Value);
            }

            // CouldNotRun refunds, so a refused garbage value costs nothing either.
            _out.WriteLine($"credits available after five refusals: {w.Credits.AvailableFor(target)}");
            Assert.Equal(50, w.Credits.AvailableFor(target));
        }

        // ── FACT 5 — the refusal sentence is TRUE: no query ran (fix round, gate blocker 1) ──

        /// <summary>
        /// The instrument: how many times this instance has executed the host-memory probe, summed
        /// over the plan cache. Reading the same counter the gate read, on the same instance.
        /// </summary>
        private long PhysicalMemoryProbeExecutions(string target)
        {
            using var conn = new SqlConnection(ConnString(target));
            conn.Open();
            using var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(qs.execution_count), 0)
FROM sys.dm_exec_query_stats AS qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS t
WHERE t.text LIKE '%physical\_memory\_kb%' ESCAPE '\'
  AND t.text NOT LIKE '%dm\_exec\_query\_stats%' ESCAPE '\';", conn);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        /// <summary>
        /// Runs the probe's own text twice from an independent connection, so the counter above is
        /// LIVE before anything is measured against it.
        ///
        /// <para>⚠ WHY THIS IS NECESSARY AND WHY IT IS HONEST. `optimize for ad hoc workloads` is 1
        /// on this instance (measured), so the FIRST execution of an ad-hoc batch caches a stub and
        /// produces no sys.dm_exec_query_stats row at all; counting starts from the second. Without
        /// warming, a positive control can read 0 -> 0 for a probe that really ran, and the whole
        /// instrument would be reporting "no query" for the case where a query happened - the exact
        /// error class this test exists to catch, committed by the test itself. Warming touches
        /// nothing: the probe is a read of sys.dm_os_sys_info.</para>
        /// </summary>
        private void WarmThePhysicalMemoryProbePlan(string target)
        {
            using var conn = new SqlConnection(ConnString(target));
            conn.Open();
            for (int i = 0; i < 2; i++)
            {
                using var cmd = new SqlCommand(RemediationValueBounds.PhysicalMemoryMbQuery, conn);
                cmd.ExecuteScalar();
            }
        }

        [LiveFact(TargetVariable)]
        public async Task AGarbageMaxMemoryValue_RunsNOQueryAtAll_AndTheSentenceSaysSoTruthfully()
        {
            var target = RequireTarget();

            // ⚠ THIS IS THE GATE'S OWN MEASUREMENT, RE-RUN. Before the fix round, the executor read
            // sys.dm_os_sys_info to bound 'max server memory (MB)' BEFORE resolving the value, so
            // MaxServerMemoryMb="banana" printed "Nothing was sent to the server" while this counter
            // moved 0 -> 1. The refusal was right and the sentence beside it was an operator-facing
            // lie. MAXSERVERMEMORY is a SHIPPED template - no promotion, no fixture.
            var memBaseline = ReadConfig(target, "max server memory (MB)");
            var advancedBaseline = ReadConfig(target, ShowAdvanced);
            _out.WriteLine($"BASELINE 'max server memory (MB)': value={memBaseline.Value} value_in_use={memBaseline.InUse}");

            var w = Wire(target);

            // The instrument must be live before it is trusted (see the method's note).
            WarmThePhysicalMemoryProbePlan(target);
            var warmed = PhysicalMemoryProbeExecutions(target);
            _out.WriteLine($"host-probe executions after warming the plan: {warmed}");
            Assert.True(warmed > 0,
                "the host-memory probe's plan does not appear in sys.dm_exec_query_stats even after "
                + "being executed twice, so this test cannot tell a query that ran from one that did "
                + "not. It must not pass.");

            foreach (var garbage in new[] { "banana", "", "-1", "128" })
            {
                var before = PhysicalMemoryProbeExecutions(target);

                var result = await w.Runner.ApplyAsync("MAXSERVERMEMORY", target, approved: true, approvedBy: "phase2-live",
                    parameters: new Dictionary<string, string> { ["MaxServerMemoryMb"] = garbage });

                var after = PhysicalMemoryProbeExecutions(target);
                _out.WriteLine($"[{garbage}] -> {result.Outcome} | host-probe executions {before} -> {after}");
                _out.WriteLine($"          {result.Message}");

                Assert.Equal(RemediationOutcome.CouldNotRun, result.Outcome);

                // ⚠ THE ASSERTION THIS FACT EXISTS FOR: the counter did not move.
                Assert.Equal(before, after);

                // And the sentence printed is the one that belongs to a path where nothing ran.
                Assert.Contains(RemediationValueBounds.RefusedBeforeAnySqlSentence, result.Message);
                Assert.DoesNotContain(RemediationValueBounds.HostCheckRanSentence, result.Message);

                // Nothing moved on the server either, on the option itself or the advanced flag.
                var now = ReadConfig(target, "max server memory (MB)");
                Assert.Equal(memBaseline.Value, now.Value);
                Assert.Equal(memBaseline.InUse, now.InUse);
                Assert.Equal(advancedBaseline.Value, ReadConfig(target, ShowAdvanced).Value);
            }

            // ── THE POSITIVE CONTROL. An instrument that never fires proves nothing. A value that
            //    PARSES and passes the static range is exactly the value allowed to cause the probe,
            //    and its refusal says a read-only host check ran - because one did.
            var cBefore = PhysicalMemoryProbeExecutions(target);
            var absurd = await w.Runner.ApplyAsync("MAXSERVERMEMORY", target, approved: true, approvedBy: "phase2-live",
                parameters: new Dictionary<string, string> { ["MaxServerMemoryMb"] = "2000000" });   // 2 TB
            var cAfter = PhysicalMemoryProbeExecutions(target);

            _out.WriteLine($"[2000000] -> {absurd.Outcome} | host-probe executions {cBefore} -> {cAfter}");
            _out.WriteLine($"          {absurd.Message}");
            Assert.Equal(RemediationOutcome.CouldNotRun, absurd.Outcome);
            Assert.True(cAfter > cBefore,
                $"the host-memory probe did not run for a validly-parsed cap ({cBefore} -> {cAfter}), "
                + "so the instrument above cannot detect it running and the four assertions are worthless.");
            Assert.Contains(RemediationValueBounds.HostCheckRanSentence, absurd.Message);
            Assert.DoesNotContain(RemediationValueBounds.RefusedBeforeAnySqlSentence, absurd.Message);

            // Refused values cost nothing, so the balance is untouched by all five.
            Assert.Equal(50, w.Credits.AvailableFor(target));

            // And the server is exactly where it was: five refusals wrote nothing.
            var finalRead = ReadConfig(target, "max server memory (MB)");
            _out.WriteLine($"FINAL 'max server memory (MB)': value={finalRead.Value} value_in_use={finalRead.InUse}");
            Assert.Equal(memBaseline.Value, finalRead.Value);
            Assert.Equal(advancedBaseline.Value, ReadConfig(target, ShowAdvanced).Value);
        }

        // ── FACT 6 — the side effect is put back when the APPLY ITSELF FAILS (blocker 2) ──

        private const string FailingKey = "SQLT-P2LIVE-INDEXCREATEMEM";
        private const string IndexCreateMemory = "index create memory (KB)";

        /// <summary>
        /// Promotes an ADVANCED option whose engine minimum (704 KB) is below what this template's
        /// range allows, so a target of 1 passes every app-side gate and is rejected BY THE SERVER -
        /// after the rendered prelude has already committed. That is the only way to reach the apply
        /// catch with a real batch, and it is exactly the shape the gate proved.
        /// </summary>
        private static void PromoteFailingAdvancedOption(RemediationTemplateStore templates)
        {
            templates.LoadCorpusTemplates(new[]
            {
                new SqlCheck
                {
                    Id = FailingKey,
                    Name = "Index create memory (live apply-failure proof)",
                    Remediation = new CheckRemediation
                    {
                        AutoFixable = true,
                        RiskClass = "standard",
                        Reversible = true,
                        CmdletOrTemplate = "sp_configure 'index create memory (KB)'",
                        Operation = new CheckRemediationOperation
                        {
                            OpKind = "sp_configure",
                            ConfigName = IndexCreateMemory,
                            AdvancedOption = true,
                            ValueParam = "IndexCreateMemoryKb",
                            MinValue = 0,
                            MaxValue = 2147483647,
                        },
                    },
                },
            });
            Assert.True(templates.IsRegistered(FailingKey),
                "the corpus promotion did not register; the rest of this test would prove nothing.");
        }

        [LiveFact(TargetVariable)]
        public async Task TheRenderedSideEffect_IsPutBack_WhenTheApplyItselfFAILS()
        {
            var target = RequireTarget();

            var advancedBaseline = ReadConfig(target, ShowAdvanced);
            var icmBaseline = ReadConfig(target, IndexCreateMemory);
            _out.WriteLine($"BASELINE '{ShowAdvanced}'={advancedBaseline.Value} '{IndexCreateMemory}'={icmBaseline.Value}");
            Assert.True(advancedBaseline.IsDynamic && icmBaseline.IsDynamic,
                "this test refuses to touch a restart-required option.");

            try
            {
                // THE GATE'S FIXTURE, EXACTLY: 'show advanced options' at 0, set from OUTSIDE the app.
                SetConfig(target, ShowAdvanced, 0, advanced: false);
                Assert.Equal(0, ReadConfig(target, ShowAdvanced).Value);

                var w = Wire(target);
                PromoteFailingAdvancedOption(w.Templates);

                var before = w.Credits.AvailableFor(target);
                var result = await w.Runner.ApplyAsync(FailingKey, target, approved: true, approvedBy: "phase2-live",
                    parameters: new Dictionary<string, string> { ["IndexCreateMemoryKb"] = "1" });

                _out.WriteLine($"APPLY -> Outcome={result.Outcome}");
                _out.WriteLine($"        Message        = {result.Message}");
                _out.WriteLine($"        PreChangeValue = {result.PreChangeValue} PreChangeValueInUse={result.PreChangeValueInUse}");

                // The server rejected the value: the fix did not land, and the app says so.
                Assert.False(result.IsRefused, result.Message);
                Assert.Equal(RemediationOutcome.CouldNotRun, result.Outcome);
                Assert.Contains("not a valid value", result.Message);

                // ⚠ THE ASSERTION THIS FACT EXISTS FOR. The rendered batch turned 'show advanced
                // options' on and COMMITTED that before the target statement raised. Read on a
                // second connection the app never saw: in the gate's run this said 1.
                var advancedAfter = ReadConfig(target, ShowAdvanced);
                _out.WriteLine($"INDEPENDENT READ '{ShowAdvanced}' after the FAILED apply: value={advancedAfter.Value} (fixture was 0)");
                Assert.Equal(0, advancedAfter.Value);

                // The message NAMES the option it put back, so the operator does not have to infer
                // that a second setting was touched at all.
                Assert.Contains($"'{ShowAdvanced}'", result.Message);
                Assert.Contains("put back to 0", result.Message);

                // And the before-state survives the failure. It used to be dropped on this path.
                Assert.Equal(icmBaseline.Value, result.PreChangeValue);

                // The target option is untouched, and a failed apply costs nothing.
                Assert.Equal(icmBaseline.Value, ReadConfig(target, IndexCreateMemory).Value);
                Assert.Equal(before, w.Credits.AvailableFor(target));
            }
            finally
            {
                SetConfig(target, ShowAdvanced, 1, advanced: false);
                SetConfig(target, IndexCreateMemory, icmBaseline.Value);
                SetConfig(target, ShowAdvanced, advancedBaseline.Value, advanced: false);

                var advRestored = ReadConfig(target, ShowAdvanced);
                var icmRestored = ReadConfig(target, IndexCreateMemory);
                _out.WriteLine($"RESTORED '{ShowAdvanced}'={advRestored.Value} (baseline {advancedBaseline.Value}) "
                             + $"'{IndexCreateMemory}'={icmRestored.Value} (baseline {icmBaseline.Value})");
                Assert.Equal(advancedBaseline.Value, advRestored.Value);
                Assert.Equal(icmBaseline.Value, icmRestored.Value);
            }
        }

        // ── FACT 4 — zero residue, asserted as its own fact rather than assumed ──

        [LiveFact(TargetVariable)]
        public void EveryOptionThisFileTouchesReadsBackAtItsBaseline()
        {
            var target = RequireTarget();

            foreach (var name in new[] { MinServerMemory, ShowAdvanced, CostThreshold,
                                         "max server memory (MB)", IndexCreateMemory })
            {
                var c = ReadConfig(target, name);
                _out.WriteLine($"RESIDUE CHECK '{name}': value={c.Value} value_in_use={c.InUse} is_dynamic={c.IsDynamic}");
                Assert.True(c.IsDynamic, $"'{name}' is restart-required on this instance; this file must never write it.");
            }

            // A pending reconfigure this file created would show as a value/value_in_use split on
            // an option other than the engine's own coercion floor. Reported, not asserted blind:
            // 'min server memory (MB)' legitimately reads 0/16 on a stock instance.
            using var conn = new SqlConnection(ConnString(target));
            conn.Open();
            using var cmd = new SqlCommand(
                "SELECT name, value, value_in_use FROM sys.configurations WHERE value <> value_in_use;", conn);
            using var r = cmd.ExecuteReader();
            var split = new List<string>();
            while (r.Read()) split.Add($"{r.GetString(0)} | {r.GetValue(1)} | {r.GetValue(2)}");
            foreach (var s in split) _out.WriteLine("value <> value_in_use: " + s);

            Assert.All(split, s => Assert.Contains(MinServerMemory, s, StringComparison.OrdinalIgnoreCase));
        }
    }
}
