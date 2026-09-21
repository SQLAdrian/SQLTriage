/* In the name of God, the Merciful, the Compassionate */
/*
 * What a job-history sweep actually covered, in words.
 *
 * pages-r2-01 (honesty hunt, 2026-08-28): /agent-timeline fans out one msdb read per configured
 * instance and every one of them was wrapped in a wholly silent `catch (Exception) { }` — no
 * toast, no log at any level, not even Debug. The four summary cards (Total / Failed / Succeeded
 * / Unique) were then computed over whatever subset answered and rendered with no qualifier, so
 * "Failed 0" read as a fact about the estate. Proved live at hunt time with five configured
 * connections of which three could not be read.
 *
 * The sentences below are the measurement's own description of itself: they are computed from
 * the same per-server outcomes the cards are computed from, so a card and its coverage line can
 * never disagree. Pure functions - no SQL, no UI - so the wording is unit-testable.
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLTriage.Data.Services.Jobs
{
    /// <summary>One instance's contribution to a job-history sweep.</summary>
    /// <param name="ServerName">The instance as configured.</param>
    /// <param name="Answered">True only when the msdb query returned rows or an empty result set.</param>
    /// <param name="FailureDetail">The exception message when it did not answer; null when it did.</param>
    /// <param name="Executions">Rows this instance contributed. Always 0 when it did not answer.</param>
    public sealed record JobHistoryServerRead(string ServerName, bool Answered, string? FailureDetail, int Executions);

    public static class JobHistoryCoverage
    {
        /// <summary>
        /// The scope sentence that must sit beside any total computed from a sweep: what the
        /// numbers were measured across. Never claims an instance that did not answer.
        /// </summary>
        public static string DescribeScope(IReadOnlyList<JobHistoryServerRead> reads)
        {
            if (reads is null || reads.Count == 0)
                return "No instances are configured, so nothing was read.";

            var answered = reads.Count(r => r.Answered);
            if (answered == reads.Count)
                return reads.Count == 1
                    ? "Measured across the 1 configured instance."
                    : $"Measured across all {reads.Count} configured instances.";

            return $"Measured across {answered} of {reads.Count} configured instances.";
        }

        /// <summary>
        /// Names the instances the sweep could not read, and why. Null when every instance
        /// answered - a caller that renders this unconditionally renders nothing extra.
        /// </summary>
        public static string? DescribeGap(IReadOnlyList<JobHistoryServerRead> reads)
        {
            if (reads is null || reads.Count == 0) return null;

            var silent = reads.Where(r => !r.Answered).ToList();
            if (silent.Count == 0) return null;

            var names = string.Join(", ", silent.Select(r => Describe(r.ServerName)));
            var noun = silent.Count == 1 ? "instance" : "instances";
            var verb = silent.Count == 1 ? "did not answer" : "did not answer";

            var reason = silent
                .Select(r => r.FailureDetail)
                .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));

            var sentence = $"{silent.Count} {noun} {verb}, so nothing above describes {(silent.Count == 1 ? "it" : "them")}: {names}.";
            return string.IsNullOrWhiteSpace(reason) ? sentence : sentence + $" First reason: {reason}";
        }

        /// <summary>
        /// What to print when the sweep produced no rows at all. "No job history in the last 24
        /// hours" is a claim about the estate and may only be made about instances that answered.
        /// </summary>
        public static string DescribeEmptyState(IReadOnlyList<JobHistoryServerRead> reads)
        {
            if (reads is null || reads.Count == 0)
                return "No enabled connections are configured, so no instance was contacted.";

            var answered = reads.Count(r => r.Answered);
            if (answered == 0)
                return $"None of the {reads.Count} configured instance(s) answered, so nothing is known about "
                     + "their job history. This is not a report that no jobs ran.";

            var head = answered == reads.Count
                ? $"No job history in the last 24 hours on the {answered} instance(s) contacted."
                : $"No job history in the last 24 hours on the {answered} of {reads.Count} instance(s) that answered.";

            var gap = DescribeGap(reads);
            return gap is null ? head : head + " " + gap;
        }

        private static string Describe(string serverName) =>
            string.IsNullOrWhiteSpace(serverName) ? "(unnamed instance)" : serverName;
    }
}
