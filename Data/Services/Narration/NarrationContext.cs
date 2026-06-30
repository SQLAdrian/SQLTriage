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
        public bool FullyClean => Total > 0 && Passed == Total;
    }

    public enum ScoreBucket { Unknown, Critical, Poor, NeedsAttention, Healthy, Strong }
    public enum TrendDirection { None, Improving, Steady, Worsening }
    public enum NarrationAudience { Neutral, ComplianceExec, MspDeliverable, OpsContinuity }
}
