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
 * IsEnabled(id) = HARD && SOFT. The feature set is registered ONCE per gate instance, by the gate
 * itself — see EnsureRegistered and the FeatureGate(Action) constructor — and nav/pages then call
 * IsEnabled.
 *
 * WHY THE GATE CARRIES ITS OWN REGISTRATION (GATE-02, 2026-08-08). Registration used to be a
 * per-host startup call, and only ONE host made it: App.xaml.cs:231 (WPF desktop). The headless
 * WindowsServiceHost — shared by --server AND --service, i.e. the installed live service — never
 * called it, so every hard gate was ABSENT rather than false, IsLicensed answered false for every
 * feature, and the nav rendered four sections with zero dashboard links while dashboard-config.json
 * held 27 dashboards and each /dashboard/{id} route still rendered fine by URL. Behaviour differed
 * by host on the same licence bundle. Per-host duplication is how that happened, so the fix is not
 * a second call site: the registration now travels with the DI registration in AddSharedServices,
 * which every host composes, and runs on first read of THIS instance. If a host can resolve the
 * gate, the gate is registered.
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
using Serilog;

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

    /// <summary>
    /// Runs this instance's deferred registration if it has not run yet, then returns. Idempotent,
    /// thread-safe, and NEVER throws — a registration fault leaves the gate empty (fail-closed) and
    /// is logged, which is the same posture as a throwing hard gate.
    ///
    /// <para>Every read below already calls this, so nothing has to. Hosts call it at startup only
    /// to move the work (and any fault) into the boot log rather than into the first render; see
    /// <c>WindowsServiceHost.InitializeHostDiagnostics</c>, which both hosts pass through. It is NOT
    /// the mechanism that keeps the hosts in step — the DI registration is.</para>
    /// </summary>
    void EnsureRegistered();

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

    // ── Deferred registration (see the file header: GATE-02) ─────────────────────────────────
    private readonly Action<IFeatureGate>? _deferredRegistration;
    private readonly object _registrationLock = new();
    private volatile bool _registrationComplete;
    private bool _registrationStarted;   // guarded by _registrationLock

    /// <summary>
    /// A gate with NO deferred registration: every feature must be registered by hand. Used by
    /// tests, which is what makes the DI path's registration provable by contrast — a gate built
    /// this way and never registered answers false for everything.
    /// </summary>
    public FeatureGate() { }

    /// <summary>
    /// The shape the DI container builds (<see cref="ServiceCollectionExtensions.AddSharedServices"/>).
    /// <paramref name="deferredRegistration"/> is invoked at most once, on this instance, the first
    /// time anything reads the gate.
    /// </summary>
    /// <remarks>
    /// Deferred rather than eager on purpose, for two reasons that are both load-bearing:
    /// <list type="bullet">
    ///   <item>ORDER. The hard gates must see the resolved licence bundle, so registration has to
    ///   happen after <c>LicenseService.Initialize</c>. A first READ is always later than that on
    ///   every host (the earliest reader is a render), so the ordering constraint that used to live
    ///   in a host's startup sequence — and was got wrong by omission — is satisfied by
    ///   construction.</item>
    ///   <item>THE SERVICE. Nothing runs while the container is being built, so a registration fault
    ///   cannot fail an SCM start. Combined with the catch below, nothing on this path can throw into
    ///   a service restart loop.</item>
    /// </list>
    /// </remarks>
    public FeatureGate(Action<IFeatureGate> deferredRegistration)
        => _deferredRegistration = deferredRegistration
            ?? throw new ArgumentNullException(nameof(deferredRegistration));

    public event EventHandler? Changed;

    /// <inheritdoc />
    public void EnsureRegistered()
    {
        if (_registrationComplete || _deferredRegistration is null) return;

        lock (_registrationLock)
        {
            // Covers three arrivals with one check: a second thread that queued on the lock while
            // the first was registering, a re-entrant read from inside the registration itself
            // (Monitor is re-entrant, so this returns instead of recursing), and a repeat call
            // after a registration that threw.
            if (_registrationStarted) return;
            _registrationStarted = true;

            try
            {
                _deferredRegistration(this);
            }
            catch (Exception ex)
            {
                // Fail CLOSED and stay up. Whatever the registration had managed to register before
                // the fault stands; everything it did not reach reads as not licensed (see
                // IsLicensed) and its surface stays hidden — the same answer a throwing hard gate
                // gives. The message does not claim the gate is empty: a throw partway through
                // leaves a partial set, and only the count below is measured.
                Log.Error(ex, "[FeatureGate] deferred feature registration threw after registering "
                            + "{Count} feature(s). Anything it did not reach reads as NOT licensed, so "
                            + "those surfaces stay hidden.", _hard.Count);
            }
            finally
            {
                // Set even on the failure path: retrying on every read would log once per render.
                _registrationComplete = true;
            }
        }
    }

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

    /// <summary>
    /// HARD gate only. An UNREGISTERED feature answers false — absence is a denial, not a pass. That
    /// is deliberate (a missing registration must never open a licensed surface) and it is why the
    /// registration itself has to be structural rather than a call a host can forget: for twelve days
    /// the headless host forgot, and this method's honest false hid every gated nav entry there.
    /// </summary>
    public bool IsLicensed(string featureId)
    {
        EnsureRegistered();
        try { return _hard.TryGetValue(featureId, out var g) && g(); }
        catch { return false; } // a throwing hard gate must fail closed
    }

    public bool IsSoftEnabled(string featureId)
    {
        EnsureRegistered();
        return !_soft.TryGetValue(featureId, out var s) || s;
    }

    public bool IsEnabled(string featureId) => IsLicensed(featureId) && IsSoftEnabled(featureId);

    public void SetSoftEnabled(string featureId, bool enabled)
    {
        EnsureRegistered();
        _soft[featureId] = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyCollection<string> Features
    {
        get { EnsureRegistered(); return _hard.Keys.ToArray(); }
    }

    public IReadOnlyCollection<FeatureDescriptor> Descriptors
    {
        get { EnsureRegistered(); return _meta.Values.ToArray(); }
    }

    public FeatureDescriptor? Describe(string featureId)
    {
        EnsureRegistered();
        return _meta.TryGetValue(featureId, out var d) ? d : null;
    }
}
