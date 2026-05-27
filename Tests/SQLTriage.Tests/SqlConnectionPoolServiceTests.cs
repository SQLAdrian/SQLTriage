/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    public class SqlConnectionPoolServiceTests : IDisposable
    {
        private readonly SqlConnectionPoolService _svc;

        public SqlConnectionPoolServiceTests()
        {
            // Pool of size 1, with a 200 ms timeout so the saturation test completes quickly.
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionPool:MaxSize"] = "1",
                    ["ConnectionPool:MinSize"] = "0",
                    ["ConnectionPool:TimeoutSeconds"] = "0", // zero → rounds up to at least 1 tick
                })
                .Build();

            _svc = new SqlConnectionPoolService(config, NullLogger<SqlConnectionPoolService>.Instance);
        }

        public void Dispose()
        {
            try { _svc.Dispose(); } catch { /* test cleanup; ignore */ }
        }

        // ── T5: saturated pool throws TimeoutException ───────────────────

        [Fact(Skip = "Requires live SQL Server — pool saturation logic validated structurally; " +
                     "GetConnectionAsync must open a real connection to consume a slot.")]
        public async Task GetConnectionAsync_WhenPoolSaturated_ThrowsTimeoutException()
        {
            // This test cannot run without a real SQL Server connection because
            // SqlConnectionPoolService only increments _connectionCounts when it
            // successfully opens a SqlConnection.  The timeout path (SemaphoreSlim.WaitAsync)
            // is only reached after a real slot is occupied.
            //
            // Structural coverage is confirmed by code inspection:
            //   - SemaphoreSlim.WaitAsync(_connectionTimeout) at line 114 of SqlConnectionPoolService.cs
            //   - If !acquired → throw new TimeoutException(...)
            //
            // To run against a live instance set SQLTRIAGE_TEST_CONNSTR and remove the Skip attr.
            var connStr = Environment.GetEnvironmentVariable("SQLTRIAGE_TEST_CONNSTR")
                          ?? "Server=localhost;Database=master;Trusted_Connection=True;";

            // Occupy the single slot
            var first = await _svc.GetConnectionAsync(connStr);

            // Second call must time out (pool size = 1, no one returns the connection)
            await Assert.ThrowsAsync<TimeoutException>(
                () => _svc.GetConnectionAsync(connStr, CancellationToken.None));

            // Cleanup: return the first connection
            _svc.ReturnConnection(first, connStr);
        }

        // ── Counter-accounting regressions (no live SQL needed) ──────────
        // These guard the bug class where the global counter drifts upward on
        // dispose/failed-return paths until the pool permanently wedges at
        // MaxGlobal ("[POOL] total active:100"). Verified via the failed-return
        // path, which decrements counters without ever opening a connection.

        [Fact]
        public void NewPool_HasZeroActiveConnections()
        {
            Assert.Equal(0, _svc.TotalActiveConnections);
            Assert.Equal(0, _svc.ActiveCountForServer("Server=a;Database=master;Trusted_Connection=True;"));
        }

        [Fact]
        public void ReturnConnection_ClosedConnection_DoesNotDriveCounterNegative()
        {
            // A never-opened SqlConnection is in the Closed state. Returning it hits
            // the failed-return branch, which decrements the counters. Starting from
            // zero, the decrement must clamp at zero — never go negative — or the cap
            // arithmetic silently corrupts and the pool can over- or under-count slots.
            using var closed = new SqlConnection("Server=a;Database=master;Trusted_Connection=True;");

            _svc.ReturnConnection(closed, "Server=a;Database=master;Trusted_Connection=True;");

            Assert.Equal(0, _svc.TotalActiveConnections);
            Assert.Equal(0, _svc.ActiveCountForServer("Server=a;Database=master;Trusted_Connection=True;"));
        }

        [Fact]
        public void ReturnConnection_NullConnection_IsNoOp()
        {
            _svc.ReturnConnection(null!, "Server=a;Database=master;Trusted_Connection=True;");
            Assert.Equal(0, _svc.TotalActiveConnections);
        }
    }
}
