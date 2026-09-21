/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data;

/// <summary>
/// FI-2 micro-nit (2026-07-13): shared trend-period pluralization for CIO/DBA dashboards.
/// The prior inline expression (days/30 &gt; 0 ? "months" : "days") always used the plural
/// form regardless of count, producing "1 days" / "1 months". Extracted so both call sites
/// share one correctly-pluralized implementation and it can be unit tested directly.
/// </summary>
public static class DashboardTrendFormatter
{
    /// <summary>
    /// Formats an elapsed day count as a human trend period, singular/plural correct:
    /// 1 day, 2 days, 1 month, 2 months. Uses whole months (days/30) above a 30-day span,
    /// matching the original threshold.
    /// </summary>
    public static string FormatTrendPeriod(int days)
    {
        if (days > 30)
        {
            var months = days / 30;
            return months == 1 ? "1 month" : $"{months} months";
        }

        return days == 1 ? "1 day" : $"{days} days";
    }

    /// <summary>
    /// FI-2 micro-nit (2026-07-13): true when a trend delta is large enough to render as a
    /// directional change. Mirrors the CIO Dashboard's own TrendGlyph ±0.05 threshold — the
    /// render guard used to fire on any nonzero delta, so a 0.01 movement showed the flat "▬"
    /// glyph next to "0.0 points" (an honest-but-ugly zero). Deltas below this threshold route
    /// to the "no material change" branch instead.
    /// </summary>
    public static bool IsMaterialTrendDelta(double delta) => Math.Abs(delta) >= 0.05;

    /// <summary>
    /// Polish (2026-07-13): the directional glyph for a trend delta, using the SAME >= 0.05
    /// threshold as <see cref="IsMaterialTrendDelta"/> (was a strict &gt;/&lt; here, which let
    /// exactly ±0.05 pass the render guard above as "material" while this still evaluated to
    /// the flat shape — producing the contradictory "▬ 0.1 points over N days").
    /// </summary>
    public static string TrendGlyph(double delta) =>
        delta >= 0.05 ? "▲" : delta <= -0.05 ? "▼" : "▬";
}
