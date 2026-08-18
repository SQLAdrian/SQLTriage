/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Text.Json;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Parser
{
    /// <summary>
    /// B2 Slice 3 — loads the three optional mapping JSON files from the active
    /// <see cref="IBundleAccessor"/>. Full schema-specific joins by <c>check_id</c>
    /// land in Slice 4 when <c>SourceCatalogueLoader</c> wires the catalogue together.
    ///
    /// All three files are optional — absent keys (e.g. Free tier) leave the
    /// corresponding property null. The static <see cref="Empty"/> factory is kept for
    /// unit-test convenience.
    /// </summary>
    public sealed class MappingResolver : IDisposable
    {
        public JsonDocument? ControlMappings { get; private set; }
        public JsonDocument? RoadmapMapping { get; private set; }
        public JsonDocument? RoadmapAliases { get; private set; }

        /// <summary>
        /// Loads the three mapping files from <paramref name="bundle"/>.
        /// Missing keys are silently skipped (returns null for that property).
        /// </summary>
        public MappingResolver(IBundleAccessor bundle)
        {
            if (bundle is null) throw new ArgumentNullException(nameof(bundle));
            ControlMappings = TryParse(bundle.GetText("Config/control_mappings.json"));
            RoadmapMapping = TryParse(bundle.GetText("Config/roadmap-mapping.json"));
            RoadmapAliases = TryParse(bundle.GetText("Config/roadmap-aliases.json"));
        }

        /// <summary>Empty resolver — all three properties null. Useful for unit tests.</summary>
        public static MappingResolver Empty() => new(emptyMarker: true);

        private MappingResolver(bool emptyMarker) { /* no-op; all properties stay null */ }

        private static JsonDocument? TryParse(string? text)
        {
            if (text is null) return null;
            try
            {
                return JsonDocument.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            ControlMappings?.Dispose();
            RoadmapMapping?.Dispose();
            RoadmapAliases?.Dispose();
        }
    }
}
