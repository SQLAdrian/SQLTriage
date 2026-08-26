/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// ONE estate-wide unknown policy (2026-08-05). Before this, /cio, /dba, /health and the
    /// portal daily-summary publisher each invented their own answer to "what do we say when
    /// there is no number?", and each invented a different one. Lifting the question into a
    /// single object is what stops the next surface inventing a twelfth.
    ///
    /// THE LAW this object exists to enforce: <b>a printed claim must be conditioned on the same
    /// measurement that produced the verdict beside it.</b> Every sentence below is composed from
    /// counts and states that were measured; none is composed from a count that was assumed.
    ///
    /// The sentences live here rather than in markup on purpose: a string returned by a method is
    /// assertable character-for-character in a unit test, where a sentence assembled inside a
    /// .razor is only assertable as a markup substring, and a markup substring assertion is how a
    /// prior round of this same work passed vacuously.
    ///
    /// <para><b>Surface x state — what each surface renders, and from which measurement.</b>
    /// Every cell is produced by exactly one method below.</para>
    /// <list type="table">
    ///   <listheader><term>Surface</term><description>Method / behaviour</description></listheader>
    ///   <item><term>/cio estate caption</term>
    ///         <description><see cref="EstateBasis"/> over <see cref="Summarise"/>; the mean is
    ///         <see cref="EstateHealthRollup.MeanScore"/>, null unless something was measured.</description></item>
    ///   <item><term>/dba per-server card</term>
    ///         <description><see cref="ServerBasis"/> for the card, <see cref="DimensionValue"/> and
    ///         <see cref="DimensionBasis"/> per dimension row.</description></item>
    ///   <item><term>/health hero + breakdown</term>
    ///         <description><see cref="ServerCoverage"/> for the hero sub-line, the same two
    ///         dimension methods for the rows.</description></item>
    ///   <item><term>trend chips (/health hero, /server-comparison card)</term>
    ///         <description><see cref="TrendValue"/> for the label and <see cref="TrendBasis"/> for
    ///         the sentence/tooltip, both over
    ///         <see cref="ExecutiveHealthScore.MeasuredTrend"/>, which is null unless a real
    ///         comparison happened (SR-11, 2026-08-08).</description></item>
    ///   <item><term>portal daily-summary blob</term>
    ///         <description><see cref="MayPublish"/>; false in every state except Assessed, so the
    ///         health block is OMITTED rather than filled with a placeholder.</description></item>
    /// </list>
    /// </summary>
    public static class EstateHealthPolicy
    {
        // ── Classification ───────────────────────────────────────────────────

        /// <summary>
        /// The four-state read of one server. A null score is a server nothing has been
        /// collected for, which is <see cref="HealthAssessmentState.NotAssessed"/> and not a
        /// fault: the caller never got as far as a fault.
        /// </summary>
        public static HealthAssessmentState Classify(ExecutiveHealthScore? score)
            => score?.State ?? HealthAssessmentState.NotAssessed;

        /// <summary>
        /// True only when a real measurement exists to publish. Every other state omits the
        /// block. This is the publisher's guard, lifted out of the publisher so the guard and
        /// the on-screen sentences cannot drift apart.
        /// </summary>
        public static bool MayPublish(ExecutiveHealthScore? score)
            => Classify(score) == HealthAssessmentState.Assessed;

        // ── Estate rollup ────────────────────────────────────────────────────

        /// <summary>
        /// Counts each state across the estate and takes the equal-weight mean over the ASSESSED
        /// servers only. Nothing else contributes: an unreachable server has no score to average,
        /// and averaging its placeholder is precisely the defect this wave removed.
        /// </summary>
        public static EstateHealthRollup Summarise(IEnumerable<ExecutiveHealthScore?>? scores)
        {
            var rollup = new EstateHealthRollup();
            if (scores == null) return rollup;

            var assessed = new List<int>();
            foreach (var s in scores)
            {
                rollup.ServerCount++;
                switch (Classify(s))
                {
                    case HealthAssessmentState.Assessed:
                        rollup.AssessedCount++;
                        assessed.Add(s!.Score);
                        break;
                    case HealthAssessmentState.Unreachable:
                        rollup.UnreachableCount++;
                        break;
                    case HealthAssessmentState.Unknown:
                        rollup.UnknownCount++;
                        break;
                    default:
                        rollup.NotAssessedCount++;
                        break;
                }
            }

            if (assessed.Count > 0)
            {
                var mean = (int)Math.Round(assessed.Average(), MidpointRounding.AwayFromZero);
                rollup.MeanScore = mean;
                rollup.Severity = ScoreToSeverity(mean);
            }

            return rollup;
        }

        /// <summary>
        /// Band thresholds, kept identical to <c>ExecutiveHealthService.ScoreToSeverity</c> so the
        /// estate mean and a single server's score never band differently for the same number.
        /// </summary>
        public static HealthSeverity ScoreToSeverity(int score)
            => score >= 71 ? HealthSeverity.Healthy
             : score >= 51 ? HealthSeverity.Warning
             : HealthSeverity.Critical;

        // ── Sentences ────────────────────────────────────────────────────────

        /// <summary>
        /// The /cio estate caption. Opens with what the headline number actually is (or that
        /// there is no headline number), then names every server the number does not cover and
        /// why. Each clause is emitted only when its own count is non-zero, so no clause can
        /// describe a population that was not observed.
        /// </summary>
        public static string EstateBasis(EstateHealthRollup? rollup)
        {
            // Gate fix R4 (2026-08-05). These two used to share a sentence, and they are not the
            // same fact. A null rollup is a caller that never got a rollup: the surrounding pass
            // threw before Summarise ran, so the estate was never counted. Saying "no servers are
            // configured" there tells a reader their configuration is empty when what actually
            // happened is that the count failed, and the remedies point in opposite directions.
            if (rollup == null)
                return "The estate was not summarised on this pass, so nothing can be said about "
                     + "its coverage. This is a failure to count, not a count of zero.";

            if (rollup.ServerCount == 0)
                return "No servers are configured, so nothing has been measured.";

            var parts = new List<string>();
            var anyObserved = rollup.AssessedCount > 0
                              || rollup.UnreachableCount > 0
                              || rollup.UnknownCount > 0;

            if (rollup.AssessedCount > 0)
                parts.Add($"Equal-weight mean across {rollup.AssessedCount} assessed "
                          + $"{Plural(rollup.AssessedCount, "server")}.");
            else if (anyObserved)
                parts.Add("No score is shown because no server was measured.");
            else
                parts.Add("Nothing has been assessed yet, so no score is shown.");

            if (rollup.UnreachableCount > 0)
                parts.Add($"{rollup.UnreachableCount} {Plural(rollup.UnreachableCount, "server")} did not "
                          + $"answer and {Is(rollup.UnreachableCount)} not in the mean.");

            if (rollup.UnknownCount > 0)
                parts.Add($"Health collection failed for {rollup.UnknownCount} "
                          + $"{Plural(rollup.UnknownCount, "server")}, so nothing is claimed about "
                          + $"{(rollup.UnknownCount == 1 ? "it" : "them")}.");

            // Only when the opening clause did not already say it: with nothing observed at all,
            // the opening line IS the not-assessed sentence and repeating it would double-count.
            if (rollup.NotAssessedCount > 0 && anyObserved)
                parts.Add($"{rollup.NotAssessedCount} {Plural(rollup.NotAssessedCount, "server")} "
                          + $"{Has(rollup.NotAssessedCount)} not been assessed yet.");

            return string.Join(" ", parts);
        }

        /// <summary>
        /// The /dba per-server card sentence, printed in place of the dimension rows when no
        /// dimension was measured. Names the observation that produced the emptiness.
        /// </summary>
        public static string ServerBasis(ExecutiveHealthScore? score) => Classify(score) switch
        {
            HealthAssessmentState.Assessed => "",
            HealthAssessmentState.Unreachable =>
                "This server did not answer, so no dimension was measured.",
            HealthAssessmentState.Unknown =>
                "Health collection failed for this server, so no dimension was measured.",
            _ => "No dimension has been collected for this server yet.",
        };

        /// <summary>
        /// The /health hero sub-line. When something was measured it says how much of the score
        /// is covered by measurement, and names the unmeasured dimensions by the reason they are
        /// unmeasured, so "3 of 5" never has to be read as "the other 2 are fine".
        /// Empty string when all five were measured, because then there is nothing to qualify.
        /// </summary>
        public static string ServerCoverage(ExecutiveHealthScore? score)
        {
            var state = Classify(score);
            if (state != HealthAssessmentState.Assessed)
                return ServerBasis(score);

            var dims = score!.Breakdown.Dimensions.ToList();
            var measured = dims.Count(d => d.State == DimensionState.Measured);
            var total = dims.Count;
            if (measured == total) return "";

            var parts = new List<string>
            {
                $"Indicative: {measured} of {total} dimensions measured."
            };

            var unreachable = dims.Where(d => d.State == DimensionState.Unreachable)
                                  .Select(d => d.Name).ToList();
            var failed = dims.Where(d => d.State == DimensionState.CollectionFailed)
                             .Select(d => d.Name).ToList();
            var notAssessed = dims.Where(d => d.State == DimensionState.NotAssessed)
                                  .Select(d => d.Name).ToList();

            if (unreachable.Count > 0)
                parts.Add($"{Join(unreachable)} could not be measured because the server did not answer.");
            if (failed.Count > 0)
                parts.Add($"Collecting {Join(failed)} failed.");
            if (notAssessed.Count > 0)
                parts.Add($"{Join(notAssessed)} {Has(notAssessed.Count)} no data collected yet.");

            parts.Add("Unmeasured dimensions are excluded from the score, not assumed healthy.");
            return string.Join(" ", parts);
        }

        // ── Trend ────────────────────────────────────────────────────────────

        /// <summary>
        /// What a trend chip prints where a direction would go. "No trend yet" in every state where
        /// nothing was compared, because <see cref="ExecutiveHealthScore.Trend"/> holds
        /// <c>Stable</c> there and printing it is the defect (SR-11: /health rendered the literal word
        /// "Stable" under an equals icon for a server with no history at all). Paired with
        /// <see cref="TrendBasis"/>, which says which kind of nothing it is.
        /// </summary>
        public static string TrendValue(ExecutiveHealthScore? score)
            => score?.MeasuredTrend?.ToString() ?? "No trend yet";

        /// <summary>
        /// The sentence beside a trend chip — the tooltip on /health, the cell text on
        /// /server-comparison. Conditioned on the same two measurements
        /// <see cref="ExecutiveHealthScore.MeasuredTrend"/> is: a score today and a snapshot to
        /// compare it against. The old tooltip read "Trend vs yesterday" unconditionally, which is a
        /// claim about a yesterday, made on a page that had never seen one.
        /// </summary>
        public static string TrendBasis(ExecutiveHealthScore? score)
        {
            if (score == null)
                return "No score has been collected for this server, so nothing has been compared.";

            // "yesterday" is exact, not loose: ExecutiveHealthService.GetTrendAsync compares against a
            // snapshot dated yesterday and nothing else — not today's own row (which it used to find),
            // and not a snapshot from last week.
            if (score.MeasuredTrend.HasValue)
                return "Compared with yesterday's snapshot for this server.";

            // Today has no number, so there is nothing to compare FROM — whatever the history holds.
            // Ordered first because it outranks the history question entirely.
            if (!score.IsAssessed)
                return ServerBasis(score) + " Nothing has been compared.";

            return score.TrendState switch
            {
                HealthTrendState.LookupFailed =>
                    "Reading this server's score history failed, so nothing has been compared.",
                _ =>
                    "No earlier snapshot for this server yet — a trend needs a second measurement "
                    + "to compare against.",
            };
        }

        // ── Per-dimension ────────────────────────────────────────────────────

        /// <summary>
        /// What a dimension row prints where a number would go. "n/a" in every unmeasured state,
        /// because <c>DimensionScore.Score</c> holds a placeholder there and printing it is the
        /// defect. Paired with <see cref="DimensionBasis"/>, which says which kind of n/a it is.
        /// </summary>
        public static string DimensionValue(DimensionScore? dim)
            => dim != null && dim.State == DimensionState.Measured ? $"{dim.Score}/100" : "n/a";

        /// <summary>
        /// The explanation beside a dimension row. Conditioned on the dimension's own state, so
        /// an unreachable dimension is never described as "no data collected yet" (which reads as
        /// a to-do the reader could clear by running checks, when in fact the server was silent).
        /// </summary>
        public static string DimensionBasis(DimensionScore? dim)
        {
            if (dim == null) return "";
            return dim.State switch
            {
                DimensionState.Measured => dim.Tooltip,
                DimensionState.Unreachable =>
                    $"{dim.Name}: the server did not answer, so this was not measured. "
                    + "Excluded from the score, not assumed healthy.",
                DimensionState.CollectionFailed =>
                    $"{dim.Name}: collection failed, so this was not measured. "
                    + "Excluded from the score, not assumed healthy.",
                _ =>
                    $"{dim.Name}: no data collected yet. "
                    + "Excluded from the score, not assumed healthy.",
            };
        }

        // ── Grammar helpers ──────────────────────────────────────────────────

        private static string Plural(int n, string noun) => n == 1 ? noun : noun + "s";
        private static string Is(int n) => n == 1 ? "is" : "are";
        private static string Has(int n) => n == 1 ? "has" : "have";

        private static string Join(IReadOnlyList<string> names) => names.Count switch
        {
            0 => "",
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1],
        };
    }

    /// <summary>
    /// A counted read of the estate. Every field is a tally of observed states; nothing here is
    /// inferred. <see cref="MeanScore"/> and <see cref="Severity"/> are null when nothing was
    /// measured, rather than 0 and Critical, which is what a forced int used to produce.
    /// </summary>
    public sealed class EstateHealthRollup
    {
        /// <summary>Servers looked at, whatever came back.</summary>
        public int ServerCount { get; set; }
        /// <summary>Servers with at least one measured dimension. These, and only these, form the mean.</summary>
        public int AssessedCount { get; set; }
        /// <summary>Servers with nothing collected yet.</summary>
        public int NotAssessedCount { get; set; }
        /// <summary>Servers that did not answer.</summary>
        public int UnreachableCount { get; set; }
        /// <summary>Servers whose health collection threw.</summary>
        public int UnknownCount { get; set; }
        /// <summary>Equal-weight mean over the assessed servers, or null when there are none.</summary>
        public int? MeanScore { get; set; }
        /// <summary>Band of <see cref="MeanScore"/>, or null when there is no mean to band.</summary>
        public HealthSeverity? Severity { get; set; }
        /// <summary>True when there is a headline number the caller may render.</summary>
        public bool HasMean => MeanScore.HasValue;
    }
}
