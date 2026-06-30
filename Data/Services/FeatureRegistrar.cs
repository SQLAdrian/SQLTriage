/* In the name of God, the Merciful, the Compassionate */
/*
 * FeatureRegistrar — one place that declares the runtime-gated feature modules and wires each into
 * the IFeatureGate (HARD licence gate) with display metadata.
 *
 * Called once at startup AFTER LicenseService.Initialize (so HARD gates see the resolved bundle/tier).
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

    public static void RegisterAll(IServiceProvider sp)
    {
        var gate    = sp.GetRequiredService<IFeatureGate>();
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
        // covers a full build running on a non-Adrian machine. Single boolean claim from the
        // bundle manifest — explicit false denies, absent (pre-claim bundle) permits.
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
        // Fail-CLOSED: only an explicit bundle remediation claim (or DevBridge on a dev
        // machine) unlocks the preview→approve→apply→verify surface. Mirrors the gate-2
        // capability check (BundleBackedRemediationCapability) so nav visibility and the
        // runtime gate agree.
        gate.Register(
            new FeatureDescriptor(
                Remediation,
                "Apply Remediations",
                "Operations",
                "Gated preview→approve→apply→verify remediation surface (MAXDOP). A write capability: the bundle remediation claim gates it, fail-closed.",
                Pattern: 2),
            () => SQLTriage.Data.BuildMode.DevBridgeActive || bundle.Features.Remediation);

        // SOFT toggles stay at their default (on): build-profile exclusion is compile-time now,
        // so the dev nav always shows the full superset and FeatureGate.IsEnabled reduces to the
        // HARD licence gate (bundle payload present?).
        logger?.LogInformation(
            "[FeatureRegistrar] registered {Count} runtime-gated feature module(s).",
            gate.Descriptors.Count);
    }
}
