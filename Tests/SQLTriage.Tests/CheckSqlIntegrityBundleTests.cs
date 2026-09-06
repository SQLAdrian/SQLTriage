/* In the name of God, the Merciful, the Compassionate */

using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services.Licensing;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// S1 end-to-end integration: a check loaded through the REAL bundle chain
/// (BundleManifest → BundleAccessor → CheckRepositoryService.LoadViaBundle →
/// CheckSqlStore) verifies Ok against its own SQL and Mismatch (→ block) against
/// tampered SQL. This proves the tamper-block decision on the live load path, not
/// just CheckSqlStore in isolation.
/// </summary>
public class CheckSqlIntegrityBundleTests
{
    // V2 Markdown+frontmatter the bundle loader understands (972cb27 contract;
    // realigned 2026-06-12). All required fields per ParseInvariants (id, title,
    // category, severity, source, applicability, result_contract,
    // framework_mappings, provenance). SQL is supplied via the .sql sibling,
    // so the Markdown body carries no ## Query block.
    private const string CheckYaml = @"---
id: SQLT-CORE-00042
title: Integrity Test Check
category: Performance
severity: Medium
source:
  framework: sp_Blitz
  ref: 42
applicability:
  engine_editions: [SqlServer]
  scope: instance
result_contract: verdict
framework_mappings: []
provenance: custom
---

## Intent
An integrity test check.
";
    private const string CheckSql = "SELECT CASE WHEN 1=0 THEN 1 ELSE 0 END AS Result";

    private static BundleManifest ManifestWithCheck() => new()
    {
        BundleVersion = 1,
        BuildNumber = 1,
        CreatedUtc = "2026-05-26T00:00:00Z",
        ClientName = "TEST_CLIENT_NEVER_PROD",
        Tier = "Full",
        Features = new ManifestFeatures { FullCorpus = true, CheckIds = new() },
        Corpus = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["check_042_integrity.yaml"] = CheckYaml,
            ["check_042_integrity.sql"] = CheckSql,
        }
    };

    /// <summary>Loads the manifest through the real repo + populates a CheckSqlStore.</summary>
    private static async Task<(CheckSqlStore store, SqlCheck check)> LoadThroughBundle()
    {
        var accessor = new BundleAccessor();
        accessor.Replace(ManifestWithCheck(), Tier.Full);

        var repo = new CheckRepositoryService(
            NullLogger<CheckRepositoryService>.Instance, configuration: null, bundle: accessor);
        await repo.LoadChecksAsync();

        Assert.Null(repo.LoadError);
        Assert.Equal("bundle", repo.LoadSource);
        var check = Assert.Single(repo.Checks);

        var store = new CheckSqlStore();
        store.PopulateFromCatalogue(repo.Checks);
        return (store, check);
    }

    [Fact]
    public async Task BundleLoadedCheck_VerifiesOk_AgainstOwnSql()
    {
        var (store, check) = await LoadThroughBundle();

        // The SQL the executor would resolve for this check, straight from the store.
        Assert.True(store.TryGet(check.Id, out var sql));
        Assert.Equal(SqlIntegrity.Ok, store.Verify(check.Id, sql));
    }

    [Fact]
    public async Task BundleLoadedCheck_TamperedSql_VerifiesMismatch()
    {
        var (store, check) = await LoadThroughBundle();

        Assert.True(store.TryGet(check.Id, out var sql));
        // Simulate post-decrypt / in-memory tampering before execution.
        var tampered = sql + "; DROP TABLE Sensitive;";

        Assert.Equal(SqlIntegrity.Mismatch, store.Verify(check.Id, tampered));
    }
}
