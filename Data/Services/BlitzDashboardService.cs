/* In the name of God, the Merciful, the Compassionate */
/*
 * BlitzDashboardService — turns the FIRED sp_Blitz CheckIDs (from AuditOutputScanner) into a
 * CIO-style health view, enriched from the bundled corpus.
 *
 * THE MODEL (Adrian, 2026-06-30 — "we are just grading sp_Blitz results, not showing cleverness"):
 *
 *   sp_Blitz emits a CSV ROW only when a check FIRES (a finding). Passes are never observed, so the
 *   scoring UNIVERSE is SYNTHESIZED from the catalog of sp_Blitz checks we know of. A non-fired
 *   universe check = PASS; a fired one = a DING. EVERY fired check counts — we do not second-guess
 *   it with the corpus IsBad flag.
 *
 *   UNIVERSE  = every check in the unified catalog (corpus ∪ roadmap-mapping) that is a real
 *               gradeable check — i.e. its category is NOT a pure sp_Blitz banner/report
 *               (Server Info / Information / Rundate; see InfoCategories). Those banner rows fire on
 *               every healthy server and are listed-only, never scored. `Scored` carries this.
 *               `IsBad`/severity are DISPLAY-only (badge + ordering) — they no longer gate the score.
 *   JOIN      = a corpus SqlCheck is a Blitz check iff Id starts "SQLT-BLITZ-"; its BlitzCheckID
 *               = int.TryParse(check.Source) (Source = corpus `source.ref`). `composite:a,b,c`
 *               refs enrich every listed id. Non-numeric refs (internal-best-practice, CIS '2.2')
 *               are NOT CSV-joinable and are ignored for the dashboard.
 *   WEIGHT    = corpus-backed: max(ScoreWeight,1) × max(EffortHours,1)  (the GovernanceService
 *               weighted-ratio formula, reused). map-only (no numeric weight): 1 × max(effort,1)
 *               where effort is parsed from the map's effort_estimate string ("30 min" → 0.5).
 *   SCORE     = Σ(weight of NON-fired scored) / Σ(weight of all scored) × 100.  (Per instance.)
 *   DEPTH     = corpus-backed / universe — honest indicator of how much is richly analyzed vs
 *               basic (grows as the corpus backfills the BlitzCheckID delta).
 *   VOICE     = each finding renders a two-line CIO/DBA cell (BuildVoiceLines): a business "why"
 *               line (Business Impact → Intent fallback) and a "do this" line (Remediation, SQL
 *               stripped → eli5/Intent fallback). The full remediation (with SQL) is the tooltip.
 *
 * Unknown fired ids (in neither corpus nor map) DID fire, so they count as a ding too (weight 1)
 * and are flagged unclassified so the corpus team knows to map them.
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services;

/// <summary>Where a fired check's metadata came from.</summary>
public enum BlitzMetaSource { Corpus, Map, Unknown }

/// <summary>One entry in the synthesized universe of known sp_Blitz checks.</summary>
public sealed record BlitzCatalogEntry(
    int BlitzCheckId,
    string Name,
    string Category,
    string? Severity,        // corpus severity (Critical/High/Medium/Low/Info); null for map-only
    bool IsBad,              // DISPLAY-only: finding (badge) vs informational. Does NOT gate scoring.
    bool Scored,             // in the scored universe (a real check, not a banner/report row)
    double Weight,           // check_value contribution to the score
    string? BusinessImpact,  // CIO voice (## Business Impact)
    string? NextAction,      // raw remediation (## Remediation) — verbose, may carry SQL
    BlitzMetaSource Source,
    string? Description = null,        // DBA voice (## Intent) — fallback for the voice lines
    string? Eli5Remediation = null);  // plain-English remediation — fallback for the DBA line

/// <summary>A fired check as shown on the dashboard.</summary>
public sealed record BlitzFinding(
    int BlitzCheckId,
    string Name,
    string Category,
    string? Severity,
    bool IsBad,
    bool Unclassified,       // true when in neither corpus nor map
    int FireCount,           // rows/databases that fired it
    double Weight,
    string? BusinessImpact,
    string? NextAction,
    BlitzMetaSource Source,
    bool Scored = false,            // counted against the score (vs a listed-only banner row)
    string? CioLine = null,         // pre-built business "why" line (SQL stripped, truncated)
    string? DbaLine = null,         // pre-built "do this" line (SQL stripped, truncated)
    string? RemediationFull = null);// raw remediation (with SQL) for the cell tooltip

/// <summary>Per-category health rollup for one instance.</summary>
public sealed record BlitzCategoryRollup(
    string Category,
    int UniverseCount,
    int FiredCount,
    double Score);

/// <summary>Health report for a single SQL instance.</summary>
public sealed record BlitzInstanceReport(
    string Instance,
    string Domain,
    DateTime AuditDate,
    AuditFileType SourceType,
    double HealthScore,          // 0-100
    int ChecksFired,            // distinct scored checks that fired (dings) + unclassified fires
    int UniverseSize,           // distinct scored checks in the universe
    int InfoFired,              // banner/informational fires (listed, not scored)
    int UnclassifiedFired,      // name-only fires (counted as dings)
    double AnalysisDepth,       // 0-1: corpus-backed / universe
    IReadOnlyList<BlitzFinding> Findings,
    IReadOnlyList<BlitzCategoryRollup> Categories);

/// <summary>The whole dashboard: per-instance reports + estate rollup.</summary>
public sealed record BlitzDashboardData(
    IReadOnlyList<BlitzInstanceReport> Instances,
    double EstateHealthScore,
    int InstanceCount,
    bool CatalogAvailable);

public interface IBlitzDashboardService
{
    /// <summary>
    /// Build the dashboard from scanned files whose fired counts are already loaded. Files are
    /// deduped per instance (native sp_Blitz preferred over sp_triage) to avoid double-counting.
    /// </summary>
    BlitzDashboardData Build(IReadOnlyList<AuditedFile> filesWithFiredCounts);
}

public sealed class BlitzDashboardService : IBlitzDashboardService
{
    private const string BlitzIdPrefix = "SQLT-BLITZ-";

    // BlitzCheckIDs >= this are NOT real sp_Blitz checks — they are the sp_triage custom checks
    // (CPU saturation, weak passwords, power plan, …) that carry the sentinel 9999 in the
    // AllCheckTable because they have no upstream sp_Blitz id. An sp_Blitz grade must exclude them
    // from the universe AND ignore any that leak into the fired set. Real sp_Blitz ids are 1..~260.
    private const int SentinelIdFloor = 9000;

    private readonly CheckRepositoryService _repo;
    private readonly IBundleAccessor _bundle;
    private readonly ILogger<BlitzDashboardService> _logger;

    public BlitzDashboardService(
        CheckRepositoryService repo,
        IBundleAccessor bundle,
        ILogger<BlitzDashboardService> logger)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public BlitzDashboardData Build(IReadOnlyList<AuditedFile> filesWithFiredCounts)
    {
        var catalog = BuildCatalog(_repo.GetAllChecks(), ReadMapJson(), _logger);
        if (catalog.Count == 0)
            _logger.LogWarning("[BlitzDashboard] Empty catalog — corpus not loaded and roadmap-mapping.json unavailable.");

        // MED-6: one report per instance; prefer the native sp_Blitz file over sp_triage.
        var perInstance = filesWithFiredCounts
            .GroupBy(f => f.SqlInstance, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(f => f.FileType == AuditFileType.SpBlitz ? 0 : 1).First())
            .Select(f => ComputeInstanceReport(f, catalog))
            .OrderBy(r => r.Instance, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Estate score = simple average of instance scores (each instance equally weighted).
        double estate = perInstance.Count == 0 ? 100.0 : perInstance.Average(r => r.HealthScore);

        return new BlitzDashboardData(perInstance, Math.Round(estate, 1), perInstance.Count, catalog.Count > 0);
    }

    private string? ReadMapJson()
    {
        var fromBundle = _bundle.GetText("Config/roadmap-mapping.json");
        if (!string.IsNullOrEmpty(fromBundle)) return fromBundle;
        var path = Path.Combine(AppContext.BaseDirectory, "Config", "roadmap-mapping.json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // ── Pure functions (no IO) — unit-testable without a bundle/disk ──────────

    private static readonly HashSet<string> InfoCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Server Info", "Information", "Informational", "Rundate"
    };

    /// <summary>
    /// Build the unified BlitzCheckID → catalog map. Corpus is authoritative (real weights +
    /// narrative); roadmap-mapping fills ids the corpus doesn't cover yet.
    /// </summary>
    public static IReadOnlyDictionary<int, BlitzCatalogEntry> BuildCatalog(
        IEnumerable<SqlCheck> corpusChecks, string? mapJson, ILogger? logger = null)
    {
        var catalog = new Dictionary<int, BlitzCatalogEntry>();

        // 1) Corpus blitz checks (primary).
        foreach (var c in corpusChecks)
        {
            if (string.IsNullOrEmpty(c.Id) || !c.Id.StartsWith(BlitzIdPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var ids = ParseBlitzRefs(c.Source);   // handles bare int + composite:a,b,c; skips non-numeric
            if (ids.Count == 0) continue;

            double weight = Math.Max(c.ScoreWeight, 1) * Math.Max(c.EffortHours, 1.0);
            string category = string.IsNullOrWhiteSpace(c.Category) ? "Uncategorized" : c.Category;
            var infoBySeverity = string.Equals(c.Severity, "Info", StringComparison.OrdinalIgnoreCase);
            // DISPLAY flag (badge): info-severity or banner category reads as informational.
            bool isBad = c.IsBad && !infoBySeverity && !InfoCategories.Contains(category);
            // SCORED universe: every real check counts; only true banner/report categories are excluded.
            bool scored = !InfoCategories.Contains(category);

            foreach (var id in ids)
            {
                if (id >= SentinelIdFloor) continue;   // sp_triage custom sentinel, not sp_Blitz
                // First corpus writer wins; composite siblings don't overwrite a dedicated entry.
                if (catalog.ContainsKey(id)) continue;
                catalog[id] = new BlitzCatalogEntry(
                    id, c.Name, category,
                    c.Severity, isBad, scored, weight, c.BusinessImpact, c.RecommendedAction,
                    BlitzMetaSource.Corpus, c.Description, c.Eli5Remediation);
            }
        }

        // 2) roadmap-mapping fallback for ids the corpus doesn't cover.
        if (!string.IsNullOrEmpty(mapJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(mapJson);
                if (doc.RootElement.TryGetProperty("blitzCheckMap", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray())
                    {
                        int id = e.TryGetProperty("checkId", out var ci) && ci.TryGetInt32(out var civ) ? civ : -1;
                        if (id < 0 || id >= SentinelIdFloor || catalog.ContainsKey(id)) continue;

                        string category = e.TryGetProperty("category", out var cv) ? cv.GetString() ?? "" : "";
                        category = string.IsNullOrWhiteSpace(category) ? "Uncategorized" : category;
                        // map IsBad (DISPLAY): int 0/1, default true if absent — matches DiagnosticsRoadmap.
                        bool mapIsBad = !e.TryGetProperty("IsBad", out var ib) || (ib.TryGetInt32(out var ibv) && ibv != 0);
                        bool isBad = mapIsBad && !InfoCategories.Contains(category);
                        bool scored = !InfoCategories.Contains(category);

                        string name = e.TryGetProperty("findingName", out var fn) ? fn.GetString() ?? $"Check {id}" : $"Check {id}";
                        string? biz = e.TryGetProperty("business_translation", out var bt) ? bt.GetString() : null;
                        string? next = e.TryGetProperty("next_action", out var na) ? na.GetString() : null;
                        double effort = e.TryGetProperty("effort_estimate", out var ee)
                            ? ParseEffortHours(ee.GetString()) : 1.0;
                        double weight = 1.0 * Math.Max(effort, 1.0); // no numeric scoreWeight in the map

                        catalog[id] = new BlitzCatalogEntry(
                            id, name, category,
                            Severity: null, isBad, scored, weight, biz, next, BlitzMetaSource.Map);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[BlitzDashboard] roadmap-mapping.json parse failed; corpus-only catalog.");
            }
        }

        return catalog;
    }

    /// <summary>Compute one instance's health from its fired counts and the catalog.</summary>
    public static BlitzInstanceReport ComputeInstanceReport(
        AuditedFile file, IReadOnlyDictionary<int, BlitzCatalogEntry> catalog)
    {
        var fired = file.FiredCheckCounts;

        // Score over the universe of real (Scored) checks: PASS = not fired, DING = fired.
        // Every fired check counts — we do not second-guess it with the corpus IsBad flag.
        double denom = 0, num = 0;
        int universe = 0, checksFired = 0;
        var catByCategory = new Dictionary<string, (double denom, double num, int universe, int fired)>(StringComparer.OrdinalIgnoreCase);

        void Accrue(string category, double weight, bool isFired)
        {
            universe++;
            denom += weight;
            if (!isFired) num += weight; else checksFired++;
            var cat = catByCategory.TryGetValue(category, out var agg) ? agg : (0, 0, 0, 0);
            cat.denom += weight;
            if (!isFired) cat.num += weight;
            cat.universe += 1;
            if (isFired) cat.fired += 1;
            catByCategory[category] = cat;
        }

        foreach (var entry in catalog.Values)
        {
            if (!entry.Scored) continue;   // banner/report rows are listed-only, never scored
            Accrue(entry.Category, entry.Weight, fired.ContainsKey(entry.BlitzCheckId));
        }

        // Unknown fired ids DID fire — they count as a ding (weight 1) and join the universe.
        // Sentinel ids (sp_triage custom, >= 9000) are not sp_Blitz checks → ignore entirely.
        foreach (var id in fired.Keys)
            if (id < SentinelIdFloor && !catalog.ContainsKey(id))
                Accrue("Uncategorized", 1.0, isFired: true);

        double health = denom > 0 ? Math.Round(num / denom * 100.0, 1) : 100.0;

        // Findings = everything that fired (scored dings + banner/informational + unclassified).
        var findings = new List<BlitzFinding>();
        int infoFired = 0, unclassifiedFired = 0;
        foreach (var (id, count) in fired)
        {
            if (id >= SentinelIdFloor) continue;   // sp_triage custom sentinel, not an sp_Blitz finding
            if (catalog.TryGetValue(id, out var entry))
            {
                if (!entry.Scored) infoFired++;
                var (cio, dba) = BuildVoiceLines(entry);
                findings.Add(new BlitzFinding(
                    id, entry.Name, entry.Category, entry.Severity, entry.IsBad,
                    Unclassified: false, count, entry.Weight, entry.BusinessImpact, entry.NextAction,
                    entry.Source, entry.Scored, cio, dba, entry.NextAction));
            }
            else
            {
                unclassifiedFired++;
                findings.Add(new BlitzFinding(
                    id, $"sp_Blitz Check {id}", "Uncategorized", Severity: null, IsBad: false,
                    Unclassified: true, count, 1.0, null, null, BlitzMetaSource.Unknown, Scored: true));
            }
        }

        // Roll up display rows that are the SAME logical check — e.g. a `composite:` corpus check
        // whose sibling BlitzCheckIDs (placement/growth/VLF/…) each fired produces one row per
        // sibling, all sharing the Name + voice. Merge them into one row, summing the fire counts.
        // Scoring already treats each sibling as its own universe check — this is DISPLAY-only.
        findings = findings
            .GroupBy(f => (f.Name, f.Category, f.Source, f.Unclassified))
            .Select(g => g.Count() == 1
                ? g.First()
                : g.First() with { FireCount = g.Sum(x => x.FireCount), Weight = g.Max(x => x.Weight) })
            .OrderByDescending(f => f.Scored)
            .ThenByDescending(f => f.IsBad)
            .ThenByDescending(f => f.Weight)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var categories = catByCategory
            .Select(kv => new BlitzCategoryRollup(
                kv.Key, kv.Value.universe, kv.Value.fired,
                kv.Value.denom > 0 ? Math.Round(kv.Value.num / kv.Value.denom * 100.0, 1) : 100.0))
            .OrderBy(c => c.Score)
            .ToList();

        int corpusBacked = catalog.Values.Count(e => e.Scored && e.Source == BlitzMetaSource.Corpus);
        double depth = universe > 0 ? (double)corpusBacked / universe : 0;

        return new BlitzInstanceReport(
            file.SqlInstance, file.Domain, file.AuditDate, file.FileType,
            health, checksFired, universe, infoFired, unclassifiedFired,
            Math.Round(depth, 3), findings, categories);
    }

    // ── small parsers ────────────────────────────────────────────────────────

    /// <summary>
    /// Parse a corpus `source.ref` into BlitzCheckIDs. Bare int → [n]; `composite:1,2,3` → [1,2,3];
    /// non-numeric (internal-best-practice, CIS '2.2', free text) → empty (not CSV-joinable).
    /// </summary>
    public static IReadOnlyList<int> ParseBlitzRefs(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return Array.Empty<int>();
        var s = source.Trim();

        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single))
            return new[] { single };

        if (s.StartsWith("composite:", StringComparison.OrdinalIgnoreCase))
        {
            var ids = new List<int>();
            foreach (var part in s.Substring("composite:".Length).Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    ids.Add(n);
            return ids;
        }

        return Array.Empty<int>();
    }

    /// <summary>Parse a human effort string ("30 min", "2 hours", "1 day") to hours. Default 1.</summary>
    public static double ParseEffortHours(string? estimate)
    {
        if (string.IsNullOrWhiteSpace(estimate)) return 1.0;
        var s = estimate.Trim().ToLowerInvariant();

        // first number in the string
        var numStr = new string(s.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
        if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0)
            n = 1;

        if (s.Contains("min")) return n / 60.0;
        if (s.Contains("hour") || s.Contains("hr")) return n;
        if (s.Contains("day")) return n * 8.0;       // working day
        if (s.Contains("week")) return n * 40.0;
        return Math.Max(n, 1.0);                      // bare number → treat as hours
    }

    // ── voice cell (CIO "why" + DBA "do this") ────────────────────────────────

    private const int VoiceMaxChars = 180;

    /// <summary>
    /// Build the two-line cell for a finding: a business "why" line (CIO) and a "do this" line (DBA),
    /// both SQL-stripped and truncated. Falls back gracefully so map-only / informational checks —
    /// whose Remediation/Business-Impact are often empty — still show something useful.
    ///   CIO ← Business Impact → Intent(Description) → first eli5/remediation prose.
    ///   DBA ← Remediation(stripped) → eli5 remediation → Intent(Description).
    /// The DBA line is suppressed if it would duplicate the CIO line.
    /// </summary>
    public static (string? Cio, string? Dba) BuildVoiceLines(BlitzCatalogEntry e)
    {
        string? cio = StripMarkup(FirstNonEmpty(e.BusinessImpact, e.Description, e.Eli5Remediation, e.NextAction));
        string? dba = StripMarkup(FirstNonEmpty(e.NextAction, e.Eli5Remediation, e.Description));

        if (!string.IsNullOrEmpty(dba) && !string.IsNullOrEmpty(cio) &&
            string.Equals(dba, cio, StringComparison.OrdinalIgnoreCase))
            dba = null;

        return (cio, dba);
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c)) return c;
        return null;
    }

    /// <summary>
    /// Reduce a markdown remediation/impact blob to a single plain-text headline: drop fenced and
    /// inline code (the SQL), strip markdown emphasis + leading list markers, collapse whitespace,
    /// and truncate to <see cref="VoiceMaxChars"/> at a word boundary.
    /// </summary>
    public static string? StripMarkup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text;

        // Fenced code blocks ```...``` (includes ```sql) — and the corpus' malformed `\nsql\n...` fences.
        s = FencedCode.Replace(s, " ");
        // Inline code `...` (often a SQL snippet) — drop entirely.
        s = InlineCode.Replace(s, " ");
        // Markdown emphasis / heading / list markers.
        s = s.Replace("**", "").Replace("__", "");
        s = LeadingListMarker.Replace(s, " ");
        s = MarkdownNoise.Replace(s, " ");
        // Collapse all whitespace (newlines, tabs) to single spaces.
        s = Whitespace.Replace(s, " ").Trim();
        // Tidy artefacts left by stripped code: empty () and space-before-punct ("Hallengren  )").
        s = EmptyParens.Replace(s, " ");
        s = SpaceBeforePunct.Replace(s, "$1");
        s = OpenParenSpace.Replace(s, "(");
        s = Whitespace.Replace(s, " ").Trim();

        if (s.Length == 0) return null;
        if (s.Length <= VoiceMaxChars) return s;

        var cut = s.LastIndexOf(' ', Math.Min(VoiceMaxChars, s.Length - 1));
        if (cut < VoiceMaxChars / 2) cut = VoiceMaxChars;   // no early space → hard cut
        return s.Substring(0, cut).TrimEnd(',', ';', '.', ' ') + "…";
    }

    private static readonly System.Text.RegularExpressions.Regex FencedCode =
        new(@"```[\s\S]*?```", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex InlineCode =
        new("`[^`]*`", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex LeadingListMarker =
        new(@"(^|\n)\s*(\d+\.|[-*+])\s+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex MarkdownNoise =
        new(@"[#>]+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex Whitespace =
        new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex EmptyParens =
        new(@"\(\s*\)", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex SpaceBeforePunct =
        new(@"\s+([),.;:])", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex OpenParenSpace =
        new(@"\(\s+", System.Text.RegularExpressions.RegexOptions.Compiled);
}
