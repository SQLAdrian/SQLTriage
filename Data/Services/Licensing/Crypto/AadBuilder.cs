/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.Text;

namespace SQLTriage.Data.Services.Licensing.Crypto;

/// <summary>
/// Single source of truth for the GCM Associated Data string format.
///
/// AAD binds the encryption operation to a tuple of (clientName, tier, bundleVersion, buildNumber).
/// Any mismatch at decrypt time breaks the auth tag and throws.
///
/// This class is a VERBATIM COPY of CorpusEncryptor/AadBuilder.cs with only the
/// namespace changed. Any change here MUST be applied to the corpus repo in the same commit.
/// </summary>
public static class AadBuilder
{
    /// <summary>Canonical separator between AAD fields. Pipe is unambiguous in client names.</summary>
    public const char FieldSeparator = '|';

    /// <summary>Construct the AAD bytes for a given encryption/decryption operation.</summary>
    public static byte[] Build(string clientName, string tier, int bundleVersion, int buildNumber)
    {
        if (string.IsNullOrEmpty(clientName)) throw new ArgumentException("clientName is required", nameof(clientName));
        if (string.IsNullOrEmpty(tier)) throw new ArgumentException("tier is required", nameof(tier));
        if (clientName.Contains(FieldSeparator)) throw new ArgumentException($"clientName must not contain '{FieldSeparator}'", nameof(clientName));
        if (tier.Contains(FieldSeparator)) throw new ArgumentException($"tier must not contain '{FieldSeparator}'", nameof(tier));

        var s = $"client={clientName}|tier={tier}|bundle_v={bundleVersion}|build={buildNumber}";
        return Encoding.UTF8.GetBytes(s);
    }
}
