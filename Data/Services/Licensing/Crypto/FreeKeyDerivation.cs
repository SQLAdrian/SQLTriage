/* In the name of God, the Merciful, the Compassionate */
/*
 * FreeKeyDerivation — the deterministic key derivation for the COMMUNITY (free) bundle.
 *
 * WHY THIS EXISTS (see .handoff/HANDOFF-2026-06-30-free-bundle-hardening-SPEC.md):
 * The free bundle ships the full corpus. Previously it was encrypted with a ZERO key that is
 * literally in the open-source app, so anyone reading the source could decrypt it in minutes. This
 * helper replaces the zero key with a key DERIVED at runtime from scattered, generation-pinned
 * material:
 *   - the inputs are several unrelated-looking byte fragments combined with the bundle generation;
 *   - they are run through a slow KDF (PBKDF2-SHA256) to a 32-byte key.
 *
 * HONEST CEILING (doctrine #3 — do NOT oversell): any zero-input auto-unlock is ultimately
 * recoverable (a debugger reads the derived key out of memory at decrypt time). The goal is to raise
 * the STATIC reverse-engineering cost from "copy a zero array" to "decompile + trace a staged
 * derivation, and redo it when the generation rotates", NOT to achieve confidentiality. The
 * fragments below WILL be in the binary; the deterrent is scatter + KDF + generation rotation + the
 * two-stage body-key step — not secrecy of any single value.
 *
 * KDF choice: PBKDF2-SHA256 (pure BCL) rather than Argon2id, so the (off-GitHub) CorpusEncryptor can
 * mirror this with ZERO extra package dependencies. Memory-hardness buys nothing here — there is no
 * brute-force target (the fragments are in the binary); the attacker reads them, they don't guess.
 *
 * GENERATION ROTATION: the generation feeds the derivation, so a key extracted from one generation
 * does NOT open another generation's bundle. It is a constant that bumps deliberately per release
 * (NOT the exact build number — patch builds must stay compatible, or every dev rebuild would brick
 * the free bundle). See FreeBundleCodec.Generation. The CorpusEncryptor MUST use this EXACT same
 * derivation to bake a decryptable free bundle — a new "must agree" contract alongside the AAD
 * (reference_license_bundle_aad_contract).
 *
 * TWO-STAGE: DeriveOuterKey(gen) is combined with a per-bundle random contentSalt by
 * DeriveBodyKey(outerKey, contentSalt, gen) to produce the key for the actual manifest body, so two
 * bundles of the same generation still have different body keys and "lift the key bytes" requires
 * replicating the staged derivation.
 */

#nullable enable

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SQLTriage.Data.Services.Licensing.Crypto;

public static class FreeKeyDerivation
{
    public const int KeySize = 32;

    // PBKDF2 cost. ~tens of ms for one startup derivation; imposes a real per-iteration cost on any
    // brute force. Part of the must-agree contract with the CorpusEncryptor — changing it changes
    // the derived key.
    private const int Iterations = 210_000;

    // ── Scattered key fragments ────────────────────────────────────────────────
    // CUTOVER NOTE: for real scatter these should be relocated into several unrelated types/files (so
    // an attacker must find + order + combine them, not read one block). Kept together here for the
    // first cut; relocation is a mechanical refinement that does NOT change the derived key as long
    // as the concatenation order is preserved. Values are arbitrary, not secret.
    private static readonly byte[] FragA =
        { 0x53, 0x4C, 0x54, 0x46, 0x72, 0x65, 0x65, 0x9A, 0x21, 0xC4, 0x7E, 0x10, 0xBB, 0x05, 0xDE, 0x77 };
    private static readonly byte[] FragB =
        { 0x12, 0xF0, 0x8C, 0x44, 0x6D, 0x29, 0xA1, 0x5B, 0xE3, 0x90, 0x3F, 0xC7, 0x14, 0x88, 0x2A, 0xD6 };
    private static readonly byte[] FragC =
        { 0xBE, 0x07, 0x55, 0x9C, 0x31, 0xEA, 0x4D, 0x72, 0x18, 0xA6, 0xFB, 0x60, 0x2C, 0x93, 0x0E, 0xC1 };
    private static readonly byte[] FragD =
        { 0x6F, 0xD4, 0x83, 0x1B, 0x57, 0xAC, 0x09, 0xE8, 0x35, 0x70, 0xCA, 0x42, 0x9D, 0x16, 0xB3, 0x5E };

    // Fixed salts for the two stages. Distinct so outer and body keys never collide.
    private static readonly byte[] OuterSalt = "SQLTriage.FreeBundle.Outer.v1"u8.ToArray();
    private static readonly byte[] BodySalt  = "SQLTriage.FreeBundle.Body.v1"u8.ToArray();

    /// <summary>
    /// Stage-1 outer key: derived from the scattered fragments + the bundle generation.
    /// </summary>
    public static byte[] DeriveOuterKey(int generation)
    {
        Span<byte> gen = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(gen, generation);

        var material = Concat(FragA, FragB, FragC, FragD, gen.ToArray());
        try { return Pbkdf2(material, OuterSalt); }
        finally { Array.Clear(material, 0, material.Length); }
    }

    /// <summary>
    /// Stage-2 body key: derived from the outer key + the per-bundle content salt + the generation.
    /// An attacker who recovers only the outer key still cannot read the body without replicating
    /// this step against the (file-carried) content salt.
    /// </summary>
    public static byte[] DeriveBodyKey(byte[] outerKey, byte[] contentSalt, int generation)
    {
        if (outerKey is null || outerKey.Length == 0) throw new ArgumentException("outerKey required", nameof(outerKey));
        if (contentSalt is null || contentSalt.Length == 0) throw new ArgumentException("contentSalt required", nameof(contentSalt));

        Span<byte> gen = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(gen, generation);

        // Bind the body salt with the per-bundle content salt so the effective salt is unique per bundle.
        var salt = Concat(BodySalt, contentSalt);
        var material = Concat(outerKey, gen.ToArray());
        try { return Pbkdf2(material, salt); }
        finally { Array.Clear(material, 0, material.Length); Array.Clear(salt, 0, salt.Length); }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static byte[] Pbkdf2(byte[] password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);

    private static byte[] Concat(params byte[][] parts)
    {
        int len = 0;
        foreach (var p in parts) len += p.Length;
        var result = new byte[len];
        int o = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, result, o, p.Length); o += p.Length; }
        return result;
    }
}
