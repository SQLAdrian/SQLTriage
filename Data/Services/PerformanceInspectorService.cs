using System;
using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services;

/// <summary>
/// In-memory ring buffer of panel load traces. Enabled via Settings
/// (no cost when disabled). Holds the last 500 traces across all dashboards.
/// </summary>
public class PerformanceInspectorService
{
    private readonly LinkedList<PanelTrace> _traces = new();
    private readonly object _lock = new();
    private const int MaxTraces = 500;
    private bool _enabled;
    private readonly PanelMetricsService? _metrics;

    public bool IsEnabled => _enabled;

    public PerformanceInspectorService(PanelMetricsService? metrics = null)
    {
        _metrics = metrics;
    }

    public void SetEnabled(bool enabled) => _enabled = enabled;

    public void AddTrace(PanelTrace trace)
    {
        // Always forward successful traces to PanelMetrics — nav badge needs data even
        // when the inspector UI is disabled. Ring buffer is guarded by _enabled though.
        if (trace.Success && _metrics != null)
        {
            var pipeline = trace.PipelineDuration.TotalMilliseconds;
            if (pipeline > 0) _metrics.RecordMeasurement(trace.PanelId, pipeline);
        }

        if (!_enabled) return;
        lock (_lock)
        {
            _traces.AddLast(trace);
            while (_traces.Count > MaxTraces) _traces.RemoveFirst();
        }
    }

    public void Clear()
    {
        lock (_lock) _traces.Clear();
    }

    /// <summary>Snapshot of all retained traces, newest last.</summary>
    public IReadOnlyList<PanelTrace> GetRecentTraces()
    {
        lock (_lock) return _traces.ToArray();
    }

    /// <summary>
    /// Returns traces belonging to the most recent load cycle for the given dashboard.
    /// A "load cycle" is the cluster of traces whose StartedAt falls within a 10-second
    /// window ending at the most recent trace for that dashboard. If dashboardId is null,
    /// uses the dashboard from the most recent trace overall.
    /// </summary>
    public PanelTrace[] GetLastLoadTraces(string? dashboardId = null)
    {
        PanelTrace[] snapshot;
        lock (_lock) snapshot = _traces.ToArray();
        if (snapshot.Length == 0) return Array.Empty<PanelTrace>();

        if (string.IsNullOrEmpty(dashboardId))
            dashboardId = snapshot[^1].DashboardId;

        var forDashboard = snapshot
            .Where(t => t.DashboardId == dashboardId)
            .ToArray();
        if (forDashboard.Length == 0) return Array.Empty<PanelTrace>();

        var lastStart = forDashboard.Max(t => t.StartedAt);
        var windowStart = lastStart.AddSeconds(-10);
        return forDashboard
            .Where(t => t.StartedAt >= windowStart)
            .OrderBy(t => t.StartedAt)
            .ToArray();
    }
}
