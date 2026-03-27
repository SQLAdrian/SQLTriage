/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json.Serialization;

namespace SqlHealthAssessment.Data.Models
{
    public class DashboardConfigRoot
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("dashboards")]
        public List<DashboardDefinition> Dashboards { get; set; } = new();

        [JsonPropertyName("supportQueries")]
        public Dictionary<string, QueryPair> SupportQueries { get; set; } = new();
    }

    public class DashboardDefinition
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("navTitle")]
        public string NavTitle { get; set; } = "";

        [JsonPropertyName("navIcon")]
        public string NavIcon { get; set; } = "📊";

        [JsonPropertyName("navCategory")]
        public string NavCategory { get; set; } = "Dashboards";

        [JsonPropertyName("navOrder")]
        public int NavOrder { get; set; } = 0;

        [JsonPropertyName("route")]
        public string Route { get; set; } = "";

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>Data source: "sqlwatch" (default), "pm" (PerformanceMonitor), or "both"</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; } = "sqlwatch";

        /// <summary>Default database context for all panels in this dashboard</summary>
        [JsonPropertyName("defaultDatabase")]
        public string DefaultDatabase { get; set; } = "master";

        [JsonPropertyName("showAllOption")]
        public bool ShowAllOption { get; set; } = false;

        [JsonPropertyName("panels")]
        public List<PanelDefinition> Panels { get; set; } = new();

        /// <summary>Returns true if this dashboard should be visible for the given source.</summary>
        public bool IsVisibleForSource(string source)
        {
            return Source == "both" || Source == source || string.IsNullOrEmpty(Source);
        }
    }

    public class PanelDefinition
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        /// <summary>Description shown below chart titles, or as tooltip on stat cards</summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>Data source: "sqlwatch" (default), "pm" (PerformanceMonitor), or "both". If empty, inherits from dashboard.</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        /// <summary>TimeSeries, StatCard, BarGauge, DataGrid, CheckStatus, TextCard</summary>
        [JsonPropertyName("panelType")]
        public string PanelType { get; set; } = "TimeSeries";

        /// <summary>Line, Bar, Area (only for TimeSeries)</summary>
        [JsonPropertyName("chartType")]
        public string ChartType { get; set; } = "Line";

        [JsonPropertyName("height")]
        public int Height { get; set; } = 250;

        [JsonPropertyName("refreshIntervalSeconds")]
        public int? RefreshIntervalSeconds { get; set; }

        [JsonPropertyName("layout")]
        public PanelLayout Layout { get; set; } = new();

        [JsonPropertyName("query")]
        public QueryPair Query { get; set; } = new();

        /// <summary>Default database context for this panel's queries</summary>
        [JsonPropertyName("defaultDatabase")]
        public string DefaultDatabase { get; set; } = "master";

        // StatCard-specific
        [JsonPropertyName("statUnit")]
        public string StatUnit { get; set; } = "";

        [JsonPropertyName("statThresholdKey")]
        public string? StatThresholdKey { get; set; }

        // BarGauge-specific
        [JsonPropertyName("barGaugeThresholdKey")]
        public string? BarGaugeThresholdKey { get; set; }

        [JsonPropertyName("barGaugeUnitSuffix")]
        public string BarGaugeUnitSuffix { get; set; } = "%";

        // Value formatting
        /// <summary>Format for displaying values: N0 (integer), N2 (2 decimals), P0 (percent), 0.## GB, etc.</summary>
        [JsonPropertyName("valueFormat")]
        public string ValueFormat { get; set; } = "";

        // Conditional color formatting
        /// <summary>
        /// Color threshold rules evaluated in order. Last matching rule wins.
        /// If empty, falls back to StatThresholdKey or default blue.
        /// </summary>
        [JsonPropertyName("colorThresholds")]
        public List<ColorThresholdRule> ColorThresholds { get; set; } = new();

        // DataGrid-specific
        [JsonPropertyName("dataGridIsClickable")]
        public bool DataGridIsClickable { get; set; } = false;

        [JsonPropertyName("dataGridMaxRows")]
        public int DataGridMaxRows { get; set; } = 500;

        /// <summary>
        /// When > 0, the query uses a @TopRows parameter and the panel header shows an editable count.
        /// </summary>
        [JsonPropertyName("dataGridTopRows")]
        public int DataGridTopRows { get; set; } = 0;

        /// <summary>
        /// Column name whose value is executable SQL. Adds an Execute button per row with progress/cancel.
        /// </summary>
        [JsonPropertyName("actionColumn")]
        public string? ActionColumn { get; set; }

        /// <summary>
        /// When "query", the ActionColumn cell value is a SELECT that returns the actual DDL to execute (double-hop).
        /// When null or "direct", the cell value is executed as-is.
        /// </summary>
        [JsonPropertyName("actionColumnMode")]
        public string? ActionColumnMode { get; set; }

        /// <summary>
        /// Column-level color gradient rules for DataGrid cells.
        /// Each rule maps a column name to a color gradient based on the cell's numeric value.
        /// </summary>
        [JsonPropertyName("dataGridColumnColors")]
        public List<DataGridColumnColorRule>? DataGridColumnColors { get; set; }

        /// <summary>Gets the effective source, inheriting from dashboard if not specified.</summary>
        public string GetEffectiveSource(string dashboardSource)
        {
            return string.IsNullOrEmpty(Source) ? dashboardSource : Source;
        }

        /// <summary>Gets the effective default database, inheriting from dashboard if not specified.</summary>
        public string GetEffectiveDefaultDatabase(string dashboardDefaultDatabase)
        {
            return string.IsNullOrEmpty(DefaultDatabase) ? dashboardDefaultDatabase : DefaultDatabase;
        }

        /// <summary>Evaluates colorThresholds rules in order, returns last matching color. Falls back to default.</summary>
        public string GetThresholdColor(double value, string defaultColor = "#2196f3")
        {
            if (ColorThresholds == null || ColorThresholds.Count == 0)
                return defaultColor;

            string color = defaultColor;
            foreach (var rule in ColorThresholds)
            {
                if (rule.Matches(value))
                    color = rule.Color;
            }
            return color;
        }

        /// <summary>Formats a value according to the ValueFormat string.</summary>
        public string FormatValue(object? value)
        {
            if (value == null || string.IsNullOrEmpty(ValueFormat))
                return value?.ToString() ?? "";

            try
            {
                if (value is IFormattable formattable)
                {
                    return formattable.ToString(ValueFormat, System.Globalization.CultureInfo.InvariantCulture);
                }
                return value.ToString() ?? "";
            }
            catch
            {
                return value.ToString() ?? "";
            }
        }
    }

    public class PanelLayout
    {
        /// <summary>0 = special section (stats/gauges), 1 = left column, 2 = right column</summary>
        [JsonPropertyName("column")]
        public int Column { get; set; } = 1;

        [JsonPropertyName("order")]
        public int Order { get; set; } = 0;

        [JsonPropertyName("spanColumns")]
        public bool SpanColumns { get; set; } = false;
    }

    /// <summary>
    /// A single threshold rule: if value meets the operator + threshold, apply this color.
    /// Rules are evaluated in order; the last matching rule wins.
    /// </summary>
    public class ColorThresholdRule
    {
        /// <summary>Comparison operator: >=, >, <=, <, ==</summary>
        [JsonPropertyName("operator")]
        public string Operator { get; set; } = ">=";

        [JsonPropertyName("value")]
        public double Value { get; set; }

        /// <summary>Hex color e.g. #4caf50</summary>
        [JsonPropertyName("color")]
        public string Color { get; set; } = "#2196f3";

        /// <summary>Optional label for the editor, e.g. "Warning", "Critical"</summary>
        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        public bool Matches(double actual)
        {
            return Operator switch
            {
                ">=" => actual >= Value,
                ">"  => actual > Value,
                "<=" => actual <= Value,
                "<"  => actual < Value,
                "==" => Math.Abs(actual - Value) < 0.0001,
                _    => actual >= Value
            };
        }
    }

    /// <summary>
    /// Defines a color gradient for a DataGrid column based on numeric values.
    /// The gradient interpolates between minColor and maxColor across the data range.
    /// </summary>
    public class DataGridColumnColorRule
    {
        /// <summary>Column name to apply the gradient to (case-insensitive match).</summary>
        [JsonPropertyName("column")]
        public string Column { get; set; } = "";

        /// <summary>"higher-is-better" (green at top) or "higher-is-worse" (red at top).</summary>
        [JsonPropertyName("mode")]
        public string Mode { get; set; } = "higher-is-better";

        /// <summary>Color for the low end of the range. Default: muted/neutral.</summary>
        [JsonPropertyName("minColor")]
        public string MinColor { get; set; } = "";

        /// <summary>Color for the high end of the range. Default: bright green or red based on mode.</summary>
        [JsonPropertyName("maxColor")]
        public string MaxColor { get; set; } = "";
    }

    public class QueryPair
    {
        /// <summary>Data source: "sqlwatch" (default), "pm" (PerformanceMonitor)</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; } = "sqlwatch";

        [JsonPropertyName("sqlServer")]
        public string SqlServer { get; set; } = "";
    }
}
