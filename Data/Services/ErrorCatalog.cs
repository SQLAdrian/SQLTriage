/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Provides human-friendly error guidance loaded from Config/error-catalog.json in the bundle.
    /// When the bundle state changes, the catalog is reloaded from the new bundle content.
    /// Free-state apps boot with an empty catalog — callers degrade gracefully.
    /// </summary>
    public interface IErrorCatalog
    {
        /// <summary>Retrieve a single entry by its stable error code.</summary>
        ErrorCatalogEntry? Get(string errorCode);

        /// <summary>Get all entries in a category.</summary>
        IReadOnlyList<ErrorCatalogEntry> GetByCategory(string category);

        /// <summary>Search entries by keyword in UserMessage or Remediation.</summary>
        IReadOnlyList<ErrorCatalogEntry> Search(string keyword);

        /// <summary>Get the formatted message for a specific audience.</summary>
        string GetMessage(string errorCode, string audience = ErrorAudiences.Dba, params object?[] args);

        /// <summary>Reload catalog from the active bundle.</summary>
        Task ReloadAsync();

        /// <summary>Total entries currently loaded.</summary>
        int Count { get; }
    }

    public sealed class ErrorCatalog : IErrorCatalog, IDisposable
    {
        private readonly ILogger<ErrorCatalog> _logger;
        private readonly IBundleAccessor _bundle;
        private readonly ConcurrentDictionary<string, ErrorCatalogEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public int Count => _entries.Count;

        public ErrorCatalog(ILogger<ErrorCatalog> logger, IBundleAccessor bundle)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));

            // Initial load (fire-and-forget is safe; callers can await ReloadAsync if needed)
            Load();

            // Reload whenever the active bundle is replaced.
            _bundle.BundleStateChanged += (_, _) => Load();
        }

        public ErrorCatalogEntry? Get(string errorCode)
            => _entries.TryGetValue(errorCode, out var entry) ? entry : null;

        public IReadOnlyList<ErrorCatalogEntry> GetByCategory(string category)
            => _entries.Values.Where(e => e.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();

        public IReadOnlyList<ErrorCatalogEntry> Search(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return Array.Empty<ErrorCatalogEntry>();
            var k = keyword.Trim();
            return _entries.Values.Where(e =>
                e.ErrorCode.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                e.UserMessage.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                e.Remediation.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                e.GovernanceImpact.Contains(k, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        public string GetMessage(string errorCode, string audience = ErrorAudiences.Dba, params object?[] args)
        {
            if (!_entries.TryGetValue(errorCode, out var entry))
            {
                _logger.LogWarning("ErrorCatalog missing entry for {ErrorCode}", errorCode);
                return $"[{errorCode}] An error occurred.";
            }

            var template = entry.AudienceMessages.TryGetValue(audience, out var msg)
                ? msg
                : entry.UserMessage;

            try
            {
                return args.Length > 0 ? string.Format(template, args) : template;
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(ex, "Format failed for {ErrorCode} with {ArgCount} args", errorCode, args.Length);
                return template;
            }
        }

        public Task ReloadAsync()
        {
            Load();
            return Task.CompletedTask;
        }

        private void Load()
        {
            try
            {
                var catalogJson = _bundle.GetText("Config/error-catalog.json");
                if (catalogJson == null)
                {
                    _logger.LogInformation("error-catalog.json not found in bundle; no entries loaded");
                    return;
                }

                var wrapper = JsonSerializer.Deserialize<ErrorCatalogFile>(catalogJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var entries = wrapper?.Entries;
                if (entries == null)
                {
                    _logger.LogWarning("error-catalog.json deserialized to null or missing 'entries'; keeping existing entries");
                    return;
                }

                var newDict = new Dictionary<string, ErrorCatalogEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in entries)
                {
                    if (string.IsNullOrWhiteSpace(e.ErrorCode))
                    {
                        _logger.LogWarning("Skipping catalog entry with missing ErrorCode");
                        continue;
                    }
                    newDict[e.ErrorCode] = e;
                }

                _entries.Clear();
                foreach (var kvp in newDict)
                    _entries[kvp.Key] = kvp.Value;

                _logger.LogInformation("Loaded {Count} error-catalog entries from bundle", _entries.Count);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Malformed error-catalog.json — keeping existing entry set");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load error-catalog.json");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }
}
