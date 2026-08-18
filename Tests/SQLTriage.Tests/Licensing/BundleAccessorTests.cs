/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// Unit tests for BundleAccessor — Replace semantics, event firing, and IsCheckPermitted logic.
/// No crypto involved; manifests are constructed inline.
/// </summary>
public class BundleAccessorTests
{
    // ── Initial state ────────────────────────────────────────────────────────

    [Fact]
    public void NewAccessor_IsUnlocked_False()
    {
        var acc = new BundleAccessor();
        Assert.False(acc.IsUnlocked);
    }

    [Fact]
    public void NewAccessor_Tier_IsFree()
    {
        var acc = new BundleAccessor();
        Assert.Equal(Tier.Free, acc.Tier);
    }

    [Fact]
    public void NewAccessor_ClientName_IsNull()
    {
        var acc = new BundleAccessor();
        Assert.Null(acc.ClientName);
    }

    [Fact]
    public void NewAccessor_Features_AreAllFalse()
    {
        var acc = new BundleAccessor();
        var f = acc.Features;
        Assert.False(f.RagEnabled);
        Assert.False(f.SpBlitzImport);
        Assert.False(f.FullCorpus);
        Assert.Empty(f.PermittedCheckIds);
    }

    // ── Replace with a manifest ──────────────────────────────────────────────

    [Fact]
    public void Replace_WithManifest_SetsIsUnlocked()
    {
        var acc = new BundleAccessor();
        acc.Replace(MakeManifest("Acme Corp", "Full", ragEnabled: true), Tier.Full);
        Assert.True(acc.IsUnlocked);
    }

    [Fact]
    public void Replace_Full_SetsTierFull()
    {
        var acc = new BundleAccessor();
        acc.Replace(MakeManifest("Acme Corp", "Full"), Tier.Full);
        Assert.Equal(Tier.Full, acc.Tier);
    }

    [Fact]
    public void Replace_WithNull_ResetsIsUnlocked()
    {
        var acc = new BundleAccessor();
        acc.Replace(MakeManifest("X", "Full"), Tier.Full);
        acc.Replace(null, Tier.Free);
        Assert.False(acc.IsUnlocked);
    }

    [Fact]
    public void Replace_WithNull_TierRemainsAsProvided()
    {
        var acc = new BundleAccessor();
        acc.Replace(null, Tier.Free);
        Assert.Equal(Tier.Free, acc.Tier);
    }

    // ── BundleStateChanged event ─────────────────────────────────────────────

    [Fact]
    public void Replace_FiresBundleStateChanged()
    {
        var acc = new BundleAccessor();
        var fired = false;
        acc.BundleStateChanged += (_, _) => fired = true;

        acc.Replace(MakeManifest("X", "Full"), Tier.Full);

        Assert.True(fired);
    }

    [Fact]
    public void Replace_Null_FiresBundleStateChanged()
    {
        var acc = new BundleAccessor();
        var fired = false;
        acc.Replace(MakeManifest("X", "Full"), Tier.Full); // prime state
        acc.BundleStateChanged += (_, _) => fired = true;

        acc.Replace(null, Tier.Free);

        Assert.True(fired);
    }

    // ── IsCheckPermitted ─────────────────────────────────────────────────────

    [Fact]
    public void IsCheckPermitted_NoManifest_ReturnsFalse()
    {
        var acc = new BundleAccessor();
        Assert.False(acc.IsCheckPermitted(42));
    }

    [Fact]
    public void IsCheckPermitted_FullTier_EmptyList_ReturnsTrue()
    {
        // Full tier with empty CheckIds = all checks permitted
        var acc = new BundleAccessor();
        acc.Replace(MakeManifest("Acme", "Full", checkIds: new List<int>()), Tier.Full);
        Assert.True(acc.IsCheckPermitted(1));
        Assert.True(acc.IsCheckPermitted(9999));
    }

    [Fact]
    public void IsCheckPermitted_FreeTier_ExplicitList_EnforcesAllowlist()
    {
        var acc = new BundleAccessor();
        acc.Replace(MakeManifest("FREE", "Free", checkIds: new List<int> { 10, 20, 30 }), Tier.Free);
        Assert.True(acc.IsCheckPermitted(10));
        Assert.True(acc.IsCheckPermitted(20));
        Assert.False(acc.IsCheckPermitted(11));
    }

    [Fact]
    public void IsCheckPermitted_FullTier_ExplicitList_EnforcesAllowlist()
    {
        var acc = new BundleAccessor();
        acc.Replace(MakeManifest("Acme", "Full", checkIds: new List<int> { 5, 15 }), Tier.Full);
        Assert.True(acc.IsCheckPermitted(5));
        Assert.False(acc.IsCheckPermitted(6));
    }

    // ── GetText / GetBytes ───────────────────────────────────────────────────

    [Fact]
    public void GetText_ExistingKey_ReturnsText()
    {
        var acc = new BundleAccessor();
        var m = new BundleManifest
        {
            ClientName = "X",
            Tier = "Full",
            Files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Config/control_mappings.json"] = "{}"
            }
        };
        acc.Replace(m, Tier.Full);
        Assert.Equal("{}", acc.GetText("Config/control_mappings.json"));
    }

    [Fact]
    public void GetText_MissingKey_ReturnsNull()
    {
        var acc = new BundleAccessor();
        acc.Replace(MakeManifest("X", "Full"), Tier.Full);
        Assert.Null(acc.GetText("Config/missing.json"));
    }

    [Fact]
    public void GetBytes_ExistingKey_ReturnUtf8Bytes()
    {
        var acc = new BundleAccessor();
        var m = new BundleManifest
        {
            ClientName = "X",
            Tier = "Full",
            Files = new Dictionary<string, string>(StringComparer.Ordinal) { ["f"] = "hello" }
        };
        acc.Replace(m, Tier.Full);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("hello"), acc.GetBytes("f"));
    }

    // ── Corpus accessors ─────────────────────────────────────────────────────

    [Fact]
    public void EnumerateCorpusYamlHandles_ReturnsOnlyYamlKeys()
    {
        var acc = new BundleAccessor();
        var m = new BundleManifest
        {
            ClientName = "X",
            Tier = "Full",
            Corpus = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["check_001.yaml"] = "---",
                ["check_001.sql"] = "SELECT 1",
                ["check_002.yaml"] = "---",
            }
        };
        acc.Replace(m, Tier.Full);

        var handles = acc.EnumerateCorpusYamlHandles().ToList();
        Assert.Equal(2, handles.Count);
        Assert.All(handles, h => Assert.EndsWith(".yaml", h, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadCorpusYaml_ReturnsContent()
    {
        var acc = new BundleAccessor();
        var m = new BundleManifest
        {
            ClientName = "X",
            Tier = "Full",
            Corpus = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["check_001.yaml"] = "id: 1"
            }
        };
        acc.Replace(m, Tier.Full);
        Assert.Equal("id: 1", acc.ReadCorpusYaml("check_001.yaml"));
    }

    [Fact]
    public void ReadCorpusSqlFallback_ReturnsSqlSibling()
    {
        var acc = new BundleAccessor();
        var m = new BundleManifest
        {
            ClientName = "X",
            Tier = "Full",
            Corpus = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["check_001.yaml"] = "id: 1",
                ["check_001.sql"] = "SELECT 1"
            }
        };
        acc.Replace(m, Tier.Full);
        Assert.Equal("SELECT 1", acc.ReadCorpusSqlFallback("check_001.yaml"));
    }

    [Fact]
    public void ReadCorpusSqlFallback_NoSibling_ReturnsNull()
    {
        var acc = new BundleAccessor();
        var m = new BundleManifest
        {
            ClientName = "X",
            Tier = "Full",
            Corpus = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["check_001.yaml"] = "id: 1"
            }
        };
        acc.Replace(m, Tier.Full);
        Assert.Null(acc.ReadCorpusSqlFallback("check_001.yaml"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static BundleManifest MakeManifest(
        string clientName,
        string tier,
        bool ragEnabled = false,
        List<int>? checkIds = null)
    {
        return new BundleManifest
        {
            BundleVersion = 1,
            BuildNumber = 1903,
            CreatedUtc = "2026-05-23T00:00:00Z",
            ClientName = clientName,
            Tier = tier,
            Features = new ManifestFeatures
            {
                RagEnabled = ragEnabled,
                SpBlitzImport = true,
                FullCorpus = tier == "Full",
                CheckIds = checkIds ?? new List<int>()
            }
        };
    }
}
