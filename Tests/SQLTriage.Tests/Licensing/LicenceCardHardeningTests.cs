/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Licensing.Crypto;
using SQLTriage.Tests.Licensing.Fixtures;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// The lane opened by defects reported from a live client site on 2026-09-02: the Full Audit licence
/// card froze on Install and Activate, named the wrong file as the decrypted one, and destroyed a
/// working licence when an Activate failed.
///
/// <para>Everything here drives the REAL <see cref="LicenseService"/> over REAL bundles written by
/// the shipped encryptor. Nothing asserts on a string a screen would have shown without the code
/// that produces it having run.</para>
///
/// <para><b>Isolation.</b> <see cref="LicenseService"/> reads
/// <see cref="AppContext.BaseDirectory"/> with no seam, and that folder is shared with every other
/// licensing suite in this assembly. Each test here mints its bundles to a UNIQUE client name, so a
/// bundle another suite has in flight fails the auth tag under this name and is skipped. That is the
/// same isolation <c>ActivateFullAuditCardRenderTests</c> uses for its expiry cell.</para>
///
/// <para>This file binds only <see cref="LicenseService"/> and <see cref="BundleAccessor"/>, both of
/// which ship in BOTH build profiles, so it is deliberately NOT in the test project's
/// community Compile-Remove list.</para>
/// </summary>
public class LicenceCardHardeningTests : IDisposable
{
    private readonly string _installDir = AppContext.BaseDirectory;
    private readonly List<string> _createdFiles = new();

    private readonly string _settingsDir =
        Path.Combine(Path.GetTempPath(), "sqlt-liccard-tests-" + Guid.NewGuid().ToString("N"));

    public LicenceCardHardeningTests()
    {
        var configDir = Path.Combine(_installDir, "Config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "version.json"),
            $"{{\"version\":\"0.90.2\",\"buildNumber\":{BundleFixtureFactory.TestBuildNumber}}}");
    }

    public void Dispose()
    {
        foreach (var f in _createdFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
        }
        try { if (Directory.Exists(_settingsDir)) Directory.Delete(_settingsDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── (i) Precedence is the manifest date, not the file's write time ───────

    /// <summary>
    /// THE DEFECT. <c>TryUnlockFull</c> returned on the FIRST <c>*.aesgcm</c> that opened, in
    /// <c>Directory.GetFiles</c> order. Ten bundles were minted for one client on 2026-09-02, and
    /// which one an install ran on was decided by enumeration order.
    ///
    /// <para>File write time cannot be the tiebreak either, and this test is built so that using it
    /// would fail: <c>File.Copy</c> PRESERVES the source's last-write time, so the fixtures here set
    /// the mtimes DELIBERATELY INVERTED — the older-manifest file is the newer file on disk, and it
    /// also sorts first alphabetically. Only reading the authenticated <c>createdUtc</c> inside the
    /// GCM-protected manifest picks the right one.</para>
    /// </summary>
    [Fact]
    public void AmongOpenableBundles_TheNewestManifestDateWins_NotTheNewestFile()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI

        var client = "PRECEDENCE_" + Guid.NewGuid().ToString("N");
        var older = WriteBundle(client, "aaa-older", createdUtc: "2026-09-02T01:00:00Z");
        var newer = WriteBundle(client, "zzz-newer", createdUtc: "2026-09-02T01:00:30Z");

        // Inverted on purpose: the OLDER manifest is the NEWER file, and sorts first by name.
        File.SetLastWriteTimeUtc(older, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var (svc, acc) = MakeService(client, BundleFixtureFactory.TestKey);
        svc.Initialize();

        Assert.Equal(Tier.Full, acc.Tier);
        Assert.Equal(newer, svc.LastUnlockedBundlePath);

        var scan = svc.LastBundleScan;
        Assert.Equal(BundleFileState.InUse, StateOf(scan, newer));
        Assert.Equal(BundleFileState.Superseded, StateOf(scan, older));
    }

    // ── (ii) An expired newer bundle does not shadow a valid older one ───────

    /// <summary>
    /// The newest mint is not automatically the right one: a bundle that has lapsed must be stepped
    /// over, not preferred. Before this lane the first file that opened won and an expired one ended
    /// the search for that file, so which of these two the install ran on was again enumeration
    /// order. Here the expired file is the newer manifest AND sorts first.
    /// </summary>
    [Fact]
    public void AnExpiredNewerBundleIsSkipped_AndTheValidOlderOneIsNamed()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI

        var client = "EXPIRY_PRECEDENCE_" + Guid.NewGuid().ToString("N");
        var valid = WriteBundle(client, "mmm-valid", createdUtc: "2026-09-02T01:00:00Z");
        var expired = WriteBundle(client, "aaa-expired", createdUtc: "2026-09-02T02:00:00Z",
            licenseExpiryUtc: DateTime.UtcNow.AddDays(-1).ToString("o"));

        var (svc, acc) = MakeService(client, BundleFixtureFactory.TestKey);
        svc.Initialize();

        Assert.Equal(Tier.Full, acc.Tier);
        Assert.Equal(valid, svc.LastUnlockedBundlePath);

        var scan = svc.LastBundleScan;
        Assert.Equal(BundleFileState.InUse, StateOf(scan, valid));
        Assert.Equal(BundleFileState.Expired, StateOf(scan, expired));

        // The lapsed sibling must not put the whole install into the "renew" state: the licence
        // running here has not expired.
        Assert.Null(svc.FullExpiredOn);
    }

    // ── (iii) A failed Activate keeps the working licence ────────────────────

    /// <summary>
    /// THE DESTRUCTIVE DEFECT. <c>TryActivate</c> wrote the new credentials, re-ran the unlock, and
    /// on failure called <c>ClearLicense()</c>. One mistyped character on a working install lost the
    /// licence and dropped the app to Free, with no way back except the original licence email.
    /// </summary>
    [Fact]
    public void AFailedActivateKeepsThePreviousLicence_AndSaysSo()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI

        var client = "KEEPME_" + Guid.NewGuid().ToString("N");
        WriteBundle(client, "keepme", createdUtc: "2026-09-02T03:00:00Z");

        var (svc, acc) = MakeService(client, BundleFixtureFactory.TestKey);
        svc.Initialize();

        // The precondition, asserted rather than assumed: without a working licence to lose, the
        // rest of this test grades nothing.
        Assert.Equal(Tier.Full, acc.Tier);
        var settings = NewUserSettings();
        var (savedNameBefore, savedKeyBefore) = settings.GetSavedLicense();
        Assert.NotNull(savedNameBefore);
        Assert.NotNull(savedKeyBefore);

        var result = svc.TryActivate(client, Convert.ToBase64String(WrongKey()));

        Assert.False(result.Success);
        Assert.Contains(LicenseService.PreviousLicenceKeptMessage, result.ErrorMessage!, StringComparison.Ordinal);

        // The tier the app is actually running at, not the one the message claims.
        Assert.Equal(Tier.Full, acc.Tier);
        Assert.True(acc.IsUnlocked);
        Assert.Equal(client, acc.ClientName);

        // And the stored pair is byte-identical to what was there before the failed attempt.
        var (savedNameAfter, savedKeyAfter) = settings.GetSavedLicense();
        Assert.Equal(savedNameBefore, savedNameAfter);
        Assert.Equal(savedKeyBefore, savedKeyAfter);
    }

    // ── (iv) One faulting subscriber cannot decide the licence ───────────────

    /// <summary>
    /// <c>BundleAccessor.Replace</c> fired <c>BundleStateChanged?.Invoke(...)</c>, which runs the
    /// whole invocation list on one stack. The first subscriber that threw ended the list — every
    /// later subscriber was skipped — and the exception unwound into
    /// <c>LicenseService.TryActivate</c>, which had already resolved the licence before it called
    /// Replace. A consumer's bug could therefore fail an activation that had succeeded.
    /// </summary>
    [Fact]
    public void AThrowingSubscriberDoesNotFaultReplace_AndTheOthersStillRun()
    {
        var acc = new BundleAccessor();
        var ran = new List<string>();

        acc.BundleStateChanged += (_, _) => ran.Add("first");
        acc.BundleStateChanged += (_, _) => throw new InvalidOperationException("subscriber is broken");
        acc.BundleStateChanged += (_, _) => ran.Add("third");

        var ex = Record.Exception(() => acc.Replace(BundleFixtureFactory.MakeFullManifest(), Tier.Full));

        Assert.Null(ex);
        Assert.Equal(new[] { "first", "third" }, ran);

        // …and the state swap itself landed, which is the fact the callers depend on.
        Assert.True(acc.IsUnlocked);
        Assert.Equal(Tier.Full, acc.Tier);
    }

    /// <summary>
    /// The same rule when the FIRST subscriber is the broken one. Order matters here: an
    /// implementation that only caught after the first successful handler would pass the cell above
    /// and fail this one.
    /// </summary>
    [Fact]
    public void AThrowingFirstSubscriberDoesNotStopTheRest()
    {
        var acc = new BundleAccessor();
        var ran = new List<string>();

        acc.BundleStateChanged += (_, _) => throw new InvalidOperationException("subscriber is broken");
        acc.BundleStateChanged += (_, _) => ran.Add("second");
        acc.BundleStateChanged += (_, _) => ran.Add("third");

        var ex = Record.Exception(() => acc.Replace(null, Tier.Free));

        Assert.Null(ex);
        Assert.Equal(new[] { "second", "third" }, ran);
    }

    // ── (v) The install probe is bounded, and refuses a non-fixed drive ──────

    /// <summary>
    /// The bounded wait, driven with a source that never answers.
    ///
    /// <para><b>How the source is made, stated plainly.</b> There is no portable way to make a real
    /// Windows path whose <c>File.Exists</c> never returns — a locked file does not block, and there
    /// are no FIFOs. So this drives <c>InstallWorkOverrideForTests</c>, the documented seam that the
    /// bounded wait races in place of the real file work. What is PROVED here is the wait, the
    /// message and the release; that the four real <c>File.*</c> calls are the thing being raced is
    /// read from <c>InstallBundleFileAsync</c>, not exercised.</para>
    /// </summary>
    [Fact]
    public async Task TheInstallWaitGivesUpAndSaysWhatItAttempted()
    {
        var (svc, _) = MakeService("TIMEOUT_" + Guid.NewGuid().ToString("N"), BundleFixtureFactory.TestKey);

        using var neverAnswers = new ManualResetEventSlim(false);
        svc.InstallWorkOverrideForTests = (path, _) =>
        {
            neverAnswers.Wait();                     // released in the finally below
            return new BundleInstallResult(true, "should never be seen", path);
        };

        try
        {
            var result = await svc.InstallBundleFileAsync(
                @"C:\Users\nobody\Downloads\never.aesgcm", TimeSpan.FromSeconds(1));

            Assert.False(result.Success);
            Assert.Null(result.InstalledPath);

            // The message names the wait, the four steps, and that the copy may still land. It must
            // not claim nothing happened, because the blocking call is still running.
            Assert.Contains("waited 1 second", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("has not answered", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("may still finish", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("never.aesgcm", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            neverAnswers.Set();
        }
    }

    /// <summary>The same seam, released in time: a wait that DOES answer returns its own result.</summary>
    [Fact]
    public async Task TheInstallWaitReturnsTheRealResultWhenTheWorkAnswers()
    {
        var (svc, _) = MakeService("TIMEOUT_OK_" + Guid.NewGuid().ToString("N"), BundleFixtureFactory.TestKey);

        svc.InstallWorkOverrideForTests = (path, _) => new BundleInstallResult(true, "landed", path);

        var result = await svc.InstallBundleFileAsync(@"C:\x\y.aesgcm", TimeSpan.FromSeconds(30));

        Assert.True(result.Success);
        Assert.Equal("landed", result.Message);
    }

    /// <summary>
    /// A fixed local disk is accepted. This is the cell that must not regress: every real install
    /// path an operator types is on one.
    /// </summary>
    [Fact]
    public void AFixedDrivePathIsAccepted()
    {
        var ok = LicenseService.IsFixedDriveSource(
            Path.Combine(Path.GetTempPath(), "bundle.aesgcm"), out var problem);

        Assert.True(ok, $"the temp folder must sit on a fixed drive; guard said: {problem}");
        Assert.Equal(string.Empty, problem);
    }

    /// <summary>
    /// A drive letter that is not a fixed disk is refused, and the refusal reaches the operator
    /// through <c>InstallBundleFile</c> — i.e. the guard is wired in front of the blocking calls,
    /// not merely present.
    ///
    /// <para><b>Which cell this is.</b> The letter used here has nothing mounted on it, so Windows
    /// reports <c>DriveType.NoRootDirectory</c>. That is a non-Fixed drive type and it exercises the
    /// same conjunct a mapped network drive hits. The specifically-<c>Network</c> cell is NOT proved
    /// here: mapping a share needs a server and a network state change, which this suite will not
    /// make. If no unmounted letter exists on the box the test is skipped rather than faked.</para>
    /// </summary>
    [Fact]
    public void ANonFixedDrivePathIsRefusedBeforeAnyFileCall()
    {
        if (!OperatingSystem.IsWindows()) return;

        var letter = FindUnmountedDriveLetter();
        if (letter is null) return;   // nothing to measure on this box; not a pass claim

        var path = $"{letter}:\\bundles\\test.aesgcm";

        Assert.False(LicenseService.IsFixedDriveSource(path, out var problem));
        Assert.Contains("not a fixed drive", problem, StringComparison.OrdinalIgnoreCase);

        var (svc, _) = MakeService("DRIVE_" + Guid.NewGuid().ToString("N"), BundleFixtureFactory.TestKey);
        var result = svc.InstallBundleFile(path);

        Assert.False(result.Success);
        Assert.Contains("not a fixed drive", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.InstalledPath);
    }

    // ── (vii) The key-format message names both accepted forms ───────────────

    /// <summary>
    /// A phrase that is neither 24 words nor Base-64 used to come back as "The input is not a valid
    /// Base-64 string", which names one of the two accepted forms and hides the one the operator was
    /// almost certainly reaching for.
    /// </summary>
    [Fact]
    public void ANonPhraseNonBase64KeyNamesBothAcceptedForms()
    {
        if (!OperatingSystem.IsWindows()) return;   // TryActivate refuses earlier off Windows

        var (svc, _) = MakeService("KEYFMT_" + Guid.NewGuid().ToString("N"), BundleFixtureFactory.TestKey);

        var result = svc.TryActivate("KEYFMT client", "these three words");

        Assert.False(result.Success);
        Assert.Contains("not a 24-word phrase", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a Base-64 key", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    // ── (iv-b) The same rule stated where the client feels it: an activation ─

    /// <summary>
    /// The property the licence card's client actually depends on, driven through the REAL
    /// <see cref="LicenseService"/> rather than through <c>BundleAccessor</c> alone: a consumer of
    /// <c>BundleStateChanged</c> that throws cannot turn a resolved activation into a failed one.
    ///
    /// <para><b>What this cell does and does not discriminate, stated plainly.</b> It proves the
    /// end-to-end outcome. It does NOT distinguish which of the two guards carries it — the
    /// per-subscriber isolation inside <c>BundleAccessor.Replace</c> or the <c>catch</c> in
    /// <c>LicenseService.SafeReplace</c>. While the accessor isolates, nothing reaches that catch,
    /// so no test in this suite can turn it red; it is deliberate redundancy against the accessor
    /// regressing, and the notes for this lane grade it as such rather than as proved code.</para>
    /// </summary>
    [Fact]
    public void AThrowingSubscriberCannotFailAnActivation()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI

        var client = "SUBFAULT_" + Guid.NewGuid().ToString("N");
        WriteBundle(client, "subfault", createdUtc: "2026-09-02T04:00:00Z");

        var (svc, acc) = MakeService(client, BundleFixtureFactory.TestKey);

        var ran = 0;
        acc.BundleStateChanged += (_, _) => throw new InvalidOperationException("a consumer is broken");
        acc.BundleStateChanged += (_, _) => Interlocked.Increment(ref ran);

        var result = svc.TryActivate(client, Convert.ToBase64String(BundleFixtureFactory.TestKey));

        Assert.True(result.Success,
            $"a broken subscriber decided the licence; the service said: {result.ErrorMessage}");
        Assert.Equal(Tier.Full, acc.Tier);
        Assert.True(acc.IsUnlocked);
        Assert.True(ran > 0, "the subscriber after the broken one never ran");
    }

    // ── (v-b) A non-answer is not the same fact as a refusal ─────────────────

    /// <summary>
    /// The timeout path is marked on the RESULT, not left for a caller to infer from the prose.
    ///
    /// <para>The card titles its failure toast from this flag: a refusal is "Bundle not installed."
    /// and a non-answer is "Bundle install did not answer.", because on the timeout path the file
    /// work is still running and its copy may still land. Both titles over one message would assert
    /// as fact the one thing that message calls unknown.</para>
    /// </summary>
    [Fact]
    public async Task ATimeoutIsMarkedTimedOut_AndARefusalIsNot()
    {
        var (svc, _) = MakeService("TIMEDOUT_FLAG_" + Guid.NewGuid().ToString("N"),
                                   BundleFixtureFactory.TestKey);

        using var neverAnswers = new ManualResetEventSlim(false);
        svc.InstallWorkOverrideForTests = (path, _) =>
        {
            neverAnswers.Wait();
            return new BundleInstallResult(true, "should never be seen", path);
        };

        try
        {
            var timedOut = await svc.InstallBundleFileAsync(
                @"C:\Users\nobody\Downloads\never.aesgcm", TimeSpan.FromSeconds(1));

            Assert.False(timedOut.Success);
            Assert.True(timedOut.TimedOut, "the bounded wait must say it gave up, not that it refused");
        }
        finally
        {
            neverAnswers.Set();
        }

        // A genuine refusal knows the file was not installed, and must not carry the flag.
        svc.InstallWorkOverrideForTests = null;
        var refused = await svc.InstallBundleFileAsync("   ", TimeSpan.FromSeconds(30));

        Assert.False(refused.Success);
        Assert.False(refused.TimedOut, "a refusal is not a non-answer");
    }

    // ── (viii) The lapse date is the latest one, not the last file seen ──────

    /// <summary>
    /// With several expired bundles and none current, the date the operator is shown was whichever
    /// expired file <c>Directory.GetFiles</c> returned LAST: the assignment inside the per-file loop
    /// had no comparison. Ten bundles were minted for one client on 2026-09-02, which is exactly the
    /// shape that makes an arbitrary date visible. The question the message answers is "when did
    /// this install stop being licensed", so the answer is the LATEST lapse.
    ///
    /// <para>The fixture puts the EARLIER lapse last in enumeration order by name, so the previous
    /// implementation prints the earlier date and fails here. That ordering is asserted rather than
    /// assumed: if the filesystem hands the files back in another order this cell fails loudly
    /// instead of passing without measuring anything.</para>
    /// </summary>
    [Fact]
    public void WithSeveralExpiredBundles_TheLatestLapseIsTheOneNamed()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI

        var client = "LAPSE_" + Guid.NewGuid().ToString("N");
        var laterLapse = DateTime.UtcNow.AddDays(-1);
        var earlierLapse = DateTime.UtcNow.AddDays(-40);

        var later = WriteBundle(client, "aaa-later-lapse", createdUtc: "2026-07-01T00:00:00Z",
            licenseExpiryUtc: laterLapse.ToString("o"));
        var earlier = WriteBundle(client, "zzz-earlier-lapse", createdUtc: "2026-06-01T00:00:00Z",
            licenseExpiryUtc: earlierLapse.ToString("o"));

        var order = Directory.GetFiles(_installDir, "*.aesgcm", SearchOption.TopDirectoryOnly);
        Assert.True(Array.IndexOf(order, earlier) > Array.IndexOf(order, later),
            "the fixture needs the EARLIER lapse enumerated last, or this cell does not discriminate");

        var (svc, acc) = MakeService(client, BundleFixtureFactory.TestKey);
        svc.Initialize();

        Assert.NotEqual(Tier.Full, acc.Tier);   // nothing current opened, which is the branch under test

        Assert.NotNull(svc.FullExpiredOn);
        Assert.Equal(laterLapse.ToString("yyyy-MM-dd"), svc.FullExpiredOn!.Value.ToString("yyyy-MM-dd"));

        var failure = svc.LastFullFailure;
        Assert.NotNull(failure);
        Assert.Equal(FullUnlockFailureReason.Expired, failure!.Reason);
        Assert.Contains(laterLapse.ToString("yyyy-MM-dd"), failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(earlierLapse.ToString("yyyy-MM-dd"), failure.Message, StringComparison.Ordinal);
    }

    // ── The other two bounded waits ─────────────────────────────────────────

    /// <summary>
    /// THE HALF OF THE FREEZE THAT ROUND 1 LEFT OPEN. The client reported Install AND Activate
    /// freezing the page. Round 1 bounded Install and gave Activate a <c>finally</c> — which closes
    /// a FAULT and does nothing at all about a call that never returns. Underneath,
    /// <c>TryUnlockFull</c> enumerates the install folder and reads every <c>.aesgcm</c> in it, and
    /// this lane deliberately stopped short-circuiting on the first file that opened, so the
    /// unbounded path got WIDER: on the client install holding ten bundles an Activate now reads
    /// ten files where it used to read one.
    ///
    /// <para>The wait is bounded; the work is not. The message has to say so rather than report a
    /// failure that has not happened, and <c>TimedOut</c> carries that fact to the card's headline
    /// instead of the card sniffing the text.</para>
    /// </summary>
    [Fact]
    public async Task TheActivateWaitGivesUpAndSaysTheWorkIsStillRunning()
    {
        var (svc, _) = MakeService("ACTTIMEOUT_" + Guid.NewGuid().ToString("N"),
                                   BundleFixtureFactory.TestKey);

        using var neverAnswers = new ManualResetEventSlim(false);
        svc.ActivateWorkOverrideForTests = (_, __) =>
        {
            neverAnswers.Wait();                     // released in the finally below
            return new LicenseActivationResult(true, Tier.Full, "should never be seen");
        };

        try
        {
            var result = await svc.TryActivateAsync("who", "ever", TimeSpan.FromSeconds(1));

            Assert.False(result.Success);
            Assert.True(result.TimedOut);

            // It must not claim the activation failed, because that is the one thing not yet known.
            Assert.Contains("waited 1 second", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("has not answered", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("still running", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Do not assume it failed", result.ErrorMessage!, StringComparison.Ordinal);
        }
        finally
        {
            neverAnswers.Set();
        }
    }

    /// <summary>The same seam, answering in time: the real result comes back, and it is NOT flagged
    /// as a non-answer. Without this cell the bound above could be satisfied by always timing out.</summary>
    [Fact]
    public async Task TheActivateWaitReturnsTheRealResultWhenTheWorkAnswers()
    {
        var (svc, _) = MakeService("ACTTIMEOUT_OK_" + Guid.NewGuid().ToString("N"),
                                   BundleFixtureFactory.TestKey);

        svc.ActivateWorkOverrideForTests =
            (_, __) => new LicenseActivationResult(true, Tier.Full, null);

        var result = await svc.TryActivateAsync("who", "ever", TimeSpan.FromSeconds(30));

        Assert.True(result.Success);
        Assert.False(result.TimedOut);
    }

    /// <summary>
    /// Deactivate carries the same shape: <c>Deactivate()</c> calls <c>Initialize()</c>, which is
    /// the same folder enumeration and the same per-file read that can hang on a stalled volume.
    /// </summary>
    [Fact]
    public async Task TheDeactivateWaitGivesUpWhenTheWorkDoesNotAnswer()
    {
        var (svc, _) = MakeService("DEACTTIMEOUT_" + Guid.NewGuid().ToString("N"),
                                   BundleFixtureFactory.TestKey);

        using var neverAnswers = new ManualResetEventSlim(false);
        svc.DeactivateWorkOverrideForTests = () => neverAnswers.Wait();

        try
        {
            Assert.False(await svc.DeactivateAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            neverAnswers.Set();
        }

        svc.DeactivateWorkOverrideForTests = () => { /* answers at once */ };
        Assert.True(await svc.DeactivateAsync(TimeSpan.FromSeconds(30)));
    }

    // ── The summary log line counts what the key opened ──────────────────────

    /// <summary>
    /// AN EXPIRED BUNDLE DID OPEN WITH THE SAVED KEY. That is how its expiry date was read — off
    /// the manifest the service had just decrypted. The summary line excluded Expired from its
    /// count, so a folder holding one current and one expired bundle logged "1 of 2 file(s) opened
    /// with the saved key" directly beneath two per-file lines saying both opened. The per-file
    /// lines were right; only the summary was wrong, in a file whose own comments insist the log
    /// says what the UI says.
    /// </summary>
    [Fact]
    public void TheSummaryLogLineCountsAnExpiredBundleAsOpened()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI

        var client = "LOGCOUNT_" + Guid.NewGuid().ToString("N");
        WriteBundle(client, "logcount-current", createdUtc: "2026-09-02T01:00:00Z");
        WriteBundle(client, "logcount-lapsed", createdUtc: "2026-09-01T01:00:00Z",
                    licenseExpiryUtc: "2026-01-01T00:00:00Z");

        var log = new CapturingLicenseLogger();
        var (svc, acc) = MakeService(client, BundleFixtureFactory.TestKey, log);
        svc.Initialize();

        // Precondition: one current bundle is running and the expired one was seen and skipped.
        Assert.Equal(Tier.Full, acc.Tier);
        Assert.Equal(1, svc.LastBundleScan.Count(s => s.State == BundleFileState.InUse));
        Assert.Equal(1, svc.LastBundleScan.Count(s => s.State == BundleFileState.Expired));

        var summary = Assert.Single(log.Lines.Where(l =>
            l.Contains("opened with the saved key", StringComparison.Ordinal)));

        Assert.Contains("2 of 2 file(s) opened with the saved key", summary, StringComparison.Ordinal);
    }

    // ── A restore that cannot be written back is said out loud ───────────────

    /// <summary>
    /// THE FIX FOR THE DESTRUCTIVE DEFECT WAS ITSELF UNGUARDED. On a failed activation the service
    /// writes the snapshotted previous pair back with <c>SaveLicense</c>, which rewrites
    /// user-settings.json and can throw on a locked file, a full disk or an ACL change. Left
    /// unguarded that exception escaped <c>TryActivate</c> with the NEW, bad pair already
    /// persisted — the exact destructive outcome the snapshot exists to remove, now with no message
    /// at all.
    ///
    /// <para><b>How the failure is raised.</b> <c>SaveLicense</c> is a static extension over the
    /// concrete <c>UserSettingsService</c>, so it cannot be mocked. The settings file is instead
    /// held open with <c>FileShare.None</c> from a <c>BundleStateChanged</c> subscriber, which fires
    /// inside the <c>Initialize()</c> that runs between the two writes: the first save has already
    /// landed, and the restore write then meets a locked file. That is a real Windows failure mode
    /// for this exact call, not a contrived one.</para>
    /// </summary>
    [Fact]
    public void ARestoreThatCannotBeWrittenBackIsReportedInsteadOfThrown()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI

        var client = "RESTOREFAIL_" + Guid.NewGuid().ToString("N");
        WriteBundle(client, "restorefail", createdUtc: "2026-09-02T04:00:00Z");

        var (svc, acc) = MakeService(client, BundleFixtureFactory.TestKey);
        svc.Initialize();
        Assert.Equal(Tier.Full, acc.Tier);          // there is a working licence to lose

        var settingsFile = Path.Combine(_settingsDir, "user-settings.json");
        FileStream? hold = null;
        acc.BundleStateChanged += (_, __) =>
        {
            // Once only: the second Initialize would otherwise try to take a lock we hold.
            hold ??= new FileStream(settingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        };

        LicenseActivationResult result;
        try
        {
            result = svc.TryActivate(client, Convert.ToBase64String(WrongKey()));
        }
        finally
        {
            hold?.Dispose();
        }

        // The lock has to have been taken, or this cell grades nothing.
        Assert.NotNull(hold);

        // It returned rather than threw, and it says plainly what could not be done and where.
        Assert.False(result.Success);
        Assert.Contains("could NOT be written back", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains(settingsFile, result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        // And it must NOT claim the previous licence was kept, because it was not.
        Assert.DoesNotContain(LicenseService.PreviousLicenceKeptMessage, result.ErrorMessage!,
            StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Renders log messages the way a sink would, so an assertion reads the engineer's line.</summary>
    private sealed class CapturingLicenseLogger : Microsoft.Extensions.Logging.ILogger<LicenseService>
    {
        private readonly List<string> _lines = new();

        internal IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.ToList(); }
        }

        IDisposable? Microsoft.Extensions.Logging.ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add(formatter(state, exception));
        }
    }

    private UserSettingsService NewUserSettings()
        => new UserSettingsService(Path.Combine(_settingsDir, "user-settings.json"));

    private (LicenseService svc, BundleAccessor acc) MakeService(
        string clientName,
        byte[] rawKey,
        Microsoft.Extensions.Logging.ILogger<LicenseService>? logger = null)
    {
        var userSettings = NewUserSettings();
        userSettings.ClearLicense();

        var entropy = System.Text.Encoding.UTF8.GetBytes("SQLTriage.License.v1");
        var wrapped = System.Security.Cryptography.ProtectedData.Protect(
            rawKey, entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        userSettings.SaveLicense(clientName, wrapped);

        var acc = new BundleAccessor();
        var svc = new LicenseService(
            logger ?? NullLogger<LicenseService>.Instance, userSettings, acc);
        return (svc, acc);
    }

    /// <summary>Writes a Full bundle for <paramref name="clientName"/> with a chosen mint date.</summary>
    private string WriteBundle(string clientName, string namePrefix, string createdUtc,
                               string? licenseExpiryUtc = null)
    {
        var path = Path.Combine(_installDir, $"{namePrefix}-{Guid.NewGuid():N}.aesgcm");
        var manifest = BundleFixtureFactory.MakeFullManifest(
            clientName, licenseExpiryUtc, licenseId: null, createdUtc: createdUtc);
        var aad = AadBuilder.Build(clientName, "Full", 1, BundleFixtureFactory.TestBuildNumber);
        File.WriteAllBytes(path, BundleCrypto.EncryptManifest(manifest, BundleFixtureFactory.TestKey, aad));
        _createdFiles.Add(path);
        return path;
    }

    private static BundleFileState StateOf(IReadOnlyList<BundleFileStatus> scan, string path)
    {
        var hit = scan.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
        Assert.True(hit is not null,
            $"the scan did not mention {Path.GetFileName(path)}. It listed: "
            + string.Join(", ", scan.Select(s => Path.GetFileName(s.Path) + "=" + s.State)));
        return hit!.State;
    }

    private static byte[] WrongKey()
    {
        var k = (byte[])BundleFixtureFactory.TestKey.Clone();
        k[5] ^= 0xFF;
        return k;
    }

    /// <summary>
    /// A drive letter with nothing mounted on it, or null when every letter is in use. Reads the
    /// session's own drive table; makes no network call.
    /// </summary>
    private static string? FindUnmountedDriveLetter()
    {
        foreach (var letter in "QRSTUVWXY")
        {
            try
            {
                if (new DriveInfo(letter + ":\\").DriveType == DriveType.NoRootDirectory)
                    return letter.ToString();
            }
            catch (Exception) { /* letter is not usable as a drive name — try the next */ }
        }
        return null;
    }
}
