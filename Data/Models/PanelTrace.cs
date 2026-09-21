using System;

namespace SQLTriage.Data.Models;

/// <summary>
/// Per-panel timing record for the Performance Inspector.
///
/// NOTE: timings are measured at the DynamicDashboard level, which only sees
/// the boundary around CachingQueryExecutor.ExecuteQueryAsync(). It cannot
/// split cache-hit vs SQL vs serialization without deeper instrumentation.
///
///   StartedAt      — panel load method entered
///   FetchCompleted — CachingQueryExecutor returned (includes hot/SQLite/SQL + any cache write)
///   DataReadyAt    — result mapped into the dashboard's results dictionary
///
/// "Pipeline" = StartedAt → DataReadyAt. Render time is not captured here;
/// Blazor's render loop runs after this method returns on the WhenAll join.
/// </summary>
public class PanelTrace
{
    public string DashboardId { get; set; } = "";
    public string PanelId { get; set; } = "";
    public string QueryId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string PanelType { get; set; } = "";

    public DateTime StartedAt { get; set; }
    public DateTime FetchCompleted { get; set; }
    public DateTime DataReadyAt { get; set; }

    public string CacheHitTier { get; set; } = "Unknown"; // None / Hot / SQLite / Fresh / Unknown
    public int RowCount { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }

    public TimeSpan FetchDuration => Ordered(StartedAt, FetchCompleted);
    public TimeSpan MappingDuration => Ordered(FetchCompleted, DataReadyAt);
    public TimeSpan PipelineDuration => Ordered(StartedAt, DataReadyAt);

    /// <summary>Returns the positive span between two stamps, or zero if either is unset.</summary>
    private static TimeSpan Ordered(DateTime a, DateTime b)
    {
        if (a == default || b == default) return TimeSpan.Zero;
        return b > a ? b - a : TimeSpan.Zero;
    }
}
