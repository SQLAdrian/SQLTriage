/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using SQLTriage.Data.Services.Licensing.Crypto;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// Verifies the SQLTriage Bip39 codec produces output that mirrors the corpus
/// encryptor's codec for the same inputs.
///
/// <para>Known-vector tests use a fixed 32-byte input so results are deterministic.</para>
///
/// <para>THE WORDLIST IS NOT ALWAYS THERE. Resources\bip39-english.txt is gitignored and
/// fetched by tools\fetch-bip39-wordlist.ps1, so a fresh clone, a fresh worktree and every CI
/// runner build SQLTriage.dll without it. Eight of the nine facts below need it. They carry
/// <see cref="Bip39WordlistFactAttribute"/>, which reports them SKIPPED with a reason when the
/// resource is absent. They used to carry a plain [Fact] and open with
/// <c>if (!WordlistAvailable()) return;</c>, which reported PASSED for a run that asserted
/// nothing - eight silent greens inside every suite-wide "passed" count.</para>
///
/// <para>Each of those eight also calls <see cref="RequireWordlist"/> first, so the pair FAILS
/// loudly rather than passing vacuously if the attribute is ever weakened or removed. Two of
/// them need that belt as well as the braces: Decode_EmptyPhrase_Throws and
/// Decode_WrongWordCount_Throws assert on argument validation that Bip39.Decode performs BEFORE
/// it ever loads the wordlist, so without RequireWordlist they would pass on a build that has
/// no wordlist at all.</para>
///
/// <para>Encode_WrongLengthEntropy_Throws is the ninth, and it stays a plain [Fact]: its
/// assertion is also pre-load argument validation, but that is the whole of what it claims to
/// test, so it is honest on every build.</para>
/// </summary>
public class Bip39MirrorTests
{
    // Fixed 32-byte test entropy - all bytes from 0x01..0x20
    private static readonly byte[] KnownEntropy = new byte[]
    {
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20
    };

    // -- Roundtrip -----------------------------------------------------------

    [Bip39WordlistFact]
    public void Encode_ThenDecode_ReturnsSameBytes()
    {
        RequireWordlist();

        var phrase = Bip39.Encode(KnownEntropy);
        var decoded = Bip39.Decode(phrase);
        Assert.Equal(KnownEntropy, decoded);
    }

    [Bip39WordlistFact]
    public void Encode_Produces24Words()
    {
        RequireWordlist();

        var phrase = Bip39.Encode(KnownEntropy);
        var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(24, words.Length);
    }

    [Bip39WordlistFact]
    public void Decode_WhitespaceSeparated_Works()
    {
        RequireWordlist();

        var phrase = Bip39.Encode(KnownEntropy);
        var tabbed = phrase.Replace(' ', '\t');
        var decoded = Bip39.Decode(tabbed);
        Assert.Equal(KnownEntropy, decoded);
    }

    [Bip39WordlistFact]
    public void Decode_MixedCase_Works()
    {
        RequireWordlist();

        var phrase = Bip39.Encode(KnownEntropy);
        var upper = phrase.ToUpperInvariant();
        var decoded = Bip39.Decode(upper);
        Assert.Equal(KnownEntropy, decoded);
    }

    // -- Edge cases ----------------------------------------------------------

    [Bip39WordlistFact]
    public void Decode_WrongWordCount_Throws()
    {
        RequireWordlist();

        Assert.Throws<ArgumentException>(() => Bip39.Decode("word1 word2"));
    }

    [Bip39WordlistFact]
    public void Decode_EmptyPhrase_Throws()
    {
        RequireWordlist();

        Assert.Throws<ArgumentException>(() => Bip39.Decode(""));
    }

    [Bip39WordlistFact]
    public void Decode_UnknownWord_Throws()
    {
        RequireWordlist();

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

    // -- Checksum verification -----------------------------------------------

    [Bip39WordlistFact]
    public void Decode_CorruptChecksum_Throws()
    {
        RequireWordlist();

        var phrase = Bip39.Encode(KnownEntropy);
        var words = phrase.Split(' ');

        // Flip the last word to a different valid BIP39 word - this corrupts the checksum
        // We need to find a word different from words[23]; use the first word of the list
        // by encoding a different entropy and taking its last word.
        var altEntropy = (byte[])KnownEntropy.Clone();
        altEntropy[31] ^= 0xFF;
        var altPhrase = Bip39.Encode(altEntropy);
        words[23] = altPhrase.Split(' ')[23];

        // The resulting phrase should fail checksum validation
        var ex = Record.Exception(() => Bip39.Decode(string.Join(' ', words)));
        // Either checksum mismatch OR unknown word - both are ArgumentException
        Assert.IsType<ArgumentException>(ex);
    }

    // -- Guard ---------------------------------------------------------------

    /// <summary>
    /// Asserts the wordlist this test needs is actually embedded. The test should have been
    /// SKIPPED by <see cref="Bip39WordlistFactAttribute"/> if it is not; reaching here with no
    /// wordlist means that attribute is no longer doing its job, and the honest verdict is a
    /// failure rather than an assertion-free pass.
    /// </summary>
    private static void RequireWordlist()
    {
        Assert.True(Bip39WordlistProbe.Available,
            "The BIP39 wordlist is not embedded in SQLTriage.dll, so this test has nothing to "
            + "encode against and asserts nothing. It should have been skipped by "
            + "Bip39WordlistFactAttribute; if it ran, that attribute is no longer doing its job. "
            + "Run tools/fetch-bip39-wordlist.ps1 and rebuild to run it for real.");
    }
}
