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
 * The reverse index (checkId -> template) is REBUILT WHENEVER THE STORE CHANGES.
 * It used to be built once in the constructor, under a comment claiming templates
 * were shipped and read-only so "there is no invalidation path to wire". That was
 * false: RemediationTemplateStore.LoadCorpusTemplates registers corpus-fed
 * templates and appends corpus check ids to built-ins AT RUNTIME, from
 * Pages/Remediation.razor. This class is a DI singleton read by
 * Pages/QuickCheck.razor, so whichever page a session opened first permanently
 * decided whether corpus-fed checks offered a one-click fix, for the life of the
 * process (honesty hunt r1-08). The index now keys on
 * RemediationTemplateStore.Generation and rebuilds when that moves.
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
    /// review-only maintenance generator). DI-singleton (mirrors the store's own lifetime),
    /// and the index rebuilds whenever the store's registered set changes.
    /// </summary>
    public class CheckResolutionLookup
    {
        private readonly RemediationTemplateStore _templates;
        private readonly object _sync = new();

        // checkId -> template. First-registered-template-wins on a hypothetical collision
        // (none exist today — each check id is claimed by exactly one shipped template).
        private Dictionary<string, RemediationTemplate> _byCheckId = new(StringComparer.Ordinal);

        // The store generation _byCheckId was built from. -1 is "never built" and can never
        // equal a real generation, so the first read always indexes.
        private int _indexedGeneration = -1;

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
            _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        }

        private static Dictionary<string, RemediationTemplate> BuildIndex(IEnumerable<RemediationTemplate> all)
        {
            var index = new Dictionary<string, RemediationTemplate>(StringComparer.Ordinal);
            foreach (var t in all)
            {
                foreach (var checkId in t.ResolvesCheckIds)
                {
                    if (string.IsNullOrWhiteSpace(checkId)) continue;
                    // First template registered for a given check id wins; ResolvesCheckIds is
                    // shipped/reviewed data, so a real collision would be a corpus authoring bug,
                    // not something to silently overwrite.
                    if (!index.ContainsKey(checkId))
                        index[checkId] = t;
                }
            }
            return index;
        }

        // Read the generation BEFORE snapshotting the templates. If the store changes in
        // between, the index is stamped with the older number and the next call rebuilds —
        // one wasted rebuild, never a stale answer. The reverse order could cache a torn view
        // under the current generation and keep serving it, which is the defect this fixes.
        private Dictionary<string, RemediationTemplate> CurrentIndex()
        {
            var generation = _templates.Generation;
            lock (_sync)
            {
                if (_indexedGeneration == generation) return _byCheckId;
            }

            var rebuilt = BuildIndex(_templates.All());
            lock (_sync)
            {
                if (generation >= _indexedGeneration)
                {
                    _byCheckId = rebuilt;
                    _indexedGeneration = generation;
                }
                return _byCheckId;
            }
        }

        /// <summary>
        /// Resolution for a corpus check id, or null if this check has no registered
        /// one-click template AND is not one of the mapped maintenance checks.
        /// </summary>
        public CheckResolution? Resolve(string? checkId)
        {
            if (string.IsNullOrWhiteSpace(checkId)) return null;

            if (CurrentIndex().TryGetValue(checkId, out var template))
                return new CheckResolution { Template = template };

            if (_maintenanceMap.TryGetValue(checkId, out var generator))
                return new CheckResolution { Generator = generator };

            return null;
        }
    }
}
