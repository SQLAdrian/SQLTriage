/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.IO;
using SQLTriage.Data.Parser;
using Xunit;

namespace SQLTriage.Tests.Parser
{
    /// <summary>
    /// B2 Slice 6 — proves the B3 dev-vs-bundle byte-identity invariant.
    /// Same source bytes (disk vs in-memory) → byte-identical catalogue
    /// (same integrity hash + same check Ids). This is the ground truth
    /// that makes Phase B's BundleReader a safe drop-in for
    /// CorpusFileReader. Fixtures are V2 Markdown+frontmatter (2026-06-12).
    /// </summary>
    public class ByteIdentityInvariantTests
    {
        private const string Y1 = @"---
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
        private const string Y2 = @"---
id: SQLT-CORE-00020
title: u
category: Configuration
severity: Low
source:
  framework: sp_Blitz
  ref: 20
applicability:
  engine_editions: [SqlServer]
  scope: instance
result_contract: verdict
framework_mappings:
  - framework: NIST SP 800-53 Rev 5
    control_id: 'CM-6'
    mapping_type: direct_match
provenance: custom
---

## Intent
e

## Query
```sql
SELECT 0
```
";

        [Fact]
        public void Corpus_and_bundle_produce_identical_catalogue()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"b2-s6-{System.Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // disk source — both fixtures written verbatim
                File.WriteAllText(Path.Combine(dir, "a.md"), Y1);
                File.WriteAllText(Path.Combine(dir, "b.md"), Y2);

                // in-memory source — exact same bytes via BundleReader
                var bundle = new Dictionary<string, (string Yaml, string? Sql)>
                {
                    ["a"] = (Y1, null),
                    ["b"] = (Y2, null),
                };

                var loader = new SourceCatalogueLoader();
                var fromDisk = loader.Load(new CorpusFileReader(dir));
                var fromBundle = loader.Load(new BundleReader(bundle));

                // both sources must load cleanly — a skip means fixture drift
                Assert.Empty(fromDisk.Skipped);
                Assert.Empty(fromBundle.Skipped);

                // B3 invariant: identical integrity hashes
                Assert.Equal(fromDisk.IntegrityHash, fromBundle.IntegrityHash);

                // identical id sets + identical ordering
                Assert.Equal(fromDisk.Checks.Count, fromBundle.Checks.Count);
                Assert.Equal(
                    string.Join(",", fromDisk.Checks.Keys),
                    string.Join(",", fromBundle.Checks.Keys));

                // identical derivation tally + identical framework mappings
                Assert.Equal(fromDisk.Derivations.Count, fromBundle.Derivations.Count);
                Assert.Equal(fromDisk.FrameworkMappings.Count, fromBundle.FrameworkMappings.Count);

                // spot-check a populated SqlCheck — same SQL, same derived values
                var d = fromDisk.Checks["SQLT-CORE-00010"];
                var b = fromBundle.Checks["SQLT-CORE-00010"];
                Assert.Equal(d.SqlQuery, b.SqlQuery);
                Assert.Equal(d.ScoreWeight, b.ScoreWeight);
                Assert.Equal(d.ExecutionType, b.ExecutionType);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Different_bytes_produce_different_hash()
        {
            var loader = new SourceCatalogueLoader();
            var h1 = loader.Load(new BundleReader(new Dictionary<string, (string, string?)>
            {
                ["a"] = (Y1, null),
            })).IntegrityHash;

            // single-byte change: trailing whitespace mod
            var h2 = loader.Load(new BundleReader(new Dictionary<string, (string, string?)>
            {
                ["a"] = (Y1 + "\n", null),
            })).IntegrityHash;

            Assert.NotEqual(h1, h2); // hash is sensitive to any byte change
        }
    }
}
