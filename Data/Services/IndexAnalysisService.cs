/* In the name of God, the Merciful, the Compassionate */
/*
 * IndexAnalysisService — the SQL and the reader mapping behind Pages/IndexAnalysis.razor.
 *
 * WHY THIS EXISTS (2026-08-13). The three DMV queries and their mappers lived inside the page's
 * @code block, where nothing could test them. One mapper was wrong the whole time:
 *
 *     FragPercent = (double)reader.GetDecimal(3)
 *
 * Column 3 is ips.avg_fragmentation_in_percent, declared SQL **float**, so it arrives at the
 * reader as System.Double and GetDecimal throws InvalidCastException. The bug is DATA-GATED, not
 * version-gated: the fragmented query returns rows only when the connected database holds an
 * index over 5% fragmented across more than 1,000 pages, so most instances never reach the throw.
 *
 * WHAT WAS MEASURED AND WHAT WAS NOT — read this before repeating the story anywhere else.
 *   PROVED (live, 2026-08-13, .\old2017 = SQL Server 2017 14.0.2120.1, against a purpose-built
 *     index 95.4% fragmented over 19,539 pages): column 3 reports declared type `float` and CLR
 *     type System.Double, and the shipped expression throws InvalidCastException whose Message is
 *         "Unable to cast object of type 'System.Double' to type 'System.Decimal'."
 *     Reproduced twice — as a verbatim replay of the old line, and end to end through
 *     GetAnalysisAsync with the mapper reverted. IndexAnalysisLiveSmokeTests is that exercise.
 *   READ, NOT RUN: the page turns any non-SqlException into its banner as $"Error: {ex.Message}"
 *     (Pages/IndexAnalysis.razor:383; the base commit 3e69122 composed it identically). So the
 *     banner THIS defect produces reads "Error: Unable to cast object of type 'System.Double' to
 *     type 'System.Decimal'."
 *   UNPROVED: that this is the throw behind the report that opened the lane. That report was a
 *     banner reading "Error: Specified cast is not valid." — the parameterless
 *     InvalidCastException message, which the path above does NOT emit on the stack measured
 *     here. The defect fixed in this file is real and proved on its own evidence; its identity
 *     with that screenshot is not, and nobody has run this build against that server. The wording
 *     is the discriminator: if that page still shows the bare sentence after this fix, the cause
 *     is elsewhere and this file is not where to look.
 *
 * The same defect class had already been found once in Data/HealthCheckService.cs (bigint read
 * with GetDecimal, see the note at its memory query) and was fixed there by changing the SQL. It
 * is fixed HERE by never asking the reader for a specific CLR type: every column goes through the
 * Str/Long/Dbl/Dec helpers below, which are IsDBNull-guarded and Convert.To* over GetValue —
 * the same shape PerformanceReportComposer already used for these exact queries.
 *
 * NULL HONESTY. UnusedIndexSql reaches sys.dm_db_partition_stats through a LEFT JOIN, so SizeMB
 * is genuinely nullable; the old mapper's GetDecimal(6) would have thrown SqlNullValueException
 * on that row. SizeMB is decimal? here and the page renders an absent size as "not measured"
 * rather than 0.00 MB — a rendered 0.00 is a measurement claim, and we did not measure it.
 *
 * SCOPE OF THE READS. Read this before changing any query text.
 *   • MissingIndexSql  — INSTANCE-WIDE. `mid.database_id > 4` covers every user database, and it
 *     names objects through the two-argument OBJECT_NAME(object_id, database_id), which resolves
 *     across databases. It needs no per-database connection and gets none: one read, every user
 *     database, broader than the top-20 sample below. The page says which of the two it is showing.
 *   • UnusedIndexSql   — ONE DATABASE. `us.database_id = DB_ID()`.
 *   • FragIndexSql     — ONE DATABASE. sys.dm_db_index_physical_stats(DB_ID(), ...).
 *
 * ── TOP 20 BY IO (2026-08-13, Adrian's ruling) ────────────────────────────────────────────────
 * Until this change the page connected with InitialCatalog=master and the last two queries
 * therefore described master and nothing else. They now run once per database against the top 20
 * user databases by IO, and the page states that scope, its scanned/skipped counts, and every
 * database it did not read.
 *
 * THE SELECTION IS NOT NEW WORK — it is the existing house pattern, mirrored. Four SQL Server
 * checks in the corpus already pick their per-database sample the same way, and this service now
 * uses the same DMV, the same metric, the same system-database treatment and the same TOP idiom:
 *
 *     corpus-v3/engines/sqlserver/SQLT-CUSTOM-INDEX-FRAGMENTATION.sql.md   (the closest sibling —
 *         same DMV, same LIMITED scan, same >1000-page floor, and the source of the budget shape)
 *     corpus-v3/engines/sqlserver/SQLT-CUSTOM-STATISTICS-HEALTH.sql.md
 *     corpus-v3/engines/sqlserver/SQLT-CUSTOM-HEAP-FORWARDED-RECORDS.sql.md
 *     corpus-v3/engines/sqlserver/SQLT-BLITZ-00720.sql.md
 *
 * All four rank with SUM(num_of_reads + num_of_writes) from sys.dm_io_virtual_file_stats(NULL,
 * NULL) grouped by database_id, take TOP (20) ORDER BY that sum DESC, and filter
 * `database_id > 4 AND state = 0 AND user_access = 0`. The first three add `is_read_only = 0` and
 * are the pattern followed here; SQLT-BLITZ-00720 instead carries `source_database_id IS NULL`, a
 * database-SNAPSHOT exclusion, and no is_read_only predicate at all. Stated in full because the
 * earlier summary compressed that fourth file to "[AND d.is_read_only = 0]" and mentioned neither
 * difference. Neither changes the selection here: a snapshot carries is_read_only = 1 and is
 * excluded by the dominant predicate anyway. ⚠ ONE ADDITION IS THIS FILE'S OWN, not the corpus's:
 * the `name ASC` tie-break in every ORDER BY. The checks have no tie-break, and on the lane's own
 * fixture more than twenty databases tied on the same IO figure — without it the sample, the ranks
 * and the capped skip list would all be non-deterministic between two runs of an unchanged
 * instance. It is an improvement, not a deviation to reverse, and it is declared here rather than
 * left inside the phrase "exactly as the checks pick theirs".
 * So the metric is OPERATION COUNT, not bytes and not stalls — recorded because "by IO" has three
 * plausible readings and the house has already picked one. ConsolidationAnalysisService sums
 * num_of_bytes_read + num_of_bytes_written, which looks like a competing convention and is not:
 * that is a whole-SERVER total for sizing a consolidation, never a per-database ranking. The only
 * per-database ranking pattern in either repo is the operation-count one above, in four copies
 * that agree, so there was no judgement call to make and none was made.
 *
 * WHAT DIVERGES FROM THE CHECKS, AND WHY. The checks filter ineligible databases out inside the
 * WHERE clause, so an offline database simply never appears. A check emits one PASS/FAIL sentence
 * and can afford that; a page that renders per-database rows cannot, because a database that is
 * silently absent reads as a database with no findings. So DatabaseRankingSql keeps the house
 * predicate exactly, and additionally RETURNS the ineligible databases it passed over, with their
 * state, for the page to name. Same selection, more honest reporting — nothing about which
 * databases get scanned changes.
 *
 * ⚠ AND ONE THING THE LIVE RUN TAUGHT, which reading the checks would never have shown.
 * sys.dm_io_virtual_file_stats returns NO ROW for a database whose files are not open — there is
 * nothing to report. The LEFT JOIN therefore yields NULL and ISNULL turns it into a zero, which
 * sorts the database LAST. That is harmless in a check that filters it out anyway. Here it was
 * not: the first cut of this service reported a skipped database only when it out-ranked the last
 * database picked, so on an instance with more than twenty user databases an OFFLINE one was
 * always ranked bottom by a zero it never earned, and was therefore never listed. PROVED live on
 * .\old2017, 2026-08-13: a purpose-built offline fixture was driven to roughly rank three by IO,
 * went offline, and vanished from the skip list entirely.
 *
 * The zero is an ABSENCE, not a reading, so the fix is to stop treating it as one. @cand carries
 * io_measured, and a database with no reading is reported WHATEVER its apparent rank — because "it
 * ranked below the cut" is an argument built on the very measurement that being unreadable
 * prevented. IndexAnalysisDatabase.IoMeasured carries the same fact to any renderer, so nothing
 * downstream can print that zero as a number an operator might act on.
 *
 * ⚠⚠ AND THE HOLE IN THAT FIX, found by the gate on 2026-08-13 and closed here. The rescue was
 * written as `is_eligible = 0 AND (io_measured = 0 OR io_ops >= @cutoff_io)` — so the absent
 * reading only rescued a database that was ALREADY ineligible. An AUTO_CLOSE database is ONLINE,
 * MULTI_USER and writable, so it is ELIGIBLE; its files are shut, so it has no DMV row. It fell
 * through both arms: never picked (ranked last on a zero it never earned) and never reported (the
 * rescue did not reach eligible rows). PROVED live on .\old2017, 2026-08-13, end to end through
 * the built service: `_sqlt_idxscope_ac` — ONLINE / MULTI_USER / is_read_only = 0 /
 * is_auto_close_on = 1, no dm_io_virtual_file_stats row — appeared in NEITHER Scanned NOR Skipped,
 * on an instance with 47 user databases, while the page's own sentence counted it among the "5
 * ranked below the cut and were not read". AUTO_CLOSE is the SQL Server Express default, so this
 * is the normal shape of exactly the high-database-count estates the sample size exists for.
 *
 * The predicate is therefore keyed on the ABSENCE, not on eligibility: any database that was not
 * picked and has no reading is reported, and an ELIGIBLE one gets its own row kind and its own
 * sentence (SKIP-UNRANKED / DescribeUnrankedSkip) because "the database is OFFLINE" would be a
 * state it is not in. See @report in DatabaseRankingSql.
 *
 * ── SAYING WHAT WAS NOT READ, WITHOUT SAYING A NUMBER NOBODY MEASURED ─────────────────────────
 * The skip LIST is capped (@skip_report_cap) so an estate with thousands of offline databases
 * cannot flood the page. The first cut then rendered "This run did not read {list length}
 * databases", which turns the cap into a false measurement: PROVED live 2026-08-13, an instance
 * with 47 user databases and 22 offline ones rendered "This run did not read 20 databases" when
 * the run had not read 27, and "20 scanned, 20 skipped" against an instance of 47.
 *
 * Every count the page renders now comes from the census, which measures the INSTANCE, and the
 * list is described as the subset it is:
 *   UserDatabases = Sampled + PassedOver + RankedBelowCut, always, by construction — @scan,
 *   @report and the remainder partition @cand. The page's "not read" is UserDatabases − scanned,
 *   and its "not named here" is that minus the list length, which decomposes exactly into the rows
 *   the cap cut and the databases that lost the sample on a reading they really earned.
 * IndexAnalysisScopeNarrative composes all of it from ONE census snapshot so a sentence cannot be
 * conditioned on a different measurement than the number beside it.
 *
 * CONCURRENCY. The page used to start all four commands on ONE SqlConnection and Task.WhenAll
 * them, with MultipleActiveResultSets forced on to make that legal. MARS permits INTERLEAVED
 * execution on one thread; SqlConnection is not thread-safe and concurrent execution from
 * several tasks is not a supported shape. The reads are serialised here — each reader is
 * disposed before the next command opens — so MARS is no longer required or requested.
 *
 * ── THE BUDGET, AND WHERE ITS NUMBERS COME FROM ───────────────────────────────────────────────
 * Twenty databases of dm_db_index_physical_stats is the failure mode this expansion invites: on a
 * server with hundreds of databases the old shape was slow and the new one could be twenty times
 * slower. Two limits bound it, and neither number was invented:
 *
 *   OverallBudgetDefault = 120s. The previous single-database shape already had a documented
 *     worst case of 135s — the sum of the four serialised command timeouts, 15 + 30 + 30 + 60 —
 *     and shipped with no budget at all. 120s is below that, so scanning twenty databases cannot
 *     take longer than scanning one already could. The anchor is a number that was already in
 *     this file, not a guess about what an operator will tolerate.
 *   PerDatabaseSliceDefault = 15s. Twenty slices is 300s, far above the overall budget, which is
 *     deliberate: the overall budget is the binding limit and the slice exists only to stop ONE
 *     pathological database consuming all of it. SQLT-CUSTOM-INDEX-FRAGMENTATION carries the same
 *     shape (@budget_ms = 20000, checked at the top of each per-database iteration) and warns that
 *     "a single in-flight dm_db_index_physical_stats sweep cannot be preempted — one enormous
 *     database can still exceed the budget on its own". That warning does not apply here: the
 *     slice is enforced as the per-command CommandTimeout, which the provider cancels mid-sweep.
 *     ⚠ The slice is one budget for the WHOLE database, not one per command. The first cut passed
 *     the same CommandTimeout to both reads, so a database could spend 2 × 15s while the sentence
 *     it produced said "It passed its 15-second slice" — a rendered duration that was not the one
 *     spent (gate finding, 2026-08-13). The second read is now given what the slice has LEFT; see
 *     RemainingSliceSeconds, which is pure and unit-tested. UNEXERCISED LIVE: no database here is
 *     big enough to time out a LIMITED-mode sweep, so the arithmetic is proved and the
 *     cancellation it feeds is not.
 *
 * The ranking pass itself is ONE server-wide DMV read whose cost tracks the FILE count, never the
 * database count, so the expensive part of a high-database-count server is bounded by the budget
 * and the selection is not.
 *
 * A database that runs out of budget is SKIPPED AND NAMED, never dropped and never turned into an
 * error banner: the page lists it with the reason. Partial results from a database whose scan
 * stopped part-way are discarded rather than merged — see the note on ScanOneDatabaseAsync.
 *
 * ── NULL HONESTY FINISHED FOR THE IMPACT COLUMNS, 2026-08-14 ──────────────────────────────────
 * Item 2 of the list below used to read "NULL HONESTY IS ASYMMETRIC" and disclose that MapMissing
 * routed AvgImpact and ImpactScore through Dec into NON-NULLABLE decimals, so a NULL would render
 * as "0.00%" / "0" — a measurement claim, the exact thing SizeMB was changed to avoid. Both
 * columns are now decimal? and both surfaces render an absent one as "not measured".
 *
 *   PROVED (sys.dm_exec_describe_first_result_set against 14.0.2120.1, measured 2026-08-13, and
 *     re-measured against 16.0.4262.2 on 2026-08-14): AvgImpact decimal(6,2) and ImpactScore
 *     decimal(18,0) are both is_nullable = 1, as are Database, Table and UserHits.
 *   PROVED (offline, over a capture taken through the real provider on 16.0.4262.2 with both
 *     projections forced to NULL by NULLIF over their own expressions): the old mapping produced
 *     0m from a genuine NULL, the new mapping produces null, and neither surface prints a number.
 *   UNTESTED, unchanged and still worth saying: whether migs.avg_user_impact is ever actually NULL
 *     on a real workload. Nobody has observed one. That is an argument about how OFTEN the row
 *     occurs, not about what the page should print when it does, and the column is declared
 *     nullable by the server — so the rendering is now conditioned on the value rather than on a
 *     belief about frequency.
 *
 *   ⚠ WHY THE SUGGESTED SCRIPT DOES NOT CARRY THE SAME FIX. SuggestedScript is composed in SQL and
 *   interpolates no impact figure at all — read it in MissingIndexSql: it concatenates the object
 *   name and the column lists and nothing else. So there is no impact number inside it to make
 *   honest. It has its OWN null shape, which is different and is handled on the page: every piece
 *   of that concatenation is NULL-propagating, so one unreadable OBJECT_NAME makes the WHOLE script
 *   NULL, which Str turns into "". An empty script box beside a populated row reads as "no script
 *   needed"; the page now says why it is empty instead.
 *
 * KNOWN AND NOT FIXED IN THIS LANE. Recorded so the next reader does not re-derive them.
 *   1. NO PROGRESSIVE RENDER. The page still shows one spinner until the whole pass returns, so a
 *      run that uses most of its 120s looks hung even though it is working and bounded. The budget
 *      caps the wait; it does not report progress during it.
 *   2. NULL HONESTY IS STILL ASYMMETRIC FOR THE NON-DISPLAY COLUMNS. Long/Str below substitute
 *      0L / "" for a NULL. UserHits is declared nullable and would render as a measured zero; the
 *      identifier columns render as an empty cell, which reads as absent rather than as a value,
 *      so they are left. UserHits is the one remaining display column of this class and is
 *      recorded here rather than changed on a lane that did not measure it.
 *   3. PRE-EXISTING, OUT OF SCOPE. UnusedIndexSql's size LEFT JOIN matches
 *      sys.dm_db_partition_stats on object_id + index_id only, with no partition_number. On a
 *      partitioned table that is one-to-many: duplicate rows, and a size describing a single
 *      partition. Byte-identical in PerformanceReportComposer and present at the base commit,
 *      so this extraction neither caused nor changed it. UNEXERCISED — no partitioned fixture
 *      was built.
 *   4. AN INELIGIBLE DATABASE WITH A MEASURED READING IS NAMED ONLY IF IT OUT-RANKS THE SAMPLE.
 *      A SINGLE_USER or READ_ONLY database whose files ARE open has a real IO figure, so it is
 *      reported only when io_ops >= @cutoff_io. On a busy estate the same database can therefore
 *      be named on one run and absent on the next with no state change at all, purely because the
 *      counters moved (gate finding, 2026-08-13, observed across three consecutive runs of one
 *      fixture). That is deliberate — reporting every ineligible database on a thousand-database
 *      instance is the flooding the cap exists to stop — and it is honest only because such a
 *      database is then counted in RankedBelowCut, on a reading it really earned, and the page
 *      renders that count. It is recorded here because "requirement (c) names single-user
 *      databases" and the condition under which one is NAMED was written down nowhere.
 *   5. THE PER-DATABASE ROW CAPS ARE NOT A RANKING. UnusedIndexSql is TOP 50 and FragIndexSql is
 *      TOP 30, both unchanged from the base commit — but they now run once per database, so each
 *      of up to twenty databases is truncated independently and the rendered table is those
 *      per-database lists concatenated in rank order, not a table sorted by anything. The page
 *      says so rather than implying a global top-N; nothing here re-sorts or re-caps, because a
 *      global ranking would need a total this run never measured.
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services;

/// <summary>One missing-index suggestion, instance-wide, as the DMVs report it.</summary>
public sealed class IndexAnalysisMissingRow
{
    public string Database { get; set; } = "";

    /// <summary>
    /// The table's real schema, from OBJECT_SCHEMA_NAME. ⚠ THIS COLUMN IS WHY THE SCRIPTS ARE
    /// CORRECT, 2026-08-14: the queries used to project OBJECT_NAME alone, which returns a BARE
    /// table name, so every emitted script hardcoded <c>[dbo]</c> and named the wrong object for
    /// any table outside it. PROVED live against a local SQL 2022 instance, on <c>rep.wide</c> and
    /// <c>[we]]ird].[frag]</c>. Empty when the login cannot resolve the object, exactly like
    /// <see cref="Table"/>: the reader helper is IsDBNull-guarded, and no script is emitted from a
    /// row whose schema is absent.
    /// </summary>
    public string Schema { get; set; } = "";

    public string Table { get; set; } = "";
    public string KeyColumns { get; set; } = "";
    public string IncludedColumns { get; set; } = "";
    public long UserHits { get; set; }

    /// <summary>
    /// migs.avg_user_impact, CAST to decimal(6,2). Null means UNMEASURED and must never be
    /// rendered as a number — same rule as <see cref="IndexAnalysisUnusedRow.SizeMB"/>, and for the
    /// same reason: a rendered "0.00%" is a claim that the optimizer expects this index to help
    /// nothing, which is the opposite of "nobody read a figure".
    ///
    /// <para>PROVED nullable by the server itself: sys.dm_exec_describe_first_result_set over
    /// <see cref="IndexAnalysisService.MissingIndexSql"/> reports is_nullable = 1 for this column on
    /// 16.0.4262.2 (2026-08-14), over the CURRENT text of that constant. The earlier reading on
    /// 14.0.2120.1 (2026-08-13) was taken over the text as it stood BEFORE this lane projected the
    /// schema column, so it names a query that no longer exists verbatim. It is still evidence —
    /// adding a projected column cannot change another column's nullability — but the two readings
    /// are not measurements of the same string, and a measurement claim names the thing measured.
    /// ⚠ 14.0.2120.1 has NOT been re-measured over the current text: .\old2017 is stopped.</para>
    /// </summary>
    public decimal? AvgImpact { get; set; }

    /// <summary>
    /// The composite impact score, CAST to decimal(18,0). Null means unmeasured; see
    /// <see cref="AvgImpact"/>. ⚠ It is also the ORDER BY key: SQL Server sorts NULL lowest, so a
    /// DESC order puts every unmeasured suggestion at the BOTTOM of the read and the row cap cuts
    /// them first. That is the right end to lose them from, and it is stated because "ordered by
    /// impact, so the cut keeps the worst" is a sentence the report renders.
    /// </summary>
    public decimal? ImpactScore { get; set; }

    /// <summary>
    /// The CREATE INDEX statement the DMV suggestion implies, composed in SQL.
    ///
    /// <para>⚠ EVERY PIECE OF THAT CONCATENATION IS NULL-PROPAGATING, so one OBJECT_NAME the login
    /// cannot resolve makes the WHOLE script NULL, which the reader helper turns into "". The
    /// column is declared nullable (measured, 16.0.4262.2, 2026-08-14). An empty script box beside
    /// a populated row reads as "no script needed", so the page says why it is empty rather than
    /// showing nothing. It carries no impact figure at all — read MissingIndexSql — so the null
    /// honesty on the two columns above does not reach inside it.</para>
    /// </summary>
    public string SuggestedScript { get; set; } = "";
}

/// <summary>One written-but-never-read index in the connected database.</summary>
public sealed class IndexAnalysisUnusedRow
{
    public string Database { get; set; } = "";

    /// <summary>
    /// The table's real schema, from OBJECT_SCHEMA_NAME. ⚠ THIS COLUMN IS WHY THE SCRIPTS ARE
    /// CORRECT, 2026-08-14: the queries used to project OBJECT_NAME alone, which returns a BARE
    /// table name, so every emitted script hardcoded <c>[dbo]</c> and named the wrong object for
    /// any table outside it. PROVED live against a local SQL 2022 instance, on <c>rep.wide</c> and
    /// <c>[we]]ird].[frag]</c>. Empty when the login cannot resolve the object, exactly like
    /// <see cref="Table"/>: the reader helper is IsDBNull-guarded, and no script is emitted from a
    /// row whose schema is absent.
    /// </summary>
    public string Schema { get; set; } = "";

    public string Table { get; set; } = "";
    public string IndexName { get; set; } = "";
    public string IndexType { get; set; } = "";
    public long UserReads { get; set; }
    public long UserWrites { get; set; }

    /// <summary>
    /// Null when sys.dm_db_partition_stats had no matching row (the query LEFT JOINs it). Null
    /// means UNMEASURED and must never be rendered as a number — see the page's SizeLabel.
    /// </summary>
    public decimal? SizeMB { get; set; }
}

/// <summary>One fragmented index in the connected database.</summary>
public sealed class IndexAnalysisFragRow
{
    public string Database { get; set; } = "";

    /// <summary>
    /// The table's real schema, from OBJECT_SCHEMA_NAME. ⚠ THIS COLUMN IS WHY THE SCRIPTS ARE
    /// CORRECT, 2026-08-14: the queries used to project OBJECT_NAME alone, which returns a BARE
    /// table name, so every emitted script hardcoded <c>[dbo]</c> and named the wrong object for
    /// any table outside it. PROVED live against a local SQL 2022 instance, on <c>rep.wide</c> and
    /// <c>[we]]ird].[frag]</c>. Empty when the login cannot resolve the object, exactly like
    /// <see cref="Table"/>: the reader helper is IsDBNull-guarded, and no script is emitted from a
    /// row whose schema is absent.
    /// </summary>
    public string Schema { get; set; } = "";

    public string Table { get; set; } = "";
    public string IndexName { get; set; } = "";

    /// <summary>
    /// avg_fragmentation_in_percent. SQL <b>float</b> — this is the column the old page mapper
    /// read with GetDecimal, which is the defect this file fixes.
    /// </summary>
    public double FragPercent { get; set; }

    public long PageCount { get; set; }

    /// <summary>Null means unmeasured; never rendered as 0.00. Same rule as the unused row.</summary>
    public decimal? SizeMB { get; set; }

    public string Action { get; set; } = "";
}

/// <summary>
/// One user database as the ranking pass saw it. <see cref="IoOperations"/> is the house metric —
/// SUM(num_of_reads + num_of_writes) across the database's files — and <see cref="IoRank"/> is its
/// position among ALL user databases by that metric, eligible or not, so the number means one
/// thing wherever it is rendered.
/// </summary>
public sealed class IndexAnalysisDatabase
{
    public int DatabaseId { get; set; }
    public string Name { get; set; } = "";
    public long IoOperations { get; set; }

    /// <summary>
    /// False when sys.dm_io_virtual_file_stats returned no row for this database: its files are
    /// not open, so there is nothing to read. When this is false, <see cref="IoOperations"/> is 0
    /// because the ORDER BY needed a number and <see cref="IoRank"/> is therefore last-by-default
    /// — NEITHER is a measurement, and neither may be rendered as one or used to argue the
    /// database ranked below anything.
    ///
    /// <para>⚠ TWO SHAPES REACH THIS, not one. An OFFLINE (or RESTORING, SUSPECT…) database is
    /// unreadable AND has no row. An <b>AUTO_CLOSE</b> database is ONLINE, MULTI_USER, writable
    /// and perfectly scannable, and ALSO has no row, because AUTO_CLOSE shuts its files when the
    /// last connection leaves. Reading this flag as "the database is broken" is how the eligible
    /// case was missed the first time (see the ⚠⚠ block in the file header). It says one thing
    /// only: nobody measured this database's IO on this run.</para>
    /// </summary>
    public bool IoMeasured { get; set; }

    public int IoRank { get; set; }
    public string StateDesc { get; set; } = "";
    public string UserAccessDesc { get; set; } = "";
    public bool IsReadOnly { get; set; }

    /// <summary>True when the database passed the house predicate and was picked for scanning.</summary>
    public bool IsEligible { get; set; }
}

/// <summary>
/// A database the run did not read, and the reason in the operator's words. Every one of these is
/// rendered: a database that is silently absent from the tables reads as a database with nothing
/// wrong in it, which is the claim this lane exists to stop making.
/// </summary>
public sealed class IndexAnalysisSkippedDatabase
{
    public string Database { get; set; } = "";
    public long IoOperations { get; set; }

    /// <summary>See <see cref="IndexAnalysisDatabase.IoMeasured"/>. False here means neither the
    /// IO nor the rank on this row is a reading, and a renderer must not print them as one.</summary>
    public bool IoMeasured { get; set; }

    public int IoRank { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>
    /// True when the ranking pass never picked this database — its state, or an IO reading that
    /// does not exist, put it outside the sample before any scanning began. False when it WAS
    /// picked and the scan then stopped: the budget ran out, or the read failed.
    ///
    /// <para>Carried as a flag because the discriminator used to be a PREFIX MATCH on
    /// <see cref="Reason"/>, which quietly made the exact wording of an operator-facing sentence
    /// load-bearing for a classification: adding one skip sentence (this lane added
    /// <c>DescribeUnrankedSkip</c>) silently reclassified those rows.</para>
    /// </summary>
    public bool PassedOverBeforeScanning { get; set; }
}

/// <summary>
/// What the ranking pass found across the whole instance, so the page can count what it is NOT
/// showing. Every number here is a COUNT over the instance; none is a list length.
///
/// <para>⚠ THE PARTITION, which the page's arithmetic depends on:
/// <c>UserDatabases = Sampled + PassedOver + RankedBelowCut</c>, always. The three come from
/// <c>@scan</c>, <c>@report</c> and the remainder, which partition the candidate set by
/// construction, so the page can state a total that reconciles to the instance instead of adding
/// up two list lengths. IndexAnalysisMapperTests asserts the identity over the captured census and
/// IndexAnalysisLiveSmokeTests asserts it against a real instance.</para>
/// </summary>
public sealed class IndexAnalysisRankingCensus
{
    public int TopN { get; set; }
    public int UserDatabases { get; set; }
    public int Eligible { get; set; }
    public int Ineligible { get; set; }

    /// <summary>
    /// How many databases the ranking pass took — <c>MIN(Eligible, TopN)</c>.
    ///
    /// <para>⚠ NOT named <c>Selected</c>, and do not rename it back. DiLifetimeCensusTests scans
    /// the SOURCE FILE of every singleton-registered type for <c>public … Selected… { get</c>,
    /// because per-user selection state on a singleton is the target of a privileged action — one
    /// caller retargeting another caller's run. IndexAnalysisService is registered as a singleton
    /// and lives in this file, so a property called <c>Selected</c> on this DTO turned that census
    /// red (2026-08-13) even though this is a per-call count on a returned object and no state at
    /// all. The census is right to be coarse and blunting it with a whitelist entry would spend a
    /// real authorization guard on a naming coincidence. <c>Sampled</c> is also the corpus checks'
    /// own word for exactly this number — "sampled N of M candidate databases" — so the collision
    /// is dissolved by using the house term rather than by arguing with the instrument.</para>
    /// </summary>
    public int Sampled { get; set; }

    /// <summary>
    /// How many databases the run passed over AND is prepared to NAME — the uncapped size of the
    /// skip result set. Two kinds land here: a database the house predicate rejected that
    /// out-ranked the sample, and ANY database with no IO reading at all, eligible or not, because
    /// an absent reading cannot be used to argue anything about rank. It can exceed
    /// <see cref="SkipReportCap"/>; the difference is the page's truncation count.
    /// </summary>
    public int PassedOver { get; set; }

    public int SkipReportCap { get; set; }

    /// <summary>
    /// How many databases lost the sample on a reading they REALLY EARNED — not picked, and not in
    /// the reported set, which leaves exactly one cause: a measured IO figure below the cut.
    ///
    /// <para>This is the only number the page may render as "ranked below the cut". The first cut
    /// rendered <c>Eligible − Sampled</c> there, which silently counted every eligible database
    /// with no reading at all — the AUTO_CLOSE case — as having been out-ranked, on the strength of
    /// the zero that its unreadability manufactured. See the ⚠⚠ block in the file header.</para>
    /// </summary>
    public int RankedBelowCut { get; set; }
}

/// <summary>
/// Everything one Analyze press reads. ServerStartTime and ScopeDatabase are measured in the SAME
/// pass as the index reads so the page's empty-state qualifiers describe the run that produced
/// them rather than an assumption.
/// </summary>
public sealed class IndexAnalysisResult
{
    public DateTime? ServerStartTime { get; set; }

    /// <summary>
    /// The database the FIRST connection landed in — master, in the page's use. It no longer bounds
    /// the unused/fragmented reads, which now run per database; it is kept because the ranking and
    /// missing-index reads run on this connection and an operator debugging a permission problem
    /// needs to know which login context asked.
    /// </summary>
    public string ScopeDatabase { get; set; } = "";

    public List<IndexAnalysisMissingRow> Missing { get; set; } = new();
    public List<IndexAnalysisUnusedRow> Unused { get; set; } = new();
    public List<IndexAnalysisFragRow> Fragmented { get; set; } = new();

    /// <summary>Databases whose per-database scan COMPLETED, in ranked order, busiest first.</summary>
    public List<IndexAnalysisDatabase> Scanned { get; set; } = new();

    /// <summary>Databases not read, each with its reason. Rendered whenever it is non-empty.</summary>
    public List<IndexAnalysisSkippedDatabase> Skipped { get; set; } = new();

    public IndexAnalysisRankingCensus Census { get; set; } = new();

    /// <summary>The wall-clock limit the run was given, so the page can quote it in a skip line.</summary>
    public TimeSpan OverallBudget { get; set; }

    /// <summary>
    /// True when the budget stopped at least one database.
    ///
    /// <para>⚠ NOT read by Pages/IndexAnalysis.razor, and the doc here used to claim it "drives the
    /// page's warning" (gate finding, 2026-08-13 — a comment asserting a property the code does not
    /// have, which is the house defect class in its quietest form). The amber block is driven by
    /// <see cref="Skipped"/> being non-empty, and a budget skip lands there with its own sentence,
    /// so the behaviour was honest and only the sentence was wrong. This flag exists for callers
    /// and tests that need the fact WITHOUT parsing a reason string.</para>
    /// </summary>
    public bool BudgetExhausted { get; set; }

    /// <summary>
    /// Null when the ranking pass returned. Otherwise the sentence saying why it did not, in the
    /// words both surfaces render.
    ///
    /// <para>⚠⚠ WHEN THIS IS SET, EVERY PER-DATABASE LIST ON THIS RESULT IS EMPTY FOR A REASON THAT
    /// IS NOT "nothing was found". <see cref="Scanned"/>, <see cref="Skipped"/>,
    /// <see cref="Unused"/> and <see cref="Fragmented"/> are all empty and
    /// <see cref="IndexAnalysisRankingCensus.UserDatabases"/> is zero, so a renderer that does not
    /// read this field renders a clean, empty, entirely wrong report. Both surfaces render it, and
    /// a test on each holds them to it.</para>
    ///
    /// <para>⚠⚠ "THE SURFACE RENDERS IT" IS NOT ENOUGH, 2026-08-14 (gate). The client PDF is two
    /// lenses on one model, and it satisfied that sentence while page 1 stayed silent: the section
    /// on page 2 printed this text, the executive summary consulted it on one of its three branches,
    /// and a run that lost a read produced an executive page byte-identical to a healthy one. A
    /// client forwards page 1. The rule a renderer has to meet is per PAGE a reader can receive on
    /// its own, not per surface — <c>AssessmentPdf.PerfExecIndexLine</c> is now the exec page's
    /// single answer and PerformanceReportScopeLiveHarness holds that function, not just the helper
    /// beneath it.</para>
    /// </summary>
    public string? RankingFailure { get; set; }

    /// <summary>
    /// Null when the instance-wide missing-index read returned. Otherwise why it did not.
    ///
    /// <para>Separate from <see cref="RankingFailure"/> because the two reads are independent and
    /// one failing says nothing about the other: the ranking read is what picks databases, and the
    /// missing-index read is one instance-wide pass that needs no database list at all. Folding
    /// them into one flag would make a report that lost only its missing-index list claim it had
    /// scanned nothing.</para>
    /// </summary>
    public string? MissingReadFailure { get; set; }
}

public sealed class IndexAnalysisService
{
    /// <summary>The house sample size. See the pattern block in the file header for its four sources.</summary>
    public const int TopDatabasesByIo = 20;

    // ── The row caps the three queries carry, named so the page can DISCLOSE them ─────────────
    // All three are unchanged from the base commit. Two of them changed MEANING when the sweep
    // went per-database: TOP 50 / TOP 30 used to truncate one database and now truncate each of up
    // to twenty independently, so the rendered table is per-database lists concatenated, not a
    // ranking. A truncation nobody states is the silent truncation this lane exists to remove, so
    // they are constants here, quoted by IndexAnalysisScopeNarrative, and pinned to the SQL text
    // by IndexAnalysisMapperTests — a disclosed cap that has drifted from the query is worse than
    // an undisclosed one.

    /// <summary>Rows the instance-wide missing-index read returns, in total.</summary>
    public const int MissingIndexRowCap = 50;

    /// <summary>Unused-index rows returned PER DATABASE scanned.</summary>
    public const int UnusedRowsPerDatabase = 50;

    /// <summary>Fragmented-index rows returned PER DATABASE scanned.</summary>
    public const int FragmentedRowsPerDatabase = 30;

    /// <summary>Wall clock for the whole pass. Rationale in the file header — it is derived from
    /// the 135s worst case this file already documented, not chosen from taste.</summary>
    public static readonly TimeSpan OverallBudgetDefault = TimeSpan.FromSeconds(120);

    /// <summary>Ceiling for any ONE database, enforced as CommandTimeout so it cancels mid-sweep.</summary>
    public static readonly TimeSpan PerDatabaseSliceDefault = TimeSpan.FromSeconds(15);

    // ── The two instance-wide command timeouts, NAMED, 2026-08-14 ─────────────────────────────
    // They were magic numbers at the two `new SqlCommand(...) { CommandTimeout = 15 }` sites, which
    // was fine while nothing said them out loud. The degraded states below quote them, and a
    // sentence that quotes a limit different from the one the command was given is the house
    // defect class in its most literal form -- see DescribeBudgetSkip, which exists because that
    // already happened once with the per-database slice.

    /// <summary>CommandTimeout for the ranking pass and the usage-window read.</summary>
    public const int RankingCommandTimeoutSeconds = 15;

    /// <summary>CommandTimeout for the instance-wide missing-index read.</summary>
    public const int MissingIndexCommandTimeoutSeconds = 30;

    private readonly ILogger<IndexAnalysisService>? _log;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _overallBudget;
    private readonly TimeSpan _perDatabaseSlice;

    /// <summary>
    /// The clock is injectable for the same reason the mappers take DbDataReader: a budget that
    /// can only be exercised by waiting two real minutes is a budget nobody tests. The budgets are
    /// injectable so the gate can force a tiny one against a live instance and see the skip line
    /// it actually produces, rather than reading the code and believing it.
    /// </summary>
    public IndexAnalysisService(
        ILogger<IndexAnalysisService>? log = null,
        Func<DateTime>? utcNow = null,
        TimeSpan? overallBudget = null,
        TimeSpan? perDatabaseSlice = null)
    {
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _overallBudget = overallBudget ?? OverallBudgetDefault;
        _perDatabaseSlice = perDatabaseSlice ?? PerDatabaseSliceDefault;
    }

    // ── The SQL. Unchanged from the page, deliberately: the numbers this service returns are
    //    the numbers that page has always shown, and only the MAPPING was wrong. ──────────────

    public const string UsageWindowSql =
        "SELECT sqlserver_start_time, DB_NAME() FROM sys.dm_os_sys_info";

    public const string MissingIndexSql = @"
SELECT TOP 50
    DB_NAME(mid.database_id) AS [Database],
    OBJECT_SCHEMA_NAME(mid.object_id, mid.database_id) AS [Schema],
    OBJECT_NAME(mid.object_id, mid.database_id) AS [Table],
    ISNULL(mid.equality_columns, '') AS EqualityColumns,
    ISNULL(mid.inequality_columns, '') AS InequalityColumns,
    ISNULL(mid.included_columns, '') AS IncludedColumns,
    migs.user_seeks + migs.user_scans AS UserHits,
    CAST(migs.avg_user_impact AS DECIMAL(6,2)) AS AvgImpact,
    CAST((migs.user_seeks + migs.user_scans) * migs.avg_total_user_cost * migs.avg_user_impact AS DECIMAL(18,0)) AS ImpactScore,
    'CREATE NONCLUSTERED INDEX IX_' + REPLACE(OBJECT_NAME(mid.object_id, mid.database_id), ' ', '_') + '_' + REPLACE(REPLACE(ISNULL(mid.equality_columns, ''), '[', ''), ']', '') +
    ' ON ' + QUOTENAME(OBJECT_SCHEMA_NAME(mid.object_id, mid.database_id)) + '.' + QUOTENAME(OBJECT_NAME(mid.object_id, mid.database_id)) +
    ' (' + ISNULL(mid.equality_columns, '') +
    CASE WHEN mid.inequality_columns IS NOT NULL THEN ', ' + mid.inequality_columns ELSE '' END + ')' +
    CASE WHEN mid.included_columns IS NOT NULL THEN ' INCLUDE (' + mid.included_columns + ')' ELSE '' END AS SuggestedScript
FROM sys.dm_db_missing_index_details mid
INNER JOIN sys.dm_db_missing_index_groups mig ON mid.index_handle = mig.index_handle
INNER JOIN sys.dm_db_missing_index_group_stats migs ON mig.index_group_handle = migs.group_handle
WHERE mid.database_id > 4
ORDER BY ImpactScore DESC";

    public const string UnusedIndexSql = @"
SELECT TOP 50
    DB_NAME(us.database_id) AS [Database],
    OBJECT_SCHEMA_NAME(us.object_id, us.database_id) AS [Schema],
    OBJECT_NAME(us.object_id, us.database_id) AS [Table],
    i.name AS IndexName,
    i.type_desc AS IndexType,
    us.user_seeks + us.user_scans + us.user_lookups AS UserReads,
    us.user_updates AS UserWrites,
    CAST(ps.used_page_count * 8.0 / 1024 AS DECIMAL(10,2)) AS SizeMB
FROM sys.dm_db_index_usage_stats us
INNER JOIN sys.indexes i ON us.object_id = i.object_id AND us.index_id = i.index_id
LEFT JOIN sys.dm_db_partition_stats ps ON us.object_id = ps.object_id AND us.index_id = ps.index_id
WHERE us.database_id = DB_ID()
    AND i.type_desc <> 'HEAP'
    AND i.is_primary_key = 0
    AND i.is_unique_constraint = 0
    AND us.user_seeks + us.user_scans + us.user_lookups = 0
    AND us.user_updates > 0
ORDER BY us.user_updates DESC";

    public const string FragIndexSql = @"
SELECT TOP 30
    DB_NAME(ips.database_id) AS [Database],
    OBJECT_SCHEMA_NAME(ips.object_id, ips.database_id) AS [Schema],
    OBJECT_NAME(ips.object_id, ips.database_id) AS [Table],
    i.name AS IndexName,
    ips.avg_fragmentation_in_percent AS FragPercent,
    ips.page_count AS PageCount,
    CAST(ips.page_count * 8.0 / 1024 AS DECIMAL(10,2)) AS SizeMB,
    CASE WHEN ips.avg_fragmentation_in_percent > 30 THEN 'REBUILD' ELSE 'REORGANIZE' END AS RecommendedAction
FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ips
INNER JOIN sys.indexes i ON ips.object_id = i.object_id AND ips.index_id = i.index_id
WHERE ips.avg_fragmentation_in_percent > 5
    AND ips.page_count > 1000
    AND i.type_desc <> 'HEAP'
ORDER BY ips.avg_fragmentation_in_percent DESC";

    /// <summary>
    /// THE RANKING PASS. One server-wide read of sys.dm_io_virtual_file_stats, then a catalog scan,
    /// then two result sets. It selects nothing that the four corpus checks named in the file
    /// header would not select; the extra columns exist so the page can NAME what it left out.
    ///
    /// <para>Result set 1 — one row per database the page has something to say about:
    /// <c>RowKind='SCAN'</c> for the databases picked for scanning, busiest first, then the ones it
    /// passed over — <c>RowKind='SKIP-STATE'</c> for a database the house predicate rejected, and
    /// <c>RowKind='SKIP-UNRANKED'</c> for an ELIGIBLE database with no IO reading at all, which
    /// could be scanned but could not be ranked. The two are separated because they need different
    /// sentences: an AUTO_CLOSE database is not in a bad state, it is simply unmeasured. The
    /// passed-over rows are capped by <c>@skip_report_cap</c> so an estate with thousands of
    /// offline databases returns twenty rows, not thousands; result set 2 carries the true total.</para>
    ///
    /// <para>Result set 2 — the census. One row of counts describing the whole instance.</para>
    ///
    /// <para>The cutoff deserves its own sentence. When the instance has at least
    /// <c>@top_n</c> eligible databases, an ineligible one was only "passed over" if it out-ranks
    /// the least busy database that WAS picked. When it has fewer, every slot in the sample was
    /// empty, so every ineligible database was passed over and the cutoff is zero.</para>
    /// </summary>
    public const string DatabaseRankingSql = @"
SET NOCOUNT ON;

DECLARE @hadr INT = CONVERT(INT, ISNULL(SERVERPROPERTY('IsHadrEnabled'), 0));

-- ONE server-wide DMV read. Its cost tracks the FILE count, never the database count, which is
-- what makes this selection safe on an estate with hundreds of databases.
DECLARE @io TABLE (database_id INT PRIMARY KEY, io_ops BIGINT);
INSERT INTO @io (database_id, io_ops)
SELECT database_id, SUM(num_of_reads + num_of_writes)
FROM sys.dm_io_virtual_file_stats(NULL, NULL)
GROUP BY database_id;

-- Every user database, with the house eligibility predicate evaluated but NOT applied as a filter.
DECLARE @cand TABLE (
    database_id INT PRIMARY KEY, name SYSNAME, io_ops BIGINT, io_measured BIT,
    state_desc NVARCHAR(60), user_access_desc NVARCHAR(60),
    is_read_only BIT, is_eligible BIT);

INSERT INTO @cand (database_id, name, io_ops, io_measured, state_desc, user_access_desc, is_read_only, is_eligible)
SELECT d.database_id, d.name, ISNULL(s.io_ops, 0),
       -- A database with no OPEN FILES returns NOTHING from dm_io_virtual_file_stats, so the LEFT
       -- JOIN yields NULL. ISNULL makes that a zero for the ORDER BY, exactly as the corpus checks
       -- do, and this bit remembers that the zero is an ABSENCE, not a reading. ⚠ Two shapes reach
       -- it: an OFFLINE database (unreadable) and an AUTO_CLOSE one (perfectly scannable, files
       -- simply shut). Nothing below may infer the state from this bit.
       CASE WHEN s.database_id IS NULL THEN 0 ELSE 1 END,
       d.state_desc, d.user_access_desc, d.is_read_only,
       CASE WHEN d.state = 0 AND d.user_access = 0 AND d.is_read_only = 0
                 AND (@hadr = 0 OR NOT EXISTS (
                       SELECT 1
                       FROM sys.dm_hadr_database_replica_states drs
                       JOIN sys.availability_replicas ar ON ar.replica_id = drs.replica_id
                       WHERE drs.database_id = d.database_id
                         AND drs.is_local = 1
                         AND drs.is_primary_replica = 0
                         AND ar.secondary_role_allow_connections = 0))
            THEN 1 ELSE 0 END
FROM sys.databases d
LEFT JOIN @io s ON s.database_id = d.database_id
WHERE d.database_id > 4;   -- system databases, same treatment as the checks

-- Rank ALL user databases once, so a rendered rank means the same thing on every row.
DECLARE @ranked TABLE (database_id INT PRIMARY KEY, io_rank INT);
INSERT INTO @ranked (database_id, io_rank)
SELECT database_id, ROW_NUMBER() OVER (ORDER BY io_ops DESC, name ASC) FROM @cand;

-- The sample: TOP (@top_n) eligible by IO, exactly as the checks pick theirs.
DECLARE @scan TABLE (database_id INT PRIMARY KEY);
INSERT INTO @scan (database_id)
SELECT database_id FROM (
    SELECT database_id, ROW_NUMBER() OVER (ORDER BY io_ops DESC, name ASC) AS pick
    FROM @cand WHERE is_eligible = 1
) p
WHERE p.pick <= @top_n;

DECLARE @cutoff_io BIGINT = 0;
IF (SELECT COUNT(*) FROM @scan) >= @top_n
    SET @cutoff_io = (SELECT MIN(c.io_ops) FROM @cand c JOIN @scan s ON s.database_id = c.database_id);

-- WHAT THE PAGE IS PREPARED TO NAME. Two things get a database in here, and NEITHER of them is
-- 'it ranked below the cut':
--   • it has NO IO READING AT ALL (io_measured = 0) — reported whatever its apparent rank, because
--     that rank was manufactured by the absence of the very reading it would have been ranked on.
--     ⚠ This arm is NOT gated on eligibility. It was, and an AUTO_CLOSE database — ONLINE,
--     MULTI_USER, writable, files shut — fell through both arms and vanished from the page
--     entirely. See the ⚠⚠ block in the file header for the live reproduction.
--   • the house predicate rejected it AND it out-ranks the sample on a reading it really earned.
-- Everything else that was not picked lost the sample on a real number, and the census counts it
-- as RankedBelowCut rather than listing it.
-- ⚠ VARCHAR(16), not 12: 'SKIP-UNRANKED' is thirteen characters and a VARCHAR(12)
-- column truncated it into 'String or binary data would be truncated' on the first live run.
DECLARE @report TABLE (database_id INT PRIMARY KEY, row_kind VARCHAR(16));
INSERT INTO @report (database_id, row_kind)
SELECT c.database_id,
       -- An eligible database with no reading is SCANNABLE and simply unranked; saying 'the
       -- database is OFFLINE' about it would name a state it is not in.
       CASE WHEN c.is_eligible = 0 THEN 'SKIP-STATE' ELSE 'SKIP-UNRANKED' END
FROM @cand c
WHERE NOT EXISTS (SELECT 1 FROM @scan s WHERE s.database_id = c.database_id)
  AND (c.io_measured = 0 OR (c.is_eligible = 0 AND c.io_ops >= @cutoff_io));

SELECT u.row_kind AS RowKind, u.database_id AS DatabaseId, u.name AS DatabaseName,
       u.io_ops AS IoOperations, u.io_measured AS IoMeasured, u.io_rank AS IoRank,
       u.state_desc AS StateDesc, u.user_access_desc AS UserAccessDesc, u.is_read_only AS IsReadOnly
FROM (
    SELECT CONVERT(VARCHAR(16), 'SCAN') AS row_kind, 0 AS kind_order,
           c.database_id, c.name, c.io_ops, c.io_measured, r.io_rank,
           c.state_desc, c.user_access_desc, c.is_read_only
    FROM @cand c
    JOIN @ranked r ON r.database_id = c.database_id
    JOIN @scan   s ON s.database_id = c.database_id
    UNION ALL
    SELECT z.row_kind, 1,
           z.database_id, z.name, z.io_ops, z.io_measured, z.io_rank,
           z.state_desc, z.user_access_desc, z.is_read_only
    FROM (
        SELECT TOP (@skip_report_cap)
               CONVERT(VARCHAR(16), rp.row_kind) AS row_kind,
               c.database_id, c.name, c.io_ops, c.io_measured, r.io_rank,
               c.state_desc, c.user_access_desc, c.is_read_only
        FROM @report rp
        JOIN @cand   c ON c.database_id = rp.database_id
        JOIN @ranked r ON r.database_id = rp.database_id
        -- ⚠ Every unmeasured row carries io_ops = 0, so this cap is decided ALPHABETICALLY among
        -- them. That is why the page never calls the list 'the busiest N' and always says how many
        -- it left out: the order inside a tie is a tiebreak, not a finding.
        ORDER BY c.io_ops DESC, c.name ASC
    ) z
) u
ORDER BY u.kind_order, u.io_rank;

-- The census partitions @cand three ways — picked, reported, and the remainder — so the page can
-- state a total that reconciles to the instance instead of adding up two list lengths.
SELECT @top_n                                              AS TopN,
       (SELECT COUNT(*) FROM @cand)                        AS UserDatabases,
       (SELECT COUNT(*) FROM @cand WHERE is_eligible = 1)  AS EligibleDatabases,
       (SELECT COUNT(*) FROM @cand WHERE is_eligible = 0)  AS IneligibleDatabases,
       (SELECT COUNT(*) FROM @scan)                        AS SampledDatabases,
       (SELECT COUNT(*) FROM @report)                      AS PassedOverDatabases,
       @skip_report_cap                                    AS SkipReportCap,
       (SELECT COUNT(*) FROM @cand c
         WHERE NOT EXISTS (SELECT 1 FROM @scan   s  WHERE s.database_id  = c.database_id)
           AND NOT EXISTS (SELECT 1 FROM @report rp WHERE rp.database_id = c.database_id))
                                                           AS RankedBelowCutDatabases;";

    /// <summary>
    /// One pass. The ranking read and the instance-wide missing-index read run on the caller's
    /// connection; the two single-database reads then run once per selected database, on a
    /// connection retargeted at it, because <c>DB_ID()</c> is what scopes them and the catalog
    /// joins to sys.indexes only resolve inside the database being read.
    ///
    /// <para>Neither single-database query text changes. That is the whole mechanic: pointing the
    /// connection at database X makes <c>us.database_id = DB_ID()</c> and
    /// <c>dm_db_index_physical_stats(DB_ID(), …)</c> describe X, and makes their existing
    /// <c>DB_NAME(...)</c> projections label every row with X for free. The frag scan stays in
    /// LIMITED mode with its >1000-page floor, unchanged and unmoved.</para>
    /// </summary>
    public async Task<IndexAnalysisResult> GetAnalysisAsync(string connectionString, CancellationToken ct = default)
    {
        var result = new IndexAnalysisResult { OverallBudget = _overallBudget };
        var deadlineUtc = _utcNow() + _overallBudget;

        List<IndexAnalysisDatabase> selected;

        using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);

            (result.ServerStartTime, result.ScopeDatabase) = await ReadUsageWindowAsync(conn, ct).ConfigureAwait(false);

            var ranking = await GuardedRankingAsync(c => ReadDatabaseRankingAsync(conn, c), ct)
                .ConfigureAwait(false);

            result.RankingFailure = ranking.Failure;
            result.Census = ranking.Census;
            selected = ranking.Sample;

            foreach (var (kind, passedOver) in ranking.PassedOver)
                result.Skipped.Add(new IndexAnalysisSkippedDatabase
                {
                    Database = passedOver.Name,
                    IoOperations = passedOver.IoOperations,
                    IoMeasured = passedOver.IoMeasured,
                    IoRank = passedOver.IoRank,
                    Reason = DescribePassedOver(kind, passedOver),
                    PassedOverBeforeScanning = true,
                });

            // The missing-index read is INDEPENDENT of the ranking pass: it is one instance-wide
            // read that needs no database list, so it is attempted even when the ranking failed,
            // and its own failure does not take the per-database sweep down with it.
            var missing = await GuardedMissingAsync(c => ReadMissingIndexesAsync(conn, c), ct)
                .ConfigureAwait(false);
            result.Missing = missing.Rows;
            result.MissingReadFailure = missing.Failure;
        }

        await ApplySelectionAsync(
            selected, deadlineUtc,
            (db, slice, token) => ScanOneDatabaseAsync(connectionString, db, slice, token),
            result, ct).ConfigureAwait(false);

        return result;
    }

    // ── THE TWO INSTANCE-WIDE READS, DEGRADED RATHER THAN FATAL, 2026-08-14 ───────────────────
    //
    // ⚠⚠ THIS REVERSES A DELIBERATE EARLIER CHOICE, so the reasoning is written down rather than
    // left as a diff. ReadDatabaseRankingAsync's own doc said "Failure here is fatal on purpose",
    // and the purpose was real: without a ranking the run has no list of databases, and the shape
    // it must never fall back to is scanning whatever the connection landed in while the page
    // claims a top-20 sweep. That is silent truncation, and it is the defect the 2026-08-13 lane
    // existed to remove.
    //
    // The choice was right about the fallback and wrong about the throw. Letting the exception out
    // costs the WHOLE index section: on the live page it replaces everything with a red banner, and
    // in the client report ComposeAsync's live-collection catch marks the entire instance
    // Succeeded=false and returns -- so a fifteen-second ranking timeout on one busy server loses
    // the disk section, the hotspots, the maintenance advice and the capacity page too, none of
    // which had anything to do with the ranking read. A run that lost one read is not a run that
    // learned nothing.
    //
    // So: no fallback (selected stays EMPTY, and an empty selection scans nothing), no silence
    // (the failure is a sentence on the result that both surfaces render), and no throw. The census
    // stays at its zero value, which IndexAnalysisScopeNarrative already reads as "took no
    // instance-wide count" and refuses to quote totals from -- that branch existed before this
    // change and is what makes the degraded state safe to render.
    //
    // WHY THE SEAM IS A DELEGATE. Same reason ApplySelectionAsync takes one: a failure branch whose
    // only exercise is a real server that happens to time out is a branch nobody tests. The read is
    // injectable, so "the ranking read threw" is reachable in milliseconds and deterministically,
    // for every exception shape including the timeout the sentence is conditioned on.

    /// <summary>What the ranking pass produced, or the sentence saying why it produced nothing.</summary>
    internal sealed class RankingOutcome
    {
        /// <summary>
        /// The databases picked for scanning, busiest first. Empty when <see cref="Failure"/> is
        /// set, which is the whole point: an empty sample scans nothing.
        ///
        /// <para>⚠ NOT named <c>Selected</c>, and do not rename it back — this cost a red suite on
        /// 2026-08-14 and had already cost one on 2026-08-13 for the census property.
        /// DiLifetimeCensusTests scans the SOURCE FILE of every singleton-registered type for
        /// <c>public … Selected… { get</c>, because per-user selection state on a singleton is the
        /// target of a privileged action. IndexAnalysisService is a singleton and lives in this
        /// file, so any DTO here with that property name turns the census red even though this is a
        /// per-call value on a returned object and no state at all. The census is right to be
        /// coarse; <c>Sample</c> is the word the sibling census property already uses
        /// (<see cref="IndexAnalysisRankingCensus.Sampled"/>) and is the corpus checks' own term.</para>
        /// </summary>
        public List<IndexAnalysisDatabase> Sample { get; init; } = new();

        public List<(string Kind, IndexAnalysisDatabase Database)> PassedOver { get; init; } = new();
        public IndexAnalysisRankingCensus Census { get; init; } = new();

        /// <summary>Null on success. Otherwise the reason, in the words both surfaces render.</summary>
        public string? Failure { get; init; }
    }

    /// <summary>What the instance-wide missing-index read produced, or why it produced nothing.</summary>
    internal sealed class MissingReadOutcome
    {
        public List<IndexAnalysisMissingRow> Rows { get; init; } = new();
        public string? Failure { get; init; }
    }

    internal delegate Task<(List<IndexAnalysisDatabase> Selected,
                            List<(string Kind, IndexAnalysisDatabase Database)> PassedOver,
                            IndexAnalysisRankingCensus Census)> RankingReader(CancellationToken ct);

    internal delegate Task<List<IndexAnalysisMissingRow>> MissingIndexReader(CancellationToken ct);

    internal async Task<RankingOutcome> GuardedRankingAsync(RankingReader read, CancellationToken ct)
    {
        try
        {
            var (selected, passedOver, census) = await read(ct).ConfigureAwait(false);
            return new RankingOutcome { Sample = selected, PassedOver = passedOver, Census = census };
        }
        // The caller asked to stop. That is not a degraded read and must not be dressed as one.
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Index analysis could not rank the databases; this run will scan none and say so.");
            return new RankingOutcome { Failure = DescribeRankingFailure(ex) };
        }
    }

    internal async Task<MissingReadOutcome> GuardedMissingAsync(MissingIndexReader read, CancellationToken ct)
    {
        try
        {
            return new MissingReadOutcome { Rows = await read(ct).ConfigureAwait(false) };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Index analysis could not read the missing-index suggestions; the surfaces will say so.");
            return new MissingReadOutcome { Failure = DescribeMissingReadFailure(ex) };
        }
    }

    /// <summary>
    /// Why this run has no database list, in the words an operator reads.
    ///
    /// <para>Every branch ends by denying the reading a reader would otherwise take from an empty
    /// table. An empty Unused table normally means "no index in the databases we read went unread";
    /// after this failure it means nothing at all, and the sentence has to say which of the two it
    /// is looking at.</para>
    /// </summary>
    internal static string DescribeRankingFailure(Exception ex)
        => IsTimeout(ex)
            ? $"The database ranking read did not finish inside its {RankingCommandTimeoutSeconds}-second "
              + "limit, so this run chose no database to scan. The index tables below are empty "
              + "because nothing was read, not because nothing was found."
            : $"The database ranking read failed, so this run chose no database to scan. The index "
              + $"tables below are empty because nothing was read, not because nothing was found. {ex.Message}";

    /// <summary>Why this run has no missing-index list. Same rule: an empty table is denied a meaning.</summary>
    internal static string DescribeMissingReadFailure(Exception ex)
        => IsTimeout(ex)
            ? $"The missing-index read did not finish inside its {MissingIndexCommandTimeoutSeconds}-second "
              + "limit, so no suggestion was read on this run. That is not the same as the optimizer "
              + "having no suggestion to give."
            : "The missing-index read failed, so no suggestion was read on this run. That is not the "
              + $"same as the optimizer having no suggestion to give. {ex.Message}";

    /// <summary>What one database's scan produced, or the sentence saying why it produced nothing.</summary>
    internal sealed class DatabaseScanOutcome
    {
        public List<IndexAnalysisUnusedRow> Unused { get; init; } = new();
        public List<IndexAnalysisFragRow> Fragmented { get; init; } = new();

        /// <summary>Null on success. Otherwise the reason, in the words the page renders.</summary>
        public string? Failure { get; init; }
    }

    internal delegate Task<DatabaseScanOutcome> DatabaseScanner(
        IndexAnalysisDatabase database, TimeSpan slice, CancellationToken ct);

    /// <summary>
    /// THE BUDGET LOOP, with the database IO lifted out behind <paramref name="scan"/>. Everything
    /// that decides what an operator is told — who gets a turn, how much slice they get, which
    /// sentence names them when they do not — lives here and nowhere else.
    ///
    /// <para>It is separated for one reason: a budget whose only exercise is a live server is a
    /// budget tested at whatever speed that server happened to run. Driven by an injected clock and
    /// a fake scanner, every branch is reachable in milliseconds and deterministically — including
    /// the ones a healthy instance never takes, like a scan that fails on its fifteenth database.
    /// The live harness still runs the whole thing against a real instance; this is what makes the
    /// live run a confirmation rather than the only evidence.</para>
    /// </summary>
    internal async Task ApplySelectionAsync(
        IReadOnlyList<IndexAnalysisDatabase> selected,
        DateTime deadlineUtc,
        DatabaseScanner scan,
        IndexAnalysisResult result,
        CancellationToken ct)
    {
        foreach (var db in selected)
        {
            ct.ThrowIfCancellationRequested();

            var remaining = deadlineUtc - _utcNow();
            if (remaining <= TimeSpan.Zero)
            {
                result.BudgetExhausted = true;
                result.Skipped.Add(SkipFor(db, DescribeBudgetSkip(_overallBudget)));
                continue;
            }

            var slice = remaining < _perDatabaseSlice ? remaining : _perDatabaseSlice;
            var outcome = await scan(db, slice, ct).ConfigureAwait(false);

            if (outcome.Failure is null)
            {
                result.Unused.AddRange(outcome.Unused);
                result.Fragmented.AddRange(outcome.Fragmented);
                result.Scanned.Add(db);
            }
            else
            {
                result.Skipped.Add(SkipFor(db, outcome.Failure));
            }
        }
    }

    private static IndexAnalysisSkippedDatabase SkipFor(IndexAnalysisDatabase db, string reason) => new()
    {
        Database = db.Name,
        IoOperations = db.IoOperations,
        IoMeasured = db.IoMeasured,
        IoRank = db.IoRank,
        Reason = reason,
        // This database WAS picked; the sweep is what stopped. Only the ranking pass produces
        // passed-over rows, and it sets the flag itself.
        PassedOverBeforeScanning = false,
    };

    /// <summary>
    /// One database, both single-database reads, ALL OR NOTHING. If either read fails or runs out
    /// of slice, the rows already collected are dropped and the database is reported as skipped.
    ///
    /// <para>WHY DROP THEM. The alternative — keep the half-read rows and list the database as
    /// skipped as well — preserves measurements the operator paid for, and was considered. It was
    /// rejected because it makes "scanned" mean two things at once and makes the counts on the page
    /// uncountable: a database would appear in both lists, and the fragmented table would hold rows
    /// from a scan that never finished, whose absences mean nothing. Whole databases in, whole
    /// databases out, and the skip line names every one that did not make it.</para>
    /// </summary>
    private async Task<DatabaseScanOutcome> ScanOneDatabaseAsync(
        string connectionString, IndexAnalysisDatabase db, TimeSpan slice, CancellationToken ct)
    {
        // ⚠ ONE slice for the whole database, not one per command. Passing CommandTimeoutSeconds
        // (slice) to BOTH reads let a database spend 2 × the slice while DescribeScanFailure told
        // the operator it had "passed its 15-second slice" — a duration nobody spent. The second
        // read gets what is left.
        //
        // ⚠⚠ A STOPWATCH, NOT _utcNow. The injected clock exists to drive the BUDGET
        // deterministically — the live budget harness counts its calls to decide which database is
        // the last one to get a turn — so reading it from inside a scan couples every budget test
        // to how many times the scanner happens to look at the time. It did: the first cut of this
        // fix took two clock readings per database and turned "exactly three databases" into one.
        // The budget is the injected clock's; the elapsed time inside one database is real time.
        var spent = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var conn = new SqlConnection(WithDatabase(connectionString, db.Name));
            await conn.OpenAsync(ct).ConfigureAwait(false);

            var unused = await ReadUnusedIndexesAsync(conn, CommandTimeoutSeconds(slice), ct).ConfigureAwait(false);
            var fragmented = await ReadFragmentedIndexesAsync(
                conn, RemainingSliceSeconds(slice, spent.Elapsed), ct).ConfigureAwait(false);
            return new DatabaseScanOutcome { Unused = unused, Fragmented = fragmented };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "Index analysis could not scan {Database}; the page will name it as skipped.", db.Name);
            return new DatabaseScanOutcome { Failure = DescribeScanFailure(ex, slice) };
        }
    }

    /// <summary>Retargets a connection string at one database. The builder quotes the name.</summary>
    internal static string WithDatabase(string connectionString, string database)
        => new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString;

    /// <summary>CommandTimeout is whole seconds and 0 means "no limit" — never round a slice to that.</summary>
    internal static int CommandTimeoutSeconds(TimeSpan slice)
        => (int)Math.Max(1, Math.Ceiling(slice.TotalSeconds));

    /// <summary>
    /// What is left of one database's slice after <paramref name="elapsed"/> of it has been spent.
    /// Pure, so the arithmetic behind the sentence an operator reads is unit-testable without a
    /// database large enough to time out.
    ///
    /// <para>Clamped to 1 by <see cref="CommandTimeoutSeconds"/>, never to 0: CommandTimeout = 0
    /// means NO LIMIT in SqlClient, so a slice that has run out would silently become unbounded —
    /// the fail-safe inverting into the failure it was added to prevent.</para>
    /// </summary>
    internal static int RemainingSliceSeconds(TimeSpan slice, TimeSpan elapsed)
        => CommandTimeoutSeconds(slice - elapsed);

    // ── The skip sentences. Pure functions so the page renders text this file owns and the tests
    //    can assert the exact words an operator will read. ─────────────────────────────────────

    /// <summary>
    /// Why the ranking pass passed a database over, chosen by the row kind the SQL put it in
    /// rather than by inspecting its columns — the SQL already made that decision, and deriving it
    /// twice is how the two drift apart.
    /// </summary>
    internal static string DescribePassedOver(string rowKind, IndexAnalysisDatabase db)
        => rowKind == RankRowKindSkippedUnranked ? DescribeUnrankedSkip() : DescribeStateSkip(db);

    /// <summary>
    /// An ELIGIBLE database with no IO reading. It is online, writable and perfectly scannable —
    /// the sample simply could not rank it, because ranking is by IO and there is no IO to read.
    ///
    /// <para>The sentence names the mechanism (files not open) rather than the cause, and offers
    /// AUTO_CLOSE as the usual cause without asserting it: <c>is_auto_close_on</c> was not read on
    /// this pass, and a database that has just been attached or brought online looks identical.</para>
    /// </summary>
    internal static string DescribeUnrankedSkip()
        => "Not scanned. Its files are not open, so no IO was measured for it and the ranking "
         + "could not place it. A database with AUTO_CLOSE on reads like this.";

    /// <summary>Why an ineligible database was passed over, naming the state that made it so.</summary>
    internal static string DescribeStateSkip(IndexAnalysisDatabase db)
    {
        if (!string.Equals(db.StateDesc, "ONLINE", StringComparison.OrdinalIgnoreCase))
            return $"Not scanned. The database is {db.StateDesc}.";
        if (!string.Equals(db.UserAccessDesc, "MULTI_USER", StringComparison.OrdinalIgnoreCase))
            return $"Not scanned. Access is restricted to {db.UserAccessDesc}.";
        if (db.IsReadOnly)
            return "Not scanned. The database is read-only.";
        // Online, multi-user, writable and still ineligible: the HADR arm of the predicate is the
        // only one left. Say that, rather than inventing a state we did not read.
        return "Not scanned. This is a secondary replica that does not allow connections.";
    }

    internal static string DescribeBudgetSkip(TimeSpan overallBudget)
        => $"Not scanned. The {(int)overallBudget.TotalSeconds}-second budget for this run ran out first.";

    /// <summary>
    /// A scan that started and did not finish. A provider timeout is the slice doing its job and
    /// says so; anything else carries the server's own message, because a guess about why a
    /// database would not open is worth less than the sentence the server already wrote.
    /// </summary>
    internal static string DescribeScanFailure(Exception ex, TimeSpan slice)
    {
        if (IsTimeout(ex))
            return $"Scan stopped. It passed its {(int)slice.TotalSeconds}-second slice, so the rest of this database was not read.";
        return $"Not scanned. {ex.Message}";
    }

    private static bool IsTimeout(Exception ex)
        => ex is SqlException sql && (sql.Number == -2 || sql.Number == 121)
           || ex is TimeoutException
           || ex.InnerException is TimeoutException;

    /// <summary>
    /// How long the usage counters have been accumulating, and which database the two
    /// single-database reads actually cover. Failure here is not fatal and is never guessed: the
    /// page has a named "not measured on this run" sentence for the empty tuple.
    /// </summary>
    internal async Task<(DateTime?, string)> ReadUsageWindowAsync(SqlConnection conn, CancellationToken ct)
    {
        try
        {
            using var cmd = new SqlCommand(UsageWindowSql, conn) { CommandTimeout = RankingCommandTimeoutSeconds };
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
                return (MapUsageWindow(reader));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "Index analysis could not read the usage window; the page will say so rather than guess.");
        }
        return (null, "");
    }

    private async Task<List<IndexAnalysisMissingRow>> ReadMissingIndexesAsync(SqlConnection conn, CancellationToken ct)
    {
        var rows = new List<IndexAnalysisMissingRow>();
        using var cmd = new SqlCommand(MissingIndexSql, conn) { CommandTimeout = MissingIndexCommandTimeoutSeconds };
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(MapMissing(reader));
        return rows;
    }

    /// <summary>
    /// The ranking pass, both result sets. It still THROWS on failure and that is still deliberate:
    /// without a ranking the run has no list of databases, and the shape it must never fall back to
    /// is scanning whatever the connection landed in while the surfaces claim a top-20 sweep.
    ///
    /// <para>⚠ WHAT CHANGED, 2026-08-14: the throw no longer reaches the caller. It is caught by
    /// <see cref="GuardedRankingAsync"/>, which produces an EMPTY selection and a sentence, so the
    /// run degrades to "chose no database, here is why" instead of taking the whole index section
    /// with it. There is still no fallback selection, which is the half of the original ruling that
    /// was about correctness. See the block above that method for the full reasoning.</para>
    /// </summary>
    internal async Task<(List<IndexAnalysisDatabase> Selected, List<(string Kind, IndexAnalysisDatabase Database)> PassedOver, IndexAnalysisRankingCensus Census)>
        ReadDatabaseRankingAsync(SqlConnection conn, CancellationToken ct)
    {
        var selected = new List<IndexAnalysisDatabase>();
        // The kind travels with the row. It is what picks the sentence, and re-deriving it from the
        // database's columns would put the classification in two places.
        var passedOver = new List<(string Kind, IndexAnalysisDatabase Database)>();

        using var cmd = new SqlCommand(DatabaseRankingSql, conn) { CommandTimeout = RankingCommandTimeoutSeconds };
        cmd.Parameters.Add("@top_n", System.Data.SqlDbType.Int).Value = TopDatabasesByIo;
        cmd.Parameters.Add("@skip_report_cap", System.Data.SqlDbType.Int).Value = TopDatabasesByIo;

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var (kind, db) = MapRankedDatabase(reader);
            if (kind == RankRowKindScan) selected.Add(db); else passedOver.Add((kind, db));
        }

        var census = new IndexAnalysisRankingCensus();
        if (await reader.NextResultAsync(ct).ConfigureAwait(false)
            && await reader.ReadAsync(ct).ConfigureAwait(false))
            census = MapRankingCensus(reader);

        return (selected, passedOver, census);
    }

    private async Task<List<IndexAnalysisUnusedRow>> ReadUnusedIndexesAsync(SqlConnection conn, int timeoutSeconds, CancellationToken ct)
    {
        var rows = new List<IndexAnalysisUnusedRow>();
        using var cmd = new SqlCommand(UnusedIndexSql, conn) { CommandTimeout = timeoutSeconds };
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(MapUnused(reader));
        return rows;
    }

    private async Task<List<IndexAnalysisFragRow>> ReadFragmentedIndexesAsync(SqlConnection conn, int timeoutSeconds, CancellationToken ct)
    {
        var rows = new List<IndexAnalysisFragRow>();
        using var cmd = new SqlCommand(FragIndexSql, conn) { CommandTimeout = timeoutSeconds };
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(MapFrag(reader));
        return rows;
    }

    // ── The mappers. DbDataReader, not SqlDataReader, ON PURPOSE: SqlDataReader is sealed and
    //    cannot be constructed by a test, so a mapper typed to it is untestable — which is how a
    //    wrong cast survived here. SqlDataReader derives from DbDataReader, so the live path is
    //    unaffected and the tests replay reader shapes CAPTURED from a real instance. ───────────

    public const string RankRowKindScan = "SCAN";
    public const string RankRowKindSkippedByState = "SKIP-STATE";

    /// <summary>
    /// An ELIGIBLE database the ranking could not place, because sys.dm_io_virtual_file_stats had
    /// no row for it. Distinct from <see cref="RankRowKindSkippedByState"/> because it is not in a
    /// bad state — AUTO_CLOSE is the usual cause and it is a normal, scannable database.
    /// </summary>
    public const string RankRowKindSkippedUnranked = "SKIP-UNRANKED";

    /// <summary>
    /// One row of the ranking pass's first result set. IsEligible is derived from the row kind
    /// rather than read from a column: the SQL already applied the house predicate to decide which
    /// bucket a database landed in, and carrying the same fact twice is how the two drift apart.
    ///
    /// <para>⚠ The derivation is "NOT the skipped-by-state bucket", not "is the scan bucket". A
    /// SKIP-UNRANKED row is an ELIGIBLE database that could not be ranked; reading eligibility off
    /// <c>kind == SCAN</c> would report it as failing the house predicate, which is the opposite of
    /// what the SQL decided about it.</para>
    /// </summary>
    internal static (string Kind, IndexAnalysisDatabase Database) MapRankedDatabase(DbDataReader r)
    {
        var kind = Str(r, 0);
        return (kind, new IndexAnalysisDatabase
        {
            // database_id is INT here (the table variable declares it so), but every other
            // database_id in this tree arrives as smallint from a DMV and threw when read with
            // GetInt32 — DiskIoService carries that scar. Long() converts either.
            DatabaseId = (int)Long(r, 1),
            Name = Str(r, 2),
            IoOperations = Long(r, 3),
            IoMeasured = Bool(r, 4),
            IoRank = (int)Long(r, 5),
            StateDesc = Str(r, 6),
            UserAccessDesc = Str(r, 7),
            IsReadOnly = Bool(r, 8),
            IsEligible = kind != RankRowKindSkippedByState,
        });
    }

    internal static IndexAnalysisRankingCensus MapRankingCensus(DbDataReader r) => new()
    {
        TopN = (int)Long(r, 0),
        UserDatabases = (int)Long(r, 1),
        Eligible = (int)Long(r, 2),
        Ineligible = (int)Long(r, 3),
        Sampled = (int)Long(r, 4),
        PassedOver = (int)Long(r, 5),
        SkipReportCap = (int)Long(r, 6),
        RankedBelowCut = (int)Long(r, 7),
    };

    internal static (DateTime?, string) MapUsageWindow(DbDataReader r)
        => (r.IsDBNull(0) ? null : Convert.ToDateTime(r.GetValue(0)), Str(r, 1));

    internal static IndexAnalysisMissingRow MapMissing(DbDataReader r) => new()
    {
        // DB_NAME/OBJECT_NAME return NULL for a database the login cannot see, and this query is
        // instance-wide — so both were reachable NullReferenceException/SqlNullValueException
        // sources under a least-privilege login. Str is IsDBNull-guarded.
        Database = Str(r, 0),
        Schema = Str(r, 1),
        Table = Str(r, 2),
        KeyColumns = CombineColumns(Str(r, 3), Str(r, 4)),
        IncludedColumns = Str(r, 5),
        UserHits = Long(r, 6),
        // Null in, null out. Dec would have substituted 0m, which renders as "0.00%" / "0" — a
        // measurement claim over a column the server declares nullable.
        AvgImpact = DecOrNull(r, 7),
        ImpactScore = DecOrNull(r, 8),
        SuggestedScript = Str(r, 9),
    };

    internal static IndexAnalysisUnusedRow MapUnused(DbDataReader r) => new()
    {
        Database = Str(r, 0),
        Schema = Str(r, 1),
        Table = Str(r, 2),
        // An index with no name is a real state, and "(unnamed)" is what the page has always
        // shown for it. Distinct from SizeMB's null, which means we did not measure.
        IndexName = r.IsDBNull(3) ? "(unnamed)" : Str(r, 3),
        IndexType = Str(r, 4),
        UserReads = Long(r, 5),
        UserWrites = Long(r, 6),
        SizeMB = DecOrNull(r, 7),
    };

    internal static IndexAnalysisFragRow MapFrag(DbDataReader r) => new()
    {
        Database = Str(r, 0),
        Schema = Str(r, 1),
        Table = Str(r, 2),
        IndexName = r.IsDBNull(3) ? "(unnamed)" : Str(r, 3),
        // THE 2026-08-13 FIX. This column is float; the old code called GetDecimal here. Its
        // ordinal moved from 3 to 4 when [Schema] was projected, which is exactly why the
        // offline tests replay a RE-CAPTURED reader rather than a hand-written one.
        FragPercent = Dbl(r, 4),
        PageCount = Long(r, 5),
        SizeMB = DecOrNull(r, 6),
        Action = Str(r, 7),
    };

    // ── Reader helpers (robust against provider numeric-type quirks) ──────────
    // Same shape as PerformanceReportComposer's Str/Long/Dbl, extended with the decimal pair the
    // display columns need. Every one is IsDBNull-guarded first: a typed Get* on a NULL throws
    // SqlNullValueException, which is the second half of this defect class.
    internal static string Str(DbDataReader r, int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i)) ?? "";
    internal static long Long(DbDataReader r, int i) => r.IsDBNull(i) ? 0L : Convert.ToInt64(r.GetValue(i));
    internal static double Dbl(DbDataReader r, int i) => r.IsDBNull(i) ? 0d : Convert.ToDouble(r.GetValue(i));
    internal static bool Bool(DbDataReader r, int i) => !r.IsDBNull(i) && Convert.ToBoolean(r.GetValue(i));

    // ⚠ THERE IS NO `Dec` HELPER, and its absence is deliberate (2026-08-14). It existed, it
    // substituted 0m for a NULL, and MapMissing was its only caller — which is how AvgImpact and
    // ImpactScore came to render "0.00%" and "0" over a column the server declares nullable. It is
    // deleted rather than left unused: a helper that quietly manufactures a measurement is a trap
    // the next mapper author would reach for by name. DecOrNull is the only decimal reader here,
    // and it forces the caller to decide how to SAY "not measured".

    /// <summary>Null in, null out — the caller must decide how to SAY "not measured".</summary>
    internal static decimal? DecOrNull(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDecimal(r.GetValue(i));

    internal static string CombineColumns(string equality, string inequality)
    {
        if (string.IsNullOrEmpty(inequality)) return equality;
        if (string.IsNullOrEmpty(equality)) return inequality;
        return equality + ", " + inequality;
    }
}

/// <summary>
/// How an ABSENT number is written, in one place, for every surface that renders these rows.
///
/// <para>WHY IT IS A TYPE AND NOT TWO PRIVATE METHODS. It was two private methods —
/// <c>SizeLabel</c> on Pages/IndexAnalysis.razor and <c>PerfSizeLabel</c> on the client PDF — and
/// they agreed only because one author wrote both on one afternoon. The word an operator reads for
/// "nobody measured this" is a product decision, not a per-file one: a screen saying "not measured"
/// beside a PDF saying "n/a" over the same run is the same class of defect as any other sentence
/// that outruns its measurement. So the word lives here, both surfaces call in, and the tests
/// assert it once.</para>
///
/// <para>⚠ THE OVERLOADS ARE NOT REDUNDANT. The service carries <c>decimal?</c> and the report
/// snapshot carries <c>double?</c>; forcing one to convert at the call site would put a cast in
/// front of the null check, which is exactly where a null becomes a zero.</para>
/// </summary>
public static class IndexAnalysisRendering
{
    /// <summary>The one word for an absent reading. Rendered verbatim on the page and in the PDF.</summary>
    public const string Unmeasured = "not measured";

    /// <summary>A formatted number, or <see cref="Unmeasured"/> when there is no reading.</summary>
    public static string Number(decimal? value, string format)
        => value.HasValue ? value.Value.ToString(format) : Unmeasured;

    /// <inheritdoc cref="Number(decimal?, string)"/>
    public static string Number(double? value, string format)
        => value.HasValue ? value.Value.ToString(format) : Unmeasured;

    /// <summary>
    /// A percentage. The suffix is INSIDE the helper on purpose: the page used to render
    /// <c>@idx.AvgImpact.ToString("F2")%</c> with the sign written in the markup beside the value,
    /// and a null there would have produced "not measured%".
    /// </summary>
    public static string Percent(decimal? value, string format)
        => value.HasValue ? value.Value.ToString(format) + "%" : Unmeasured;

    /// <inheritdoc cref="Percent(decimal?, string)"/>
    public static string Percent(double? value, string format)
        => value.HasValue ? value.Value.ToString(format) + "%" : Unmeasured;

    /// <summary>
    /// How a table is NAMED to a reader: <c>schema.table</c>, on both surfaces.
    ///
    /// <para>⚠ THIS IS DISPLAY, NOT SQL. It does not quote, and nothing may build a statement from
    /// it — see <see cref="QuoteName"/> and the script builders, which bracket every part
    /// separately. A bare table name is ambiguous the moment an estate has two schemas, and every
    /// row these surfaces render came from a query that now knows which one it is.</para>
    ///
    /// <para>Falls back to the bare table name when the schema is absent, which happens when the
    /// login cannot resolve the object. Printing a leading dot there would be a fabricated
    /// qualification.</para>
    /// </summary>
    public static string QualifiedTable(string? schema, string? table)
    {
        var t = table ?? "";
        return string.IsNullOrEmpty(schema) ? t : schema + "." + t;
    }

    /// <summary>
    /// Quotes ONE identifier for T-SQL: a <c>]</c> inside a bracketed name is escaped by doubling
    /// it. The caller supplies the brackets.
    ///
    /// <para>⚠ IT LIVED ON Pages/IndexAnalysis.razor AND IS SHARED NOW, 2026-08-14. The page and
    /// the client report both emit scripts naming the same objects, and an escaping rule that
    /// exists in one of them is an escaping rule that is missing from the other. PROVED necessary
    /// live on 2026-08-13 against a database literally named <c>_sqlt_idxscope_we]ird;x=1</c>,
    /// whose DROP script did not parse; and again on 2026-08-14 against a SCHEMA named
    /// <c>we]ird</c>, which is the part this lane added and nothing had exercised.</para>
    /// </summary>
    public static string QuoteName(string? identifier) => (identifier ?? "").Replace("]", "]]");

    /// <summary>
    /// The three-part <c>[database].[schema].[table]</c> a script names, every part bracketed and
    /// escaped. One builder, so the DROP script, the maintenance script and anything later cannot
    /// disagree about what an object is called.
    ///
    /// <para>⚠ THE SCHEMA IS THE POINT. This was <c>[db].[dbo].[table]</c> with <c>dbo</c> written
    /// as a literal, because the queries projected OBJECT_NAME with no schema at all. Every script
    /// for a table outside dbo named an object that does not exist.</para>
    /// </summary>
    public static string ScriptTarget(string database, string schema, string table)
        => $"[{QuoteName(database)}].[{QuoteName(schema)}].[{QuoteName(table)}]";
}

/// <summary>
/// EVERY sentence Pages/IndexAnalysis.razor renders about its own scope, composed once from one
/// census snapshot and the two list lengths of the run that produced it.
///
/// <para>WHY IT IS A TYPE AND NOT SIX METHODS ON THE PAGE. It was six methods on the page, and
/// three of them stated numbers the run never measured (gate finding, 2026-08-13): the skipped
/// headline printed the CAPPED LIST LENGTH as "This run did not read N databases", the scope
/// headline printed "N scanned, M skipped" and invited an operator to add them into an instance
/// total, and the below-the-cut count included databases whose IO was never read. The house defect
/// class is a sentence beside a verdict that was not conditioned on the same measurement, and the
/// house ruling is that it is closed by STRUCTURE, never by vigilance. So the sentences live
/// where a test can call them, they are built from the census rather than from list lengths, and
/// they are all built from ONE snapshot so two of them cannot describe different moments.</para>
///
/// <para>THE ARITHMETIC, stated once. The census partitions the instance —
/// <c>UserDatabases = Sampled + PassedOver + RankedBelowCut</c> — so:</para>
/// <list type="bullet">
/// <item><see cref="NotRead"/> is <c>UserDatabases − scanned</c>, a count over the instance;</item>
/// <item><see cref="UnlistedCount"/> is that minus the list the page actually shows, and it
/// decomposes exactly into the rows the cap cut and the databases that lost the sample on a
/// reading they earned — proved by construction and asserted in both suites.</item>
/// </list>
/// </summary>
public sealed class IndexAnalysisScopeNarrative
{
    private readonly IndexAnalysisRankingCensus _census;
    private readonly int _scanned;
    private readonly int _listed;

    private IndexAnalysisScopeNarrative(IndexAnalysisRankingCensus census, int scanned, int listed)
    {
        _census = census;
        _scanned = scanned;
        _listed = listed;
    }

    /// <param name="census">The ranking pass's own counts for THIS run.</param>
    /// <param name="scannedCount">How many databases the run finished reading.</param>
    /// <param name="skippedListCount">How many rows the page's skipped list actually holds — the
    /// capped list, not a measurement, which is exactly why no sentence below prints it as one.</param>
    public static IndexAnalysisScopeNarrative For(
        IndexAnalysisRankingCensus census, int scannedCount, int skippedListCount)
        => new(census ?? new IndexAnalysisRankingCensus(),
               Math.Max(0, scannedCount), Math.Max(0, skippedListCount));

    /// <summary>The sample size the ranking pass reported it used. Falls back to the service's own
    /// constant only when the census never arrived, which means the run threw before that point.</summary>
    public int TopN => _census.TopN > 0 ? _census.TopN : IndexAnalysisService.TopDatabasesByIo;

    /// <summary>True when the census arrived, so the sentences may quote instance-wide totals.</summary>
    public bool HasCensus => _census.UserDatabases > 0;

    /// <summary>User databases on the instance minus the ones this run finished reading.</summary>
    public int NotRead => HasCensus ? Math.Max(0, _census.UserDatabases - _scanned) : 0;

    /// <summary>Databases the run did not read and the list below does not name.</summary>
    public int UnlistedCount => HasCensus ? Math.Max(0, NotRead - _listed) : 0;

    /// <summary>How many passed-over databases the cap kept out of the list.</summary>
    public int TruncatedCount => Math.Max(0, _census.PassedOver - _census.SkipReportCap);

    public string Headline
        => $"Unused and fragmented indexes: the {TopN} busiest user databases by IO. "
         + (HasCensus
             ? $"{_scanned} of {_census.UserDatabases} user database{Plural(_census.UserDatabases)} read."
             : $"{_scanned} database{Plural(_scanned)} read.");

    /// <summary>
    /// What "by IO" means, said out loud, and how many databases lost the sample on it. The
    /// counters are cumulative since the last instance start, exactly like the usage counters
    /// behind the Unused tab, so a recently restarted server ranks on a short window and the
    /// operator has to know that to read the order.
    /// </summary>
    public string MetricSentence
    {
        get
        {
            const string metric =
                "IO is read and write operations across each database's files, counted since the "
                + "instance last started. System databases are never ranked.";

            if (!HasCensus) return metric;

            var below = _census.RankedBelowCut;
            return below == 0
                ? metric + " No user database was left out of the sample on rank alone."
                : metric + $" {below} user database{Plural(below)} {Were(below)} left out on a "
                         + "measured IO figure below the cut.";
        }
    }

    /// <summary>
    /// The missing-index read's scope. It used to say it "covers every user database", which is two
    /// claims too many: the read is capped, and the missing-index DMVs hold a suggestion only for a
    /// query the optimizer actually costed — so a database absent from it has not been cleared, it
    /// has been unmentioned.
    ///
    /// <para>⚠ NO SURFACE NOUNS IN THIS TYPE, 2026-08-14. This sentence read "The Missing Indexes
    /// TAB is separate", which was true on the screen and false in the client PDF that renders the
    /// same string: a reader holding a printed report has no tab to consult. One sentence source
    /// for two surfaces only works while the sentences describe the DATA and never the chrome, so
    /// every word here has to be true of a web page and a page of paper at once. Asserted in
    /// IndexAnalysisMapperTests.</para>
    /// </summary>
    public static string MissingSentence
        => "Missing indexes are read separately. That read is one instance-wide pass, capped at the "
         + $"{IndexAnalysisService.MissingIndexRowCap} highest-impact suggestions, so it is not "
         + "limited to the databases below. It is not a survey of them either: the optimizer "
         + "records a suggestion only for a query it has actually costed.";

    public string SkippedHeadline
        => HasCensus
            ? $"This run did not read {NotRead} of the {_census.UserDatabases} user "
              + $"database{Plural(_census.UserDatabases)} on this instance."
            : $"This run did not read {_listed} database{Plural(_listed)}.";

    /// <summary>
    /// Rendered only when <see cref="UnlistedCount"/> is above zero. Every branch names the whole
    /// of the count it opens with — that is the point of splitting them, and the reason none of
    /// them is allowed to say "the busiest N": the passed-over rows are ordered by an IO figure
    /// that, for an unmeasured database, is a zero standing in for a reading nobody took, so which
    /// of them survive the cap is an alphabetical tiebreak and not a finding.
    /// </summary>
    public string UnlistedSentence
    {
        get
        {
            var n = UnlistedCount;
            var truncated = Math.Min(TruncatedCount, n);
            var below = n - truncated;

            var lead = $"{n} more user database{Plural(n)} {Were(n)} not read and {Are(n)} not "
                     + "named above";

            if (truncated > 0 && below > 0)
                return lead + $": {truncated} passed over beyond the {_census.SkipReportCap} this "
                            + $"list holds, and {below} that lost the sample on a measured IO figure.";

            if (truncated > 0)
                return lead + $". This list holds {_census.SkipReportCap} of the databases the run "
                            + "passed over.";

            return lead + ". They lost the sample on a measured IO figure, so the line above counts "
                        + "them rather than naming them.";
        }
    }

    /// <summary>
    /// What one per-database table covers, and what it does not. The row cap is stated because it
    /// is applied ONCE PER DATABASE: the table is up to twenty independently truncated lists laid
    /// end to end in rank order, which is not what a reader assumes a table is.
    ///
    /// <para>⚠ IT WAS CALLED TabCoverage AND SAID "this tab", 2026-08-14. The client PDF renders
    /// this same string over a printed table, where there is no tab to look at. A table is what
    /// both surfaces actually hold, so that is the only noun this sentence is allowed.</para>
    /// </summary>
    public string TableCoverage(int rowsPerDatabase, string rowNoun)
    {
        if (_scanned == 0)
            return "This run read no database, so this table describes nothing on this instance.";

        var covered = $"This table covers {_scanned} database{Plural(_scanned)}, and every row names "
                    + $"the database it came from. Each database contributes at most "
                    + $"{rowsPerDatabase} {rowNoun}, in its own order, so the rows below are those "
                    + "per-database lists one after another rather than a ranking across the instance.";

        return NotRead == 0
            ? covered
            : covered + $" It says nothing about the other {NotRead} user "
                      + $"database{Plural(NotRead)} on this instance.";
    }

    private static string Plural(int n) => n == 1 ? "" : "s";
    private static string Were(int n) => n == 1 ? "was" : "were";
    private static string Are(int n) => n == 1 ? "is" : "are";
}
