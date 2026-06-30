/* In the name of God, the Merciful, the Compassionate */
/*
 * ConsolidationCollector — scheduled, opt-in telemetry sampler for the Premium
 * consolidation engine. Runs in BOTH the WPF app and the headless Windows service
 * (registered via AddSharedServices), so data accrues 24/7 once installed.
 *
 * Security umbrella (same as the rest of the app):
 *   - PREMIUM-GATED: collects only when the licensed consolidation model is present.
 *   - Persists to the SQLCipher-encrypted consolidation-history.db (DPAPI-wrapped key).
 *   - Reuses ServerConnectionManager connections + the metadata-ONLY probe (no query
 *     text / no plan XML ever read).
 *   - Start/stop and per-server toggles are AUDIT-LOGGED as security events.
 *   - Opt-in: ships OFF; an operator must explicitly Start it.
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services.Capacity;

public sealed class ConsolidationCollector : IDisposable
{
    private const string KeyGlobal = "global_enabled";
    private const string KeyCadence = "cadence_minutes";
    private const int DefaultCadenceMinutes = 60;
    private const int RetentionDays = 400;

    private readonly ILogger<ConsolidationCollector> _logger;
    private readonly ConsolidationAnalysisService _probe;
    private readonly ConsolidationHistoryStore _store;
    private readonly IConsolidationModelProvider _model;
    private readonly AuditLogService? _audit;

    private readonly System.Timers.Timer _timer;
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private DateTime _lastPurgeUtc = DateTime.MinValue;

    public bool IsEnabled { get; private set; }
    public int CadenceMinutes { get; private set; } = DefaultCadenceMinutes;
    public DateTime? LastRunUtc { get; private set; }
    public int LastSampleCount { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Premium gate — the collector is dormant unless the licensed model is present.</summary>
    public bool IsLicensed => _model.IsUnlocked;

    public event EventHandler? StateChanged;

    public ConsolidationCollector(
        ILogger<ConsolidationCollector> logger,
        ConsolidationAnalysisService probe,
        ConsolidationHistoryStore store,
        IConsolidationModelProvider model,
        AuditLogService? audit = null)
    {
        _logger = logger;
        _probe = probe;
        _store = store;
        _model = model;
        _audit = audit;

        IsEnabled = _store.GetConfig(KeyGlobal) == "1";
        CadenceMinutes = int.TryParse(_store.GetConfig(KeyCadence), out var c) && c > 0 ? c : DefaultCadenceMinutes;

        _timer = new System.Timers.Timer(TimeSpan.FromMinutes(CadenceMinutes).TotalMilliseconds);
        _timer.Elapsed += (_, _) => _ = TickAsync(CancellationToken.None);
        _timer.AutoReset = true;
        _timer.Start();

        _logger.LogInformation("[Consolidation] collector ready (enabled={Enabled}, cadence={Cadence}m).", IsEnabled, CadenceMinutes);
        // If it was left enabled across restarts, take a sample shortly after boot.
        if (IsEnabled) _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(20)); await TickAsync(CancellationToken.None); });
    }

    // ── Control surface (audit-logged) ───────────────────────────────────────

    public void Start(string actor)
    {
        IsEnabled = true;
        _store.SetConfig(KeyGlobal, "1");
        Audit($"Consolidation telemetry collection STARTED by {actor}", AuditSeverity.Info, actor, "Start");
        _logger.LogInformation("[Consolidation] collection started by {Actor}.", actor);
        Raise();
        _ = TickAsync(CancellationToken.None); // immediate first sample
    }

    public void Stop(string actor)
    {
        IsEnabled = false;
        _store.SetConfig(KeyGlobal, "0");
        Audit($"Consolidation telemetry collection STOPPED by {actor}", AuditSeverity.Warning, actor, "Stop");
        _logger.LogInformation("[Consolidation] collection stopped by {Actor}.", actor);
        Raise();
    }

    public void SetCadence(int minutes, string actor)
    {
        CadenceMinutes = Math.Clamp(minutes, 5, 1440);
        _store.SetConfig(KeyCadence, CadenceMinutes.ToString());
        _timer.Interval = TimeSpan.FromMinutes(CadenceMinutes).TotalMilliseconds;
        Audit($"Consolidation collection cadence set to {CadenceMinutes}m by {actor}", AuditSeverity.Info, actor, "SetCadence");
        Raise();
    }

    public bool IsServerEnabled(string serverName)
        => !_store.GetServerCollectionFlags().TryGetValue(serverName, out var en) || en; // default ON

    public void SetServerEnabled(string serverName, bool enabled, string actor)
    {
        _store.SetServerCollection(serverName, enabled);
        Audit($"Consolidation collection for '{serverName}' {(enabled ? "ENABLED" : "DISABLED")} by {actor}",
            AuditSeverity.Info, actor, enabled ? "EnableServer" : "DisableServer");
        Raise();
    }

    public List<ServerSampleSummary> GetSummaries() => _store.GetServerSummaries();

    public Task RunOnceAsync(CancellationToken ct = default) => TickAsync(ct);

    /// <summary>No-op accessor used at startup to force singleton instantiation (timer start).</summary>
    public void EnsureStarted() { }

    // ── The tick ─────────────────────────────────────────────────────────────

    private async Task TickAsync(CancellationToken ct)
    {
        if (!IsEnabled) return;
        if (!_model.IsUnlocked)
        {
            _logger.LogDebug("[Consolidation] tick skipped — Premium model not licensed.");
            return;
        }
        if (!await _tickGate.WaitAsync(0, ct)) return; // a previous tick is still running

        try
        {
            var flags = _store.GetServerCollectionFlags();
            var servers = await _probe.ProbeEstateAsync(ct);
            var now = DateTime.UtcNow;

            var samples = servers
                .Where(s => !flags.TryGetValue(s.ServerName, out var en) || en) // per-server default ON
                .Select(s => new ConsolidationSample
                {
                    ServerName = s.ServerName,
                    RecordedUtc = now,
                    Cores = s.Cores,
                    SnapCpuPct = s.AvgCpuPercent,
                    QsCpuCoresMean = s.QsCpuCoresMean,
                    QsCpuCoresP95 = s.QsCpuCoresP95,
                    QsWindowHours = s.QsWindowHours,
                    WorkerCpuCores = s.PlanCacheWorkerCores,
                    LogicalReadsPerSec = s.LogicalReadsPerSec,
                    PhysicalReadsPerSec = s.PhysicalReadsPerSec,
                    DailyIops = s.DailyTotalIops,
                })
                .ToList();

            if (samples.Count > 0)
                await _store.SaveSamplesAsync(samples, ct);

            // Daily retention sweep.
            if ((now - _lastPurgeUtc).TotalHours >= 24)
            {
                _store.PurgeOlderThan(RetentionDays);
                _lastPurgeUtc = now;
            }

            LastRunUtc = now;
            LastSampleCount = samples.Count;
            LastError = null;
            _logger.LogInformation("[Consolidation] sampled {Count} server(s).", samples.Count);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "[Consolidation] collection tick failed");
        }
        finally
        {
            _tickGate.Release();
            Raise();
        }
    }

    private void Audit(string message, AuditSeverity sev, string actor, string action)
    {
        try
        {
            _audit?.LogSecurityEvent(message, sev, new Dictionary<string, string>
            {
                ["User"] = actor,
                ["Action"] = action,
                ["Feature"] = "ConsolidationCollector",
            });
        }
        catch (Exception ex) { _logger.LogDebug(ex, "[Consolidation] audit log failed"); }
    }

    private void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _tickGate.Dispose();
    }
}
