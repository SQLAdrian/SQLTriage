/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

#pragma warning disable CA1416 // Windows-only API (DPAPI) - project targets net8.0-windows
namespace SQLTriage.Data
{
    /// <summary>
    /// What a measurement of an aside's bytes actually established. Four values, not a bool, for
    /// the reason <see cref="SqliteCipherHelper.FileHeadReading"/> has four: the two ways of NOT
    /// finding out have to stay apart from each other and, above all, apart from a measurement.
    /// A verdict printed over a reading that never happened is the house defect class.
    /// </summary>
    internal enum KeyAsideReading
    {
        /// <summary>
        /// MEASURED: DPAPI returned key material for these bytes under this account and machine.
        /// The failure that set the file aside was therefore transient, and this file still holds
        /// usable material. The loudest state there is, and never a deletable one on its own.
        /// </summary>
        Unwrapped,

        /// <summary>
        /// MEASURED: the bytes were read and DPAPI refused them here. Nothing is known about what
        /// they hold, which is exactly why the file is kept.
        /// </summary>
        Sealed,

        /// <summary>
        /// MEASURED: the file is zero bytes long, so it holds no key material of any kind. Real,
        /// not theoretical: <c>File.WriteAllBytes</c> creates and truncates before it writes, so a
        /// crash or a full disk between those two steps leaves exactly this, and the next start
        /// fails to unwrap it and sets it aside.
        /// </summary>
        Empty,

        /// <summary>
        /// NOT measured: the read itself threw, or the file was gone by the time it was opened.
        /// Nothing whatever is known. The FAIL-SAFE value, and it keeps the file.
        /// </summary>
        Unmeasured,
    }

    /// <summary>
    /// One aside, and what reading its bytes established. <paramref name="DuplicatesReplacement"/>
    /// is only ever true off an <see cref="KeyAsideReading.Unwrapped"/> reading whose material was
    /// compared, byte for byte, with the material the replacement now holds.
    /// </summary>
    internal readonly record struct KeyAsideClassification(
        string Path, KeyAsideReading Reading, bool DuplicatesReplacement);

    /// <summary>
    /// Whether the replacement key was PROVEN through the same path production reads it by, and the
    /// sentence saying how it was established or which way it failed. The detail is carried so a
    /// refusal can name its reason rather than assert one.
    /// </summary>
    internal readonly record struct ReplacementProof(bool Proven, string Detail);

    /// <summary>
    /// What an attempt to set a key file aside actually did. Three values, not a nullable path, for
    /// the same reason <see cref="KeyAsideReading"/> has four: "there was nothing to preserve" and
    /// "the preservation failed" put the caller in opposite positions, and a null return collapsed
    /// them into one. Every caller of <see cref="KeyAsideLifecycle.SetAside"/> goes on to write a
    /// fresh key at the same path, so telling those two apart decides whether that write destroys
    /// anything.
    /// </summary>
    internal enum KeyAsidePreservation
    {
        /// <summary>The file was moved, and <see cref="KeyAsideResult.Path"/> says where it went.</summary>
        Moved,

        /// <summary>
        /// There was no file at the path when the move was attempted, so nothing was preserved and
        /// nothing is at risk. Reachable as a race: the caller tested for the file, read it and
        /// failed to unwrap it, and it was gone by the time the move ran.
        /// </summary>
        NothingToPreserve,

        /// <summary>
        /// A file was there and the move failed. The material is still at its own path, and it will
        /// be destroyed by the write the caller was about to make. This is the value that makes a
        /// caller REFUSE (Adrian, 2026-08-10).
        /// </summary>
        Failed,
    }

    /// <summary>
    /// What a MEASUREMENT taken at the moment a move failed established about why it failed. The
    /// recovery advice in <see cref="KeyAsideLifecycle.RefusalNotPreserved"/> is chosen from this
    /// value and from nothing else.
    ///
    /// <para>Four values rather than a bool, and for the same reason
    /// <see cref="KeyAsideReading"/> has four: "not established" has to stay apart from every
    /// reading that WAS established. Advice is an assertion, and an assertion beside a verdict is
    /// conditioned on the same measurement as the verdict.</para>
    /// </summary>
    internal enum KeyAsideObstruction
    {
        /// <summary>No move failed, so nothing was diagnosed and nothing may be advised.</summary>
        NotApplicable,

        /// <summary>
        /// MEASURED: this process could not open the key file exclusively either, so something else
        /// holds it open. The one reading under which "start SQLTriage again" is real advice.
        /// </summary>
        SourceHeldOpen,

        /// <summary>
        /// MEASURED: nothing was found holding the key file, and the name the move needed is
        /// already taken on disk. The obstruction is at the DESTINATION, so every remedy aimed at a
        /// lock on the key file is wrong here, and the destination has to be named or the operator
        /// cannot find it.
        /// </summary>
        DestinationOccupied,

        /// <summary>
        /// MEASURED: the move was refused for permission rather than for a lock (an ACL, a
        /// read-only file or a read-only folder). It does not pass on its own.
        /// </summary>
        PermissionDenied,

        /// <summary>
        /// NOT measured: the move failed and no probe here established why. The FAIL-SAFE value. It
        /// says so rather than borrowing another reading's remedy.
        /// </summary>
        Undiagnosed,
    }

    /// <summary>
    /// The outcome of one <see cref="KeyAsideLifecycle.SetAside"/> call. <paramref name="Path"/> is
    /// non-null only on <see cref="KeyAsidePreservation.Moved"/>; <paramref name="Detail"/> is the
    /// clause a refusal quotes so it can name its reason rather than assert one.
    ///
    /// <para><paramref name="Obstruction"/> and <paramref name="BlockedDestination"/> carry the
    /// measurement taken on a <see cref="KeyAsidePreservation.Failed"/> outcome, and are the only
    /// thing the refusal's recovery advice is allowed to read.
    /// <paramref name="BlockedDestination"/> is the aside name the move was going to use, which is
    /// deliberately NOT <paramref name="Path"/>: that one means "here is what was preserved", and
    /// on a failure nothing was.</para>
    /// </summary>
    internal readonly record struct KeyAsideResult(
        KeyAsidePreservation Preservation, string? Path, string Detail,
        KeyAsideObstruction Obstruction = KeyAsideObstruction.NotApplicable,
        string? BlockedDestination = null)
    {
        /// <summary>
        /// True when writing a fresh key over the original path destroys nothing that was there.
        /// False means the existing material is still at that path and is about to be lost.
        /// </summary>
        internal bool SafeToOverwrite => Preservation != KeyAsidePreservation.Failed;
    }

    /// <summary>
    /// A key file was NOT replaced, and the reason is in <see cref="Exception.Message"/>.
    ///
    /// <para>Derives from <see cref="IOException"/> deliberately: both refusals it carries are about
    /// a file (one could not be moved, the other could not be read back), and
    /// <see cref="SqliteCipherHelper"/> already refuses with <c>IOException</c> for the store-level
    /// equivalent, so its callers need no new catch to keep behaving as they do today.</para>
    ///
    /// <para>The message is composed by <see cref="KeyAsideLifecycle.RefusalNotPreserved"/> or
    /// <see cref="KeyAsideLifecycle.RefusalNotProven"/> so that all three key-holding services say
    /// the same words for the same state.</para>
    ///
    /// <para><b>Where it actually goes.</b> MEASURED 2026-08-10: the LOG, and no other surface. All
    /// three sites write it at Error, and no production code path returns it to a screen.
    /// <c>SeatRegister.VerifyChain</c> WOULD return it verbatim, and nothing outside the tests calls
    /// <c>VerifyChain</c> at all: the three pages that inject <c>ISeatRegister</c> call
    /// <c>Seats.Filter</c> and nothing else. That census is pinned by
    /// <c>AsideProducerCensusTests.The_seat_refusal_reaches_the_log_and_nothing_returns_it_to_a_screen</c>,
    /// which fails the day someone wires it to a screen, because a new surface is a reason to
    /// revisit this copy. The message is written for a log reader with no other context around it,
    /// which is why it names the file, says what is at stake and says what to do about it.</para>
    ///
    /// <para>An earlier draft of this paragraph cited that VerifyChain caller as proof the sentence
    /// reaches an operator. The caller does not exist, and the claim was never measured.</para>
    /// </summary>
    internal sealed class KeyAsideRefusedException : IOException
    {
        internal KeyAsideRefusedException(string message) : base(message) { }
    }

    /// <summary>
    /// Where this file's sentences go. Three call sites with three different logging stacks (Serilog
    /// static in two, an injected <c>ILogger</c> in the third) would otherwise carry three copies of
    /// every sentence, and copies drift apart. The cost is that the messages are composed rather
    /// than structured; the benefit is that the operator reads the same words at all three.
    /// </summary>
    internal delegate void KeyAsideLog(bool isError, Exception? error, string message);

    /// <summary>
    /// The discriminate-and-prove lifecycle for a DPAPI-wrapped KEY FILE that had to be moved aside
    /// (HOUSE RULE, Adrian 2026-08-09: classify the aside, delete only after the replacement is
    /// proven). Shared by <see cref="CredentialProtector"/>, <see cref="SqliteCipherHelper"/> and
    /// <c>SeatRegister</c>, which had three copies of the same nine-line rename and no classifier,
    /// no proof and no cleanup between them.
    ///
    /// <para><b>What this is NOT.</b> It is not the ruling for the <c>.pre-reinit-</c> DATABASE
    /// aside (2026-08-06, <see cref="SqliteCipherHelper.RemoveVerifiedPlaintextAside"/>). That one
    /// deletes a PLAINTEXT population because a readable copy of a database beside an encrypted one
    /// defeats the encryption. A key file has no such population: every one of these is written by
    /// <c>ProtectedData.Protect</c>, so a copy is never more readable than the active file beside
    /// it. The exposure argument that authorises that delete does not exist here.</para>
    ///
    /// <para><b>So what IS deletable.</b> Only what a measurement can prove holds nothing the
    /// replacement does not already hold, which is two populations and no others:
    /// <see cref="KeyAsideReading.Empty"/> (zero bytes, so no material at all) and a
    /// <see cref="KeyAsideClassification.DuplicatesReplacement"/> aside (it unwraps, and its
    /// material is byte-identical to the key now in use, so it is a second on-disk copy of live
    /// secret material with no recovery value). Everything else is KEPT and named in the log with
    /// the reading that kept it. In particular an aside that unwraps to DIFFERENT material is the
    /// most valuable file in the folder, because it is the only route back to whatever was
    /// encrypted under it, and it is reported at Error for that reason.</para>
    ///
    /// <para><b>Why "does not unwrap" is not enough to delete.</b> All three call sites seal at
    /// <see cref="DataProtectionScope.LocalMachine"/>, so a refusal here cannot be read as "no
    /// machine will ever open this": the documented reason these asides exist at all is a TRANSIENT
    /// fault (a restore, a SID change, a master-key store that was briefly unavailable), and that is
    /// the same reading. Deleting on it would destroy precisely the recovery the aside was created
    /// for. This is the opposite polarity to the database ruling, and deliberately so.</para>
    ///
    /// <para>Internal (InternalsVisibleTo SQLTriage.Tests). The reason first written here was that no
    /// end-to-end path could make a delete fail, hand the classifier a zero-byte file, or produce an
    /// aside sealed under an entropy this machine will refuse. Two thirds of that is false, and it was
    /// falsified by a test in this same lane: <c>SeatRegisterKeyAsideTests</c> plants both a zero-byte
    /// aside and one sealed under a foreign entropy, then drives them through the real SeatRegister,
    /// with no seam. What survives is the failed delete, which is produced here by holding a real
    /// share-denying handle on the aside while the sweep runs, and which no call site has been driven
    /// through. That, and directness: the 2026-08-06 note on
    /// <see cref="SqliteCipherHelper.RemoveVerifiedPlaintextAside"/> records what happens when a
    /// contract like this is left private: a mutation making the delete rethrow passed all 17
    /// tests.</para>
    /// </summary>
    internal static class KeyAsideLifecycle
    {
        /// <summary>
        /// The suffix an aside's name is built from. Used to WRITE the name, and to ENUMERATE
        /// candidates on a later pass. It is not, and must never become, the classifier: a name
        /// says who wrote a file, never what is inside it.
        /// </summary>
        internal const string AsideSuffix = ".corrupt-";

        /// <summary>
        /// Moves a key file that is about to be replaced aside, and says what that attempt did.
        ///
        /// <para>⚠ The <see cref="KeyAsidePreservation.Failed"/> outcome is not cosmetic. Every
        /// caller of this method goes on to write a fresh key at the SAME path, so a failed move
        /// means the original material is about to be overwritten and lost. RULED (Adrian,
        /// 2026-08-10): a caller that could not preserve the existing material REFUSES to overwrite
        /// it. That is the <c>config-store-write-guard</c> posture for secrets, applied to the one
        /// file whose loss is not re-authorable, and it is why this returns a three-way result
        /// rather than the nullable path it used to: "no file was there" and "the file is there and
        /// the move failed" are opposite positions and must not share a return value.</para>
        ///
        /// <para>Called on TWO branches now, not one. The original is the unwrap failure. The second
        /// is material that DID unwrap and is the wrong length: it is readable key material, so it
        /// may be the only route back to whatever was encrypted under it, and regenerating over it
        /// destroys it exactly as surely (RULED with the above).</para>
        /// </summary>
        internal static KeyAsideResult SetAside(string filePath, KeyAsideLog log)
        {
            string? aside = null;
            try
            {
                if (!File.Exists(filePath))
                    return new KeyAsideResult(KeyAsidePreservation.NothingToPreserve, null,
                        "there was no file at " + filePath + " by the time the move ran, so nothing "
                        + "was preserved and nothing was at risk");

                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                aside = filePath + AsideSuffix + stamp;
                for (int suffix = 2; File.Exists(aside); suffix++)
                    aside = filePath + AsideSuffix + stamp + "-" + suffix;

                File.Move(filePath, aside);
                log(false, null,
                    "Preserved the unreadable key file as " + aside + ". It is kept, not deleted: "
                    + "nothing has yet measured what it holds.");
                return new KeyAsideResult(KeyAsidePreservation.Moved, aside,
                    "the previous key file was preserved as " + aside);
            }
            catch (Exception ex)
            {
                // Diagnosed HERE, while the disk is still in the state that produced the failure.
                // A refusal composed later can then name a cause it measured instead of one it
                // assumed, which is what it did until 2026-08-10.
                var obstruction = Diagnose(filePath, aside, ex);

                log(true, ex,
                    "Could not move the key file " + filePath + " aside, so it is still at its own "
                    + "path holding whatever it held. No fresh key is being written over it: the "
                    + "replacement is refused instead.");
                return new KeyAsideResult(KeyAsidePreservation.Failed, null,
                    ex.GetType().Name + ": " + ex.Message, obstruction, aside);
            }
        }

        /// <summary>
        /// Why the move failed, from the exception plus two probes taken on the spot. Answers
        /// <see cref="KeyAsideObstruction.Undiagnosed"/> rather than guessing when neither probe
        /// establishes anything.
        ///
        /// <para>The order is the order of the evidence. A permission refusal is named by the
        /// exception type itself. A lock is the only reading that makes a retry meaningful, so it is
        /// probed next and wins where it holds. An occupied destination is checked last because it
        /// is the reading that survives when the file itself is free.</para>
        /// </summary>
        private static KeyAsideObstruction Diagnose(string filePath, string? destination, Exception ex)
        {
            if (ex is UnauthorizedAccessException) return KeyAsideObstruction.PermissionDenied;

            if (IsHeldOpen(filePath) == true) return KeyAsideObstruction.SourceHeldOpen;

            if (destination is not null && (File.Exists(destination) || Directory.Exists(destination)))
                return KeyAsideObstruction.DestinationOccupied;

            return KeyAsideObstruction.Undiagnosed;
        }

        /// <summary>
        /// Whether something else holds <paramref name="filePath"/> open. Null where the probe
        /// established neither, which is a third state on purpose: reading it as "not locked" is how
        /// a refusal ends up offering a remedy for a condition nobody measured.
        ///
        /// <para>Opening for READ with <see cref="FileShare.None"/> succeeds only where this process
        /// is the sole holder, and that is the whole measurement. The two not-found exceptions are
        /// caught FIRST because both derive from <see cref="IOException"/>: without that, a file that
        /// had vanished would be reported as one held open by somebody.</para>
        /// </summary>
        private static bool? IsHeldOpen(string filePath)
        {
            try
            {
                using var probe = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
                return false;
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) { return true; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The words a service says when it will not overwrite a key file it could not preserve
        /// (posture (b), RULED 2026-08-10). Composed here so all three say the same thing.
        ///
        /// <para><paramref name="atStake"/> is the ONE clause each service supplies, because only it
        /// knows what its key protects. The rest is fixed except the recovery paragraph, which is
        /// chosen by <see cref="KeyAsideResult.Obstruction"/>: the measurement taken at the moment
        /// the move failed, never a cause assumed from a list.</para>
        ///
        /// <para><b>Why it is not one paragraph.</b> It was, and the doc above it asserted that the
        /// causes of a failed move here are a file briefly held open. EXERCISED 2026-08-10 with
        /// directories planted at the aside names: nothing held the key file, the obstruction was at
        /// the destination, a restart cleared nothing, and the message still told the operator to
        /// close a handle and start again. Three of the four remedies it offered were for a lock
        /// that did not exist, and the destination was never named, so there was no way to find the
        /// real blocker from the message. The lane's own primary posture-(b) test produces the
        /// failed move exactly that way, so the cause list was falsified inside the commit that
        /// wrote it. This is the standard already recorded at
        /// <c>Data/Services/Portal/Export/DpapiKeyStore.cs</c>: the advice in a line is conditioned
        /// on the same measurement as the code around it.</para>
        ///
        /// <para>Nothing here writes a marker that would make a refusal stick. It is per attempt
        /// whatever the obstruction was, and the next start tries again.</para>
        /// </summary>
        internal static string RefusalNotPreserved(string keyFilePath, string atStake, KeyAsideResult aside)
            => "SQLTriage will not replace the key file at " + keyFilePath + ". The file that is "
            + "there could not be moved aside first (" + aside.Detail + "), so writing a new key "
            + "over it would destroy it, and " + atStake + ". No new key was written, and this run "
            + "has not renamed, moved or deleted that file. " + Remedy(keyFilePath, aside);

        /// <summary>
        /// The recovery paragraph, drawn from the same measurement the refusal was raised on. Each
        /// arm states the reading it rests on, because an operator acting on this needs to know
        /// which of these was measured and which was not.
        /// </summary>
        private static string Remedy(string keyFilePath, KeyAsideResult aside) => aside.Obstruction switch
        {
            KeyAsideObstruction.SourceHeldOpen =>
                "Something else has that file open: this run could not open it exclusively either. "
                + "That is usually an antivirus scan, a backup agent or a second copy of SQLTriage, "
                + "and it usually passes, so starting SQLTriage again may be all this needs. If it "
                + "keeps refusing, close whatever has " + keyFilePath + " open, or move that file "
                + "somewhere safe by hand, and start again.",

            KeyAsideObstruction.DestinationOccupied =>
                "Nothing was found holding that file open, so this is not a lock and closing "
                + "programs will not clear it. The name the move needed, "
                + (aside.BlockedDestination ?? keyFilePath + AsideSuffix)
                + ", is already taken on disk. Move or rename it, and anything else beside "
                + keyFilePath + " whose name starts with " + Path.GetFileName(keyFilePath)
                + AsideSuffix + ", then start SQLTriage again.",

            KeyAsideObstruction.PermissionDenied =>
                "The move was refused for permission rather than for a lock, so closing programs "
                + "and starting again will not clear it on its own. Check that the account "
                + "SQLTriage runs under can write to "
                + (Path.GetDirectoryName(keyFilePath) ?? "that folder")
                + ", and that " + keyFilePath + " is not read only, then start SQLTriage again.",

            KeyAsideObstruction.Undiagnosed =>
                "This run could not establish why the move failed. The reason quoted above is all "
                + "there is to go on: nothing here found " + keyFilePath + " locked, and the name "
                + "the move needed was free. Fix what that reason names, then start SQLTriage again.",

            _ => "Read the reason quoted above, fix what it names, then start SQLTriage again.",
        };

        /// <summary>
        /// The words a service says when the key it just wrote could not be read back, so it refuses
        /// to run on it (posture (c), RULED 2026-08-10). Before this, a failed proof was logged at
        /// Error and the unproven key was used anyway, which is the state the proof exists to catch.
        ///
        /// <para>The disk is in a different state from <see cref="RefusalNotPreserved"/> and the
        /// sentence says so: a new file HAS been written at the key path by the time this is
        /// composed, and anything set aside is still beside it.</para>
        /// </summary>
        internal static string RefusalNotProven(string keyFilePath, string atStake, string detail)
            => "SQLTriage wrote a new key file at " + keyFilePath + " and could not prove it reads "
            + "back off disk (" + detail + "). It is NOT being used, because " + atStake + ". "
            + "Anything preserved beside that file is still there and untouched; the lines above "
            + "name each one. Check that the folder is writable and the disk is not full, then start "
            + "SQLTriage again. If it keeps refusing, delete " + keyFilePath + " so a fresh one is "
            + "written on the next start, and keep every preserved file beside it: those are the "
            + "only route back to anything encrypted under the old key.";

        /// <summary>
        /// Every aside sitting beside <paramref name="keyFilePath"/>, oldest name first.
        ///
        /// <para>This is the only place a NAME is used, and it is used to find candidates, never to
        /// judge them: <see cref="Classify"/> reads the bytes of everything this returns. The
        /// distinction matters because a name-based verdict is the heuristic the house rule exists
        /// to replace.</para>
        /// </summary>
        internal static IReadOnlyList<string> FindAsides(string keyFilePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(keyFilePath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return Array.Empty<string>();

                var name = Path.GetFileName(keyFilePath);
                var found = new List<string>(
                    Directory.EnumerateFiles(dir, name + AsideSuffix + "*", SearchOption.TopDirectoryOnly));
                found.Sort(StringComparer.OrdinalIgnoreCase);
                return found;
            }
            catch (Exception)
            {
                // Nothing is logged here on purpose: the caller's next line is the one that has to
                // say what was and was not looked at, and a sweep that found nothing must never be
                // reported as a folder with nothing in it.
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Reads the bytes at <paramref name="asidePath"/> and reports what the reading established.
        /// Says nothing about what should be done with the answer: that is
        /// <see cref="ReconcileAsides"/>'s sentence to write, and it depends on a proof this method
        /// knows nothing about.
        ///
        /// <para><paramref name="replacementMaterial"/> is compared only on the branch where the
        /// aside actually unwrapped. A comparison against material that was never recovered would be
        /// a verdict resting on a measurement not taken.</para>
        /// </summary>
        internal static KeyAsideClassification Classify(
            string asidePath, byte[] entropy, DataProtectionScope scope, byte[]? replacementMaterial)
        {
            byte[] wrapped;
            try
            {
                wrapped = File.ReadAllBytes(asidePath);
            }
            catch (Exception)
            {
                return new KeyAsideClassification(asidePath, KeyAsideReading.Unmeasured, false);
            }

            if (wrapped.Length == 0)
                return new KeyAsideClassification(asidePath, KeyAsideReading.Empty, false);

            byte[] material;
            try
            {
                material = ProtectedData.Unprotect(wrapped, entropy, scope);
            }
            catch (CryptographicException)
            {
                // MEASURED: DPAPI was asked and refused. Distinct from the read failing, which
                // establishes nothing at all.
                return new KeyAsideClassification(asidePath, KeyAsideReading.Sealed, false);
            }
            catch (Exception)
            {
                return new KeyAsideClassification(asidePath, KeyAsideReading.Unmeasured, false);
            }

            var duplicate = replacementMaterial is not null
                && CryptographicOperations.FixedTimeEquals(material, replacementMaterial);

            CryptographicOperations.ZeroMemory(material);
            return new KeyAsideClassification(asidePath, KeyAsideReading.Unwrapped, duplicate);
        }

        /// <summary>
        /// Proves that the key file just written can be read back the way PRODUCTION reads it: off
        /// disk, through <c>ProtectedData.Unprotect</c>, yielding the same bytes that were generated.
        ///
        /// <para>Nothing in this codebase did that before. All three call sites wrote the file and
        /// returned the in-memory key, so a write that landed short, a folder that silently refused
        /// it, or a DPAPI that would not take back what it had just sealed all produced a running
        /// process with a key on disk that the NEXT start cannot use. Every credential, cipher and
        /// signature written in between is then unrecoverable, and the first sign of it is the next
        /// restart. The proof is also what authorises the cleanup: an aside may only be judged
        /// redundant against a replacement that has been shown to work.</para>
        /// </summary>
        internal static ReplacementProof ProveReplacement(
            string keyFilePath, byte[] expectedMaterial, byte[] entropy, DataProtectionScope scope)
        {
            byte[] wrapped;
            try
            {
                wrapped = File.ReadAllBytes(keyFilePath);
            }
            catch (Exception ex)
            {
                return new ReplacementProof(false,
                    "the replacement key file at " + keyFilePath + " could not be read back after it "
                    + "was written (" + ex.GetType().Name + ": " + ex.Message + ")");
            }

            byte[] material;
            try
            {
                material = ProtectedData.Unprotect(wrapped, entropy, scope);
            }
            catch (Exception ex)
            {
                return new ReplacementProof(false,
                    "the replacement key file at " + keyFilePath + " was read back but would not "
                    + "unwrap (" + ex.GetType().Name + ": " + ex.Message + ")");
            }

            var matches = CryptographicOperations.FixedTimeEquals(material, expectedMaterial);
            CryptographicOperations.ZeroMemory(material);

            return matches
                ? new ReplacementProof(true,
                    "the replacement key file at " + keyFilePath + " was read back from disk and "
                    + "unwrapped to the same material that is now in use")
                : new ReplacementProof(false,
                    "the replacement key file at " + keyFilePath + " unwrapped to material that is "
                    + "NOT the key now in use, so the file on disk and the running process disagree");
        }

        /// <summary>
        /// Classifies every aside beside the key file and acts on the two populations a measurement
        /// can prove worthless. THE DELETE LIVES HERE, in the same call that was handed the proof,
        /// and it is skipped entirely unless <paramref name="proof"/> came back proven.
        ///
        /// <para>Does not throw. By the time this runs the replacement key is in hand and the caller
        /// is on its way back with it; failing that caller over a leftover file would trade a working
        /// service for a tidy folder. A delete that fails is reported at Error naming the path,
        /// because the honest claim in that case is that the file is still there.</para>
        /// </summary>
        internal static void ReconcileAsides(
            string keyFilePath,
            byte[] replacementMaterial,
            byte[] entropy,
            DataProtectionScope scope,
            ReplacementProof proof,
            KeyAsideLog log)
        {
            foreach (var path in FindAsides(keyFilePath))
            {
                var found = Classify(path, entropy, scope, replacementMaterial);

                if (!proof.Proven)
                {
                    log(true, null,
                        "Kept " + path + " (" + Describe(found) + "): nothing beside an unproven "
                        + "replacement may be removed, and " + proof.Detail + ".");
                    continue;
                }

                if (found.Reading == KeyAsideReading.Unwrapped && !found.DuplicatesReplacement)
                {
                    log(true, null,
                        "Kept " + path + ": it unwrapped here, so the fault that set it aside has "
                        + "passed and this file still holds material the replacement does not. It is "
                        + "the only way back to whatever was encrypted under it. Nothing here restores "
                        + "it; put it back over the active key file by hand if that is what you want.");
                    continue;
                }

                if (found.Reading == KeyAsideReading.Sealed)
                {
                    log(false, null,
                        "Kept " + path + ": it was read and DPAPI refused it under this account, so "
                        + "nothing is known about what it holds. The refusal may be transient, and "
                        + "this is the only copy, so it stays where it is.");
                    continue;
                }

                if (found.Reading == KeyAsideReading.Unmeasured)
                {
                    log(false, null,
                        "Kept " + path + ": its bytes could not be read at all, so nothing about it "
                        + "was measured and nothing about it may be acted on.");
                    continue;
                }

                try
                {
                    File.Delete(path);
                    log(false, null, "Deleted " + path + ": " + WhyRedundant(found) + ", and "
                        + proof.Detail + ". Both were measured before this delete ran.");
                }
                catch (Exception ex)
                {
                    log(true, ex,
                        "Could not delete " + path + ", so it is STILL ON DISK. " + WhyRedundant(found)
                        + ". Remove it by hand.");
                }
            }
        }

        /// <summary>The reading, in words, for a line that has to say what it is keeping.</summary>
        private static string Describe(KeyAsideClassification found) => found.Reading switch
        {
            KeyAsideReading.Unwrapped => found.DuplicatesReplacement
                ? "it unwraps to the same material as the key now in use"
                : "it unwraps here, to material the replacement does not hold",
            KeyAsideReading.Sealed => "it was read and DPAPI refused it here",
            KeyAsideReading.Empty => "it is zero bytes long",
            _ => "its bytes could not be read, so nothing was measured",
        };

        /// <summary>
        /// The justification a delete line carries, drawn from the same classification the delete
        /// was authorised by. Only the two deletable readings have one, and the fallback says so
        /// rather than inventing a reason for a file that should not have reached the delete.
        /// </summary>
        private static string WhyRedundant(KeyAsideClassification found) => found.Reading switch
        {
            KeyAsideReading.Empty =>
                "it measured zero bytes, so it held no key material at all",
            KeyAsideReading.Unwrapped when found.DuplicatesReplacement =>
                "it unwrapped to the same bytes as the key now in use, so it carried no way back to "
                + "anything and was a second copy of live key material on disk",
            _ =>
                "no measurement authorised removing it, which is a coding error in this file rather "
                + "than an operator problem",
        };
    }
}
#pragma warning restore CA1416
