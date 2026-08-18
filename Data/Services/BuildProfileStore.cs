/* In the name of God, the Merciful, the Compassionate */
/*
 * BuildProfileStore — reads/writes buildprofile.json at the REPO ROOT (checked in), the single
 * source of truth for the Community Edition build profile. buildprofile.targets reads the same
 * file at build time to exclude gated modules from a -p:SQLTriageProfile=community build.
 *
 * Replaces PublicProfileStore (per-feature ticks in %APPDATA%, fail-open to loss on rebuild)
 * with the checked-in module-state model decided 2026-06-11 (internal decisions log).
 *
 * Only meaningful in a dev working tree: the file is located by walking up from the exe
 * directory. In an installed/published app there is no repo root — IsAvailable is false and
 * the (dev-tools-only, build-absent in community) BuildProfile page shows an unavailable state.
 *
 * States: ship (always in community) · on/off (community toggle) · absent · never.
 * Only toggle modules are writable; absent/never are fail-closed in buildprofile.targets
 * regardless of this file.
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services;

public sealed class BuildProfileStore
{
    public enum ModuleKind { Ship, Toggle, Absent, Never }

    public sealed record ModuleInfo(string Id, string Title, ModuleKind Kind, string Description);

    /// <summary>The module catalog — mirrors the internal page inventory and buildprofile.targets.</summary>
    public static readonly IReadOnlyList<ModuleInfo> Modules = new[]
    {
        new ModuleInfo("core",            "Core",             ModuleKind.Ship,   "App shell: Index, Login, Onboarding, Settings, Servers, Guide, About, Health, MemoryProfile."),
        new ModuleInfo("assessment",      "Assessment",       ModuleKind.Ship,   "The product: QuickCheck/FullAudit, Checks, BP checks, VA runner, snapshot analysis, knowledge base, DbaTools, CapacityPlanning."),
        new ModuleInfo("governance",      "Governance",       ModuleKind.Ship,   "The differentiator: CIO dashboard, governance score, compliance mapping, playbooks, audit log, roadmap, benchmark."),
        new ModuleInfo("reporting-basic", "Reporting (basic)",ModuleKind.Ship,   "ReportBundles — basic PDF/HTML reports ending in the consulting CTA."),
        new ModuleInfo("operations",      "Operations",       ModuleKind.Toggle, "Ops hub, NOC/alerting, agent timeline, replication map, multi-server execution (No-Pants-gated at runtime), service control, deploy tooling."),
        new ModuleInfo("live-monitoring", "Live Monitoring",  ModuleKind.Toggle, "Live dashboards, sessions/waits/XEvents, query + resource monitoring, performance trends. Default off — other tools own this space."),
        new ModuleInfo("premium",         "Premium surfaces", ModuleKind.Absent, "Premium page, consolidation engine UI, advanced reporting. Engagement deliverables — compiled out of community."),
        new ModuleInfo("dev-tools",       "Dev Tools",        ModuleKind.Never,  "Corpus editors, check validator, build profile, remediation tuner, test plan, perf inspector. Never ship."),
    };

    private sealed class ProfileModel
    {
        public int Version { get; set; } = 1;
        [JsonPropertyName("_doc")] public string? Doc { get; set; }
        public Dictionary<string, string> Modules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime? UpdatedUtc { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<BuildProfileStore> _logger;
    private readonly object _lock = new();
    private readonly string? _path;
    private ProfileModel? _model;

    public BuildProfileStore(ILogger<BuildProfileStore> logger)
    {
        _logger = logger;
        _path = LocateProfileJson();
        if (_path is null)
        {
            _logger.LogInformation("[BuildProfile] buildprofile.json not found above {Base} — not a dev working tree; store unavailable.", AppContext.BaseDirectory);
            return;
        }
        _model = Load();
    }

    /// <summary>True when running inside the dev working tree (repo-root buildprofile.json found and parsed).</summary>
    public bool IsAvailable => _model is not null;

    public string? ProfilePath => _path;

    public DateTime? UpdatedUtc { get { lock (_lock) return _model?.UpdatedUtc; } }

    /// <summary>Raw state string for a module ("ship"/"on"/"off"/"absent"/"never"), or null when unavailable/unknown.</summary>
    public string? GetState(string moduleId)
    {
        lock (_lock)
            return _model is not null && _model.Modules.TryGetValue(moduleId, out var s) ? s : null;
    }

    /// <summary>Will this module be present in a community build? (Mirrors buildprofile.targets fail-closed rules.)</summary>
    public bool IsInCommunity(string moduleId)
    {
        var info = Modules.FirstOrDefault(m => string.Equals(m.Id, moduleId, StringComparison.OrdinalIgnoreCase));
        return info?.Kind switch
        {
            ModuleKind.Ship => true,
            ModuleKind.Toggle => string.Equals(GetState(moduleId), "on", StringComparison.OrdinalIgnoreCase),
            _ => false, // Absent/Never/unknown — fail closed
        };
    }

    /// <summary>Set a toggle module on/off and persist. Returns false for non-toggle modules or when unavailable.</summary>
    public bool SetToggle(string moduleId, bool on)
    {
        var info = Modules.FirstOrDefault(m => string.Equals(m.Id, moduleId, StringComparison.OrdinalIgnoreCase));
        if (info is null || info.Kind != ModuleKind.Toggle) return false;
        lock (_lock)
        {
            if (_model is null || _path is null) return false;
            var next = on ? "on" : "off";
            if (_model.Modules.TryGetValue(info.Id, out var cur) && cur == next) return true;
            _model.Modules[info.Id] = next;
            _model.UpdatedUtc = DateTime.UtcNow;
            return Save();
        }
    }

    private static string? LocateProfileJson()
    {
        // Dev runs from bin\Debug\net10.0-windows\win-x64\ — the repo root is a few levels up.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "buildprofile.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private ProfileModel? Load()
    {
        try
        {
            var m = JsonSerializer.Deserialize<ProfileModel>(File.ReadAllText(_path!), JsonOpts);
            if (m is not null)
            {
                m.Modules ??= new(StringComparer.OrdinalIgnoreCase);
                _logger.LogInformation("[BuildProfile] loaded {Count} module state(s) from {Path}.", m.Modules.Count, _path);
                return m;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BuildProfile] failed to parse {Path} — store unavailable.", _path);
        }
        return null;
    }

    private bool Save()
    {
        try
        {
            // UTF-8 no BOM (File.WriteAllText default) — the corpus/dev encoding rule.
            File.WriteAllText(_path!, JsonSerializer.Serialize(_model, JsonOpts) + Environment.NewLine);
            _logger.LogInformation("[BuildProfile] saved module states to {Path}.", _path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BuildProfile] failed to save {Path}.", _path);
            return false;
        }
    }
}
