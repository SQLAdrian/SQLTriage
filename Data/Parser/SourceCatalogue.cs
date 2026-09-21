/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Parser
{
    /// <summary>
    /// The end-to-end output of <see cref="SourceCatalogueLoader"/>.
    /// Replaces <c>sql-checks.json</c> as the in-memory truth (per #27 v3).
    /// </summary>
    public sealed record SourceCatalogue(
        IReadOnlyDictionary<string, SqlCheck> Checks,
        IReadOnlyDictionary<string, IReadOnlyList<FrameworkMapping>> FrameworkMappings,
        IReadOnlyList<DerivationInfo> Derivations,
        string IntegrityHash,
        IReadOnlyList<SkippedCheck> Skipped)
    {
        public int Count => Checks.Count;
    }

    /// <summary>
    /// A single check file that failed validation and was skipped during load,
    /// rather than aborting the whole catalogue. Surfaced for diagnostics so one
    /// malformed check (bad id, encoding, missing field) never blocks the rest.
    /// </summary>
    public sealed record SkippedCheck(string Handle, string Reason);

    /// <summary>
    /// One framework-mapping entry as carried in YAML
    /// <c>framework_mappings: [{framework, control_id, …}]</c>. Held in a
    /// side-index on <see cref="SourceCatalogue"/> rather than on
    /// <see cref="SqlCheck"/> (model currently has no such property; the
    /// side-index is the bridge until the model evolves).
    /// </summary>
    public sealed record FrameworkMapping(
        string Framework,
        string ControlId,
        string? ControlName,
        string? MappingType,
        IReadOnlyDictionary<string, string> Extras);
}
