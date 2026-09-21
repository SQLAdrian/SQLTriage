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
        /// <param name="headlineScore">
        /// 2026-07-21 (persona board, FIX 3). The score the reader is LOOKING AT when this sentence
        /// renders, on a 0-100 scale — /cio passes its Estate Executive Health value.
        ///
        /// Why it exists: the /cio insight card sits directly above the Executive Health gauge, and
        /// its opening clause is keyed to a ScoreBucket. That bucket was derived from the GOVERNANCE
        /// score (78), so the page rendered "This estate looks reasonably healthy" in a green banner
        /// immediately above a gauge reading 38 / Critical with six CRITICAL findings under it. The
        /// sentence was not wrong about governance; it was answering a question the reader had not
        /// asked, in the position where the headline answer belongs.
        ///
        /// Passing the headline score makes the opening clause follow the number beside it. The
        /// dimension slots keep coming from governance — those ARE governance dimensions and are
        /// labelled as such on the page.
        ///
        /// Null (the default) preserves the old behaviour exactly, for callers with no headline
        /// number of their own to reconcile against.
        /// </param>
        /// <param name="estate">
        /// Gate fix C2 (2026-08-05). The Executive Health rollup rendered on the SAME screen, when
        /// the caller has one. The headline bucket is keyed to the governance score, and a
        /// governance score with nothing evaluated buckets Unknown, whose phrasings open "nothing
        /// has been assessed yet". On /cio that sentence rendered directly above an Executive
        /// Health card printing a mean across N assessed servers and a caption naming those N.
        /// Passing the rollup lets the composer pick a phrasing that says which of the two is
        /// missing, instead of declaring the page empty over the top of a measured number.
        ///
        /// Null (the default) preserves the old behaviour exactly.
        /// </param>
        public static string Narrate(GovernanceScore? score, double? headlineScore = null,
            EstateHealthRollup? estate = null)
        {
            if (score == null) return string.Empty;
            return NarrationComposer.Compose(ToContext(score, headlineScore, estate));
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

        private static NarrationContext ToContext(GovernanceScore score, double? headlineScore = null,
            EstateHealthRollup? estate = null)
        {
            var withFindings = score.Categories.Values.Where(c => c.FindingCount > 0).ToList();

            // 2026-07-21 (persona board, FIX 3): ranked by RawScore — the value the /cio mini-rings
            // actually render — not by the unweighted pass ratio. Ranking by a quantity the page
            // does not draw meant "your weakest area is X" could name a dimension whose ring was
            // NOT the lowest one on screen: the sentence and the picture disagreed about which
            // dimension was worst, which is a harder contradiction to explain away than a
            // percentage gap. Ties still break toward the dimension with more checks behind it.
            DimensionFact? weakest = withFindings
                .OrderBy(c => c.RawScore)
                .ThenByDescending(c => c.FindingCount)
                .Select(ToFact)
                .FirstOrDefault();

            DimensionFact? strongest = withFindings
                .OrderByDescending(c => c.RawScore)
                .ThenByDescending(c => c.FindingCount)
                .Select(ToFact)
                .FirstOrDefault();

            return new NarrationContext
            {
                // The headline clause follows the number printed beside it when the caller has one;
                // otherwise it falls back to the governance score exactly as before.
                OverallScore = score.Evaluated() == 0 ? -1 : (headlineScore ?? score.Overall),
                FailedCount = score.FailedFindings,
                PassedCount = score.PassedFindings,
                TotalChecks = score.TotalFindings,
                IsIndicative = score.IsIndicative,
                Weakest = weakest,
                Strongest = strongest,
                // The SAME counts the estate caption is composed from, so the headline and the
                // caption cannot disagree about whether anything on the page was measured.
                EstateAssessedCount = estate?.AssessedCount ?? 0,
                EstateServerCount = estate?.ServerCount ?? 0,
                // Trend/NetImproved/Regressed stay default until the history subsystem (F-VAL/P3)
                // feeds them; the composer simply omits those slots when there's no history.
            };
        }

        private static DimensionFact ToFact(CategoryScore c) => new()
        {
            Label = Humanise(c.Dimension),
            Passed = c.PassedCount,
            Total = c.FindingCount,
            // The SAME value CioDashboard draws in the mini-ring for this dimension, so the
            // sentence and the ring cannot print different numbers for one dimension.
            Score = c.RawScore,
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
