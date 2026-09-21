/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.Text;
using System.Text.RegularExpressions;

namespace SQLTriage.Data.Services.Licensing;

/// <summary>
/// Whether <see cref="LicenseService.Initialize"/> is allowed to pick up a key file dropped beside
/// the executable.
///
/// <para><b>Why a host can opt out.</b> <c>--audit</c> is a short-lived process an operator may run
/// interactively under their own desktop account on the same box as the service. If it consumed the
/// key file, the phrase would be wrapped into the DESKTOP profile and deleted from disk, and the
/// LocalSystem service would still be on Free — the exact account split this feature exists to
/// close, re-created by a race between two processes. Whichever process picks up first wins the key
/// file, and it activates for that account only.</para>
/// </summary>
public enum KeyFilePickupMode
{
    /// <summary>Pickup runs once per process, at the first <c>Initialize</c>.</summary>
    Enabled,

    /// <summary>Pickup never runs in this process. No key file is opened, renamed or deleted.</summary>
    Disabled,
}

/// <summary>
/// What the last key-file pickup pass did. One value per terminal branch, so a surface can say
/// which one happened instead of listing every possibility as a guess.
/// </summary>
public enum KeyFilePickupKind
{
    /// <summary>Pickup has not run in this process (disabled, non-Windows, or not reached yet).</summary>
    NotRun,

    /// <summary>Pickup ran and found no <c>*.key.txt</c> paired with an installed bundle.</summary>
    NoKeyFile,

    /// <summary>A pair opened and the licence was activated from it.</summary>
    Activated,

    /// <summary>A pair was found, the key did not open the bundle. The saved licence is unchanged.</summary>
    Rejected,

    /// <summary>
    /// The key file was read and could not be understood: no <c>Customer:</c> line, no 24-word
    /// phrase and no Base64 key, or the file was too large to be a key file (in which case it was
    /// not read at all).
    /// </summary>
    Malformed,

    /// <summary>
    /// RATCHET (Adrian, 2026-09-02 15:35). The key file names a customer that is not the one this
    /// install has saved, or nothing is saved at all. The pair is HELD: nothing was decrypted,
    /// nothing was consumed, and an administrator must accept it explicitly on the card.
    /// </summary>
    RefusedByIdentity,

    /// <summary>
    /// Something threw inside pickup. The licence path ran anyway — a pickup failure must never
    /// stop the free bundle from loading.
    /// </summary>
    Failed,
}

/// <summary>What happened to the key file after a successful activation.</summary>
public enum KeyFileConsumeState
{
    /// <summary>No activation happened, so nothing was consumed.</summary>
    NotApplicable,

    /// <summary>Overwritten with zeros and deleted.</summary>
    Consumed,

    /// <summary>Overwritten with zeros; the delete failed. A zero-length file remains.</summary>
    NotDeleted,

    /// <summary>The overwrite failed. THE FILE IS STILL ON DISK WITH THE KEY IN IT.</summary>
    Intact,
}

/// <summary>
/// What the pickup pass made of one <c>*.key.txt</c> still sitting in the install folder. The card
/// renders one line per entry, so an un-restarted or held drop is visible rather than silent.
/// </summary>
public enum KeyFilePairState
{
    /// <summary>Paired with a bundle and not yet acted on — pickup has not run since it appeared.</summary>
    Waiting,

    /// <summary>No <c>*.aesgcm</c> of the same base name. Rule P2: the file was NOT opened.</summary>
    NoBundle,

    /// <summary>Held by the identity ratchet. Nothing decrypted, nothing consumed.</summary>
    HeldForIdentity,

    /// <summary>Read and not understood, or refused unread for size.</summary>
    Malformed,

    /// <summary>The key did not open the paired bundle. Renamed to <c>.rejected</c>.</summary>
    Rejected,

    /// <summary>The bytes could not be read off disk at all.</summary>
    Unreadable,

    /// <summary>An earlier pair won this pass, so this one was never touched (Rule P5).</summary>
    NotTried,

    /// <summary>
    /// RESIDUE. A key file rejected on an EARLIER pass, renamed to
    /// <c>&lt;name&gt;.key.txt.rejected</c> and still sitting in the install folder with a
    /// plaintext key in it.
    ///
    /// <para>Appended at the end, after <see cref="NotTried"/>, on purpose: nothing here reorders.
    /// This member exists because the rename made the file invisible to the <c>*.key.txt</c> glob,
    /// so from the next start onward the card said no key file was waiting while the credential
    /// was still on disk. The rename is still the right call — see
    /// <c>LicenseService.RenameRejected</c> — but only if the residue keeps being named.</para>
    /// </summary>
    RejectedResidue,

    /// <summary>
    /// The activation succeeded and <see cref="LicenseService"/> overwrote this file's bytes with
    /// zeros, but the delete failed. What is left is a ZERO-LENGTH FILE — a name, not a credential.
    ///
    /// <para><b>Why this is its own member and not <see cref="Waiting"/>.</b> Every non-
    /// <see cref="KeyFileConsumeState.Consumed"/> outcome used to be listed as <c>Waiting</c>, and
    /// the card's default word for <c>Waiting</c> is "it still holds your licence key in plain
    /// text". For this state that sentence is FALSE, and it rendered directly under the panel that
    /// correctly said the contents had been overwritten — two facts welded, one of them asserting a
    /// state nothing measured. <see cref="KeyFileConsumeState.Intact"/> keeps <c>Waiting</c>,
    /// because for Intact the sentence is exactly true.</para>
    ///
    /// <para>Appended at the END, after <see cref="RejectedResidue"/>, on purpose: nothing here
    /// reorders.</para>
    /// </summary>
    OverwrittenNotDeleted,

    /// <summary>
    /// Paired with a bundle, and NOT OPENED because <c>Licensing:KeyFilePickup:Enabled</c> is
    /// false for this install.
    ///
    /// <para><b>DO NOT ACT BUT STILL LIST</b> (Adrian, DECISIONS 2026-09-03 11:20). The kill switch
    /// used to return before the folder was looked at, so a disabled install rendered nothing at
    /// all about key files — and the standing panel is the ONLY surface that says a plaintext
    /// 32-byte AES key is sitting beside the executable. Switching pickup off is a decision not to
    /// CONSUME a dropped credential; it was never a decision to stop being told one is there.
    /// The disabled pass therefore runs the two globs and publishes what it saw, and opens,
    /// renames and removes nothing.</para>
    ///
    /// <para>The customer name is null here on purpose: naming it would mean reading the file, and
    /// this arm does not read files.</para>
    ///
    /// <para>Appended at the END, after <see cref="OverwrittenNotDeleted"/>, on purpose: nothing
    /// here reorders.</para>
    /// </summary>
    PickupDisabled,
}

/// <summary>One key file in the install folder and what the last pickup pass made of it.</summary>
public sealed record KeyFilePairStatus(
    string KeyFilePath,
    string? BundlePath,
    KeyFilePairState State,
    string? CustomerName);

/// <summary>
/// The standing per-file list AND the moment it was measured, published as ONE value.
///
/// <para><b>Why they travel together.</b> The card dates its standing panel ("as of the last check
/// at ..."), and it snapshots the list into a field so nothing on the render path touches the
/// filesystem. Reading the list from one property and the time from another is two reads of a
/// service a second circuit can be re-scanning between them — which dates one circuit's list with
/// another circuit's clock, and the whole point of the date is that the panel's age is honest.
/// One field, one <c>Volatile.Read</c>, one fact.</para>
/// </summary>
/// <param name="Pairs">Every <c>*.key.txt</c> that pass saw, in the order it listed them.</param>
/// <param name="WhenUtc">When that pass published the list; null before any pass has run.</param>
public sealed record KeyFileScanSnapshot(IReadOnlyList<KeyFilePairStatus> Pairs, DateTime? WhenUtc)
{
    /// <summary>Nothing measured yet: an empty list with no date, so no surface can claim one.</summary>
    public static readonly KeyFileScanSnapshot Empty =
        new(Array.Empty<KeyFilePairStatus>(), null);
}

/// <summary>
/// The outcome of the last key-file pickup pass, published as a live property on
/// <see cref="LicenseService"/> so every surface reads the same fact. Never carries key material:
/// the customer name and the file names are already treated as loggable, the phrase never is.
/// </summary>
public sealed record KeyFilePickupOutcome(
    KeyFilePickupKind Kind,
    string? BundleFileName,
    string? KeyFileName,
    string? CustomerName,
    DateTime WhenUtc,
    int PairsSeen,
    KeyFileConsumeState Consume,
    string Message)
{
    /// <summary>
    /// WARN ONLY (Adrian, 2026-09-02 15:35). True when the install folder grants write to a
    /// broad principal, so a non-administrator could plant a pair there. Pickup still runs —
    /// this is a warning on the card and in the log, not a refusal.
    /// </summary>
    public bool InstallFolderLooselyAcled { get; init; }

    /// <summary>The broad principals that hold write on the install folder, comma separated.</summary>
    public string? LooseAclPrincipals { get; init; }

    /// <summary>
    /// True when this pass did nothing because <c>Licensing:KeyFilePickup:Enabled</c> is false.
    ///
    /// <para><b>Why a flag and not just <see cref="KeyFilePickupKind.NotRun"/>.</b> NotRun covers
    /// three unrelated postures — not Windows, not reached yet, and switched off — and the card
    /// hides its outcome panel for all of them. Only the third one has a folder listing behind it
    /// and a sentence worth printing, and a surface may not tell those apart by reading the
    /// message text. DO NOT ACT BUT STILL LIST (Adrian, DECISIONS 2026-09-03 11:20).</para>
    /// </summary>
    public bool PickupDisabledByConfig { get; init; }
}

/// <summary>
/// The key file's on-disk shape, parsed. Pure string work over the file's decoded text: it opens
/// nothing, so it can be exercised without a filesystem.
///
/// <para><b>The format.</b> Two lines, written by <c>CorpusEncryptor/Program.cs</c>:</para>
/// <code>
/// Customer: Acme Holdings Pty Ltd
/// &lt;24 words&gt;
/// </code>
/// <para>The customer name is REQUIRED and is taken verbatim after trimming, because the AAD is
/// case-sensitive (<c>AadBuilder.Build</c>). Unrecognised lines are skipped rather than refused, so
/// the existing <c>&lt;slug&gt;-delivery.txt</c> note — which already carries
/// <c>"Customer   : &lt;name&gt;"</c> and the phrase alone on an indented line — parses unchanged
/// when an operator renames it.</para>
/// </summary>
internal static class KeyFileParser
{
    /// <summary>
    /// The largest file pickup will read. A 24-word phrase is about 200 bytes and the delivery
    /// note is under 2 KB. This is a bound on what a caller can make this service read, not an
    /// estimate — the same reason <c>LicenseService.MaxBundleFileBytes</c> exists.
    /// </summary>
    public const long MaxKeyFileBytes = 8L * 1024;

    /// <summary>The label line. Case-insensitive on the label; the value is taken verbatim.</summary>
    private static readonly Regex CustomerLine =
        new(@"^\s*(?:Customer|Client)\s*[:=]\s*(?<name>.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Decodes the file's bytes as UTF-8 and strips a leading BOM.
    ///
    /// <para><c>CorpusEncryptor</c> writes with <c>Encoding.UTF8</c>, whose preamble .NET emits on
    /// <c>File.WriteAllText</c>, so a BOM is the expected case rather than an edge one. A BOM left
    /// on the front of the first line would make <c>Customer: X</c> fail the label match and the
    /// whole pair read as Malformed.</para>
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>
    /// Pulls the customer name and the key text out of a key file's decoded text.
    /// Returns false with a named <paramref name="problem"/> when either is missing.
    /// </summary>
    public static bool TryParse(string text, out string? customer, out string? keyText, out string problem)
    {
        customer = null;
        keyText = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            if (customer is null)
            {
                var m = CustomerLine.Match(line);
                if (m.Success)
                {
                    customer = m.Groups["name"].Value;
                    continue;
                }
            }

            if (keyText is null && LooksLikeKey(line)) keyText = line;

            if (customer is not null && keyText is not null) break;
        }

        if (customer is null)
        {
            problem = "The key file does not say which customer it is for. It needs a line reading "
                + "\"Customer: <the exact name from your licence email>\" above the 24-word phrase.";
            return false;
        }

        if (keyText is null)
        {
            problem = "The key file carries no 24-word phrase and no Base64 key. Paste the phrase "
                + "from your licence email onto its own line, whole.";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// A line is a key when it is exactly 24 whitespace-separated words, or a single token that
    /// decodes as Base64 to 32 bytes. Both forms are what <c>LicenseService.DecodeKeyInput</c>
    /// already accepts; this adds nothing to it, it only decides WHICH line to hand it.
    /// </summary>
    private static bool LooksLikeKey(string line)
    {
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 24) return true;
        if (parts.Length != 1) return false;

        // A Base64 key is 32 bytes. Anything else that happens to parse as Base64 (a slug, a word)
        // is NOT treated as a key, because a false positive here consumes the line the real key
        // would have been found on.
        try { return Convert.FromBase64String(parts[0]).Length == 32; }
        catch (FormatException) { return false; }
    }
}
