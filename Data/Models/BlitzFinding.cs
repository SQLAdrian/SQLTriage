/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Models;

/// <summary>
/// A single finding row imported from an sp_BLITZ CSV output.
/// </summary>
public sealed record BlitzFinding
{
    /// <summary>Urgency level (1 = most urgent, 255 = least urgent).</summary>
    public required int Priority { get; init; }

    /// <summary>High-level category string from sp_BLITZ (e.g. "Reliability").</summary>
    public required string FindingsGroup { get; init; }

    /// <summary>Short finding title (e.g. "Database in 80% Compat Mode").</summary>
    public required string Finding { get; init; }

    /// <summary>Database context for the finding. Null when server-scoped.</summary>
    public string? DatabaseName { get; init; }

    /// <summary>Verbose prose description of the finding.</summary>
    public required string Details { get; init; }

    /// <summary>Reference URL from sp_BLITZ documentation. Null when not present in CSV.</summary>
    public string? Url { get; init; }

    /// <summary>
    /// Server label assigned at import time. Used to distinguish rows from different
    /// servers when multiple CSVs are merged into a single assessment view.
    /// </summary>
    public required string ServerLabel { get; init; }

    /// <summary>UTC timestamp when this finding was imported into the local cache.</summary>
    public required DateTime ImportedUtc { get; init; }

    /// <summary>Groups all rows from one CSV upload together. Allows single-import removal.</summary>
    public required Guid ImportId { get; init; }
}
