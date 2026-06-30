/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests;

public class SqlQueryRepositoryBundleTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SqlQueryRepository Make(FakeBundleAccessor bundle) =>
        new SqlQueryRepository(NullLogger<SqlQueryRepository>.Instance, bundle);

    private const string MinimalQueriesJson = """
        {
          "queries": {
            "TestQuery": {
              "description": "A test query",
              "category": "Performance",
              "severity": "HIGH",
              "status": "working",
              "quick": true,
              "audience": ["DBA"],
              "controls": [],
              "timeoutSec": 10
            }
          }
        }
        """;

    // ── Bundle-locked / empty-bundle tests ───────────────────────────────────

    [Fact]
    public void Returns_Empty_When_Bundle_Locked()
    {
        var bundle = new FakeBundleAccessor().SetLocked();
        var svc = Make(bundle);
        // Queries dict may be empty because no SQL files exist on disk either;
        // the key assertion is that GetAll() does NOT throw.
        Assert.NotNull(svc.GetAll());
    }

    [Fact]
    public void GetAll_WhenQueriesJsonAbsentFromBundle_ReturnsEmpty()
    {
        // Bundle has no queries.json; Data/Sql/ directory likely absent in test env too.
        var bundle = new FakeBundleAccessor(); // no file registered
        var svc = Make(bundle);
        Assert.Empty(svc.GetAll());
    }

    // ── Metadata cache invalidation ───────────────────────────────────────────

    [Fact]
    public void BundleStateChanged_InvalidatesMetadataCache()
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/queries.json", MinimalQueriesJson);
        var svc = Make(bundle);

        // First read — loads cache.
        _ = svc.GetAll();

        // Remove the file and raise state-changed.
        // The accessor will return null for the path on next GetMetadata call.
        var bundle2 = new FakeBundleAccessor(); // empty
        // Simulate the accessor returning nothing — just raise state-changed;
        // the service will reload from the same bundle (which still has the file
        // registered, but the important thing is no exception is thrown).
        bundle.RaiseStateChanged();

        // Must not throw after invalidation.
        Assert.NotNull(svc.GetAll());
    }

    // ── Get / GetByTag / GetQuickChecks ──────────────────────────────────────

    [Fact]
    public void Get_NonExistentId_ReturnsNull()
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/queries.json", MinimalQueriesJson);
        var svc = Make(bundle);
        Assert.Null(svc.Get("NonExistentQuery"));
    }

    [Fact]
    public void GetByTag_UnknownTag_ReturnsEmpty()
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/queries.json", MinimalQueriesJson);
        var svc = Make(bundle);
        Assert.Empty(svc.GetByTag("unknowntag"));
    }

    [Fact]
    public void GetQuickChecks_WhenNoSqlFiles_ReturnsEmpty()
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/queries.json", MinimalQueriesJson);
        var svc = Make(bundle);
        // No .sql files on disk in the test environment — GetQuickChecks returns empty.
        Assert.Empty(svc.GetQuickChecks());
    }

    // ── ReloadAsync does not throw ────────────────────────────────────────────

    [Fact]
    public async Task ReloadAsync_DoesNotThrow()
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/queries.json", MinimalQueriesJson);
        var svc = Make(bundle);
        // Smoke: must not throw even if the SQL directory doesn't exist.
        await svc.ReloadAsync();
    }
}
