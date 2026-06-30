/* In the name of God, the Merciful, the Compassionate */

using FluentAssertions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

// Guards the read-only decision-support power band: a finding gets a power chip
// ONLY when its remediation genuinely reduces CPU/I-O work. The honesty half of
// these tests — the null cases — matters most: a security/backup/HA finding must
// NEVER carry a "power saved" band, because its fix saves no compute.
public class RemediationFixClassifierTests
{
    [Theory]
    // Parallelism / parameter-sniffing → MaxdopParamSniff
    [InlineData("SQLT-CUSTOM-MAXDOP", "Parallelism MaxDOP", RemediationType.MaxdopParamSniff)]
    [InlineData("SQLT-BPCHK-00220-PARALLELISM-MAXDOP", "", RemediationType.MaxdopParamSniff)]
    [InlineData("SQLT-VA-COST-THRESHOLD-PARALLELISM", "Non-default value for 'cost threshold for parallelism'", RemediationType.MaxdopParamSniff)]
    [InlineData("SQLT-FRONTIER-MAXDOP-CXPACKET", "Unbounded MAXDOP Correlated with High Parallelism Wait Times", RemediationType.MaxdopParamSniff)]
    // Indexing → IndexAddRebuild
    [InlineData("SQLT-BPCHK-01200-UNUSED-INDEXES", "", RemediationType.IndexAddRebuild)]
    [InlineData("SQLT-CUSTOM-INDEX-FRAGMENTATION", "", RemediationType.IndexAddRebuild)]
    [InlineData("SQLT-CUSTOM-FK-MISSING-INDEX", "", RemediationType.IndexAddRebuild)]
    [InlineData("SQLT-CUSTOM-HEAP-FORWARDED-RECORDS", "", RemediationType.IndexAddRebuild)]
    [InlineData("X", "Detect duplicate indexes (same key columns)", RemediationType.IndexAddRebuild)]
    // I/O → IoReduction
    [InlineData("SQLT-BLITZ-HIGH-VLF-COUNT", "", RemediationType.IoReduction)]
    [InlineData("SQLT-BLITZ-TEMPDB-HAS-MORE-THAN-16-DATA-FILES", "", RemediationType.IoReduction)]
    [InlineData("SQLT-BPCHK-00840-PERCENT-AUTOGROWS", "", RemediationType.IoReduction)]
    [InlineData("SQLT-CUSTOM-FILE-IO-LATENCY", "", RemediationType.IoReduction)]
    [InlineData("X", "Optimize Storage and I/O using Data Compression", RemediationType.IoReduction)]
    [InlineData("SQLT-CUSTOM-TEMPDB-VERSION-STORE", "TempDB Version Store", RemediationType.IoReduction)]
    // Plan-quality / cardinality fixes → PlanQuality (stale/missing stats, implicit conversion)
    [InlineData("SQLT-CUSTOM-IMPLICIT-CONVERSION-SEEK", "Seek-Defeating Implicit Conversion on a CPU-Heavy Cached Query", RemediationType.PlanQuality)]
    [InlineData("SQLT-CUSTOM-STATISTICS-HEALTH", "Statistics Stale by Modification Churn or Last Sampled Below 25%", RemediationType.PlanQuality)]
    [InlineData("SQLT-BLITZ-AUTO-UPDATE-STATS", "User databases with Auto-Update Statistics disabled", RemediationType.PlanQuality)]
    [InlineData("SQLT-VA-AUTOCREATESTATS", "Auto-Create Statistics should be ON", RemediationType.PlanQuality)]
    [InlineData("SQLT-BLITZ-STATISTICS-WITHOUT-HISTOGRAMS", "Statistics Without Histograms", RemediationType.PlanQuality)]
    [InlineData("SQLT-CUSTOM-PLAN-WARNINGS", "Cross-Join or Missing-Statistics Plan Warning on a CPU-Heavy Query", RemediationType.PlanQuality)]
    // CPU-affecting config toggles → Config (only genuine work-reducing toggles)
    [InlineData("SQLT-CUSTOM-OPTIMIZE-FOR-ADHOC", "Optimize for ad hoc workloads should be enabled", RemediationType.Config)]
    // Real MS Assessment ruleset display names (the live VA path) — genuine compute fixes.
    [InlineData("TablesWithoutClusteredIndex", "Tables without clustered indexes", RemediationType.IndexAddRebuild)]
    [InlineData("TablesWithMoreIndexesThanColumns", "Tables with more indexes than columns", RemediationType.IndexAddRebuild)]
    [InlineData("StatisticsOutOfDate", "Statistics need to be updated", RemediationType.PlanQuality)]
    [InlineData("IndexFragmentation", "Index Fragmentation", RemediationType.IndexAddRebuild)]
    [InlineData("RedundantIndexes", "Tables with redundant indexes", RemediationType.IndexAddRebuild)]
    [InlineData("RarelyUsedIndexes", "Rarely used index", RemediationType.IndexAddRebuild)]
    [InlineData("TablesWithoutIndexes", "Tables without indexes", RemediationType.IndexAddRebuild)]
    [InlineData("FKWithoutIndex", "Foreign key constraints should have corresponding indexes", RemediationType.IndexAddRebuild)]
    [InlineData("PlansUseRatio", "Amount of single-use plans in cache is high", RemediationType.Config)]
    public void Classify_maps_performance_fixes_to_their_class(string id, string name, RemediationType expected)
        => RemediationFixClassifier.Classify(id, name).Should().Be(expected);

    [Theory]
    // Index keyword wins over I/O keyword: the fix is dropping indexes, not I/O tuning.
    [InlineData("X", "Telemetry-Weighted: Unused Indexes Exacerbating I/O Bottlenecks", RemediationType.IndexAddRebuild)]
    public void Classify_resolves_overlaps_by_rule_precedence(string id, string name, RemediationType expected)
        => RemediationFixClassifier.Classify(id, name).Should().Be(expected);

    [Theory]
    // The honesty guard: non-compute findings get NO power band.
    [InlineData("SQLT-CUSTOM-BACKUP-NOT-ENCRYPTED", "Backup is not encrypted")]
    [InlineData("SQLT-CUSTOM-WEAK-SQL-PASSWORD", "Weak SQL login password")]
    [InlineData("SQLT-CUSTOM-UNENCRYPTED-CONNECTIONS", "Connections are not encrypted")]
    [InlineData("SQLT-CUSTOM-AG-LISTENER-OFFLINE", "Availability Group listener offline")]
    [InlineData("SQLT-BLITZ-LINKED-SERVER-CONFIGURED", "Linked server configured")]
    // "Ad Hoc Distributed Queries" is surface-area, NOT "optimize for ad hoc" — must not match.
    [InlineData("X", "Ad Hoc Distributed Queries should be disabled")]
    // Index-shaped but not an add/rebuild-for-CPU fix → no band.
    [InlineData("SQLT-VA-INDEX-CREATE-MEMORY", "'index create memory' should be 0 or greater than 'min memory per query'")]
    [InlineData("SQLT-CUSTOM-XTP-INDEX-HEALTH", "In-memory OLTP index health")]
    [InlineData("SQLT-CUSTOM-OVERSIZED-INDEX-KEY", "Oversized index key")]
    // Optimizer-ignored index cleanup → metadata only, zero compute.
    [InlineData("SQLT-CORE-DETECT-AND-CLEAN-UP-HYPOTHETICAL-INDEXES-LEFT-BEHI", "Detect and Clean Up Hypothetical Indexes Left Behind by Tuning Tools")]
    [InlineData("SQLT-BLITZ-LEFTOVER-FAKE-INDEXES-FROM-WIZARDS", "Leftover Fake Indexes From Wizards")]
    // Scheduler/stability config toggles — rebalance/stabilise, do not reduce work.
    [InlineData("SQLT-VA-AFFINITY-MASK", "Non-default value for 'affinity mask' option")]
    [InlineData("SQLT-VA-AFFINITY-IO-MASK", "Non-default value for 'affinity I/O mask' option")]
    [InlineData("X", "Affinity mask and affinity I/O mask should not overlap")]
    [InlineData("X", "priority boost should be disabled")]
    [InlineData("SQLT-VA-LIGHTWEIGHT-POOLING", "Lightweight pooling (fiber mode) should be disabled")]
    // TF 1117 / TF 4136 surface-match AUTOGROW / PARAMETER SNIFF but are trace-flag advisories.
    [InlineData("SQLT-VA-TF1117", "TF 1117 enables filegroup-level autogrow")]
    [InlineData("TF4136", "TF 4136 disables parameter sniffing")]
    // OS disk-defrag (not SQL index fragmentation) + surface-area ad-hoc (not optimize-for-adhoc).
    [InlineData("DiskFragmentationAnalysis", "Disk fragmentation analysis")]
    [InlineData("AdHocDistributedQueries", "Option 'ad hoc distributed queries' should be disabled")]
    // TempDB file-error — metadata/reliability fix (restart), saves no I/O despite "TEMPDB".
    // (version-store is treated as I/O — see the positive cases above.)
    [InlineData("SQLT-BLITZ-TEMPDB-FILE-ERROR", "TempDB File Error")]
    // Informational / observational statistics checks — not compute-reducing fixes.
    [InlineData("SQLT-VA-AUTO-UPDATE-STATS-ASYNC", "Auto Update Statistics Async usage (informational)")]
    [InlineData("SQLT-BLITZ-USER-CREATED-STATISTICS-IN-PLACE", "User-Created Statistics In Place")]
    [InlineData("SQLT-BLITZ-STATS-UPDATED-ASYNCHRONOUSLY", "Stats Updated Asynchronously")]
    [InlineData("SQLT-VA-TF2389", "TF 2389 enables automatic statistics for ascending keys")]
    // Real MS Assessment ruleset findings whose CheckId/display tripped substring false
    // positives (the live VA path feeds these, not corpus titles) — must get NO badge:
    // wait-stats (not I/O), and clustered-index design issues (not add/rebuild-for-CPU).
    [InlineData("WaitsPageLatch", "High Page Latch waits")]
    [InlineData("WaitsNonPageLatch", "High non-Page latch waits")]
    [InlineData("NonUniqueClusteredIndex", "Non-unique clustered indexes")]
    [InlineData("NonClusteredIndexRetry", "NonClustered index retry amount")]
    [InlineData("ClusteredIndexWideKey", "Clustered indexes keys with more than 900 bytes")]
    [InlineData("GuidClusteredKey", "Guid in clustered index key column")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Classify_returns_null_for_non_compute_findings(string? id, string? name)
        => RemediationFixClassifier.Classify(id, name).Should().BeNull();
}
