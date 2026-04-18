<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

using SQLTriage.Data;

namespace SQLTriage.Data.Services;

/// <summary>
/// Contract for theme colour palette and change notifications.
/// </summary>
public interface IChartThemeService
{
    /// <summary>Returns ApexCharts colour series (success, warning, critical, neutral) for the current theme.</summary>
    (string Success, string Warning, string Critical, string Neutral) GetCurrentPalette();

    /// <summary>True if glassmorphism blur is enabled for the current theme.</summary>
    bool IsGlassEnabled { get; }

    /// <summary>Blur amount in pixels (theme-dependent).</summary>
    int GlassBlurPx { get; }

    /// <summary>Fired when the user switches themes so chart components can refresh.</summary>
    event Action? OnChartThemeChanged;
}
