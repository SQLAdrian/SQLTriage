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
        /// <summary>
        /// The PRIMARY one-click RemediationTemplate for this check (deep-links to /remediation).
        /// When more than one template claims the check, this is the winner by explicit precedence
        /// (<see cref="ResolutionPrecedence"/>), never by enumeration order.
        /// </summary>
        public RemediationTemplate? Template { get; init; }

        /// <summary>
        /// The other templates that also resolve this check, in precedence order after the primary.
        /// Empty for the ordinary single-claimant case.
        ///
        /// <para>Phase-2 item 5. The relationship is REAL product data — "install the Maintenance
        /// Solution" and "run a backup now" both genuinely clear a backup-recency finding, one
        /// durably and one immediately — and it used to be destroyed at index time: BuildIndex kept
        /// the first template it met and dropped the rest, so the secondary was not merely
        /// deprioritised, it was unreachable. Keeping it here means a surface that wants to offer
        /// "or do this instead" has the data, and a surface that does not simply reads
        /// <see cref="Template"/> as before.</para>
        /// </summary>
        public IReadOnlyList<RemediationTemplate> Alternatives { get; init; }
            = Array.Empty<RemediationTemplate>();

        /// <summary>Set when a review-only maintenance generator resolves this check (deep-links to /maintenance-recommendations).</summary>
        public MaintenanceGenerator? Generator { get; init; }

        public bool IsOneClick => Template is not null;
        public bool IsMaintenance => Generator is not null;

        /// <summary>True when a second fix also clears this check and is available as an alternative.</summary>
        public bool HasAlternatives => Alternatives.Count > 0;
    }

    /// <summary>
    /// Which fix wins when two registered templates both resolve the same check id.
    ///
    /// <para><b>The ruling (Adrian, 2026-09-01): DURABLE WINS.</b> A fix that installs a standing
    /// mechanism — the Maintenance Solution, which keeps taking backups and running CHECKDB from
    /// then on — outranks a one-shot that clears today's finding and lets it come back tomorrow.
    /// The one-shot stays available as a secondary, because "I need a backup right now" is a real
    /// operator intent; it is just not the fix to lead with.</para>
    ///
    /// <para><b>Why this class exists at all.</b> Five check ids ship claimed by two templates each
    /// (found by the Phase-1 build, B1.6). Which one an operator was offered depended on the order
    /// <c>RemediationTemplateStore.All()</c> happened to enumerate a dictionary — a product decision
    /// nobody had made, taken by an implementation detail. The rank below is small and explicit on
    /// purpose: the alternative, ranking by some inferred property of a template, would be a second
    /// place for the ruling to live and a second place for it to drift.</para>
    /// </summary>
    public static class ResolutionPrecedence
    {
        /// <summary>Rank of a durable fix: it installs something that keeps the check passing.</summary>
        public const int Durable = 0;

        /// <summary>Rank of anything not named below. The ordinary single-claimant case never sorts.</summary>
        public const int Default = 10;

        /// <summary>Rank of a one-shot fix: it clears the finding once, and the finding can return.</summary>
        public const int OneShot = 20;

        private static readonly IReadOnlyDictionary<string, int> Ranks =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["INSTALLMAINTENANCESOLUTION"] = Durable,
                ["BACKUPDATABASENOW"] = OneShot,
                ["CHECKDBNOW"] = OneShot,
            };

        /// <summary>The precedence rank of a template key. Lower wins.</summary>
        public static int RankOf(string? templateKey) =>
            !string.IsNullOrWhiteSpace(templateKey) && Ranks.TryGetValue(templateKey!, out var r) ? r : Default;

        /// <summary>The precedence rank of a template. Lower wins.</summary>
        public static int RankOf(RemediationTemplate? template) => RankOf(template?.Key);
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

        // checkId -> every template claiming it, ordered by explicit precedence (primary first).
        // It used to be checkId -> ONE template, keeping whichever the enumeration met first.
        private Dictionary<string, List<RemediationTemplate>> _byCheckId = new(StringComparer.Ordinal);

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

        private static Dictionary<string, List<RemediationTemplate>> BuildIndex(IEnumerable<RemediationTemplate> all)
        {
            var index = new Dictionary<string, List<RemediationTemplate>>(StringComparer.Ordinal);
            foreach (var t in all)
            {
                foreach (var checkId in t.ResolvesCheckIds)
                {
                    if (string.IsNullOrWhiteSpace(checkId)) continue;
                    if (!index.TryGetValue(checkId, out var claimants))
                        index[checkId] = claimants = new List<RemediationTemplate>();
                    // Registering the same template twice for one id is authoring noise, not a
                    // second option; it must never show up as an "alternative" to itself.
                    if (!claimants.Exists(x => string.Equals(x.Key, t.Key, StringComparison.OrdinalIgnoreCase)))
                        claimants.Add(t);
                }
            }

            // ⚠ ORDER IS A PRODUCT DECISION HERE, NOT AN ENUMERATION ACCIDENT (Phase-2 item 5).
            // Sort by explicit rank, then by key, so the answer is identical whatever order the
            // store enumerated. The key tie-break matters: two templates of the SAME rank must not
            // reintroduce enumeration order through the back door.
            foreach (var claimants in index.Values)
                claimants.Sort((a, b) =>
                {
                    int byRank = ResolutionPrecedence.RankOf(a).CompareTo(ResolutionPrecedence.RankOf(b));
                    return byRank != 0 ? byRank : string.CompareOrdinal(a.Key, b.Key);
                });

            return index;
        }

        // Read the generation BEFORE snapshotting the templates. If the store changes in
        // between, the index is stamped with the older number and the next call rebuilds —
        // one wasted rebuild, never a stale answer. The reverse order could cache a torn view
        // under the current generation and keep serving it, which is the defect this fixes.
        private Dictionary<string, List<RemediationTemplate>> CurrentIndex()
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

            if (CurrentIndex().TryGetValue(checkId, out var claimants) && claimants.Count > 0)
                return new CheckResolution
                {
                    Template = claimants[0],
                    Alternatives = claimants.Count > 1
                        ? claimants.GetRange(1, claimants.Count - 1)
                        : Array.Empty<RemediationTemplate>(),
                };

            if (_maintenanceMap.TryGetValue(checkId, out var generator))
                return new CheckResolution { Generator = generator };

            return null;
        }
    }
}
