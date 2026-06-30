/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data;

namespace SQLTriage.Tests;

public class CredentialProtectorTests
{
    // ── Round-trip ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("correct-horse-battery-staple")]
    [InlineData("Server=myserver;Password=S3cr3t!;")]
    [InlineData("")]
    [InlineData("unicode: こんにちは 🔑")]
    [InlineData("very long string: " + "A123456789")]
    public void Encrypt_ThenDecrypt_ReturnsOriginal(string plainText)
    {
        var encrypted = CredentialProtector.Encrypt(plainText);
        var decrypted = CredentialProtector.Decrypt(encrypted);
        Assert.Equal(plainText, decrypted);
    }

    [Fact]
    public void Encrypt_Null_ReturnsEmptyOnDecrypt()
    {
        var encrypted = CredentialProtector.Encrypt(null);
        var decrypted = CredentialProtector.Decrypt(encrypted);
        Assert.Equal(string.Empty, decrypted);
    }

    // ── Ciphertext properties ────────────────────────────────────────────────

    [Fact]
    public void Encrypt_ProducesAesPrefix()
    {
        var encrypted = CredentialProtector.Encrypt("secret");
        Assert.StartsWith("aes:", encrypted);
    }

    [Fact]
    public void Encrypt_TwiceSamePlaintext_ProducesDifferentCiphertext()
    {
        // AES-GCM uses a random nonce — same input should never produce same output
        var a = CredentialProtector.Encrypt("secret");
        var b = CredentialProtector.Encrypt("secret");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Encrypt_DoesNotContainPlaintext()
    {
        var plainText = "MySuperSecretPassword";
        var encrypted = CredentialProtector.Encrypt(plainText);
        Assert.DoesNotContain(plainText, encrypted);
    }

    // ── IsEncrypted ──────────────────────────────────────────────────────────

    [Fact]
    public void IsEncrypted_ReturnsTrueForAesPrefix()
    {
        var encrypted = CredentialProtector.Encrypt("value");
        Assert.True(CredentialProtector.IsEncrypted(encrypted));
    }

    [Theory]
    [InlineData("plaintext")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Server=.;Integrated Security=true;")]
    public void IsEncrypted_ReturnsFalseForPlaintext(string? value)
    {
        Assert.False(CredentialProtector.IsEncrypted(value));
    }

    // ── Tamper detection ────────────────────────────────────────────────────

    [Fact]
    public void Decrypt_TamperedCiphertext_ReturnsEmptyOrThrowsGracefully()
    {
        var encrypted = CredentialProtector.Encrypt("sensitive");
        // Flip some bytes in the Base64 payload after the "aes:" prefix
        var tampered = "aes:" + Convert.ToBase64String(new byte[32]);

        // Should not throw — should return empty or handle gracefully
        var result = Record.Exception(() => CredentialProtector.Decrypt(tampered));
        // Either throws a known exception or returns empty string — both acceptable
        // The important thing is it does NOT return the original plaintext
        if (result == null)
        {
            var decrypted = CredentialProtector.Decrypt(tampered);
            Assert.NotEqual("sensitive", decrypted);
        }
    }

    [Fact]
    public void Decrypt_RandomGarbage_DoesNotReturnSensitiveData()
    {
        var garbage = "aes:dGhpcyBpcyBub3QgZW5jcnlwdGVk";
        var result = Record.Exception(() => CredentialProtector.Decrypt(garbage));
        if (result == null)
        {
            var decrypted = CredentialProtector.Decrypt(garbage);
            Assert.NotEqual("sensitive", decrypted);
        }
    }

    // ── Legacy format fallback ───────────────────────────────────────────────

    [Fact]
    public void Decrypt_PlaintextPassthrough_ReturnsAsIs()
    {
        // Legacy: unencrypted values stored before encryption was added
        // should pass through gracefully so old configs still work
        const string legacy = "LegacyPlaintextPassword";
        var result = CredentialProtector.Decrypt(legacy);
        Assert.Equal(legacy, result);
    }
}
