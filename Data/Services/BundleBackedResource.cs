/* In the name of God, the Merciful, the Compassionate */
/*
 * BundleBackedResource<T> — the reusable core of the "shell + bundled brains" pattern.
 *
 * Lazily loads a typed payload T from a file inside the encrypted licence bundle
 * (IBundleAccessor.GetText), caches it, and invalidates on BundleStateChanged. Optional
 * DEBUG-only local-file fallback so the feature can be exercised on a dev box without a licence
 * (compiled OUT of Release).
 *
 * This generalises the hand-rolled load/cache/bust in ConsolidationModelProvider,
 * HealthBenchmarkProvider, LicensingEstimator, GovernanceWeightsProvider, etc. New gated
 * features should COMPOSE one of these instead of re-writing the dance:
 *
 *     private readonly BundleBackedResource<MyModel> _res;
 *     public MyProvider(ILogger<MyProvider> log, IBundleAccessor bundle)
 *         => _res = new(log, bundle, "Config/my-model.json", debugFallbackFile: "my-model.local.json");
 *     public MyModel? Current   => _res.Current;
 *     public bool     IsUnlocked => _res.IsAvailable;   // wire into IFeatureGate.Register(...)
 *
 * The feature SHELL (page/nav) always ships; this supplies the VALUE only when the licensed
 * bundle carries the payload — inert/teaser in public, full when licensed. (It protects the
 * value/IP, not the shell code; to keep code itself out of the public build, use build-exclusion.)
 */

#nullable enable

using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services;

public sealed class BundleBackedResource<T> where T : class
{
    private readonly ILogger _logger;
    private readonly IBundleAccessor _bundle;
    private readonly string _bundlePath;
    private readonly Func<string, T?> _parse;
    private readonly string? _debugFallbackFile;

    private readonly object _lock = new();
    private T? _cached;
    private bool _loaded;
    private bool _fromBundle;

    /// <param name="bundlePath">Forward-slash bundle key, e.g. "Config/my-model.json".</param>
    /// <param name="parse">Custom parser; defaults to case-insensitive System.Text.Json of T.</param>
    /// <param name="debugFallbackFile">DEBUG-only filename under the app dir (or its Config/) to read
    /// when the bundle has no payload. Gitignore it; it is compiled out of Release.</param>
    public BundleBackedResource(
        ILogger logger,
        IBundleAccessor bundle,
        string bundlePath,
        Func<string, T?>? parse = null,
        string? debugFallbackFile = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        _bundlePath = bundlePath;
        _parse = parse ?? DefaultJsonParse;
        _debugFallbackFile = debugFallbackFile;
        _bundle.BundleStateChanged += (_, _) =>
        {
            lock (_lock) { _loaded = false; _cached = null; _fromBundle = false; }
        };
    }

    /// <summary>Active payload, or null when the bundle carries none (feature locked).</summary>
    public T? Current
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

    /// <summary>True when a payload is available — i.e. the gated feature is unlocked.</summary>
    public bool IsAvailable => Current is not null;

    /// <summary>True only when the payload came from the encrypted bundle (not the DEBUG fallback).</summary>
    public bool IsLicensed
    {
        get { _ = Current; return _fromBundle; }
    }

    private T? Load(out bool fromBundle)
    {
        fromBundle = false;

        var text = _bundle.GetText(_bundlePath);
        if (text is not null)
        {
            var m = SafeParse(text, "bundle");
            if (m is not null)
            {
                fromBundle = true;
                _logger.LogInformation("[BundleBacked] {Path} loaded from bundle (tier={Tier}).", _bundlePath, _bundle.Tier);
                return m;
            }
        }

#if DEBUG
        if (_debugFallbackFile is not null)
        {
            foreach (var p in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Config", _debugFallbackFile),
                Path.Combine(AppContext.BaseDirectory, _debugFallbackFile),
            })
            {
                if (!File.Exists(p)) continue;
                try
                {
                    var m = SafeParse(File.ReadAllText(p), "DEBUG fallback");
                    if (m is not null)
                    {
                        _logger.LogWarning(
                            "[BundleBacked] {Path} loaded from DEBUG fallback {File} — compiled OUT of Release.",
                            _bundlePath, p);
                        return m;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[BundleBacked] DEBUG fallback present but failed to parse for {Path}.", _bundlePath);
                }
            }
        }
#endif

        _logger.LogInformation("[BundleBacked] {Path} not in bundle (tier={Tier}) — feature locked.", _bundlePath, _bundle.Tier);
        return null;
    }

    private T? SafeParse(string text, string source)
    {
        try
        {
            return _parse(text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BundleBacked] failed to parse {Path} from {Source}.", _bundlePath, source);
            return null;
        }
    }

    private static T? DefaultJsonParse(string text) =>
        JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
}
