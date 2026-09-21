/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services.Capacity;

/// <summary>
/// Supplies the premium <see cref="ConsolidationModel"/> from the encrypted bundle.
/// Gating rule (per design decision): the Capacity / Consolidation engine unlocks ONLY when
/// the active bundle contains <c>Config/consolidation-model.json</c>. Free bundles never carry
/// it (the encryptor adds it to Full bundles only), so Free tier sees a locked teaser.
///
/// Mirrors <see cref="SQLTriage.Data.Services.HealthBenchmarkProvider"/>: lazy-load, cached,
/// invalidated on <see cref="IBundleAccessor.BundleStateChanged"/>.
///
/// DEBUG builds add a developer fallback: if the bundle has no model, it reads a gitignored
/// local file (<c>Config/consolidation-model.local.json</c>) so the unlocked path can be
/// exercised on a dev box without a Full licence. The fallback is compiled out of Release.
/// </summary>
public interface IConsolidationModelProvider
{
    /// <summary>The active model, or null when the bundle carries no premium model (locked).</summary>
    ConsolidationModel? Current { get; }

    /// <summary>True when a model is available — i.e. the Premium Consolidation engine is unlocked.</summary>
    bool IsUnlocked { get; }

    /// <summary>True only when the model came from the encrypted bundle (not the DEBUG dev fallback).</summary>
    bool IsLicensed { get; }
}

/// <inheritdoc cref="IConsolidationModelProvider"/>
public sealed class ConsolidationModelProvider : IConsolidationModelProvider
{
    public const string BundlePath = "Config/consolidation-model.json";
    private const string DevFallbackFile = "consolidation-model.local.json";

    private readonly ILogger<ConsolidationModelProvider> _logger;
    private readonly IBundleAccessor _bundle;
    private readonly object _lock = new();
    private ConsolidationModel? _cached;
    private bool _loaded;
    private bool _fromBundle;

    public ConsolidationModelProvider(ILogger<ConsolidationModelProvider> logger, IBundleAccessor bundle)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        _bundle.BundleStateChanged += (_, _) => { lock (_lock) { _loaded = false; _cached = null; _fromBundle = false; } };
    }

    public ConsolidationModel? Current
    {
        get
        {
            if (_loaded) return _cached;
            lock (_lock)
            {
                if (_loaded) return _cached;
                _cached = Load(out _fromBundle);
                _loaded = true;
            }
            return _cached;
        }
    }

    public bool IsUnlocked => Current is not null;

    public bool IsLicensed
    {
        get { _ = Current; return _fromBundle; }
    }

    private ConsolidationModel? Load(out bool fromBundle)
    {
        fromBundle = false;

        // Demo refusal (2026-08-05 ruling). The premium model is packed by tier, not by mint shape,
        // so a -Demo bundle carries Config/consolidation-model.json exactly as a paid Full bundle
        // does — presence of the file was never a statement about who is entitled to the engine.
        // The read is refused here rather than at the page, because this provider is what
        // PremiumConsolidationEngine and ConsolidationCollector both ask; a page-level test would
        // have left the engine reachable from the collector.
        //
        // The DEBUG dev fallback below is deliberately NOT refused: it is compiled out of Release
        // and its whole purpose is exercising the unlocked path without a licence.
        var isDemoBundle = FullTierGate.Evaluate(_bundle) == FullTierState.DemoLicence;
        if (isDemoBundle)
        {
            _logger.LogInformation(
                "[Consolidation] Bundle is a time-boxed demo mint; the premium model is not read — engine locked.");
        }
        else
        {
            var text = _bundle.GetText(BundlePath);
            if (text is not null)
            {
                var fromBundleModel = Parse(text, "bundle");
                if (fromBundleModel is not null)
                {
                    fromBundle = true;
                    _logger.LogInformation(
                        "[Consolidation] Premium model loaded from bundle (tier={Tier}, schema={Schema}).",
                        _bundle.Tier, fromBundleModel.SchemaVersion);
                    return fromBundleModel;
                }
            }
        }

#if DEBUG
        var devPath = Path.Combine(AppContext.BaseDirectory, "Config", DevFallbackFile);
        if (!File.Exists(devPath))
            devPath = Path.Combine(AppContext.BaseDirectory, DevFallbackFile);
        if (File.Exists(devPath))
        {
            try
            {
                var devModel = Parse(File.ReadAllText(devPath), "DEBUG dev fallback");
                if (devModel is not null)
                {
                    _logger.LogWarning(
                        "[Consolidation] Premium model loaded from DEBUG dev fallback {Path}. " +
                        "This path is compiled OUT of Release — production requires the bundle.",
                        devPath);
                    return devModel;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Consolidation] DEBUG dev fallback present but failed to parse.");
            }
        }
#endif

        // Two different reasons for the same lock, and the log says which. "No premium model in
        // bundle" would be an assertion about the bundle's CONTENTS on the demo path, where the
        // contents were never looked at.
        if (isDemoBundle)
            _logger.LogInformation(
                "[Consolidation] Engine locked: the active bundle is a demo mint, so its premium model was not read (tier={Tier}).",
                _bundle.Tier);
        else
            _logger.LogInformation(
                "[Consolidation] No premium model in bundle (tier={Tier}); engine locked — page shows upgrade teaser.",
                _bundle.Tier);
        return null;
    }

    private ConsolidationModel? Parse(string text, string sourceLabel)
    {
        try
        {
            var model = JsonSerializer.Deserialize<ConsolidationModel>(
                text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (model is null || model.SchemaVersion <= 0)
            {
                _logger.LogWarning("[Consolidation] Model from {Source} parsed empty/invalid.", sourceLabel);
                return null;
            }
            return model;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consolidation] Failed to parse consolidation model from {Source}.", sourceLabel);
            return null;
        }
    }
}
