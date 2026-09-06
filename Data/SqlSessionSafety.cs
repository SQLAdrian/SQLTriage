/* In the name of God, the Merciful, the Compassionate */

using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SQLTriage.Data
{
    /// <summary>
    /// S-1 session-safety options for every read this app issues against a client's production
    /// server. Hoisted out of <c>CheckExecutionService</c> (where it shipped 2026-06-01 as a
    /// private BuildSessionPrefix) so the other read lanes — DiagnosticScriptRunner and
    /// SqlAssessmentService — apply the same options from the same one definition instead of
    /// running at the server default.
    ///
    ///   • READ UNCOMMITTED → reads take no shared locks, so a query cannot block a busy
    ///     production server.
    ///   • LOCK_TIMEOUT → bounds the rare schema-stability (Sch-S) lock wait behind DDL so a
    ///     query aborts (Msg 1222) instead of hanging the whole run.
    ///
    /// ApplicationIntent=ReadOnly is deliberately NOT set — it is an Always-On read-routing hint,
    /// not a safety control, and would change which replica a query observes.
    /// </summary>
    public static class SqlSessionSafety
    {
        /// <summary>Shipped defaults, matching the CheckExecution:* configuration defaults.</summary>
        public const bool DefaultReadUncommitted = true;
        public const int DefaultLockTimeoutMs = 5000;

        /// <summary>
        /// Builds the session-options preamble prepended to a batch. SET statements return no
        /// result set, so ExecuteScalar/ExecuteReader still read the caller's own first rowset
        /// unchanged. A negative <paramref name="lockTimeoutMs"/> leaves the server default in
        /// place; <paramref name="readUncommitted"/> = false omits the isolation change.
        ///
        /// ANSI_WARNINGS is pinned ON unconditionally: sessions are shared through the connection
        /// pool. SqlConnectionPoolService resets session state on reuse, but the pin stays as
        /// defense-in-depth (deterministic per batch even if a connection reaches a caller without
        /// passing through the pool's reset path) — a check that sets it OFF and never restores it
        /// (SQLT-BPCHK-01470 does, to silence DBCC DBINFO aggregate warnings) poisons the session:
        /// every later check on it that invokes XML data-type methods (.exist/.value/.nodes;
        /// 14 checks in the corpus) then fails with Msg 1934. Pinning at batch start makes each
        /// caller's SET state deterministic regardless of what ran before.
        /// QUOTED_IDENTIFIER / ANSI_NULLS cannot be pinned here — they are parse-time options, so
        /// an in-batch SET does not affect the current batch (no shipped check turns them off;
        /// login default is ON).
        ///
        /// NOTE: the returned text is a statement preamble, so it must NOT be prepended to a batch
        /// whose first statement has to be first (CREATE/ALTER PROCEDURE, VIEW, TRIGGER, FUNCTION).
        /// Use <see cref="ApplyAsync"/> for those lanes instead.
        /// </summary>
        public static string BuildPrefix(
            bool readUncommitted = DefaultReadUncommitted,
            int lockTimeoutMs = DefaultLockTimeoutMs)
        {
            var sb = new StringBuilder();
            sb.Append("SET ANSI_WARNINGS ON;");
            if (readUncommitted)
                sb.Append("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;");
            if (lockTimeoutMs >= 0)
                sb.Append("SET LOCK_TIMEOUT ").Append(lockTimeoutMs).Append(';');
            sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>The shipped-default prefix, built once.</summary>
        public static readonly string DefaultPrefix = BuildPrefix();

        /// <summary>
        /// Applies the same options to an already-open connection as a standalone batch, so they
        /// hold for every later command on that session. This is the form to use when the caller
        /// runs script batches that must start with their own statement (CREATE PROCEDURE and
        /// friends), where prepending <see cref="BuildPrefix"/> would raise
        /// "CREATE/ALTER PROCEDURE must be the first statement in a query batch".
        /// </summary>
        public static async Task ApplyAsync(
            SqlConnection connection,
            bool readUncommitted = DefaultReadUncommitted,
            int lockTimeoutMs = DefaultLockTimeoutMs,
            CancellationToken cancellationToken = default)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = BuildPrefix(readUncommitted, lockTimeoutMs);
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
