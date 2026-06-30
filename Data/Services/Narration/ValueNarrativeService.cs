/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SQLTriage.Data.Services;

namespace SQLTriage.Data.Services.Narration
{
    /// <summary>
    /// F-VAL — the temporal value engine. Turns P3 baseline transitions into a ranged,
    /// defensible estimate of the remediation effort represented by what's been RESOLVED
    /// (Fail→Pass) since baseline, plus an honest narrative.
    ///
    /// Bound by the Language Contract (HonestyContractTests):
    ///   • The figure is ALWAYS a range (80–120% of summed effort), never a point claim.
    ///   • It is attributed to a standard ("a senior DBA would typically spend"), never a
    ///     timesheet ("your DBA spent" / "hours worked").
    ///   • It LABELS rather than asserts ("it looks like real work went in").
    ///   • Regressions (Pass→Fail) are surfaced alongside progress, always.
    ///   • Effort values are oracle-derived but not yet defensibility-audited per check, so
    ///     the estimate is flagged indicative until that corpus audit lands.
    /// Offline + deterministic: no model call, same inputs → same words.
    /// </summary>
    public sealed class ValueNarrativeService
    {
        // Ranges widen the figure so it's harder to attack than a single number.
        private const double LowFactor = 0.80;
        private const double HighFactor = 1.20;

        private readonly Func<string, double>? _effortResolver;

        /// <param name="effortResolver">
        /// Optional: resolves a checkId to its remediation-effort hours, used to BACKFILL when a
        /// transition's own EffortHours is zero (disk-hydrated results drop effort — see the
        /// effort/IsBad pricing chain). Typically <c>id =&gt; repo.GetCheckById(id)?.EffortHours ?? 0</c>.
        /// </param>
        public ValueNarrativeService(Func<string, double>? effortResolver = null)
        {
            _effortResolver = effortResolver;
        }

        /// <summary>
        /// Computes the value estimate from a P3 transition result. Effort is summed over the
        /// RESOLVED set only (the rock-solid Fail→Pass signal). Returns a zeroed estimate (not
        /// null) when nothing has been resolved, so callers can render a neutral state.
        /// </summary>
        public ValueEstimate Estimate(CheckTransitionResult transitions)
        {
            if (transitions == null) return ValueEstimate.Empty;

            double totalEffort = 0;
            foreach (var c in transitions.Resolved)
                totalEffort += EffortFor(c);

            var lowHours = (int)Math.Floor(totalEffort * LowFactor);
            var highHours = (int)Math.Ceiling(totalEffort * HighFactor);

            return new ValueEstimate
            {
                ResolvedCount = transitions.Resolved.Count,
                RegressedCount = transitions.Regressed.Count,
                TotalEffortHours = totalEffort,
                LowHours = lowHours,
                HighHours = highHours,
                HealthDelta = transitions.HealthDelta,
                IsIndicative = true, // per-check effort defensibility audit still parked
            };
        }

        /// <summary>
        /// Produces the deterministic, contract-compliant narrative for an estimate.
        /// Empty estimate → a neutral "nothing resolved yet" line (never an invented verdict).
        /// </summary>
        public string Narrate(ValueEstimate est)
        {
            if (est == null || est.ResolvedCount == 0 || est.TotalEffortHours <= 0)
                return ValuePhrasings.NothingResolvedYet;

            var seed = Seed(est);
            var sb = new StringBuilder();

            // Slot 1 — the ranged value observation (attributed to a standard, labelled not asserted).
            sb.Append(Fill(Pick(ValuePhrasings.Headline, seed), est));

            // Slot 2 — regression rider, ALWAYS when regressions exist, even mid-progress.
            if (est.RegressedCount > 0)
                sb.Append(' ').Append(Fill(Pick(ValuePhrasings.RegressionRider, seed + 7), est));

            // Slot 3 — indicative caveat (effort values not yet per-check audited).
            if (est.IsIndicative)
                sb.Append(' ').Append(ValuePhrasings.IndicativeCaveat);

            return sb.ToString();
        }

        private double EffortFor(CheckTransition c)
        {
            if (c.EffortHours > 0) return c.EffortHours;
            if (_effortResolver != null && !string.IsNullOrWhiteSpace(c.CheckId))
            {
                try { return Math.Max(0, _effortResolver(c.CheckId)); }
                catch { return 0; }
            }
            return 0;
        }

        // ── token fill ──
        private static string Fill(string template, ValueEstimate est) => template
            .Replace("{low}", est.LowHours.ToString())
            .Replace("{high}", est.HighHours.ToString())
            .Replace("{resolved}", est.ResolvedCount.ToString())
            .Replace("{resolvedNoun}", est.ResolvedCount == 1 ? "finding" : "findings")
            .Replace("{regressed}", est.RegressedCount.ToString())
            .Replace("{regressedNoun}", est.RegressedCount == 1 ? "finding" : "findings");

        private static string Pick(string[] options, int seed)
        {
            if (options.Length == 0) return string.Empty;
            return options[(uint)seed % (uint)options.Length];
        }

        // Coarse seed: same estimate shape → same words, but different estates vary.
        private static int Seed(ValueEstimate est)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + est.ResolvedCount;
                h = h * 31 + est.RegressedCount;
                h = h * 31 + (int)Math.Round(est.TotalEffortHours);
                return h & 0x7fffffff;
            }
        }
    }

    /// <summary>The computed value figure. Hours are ALWAYS presented as a range [Low, High].</summary>
    public sealed class ValueEstimate
    {
        public int ResolvedCount { get; init; }
        public int RegressedCount { get; init; }
        public double TotalEffortHours { get; init; }
        public int LowHours { get; init; }
        public int HighHours { get; init; }
        public int HealthDelta { get; init; }
        public bool IsIndicative { get; init; }

        public static ValueEstimate Empty { get; } = new();
    }

    /// <summary>
    /// Captured-eloquence phrasing for the value narrative. Every line is an OBSERVATION the
    /// data supports — attributed to a standard, ranged, labelled not asserted. Guarded by
    /// HonestyContractTests (forbidden-phrase scan + structure).
    /// </summary>
    public static class ValuePhrasings
    {
        // {low}/{high} = ranged hours, {resolved} = count resolved since baseline.
        public static readonly string[] Headline =
        {
            "Since baseline, {resolved} {resolvedNoun} moved from fail to pass — a senior DBA would typically spend around {low}–{high} hours on work like that, so it looks like real effort went in.",
            "{resolved} {resolvedNoun} have been resolved since baseline. On a typical book rate that's roughly {low}–{high} hours of senior DBA work — it seems like meaningful work landed here.",
            "Work since baseline cleared {resolved} {resolvedNoun}; that's the kind of remediation a senior DBA would usually budget around {low}–{high} hours for.",
        };

        public static readonly string[] RegressionRider =
        {
            " It's not all one direction, though — {regressed} {regressedNoun} regressed (pass → fail) over the same period and would be worth a look.",
            " To keep it honest, {regressed} {regressedNoun} went the other way (pass → fail) since baseline.",
        };

        public const string IndicativeCaveat =
            "These hour figures are indicative estimates drawn from the check library, not a record of time logged.";

        public const string NothingResolvedYet =
            "Nothing has moved from fail to pass since baseline yet, so there's no remediation effort to estimate.";
    }
}
