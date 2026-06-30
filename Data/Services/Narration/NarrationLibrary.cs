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
            [ScoreBucket.Unknown] = new[]
            {
                "Nothing has been assessed yet — run a Vulnerability Assessment and I'll tell you where this estate stands.",
                "There's no assessment data to read yet. Run a scan and I'll give you the lay of the land.",
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

        // Strong AND genuinely clean (zero failures) — the composer uses these instead of the
        // Strong dict entry when FailedCount == 0, so a flawless estate never reads "0 to tidy up".
        public static readonly string[] HeadlineStrongClean =
        {
            "This estate looks to be in strong shape — nothing is actively failing.",
            "On these numbers the estate is in good order; there's very little to chase here.",
            "This is a well-kept estate — every check is passing.",
        };

        // ── Slot 2: weakest dimension pointer. {dim} {passed} {total} ──
        public static readonly string[] Weakest =
        {
            "Your weakest area is {dim} — {passed} of {total} checks passing there.",
            "The thinnest dimension is {dim}, with {passed} of {total} passing.",
            "If you fix one thing, start with {dim} — only {passed} of {total} are clean.",
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
