/* In the name of God, the Merciful, the Compassionate */

// ── lane logon-failure-root-cause, 2026-09-09: xp_readerrorlog's search strings must be nvarchar ──
//
// WHY THIS FILE EXISTS. The shipped logon_failure alert could not be measured on ANY monitored
// instance, every evaluation cycle, from ~60 s after the 2026-09-08 22:31 deploy of build 3995.
// The visibility lane made the client-side exception readable:
//
//     Microsoft.Data.SqlClient.SqlException: A severe error occurred on the current command.
//     The results, if any, should be discarded.        (Number 0, Class 11, State 0)
//
// THE MECHANISM, exercised on .\OLD2017 (14.0.2120.1) and .\NEW2022 (16.0.4262.2) on 2026-09-09:
//
//  1. xp_readerrorlog is an ODS extended stored procedure. It validates the TDS type of its
//     search-string parameters and requires nvarchar. A varchar argument - the bare literal
//     'Login failed', a VARCHAR variable, or a SqlDbType.VarChar parameter - is rejected with
//     Msg 22004, Level 12, State 1: "Error executing extended stored procedure: Invalid
//     Parameter Type". PROVED for all three argument shapes on both rigs.
//  2. That message is raised by the XP onto the client's TDS stream, NOT through the engine's
//     error subsystem. An Extended Events session capturing sqlserver.error_reported at
//     severity >= 11 recorded ZERO events for these calls on either rig; the ERRORLOG records
//     nothing and no SQLDump is written. So nothing server-side names the cause.
//  3. Wrapped in INSERT INTO @r EXEC xp_readerrorlog ... - the shape this handler runs - the XP's
//     output stream is consumed by the INSERT and the error token never reaches the client. What
//     the client gets is the batch's DONE token carrying the server-error / discard-results
//     status bit. The engine still books the batch as successful: XE sql_batch_completed
//     result = 0, row_count = 2, captured on the LIVE service's own logon_failure batch on both
//     rigs at 2026-09-08T19:22:29Z.
//  4. Microsoft.Data.SqlClient turns that status bit into a synthesized SqlException with
//     Number 0, Class 11, State 0 and the "severe error" text - a client-side error carrying no
//     server error number, which is exactly why the message named nothing.
//  5. sqlcmd (ODBC) does not surface that status bit at all: the identical statement prints no
//     message and returns Matches = 0 with exit 0. That is why six sqlcmd runs could not
//     reproduce the failure for ANY variant, and why the theory looked refuted. Worse for an
//     ODBC consumer: it is a silent FALSE ZERO - in the same five-minute window on .\OLD2017 the
//     N-prefixed control returned 2 matching rows where the bare-literal statement reported 0
//     (on .\NEW2022 both returned 0 in that window; the discriminator that holds on BOTH rigs is
//     the un-wrapped call's Msg 22004 against the wrapped call's silence - cold-gate defect 1).
//
// The 2x2 isolates the prefix from the content: bare 'Severity:' FAILS and N'Login failed'
// SUCCEEDS, so the fault is the literal's type, not which log lines match. The two sibling
// error_log_scan alerts survived only because error_log_severity passes N'Severity:' and
// error_log_fatal passes NULL.
//
// WHAT THIS FILE PINS. Not just the one character. Every string-shaped argument this handler
// passes to xp_readerrorlog, for every alert id it maps, must be NULL, a variable, or an
// N-prefixed literal. Add a fourth error_log_scan alert with a bare literal and this goes red.
//
// Census, same shape, over the tree at 4108cda: this was the ONLY site. Data/Services/
// AlertEvaluationService.cs:1068 (xp_instance_regread) and AgentMailChainProbe.cs:361/396/398
// already N-prefix their literals; AccessSurfaceCollector.cs:382 passes @acctname via
// AddWithValue(string), which SqlClient sends as nvarchar - PROVED to succeed live. The bare
// literals at SqlAssessmentService.cs:787 and in Config/alert-definitions.json's
// windows_power_plan query go to xp_regread / xp_instance_regread, which were PROVED on both
// rigs to ACCEPT varchar arguments - so they are not this defect and were deliberately not
// touched. The strictness is per-XP, not a general ODS rule.

using System;
using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class ErrorLogScanUnicodeArgumentTests
    {
        /// <summary>Every alert id BuildErrorLogScanSql maps to an xp_readerrorlog statement.</summary>
        public static IEnumerable<object[]> ScanAlertIds() => new[]
        {
            new object[] { "error_log_severity" },
            new object[] { "error_log_fatal" },
            new object[] { "logon_failure" },
        };

        [Fact]
        public void LogonFailure_searchString_isAUnicodeLiteral()
        {
            var sql = AlertEvaluationService.BuildErrorLogScanSql("logon_failure");

            Assert.NotNull(sql);
            // The defect: a bare varchar literal, which xp_readerrorlog rejects with Msg 22004
            // and which INSERT ... EXEC converts into an unnamed "severe error" at the client.
            Assert.DoesNotContain(", 'Login failed'", sql!, StringComparison.Ordinal);
            Assert.Contains("N'Login failed'", sql!, StringComparison.Ordinal);
        }

        [Theory]
        [MemberData(nameof(ScanAlertIds))]
        public void EveryErrorLogScanArgument_isNvarcharSafe(string alertId)
        {
            var sql = AlertEvaluationService.BuildErrorLogScanSql(alertId);
            Assert.NotNull(sql);

            var args = ReadErrorLogArguments(sql!);
            // 0 = log number, 1 = log file type, 2 = search string 1, 3 = search string 2,
            // 4 = start datetime, 5 = end datetime.
            Assert.True(args.Count >= 4, $"{alertId}: expected at least 4 xp_readerrorlog arguments, got {args.Count}: {string.Join(" | ", args)}");

            foreach (var arg in args)
            {
                if (!arg.Contains('\'')) continue;   // numeric, NULL or a variable - no literal here
                Assert.True(
                    arg.StartsWith("N'", StringComparison.Ordinal),
                    $"{alertId}: xp_readerrorlog argument {arg} is a non-Unicode literal. " +
                    "The XP rejects varchar with Msg 22004 and INSERT ... EXEC turns that into an " +
                    "unnamed SqlException the operator cannot diagnose. Prefix it with N.");
            }
        }

        /// <summary>
        /// Splits the argument list of the single <c>EXEC xp_readerrorlog ...</c> call in
        /// <paramref name="sql"/> on top-level commas, trimming each argument. The generated
        /// statements carry no commas inside their literals; the assertion below fails loudly if
        /// a future one does, rather than silently mis-splitting.
        /// </summary>
        private static List<string> ReadErrorLogArguments(string sql)
        {
            const string marker = "EXEC xp_readerrorlog";
            var start = sql.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            Assert.True(start >= 0, "no EXEC xp_readerrorlog found in: " + sql);
            Assert.True(
                sql.IndexOf(marker, start + marker.Length, StringComparison.OrdinalIgnoreCase) < 0,
                "more than one EXEC xp_readerrorlog in one statement; this splitter reads only the first");

            start += marker.Length;
            var end = sql.IndexOf(';', start);
            Assert.True(end > start, "the xp_readerrorlog call is not terminated by ';'");

            var argList = sql.Substring(start, end - start);
            Assert.True(
                argList.Count(ch => ch == '\'') % 2 == 0,
                "unbalanced quote in the xp_readerrorlog argument list: " + argList);

            return argList.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        }
    }
}
