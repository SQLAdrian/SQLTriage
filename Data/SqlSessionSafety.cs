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
    ///
    /// <para><b>⚠ THE SENTENCE ABOVE IS AN INVARIANT, AND FOR MONTHS NOTHING ENFORCED IT.</b>
    /// "every read this app issues" was prose here and true of three services;
    /// <c>Data/Services/AlertEvaluationService.cs</c> — the alert loop — issued SIX recurring
    /// background reads at monitored production servers on a timer and referenced this class nowhere,
    /// so those reads ran at the server default: shared locks, no lock timeout, free to block behind a
    /// client's own transaction. Fixed 2026-09-15. On this project the cost is not theoretical —
    /// PerfMon has already caused two client outages.</para>
    ///
    /// <para><b>THE GUARD, by its real name:</b> <c>SessionSafetyCensusTests</c> in
    /// <c>Tests/SQLTriage.Tests/SessionSafetyCensusTests.cs</c>, over
    /// <c>SessionSafetyAnalyzer</c>. It reads compiled IL (not source text) and fails when a method
    /// in the product assembly builds a SQL command on a connection that a MONITORED-SERVER
    /// connection string reaches - following that connection ACROSS method boundaries - without
    /// referencing this class. Its
    /// <c>The_analyser_flags_reached_sites_and_clears_guarded_and_local_ones</c> case keeps
    /// deliberately unguarded specimens compiled into the test assembly, so the census is proved
    /// able to show a positive rather than merely reporting zero - and a LOCAL-STORE specimen it
    /// must NOT report, so it is equally proved not to have started reporting everything.</para>
    ///
    /// <para>⚠⚠ <b>KNOW WHAT THAT GUARD DOES NOT SEE - RESTATED 2026-09-15, because the
    /// analyser was widened and the old warning is no longer true.</b> This paragraph used to say the
    /// guard's reach was "in the same method" and therefore <b>17%</b> of the surface - 49 of 291
    /// command-building methods, with <c>AccessSurfaceCollector</c>'s six commands and the whole of
    /// <c>XEventService</c> invisible to it. <b>THAT LIMITATION IS GONE.</b> The analyser now follows
    /// the connection through locals, fields, arguments and return values to a fixpoint, and both of
    /// those worked examples are in the census: 96 methods / 117 sites on the full profile, 73 / 94
    /// on community, measured 2026-09-15.
    ///
    /// <b>WHAT SURVIVES from the old warning</b>, stated as precisely as the retraction: the unit of
    /// analysis is still the METHOD, so a green census proves the preamble is referenced beside every
    /// monitored command built there, not that it was concatenated onto that exact command text;
    /// <c>ApplyAsync</c> coverage is judged per method rather than per connection object; and
    /// propagation stops at the assembly boundary. <b>And the walk OVER-approximates</b>: it is linear, taint
    /// on a local is never cleared, so a reported site is a LEAD, not a verdict - confirm which connection
    /// the command is really on before "fixing" it. The boundaries known TODAY are listed on
    /// <c>SessionSafetyAnalyzer</c>; that list is where they are kept honest, but do not read it as
    /// exhaustive - the cold gate refuted one of its own bullets on the day it was written. <b>So a green
    /// census still means "no KNOWN-SHAPED unguarded site", never "S-1 holds".</b></para>
    ///
    /// <para><b>IF YOU ADD A READ AGAINST A CLIENT SERVER, apply this class to it.</b> Either form
    /// works: <see cref="DefaultPrefix"/> / <see cref="BuildPrefix"/> concatenated onto the command
    /// text, or <see cref="ApplyAsync"/> on the connection.
    /// ⚠ An earlier version of this comment claimed the prefix form must be preferred on pooled
    /// connections because <see cref="ApplyAsync"/> "leaves state on a connection handed back for
    /// reuse". <b>That was FALSE and the cold gate refuted it live:</b> the prefix's SET statements are
    /// session-scoped exactly as <see cref="ApplyAsync"/>'s are — the forms differ only in that the
    /// prefix re-applies every batch. Leakage is bounded either way, because
    /// <c>SqlConnectionPoolService.SessionResetSql</c> restores READ COMMITTED and LOCK_TIMEOUT -1 on
    /// reuse and disposes any connection whose reset fails, and the non-pooled path gets ADO.NET's
    /// <c>sp_reset_connection</c>. Choose on batch position instead: <see cref="ApplyAsync"/> when the
    /// caller runs batches that must start with their own statement (CREATE/ALTER PROCEDURE), the
    /// prefix otherwise.</para>
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
