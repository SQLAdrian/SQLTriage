/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SQLTriage.Data.Services.Licensing.Crypto;

/// <summary>
/// BIP-0039 transport encoding for 32-byte secret keys ↔ 24-word mnemonic phrases.
///
/// We use BIP39 strictly as a human-friendly encoding of raw key bytes; the
/// PBKDF2 seed-derivation step of the full BIP39 protocol is NOT applied here.
/// The 24-word phrase encodes:
///   - 256 bits of entropy (the raw 32-byte key)
///   - 8 bits checksum = first byte of SHA256(entropy)
///   total = 264 bits = 24 × 11 bits, one word per 11-bit chunk
///
/// This is a VERBATIM COPY of CorpusEncryptor/Bip39.cs with:
///   - Namespace changed to SQLTriage.Data.Services.Licensing.Crypto
///   - Resource lookup adapted to find the wordlist in the SQLTriage assembly
///   - Error message updated to reference SQLTriage's fetch script path
///
/// The wordlist (Resources\bip39-english.txt) must be present as an EmbeddedResource
/// before building. Run tools\fetch-bip39-wordlist.ps1 once to download it.
/// </summary>
public static class Bip39
{
    private const int EntropyBytes = 32;   // 256 bits
    private const int ExpectedWordCount = 24;
    private const int ExpectedWordlistSize = 2048;
    private const int BitsPerWord = 11;

    private static string[]? _words;
    private static Dictionary<string, int>? _index;
    private static readonly object _loadLock = new();

    /// <summary>Encode a 32-byte key as a 24-word BIP39 English phrase.</summary>
    public static string Encode(byte[] entropy)
    {
        if (entropy is null || entropy.Length != EntropyBytes)
            throw new ArgumentException($"Entropy must be exactly {EntropyBytes} bytes ({EntropyBytes * 8} bits).", nameof(entropy));

        EnsureLoaded();

        // checksum = first byte of SHA256(entropy)
        var checksum = SHA256.HashData(entropy)[0];

        // Build 264-bit string = entropy bits + 8 checksum bits
        var bits = new StringBuilder(EntropyBytes * 8 + 8);
        foreach (var b in entropy)
        {
            for (var i = 7; i >= 0; i--) bits.Append(((b >> i) & 1) == 1 ? '1' : '0');
        }
        for (var i = 7; i >= 0; i--) bits.Append(((checksum >> i) & 1) == 1 ? '1' : '0');

        // Slice into 24 × 11-bit groups
        var words = new string[ExpectedWordCount];
        for (var w = 0; w < ExpectedWordCount; w++)
        {
            var chunk = bits.ToString(w * BitsPerWord, BitsPerWord);
            var idx = Convert.ToInt32(chunk, 2);
            words[w] = _words![idx];
        }
        return string.Join(' ', words);
    }

    /// <summary>Decode a 24-word BIP39 English phrase back to the 32-byte key. Throws on checksum mismatch.</summary>
    public static byte[] Decode(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            throw new ArgumentException("Phrase is empty.", nameof(phrase));

        EnsureLoaded();

        var words = phrase.Trim().ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != ExpectedWordCount)
            throw new ArgumentException($"Expected {ExpectedWordCount} words, got {words.Length}.", nameof(phrase));

        var bits = new StringBuilder(ExpectedWordCount * BitsPerWord);
        foreach (var w in words)
        {
            if (!_index!.TryGetValue(w, out var idx))
                throw new ArgumentException($"Word not in BIP39 wordlist: '{w}'.", nameof(phrase));
            bits.Append(Convert.ToString(idx, 2).PadLeft(BitsPerWord, '0'));
        }

        // First 256 bits = entropy, last 8 bits = checksum
        var entropy = new byte[EntropyBytes];
        for (var i = 0; i < EntropyBytes; i++)
        {
            entropy[i] = Convert.ToByte(bits.ToString(i * 8, 8), 2);
        }
        var checksumByte = Convert.ToByte(bits.ToString(EntropyBytes * 8, 8), 2);

        var expectedChecksum = SHA256.HashData(entropy)[0];
        if (checksumByte != expectedChecksum)
            throw new ArgumentException("Checksum mismatch — phrase corrupt or mistyped.", nameof(phrase));

        return entropy;
    }

    private static void EnsureLoaded()
    {
        if (_words != null) return;
        lock (_loadLock)
        {
            if (_words != null) return;

            var asm = Assembly.GetExecutingAssembly();
            // Resource name pattern: "<DefaultNamespace>.Resources.bip39-english.txt"
            // Suffix-match is stable even if the default namespace changes.
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("bip39-english.txt", StringComparison.OrdinalIgnoreCase));

            if (resName == null)
                throw new InvalidOperationException(
                    "BIP39 wordlist resource missing. Run tools/fetch-bip39-wordlist.ps1 " +
                    "from the SQLTriage project root, then rebuild.");

            using var stream = asm.GetManifestResourceStream(resName)
                ?? throw new InvalidOperationException($"Could not open embedded resource: {resName}");
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var lines = new List<string>(ExpectedWordlistSize);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) lines.Add(trimmed);
            }

            if (lines.Count != ExpectedWordlistSize)
                throw new InvalidOperationException(
                    $"BIP39 wordlist has {lines.Count} entries, expected {ExpectedWordlistSize}. " +
                    "File may be corrupt — re-fetch via tools/fetch-bip39-wordlist.ps1.");

            var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < lines.Count; i++) idx[lines[i]] = i;

            _words = lines.ToArray();
            _index = idx;
        }
    }
}
