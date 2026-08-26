/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Components.Shared;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Licensing.Crypto;
using SQLTriage.Tests.Licensing.Fixtures;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// THE CARD, RENDERED. Everything the 2026-08-25 licence work added to this component had been
/// reasoned about and never drawn: the persistent failure panel, the bundle-path box and the
/// Install button live on the Settings "Full Audit" tab, which a Blazor Server prerender does not
/// contain, so <c>curl /settings</c> answered 41 KB of markup with none of it in there. Two defects
/// lived in exactly that gap and were found by reading rather than by running.
///
/// <para>These tests render the REAL component through the REAL Blazor <see cref="HtmlRenderer"/>
/// over a REAL <see cref="LicenseService"/> and REAL bundles written by the shipped encryptor, and
/// drive the component's OWN click handler — the method the Activate button binds to — rather than
/// calling the service and asserting what a screen would have shown.</para>
///
/// <para><b>Community axis.</b> <c>ActivateFullAuditCard.razor</c> is Content-Removed by
/// <c>buildprofile.targets</c> under <c>-p:SQLTriageProfile=community</c>, so this file binds a
/// type that does not exist there. It is Compile-Removed from the test project under the same
/// condition, in the same ItemGroup as the other profile-gated suites — NOT gated on a
/// <c>#if</c> constant, which is undefined in the test assembly and silently false.</para>
/// </summary>
public class ActivateFullAuditCardRenderTests : IDisposable
{
    private readonly string _installDir = AppContext.BaseDirectory;
    private readonly List<string> _createdFiles = new();

    private readonly string _settingsDir =
        Path.Combine(Path.GetTempPath(), "sqlt-cardrender-tests-" + Guid.NewGuid().ToString("N"));

    public ActivateFullAuditCardRenderTests()
    {
        var configDir = Path.Combine(_installDir, "Config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "version.json"),
            $"{{\"version\":\"0.90.2\",\"buildNumber\":{BundleFixtureFactory.TestBuildNumber}}}");
    }

    private readonly List<ServiceProvider> _providers = new();
    private readonly List<IServiceScope> _scopes = new();

    public void Dispose()
    {
        foreach (var s in _scopes)
        {
            try { s.Dispose(); } catch { /* best-effort */ }
        }
        foreach (var p in _providers)
        {
            try { p.Dispose(); } catch { /* best-effort */ }
        }
        foreach (var f in _createdFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
        }
        try { if (Directory.Exists(_settingsDir)) Directory.Delete(_settingsDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── The boot-time reason reaches the screen ─────────────────────────────

    [Fact]
    public async Task TheFailurePanelRendersTheReasonTheServiceRecorded()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI

        var bundlePath = WriteFullBundle();
        WriteFreeBundle();

        var rig = NewRig(SaveWrongKey);
        rig.License.Initialize();

        Assert.Equal(FullUnlockFailureReason.NoBundleDecrypted, rig.License.LastFullFailure!.Reason);

        var text = Visible(await rig.RenderAsync());

        Assert.Contains("Full Audit is not active", text, StringComparison.Ordinal);
        Assert.Contains("did not open with the saved customer name", text, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(bundlePath), text, StringComparison.Ordinal);
    }

    /// <summary>
    /// platform-r1-03 (honesty-hunt 2026-08-25), RENDERED. A valid bundle past its expiry once
    /// reached the screen as a decryption/name failure — the client was told to re-check spelling
    /// and key for a licence that had simply lapsed. The dedicated Expired branch now carries an
    /// honest lapse message, and the card binds the red panel to the LIVE <c>LastFullFailure</c>
    /// with no separate <c>FullExpiredOn</c> consumer. This drives a REAL expired bundle through
    /// boot and asserts the honest sentence — not the misattribution — is the one on screen, so the
    /// already-resolved fix is confirmed by a live render, not by reading.
    /// </summary>
    [Fact]
    public async Task TheFailurePanelRendersTheExpiredReasonHonestly()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI

        // A UNIQUE client name isolates this bundle from every other licensing suite that shares
        // AppContext.BaseDirectory: a bundle issued to any other name fails the auth tag under this
        // saved name and is skipped, so Initialize can only land on THIS expired bundle.
        var client = "EXPIRED_RENDER_" + Guid.NewGuid().ToString("N");

        WriteExpiredFullBundle(client);
        WriteFreeBundle();

        var rig = NewRig(settings => SaveKey(settings, client, BundleFixtureFactory.TestKey));
        rig.License.Initialize();

        // Precondition asserted rather than assumed: a run that landed on any other branch would
        // grade the wrong sentence and pass.
        Assert.Equal(FullUnlockFailureReason.Expired, rig.License.LastFullFailure!.Reason);

        var text = Visible(await rig.RenderAsync());

        Assert.Contains("Full Audit is not active", text, StringComparison.Ordinal);
        Assert.Contains("expired", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("renewed bundle", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("did not open with the saved customer name", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE DEFECT. <c>TryActivate</c> captured the failure message for its return value, then let
    /// its own cleanup <c>Initialize()</c> overwrite <c>LastFullFailure</c> with "No licence has
    /// been activated on this Windows account". The card binds the red panel to that LIVE property
    /// and the inline line to the returned string, so a failed Activate put BOTH sentences on
    /// screen at once and the prominent red one was the wrong one. Only a render shows that.
    /// </summary>
    [Fact]
    public async Task AFailedActivatePutsOneSentenceOnScreen_NotTheCleanupsReason()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFullBundle();
        WriteFreeBundle();

        var rig = NewRig(settings => settings.ClearLicense());
        rig.License.Initialize();

        var text = Visible(await rig.RenderAsync(card =>
        {
            SetField(card, "_customerName", BundleFixtureFactory.TestClientName);
            SetField(card, "_licenseKey", Convert.ToBase64String(WrongKey()));
            return Invoke(card, "OnActivateClicked");
        }));

        Assert.Contains("did not open with the saved customer name", text, StringComparison.Ordinal);
        Assert.DoesNotContain("No licence has been activated on this Windows account", text, StringComparison.Ordinal);
    }

    // ── One click, one instruction ──────────────────────────────────────────

    /// <summary>
    /// The success path said "Reload the page to load full data." inline and "Restart pages to
    /// load full data." in the toast fired in the same breath, under a comment claiming the two
    /// carriers held the same sentence. Both now read one constant, and this drives both.
    /// </summary>
    [Fact]
    public async Task ASuccessfulActivateGivesTheSameInstructionInBothCarriers()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFullBundle();
        WriteFreeBundle();

        var rig = NewRig(settings => settings.ClearLicense());
        rig.License.Initialize();

        ActivateFullAuditCard? captured = null;
        var text = Visible(await rig.RenderAsync(card =>
        {
            captured = card;
            SetField(card, "_customerName", BundleFixtureFactory.TestClientName);
            SetField(card, "_licenseKey", Convert.ToBase64String(BundleFixtureFactory.TestKey));
            return Invoke(card, "OnActivateClicked");
        }));

        // The precondition, asserted rather than assumed: a run that failed to activate would make
        // both carriers empty and every assertion below vacuous.
        Assert.Equal(Tier.Full, rig.Accessor.Tier);

        var inline = (string?)GetField(captured!, "_activateMessage");
        Assert.NotNull(inline);
        Assert.Contains(ActivateFullAuditCard.ReloadInstruction, inline!, StringComparison.Ordinal);

        var toast = Assert.Single(rig.Toasts.Where(t => t.Title == "Full Audit activated."));
        Assert.Equal(ActivateFullAuditCard.ReloadInstruction, toast.Message);

        // …and the panel that explains a failure is gone once there is no failure to explain.
        Assert.DoesNotContain("Full Audit is not active", text, StringComparison.Ordinal);
    }

    // ── The copy an operator reads ──────────────────────────────────────────

    /// <summary>
    /// VOICE_GUIDE: "One idea per sentence. If you used 'and' twice, split." Measured over the
    /// rendered hint that tells an operator how to install a bundle file, which is the sentence a
    /// locked-out install is read through.
    /// </summary>
    [Fact]
    public async Task TheInstallHintHoldsOneIdeaPerSentence()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();   // no .aesgcm at all, so the card renders the "no bundle found" hint

        var rig = NewRig(settings => settings.ClearLicense());
        rig.License.Initialize();

        var text = Visible(await rig.RenderAsync());

        var start = text.IndexOf("No *.aesgcm bundle found", StringComparison.Ordinal);
        Assert.True(start >= 0,
            "The no-bundle hint did not render, so this test would grade nothing. The card reads "
            + "AppContext.BaseDirectory with no seam, and that folder is shared with every other "
            + "licence suite in this assembly — a bundle one of them had in flight would put the "
            + "card on its other branch. Bundles in the folder now: "
            + string.Join(", ", Directory.GetFiles(_installDir, "*.aesgcm").Select(Path.GetFileName)));
        var hint = text.Substring(start, Math.Min(300, text.Length - start));

        foreach (var sentence in hint.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var ands = Regex.Matches(sentence, @"(?<![A-Za-z])and(?![A-Za-z])").Count;
            Assert.True(ands < 2, $"this sentence carries {ands} 'and's: \"{sentence.Trim()}.\"");
        }
    }

    // ── SEC-3: the install control requires an authenticated admin ──────────
    //
    // Break-glass keeps the whole Settings page reachable by any loopback caller so a broken RBAC
    // config can be recovered; the licence-install probe reaches the whole filesystem, so it needs
    // more. MayInstallBundle = IsAuthorized("settings") && IsAdmin. The && IsAdmin is the SEC-3
    // change, and its whole job is one cell: the unconfigured-install bootstrap caller, whom the
    // hatch grants "settings" yet who is not an admin. These render the REAL card over the REAL
    // AppUserState / RbacService graph and drive its OWN install handler.

    /// <summary>
    /// The permit cell, and the admin-access happy path that must not regress: an authenticated
    /// admin sees the install control, and driving it with a REAL local .aesgcm installs the bundle
    /// where the unlock looks.
    /// </summary>
    [Fact]
    public async Task InstallControl_AuthenticatedAdmin_SeesItAndARealLocalBundleInstalls()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI

        WriteFreeBundle();
        var srcDir = Path.Combine(Path.GetTempPath(), "sqlt-cardsrc-" + Guid.NewGuid().ToString("N"));
        var source = WriteBundleTo(srcDir);
        var landed = Path.Combine(_installDir, Path.GetFileName(source));
        _createdFiles.Add(landed);

        var rig = NewRig(settings => settings.ClearLicense(), role: AppRoles.Admin, loopback: true);
        rig.License.Initialize();

        var text = Visible(await rig.RenderAsync(card =>
        {
            SetField(card, "_bundlePath", source);
            return Invoke(card, "OnInstallBundleClicked");
        }));

        Assert.True(File.Exists(landed), "an authenticated admin's install of a real local bundle must land");
        Assert.Contains("was installed", text, StringComparison.OrdinalIgnoreCase);
        // "A path on THIS machine" is the install control's OWN hint, rendered only inside the
        // MayInstallBundle gate — unlike the "click Install bundle file" no-bundle hint, which is
        // ungated and shows either way. So this discriminates "the control rendered", not "the words
        // appear somewhere".
        Assert.Contains("A path on THIS machine", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ActivateFullAuditCard.InstallRestrictedMessage, text, StringComparison.Ordinal);

        try { Directory.Delete(srcDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// A signed-in Viewer is refused. The control is not rendered, and — the load-bearing half —
    /// driving the handler with a REAL, shape-valid local bundle does NOT install it. Without the
    /// gate that bundle would land, so the file's ABSENCE is the red→green.
    /// </summary>
    [Fact]
    public async Task InstallControl_SignedInViewer_IsRefusedAndTheBundleDoesNotLand()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();
        var srcDir = Path.Combine(Path.GetTempPath(), "sqlt-cardsrc-" + Guid.NewGuid().ToString("N"));
        var source = WriteBundleTo(srcDir);
        var landed = Path.Combine(_installDir, Path.GetFileName(source));
        _createdFiles.Add(landed);

        var rig = NewRig(settings => settings.ClearLicense(), role: AppRoles.Viewer, loopback: false);
        rig.License.Initialize();

        var text = Visible(await rig.RenderAsync(card =>
        {
            SetField(card, "_bundlePath", source);
            return Invoke(card, "OnInstallBundleClicked");
        }));

        Assert.False(File.Exists(landed),
            "a Viewer's install of a real local bundle must be refused before it lands");
        Assert.Contains(ActivateFullAuditCard.InstallRestrictedMessage, text, StringComparison.Ordinal);
        // The gated control's own hint must be gone (the ungated no-bundle hint keeps the words
        // "Install bundle file", so this is the reliable "control is hidden" discriminator).
        Assert.DoesNotContain("A path on THIS machine", text, StringComparison.Ordinal);

        try { Directory.Delete(srcDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// THE HATCH-CLOSING CELL. On an unconfigured install a loopback caller is bootstrap eligible —
    /// the hatch grants it "settings", asserted here as a precondition — yet it is NOT an admin, so
    /// the && IsAdmin conjunct refuses the licence install. A real local bundle does not land. This
    /// is the SEC-3 accepted cost proved live: no licence from the UI until first-admin exists.
    /// </summary>
    [Fact]
    public async Task InstallControl_UnconfiguredBootstrapLoopbackCaller_IsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();
        var srcDir = Path.Combine(Path.GetTempPath(), "sqlt-cardsrc-" + Guid.NewGuid().ToString("N"));
        var source = WriteBundleTo(srcDir);
        var landed = Path.Combine(_installDir, Path.GetFileName(source));
        _createdFiles.Add(landed);

        var rig = NewRig(settings => settings.ClearLicense(), role: AppRoles.Viewer, loopback: true);
        rig.License.Initialize();

        // The precondition that makes this the hatch cell and not just another viewer: the hatch
        // DOES grant "settings" here, and the caller is NOT an admin. If IsAuthorized were false the
        // test would prove nothing about && IsAdmin.
        Assert.True(rig.User.IsAuthorized("settings"),
            "the bootstrap hatch must grant settings on an unconfigured loopback install, or this "
            + "cell is not exercising what && IsAdmin closes");
        Assert.False(rig.User.IsAdmin);

        var text = Visible(await rig.RenderAsync(card =>
        {
            SetField(card, "_bundlePath", source);
            return Invoke(card, "OnInstallBundleClicked");
        }));

        Assert.False(File.Exists(landed),
            "the bootstrap loopback caller holds the settings hatch yet is not an admin, so the "
            + "licence install must be refused and the bundle must not land");
        Assert.Contains(ActivateFullAuditCard.InstallRestrictedMessage, text, StringComparison.Ordinal);
        Assert.DoesNotContain("A path on THIS machine", text, StringComparison.Ordinal);

        try { Directory.Delete(srcDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Rig ─────────────────────────────────────────────────────────────────

    private sealed record Rig(
        LicenseService License,
        BundleAccessor Accessor,
        IServiceProvider Services,
        AppUserState User,
        List<ToastNotification> Toasts)
    {
        /// <summary>
        /// Renders the card, optionally driving one of its own handlers first, and returns the
        /// HTML. The explicit re-render matches the app: in a circuit the event dispatcher does it,
        /// and nothing dispatched this call.
        /// </summary>
        public async Task<string> RenderAsync(Func<ActivateFullAuditCard, Task>? drive = null)
        {
            var loggerFactory = Services.GetRequiredService<ILoggerFactory>();
            await using var renderer = new HtmlRenderer(Services, loggerFactory);

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                ActivateFullAuditCard? card = null;
                var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["OnCaptured"] = (Action<ActivateFullAuditCard>)(c => card = c),
                });

                var root = await renderer.RenderComponentAsync<CapturingHost>(parameters);
                Assert.NotNull(card);

                if (drive != null)
                {
                    await drive(card!);
                    typeof(ComponentBase)
                        .GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(card, null);
                    await root.QuiescenceTask;
                }

                return root.ToHtmlString();
            });
        }
    }

    /// <summary>
    /// Builds the card's DI graph in a named authorization posture. Defaults to an authenticated
    /// admin so the tests written before SEC-3 still see the install control; the SEC-3 cells pass
    /// an explicit (role, loopback) pair. AppUserState is scoped, so the rig renders through a scope
    /// and the same real RbacService / HostEnvironmentInfo seams AuditLogViewerGateTests uses.
    /// </summary>
    private Rig NewRig(Action<UserSettingsService> prepare,
                       string role = AppRoles.Admin, bool loopback = true)
    {
        var settings = new UserSettingsService(Path.Combine(_settingsDir, "user-settings.json"));
        prepare(settings);

        var accessor = new BundleAccessor();
        var license = new LicenseService(NullLogger<LicenseService>.Instance, settings, accessor);
        var toast = new ToastService();
        var seen = new List<ToastNotification>();
        toast.OnShow += n => { lock (seen) seen.Add(n); };

        var services = new ServiceCollection();
        // Fully qualified: SQLTriage.Data also declares a LogLevel, and both namespaces are in
        // scope here.
        services.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.None));
        services.AddSingleton(settings);
        services.AddSingleton(accessor);
        services.AddSingleton<IBundleAccessor>(accessor);
        services.AddSingleton(license);
        services.AddSingleton(toast);

        // The authorization graph the SEC-3 gate reads. RBAC config/users paths are left absent, so
        // the service is DORMANT (unenforced) — the posture in which a loopback caller is bootstrap
        // eligible, which is the cell the && IsAdmin conjunct exists to close.
        services.AddSingleton(HostEnvironmentInfo.BrowserHosted);
        services.AddSingleton(new RbacService(NullLogger<RbacService>.Instance,
            Path.Combine(_settingsDir, "rbac-config.json"),
            Path.Combine(_settingsDir, "rbac-users.json")));
        services.AddScoped<AppUserState>();

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var scope = provider.CreateScope();
        _scopes.Add(scope);
        var user = scope.ServiceProvider.GetRequiredService<AppUserState>();
        user.SetRole(role);
        user.SetLoopbackForTests(loopback);

        return new Rig(license, accessor, scope.ServiceProvider, user, seen);
    }

    /// <summary>Hosts the card and hands back the instance the renderer created.</summary>
    private sealed class CapturingHost : ComponentBase
    {
        [Parameter] public Action<ActivateFullAuditCard>? OnCaptured { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ActivateFullAuditCard>(0);
            builder.AddComponentReferenceCapture(1, o => OnCaptured?.Invoke((ActivateFullAuditCard)o));
            builder.CloseComponent();
        }
    }

    private static void SaveWrongKey(UserSettingsService settings)
    {
        settings.ClearLicense();
        var entropy = System.Text.Encoding.UTF8.GetBytes("SQLTriage.License.v1");
        settings.SaveLicense(BundleFixtureFactory.TestClientName,
            System.Security.Cryptography.ProtectedData.Protect(
                WrongKey(), entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));
    }

    private static byte[] WrongKey()
    {
        var k = (byte[])BundleFixtureFactory.TestKey.Clone();
        k[5] ^= 0xFF;
        return k;
    }

    private string WriteFullBundle()
    {
        var path = Path.Combine(_installDir, $"cardrender-{Guid.NewGuid():N}.aesgcm");
        var aad = AadBuilder.Build(BundleFixtureFactory.TestClientName, "Full", 1, BundleFixtureFactory.TestBuildNumber);
        File.WriteAllBytes(path, BundleCrypto.EncryptManifest(
            BundleFixtureFactory.MakeFullManifest(), BundleFixtureFactory.TestKey, aad));
        _createdFiles.Add(path);
        return path;
    }

    /// <summary>A shape-valid Full bundle written to an arbitrary LOCAL source folder, for the
    /// SEC-3 cells that drive the install handler with a real path a caller would type.</summary>
    private string WriteBundleTo(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"cardsrc-{Guid.NewGuid():N}.aesgcm");
        var aad = AadBuilder.Build(BundleFixtureFactory.TestClientName, "Full", 1, BundleFixtureFactory.TestBuildNumber);
        File.WriteAllBytes(path, BundleCrypto.EncryptManifest(
            BundleFixtureFactory.MakeFullManifest(), BundleFixtureFactory.TestKey, aad));
        _createdFiles.Add(path);
        return path;
    }

    private string WriteExpiredFullBundle(string clientName)
    {
        var path = Path.Combine(_installDir, $"cardrender-expired-{Guid.NewGuid():N}.aesgcm");
        var aad = AadBuilder.Build(clientName, "Full", 1, BundleFixtureFactory.TestBuildNumber);
        var manifest = BundleFixtureFactory.MakeFullManifest(
            clientName, licenseExpiryUtc: DateTime.UtcNow.AddDays(-1).ToString("o"));
        File.WriteAllBytes(path, BundleCrypto.EncryptManifest(manifest, BundleFixtureFactory.TestKey, aad));
        _createdFiles.Add(path);
        return path;
    }

    private static void SaveKey(UserSettingsService settings, string clientName, byte[] key)
    {
        settings.ClearLicense();
        var entropy = System.Text.Encoding.UTF8.GetBytes("SQLTriage.License.v1");
        settings.SaveLicense(clientName,
            System.Security.Cryptography.ProtectedData.Protect(
                (byte[])key.Clone(), entropy,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
    }

    private string WriteFreeBundle()
    {
        var path = BundleFixtureFactory.WriteFreeBundle(_installDir);
        _createdFiles.Add(path);
        return path;
    }

    private static void SetField(object target, string name, object? value) =>
        Field(target, name).SetValue(target, value);

    private static object? GetField(object target, string name) =>
        Field(target, name).GetValue(target);

    private static FieldInfo Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(target.GetType().FullName, name);

    private static Task Invoke(object target, string method) =>
        (Task)target.GetType()
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(target, null)!;

    /// <summary>Strips tags and collapses whitespace, so an assertion reads the words a person does.</summary>
    private static string Visible(string html)
        => System.Net.WebUtility.HtmlDecode(
            Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ")).Trim();
}
