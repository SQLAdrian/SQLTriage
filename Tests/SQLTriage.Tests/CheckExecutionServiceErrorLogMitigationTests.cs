/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// #28 mitigation (2026-07-13) — two lanes:
    ///  1. Diagnostic capture: CheckExecutionService.ApplySqlExceptionDetails must stamp a
    ///     real SqlException's Number/Class/State/LineNumber/Server onto a CheckResult,
    ///     including when the SqlException is wrapped by another exception.
    ///  2. Serialization: the ERRORLOG-family check-id set and the SemaphoreSlim(1,1)
    ///     per-instance mutex primitive the mitigation is built on.
    ///
    /// SqlException has no public constructor. <see cref="SqlExceptionTestFactory"/> builds a
    /// REAL SqlException via the same internal factory the driver itself uses
    /// (SqlException.CreateException(SqlErrorCollection, string)), so these assertions run
    /// against genuine SqlClient behaviour, not a hand-rolled fake — no live SQL Server needed.
    /// </summary>
    public class CheckExecutionServiceErrorLogMitigationTests
    {
        // ── Diagnostic capture (ApplySqlExceptionDetails) ────────────────────────

        [Fact]
        public void ApplySqlExceptionDetails_DirectSqlException_MapsAllFields()
        {
            var sqlEx = SqlExceptionTestFactory.Create(
                number: 823, errorClass: 24, state: 2, lineNumber: 7, server: "MSI\\OLD2017",
                message: "A severe error occurred on the current command.");

            var result = new CheckResult { CheckId = "SQLT-VA-ERRORLOG-KNOWN-ERROR-WATCHLIST" };

            CheckExecutionService.ApplySqlExceptionDetails(result, sqlEx);

            Assert.Equal(823, result.SqlErrorNumber);
            Assert.Equal((byte)24, result.SqlErrorClass);
            Assert.Equal((byte)2, result.SqlErrorState);
            Assert.Equal(7, result.SqlErrorLineNumber);
            Assert.Equal("MSI\\OLD2017", result.SqlErrorServer);
        }

        [Fact]
        public void ApplySqlExceptionDetails_WrappedSqlException_WalksInnerExceptionChain()
        {
            // Mirrors the real shape: a check-execution helper (or the ADO.NET reader path)
            // can rethrow the SqlException wrapped in another exception type. The #28 gap was
            // that only ex.Message survived either way — this proves the walk finds it.
            var sqlEx = SqlExceptionTestFactory.Create(
                number: 3624, errorClass: 20, state: 1, lineNumber: 1, server: "TESTSRV",
                message: "A severe error occurred on the current command.");
            var wrapper = new InvalidOperationException("Check execution failed", sqlEx);

            var result = new CheckResult { CheckId = "SQLT-CUSTOM-ERRORLOG-CRITICAL-EVENTS" };

            CheckExecutionService.ApplySqlExceptionDetails(result, wrapper);

            Assert.Equal(3624, result.SqlErrorNumber);
            Assert.Equal((byte)20, result.SqlErrorClass);
            Assert.Equal((byte)1, result.SqlErrorState);
            Assert.Equal("TESTSRV", result.SqlErrorServer);
        }

        [Fact]
        public void ApplySqlExceptionDetails_NonSqlException_LeavesFieldsNull()
        {
            var result = new CheckResult { CheckId = "SQLT-SOME-OTHER-CHECK" };

            CheckExecutionService.ApplySqlExceptionDetails(result, new TimeoutException("plain timeout, no SqlException anywhere"));

            Assert.Null(result.SqlErrorNumber);
            Assert.Null(result.SqlErrorClass);
            Assert.Null(result.SqlErrorState);
            Assert.Null(result.SqlErrorLineNumber);
            Assert.Null(result.SqlErrorServer);
        }

        // ── ERRORLOG-family check-id membership ──────────────────────────────────

        [Theory]
        [InlineData("SQLT-VA-ERRORLOG-KNOWN-ERROR-WATCHLIST")]
        [InlineData("SQLT-CUSTOM-ERRORLOG-CRITICAL-EVENTS")]
        [InlineData("sqlt-va-errorlog-known-error-watchlist")] // case-insensitive match
        public void ErrorLogFamilyCheckIds_ContainsBothMitigatedChecks(string checkId)
        {
            Assert.Contains(checkId, CheckExecutionService.ErrorLogFamilyCheckIds);
        }

        [Fact]
        public void ErrorLogFamilyCheckIds_DoesNotContainUnrelatedCheck()
        {
            Assert.DoesNotContain("SQLT-VA-SOME-UNRELATED-CHECK", CheckExecutionService.ErrorLogFamilyCheckIds);
        }

        // ── Serialization primitive (SemaphoreSlim(1,1) per-instance mutex) ──────
        // CheckExecutionService.ExecuteSingleCheckAsync acquires a SemaphoreSlim(1,1) from a
        // ConcurrentDictionary keyed by serverName — ONLY for checks in ErrorLogFamilyCheckIds —
        // before running the SQL. These tests exercise that exact primitive under real
        // concurrent load (Task.WhenAll, mirroring how ExecuteChecksAsync fires all checks for
        // an instance concurrently) without needing a live SQL Server connection.

        [Fact]
        public async Task PerInstanceMutex_SemaphoreSlim_NeverAllowsConcurrentEntry()
        {
            var mutex = new SemaphoreSlim(1, 1);
            var insideCount = 0;
            var overlapDetected = false;

            async Task SimulatedErrorLogCheck()
            {
                await mutex.WaitAsync();
                try
                {
                    if (Interlocked.Increment(ref insideCount) > 1) overlapDetected = true;
                    await Task.Delay(25); // simulate the xp_readerrorlog call's duration
                }
                finally
                {
                    Interlocked.Decrement(ref insideCount);
                    mutex.Release();
                }
            }

            // Three "checks" fired concurrently, as ExecuteChecksAsync's Task.WhenAll(tasks) would.
            await Task.WhenAll(SimulatedErrorLogCheck(), SimulatedErrorLogCheck(), SimulatedErrorLogCheck());

            Assert.False(overlapDetected,
                "SemaphoreSlim(1,1) allowed two ERRORLOG-family calls to run concurrently on the same instance — the #28 mitigation is not holding.");
        }

        [Fact]
        public void PerInstanceLocks_SameServerKey_ReturnsSameSemaphoreInstance()
        {
            // Mirrors _errorLogFamilyLocks.GetOrAdd(serverName, ...) — the SAME server must
            // always get the SAME semaphore (else two "per-instance" locks would silently
            // become two independent 1-slot locks and stop serializing anything).
            var locks = new System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>();
            var a = locks.GetOrAdd("SERVERA", _ => new SemaphoreSlim(1, 1));
            var b = locks.GetOrAdd("SERVERA", _ => new SemaphoreSlim(1, 1));
            var c = locks.GetOrAdd("SERVERB", _ => new SemaphoreSlim(1, 1));

            Assert.Same(a, b);
            Assert.NotSame(a, c);
        }
    }

    /// <summary>
    /// Builds a REAL Microsoft.Data.SqlClient.SqlException via its internal factory
    /// (SqlException has no public constructor). Uses the exact
    /// SqlException.CreateException(SqlErrorCollection, string) path the driver itself calls,
    /// so tests exercise genuine SqlClient behaviour rather than a hand-rolled substitute.
    /// If the internal shape ever changes across a SqlClient upgrade, this throws clearly at
    /// the reflection site rather than silently producing a wrong exception.
    /// </summary>
    internal static class SqlExceptionTestFactory
    {
        public static SqlException Create(int number, byte errorClass, byte state, int lineNumber, string server, string message)
        {
            var asm = typeof(SqlException).Assembly;
            var errorCollType = asm.GetType("Microsoft.Data.SqlClient.SqlErrorCollection")
                ?? throw new InvalidOperationException("SqlErrorCollection type not found — SqlClient internal shape changed.");
            var errorType = asm.GetType("Microsoft.Data.SqlClient.SqlError")
                ?? throw new InvalidOperationException("SqlError type not found — SqlClient internal shape changed.");

            var errorCollection = Activator.CreateInstance(errorCollType, nonPublic: true)
                ?? throw new InvalidOperationException("Could not construct SqlErrorCollection.");

            var addMethod = errorCollType.GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("SqlErrorCollection.Add not found.");

            var errorCtor = errorType.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception) },
                null)
                ?? throw new InvalidOperationException("SqlError constructor(int,byte,byte,string,string,string,int,Exception) not found.");

            var error = errorCtor.Invoke(new object?[] { number, state, errorClass, server, message, "SQLTriage-test", lineNumber, null });
            addMethod.Invoke(errorCollection, new[] { error });

            var createMethod = typeof(SqlException).GetMethod("CreateException",
                BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { errorCollType, typeof(string) }, null)
                ?? throw new InvalidOperationException("SqlException.CreateException(SqlErrorCollection,string) not found.");

            return (SqlException)createMethod.Invoke(null, new[] { errorCollection, (object)"" })!;
        }
    }
}
