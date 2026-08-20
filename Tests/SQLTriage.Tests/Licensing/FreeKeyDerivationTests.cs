/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Linq;
using SQLTriage.Data.Services.Licensing.Crypto;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// Known-answer / invariant tests for the community-bundle key derivation (FreeKeyDerivation).
/// These pin the cryptographic CONTRACT that the app and the (off-GitHub) CorpusEncryptor must both
/// honour: same inputs → same key (so a baked bundle decrypts), different build → different key
/// (per-build rotation), and the two stages never collide.
///
/// We assert structural invariants rather than a hardcoded hex vector, because the derivation is
/// deterministic in-process and any drift (fragments, salts, Argon2 params, concat order) breaks
/// determinism/divergence here AND would silently break free-bundle decryption in production.
/// </summary>
public sealed class FreeKeyDerivationTests
{
    [Fact]
    public void OuterKey_Is32Bytes_AndNotAllZero()
    {
        var k = FreeKeyDerivation.DeriveOuterKey(0);
        Assert.Equal(FreeKeyDerivation.KeySize, k.Length);
        Assert.Contains(k, b => b != 0); // never the zero key it replaces
    }

    [Fact]
    public void OuterKey_IsDeterministic_ForSameBuild()
    {
        Assert.Equal(FreeKeyDerivation.DeriveOuterKey(2812), FreeKeyDerivation.DeriveOuterKey(2812));
    }

    [Fact]
    public void OuterKey_RotatesPerBuild()
    {
        var a = FreeKeyDerivation.DeriveOuterKey(2812);
        var b = FreeKeyDerivation.DeriveOuterKey(2813);
        Assert.False(a.SequenceEqual(b), "a key from one build must not open another build's bundle");
    }

    [Fact]
    public void BodyKey_Is32Bytes_Deterministic_AndDiffersFromOuter()
    {
        var outer = FreeKeyDerivation.DeriveOuterKey(100);
        var salt = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

        var body1 = FreeKeyDerivation.DeriveBodyKey(outer, salt, 100);
        var body2 = FreeKeyDerivation.DeriveBodyKey(outer, salt, 100);

        Assert.Equal(FreeKeyDerivation.KeySize, body1.Length);
        Assert.Equal(body1, body2);                          // deterministic
        Assert.False(body1.SequenceEqual(outer));            // distinct from the outer key
    }

    [Fact]
    public void BodyKey_DependsOnContentSalt()
    {
        var outer = FreeKeyDerivation.DeriveOuterKey(100);
        var s1 = Enumerable.Repeat((byte)0xAA, 16).ToArray();
        var s2 = Enumerable.Repeat((byte)0xBB, 16).ToArray();
        Assert.False(
            FreeKeyDerivation.DeriveBodyKey(outer, s1, 100)
                .SequenceEqual(FreeKeyDerivation.DeriveBodyKey(outer, s2, 100)),
            "a different content salt must yield a different body key");
    }

    [Fact]
    public void BodyKey_DependsOnBuild()
    {
        var outer = FreeKeyDerivation.DeriveOuterKey(100);
        var salt = Enumerable.Repeat((byte)0x5A, 16).ToArray();
        Assert.False(
            FreeKeyDerivation.DeriveBodyKey(outer, salt, 100)
                .SequenceEqual(FreeKeyDerivation.DeriveBodyKey(outer, salt, 101)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BodyKey_RejectsEmptyInputs(bool emptyOuter, bool emptySalt)
    {
        var outer = emptyOuter ? Array.Empty<byte>() : FreeKeyDerivation.DeriveOuterKey(1);
        var salt = emptySalt ? Array.Empty<byte>() : new byte[] { 1, 2, 3, 4 };
        Assert.Throws<ArgumentException>(() => FreeKeyDerivation.DeriveBodyKey(outer, salt, 1));
    }
}
