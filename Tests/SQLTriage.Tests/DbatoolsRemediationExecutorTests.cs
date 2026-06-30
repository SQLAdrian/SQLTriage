/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Build step 5: the dbatools command construction for the executor. The live
    /// snapshot/apply/verify loop needs a real SQL Server + dbatools (step 6 / a
    /// manual run); here we pin the command-building logic that is pure and
    /// testable — preview vs real, parameter validation, instance escaping, and
    /// the always-present non-interactive/confirm guards.
    /// </summary>
    public class DbatoolsRemediationExecutorTests
    {
        private static RemediationTemplate MaxDop() => new()
        {
            Key = "MAXDOP",
            DisplayName = "Max Degree of Parallelism",
            DbatoolsCommand = "Set-DbaMaxDop",
            Kind = RemediationKind.Configuration,
        };

        private static RemediationRequest Req(string server, params (string k, string v)[] ps)
        {
            var d = new Dictionary<string, string>();
            foreach (var (k, v) in ps) d[k] = v;
            return new RemediationRequest(MaxDop(), server, d);
        }

        // ── MAXDOP parameter validation ─────────────────────────────────────

        [Fact]
        public void Build_MaxDop_MissingParam_ReturnsError()
        {
            var cmd = DbatoolsRemediationExecutor.BuildDbatoolsCommand(Req("srv1"), whatIf: true, out var error);
            Assert.NotNull(error);
            Assert.Equal(string.Empty, cmd);
        }

        [Fact]
        public void Build_MaxDop_NonIntegerParam_ReturnsError()
        {
            var cmd = DbatoolsRemediationExecutor.BuildDbatoolsCommand(Req("srv1", ("MaxDop", "lots")), whatIf: false, out var error);
            Assert.NotNull(error);
            Assert.Equal(string.Empty, cmd);
        }

        [Fact]
        public void Build_MaxDop_Valid_ProducesSetDbaMaxDop()
        {
            var cmd = DbatoolsRemediationExecutor.BuildDbatoolsCommand(Req("srv1", ("MaxDop", "4")), whatIf: false, out var error);
            Assert.Null(error);
            Assert.Contains("Set-DbaMaxDop", cmd);
            Assert.Contains("-SqlInstance 'srv1'", cmd);
            Assert.Contains("-MaxDop 4", cmd);
        }

        // ── -WhatIf appears for preview only ────────────────────────────────

        [Fact]
        public void Build_Preview_AppendsWhatIf()
        {
            var cmd = DbatoolsRemediationExecutor.BuildDbatoolsCommand(Req("srv1", ("MaxDop", "2")), whatIf: true, out _);
            Assert.Contains("-WhatIf", cmd);
        }

        [Fact]
        public void Build_Real_DoesNotAppendWhatIf()
        {
            var cmd = DbatoolsRemediationExecutor.BuildDbatoolsCommand(Req("srv1", ("MaxDop", "2")), whatIf: false, out _);
            Assert.DoesNotContain("-WhatIf", cmd);
        }

        // ── Safety guards always present ────────────────────────────────────

        [Fact]
        public void Build_AlwaysNonInteractiveAndEnableException()
        {
            var cmd = DbatoolsRemediationExecutor.BuildDbatoolsCommand(Req("srv1", ("MaxDop", "1")), whatIf: false, out _);
            Assert.Contains("-Confirm:$false", cmd);   // no interactive prompt in a headless host
            Assert.Contains("-EnableException", cmd);  // surface a clean terminating error
        }

        // ── Instance name is single-quote escaped (no injection) ────────────

        [Fact]
        public void Build_EscapesSingleQuotesInInstanceName()
        {
            var cmd = DbatoolsRemediationExecutor.BuildDbatoolsCommand(
                Req("srv';Remove-Item C:\\ -Recurse;'", ("MaxDop", "1")), whatIf: false, out _);
            // The lone quote is doubled so it stays inside the PS single-quoted literal.
            Assert.Contains("''", cmd);
            Assert.DoesNotContain("-SqlInstance 'srv';Remove-Item", cmd);
        }

        // ── Unknown command falls back to the generic shape ─────────────────

        [Fact]
        public void Build_GenericCommand_NoRequiredParams()
        {
            var template = new RemediationTemplate { Key = "AGENTOWNER", DbatoolsCommand = "Set-DbaAgentJobOwner" };
            var req = new RemediationRequest(template, "srv2");
            var cmd = DbatoolsRemediationExecutor.BuildDbatoolsCommand(req, whatIf: true, out var error);
            Assert.Null(error);
            Assert.Contains("Set-DbaAgentJobOwner -SqlInstance 'srv2'", cmd);
            Assert.Contains("-WhatIf", cmd);
        }
    }
}
