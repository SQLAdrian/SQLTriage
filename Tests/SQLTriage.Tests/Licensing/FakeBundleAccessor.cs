/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// Test double for <see cref="IBundleAccessor"/>.
/// Mutable maps so tests can swap bundle state and raise state-change events.
/// </summary>
public sealed class FakeBundleAccessor : IBundleAccessor
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _corpus = new(StringComparer.Ordinal);

    public bool IsUnlocked { get; set; } = true;
    public Tier Tier { get; set; } = Tier.Full;
    public string? ClientName { get; set; } = "TEST";
    public BundleFeatures Features { get; set; } = new(false, true, true, Array.Empty<int>());

    public event EventHandler? BundleStateChanged;

    public bool IsCheckPermitted(int checkId) =>
        Features.PermittedCheckIds.Count == 0 || Features.PermittedCheckIds.Contains(checkId);

    public string? GetText(string path) =>
        _files.TryGetValue(path, out var v) ? v : null;

    public byte[]? GetBytes(string path) =>
        GetText(path) is { } s ? System.Text.Encoding.UTF8.GetBytes(s) : null;

    public IEnumerable<string> EnumerateCorpusYamlHandles() =>
        _corpus.Keys.Where(k => k.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    public string? ReadCorpusYaml(string handle) =>
        _corpus.TryGetValue(handle, out var v) ? v : null;

    public string? ReadCorpusSqlFallback(string handle) =>
        _corpus.TryGetValue(handle, out var v) ? v : null;

    public string? TryGetReportAsset(string reportId) => null;

    public IEnumerable<string> EnumerateReportHandles() => Enumerable.Empty<string>();

    // ── Builder helpers ─────────────────────────────────────────────────────

    /// <summary>Registers a file in the fake bundle. Returns <c>this</c> for fluent chaining.</summary>
    public FakeBundleAccessor PutFile(string path, string contents)
    {
        _files[path] = contents;
        return this;
    }

    /// <summary>Registers a corpus entry. Returns <c>this</c> for fluent chaining.</summary>
    public FakeBundleAccessor PutCorpus(string handle, string contents)
    {
        _corpus[handle] = contents;
        return this;
    }

    /// <summary>Fires <see cref="BundleStateChanged"/> so consumer services invalidate their caches.</summary>
    public void RaiseStateChanged() => BundleStateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Sets <see cref="IsUnlocked"/> = false to simulate the locked / unauthenticated state.</summary>
    public FakeBundleAccessor SetLocked()
    {
        IsUnlocked = false;
        return this;
    }

    /// <summary>Mimics BundleAccessor.Replace for test scenarios.</summary>
    public void Replace(BundleManifest manifest, Tier tier)
    {
        if (manifest == null)
        {
            _files.Clear();
            _corpus.Clear();
            Tier = tier;
            ClientName = null;
            Features = new BundleFeatures(false, false, false, Array.Empty<int>());
            IsUnlocked = false;
            return;
        }
        foreach (var kvp in manifest.Files)
        {
            _files[kvp.Key] = kvp.Value;
        }
        foreach (var kvp in manifest.Corpus)
        {
            _corpus[kvp.Key] = kvp.Value;
        }
        Tier = tier;
        ClientName = manifest.ClientName;
        Features = new BundleFeatures(
            manifest.Features.RagEnabled,
            manifest.Features.SpBlitzImport,
            manifest.Features.FullCorpus,
            manifest.Features.CheckIds ?? new List<int>());
        IsUnlocked = true;
    }

    /// <summary>Alias for PutFile — fluent-friendly for test wiring.</summary>
    public FakeBundleAccessor AddFile(string path, string contents) => PutFile(path, contents);
}
