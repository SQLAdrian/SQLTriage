/* In the name of God, the Merciful, the Compassionate */

#nullable enable

namespace SQLTriage.Data.Services.Licensing;

/// <summary>
/// Sealed implementation of <see cref="IBundleAccessor"/>.
/// Holds an immutable snapshot of the active <see cref="BundleManifest"/>.
/// Thread-safe: the manifest reference is replaced atomically under a lock;
/// all reads after the lock see the new snapshot.
///
/// Registered as a singleton. <see cref="LicenseService.Initialize"/> calls
/// <see cref="Replace"/> at startup; the same method is called on activation
/// or deactivation. Consumers subscribe to <see cref="BundleStateChanged"/>
/// to refresh their own cached state.
/// </summary>
public sealed class BundleAccessor : IBundleAccessor
{
    private readonly object _lock = new();
    private BundleManifest? _manifest;
    private Tier _tier = Tier.Free;

    // ── IBundleAccessor ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool IsUnlocked
    {
        get { lock (_lock) return _manifest is not null; }
    }

    /// <inheritdoc/>
    public Tier Tier
    {
        get { lock (_lock) return _tier; }
    }

    /// <inheritdoc/>
    public string? ClientName
    {
        get
        {
            lock (_lock)
                return string.IsNullOrEmpty(_manifest?.ClientName) ? null : _manifest.ClientName;
        }
    }

    /// <inheritdoc/>
    public BundleFeatures Features
    {
        get
        {
            lock (_lock)
            {
                if (_manifest is null)
                    return new BundleFeatures(false, false, false, Array.Empty<int>());

                var f = _manifest.Features;
                return new BundleFeatures(
                    f.RagEnabled,
                    f.SpBlitzImport,
                    f.FullCorpus,
                    f.CheckIds.AsReadOnly());
            }
        }
    }

    /// <inheritdoc/>
    public bool IsCheckPermitted(int checkId)
    {
        lock (_lock)
        {
            if (_manifest is null) return false;

            // Full tier with an empty allow-list → all checks are permitted
            if (_tier == Tier.Full && _manifest.Features.CheckIds.Count == 0)
                return true;

            return _manifest.Features.CheckIds.Contains(checkId);
        }
    }

    /// <inheritdoc/>
    public string? GetText(string relativePath)
    {
        lock (_lock)
        {
            if (_manifest is null) return null;
            return _manifest.Files.TryGetValue(relativePath, out var text) ? text : null;
        }
    }

    /// <inheritdoc/>
    public byte[]? GetBytes(string relativePath)
    {
        var text = GetText(relativePath);
        return text is null ? null : System.Text.Encoding.UTF8.GetBytes(text);
    }

    /// <inheritdoc/>
    public IEnumerable<string> EnumerateCorpusYamlHandles()
    {
        lock (_lock)
        {
            if (_manifest is null) return Enumerable.Empty<string>();

            // Snapshot keys under lock to avoid mutation during enumeration
            return _manifest.Corpus.Keys
                .Where(k => k.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    /// <inheritdoc/>
    public string? ReadCorpusYaml(string handle)
    {
        lock (_lock)
        {
            if (_manifest is null) return null;
            // Case-insensitive lookup matching the encryptor's OrdinalIgnoreCase convention
            var key = _manifest.Corpus.Keys
                .FirstOrDefault(k => string.Equals(k, handle, StringComparison.OrdinalIgnoreCase));
            return key is null ? null : _manifest.Corpus[key];
        }
    }

    /// <inheritdoc/>
    public string? ReadCorpusSqlFallback(string handle)
    {
        lock (_lock)
        {
            if (_manifest is null) return null;

            // Replace .yaml extension with .sql (same stem)
            var stem = System.IO.Path.GetFileNameWithoutExtension(handle);
            var sqlHandle = stem + ".sql";

            var key = _manifest.Corpus.Keys
                .FirstOrDefault(k => string.Equals(k, sqlHandle, StringComparison.OrdinalIgnoreCase));
            return key is null ? null : _manifest.Corpus[key];
        }
    }

    /// <inheritdoc/>
    public event EventHandler? BundleStateChanged;

    // ── Mutation API (used only by LicenseService) ──────────────────────────

    /// <summary>
    /// Replaces the active manifest + tier and fires <see cref="BundleStateChanged"/>.
    /// Pass <paramref name="newManifest"/> = null to reset to unlocked=false (no bundle).
    /// </summary>
    public void Replace(BundleManifest? newManifest, Tier tier)
    {
        lock (_lock)
        {
            _manifest = newManifest;
            _tier = tier;
        }
        BundleStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
