/* In the name of God, the Merciful, the Compassionate */

using ApexCharts;
using SQLTriage.Data.Services;

namespace SQLTriage.Data;

// BM:ChartThemeService.Class — provides ApexCharts colour palettes for each UI theme
/// <summary>
/// Provides ApexCharts colour palettes for each UI theme, and synchronises
/// theme changes to chart components via an event bus.
/// </summary>
public class ChartThemeService : IChartThemeService
{
    private readonly IUserSettingsService _userSettings;
    private string _currentTheme;

    // Theme → (success, warning, critical, neutral) for ApexCharts series
    private static readonly Dictionary<string, (string Success, string Warning, string Critical, string Neutral)> _themePalettes = new()
    {
        ["default"] = ("#4ec9b0", "#ce9178", "#f44747", "#6a6a6a"),
        ["rolls-royce"] = ("#6bbd99", "#e7a978", "#d97b7b", "#8a8a9a"),
        ["amg"] = ("#22c55e", "#f97316", "#dc2626", "#6b7280"),
        // Fallback for cyberpunk legacy settings
        ["cyberpunk"] = ("#4ec9b0", "#ce9178", "#f44747", "#6a6a6a")
    };

    public event Action? OnChartThemeChanged;

    public ChartThemeService(IUserSettingsService userSettings)
    {
        _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        _currentTheme = _userSettings.GetSelectedTheme();
        _userSettings.OnSelectedThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(string theme)
    {
        _currentTheme = theme;
        OnChartThemeChanged?.Invoke();
    }

    /// <summary>
    /// Returns the ApexCharts colour series for the current theme.
    /// </summary>
    public (string Success, string Warning, string Critical, string Neutral) GetCurrentPalette()
    {
        if (_themePalettes.TryGetValue(_currentTheme, out var palette))
            return palette;

        // Unknown theme — fall back to default
        return _themePalettes["default"];
    }

    /// <summary>
    /// Returns true if the current theme enables glassmorphism (blur).
    /// </summary>
    public bool IsGlassEnabled => _currentTheme == "rolls-royce";

    /// <summary>
    /// Returns blur amount in pixels (0 for AMG, 12 for Rolls Royce).
    /// </summary>
    public int GlassBlurPx => _currentTheme == "rolls-royce" ? 12 : 0;

    /// <summary>
    /// Returns fully configured options for a line chart.
    /// </summary>
    public ApexChartOptions<T> GetLineOptions<T>(string title, string yAxisLabel) where T : class
    {
        var palette = GetCurrentPalette();
        return new ApexChartOptions<T>
        {
            Chart = new Chart { Background = "transparent", Toolbar = new Toolbar { Show = false } },
            Colors = new List<string> { palette.Success, palette.Warning, palette.Critical, palette.Neutral },
            Title = new Title { Text = title },
            Yaxis = new List<YAxis> { new YAxis { Title = new AxisTitle { Text = yAxisLabel } } }
        };
    }

    /// <summary>
    /// Returns fully configured options for a donut chart.
    /// </summary>
    public ApexChartOptions<T> GetDonutOptions<T>(string title) where T : class
    {
        var palette = GetCurrentPalette();
        return new ApexChartOptions<T>
        {
            Chart = new Chart { Background = "transparent", Toolbar = new Toolbar { Show = false } },
            Colors = new List<string> { palette.Success, palette.Warning, palette.Critical, palette.Neutral },
            Title = new Title { Text = title }
        };
    }

    /// <summary>
    /// Returns fully configured options for a horizontal bar chart.
    /// </summary>
    public ApexChartOptions<T> GetBarOptions<T>(string title, string xAxisLabel) where T : class
    {
        var palette = GetCurrentPalette();
        return new ApexChartOptions<T>
        {
            Chart = new Chart { Background = "transparent", Toolbar = new Toolbar { Show = false } },
            Colors = new List<string> { palette.Success, palette.Warning, palette.Critical, palette.Neutral },
            Title = new Title { Text = title },
            Xaxis = new XAxis { Title = new AxisTitle { Text = xAxisLabel } },
            PlotOptions = new PlotOptions { Bar = new PlotOptionsBar { Horizontal = true } }
        };
    }
}
