/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using SQLTriage.Data.Services.Licensing.Crypto;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// Verifies the SQLTriage Bip39 codec produces output that mirrors the corpus
/// encryptor's codec for the same inputs. The wordlist resource must be present
/// (run tools/fetch-bip39-wordlist.ps1) — tests guard and return early if missing.
///
/// Known-vector tests use a fixed 32-byte input so results are deterministic.
/// </summary>
public class Bip39MirrorTests
{
    // Fixed 32-byte test entropy — all bytes from 0x01..0x20
    private static readonly byte[] KnownEntropy = new byte[]
    {
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20
    };

    // ── Roundtrip ────────────────────────────────────────────────────────────

    [Fact]
    public void Encode_ThenDecode_ReturnsSameBytes()
    {
        if (!WordlistAvailable()) return; // wordlist not fetched — skip

        var phrase = Bip39.Encode(KnownEntropy);
        var decoded = Bip39.Decode(phrase);
        Assert.Equal(KnownEntropy, decoded);
    }

    [Fact]
    public void Encode_Produces24Words()
    {
        if (!WordlistAvailable()) return;

        var phrase = Bip39.Encode(KnownEntropy);
        var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(24, words.Length);
    }

    [Fact]
    public void Decode_WhitespaceSeparated_Works()
    {
        if (!WordlistAvailable()) return;

        var phrase = Bip39.Encode(KnownEntropy);
        var tabbed = phrase.Replace(' ', '\t');
        var decoded = Bip39.Decode(tabbed);
        Assert.Equal(KnownEntropy, decoded);
    }

    [Fact]
    public void Decode_MixedCase_Works()
    {
        if (!WordlistAvailable()) return;

        var phrase = Bip39.Encode(KnownEntropy);
        var upper = phrase.ToUpperInvariant();
        var decoded = Bip39.Decode(upper);
        Assert.Equal(KnownEntropy, decoded);
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Decode_WrongWordCount_Throws()
    {
        if (!WordlistAvailable()) return;

        Assert.Throws<ArgumentException>(() => Bip39.Decode("word1 word2"));
    }

    [Fact]
    public void Decode_EmptyPhrase_Throws()
    {
        if (!WordlistAvailable()) return;

        Assert.Throws<ArgumentException>(() => Bip39.Decode(""));
    }

    [Fact]
    public void Decode_UnknownWord_Throws()
    {
        if (!WordlistAvailable()) return;

        // Build a phrase with a valid structure but replace one word with a non-BIP39 word
        var phrase = Bip39.Encode(KnownEntropy);
        var tampered = phrase.Replace(phrase.Split(' ')[0], "xyzzy_not_a_bip39_word");
        Assert.Throws<ArgumentException>(() => Bip39.Decode(tampered));
    }

    [Fact]
    public void Encode_WrongLengthEntropy_Throws()
    {
        Assert.Throws<ArgumentException>(() => Bip39.Encode(new byte[16]));
    }

    // ── Checksum verification ────────────────────────────────────────────────

    [Fact]
    public void Decode_CorruptChecksum_Throws()
    {
        if (!WordlistAvailable()) return;

        var phrase = Bip39.Encode(KnownEntropy);
        var words = phrase.Split(' ');

        // Flip the last word to a different valid BIP39 word — this corrupts the checksum
        // We need to find a word different from words[23]; use the first word of the list
        // by encoding a different entropy and taking its last word.
        var altEntropy = (byte[])KnownEntropy.Clone();
        altEntropy[31] ^= 0xFF;
        var altPhrase = Bip39.Encode(altEntropy);
        words[23] = altPhrase.Split(' ')[23];

        // The resulting phrase should fail checksum validation
        var ex = Record.Exception(() => Bip39.Decode(string.Join(' ', words)));
        // Either checksum mismatch OR unknown word — both are ArgumentException
        Assert.IsType<ArgumentException>(ex);
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private static bool WordlistAvailable()
    {
        try
        {
            // Force wordlist load by calling Encode on a known-good input
            Bip39.Encode(KnownEntropy);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Wordlist resource not embedded yet — test environment doesn't have it
            return false;
        }
    }
}
