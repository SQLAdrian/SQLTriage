/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// The licence binding for the SERVER CONFIGURATION feature set — Server Config &amp; Hardening,
/// Remediation, AG Job Guard, AG Job Sync (Adrian's ruling 2026-08-05).
///
/// <para>The gate is <c>IsUnlocked &amp;&amp; Tier == Full &amp;&amp; Features.Remediation</c>. Both
/// halves are pinned separately below because each refuses a DIFFERENT bundle shape, and a test
/// that only exercised the pair would not notice one of them being dropped.</para>
///
/// <para><b>Evidence limit, stated rather than implied.</b> These measure the gate and the
/// capability that calls it. They do NOT exercise DevBridge: <c>BuildMode.MarkDevBridgeActive()</c>
/// is a one-way process-global switch, so calling it here would poison every later test in the
/// run. They also do not exercise AG Job Guard / AG Job Sync BEHAVIOUR — that needs an availability
/// group, and no rig exists on this box. What is proven is that all three pages' write path is the
/// same <c>RemediationRunner</c>, which asks the capability tested here.</para>
/// </summary>
public sealed class ServerConfigSuiteGateTests
{
    private static BundleFeatures Features(bool remediation) =>
        new(false, false, false, Array.Empty<int>(), Remediation: remediation);

    // ── The four states ──────────────────────────────────────────────────────

    [Fact]
    public void NullAccessor_IsRefused()
    {
        // Fail-closed on null, mirroring ExportPackGate. A gate that threw or granted here would
        // be reached before DI finished wiring.
        Assert.Equal(ServerConfigSuiteState.NoLicenceUnlocked, ServerConfigSuiteGate.Evaluate(null));
        Assert.False(ServerConfigSuiteGate.IsAvailable(null));
    }

    [Fact]
    public void LockedBundle_IsRefused_EvenCarryingTheClaim()
    {
        // IsUnlocked false = nothing decrypted. The claim fields still hold whatever the test set,
        // which is exactly why the gate reads IsUnlocked FIRST rather than trusting Features.
        var bundle = new FakeBundleAccessor { Tier = Tier.Full, Features = Features(true) };
        bundle.SetLocked();

        Assert.Equal(ServerConfigSuiteState.NoLicenceUnlocked, ServerConfigSuiteGate.Evaluate(bundle));
        Assert.False(ServerConfigSuiteGate.IsAvailable(bundle));
    }

    [Fact]
    public void FreeTier_IsRefused_EvenCarryingTheClaim()
    {
        // The community bundle and any Free-tier demo land here. Deliberately tested WITH the claim
        // set: the tier alone must refuse, or "not available on demo" would rest on the claim only.
        var bundle = new FakeBundleAccessor { Tier = Tier.Free, Features = Features(true) };

        Assert.Equal(ServerConfigSuiteState.FreeTier, ServerConfigSuiteGate.Evaluate(bundle));
        Assert.False(ServerConfigSuiteGate.IsAvailable(bundle));
    }

    [Fact]
    public void FullTier_WithoutTheClaim_IsRefused()
    {
        // A demo issued through issue-license.ps1 is FULL tier (the encrypt verb emits nothing
        // else) and passes no --remediation. This is the case the tier check cannot catch — the
        // claim is what makes "not on demo" true for that shape.
        var bundle = new FakeBundleAccessor { Tier = Tier.Full, Features = Features(false) };

        Assert.Equal(ServerConfigSuiteState.ClaimNotGranted, ServerConfigSuiteGate.Evaluate(bundle));
        Assert.False(ServerConfigSuiteGate.IsAvailable(bundle));
    }

    [Fact]
    public void FullTier_WithTheClaim_IsAvailable()
    {
        var bundle = new FakeBundleAccessor { Tier = Tier.Full, Features = Features(true) };

        Assert.Equal(ServerConfigSuiteState.Available, ServerConfigSuiteGate.Evaluate(bundle));
        Assert.True(ServerConfigSuiteGate.IsAvailable(bundle));
    }

    // ── End-to-end from the manifest wire shape ──────────────────────────────

    [Fact]
    public void LegacyFullBundle_WithNoRemediationKey_IsRefused()
    {
        // The shape every Full bundle minted before -Remediation existed carries. Driven through
        // the REAL accessor, not a fake, because the fail-closed `?? false` lives there.
        var accessor = new BundleAccessor();
        accessor.Replace(new BundleManifest { Features = new ManifestFeatures() }, Tier.Full);

        Assert.Equal(ServerConfigSuiteState.ClaimNotGranted, ServerConfigSuiteGate.Evaluate(accessor));
    }

    [Fact]
    public void MintedFullBundle_WithRemediationTrue_IsAvailable()
    {
        var accessor = new BundleAccessor();
        accessor.Replace(
            new BundleManifest { Features = new ManifestFeatures { Remediation = true } }, Tier.Full);

        Assert.Equal(ServerConfigSuiteState.Available, ServerConfigSuiteGate.Evaluate(accessor));
    }

    [Fact]
    public void NoBundleAtAll_IsRefused()
    {
        Assert.Equal(ServerConfigSuiteState.NoLicenceUnlocked,
            ServerConfigSuiteGate.Evaluate(new BundleAccessor()));
    }

    // ── The chokepoint: the write capability all four features share ─────────

    [Fact]
    public void Capability_FollowsTheGate_IncludingTheTierBinding()
    {
        // This is the behaviour CHANGE: before 2026-08-05 the capability read the claim alone, so a
        // Free-tier bundle carrying remediation:true granted writes. Gate 2 of RemediationRunner is
        // this object, and RemediationRunner is what /remediation, /agent-job-guard and
        // /agent-job-sync all call — so this one assertion covers the write path of all three.
        Assert.False(new BundleBackedRemediationCapability(
            new FakeBundleAccessor { Tier = Tier.Free, Features = Features(true) }).IsGranted);

        Assert.False(new BundleBackedRemediationCapability(
            new FakeBundleAccessor { Tier = Tier.Full, Features = Features(false) }).IsGranted);

        Assert.True(new BundleBackedRemediationCapability(
            new FakeBundleAccessor { Tier = Tier.Full, Features = Features(true) }).IsGranted);
    }

    // ── The words beside the verdict ─────────────────────────────────────────

    [Fact]
    public void EveryRefusalStatesTheMeasuredState_AndAvailableSaysNothing()
    {
        // A control is an assertion. The wording this replaced said "not enabled in this build" on
        // a page that compiles into every full build — a claim about a thing the gate never read.
        var refused = new[]
        {
            ServerConfigSuiteState.NoLicenceUnlocked,
            ServerConfigSuiteState.FreeTier,
            ServerConfigSuiteState.ClaimNotGranted,
        };

        foreach (var state in refused)
        {
            var text = ServerConfigSuiteGate.DescribeRefusal(state);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("build", text, StringComparison.OrdinalIgnoreCase);
        }

        // Distinct states get distinct words: a shared sentence would make the three unreadable.
        Assert.Equal(refused.Length,
            new HashSet<string>(Array.ConvertAll(refused, ServerConfigSuiteGate.DescribeRefusal)).Count);

        Assert.Equal("", ServerConfigSuiteGate.DescribeRefusal(ServerConfigSuiteState.Available));
    }

    [Fact]
    public void TheRemedyClauseNamesADestinationThatExists()
    {
        // It first read "Activate a licence under Settings, License." There is no License tab and
        // no License page: the licence UI is the "Full Audit" TAB on Pages/Settings.razor (that is
        // its own label in the tab strip), holding the Full Audit License card. A remedy pointing
        // at a place that is not there is worse than no remedy — the reader concludes the app is
        // broken, or that they are.
        var text = ServerConfigSuiteGate.DescribeRefusal(ServerConfigSuiteState.NoLicenceUnlocked);

        Assert.Contains("Settings page, Full Audit tab", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings, License", text, StringComparison.Ordinal);

        // And the tab label is really the one this sentence sends people to. A lint over a fixed
        // literal, like the other markup assertions here — it proves the label exists in the
        // shipped markup, not that the tab renders for this user.
        var settings = ReadMarkup("Settings.razor");
        Assert.Contains("SetActiveTab(\"Full Audit\")", settings, StringComparison.Ordinal);
    }

    // ── /server-configuration: the fourth page, gated at three layers ─────────

    [Fact]
    public void ServerConfigurationPage_RefusesAtThePage_UsingTheSharedWords()
    {
        // The page shipped on 2026-08-05 with NO licence gate at all: its three siblings got the
        // DescribeRefusal block and it did not, while its nav entry read BuildModules.Premium —
        // a BUILD reading in front of a licence-bound page. Lint ceiling as ever: this proves the
        // call is written, not that the branch renders.
        var markup = StripCommentLines(ReadMarkup("ServerConfiguration.razor"));

        Assert.Contains("IRemediationCapability Capability", markup, StringComparison.Ordinal);
        Assert.Contains("!Capability.IsGranted", markup, StringComparison.Ordinal);
        Assert.Contains("ServerConfigSuiteGate.DescribeRefusal(Bundle)", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerConfigurationNavEntry_ReadsTheLicence_NotOnlyTheBuild()
    {
        var nav = StripCommentLines(ReadMarkup("NavMenu.razor"));

        Assert.Contains(
            "BuildModules.Premium && FeatureGate.IsEnabled(FeatureRegistrar.ServerHardening)",
            nav, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptRunner_RefusesBeforeItTouchesAnything_WhenTheSetIsNotLicensed()
    {
        // The layer that is NOT markup. The runner is handed a NULL connection factory: if the
        // refusal ever stopped short-circuiting, the run would reach _factory.CreateConnection and
        // this test would throw rather than quietly pass.
        var unlicensed = new FakeBundleAccessor { Tier = Tier.Full, Features = Features(false) };
        var service = new SQLTriage.Data.Services.ServerConfigScriptService(
            null!,
            NullLogger<SQLTriage.Data.Services.ServerConfigScriptService>.Instance,
            new BundleBackedRemediationCapability(unlicensed),
            unlicensed);

        var messages = new List<string>();
        await service.RunAsync(apply: true, operatorName: null, (m, _) => messages.Add(m));

        Assert.Single(messages);
        Assert.Equal(
            ServerConfigSuiteGate.DescribeRefusal(ServerConfigSuiteState.ClaimNotGranted),
            messages[0]);
        Assert.DoesNotContain(messages, m => m.Contains("Connected to", StringComparison.Ordinal));

        // Preview is bound to the same licence and returns nothing rather than rows.
        var rows = await service.RunPreviewAsync((m, _) => messages.Add(m));
        Assert.Empty(rows);
    }

    [Fact]
    public async Task ScriptRunner_PassesTheGate_WhenTheSetIsLicensed()
    {
        // Positive control for the test above — without it, a gate that refused EVERYTHING would
        // look identical. With the claim the run goes past the licence check and on to the work,
        // and WHERE it then stops depends on the profile, so the assertion covers both arms
        // rather than pinning one and failing in the other suite (it did, on the first run):
        //   full/dev  — ConfigScripts\ is copied to the test output, so the next thing needed is a
        //               connection and the deliberately-null factory is what it dies on;
        //   community — the .sql is build-excluded, so it stops at the ScriptExists guard instead.
        // Either way the licence gate is not what stopped it, and no refusal sentence is printed.
        var licensed = new FakeBundleAccessor { Tier = Tier.Full, Features = Features(true) };
        var service = new SQLTriage.Data.Services.ServerConfigScriptService(
            null!,
            NullLogger<SQLTriage.Data.Services.ServerConfigScriptService>.Instance,
            new BundleBackedRemediationCapability(licensed),
            licensed);

        var messages = new List<string>();
        var thrown = await Record.ExceptionAsync(
            () => service.RunAsync(apply: false, operatorName: null, (m, _) => messages.Add(m)));

        var reachedTheWork =
            thrown is NullReferenceException
            || messages.Exists(m => m.StartsWith("Script not found:", StringComparison.Ordinal));
        Assert.True(reachedTheWork,
            "the licensed run stopped somewhere other than the connection or the ScriptExists "
            + $"guard — thrown={thrown?.GetType().Name ?? "none"}, messages=[{string.Join(" | ", messages)}]");

        var refusals = new[]
        {
            ServerConfigSuiteGate.DescribeRefusal(ServerConfigSuiteState.NoLicenceUnlocked),
            ServerConfigSuiteGate.DescribeRefusal(ServerConfigSuiteState.FreeTier),
            ServerConfigSuiteGate.DescribeRefusal(ServerConfigSuiteState.ClaimNotGranted),
        };
        Assert.DoesNotContain(messages, m => Array.IndexOf(refusals, m) >= 0);
    }

    [Fact]
    public void QuickCheck_WithdrawsTheOneClickOffer_OnTheLicenceStateToo()
    {
        // The 2026-08-05 commit withdrew the offer when the TARGET IS ABSENT (community, where
        // /remediation is Content-Removed) and left it standing when the target is PRESENT AND
        // REFUSES — a full build on a bundle without the claim rendered "Resolve..." straight onto
        // a refusal page. Both halves now have to be true for the button to appear.
        var markup = StripCommentLines(ReadMarkup("QuickCheck.razor"));

        Assert.Contains("!BuildModules.Premium || !RemediationCapability.IsGranted",
            markup, StringComparison.Ordinal);
    }

    // ── markup helpers (same idiom as DemoAllocationGateFixesTests) ───────────

    private static string ReadMarkup(string fileName)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Markup", fileName);
        Assert.True(System.IO.File.Exists(path),
            $"{fileName} is copied to the test output by SQLTriage.Tests.csproj; if this fails the " +
            "assertions on it would vacuously pass");
        return System.IO.File.ReadAllText(path);
    }

    private static string StripCommentLines(string source) =>
        string.Join("\n", System.Linq.Enumerable.Where(
            source.Split('\n'),
            line =>
            {
                var t = line.TrimStart();
                return !t.StartsWith("//", StringComparison.Ordinal)
                       && !t.StartsWith("@*", StringComparison.Ordinal)
                       && !t.StartsWith("*", StringComparison.Ordinal);
            }));
}
