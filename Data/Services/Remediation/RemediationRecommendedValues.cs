/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationRecommendedValues — the standard recommendation SQLTriage pre-fills for the three
 * Configuration fixes whose right answer depends on the host, not on a constant: MAXDOP, cost
 * threshold for parallelism, and max server memory (MB).
 *
 * Ruling 1 (DECISIONS 2026-08-25 17:33). Those three templates shipped RecommendedValue = null,
 * because the app has no CONSTANT to declare. It does have the host facts. So the recommendation
 * is computed per server from what the server reports, the operator still confirms it before
 * anything runs, and the tooltip says it is a recommendation and where it comes from.
 *
 * ┌──────────────────────────────────────────────────────────────────────────────────────────┐
 * │ ADRIAN REVIEWS THESE FORMULAS AT MERGE REVIEW. The three below are the standard published │
 * │ recommendations, not SQLTriage's own opinion. Each carries its source in the comment above │
 * │ it. Change the numbers here and every pre-fill, tooltip and test moves with them.          │
 * └──────────────────────────────────────────────────────────────────────────────────────────┘
 *
 * Nothing here writes. A recommendation is a pre-filled input the operator can overtype or clear,
 * and every apply still goes through the same five gates. Where the facts cannot be read, no
 * recommendation is produced and nothing is pre-filled — the honest state this lane shipped.
 */

using System;

namespace SQLTriage.Data.Services.Remediation
{
    /// <summary>
    /// The host facts a recommendation is computed from, as read from one server. Every member is
    /// nullable because every one of them can fail to read (VIEW SERVER STATE, an older DMV shape,
    /// a dropped connection) and a recommendation computed from a guessed core count is a guess.
    /// </summary>
    public sealed record ServerSizingFacts(int? LogicalCpuCount, int? NumaNodeCount, int? PhysicalMemoryMb)
    {
        /// <summary>Nothing was read. Used as the "no facts" value so call sites need no null dance.</summary>
        public static readonly ServerSizingFacts None = new(null, null, null);
    }

    public static class RemediationRecommendedValues
    {
        /// <summary>
        /// Read-only. One row, three columns: logical processors, NUMA node count, physical RAM in MB.
        /// The NUMA count is the <c>sys.dm_os_nodes</c> form (excluding the DAC node), which is what
        /// the MAXDOP guidance below means by "NUMA node".
        /// </summary>
        public const string SizingQuery =
            "SELECT si.cpu_count AS LogicalCpuCount, " +
            "(SELECT COUNT(DISTINCT n.memory_node_id) FROM sys.dm_os_nodes n WHERE n.node_state_desc <> 'ONLINE DAC') AS NumaNodeCount, " +
            "si.physical_memory_kb / 1024 AS PhysicalMemoryMb " +
            "FROM sys.dm_os_sys_info si;";

        /// <summary>
        /// MAXDOP, per Microsoft's published guidance ("Configure the max degree of parallelism
        /// Server Configuration Option", SQL Server 2016 and later):
        /// <list type="bullet">
        /// <item>one NUMA node, 8 or fewer logical processors: MAXDOP = the logical processor count;</item>
        /// <item>one NUMA node, more than 8: MAXDOP = 8;</item>
        /// <item>more than one NUMA node, 16 or fewer logical processors per node: MAXDOP = processors per node;</item>
        /// <item>more than one NUMA node, more than 16 per node: MAXDOP = half the processors per node, capped at 16.</item>
        /// </list>
        /// ADRIAN REVIEWS AT MERGE REVIEW.
        ///
        /// <para>Null when the core count is unknown, and null for a 1-core server: the only value
        /// this rule could offer there is 1, which is also SQL Server's serial setting, so the
        /// recommendation carries no information the operator does not already have.</para>
        /// </summary>
        public static int? MaxDop(ServerSizingFacts? facts)
        {
            if (facts?.LogicalCpuCount is not int cores || cores < 2) return null;

            var nodes = facts.NumaNodeCount is int n && n > 0 ? n : 1;
            var perNode = Math.Max(1, cores / nodes);

            if (nodes == 1)
                return cores <= 8 ? cores : 8;

            if (perNode <= 16) return perNode;
            return Math.Min(16, Math.Max(1, perNode / 2));
        }

        /// <summary>
        /// Cost threshold for parallelism. SQL Server ships 5, a 1997 number nobody defends; the
        /// long-standing community starting point (Brent Ozar, Erik Darling, Glenn Berry all publish
        /// the same range) is 50, then tune from the plan cache. ADRIAN REVIEWS AT MERGE REVIEW.
        ///
        /// <para>A constant on purpose: this one does not depend on the host at all, which is why it
        /// needs no facts and is never null.</para>
        /// </summary>
        public const int CostThresholdForParallelism = 50;

        /// <summary>
        /// Max server memory (MB), per Jonathan Kehayias's widely-used reservation formula: leave the
        /// OS 1 GB, plus 1 GB for every 4 GB of host RAM between 4 GB and 16 GB, plus 1 GB for every
        /// 8 GB above 16 GB. SQL Server gets what is left. ADRIAN REVIEWS AT MERGE REVIEW.
        ///
        /// <para>Null below 4 GB of host RAM: the reservation would leave SQL Server less than the
        /// engine's own documented minimum, so there is no honest cap to offer. Null when host RAM
        /// is unknown. The result is floored at 1024 MB so a small host can never be handed a cap
        /// that starves the engine, and it says nothing about what ELSE runs on the box — a shared
        /// host needs a smaller cap than this, which is why the operator confirms it.</para>
        /// </summary>
        public static int? MaxServerMemoryMb(ServerSizingFacts? facts)
        {
            if (facts?.PhysicalMemoryMb is not int totalMb || totalMb < 4096) return null;

            var reservedMb = 1024;                                   // the OS floor
            var tierOneMb = Math.Min(totalMb, 16384) - 4096;         // the 4-16 GB band
            if (tierOneMb > 0) reservedMb += (tierOneMb / 4096) * 1024;
            var tierTwoMb = totalMb - 16384;                         // everything above 16 GB
            if (tierTwoMb > 0) reservedMb += (tierTwoMb / 8192) * 1024;

            var cap = totalMb - reservedMb;
            return cap < 1024 ? 1024 : cap;
        }

        /// <summary>
        /// The recommendation for one template on one server, or null when there is none. Keyed on
        /// the op's sp_configure setting name rather than the template key, so a corpus-fed template
        /// that targets the same shipped setting gets the same recommendation the built-in does.
        ///
        /// <para>A template that declares its own <see cref="RemediationOperation.RecommendedValue"/>
        /// wins outright: that is a target the fix's own description states ("Enable ... (1)"), and
        /// it is also the value the regression guard defends. This method never overrides it.</para>
        /// </summary>
        public static int? For(RemediationTemplate? template, ServerSizingFacts? facts)
        {
            var op = template?.Operation;
            if (op is null) return null;
            if (op.RecommendedValue is int declared) return declared;
            if (op.OpKind != RemediationOpKind.SpConfigure) return null;

            var value = op.ConfigName?.Trim().ToLowerInvariant() switch
            {
                "max degree of parallelism"      => MaxDop(facts),
                "cost threshold for parallelism" => CostThresholdForParallelism,
                "max server memory (mb)"         => MaxServerMemoryMb(facts),
                _                                => null,
            };

            // Never pre-fill something the template's own bounds would refuse: the input would be
            // rendered pre-loaded with a value the executor rejects on sight.
            if (value is not int v) return null;
            return v < op.MinValue || v > op.MaxValue ? null : v;
        }

        /// <summary>
        /// Where a pre-filled number came from, in one sentence, for the input's tooltip. States
        /// that it is a recommendation and why it is that number, so nobody reads a pre-filled box
        /// as a measurement of their server.
        /// </summary>
        public static string DescribeRecommendation(RemediationTemplate? template, ServerSizingFacts? facts, int value)
        {
            var op = template?.Operation;
            if (op?.RecommendedValue is int declared && declared == value)
                return $"Pre-filled with {value}. That is the target this fix exists to reach. You confirm it before anything runs.";

            return op?.ConfigName?.Trim().ToLowerInvariant() switch
            {
                "max degree of parallelism" =>
                    $"Recommended: {value}. Microsoft's MAXDOP guidance, applied to this server's "
                    + $"{DescribeCount(facts?.LogicalCpuCount, "logical processor")} across "
                    + $"{DescribeCount(facts?.NumaNodeCount, "NUMA node")}. It is a recommendation, not a measurement of your workload. "
                    + "You confirm it before anything runs.",
                "cost threshold for parallelism" =>
                    $"Recommended: {value}. SQL Server ships 5, set in 1997 and far too low for modern hardware. "
                    + "50 is the standard starting point. It is a recommendation, not a measurement of your workload. "
                    + "You confirm it before anything runs.",
                "max server memory (mb)" =>
                    $"Recommended: {value} MB. Kehayias's reservation formula applied to this server's "
                    + $"{DescribeMb(facts?.PhysicalMemoryMb)} of RAM. It assumes SQL Server is the only thing on this host. "
                    + "If anything else runs here, cap it lower. You confirm it before anything runs.",
                _ =>
                    $"Pre-filled with {value}. It is a recommendation. You confirm it before anything runs.",
            };
        }

        private static string DescribeCount(int? count, string singular) =>
            count is int c ? $"{c} {singular}{(c == 1 ? "" : "s")}" : $"an unread {singular} count";

        private static string DescribeMb(int? mb) =>
            mb is int m ? $"{m} MB" : "an unread amount";
    }
}
