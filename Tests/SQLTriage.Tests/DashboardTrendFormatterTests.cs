/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data;

namespace SQLTriage.Tests;

public class DashboardTrendFormatterTests
{
    // FI-2 micro-nit (2026-07-13): the original inline expression always pluralized
    // ("1 days" / "1 months"). These pin the singular/plural boundary in both units.

    [Fact]
    public void FormatTrendPeriod_OneDay_ReturnsSingular()
        => Assert.Equal("1 day", DashboardTrendFormatter.FormatTrendPeriod(1));

    [Fact]
    public void FormatTrendPeriod_ZeroDays_ReturnsPluralDays()
        => Assert.Equal("0 days", DashboardTrendFormatter.FormatTrendPeriod(0));

    [Fact]
    public void FormatTrendPeriod_MultipleDays_ReturnsPlural()
        => Assert.Equal("5 days", DashboardTrendFormatter.FormatTrendPeriod(5));

    [Fact]
    public void FormatTrendPeriod_ThirtyDays_StaysInDaysUnit()
        => Assert.Equal("30 days", DashboardTrendFormatter.FormatTrendPeriod(30));

    [Fact]
    public void FormatTrendPeriod_ThirtyOneDays_CrossesToOneMonth()
        => Assert.Equal("1 month", DashboardTrendFormatter.FormatTrendPeriod(31));

    [Fact]
    public void FormatTrendPeriod_SixtyDays_ReturnsTwoMonthsPlural()
        => Assert.Equal("2 months", DashboardTrendFormatter.FormatTrendPeriod(60));

    [Fact]
    public void FormatTrendPeriod_NinetyDays_ReturnsThreeMonthsPlural()
        => Assert.Equal("3 months", DashboardTrendFormatter.FormatTrendPeriod(90));

    // FI-2 micro-nit (2026-07-13): the trend-delta render guard predicate — mirrors
    // TrendGlyph's own ±0.05 threshold so a sub-threshold nonzero delta never renders
    // the flat glyph next to a rounded "0.0 points".

    [Fact]
    public void IsMaterialTrendDelta_ExactlyZero_ReturnsFalse()
        => Assert.False(DashboardTrendFormatter.IsMaterialTrendDelta(0.0));

    [Fact]
    public void IsMaterialTrendDelta_JustBelowThreshold_ReturnsFalse()
        => Assert.False(DashboardTrendFormatter.IsMaterialTrendDelta(0.04));

    [Fact]
    public void IsMaterialTrendDelta_NegativeJustBelowThreshold_ReturnsFalse()
        => Assert.False(DashboardTrendFormatter.IsMaterialTrendDelta(-0.04));

    [Fact]
    public void IsMaterialTrendDelta_AtThreshold_ReturnsTrue()
        => Assert.True(DashboardTrendFormatter.IsMaterialTrendDelta(0.05));

    [Fact]
    public void IsMaterialTrendDelta_NegativeAtThreshold_ReturnsTrue()
        => Assert.True(DashboardTrendFormatter.IsMaterialTrendDelta(-0.05));

    [Fact]
    public void IsMaterialTrendDelta_WellAboveThreshold_ReturnsTrue()
        => Assert.True(DashboardTrendFormatter.IsMaterialTrendDelta(0.8));

    // Polish (2026-07-13): TrendGlyph must agree with IsMaterialTrendDelta at the exact
    // ±0.05 boundary — a strict >/< here previously let ±0.05 pass the render guard as
    // "material" while still evaluating to the flat "▬" shape ("▬ 0.1 points").

    [Fact]
    public void TrendGlyph_AtThreshold_ReturnsUpArrow()
        => Assert.Equal("▲", DashboardTrendFormatter.TrendGlyph(0.05));

    [Fact]
    public void TrendGlyph_NegativeAtThreshold_ReturnsDownArrow()
        => Assert.Equal("▼", DashboardTrendFormatter.TrendGlyph(-0.05));

    [Fact]
    public void TrendGlyph_JustBelowThreshold_ReturnsFlat()
        => Assert.Equal("▬", DashboardTrendFormatter.TrendGlyph(0.04));

    [Fact]
    public void TrendGlyph_NegativeJustBelowThreshold_ReturnsFlat()
        => Assert.Equal("▬", DashboardTrendFormatter.TrendGlyph(-0.04));

    [Fact]
    public void TrendGlyph_ExactlyZero_ReturnsFlat()
        => Assert.Equal("▬", DashboardTrendFormatter.TrendGlyph(0.0));
}
