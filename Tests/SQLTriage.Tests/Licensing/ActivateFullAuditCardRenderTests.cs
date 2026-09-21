/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
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

    // ── The handlers do not strand the card on "Working..." ─────────────

    /// <summary>
    /// THE FREEZE, driven. Every handler set <c>_busy = true</c>, awaited a bare
    /// <c>Task.Run(...)</c>, and cleared the flag on the line AFTER the await. An exception inside
    /// the lambda never reached that line: it unwound into the page ErrorBoundary and left the card
    /// with its inputs disabled and "Working..." on screen for good.
    ///
    /// <para><b>How the fault is injected, stated plainly.</b> The bound customer-name field is set
    /// to null, so the handler's own <c>_customerName.Trim()</c> throws inside the Task.Run lambda.
    /// That is a real exception on the real line, and it is the path any exception out of
    /// <see cref="LicenseService"/> takes. What is NOT proved here is a specific service fault: this
    /// rig cannot inject one, because LicenseService is sealed and its methods are not virtual.</para>
    /// </summary>
    [Fact]
    public async Task AThrowingActivateHandlerClearsBusyAndSaysWhatFailed()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();

        var rig = NewRig(settings => settings.ClearLicense());
        rig.License.Initialize();

        ActivateFullAuditCard? captured = null;
        var text = Visible(await rig.RenderAsync(card =>
        {
            captured = card;
            SetField(card, "_customerName", null);          // makes the handler's own Trim() throw
            SetField(card, "_licenseKey", "irrelevant");
            return Invoke(card, "OnActivateClicked");
        }));

        Assert.False((bool)GetField(captured!, "_busy")!,
            "the handler must clear _busy in a finally, or the card stays disabled for good");
        var inline = (string?)GetField(captured!, "_activateMessage");
        Assert.NotNull(inline);
        Assert.StartsWith("Activation failed:", inline!, StringComparison.Ordinal);
        Assert.DoesNotContain("Working...", text, StringComparison.Ordinal);
    }

    // ── The card names the file that actually opened ─────────────────────

    /// <summary>
    /// The status line read "&lt;first .aesgcm alphabetically&gt; (decrypted)". With two bundles in
    /// the folder that named a file the key had never opened. It now renders
    /// <c>LicenseService.LastUnlockedBundlePath</c>, and the per-file list beside it carries the
    /// state the service recorded for each file.
    /// </summary>
    [Fact]
    public async Task TheStatusLineNamesTheBundleThatOpened_NotTheFirstFileByName()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();

        var client = "CARDPRECEDENCE_" + Guid.NewGuid().ToString("N");
        // The decoy sorts FIRST by name and is issued to a different customer, so the saved key
        // cannot open it. Before this lane it is the name the status line printed.
        var decoy = WriteNamedBundle("aaa-decoy", "SOMEONE_ELSE_" + Guid.NewGuid().ToString("N"));
        var real = WriteNamedBundle("zzz-real", client);

        var rig = NewRig(settings => SaveKey(settings, client, BundleFixtureFactory.TestKey));
        rig.License.Initialize();

        Assert.Equal(Tier.Full, rig.Accessor.Tier);
        Assert.Equal(real, rig.License.LastUnlockedBundlePath);

        var text = Visible(await rig.RenderAsync());

        Assert.Contains(Path.GetFileName(real) + " (in use)", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(decoy) + " (in use)", text, StringComparison.Ordinal);
        Assert.Contains("did not open with the saved phrase", text, StringComparison.Ordinal);

        // The sentence that tells an operator what to do about a file that did not open.
        Assert.Contains("needs the customer name and phrase it was issued with", text, StringComparison.Ordinal);
    }

    // ── The OTHER two handlers clear _busy when the work throws ─────────────

    /// <summary>
    /// The install handler's <c>catch</c>/<c>finally</c>, driven by a REAL fault raised inside the
    /// handler's own <c>try</c>.
    ///
    /// <para><b>How the fault is raised, stated plainly.</b> <c>LicenseService</c> is sealed and its
    /// methods are not virtual, so a service failure cannot be injected. <c>ToastService.Show</c>
    /// invokes <c>OnShow</c> directly, so a subscriber that throws makes the card's own
    /// <c>Toast.ShowError(...)</c> call throw — a real exception on a real line of the handler,
    /// raised by a collaborator, which is the shape a service fault would have. It throws ONCE, so
    /// the catch block's own toast still lands and the assertions read the catch, not a second
    /// fault.</para>
    ///
    /// <para>Before this lane the body ran in a bare <c>await Task.Run(...)</c> with
    /// <c>_busy = false</c> on the line after it — a line an exception never reaches — so the card
    /// stayed disabled with "Working..." on screen. That is the reported client symptom.</para>
    ///
    /// <para><b>What this cell grades changed in round 3, and why.</b> It used to assert the inline
    /// message STARTED "Install bundle file failed:" — i.e. that the catch had overwritten whatever
    /// the code already had. In this fixture the service has already refused the blank path with a
    /// sentence naming what to type, and the overwrite deleted it in favour of a toast consumer's
    /// exception text. So the assertion is inverted: the service's own account must survive, and
    /// the generic sentence must not appear over it. The <c>_busy</c> property this cell exists to
    /// prove is untouched.</para>
    /// </summary>
    [Fact]
    public async Task AThrowingInstallHandlerClearsBusyAndSaysWhatFailed()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();

        var rig = NewRig(settings => settings.ClearLicense());
        rig.License.Initialize();
        ThrowOnFirstToast(rig);

        ActivateFullAuditCard? captured = null;
        var text = Visible(await rig.RenderAsync(card =>
        {
            captured = card;
            SetField(card, "_bundlePath", "   ");   // refused by the service; the toast then throws
            return Invoke(card, "OnInstallBundleClicked");
        }));

        Assert.False((bool)GetField(captured!, "_busy")!,
            "the install handler must clear _busy in a finally, or the card stays disabled for good");
        var inline = (string?)GetField(captured!, "_installMessage");
        Assert.NotNull(inline);
        Assert.Contains(".aesgcm", inline!, StringComparison.Ordinal);
        Assert.DoesNotContain("Install bundle file failed:", inline!, StringComparison.Ordinal);
        Assert.DoesNotContain("Working...", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The deactivate handler's <c>catch</c>/<c>finally</c>, on the same seam. Here the throwing
    /// toast is the success-path <c>ShowInfo</c>, so the fault lands AFTER the deactivate has run.
    ///
    /// <para><b>This cell used to pin a lie.</b> It asserted the inline message started
    /// "Deactivate failed:" in a scenario where the deactivate had succeeded — the tier is Free
    /// when the assertion runs — so a green suite was evidence for the wrong sentence. The
    /// <c>_busy</c> assertion is the property the cell exists to prove and it is kept; the message
    /// assertion now grades the honest sentence, and the outcome it is grading is asserted rather
    /// than left implied.</para>
    /// </summary>
    [Fact]
    public async Task AThrowingDeactivateHandlerClearsBusyAndSaysWhatFailed()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();

        var client = "CARDDEACT_" + Guid.NewGuid().ToString("N");
        WriteNamedBundle("deact", client);

        var rig = NewRig(settings => SaveKey(settings, client, BundleFixtureFactory.TestKey));
        rig.License.Initialize();
        Assert.Equal(Tier.Full, rig.Accessor.Tier);   // precondition: there is a licence to drop
        ThrowOnFirstToast(rig);

        ActivateFullAuditCard? captured = null;
        var text = Visible(await rig.RenderAsync(card =>
        {
            captured = card;
            return Invoke(card, "OnDeactivateClicked");
        }));

        Assert.False((bool)GetField(captured!, "_busy")!,
            "the deactivate handler must clear _busy in a finally, or the card stays disabled for good");

        // THE OUTCOME THIS CELL IS GRADING, stated. The deactivate ran to completion; only the
        // notification after it threw.
        Assert.Equal(Tier.Free, rig.Accessor.Tier);

        var inline = (string?)GetField(captured!, "_activateMessage");
        Assert.NotNull(inline);
        Assert.StartsWith("Deactivated.", inline!, StringComparison.Ordinal);
        Assert.Contains("back on the Free tier", inline!, StringComparison.Ordinal);
        Assert.Contains("a toast consumer is broken", inline!, StringComparison.Ordinal);
        Assert.DoesNotContain("Deactivate failed", inline!, StringComparison.Ordinal);
        Assert.DoesNotContain("Working...", text, StringComparison.Ordinal);
    }

    // ── A succeeded operation is never reported as a failure ────────────────

    /// <summary>
    /// THE DEFECT (verifier round 2, HIGH). All three handlers treated "an exception happened
    /// somewhere in this method" as "the operation failed", and by the time anything in them can
    /// throw the outcome is already decided. Here the licence IS active — <c>TryActivate</c> has
    /// returned success and the accessor is on Full — and the card told the client activation had
    /// failed, beside its own tier badge reading Full.
    ///
    /// <para><b>How the fault is raised.</b> <c>LicenseService</c> is sealed with non-virtual
    /// methods, so a post-success service fault cannot be injected. The success-path
    /// <c>Toast.ShowSuccess(...)</c> is a real line of the handler that runs after the work, and a
    /// broken toast consumer makes it throw — the shape any post-success fault would have.</para>
    ///
    /// <para>A false failure is not a cosmetic defect on this card: by this lane's own defect list
    /// it is what pushes a client toward Deactivate, the destructive path the lane exists to
    /// remove.</para>
    /// </summary>
    [Fact]
    public async Task AnActivateThatSucceededIsNotReportedAsAFailure()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFullBundle();
        WriteFreeBundle();

        var rig = NewRig(settings => settings.ClearLicense());
        rig.License.Initialize();
        ThrowOnFirstToast(rig);

        ActivateFullAuditCard? captured = null;
        var text = Visible(await rig.RenderAsync(card =>
        {
            captured = card;
            SetField(card, "_customerName", BundleFixtureFactory.TestClientName);
            SetField(card, "_licenseKey", Convert.ToBase64String(BundleFixtureFactory.TestKey));
            return Invoke(card, "OnActivateClicked");
        }));

        // The precondition, asserted rather than assumed: the licence really is active.
        Assert.Equal(Tier.Full, rig.Accessor.Tier);
        Assert.NotNull(rig.License.LastUnlockedBundlePath);

        Assert.False((bool)GetField(captured!, "_busy")!);
        Assert.True((bool)GetField(captured!, "_activateOk")!,
            "the licence is active, so the card must not style this as a failure");

        var inline = (string?)GetField(captured!, "_activateMessage");
        Assert.NotNull(inline);
        Assert.StartsWith("Full Audit activated.", inline!, StringComparison.Ordinal);
        Assert.Contains("a toast consumer is broken", inline!, StringComparison.Ordinal);
        Assert.DoesNotContain("Activation failed", inline!, StringComparison.Ordinal);
        Assert.DoesNotContain("Activation failed", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same defect on the install handler, where THIS LANE introduced the reachable trigger:
    /// round 1 added <c>if (result.Success) await Task.Run(() =&gt; LicenseSvc.Initialize());</c>
    /// inside the try, after the copy has landed. <c>Initialize</c> enumerates the install folder
    /// and reads the saved licence off disk with neither call inside a try, so an unreadable folder
    /// or a corrupt user-settings.json turned a bundle that DID land into "Bundle not installed."
    ///
    /// <para>The install work is driven through <c>InstallWorkOverrideForTests</c>, the seam the
    /// timeout cell already uses: it reports a successful install without touching the filesystem,
    /// and the success toast then throws. The file itself is beside the point here — what is
    /// graded is that a success reported by the service survives a later fault.</para>
    ///
    /// <para><b>And the sentence must not name a cause.</b> Two lines inside that try can throw
    /// after the install succeeded — the success toast and the <c>Initialize</c> re-read — and this
    /// fixture raises the FIRST. "Reading the install folder afterwards failed" was therefore a
    /// misattribution that this very cell was pinning. The neutral wording the sibling handlers
    /// already use is what the fixture supports.</para>
    /// </summary>
    [Fact]
    public async Task AnInstallThatSucceededIsNotReportedAsARefusal()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();

        var rig = NewRig(settings => settings.ClearLicense());
        rig.License.Initialize();

        var landed = Path.Combine(_installDir, "install-succeeded.aesgcm");
        rig.License.InstallWorkOverrideForTests =
            (_, __) => new BundleInstallResult(true, "Bundle installed.", landed);
        ThrowOnFirstToast(rig);

        ActivateFullAuditCard? captured = null;
        string text;
        try
        {
            text = Visible(await rig.RenderAsync(card =>
            {
                captured = card;
                SetField(card, "_bundlePath", @"C:\Users\nobody\Downloads\ok.aesgcm");
                return Invoke(card, "OnInstallBundleClicked");
            }));
        }
        finally
        {
            rig.License.InstallWorkOverrideForTests = null;
        }

        Assert.False((bool)GetField(captured!, "_busy")!);
        Assert.True((bool)GetField(captured!, "_installOk")!,
            "the service reported the bundle installed, so the card must not style this as a refusal");

        var inline = (string?)GetField(captured!, "_installMessage");
        Assert.NotNull(inline);
        Assert.StartsWith("The bundle was installed.", inline!, StringComparison.Ordinal);
        Assert.Contains("a toast consumer is broken", inline!, StringComparison.Ordinal);
        Assert.DoesNotContain("Install bundle file failed", inline!, StringComparison.Ordinal);
        Assert.DoesNotContain("Bundle not installed", text, StringComparison.Ordinal);

        // The fault came from the TOAST, so the sentence may not blame the folder re-read.
        Assert.DoesNotContain("Reading the install folder", inline!, StringComparison.Ordinal);
    }

    // ── The card claims a date comparison only when one was won ─────────────

    /// <summary>
    /// The state word for a losing file said "superseded by a newer bundle" for every file that
    /// opened and is not the one in use. The service keeps the INCUMBENT on a tie, so two bundles
    /// carrying the same manifest date make the card assert a date comparison that was never won.
    /// Ten bundles were minted for one client on 2026-09-02 seconds apart, which is the shape that
    /// makes a tie a real client outcome rather than a contrived one.
    /// </summary>
    [Fact]
    public async Task TiedManifestDatesAreNotRenderedAsASupersededBundle()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();

        var client = "CARDTIE_" + Guid.NewGuid().ToString("N");
        const string sameMoment = "2026-09-02T02:15:00Z";
        WriteNamedBundleCreated("tie-a", client, sameMoment);
        WriteNamedBundleCreated("tie-b", client, sameMoment);

        var rig = NewRig(settings => SaveKey(settings, client, BundleFixtureFactory.TestKey));
        rig.License.Initialize();

        // Preconditions: one file is in use and exactly one other opened and lost the tie.
        Assert.Equal(Tier.Full, rig.Accessor.Tier);
        Assert.Equal(1, rig.License.LastBundleScan.Count(s => s.State == BundleFileState.InUse));
        Assert.Equal(1, rig.License.LastBundleScan.Count(s => s.State == BundleFileState.Superseded));

        var text = Visible(await rig.RenderAsync());

        Assert.Contains("opened with the saved phrase, not the one in use", text, StringComparison.Ordinal);
        Assert.DoesNotContain("superseded by a newer bundle", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the same word: when the file in use really does carry a later manifest
    /// date, "superseded by a newer bundle" is the true sentence and must still be printed. Without
    /// this cell the fix above could be satisfied by deleting the phrase.
    /// </summary>
    [Fact]
    public async Task AGenuinelyNewerBundleIsRenderedAsSupersedingTheOlderOne()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();

        var client = "CARDNEWER_" + Guid.NewGuid().ToString("N");
        WriteNamedBundleCreated("older", client, "2026-06-01T00:00:00Z");
        var newer = WriteNamedBundleCreated("newer", client, "2026-09-01T00:00:00Z");

        var rig = NewRig(settings => SaveKey(settings, client, BundleFixtureFactory.TestKey));
        rig.License.Initialize();

        Assert.Equal(Tier.Full, rig.Accessor.Tier);
        Assert.Equal(newer, rig.License.LastUnlockedBundlePath);

        var text = Visible(await rig.RenderAsync());

        Assert.Contains("superseded by a newer bundle", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A REFUSAL and a NON-ANSWER get different toast headlines, driven through the card's own
    /// install handler.
    ///
    /// <para>The timeout message says the copy may still finish and that which of the four file
    /// steps has not returned is not known here. "Bundle not installed." over that body asserts as
    /// fact the one thing the body calls unknown, and the toast is the surface a client reads
    /// first. The card now titles from <c>BundleInstallResult.TimedOut</c>.</para>
    ///
    /// <para><b>Cost, stated.</b> The second half waits out the real
    /// <c>LicenseService.InstallProbeTimeout</c> (15 s), because the card calls the bounded wait
    /// with no timeout argument and that default is what ships. A shorter budget would prove a
    /// number this card never passes.</para>
    /// </summary>
    [Fact]
    public async Task ATimedOutInstallIsNotTitledAsARefusal()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();

        var rig = NewRig(settings => settings.ClearLicense());
        rig.License.Initialize();

        // A genuine refusal: the service knows the file was not installed.
        await rig.RenderAsync(card =>
        {
            SetField(card, "_bundlePath", "   ");
            return Invoke(card, "OnInstallBundleClicked");
        });

        var refusal = rig.Toasts.LastOrDefault();
        Assert.NotNull(refusal);
        Assert.Equal("Bundle not installed.", refusal!.Title);

        // A non-answer: the file work is still running and its copy may still land.
        using var neverAnswers = new ManualResetEventSlim(false);
        rig.License.InstallWorkOverrideForTests = (path, _) =>
        {
            neverAnswers.Wait();
            return new BundleInstallResult(true, "should never be seen", path);
        };

        try
        {
            await rig.RenderAsync(card =>
            {
                SetField(card, "_bundlePath", @"C:\Users\nobody\Downloads\never.aesgcm");
                return Invoke(card, "OnInstallBundleClicked");
            });
        }
        finally
        {
            neverAnswers.Set();
            rig.License.InstallWorkOverrideForTests = null;
        }

        var timedOut = rig.Toasts.LastOrDefault();
        Assert.NotNull(timedOut);
        Assert.Equal("Bundle install did not answer.", timedOut!.Title);
        Assert.Contains("may still finish", timedOut.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Makes the NEXT toast this rig raises throw, once. Registered after the rig's own recorder,
    /// so the recorded list still sees the toast that faulted.
    /// </summary>
    private static void ThrowOnFirstToast(Rig rig)
    {
        var toast = rig.Services.GetRequiredService<ToastService>();
        var fired = 0;
        toast.OnShow += _ =>
        {
            if (Interlocked.Increment(ref fired) == 1)
                throw new InvalidOperationException("a toast consumer is broken");
        };
    }

    /// <summary>
    /// While Full is active the two credential boxes ARE the update path, and nothing said so. The
    /// heading and its sentence are the whole fix, and they must NOT appear on a Free install, where
    /// there is no licence to replace.
    /// </summary>
    [Fact]
    public async Task AnActiveLicenceRendersTheUpdateHeading_AndAFreeOneDoesNot()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();

        var client = "CARDUPDATE_" + Guid.NewGuid().ToString("N");
        WriteNamedBundle("upd", client);

        var full = NewRig(settings => SaveKey(settings, client, BundleFixtureFactory.TestKey));
        full.License.Initialize();
        Assert.Equal(Tier.Full, full.Accessor.Tier);

        var fullText = Visible(await full.RenderAsync());
        Assert.Contains("Update licence", fullText, StringComparison.Ordinal);
        // The sentence must not contradict the Deactivate button rendered a few lines below it on
        // the same card: it says Deactivate is not a REQUIRED step, not that no such control exists.
        Assert.Contains("You do not need to click Deactivate first", fullText, StringComparison.Ordinal);
        Assert.DoesNotContain("There is no Deactivate step", fullText, StringComparison.Ordinal);

        var free = NewRig(settings => settings.ClearLicense());
        free.License.Initialize();
        Assert.NotEqual(Tier.Full, free.Accessor.Tier);

        var freeText = Visible(await free.RenderAsync());
        Assert.DoesNotContain("Update licence", freeText, StringComparison.Ordinal);
    }

    // ── The catch must not delete the message the code already had ──────────

    /// <summary>
    /// THE DEFECT (verifier round 3). Round 2 fixed the <c>if (installed)</c> arm of this catch and
    /// left the <c>else</c> arm overwriting unconditionally. On the timeout path
    /// <c>_installMessage</c> already holds LicenseService's honest body — the copy may still
    /// finish, look in the install folder before trying again — and the toast is already titled
    /// "Bundle install did not answer." If anything after that throws, the old else arm wrote
    /// "Install bundle file failed: …" over it and re-titled the toast "Bundle not installed.",
    /// which is the exact claim the bounded wait exists to stop making, asserted while the copy may
    /// still be running.
    ///
    /// <para>The fault is raised through <c>ThrowOnFirstToast</c>, the same seam every other
    /// post-outcome cell in this file uses: the toast raised on the timeout path throws, and the
    /// catch then has to preserve what it found.</para>
    ///
    /// <para><b>Cost, stated.</b> This waits out the real <c>InstallProbeTimeout</c> (15 s) for the
    /// same reason the sibling cell does: the card calls the bounded wait with no timeout argument,
    /// so a shorter budget would prove a number this card never passes.</para>
    /// </summary>
    [Fact]
    public async Task ATimedOutInstallKeepsItsHonestMessageWhenTheToastConsumerBreaks()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();

        var rig = NewRig(settings => settings.ClearLicense());
        rig.License.Initialize();
        ThrowOnFirstToast(rig);

        using var neverAnswers = new ManualResetEventSlim(false);
        rig.License.InstallWorkOverrideForTests = (path, _) =>
        {
            neverAnswers.Wait();
            return new BundleInstallResult(true, "should never be seen", path);
        };

        ActivateFullAuditCard? captured = null;
        try
        {
            await rig.RenderAsync(card =>
            {
                captured = card;
                SetField(card, "_bundlePath", @"C:\Users\nobody\Downloads\never.aesgcm");
                return Invoke(card, "OnInstallBundleClicked");
            });
        }
        finally
        {
            neverAnswers.Set();
            rig.License.InstallWorkOverrideForTests = null;
        }

        Assert.False((bool)GetField(captured!, "_busy")!);

        // The service's own account survived the fault…
        var inline = (string?)GetField(captured!, "_installMessage");
        Assert.NotNull(inline);
        Assert.Contains("may still finish", inline!, StringComparison.Ordinal);
        Assert.DoesNotContain("Install bundle file failed", inline!, StringComparison.Ordinal);

        // …and so did the headline, which must not call a non-answer a refusal.
        var last = rig.Toasts.LastOrDefault();
        Assert.NotNull(last);
        Assert.Equal("Bundle install did not answer.", last!.Title);
    }

    /// <summary>
    /// The same arm on the activate handler, where the sentence at stake is the one this whole lane
    /// exists to deliver. A failed re-activation over a working licence returns a message ending in
    /// <c>LicenseService.PreviousLicenceKeptMessage</c> — "Your previous licence was kept and is
    /// still active. Nothing was lost." — and the old else arm replaced it with
    /// "Activation failed: &lt;exception&gt;", deleting the one reassurance the client needs.
    /// </summary>
    [Fact]
    public async Task AFailedActivateKeepsTheKeptLicenceSentenceWhenTheToastConsumerBreaks()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();

        var client = "CARDKEEP_" + Guid.NewGuid().ToString("N");
        WriteNamedBundle("keep", client);

        var rig = NewRig(settings => SaveKey(settings, client, BundleFixtureFactory.TestKey));
        rig.License.Initialize();

        // Precondition: there is a working licence to lose.
        Assert.Equal(Tier.Full, rig.Accessor.Tier);

        ThrowOnFirstToast(rig);

        ActivateFullAuditCard? captured = null;
        await rig.RenderAsync(card =>
        {
            captured = card;
            SetField(card, "_customerName", client);
            SetField(card, "_licenseKey", Convert.ToBase64String(WrongKey()));
            return Invoke(card, "OnActivateClicked");
        });

        Assert.False((bool)GetField(captured!, "_busy")!);

        // The licence really did survive, and the card really does still say so.
        Assert.Equal(Tier.Full, rig.Accessor.Tier);
        var inline = (string?)GetField(captured!, "_activateMessage");
        Assert.NotNull(inline);
        Assert.Contains(LicenseService.PreviousLicenceKeptMessage, inline!, StringComparison.Ordinal);
        Assert.DoesNotContain("Activation failed:", inline!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The card is wired to the BOUNDED activate, not merely to a service that owns one. "Activate
    /// froze the page" was reported alongside the install freeze, and round 1 closed only the
    /// install half; this drives the card's own handler against work that never answers and grades
    /// the two things the client sees: the page comes back, and the headline does not call a
    /// non-answer a failure.
    ///
    /// <para><b>Cost, stated.</b> 15 s, the real <c>ActivateProbeTimeout</c>, because the card
    /// calls with no timeout argument.</para>
    /// </summary>
    [Fact]
    public async Task ATimedOutActivateReleasesThePageAndIsNotTitledAsAFailure()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();

        var rig = NewRig(settings => settings.ClearLicense());
        rig.License.Initialize();

        using var neverAnswers = new ManualResetEventSlim(false);
        rig.License.ActivateWorkOverrideForTests = (_, __) =>
        {
            neverAnswers.Wait();
            return new LicenseActivationResult(true, Tier.Full, "should never be seen");
        };

        ActivateFullAuditCard? captured = null;
        try
        {
            await rig.RenderAsync(card =>
            {
                captured = card;
                SetField(card, "_customerName", BundleFixtureFactory.TestClientName);
                SetField(card, "_licenseKey", Convert.ToBase64String(BundleFixtureFactory.TestKey));
                return Invoke(card, "OnActivateClicked");
            });
        }
        finally
        {
            neverAnswers.Set();
            rig.License.ActivateWorkOverrideForTests = null;
        }

        // The page is usable again — the reported symptom was that it never was.
        Assert.False((bool)GetField(captured!, "_busy")!);

        var inline = (string?)GetField(captured!, "_activateMessage");
        Assert.NotNull(inline);
        Assert.Contains("still running", inline!, StringComparison.Ordinal);

        var last = rig.Toasts.LastOrDefault();
        Assert.NotNull(last);
        Assert.Equal("Activation did not answer.", last!.Title);
    }

    /// <summary>
    /// The D6 shape on the last handler that still carried it. <c>LicenseService.Deactivate()</c> is
    /// <c>ClearLicense(); Initialize();</c> with no try, so the licence is gone the moment the first
    /// call returns — but the card set its flag only after the WHOLE call came back, and a throw
    /// from the second half printed "Deactivate failed." over an install already on Free. The flag
    /// is now read from the live state instead.
    /// </summary>
    [Fact]
    public async Task ADeactivateThatThrewAfterTheLicenceWasClearedIsNotCalledAFailure()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();

        var client = "CARDDEACT_" + Guid.NewGuid().ToString("N");
        WriteNamedBundle("deact", client);

        var rig = NewRig(settings => SaveKey(settings, client, BundleFixtureFactory.TestKey));
        rig.License.Initialize();
        Assert.Equal(Tier.Full, rig.Accessor.Tier);

        // The real first half of Deactivate(), then a fault where its second half is.
        var settingsService = rig.Services.GetRequiredService<UserSettingsService>();
        rig.License.DeactivateWorkOverrideForTests = () =>
        {
            settingsService.ClearLicense();
            rig.License.Initialize();
            throw new InvalidOperationException("a log provider is broken");
        };

        ActivateFullAuditCard? captured = null;
        string text;
        try
        {
            text = Visible(await rig.RenderAsync(card =>
            {
                captured = card;
                return Invoke(card, "OnDeactivateClicked");
            }));
        }
        finally
        {
            rig.License.DeactivateWorkOverrideForTests = null;
        }

        // The licence really is off, so "Deactivate failed." would be a false sentence.
        Assert.NotEqual(Tier.Full, rig.Accessor.Tier);
        Assert.False((bool)GetField(captured!, "_busy")!);

        var inline = (string?)GetField(captured!, "_activateMessage");
        Assert.NotNull(inline);
        Assert.StartsWith("Deactivated.", inline!, StringComparison.Ordinal);
        Assert.DoesNotContain("Deactivate failed", inline!, StringComparison.Ordinal);
        Assert.DoesNotContain("Deactivate failed", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A consumer that throws EVERY time, not just once. The catch arms raise their own
    /// notification, so a permanently broken toast consumer re-faulted the recovery path and the
    /// exception escaped the handler into MainLayout's ErrorBoundary — the page-freeze class this
    /// lane exists to close, reached through the code added to close it.
    /// </summary>
    [Fact]
    public async Task ANotificationThatThrowsEveryTimeCannotEscapeTheHandler()
    {
        if (!OperatingSystem.IsWindows()) return;

        WriteFreeBundle();

        var rig = NewRig(settings => settings.ClearLicense());
        rig.License.Initialize();

        var toast = rig.Services.GetRequiredService<ToastService>();
        toast.OnShow += _ => throw new InvalidOperationException("every toast consumer is broken");

        ActivateFullAuditCard? captured = null;

        // No try/catch here on purpose: an escaping exception fails this cell, which is the point.
        await rig.RenderAsync(card =>
        {
            captured = card;
            SetField(card, "_customerName", BundleFixtureFactory.TestClientName);
            SetField(card, "_licenseKey", "not-a-phrase-and-not-base64");
            return Invoke(card, "OnActivateClicked");
        });

        Assert.False((bool)GetField(captured!, "_busy")!);
    }

    // ── The status line does not out-claim the scan beside it ───────────────

    /// <summary>
    /// The summary read "N candidate file(s), none decrypted" while every file below it rendered
    /// its own state as "not tried yet, because no licence is saved on this Windows account".
    /// "none decrypted" asserts a decryption attempt that the service's own scan says never
    /// happened — the same family as the original client-reported defect, where "(decrypted)"
    /// named a file nothing had opened.
    /// </summary>
    [Fact]
    public async Task WithNoSavedLicenceTheStatusLineDoesNotClaimTheFilesWereTried()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Deliberately NO free bundle: with one present the card reports the Free unlock instead
        // and this branch is never reached.
        var client = "CARDNOTTRIED_" + Guid.NewGuid().ToString("N");
        WriteNamedBundle("nottried", client);

        var rig = NewRig(settings => settings.ClearLicense());
        rig.License.Initialize();

        // Preconditions, asserted rather than assumed.
        Assert.False(rig.Accessor.IsUnlocked,
            "a leftover free bundle in the shared install folder would send this cell down the "
            + "Free branch and grade nothing");
        Assert.NotEmpty(rig.License.LastBundleScan);
        Assert.True(rig.License.LastBundleScan.All(s => s.State == BundleFileState.NotTried));

        var text = Visible(await rig.RenderAsync());

        Assert.Contains("not tried: no licence is saved on this Windows account", text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("none decrypted", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE OTHER HALF OF THE SAME DEFECT. An expired bundle DID decrypt — opening it is the only
    /// way its lapse date, which the file list renders, could be read out of the GCM-authenticated
    /// manifest — and the summary line above that list still said "none decrypted". Found by
    /// reading in verify round 3 and fixed here; this cell is the render that pins it.
    ///
    /// <para>The licence is not active (the bundle lapsed), so the status line falls past the
    /// in-use and Free branches into the summary. Deliberately NO free bundle, for the same reason
    /// the not-tried cell above has none.</para>
    /// </summary>
    [Fact]
    public async Task WithOnlyAnExpiredBundleTheStatusLineSaysItOpened_NotNoneDecrypted()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI

        var client = "CARDEXPIREDSTATUS_" + Guid.NewGuid().ToString("N");
        var bundlePath = WriteExpiredFullBundle(client);

        var rig = NewRig(settings => SaveKey(settings, client, BundleFixtureFactory.TestKey));
        rig.License.Initialize();

        // Preconditions, asserted rather than assumed. A leftover free bundle in the shared install
        // folder would send this cell down the Free branch and grade nothing.
        Assert.False(rig.Accessor.IsUnlocked,
            "a free bundle in the shared install folder would render the Free branch instead");
        Assert.Equal(FullUnlockFailureReason.Expired, rig.License.LastFullFailure!.Reason);

        var scanned = Assert.Single(rig.License.LastBundleScan.Where(s => s.Path == bundlePath));
        Assert.Equal(BundleFileState.Expired, scanned.State);
        Assert.NotNull(scanned.ExpiredOn);

        var text = Visible(await rig.RenderAsync());

        // The summary now agrees with the detail line beside it, and names the lapse it read.
        Assert.Contains("file(s) opened; the newest expired on "
                        + scanned.ExpiredOn!.Value.ToString("yyyy-MM-dd"),
                        text, StringComparison.Ordinal);
        Assert.DoesNotContain("none decrypted", text, StringComparison.Ordinal);
        Assert.DoesNotContain("not tried: no licence is saved", text, StringComparison.Ordinal);
    }

    // ── A handler does not leave the previous handler's sentence ──
    /// <summary>
    /// "Full Audit activated. Reload the page to load full data." survived a SUCCESSFUL Deactivate,
    /// in the success style, beside a Tier badge reading Free. Both sibling handlers null their own
    /// message on entry; this one did not, and its success path writes no message of its own, so
    /// nothing ever overwrote it. Found by rendering in verify round 3.
    ///
    /// <para>Two handlers are driven on ONE card instance, which is the only way this shows: the
    /// stale sentence is state carried over from the click before.</para>
    /// </summary>
    [Fact]
    public async Task ASuccessfulDeactivateClearsTheActivatedSentence()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI

        WriteFullBundle();
        WriteFreeBundle();

        var rig = NewRig(settings => settings.ClearLicense());
        rig.License.Initialize();

        ActivateFullAuditCard? captured = null;
        var text = Visible(await rig.RenderAsync(async card =>
        {
            captured = card;
            SetField(card, "_customerName", BundleFixtureFactory.TestClientName);
            SetField(card, "_licenseKey", Convert.ToBase64String(BundleFixtureFactory.TestKey));
            await Invoke(card, "OnActivateClicked");

            // The precondition for the second click, asserted INSIDE the drive: a failed activate
            // would leave a different sentence behind and grade nothing.
            Assert.Equal(Tier.Full, rig.Accessor.Tier);
            Assert.Contains("Full Audit activated.",
                (string?)GetField(card, "_activateMessage") ?? string.Empty,
                StringComparison.Ordinal);

            await Invoke(card, "OnDeactivateClicked");
        }));

        // The licence really is off, so the activated sentence would be a false one.
        Assert.NotEqual(Tier.Full, rig.Accessor.Tier);
        Assert.False((bool)GetField(captured!, "_busy")!);
        Assert.Null((string?)GetField(captured!, "_activateMessage"));
        Assert.False((bool)GetField(captured!, "_activateOk")!);
        Assert.DoesNotContain("Full Audit activated.", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ActivateFullAuditCard.ReloadInstruction, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE SECOND CAUSE OF "NOT TRIED". <c>BundleFileState.NotTried</c> is published from TWO
    /// branches of <c>TryUnlockFull</c> — <c>NoSavedLicense</c> and <c>SavedKeyUnreadable</c> — and
    /// the card named only the first, in both the file list and the summary line. So a licence that
    /// IS saved but whose DPAPI blob will not unwrap on this account rendered "no licence is saved
    /// on this Windows account" beside a Customer field naming that saved licence, and beside the
    /// red panel's own, correct, "could not be read back" sentence. Found by rendering in verify
    /// round 4.
    ///
    /// <para>The unwrappable blob is six bytes of rubbish, which is what a licence saved on ANOTHER
    /// Windows account looks like to DPAPI here: the wrap is account-bound, so this reaches the real
    /// <c>SavedKeyUnreadable</c> branch rather than simulating it. No free bundle, for the same
    /// reason the sibling not-tried cell has none.</para>
    /// </summary>
    [Fact]
    public async Task WithAnUnreadableSavedKeyTheCardSaysSo_NotThatNoLicenceIsSaved()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI

        var client = "CARDUNREADABLE_" + Guid.NewGuid().ToString("N");
        WriteNamedBundle("unreadable", client);

        var rig = NewRig(settings =>
        {
            settings.ClearLicense();
            // Not a DPAPI blob at all, so UnwrapKeyDpapi throws and the service takes the
            // SavedKeyUnreadable branch — with a client name still saved, which is the whole point.
            settings.SaveLicense(client, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02 });
        });
        rig.License.Initialize();

        // Preconditions, asserted rather than assumed.
        Assert.False(rig.Accessor.IsUnlocked,
            "a leftover free bundle in the shared install folder would send this cell down the "
            + "Free branch and grade nothing");
        Assert.Equal(FullUnlockFailureReason.SavedKeyUnreadable, rig.License.LastFullFailure!.Reason);
        Assert.NotEmpty(rig.License.LastBundleScan);
        Assert.True(rig.License.LastBundleScan.All(s => s.State == BundleFileState.NotTried));

        var text = Visible(await rig.RenderAsync());

        // Both surfaces: the per-file state word and the summary line above the list.
        Assert.Contains(
            "not tried yet, because the saved licence key could not be read back on this Windows account",
            text, StringComparison.Ordinal);
        Assert.Contains(
            "not tried: the saved licence key could not be read back on this Windows account",
            text, StringComparison.Ordinal);

        // The false sentence is gone. It is card-only wording — the service's NoSavedLicense
        // message reads "No licence has been activated on this Windows account" — so this
        // discriminates the card's own claim and not the panel's.
        Assert.DoesNotContain("no licence is saved on this Windows account", text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("none decrypted", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A SPENT INSTRUCTION SURVIVED THE ACTION IT ASKED FOR. Both install success messages end
    /// "Enter your customer name and key, then click Activate.", and nothing cleared
    /// <c>_installMessage</c> — not the Activate handler, not Deactivate. So after a SUCCESSFUL
    /// Activate the card rendered that imperative, in the success style, beside a Tier badge
    /// reading Full. Found by rendering in verify round 4; same "one action, two instructions"
    /// family as the Deactivate sentence fixed in round 4.
    ///
    /// <para>Two handlers on ONE card instance, which is the only way this shows: the stale
    /// sentence is state carried over from the click before. The install drives the
    /// already-installed success branch — <c>_bundlePath</c> points at the file the fixture put in
    /// the install folder, so source and destination are the same path — which returns Success with
    /// exactly that trailing imperative.</para>
    /// </summary>
    [Fact]
    public async Task ASuccessfulActivateClearsTheSpentInstallInstruction()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI

        var bundlePath = WriteFullBundle();
        WriteFreeBundle();

        var rig = NewRig(settings => settings.ClearLicense());
        rig.License.Initialize();

        ActivateFullAuditCard? captured = null;
        var text = Visible(await rig.RenderAsync(async card =>
        {
            captured = card;

            SetField(card, "_bundlePath", bundlePath);
            await Invoke(card, "OnInstallBundleClicked");

            // The precondition for the second click, asserted INSIDE the drive: without the
            // imperative on screen this cell would grade nothing.
            Assert.True((bool)GetField(card, "_installOk")!);
            Assert.Contains("then click Activate.",
                (string?)GetField(card, "_installMessage") ?? string.Empty,
                StringComparison.Ordinal);

            SetField(card, "_customerName", BundleFixtureFactory.TestClientName);
            SetField(card, "_licenseKey", Convert.ToBase64String(BundleFixtureFactory.TestKey));
            await Invoke(card, "OnActivateClicked");

            Assert.Equal(Tier.Full, rig.Accessor.Tier);
        }));

        // The licence really is on, so "then click Activate." would be an instruction to repeat
        // the action that just succeeded.
        Assert.Equal(Tier.Full, rig.Accessor.Tier);
        Assert.False((bool)GetField(captured!, "_busy")!);
        Assert.Null((string?)GetField(captured!, "_installMessage"));
        Assert.False((bool)GetField(captured!, "_installOk")!);
        Assert.DoesNotContain("then click Activate.", text, StringComparison.Ordinal);

        // …and the badge beside it reads Full, so the removed sentence is removed from the posture
        // that made it false, not from a card that failed to activate.
        Assert.Contains("Tier Full", text, StringComparison.Ordinal);
        Assert.Contains("Full Audit activated.", text, StringComparison.Ordinal);
    }

    // ── Key-file pickup on the card (board #19) ─────────────────────────────

    /// <summary>A private install folder for a pickup test. Pickup deletes and renames in it.</summary>
    private string NewKeyFileDir()
    {
        var dir = Path.Combine(_settingsDir, "kf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteKeyFileFor(string bundlePath, string customer, byte[] key)
    {
        var path = Path.Combine(Path.GetDirectoryName(bundlePath)!,
            Path.GetFileNameWithoutExtension(bundlePath) + ".key.txt");
        File.WriteAllText(path,
            $"Customer: {customer}\r\n{Convert.ToBase64String(key)}\r\n", System.Text.Encoding.UTF8);
        return path;
    }

    /// <summary>
    /// Grants Everyone Modify on a folder, which is what a <c>C:\temp</c>-style folder and a good
    /// many real install folders already look like. Written with the ACL API rather than shelling
    /// out to icacls so the cell fails loudly if the grant does not take.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void GrantEveryoneWrite(string dir)
    {
        var info = new DirectoryInfo(dir);
        var security = info.GetAccessControl();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.WorldSid, null),
            System.Security.AccessControl.FileSystemRights.Modify,
            System.Security.AccessControl.InheritanceFlags.ObjectInherit
                | System.Security.AccessControl.InheritanceFlags.ContainerInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private const string ReadClause = "SQLTriage still read it";
    private const string NothingWaiting = "No key file is waiting beside the program.";

    /// <summary>
    /// THE INVARIANT, not one example of it. The ACL paragraph asserts an act — "SQLTriage still
    /// read it" — and the card may print that sentence only when a pair was actually opened. Run
    /// this against every rendered card in a pickup cell, so the next person who edits the
    /// paragraph has to keep the two facts apart everywhere, not just in the case that was filed.
    /// </summary>
    private static void AssertTheReadClauseMatchesTheFacts(string text, KeyFilePickupOutcome pickup)
    {
        var claimsARead = text.Contains(ReadClause, StringComparison.Ordinal);

        if (pickup.PairsSeen == 0)
        {
            Assert.False(claimsARead,
                "the card said SQLTriage read a key file, and the pass opened none");
        }

        if (text.Contains(NothingWaiting, StringComparison.Ordinal))
        {
            Assert.False(claimsARead,
                "the card said no key file is waiting AND that SQLTriage read one, in one panel");
        }
    }

    [Fact]
    public async Task ALooseFolderHoldingNoKeyFile_DoesNotClaimSQLTriageReadOne()
    {
        if (!OperatingSystem.IsWindows()) return;

        // THE SENTENCE THAT DESCRIBED AN ACT THAT NEVER HAPPENED, asserted on the SCREEN.
        //
        // The ACL warning is computed from the FOLDER, before pickup knows whether any pair is
        // there, and the paragraph welded the permission fact to a claim about reading. So a
        // loosely-ACLed folder holding a bundle and NO key file rendered both of these at once:
        //
        //     "No key file is waiting beside the program."
        //     "... SQLTriage still read it; this is a warning, not a refusal."
        //
        // The shipped UI asserted an act that provably did not occur, on a folder shape that is
        // ordinary rather than exotic. Everyone:(OI)(CI)M below is exactly that shape.
        var dir = NewKeyFileDir();
        GrantEveryoneWrite(dir);

        const string customer = "CARD_LOOSE_NOKEY_CLIENT";
        BundleFixtureFactory.WriteFullBundleNamed(
            dir, "loose.aesgcm", customer, BundleFixtureFactory.TestKey);
        // Deliberately NO key file beside it.

        var rig = NewRig(s => s.ClearLicense());
        rig.License.InstallDirOverrideForTests = dir;
        rig.License.Initialize();

        var pickup = rig.License.LastKeyFilePickup!;
        Assert.Equal(KeyFilePickupKind.NoKeyFile, pickup.Kind);
        Assert.Equal(0, pickup.PairsSeen);
        Assert.True(pickup.InstallFolderLooselyAcled,
            "the Everyone grant did not take, so this cell would prove nothing");

        var text = Visible(await rig.RenderAsync());

        // The folder warning still stands on its own — the permissions are worth tightening
        // whether or not a key file has arrived yet.
        Assert.Contains("This folder grants write access to", text, StringComparison.Ordinal);
        Assert.Contains(NothingWaiting, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ReadClause, text, StringComparison.Ordinal);
        AssertTheReadClauseMatchesTheFacts(text, pickup);
    }

    [Fact]
    public async Task ALooseFolderThatDidOpenAPair_KeepsTheSentenceAboutHavingReadIt()
    {
        if (!OperatingSystem.IsWindows()) return;

        // THE OTHER HALF. Gating a sentence is only honest if the sentence still appears when it
        // is true — otherwise the fix is a deletion wearing a condition. Same loose folder, this
        // time with a pair in it: the operator is told the folder is writable by anyone AND that
        // the file was read anyway, which is the warn-only ruling (Adrian, 2026-09-02 15:35).
        var dir = NewKeyFileDir();
        GrantEveryoneWrite(dir);

        const string customer = "CARD_LOOSE_PAIR_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            dir, "loosepair.aesgcm", customer, BundleFixtureFactory.TestKey);
        WriteKeyFileFor(bundle, customer, BundleFixtureFactory.TestKey);

        var rig = NewRig(s => s.ClearLicense());
        rig.License.InstallDirOverrideForTests = dir;
        rig.License.Initialize();

        var pickup = rig.License.LastKeyFilePickup!;
        Assert.True(pickup.PairsSeen > 0);
        Assert.True(pickup.InstallFolderLooselyAcled,
            "the Everyone grant did not take, so this cell would prove nothing");

        var text = Visible(await rig.RenderAsync());

        Assert.Contains("This folder grants write access to", text, StringComparison.Ordinal);
        Assert.Contains(ReadClause, text, StringComparison.Ordinal);
        Assert.DoesNotContain(NothingWaiting, text, StringComparison.Ordinal);
        AssertTheReadClauseMatchesTheFacts(text, pickup);
    }

    [Fact]
    public async Task AHeldPairRendersTheStandingWarningAndTheAdminAcceptButton()
    {
        if (!OperatingSystem.IsWindows()) return;

        // RATCHET WITH ADMIN CONFIRM. On a virgin install the pair is held, and the ONLY thing that
        // makes a plaintext credential sitting in the install folder visible is this panel.
        var dir = NewKeyFileDir();
        const string customer = "CARD_HELD_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            dir, "held.aesgcm", customer, BundleFixtureFactory.TestKey);
        WriteKeyFileFor(bundle, customer, BundleFixtureFactory.TestKey);

        var rig = NewRig(s => s.ClearLicense());
        rig.License.InstallDirOverrideForTests = dir;
        rig.License.Initialize();

        var html = await rig.RenderAsync();
        var text = Visible(html);

        Assert.Equal(KeyFilePickupKind.RefusedByIdentity, rig.License.LastKeyFilePickup!.Kind);
        Assert.Contains("A licence key file is still on disk.", text, StringComparison.Ordinal);
        Assert.Contains("held.key.txt", text, StringComparison.Ordinal);
        Assert.Contains("Accept licence for", text, StringComparison.Ordinal);
        Assert.Contains(customer, text, StringComparison.Ordinal);
        Assert.Contains("Check for a key file now", text, StringComparison.Ordinal);
        AssertTheReadClauseMatchesTheFacts(text, rig.License.LastKeyFilePickup!);
    }

    [Fact]
    public async Task AcceptingOneHeldPair_KeepsEveryOtherKeyFileNamedOnTheCard()
    {
        if (!OperatingSystem.IsWindows()) return;

        // VERIFIER ROUND 3, V1 — ON THE SCREEN, THROUGH THE CARD'S OWN CLICK HANDLER.
        //
        // Two held pairs on a virgin install. Before the click the panel names both. The Accept
        // narrowed the pass to one pair BEFORE the loop that fills the standing list, and the list
        // is published wholesale, so the click erased the other key file from the only surface that
        // names it: the panel's own render gate is `_keyFiles.Count > 0`, and with one pair
        // consumed and the other never listed the whole alert stopped rendering. The card then told
        // the administrator that zzz-beta.aesgcm "did not open with the saved phrase" and to enter
        // the phrase it was issued with — while that phrase sat in plaintext beside the exe,
        // unmentioned.
        var dir = NewKeyFileDir();
        const string alpha = "CARD_KEEPLIST_ALPHA";
        const string beta = "CARD_KEEPLIST_BETA";

        var alphaBundle = BundleFixtureFactory.WriteFullBundleNamed(
            dir, "aaa-alpha.aesgcm", alpha, BundleFixtureFactory.TestKey);
        var betaBundle = BundleFixtureFactory.WriteFullBundleNamed(
            dir, "zzz-beta.aesgcm", beta, WrongKey());

        var alphaKey = WriteKeyFileFor(alphaBundle, alpha, BundleFixtureFactory.TestKey);
        var betaKey = WriteKeyFileFor(betaBundle, beta, WrongKey());
        var betaBytes = File.ReadAllBytes(betaKey);

        var rig = NewRig(s => s.ClearLicense());
        rig.License.InstallDirOverrideForTests = dir;
        rig.License.Initialize();
        Assert.Equal(KeyFilePickupKind.RefusedByIdentity, rig.License.LastKeyFilePickup!.Kind);

        // The BEFORE state, asserted so the after-state is a change rather than a coincidence.
        var before = Visible(await rig.RenderAsync());
        Assert.Contains("aaa-alpha.key.txt", before, StringComparison.Ordinal);
        Assert.Contains("zzz-beta.key.txt", before, StringComparison.Ordinal);

        var after = Visible(await rig.RenderAsync(
            card => Invoke(card, "OnAcceptKeyFileClicked", alphaKey)));

        // The accept did its job.
        Assert.Equal(KeyFilePickupKind.Activated, rig.License.LastKeyFilePickup!.Kind);
        Assert.Equal(alpha, rig.Accessor.ClientName);
        Assert.False(File.Exists(alphaKey));

        // Beta is untouched on disk — so a card that stopped naming it would be hiding a live
        // plaintext credential, not describing a folder that had changed.
        Assert.True(File.Exists(betaKey));
        Assert.Equal(betaBytes, File.ReadAllBytes(betaKey));

        // THE ASSERTIONS THAT WERE FAILING.
        Assert.Contains("A licence key file is still on disk.", after, StringComparison.Ordinal);
        Assert.Contains("zzz-beta.key.txt", after, StringComparison.Ordinal);
        Assert.Contains("holds your licence key in plain text", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptingOneHeldPair_LeavesTheOthersWithTheirNameAndTheirOwnAcceptButton()
    {
        if (!OperatingSystem.IsWindows()) return;

        // THE RULING, ON THE SCREEN (Adrian, DECISIONS 2026-09-03 11:20). The round-3 fix kept the
        // other pairs NAMED, and that was only half the job: it listed them as NotTried with a null
        // customer, and the card gates its Accept button on HeldForIdentity WITH a name. So after
        // accepting one of three held pairs the administrator saw two files, a sentence saying they
        // still hold a plaintext key, and no way to act on either — the only route forward was to
        // restart the service so a boot pass would re-hold them.
        //
        // The pass now re-scans the pairs it narrowed away, after the activation, so each keeps the
        // state it really has. Driven through the card's OWN click handler.
        var dir = NewKeyFileDir();
        const string alpha = "CARD_RESCAN_ALPHA";
        const string beta = "CARD_RESCAN_BETA";
        const string gamma = "CARD_RESCAN_GAMMA";

        var alphaBundle = BundleFixtureFactory.WriteFullBundleNamed(
            dir, "aaa-rescan.aesgcm", alpha, BundleFixtureFactory.TestKey);
        var betaBundle = BundleFixtureFactory.WriteFullBundleNamed(
            dir, "mmm-rescan.aesgcm", beta, WrongKey());
        var gammaBundle = BundleFixtureFactory.WriteFullBundleNamed(
            dir, "zzz-rescan.aesgcm", gamma, WrongKey());

        var alphaKey = WriteKeyFileFor(alphaBundle, alpha, BundleFixtureFactory.TestKey);
        var betaKey = WriteKeyFileFor(betaBundle, beta, WrongKey());
        var gammaKey = WriteKeyFileFor(gammaBundle, gamma, WrongKey());

        var rig = NewRig(s => s.ClearLicense());
        rig.License.InstallDirOverrideForTests = dir;
        rig.License.Initialize();
        Assert.Equal(KeyFilePickupKind.RefusedByIdentity, rig.License.LastKeyFilePickup!.Kind);

        var after = Visible(await rig.RenderAsync(
            card => Invoke(card, "OnAcceptKeyFileClicked", alphaKey)));

        Assert.Equal(KeyFilePickupKind.Activated, rig.License.LastKeyFilePickup!.Kind);
        Assert.Equal(alpha, rig.Accessor.ClientName);
        Assert.False(File.Exists(alphaKey));
        Assert.True(File.Exists(betaKey));
        Assert.True(File.Exists(gammaKey));

        // BOTH survivors keep their name AND their own button. The button's label carries the
        // customer name, so asserting the label is asserting both halves at once.
        Assert.Contains("mmm-rescan.key.txt", after, StringComparison.Ordinal);
        Assert.Contains("zzz-rescan.key.txt", after, StringComparison.Ordinal);
        Assert.Contains($"Accept licence for {beta}", after, StringComparison.Ordinal);
        Assert.Contains($"Accept licence for {gamma}", after, StringComparison.Ordinal);

        // NOT DEMOTED: the old word is gone from the screen, not merely outnumbered.
        Assert.DoesNotContain("not opened by this check", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PickupSwitchedOffInConfig_StillNamesEveryKeyFileAndSaysWhy()
    {
        if (!OperatingSystem.IsWindows()) return;

        // DO NOT ACT BUT STILL LIST, ON THE SCREEN (Adrian, DECISIONS 2026-09-03 11:20).
        //
        // With the kill switch off the service published an EMPTY scan, so this card rendered
        // nothing whatsoever about key files: no panel, no list, no sentence. A plaintext 32-byte
        // AES key — the whole product for that customer — could sit beside the executable
        // indefinitely and no screen in the product would say so. Turning pickup off is a decision
        // not to CONSUME a dropped credential; it was never a decision to stop being told about one.
        var dir = NewKeyFileDir();
        const string customer = "CARD_KILLSWITCH_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            dir, "off-paired.aesgcm", customer, BundleFixtureFactory.TestKey);
        var pairedKey = WriteKeyFileFor(bundle, customer, BundleFixtureFactory.TestKey);

        // An orphan and a residue file, so all three of the disabled pass's states are drawn.
        var orphanKey = Path.Combine(dir, "off-orphan.key.txt");
        File.WriteAllText(orphanKey, $"Customer: {customer}\r\nnot-a-key\r\n");
        var residueKey = Path.Combine(dir, "off-old.key.txt.rejected");
        File.WriteAllText(residueKey, $"Customer: {customer}\r\nnot-a-key\r\n");
        var pairedBytes = File.ReadAllBytes(pairedKey);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Licensing:KeyFilePickup:Enabled"] = "false",
            })
            .Build();

        var rig = NewRig(s => s.ClearLicense(), configuration: config);
        rig.License.InstallDirOverrideForTests = dir;
        rig.License.Initialize();

        var text = Visible(await rig.RenderAsync());

        // THE PANEL SAYS WHY, in the operator's words and naming the setting.
        Assert.Contains("Key-file pickup is switched off for this install.", text, StringComparison.Ordinal);
        Assert.Contains("Licensing:KeyFilePickup:Enabled", text, StringComparison.Ordinal);

        // EVERY FILE IS NAMED, and every line says pickup is switched off.
        Assert.Contains("A licence key file is still on disk.", text, StringComparison.Ordinal);
        foreach (var name in new[] { "off-paired.key.txt", "off-orphan.key.txt", "off-old.key.txt.rejected" })
            Assert.Contains(name, text, StringComparison.Ordinal);
        // Lower-case "key-file", so this counts the three PER-FILE words and not the panel's
        // headline ("Key-file pickup is switched off for this install."), which is asserted above.
        // One clause per listed file is the ruling: each file is named with a state word that says
        // pickup is disabled.
        Assert.Equal(3, Regex.Matches(text, "key-file pickup is switched off for this install").Count);

        // NOTHING WAS ACTED ON, and the card does not offer the one control that would imply it was:
        // there is no Accept button, because nothing was held — nothing was opened at all.
        Assert.Equal(pairedBytes, File.ReadAllBytes(pairedKey));
        Assert.DoesNotContain("Accept licence for", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeyFileRejectedOnAnEarlierStart_IsStillNamedOnTheCard()
    {
        if (!OperatingSystem.IsWindows()) return;

        // THE SENTENCE THAT REVERSED ITSELF, asserted on the SCREEN.
        //
        // A rejected key file is renamed, not deleted, and the whole justification for that is
        // "the card says so and asks for it to be removed". It was true for one pass. The pickup
        // scanned with "*.key.txt", which does not match "X.key.txt.rejected", so from the next
        // start the card read "No key file was picked up. No key file is waiting beside the
        // program." while a plaintext 32-byte AES key sat in the install folder — the standing
        // alert had an empty list and did not render at all.
        var dir = NewKeyFileDir();
        const string customer = "CARD_RESIDUE_CLIENT";
        var wrongKey = Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i)).ToArray();
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            dir, "residue.aesgcm", customer, BundleFixtureFactory.TestKey);
        WriteKeyFileFor(bundle, customer, wrongKey);   // right name, wrong key => Rejected

        // Pass one does the rejecting and the rename.
        var first = NewRig(s => SaveKey(s, customer, BundleFixtureFactory.TestKey));
        first.License.InstallDirOverrideForTests = dir;
        first.License.Initialize();
        Assert.Equal(KeyFilePickupKind.Rejected, first.License.LastKeyFilePickup!.Kind);

        // Pass two is a RESTART: its own service, its own one-shot, the same folder.
        var restarted = NewRig(s => SaveKey(s, customer, BundleFixtureFactory.TestKey));
        restarted.License.InstallDirOverrideForTests = dir;
        restarted.License.Initialize();

        var text = Visible(await restarted.RenderAsync());

        Assert.DoesNotContain("No key file is waiting beside the program.", text, StringComparison.Ordinal);
        Assert.Contains("A licence key file is still on disk.", text, StringComparison.Ordinal);
        Assert.Contains("residue.key.txt.rejected", text, StringComparison.Ordinal);
        Assert.Contains("it still holds a plaintext key", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonAdminSeesNeitherKeyFileButton()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = NewKeyFileDir();
        const string customer = "CARD_VIEWER_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            dir, "viewer.aesgcm", customer, BundleFixtureFactory.TestKey);
        WriteKeyFileFor(bundle, customer, BundleFixtureFactory.TestKey);

        var rig = NewRig(s => s.ClearLicense(), role: AppRoles.Viewer, loopback: false);
        rig.License.InstallDirOverrideForTests = dir;
        rig.License.Initialize();

        var text = Visible(await rig.RenderAsync());

        // The warning is for everyone — a credential on disk is not an admin-only fact. The two
        // acts are not.
        Assert.Contains("A licence key file is still on disk.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Accept licence for", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Check for a key file now", text, StringComparison.Ordinal);
        Assert.Contains("restricted to a signed-in administrator", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AZeroLengthRemnant_IsNotCalledAPlaintextCredential()
    {
        if (!OperatingSystem.IsWindows()) return;

        // VERIFIER ROUND 2, D1, ON THE RENDERED COMPONENT. After an activation whose DELETE failed,
        // the file left behind is zero bytes long and its contents were already overwritten. The
        // card listed every non-Consumed outcome as Waiting, whose word is "it still holds your
        // licence key in plain text", and printed the panel headline "A .key.txt file holds your
        // licence key in plain text" above it -- both false, and both directly under the outcome
        // line that correctly said the contents had been overwritten. Two facts welded, one of them
        // asserting a state nothing measured: the same shape as the round-1 defect, in a new cell.
        var dir = NewKeyFileDir();
        const string customer = "CARD_NOTDELETED_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            dir, "remnant.aesgcm", customer, BundleFixtureFactory.TestKey);
        var keyPath = WriteKeyFileFor(bundle, customer, BundleFixtureFactory.TestKey);

        // The saved NAME matches, so the identity ratchet opens; the saved KEY is wrong, so the
        // licence really does come up from the key file rather than from the saved pair.
        var rig = NewRig(s => SaveKey(s, customer, WrongKey()));
        rig.License.InstallDirOverrideForTests = dir;

        // Hold the file open WITHOUT FileShare.Delete, from the moment between the overwrite and
        // the delete, so the delete fails and the NotDeleted arm is exercised rather than described.
        FileStream? blocker = null;
        rig.License.AfterKeyFileOverwriteForTests = (path, _) =>
            blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        try { rig.License.Initialize(); }
        finally { blocker?.Dispose(); }

        Assert.Equal(KeyFilePickupKind.Activated, rig.License.LastKeyFilePickup!.Kind);
        Assert.Equal(KeyFileConsumeState.NotDeleted, rig.License.LastKeyFilePickup!.Consume);
        Assert.True(File.Exists(keyPath), "the remnant is gone, so this cell would prove nothing");
        Assert.Equal(0, new FileInfo(keyPath).Length);

        var text = Visible(await rig.RenderAsync());

        // The file is still named -- that is the point of the panel, and it must not be lost.
        Assert.Contains("remnant.key.txt", text, StringComparison.Ordinal);
        Assert.Contains("overwritten and emptied", text, StringComparison.Ordinal);

        // AND NEITHER SENTENCE CALLING IT A CREDENTIAL IS ON THE SCREEN.
        Assert.DoesNotContain("still holds your licence key", text, StringComparison.Ordinal);
        Assert.DoesNotContain("holds your licence key in plain text", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStandingKeyFilePanel_SaysWhenItWasLastChecked()
    {
        if (!OperatingSystem.IsWindows()) return;

        // VERIFIER ROUND 2, D5. The panel is fed by LastKeyFileScan, which is replaced only when a
        // pickup pass runs, and nothing on the render path re-validates it -- deliberately, because
        // that would put directory IO back on the Blazor dispatcher. Undated, it silently outlives
        // the file it describes: TheRenderPathDoesNoDirectoryIo below deletes the folder and the
        // card still says a key file is on disk, which is the right proof of no-IO and also a
        // demonstration of the staleness. The date is what makes the claim readable as of a moment.
        var dir = NewKeyFileDir();
        const string customer = "CARD_ASOF_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            dir, "asof.aesgcm", customer, BundleFixtureFactory.TestKey);
        WriteKeyFileFor(bundle, customer, BundleFixtureFactory.TestKey);

        var rig = NewRig(s => s.ClearLicense());
        rig.License.InstallDirOverrideForTests = dir;
        rig.License.Initialize();

        var pickup = rig.License.LastKeyFilePickup!;
        Assert.Equal(KeyFilePickupKind.RefusedByIdentity, pickup.Kind);

        var text = Visible(await rig.RenderAsync());

        // VERIFIER ROUND 3, V4. The date is now taken from the SCAN's own snapshot, not from the
        // live outcome: the two are published by different statements, and a second circuit
        // scanning between two reads dated one list with another pass's clock.
        var snapshot = rig.License.LastKeyFileScanSnapshot;
        Assert.NotNull(snapshot.WhenUtc);
        Assert.NotEmpty(snapshot.Pairs);

        // The wording, and the ACTUAL moment the service recorded -- rendered in local time,
        // because the operator reading it is standing in front of the box.
        var local = snapshot.WhenUtc!.Value.ToLocalTime();
        Assert.Contains("As of the last check at", text, StringComparison.Ordinal);
        Assert.Contains(
            "As of the last check at "
            + local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            text, StringComparison.Ordinal);

        // AND THE HALF A BARE CLOCK TIME CANNOT CARRY. On the Windows service host this feature
        // targets, pickup runs once at boot and then only when an administrator clicks -- so
        // "at 08:12" reads as this morning on an install that has been up for three days, which is
        // exactly the staleness the date was added to expose. The calendar date and the age in
        // words are both on the panel.
        Assert.Contains(
            local.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
            text, StringComparison.Ordinal);
        Assert.Contains("(just now)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStandingKeyFilePanelFromDaysAgo_SaysHowOldItIs()
    {
        if (!OperatingSystem.IsWindows()) return;

        // THE OTHER HALF OF V4, and the case the feature exists for: a service host that booted
        // days ago and has never been clicked. Dating the panel is only worth anything if an OLD
        // date reads as old, so the age is driven rather than described -- the snapshot's stamp is
        // pushed back three days and the rendered words are asserted.
        //
        // The stamp is moved on the SERVICE's published snapshot, through the same field the pass
        // writes, so this measures the card's rendering of an age and not a fixture of its own.
        var dir = NewKeyFileDir();
        const string customer = "CARD_ASOF_OLD_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            dir, "asofold.aesgcm", customer, BundleFixtureFactory.TestKey);
        WriteKeyFileFor(bundle, customer, BundleFixtureFactory.TestKey);

        var rig = NewRig(s => s.ClearLicense());
        rig.License.InstallDirOverrideForTests = dir;
        rig.License.Initialize();

        var fresh = rig.License.LastKeyFileScanSnapshot;
        Assert.NotEmpty(fresh.Pairs);
        Assert.NotNull(fresh.WhenUtc);

        var threeDaysAgo = fresh.WhenUtc!.Value.AddDays(-3);
        SetField(rig.License, "_keyFileScan",
            new KeyFileScanSnapshot(fresh.Pairs, threeDaysAgo));

        var text = Visible(await rig.RenderAsync());

        Assert.Contains("3 days ago", text, StringComparison.Ordinal);
        Assert.Contains(
            threeDaysAgo.ToLocalTime().ToString("d MMM yyyy",
                System.Globalization.CultureInfo.InvariantCulture),
            text, StringComparison.Ordinal);
        Assert.DoesNotContain("(just now)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRenderPathDoesNoDirectoryIo()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The card used to enumerate the install folder on every read of its bundle list, on the
        // Blazor dispatcher, on every keystroke. The key-file panel must not reintroduce that.
        //
        // THE PROOF: the folder is DELETED after Initialize has published its scan. A render that
        // enumerated the folder would find nothing (or throw); a render that reads the service's
        // published list still names the file.
        var dir = NewKeyFileDir();
        const string customer = "CARD_NOIO_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            dir, "noio.aesgcm", customer, BundleFixtureFactory.TestKey);
        WriteKeyFileFor(bundle, customer, BundleFixtureFactory.TestKey);

        var rig = NewRig(s => s.ClearLicense());
        rig.License.InstallDirOverrideForTests = dir;
        rig.License.Initialize();
        Assert.Equal(KeyFilePickupKind.RefusedByIdentity, rig.License.LastKeyFilePickup!.Kind);

        Directory.Delete(dir, recursive: true);

        var text = Visible(await rig.RenderAsync());

        Assert.Contains("noio.key.txt", text, StringComparison.Ordinal);
        Assert.Contains("A licence key file is still on disk.", text, StringComparison.Ordinal);
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
    /// <param name="configuration">
    /// The host configuration the service reads its key-file kill switch from. Null (the default,
    /// and what every cell written before the switch existed passes) is the posture of a host that
    /// registered no IConfiguration, and behaves exactly as pickup did before it.
    /// </param>
    private Rig NewRig(Action<UserSettingsService> prepare,
                       string role = AppRoles.Admin, bool loopback = true,
                       IConfiguration? configuration = null)
    {
        var settings = new UserSettingsService(Path.Combine(_settingsDir, "user-settings.json"));
        prepare(settings);

        var accessor = new BundleAccessor();
        var license = new LicenseService(NullLogger<LicenseService>.Instance, settings, accessor,
            audit: null, configuration: configuration);
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

    /// <summary>
    /// A Full bundle in the install folder with a chosen file-name prefix, customer AND manifest
    /// date. The date is the only thing that decides precedence, and it sits inside the
    /// GCM-authenticated ciphertext, so it cannot be set from outside the encryptor.
    /// </summary>
    private string WriteNamedBundleCreated(string namePrefix, string clientName, string createdUtc)
    {
        var path = Path.Combine(_installDir, $"{namePrefix}-{Guid.NewGuid():N}.aesgcm");
        var aad = AadBuilder.Build(clientName, "Full", 1, BundleFixtureFactory.TestBuildNumber);
        var manifest = BundleFixtureFactory.MakeFullManifest(clientName, createdUtc: createdUtc);
        File.WriteAllBytes(path, BundleCrypto.EncryptManifest(
            manifest, BundleFixtureFactory.TestKey, aad));
        _createdFiles.Add(path);
        return path;
    }

    /// <summary>A Full bundle in the install folder with a chosen file-name prefix and customer.</summary>
    private string WriteNamedBundle(string namePrefix, string clientName)
    {
        var path = Path.Combine(_installDir, $"{namePrefix}-{Guid.NewGuid():N}.aesgcm");
        var aad = AadBuilder.Build(clientName, "Full", 1, BundleFixtureFactory.TestBuildNumber);
        File.WriteAllBytes(path, BundleCrypto.EncryptManifest(
            BundleFixtureFactory.MakeFullManifest(clientName), BundleFixtureFactory.TestKey, aad));
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

    /// <summary>The same, for a handler that takes arguments — the Accept button binds one.</summary>
    private static Task Invoke(object target, string method, params object?[] args) =>
        (Task)target.GetType()
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(target, args)!;

    /// <summary>Strips tags and collapses whitespace, so an assertion reads the words a person does.</summary>
    private static string Visible(string html)
        => System.Net.WebUtility.HtmlDecode(
            Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ")).Trim();
}
