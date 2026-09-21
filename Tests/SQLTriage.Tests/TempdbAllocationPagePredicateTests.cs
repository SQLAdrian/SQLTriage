/* In the name of God, the Merciful, the Compassionate */

// ── tempdb_contention's allocation-page filter (strings lane fix round, 2026-08-28) ────────────
//
// WHAT WAS WRONG. The lane's r2-11 fix gave tempdb_contention the database and page-type dimension
// sys.dm_os_wait_stats never had, by filtering sys.dm_os_waiting_tasks to database 2 and to
// allocation page numbers. The page arithmetic it shipped with was off by two:
//
//     p.pg = 2 OR (p.pg - 2) % 511232 = 0     -- GAM, as shipped
//     p.pg = 3 OR (p.pg - 3) % 511232 = 0     -- SGAM, as shipped
//
// A GAM interval is 511,232 pages, and the second GAM page is 511232 - not 511234. So the shipped
// predicate MISSED every GAM and SGAM page after the first pair and matched two ordinary data pages
// instead. Any tempdb data file over about 4 GB has those pages, and classic SGAM allocation
// contention on 2:1:511233 counted as zero while the alert's own description promised it was
// counting PFS, GAM and SGAM latch waits. Production tempdb files are routinely over 4 GB, so this
// was the common case and not the edge one. The PFS half (pg = 1 OR pg % 8088 = 0) was always right.
//
// THE TRUTH TABLE BELOW IS A MEASUREMENT, NOT A RECOLLECTION. On .\NEW2022 on 2026-08-28 a scratch
// database was created with a 4200 MB data file (instant file initialization confirmed enabled) and
// sys.dm_db_page_info was read directly for each page number:
//
//     1 PFS_PAGE   2 GAM_PAGE   3 SGAM_PAGE   8088 PFS_PAGE   16176 PFS_PAGE
//     511230 NULL  511231 NULL  511232 GAM_PAGE  511233 SGAM_PAGE
//     511234 NULL  511235 NULL  511236 NULL
//
// The scratch database was dropped and its files verified gone. That run also evaluated both the
// shipped and the corrected predicate against the same page numbers, which is what identified
// 511234/511235 as the two ordinary pages the shipped form matched.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SQLTriage.Data.Models;
using Xunit;

namespace SQLTriage.Tests
{
    public class TempdbAllocationPagePredicateTests
    {
        private static string ShippedQuery()
        {
            var json = File.ReadAllText(
                Path.Combine(RawPassedScan.RepoRoot().FullName, "Config", "alert-definitions.json"));
            var file = JsonSerializer.Deserialize<AlertDefinitionsFile>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(file);
            return file!.Alerts
                .Single(a => string.Equals(a.Id, "tempdb_contention", StringComparison.OrdinalIgnoreCase))
                .Query!;
        }

        /// <summary>
        /// The predicate as the shipped query carries it: everything after <c>p.pg IS NOT NULL AND</c>
        /// up to the trailing semicolon. Lifted from the file rather than retyped, so this suite can
        /// only ever be testing the text that installs.
        /// </summary>
        private static string PagePredicate()
        {
            var m = Regex.Match(ShippedQuery(), @"p\.pg IS NOT NULL AND\s*(\(.+\))\s*;", RegexOptions.Singleline);
            Assert.True(m.Success,
                "tempdb_contention's query no longer has the 'p.pg IS NOT NULL AND (...)' shape this "
                + "suite lifts its predicate from. Re-anchor it rather than deleting the tests.");
            return m.Groups[1].Value;
        }

        // ── 1. The always-on lint: the off-by-two form may not come back ────────────────────────

        [Fact]
        public void The_allocation_page_filter_uses_the_interval_and_not_an_offset_from_it()
        {
            var q = ShippedQuery();

            Assert.DoesNotContain("(p.pg - 2) % 511232", q, StringComparison.Ordinal);
            Assert.DoesNotContain("(p.pg - 3) % 511232", q, StringComparison.Ordinal);

            // The second GAM page is 511232 and the second SGAM page is 511233, so the interval is
            // taken modulo, not offset by the first pair's page numbers.
            Assert.Contains("p.pg % 511232 = 0", q, StringComparison.Ordinal);   // GAM
            Assert.Contains("p.pg % 511232 = 1", q, StringComparison.Ordinal);   // SGAM
            Assert.Contains("p.pg = 2", q, StringComparison.Ordinal);            // the first GAM
            Assert.Contains("p.pg = 3", q, StringComparison.Ordinal);            // the first SGAM
            Assert.Contains("p.pg % 8088 = 0", q, StringComparison.Ordinal);     // PFS, always correct
        }

        // ── 2. The predicate EXECUTED, against the measured truth table ─────────────────────────

        /// <summary>
        /// Runs the shipped predicate as SQL over the page numbers whose types were read from
        /// sys.dm_db_page_info (see this file's header) and asserts it selects exactly the allocation
        /// pages among them. Armed by SQLTRIAGE_LIVE_INSTANCE; reports SKIPPED, never a vacuous pass,
        /// when unset. RUN ARMED on 2026-08-28 against <c>.\NEW2022</c>.
        ///
        /// <para>The arithmetic needs no tempdb and no big file - only a SQL Server to evaluate it -
        /// which is why the 4200 MB scratch database belongs in the header as the provenance of the
        /// expected set, and not in the test body as a four-second setup on every run.</para>
        /// </summary>
        [LiveFact("SQLTRIAGE_LIVE_INSTANCE")]
        public void The_shipped_predicate_selects_exactly_the_measured_allocation_pages()
        {
            var target = Environment.GetEnvironmentVariable("SQLTRIAGE_LIVE_INSTANCE");
            Assert.False(string.IsNullOrWhiteSpace(target),
                "the attribute skips an unarmed run; this assertion is what makes a WEAKENED "
                + "attribute fail instead of passing on nothing");

            // page number -> is it an allocation page, per sys.dm_db_page_info on 2026-08-28
            var measured = new Dictionary<int, bool>
            {
                [1] = true,        // PFS_PAGE
                [2] = true,        // GAM_PAGE
                [3] = true,        // SGAM_PAGE
                [8088] = true,     // PFS_PAGE
                [16176] = true,    // PFS_PAGE
                [511230] = false,
                [511231] = false,
                [511232] = true,   // GAM_PAGE   - the shipped predicate MISSED this
                [511233] = true,   // SGAM_PAGE  - and this
                [511234] = false,  // and matched this instead
                [511235] = false,  // and this
                [511236] = false,
            };

            var values = string.Join(",", measured.Keys.Select(k => "(" + k + ")"));
            var sql = "SET NOCOUNT ON; SELECT p.pg FROM (VALUES " + values
                      + ") AS v(pg) CROSS APPLY (SELECT v.pg AS pg) AS p WHERE " + PagePredicate();

            var matched = new HashSet<int>();
            var csb = new SqlConnectionStringBuilder
            {
                DataSource = target!,
                InitialCatalog = "master",
                IntegratedSecurity = true,
                TrustServerCertificate = true,
                ConnectTimeout = 10,
            };

            using (var conn = new SqlConnection(csb.ConnectionString))
            {
                conn.Open();
                using var cmd = new SqlCommand(sql, conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) matched.Add(reader.GetInt32(0));
            }

            var expected = measured.Where(kv => kv.Value).Select(kv => kv.Key).OrderBy(x => x).ToArray();
            Assert.Equal(expected, matched.OrderBy(x => x).ToArray());
        }
    }
}
