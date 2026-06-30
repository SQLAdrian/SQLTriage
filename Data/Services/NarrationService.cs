/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Linq;
using SQLTriage.Data.Services.Narration;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Adapter: turns a GovernanceScore into a NarrationContext and composes a plain-language
    /// read via the shared offline NarrationEngine (Narration/*). The eloquence lives in
    /// NarrationLibrary; this class only maps app state → the engine's situation model, so the
    /// same engine can later narrate the Risk Register, the F-VAL value story, and report cover
    /// notes from their own contexts. No model call — the voice is captured, not generated live.
    /// </summary>
    public static class NarrationService
    {
        public static string Narrate(GovernanceScore? score)
        {
            if (score == null) return string.Empty;
            return NarrationComposer.Compose(ToContext(score));
        }

        /// <summary>
        /// Colleague-voice read of the "progress since baseline" story (P3/F-VAL). Feeds the
        /// engine's trend/improved/regressed slots — the ones that stayed default until the
        /// history subsystem existed. Distinct from ValueNarrativeService: this is the
        /// qualitative situational read (direction + what moved), NOT the ranged hours figure,
        /// so the two don't overlap on the page. Reuses the existing library, so the honesty
        /// contract already covers it (no new phrasings). Empty string when there's no baseline.
        /// </summary>
        public static string NarrateBaseline(CheckTransitionResult? t, BaselineSummary? baseline)
        {
            if (t == null || baseline == null) return string.Empty;

            // Trend must follow ACTUAL check movement, not composite-score noise: a score that
            // wobbles on the pass-rate fallback must never narrate "slipped" with zero regressions
            // (Language Contract — don't assert a direction the check data doesn't support).
            // Worsening only when checks actually regressed; Improving only when some resolved and
            // none regressed; mixed/none ⇒ Steady (the regression rider still surfaces regressions).
            var resolved = t.Resolved.Count;
            var regressed = t.Regressed.Count;
            var trend =
                regressed > 0 && resolved == 0 ? TrendDirection.Worsening :
                resolved > 0 && regressed == 0 ? TrendDirection.Improving :
                TrendDirection.Steady;

            // Frame the headline with the baseline's known pass/fail counts (the current full run
            // covers the same check set, so these are the right order of magnitude for the
            // "evaluated > 0" gate and the headline bucket). The trend/improved/regressed slots
            // carry the actual since-baseline story.
            var ctx = new NarrationContext
            {
                OverallScore = t.CurrentCompositeScore,
                PassedCount = baseline.PassedChecks,
                FailedCount = baseline.FailedChecks,
                TotalChecks = baseline.TotalChecks,
                IsIndicative = false,
                Trend = trend,
                NetImproved = resolved,
                Regressed = regressed,
            };

            return NarrationComposer.Compose(ctx);
        }

        private static NarrationContext ToContext(GovernanceScore score)
        {
            var withFindings = score.Categories.Values.Where(c => c.FindingCount > 0).ToList();

            DimensionFact? weakest = withFindings
                .OrderBy(c => (double)c.PassedCount / c.FindingCount)
                .ThenByDescending(c => c.FindingCount)
                .Select(ToFact)
                .FirstOrDefault();

            DimensionFact? strongest = withFindings
                .OrderByDescending(c => (double)c.PassedCount / c.FindingCount)
                .ThenByDescending(c => c.FindingCount)
                .Select(ToFact)
                .FirstOrDefault();

            return new NarrationContext
            {
                OverallScore = score.Evaluated() == 0 ? -1 : score.Overall,
                FailedCount = score.FailedFindings,
                PassedCount = score.PassedFindings,
                TotalChecks = score.TotalFindings,
                IsIndicative = score.IsIndicative,
                Weakest = weakest,
                Strongest = strongest,
                // Trend/NetImproved/Regressed stay default until the history subsystem (F-VAL/P3)
                // feeds them; the composer simply omits those slots when there's no history.
            };
        }

        private static DimensionFact ToFact(CategoryScore c) => new()
        {
            Label = Humanise(c.Dimension),
            Passed = c.PassedCount,
            Total = c.FindingCount,
        };

        private static string Humanise(string dimension)
        {
            if (string.IsNullOrWhiteSpace(dimension)) return "this area";
            return System.Text.RegularExpressions.Regex.Replace(dimension, "(?<=[a-z])(?=[A-Z])", " ").Trim();
        }

        // Local helper mirroring the dashboard's "evaluated" notion.
        private static int Evaluated(this GovernanceScore s) => s.PassedFindings + s.FailedFindings;
    }
}
