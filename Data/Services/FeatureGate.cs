/* In the name of God, the Merciful, the Compassionate */
/*
 * FeatureGate — one consistent answer to "is feature X available right now?" for nav + pages.
 *
 * Combines two gates:
 *   HARD (licence) : the feature's bundled payload is present — usually () => provider.IsAvailable
 *                    from a BundleBackedResource<T>. Absent bundle => hard-disabled.
 *   SOFT (operator): an on/off toggle (defaults ON) — the runtime selection that will drive the
 *                    public-build profile. Unticking soft-disables a licensed feature.
 *
 * IsEnabled(id) = HARD && SOFT. Register features once at startup, then nav/pages call IsEnabled.
 *
 * NOTE: soft state is in-memory for now. The next step (master/public split) is to persist it to a
 * public-profile.json that the publish step reads to exclude/disable unticked modules. The shape
 * here is deliberately ready for that — wire SetSoftEnabled to persist + the publish to consume it.
 */

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SQLTriage.Data.Services;

/// <summary>
/// Display + classification metadata for a gated feature module. Drives the master-only
/// "Build Profile" authoring page (one checkbox per descriptor, grouped by Category).
/// Pattern documents WHICH master/public split strategy the module uses (see NEXT_SESSION §4):
///   1 = pure-data (bundle the JSON, public absent → nav auto-hides)
///   2 = shell + bundled brains (page ships as teaser, value from bundle)
///   3 = build-exclude (code itself kept out of public — reserved, not soft-gatable)
/// </summary>
public sealed record FeatureDescriptor(
    string Id,
    string Title,
    string Category,
    string Description,
    int Pattern = 1);

public interface IFeatureGate
{
    /// <summary>Register a feature's HARD (licence) gate. Soft defaults ON. Idempotent.</summary>
    void Register(string featureId, Func<bool> hardGate);

    /// <summary>Register with display metadata for the Build Profile page. Soft defaults ON. Idempotent.</summary>
    void Register(FeatureDescriptor descriptor, Func<bool> hardGate);

    /// <summary>HARD &amp;&amp; SOFT — the answer nav/pages should use.</summary>
    bool IsEnabled(string featureId);

    /// <summary>HARD gate only (is the licensed payload present?).</summary>
    bool IsLicensed(string featureId);

    /// <summary>SOFT toggle state (operator on/off). True when unset.</summary>
    bool IsSoftEnabled(string featureId);

    void SetSoftEnabled(string featureId, bool enabled);

    IReadOnlyCollection<string> Features { get; }

    /// <summary>Registered descriptors (only features registered WITH metadata appear here).</summary>
    IReadOnlyCollection<FeatureDescriptor> Descriptors { get; }

    /// <summary>Descriptor for a feature, or null if it was registered without metadata.</summary>
    FeatureDescriptor? Describe(string featureId);

    /// <summary>Fires when a soft toggle changes (so nav can refresh).</summary>
    event EventHandler? Changed;
}

public sealed class FeatureGate : IFeatureGate
{
    private readonly ConcurrentDictionary<string, Func<bool>> _hard = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _soft = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FeatureDescriptor> _meta = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public void Register(string featureId, Func<bool> hardGate)
    {
        if (string.IsNullOrWhiteSpace(featureId)) throw new ArgumentException("featureId required", nameof(featureId));
        _hard[featureId] = hardGate ?? throw new ArgumentNullException(nameof(hardGate));
        _soft.TryAdd(featureId, true); // soft-on by default
    }

    public void Register(FeatureDescriptor descriptor, Func<bool> hardGate)
    {
        if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
        Register(descriptor.Id, hardGate);
        _meta[descriptor.Id] = descriptor;
    }

    public bool IsLicensed(string featureId)
    {
        try { return _hard.TryGetValue(featureId, out var g) && g(); }
        catch { return false; } // a throwing hard gate must fail closed
    }

    public bool IsSoftEnabled(string featureId) => !_soft.TryGetValue(featureId, out var s) || s;

    public bool IsEnabled(string featureId) => IsLicensed(featureId) && IsSoftEnabled(featureId);

    public void SetSoftEnabled(string featureId, bool enabled)
    {
        _soft[featureId] = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyCollection<string> Features => _hard.Keys.ToArray();

    public IReadOnlyCollection<FeatureDescriptor> Descriptors => _meta.Values.ToArray();

    public FeatureDescriptor? Describe(string featureId) =>
        _meta.TryGetValue(featureId, out var d) ? d : null;
}
