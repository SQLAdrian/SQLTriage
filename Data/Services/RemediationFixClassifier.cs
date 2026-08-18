/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationFixClassifier — maps an assessment finding to a coarse remediation
 * fix class (RemediationType) so the read-only decision-support power band can be
 * shown next to it ("remediating this could cut ~X–Y% of CPU work").
 *
 * HONESTY (the whole point): this returns a class ONLY when the finding's fix is
 * genuinely a CPU/I-O-reducing change (parallelism, indexing, I/O, a CPU-affecting
 * config toggle). Everything else — security, backup, encryption, HA, auth,
 * monitoring, informational — returns null and gets NO power badge. Showing a
 * "power saved" band on a finding whose fix saves no compute would be a claim the
 * data does not support. Under-claim by design: an unmatched performance check
 * simply shows no badge rather than a wrong one.
 *
 * The band itself (the %) is the illustrative per-fix-class band in
 * PowerEstimateService.ReductionBand — NOT a per-server promise. This classifier
 * only decides WHICH class, never the number.
 *
 * Matching is conservative keyword substring matching over (CheckId + DisplayName),
 * upper-cased, first rule wins. Rule order resolves overlaps (e.g. "unused indexes
 * exacerbating I/O" is an index fix, not an I/O fix; "affinity mask / affinity I/O
 * mask" is a CPU config, not an I/O fix).
 */

using System;

namespace SQLTriage.Data.Services
{
    public static class RemediationFixClassifier
    {
        // Ordered rules — first containing-keyword match wins. Order matters for
        // overlapping titles (see header). Keywords are upper-case substrings.
        private static readonly (RemediationType Type, string[] Keywords)[] Rules =
        {
            // 1. Parallelism / plan-shape CPU fixes (MAXDOP, CTFP, param sniffing).
            (RemediationType.MaxdopParamSniff, new[]
            {
                "MAXDOP", "MAX DEGREE OF PARALLELISM", "COST THRESHOLD",
                "PARALLELISM", "CXPACKET", "PARAMETER SNIFF", "PARAMETER-SNIFF",
            }),

            // 2. Plan-quality / cardinality fixes — stale or missing statistics, and
            //    implicit conversions that defeat an index seek. Keywords are narrow,
            //    full-word phrases VERIFIED against the corpus to MISS the informational
            //    stats checks (User-Created Statistics In Place, Auto-Update-Stats-Async,
            //    Stats-Updated-Asynchronously, the TF2389/2390/4139 toggles).
            (RemediationType.PlanQuality, new[]
            {
                "STATISTICS-HEALTH", "STATISTICS STALE", "STATISTICS NEED TO BE UPDATED",
                "AUTO-CREATE STATISTICS", "AUTO-UPDATE STATISTICS",
                "STATISTICS WITHOUT HISTOGRAM",
                "MISSING-STATISTICS", "MISSING STATISTICS",
                "IMPLICIT CONVERSION", "IMPLICIT-CONVERSION",
            }),

            // 3. Indexing fixes that cut CPU + reads. Specific sub-signals
            //    only; bare "INDEX" is too broad (catches memory config, full-text
            //    maintenance, disabled-index reliability, etc.). NOTE: "HYPOTHETICAL
            //    INDEX"/"FAKE INDEX" are deliberately EXCLUDED — those are catalog
            //    cleanup of optimizer-ignored objects and reduce no compute.
            (RemediationType.IndexAddRebuild, new[]
            {
                "MISSING INDEX", "MISSING-INDEX",
                "UNUSED INDEX", "UNUSED-INDEX",
                "DUPLICATE INDEX", "DUPLICATE-INDEX",
                "INDEX FRAGMENT", "INDEX-FRAGMENT",
                "OVER-INDEX", "OVER INDEX", "EXCESSIVE INDEX",
                "COVERING INDEX", "COVERING-INDEX",
                "HEAP", "FORWARDED-RECORD", "FORWARDED RECORD",
                // Heap (no clustered index) only — NOT bare "CLUSTERED INDEX", which the MS
                // ruleset trips via non-unique / nonclustered / wide-key / GUID clustered-index
                // findings (design issues, not add/rebuild-for-CPU fixes).
                "WITHOUT CLUSTERED INDEX",
                "PARTITION-MISALIGNED",
                "MORE-INDEXES-THAN-COLS", "MORE INDEXES THAN COLUMNS",
                "ZERO-INDEXES", "ZERO INDEXES", "TABLES WITHOUT INDEXES",
                // MS Assessment ruleset display-name aliases for genuine index fixes:
                "REDUNDANT INDEX", "RARELY USED INDEX", "FOREIGN KEY CONSTRAINTS SHOULD HAVE",
            }),

            // 4. CPU-affecting config toggles — small effect. ONLY toggles that reduce
            //    actual compute belong here. Scheduler/stability toggles (priority boost,
            //    lightweight pooling, affinity mask) are NOT here: they rebalance or
            //    stabilise, they do not reduce the work performed (excluded above).
            (RemediationType.Config, new[]
            {
                "OPTIMIZE FOR AD HOC", "OPTIMIZE-FOR-ADHOC", "OPTIMIZE FOR ADHOC",
                // MS ruleset: high single-use-plan ratio -> enable optimize-for-ad-hoc
                // (cuts plan-cache bloat / compile pressure). NOT bare "AD HOC" — that
                // catches the surface-area "ad hoc distributed queries" security finding.
                "SINGLE-USE PLANS", "SINGLE USE PLANS",
            }),

            // 5. I/O reduction — tempdb, file growth, VLFs, I/O stalls, compression.
            (RemediationType.IoReduction, new[]
            {
                "TEMPDB", "AUTOGROW", "AUTO-GROW", "FILE GROWTH", "FILE-GROWTH",
                "VLF", "VIRTUAL LOG FILE",
                "IO STALL", "I/O", "IO-LATENCY", "IO LATENCY", "FILE IO",
                "WRITELOG", "DATA COMPRESSION",
                "SEPARATE DATA", "VERSION STORE", "VERSION-STORE",
            }),
        };

        /// <summary>
        /// Returns the coarse fix class a finding's remediation falls into, or null
        /// when the fix is not a compute/I-O-reducing change (so no power band is shown).
        /// </summary>
        public static RemediationType? Classify(string? checkId, string? displayName)
        {
            var haystack = ((checkId ?? string.Empty) + " " + (displayName ?? string.Empty))
                .ToUpperInvariant();
            if (haystack.Trim().Length == 0) return null;

            // Surface-match exclusions: fixes that contain a compute keyword but whose
            // remediation rebalances / cleans up rather than reducing CPU or I/O work.
            // Badging them would over-claim, so they get NO band (adversarial-review confirmed):
            //  · affinity mask / affinity I/O mask — CPU-scheduler thread pinning; resetting
            //    rebalances across schedulers, it does not reduce work (and "affinity I/O mask"
            //    would otherwise surface-match the I/O rule).
            //  · TF 1117 / TF 4136 — trace-flag advisories (set/unset an instance flag); not work
            //    reducers, and they surface-match "AUTOGROW" / "PARAMETER SNIFF" respectively.
            //    (Disabling TF 4136 actually RESTORES default parameter sniffing.)
            //  · tempdb file-error — a reliability/metadata fix (needs a restart) that saves no
            //    I/O, though it surface-matches "TEMPDB". (The version-store check IS treated as
            //    I/O — draining a bloated store cuts tempdb I/O — so it lives in the I/O rule.)
            if (haystack.Contains("AFFINITY", StringComparison.Ordinal)
             || haystack.Contains("TF 1117", StringComparison.Ordinal)
             || haystack.Contains("TF1117", StringComparison.Ordinal)
             || haystack.Contains("TF 4136", StringComparison.Ordinal)
             || haystack.Contains("TF4136", StringComparison.Ordinal)
             || haystack.Contains("TEMPDB-FILE-ERROR", StringComparison.Ordinal)
             || haystack.Contains("TEMPDB FILE ERROR", StringComparison.Ordinal))
                return null;

            foreach (var (type, keywords) in Rules)
                foreach (var kw in keywords)
                    if (haystack.Contains(kw, StringComparison.Ordinal))
                        return type;

            return null;
        }
    }
}
