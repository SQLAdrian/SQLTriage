/* In the name of God, the Merciful, the Compassionate */
/*
 * DemoRunLedger — meters CORPUS audit runs (/audit) against the SIGNED per-bundle demo
 * allocation (BundleFeatures.DemoCorpusInstancesPer24h). The allocation is the number of
 * DISTINCT SQL instances the operator may run the corpus against within any rolling 24h
 * window:
 *     community bundle = 1   (the public demo limit)
 *     full bundle      = 0   → UNLIMITED (gate no-ops)
 *
 * The limit lives in the GCM-authenticated bundle, NOT the GitHub source — so recompiling the
 * public build cannot lift it, and tampering the bundle breaks the auth tag at decrypt. This is
 * the same trust model as the remediation credit ledger (PersistedRemediationCreditLedger).
 *
 * Mechanics:
 *   • A "claim" = { instance, firstRunUtc }, recorded on the first SUCCESSFUL corpus run for an
 *     instance. Re-running a CLAIMED instance inside its 24h window is always free.
 *   • A NEW instance is allowed only while the count of DISTINCT claims active in the last 24h is
 *     below the signed N. Claims older than 24h expire (pruned on every read) → a slot frees.
 *   • Allocation is read LIVE from the bundle, so loading a different bundle (community → demo →
 *     full) re-licenses immediately, exactly like the credit ledger.
 *
 * Persistence mirrors PersistedRemediationCreditLedger: atomic tmp→delete→move to
 * Config/demo-run-ledger.json. A crash never corrupts the ledger; at worst a claim is lost,
 * which only ever GRANTS the operator another run (fail-open on persistence, fail-closed on the
 * allocation itself).
 *
 * DevBridge (dev machine) gets an UNLIMITED escape hatch so the gated surface is testable without
 * a stamped bundle; real installs honour only the signed allocation.
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services.Licensing;

/// <summary>Why a corpus run is (dis)allowed, plus the figures the UI shows.</summary>
public sealed record DemoRunDecision(
    bool Allowed,
    bool Unlimited,
    int Allowance,          // signed N (0 when unlimited)
    int InstancesUsed,      // distinct instances claimed in the last 24h
    string? Instance,       // the instance this decision was made for (null for Status())
    DateTime? UnlocksUtc,   // when the next slot frees (oldest active claim + 24h); null if a slot is free now
    string? BlockReason);   // human message when !Allowed

public interface IDemoRunLedger
{
    /// <summary>True if the operator may run the corpus against <paramref name="instance"/> right now.</summary>
    DemoRunDecision CanRun(string instance);

    /// <summary>Records a successful corpus run for <paramref name="instance"/> (claims a slot if new).</summary>
    void RecordRun(string instance);

    /// <summary>Snapshot for the UI (no specific instance): allowance, used, next-unlock.</summary>
    DemoRunDecision Status();
}

public sealed class DemoRunLedger : IDemoRunLedger
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private readonly IBundleAccessor _bundle;
    private readonly ILogger<DemoRunLedger> _logger;
    private readonly string _path;
    private readonly object _lock = new();

    // Persisted: one entry per claimed instance, holding the FIRST run time inside the current
    // window. Re-running refreshes nothing (the window is anchored to first use, by design).
    private Dictionary<string, DateTime> _claims = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DemoRunLedger(IBundleAccessor bundle, ILogger<DemoRunLedger> logger, string? pathOverride = null)
    {
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = pathOverride ?? Path.Combine(AppContext.BaseDirectory, "Config", "demo-run-ledger.json");
        Load();
    }

    public string LedgerPath => _path;

    // The signed allocation. 0 = unlimited. DevBridge forces unlimited so the surface is testable
    // on a dev build without a stamped bundle.
    private bool IsUnlimited => BuildMode.DevBridgeActive || Allowance == 0;
    private int Allowance => Math.Max(0, _bundle.Features.DemoCorpusInstancesPer24h);

    public DemoRunDecision CanRun(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance))
            return new DemoRunDecision(false, false, Allowance, 0, instance, null, "No SQL instance selected.");

        lock (_lock)
        {
            Prune();

            if (IsUnlimited)
                return new DemoRunDecision(true, true, 0, _claims.Count, instance, null, null);

            // Re-running an already-claimed instance is always free inside its window.
            if (_claims.ContainsKey(instance))
                return new DemoRunDecision(true, false, Allowance, _claims.Count, instance, null, null);

            // A new instance is allowed only if a slot is free.
            if (_claims.Count < Allowance)
                return new DemoRunDecision(true, false, Allowance, _claims.Count, instance, null, null);

            // No slot — report when the oldest claim frees one.
            DateTime? unlocks = _claims.Values.Count == 0 ? null : _claims.Values.Min() + Window;
            return new DemoRunDecision(false, false, Allowance, _claims.Count, instance, unlocks,
                BuildBlockReason(instance, unlocks));
        }
    }

    public void RecordRun(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance)) return;
        lock (_lock)
        {
            Prune();
            if (IsUnlimited) return;             // unlimited path never spends the ledger
            if (_claims.ContainsKey(instance)) return; // already claimed inside the window
            _claims[instance] = DateTime.UtcNow;
            Save();
            _logger.LogInformation(
                "[DemoRunLedger] Claimed corpus-demo slot for '{Instance}' ({Used}/{Allow} used in 24h).",
                instance, _claims.Count, Allowance);
        }
    }

    public DemoRunDecision Status()
    {
        lock (_lock)
        {
            Prune();
            if (IsUnlimited)
                return new DemoRunDecision(true, true, 0, _claims.Count, null, null, null);

            bool slotFree = _claims.Count < Allowance;
            DateTime? unlocks = slotFree || _claims.Count == 0 ? null : _claims.Values.Min() + Window;
            return new DemoRunDecision(slotFree, false, Allowance, _claims.Count, null, unlocks,
                slotFree ? null : BuildBlockReason(null, unlocks));
        }
    }

    private string BuildBlockReason(string? instance, DateTime? unlocks)
    {
        var when = unlocks is { } u
            ? FormatRemaining(u - DateTime.UtcNow)
            : "soon";
        var bits = Allowance == 1 ? "instance" : $"{Allowance} instances";
        // Adrian-approved copy: honest, single path, non-naggy.
        return $"Community version: you've assessed your {bits} for this 24h window. " +
               $"Next unlocks in {when}, or get in touch with Adrian if you need a larger allocation.";
    }

    private static string FormatRemaining(TimeSpan ts)
    {
        if (ts <= TimeSpan.Zero) return "moments";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }

    // Drop claims whose 24h window has elapsed. Caller holds _lock.
    private void Prune()
    {
        var cutoff = DateTime.UtcNow - Window;
        var expired = _claims.Where(kv => kv.Value <= cutoff).Select(kv => kv.Key).ToList();
        if (expired.Count == 0) return;
        foreach (var k in expired) _claims.Remove(k);
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _logger.LogInformation("[DemoRunLedger] No ledger at {Path}; starting fresh.", _path);
                return;
            }
            var payload = JsonSerializer.Deserialize<LedgerPayload>(File.ReadAllText(_path), _json);
            if (payload?.Claims == null) return;
            lock (_lock)
            {
                _claims = new Dictionary<string, DateTime>(payload.Claims, StringComparer.OrdinalIgnoreCase);
                Prune();
            }
            _logger.LogInformation("[DemoRunLedger] Loaded {Count} active demo claim(s).", _claims.Count);
        }
        catch (Exception ex)
        {
            // Fail-open on a corrupt ledger: an unreadable file resets to zero claims, which only
            // ever GRANTS another run — never silently extends a paid allocation.
            _logger.LogWarning(ex, "[DemoRunLedger] Failed to load ledger from {Path}; starting fresh.", _path);
        }
    }

    // Atomic persist (tmp -> delete -> move), mirroring PersistedRemediationCreditLedger. Caller holds _lock.
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var payload = new LedgerPayload
            {
                SchemaVersion = 1,
                LastUpdatedUtc = DateTime.UtcNow,
                Claims = new Dictionary<string, DateTime>(_claims, StringComparer.OrdinalIgnoreCase),
            };
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, _json));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DemoRunLedger] Failed to persist ledger to {Path}", _path);
        }
    }

    private sealed class LedgerPayload
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("lastUpdatedUtc")]
        public DateTime LastUpdatedUtc { get; set; }

        [JsonPropertyName("claims")]
        public Dictionary<string, DateTime> Claims { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
