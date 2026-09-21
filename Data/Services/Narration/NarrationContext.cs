/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Services.Narration
{
    /// <summary>
    /// The structured situation a narration is composed from — pure data, no prose. Every field
    /// is a fact derived from real assessment state; the library only ever phrases what is here,
    /// so it cannot assert anything the data doesn't carry (the honesty contract, structurally).
    /// </summary>
    public sealed class NarrationContext
    {
        /// <summary>0–100 overall posture. -1 when nothing assessed.</summary>
        public double OverallScore { get; init; } = -1;

        public int FailedCount { get; init; }
        public int PassedCount { get; init; }
        public int TotalChecks { get; init; }

        /// <summary>Checks that ran (passed + failed). 0 ⇒ nothing assessed.</summary>
        public int Evaluated => PassedCount + FailedCount;

        /// <summary>Checks defined but not yet run — drives the partial-coverage caveat.</summary>
        public int NotAssessed => System.Math.Max(0, TotalChecks - Evaluated);

        /// <summary>True when this is an indicative (partial) read rather than a full audit.</summary>
        public bool IsIndicative { get; init; }

        /// <summary>Weakest dimension by pass-rate (human label + counts). Null when unknown.</summary>
        public DimensionFact? Weakest { get; init; }

        /// <summary>Strongest dimension. Null when unknown.</summary>
        public DimensionFact? Strongest { get; init; }

        /// <summary>Direction vs the last assessment, when history exists. None when single-point.</summary>
        public TrendDirection Trend { get; init; } = TrendDirection.None;

        /// <summary>Net checks moved Fail→Pass since the comparison point (F-VAL). 0 when no history.</summary>
        public int NetImproved { get; init; }

        /// <summary>Checks that regressed Pass→Fail (always surfaced — honesty over flattery).</summary>
        public int Regressed { get; init; }

        /// <summary>The audience framing (flavour seam) — drives which phrasing register is used.</summary>
        public NarrationAudience Audience { get; init; } = NarrationAudience.Neutral;

        /// <summary>Single servers vs an estate roll-up — changes pronouns/scope wording.</summary>
        public bool IsEstate { get; init; }
        public int ServerCount { get; init; }

        /// <summary>
        /// Gate fix C2 (2026-08-05). Servers with a MEASURED Executive Health score on the same
        /// screen as this narration, straight from <c>EstateHealthRollup.AssessedCount</c>.
        ///
        /// Why the narration needs it: the headline bucket is keyed to the GOVERNANCE score, and
        /// a governance score with nothing evaluated buckets Unknown. On /cio that printed
        /// "Nothing has been assessed yet" directly above an Executive Health card reading a mean
        /// across N assessed servers, and directly above the estate caption that names those N.
        /// The narration was not wrong about governance; it was answering for the whole page.
        ///
        /// Zero (the default) preserves the old behaviour exactly, for callers with no estate
        /// rollup of their own beside them. Nothing here is inferred: it is the same count the
        /// caption is composed from, on the same pass over the same dictionary.
        /// </summary>
        public int EstateAssessedCount { get; init; }

        /// <summary>Servers the rollup looked at, whatever came back. Companion to
        /// <see cref="EstateAssessedCount"/>; 0 when the caller has no rollup.</summary>
        public int EstateServerCount { get; init; }

        /// <summary>Score band buckets, the primary phrasing key.</summary>
        public ScoreBucket Bucket => OverallScore switch
        {
            < 0    => ScoreBucket.Unknown,
            >= 85  => ScoreBucket.Strong,
            >= 70  => ScoreBucket.Healthy,
            >= 50  => ScoreBucket.NeedsAttention,
            >= 1   => ScoreBucket.Poor,
            _      => ScoreBucket.Critical,
        };
    }

    /// <summary>A dimension's standing — label + the counts that justify any phrasing about it.</summary>
    public sealed class DimensionFact
    {
        public string Label { get; init; } = "this area";
        public int Passed { get; init; }
        public int Total { get; init; }

        /// <summary>
        /// 2026-07-21 (persona board, FIX 3): the dimension's RENDERED score — the very number
        /// /cio draws in its mini-ring. Added because the narration quoted raw check counts
        /// ("Compliance — 26 of 38 checks passing", 68%) beside a ring reading 45%, and the two
        /// are different quantities: the ring is weighted by ScoreWeight x EffortHours
        /// (GovernanceService.RawScore) while the counts are unweighted. Two figures for one
        /// dimension, side by side, neither explaining the other.
        ///
        /// Carrying the score here lets the sentence state BOTH and say why they differ, and lets
        /// "weakest" be chosen by the same quantity the rings rank by — before this, the sentence
        /// could name a dimension that was not the lowest ring on screen.
        /// </summary>
        public double Score { get; init; } = -1;

        public bool FullyClean => Total > 0 && Passed == Total;
    }

    public enum ScoreBucket { Unknown, Critical, Poor, NeedsAttention, Healthy, Strong }
    public enum TrendDirection { None, Improving, Steady, Worsening }
    public enum NarrationAudience { Neutral, ComplianceExec, MspDeliverable, OpsContinuity }
}
