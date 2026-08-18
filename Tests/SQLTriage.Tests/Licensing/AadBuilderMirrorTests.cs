/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.Text;
using SQLTriage.Data.Services.Licensing.Crypto;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// Verifies the SQLTriage AadBuilder produces byte-for-byte identical output
/// to the known-correct AAD string defined in the corpus encryptor.
///
/// The expected string is computed directly from the documented format
///   "client={clientName}|tier={tier}|bundle_v={bundleVersion}|build={buildNumber}"
/// so the test is independent of both implementations.
/// </summary>
public class AadBuilderMirrorTests
{
    [Theory]
    [InlineData("Acme Corp", "Full", 1, 1903)]
    [InlineData("FREE", "Free", 1, 0)]
    [InlineData("Test", "Full", 1, 9999)]
    [InlineData("Zürich AG", "Full", 1, 100)]   // non-ASCII client name
    public void Build_MatchesExpectedUtf8(string client, string tier, int bundleVer, int build)
    {
        var expected = Encoding.UTF8.GetBytes(
            $"client={client}|tier={tier}|bundle_v={bundleVer}|build={build}");

        var actual = AadBuilder.Build(client, tier, bundleVer, build);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Build_EmptyClientName_Throws()
    {
        Assert.Throws<ArgumentException>(() => AadBuilder.Build("", "Full", 1, 1));
    }

    [Fact]
    public void Build_EmptyTier_Throws()
    {
        Assert.Throws<ArgumentException>(() => AadBuilder.Build("Acme", "", 1, 1));
    }

    [Fact]
    public void Build_ClientNameWithPipe_Throws()
    {
        Assert.Throws<ArgumentException>(() => AadBuilder.Build("Acme|Evil", "Full", 1, 1));
    }

    [Fact]
    public void Build_TierWithPipe_Throws()
    {
        Assert.Throws<ArgumentException>(() => AadBuilder.Build("Acme", "Full|Injected", 1, 1));
    }

    [Fact]
    public void FieldSeparator_IsVerticalBar()
    {
        Assert.Equal('|', AadBuilder.FieldSeparator);
    }
}
