/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services;

/// <summary>
/// Rolling per-panel timing metrics. Persists p50/p95 summaries to
/// Config/panel-metrics.json so the nav render-time badge survives app restarts.
///
/// Writes are debounced (~5s) to avoid hammering disk. In-memory ring holds
/// the last 20 raw samples per panel; only the summary is written to disk.
/// </summary>
public class PanelMetricsService : IDisposable
{
    private const int SampleWindow = 20;
    private const int DebounceMs = 5000;

    private readonly ILogger<PanelMetricsService> _logger;
    private readonly string _path;
    private readonly ConcurrentDictionary<string, RingBuffer> _samples = new();
    private readonly ConcurrentDictionary<string, PanelMetric> _persisted = new();
    private readonly Timer _flushTimer;
    private int _dirty;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public PanelMetricsService(ILogger<PanelMetricsService> logger)
    {
        _logger = logger;
        _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "panel-metrics.json");
        Load();
        _flushTimer = new Timer(OnFlushTick, null, DebounceMs, DebounceMs);
    }

    /// <summary>Record a successful panel load timing sample.</summary>
    public void RecordMeasurement(string panelId, double durationMs)
    {
        if (string.IsNullOrEmpty(panelId) || durationMs < 0) return;

        var ring = _samples.GetOrAdd(panelId, _ => new RingBuffer(SampleWindow));
        ring.Add(durationMs);

        var (p50, p95, count) = ring.Summary();
        _persisted[panelId] = new PanelMetric
        {
            LastMeasuredMs = durationMs,
            MeasuredAt = DateTime.UtcNow,
            SampleCount = count,
            P50Ms = p50,
            P95Ms = p95
        };
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>Snapshot of a single panel's metric, or null if never measured.</summary>
    public PanelMetric? GetMetric(string panelId)
        => _persisted.TryGetValue(panelId, out var m) ? m : null;

    /// <summary>Snapshot of all current metrics.</summary>
    public IReadOnlyDictionary<string, PanelMetric> GetAll()
        => new Dictionary<string, PanelMetric>(_persisted);

    private void OnFlushTick(object? state)
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        try
        {
            var payload = new MetricsFile { Panels = new Dictionary<string, PanelMetric>(_persisted) };
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, _jsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist panel-metrics.json");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<MetricsFile>(json);
            if (file?.Panels == null) return;
            foreach (var (k, v) in file.Panels)
                _persisted[k] = v;
            _logger.LogInformation("Loaded {Count} panel metrics from {Path}", file.Panels.Count, _path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load panel-metrics.json — starting fresh");
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        OnFlushTick(null);
    }

    // ──────────────────────── Support types ─────────────────────────

    public class PanelMetric
    {
        [JsonPropertyName("lastMeasuredMs")] public double LastMeasuredMs { get; set; }
        [JsonPropertyName("measuredAt")] public DateTime MeasuredAt { get; set; }
        [JsonPropertyName("sampleCount")] public int SampleCount { get; set; }
        [JsonPropertyName("p50Ms")] public double P50Ms { get; set; }
        [JsonPropertyName("p95Ms")] public double P95Ms { get; set; }
    }

    private class MetricsFile
    {
        [JsonPropertyName("panels")] public Dictionary<string, PanelMetric>? Panels { get; set; }
    }

    private class RingBuffer
    {
        private readonly double[] _buf;
        private int _count;
        private int _next;
        private readonly object _lock = new();

        public RingBuffer(int capacity) { _buf = new double[capacity]; }

        public void Add(double value)
        {
            lock (_lock)
            {
                _buf[_next] = value;
                _next = (_next + 1) % _buf.Length;
                if (_count < _buf.Length) _count++;
            }
        }

        public (double p50, double p95, int count) Summary()
        {
            double[] snapshot;
            int count;
            lock (_lock)
            {
                if (_count == 0) return (0, 0, 0);
                snapshot = new double[_count];
                Array.Copy(_buf, snapshot, _count);
                count = _count;
            }
            Array.Sort(snapshot);
            var p50 = snapshot[count / 2];
            var p95 = snapshot[(int)Math.Min(count - 1, Math.Ceiling(count * 0.95) - 1)];
            return (p50, p95, count);
        }
    }
}
