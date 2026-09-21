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
    /// Returns true when a report over these servers must carry the non-production watermark.
    ///
    /// <para>THE RULE: watermark unless EVERY server in scope is a KNOWN production server. Any
    /// server that is non-production, blank, or absent from the list forces the mark.</para>
    ///
    /// <para>WHY THE EMPTY CASE IS TRUE AND NOT FALSE (fixed 2026-08-26). The predicate used to be
    /// <c>environments.Any(e =&gt; !IsProduction(e))</c>, which is false for an empty sequence, so a
    /// report over servers that matched NO configured connection dropped the watermark. That is the
    /// documented rule inverted at exactly the point it matters: an UNKNOWN server read as
    /// production and shipped unmarked, while a KNOWN server with a blank Environment watermarked.
    /// The empty list is the normal shape when an operator drops an audit CSV into the output
    /// folder by hand, which the app's own empty-state text invites, so this was the common path
    /// rather than an edge case. A watermark is a claim about what a report is; absence of evidence
    /// that a server is production is not evidence that it is.</para>
    ///
    /// <para>The cost of the two mistakes is not symmetric. A spurious watermark on a production
    /// report is a cosmetic annoyance the operator can fix by filling in the Environment field. A
    /// missing watermark on a non-production report is a document that reads as an assessment of
    /// live infrastructure when it is not.</para>
    /// </summary>
    public static bool RequiresWatermark(IEnumerable<string?>? environments)
    {
        if (environments is null) return true;

        bool sawAny = false;
        foreach (var environment in environments)
        {
            sawAny = true;
            if (!IsProduction(environment)) return true;
        }
        return !sawAny;   // nothing in scope was proved production
    }
}
