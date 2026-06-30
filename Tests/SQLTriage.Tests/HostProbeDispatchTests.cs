/* In the name of God, the Merciful, the Compassionate */

using System;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Host-probe dispatch in CheckExecutionService: the Windows-host derivation from a SQL
    /// server name (probes take a ComputerName, not an instance/port). The fail-closed
    /// capability/elevation contract is pinned separately in HostProbeServiceTests; the
    /// outcome→CheckResult mapping (Compliant→Pass / NotCompliant→Fail / did-not-run→SKIP)
    /// is exercised live (it shells dbatools) — not unit-mockable without a live elevated host.
    /// </summary>
    public class HostProbeDispatchTests
    {
        [Theory]
        [InlineData("SQLBOX\\PROD", "SQLBOX")]   // named instance stripped
        [InlineData("sqlbox,1433", "sqlbox")]    // port stripped
        [InlineData("SQLBOX\\PROD,1433", "SQLBOX")]
        [InlineData("sql.contoso.com", "sql.contoso.com")] // FQDN kept (dbatools accepts it)
        public void ResolveHostName_stripsInstanceAndPort(string serverName, string expected)
        {
            Assert.Equal(expected, CheckExecutionService.ResolveHostName(serverName));
        }

        [Theory]
        [InlineData(".")]
        [InlineData("(local)")]
        [InlineData("localhost")]
        [InlineData("127.0.0.1")]
        [InlineData("")]
        public void ResolveHostName_localAliases_mapToThisMachine(string serverName)
        {
            Assert.Equal(Environment.MachineName, CheckExecutionService.ResolveHostName(serverName));
        }
    }
}
