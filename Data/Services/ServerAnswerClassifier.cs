/* In the name of God, the Merciful, the Compassionate */

using System;
using Microsoft.Data.SqlClient;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// THE ONE place that decides whether a failure means "the server did not answer".
    ///
    /// <para><b>INVARIANT B (lane alert-correctness, 2026-09-19).</b> "The server refused me" and
    /// "the server answered with an error" are not "the server did not answer". Only a genuine
    /// no-answer counts toward opening <see cref="ServerCircuitBreakerService"/>, or marks a server
    /// Unreachable. Every breaker failure carries its cause
    /// (<see cref="ServerCircuitBreakerService.RecordFailure(string, Exception)"/>), and the breaker
    /// asks THIS class about it. Nothing classifies for itself.</para>
    ///
    /// <para><b>THE RULE.</b> The server answered when the cause is a <see cref="SqlException"/> and
    /// EVERY entry in its <see cref="SqlException.Errors"/> is below severity 20 AND is either a
    /// permission refusal (229, 297, 300) or a user-defined error (number 50000 or above, the range
    /// <c>THROW</c> requires). Everything else did not answer, which is the behaviour before this
    /// class existed. Measured on this box 2026-09-19 with SqlClient 6.17.26253.4 against
    /// <c>.\NEW2022</c> and <c>.\OLD2017</c> under <c>EXECUTE AS LOGIN = 'sqlt_plain'</c>:</para>
    /// <list type="bullet">
    /// <item>A VIEW SERVER STATE denial is ONE exception carrying 300 (severity 14) and then 297
    /// (severity 16), so <c>.Number</c> reads 300. That is why every entry is read, never
    /// <c>.Number</c> alone.</item>
    /// <item>229 (severity 14) for an object permission; 297 alone (severity 16) for the
    /// <c>index_fragmentation</c> query; <c>THROW 50001</c> arrives as 50001, severity 16.</item>
    /// <item>A COMMAND TIMEOUT is number -2, severity 11. It is not in the answered set, so a hung
    /// server stays a breaker failure and is still backed off.</item>
    /// <item>No answer at all is severity 20: 11001 for an unresolvable name, 258 or 1225 for a dead
    /// port depending on the connect timeout. The rule keys on "not in the answered set" and on
    /// severity 20 or above, never on one transport number.</item>
    /// </list>
    ///
    /// <para><b>WHY REFUSED MEANS REACHED.</b> The breaker exists to back off from servers that do
    /// not answer. A login without VIEW SERVER STATE is refused by most standard alert queries (42 of
    /// 65 on NEW2022 on 2026-09-18). Before this class every refusal was a breaker failure, so three
    /// in a row suppressed polling of a healthy server. A refusal is still an evaluation failure: the
    /// alert reads Unknown, and that is the caller's job, not this class's.</para>
    ///
    /// <para><b>NOT DECIDED HERE: whose fault a NON-SQL exception is.</b> That depends on where it was
    /// thrown, which only the call site knows. A slot the query orchestrator could not grant is ours
    /// (<see cref="SQLTriage.Data.Scheduling.QueryResult.FailedBeforeWorkStarted"/>), while a
    /// <c>CancelAfter</c> on a login turns a HUNG server into an <see cref="OperationCanceledException"/>
    /// that is a genuine no-answer (ConnectionHealthService). So a non-SQL cause reaching the breaker
    /// is a failure here, and a caller that can prove the exception is its own does not call the
    /// breaker at all (invariant B-prime).</para>
    ///
    /// <para>Census: ServerAnswerClassifierTests (the rule, with SqlExceptions built through
    /// SqlClient's own factory) and ServerAnswerClassifierLiveTests (the same rule against
    /// exceptions the two local servers really raise). The breaker's callers are enumerated by the
    /// compiler: <c>RecordFailure</c> has no overload without a cause.</para>
    /// </summary>
    public static class ServerAnswerClassifier
    {
        private static readonly int[] RefusalNumbers = { 229, 297, 300 };

        /// <summary>Permission refusals the server sends when it answered and said no. PROVED live.</summary>
        public static System.Collections.Generic.IReadOnlyList<int> PermissionRefusalNumbers => RefusalNumbers;

        /// <summary>The first user-defined error number. <c>THROW</c> requires 50000 or above.</summary>
        public const int FirstUserDefinedErrorNumber = 50000;

        /// <summary>Severity 20 and above is a fatal error: the connection is gone.</summary>
        public const byte FirstFatalSeverity = 20;

        /// <summary>True when <paramref name="cause"/> proves the server answered. See the class
        /// summary for the rule and the measurements behind it.</summary>
        public static bool ServerAnswered(Exception? cause)
        {
            if (cause is not SqlException sql) return false;
            if (sql.Errors.Count == 0) return false;

            foreach (SqlError error in sql.Errors)
            {
                if (!IsAnsweredError(error.Number, error.Class)) return false;
            }
            return true;
        }

        /// <summary>The per-entry rule, exposed so the rule itself can be tested entry by entry.</summary>
        internal static bool IsAnsweredError(int number, byte severity)
        {
            if (severity >= FirstFatalSeverity) return false;
            if (number >= FirstUserDefinedErrorNumber) return true;
            return Array.IndexOf(RefusalNumbers, number) >= 0;
        }
    }
}
