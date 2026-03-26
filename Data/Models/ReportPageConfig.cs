/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json.Serialization;

namespace SqlHealthAssessment.Data.Models
{
    // ── Root ─────────────────────────────────────────────────────────────────

    public class ReportPagesRoot
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("pages")]
        public List<ReportPageDefinition> Pages { get; set; } = new();
    }

    // ── Page ─────────────────────────────────────────────────────────────────

    public class ReportPageDefinition
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        /// <summary>Route that this config applies to, e.g. "/quickcheck"</summary>
        [JsonPropertyName("route")]
        public string Route { get; set; } = "";

        /// <summary>"audit" for hardcoded pages, "dashboard" for config-driven pages (phase 2)</summary>
        [JsonPropertyName("pageType")]
        public string PageType { get; set; } = "audit";

        [JsonPropertyName("sections")]
        public List<ReportSection> Sections { get; set; } = new();
    }

    // ── Section ───────────────────────────────────────────────────────────────

    public class ReportSection
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        /// <summary>
        /// "native" — content is a hardcoded Blazor slot identified by NativeKey.
        /// "panel"  — content is a full DynamicPanel driven by PanelConfig.
        /// </summary>
        [JsonPropertyName("sectionType")]
        public string SectionType { get; set; } = "native";

        /// <summary>
        /// For sectionType "native": matches the named slot in the host page
        /// (e.g. "summary-cards", "results-grid").
        /// </summary>
        [JsonPropertyName("nativeKey")]
        public string NativeKey { get; set; } = "";

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("order")]
        public int Order { get; set; } = 0;

        /// <summary>
        /// "before" — renders above the page's main native content block.
        /// "after"  — renders below (default). Panel sections placed "after" have
        ///            access to the page's result data via the field reference.
        /// </summary>
        [JsonPropertyName("position")]
        public string Position { get; set; } = "after";

        /// <summary>Only populated for sectionType "panel".</summary>
        [JsonPropertyName("panelConfig")]
        public PanelDefinition? PanelConfig { get; set; }
    }

    // ── Page field schema (used in editor field-reference panel) ──────────────

    /// <summary>Describes one available column/field on a report page.</summary>
    public class ReportFieldInfo
    {
        public string Name        { get; init; } = "";
        public string Type        { get; init; } = "";
        public string Description { get; init; } = "";
        public bool   IsKey       { get; init; }
    }
}
