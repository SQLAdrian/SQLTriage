/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.IO;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Licensing.Crypto;
using SQLTriage.Tests.Licensing.Fixtures;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// Board #19: activating from a key file dropped beside the executable.
///
/// <para>Everything here drives the REAL <see cref="LicenseService"/> over REAL bundles written by
/// the shipped codec. No client bundle and no real licence phrase enters the test tree: the
/// fixtures come from <see cref="BundleFixtureFactory"/>, which builds bundles in process with a
/// fixed non-production key.</para>
///
/// <para><b>Isolation.</b> Pickup DELETES and RENAMES files in the folder it scans, and the test
/// assembly's own output folder is shared with every other licensing suite in this process. Each
/// test therefore points <c>LicenseService.InstallDirOverrideForTests</c> at its own temp folder
/// and its settings at its own temp file, so nothing here can touch another suite's fixtures or
/// the developer's real <c>%APPDATA%\SQLTriage\user-settings.json</c>.</para>
///
/// <para><b>Base64, not BIP39, for everything that decrypts.</b> The BIP39 wordlist is gitignored
/// and absent from every CI build, so a test that fed a 24-word phrase through
/// <c>DecodeKeyInput</c> would throw on the runner. <c>DecodeKeyInput</c> accepts both forms, so
/// the crypto-driving tests use the Base64 form and the word-count shape is exercised where it
/// actually lives: in the parser, which counts words and never decodes them.</para>
///
/// <para>This file binds only <see cref="LicenseService"/>, <see cref="BundleAccessor"/> and the
/// pickup types, all of which ship in BOTH build profiles, so it is deliberately NOT in the test
/// project's community Compile-Remove list.</para>
/// </summary>
public class KeyFilePickupTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "sqlt-keyfile-" + Guid.NewGuid().ToString("N"));

    private readonly string _installDir;
    private readonly string _settingsDir;

    /// <summary>Base64 of the fixture key — the form <c>DecodeKeyInput</c> takes without a wordlist.</summary>
    private static string KeyB64 => Convert.ToBase64String(BundleFixtureFactory.TestKey);

    /// <summary>A second, different 32-byte key. Used wherever a WRONG key is the point.</summary>
    private static readonly byte[] OtherKey = Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i)).ToArray();
    private static string OtherKeyB64 => Convert.ToBase64String(OtherKey);

    public KeyFilePickupTests()
    {
        _installDir = Path.Combine(_root, "install");
        _settingsDir = Path.Combine(_root, "settings");
        Directory.CreateDirectory(_installDir);
        Directory.CreateDirectory(_settingsDir);

        // ReadBuildNumber() reads Config\version.json from AppContext.BaseDirectory (NOT from the
        // overridden install folder), so the AAD build candidate has to match what the fixture
        // encrypted with. Same synthetic file every other licensing suite writes.
        var configDir = Path.Combine(AppContext.BaseDirectory, "Config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "version.json"),
            $"{{\"version\":\"0.90.2\",\"buildNumber\":{BundleFixtureFactory.TestBuildNumber}}}");
    }

    public void Dispose()
    {
        try
        {
            // A read-only attribute is set deliberately by one test; clear it so the tree deletes.
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* best-effort */ }
            }
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private UserSettingsService NewUserSettings()
        => new UserSettingsService(Path.Combine(_settingsDir, "user-settings.json"));

    /// <param name="configuration">
    /// The host configuration the service reads its key-file kill switch from. Null (the default,
    /// and what every cell written before the switch existed passes) is the posture of a host that
    /// registered no IConfiguration, and must behave exactly as pickup did before it.
    /// </param>
    /// <param name="logger">A capturing logger where the LINES are the thing under test.</param>
    private (LicenseService Svc, BundleAccessor Acc, UserSettingsService Settings) MakeService(
        IConfiguration? configuration = null,
        Microsoft.Extensions.Logging.ILogger<LicenseService>? logger = null)
    {
        var settings = NewUserSettings();
        var accessor = new BundleAccessor();
        var svc = new LicenseService(
            logger ?? NullLogger<LicenseService>.Instance, settings, accessor,
            audit: null, configuration: configuration)
        {
            InstallDirOverrideForTests = _installDir
        };
        return (svc, accessor, settings);
    }

    /// <summary>A configuration carrying exactly the key-file switch, set to the given value.</summary>
    private static IConfiguration ConfigWithPickup(bool enabled)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Licensing:KeyFilePickup:Enabled"] = enabled ? "true" : "false",
            })
            .Build();

    /// <summary>Renders log messages the way a sink would, so an assertion reads the engineer's line.</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<LicenseService>
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

    /// <summary>Persists a (name, key) pair the way a prior successful activation would have.</summary>
    private void SaveLicence(UserSettingsService settings, string clientName, byte[] rawKey)
    {
        var wrapped = System.Security.Cryptography.ProtectedData.Protect(
            rawKey,
            Encoding.UTF8.GetBytes("SQLTriage.License.v1"),
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        settings.SaveLicense(clientName, wrapped);
    }

    private string WriteKeyFile(string bundlePath, string customer, string key, bool bom = true)
    {
        var path = LicenseServiceKeyFilePath(bundlePath);
        var text = $"Customer: {customer}\r\n{key}\r\n";
        File.WriteAllBytes(path, bom
            ? Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray()
            : Encoding.UTF8.GetBytes(text));
        return path;
    }

    private static string LicenseServiceKeyFilePath(string bundlePath)
        => Path.Combine(Path.GetDirectoryName(bundlePath)!,
                        Path.GetFileNameWithoutExtension(bundlePath) + ".key.txt");

    // ── Pairing (spec 9.1 "Pairing") ─────────────────────────────────────────

    [Fact]
    public void APairBesideTheExe_ActivatesTheLicenceItsKeyOpens()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI required

        const string customer = "KEYFILE_PAIR_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "pair-a.aesgcm", customer, BundleFixtureFactory.TestKey);

        var (svc, acc, settings) = MakeService();

        // A licence IS saved for this customer, with the WRONG key. That is what makes this test
        // prove the key file did the work: without pickup the install stays off Full, because the
        // saved key does not open the bundle.
        SaveLicence(settings, customer, OtherKey);
        WriteKeyFile(bundle, customer, KeyB64);

        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.Activated, svc.LastKeyFilePickup!.Kind);
        Assert.Equal(customer, svc.LastKeyFilePickup!.CustomerName);
        Assert.Equal("pair-a.aesgcm", svc.LastKeyFilePickup!.BundleFileName);
        Assert.True(acc.IsUnlocked);
        Assert.Equal(Tier.Full, acc.Tier);
    }

    [Fact]
    public void TheSameBundleWithoutPickup_StaysOffFull()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string customer = "KEYFILE_DISABLED_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "pair-b.aesgcm", customer, BundleFixtureFactory.TestKey);

        var (svc, acc, settings) = MakeService();
        SaveLicence(settings, customer, OtherKey);
        var keyPath = WriteKeyFile(bundle, customer, KeyB64);

        // The --audit CLI path. Whichever process picks up first wins the key file and activates
        // for ITS OWN account only, so the short-lived interactive one must not race the service.
        svc.Initialize(null, KeyFilePickupMode.Disabled);

        Assert.NotEqual(Tier.Full, acc.Tier);
        Assert.True(File.Exists(keyPath), "Disabled pickup must not touch the key file.");
        Assert.Null(svc.LastKeyFilePickup);
    }

    [Fact]
    public void PairingIsCaseInsensitive()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string customer = "KEYFILE_CASE_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "pair-c.aesgcm", customer, BundleFixtureFactory.TestKey);

        var (svc, acc, settings) = MakeService();
        SaveLicence(settings, customer, OtherKey);

        // NTFS is case-insensitive, so an operator who typed the name themselves gets this shape.
        File.WriteAllBytes(Path.Combine(_installDir, "PAIR-C.KEY.TXT"),
            Encoding.UTF8.GetBytes($"Customer: {customer}\r\n{KeyB64}\r\n"));

        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.Activated, svc.LastKeyFilePickup!.Kind);
        Assert.Equal(Tier.Full, acc.Tier);
    }

    [Fact]
    public void AKeyFileWithNoBundle_IsNeverOpened()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Rule P2. Reading a credential you cannot use is the same act as reading one you can.
        // The proof is a handle the test holds EXCLUSIVELY: any read of this file throws, so a
        // pass that completes without an exception is a pass that did not read it.
        var orphan = Path.Combine(_installDir, "orphan.key.txt");
        File.WriteAllText(orphan, "Customer: NOBODY\r\n" + KeyB64);

        using var exclusive = new FileStream(orphan, FileMode.Open, FileAccess.Read, FileShare.None);

        var (svc, _, _) = MakeService();
        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.NoKeyFile, svc.LastKeyFilePickup!.Kind);
        var seen = Assert.Single(svc.LastKeyFileScan);
        Assert.Equal(KeyFilePairState.NoBundle, seen.State);
        Assert.Null(seen.CustomerName);
    }

    [Fact]
    public void ABundleWithNoKeyFile_LeavesTodaysBehaviourAlone()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string customer = "KEYFILE_NOPAIR_CLIENT";
        BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "lonely.aesgcm", customer, BundleFixtureFactory.TestKey);

        var (svc, acc, settings) = MakeService();
        SaveLicence(settings, customer, BundleFixtureFactory.TestKey);

        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.NoKeyFile, svc.LastKeyFilePickup!.Kind);
        Assert.Empty(svc.LastKeyFileScan);
        Assert.Equal(Tier.Full, acc.Tier);   // the ordinary saved-key path, untouched
    }

    // ── Parsing (spec 9.1 "Parsing") — pure, no filesystem, no crypto ────────

    [Fact]
    public void Parser_LabelledForm()
    {
        Assert.True(KeyFileParser.TryParse(
            "Customer: Acme Holdings Pty Ltd\nword01 word02 word03 word04 word05 word06 "
            + "word07 word08 word09 word10 word11 word12 word13 word14 word15 word16 word17 word18 "
            + "word19 word20 word21 word22 word23 word24",
            out var customer, out var key, out var problem));

        Assert.Equal("Acme Holdings Pty Ltd", customer);
        Assert.StartsWith("word01 ", key);
        Assert.Equal(24, key!.Split(' ').Length);
        Assert.Equal(string.Empty, problem);
    }

    [Fact]
    public void Parser_DeliveryNoteForm()
    {
        // AN OPERATOR RENAMING <slug>-delivery.txt TO X.key.txt IS A DOCUMENTED WORKFLOW, so this
        // cell is a compatibility contract with another repo's file. It used to assert that
        // against a PARAPHRASE — a made-up title, five lines, none of the Activation or Verify
        // blocks — while its own comment claimed "the exact line shapes issue-license.ps1 writes".
        // The claim was true and ungated: the fixture could not have caught a template edit.
        //
        // The fixture below is the real line list, TRANSCRIBED 2026-09-02 from
        // corpus tools/issue-license.ps1:394-436 ($deliveryLines, the non-demo arm), with the
        // interpolations filled in.
        //
        // WHAT THIS CELL DOES NOT DO, stated because the previous comment claimed it did: NOTHING
        // COMPARES THE TWO REPOS. The corpus is a different repository and is not present when this
        // assembly runs, so a corpus-side edit to the template cannot turn this cell red. What this
        // cell gates is the APP's side of the contract — that a note of this shape parses as a key
        // file — and what gates the corpus side is Assert-KeyPhraseIsUnambiguous, which runs over
        // the real $deliveryLines array at mint time and refuses to write a note the app would read
        // differently. Two guards, one on each side of a boundary neither can see across.
        var note = RealDeliveryNote();

        Assert.True(KeyFileParser.TryParse(note, out var customer, out var key, out _));
        Assert.Equal("Acme Holdings Pty Ltd", customer);
        Assert.Equal(24, key!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.StartsWith("abandon ", key, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_DeliveryNote_HasExactlyOneTwentyFourWordLine()
    {
        // THE CROSS-REPO ASYMMETRY, GATED RATHER THAN HOPED.
        //
        // Two readers pull the phrase out of the same file and they do not share a rule. This app's
        // KeyFileParser takes the FIRST 24-word line; the mint wrapper's Get-KeyPhraseFromKeyFileText
        // (corpus tools/issue-license.ps1) takes the LAST. On the minted two-line key file they
        // cannot disagree — there is one candidate. On the delivery note they agree only while the
        // note contains exactly ONE 24-word line, which is a property of a template in a DIFFERENT
        // repository that nobody was measuring.
        //
        // A single added sentence that happens to run to 24 words above the phrase would split the
        // two readers: the app would activate on the sentence and refuse the licence, the wrapper's
        // read-back would still pass. It is deliberately stricter than Parser_DeliveryNoteForm,
        // which would still pass with a 24-word line added BELOW the phrase.
        //
        // IT IS A TRIPWIRE ON THE TRANSCRIPTION, NOT ON THE OTHER REPO. RealDeliveryNote() is a
        // 2026-09-02 copy and the corpus is not present when this runs, so an edit made THERE
        // cannot fail this cell. The corpus-side guard is the one that sees the real template.
        var lines = RealDeliveryNote().Split('\n');

        var twentyFourWordLines = lines
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0
                        && l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length == 24)
            .ToList();

        Assert.Single(twentyFourWordLines);
        Assert.StartsWith("abandon ", twentyFourWordLines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The delivery note as <c>issue-license.ps1</c> wrote it on 2026-09-02 — TRANSCRIBED from that
    /// script's <c>$deliveryLines</c> (tools/issue-license.ps1:394-436, the non-demo arm) with the
    /// interpolations filled in, not paraphrased. The demo arm only APPENDS lines below, so a note
    /// that parses in this shape parses in that one too.
    ///
    /// <para><b>It is a transcription, and nothing compares the two repos.</b> The corpus repo is
    /// not present when this assembly runs. This fixture can go stale against the template without
    /// any cell here failing; the guard that cannot go stale is the corpus-side
    /// <c>Assert-KeyPhraseIsUnambiguous</c>, which evaluates the REAL array at mint time. Re-prove
    /// this fixture by diffing it against that script whenever the delivery note is edited.</para>
    /// </summary>
    private static string RealDeliveryNote()
    {
        const string customer = "Acme Holdings Pty Ltd";
        const string bundleName = "acme-holdings-pty-ltd.aesgcm";
        const string phrase =
            "abandon ability able about above absent absorb abstract absurd abuse access accident "
            + "account accuse achieve acid acoustic acquire across act action actor actress actual";
        const string title = "SQLTriage - Full edition license";

        return string.Join("\r\n", new[]
        {
            title,
            new string('=', title.Length),
            "",
            $"Customer   : {customer}",
            "Tier       : Full",
            "Expiry     : 2027-09-02",
            "License id : SQLT-2026-0042",
            "Instances  : 3 seat(s)",
            $"Bundle     : {bundleName}",
            "SHA-256    : 9F2C4A1E7B0D6358E4A9C1F03B7D28E56A0C9147D3B8E2F05A6C4917B0D3E8F2",
            "",
            "Your 24-word key phrase (keep this private - it unlocks your edition):",
            "",
            $"  {phrase}",
            "",
            "Activation:",
            $"  1. Copy the bundle file ({bundleName}) into the SQLTriage install folder,",
            "     next to SQLTriage.exe.",
            "  2. Launch SQLTriage, open Settings -> License, and enter:",
            $"        Customer name : {customer}",
            "        Key phrase    : the 24 words above",
            "  3. The Full edition unlocks immediately (offline; no sign-up, no phone-home).",
            "",
            "Verify your download before activating (optional):",
            $"  PowerShell> (Get-FileHash .\\{bundleName} -Algorithm SHA256).Hash",
            "  It must equal the SHA-256 above.",
        });
    }

    [Fact]
    public void Parser_PhraseOnly_IsRefusedForHavingNoCustomer()
    {
        Assert.False(KeyFileParser.TryParse(KeyB64, out var customer, out _, out var problem));
        Assert.Null(customer);
        Assert.Contains("does not say which customer", problem);
    }

    [Fact]
    public void Parser_Base64KeyAccepted()
    {
        Assert.True(KeyFileParser.TryParse($"Customer: X\n{KeyB64}", out var customer, out var key, out _));
        Assert.Equal("X", customer);
        Assert.Equal(KeyB64, key);
    }

    [Fact]
    public void Parser_TwentyFiveWordsIsNotAKey()
    {
        var twentyFive = string.Join(' ', Enumerable.Range(1, 25).Select(i => "w" + i));
        Assert.False(KeyFileParser.TryParse($"Customer: X\n{twentyFive}", out _, out var key, out var problem));
        Assert.Null(key);
        Assert.Contains("no 24-word phrase", problem);
    }

    [Fact]
    public void Parser_ToleratesBomCrlfAndSurroundingWhitespace()
    {
        // CorpusEncryptor writes with Encoding.UTF8, whose preamble .NET emits, so a BOM is the
        // EXPECTED case. Left on the front of line one it would break the Customer match and make
        // every minted key file read as malformed.
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes($"  Customer :  Padded Name  \r\n\r\n   {KeyB64}   \r\n"))
            .ToArray();

        Assert.True(KeyFileParser.TryParse(KeyFileParser.Decode(bytes), out var customer, out var key, out _));
        Assert.Equal("Padded Name", customer);
        Assert.Equal(KeyB64, key);
    }

    [Fact]
    public void AnOversizeKeyFile_IsRefusedWithoutBeingRead()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string customer = "KEYFILE_BIG_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "big.aesgcm", customer, BundleFixtureFactory.TestKey);

        var keyPath = LicenseServiceKeyFilePath(bundle);
        File.WriteAllText(keyPath, new string('x', 9 * 1024));

        // Same proof as the orphan test: an exclusive handle makes any read throw, so completing
        // without an exception is the evidence the ceiling was applied BEFORE the read.
        using var exclusive = new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var (svc, _, settings) = MakeService();
        SaveLicence(settings, customer, BundleFixtureFactory.TestKey);
        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.Malformed, svc.LastKeyFilePickup!.Kind);
        Assert.Contains("too large", svc.LastKeyFilePickup!.Message);
    }

    // ── The identity ratchet (Adrian, 2026-09-02 15:35) ──────────────────────

    [Fact]
    public void AVirginInstall_HoldsThePairAndDecryptsNothing()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string customer = "KEYFILE_VIRGIN_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "virgin.aesgcm", customer, BundleFixtureFactory.TestKey);
        var keyPath = WriteKeyFile(bundle, customer, KeyB64);
        var before = File.ReadAllBytes(keyPath);

        var (svc, acc, _) = MakeService();   // nothing saved
        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.RefusedByIdentity, svc.LastKeyFilePickup!.Kind);
        Assert.Equal(customer, svc.LastKeyFilePickup!.CustomerName);
        Assert.NotEqual(Tier.Full, acc.Tier);
        Assert.Equal(before, File.ReadAllBytes(keyPath));   // untouched: nothing consumed
        Assert.Equal(KeyFilePairState.HeldForIdentity, Assert.Single(svc.LastKeyFileScan).State);
    }

    [Fact]
    public void AForeignCustomer_IsHeldAndTheSavedLicenceStands()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string mine = "KEYFILE_MINE_CLIENT";
        const string theirs = "KEYFILE_THEIRS_CLIENT";

        // My own working install: my bundle, my key, saved.
        BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "a-mine.aesgcm", mine, BundleFixtureFactory.TestKey);

        // Their pair, planted beside it. It is genuine and WOULD decrypt — which is exactly why
        // the gate runs on the parsed NAME, before any decrypt.
        var theirBundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "b-theirs.aesgcm", theirs, OtherKey);
        var theirKeyPath = WriteKeyFile(theirBundle, theirs, OtherKeyB64);
        var before = File.ReadAllBytes(theirKeyPath);

        var (svc, acc, settings) = MakeService();
        SaveLicence(settings, mine, BundleFixtureFactory.TestKey);

        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.RefusedByIdentity, svc.LastKeyFilePickup!.Kind);
        Assert.Equal(theirs, svc.LastKeyFilePickup!.CustomerName);
        Assert.Equal(before, File.ReadAllBytes(theirKeyPath));
        Assert.Equal(Tier.Full, acc.Tier);
        Assert.Equal(mine, acc.ClientName);            // still MY licence
        Assert.Equal(mine, settings.GetSavedLicense().ClientName);
    }

    [Fact]
    public void AnAdminAcceptingAHeldPair_IsTheOnePathPastTheRatchet()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string customer = "KEYFILE_ACCEPT_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "accept.aesgcm", customer, BundleFixtureFactory.TestKey);
        var keyPath = WriteKeyFile(bundle, customer, KeyB64);

        var (svc, acc, _) = MakeService();
        svc.Initialize();
        Assert.Equal(KeyFilePickupKind.RefusedByIdentity, svc.LastKeyFilePickup!.Kind);

        var outcome = svc.AcceptKeyFilePair(keyPath);

        Assert.Equal(KeyFilePickupKind.Activated, outcome.Kind);
        Assert.Equal(Tier.Full, acc.Tier);
        Assert.Equal(customer, acc.ClientName);
        Assert.False(File.Exists(keyPath));
    }

    [Fact]
    public void AnAcceptedPair_DoesNotEraseTheOtherKeyFilesFromTheStandingList()
    {
        if (!OperatingSystem.IsWindows()) return;

        // VERIFIER ROUND 3, V1. Accept narrowed `pairs` BEFORE the loop that fills the standing
        // list, and the list is published wholesale — so accepting one held pair deleted every
        // OTHER waiting key file from the only surface that names them. Two held pairs on a virgin
        // install, one click, and a second plaintext credential was still beside the exe with
        // nothing on any screen saying so. It is the same failure the residue glob closed, on a
        // different path, and the fix is that Accept narrows what is OPENED, not what is LISTED.
        const string alpha = "KEYFILE_KEEPLIST_ALPHA";
        const string beta = "KEYFILE_KEEPLIST_BETA";

        var alphaBundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "aaa-alpha.aesgcm", alpha, BundleFixtureFactory.TestKey);
        var betaBundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "zzz-beta.aesgcm", beta, OtherKey);

        var alphaKey = WriteKeyFile(alphaBundle, alpha, KeyB64);
        var betaKey = WriteKeyFile(betaBundle, beta, OtherKeyB64);
        var betaBytes = File.ReadAllBytes(betaKey);

        var (svc, acc, _) = MakeService();          // virgin install: both pairs are HELD
        svc.Initialize();
        Assert.Equal(KeyFilePickupKind.RefusedByIdentity, svc.LastKeyFilePickup!.Kind);
        Assert.Equal(2, svc.LastKeyFileScan.Count);

        var outcome = svc.AcceptKeyFilePair(alphaKey);

        // The accept did what it was asked to do…
        Assert.Equal(KeyFilePickupKind.Activated, outcome.Kind);
        Assert.Equal(alpha, acc.ClientName);
        Assert.False(File.Exists(alphaKey));

        // …and beta is untouched on disk, which is what makes the missing list line a defect and
        // not a description.
        Assert.True(File.Exists(betaKey));
        Assert.Equal(betaBytes, File.ReadAllBytes(betaKey));

        // THE ASSERTION THAT WAS FAILING: it is still NAMED, as a file nothing opened.
        //
        // ACCEPT RE-SCANS (Adrian, DECISIONS 2026-09-03 11:20). It was NotTried with a null
        // customer, which is a demotion: the card's Accept button renders only for
        // HeldForIdentity WITH a name, so accepting alpha left beta named and unactionable. The
        // pass now re-scans the pairs it narrowed away, after the activation, so beta carries the
        // state it really has — held, because this install is now licensed to alpha.
        var stillListed = Assert.Single(svc.LastKeyFileScan,
            s => string.Equals(s.KeyFilePath, betaKey, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(KeyFilePairState.HeldForIdentity, stillListed.State);
        Assert.Equal(beta, stillListed.CustomerName);
        Assert.NotEmpty(svc.LastKeyFileScan);
    }

    [Fact]
    public void AcceptingOneHeldPair_ReScansSoTheOthersKeepTheirNameAndTheirOwnAcceptButton()
    {
        if (!OperatingSystem.IsWindows()) return;

        // THE RULING, AT THE SERVICE (Adrian, DECISIONS 2026-09-03 11:20). Three held pairs on a
        // virgin install. An administrator accepts one. The other two must not be demoted to
        // NotTried-with-no-name, because the card gates its Accept button on HeldForIdentity AND a
        // customer name — so a demotion left the administrator with two named files, no button, and
        // no way forward except restarting the service.
        //
        // The customer name is the load-bearing half: it is the button's label AND the gate.
        const string alpha = "KEYFILE_RESCAN_ALPHA";
        const string beta = "KEYFILE_RESCAN_BETA";
        const string gamma = "KEYFILE_RESCAN_GAMMA";

        var alphaBundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "aaa-rescan.aesgcm", alpha, BundleFixtureFactory.TestKey);
        var betaBundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "mmm-rescan.aesgcm", beta, OtherKey);
        var gammaBundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "zzz-rescan.aesgcm", gamma, OtherKey);

        var alphaKey = WriteKeyFile(alphaBundle, alpha, KeyB64);
        var betaKey = WriteKeyFile(betaBundle, beta, OtherKeyB64);
        var gammaKey = WriteKeyFile(gammaBundle, gamma, OtherKeyB64);
        var betaBytes = File.ReadAllBytes(betaKey);
        var gammaBytes = File.ReadAllBytes(gammaKey);

        var (svc, acc, _) = MakeService();          // virgin install: all three are HELD
        svc.Initialize();
        Assert.Equal(3, svc.LastKeyFileScan.Count);

        var outcome = svc.AcceptKeyFilePair(alphaKey);

        Assert.Equal(KeyFilePickupKind.Activated, outcome.Kind);
        Assert.Equal(alpha, acc.ClientName);
        Assert.False(File.Exists(alphaKey));

        // Neither of the other two was opened, renamed or touched by the accept.
        Assert.Equal(betaBytes, File.ReadAllBytes(betaKey));
        Assert.Equal(gammaBytes, File.ReadAllBytes(gammaKey));
        Assert.Empty(Directory.GetFiles(_installDir, "*.rejected"));

        // …and both keep the name and the state the card needs to draw an Accept button.
        foreach (var (path, who) in new[] { (betaKey, beta), (gammaKey, gamma) })
        {
            var listed = Assert.Single(svc.LastKeyFileScan,
                s => string.Equals(s.KeyFilePath, path, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(KeyFilePairState.HeldForIdentity, listed.State);
            Assert.Equal(who, listed.CustomerName);
        }

        // NOT DEMOTED. Stated as its own assertion because NotTried is the exact regression.
        Assert.DoesNotContain(svc.LastKeyFileScan, s => s.State == KeyFilePairState.NotTried);
    }

    [Fact]
    public void TheReScanAfterAnAccept_DoesNotOpenTheOtherPairs()
    {
        if (!OperatingSystem.IsWindows()) return;

        // THE RE-SCAN IS A LOOK, NOT AN ACT. It reads a pair's bytes to parse the customer name and
        // evaluate the identity gate — exactly what the boot pass does to hold a pair — and it stops
        // there. This cell is what stops the re-scan quietly growing into a second activation pass:
        // beta's key OPENS beta's bundle, and this install is licensed to alpha after the accept, so
        // an "act" here would consume beta and silently switch the licence a second time.
        const string alpha = "KEYFILE_RESCAN_LOOK_ALPHA";
        const string beta = "KEYFILE_RESCAN_LOOK_BETA";

        var alphaBundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "aaa-look.aesgcm", alpha, BundleFixtureFactory.TestKey);
        var betaBundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "zzz-look.aesgcm", beta, BundleFixtureFactory.TestKey);

        var alphaKey = WriteKeyFile(alphaBundle, alpha, KeyB64);
        var betaKey = WriteKeyFile(betaBundle, beta, KeyB64);
        var betaBytes = File.ReadAllBytes(betaKey);

        var (svc, acc, _) = MakeService();
        svc.Initialize();

        svc.AcceptKeyFilePair(alphaKey);

        Assert.Equal(alpha, acc.ClientName);            // the accepted one, and only that one
        Assert.True(File.Exists(betaKey));
        Assert.Equal(betaBytes, File.ReadAllBytes(betaKey));

        var listed = Assert.Single(svc.LastKeyFileScan,
            s => string.Equals(s.KeyFilePath, betaKey, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(KeyFilePairState.HeldForIdentity, listed.State);
        Assert.Equal(beta, listed.CustomerName);
    }

    [Fact]
    public void AcceptingAKeyFileThatHasGone_DoesNotSayNothingIsWaiting()
    {
        if (!OperatingSystem.IsWindows()) return;

        // VERIFIER ROUND 3, V2. A card is rendered once and clicked later, and the panel documents
        // itself as possibly stale — so "the accepted key file is no longer there" is a NORMAL
        // outcome, not an exotic one. The narrowed pair list was then empty and the pass returned
        // NothingActedOnMessage(), which counted orphans and residue only: with a second PAIRED key
        // file in the folder it returned the bare "No key file is waiting beside the program.",
        // which the card prints and a toast repeats. The message is a claim about the folder.
        const string alpha = "KEYFILE_GONE_ALPHA";
        const string beta = "KEYFILE_GONE_BETA";

        var alphaBundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "aaa-gone.aesgcm", alpha, BundleFixtureFactory.TestKey);
        var betaBundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "zzz-gone.aesgcm", beta, OtherKey);

        var alphaKey = WriteKeyFile(alphaBundle, alpha, KeyB64);
        var betaKey = WriteKeyFile(betaBundle, beta, OtherKeyB64);

        var (svc, _, _) = MakeService();
        svc.Initialize();
        Assert.Equal(2, svc.LastKeyFileScan.Count);

        // The stale-card race, made concrete: the accepted file goes between render and click.
        File.Delete(alphaKey);

        var outcome = svc.AcceptKeyFilePair(alphaKey);

        Assert.Equal(KeyFilePickupKind.NoKeyFile, outcome.Kind);
        Assert.DoesNotContain("No key file is waiting", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("aaa-gone.key.txt is no longer beside the program", outcome.Message,
            StringComparison.Ordinal);
        Assert.Contains("STILL HOLD A PLAINTEXT KEY", outcome.Message, StringComparison.Ordinal);

        // And the file the sentence is about is really still there.
        //
        // ACCEPT RE-SCANS (2026-09-03): beta carries its real state — held, on a virgin install —
        // rather than NotTried, and the sentence above is counted from the pairs the accept
        // narrowed away rather than from that state word, so the count does not depend on it.
        Assert.True(File.Exists(betaKey));
        Assert.Contains(svc.LastKeyFileScan,
            s => string.Equals(s.KeyFilePath, betaKey, StringComparison.OrdinalIgnoreCase)
                 && s.State == KeyFilePairState.HeldForIdentity
                 && s.CustomerName == beta);
    }

    // ── Consuming ────────────────────────────────────────────────────────────

    [Fact]
    public void OnSuccess_TheKeyFileIsGone()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string customer = "KEYFILE_CONSUME_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "consume.aesgcm", customer, BundleFixtureFactory.TestKey);
        var keyPath = WriteKeyFile(bundle, customer, KeyB64);

        var (svc, _, settings) = MakeService();
        SaveLicence(settings, customer, OtherKey);
        svc.Initialize();

        Assert.Equal(KeyFileConsumeState.Consumed, svc.LastKeyFilePickup!.Consume);
        Assert.False(File.Exists(keyPath));
    }

    [Fact]
    public void TheOverwriteHappensBEFORETheDelete()
    {
        if (!OperatingSystem.IsWindows()) return;

        // THE ORDERING TEST. If anyone reorders Consume to delete first, a failed delete would
        // leave the phrase intact on disk. It asserts, from the moment between the two steps, that
        // the file is already empty AND that the overwrite wrote the whole file — then blocks the
        // delete so the NotDeleted branch is exercised rather than described.
        //
        // THE BYTE COUNT IS WHY THIS CELL MEASURES ANYTHING. Reading the file back here witnesses
        // only the truncate: the write and the SetLength(0) share one handle, so the bytes on disk
        // are an empty array either way, and Assert.All over an empty array passes. Deleting the
        // fs.Write line outright used to leave this test — and all 26 in the file — green. The
        // count is the only witness that zeros were written, so it is asserted against the key
        // file's real length on disk.
        const string customer = "KEYFILE_ORDER_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "order.aesgcm", customer, BundleFixtureFactory.TestKey);
        var keyPath = WriteKeyFile(bundle, customer, KeyB64);
        var originalLength = new FileInfo(keyPath).Length;
        Assert.True(originalLength > 0, "the fixture key file is empty, so the cell would be vacuous");

        var (svc, _, settings) = MakeService();
        SaveLicence(settings, customer, OtherKey);

        FileStream? blocker = null;
        byte[]? bytesBetweenTheSteps = null;
        long bytesOverwritten = -1;
        svc.AfterKeyFileOverwriteForTests = (path, written) =>
        {
            bytesOverwritten = written;
            bytesBetweenTheSteps = File.ReadAllBytes(path);
            // No FileShare.Delete: the delete that follows must fail.
            blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        };

        try
        {
            svc.Initialize();
        }
        finally
        {
            blocker?.Dispose();
        }

        // The overwrite covered the WHOLE key file, and it happened before the delete was tried.
        Assert.Equal(originalLength, bytesOverwritten);
        Assert.NotNull(bytesBetweenTheSteps);
        Assert.All(bytesBetweenTheSteps!, b => Assert.Equal(0, b));
        Assert.Equal(KeyFilePickupKind.Activated, svc.LastKeyFilePickup!.Kind);
        Assert.Equal(KeyFileConsumeState.NotDeleted, svc.LastKeyFilePickup!.Consume);
        Assert.Contains("could not be removed", svc.LastKeyFilePickup!.Message);
    }

    [Fact]
    public void AReadOnlyKeyFile_IsReportedAsStillHoldingTheKey()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string customer = "KEYFILE_RO_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "readonly.aesgcm", customer, BundleFixtureFactory.TestKey);
        var keyPath = WriteKeyFile(bundle, customer, KeyB64);
        var original = File.ReadAllBytes(keyPath);
        File.SetAttributes(keyPath, FileAttributes.ReadOnly);

        var (svc, acc, settings) = MakeService();
        SaveLicence(settings, customer, OtherKey);
        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.Activated, svc.LastKeyFilePickup!.Kind);
        Assert.Equal(Tier.Full, acc.Tier);
        Assert.Equal(KeyFileConsumeState.Intact, svc.LastKeyFilePickup!.Consume);
        Assert.True(File.Exists(keyPath));
        Assert.Equal(original, File.ReadAllBytes(keyPath));      // the phrase is STILL THERE
        Assert.Contains("STILL ON DISK", svc.LastKeyFilePickup!.Message);
        Assert.Contains(keyPath, svc.LastKeyFilePickup!.Message); // the path is named
    }

    // ── Failure paths ────────────────────────────────────────────────────────

    [Fact]
    public void AWrongKey_LeavesTheWorkingLicenceExactlyWhereItWas()
    {
        if (!OperatingSystem.IsWindows()) return;

        // THE REGRESSION TEST for LicenseService's destructive failed-Activate. A wrong key
        // reaching TryActivate would have persisted, failed, and (before the snapshot) cleared the
        // working licence. Pickup probes read-only first, so that arm is unreachable from here.
        const string customer = "KEYFILE_WRONGKEY_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "wrongkey.aesgcm", customer, BundleFixtureFactory.TestKey);
        var keyPath = WriteKeyFile(bundle, customer, OtherKeyB64);   // wrong key, right name

        var (svc, acc, settings) = MakeService();
        SaveLicence(settings, customer, BundleFixtureFactory.TestKey);   // a WORKING licence

        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.Rejected, svc.LastKeyFilePickup!.Kind);
        Assert.Equal(Tier.Full, acc.Tier);                       // still Full
        Assert.Equal(customer, settings.GetSavedLicense().ClientName);
        Assert.Equal(KeyFileConsumeState.NotApplicable, svc.LastKeyFilePickup!.Consume);
        Assert.Contains("was not changed", svc.LastKeyFilePickup!.Message);

        // Renamed, not deleted: it may be the operator's only on-site copy of a credential whose
        // failure cause this code explicitly cannot diagnose.
        Assert.False(File.Exists(keyPath));
        Assert.True(File.Exists(keyPath + ".rejected"));
    }

    [Fact]
    public void ARejectedKeyFile_IsNotReReadOnTheNextPass()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string customer = "KEYFILE_NOLOOP_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "noloop.aesgcm", customer, BundleFixtureFactory.TestKey);
        var keyPath = WriteKeyFile(bundle, customer, OtherKeyB64);

        var (svc, _, settings) = MakeService();
        SaveLicence(settings, customer, BundleFixtureFactory.TestKey);

        svc.Initialize();
        Assert.Equal(KeyFilePickupKind.Rejected, svc.LastKeyFilePickup!.Kind);

        // The manual re-check runs the same code path. A rejected file that was still named
        // *.key.txt would be re-opened forever.
        var second = svc.RunKeyFilePickupNow();
        Assert.Equal(KeyFilePickupKind.NoKeyFile, second.Kind);

        // NOT re-opened, and NOT forgotten. It is still a plaintext key in the install folder, so
        // it stays on the list with its own state word.
        Assert.Contains(svc.LastKeyFileScan,
            s => s.KeyFilePath == keyPath + ".rejected"
                 && s.State == KeyFilePairState.RejectedResidue);
    }

    [Fact]
    public void ARejectedKeyFile_IsStillNamedAfterARestart()
    {
        if (!OperatingSystem.IsWindows()) return;

        // THE REGRESSION TEST for the sentence that reversed itself. Renaming rather than deleting
        // a rejected key file was justified on the grounds that "the card says so and asks for it
        // to be removed" — and that was true for exactly one pass. The glob the pickup scanned with
        // was "*.key.txt", which does not match "X.key.txt.rejected", so the NEXT start reported
        // "No key file is waiting beside the program." while a plaintext 32-byte AES key sat in the
        // install folder indefinitely, and the card's standing alert had nothing to render.
        const string customer = "KEYFILE_RESIDUE_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "residue.aesgcm", customer, BundleFixtureFactory.TestKey);
        var keyPath = WriteKeyFile(bundle, customer, OtherKeyB64);   // wrong key, right name

        var (first, _, firstSettings) = MakeService();
        SaveLicence(firstSettings, customer, BundleFixtureFactory.TestKey);
        first.Initialize();
        Assert.Equal(KeyFilePickupKind.Rejected, first.LastKeyFilePickup!.Kind);

        var residuePath = keyPath + ".rejected";
        Assert.True(File.Exists(residuePath));

        // A RESTART: a brand-new service, its own one-shot, over the same folder.
        var (restarted, _, _) = MakeService();
        restarted.Initialize();

        var outcome = restarted.LastKeyFilePickup!;
        Assert.DoesNotContain("No key file is waiting", outcome.Message);
        Assert.Contains("PLAINTEXT KEY", outcome.Message);
        Assert.Contains(restarted.LastKeyFileScan,
            s => s.KeyFilePath == residuePath && s.State == KeyFilePairState.RejectedResidue);

        // Named, never re-opened: the residue is still byte-for-byte what the rename left.
        Assert.True(File.Exists(residuePath));
    }

    [Fact]
    public void AnExpiredBundle_ActivatesNothingAndConsumesNothing()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Refuter correction 1 of the 15:35 addendum. The probe decrypts (expiry lives INSIDE the
        // authenticated manifest and is read after a successful decrypt), then TryActivate fails on
        // expiry — so the key file is NOT consumed.
        //
        // WHAT THIS CELL DOES NOT SHOW. It says nothing about the saved licence, and the fixture
        // below saves a key that was already wrong, so an assertion about it would have passed
        // vacuously. The licence-survives claim is measured next door, over a WORKING prior
        // licence, in AFailedActivationFromAPair_LeavesTheWorkingLicenceInPlace.
        const string customer = "KEYFILE_EXPIRED_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "expired.aesgcm", customer, BundleFixtureFactory.TestKey,
            licenseExpiryUtc: DateTime.UtcNow.AddDays(-3).ToString("o"));
        var keyPath = WriteKeyFile(bundle, customer, KeyB64);

        var (svc, acc, settings) = MakeService();
        SaveLicence(settings, customer, OtherKey);
        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.Rejected, svc.LastKeyFilePickup!.Kind);
        Assert.Equal(KeyFileConsumeState.NotApplicable, svc.LastKeyFilePickup!.Consume);
        Assert.True(File.Exists(keyPath), "an expired bundle must not consume the key file");
        Assert.NotEqual(Tier.Full, acc.Tier);
        Assert.Contains("expired", svc.LastKeyFilePickup!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFailedActivationFromAPair_LeavesTheWorkingLicenceInPlace()
    {
        if (!OperatingSystem.IsWindows()) return;

        // THE CELL THAT WAS OWED. Pickup used to call its own RestoreSnapshot on this arm and no
        // test could tell: deleting the call left the whole suite green, because TryActivate
        // restores its own snapshot. The duplicate is gone; the PROPERTY it was supposed to defend
        // is not, so it is measured here instead of asserted in a comment.
        //
        // THE RIG. The install is running on a good bundle under a working licence. A re-issue
        // arrives as a pair — same customer, newer CreatedUtc so pickup prefers it, a DIFFERENT key
        // so the outcome turns on that key alone, and an expiry three days in the past. The probe
        // opens it (expiry lives inside the authenticated manifest), TryActivate persists the new
        // pair, re-initializes, cannot reach Full, and fails.
        //
        // A client who was on Full before the drop must still be on Full after it, holding the same
        // saved bytes. That is the whole point of the non-destructive activate.
        const string customer = "KEYFILE_RESTORE_CLIENT";

        BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "a-working.aesgcm", customer, BundleFixtureFactory.TestKey,
            createdUtc: "2026-01-01T00:00:00Z");

        var reissue = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "b-reissue.aesgcm", customer, OtherKey,
            createdUtc: "2026-08-01T00:00:00Z",
            licenseExpiryUtc: DateTime.UtcNow.AddDays(-3).ToString("o"));
        var keyPath = WriteKeyFile(reissue, customer, OtherKeyB64);

        var (svc, acc, settings) = MakeService();
        SaveLicence(settings, customer, BundleFixtureFactory.TestKey);   // a WORKING licence
        var savedBefore = settings.GetSavedLicense();
        Assert.NotNull(savedBefore.EncryptedKey);

        svc.Initialize();

        // The activation failed and nothing was consumed.
        Assert.Equal(KeyFilePickupKind.Rejected, svc.LastKeyFilePickup!.Kind);
        Assert.Equal(KeyFileConsumeState.NotApplicable, svc.LastKeyFilePickup!.Consume);
        Assert.True(File.Exists(keyPath), "a failed activation must not consume the key file");

        // AND THE OPERATOR IS STILL WHERE THEY WERE. Byte-for-byte on the DPAPI blob, not just the
        // name: a restore that wrote back the right name with the wrong key would pass a name-only
        // assertion and leave the install on Free at the next start.
        var savedAfter = settings.GetSavedLicense();
        Assert.Equal(customer, savedAfter.ClientName);
        Assert.Equal(savedBefore.EncryptedKey, savedAfter.EncryptedKey);
        Assert.Equal(Tier.Full, acc.Tier);
        Assert.Equal(customer, acc.ClientName);
    }

    // ── Precedence and lifecycle ─────────────────────────────────────────────

    [Fact]
    public void ADroppedPair_BeatsAStaleSavedKey()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string customer = "KEYFILE_REISSUE_CLIENT";

        // The install is running on an older bundle with an older key.
        BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "a-old.aesgcm", customer, OtherKey, createdUtc: "2026-01-01T00:00:00Z");

        // The re-issue arrives as a pair. Same customer, so the ratchet lets it through silently.
        var reissue = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "b-new.aesgcm", customer, BundleFixtureFactory.TestKey,
            createdUtc: "2026-08-01T00:00:00Z");
        WriteKeyFile(reissue, customer, KeyB64);

        var (svc, acc, settings) = MakeService();
        SaveLicence(settings, customer, OtherKey);

        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.Activated, svc.LastKeyFilePickup!.Kind);
        Assert.Equal(Tier.Full, acc.Tier);
        Assert.Equal("b-new.aesgcm", Path.GetFileName(svc.LastUnlockedBundlePath!));
    }

    [Fact]
    public void TwoValidPairs_TheOrdinalFirstWinsAndTheOtherIsLeftAlone()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string customer = "KEYFILE_TWOPAIR_CLIENT";
        var first = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "a-first.aesgcm", customer, BundleFixtureFactory.TestKey);
        var second = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "z-second.aesgcm", customer, BundleFixtureFactory.TestKey);

        var firstKey = WriteKeyFile(first, customer, KeyB64);
        var secondKey = WriteKeyFile(second, customer, KeyB64);
        var secondBytes = File.ReadAllBytes(secondKey);

        var (svc, _, settings) = MakeService();
        SaveLicence(settings, customer, OtherKey);
        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.Activated, svc.LastKeyFilePickup!.Kind);
        Assert.Equal("a-first.aesgcm", svc.LastKeyFilePickup!.BundleFileName);
        Assert.Equal(2, svc.LastKeyFilePickup!.PairsSeen);
        Assert.False(File.Exists(firstKey));

        // Consuming a credential that was not used destroys the operator's material for nothing.
        Assert.True(File.Exists(secondKey));
        Assert.Equal(secondBytes, File.ReadAllBytes(secondKey));
        Assert.Contains(svc.LastKeyFileScan,
            s => s.KeyFilePath == secondKey && s.State == KeyFilePairState.NotTried);
    }

    [Fact]
    public void PickupIsOneShotPerProcess()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string customer = "KEYFILE_ONESHOT_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "oneshot.aesgcm", customer, BundleFixtureFactory.TestKey);

        var (svc, _, settings) = MakeService();
        SaveLicence(settings, customer, OtherKey);

        svc.Initialize();
        Assert.Equal(KeyFilePickupKind.NoKeyFile, svc.LastKeyFilePickup!.Kind);

        // The pair appears AFTER the first Initialize. A second Initialize must not take it —
        // TryActivate and Deactivate both re-run Initialize, and a re-pickup there would undo a
        // Deactivate on the spot.
        var keyPath = WriteKeyFile(bundle, customer, KeyB64);
        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.NoKeyFile, svc.LastKeyFilePickup!.Kind);
        Assert.True(File.Exists(keyPath));

        // …and the admin button is what re-arms it, on the same code path.
        var outcome = svc.RunKeyFilePickupNow();
        Assert.Equal(KeyFilePickupKind.Activated, outcome.Kind);
        Assert.False(File.Exists(keyPath));
    }

    [Fact]
    public void PickupDoesNotRecurseThroughTryActivate()
    {
        if (!OperatingSystem.IsWindows()) return;

        // pickup -> TryActivate -> Initialize -> pickup would run until the stack ran out. The
        // one-shot flag is set BEFORE the work, so a throw inside pickup cannot re-arm it. The
        // assertion is that this returns at all, inside a budget.
        const string customer = "KEYFILE_RECURSE_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "recurse.aesgcm", customer, BundleFixtureFactory.TestKey);
        WriteKeyFile(bundle, customer, KeyB64);

        var (svc, _, settings) = MakeService();
        SaveLicence(settings, customer, OtherKey);

        var done = Task.Run(() => svc.Initialize());
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "Initialize did not return — pickup recursed.");
        Assert.Equal(KeyFilePickupKind.Activated, svc.LastKeyFilePickup!.Kind);
    }

    [Fact]
    public void APickupFailureDoesNotStopTheLicencePath()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A folder that cannot be enumerated is the cheapest reachable pickup failure. Initialize
        // must still run its Full and Free branches: a pickup failure may never be the reason the
        // free bundle does not load.
        var missing = Path.Combine(_root, "does-not-exist");
        var (svc, _, _) = MakeService();
        svc.InstallDirOverrideForTests = missing;

        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.Failed, svc.LastKeyFilePickup!.Kind);
        // The licence path DID run: it recorded its own reason after pickup had failed.
        Assert.NotNull(svc.LastFullFailure);
        Assert.Equal(FullUnlockFailureReason.NoSavedLicense, svc.LastFullFailure!.Reason);
    }

    [Fact]
    public void AFailedEnumeration_EmptiesTheScanRatherThanLeavingTheLastPassOnScreen()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The card reads LicenseSvc.LastKeyFileScan for its standing per-file list. If a pass that
        // could not read the folder left the PREVIOUS pass's list standing, the card would render
        // state words decided against a folder this pass never saw, beside a panel saying the check
        // failed. Every terminal path must own its own scan list, including the ones that give up.
        const string customer = "KEYFILE_STALESCAN_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "stale.aesgcm", customer, BundleFixtureFactory.TestKey);
        var orphan = Path.Combine(_installDir, "unpaired.key.txt");
        File.WriteAllText(orphan, $"Customer: {customer}\r\n{KeyB64}\r\n");

        var (svc, _, settings) = MakeService();
        SaveLicence(settings, customer, BundleFixtureFactory.TestKey);

        svc.Initialize();
        Assert.NotEmpty(svc.LastKeyFileScan);          // a real list, from a folder that was read

        // Now the folder goes away under the running service.
        File.Delete(orphan);
        File.Delete(bundle);
        Directory.Delete(_installDir);

        var second = svc.RunKeyFilePickupNow();

        Assert.Equal(KeyFilePickupKind.Failed, second.Kind);
        Assert.Empty(svc.LastKeyFileScan);
    }

    // ── The operator's kill switch (verifier round 2, D2) ────────────────────

    /// <summary>Bundle + matching key file + a saved licence that will activate. Returns the key path.</summary>
    private string PairThatWouldActivate(string customer, string bundleName,
                                         UserSettingsService settings)
    {
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, bundleName, customer, BundleFixtureFactory.TestKey);
        var keyPath = WriteKeyFile(bundle, customer, KeyB64);
        SaveLicence(settings, customer, OtherKey);   // saved name matches => the identity gate opens
        return keyPath;
    }

    [Fact]
    public void PickupDisabledInConfig_NeverOpensTheKeyFileAtBoot()
    {
        if (!OperatingSystem.IsWindows()) return;

        // THE SWITCH THE DESIGN ASKED FOR AND THE FIRST IMPLEMENTATION DROPPED. Without it the only
        // opt-out was KeyFilePickupMode.Disabled, which nothing but --audit passes, so an operator
        // who did not want a service that reads a dropped credential had no supported way to say so.
        //
        // The pair here is one that WOULD activate: same customer, right key, real bundle. The cell
        // is worth nothing if the pair could not have been consumed anyway.
        const string customer = "KEYFILE_KILLSWITCH_CLIENT";
        var (svc, acc, settings) = MakeService(ConfigWithPickup(enabled: false));
        var keyPath = PairThatWouldActivate(customer, "killswitch.aesgcm", settings);
        var before = File.ReadAllBytes(keyPath);

        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.NotRun, svc.LastKeyFilePickup!.Kind);
        Assert.Contains("switched off for this install", svc.LastKeyFilePickup!.Message);
        Assert.Contains("Licensing:KeyFilePickup:Enabled", svc.LastKeyFilePickup!.Message);

        // NOTHING WAS TOUCHED: not opened, not renamed, not removed, and the bytes are the same.
        Assert.True(File.Exists(keyPath));
        Assert.Equal(before, File.ReadAllBytes(keyPath));
        Assert.Empty(Directory.GetFiles(_installDir, "*.rejected"));
        Assert.NotEqual(Tier.Full, acc.Tier);   // the licence did NOT come up from the key file

        // DO NOT ACT BUT STILL LIST (Adrian, DECISIONS 2026-09-03 11:20). This asserted
        // Assert.Empty(svc.LastKeyFileScan) — an empty scan, so the card rendered nothing at all
        // about key files and a plaintext 32-byte AES key could sit beside the executable
        // indefinitely with every surface silent. Switching pickup off is a decision not to CONSUME
        // a dropped credential, never a decision to stop being told one is there.
        var listed = Assert.Single(svc.LastKeyFileScan);
        Assert.Equal(keyPath, listed.KeyFilePath);
        Assert.Equal(KeyFilePairState.PickupDisabled, listed.State);
        Assert.Null(listed.CustomerName);       // naming it would mean reading it
        Assert.True(svc.LastKeyFilePickup!.PickupDisabledByConfig);
        Assert.Equal(1, svc.LastKeyFilePickup!.PairsSeen);
        Assert.Contains("1 key file(s) with a matching bundle are still in the folder",
            svc.LastKeyFilePickup!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PickupDisabledInConfig_ListsThePairsTheOrphansAndTheResidue_AndConsumesNothing()
    {
        if (!OperatingSystem.IsWindows()) return;

        // THE RULING'S CELL (Adrian, DECISIONS 2026-09-03 11:20). All three categories the enabled
        // pass lists, listed by the disabled pass, with every byte on disk unchanged afterwards.
        //
        // The pair is one that WOULD activate. The cell is worth nothing if it could not have been
        // consumed anyway.
        const string customer = "KEYFILE_KILLSWITCH_LISTS";
        var (svc, acc, settings) = MakeService(ConfigWithPickup(enabled: false));
        var pairedKey = PairThatWouldActivate(customer, "aaa-listed.aesgcm", settings);

        // An orphan: a key file with no bundle of the same base name.
        var orphanKey = Path.Combine(_installDir, "mmm-orphan.key.txt");
        File.WriteAllBytes(orphanKey,
            Encoding.UTF8.GetBytes($"Customer: {customer}\r\n{KeyB64}\r\n"));

        // Residue: a key file rejected on an earlier pass and renamed, still holding a plaintext key.
        var residueKey = Path.Combine(_installDir, "zzz-old.key.txt.rejected");
        File.WriteAllBytes(residueKey,
            Encoding.UTF8.GetBytes($"Customer: {customer}\r\n{KeyB64}\r\n"));

        var sha = new Func<string, string>(p =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))));
        var pairedBefore = sha(pairedKey);
        var orphanBefore = sha(orphanKey);
        var residueBefore = sha(residueKey);

        svc.Initialize();

        // LISTED — all three, each with the state the ruling asks for.
        Assert.Equal(3, svc.LastKeyFileScan.Count);
        Assert.Equal(KeyFilePairState.PickupDisabled,
            Assert.Single(svc.LastKeyFileScan, s => s.KeyFilePath == pairedKey).State);
        Assert.Equal(KeyFilePairState.NoBundle,
            Assert.Single(svc.LastKeyFileScan, s => s.KeyFilePath == orphanKey).State);
        Assert.Equal(KeyFilePairState.RejectedResidue,
            Assert.Single(svc.LastKeyFileScan, s => s.KeyFilePath == residueKey).State);

        // NOT ACTED ON — byte-identical, nothing renamed, nothing removed, still Free.
        Assert.Equal(pairedBefore, sha(pairedKey));
        Assert.Equal(orphanBefore, sha(orphanKey));
        Assert.Equal(residueBefore, sha(residueKey));
        Assert.Equal(3, Directory.GetFiles(_installDir, "*key.txt*").Length);
        Assert.NotEqual(Tier.Full, acc.Tier);

        // The message counts what it saw, so the standing panel and the toast agree with the list.
        var message = svc.LastKeyFilePickup!.Message;
        Assert.Contains("switched off for this install", message, StringComparison.Ordinal);
        Assert.Contains("1 key file(s) with a matching bundle", message, StringComparison.Ordinal);
        Assert.Contains("1 key file(s) are present with no matching .aesgcm bundle",
            message, StringComparison.Ordinal);
        Assert.Contains("1 key file(s) rejected on an earlier start", message, StringComparison.Ordinal);
    }

    [Fact]
    public void PickupDisabledInConfig_LogsOneSkippedLineAndOneScanSummary()
    {
        if (!OperatingSystem.IsWindows()) return;

        // THE LOG SHAPE THE RULING FIXES (Adrian, DECISIONS 2026-09-03 11:20): "the log keeps its
        // single Skipped line plus the scan summary". Two lines, and neither of them describes an
        // act — no "Activated", no "Rejected", no "Renamed", no "HELD", and no ACL warning (that
        // warning exists to say a pair this install is ABOUT TO OPEN could have been planted, and
        // nothing here opens anything).
        const string customer = "KEYFILE_KILLSWITCH_LOG";
        var logger = new CapturingLogger();
        var (svc, _, settings) = MakeService(ConfigWithPickup(enabled: false), logger);
        PairThatWouldActivate(customer, "logshape.aesgcm", settings);

        svc.Initialize();

        var pickupLines = logger.Lines.Where(l => l.Contains("[KeyFilePickup]")).ToList();
        Assert.Equal(2, pickupLines.Count);
        Assert.Contains(pickupLines, l => l.Contains("Skipped:")
            && l.Contains("Licensing:KeyFilePickup:Enabled"));
        Assert.Contains(pickupLines, l => l.Contains("Listed without acting")
            && l.Contains("1 key file(s)")
            && l.Contains("Nothing was opened, renamed or removed"));

        foreach (var forbidden in new[] { "Activated", "Rejected", "Renamed", "HELD", "grants write" })
            Assert.DoesNotContain(pickupLines, l => l.Contains(forbidden, StringComparison.Ordinal));
    }

    [Fact]
    public void PickupDisabledInConfig_AlsoRefusesTheManualButton()
    {
        if (!OperatingSystem.IsWindows()) return;

        // An install configured never to read a dropped credential must not have that undone by a
        // button, however well gated the button is.
        const string customer = "KEYFILE_KILLSWITCH_BUTTON";
        var (svc, acc, settings) = MakeService(ConfigWithPickup(enabled: false));
        var keyPath = PairThatWouldActivate(customer, "killbutton.aesgcm", settings);
        var before = File.ReadAllBytes(keyPath);

        var outcome = svc.RunKeyFilePickupNow();

        Assert.Equal(KeyFilePickupKind.NotRun, outcome.Kind);
        Assert.Contains("switched off for this install", outcome.Message);
        Assert.Equal(before, File.ReadAllBytes(keyPath));
        Assert.NotEqual(Tier.Full, acc.Tier);

        // …and the button still LISTS. An administrator who clicks "Check for a key file now" on a
        // disabled install is asking what is in the folder; refusing to act is not a reason to
        // refuse to answer.
        Assert.True(outcome.PickupDisabledByConfig);
        Assert.Equal(KeyFilePairState.PickupDisabled,
            Assert.Single(svc.LastKeyFileScan, s => s.KeyFilePath == keyPath).State);
    }

    [Fact]
    public void PickupDisabledInConfig_AlsoRefusesAnAdminAcceptingAHeldPair()
    {
        if (!OperatingSystem.IsWindows()) return;

        // AcceptKeyFilePair is the one path that gets PAST the identity ratchet, so it is the one
        // that most needs the switch to bind it. A virgin install (no saved licence) is the posture
        // in which a pair is held and Accept is the only way forward.
        const string customer = "KEYFILE_KILLSWITCH_ACCEPT";
        var (svc, acc, settings) = MakeService(ConfigWithPickup(enabled: false));
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "killaccept.aesgcm", customer, BundleFixtureFactory.TestKey);
        var keyPath = WriteKeyFile(bundle, customer, KeyB64);
        settings.ClearLicense();
        var before = File.ReadAllBytes(keyPath);

        var outcome = svc.AcceptKeyFilePair(keyPath);

        Assert.Equal(KeyFilePickupKind.NotRun, outcome.Kind);
        Assert.Contains("switched off for this install", outcome.Message);
        Assert.Equal(before, File.ReadAllBytes(keyPath));
        Assert.NotEqual(Tier.Full, acc.Tier);

        // The Accept path lists too, and the listing is the ONLY thing it does: the pair is named,
        // and it is named as un-acted-on rather than as accepted.
        Assert.True(outcome.PickupDisabledByConfig);
        Assert.Equal(KeyFilePairState.PickupDisabled,
            Assert.Single(svc.LastKeyFileScan, s => s.KeyFilePath == keyPath).State);
        Assert.Empty(Directory.GetFiles(_installDir, "*.rejected"));
    }

    [Fact]
    public void PickupIsEnabledWhenTheKeyIsAbsent_AndWhenItIsExplicitlyTrue()
    {
        if (!OperatingSystem.IsWindows()) return;

        // DEFAULT TRUE IS THE SHIPPED BEHAVIOUR, and it is asserted rather than assumed: an
        // appsettings.json with no such key -- which is every install today -- must activate
        // exactly as it did before the switch existed. Both arms drive a real consume.
        const string absentCustomer = "KEYFILE_DEFAULT_ABSENT";
        var emptyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var (svc, acc, settings) = MakeService(emptyConfig);
        var keyPath = PairThatWouldActivate(absentCustomer, "defaultabsent.aesgcm", settings);

        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.Activated, svc.LastKeyFilePickup!.Kind);
        Assert.Equal(Tier.Full, acc.Tier);
        Assert.False(File.Exists(keyPath));

        // And the explicit true, on its own service and its own pair.
        const string trueCustomer = "KEYFILE_DEFAULT_TRUE";
        var (svc2, acc2, settings2) = MakeService(ConfigWithPickup(enabled: true));
        var keyPath2 = PairThatWouldActivate(trueCustomer, "defaulttrue.aesgcm", settings2);

        svc2.Initialize();

        Assert.Equal(KeyFilePickupKind.Activated, svc2.LastKeyFilePickup!.Kind);
        Assert.Equal(Tier.Full, acc2.Tier);
        Assert.False(File.Exists(keyPath2));
    }

    // ── The remaining round-2 defects ────────────────────────────────────────

    [Fact]
    public void AnActivationWhoseDeleteFailed_IsNotListedAsStillHoldingTheKey()
    {
        if (!OperatingSystem.IsWindows()) return;

        // D1 AT THE SERVICE. Every non-Consumed outcome used to be listed as Waiting, and the card's
        // word for Waiting is "it still holds your licence key in plain text". After a NotDeleted
        // consume that is FALSE: the bytes are zeros and only the delete failed. The state is now
        // its own member, so the card can say what actually happened.
        const string customer = "KEYFILE_NOTDELETED_STATE";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "notdeleted.aesgcm", customer, BundleFixtureFactory.TestKey);
        var keyPath = WriteKeyFile(bundle, customer, KeyB64);

        var (svc, _, settings) = MakeService();
        SaveLicence(settings, customer, OtherKey);

        FileStream? blocker = null;
        svc.AfterKeyFileOverwriteForTests = (path, _) =>
            blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        try { svc.Initialize(); }
        finally { blocker?.Dispose(); }

        Assert.Equal(KeyFileConsumeState.NotDeleted, svc.LastKeyFilePickup!.Consume);

        var entry = Assert.Single(svc.LastKeyFileScan,
            s => string.Equals(s.KeyFilePath, keyPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(KeyFilePairState.OverwrittenNotDeleted, entry.State);
        Assert.NotEqual(KeyFilePairState.Waiting, entry.State);

        // The remnant really is empty -- otherwise the new state word would be the lie instead.
        Assert.True(File.Exists(keyPath));
        Assert.Equal(0, new FileInfo(keyPath).Length);
    }

    [Fact]
    public void AMalformedKeyFile_IsNamedInTheLogWithTheProblemAndNotTheContents()
    {
        if (!OperatingSystem.IsWindows()) return;

        // D4. Oversize, held, rejected, unreadable and consumed all logged; the parse failure was
        // the one terminal branch that printed nothing, so a malformed drop showed "1 pair(s)." and
        // then silence. The contents are the other half of the cell: a key file's lines are a
        // credential, and a log that quoted the file to explain it would be worse than silence.
        const string customer = "KEYFILE_MALFORMED_LOG";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "malformedlog.aesgcm", customer, BundleFixtureFactory.TestKey);
        var keyPath = LicenseServiceKeyFilePath(bundle);
        const string secretish = "ZZTOPSECRETLINE";
        File.WriteAllText(keyPath, $"{secretish}\r\nnot a customer line and not a phrase\r\n");

        var log = new CapturingLogger();
        var (svc, _, settings) = MakeService(logger: log);
        SaveLicence(settings, customer, BundleFixtureFactory.TestKey);

        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.Malformed, svc.LastKeyFilePickup!.Kind);

        var line = Assert.Single(log.Lines,
            l => l.Contains("could not be read as a key file", StringComparison.Ordinal));
        Assert.Contains("malformedlog.key.txt", line, StringComparison.Ordinal);
        Assert.Contains("does not say which customer", line, StringComparison.Ordinal);

        // NOT ONE LINE OF THE FILE, anywhere in the log.
        Assert.DoesNotContain(log.Lines, l => l.Contains(secretish, StringComparison.Ordinal));
    }

    [Fact]
    public void AKeyLineThatIsNotAUsableKey_IsNamedWithoutQuotingAWordOfIt()
    {
        if (!OperatingSystem.IsWindows()) return;

        // VERIFIER ROUND 3, V3. THE OTHER MALFORMED ARM, and the one the D4 cell above cannot
        // reach: its fixture has no 24-word line, so it drives the PARSER's failure and never the
        // DECODE's. This one parses fine — a Customer line and a line of exactly 24 words, which is
        // all KeyFileParser.LooksLikeKey asks — and then fails inside DecodeKeyInput.
        //
        // The branch used to log and RENDER ex.Message, under a comment asserting DecodeKeyInput
        // only fails on shape ("not 24 words", "not Base64"). It does not: from here the shape arm
        // is unreachable, and Bip39.Decode throws ArgumentException("Word not in BIP39 wordlist:
        // '<w>'.") where <w> is a token read out of the operator's key file. One word of a
        // credential is still a word of a credential, and a log outlives the screen.
        //
        // THE CELL HOLDS ON A RUNNER WITH NO WORDLIST TOO. The BIP39 resource is gitignored and
        // absent from CI, so EnsureLoaded throws InvalidOperationException there and the failing
        // word is never computed. The assertion is not "which exception": it is that no token from
        // the file reaches the log or the card, and that the file is still named — true in both.
        const string customer = "KEYFILE_DECODE_LOG";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "decodelog.aesgcm", customer, BundleFixtureFactory.TestKey);

        // 24 tokens, none of them BIP39 words, each carrying a stem nothing else in the log could
        // produce. Not a real phrase and not derived from one.
        var words = string.Join(' ', Enumerable.Range(1, 24).Select(i => $"zzleak{i:00}"));
        var keyPath = LicenseServiceKeyFilePath(bundle);
        File.WriteAllText(keyPath, $"Customer: {customer}\r\n{words}\r\n", Encoding.UTF8);

        var log = new CapturingLogger();
        var (svc, _, settings) = MakeService(logger: log);
        SaveLicence(settings, customer, BundleFixtureFactory.TestKey);

        svc.Initialize();

        // The precondition: the parser accepted the file, so this really is the decode arm.
        Assert.True(KeyFileParser.TryParse(
            $"Customer: {customer}\r\n{words}\r\n", out _, out var picked, out _));
        Assert.Equal(words, picked);

        var outcome = svc.LastKeyFilePickup!;
        Assert.Equal(KeyFilePickupKind.Malformed, outcome.Kind);

        var line = Assert.Single(log.Lines,
            l => l.Contains("is not a usable licence key", StringComparison.Ordinal));
        Assert.Contains("decodelog.key.txt", line, StringComparison.Ordinal);

        // NO TOKEN OF THE KEY LINE, in the log or on the card.
        Assert.DoesNotContain(log.Lines, l => l.Contains("zzleak", StringComparison.Ordinal));
        Assert.DoesNotContain("zzleak", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("decodelog.key.txt", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleRejectedFile_IsZeroedBeforeItIsReplaced()
    {
        if (!OperatingSystem.IsWindows()) return;

        // D6. The whole justification for renaming rather than deleting a rejected key file is that
        // it may be the operator's only on-site copy of a credential. This path then destroyed
        // exactly such a file with a bare File.Delete -- the one treatment ConsumeKeyFile is careful
        // never to give a plaintext key. It is zeroed first now.
        //
        // THE WITNESS IS THE SEAM, not a handle. A handle held open across the rename would make
        // the FileShare.None overwrite fail, so the test would prevent the behaviour it came to
        // measure; and reading the path afterwards proves nothing, because the file is unlinked.
        //
        // THE BYTE COUNT IS THE LOAD-BEARING HALF, for the same reason it is in
        // TheOverwriteHappensBEFORETheDelete: the file is truncated, so reading it back at the seam
        // finds an empty file whether or not a single zero was written. Deleting the
        // OverwriteWithZeros call turns the count to -1 and fails this cell.
        const string customer = "KEYFILE_STALE_REJECTED";
        var wrongKey = OtherKeyB64;
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "stalerejected.aesgcm", customer, BundleFixtureFactory.TestKey);
        var keyPath = LicenseServiceKeyFilePath(bundle);

        // A stale .rejected from an earlier pass, with a readable key in it.
        var stale = keyPath + ".rejected";
        File.WriteAllText(stale, $"Customer: {customer}\r\n{wrongKey}\r\n");
        var staleLength = new FileInfo(stale).Length;
        Assert.True(staleLength > 0, "the stale fixture is empty, so the cell would be vacuous");

        WriteKeyFile(bundle, customer, wrongKey);   // right name, wrong key => Rejected => rename

        var (svc, _, settings) = MakeService();
        SaveLicence(settings, customer, BundleFixtureFactory.TestKey);

        long zeroed = -1;
        byte[]? atTheSeam = null;
        svc.AfterRejectedOverwriteForTests = (path, written) =>
        {
            zeroed = written;
            atTheSeam = File.ReadAllBytes(path);
        };

        svc.Initialize();

        Assert.Equal(KeyFilePickupKind.Rejected, svc.LastKeyFilePickup!.Kind);

        // The stale credential was zeroed over its WHOLE length before it was unlinked.
        Assert.Equal(staleLength, zeroed);
        Assert.NotNull(atTheSeam);
        Assert.All(atTheSeam!, b => Assert.Equal(0, b));

        // And the newly rejected key file took its place, still named for the operator.
        Assert.True(File.Exists(stale));
        Assert.False(File.Exists(keyPath));
    }

    [Fact]
    public void ASecondPassEnteredWhileOneIsInFlight_IsRefusedByName()
    {
        if (!OperatingSystem.IsWindows()) return;

        // D7. LicenseService is a DI singleton and both public entry points are reachable from any
        // admin circuit through Task.Run, so two administrators clicking together ran two passes
        // over one folder. ConsumeKeyFile opens FileShare.None, so the loser's overwrite threw and
        // it reported Intact -- "THE KEY FILE IS STILL ON DISK WITH THE KEY IN IT" -- about a file
        // the winner had already zeroed and deleted. A false alarm, printed to an operator.
        //
        // The second entry is driven from INSIDE the first, through the overwrite seam, so the
        // interleaving is exact instead of hoped for. It is the same gate either way: the flag is
        // held for the whole pass.
        const string customer = "KEYFILE_INFLIGHT_CLIENT";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "inflight.aesgcm", customer, BundleFixtureFactory.TestKey);
        var keyPath = WriteKeyFile(bundle, customer, KeyB64);

        var (svc, acc, settings) = MakeService();
        SaveLicence(settings, customer, OtherKey);

        KeyFilePickupOutcome? reentrant = null;
        svc.AfterKeyFileOverwriteForTests = (_, _) => reentrant = svc.RunKeyFilePickupNow();

        svc.Initialize();

        // The first pass finished its own work, untouched by the second.
        Assert.Equal(KeyFilePickupKind.Activated, svc.LastKeyFilePickup!.Kind);
        Assert.Equal(KeyFileConsumeState.Consumed, svc.LastKeyFilePickup!.Consume);
        Assert.Equal(Tier.Full, acc.Tier);
        Assert.False(File.Exists(keyPath));

        // The second was refused BY NAME, and it did not overwrite the first pass's published
        // outcome -- which is what would put "already running" on the card instead of the result.
        Assert.NotNull(reentrant);
        Assert.Equal(KeyFilePickupKind.NotRun, reentrant!.Kind);
        Assert.Contains("already running", reentrant!.Message, StringComparison.Ordinal);
        Assert.NotSame(reentrant, svc.LastKeyFilePickup);
        Assert.Equal(KeyFilePickupKind.Activated, svc.LastKeyFilePickup!.Kind);
    }

    [Fact]
    public void TheInFlightGateIsReleasedSoALaterPassStillRuns()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The other half of D7, and the failure mode a gate introduces: a flag left set would make
        // every later click answer "a check is already running" about a pass that ended long ago.
        const string customer = "KEYFILE_INFLIGHT_RELEASE";
        var bundle = BundleFixtureFactory.WriteFullBundleNamed(
            _installDir, "release.aesgcm", customer, BundleFixtureFactory.TestKey);
        File.WriteAllText(LicenseServiceKeyFilePath(bundle), "neither a customer line nor a phrase\r\n");

        var (svc, _, settings) = MakeService();
        SaveLicence(settings, customer, BundleFixtureFactory.TestKey);

        svc.Initialize();
        Assert.Equal(KeyFilePickupKind.Malformed, svc.LastKeyFilePickup!.Kind);

        var second = svc.RunKeyFilePickupNow();
        Assert.Equal(KeyFilePickupKind.Malformed, second.Kind);
        Assert.DoesNotContain("already running", second.Message, StringComparison.Ordinal);
    }
}
