/* In the name of God, the Merciful, the Compassionate */

// ── strings-r2-03 / r2-01 / r2-02: the cache that manufactured a zero, 2026-08-28 ──────────────
//
// WHY THIS FILE EXISTS. Two shipped stat cards returned SQL NULL on every install:
//
//   pmemory.buffer_pool          WHERE type = 'CACHESTORE_BUFPOOL'  - a memory-clerk type that
//                                exists on no SQL Server. The real one is MEMORYCLERK_SQLBUFFERPOOL.
//                                PROVED live: shipped query NULL, corrected query 532.53 MB on
//                                .\new2022 and 18.10 MB on .\old2017. The clerk list on both
//                                instances holds MEMORYCLERK_SQLBUFFERPOOL and no CACHESTORE_BUFPOOL.
//   livetempdb.used_mb           SELECT ... FROM sys.dm_db_file_space_usage - a DATABASE-scoped DMV,
//   livetempdb.version_store_mb  read unqualified while connected to the panel's own
//                                defaultDatabase "master". PROVED live: NULL from master, 8.6 MB and
//                                3.8 MB from tempdb.sys.dm_db_file_space_usage on .\new2022, while
//                                the sibling grid livetempdb.file_usage on the SAME TAB already used
//                                the three-part name and printed real per-file megabytes beside them.
//
// Those two are blank tiles, which is bad. What made it worse is the third finding:
// CachingQueryExecutor.PreloadFromCacheAsync turned each NULL into a confident 0, and
// DynamicDashboard cleared the panel's cache badge as soon as its live fetch finished while the
// preload dictionary was still on screen. So the client saw "0 MB" presented as a live reading.
//
// THE FINDING WAS FILED believe, FOR A STATED REASON: "I did not drive a NULL through the store and
// watch the pixel." This file closes the first half of that - a NULL really does survive the store
// round-trip and really did produce a 0 - by driving the REAL liveQueriesCacheStore and the REAL
// decision function. The second half, the pixel, is a live integration probe against a cold HEAD
// build, recorded in the commit message; it is not something a unit test can claim.
//
// WHAT IS REAL HERE. CachingQueryExecutor.TryReadCachedStat is the production decision function
// PreloadFromCacheAsync calls. liveQueriesCacheStore is the production store, writing to the test
// output directory's own SQLTriage-cache.db. Nothing is re-implemented.
//
// MUTATION THAT MUST FAIL. Restore the old body:
//     double val = 0;
//     if (dt.Columns.Count > 0 && row[0] != DBNull.Value) double.TryParse(row[0]?.ToString(), out val);
//     statResults[panel.Id] = new StatValue { Value = val };
// A_null_reading_is_not_published_as_zero, A_value_column_is_read_by_name_not_by_ordinal and
// A_cached_number_survives_a_comma_decimal_locale all go red - one per defect.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using Xunit;

namespace SQLTriage.Tests;

public sealed class CachedStatHonestyTests : IDisposable
{
    private readonly liveQueriesCacheStore _store = new();
    private readonly string _queryId = "strings-r2-03-" + Guid.NewGuid().ToString("N");
    private const string InstanceKey = "__strings_r2_03__";

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private static DataTable OneRow(string columnName, object? cell)
    {
        var dt = new DataTable();
        dt.Columns.Add(columnName);
        var row = dt.NewRow();
        row[0] = cell ?? DBNull.Value;
        dt.Rows.Add(row);
        return dt;
    }

    // ── The decision function, defect by defect ─────────────────────────────

    /// <summary>
    /// Defect one, and the headline. A NULL is the absence of a reading. Publishing 0 for it is not
    /// a rounding choice, it is an invention - and on a buffer-pool tile, "0 MB" is a number a DBA
    /// would act on.
    /// </summary>
    [Fact]
    public void A_null_reading_is_not_published_as_zero()
    {
        CachingQueryExecutor.TryReadCachedStat(OneRow("Value", DBNull.Value), out var v)
            .Should().BeFalse("a NULL is the absence of a measurement, not a measurement of zero");
        v.Should().Be(0, "the out parameter is untouched, which is why the bool is the answer");

        // A genuine zero is still a reading and still publishes. Absence and zero are different
        // facts, and the whole fix rests on the difference.
        CachingQueryExecutor.TryReadCachedStat(OneRow("Value", 0.0), out var zero).Should().BeTrue();
        zero.Should().Be(0);
    }

    /// <summary>
    /// Defect two. The live path reads <c>dt.Rows[0]["Value"]</c> by NAME; the cached path read
    /// <c>row[0]</c> by ORDINAL. A cached table whose first column is a label therefore parsed the
    /// label, failed, and published the same fabricated 0 - from a table that held a perfectly good
    /// number one column to the right.
    /// </summary>
    [Fact]
    public void A_value_column_is_read_by_name_not_by_ordinal()
    {
        var dt = new DataTable();
        dt.Columns.Add("Label");
        dt.Columns.Add("Value");
        var row = dt.NewRow();
        row["Label"] = "Buffer Pool";
        row["Value"] = 532.53;
        dt.Rows.Add(row);

        CachingQueryExecutor.TryReadCachedStat(dt, out var v).Should().BeTrue();
        v.Should().Be(532.53, "reading ordinal 0 here would have parsed the string \"Buffer Pool\"");
    }

    /// <summary>
    /// Defect three, which is worse than a zero because it is plausible. The SQLite tier serialises
    /// every cell to an invariant JSON number and deserialises every cell back as a STRING, and the
    /// old code parsed that string with <c>double.TryParse(s, out val)</c> - current culture. On a
    /// machine whose locale uses a comma decimal separator, "532.53" parses as 53253: a buffer pool
    /// reading a hundred times too large, on a tile with no way to tell.
    /// </summary>
    [Fact]
    public void A_cached_number_survives_a_comma_decimal_locale()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            // The shape the SQLite tier really hands back: the number as an invariant string.
            CachingQueryExecutor.TryReadCachedStat(OneRow("Value", "532.53"), out var v)
                .Should().BeTrue();
            v.Should().Be(532.53);

            // The defect, pinned so a regression is recognisable rather than merely absent. This is
            // the EXACT overload the old code called - double.TryParse(string, out double), which
            // is NumberStyles.Float | AllowThousands against CurrentCulture. Under de-DE the '.' is
            // a group separator, so the parse SUCCEEDS and returns a number a hundred times too
            // large. MEASURED, not reasoned: 53253.
            double.TryParse("532.53", out var wrong)
                .Should().BeTrue("the old parse did not fail, which is what made it dangerous");
            wrong.Should().Be(53253, "a 532.53 MB buffer pool rendered as 53,253 MB");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Nothing_that_is_not_a_reading_is_published()
    {
        CachingQueryExecutor.TryReadCachedStat(null, out _).Should().BeFalse("no table");
        CachingQueryExecutor.TryReadCachedStat(new DataTable(), out _).Should().BeFalse("no rows");
        CachingQueryExecutor.TryReadCachedStat(OneRow("Total", 5.0), out _)
            .Should().BeFalse("no Value column, so nothing here is the stat card's number");
        CachingQueryExecutor.TryReadCachedStat(OneRow("Value", "n/a"), out _)
            .Should().BeFalse("a word is not a measurement");
        CachingQueryExecutor.TryReadCachedStat(OneRow("Value", double.NaN), out _)
            .Should().BeFalse("NaN is the arithmetic spelling of 'no answer'");
    }

    // ── The real store, which is the seam the finding named ─────────────────

    /// <summary>
    /// The round trip the reproduce pass could not exercise: a NULL written to the production
    /// SQLite tier, read back from it, and refused. This is what turns "the NULLs from r2-01 and
    /// r2-02 do enter the cache" from a reading into a measurement.
    /// </summary>
    [Fact]
    public async Task A_null_written_to_the_real_store_comes_back_as_no_reading()
    {
        await _store.UpsertDataTableAsync(_queryId, InstanceKey, OneRow("Value", DBNull.Value), DateTime.UtcNow);

        var back = await ReadWhenCommittedAsync(_queryId);
        back.Should().NotBeNull("the row was written, so a miss here would test nothing");
        back!.Rows.Count.Should().Be(1);
        back.Rows[0]["Value"].Should().Be(DBNull.Value, "the NULL survives the JSON round trip");

        CachingQueryExecutor.TryReadCachedStat(back, out _).Should().BeFalse();
    }

    /// <summary>
    /// The control: a real number survives the same round trip and IS published, so the fix cannot
    /// be "refuse everything".
    /// </summary>
    [Fact]
    public async Task A_real_number_written_to_the_real_store_comes_back_intact()
    {
        var id = _queryId + "-live";
        await _store.UpsertDataTableAsync(id, InstanceKey, OneRow("Value", 532.53), DateTime.UtcNow);

        var back = await ReadWhenCommittedAsync(id);
        CachingQueryExecutor.TryReadCachedStat(back, out var v).Should().BeTrue();
        v.Should().Be(532.53, "the buffer-pool reading the corrected panel produces on NEW2022");
    }

    /// <summary>
    /// WHY THIS POLL EXISTS, and it is not flakiness-papering. liveQueriesCacheStore batches writes
    /// onto a channel, and its pump calls <c>op.Completion.TrySetResult()</c> INSIDE the
    /// transaction, before <c>tx.Commit()</c>. So <c>await UpsertDataTableAsync(...)</c> returns
    /// before the row is committed, and a read on a second connection cannot see it yet. Measured
    /// while writing these tests: without the wait, both reads above returned null.
    ///
    /// <para>That is a property of the store, not of this lane's subject, and it is left alone
    /// here rather than quietly changed - altering when a write is signalled as complete is a
    /// change to every caller's durability contract, and no finding in this lane asked for it. It
    /// is reported as a residual instead.</para>
    /// </summary>
    private async Task<DataTable?> ReadWhenCommittedAsync(string queryId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var dt = await _store.GetDataTableAsync(queryId, InstanceKey);
            if (dt is not null) return dt;
            await Task.Delay(100);
        }
        return null;
    }

    // ── The CALL SITE, not just the decision function ───────────────────────

    /// <summary>
    /// THE WIRING, driven through the real <c>PreloadFromCacheAsync</c> on a real
    /// <c>CachingQueryExecutor</c> over a real hot tier. Testing the decision function alone would
    /// not be enough, and this repo has the scar to prove it: AlertBaselineFenceDirectionTests
    /// records that bypassing BOTH call sites of its fixed functions passed all 4,672 tests. So the
    /// NULL goes into the store the executor actually reads, the executor's own method is called,
    /// and the assertion is about the dictionary the dashboard renders from.
    ///
    /// <para>This is what the finding's own <c>believe</c> tag was about - "I did not drive a NULL
    /// through the store and watch the pixel". The NULL is now driven through the store and through
    /// the production preload. The pixel itself is not captured here: the fabricated value is
    /// created in this method and the renderer only displays the dictionary this method fills, so
    /// the dictionary is where the measurement belongs.</para>
    /// </summary>
    [Fact]
    public async Task The_preload_publishes_nothing_for_a_cached_null_and_the_real_number_when_there_is_one()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var hot = new CacheHotTier(memory, NullLogger<CacheHotTier>.Instance);
        var configService = new DashboardConfigService(NullLogger<DashboardConfigService>.Instance);
        var connections = new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var inner = new QueryExecutor(
            new SqlServerConnectionFactory("Server=(local);Database=master;Integrated Security=true;"),
            configService, configuration, connections, new ResilienceService());

        var executor = new CachingQueryExecutor(
            inner, _store, hot, new CacheStateTracker(), configService, configuration,
            NullLogger<CachingQueryExecutor>.Instance);

        // The real panel from the real catalogue: the buffer-pool tile whose SQL returned NULL on
        // every install until this commit.
        var panel = new PanelDefinition
        {
            Id = "pmemory.buffer_pool",
            Title = "Buffer Pool",
            PanelType = "StatCard",
            StatUnit = "MB"
        };
        var filter = new DashboardFilter { Instances = new[] { "__strings_r2_03_probe__" } };
        var instanceKey = CachingQueryExecutor.BuildInstanceKey(filter);

        // BEFORE: exactly what strings-r2-01 produced - one row, Value NULL - sitting in the tier
        // the preload reads first.
        await hot.SetDataTableAsync(panel.Id, instanceKey, OneRow("Value", DBNull.Value));

        var stats = new ConcurrentDictionary<string, StatValue>();
        await executor.PreloadFromCacheAsync(
            new[] { panel }, filter,
            new ConcurrentDictionary<string, List<TimeSeriesPoint>>(), stats,
            new ConcurrentDictionary<string, List<StatValue>>(),
            new ConcurrentDictionary<string, DataTable>(),
            new ConcurrentDictionary<string, List<CheckStatus>>());

        stats.Should().NotContainKey(panel.Id,
            "a cached NULL used to become a StatValue of 0 here, and DynamicDashboard clears the "
            + "cache badge per panel while this dictionary is still on screen, so the client read it "
            + "as a live '0 MB'");

        // AFTER: the number the corrected panel really produces on .\new2022.
        await hot.SetDataTableAsync(panel.Id, instanceKey, OneRow("Value", 532.53));

        stats.Clear();
        await executor.PreloadFromCacheAsync(
            new[] { panel }, filter,
            new ConcurrentDictionary<string, List<TimeSeriesPoint>>(), stats,
            new ConcurrentDictionary<string, List<StatValue>>(),
            new ConcurrentDictionary<string, DataTable>(),
            new ConcurrentDictionary<string, List<CheckStatus>>());

        stats.Should().ContainKey(panel.Id, "a real reading must still be served from cache");
        stats[panel.Id].Value.Should().Be(532.53);
        stats[panel.Id].Unit.Should().Be("MB",
            "the preloaded card used to render its number with no unit beside it");
        stats[panel.Id].Label.Should().Be("Buffer Pool");
    }

    // ── The two panels whose NULL the cache was turning into a zero ─────────

    private static JsonObject Panel(string dashboardId, string panelId)
    {
        var path = Path.Combine(RawPassedScan.RepoRoot().FullName, "Config", "dashboard-config.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        var dashboard = root["dashboards"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(d => string.Equals(d["id"]!.GetValue<string>(), dashboardId, StringComparison.OrdinalIgnoreCase));

        return SQLTriage.Data.DashboardConfigMigrator.EnumerateDashboardPanels(dashboard)
            .Single(p => string.Equals(p["id"]!.GetValue<string>(), panelId, StringComparison.OrdinalIgnoreCase));
    }

    private static string Sql(JsonObject panel) => panel["query"]!["sqlServer"]!.GetValue<string>();

    /// <summary>
    /// strings-r2-01, the half the dashboards lane left behind. That lane removed the panel's
    /// page-life-expectancy over-claim but left the SQL alone, and then wrote the wrong clerk name
    /// into the corrected description as though it were correct terminology. Both are fixed here:
    /// the query names the clerk that exists, and so does the prose.
    /// </summary>
    [Fact]
    public void The_buffer_pool_tile_queries_a_memory_clerk_that_exists()
    {
        var panel = Panel("memory", "pmemory.buffer_pool");

        Sql(panel).Should().NotContain("CACHESTORE_BUFPOOL",
            "no SQL Server has a memory clerk by that name; the tile was blank on every install");
        Sql(panel).Should().Contain("MEMORYCLERK_SQLBUFFERPOOL");

        var description = panel["description"]!.GetValue<string>();
        description.Should().NotContain("CACHESTORE_BUFPOOL",
            "the corrected description stated the wrong clerk name as if it were right");
        description.Should().Contain("MEMORYCLERK_SQLBUFFERPOOL");
        description.Should().Contain("does not show page life expectancy",
            "the dashboards lane's own correction must survive this one");
    }

    /// <summary>
    /// strings-r2-02. sys.dm_db_file_space_usage is database-scoped, and these two cards read it
    /// unqualified under defaultDatabase "master" - so they measured master's own file usage, which
    /// is NULL for these columns, while the grid directly below them on the same tab used the
    /// three-part name and printed real tempdb megabytes.
    /// </summary>
    [Theory]
    [InlineData("livetempdb.used_mb")]
    [InlineData("livetempdb.version_store_mb")]
    public void The_tempdb_stat_cards_read_tempdb(string panelId)
    {
        var sql = Sql(Panel("livetempdb", panelId));

        sql.Should().Contain("tempdb.sys.dm_db_file_space_usage",
            "the sibling grid on the same tab has always used the three-part name");
        sql.Should().NotContain("FROM sys.dm_db_file_space_usage",
            "unqualified, this reads whatever database the connection is on, which is master");
    }

    /// <summary>
    /// The sibling that proves the fix is the shape that already worked. If this ever stops using
    /// the three-part name, the two cards above lost their reference point.
    /// </summary>
    [Fact]
    public void The_sibling_grid_is_still_the_worked_example()
    {
        Sql(Panel("livetempdb", "livetempdb.file_usage"))
            .Should().Contain("tempdb.sys.dm_db_file_space_usage");
    }

    /// <summary>
    /// Delivery, the strings-r1-11 gap: an install keeps its own dashboard-config.json, so all three
    /// panels need their pre-fix body registered in the superseded-bodies resource or the fix
    /// reaches fresh installs only. Driven through the real migrator over the real resource.
    /// </summary>
    [Theory]
    [InlineData("memory", "pmemory.buffer_pool")]
    [InlineData("livetempdb", "livetempdb.used_mb")]
    [InlineData("livetempdb", "livetempdb.version_store_mb")]
    public void The_prefix_panel_body_is_registered_as_superseded(string dashboardId, string panelId)
    {
        var superseded = SQLTriage.Data.DashboardConfigMigrator.ReadSupersededBodies();
        superseded.Should().NotBeNull("the superseded bodies are embedded by SQLTriage.csproj");

        var entries = JsonNode.Parse(superseded!)!["supersededPanels"]!.AsArray()
            .Select(n => n!.AsObject())
            .Where(e => string.Equals(e["dashboardId"]!.GetValue<string>(), dashboardId, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(e["panelId"]!.GetValue<string>(), panelId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        entries.Should().NotBeEmpty(panelId + " has no superseded body, so its fix reaches fresh installs only");

        // At least one registered body must be the one that was actually replaced: the pre-fix SQL.
        entries.Select(e => e["panel"]!["query"]!["sqlServer"]!.GetValue<string>())
            .Should().Contain(s => s.Contains("CACHESTORE_BUFPOOL", StringComparison.Ordinal)
                                || s.Contains("FROM sys.dm_db_file_space_usage", StringComparison.Ordinal),
                panelId + " registers bodies, but none of them is the one this commit replaced");
    }
}
