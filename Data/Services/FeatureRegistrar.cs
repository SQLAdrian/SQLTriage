/* In the name of God, the Merciful, the Compassionate */
/*
 * FeatureRegistrar — one place that declares the runtime-gated feature modules and wires each into
 * the IFeatureGate (HARD licence gate) with display metadata.
 *
 * WHO CALLS THIS (changed 2026-08-08, GATE-02): exactly one non-test caller, the DI registration of
 * IFeatureGate in ServiceCollectionExtensions.AddSharedServices. It is NOT a host startup step any
 * more. It was, and only the WPF host performed it, so the headless --server/--service host ran with
 * an empty gate and every gated nav entry vanished there on a licence the desktop honoured — see the
 * FeatureGate file header. A single call site inside the composition root is what makes "both hosts
 * agree" structural: the gate registers itself on first read, so a host cannot skip it, and
 * FeatureGateHostParityTests fails the build if a second caller appears.
 *
 * ORDER: still runs after LicenseService.Initialize on every host — not by sequencing but because
 * the trigger is the first READ of the gate, and the earliest reader is a render.
 *
 * 2026-06-11 Community Edition: build-profile selection moved to buildprofile.json + BuildModules
 * compile-time consts (see .handoff/ADR-2026-06-11-community-build-gating.md). FeatureGate now only
 * answers the RUNTIME licensing question (is the bundled payload present?); SOFT toggles default on.
 * The old PublicProfileStore (%APPDATA% per-feature ticks) is retired.
 */

#nullable enable

using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services.Capacity;

namespace SQLTriage.Data.Services;

public static class FeatureRegistrar
{
    // Feature ids — kebab-case, stable. Don't rename casually.
    public const string Consolidation     = "consolidation";
    public const string DynamicDashboards = "dynamic-dashboards";
    public const string DevTools          = "dev-tools";
    public const string Remediation       = "remediation";
    public const string ServerHardening   = "server-hardening";
    public const string PlaybookMarkdown  = "playbook-markdown";

    /// <summary>
    /// Declares the gated module set onto <paramref name="gate"/>.
    /// </summary>
    /// <param name="sp">Provider the hard gates read their licence state from.</param>
    /// <param name="gate">
    /// The gate to fill. Passed in rather than resolved from <paramref name="sp"/> deliberately: the
    /// caller is the gate's own deferred registration, so resolving IFeatureGate here would be a
    /// circular read of the very singleton being initialised.
    /// </param>
    public static void RegisterAll(IServiceProvider sp, IFeatureGate gate)
    {
        var logger  = sp.GetService<ILoggerFactory>()?.CreateLogger("FeatureRegistrar");

        // ── Pattern 2: shell + bundled brains (page is a teaser; value/IP lives in the Full bundle) ──
        var consolidation = sp.GetRequiredService<IConsolidationModelProvider>();
        gate.Register(
            new FeatureDescriptor(
                Consolidation,
                "Capacity Consolidation",
                "Premium",
                "Estate consolidation & SQL licensing optimisation. The page ships as a teaser; the costing/packing model is decrypted from the licensed Full bundle.",
                Pattern: 2),
            () => consolidation.IsUnlocked);

        // ── Pattern 1: pure-data (the dashboard JSON is the value; absent in public → nav auto-hides) ──
        var dashboards = sp.GetRequiredService<DashboardConfigService>();
        gate.Register(
            new FeatureDescriptor(
                DynamicDashboards,
                "Dynamic Dashboards",
                "Diagnostics",
                "Config-driven live dashboards (Live/SQLWATCH/PerformanceMonitor/Audit). Driven entirely by dashboard-config.json — exclude the JSON from the public build and the nav sections disappear.",
                Pattern: 1),
            () => dashboards.Config.Dashboards.Any());

        // ── Dev-tools capability claim (2026-06-12): runtime layer for FULL builds only ──
        // Community builds compile the dev-tools pages out (buildprofile.targets); this gate
        // covers a full build running anywhere else. Single boolean claim from the bundle
        // manifest, FAIL-CLOSED since 2026-08-05: only an explicit true grants, absent denies.
        // No machine or user name appears anywhere in this decision, by ruling.
        var bundle = sp.GetRequiredService<Licensing.IBundleAccessor>();
        gate.Register(
            new FeatureDescriptor(
                DevTools,
                "Dev Tools",
                "Development",
                "Corpus editors, check validator, build profile, remediation tuner, perf instrumentation. Build-absent in community; bundle dev-capability claim gates full builds at runtime.",
                Pattern: 3),
            // DevBridge (--devbridge, dev machine only) is the developer master-unlock:
            // force the dev-tools surfaces visible regardless of the bundle claim, so a
            // full dev build shows all the bits. Community still compiles them out; real
            // distribution still honours the bundle DevToolsCapability claim.
            () => SQLTriage.Data.BuildMode.DevBridgeActive || bundle.Features.DevToolsCapability);

        // ── Gated remediation surface (write capability) ──
        // Fail-CLOSED: a Full-tier customer licence carrying the remediation claim (or DevBridge
        // on a dev machine) unlocks the preview→approve→apply→verify surface. This nav entry
        // covers Remediation, AG Job Guard and AG Job Sync — all three pages are Content-Removed
        // from community and ride this one claim.
        //
        // WHAT IS ACTUALLY GUARANTEED (the previous wording said nav, page and write path "cannot
        // drift apart", which is more than this code can promise): every caller that ASKS resolves
        // the same licence predicate, ServerConfigSuiteGate, so the READING cannot differ. Nothing
        // makes a caller ask. The four pages and the script runner each opt in on their own, and
        // /server-configuration proved the gap — it shipped 2026-08-05 with a page that never
        // consulted this at all while its nav entry read BuildModules.Premium. The lambda below and
        // BundleBackedRemediationCapability also each compose the DevBridge hatch themselves; that
        // ||-clause is duplicated per call site, not shared.
        //
        // The enforcement that does NOT depend on a caller remembering is the chokepoint:
        // RemediationRunner asks BundleBackedRemediationCapability, and every write in the set
        // goes through it.
        gate.Register(
            new FeatureDescriptor(
                Remediation,
                "Apply Remediations",
                "Operations",
                "Gated preview→approve→apply→verify remediation surface (MAXDOP), plus AG Job Guard and AG Job Sync. A write capability bound to the licence: Full tier AND the bundle remediation claim, fail-closed.",
                Pattern: 2),
            () => SQLTriage.Data.BuildMode.DevBridgeActive
                  || Licensing.ServerConfigSuiteGate.IsAvailable(bundle));

        // ── Server Configuration & Hardening preview/apply surface on /remediation ──
        // Rides the same licence binding as the rest of the set (ServerConfigSuiteGate: Full tier
        // AND the remediation claim, same DevBridge hatch) — it is one more gated write surface,
        // not a separate purchase today. Split to its own dedicated bundle claim later if Adrian
        // wants to sell it as a distinct paid tier. Markup is ALSO wrapped in
        // @if (BuildModules.Premium) in Remediation.razor: the compile-time exclusion
        // (buildprofile.targets removes Pages\ServerConfiguration.razor + ConfigScripts\** from
        // community) is the real fail-closed layer; this FeatureGate id is the runtime layer for
        // a full build without the licence, plus ScriptExists as a third fail-closed check.
        gate.Register(
            new FeatureDescriptor(
                ServerHardening,
                "Server Configuration & Hardening",
                "Operations",
                "Structured preview + deep-linked apply for the Server Configuration and Hardening script (sp_configure baseline, surface-area lockdown, trace flags). Bound to a Full-tier licence carrying the remediation claim; the .sql payload itself is community-build-excluded.",
                Pattern: 2),
            () => SQLTriage.Data.BuildMode.DevBridgeActive
                  || Licensing.ServerConfigSuiteGate.IsAvailable(bundle));

        // ── Playbook Markdown rendering (SOFT-only; no licence dimension) ──
        // Toggles whether Remediation Playbook detail renders Markdown formatting
        // (headings, bold, fenced code) or is stripped to plain text. HARD gate is a
        // no-op (always "licensed") so IsEnabled reduces to the SOFT operator toggle,
        // authored on the Build Profile page. Default ON = render formatting.
        gate.Register(
            new FeatureDescriptor(
                PlaybookMarkdown,
                "Playbook Markdown",
                "Diagnostics",
                "Render Markdown (headings/bold/fenced code) in Remediation Playbook detail. Off strips to plain text.",
                Pattern: 1),
            () => true);

        // SOFT toggles stay at their default (on): build-profile exclusion is compile-time now,
        // so the dev nav always shows the full superset and FeatureGate.IsEnabled reduces to the
        // HARD licence gate (bundle payload present?).
        logger?.LogInformation(
            "[FeatureRegistrar] registered {Count} runtime-gated feature module(s).",
            gate.Descriptors.Count);
    }
}
