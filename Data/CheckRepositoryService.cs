/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data
{
    /// <summary>
    /// Loads SQL checks. Two sources, tried in order:
    ///   1. IBundleAccessor (Phase 3+) when the bundle is unlocked — corpus comes
    ///      from the encrypted bundle, filtered by tier-permitted check IDs.
    ///   2. CorpusFileReader against CheckRepository:SourceParserPath when
    ///      UseSourceParser=true (dev mode).
    /// The legacy Config/sql-checks.json fallback was removed 2026-05-26 (D5):
    /// the license/entitlement bundle is now the sole runtime catalog source.
    /// </summary>
    public class CheckRepositoryService
    {
        private readonly ILogger<CheckRepositoryService> _logger;
        private readonly IBundleAccessor? _bundle;
        private List<SqlCheck> _checks = new();

        /// <summary>Set when the last load failed or yielded zero checks against a
        /// non-empty file — surfaced loudly so a broken corpus never masquerades
        /// as "0 checks / Outside Scope" while stale history is served.</summary>
        public string? LoadError { get; private set; }

        /// <summary>Provenance of the last successful load: "bundle" or "source-parser" (#27 v3).</summary>
        public string LoadSource { get; private set; } = "none";

        /// <summary>Checks skipped during the last load due to per-file validation
        /// failures (bad id, encoding, missing field). The rest of the catalogue
        /// still loads — one malformed check never blocks everything. Empty on a
        /// fully-clean load. Surfaced for diagnostics, not a hard error.</summary>
        public IReadOnlyList<SQLTriage.Data.Parser.SkippedCheck> SkippedChecks { get; private set; }
            = new List<SQLTriage.Data.Parser.SkippedCheck>();

        /// <summary>
        /// FrameworkMappings side-index — populated only when the source-parser
        /// path is used (#27 v3). Empty otherwise. Key = SqlCheck.Id.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<SQLTriage.Data.Parser.FrameworkMapping>> FrameworkMappings
        { get; private set; } = new Dictionary<string, IReadOnlyList<SQLTriage.Data.Parser.FrameworkMapping>>();

        /// <summary>SHA-256 over the source bytes — non-null when LoadSource=="source-parser". B3 invariant anchor.</summary>
        public string? SourceIntegrityHash { get; private set; }

        /// <summary>
        /// The currently loaded checks.
        /// </summary>
        public IReadOnlyList<SqlCheck> Checks => _checks.AsReadOnly();

        private readonly Microsoft.Extensions.Configuration.IConfiguration? _configuration;

        public CheckRepositoryService(
            ILogger<CheckRepositoryService> logger,
            Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
            IBundleAccessor? bundle = null)
        {
            _logger = logger;
            _configuration = configuration;
            _bundle = bundle;

            if (_bundle is not null)
            {
                _bundle.BundleStateChanged += async (_, _) =>
                {
                    try { await LoadChecksAsync(); }
                    catch (Exception ex) { _logger.LogError(ex, "Reload-on-bundle-change failed"); }
                };
            }
        }

        /// <summary>
        /// Load checks from the unlocked license bundle (production) or — when
        /// <c>CheckRepository:UseSourceParser=true</c> (dev) — from the corpus
        /// directory at <c>CheckRepository:SourceParserPath</c> via the #27 v3
        /// source parser. Failure surfaces via <see cref="LoadError"/> —
        /// fail-fast, doctrine #8.
        /// </summary>
        public async Task LoadChecksAsync()
        {
            // #27 v3 source-parser path (feature-flagged, default false)
            if (string.Equals(_configuration?["CheckRepository:UseSourceParser"], "true", StringComparison.OrdinalIgnoreCase))
            {
                var src = _configuration?["CheckRepository:SourceParserPath"];
                await Task.Run(() => LoadViaSourceParser(src));
                return;
            }

            // Phase 3+ bundle path: when an unlocked bundle exposes corpus YAMLs,
            // build a BundleReader from its contents (filtered by tier permissions).
            if (_bundle is { IsUnlocked: true })
            {
                await Task.Run(() => LoadViaBundle());
                return;
            }

            LoadError = "No check catalog available — license/bundle not loaded and no source parser configured.";
            _logger.LogError("{Err}", LoadError);
            _checks = new List<SqlCheck>();
        }

        /// <summary>
        /// Phase 3 bundle load path. Feeds the same SourceCatalogueLoader from
        /// the in-memory bundle (no disk). Tier filtering applied: yamls whose
        /// CheckNr is excluded by <see cref="IBundleAccessor.IsCheckPermitted"/>
        /// are dropped before the parser sees them.
        /// </summary>
        private void LoadViaBundle()
        {
            if (_bundle is null || !_bundle.IsUnlocked)
            {
                LoadError = "LoadViaBundle called with no unlocked bundle.";
                _logger.LogError("{Err}", LoadError);
                _checks = new List<SqlCheck>();
                return;
            }

            try
            {
                // Build the (handle → yaml + sql) dictionary expected by BundleReader.
                // Handle = yaml filename without .yaml extension. SQL sibling = same stem + .sql.
                var handles = _bundle.EnumerateCorpusYamlHandles()
                    .Where(h => h.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || h.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    .Select(h => new { 
                        Handle = h.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ? h.Substring(0, h.Length - 5) : h.Substring(0, h.Length - 3), 
                        YamlKey = h 
                    })
                    .ToList();

                var entries = new Dictionary<string, (string Yaml, string? Sql)>(StringComparer.Ordinal);
                var filteredCount = 0;
                foreach (var h in handles)
                {
                    var yaml = _bundle.ReadCorpusYaml(h.YamlKey);
                    if (yaml is null) continue;

                    // Tier filter — parse CheckNr from frontmatter and check permission.
                    var checkNr = ExtractCheckNrFromYaml(yaml);
                    if (checkNr.HasValue && !_bundle.IsCheckPermitted(checkNr.Value))
                    {
                        filteredCount++;
                        continue;
                    }

                    var sql = _bundle.ReadCorpusSqlFallback(h.Handle + ".sql");
                    entries[h.Handle] = (yaml, sql);
                }

                var loader = new SQLTriage.Data.Parser.SourceCatalogueLoader();
                var reader = new SQLTriage.Data.Parser.BundleReader(entries);
                var catalogue = loader.Load(reader);

                _checks = catalogue.Checks.Values.ToList();
                FrameworkMappings = catalogue.FrameworkMappings;
                SourceIntegrityHash = catalogue.IntegrityHash;
                LoadSource = "bundle";
                LoadError = null;
                SkippedChecks = catalogue.Skipped;

                _logger.LogInformation(
                    "Bundle load: {N} checks (tier={Tier}, filtered out {F}), sha={Hash}",
                    _checks.Count, _bundle.Tier, filteredCount,
                    SourceIntegrityHash is { Length: > 0 } ? SourceIntegrityHash[..12] : "(none)");

                if (catalogue.Skipped.Count > 0)
                {
                    _logger.LogWarning(
                        "Bundle load skipped {Count} malformed check(s) (the rest loaded fine): {Handles}",
                        catalogue.Skipped.Count,
                        string.Join("; ", catalogue.Skipped.Take(10).Select(s => $"{s.Handle} — {s.Reason}")));
                }
            }
            catch (SQLTriage.Data.Parser.SourceParseException ex)
            {
                LoadError = $"Bundle parse rejected corpus: {ex.Message}";
                _logger.LogError(ex, "{Err}", LoadError);
                _checks = new List<SqlCheck>();
            }
            catch (Exception ex)
            {
                LoadError = $"Bundle load failed unexpectedly: {ex.Message}";
                _logger.LogError(ex, "{Err}", LoadError);
                _checks = new List<SqlCheck>();
            }
        }

        private static int? ExtractCheckNrFromYaml(string yaml)
        {
            // Cheap scan of first ~50 lines for a `CheckNr: <int>` frontmatter line.
            // Mirrors BundleBuilder.ExtractCheckNrFromYaml in the corpus encryptor.
            using var sr = new StringReader(yaml);
            for (var i = 0; i < 50; i++)
            {
                var line = sr.ReadLine();
                if (line is null) break;
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*CheckNr\s*:\s*(\d+)\s*$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) return n;
            }
            return null;
        }

        /// <summary>
        /// #27 v3 source-parser load path. Replaces the JSON intermediate by
        /// parsing corpus YAMLs (+ optional .sql fallback bodies) directly via
        /// <see cref="SQLTriage.Data.Parser.SourceCatalogueLoader"/>. Sets
        /// <see cref="LoadSource"/>="source-parser" on success;
        /// <see cref="LoadError"/> loudly on failure (no silent stale-serve).
        /// </summary>
        private void LoadViaSourceParser(string? sourceDir)
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            {
                LoadError = $"Source parser path missing or not a directory: '{sourceDir}'. " +
                            $"Set CheckRepository:SourceParserPath to the corpus dist/ folder.";
                _logger.LogError("{Err}", LoadError);
                _checks = new List<SqlCheck>();
                return;
            }

            try
            {
                var loader = new SQLTriage.Data.Parser.SourceCatalogueLoader();
                var reader = new SQLTriage.Data.Parser.CorpusFileReader(sourceDir);
                var catalogue = loader.Load(reader);

                _checks = catalogue.Checks.Values.ToList();
                FrameworkMappings = catalogue.FrameworkMappings;
                SourceIntegrityHash = catalogue.IntegrityHash;
                LoadSource = "source-parser";
                LoadError = null;
                SkippedChecks = catalogue.Skipped;

                _logger.LogInformation(
                    "Source-parser load: {N} checks from '{Dir}', sha={Hash}, {Derivs} derivation(s)",
                    _checks.Count, sourceDir, SourceIntegrityHash[..12], catalogue.Derivations.Count);

                if (catalogue.Skipped.Count > 0)
                {
                    _logger.LogWarning(
                        "Source-parser skipped {Count} malformed check(s) (the rest loaded fine): {Handles}",
                        catalogue.Skipped.Count,
                        string.Join("; ", catalogue.Skipped.Take(10).Select(s => $"{s.Handle} — {s.Reason}")));
                }

                // B1 §6 banner threshold — log loudly if >50% needed derivation
                if (_checks.Count > 0 && catalogue.Derivations.Count > _checks.Count / 2)
                {
                    _logger.LogWarning(
                        "Source-parser: >50% of checks needed field derivation " +
                        "({D} derivations / {N} checks). Corpus is sparser than expected for required fields.",
                        catalogue.Derivations.Count, _checks.Count);
                }
            }
            catch (SQLTriage.Data.Parser.SourceParseException ex)
            {
                LoadError = $"Source-parser rejected corpus: {ex.Message}";
                _logger.LogError(ex, "{Err}", LoadError);
                _checks = new List<SqlCheck>();
            }
            catch (Exception ex)
            {
                LoadError = $"Source-parser failed unexpectedly: {ex.Message}";
                _logger.LogError(ex, "{Err}", LoadError);
                _checks = new List<SqlCheck>();
            }
        }

        /// <summary>
        /// Get all checks.
        /// </summary>
        public List<SqlCheck> GetAllChecks() => _checks;

        /// <summary>
        /// Get checks by category.
        /// </summary>
        public List<SqlCheck> GetChecksByCategory(string category)
            => _checks.Where(c => c.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>
        /// Get enabled checks only.
        /// </summary>
        public List<SqlCheck> GetEnabledChecks()
            => _checks.Where(c => c.Enabled).ToList();

        /// <summary>
        /// Get unique categories from all checks.
        /// </summary>
        public List<string> GetCategories()
            => _checks.Select(c => c.Category).Distinct().OrderBy(c => c).ToList();

        /// <summary>
        /// Find a check by ID.
        /// </summary>
        public SqlCheck? GetCheckById(string id)
            => _checks.FirstOrDefault(c => c.Id == id);

        /// <summary>
        /// Add a new check.
        /// </summary>
        public void AddCheck(SqlCheck check)
        {
            _checks.Add(check);
        }

        /// <summary>
        /// Remove a check by ID.
        /// </summary>
        public bool RemoveCheck(string id)
        {
            var check = GetCheckById(id);
            if (check != null)
            {
                _checks.Remove(check);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Update a check's enabled status.
        /// </summary>
        public bool SetCheckEnabled(string id, bool enabled)
        {
            var check = GetCheckById(id);
            if (check != null)
            {
                check.Enabled = enabled;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Update multiple checks' enabled status.
        /// </summary>
        public void SetChecksEnabled(IEnumerable<string> checkIds, bool enabled)
        {
            foreach (var id in checkIds)
            {
                SetCheckEnabled(id, enabled);
            }
        }

        /// <summary>
        /// Gets checks filtered by severity levels.
        /// </summary>
        public List<SqlCheck> GetChecksBySeverity(params string[] severities)
        {
            var severitySet = new HashSet<string>(severities, StringComparer.OrdinalIgnoreCase);
            return _checks.Where(c => c.Enabled && severitySet.Contains(c.Severity)).ToList();
        }
    }
}
