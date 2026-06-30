/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests.Parser
{
    /// <summary>
    /// Slice 5 — verifies the <c>CheckRepository:UseSourceParser</c>
    /// feature flag wiring without touching the legacy JSON path.
    /// </summary>
    public class CheckRepositoryServiceSourceParserTests
    {
        private static IConfiguration ConfigWith(string? path, bool useParser = true)
        {
            var dict = new Dictionary<string, string?>
            {
                ["CheckRepository:UseSourceParser"] = useParser.ToString(),
                ["CheckRepository:SourceParserPath"] = path,
            };
            return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        }

        private static string FixtureDir(string docText)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"b2-s5-{System.Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "check.md"), docText);
            return dir;
        }

        // V2 Markdown+frontmatter (972cb27 contract; realigned 2026-06-12).
        private const string MinimalDoc = @"---
id: SQLT-CORE-00010
title: t
category: Performance
severity: Medium
source:
  framework: sp_Blitz
  ref: 10
applicability:
  engine_editions: [SqlServer]
  scope: instance
result_contract: verdict
framework_mappings: []
provenance: custom
---

## Intent
d

## Query
```sql
SELECT CASE WHEN 1=0 THEN 1 ELSE 0 END
```
";

        [Fact]
        public async Task UseSourceParser_true_with_valid_dir_loads_via_parser()
        {
            var dir = FixtureDir(MinimalDoc);
            try
            {
                var svc = new CheckRepositoryService(NullLogger<CheckRepositoryService>.Instance, ConfigWith(dir));
                await svc.LoadChecksAsync();

                Assert.Null(svc.LoadError);
                Assert.Equal("source-parser", svc.LoadSource);
                Assert.Single(svc.Checks);
                Assert.Equal("SQLT-CORE-00010", svc.Checks[0].Id);
                Assert.NotNull(svc.SourceIntegrityHash);
                Assert.Equal(64, svc.SourceIntegrityHash!.Length); // sha256 hex
                Assert.True(svc.FrameworkMappings.ContainsKey("SQLT-CORE-00010"));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task UseSourceParser_true_with_missing_dir_sets_LoadError()
        {
            var svc = new CheckRepositoryService(NullLogger<CheckRepositoryService>.Instance,
                ConfigWith(@"C:\nope\does\not\exist"));
            await svc.LoadChecksAsync();
            Assert.NotNull(svc.LoadError);
            Assert.Contains("missing or not a directory", svc.LoadError!);
            Assert.Empty(svc.Checks);
        }

        [Fact]
        public async Task UseSourceParser_true_with_corrupt_check_skips_it_and_records_reason()
        {
            // 972cb27 contract change pinned here: a malformed check no longer
            // fails the whole load (LoadError) — it is SKIPPED with a reason in
            // SkippedChecks so the rest of the corpus still loads.
            var dir = FixtureDir(MinimalDoc.Replace("id: SQLT-CORE-00010", "id: BLITZ_001"));
            try
            {
                var svc = new CheckRepositoryService(NullLogger<CheckRepositoryService>.Instance, ConfigWith(dir));
                await svc.LoadChecksAsync();
                Assert.Null(svc.LoadError);
                Assert.Empty(svc.Checks);
                var skipped = Assert.Single(svc.SkippedChecks);
                Assert.Contains("v2 regex", skipped.Reason);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task UseSourceParser_false_uses_legacy_json_path()
        {
            var svc = new CheckRepositoryService(NullLogger<CheckRepositoryService>.Instance,
                ConfigWith(path: null, useParser: false));
            await svc.LoadChecksAsync();
            // Legacy path: succeeds or fails per existing semantics; we only
            // assert LoadSource != "source-parser" (i.e. flag respected).
            Assert.NotEqual("source-parser", svc.LoadSource);
        }
    }
}
