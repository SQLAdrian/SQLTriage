using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SQLTriage.Data.Models;

public class PlaybookCheck
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("Category")]
    public string Category { get; set; } = "General";

    [JsonPropertyName("Severity")]
    public string Severity { get; set; } = "Medium";

    [JsonPropertyName("RecommendedAction")]
    public string? RecommendedAction { get; set; }

    [JsonPropertyName("ExpectedResult")]
    public string? ExpectedResult { get; set; }

    [JsonPropertyName("FrameworkMappings")]
    public List<FrameworkMapping>? FrameworkMappings { get; set; }

    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    // ── AllCheckTable narrative fields (from research_output/!AllCheckTable!.csv via Round 6) ──

    /// <summary>When true, this finding is included in the top-level "actions to take today" narrative on reports.</summary>
    [JsonPropertyName("IncludeInMainActions")]
    public bool? IncludeInMainActions { get; set; }

    /// <summary>One-line plain-English summary of what's wrong when this check fires.</summary>
    [JsonPropertyName("CheckTriggeredSimplified")]
    public string? CheckTriggeredSimplified { get; set; }

    /// <summary>One-line plain-English summary of the healthy state when this check passes.</summary>
    [JsonPropertyName("CheckClearSimplified")]
    public string? CheckClearSimplified { get; set; }

    /// <summary>The single next action a DBA should take to address this finding.</summary>
    [JsonPropertyName("NextAction")]
    public string? NextAction { get; set; }

    /// <summary>The fuller business-context story behind this finding, used in narrative reports.</summary>
    [JsonPropertyName("FullStoryAction")]
    public string? FullStoryAction { get; set; }
}

public class FrameworkMapping
{
    [JsonPropertyName("framework")]
    public string Framework { get; set; } = string.Empty;

    [JsonPropertyName("control_id")]
    public string ControlId { get; set; } = string.Empty;

    [JsonPropertyName("control_name")]
    public string ControlName { get; set; } = string.Empty;

    [JsonPropertyName("mapping_type")]
    public string? MappingType { get; set; }
}
