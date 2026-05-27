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
                File.WriteAllText(Path.Combine(dir, "a.yaml"), fixture);

                var loader = new SourceCatalogueLoader();
                var catalogue = loader.Load(new CorpusFileReader(dir));

                Assert.Equal(1, catalogue.Count);
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
                File.WriteAllText(Path.Combine(dir, "a.yaml"), fixture);

                var loader = new SourceCatalogueLoader();
                var h1 = loader.Load(new CorpusFileReader(dir)).IntegrityHash;
                var h2 = loader.Load(new CorpusFileReader(dir)).IntegrityHash;
                Assert.Equal(h1, h2); // byte-identity invariant — B3 ground truth
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Load_rejects_collision_across_files()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"b2-s4-{System.Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var fixture = File.ReadAllText(
                    Path.Combine(AppContext.BaseDirectory, "Parser", "Fixtures", "minimal-valid.yaml"));
                File.WriteAllText(Path.Combine(dir, "a.yaml"), fixture);
                File.WriteAllText(Path.Combine(dir, "b.yaml"), fixture); // same check_id

                var loader = new SourceCatalogueLoader();
                var ex = Assert.Throws<SourceParseException>(
                    () => loader.Load(new CorpusFileReader(dir)));
                Assert.Contains("collision", ex.Message);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ─────────── INTEGRATION: real corpus ───────────

        /// <summary>
        /// The first end-to-end run of the new parser against the actual
        /// corpus root (post-G1, post-STIG-DSSTIG-if-promoted). This is the
        /// load-bearing acceptance: B2's whole point is replacing
        /// regenerate_checks_json.py + sql-checks.json. If this passes,
        /// the parser is real.
        ///
        /// Skipped if the corpus dir doesn't exist (other machine / fresh
        /// clone). Run from the LiveMonitor repo root for it to find the
        /// corpus.
        /// </summary>
        [Fact]
        public void Integration_load_real_corpus_succeeds()
        {
            var corpus = FindCorpusRoot();
            if (corpus is null)
            {
                // not a failure — environment doesn't have the corpus checkout
                return;
            }

            var yamlCount = Directory.GetFiles(corpus, "*.yaml").Length;
            Assert.True(yamlCount > 100, $"corpus dir {corpus} has only {yamlCount} yamls; smells wrong");

            var loader = new SourceCatalogueLoader();
            SourceCatalogue catalogue;
            try
            {
                catalogue = loader.Load(new CorpusFileReader(corpus));
            }
            catch (SourceParseException ex)
            {
                // Fail loudly with the precise file + reason so the
                // load-bearing defect surfaces. This is information,
                // not catastrophe — the parser is doing exactly what
                // it's designed to do.
                Assert.Fail($"parser rejected real corpus: {ex.Message}");
                return;
            }

            // Real corpus invariants we expect to hold post-G1
            Assert.Equal(yamlCount, catalogue.Count);
            Assert.All(catalogue.Checks.Keys,
                id => Assert.Matches(ParseInvariants.CheckIdV2, id));
            Assert.Equal(64, catalogue.IntegrityHash.Length);

            // Sanity: no NS outside the closed 8-enum (regex enforces, but explicit assert is documentation)
            var nsAllow = new HashSet<string> { "BLITZ", "BPCHK", "GAPFIL", "CORE", "TRIAGE", "DSCIS", "SQLWCH", "DSSTIG" };
            foreach (var id in catalogue.Checks.Keys)
                Assert.Contains(id.Split('-')[1], nsAllow);
        }

        private static string? FindCorpusRoot()
        {
            // search up from the test bin dir for research_output/LLM1_deepseek
            var d = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && d != null; i++)
            {
                var probe = Path.Combine(d, "research_output", "LLM1_deepseek");
                if (Directory.Exists(probe) && Directory.GetFiles(probe, "*.yaml").Any())
                    return probe;
                d = Path.GetDirectoryName(d);
            }
            return null;
        }
    }
}
