/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// S1 per-query SQL integrity: CheckSqlStore captures a SHA-256 baseline of the
/// normalised SQL at load time and Verify() reports Ok / Missing / Mismatch.
/// </summary>
public class CheckSqlIntegrityTests
{
    private static SqlCheck Check(string id, string sql) => new()
    {
        Id = id,
        SqlQuery = sql,
        Name = "t",
        Category = "c",
        Severity = "High"
    };

    private static CheckSqlStore Populated(string id, string sql)
    {
        var store = new CheckSqlStore();
        store.PopulateFromCatalogue(new[] { Check(id, sql) });
        return store;
    }

    // ── ComputeHash: deterministic + normalisation ───────────────────────────

    [Fact]
    public void ComputeHash_IsDeterministic()
        => Assert.Equal(CheckSqlStore.ComputeHash("SELECT 1"), CheckSqlStore.ComputeHash("SELECT 1"));

    [Fact]
    public void ComputeHash_NormalisesLineEndings()
        => Assert.Equal(
            CheckSqlStore.ComputeHash("SELECT 1\nFROM t"),
            CheckSqlStore.ComputeHash("SELECT 1\r\nFROM t"));

    [Fact]
    public void ComputeHash_TrimsOuterWhitespace()
        => Assert.Equal(
            CheckSqlStore.ComputeHash("SELECT 1"),
            CheckSqlStore.ComputeHash("  \n SELECT 1 \n  "));

    [Fact]
    public void ComputeHash_PreservesInteriorWhitespace()
        => Assert.NotEqual(
            CheckSqlStore.ComputeHash("SELECT 1 FROM t"),
            CheckSqlStore.ComputeHash("SELECT 1  FROM t")); // two spaces — significant

    // ── Verify: Ok / Missing / Mismatch ──────────────────────────────────────

    [Fact]
    public void Verify_MatchingSql_ReturnsOk()
    {
        var store = Populated("BLITZ_001", "SELECT * FROM sys.databases");
        Assert.Equal(SqlIntegrity.Ok, store.Verify("BLITZ_001", "SELECT * FROM sys.databases"));
    }

    [Fact]
    public void Verify_MatchingSql_ToleratesLineEndingDiff()
    {
        var store = Populated("BLITZ_001", "SELECT 1\nFROM t");
        Assert.Equal(SqlIntegrity.Ok, store.Verify("BLITZ_001", "SELECT 1\r\nFROM t"));
    }

    [Fact]
    public void Verify_TamperedSql_ReturnsMismatch()
    {
        var store = Populated("BLITZ_001", "SELECT * FROM sys.databases");
        // post-decrypt mutation: an attacker appends a destructive statement
        Assert.Equal(SqlIntegrity.Mismatch,
            store.Verify("BLITZ_001", "SELECT * FROM sys.databases; DROP TABLE x;"));
    }

    [Fact]
    public void Verify_UnknownCheckId_ReturnsMissing()
    {
        var store = Populated("BLITZ_001", "SELECT 1");
        Assert.Equal(SqlIntegrity.Missing, store.Verify("BLITZ_999", "SELECT 1"));
    }

    [Fact]
    public void Verify_EmptyStore_ReturnsMissing()
        => Assert.Equal(SqlIntegrity.Missing, new CheckSqlStore().Verify("X", "SELECT 1"));

    [Fact]
    public void Populate_SkipsBlankSql_NoBaselineCaptured()
    {
        var store = new CheckSqlStore();
        store.PopulateFromCatalogue(new[] { Check("EMPTY", "   ") });
        Assert.Equal(SqlIntegrity.Missing, store.Verify("EMPTY", ""));
    }

    [Fact]
    public void Repopulate_RefreshesBaseline()
    {
        var store = Populated("BLITZ_001", "SELECT 1");
        // a new bundle load with changed SQL becomes the new baseline (not a mismatch)
        store.PopulateFromCatalogue(new[] { Check("BLITZ_001", "SELECT 2") });
        Assert.Equal(SqlIntegrity.Ok, store.Verify("BLITZ_001", "SELECT 2"));
        Assert.Equal(SqlIntegrity.Mismatch, store.Verify("BLITZ_001", "SELECT 1"));
    }
}
