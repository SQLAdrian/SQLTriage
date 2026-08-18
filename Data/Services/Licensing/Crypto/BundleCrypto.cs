/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SQLTriage.Data.Services.Licensing.Crypto;

/// <summary>
/// Wire-format encrypt/decrypt for .aesgcm bundles.
///
/// This is a VERBATIM COPY of CorpusEncryptor/Crypto.cs with:
///   - Namespace changed to SQLTriage.Data.Services.Licensing.Crypto
///   - Class renamed BundleCrypto (avoids collision with System.Security.Cryptography namespace)
///   - BundleManifest type resolves to SQLTriage.Data.Services.Licensing.BundleManifest
///
/// Wire format (MUST match the encryptor byte-for-byte):
///   bytes  0.. 3  : magic "SLBN" (UTF-8)
///   bytes  4.. 5  : wireVersion (uint16 LE; currently 1)
///   bytes  6.. 7  : reserved (uint16 LE; 0)
///   bytes  8..19  : nonce (12 bytes)
///   bytes 20..35  : tag (16 bytes)
///   bytes 36..N   : ciphertext (variable; gzipped JSON before encryption)
///
/// AES-256-GCM with the customer's 32-byte key. AAD constructed via <see cref="AadBuilder"/>.
/// </summary>
public static class BundleCrypto
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int HeaderSize = 8 + NonceSize + TagSize;   // 4 magic + 2 ver + 2 reserved + 12 nonce + 16 tag = 36
    public const int WireVersion = 1;

    private static readonly byte[] Magic = "SLBN"u8.ToArray();

    /// <summary>Encrypts a manifest object into the .aesgcm wire format.</summary>
    public static byte[] EncryptManifest(BundleManifest manifest, byte[] key, byte[] aad)
    {
        if (key is null || key.Length != KeySize) throw new ArgumentException("Key must be 32 bytes", nameof(key));
        if (aad is null || aad.Length == 0) throw new ArgumentException("AAD is required", nameof(aad));

        // Step 1: serialise manifest to JSON
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, jsonOptions);

        // Step 2: gzip-compress the JSON
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                gz.Write(jsonBytes, 0, jsonBytes.Length);
            }
            compressed = ms.ToArray();
        }

        // Step 3: AES-GCM encrypt with AAD
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[compressed.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, compressed, ciphertext, tag, aad);
        }

        // Step 4: assemble wire bytes
        var output = new byte[HeaderSize + ciphertext.Length];
        Buffer.BlockCopy(Magic, 0, output, 0, 4);
        output[4] = (byte)(WireVersion & 0xFF);
        output[5] = (byte)((WireVersion >> 8) & 0xFF);
        output[6] = 0;
        output[7] = 0;
        Buffer.BlockCopy(nonce, 0, output, 8, NonceSize);
        Buffer.BlockCopy(tag, 0, output, 8 + NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, output, HeaderSize, ciphertext.Length);

        return output;
    }

    /// <summary>Decrypts wire bytes back to a manifest. Throws on auth-tag failure or format errors.</summary>
    public static BundleManifest DecryptManifest(byte[] wire, byte[] key, byte[] aad)
    {
        if (wire is null || wire.Length < HeaderSize + 1) throw new ArgumentException("Wire bytes too short", nameof(wire));
        if (key is null || key.Length != KeySize) throw new ArgumentException("Key must be 32 bytes", nameof(key));
        if (aad is null || aad.Length == 0) throw new ArgumentException("AAD is required", nameof(aad));

        // Validate magic
        if (wire[0] != Magic[0] || wire[1] != Magic[1] || wire[2] != Magic[2] || wire[3] != Magic[3])
            throw new InvalidDataException("Bundle magic mismatch — not a SQLTriage bundle.");

        var ver = (ushort)(wire[4] | (wire[5] << 8));
        if (ver != WireVersion)
            throw new InvalidDataException($"Bundle wire version {ver} unsupported (this codec speaks {WireVersion}).");

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ctLen = wire.Length - HeaderSize;
        var ciphertext = new byte[ctLen];

        Buffer.BlockCopy(wire, 8, nonce, 0, NonceSize);
        Buffer.BlockCopy(wire, 8 + NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(wire, HeaderSize, ciphertext, 0, ctLen);

        var compressed = new byte[ctLen];
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, compressed, aad);
        }

        // Inflate gzip
        byte[] jsonBytes;
        using (var ms = new MemoryStream(compressed))
        using (var gz = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: false))
        using (var outMs = new MemoryStream())
        {
            gz.CopyTo(outMs);
            jsonBytes = outMs.ToArray();
        }

        var manifest = JsonSerializer.Deserialize<BundleManifest>(jsonBytes)
            ?? throw new InvalidDataException("Manifest deserialized to null.");
        return manifest;
    }

    /// <summary>SHA256 of a key, returned as lowercase hex. Used for logging the first 8 chars.</summary>
    public static string KeyFingerprintHex(byte[] key)
    {
        var hash = SHA256.HashData(key);
        var sb = new StringBuilder(hash.Length * 2);
        for (var i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>SHA256 of arbitrary bytes, lowercase hex.</summary>
    public static string Sha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        var sb = new StringBuilder(hash.Length * 2);
        for (var i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }
}
