/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json.Serialization;

namespace SQLTriage.Data.Models;

/// <summary>
/// Full server documentation snapshot, captured via SMO and persisted as JSON
/// under output/serverdocs/. Rendered by /server-docs and compared across
/// snapshots to surface drift.
/// </summary>
public class ServerDocSnapshot
{
    public string Server { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; }
    public int CaptureDurationMs { get; set; }

    /// <summary>Non-fatal SMO failures (e.g. a section the login can't read).</summary>
    public List<string> CaptureWarnings { get; set; } = new();

    public List<DocSection> Sections { get; set; } = new();
}

/// <summary>Categorised group of facts about the server. Rendered as one expandable card.</summary>
public class DocSection
{
    /// <summary>Stable identifier — used for diff keys and DOM ids (e.g. "instance", "security").</summary>
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>FontAwesome class — e.g. "fa-solid fa-server".</summary>
    public string Icon { get; set; } = "fa-solid fa-circle-info";

    /// <summary>Short one-line summary shown next to the title when collapsed.</summary>
    public string SummaryLine { get; set; } = "";

    public DocSeverity Severity { get; set; } = DocSeverity.Captured;

    /// <summary>Reasons the section is marked Attention (only populated when Severity == Attention).</summary>
    public List<string> AttentionReasons { get; set; } = new();

    /// <summary>Ordered key/value facts, e.g. (Edition, "Enterprise (64-bit)").</summary>
    public List<DocRow> Rows { get; set; } = new();

    /// <summary>Tabular sub-sections, e.g. logins, jobs, databases.</summary>
    public List<DocCollection> Collections { get; set; } = new();
}

public class DocRow
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>Optional hint shown under the value in muted text.</summary>
    public string? Hint { get; set; }
}

public class DocCollection
{
    public string Title { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    /// <summary>Cells parallel to Columns. First column should be a stable identity for diff matching.</summary>
    public List<List<string>> Rows { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocSeverity
{
    Captured,
    Changed,
    Attention,
    Pending,
}
