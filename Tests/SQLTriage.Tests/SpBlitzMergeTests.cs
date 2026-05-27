/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// Covers the sp_BLITZ cache round-trip (corrected NOT NULL schema) and the
/// BlitzFinding -> CheckResult adapter that feeds the merged Audit results grid.
/// These exercise the previously-orphaned merge path wired into QuickCheck.
/// </summary>
public sealed class SpBlitzMergeTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SpBlitzCache _cache;

    public SpBlitzMergeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"spblitz-test-{Guid.NewGuid():N}.db");
        _cache = new SpBlitzCache(_dbPath);
    }

    public void Dispose()
    {
        _cache.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
    }

    private static BlitzFinding Finding(int priority, string finding, string? db = null, string? url = null) =>
        new()
        {
            Priority = priority,
            FindingsGroup = "Reliability",
            Finding = finding,
            DatabaseName = db,
            Details = $"Details for {finding}",
            Url = url,
            ServerLabel = "TESTSRV",
            ImportedUtc = DateTime.UtcNow,
            ImportId = Guid.NewGuid()
        };

    // ── Schema fix: NOT NULL database_name (no COALESCE in PRIMARY KEY) ────────

    [Fact]
    public async Task SaveThenLoad_RoundTrips_IncludingNullDatabaseName()
    {
        await _cache.SaveAsync(new[]
        {
            Finding(1, "Server-scoped finding", db: null),
            Finding(50, "Database-scoped finding", db: "AdventureWorks"),
        });

        var rows = await _cache.LoadAllRecentAsync();

        Assert.Equal(2, rows.Count);
        // The null-database finding must persist without violating the NOT NULL PK.
        Assert.Contains(rows, r => r.Finding == "Server-scoped finding");
        Assert.Contains(rows, r => r.DatabaseName == "AdventureWorks");
    }

    [Fact]
    public async Task Save_DuplicatePrimaryKey_IsIgnored()
    {
        var importId = Guid.NewGuid();
        BlitzFinding Dup() => new()
        {
            Priority = 1,
            FindingsGroup = "Reliability",
            Finding = "Same",
            DatabaseName = null,
            Details = "x",
            ServerLabel = "TESTSRV",
            ImportedUtc = DateTime.UtcNow,
            ImportId = importId
        };

        await _cache.SaveAsync(new[] { Dup() });
        await _cache.SaveAsync(new[] { Dup() }); // INSERT OR IGNORE — same PK

        var rows = await _cache.LoadAllRecentAsync();
        Assert.Single(rows);
    }

    // ── Adapter: BlitzFinding -> CheckResult ──────────────────────────────────

    [Theory]
    [InlineData(1, "Critical")]
    [InlineData(2, "Warning")]
    [InlineData(50, "Warning")]
    [InlineData(51, "Info")]
    [InlineData(200, "Info")]
    public void Adapt_MapsPriorityToSeverity(int priority, string expectedSeverity)
    {
        var result = BlitzFindingToCheckResultAdapter.Adapt(Finding(priority, "F"));
        Assert.Equal(expectedSeverity, result.Severity);
    }

    [Fact]
    public void Adapt_SetsBlitzCategoryAndFailingState()
    {
        var result = BlitzFindingToCheckResultAdapter.Adapt(Finding(1, "Bad thing"));

        Assert.Equal(BlitzFindingToCheckResultAdapter.BlitzCategory, result.Category);
        Assert.StartsWith(BlitzFindingToCheckResultAdapter.BlitzCheckIdPrefix, result.CheckId);
        Assert.False(result.Passed);
        Assert.True(result.IsBad);
        Assert.Equal("TESTSRV", result.InstanceName);
    }

    [Fact]
    public void Adapt_IncludesUrlInRecommendedAction_WhenPresent()
    {
        var withUrl = BlitzFindingToCheckResultAdapter.Adapt(
            Finding(1, "F", url: "https://brentozar.com/go/x"));
        Assert.Contains("https://brentozar.com/go/x", withUrl.RecommendedAction);

        var noUrl = BlitzFindingToCheckResultAdapter.Adapt(Finding(1, "F"));
        Assert.Null(noUrl.RecommendedAction);
    }

    [Fact]
    public void AdaptAll_ProducesUniqueCheckIds_AcrossDistinctFindings()
    {
        var adapted = BlitzFindingToCheckResultAdapter.AdaptAll(new[]
        {
            Finding(1, "Alpha"),
            Finding(1, "Beta"),
            Finding(50, "Gamma"),
        }).ToList();

        Assert.Equal(3, adapted.Count);
        Assert.Equal(3, adapted.Select(r => r.CheckId).Distinct().Count());
    }

    [Fact]
    public void Adapt_SameFindingDifferentServers_ProducesDistinctCheckIds()
    {
        // Regression: the same sp_Blitz finding on two servers must stay TWO rows.
        // Previously the CheckId omitted the server, so merge dedup-by-CheckId
        // collapsed every server's copy into one (458 raw -> ~104 shown).
        var srvA = new BlitzFinding
        {
            Priority = 1,
            FindingsGroup = "Reliability",
            Finding = "Backups Not Taken",
            Details = "x",
            ServerLabel = "SRV-A",
            ImportedUtc = DateTime.UtcNow,
            ImportId = Guid.NewGuid()
        };
        var srvB = new BlitzFinding
        {
            Priority = 1,
            FindingsGroup = "Reliability",
            Finding = "Backups Not Taken",
            Details = "x",
            ServerLabel = "SRV-B",
            ImportedUtc = DateTime.UtcNow,
            ImportId = Guid.NewGuid()
        };

        var a = BlitzFindingToCheckResultAdapter.Adapt(srvA);
        var b = BlitzFindingToCheckResultAdapter.Adapt(srvB);

        Assert.NotEqual(a.CheckId, b.CheckId);   // distinct per server
        Assert.Equal("SRV-A", a.InstanceName);
        Assert.Equal("SRV-B", b.InstanceName);

        // ...but the same finding on the SAME server still dedups (identical CheckId).
        var aAgain = BlitzFindingToCheckResultAdapter.Adapt(srvA);
        Assert.Equal(a.CheckId, aAgain.CheckId);
    }
}
