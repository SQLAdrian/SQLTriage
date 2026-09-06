/* In the name of God, the Merciful, the Compassionate */
/*
 * FreeBundleCodec — the wire format for the hardened COMMUNITY (free) bundle. SINGLE source of truth
 * for how free-bundle.dat is packed/unpacked. The (off-GitHub) CorpusEncryptor mirrors this verbatim.
 *
 * Wire layout (replaces the old "single zero-key SLBN blob"):
 *   bytes 0..3   : generation (int32 LE)         — plaintext hint; which derivation generation packed this
 *   bytes 4..19  : contentSalt (16 bytes)         — random per bundle; salts the body key
 *   bytes 20..N  : standard SLBN blob              — GCM(bodyKey, AAD) over gzip(manifest)
 *
 *   outerKey = FreeKeyDerivation.DeriveOuterKey(generation)
 *   bodyKey  = FreeKeyDerivation.DeriveBodyKey(outerKey, contentSalt, generation)
 *   AAD      = AadBuilder.Build("FREE", "Free", BundleVersion, generation)
 *
 * The plaintext generation prefix lets a STALE app (compiled with an older Generation) detect that a
 * newer bundle needs a newer app: derivation uses the APP's Generation, so a generation mismatch
 * makes the body fail to decrypt — the caller compares the prefix to its own Generation and shows an
 * "upgrade the app" message instead of a generic "bundle corrupt".
 *
 * The generation is a deliberate, per-RELEASE rotation counter — NOT the exact build number (patch
 * builds must stay compatible or every dev rebuild would brick the free bundle). Bump it + re-bake to
 * invalidate any extracted key.
 */

#nullable enable

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SQLTriage.Data.Services.Licensing.Crypto;

public static class FreeBundleCodec
{
    /// <summary>Current free-bundle derivation generation. Bump per release to rotate the key.</summary>
    public const int Generation = 1;

    /// <summary>Codec/manifest version baked into the AAD (matches BundleCrypto's generation).</summary>
    public const int BundleVersion = 1;

    private const string FreeClientName = "FREE";
    private const string FreeTierName = "Free";
    private const int ContentSaltSize = 16;
    private const int GenerationOffset = 0;
    private const int SaltOffset = 4;
    private const int FrameHeaderSize = SaltOffset + ContentSaltSize; // 20

    /// <summary>
    /// Pack a manifest into the hardened free-bundle wire format for <paramref name="generation"/>
    /// (defaults to the current <see cref="Generation"/>).
    /// </summary>
    public static byte[] Pack(BundleManifest manifest, int? generation = null)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));
        int gen = generation ?? Generation;

        var contentSalt = RandomNumberGenerator.GetBytes(ContentSaltSize);
        var outerKey = FreeKeyDerivation.DeriveOuterKey(gen);
        byte[]? bodyKey = null;
        try
        {
            bodyKey = FreeKeyDerivation.DeriveBodyKey(outerKey, contentSalt, gen);
            var aad = AadBuilder.Build(FreeClientName, FreeTierName, BundleVersion, gen);
            var slbn = BundleCrypto.EncryptManifest(manifest, bodyKey, aad);

            var wire = new byte[FrameHeaderSize + slbn.Length];
            BinaryPrimitives.WriteInt32LittleEndian(wire.AsSpan(GenerationOffset, 4), gen);
            Buffer.BlockCopy(contentSalt, 0, wire, SaltOffset, ContentSaltSize);
            Buffer.BlockCopy(slbn, 0, wire, FrameHeaderSize, slbn.Length);
            return wire;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(outerKey);
            if (bodyKey is not null) CryptographicOperations.ZeroMemory(bodyKey);
        }
    }

    /// <summary>
    /// Unpack a hardened free bundle. Derives keys for <paramref name="appGeneration"/> (the running
    /// app's <see cref="Generation"/>); a generation mismatch surfaces as a decrypt failure. The
    /// bundle's own generation prefix is returned via <paramref name="bundleGeneration"/> so the
    /// caller can show an upgrade prompt. Throws on auth-tag/format failure.
    /// </summary>
    public static BundleManifest Unpack(byte[] wire, out int bundleGeneration, int? appGeneration = null)
    {
        if (wire is null || wire.Length < FrameHeaderSize + BundleCrypto.HeaderSize + 1)
            throw new ArgumentException("Free bundle too short / not the hardened format.", nameof(wire));

        bundleGeneration = BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(GenerationOffset, 4));
        int gen = appGeneration ?? Generation;

        var contentSalt = new byte[ContentSaltSize];
        Buffer.BlockCopy(wire, SaltOffset, contentSalt, 0, ContentSaltSize);

        var slbn = new byte[wire.Length - FrameHeaderSize];
        Buffer.BlockCopy(wire, FrameHeaderSize, slbn, 0, slbn.Length);

        var outerKey = FreeKeyDerivation.DeriveOuterKey(gen);
        byte[]? bodyKey = null;
        try
        {
            bodyKey = FreeKeyDerivation.DeriveBodyKey(outerKey, contentSalt, gen);
            var aad = AadBuilder.Build(FreeClientName, FreeTierName, BundleVersion, gen);
            return BundleCrypto.DecryptManifest(slbn, bodyKey, aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(outerKey);
            if (bodyKey is not null) CryptographicOperations.ZeroMemory(bodyKey);
        }
    }

    /// <summary>
    /// True if <paramref name="wire"/> looks like the hardened format (vs the legacy zero-key SLBN
    /// blob, which begins with the "SLBN" magic). Lets callers detect a not-yet-migrated bundle.
    /// </summary>
    public static bool IsHardenedFormat(byte[] wire)
    {
        if (wire is null || wire.Length < FrameHeaderSize) return false;
        // Legacy free bundles start directly with the SLBN magic 0x53 4C 42 4E.
        return !(wire[0] == 0x53 && wire[1] == 0x4C && wire[2] == 0x42 && wire[3] == 0x4E);
    }

    /// <summary>
    /// Reads the plaintext generation prefix WITHOUT decrypting. Used by the unlock path to decide,
    /// after a decrypt failure, whether the bundle is simply newer than this app (→ upgrade prompt).
    /// </summary>
    public static bool TryReadGeneration(byte[] wire, out int generation)
    {
        generation = 0;
        if (!IsHardenedFormat(wire) || wire.Length < SaltOffset) return false;
        generation = BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(GenerationOffset, 4));
        return true;
    }
}
