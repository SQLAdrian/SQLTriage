/* In the name of God, the Merciful, the Compassionate */

using ApexCharts;
using SQLTriage.Data;

namespace SQLTriage.Data.Services;

// BM:IChartThemeService.Class — contract for theme colour palette and change notifications
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

    /// <summary>Returns fully configured options for a line chart.</summary>
    ApexChartOptions<T> GetLineOptions<T>(string title, string yAxisLabel) where T : class;

    /// <summary>Returns fully configured options for a donut chart.</summary>
    ApexChartOptions<T> GetDonutOptions<T>(string title) where T : class;

    /// <summary>Returns fully configured options for a horizontal bar chart.</summary>
    ApexChartOptions<T> GetBarOptions<T>(string title, string xAxisLabel) where T : class;
}
