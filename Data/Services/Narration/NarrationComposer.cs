/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Text;

namespace SQLTriage.Data.Services.Narration
{
    /// <summary>
    /// Assembles a plain-language narration from a NarrationContext using the captured-eloquence
    /// NarrationLibrary. Offline, deterministic, honest-by-construction:
    ///   • Each slot fires ONLY when its facts justify it (no claim without backing data).
    ///   • Phrasing is chosen by a per-context seed, so the same estate state always reads the
    ///     same words (a CFO seeing different prose each refresh would distrust it) — yet two
    ///     different estates won't sound identical.
    ///   • Regressions are surfaced whenever they exist, even mid-improvement (honesty rider).
    /// No model call. This is how the assistant's voice travels to an air-gapped client box.
    /// </summary>
    public static class NarrationComposer
    {
        public static string Compose(NarrationContext ctx)
        {
            if (ctx.Bucket == ScoreBucket.Unknown || ctx.Evaluated == 0)
                return Pick(NarrationLibrary.Headline[ScoreBucket.Unknown], Seed(ctx));

            var seed = Seed(ctx);
            var sb = new StringBuilder();

            // Slot 1 — headline (always). A Strong score can still carry failing low-weight
            // checks, so only use the "nothing failing" clean phrasings when failures are truly 0.
            var headlineOpts = (ctx.Bucket == ScoreBucket.Strong && ctx.FailedCount == 0)
                ? NarrationLibrary.HeadlineStrongClean
                : NarrationLibrary.Headline[ctx.Bucket];
            sb.Append(Fill(Pick(headlineOpts, seed), ctx));

            // Slot 4 — trend (only when history gives a direction).
            if (ctx.Trend != TrendDirection.None &&
                NarrationLibrary.Trend.TryGetValue(ctx.Trend, out var trendOpts))
            {
                sb.Append(' ').Append(Fill(Pick(trendOpts, seed + 7), ctx));
                // Honesty rider: if improving overall but some regressed, say so.
                if (ctx.Trend == TrendDirection.Improving && ctx.Regressed > 0)
                    sb.Append(Fill(Pick(NarrationLibrary.RegressionRider, seed + 11), ctx));
            }

            // Slot 2 — weakest dimension (only when we have one with findings).
            if (ctx.Weakest is { Total: > 0 })
                sb.Append(' ').Append(Fill(Pick(NarrationLibrary.Weakest, seed + 3), ctx));

            // Slot 3 — a genuine positive (only when a DIFFERENT dimension is fully clean).
            if (ctx.Strongest is { } s && s.FullyClean &&
                (ctx.Weakest is null || !string.Equals(s.Label, ctx.Weakest.Label, StringComparison.OrdinalIgnoreCase)))
                sb.Append(' ').Append(FillStrength(Pick(NarrationLibrary.Strength, seed + 5), ctx));

            // Slot 5 — coverage caveat (only when the read is indicative).
            if (ctx.IsIndicative)
            {
                if (ctx.NotAssessed > 0)
                    sb.Append(' ').Append(Fill(Pick(NarrationLibrary.CoveragePartial, seed + 13), ctx));
                else
                    sb.Append(' ').Append(Pick(NarrationLibrary.CoverageIndicative, seed + 13));
            }

            return sb.ToString();
        }

        // ── token fill: headline/weakest/trend slots ({dim} here = the WEAKEST dimension) ──
        private static string Fill(string template, NarrationContext ctx)
        {
            var s = template
                .Replace("{failed}", Count(ctx.FailedCount, ctx.FailedCount == 1 ? "open finding" : "open findings"))
                .Replace("{notAssessed}", ctx.NotAssessed.ToString())
                .Replace("{improved}", ctx.NetImproved.ToString())
                .Replace("{regressed}", ctx.Regressed.ToString());

            if (ctx.Weakest is { } w)
                s = s.Replace("{dim}", w.Label).Replace("{passed}", w.Passed.ToString()).Replace("{total}", w.Total.ToString());

            return s;
        }

        // ── token fill for the Strength slot, where {dim} = the STRONGEST dimension ──
        private static string FillStrength(string template, NarrationContext ctx) =>
            template.Replace("{dim}", ctx.Strongest?.Label ?? "this area");

        private static string Count(int n, string noun) => $"{n} {noun}";

        // ── deterministic phrasing selection ──
        private static string Pick(string[] options, int seed)
        {
            if (options.Length == 0) return string.Empty;
            return options[(uint)seed % (uint)options.Length];
        }

        /// <summary>
        /// Seed derived from the salient facts — so the same estate state yields the same words,
        /// but different states vary. Deliberately coarse (buckets, not raw scores) so a one-point
        /// score wobble doesn't reword the whole paragraph.
        /// </summary>
        private static int Seed(NarrationContext ctx)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (int)ctx.Bucket;
                h = h * 31 + (int)ctx.Trend;
                h = h * 31 + (ctx.Weakest?.Label.GetHashCode() ?? 0);
                h = h * 31 + (ctx.IsIndicative ? 1 : 0);
                h = h * 31 + (int)ctx.Audience;
                return h & 0x7fffffff;
            }
        }
    }
}
