/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services;

// BM:ComplianceMappingService.Class — loads control_mappings.json and provides framework/control lookup
/// <summary>
/// Loads Config/control_mappings.json from the active bundle and exposes read-only lookup APIs.
/// The mapping file is keyed by region → frameworks → categories, where each category
/// carries a sqlCheckHints array (e.g. "access_control", "audit_logging").
/// At first access we build a reverse lookup: sqlCheckHint → list of (framework, controlId, controlName).
/// VA results carry a Category ("Security", "Configuration", etc.) which we map to sqlCheckHints
/// via the static CategoryToHints dictionary.
/// When the bundle is locked (Free/Full not yet loaded) the service returns empty results.
/// </summary>
public sealed class ComplianceMappingService
{
    private readonly ILogger<ComplianceMappingService> _logger;
    private readonly IBundleAccessor _bundle;
    private readonly HashSet<string> _hiddenFrameworks;

    // Lazy-loaded state — invalidated whenever the bundle state changes.
    private readonly object _lock = new();
    private List<FrameworkDefinition>? _frameworks;
    private Dictionary<string, List<ControlRef>>? _hintToControls;
    private bool _loaded;

    // ── VA Category → sqlCheckHint vocabulary ──────────────────────────────
    // Bridges AssessmentResult.Category (produced by SqlAssessmentService) to the
    // sqlCheckHints vocabulary used in control_mappings.json.
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> CategoryToHints =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Security"] = new[] { "access_control", "identity_authentication", "monitoring_detection" },
            ["Configuration"] = new[] { "configuration_hardening", "change_management" },
            ["Performance"] = new[] { "configuration_hardening" },
            ["Availability"] = new[] { "backup_recovery" },
            // Corpus emits "Backup" (33 checks) for backup/restore checks — without
            // this key they mapped to nothing and every BCDR/backup control scored
            // off only the 4 "Availability" checks. (Adrian-signed 2026-05-18.)
            ["Backup"] = new[] { "backup_recovery" },
            ["BestPractices"] = new[] { "configuration_hardening" },
            ["Information"] = new[] { "configuration_hardening" },
            ["Encryption"] = new[] { "cryptography_encryption", "key_management" },
            ["Auditing"] = new[] { "audit_logging", "monitoring_detection" },
            // Corpus emits "Monitoring" (23 checks) — bridge to detection + audit
            // logging so audit/monitoring controls score off real checks.
            ["Monitoring"] = new[] { "monitoring_detection", "audit_logging" },
            ["Patching"] = new[] { "patch_vulnerability_management" },
            ["Network"] = new[] { "network_security" },
            ["General"] = new[] { "configuration_hardening" },
        };

    public ComplianceMappingService(
        ILogger<ComplianceMappingService> logger,
        IBundleAccessor bundle,
        IConfiguration? configuration = null)
    {
        _logger = logger;
        _bundle = bundle;

        var hidden = configuration?.GetSection("Compliance:HiddenFrameworks").Get<string[]>();
        _hiddenFrameworks = hidden is { Length: > 0 }
            ? new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(new[] { "ISO 27001" }, StringComparer.OrdinalIgnoreCase);

        // Invalidate cached parse whenever a new bundle is loaded.
        _bundle.BundleStateChanged += (_, _) => { lock (_lock) { _loaded = false; _frameworks = null; _hintToControls = null; } };
    }

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns distinct framework acronyms loaded from control_mappings.json,
    /// excluding any framework listed in Compliance:HiddenFrameworks (default: ISO 27001).
    /// Pass <paramref name="includeHidden"/> = true to bypass suppression (e.g. for admin/tests).
    /// </summary>
    public IReadOnlyList<string> GetFrameworks(bool includeHidden = false)
    {
        var (frameworks, _) = GetData();
        return frameworks
            .Select(f => f.Acronym)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(a => includeHidden || !_hiddenFrameworks.Contains(a))
            .OrderBy(a => a)
            .ToList();
    }

    /// <summary>Returns all framework definitions (includes region metadata).</summary>
    public IReadOnlyList<FrameworkDefinition> GetFrameworkDefinitions()
    {
        var (frameworks, _) = GetData();
        return frameworks.AsReadOnly();
    }

    /// <summary>Returns distinct control IDs within a framework (matched by acronym).</summary>
    public IReadOnlyList<string> GetControlsForFramework(string framework)
    {
        var fw = FindFramework(framework);
        return fw?.Categories.Select(c => c.Id).ToList() ?? new List<string>();
    }

    /// <summary>
    /// Returns the VA category strings (e.g. "Security", "Configuration") whose sqlCheckHints
    /// overlap with the given framework control's sqlCheckHints.
    /// Used by VA page to show "Compliance:" hints per finding.
    /// </summary>
    public IReadOnlyList<string> GetVaCategoriesForControl(string framework, string controlId)
    {
        var fw = FindFramework(framework);
        var cat = fw?.Categories.FirstOrDefault(c => string.Equals(c.Id, controlId, StringComparison.OrdinalIgnoreCase));
        if (cat == null) return Array.Empty<string>();

        var result = new List<string>();
        foreach (var kv in CategoryToHints)
        {
            if (kv.Value.Any(h => cat.SqlCheckHints.Contains(h, StringComparer.OrdinalIgnoreCase)))
                result.Add(kv.Key);
        }
        return result;
    }

    /// <summary>
    /// Returns all (framework, controlId, controlName) tuples that apply to a given VA category.
    /// Used by VA page to show the "Compliance:" line per finding row.
    /// </summary>
    public IReadOnlyList<ControlRef> GetControlsForVaCategory(string vaCategory)
    {
        if (!CategoryToHints.TryGetValue(vaCategory, out var hints))
            return Array.Empty<ControlRef>();

        var (_, hintToControls) = GetData();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ControlRef>();
        foreach (var hint in hints)
        {
            if (!hintToControls.TryGetValue(hint, out var refs)) continue;
            foreach (var r in refs)
            {
                var key = $"{r.Framework}|{r.ControlId}";
                if (seen.Add(key)) result.Add(r);
            }
        }
        return result;
    }

    /// <summary>
    /// Returns all ControlRefs for a given framework, keyed by the sqlCheckHints they cover.
    /// Used by ComplianceScoreService to compute per-control-family scoring.
    /// </summary>
    public IReadOnlyList<FrameworkCategory> GetCategoriesForFramework(string framework)
    {
        var fw = FindFramework(framework);
        return fw?.Categories ?? new List<FrameworkCategory>();
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private FrameworkDefinition? FindFramework(string framework)
    {
        var (frameworks, _) = GetData();
        return frameworks.FirstOrDefault(f =>
            string.Equals(f.Acronym, framework, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.Name, framework, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the lazily-parsed framework list + reverse hint map, loading from the bundle
    /// on first call (or after a bundle-state change). Thread-safe via <see cref="_lock"/>.
    /// Returns empty collections when no text is available in the current bundle.
    /// </summary>
    private (List<FrameworkDefinition> Frameworks, Dictionary<string, List<ControlRef>> HintToControls) GetData()
    {
        lock (_lock)
        {
            if (_loaded)
                return (_frameworks!, _hintToControls!);

            _loaded = true;
            var text = _bundle.GetText("Config/control_mappings.json");
            if (text is null)
            {
                _logger.LogWarning(
                    "control_mappings.json not in current bundle (tier={Tier}); ComplianceMappingService returning empty.",
                    _bundle.Tier);
                _frameworks = new List<FrameworkDefinition>();
                _hintToControls = new Dictionary<string, List<ControlRef>>(StringComparer.OrdinalIgnoreCase);
                return (_frameworks, _hintToControls);
            }

            var frameworks = new List<FrameworkDefinition>();
            var hintToControls = new Dictionary<string, List<ControlRef>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;

                // Schema wraps regions under a "regions" object: { "$schema": …, "regions": { "US": { "frameworks": […] } } }.
                // Older/flat layout had region objects directly at the root. Support both.
                var regionsRoot = root.TryGetProperty("regions", out var regionsEl)
                                  && regionsEl.ValueKind == JsonValueKind.Object
                    ? regionsEl
                    : root;

                foreach (var regionProp in regionsRoot.EnumerateObject())
                {
                    var regionName = regionProp.Name;
                    if (regionName == "$schema") continue;
                    if (!regionProp.Value.TryGetProperty("frameworks", out var fwArray)) continue;

                    foreach (var fwEl in fwArray.EnumerateArray())
                    {
                        var name = fwEl.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var acronym = fwEl.TryGetProperty("acronym", out var a) ? a.GetString() ?? "" : name;
                        var url = fwEl.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                        var urlPending = fwEl.TryGetProperty("urlPending", out var up) && up.GetBoolean();

                        var categories = new List<FrameworkCategory>();
                        if (fwEl.TryGetProperty("categories", out var catArray))
                        {
                            foreach (var catEl in catArray.EnumerateArray())
                            {
                                var cid = catEl.TryGetProperty("id", out var ci) ? ci.GetString() ?? "" : "";
                                var cname = catEl.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : "";
                                var hints = new List<string>();
                                if (catEl.TryGetProperty("sqlCheckHints", out var hintsEl))
                                    foreach (var h in hintsEl.EnumerateArray())
                                        if (h.GetString() is { } hs) hints.Add(hs);

                                categories.Add(new FrameworkCategory { Id = cid, Name = cname, SqlCheckHints = hints });
                            }
                        }

                        var fwDef = new FrameworkDefinition
                        {
                            Region = regionName,
                            Name = name,
                            Acronym = acronym,
                            Url = url,
                            UrlPending = urlPending,
                            Categories = categories,
                        };
                        frameworks.Add(fwDef);

                        foreach (var cat in categories)
                        {
                            foreach (var hint in cat.SqlCheckHints)
                            {
                                if (!hintToControls.TryGetValue(hint, out var list))
                                {
                                    list = new List<ControlRef>();
                                    hintToControls[hint] = list;
                                }
                                list.Add(new ControlRef { Framework = acronym, ControlId = cat.Id, ControlName = cat.Name });
                            }
                        }
                    }
                }

                _logger.LogInformation(
                    "ComplianceMappingService loaded {FrameworkCount} frameworks across {RegionCount} regions from bundle (tier={Tier})",
                    frameworks.Count,
                    frameworks.Select(f => f.Region).Distinct().Count(),
                    _bundle.Tier);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse control_mappings.json from bundle.");
                frameworks = new List<FrameworkDefinition>();
                hintToControls = new Dictionary<string, List<ControlRef>>(StringComparer.OrdinalIgnoreCase);
            }

            _frameworks = frameworks;
            _hintToControls = hintToControls;
            return (_frameworks, _hintToControls);
        }
    }

    // ── Data models ─────────────────────────────────────────────────────────

    public sealed class FrameworkDefinition
    {
        public string Region { get; init; } = "";
        public string Name { get; init; } = "";
        public string Acronym { get; init; } = "";
        public string Url { get; init; } = "";
        public bool UrlPending { get; init; }
        public List<FrameworkCategory> Categories { get; init; } = new();
    }

    public sealed class FrameworkCategory
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public List<string> SqlCheckHints { get; init; } = new();
    }

    public sealed class ControlRef
    {
        public string Framework { get; init; } = "";
        public string ControlId { get; init; } = "";
        public string ControlName { get; init; } = "";
    }
}
