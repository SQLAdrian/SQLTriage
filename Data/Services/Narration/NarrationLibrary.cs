/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;

namespace SQLTriage.Data.Services.Narration
{
    /// <summary>
    /// The captured-eloquence corpus: for each situation slot, a SET of vetted phrasings. The
    /// composer picks one deterministically (per-estate-state seed) so the read is stable but
    /// not robotic. Phrasings use {tokens} filled from NarrationContext — they never embed a raw
    /// claim; escalation is keyed to the bucket, hedging is built in.
    ///
    /// THIS FILE IS THE VOICE. Adding phrasings here enriches the narration everywhere it's used,
    /// offline, with no model call. Author conservatively: every line must be true whenever its
    /// slot fires. Keep the honesty contract (see PRODUCT-ROADMAP §5b): observations not claims,
    /// hedge precision, attribute to a standard, never prophesy, always allow regressions.
    /// </summary>
    public static class NarrationLibrary
    {
        // ── Slot 1: overall posture headline, keyed by ScoreBucket ──
        // {failed} = failed count. Phrasings hedge ("looks", "on these numbers") by design.
        public static readonly IReadOnlyDictionary<ScoreBucket, string[]> Headline = new Dictionary<ScoreBucket, string[]>
        {
            // 2026-08-05, gate fix C2. Two things were wrong with this bucket.
            //
            // The PRESCRIPTION named a Vulnerability Assessment. Executive Health has not been fed
            // by VA since the 2026-07-16 ruling (ScoreSecurity/ScoreCompliance never emit
            // DimensionSource.VulnerabilityAssessment, and VA results are shown only on their own
            // page), so running one would not have moved the number the sentence was talking
            // about. The identical sentence was deleted from the PDF earlier in this same wave,
            // citing that ruling, and this sibling was left standing. It names the check suite now,
            // which is what actually feeds the score.
            //
            // The CLAIM, "nothing has been assessed yet", is only true when nothing on the page was
            // measured. This bucket is keyed to the governance score, so on /cio it printed above
            // an Executive Health card carrying a real mean. HeadlineUnknownBesideAMeasuredMean
            // below carries that case; the composer picks between them on the estate rollup's own
            // assessed count.
            [ScoreBucket.Unknown] = new[]
            {
                "Nothing has been assessed yet. Run the check suite and I'll tell you where this estate stands.",
                "There's no assessment data to read yet. Run the check suite and I'll give you the lay of the land.",
            },
            // Strong-bucket headlines for when some checks STILL fail (a weighted-strong score can
            // carry failing low-weight checks). These must report {failed}, never assert zero.
            [ScoreBucket.Strong] = new[]
            {
                "This estate looks to be in strong shape — {failed} to tidy up, but the fundamentals are solid.",
                "On these numbers the estate is in good order, with {failed} left to chase.",
                "This is a well-kept estate — the fundamentals are holding, with {failed} outstanding.",
            },
            [ScoreBucket.Healthy] = new[]
            {
                "This estate looks reasonably healthy, though there's real work to do — {failed} to clear.",
                "Broadly in good shape, but not finished — {failed} still want attention.",
                "A fundamentally sound estate with {failed} on the list to work through.",
            },
            [ScoreBucket.NeedsAttention] = new[]
            {
                "This estate looks like it needs attention — {failed} currently failing.",
                "There's meaningful work outstanding here — {failed} are failing right now.",
                "On these numbers the estate is middling — {failed} to address before it drifts.",
            },
            [ScoreBucket.Poor] = new[]
            {
                "On these numbers the estate is in poor health — {failed} failing. It would be worth prioritising time here.",
                "This estate is under-maintained — {failed} failing. I'd carve out dedicated time for it.",
                "There's a real backlog of risk here — {failed} failing — and it warrants a focused effort.",
            },
            [ScoreBucket.Critical] = new[]
            {
                "On these numbers the estate is in serious trouble — this reads as a 'stop and fix' situation rather than one to schedule for later.",
                "This is critical territory — {failed} failing. I'd treat this as hands-on-now, not next-sprint.",
                "The estate is in a bad way on these numbers; this needs immediate, focused remediation.",
            },
        };

        // Governance has nothing evaluated, AND the caller has a MEASURED Executive Health mean on
        // the same screen. Says which of the two is missing rather than declaring the page empty.
        // {estateAssessed} is the rollup's own assessed count, the same one the caption prints.
        //
        // 2026-08-06: both phrasings used to close on a PAGE-LEVEL absolute ("the only measured
        // figure on the page" / "and nothing else on this page"). The composer is handed a
        // governance score and an estate rollup; it cannot see the rest of the page, and /cio
        // carries live findings counts, per-server heatmap cells and trend sparklines that are
        // measured figures too. Each sentence now stops at the claim its own inputs support.
        public static readonly string[] HeadlineUnknownBesideAMeasuredMean =
        {
            "No governance checks have run yet, so I can't read this estate's governance posture. The Executive Health mean beside this covers {estateAssessed}.",
            "Governance hasn't been assessed yet. What is measured here is the Executive Health mean across {estateAssessed}.",
        };

        // Strong AND genuinely clean (zero failures) — the composer uses these instead of the
        // Strong dict entry when FailedCount == 0, so a flawless estate never reads "0 to tidy up".
        public static readonly string[] HeadlineStrongClean =
        {
            "This estate looks to be in strong shape — nothing is actively failing.",
            "On these numbers the estate is in good order; there's very little to chase here.",
            "This is a well-kept estate — every check is passing.",
        };

        // ── Slot 2: weakest dimension pointer. {dim} {score} {passed} {total} ──
        // 2026-07-21 (persona board, FIX 3): {score} added. These phrasings quoted only the raw
        // counts, which land on the reader as a percentage (26 of 38 = 68%) directly beside a ring
        // rendering the WEIGHTED score (45%). Same dimension, two numbers, no explanation — one of
        // the "too many numbers that disagree" complaints. Each phrasing now states the ring's own
        // figure first, then the counts that sit underneath it, and names the difference. Nothing
        // new is computed: both values were already on the screen.
        public static readonly string[] Weakest =
        {
            "Your weakest area is {dim}, scoring {score} of 100 — {passed} of {total} checks pass there (the score weights each check by risk and remediation effort, so it isn't the plain pass rate).",
            "The thinnest dimension is {dim} at {score} of 100, from {passed} of {total} checks passing (weighted by risk and effort, so it reads lower than the raw ratio).",
            "If you fix one thing, start with {dim} — it scores {score} of 100 on {passed} of {total} checks clean, weighted by risk and remediation effort.",
        };

        // ── Slot 3: a genuine positive — ONLY emitted when a dimension is fully clean ──
        public static readonly string[] Strength =
        {
            "On the upside, {dim} is fully clean.",
            "Credit where due — {dim} is passing across the board.",
            "{dim} is in good order, every check passing.",
        };

        // ── Slot 4: trend (only when history exists). {improved} {regressed} ──
        public static readonly IReadOnlyDictionary<TrendDirection, string[]> Trend = new Dictionary<TrendDirection, string[]>
        {
            [TrendDirection.Improving] = new[]
            {
                "Since the last look it's moving the right way — {improved} checks have gone from failing to passing.",
                "The direction is good: {improved} fixed since the previous assessment.",
            },
            [TrendDirection.Steady] = new[]
            {
                "It's holding steady against the last assessment — little net change.",
            },
            [TrendDirection.Worsening] = new[]
            {
                "Heads up — it's slipped since the last look, with {regressed} checks newly failing.",
                "The trend is the wrong way: {regressed} have regressed to failing since last time.",
            },
        };

        // ── Slot 4b: regression honesty rider (appended when there ARE regressions, even if net-positive) ──
        public static readonly string[] RegressionRider =
        {
            " That said, {regressed} have slipped back to failing — worth a look so the progress sticks.",
            " Note {regressed} new failures crept in too — don't let those undo the gains.",
        };

        // ── Slot 5: partial-coverage caveat. {notAssessed} ──
        public static readonly string[] CoveragePartial =
        {
            "Bear in mind this is a partial picture — {notAssessed} checks haven't run yet, so a Full Audit may shift this.",
            "This is an indicative read — {notAssessed} checks are still outstanding; a full audit could move the number.",
        };

        public static readonly string[] CoverageIndicative =
        {
            "Note this is an indicative read, not a full audit.",
        };
    }
}
