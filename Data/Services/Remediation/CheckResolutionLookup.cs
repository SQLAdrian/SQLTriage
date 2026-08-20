/* In the name of God, the Merciful, the Compassionate */
/*
 * CheckResolutionLookup — S4 connective tissue: given a failed check's corpus id
 * (SqlCheck.Id / CheckResult.CheckId, e.g. "SQLT-BLITZ-NO-OPERATORS"), find the
 * resolution a user should be pointed at.
 *
 * Two distinct resolution shapes exist today, and this lookup deliberately keeps
 * them separate rather than collapsing them into one "fix" concept:
 *   1. A ONE-CLICK RemediationTemplate (RemediationTemplateStore.ResolvesCheckIds)
 *      — a gated, reversible state flip the user can preview/approve/apply on
 *      /remediation (AGENTALERTPACK, INSTALLMAINTENANCESOLUTION today).
 *   2. A REVIEW-ONLY maintenance script generator on /maintenance-recommendations
 *      (MaintenanceScriptService) — CHECKDB / index fragmentation / stale
 *      statistics. These are corpus-ruled "maintenance work, not a state flip":
 *      never one-click, always "generate a script to review and run manually".
 *
 * The reverse index (checkId -> template) is built ONCE at construction from
 * RemediationTemplateStore.All() and is immutable for the lifetime of the store
 * snapshot — templates are shipped/read-only (see RemediationTemplateStore), so
 * there is no invalidation path to wire.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Services;

namespace SQLTriage.Data.Services.Remediation
{
    /// <summary>Which generator on /maintenance-recommendations resolves a MAINTENANCE check.</summary>
    public enum MaintenanceGenerator
    {
        /// <summary>MaintenanceScriptService.GenerateIndexMaintenanceScriptAsync.</summary>
        IndexFragmentation,
        /// <summary>MaintenanceScriptService.GenerateStatisticsUpdateScriptAsync.</summary>
        Statistics,
        /// <summary>MaintenanceScriptService.GenerateCheckDbScriptAsync.</summary>
        CheckDb,
    }

    /// <summary>The resolution path for one failed check, or null if none is registered.</summary>
    public sealed class CheckResolution
    {
        /// <summary>Set when a one-click RemediationTemplate resolves this check (deep-links to /remediation).</summary>
        public RemediationTemplate? Template { get; init; }

        /// <summary>Set when a review-only maintenance generator resolves this check (deep-links to /maintenance-recommendations).</summary>
        public MaintenanceGenerator? Generator { get; init; }

        public bool IsOneClick => Template is not null;
        public bool IsMaintenance => Generator is not null;
    }

    /// <summary>
    /// Reverse-index lookup: corpus check id -> its resolution (one-click template or
    /// review-only maintenance generator). Built once from RemediationTemplateStore at
    /// construction; DI-singleton (mirrors the store's own lifetime).
    /// </summary>
    public class CheckResolutionLookup
    {
        // checkId -> template. First-registered-template-wins on a hypothetical collision
        // (none exist today — each check id is claimed by exactly one shipped template).
        private readonly Dictionary<string, RemediationTemplate> _byCheckId = new(StringComparer.Ordinal);

        // The maintenance-check-id -> generator map. NOT guessed: each id was confirmed by
        // grepping corpus-v2/checks/*.md for the exact `id:` frontmatter (see S4 build report).
        // These three checks are corpus-ruled "maintenance work, not a state flip" — they
        // deliberately carry no RemediationTemplate/ResolvesCheckIds entry.
        private static readonly IReadOnlyDictionary<string, MaintenanceGenerator> _maintenanceMap =
            new Dictionary<string, MaintenanceGenerator>(StringComparer.Ordinal)
            {
                ["SQLT-CUSTOM-INDEX-FRAGMENTATION"] = MaintenanceGenerator.IndexFragmentation,
                ["SQLT-CUSTOM-STATISTICS-HEALTH"] = MaintenanceGenerator.Statistics,
                ["SQLT-BPCHK-DBCC-CHECKDB-STATUS"] = MaintenanceGenerator.CheckDb,
            };

        public CheckResolutionLookup(RemediationTemplateStore templates)
        {
            foreach (var t in templates.All())
            {
                foreach (var checkId in t.ResolvesCheckIds)
                {
                    if (string.IsNullOrWhiteSpace(checkId)) continue;
                    // First template registered for a given check id wins; ResolvesCheckIds is
                    // shipped/reviewed data, so a real collision would be a corpus authoring bug,
                    // not something to silently overwrite.
                    if (!_byCheckId.ContainsKey(checkId))
                        _byCheckId[checkId] = t;
                }
            }
        }

        /// <summary>
        /// Resolution for a corpus check id, or null if this check has no registered
        /// one-click template AND is not one of the mapped maintenance checks.
        /// </summary>
        public CheckResolution? Resolve(string? checkId)
        {
            if (string.IsNullOrWhiteSpace(checkId)) return null;

            if (_byCheckId.TryGetValue(checkId, out var template))
                return new CheckResolution { Template = template };

            if (_maintenanceMap.TryGetValue(checkId, out var generator))
                return new CheckResolution { Generator = generator };

            return null;
        }
    }
}
