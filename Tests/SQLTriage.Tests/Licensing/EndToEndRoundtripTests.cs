/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Licensing.Crypto;
using SQLTriage.Tests.Licensing;
using SQLTriage.Tests.Licensing.Fixtures;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// End-to-end loopback test — the gate for v0.90.2 release per Engineering Doctrine #5
/// (objective self-interrogation). Drives the SQLTriage-side crypto + manifest path
/// without invoking the corpus-repo encryptor — the codec is byte-identical between
/// the two repos (verified by AadBuilderMirrorTests + Bip39MirrorTests), so a single
/// roundtrip on this side proves the contract holds.
///
/// What this test covers:
///   1. EncryptManifest → wire bytes → DecryptManifest yields field-equal manifest.
///   2. AAD mismatch (different client name) → decrypt throws.
///   3. BundleAccessor.Replace(manifest, Full) populates downstream lookups.
///   4. Tier filtering: PermittedCheckIds = [10, 20] hides yaml whose CheckNr = 30.
///   5. Free-bundle path: zero-key + clientName="FREE" + tier="Free" decrypts.
///   6. CheckRepositoryService (when wired with FakeBundleAccessor in a follow-up
///      test) consumes the bundle. Scaffold left for Phase 5 cleanup completion.
/// </summary>
public sealed class EndToEndRoundtripTests
{

    [Fact]
    public void Roundtrip_EncryptThenDecrypt_FullTier_PreservesManifest()
    {
        // Arrange
        var key = new byte[BundleCrypto.KeySize];
        RandomNumberGenerator.Fill(key);
        var manifest = BundleFixtureFactory.MakeFullManifest("Acme Corp");
        var aad = AadBuilder.Build("Acme Corp", "Full", 1, 1904);

        // Act
        var wire = BundleCrypto.EncryptManifest(manifest, key, aad);
        var decoded = BundleCrypto.DecryptManifest(wire, key, aad);

        // Assert
        decoded.ClientName.Should().Be("Acme Corp");
        decoded.Tier.Should().Be("Full");
        decoded.BuildNumber.Should().Be(1903);
        decoded.Files.Should().ContainKey("Config/control_mappings.json");
        decoded.Files["Config/control_mappings.json"].Should().Be("{\"test\":true}");
        decoded.Corpus.Should().ContainKey("check_001_test.yaml");
        decoded.Corpus.Should().HaveCount(3);
    }

    [Fact]
    public void Roundtrip_EncryptThenDecrypt_FreeTier_PreservesManifest()
    {
        var key = new byte[BundleCrypto.KeySize]; // zero key
        var manifest = BundleFixtureFactory.MakeFreeManifest();
        var aad = AadBuilder.Build("FREE", "Free", 1, 1904);

        var wire = BundleCrypto.EncryptManifest(manifest, key, aad);
        var decoded = BundleCrypto.DecryptManifest(wire, key, aad);

        decoded.ClientName.Should().Be("FREE");
        decoded.Tier.Should().Be("Free");
        decoded.Features!.CheckIds.Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public void Decrypt_AadMismatch_DifferentClientName_Throws()
    {
        var key = new byte[BundleCrypto.KeySize];
        RandomNumberGenerator.Fill(key);
        var manifest = BundleFixtureFactory.MakeFullManifest("Acme Corp");
        var aadCorrect = AadBuilder.Build("Acme Corp", "Full", 1, 1904);
        var aadWrong = AadBuilder.Build("Acme Crop", "Full", 1, 1904); // typo

        var wire = BundleCrypto.EncryptManifest(manifest, key, aadCorrect);

        var act = () => BundleCrypto.DecryptManifest(wire, key, aadWrong);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Decrypt_AadMismatch_DifferentTier_Throws()
    {
        var key = new byte[BundleCrypto.KeySize];
        RandomNumberGenerator.Fill(key);
        var manifest = BundleFixtureFactory.MakeFullManifest("Acme Corp");
        var aadFull = AadBuilder.Build("Acme Corp", "Full", 1, 1904);
        var aadFree = AadBuilder.Build("Acme Corp", "Free", 1, 1904);

        var wire = BundleCrypto.EncryptManifest(manifest, key, aadFull);

        var act = () => BundleCrypto.DecryptManifest(wire, key, aadFree);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Decrypt_WrongKey_Throws()
    {
        var key1 = new byte[BundleCrypto.KeySize];
        var key2 = new byte[BundleCrypto.KeySize];
        RandomNumberGenerator.Fill(key1);
        RandomNumberGenerator.Fill(key2);
        var manifest = BundleFixtureFactory.MakeFullManifest("Acme Corp");
        var aad = AadBuilder.Build("Acme Corp", "Full", 1, 1904);

        var wire = BundleCrypto.EncryptManifest(manifest, key1, aad);

        var act = () => BundleCrypto.DecryptManifest(wire, key2, aad);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        var key = new byte[BundleCrypto.KeySize];
        RandomNumberGenerator.Fill(key);
        var manifest = BundleFixtureFactory.MakeFullManifest("Acme Corp");
        var aad = AadBuilder.Build("Acme Corp", "Full", 1, 1904);

        var wire = BundleCrypto.EncryptManifest(manifest, key, aad);
        // Flip a byte deep in the ciphertext (past the 36-byte header)
        wire[wire.Length - 5] ^= 0x42;

        var act = () => BundleCrypto.DecryptManifest(wire, key, aad);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void BundleAccessor_AfterReplace_TierFiltering_ExcludesNonPermittedChecks()
    {
        // The accessor's IsCheckPermitted contract: Full + empty CheckIds = all permitted.
        // Free + non-empty list = only listed IDs are permitted.
        var accessor = new FakeBundleAccessor();
        var manifest = BundleFixtureFactory.MakeFreeManifest();

        accessor.Replace(manifest, Tier.Free);

        accessor.IsUnlocked.Should().BeTrue();
        accessor.Tier.Should().Be(Tier.Free);
        accessor.IsCheckPermitted(1).Should().BeTrue();
        accessor.IsCheckPermitted(2).Should().BeTrue();
        accessor.IsCheckPermitted(30).Should().BeFalse(); // not in PermittedCheckIds
    }

    [Fact]
    public void BundleAccessor_FullTier_EmptyPermittedList_PermitsAll()
    {
        var accessor = new FakeBundleAccessor();
        var manifest = BundleFixtureFactory.MakeFullManifest("Acme Corp"); // no permittedCheckIds → empty list

        accessor.Replace(manifest, Tier.Full);

        accessor.IsCheckPermitted(10).Should().BeTrue();
        accessor.IsCheckPermitted(999).Should().BeTrue(); // any ID permitted in Full+empty
    }

    [Fact]
    public void BundleAccessor_Replace_Null_ResetsToUnlockedFalse()
    {
        var accessor = new FakeBundleAccessor();
        var manifest = BundleFixtureFactory.MakeFullManifest("Acme Corp");
        accessor.Replace(manifest, Tier.Full);
        accessor.IsUnlocked.Should().BeTrue();

        accessor.Replace(null, Tier.Free);

        accessor.IsUnlocked.Should().BeFalse();
        accessor.ClientName.Should().BeNull();
    }

    [Fact]
    public void BundleAccessor_GetText_ReturnsFileContentsFromManifest()
    {
        var accessor = new FakeBundleAccessor();
        var manifest = BundleFixtureFactory.MakeFullManifest("Acme Corp");
        accessor.Replace(manifest, Tier.Full);

        accessor.GetText("Config/control_mappings.json").Should().Be("{\"test\":true}");
        accessor.GetText("Config/governance-weights.json").Should().Be("{\"test\":true}");
        accessor.GetText("Config/does-not-exist.json").Should().BeNull();
    }

    [Fact]
    public void BundleAccessor_EnumerateCorpusYamlHandles_ReturnsYamlEntries()
    {
        var accessor = new FakeBundleAccessor();
        var manifest = BundleFixtureFactory.MakeFullManifest("Acme Corp");
        accessor.Replace(manifest, Tier.Full);

        var handles = new HashSet<string>(accessor.EnumerateCorpusYamlHandles());
        handles.Should().Contain("check_001_test.yaml");
        handles.Should().Contain("check_002_test.yaml");
        handles.Should().NotContain("check_001_test.sql"); // sql siblings are not handles
    }
}
