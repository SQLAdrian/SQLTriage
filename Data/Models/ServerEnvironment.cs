/* In the name of God, the Merciful, the Compassionate */
namespace SQLTriage.Data.Models;

/// <summary>
/// Canonical vocabulary for <see cref="ServerConnection.Environment"/>.
/// Keep in sync with the dropdown options in Pages/Servers.razor.
/// </summary>
public static class ServerEnvironment
{
    public const string Production = "Production";
    public const string Staging = "Staging";
    public const string Development = "Development";
    public const string Test = "Test";
    public const string QA = "QA";
    public const string DR = "DR";
    /// <summary>Sentinel for blank / unset — treat as non-production.</summary>
    public const string None = "";

    /// <summary>
    /// Returns true when the environment string indicates a production server.
    /// Blank/unset is considered non-production (defensive default).
    /// </summary>
    public static bool IsProduction(string? environment) =>
        string.Equals(environment, Production, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when ANY server in the list is non-production or unset.
    /// Use this to decide whether a watermark should be applied.
    /// </summary>
    public static bool RequiresWatermark(IEnumerable<string?> environments) =>
        environments.Any(e => !IsProduction(e));
}
