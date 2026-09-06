/* In the name of God, the Merciful, the Compassionate */

using ApexCharts;
using SQLTriage.Data.Services;

namespace SQLTriage.Data;

// BM:ChartThemeService.Class — single source of truth for ApexCharts styling across all chart components
/// <summary>
/// Single source of truth for ApexCharts styling. Provides theme-aware colour
/// palettes plus fully-configured option objects for line, donut and bar charts,
/// so individual chart components no longer hard-code colours, grid, tooltip or
/// legend styling. Restyling charts now happens here, in one place. Theme changes
/// are broadcast to chart components via <see cref="OnChartThemeChanged"/>.
/// </summary>
public class ChartThemeService : IChartThemeService
{
    // Theme personalities were removed — a single fixed palette is the source of truth.
    // (success, warning, critical, neutral) status colours — canonical brand palette.
    private static readonly (string Success, string Warning, string Critical, string Neutral) _statusPalette =
        ("#5BE58C", "#F2B24B", "#FF6B5E", "#66756F");

    // Ordered categorical series palette for multi-series line/area charts, donut
    // slices and distributed bars. Tuned to read against the ink surface stack and
    // led by the signal-green accent (--accent #34D17A), cyan second (--accent-2 #3DD6CB).
    private static readonly List<string> _seriesPalette = new()
        { "#34D17A", "#3DD6CB", "#A78BFA", "#F2B24B", "#FF6B5E", "#8FF5BE", "#F472B6", "#7FF0E6" };

    // Axis/label foreground and grid colours pulled from the app design tokens so
    // charts read correctly against the ink panel surface (--bg-panel ink-700).
    private const string AxisForeColor = "#8B9A93";   // --text-muted
    private const string GridLineColor = "#222B28";   // --line / --border

    // Retained for interface compatibility; never fires now that themes are fixed.
    public event Action? OnChartThemeChanged;

    public ChartThemeService(IUserSettingsService userSettings)
    {
        // userSettings retained for DI signature; theme personalities removed.
        _ = userSettings;
    }

    /// <summary>Returns the ApexCharts status colour series.</summary>
    public (string Success, string Warning, string Critical, string Neutral) GetCurrentPalette() => _statusPalette;

    /// <summary>Returns the ordered categorical colour palette for multi-series charts.</summary>
    public List<string> GetSeriesPalette() => new List<string>(_seriesPalette);

    /// <summary>Glassmorphism is disabled (theme personalities removed).</summary>
    public bool IsGlassEnabled => false;

    /// <summary>No glass blur (theme personalities removed).</summary>
    public int GlassBlurPx => 0;

    // ── Shared chrome ─────────────────────────────────────────────────────────
    // Every chart shares: transparent background (the .chart-panel surface shows
    // through), hidden toolbar, muted foreColor, gentle ease-in-out animation.
    private static Chart BaseChart() => new()
    {
        Background = "transparent",
        ForeColor = AxisForeColor,
        Toolbar = new Toolbar { Show = false },
        Animations = new Animations { Enabled = true, Easing = Easing.Easeinout, Speed = 500 }
    };

    private static Grid BaseGrid() => new() { BorderColor = GridLineColor, StrokeDashArray = 3 };

    /// <summary>
    /// Returns fully configured options for a (time-series) line/area chart.
    /// Callers overlay data-dependent bits (per-series stroke, annotations).
    /// </summary>
    public ApexChartOptions<T> GetLineOptions<T>(string title, string yAxisLabel) where T : class
    {
        return new ApexChartOptions<T>
        {
            Chart = BaseChart(),
            Theme = new Theme { Mode = Mode.Dark },
            Colors = GetSeriesPalette(),
            Title = new Title { Text = title },
            Xaxis = new XAxis
            {
                Type = XAxisType.Datetime,
                Labels = new XAxisLabels
                {
                    DatetimeFormatter = new DatetimeFormatter { Hour = "HH:mm", Minute = "HH:mm", Day = "MMM dd" }
                }
            },
            Yaxis = new List<YAxis>
            {
                new YAxis
                {
                    Title = new AxisTitle { Text = yAxisLabel },
                    Labels = new YAxisLabels { Formatter = "function(val) { return val != null ? val.toFixed(1) : ''; }" }
                }
            },
            Stroke = new Stroke { Width = 2, Curve = Curve.Smooth },
            Grid = BaseGrid(),
            Tooltip = new Tooltip { Theme = Mode.Dark, X = new TooltipX { Format = "yyyy-MM-dd HH:mm:ss" } },
            Legend = new Legend { Position = LegendPosition.Bottom, FontSize = "11px" }
        };
    }

    /// <summary>
    /// Returns fully configured options for a donut chart. Callers overlay the
    /// donut-specific plot labels and any value-suffix formatter.
    /// </summary>
    public ApexChartOptions<T> GetDonutOptions<T>(string title) where T : class
    {
        return new ApexChartOptions<T>
        {
            Chart = BaseChart(),
            Theme = new Theme { Mode = Mode.Dark },
            Colors = GetSeriesPalette(),
            Title = new Title { Text = title },
            Legend = new Legend { Position = LegendPosition.Right, FontSize = "11px" },
            Tooltip = new Tooltip { Theme = Mode.Dark },
            DataLabels = new DataLabels { Enabled = false }
        };
    }

    /// <summary>
    /// Returns fully configured options for a horizontal bar chart. Callers
    /// overlay any value-suffix tooltip formatter.
    /// </summary>
    public ApexChartOptions<T> GetBarOptions<T>(string title, string xAxisLabel) where T : class
    {
        return new ApexChartOptions<T>
        {
            Chart = BaseChart(),
            Theme = new Theme { Mode = Mode.Dark },
            Colors = GetSeriesPalette(),
            Title = new Title { Text = title },
            Xaxis = new XAxis
            {
                Title = new AxisTitle { Text = xAxisLabel },
                Labels = new XAxisLabels
                {
                    Style = new AxisLabelStyle { FontSize = "11px" },
                    Formatter = "function(val) { return val != null ? (val >= 1000000 ? (val/1000000).toFixed(1)+'M' : val >= 1000 ? (val/1000).toFixed(1)+'K' : val.toFixed(0)) : ''; }"
                }
            },
            Yaxis = new List<YAxis>
            {
                new YAxis { Labels = new YAxisLabels { Style = new AxisLabelStyle { FontSize = "10px" }, MaxWidth = 160 } }
            },
            PlotOptions = new PlotOptions
            {
                Bar = new PlotOptionsBar
                {
                    Horizontal = true,
                    BarHeight = "70%",
                    Distributed = true,
                    DataLabels = new PlotOptionsBarDataLabels { Position = BarDataLabelPosition.Top }
                }
            },
            Grid = BaseGrid(),
            Tooltip = new Tooltip { Theme = Mode.Dark },
            Legend = new Legend { Show = false },
            DataLabels = new DataLabels { Enabled = false }
        };
    }
}
