/* In the name of God, the Merciful, the Compassionate */
/*
 * BuildCatalogueService — looks up "patches behind" + support status for any
 * detected SQL Server build number, by reading Config/sql-build-catalogue.json
 * from the active bundle.
 * (Refreshed at publish time by scripts/update-build-catalogue.py and packed
 *  into the bundle by CorpusEncryptor.)
 *
 * Voice rule (NEGOTIATION_PRINCIPLES.md): UI copy should label findings
 * ("It looks like..."), never promise. The service returns facts; the UI frames them.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services
{
    public class BuildCatalogueService
    {
        private readonly ILogger<BuildCatalogueService> _logger;
        private readonly IBundleAccessor? _bundle;
        private readonly DateTime _referenceDate;

        // Lazy-loaded state — invalidated whenever the bundle state changes.
        private readonly object _lock = new();
        private BuildCatalogueData? _catalogue;
        private bool _loaded;

        public BuildCatalogueService(ILogger<BuildCatalogueService> logger, IBundleAccessor bundle)
        {
            _logger = logger;
            _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
            _referenceDate = DateTime.UtcNow.Date;

            // Invalidate cached parse whenever a new bundle is loaded.
            _bundle.BundleStateChanged += (_, _) => { lock (_lock) { _loaded = false; _catalogue = null; } };
        }

        // Test seam: construct from an in-memory JSON payload without touching the bundle.
        // Mirrors GetData()'s behaviour — malformed JSON is logged and IsAvailable stays false.
        public static BuildCatalogueService FromJson(string catalogueJson, ILogger<BuildCatalogueService> logger)
            => FromJson(catalogueJson, logger, DateTime.UtcNow.Date);

        // Deterministic overload — pass a fixed referenceDate so lifecycle tests do not depend on the wall clock.
        public static BuildCatalogueService FromJson(string catalogueJson, ILogger<BuildCatalogueService> logger, DateTime referenceDate)
        {
            var svc = new BuildCatalogueService(logger, referenceDate: referenceDate);
            try
            {
                svc.LoadFromJson(catalogueJson);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to parse build catalogue from in-memory JSON");
            }
            return svc;
        }

        // Private constructor used by the FromJson test seam (no bundle needed).
        private BuildCatalogueService(ILogger<BuildCatalogueService> logger, DateTime referenceDate = default)
        {
            _logger = logger;
            _referenceDate = referenceDate == default ? DateTime.UtcNow.Date : referenceDate;
        }

        public bool IsAvailable => GetData() != null;
        public string? LastUpdated => GetData()?.LastUpdated;

        /// <summary>
        /// Returns the lazily-parsed catalogue from the bundle.
        /// Thread-safe; invalidated by BundleStateChanged.
        /// Returns null when catalogue is absent from the current bundle.
        /// </summary>
        private BuildCatalogueData? GetData()
        {
            // If this instance was constructed via FromJson (test seam), _bundle is null
            // and _catalogue is set directly — return it without trying the bundle path.
            if (_bundle is null)
                return _catalogue;

            lock (_lock)
            {
                if (_loaded)
                    return _catalogue;

                _loaded = true;
                var text = _bundle.GetText("Config/sql-build-catalogue.json");
                if (text is null)
                {
                    _logger.LogWarning(
                        "sql-build-catalogue.json not in current bundle (tier={Tier}). Patches-behind unavailable.",
                        _bundle.Tier);
                    _catalogue = null;
                    return null;
                }

                try
                {
                    LoadFromJson(text);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse sql-build-catalogue.json from bundle.");
                    _catalogue = null;
                }

                return _catalogue;
            }
        }

        private void LoadFromJson(string json)
        {
            _catalogue = JsonSerializer.Deserialize<BuildCatalogueData>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            _logger.LogInformation("Build catalogue loaded — last updated {Updated}, {VersionCount} versions",
                _catalogue?.LastUpdated, _catalogue?.Versions?.Count ?? 0);
        }

        /// <summary>
        /// Given a detected build (e.g. "16.0.4115.5"), report patches-behind + support status.
        /// Returns null if the catalogue isn't loaded or the build can't be matched.
        /// </summary>
        public BuildStatus? Lookup(string detectedBuild)
        {
            var catalogue = GetData();
            if (catalogue?.Versions == null || string.IsNullOrWhiteSpace(detectedBuild)) return null;

            var version = ResolveMajorVersion(detectedBuild);
            if (version == null) return null;

            if (!catalogue.Versions.TryGetValue(version, out var versionData) || versionData?.Builds == null)
                return null;

            // Match the installed build either exactly or by closest predecessor (release date)
            var orderedBuilds = versionData.Builds
                .Where(b => !string.IsNullOrEmpty(b.Build))
                .OrderBy(b => b.ReleaseDate ?? DateTime.MinValue)
                .ToList();

            var installed = orderedBuilds.FirstOrDefault(b => CompareBuildStrings(b.Build, detectedBuild) == 0)
                          ?? orderedBuilds.LastOrDefault(b => CompareBuildStrings(b.Build, detectedBuild) <= 0);

            if (installed == null) return null;

            // Builds released AFTER the installed one
            var newerBuilds = orderedBuilds
                .Where(b => b.ReleaseDate > installed.ReleaseDate)
                .ToList();

            var patchesBehind = newerBuilds.Count;
            var daysBehind = 0;
            if (versionData.LatestReleaseDate.HasValue && installed.ReleaseDate.HasValue)
                daysBehind = (versionData.LatestReleaseDate.Value - installed.ReleaseDate.Value).Days;

            var lifecycleStatus = ComputeLifecycleStatus(versionData, _referenceDate);

            return new BuildStatus
            {
                MajorVersion = version,
                InstalledBuild = installed.Build,
                InstalledLabel = installed.Label,
                InstalledReleaseDate = installed.ReleaseDate,
                LatestBuild = versionData.LatestBuild,
                LatestReleaseDate = versionData.LatestReleaseDate,
                PatchesBehind = patchesBehind,
                DaysBehind = daysBehind,
                LifecycleStatus = lifecycleStatus,
                MainstreamSupportEnds = versionData.MainstreamSupportEnds,
                ExtendedSupportEnds = versionData.ExtendedSupportEnds,
                MissedPatchesSample = newerBuilds.Take(10)
                                                  .Select(b => new MissedPatch
                                                  {
                                                      Build = b.Build,
                                                      Label = b.Label,
                                                      ReleaseDate = b.ReleaseDate,
                                                      Kb = b.Kb,
                                                  })
                                                  .ToList(),
            };
        }

        private static string? ResolveMajorVersion(string detectedBuild)
        {
            // e.g. "16.0.4115.5" => "2022"
            var parts = detectedBuild.Split('.');
            if (parts.Length < 1) return null;
            if (!int.TryParse(parts[0], out var major)) return null;
            return major switch
            {
                16 => "2022",
                15 => "2019",
                14 => "2017",
                13 => "2016",
                12 => "2014",
                11 => "2012",
                10 when parts.Length > 1 && parts[1] == "50" => "2008R2",
                10 => "2008",
                _ => null,
            };
        }

        /// <summary>Returns negative if a &lt; b, zero if equal, positive if a &gt; b.</summary>
        private static int CompareBuildStrings(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return string.CompareOrdinal(a, b);
            var pa = a.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            var pb = b.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            int len = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < len; i++)
            {
                int av = i < pa.Length ? pa[i] : 0;
                int bv = i < pb.Length ? pb[i] : 0;
                if (av != bv) return av - bv;
            }
            return 0;
        }

        private static LifecycleStatusKind ComputeLifecycleStatus(BuildCatalogueVersionData v, DateTime referenceDate)
        {
            if (v.ExtendedSupportEnds is { } ext && ext < referenceDate) return LifecycleStatusKind.OutOfSupport;
            if (v.MainstreamSupportEnds is { } main && main < referenceDate) return LifecycleStatusKind.ExtendedSupportOnly;
            if (v.MainstreamSupportEnds is { } main2 && (main2 - referenceDate).TotalDays < 365) return LifecycleStatusKind.MainstreamEndingSoon;
            return LifecycleStatusKind.Mainstream;
        }
    }

    public enum LifecycleStatusKind
    {
        Mainstream,
        MainstreamEndingSoon,
        ExtendedSupportOnly,
        OutOfSupport,
    }

    public class BuildStatus
    {
        public string MajorVersion { get; set; } = "";
        public string? InstalledBuild { get; set; }
        public string? InstalledLabel { get; set; }
        public DateTime? InstalledReleaseDate { get; set; }
        public string? LatestBuild { get; set; }
        public DateTime? LatestReleaseDate { get; set; }
        public int PatchesBehind { get; set; }
        public int DaysBehind { get; set; }
        public LifecycleStatusKind LifecycleStatus { get; set; }
        public DateTime? MainstreamSupportEnds { get; set; }
        public DateTime? ExtendedSupportEnds { get; set; }
        public List<MissedPatch> MissedPatchesSample { get; set; } = new();

        public string LifecycleLabel => LifecycleStatus switch
        {
            LifecycleStatusKind.Mainstream => "Mainstream support",
            LifecycleStatusKind.MainstreamEndingSoon => "Mainstream ending soon",
            LifecycleStatusKind.ExtendedSupportOnly => "Extended support only",
            LifecycleStatusKind.OutOfSupport => "Out of support",
            _ => "Unknown",
        };

        // Approximate "years old" of the installed version, based on its major-version release
        public int? AgeYears
        {
            get
            {
                if (!InstalledReleaseDate.HasValue) return null;
                var years = (DateTime.UtcNow.Date - InstalledReleaseDate.Value.Date).Days / 365;
                return years;
            }
        }
    }

    public class MissedPatch
    {
        public string? Build { get; set; }
        public string? Label { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public string? Kb { get; set; }
    }

    // ── JSON model (matches Config/sql-build-catalogue.json) ──

    internal class BuildCatalogueData
    {
        public string? LastUpdated { get; set; }
        public Dictionary<string, BuildCatalogueVersionData>? Versions { get; set; }
    }

    internal class BuildCatalogueVersionData
    {
        public int MajorBuild { get; set; }
        public DateTime? MainstreamSupportEnds { get; set; }
        public DateTime? ExtendedSupportEnds { get; set; }
        public string? LatestBuild { get; set; }
        public DateTime? LatestReleaseDate { get; set; }
        public List<BuildEntryData>? Builds { get; set; }
    }

    internal class BuildEntryData
    {
        public string Build { get; set; } = "";
        public string? Label { get; set; }
        public string? Kb { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public string? Type { get; set; }
    }
}
