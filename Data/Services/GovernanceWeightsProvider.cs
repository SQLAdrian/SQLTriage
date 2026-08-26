/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Provides access to the current <see cref="GovernanceWeights"/> instance.
    /// Phase 5: weights are loaded lazily from <see cref="IBundleAccessor"/> on first access.
    /// Falls back to built-in defaults when the bundle key is absent (free-state boot).
    /// <see cref="WeightsChanged"/> is now wired to <c>IBundleAccessor.BundleStateChanged</c>
    /// so downstream consumers (GovernanceService, FindingTranslator) are notified whenever
    /// a new bundle is loaded and weights may have changed.
    /// </summary>
    public interface IGovernanceWeightsProvider
    {
        /// <summary>Gets the current governance weights, loading from the bundle on first access.</summary>
        GovernanceWeights Current { get; }

        /// <summary>
        /// Raised when the active weights change (e.g. after a new bundle is decrypted or revoked).
        /// </summary>
        event EventHandler? WeightsChanged;
    }

    /// <inheritdoc cref="IGovernanceWeightsProvider"/>
    public class GovernanceWeightsProvider : IGovernanceWeightsProvider
    {
        private readonly ILogger<GovernanceWeightsProvider> _logger;
        private readonly IBundleAccessor _bundle;

        private readonly object _lock = new();
        private GovernanceWeights? _cached;
        private bool _loaded;

        /// <inheritdoc/>
        public event EventHandler? WeightsChanged;

        /// <summary>
        /// Initialises the provider. No I/O is performed here — loading is deferred
        /// to the first access of <see cref="Current"/>.
        /// </summary>
        public GovernanceWeightsProvider(ILogger<GovernanceWeightsProvider> logger, IBundleAccessor bundle)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));

            // Bust cache and re-notify consumers when the bundle is replaced.
            _bundle.BundleStateChanged += OnBundleStateChanged;
        }

        /// <inheritdoc/>
        public GovernanceWeights Current
        {
            get
            {
                // Fast path — no lock if already loaded.
                if (_loaded && _cached is not null)
                    return _cached;

                lock (_lock)
                {
                    if (_loaded && _cached is not null)
                        return _cached;

                    _cached = LoadFromBundle();
                    _loaded = true;
                }

                return _cached;
            }
        }

        // ─── Private ────────────────────────────────────────────────────────────────

        private void OnBundleStateChanged(object? sender, EventArgs e)
        {
            lock (_lock)
            {
                _loaded = false;
                _cached = null;
            }
            // Fire WeightsChanged so GovernanceService / FindingTranslator can bust their own caches.
            WeightsChanged?.Invoke(this, EventArgs.Empty);
        }

        private GovernanceWeights LoadFromBundle()
        {
            var text = _bundle.GetText("Config/governance-weights.json");
            if (text is null)
            {
                _logger.LogWarning(
                    "[GovernanceWeightsProvider] governance-weights.json not in current bundle (tier={Tier}). " +
                    "Using built-in defaults.",
                    _bundle.Tier);
                return new GovernanceWeights();
            }

            try
            {
                var weights = JsonSerializer.Deserialize<GovernanceWeights>(
                    text,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (weights is null)
                {
                    _logger.LogError(
                        "[GovernanceWeightsProvider] governance-weights.json deserialised to null. " +
                        "Using built-in defaults.");
                    return new GovernanceWeights();
                }

                _logger.LogInformation(
                    "[GovernanceWeightsProvider] Governance weights loaded from bundle (tier={Tier}).",
                    _bundle.Tier);
                return weights;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[GovernanceWeightsProvider] Failed to parse governance-weights.json from bundle. " +
                    "Using built-in defaults.");
                return new GovernanceWeights();
            }
        }
    }
}
