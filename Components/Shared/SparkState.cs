/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data.Services;

namespace SQLTriage.Components.Shared
{
    /// <summary>
    /// What one sample in a <see cref="Sparkline"/> dot chart actually says. Three states, not two
    /// (A1, 2026-07-20).
    ///
    /// <para>The dot chart used to be <c>List&lt;bool&gt;</c> built straight off
    /// <c>CheckHistoryPoint.Passed</c>. WARN rides Passed=true (ruling #4), so a run that could not
    /// assess the target plotted a GREEN dot — on the page a DBA opens precisely to say "it has been
    /// green for thirty runs". The bool type itself was the defect: a two-state carrier cannot
    /// represent a non-assertion, so every consumer of it was forced to lie. The enum replaces the
    /// bool outright rather than sitting beside it, so the two-state path cannot be reintroduced.</para>
    /// </summary>
    public enum SparkState
    {
        /// <summary>A scorable PASS — the check asserted the control is correct.</summary>
        Pass,

        /// <summary>A scorable FAIL — the check asserted the control is wrong.</summary>
        Fail,

        /// <summary>
        /// The check ran but asserted nothing about the control (WARN). Neither a pass nor a
        /// failure: it is the absence of a verdict, and it is drawn as such.
        /// </summary>
        Unassessed,
    }

    /// <summary>
    /// The single classified mapping from a history point to a <see cref="SparkState"/>.
    ///
    /// <para>This is the ONE place the trend page's dot chart is allowed to read
    /// <c>CheckHistoryPoint.Passed</c>, and it reads it only after the WARN arm has already
    /// claimed the point. Kept out of <c>CheckTrend.razor</c> so it can be exercised directly —
    /// a mapping that lives inside a Blazor lifecycle method is a mapping no test reaches, which
    /// is how the raw read survived four cold gates.</para>
    /// </summary>
    public static class SparkStates
    {
        /// <summary>
        /// Classifies one history point. The WARN arm MUST precede the Passed arm: a WARN carries
        /// Passed=true, so testing Passed first returns <see cref="SparkState.Pass"/> and the
        /// third state becomes unreachable.
        ///
        /// <para>A null verdict is NOT a WARN — see
        /// <see cref="CheckClassification.IsWarnVerdict"/>. Rows written before
        /// <c>check_results.verdict</c> existed carry null, and they keep the pre-existing
        /// pass/fail reading rather than being retro-labelled.</para>
        /// </summary>
        public static SparkState For(CheckHistoryPoint p) =>
            CheckClassification.IsWarnVerdict(p.Verdict) ? SparkState.Unassessed
            : p.Passed ? SparkState.Pass
            : SparkState.Fail;
    }
}
