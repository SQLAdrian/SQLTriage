/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services;

/// <summary>
/// Projects <see cref="BlitzFinding"/> records into <see cref="CheckResult"/> view objects
/// so they can be merged with live check results and rendered in the existing results grid
/// without modifying the <see cref="CheckResult"/> model.
/// </summary>
/// <remarks>
/// Priority mapping:
/// <list type="bullet">
///   <item>1 → Critical</item>
///   <item>2–50 → Warning</item>
///   <item>51+ → Info</item>
/// </list>
/// </remarks>
public static class BlitzFindingToCheckResultAdapter
{
    /// <summary>Source prefix used in <see cref="CheckResult.CheckId"/> to identify BLITZ rows.</summary>
    public const string BlitzCheckIdPrefix = "BLITZ-";

    /// <summary>Category shown in the grid for all BLITZ findings.</summary>
    public const string BlitzCategory = "sp_BLITZ";

    /// <summary>
    /// Converts a single <see cref="BlitzFinding"/> to a <see cref="CheckResult"/>
    /// that can be consumed by the existing results grid.
    /// </summary>
    public static CheckResult Adapt(BlitzFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        return new CheckResult
        {
            // CheckId includes the server label so the SAME finding on different
            // servers stays a distinct row. Without it, the merge's dedup-by-CheckId
            // collapses every server's copy into one (e.g. 458 raw findings across 3
            // servers showed as ~104). Re-importing the same server's same finding
            // still dedups correctly.
            CheckId = $"{BlitzCheckIdPrefix}{Sanitize(finding.ServerLabel)}-{finding.Priority:D3}-{Sanitize(finding.Finding)}",
            CheckName = finding.Finding,
            Category = BlitzCategory,
            Severity = MapSeverity(finding.Priority),
            Passed = false,             // BLITZ findings are always failing states
            ActualValue = finding.Priority,
            ExpectedValue = 0,
            Message = finding.Details,
            ExecutedAt = finding.ImportedUtc,
            ErrorMessage = null,
            InstanceName = finding.ServerLabel,
            EffortHours = 0,
            IsBad = true,
            ScoreWeight = 1,
            DurationMs = 0,
            RecommendedAction = finding.Url != null
                                    ? $"See: {finding.Url}"
                                    : null,
            Description = BuildDescription(finding)
        };
    }

    /// <summary>Converts a sequence of BLITZ findings to <see cref="CheckResult"/> objects.</summary>
    public static IEnumerable<CheckResult> AdaptAll(IEnumerable<BlitzFinding> findings)
        => findings.Select(Adapt);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string MapSeverity(int priority)
    {
        if (priority == 1) return "Critical";
        if (priority <= 50) return "Warning";
        return "Info";
    }

    private static string BuildDescription(BlitzFinding f)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"[Priority {f.Priority}] ");
        sb.Append(f.FindingsGroup);
        if (!string.IsNullOrWhiteSpace(f.DatabaseName))
            sb.Append($" | DB: {f.DatabaseName}");
        return sb.ToString();
    }

    /// <summary>
    /// Strips characters not suitable for use in a CheckId slug.
    /// Keeps alphanumerics, hyphens, and underscores; replaces spaces with hyphens.
    /// </summary>
    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return "unknown";
        var chars = value
            .Replace(' ', '-')
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
            .Take(40);
        return new string(chars.ToArray());
    }
}
