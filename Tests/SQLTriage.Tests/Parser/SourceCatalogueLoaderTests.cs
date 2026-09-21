/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLTriage.Data.Parser;
using Xunit;

namespace SQLTriage.Tests.Parser
{
    public class SourceCatalogueLoaderTests
    {
        // ─────────── unit: fixture-driven ───────────

        [Fact]
        public void Load_minimal_yields_one_check_and_byteIdentity_hash()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"b2-s4-{System.Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var fixture = File.ReadAllText(
                    Path.Combine(AppContext.BaseDirectory, "Parser", "Fixtures", "minimal-valid.yaml"));
                File.WriteAllText(Path.Combine(dir, "a.md"), fixture);

                var loader = new SourceCatalogueLoader();
                var catalogue = loader.Load(new CorpusFileReader(dir));

                Assert.Equal(1, catalogue.Count);
                Assert.Empty(catalogue.Skipped);
                Assert.True(catalogue.Checks.ContainsKey("SQLT-CORE-00010"));
                Assert.Equal(64, catalogue.IntegrityHash.Length); // sha256 hex
                Assert.Equal(4, catalogue.Derivations.Count);     // all 4 fields derived for minimal fixture
                Assert.True(catalogue.FrameworkMappings.ContainsKey("SQLT-CORE-00010"));
                Assert.Single(catalogue.FrameworkMappings["SQLT-CORE-00010"]);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Load_is_idempotent_byteIdentity_hash_stable_on_reread()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"b2-s4-{System.Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var fixture = File.ReadAllText(
                    Path.Combine(AppContext.BaseDirectory, "Parser", "Fixtures", "minimal-valid.yaml"));
                File.WriteAllText(Path.Combine(dir, "a.md"), fixture);

                var loader = new SourceCatalogueLoader();
                var h1 = loader.Load(new CorpusFileReader(dir)).IntegrityHash;
                var h2 = loader.Load(new CorpusFileReader(dir)).IntegrityHash;
                Assert.Equal(h1, h2); // byte-identity invariant — B3 ground truth
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Load_skips_collision_across_files_and_records_reason()
        {
            // 972cb27 contract change pinned here: per-check failures (including
            // id collisions) no longer abort the whole load — the duplicate is
            // SKIPPED with a reason and the first occurrence wins.
            var dir = Path.Combine(Path.GetTempPath(), $"b2-s4-{System.Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var fixture = File.ReadAllText(
                    Path.Combine(AppContext.BaseDirectory, "Parser", "Fixtures", "minimal-valid.yaml"));
                File.WriteAllText(Path.Combine(dir, "a.md"), fixture);
                File.WriteAllText(Path.Combine(dir, "b.md"), fixture); // same id

                var loader = new SourceCatalogueLoader();
                var catalogue = loader.Load(new CorpusFileReader(dir));

                Assert.Equal(1, catalogue.Count);
                var skipped = Assert.Single(catalogue.Skipped);
                Assert.Equal("b", skipped.Handle);
                Assert.Contains("collision", skipped.Reason);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ─────────── INTEGRATION: real corpus-v2 ───────────

        /// <summary>
        /// End-to-end against the live corpus (sibling corpus working tree,
        /// corpus-v2/checks — Markdown+frontmatter). This is the drift canary:
        /// if corpus authoring and the app parser disagree, it surfaces here
        /// with per-file reasons. Skipped when the corpus checkout isn't on
        /// this machine (client/CI).
        /// </summary>
        [Fact]
        public void Integration_load_real_corpusV2_succeeds()
        {
            var corpus = SqlCheckBuilderTests.FindCorpusV2Checks();
            if (corpus is null) return; // corpus repo not on this machine — skip

            var mdCount = Directory.GetFiles(corpus, "*.md").Length;
            Assert.True(mdCount > 100, $"corpus dir {corpus} has only {mdCount} md files; smells wrong");

            var loader = new SourceCatalogueLoader();
            var catalogue = loader.Load(new CorpusFileReader(corpus));

            // Per-check resilience means parse failures land in Skipped, not as
            // a throw. Surface them loudly with the precise file + reason.
            Assert.True(catalogue.Skipped.Count == 0,
                $"parser skipped {catalogue.Skipped.Count} corpus check(s): " +
                string.Join("; ", catalogue.Skipped.Take(10).Select(s => $"{s.Handle} — {s.Reason}")));

            Assert.Equal(mdCount, catalogue.Count);
            Assert.All(catalogue.Checks.Keys,
                id => Assert.Matches(ParseInvariants.CheckIdV2, id));
            Assert.Equal(64, catalogue.IntegrityHash.Length);
        }
    }
}
