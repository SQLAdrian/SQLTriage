/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.IO;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Licensing.Crypto;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// ONE-SHOT migration: re-keys a LEGACY zero-key free-bundle.dat into the hardened FreeBundleCodec
/// format, in place, using the exact app-side crypto (so the result is guaranteed app-compatible).
///
/// It is a NO-OP unless the env var SQLTRIAGE_REKEY_BUNDLE points at a file — so normal test/CI runs
/// skip it. Run explicitly:
///   $env:SQLTRIAGE_REKEY_BUNDLE = "C:\GitHub\SQLTriage-dev\Config\free-bundle.dat"
///   dotnet test --filter FullyQualifiedName~FreeBundleMigration
/// A .legacy.bak backup of the original is written next to the target.
/// </summary>
public sealed class FreeBundleMigration
{
    private readonly ITestOutputHelper _out;
    public FreeBundleMigration(ITestOutputHelper output) => _out = output;

    [Fact]
    public void RekeyLegacyFreeBundle_WhenEnvPathSet()
    {
        var path = Environment.GetEnvironmentVariable("SQLTRIAGE_REKEY_BUNDLE");
        if (string.IsNullOrWhiteSpace(path))
        {
            _out.WriteLine("SQLTRIAGE_REKEY_BUNDLE not set — migration skipped (no-op).");
            return;
        }

        Assert.True(File.Exists(path), $"Bundle not found: {path}");
        var original = File.ReadAllBytes(path);

        if (FreeBundleCodec.IsHardenedFormat(original))
        {
            _out.WriteLine($"Already hardened ({original.Length} bytes) — nothing to do.");
            // Sanity: it must still unpack under this app's generation.
            FreeBundleCodec.Unpack(original, out var g);
            _out.WriteLine($"Verified: unpacks at generation {g}.");
            return;
        }

        // Decrypt the legacy zero-key bundle (AAD pinned to build 0).
        var legacyAad = AadBuilder.Build("FREE", "Free", 1, 0);
        var manifest = BundleCrypto.DecryptManifest(original, new byte[BundleCrypto.KeySize], legacyAad);
        _out.WriteLine($"Decrypted legacy manifest: tier={manifest.Tier}, corpus={manifest.Corpus.Count}, files={manifest.Files.Count}.");

        // Re-pack hardened.
        var hardened = FreeBundleCodec.Pack(manifest);

        // Safety backup, then atomic-ish replace.
        File.WriteAllBytes(path + ".legacy.bak", original);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, hardened);
        File.Delete(path);
        File.Move(tmp, path);

        // Verify the written file round-trips under THIS app's generation.
        var check = FreeBundleCodec.Unpack(File.ReadAllBytes(path), out var gen);
        Assert.Equal(manifest.Tier, check.Tier);
        Assert.Equal(manifest.Corpus.Count, check.Corpus.Count);
        _out.WriteLine($"Re-keyed → hardened ({hardened.Length} bytes, generation {gen}). Backup: {path}.legacy.bak");
    }
}
